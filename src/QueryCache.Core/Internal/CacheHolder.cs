using BitFaster.Caching;
using BitFaster.Caching.Lru;

namespace QueryCache;

internal static class CacheHolder<TResult>
{
    public static readonly ICache<int, (TResult, TimeSpan)> Cache = new ConcurrentLruBuilder<int, (TResult, TimeSpan)>()
        .WithExpireAfter(ExpirationCalculator<TResult>.Instance)
        .Build();
}
