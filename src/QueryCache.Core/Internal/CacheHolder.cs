using BitFaster.Caching;
using BitFaster.Caching.Lru;

namespace QueryCache;

internal static class CacheHolder<TResult>
{
    public static readonly ICache<int, (TResult, TimeSpan)> Cache = new ConcurrentLruBuilder<int, (TResult, TimeSpan)>()
        .WithCapacity(QueryCacheStore.Capacity)
        .WithExpireAfter(ExpirationCalculator<TResult>.Instance)
        .Build();

    public static readonly KeyValuePair<string, object?> TypeTag = new("querycache.type", typeof(TResult).ToString());
}
