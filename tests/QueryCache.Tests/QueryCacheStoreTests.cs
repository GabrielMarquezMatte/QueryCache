namespace QueryCache.Tests;

public sealed class QueryCacheStoreTests
{
    [Fact]
    public async Task Concurrent_misses_run_factory_once()
    {
        var calls = 0;
        var key = HashCode.Combine(nameof(Concurrent_misses_run_factory_once));
        async Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(50, ct);
            return "v";
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), Factory, CancellationToken.None).AsTask()));
        Assert.All(results, r => Assert.Equal("v", r));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Empty_collections_are_not_cached()
    {
        var key = HashCode.Combine(nameof(Empty_collections_are_not_cached));
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMinutes(1), _ => Task.FromResult(new List<int>()), CancellationToken.None);
        Assert.False(QueryCacheStore.TryGet<List<int>>(key, out _));
    }

    [Fact]
    public async Task Entries_expire()
    {
        var key = HashCode.Combine(nameof(Entries_expire));
        await QueryCacheStore.GetOrAddAsync(key, TimeSpan.FromMilliseconds(50), _ => Task.FromResult(1), CancellationToken.None);
        Assert.True(QueryCacheStore.TryGet<int>(key, out _));
        await Task.Delay(200);
        Assert.False(QueryCacheStore.TryGet<int>(key, out _));
    }
}
