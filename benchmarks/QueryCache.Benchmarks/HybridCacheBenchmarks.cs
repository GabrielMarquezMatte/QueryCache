using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace QueryCache.Benchmarks;

/// <summary>
/// A hit served by Microsoft's <see cref="HybridCache"/> local layer, over <see cref="Rows"/> rows, and the key it is looked up by.
/// No database: the entry is filled once in <see cref="Setup"/>.
/// </summary>
[MemoryDiagnoser]
public class HybridCacheBenchmarks
{
    private static readonly TimeSpan Expiration = TimeSpan.FromHours(1);
    private readonly QueryKey _key = new("select Id, Name, Code from Items where Code = @Code order by Id", "@Code", 0);
    private ServiceProvider _services = null!;

    [Params(10, 1_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _services = new ServiceCollection().AddHybridCache().Services.BuildServiceProvider();
        QueryCacheStore.HybridCache = _services.GetRequiredService<HybridCache>();
        IReadOnlyList<Item> rows = [.. Enumerable.Range(1, Rows).Select(i => new Item { Id = i, Name = $"item {i}", Code = 0 })];
        await QueryCacheStore.GetOrAddAsync(_key, Expiration, _ => Task.FromResult(rows), CancellationToken.None);
        var hit = await Hit();
        if (!ReferenceEquals(hit, rows))
        {
            throw new InvalidOperationException("The hit was not served from the local layer.");
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        QueryCacheStore.HybridCache = null;
        await _services.DisposeAsync();
    }

    [Benchmark]
    public ValueTask<IReadOnlyList<Item>> Hit()
    {
        return QueryCacheStore.GetOrAddAsync<IReadOnlyList<Item>>(_key, Expiration, _ => throw new InvalidOperationException("miss"), CancellationToken.None);
    }

    [Benchmark]
    public string Key()
    {
        return QueryCacheStore.HybridKey<IReadOnlyList<Item>>(_key);
    }
}
