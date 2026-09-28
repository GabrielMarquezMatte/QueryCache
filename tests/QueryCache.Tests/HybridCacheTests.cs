using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QueryCache.EFCore;

namespace QueryCache.Tests;

/// <summary><see cref="QueryCacheStore.HybridCache"/> is process-wide, so these tests run alone.</summary>
[CollectionDefinition(nameof(HybridCacheTests), DisableParallelization = true)]
public sealed class HybridCacheGroup;

[Collection(nameof(HybridCacheTests))]
public sealed class HybridCacheTests : IDisposable
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    public sealed record Row(int Id, string Name);

    /// <summary>
    /// Stands in for Redis. HybridCache ignores a bare <see cref="MemoryDistributedCache"/> as a distributed layer.
    /// Writes complete asynchronously, like a network cache, and are recorded.
    /// </summary>
    private sealed class SharedCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
        private int _writing;

        /// <summary>Writes started and not finished yet.</summary>
        public int Writing => Volatile.Read(ref _writing);

        /// <summary>Every key written, in order.</summary>
        public ConcurrentQueue<string> Written { get; } = new();

        public byte[]? Get(string key)
        {
            return _inner.Get(key);
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            return _inner.GetAsync(key, token);
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            _inner.Set(key, value, options);
        }

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Interlocked.Increment(ref _writing);
            try
            {
                await Task.Delay(20, token);
                await _inner.SetAsync(key, value, options, token);
                Written.Enqueue(key);
            }
            finally
            {
                Interlocked.Decrement(ref _writing);
            }
        }

        public void Refresh(string key)
        {
            _inner.Refresh(key);
        }

        public Task RefreshAsync(string key, CancellationToken token = default)
        {
            return _inner.RefreshAsync(key, token);
        }

        public void Remove(string key)
        {
            _inner.Remove(key);
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            return _inner.RemoveAsync(key, token);
        }
    }

    private readonly SharedCache _distributed = new();

    public HybridCacheTests()
    {
        QueryCacheStore.HybridCache = NewCache(_distributed);
    }

    public void Dispose()
    {
        QueryCacheStore.HybridCache = null;
    }

    private static HybridCache NewCache(IDistributedCache distributed)
    {
        var services = new ServiceCollection().AddSingleton(distributed);
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    private static QueryKey Key(string name)
    {
        return new QueryKey(name, Guid.NewGuid());
    }

    [Fact]
    public async Task Hits_removals_and_tags_go_through_the_hybrid_cache()
    {
        var calls = 0;
        Task<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("v");
        }
        var key = Key(nameof(Hits_removals_and_tags_go_through_the_hybrid_cache));

        await QueryCacheStore.GetOrAddAsync(key, Minute, Factory, ["tests.hybrid"], CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(key, Minute, Factory, ["tests.hybrid"], CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.False(QueryCacheStore.TryGet<string>(key, out _));

        await QueryCacheStore.RemoveAsync<string>(key, CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(key, Minute, Factory, ["tests.hybrid"], CancellationToken.None);
        Assert.Equal(2, calls);

        await QueryCacheStore.InvalidateTagsAsync(["tests.hybrid"], CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(key, Minute, Factory, ["tests.hybrid"], CancellationToken.None);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Empty_results_are_not_cached()
    {
        var calls = 0;
        Task<List<int>> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new List<int>());
        }
        var key = Key(nameof(Empty_results_are_not_cached));

        await QueryCacheStore.GetOrAddAsync(key, Minute, Factory, CancellationToken.None);
        await QueryCacheStore.GetOrAddAsync(key, Minute, Factory, CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Another_instance_reads_the_entry_from_the_distributed_cache()
    {
        var distributed = new SharedCache();
        var key = Key(nameof(Another_instance_reads_the_entry_from_the_distributed_cache));
        QueryCacheStore.HybridCache = NewCache(distributed);
        await QueryCacheStore.GetOrAddAsync<IReadOnlyList<Row>>(key, Minute, _ => Task.FromResult<IReadOnlyList<Row>>([new Row(1, "a")]), CancellationToken.None);

        QueryCacheStore.HybridCache = NewCache(distributed);
        var rows = await QueryCacheStore.GetOrAddAsync<IReadOnlyList<Row>>(key, Minute, _ => throw new InvalidOperationException("not served from the cache"), CancellationToken.None);

        Assert.Equal([new Row(1, "a")], rows);
    }

    [Fact]
    public async Task Tag_invalidated_during_fill_keeps_the_stale_value_out_of_the_hybrid_cache()
    {
        var key = Key(nameof(Tag_invalidated_during_fill_keeps_the_stale_value_out_of_the_hybrid_cache));
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var fill = QueryCacheStore.GetOrAddAsync(key, Minute, async _ =>
        {
            started.SetResult();
            await release.Task;
            return "stale";
        }, ["tests.hybrid-during-fill"], CancellationToken.None).AsTask();

        await started.Task;
        await QueryCacheStore.InvalidateTagsAsync(["tests.hybrid-during-fill"], CancellationToken.None);
        release.SetResult();

        Assert.Equal("stale", await fill);
        Assert.DoesNotContain(_distributed.Written, written => written.StartsWith("querycache:", StringComparison.Ordinal));
        Assert.Equal("fresh", await QueryCacheStore.GetOrAddAsync(key, Minute, _ => Task.FromResult("fresh"), CancellationToken.None));
    }

    [Fact]
    public async Task Synchronous_SaveChanges_waits_for_the_hybrid_invalidation()
    {
        await using var db = await EfCoreTests.NewDb(new EfCoreTests.Item { Id = 1 });
        Assert.Single(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));

        db.Items.Add(new EfCoreTests.Item { Id = 2 });
        db.SaveChanges();

        Assert.Equal(0, _distributed.Writing);
        Assert.Equal(2, (await db.Items.ToListCachedAsync(Minute, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task SaveChanges_invalidates_ef_entries()
    {
        await using var db = await EfCoreTests.NewDb(new EfCoreTests.Item { Id = 1, Code = 5 });
        var codes = db.Items.Select(i => i.Code);
        Assert.Equal(5, await codes.SumCachedAsync(Minute, CancellationToken.None));
        await db.Items.ExecuteUpdateAsync(s => s.SetProperty(i => i.Code, 6));
        Assert.Equal(5, await codes.SumCachedAsync(Minute, CancellationToken.None));

        db.Items.Add(new EfCoreTests.Item { Id = 2, Code = 1 });
        await db.SaveChangesAsync();

        Assert.Equal(7, await codes.SumCachedAsync(Minute, CancellationToken.None));
    }
}
