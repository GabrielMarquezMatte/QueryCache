using BitFaster.Caching;
using BitFaster.Caching.Lru;

namespace QueryCache
{
    internal readonly record struct Entry<TResult>(TResult Value, TimeSpan Expiration, string[] Tags, long[] Versions);

    internal static class CacheHolder<TResult>
    {
        public static readonly ICache<QueryKey, Entry<TResult>> Cache = new ConcurrentLruBuilder<QueryKey, Entry<TResult>>()
            .WithCapacity(QueryCacheStore.Capacity)
            .WithExpireAfter(ExpirationCalculator<TResult>.Instance)
            .Build();

        public static readonly KeyValuePair<string, object?> TypeTag = new("querycache.type", typeof(TResult).ToString());
    }
}
