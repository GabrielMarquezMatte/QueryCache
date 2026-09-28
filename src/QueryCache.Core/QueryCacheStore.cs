using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace QueryCache;

/// <summary>
/// Process-wide cache of query results, keyed by a caller-computed hash. One LRU per result type.
/// Concurrent misses for the same key run the factory once (single-flight). Empty collections are not kept.
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

    // ponytail: locks are dropped once the winner finishes; a late caller may re-run the factory. Fine for cache fills.
    private static readonly ConcurrentDictionary<int, Gate> Locks = new();

    private sealed class Gate() : SemaphoreSlim(1, 1)
    {
        public volatile bool Invalidated;
    }

    /// <summary>Tries to read a live entry.</summary>
    public static bool TryGet<T>(int key, [MaybeNullWhen(false)] out T value)
    {
        if (CacheHolder<T>.Cache.TryGet(key, out var entry))
        {
            value = entry.Item1;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Removes an entry. Returns <see langword="true"/> if it existed. A fill already running for the key is not cached.</summary>
    public static bool Remove<T>(int key)
    {
        if (Locks.TryGetValue(key, out var gate))
        {
            gate.Invalidated = true;
        }
        return CacheHolder<T>.Cache.TryRemove(key);
    }

    /// <summary>Returns the cached value, or runs <paramref name="factory"/> once per key and caches the result for <paramref name="expiration"/>.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="expiration">How long the result lives. <see cref="Timeout.InfiniteTimeSpan"/> keeps it until removed or evicted.</param>
    /// <param name="factory">Produces the value on a miss.</param>
    /// <param name="cancellationToken">Cancels the wait and is passed to <paramref name="factory"/>.</param>
    public static async ValueTask<T> GetOrAddAsync<T>(int key, TimeSpan expiration, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        if (expiration == Timeout.InfiniteTimeSpan || expiration > MaxExpiration)
        {
            expiration = MaxExpiration;
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiration, TimeSpan.Zero);
        if (TryGet<T>(key, out var cached))
        {
            Hits.Add(1, CacheHolder<T>.TypeTag);
            return cached;
        }
        var gate = Locks.GetOrAdd(key, static _ => new Gate());
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGet<T>(key, out var again))
            {
                Hits.Add(1, CacheHolder<T>.TypeTag);
                return again;
            }
            Misses.Add(1, CacheHolder<T>.TypeTag);
            gate.Invalidated = false;
            var start = Stopwatch.GetTimestamp();
            var value = await factory(cancellationToken).ConfigureAwait(false);
            FillDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, CacheHolder<T>.TypeTag);
            if (!IsEmptyEnumerable(value))
            {
                CacheHolder<T>.Cache.AddOrUpdate(key, (value, expiration));
                if (gate.Invalidated)
                {
                    CacheHolder<T>.Cache.TryRemove(key);
                }
            }
            return value;
        }
        finally
        {
            gate.Release();
            Locks.TryRemove(new KeyValuePair<int, Gate>(key, gate));
        }
    }

    private static bool IsEmptyEnumerable<T>(T value)
    {
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
