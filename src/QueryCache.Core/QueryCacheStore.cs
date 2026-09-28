using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace QueryCache;

/// <summary>
/// Process-wide cache of query results, keyed by <see cref="QueryKey"/>. One LRU per result type.
/// Concurrent misses for the same key run the factory once (single-flight) and share its result or failure.
/// Results meaning "no rows" are returned but not kept: <see langword="default"/> values (<see langword="null"/>, 0, <see langword="false"/>) and empty collections.
/// </summary>
/// <remarks>
/// Publishes metrics on the <see cref="MeterName"/> meter: <c>querycache.hits</c>, <c>querycache.misses</c> and
/// <c>querycache.fill.duration</c> (seconds), each tagged with <c>querycache.type</c> (the result type).
/// </remarks>
public static class QueryCacheStore
{
    /// <summary>Name of the <see cref="Meter"/> the cache publishes to. Pass it to <c>AddMeter</c> in OpenTelemetry.</summary>
    public const string MeterName = "QueryCache";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Hits = Meter.CreateCounter<long>("querycache.hits", description: "Lookups served from the cache.");
    private static readonly Counter<long> Misses = Meter.CreateCounter<long>("querycache.misses", description: "Lookups that ran the query.");
    private static readonly Histogram<double> FillDuration = Meter.CreateHistogram<double>("querycache.fill.duration", "s", "Time spent running the query on a miss.");

    private static readonly TimeSpan MaxExpiration = TimeSpan.FromDays(36500);

    private static readonly ConcurrentDictionary<(QueryKey, Type), Flight> Flights = new();
    private static readonly ConcurrentDictionary<string, long> TagVersions = new(StringComparer.Ordinal);

    private static int _capacity = 128;

    /// <summary>
    /// Maximum entries kept per result type (least recently used are evicted). Default 128, minimum 3.
    /// A result type's cache is sized when first used, so set this at startup.
    /// </summary>
    public static int Capacity
    {
        get => Volatile.Read(ref _capacity);
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 3);
            Volatile.Write(ref _capacity, value);
        }
    }

    private abstract class Flight
    {
        public volatile bool Invalidated;
    }

    private sealed class Flight<T> : Flight
    {
        public readonly TaskCompletionSource<T> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Tries to read a live entry.</summary>
    public static bool TryGet<T>(QueryKey key, [MaybeNullWhen(false)] out T value)
    {
        if (CacheHolder<T>.Cache.TryGet(key, out var entry))
        {
            if (IsCurrent(entry))
            {
                value = entry.Value;
                return true;
            }
            CacheHolder<T>.Cache.TryRemove(key);
        }
        value = default;
        return false;
    }

    /// <summary>Removes an entry. Returns <see langword="true"/> if it existed. A fill already running for the key is not cached.</summary>
    public static bool Remove<T>(QueryKey key)
    {
        if (Flights.TryGetValue((key, typeof(T)), out var flight))
        {
            flight.Invalidated = true;
        }
        return CacheHolder<T>.Cache.TryRemove(key);
    }

    /// <summary>
    /// Invalidates every entry filled with any of <paramref name="tags"/>, including fills still running.
    /// Entries are dropped lazily, on their next read.
    /// </summary>
    /// <param name="tags">The tags to invalidate, such as the tables a write touched.</param>
    public static void InvalidateTags(params IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            TagVersions.AddOrUpdate(tag, 1, static (_, version) => version + 1);
        }
    }

    /// <summary>Returns the cached value, or runs <paramref name="factory"/> once per key and caches the result for <paramref name="expiration"/>.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="expiration">How long the result lives. <see cref="Timeout.InfiniteTimeSpan"/> keeps it until removed or evicted.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    /// <param name="cancellationToken">Cancels this caller's wait; passed to <paramref name="factory"/> when this caller runs it.</param>
    public static ValueTask<T> GetOrAddAsync<T>(QueryKey key, TimeSpan expiration, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        return GetOrAddAsync(key, expiration, factory, static () => [], cancellationToken);
    }

    /// <summary>Returns the cached value, or runs <paramref name="factory"/> once per key and caches the result for <paramref name="expiration"/>.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="expiration">How long the result lives. <see cref="Timeout.InfiniteTimeSpan"/> keeps it until removed or evicted.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    /// <param name="tags">Tags the entry depends on; <see cref="InvalidateTags"/> on any of them drops it.</param>
    /// <param name="cancellationToken">Cancels this caller's wait; passed to <paramref name="factory"/> when this caller runs it.</param>
    public static ValueTask<T> GetOrAddAsync<T>(QueryKey key, TimeSpan expiration, Func<CancellationToken, Task<T>> factory, IReadOnlyCollection<string> tags, CancellationToken cancellationToken)
    {
        return GetOrAddAsync(key, expiration, factory, () => tags, cancellationToken);
    }

    /// <summary>Returns the cached value, or runs <paramref name="factory"/> once per key and caches the result for <paramref name="expiration"/>.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="expiration">How long the result lives. <see cref="Timeout.InfiniteTimeSpan"/> keeps it until removed or evicted.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    /// <param name="tags">Computes the tags the entry depends on; called only on a miss, before <paramref name="factory"/>.</param>
    /// <param name="cancellationToken">Cancels this caller's wait; passed to <paramref name="factory"/> when this caller runs it.</param>
    public static async ValueTask<T> GetOrAddAsync<T>(QueryKey key, TimeSpan expiration, Func<CancellationToken, Task<T>> factory, Func<IReadOnlyCollection<string>> tags, CancellationToken cancellationToken)
    {
        if (expiration == Timeout.InfiniteTimeSpan || expiration > MaxExpiration)
        {
            expiration = MaxExpiration;
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiration, TimeSpan.Zero);
        while (true)
        {
            if (TryGet<T>(key, out var cached))
            {
                Hits.Add(1, CacheHolder<T>.TypeTag);
                return cached;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var mine = new Flight<T>();
            var flight = (Flight<T>)Flights.GetOrAdd((key, typeof(T)), mine);
            if (ReferenceEquals(flight, mine))
            {
                return await FillAsync(key, expiration, factory, tags, mine, cancellationToken).ConfigureAwait(false);
            }
            try
            {
                var shared = await flight.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                Hits.Add(1, CacheHolder<T>.TypeTag);
                return shared;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<T> FillAsync<T>(QueryKey key, TimeSpan expiration, Func<CancellationToken, Task<T>> factory, Func<IReadOnlyCollection<string>> tagSource,
                                              Flight<T> flight, CancellationToken cancellationToken)
    {
        Misses.Add(1, CacheHolder<T>.TypeTag);
        var start = Stopwatch.GetTimestamp();
        try
        {
            string[] tags = [.. tagSource()];
            var versions = Array.ConvertAll(tags, static tag => TagVersions.GetValueOrDefault(tag));
            var value = await factory(cancellationToken).ConfigureAwait(false);
            FillDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, CacheHolder<T>.TypeTag);
            if (!IsEmpty(value))
            {
                CacheHolder<T>.Cache.AddOrUpdate(key, new Entry<T>(value, expiration, tags, versions));
                if (flight.Invalidated)
                {
                    CacheHolder<T>.Cache.TryRemove(key);
                }
            }
            flight.Result.SetResult(value);
            return value;
        }
        catch (Exception exception)
        {
            flight.Result.SetException(exception);
            _ = flight.Result.Task.Exception;
            throw;
        }
        finally
        {
            Flights.TryRemove(new KeyValuePair<(QueryKey, Type), Flight>((key, typeof(T)), flight));
        }
    }

    private static bool IsCurrent<T>(Entry<T> entry)
    {
        for (var i = 0; i < entry.Tags.Length; i++)
        {
            if (TagVersions.GetValueOrDefault(entry.Tags[i]) != entry.Versions[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsEmpty<T>(T value)
    {
        if (EqualityComparer<T>.Default.Equals(value, default))
        {
            return true;
        }
        if (value is string || value is not IEnumerable enumerable)
        {
            return false;
        }
        if (enumerable is ICollection collection)
        {
            return collection.Count == 0;
        }
        var enumerator = enumerable.GetEnumerator();
        try
        {
            return !enumerator.MoveNext();
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }
}
