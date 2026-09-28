using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace QueryCache;

/// <summary>
/// Process-wide cache of query results, keyed by a caller-computed hash. One LRU per result type.
/// Concurrent misses for the same key run the factory once (single-flight). Empty collections are not kept.
/// </summary>
public static class QueryCacheStore
{
    // ponytail: locks are dropped once the winner finishes; a late caller may re-run the factory. Fine for cache fills.
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Locks = new();

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

    /// <summary>Removes an entry. Returns <see langword="true"/> if it existed.</summary>
    public static bool Remove<T>(int key)
    {
        return CacheHolder<T>.Cache.TryRemove(key);
    }

    /// <summary>Returns the cached value, or runs <paramref name="factory"/> once per key and caches the result for <paramref name="expiration"/>.</summary>
    public static async ValueTask<T> GetOrAddAsync<T>(int key, TimeSpan expiration, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        if (TryGet<T>(key, out var cached))
        {
            return cached;
        }
        var gate = Locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGet<T>(key, out var again))
            {
                return again;
            }
            var value = await factory(cancellationToken).ConfigureAwait(false);
            CacheHolder<T>.Cache.AddOrUpdate(key, (value, expiration));
            if (IsEmptyEnumerable(value))
            {
                CacheHolder<T>.Cache.TryRemove(key);
            }
            return value;
        }
        finally
        {
            gate.Release();
            Locks.TryRemove(key, out _);
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
