using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QueryCache.EFCore;
using QueryCache.EFCore.Keys;

namespace QueryCache.Benchmarks;

/// <summary>Splits the fixed cost of an EF Core cache miss into its parts, on the 10-row query of <see cref="CacheBenchmarks"/>.</summary>
[MemoryDiagnoser]
public class EfMissBreakdownBenchmarks
{
    private static readonly TimeSpan Expiration = TimeSpan.FromHours(1);

    private SqliteConnection _connection = null!;
    private BenchDb _db = null!;
    private IQueryable<Item> _query = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _connection = new SqliteConnection($"Data Source=breakdown-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await _connection.OpenAsync();
        _db = new BenchDb(_connection);
        await _db.Database.EnsureCreatedAsync();
        _db.Items.AddRange(Enumerable.Range(1, 20).Select(i => new Item { Id = i, Name = $"item {i}", Code = i % 2 }));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _query = _db.Items.Where(i => i.Code == 0).OrderBy(i => i.Id);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public Task<List<Item>> Direct()
    {
        return _query.AsNoTracking().ToListAsync();
    }

    [Benchmark]
    public QueryKey CommandAndKey()
    {
        using var command = _query.CreateDbCommand();
        return DbCommandKey.Of(command, "list");
    }

    [Benchmark]
    public string[] TableTagsOnly()
    {
        return TableTags.ForQuery(_query.Expression, "scope");
    }

    [Benchmark]
    public ValueTask InvalidateOnly()
    {
        return _query.InvalidateCacheAsync(CancellationToken.None);
    }

    [Benchmark]
    public async ValueTask<IReadOnlyList<Item>> StoreMissAroundDirect()
    {
        var key = new QueryKey("breakdown");
        await QueryCacheStore.RemoveAsync<IReadOnlyList<Item>>(key, CancellationToken.None);
        return await QueryCacheStore.GetOrAddAsync(key, Expiration, async ct => (IReadOnlyList<Item>)(await _query.AsNoTracking().ToListAsync(ct)).AsReadOnly(), CancellationToken.None);
    }

    [Benchmark]
    public async Task<List<Item>> DirectAfterCommand()
    {
        using (_query.CreateDbCommand())
        {
        }
        return await _query.AsNoTracking().ToListAsync();
    }

    private int _floor;

    [Benchmark]
    public Task<List<Item>> DirectVarying()
    {
        var floor = --_floor;
        return _db.Items.Where(i => i.Code == 0 && i.Id > floor).OrderBy(i => i.Id).AsNoTracking().ToListAsync();
    }

    [Benchmark]
    public ValueTask<IReadOnlyList<Item>> MissVarying()
    {
        var floor = --_floor;
        return _db.Items.Where(i => i.Code == 0 && i.Id > floor).OrderBy(i => i.Id).ToListCachedAsync(Expiration, CancellationToken.None);
    }

    [Benchmark]
    public async ValueTask<IReadOnlyList<Item>> FullMiss()
    {
        await _query.InvalidateCacheAsync(CancellationToken.None);
        return await _query.ToListCachedAsync(Expiration, CancellationToken.None);
    }
}
