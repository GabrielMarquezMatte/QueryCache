using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Medallion.Threading;
using Microsoft.Extensions.Caching.Hybrid;

namespace QueryCache;

/// <summary>
/// Process-wide cache of query results, keyed by <see cref="QueryKey"/>. By default one in-process LRU per result type;
/// set <see cref="HybridCache"/> to keep entries in a <see cref="Microsoft.Extensions.Caching.Hybrid.HybridCache"/> instead (Redis, FusionCache...).
/// Concurrent misses for the same key run the factory once per process (single-flight) and share its result or failure.
/// Every result is kept, including <see langword="null"/>, 0, <see langword="false"/> and empty collections.
/// </summary>
/// <remarks>
/// Publishes metrics on the <see cref="MeterName"/> meter: <c>querycache.hits</c>, <c>querycache.misses</c>,
/// <c>querycache.fill.duration</c> (seconds), <c>querycache.store.failures</c> (results the <see cref="HybridCache"/> could not store,
/// returned uncached) and <c>querycache.lock.failures</c> (see <see cref="DistributedLock"/>), each tagged with <c>querycache.type</c> (the result type),
/// and <c>querycache.invalidation.failures</c> (invalidations after a committed write that the <see cref="HybridCache"/> could not carry out).
/// </remarks>
public static class QueryCacheStore
{
    /// <summary>Name of the <see cref="Meter"/> the cache publishes to. Pass it to <c>AddMeter</c> in OpenTelemetry.</summary>
    public const string MeterName = "QueryCache";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Hits = Meter.CreateCounter<long>("querycache.hits", description: "Lookups served from the cache.");
    private static readonly Counter<long> Misses = Meter.CreateCounter<long>("querycache.misses", description: "Lookups that ran the query.");
    private static readonly Counter<long> StoreFailures = Meter.CreateCounter<long>("querycache.store.failures", description: "Results the HybridCache failed to store (such as serialization errors); they were returned uncached.");
    private static readonly Counter<long> LockFailures = Meter.CreateCounter<long>("querycache.lock.failures", description: "Misses that could not take the distributed lock (timeout or error) and ran the query unlocked.");
    private static readonly Counter<long> InvalidationFailures = Meter.CreateCounter<long>("querycache.invalidation.failures", description: "Invalidations after a committed write that the HybridCache failed to carry out; entries in this process were still invalidated.");
    private static readonly Histogram<double> FillDuration = Meter.CreateHistogram<double>("querycache.fill.duration", "s", "Time spent running the query on a miss.");

    private static readonly TimeSpan MaxExpiration = TimeSpan.FromDays(36500);
    private static readonly HybridCacheEntryOptions ReadOnly = new() { Flags = HybridCacheEntryFlags.DisableUnderlyingData };
    private static readonly HybridCacheEntryOptions StampRead = new() { Flags = HybridCacheEntryFlags.DisableUnderlyingData | HybridCacheEntryFlags.DisableLocalCache };
    private static readonly HybridCacheEntryOptions StampWrite = new() { Expiration = MaxExpiration, Flags = HybridCacheEntryFlags.DisableLocalCache };

    private static readonly ConcurrentDictionary<(QueryKey, Type), Flight> Flights = new();
    private static readonly ConcurrentDictionary<string, long> TagVersions = new(StringComparer.Ordinal);

    private static int _capacity = 128;

    /// <summary>
    /// Maximum entries kept per result type by the in-process LRU (least recently used are evicted). Default 128, minimum 3.
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

    /// <summary>
    /// Where entries live. <see langword="null"/> (default): the in-process LRU, where a hit hands back the cached instance.
    /// Set it at startup, for example to <c>app.Services.GetRequiredService&lt;HybridCache&gt;()</c>, to share entries and invalidations
    /// between instances through the cache's distributed layer. Results must then be serializable by its serializer, and a hit costs a deserialization.
    /// Tag invalidation (<c>SaveChanges</c>) reaches other instances only if the cache propagates <c>RemoveByTagAsync</c>:
    /// FusionCache with a backplane does, Microsoft's default implementation does not.
    /// </summary>
    public static HybridCache? HybridCache { get; set; }

    /// <summary>
    /// Makes a miss single-flight across instances, not just within this process: with a <see cref="HybridCache"/> set, the instance that
    /// misses takes this lock (named after the entry), reads the cache again in case another instance filled it meanwhile, and only then runs
    /// the query. For Redis: <c>new RedisDistributedSynchronizationProvider(connection.GetDatabase())</c> from the <c>DistributedLock.Redis</c> package.
    /// <see langword="null"/> (default) or no <see cref="HybridCache"/>: no lock.
    /// </summary>
    /// <remarks>
    /// A lock that times out (see <see cref="DistributedLockTimeout"/>) or fails does not fail the query: it runs unlocked and
    /// <c>querycache.lock.failures</c> counts it.
    /// </remarks>
    public static IDistributedLockProvider? DistributedLock { get; set; }

    /// <summary>
    /// How long a miss waits for another instance filling the same entry before running the query itself. Default 10 seconds;
    /// set it above your slowest cached query.
    /// </summary>
    public static TimeSpan DistributedLockTimeout { get; set; } = TimeSpan.FromSeconds(10);

    private abstract class Flight
    {
        public volatile bool Invalidated;
    }

    private sealed class Flight<T> : Flight
    {
        public readonly TaskCompletionSource<T> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal static bool TryGet<T>(QueryKey key, [MaybeNullWhen(false)] out T value)
    {
        if (CacheHolder<T>.Cache.TryGet(key, out var entry))
        {
            if (IsCurrent(entry.Tags, entry.Versions))
            {
                value = entry.Value;
                return true;
            }
            CacheHolder<T>.Cache.TryRemove(key);
        }
        value = default;
        return false;
    }

    /// <summary>Removes an entry. A fill already running for the key in this process is not cached.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static ValueTask RemoveAsync<T>(QueryKey key, CancellationToken cancellationToken)
    {
        if (Flights.TryGetValue((key, typeof(T)), out var flight))
        {
            flight.Invalidated = true;
        }
        if (HybridCache is { } hybrid)
        {
            return hybrid.RemoveAsync(HybridKey<T>(key), cancellationToken);
        }
        CacheHolder<T>.Cache.TryRemove(key);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Invalidates every entry filled with any of <paramref name="tags"/>, including fills still running in this process and,
    /// through the <see cref="HybridCache"/>, fills running on other instances.
    /// </summary>
    /// <param name="tags">The tags to invalidate, such as the tables a write touched.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public static ValueTask InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken)
    {
        string[] list = [.. tags];
        foreach (var tag in list)
        {
            TagVersions.AddOrUpdate(tag, 1, static (_, version) => version + 1);
        }
        if (list.Length > 0 && HybridCache is { } hybrid)
        {
            return PublishInvalidationAsync(hybrid, list, cancellationToken);
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Like <see cref="InvalidateTagsAsync"/>, for a write already committed: a <see cref="HybridCache"/> failure is counted, not thrown.</summary>
    internal static async ValueTask InvalidateCommittedTagsAsync(string[] tags)
    {
        try
        {
            await InvalidateTagsAsync(tags, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            InvalidationFailures.Add(1);
        }
    }

    /// <summary>
    /// The part of an invalidation other instances see. The stamps are written before the tags are removed, so a fill that stores
    /// its entry after the removal finds a changed stamp and removes the entry again.
    /// </summary>
    internal static async ValueTask PublishInvalidationAsync(HybridCache hybrid, string[] tags, CancellationToken cancellationToken)
    {
        foreach (var tag in tags)
        {
            await hybrid.SetAsync(StampKey(tag), Guid.NewGuid().ToString("N"), StampWrite, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        await hybrid.RemoveByTagAsync(HybridTags(tags), cancellationToken).ConfigureAwait(false);
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
    /// <param name="tags">Tags the entry depends on; <see cref="InvalidateTagsAsync"/> on any of them drops it.</param>
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
        var hybrid = HybridCache;
        while (true)
        {
            bool found;
            T? cached;
            if (hybrid is null)
            {
                found = TryGet(key, out cached);
            }
            else
            {
                (found, cached) = await ReadAsync<T>(hybrid, key, cancellationToken).ConfigureAwait(false);
            }
            if (found)
            {
                Hits.Add(1, CacheHolder<T>.TypeTag);
                return cached!;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var mine = new Flight<T>();
            var flight = (Flight<T>)Flights.GetOrAdd((key, typeof(T)), mine);
            if (ReferenceEquals(flight, mine))
            {
                return await FillAsync(hybrid, key, expiration, factory, tags, mine, cancellationToken).ConfigureAwait(false);
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

    private static async Task<T> FillAsync<T>(HybridCache? hybrid, QueryKey key, TimeSpan expiration, Func<CancellationToken, Task<T>> factory,
                                              Func<IReadOnlyCollection<string>> tagSource, Flight<T> flight, CancellationToken cancellationToken)
    {
        try
        {
            string[] tags = [.. tagSource()];
            var versions = Array.ConvertAll(tags, static tag => TagVersions.GetValueOrDefault(tag));
            var handle = hybrid is null ? null : await LockAsync<T>(key, cancellationToken).ConfigureAwait(false);
            try
            {
                if (handle is not null)
                {
                    var (found, cached) = await ReadAsync<T>(hybrid!, key, cancellationToken).ConfigureAwait(false);
                    if (found)
                    {
                        Hits.Add(1, CacheHolder<T>.TypeTag);
                        flight.Result.SetResult(cached!);
                        return cached!;
                    }
                }
                var stamps = hybrid is null ? [] : await StampsAsync(hybrid, tags, cancellationToken).ConfigureAwait(false);
                Misses.Add(1, CacheHolder<T>.TypeTag);
                var start = Stopwatch.GetTimestamp();
                var value = await factory(cancellationToken).ConfigureAwait(false);
                FillDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, CacheHolder<T>.TypeTag);
                var entry = new Entry<T>(value, expiration, tags, versions);
                if (hybrid is null)
                {
                    Store(key, entry, flight);
                }
                else
                {
                    await StoreAsync(hybrid, key, entry, stamps, flight, cancellationToken).ConfigureAwait(false);
                }
                flight.Result.SetResult(value);
                return value;
            }
            finally
            {
                if (handle is not null)
                {
                    await handle.DisposeAsync().ConfigureAwait(false);
                }
            }
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

    private static void Store<T>(QueryKey key, Entry<T> entry, Flight flight)
    {
        CacheHolder<T>.Cache.AddOrUpdate(key, entry);
        if (flight.Invalidated)
        {
            CacheHolder<T>.Cache.TryRemove(key);
        }
    }

    private static async ValueTask StoreAsync<T>(HybridCache hybrid, QueryKey key, Entry<T> entry, string?[] stamps, Flight flight, CancellationToken cancellationToken)
    {
        if (flight.Invalidated || !IsCurrent(entry.Tags, entry.Versions))
        {
            return;
        }
        var hybridKey = HybridKey<T>(key);
        try
        {
            await hybrid.SetAsync(hybridKey, new CachedValue<T> { Value = entry.Value }, new HybridCacheEntryOptions { Expiration = entry.Expiration }, HybridTags(entry.Tags), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StoreFailures.Add(1, CacheHolder<T>.TypeTag);
            return;
        }
        if (flight.Invalidated || !IsCurrent(entry.Tags, entry.Versions)
            || !(await StampsAsync(hybrid, entry.Tags, CancellationToken.None).ConfigureAwait(false)).AsSpan().SequenceEqual(stamps))
        {
            await hybrid.RemoveAsync(hybridKey, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async ValueTask<(bool Found, T? Value)> ReadAsync<T>(HybridCache hybrid, QueryKey key, CancellationToken cancellationToken)
    {
        var cached = await hybrid.GetOrCreateAsync(HybridKey<T>(key), static _ => ValueTask.FromResult<CachedValue<T>?>(null), ReadOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
        return cached is null ? (false, default) : (true, cached.Value);
    }

    private static async ValueTask<string?[]> StampsAsync(HybridCache hybrid, string[] tags, CancellationToken cancellationToken)
    {
        var stamps = new string?[tags.Length];
        for (var i = 0; i < tags.Length; i++)
        {
            stamps[i] = await hybrid.GetOrCreateAsync(StampKey(tags[i]), static _ => ValueTask.FromResult<string?>(null), StampRead, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        return stamps;
    }

    private static async ValueTask<IDistributedSynchronizationHandle?> LockAsync<T>(QueryKey key, CancellationToken cancellationToken)
    {
        if (DistributedLock is not { } locks)
        {
            return null;
        }
        try
        {
            var handle = await locks.TryAcquireLockAsync(LockName<T>(key), DistributedLockTimeout, cancellationToken).ConfigureAwait(false);
            if (handle is null)
            {
                LockFailures.Add(1, CacheHolder<T>.TypeTag);
            }
            return handle;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LockFailures.Add(1, CacheHolder<T>.TypeTag);
            return null;
        }
    }

    internal static string LockName<T>(QueryKey key)
    {
        return $"{HybridKey<T>(key)}:lock";
    }

    internal static string HybridKey<T>(QueryKey key)
    {
        return string.Concat("querycache:", key.Digest(CacheHolder<T>.HybridScope));
    }

    private static string[] HybridTags(string[] tags)
    {
        return Array.ConvertAll(tags, static tag => $"querycache:{Digest(tag)}");
    }

    private static string StampKey(string tag)
    {
        return $"querycache:{Digest(tag)}:stamp";
    }

    private static string Digest(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static bool IsCurrent(string[] tags, long[] versions)
    {
        for (var i = 0; i < tags.Length; i++)
        {
            if (TagVersions.GetValueOrDefault(tags[i]) != versions[i])
            {
                return false;
            }
        }
        return true;
    }
}
