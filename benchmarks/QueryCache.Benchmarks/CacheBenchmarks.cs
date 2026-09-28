using BenchmarkDotNet.Attributes;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QueryCache.Dapper;
using QueryCache.EFCore;

namespace QueryCache.Benchmarks
{
    public class Item
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Code { get; set; }
    }

    public class BenchDb(SqliteConnection connection) : DbContext(new DbContextOptionsBuilder<BenchDb>().UseSqlite(connection).UseQueryCacheInvalidation().Options)
    {
        public DbSet<Item> Items => Set<Item>();
    }

    /// <summary>
    /// Direct query vs cache hit vs cache miss, for EF Core and Dapper, over <see cref="Rows"/> matching rows.
    /// <see cref="Setup"/> first checks the cache returns the same rows as the database and serves repeats without querying.
    /// </summary>
    [MemoryDiagnoser]
    [CategoriesColumn]
    [GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
    public class CacheBenchmarks
    {
        private static readonly TimeSpan Expiration = TimeSpan.FromHours(1);

        private SqliteConnection _connection = null!;
        private BenchDb _db = null!;
        private IQueryable<Item> _query = null!;
        private DapperCacheQuery<Item> _dapper = null!;
        private CommandDefinition _command;

        [Params(10, 1_000)]
        public int Rows { get; set; }

        [GlobalSetup]
        public async Task Setup()
        {
            _connection = new SqliteConnection($"Data Source=bench-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            await _connection.OpenAsync();
            _db = new BenchDb(_connection);
            await _db.Database.EnsureCreatedAsync();
            _db.Items.AddRange(Enumerable.Range(1, Rows * 2).Select(i => new Item { Id = i, Name = $"item {i}", Code = i % 2 }));
            await _db.SaveChangesAsync();
            _db.ChangeTracker.Clear();

            _query = _db.Items.Where(i => i.Code == 0).OrderBy(i => i.Id);
            _command = new CommandDefinition("select Id, Name, Code from Items where Code = @Code order by Id", new { Code = 0 });
            _dapper = _connection.ToCacheQuery<Item>(_command);

            await Validate();
        }

        private async Task Validate()
        {
            var direct = await _query.AsNoTracking().Select(i => i.Id).ToListAsync();
            var efFirst = await _query.ToListCachedAsync(Expiration, CancellationToken.None);
            var efSecond = await _query.ToListCachedAsync(Expiration, CancellationToken.None);
            Check(direct.SequenceEqual(efFirst.Select(i => i.Id)), "EF cached rows differ from the database");
            Check(ReferenceEquals(efFirst, efSecond), "EF repeat was not served from the cache");

            var dapperFirst = await _dapper.QueryAsync(Expiration, CancellationToken.None);
            var dapperSecond = await _dapper.QueryAsync(Expiration, CancellationToken.None);
            Check(direct.SequenceEqual(dapperFirst.Select(i => i.Id)), "Dapper cached rows differ from the database");
            Check(ReferenceEquals(dapperFirst, dapperSecond), "Dapper repeat was not served from the cache");

            _db.Items.Add(new Item { Id = Rows * 2 + 1, Code = 0 });
            await _db.SaveChangesAsync();
            _db.ChangeTracker.Clear();
            var afterSave = await _query.ToListCachedAsync(Expiration, CancellationToken.None);
            Check(afterSave.Count == direct.Count + 1, "SaveChanges did not invalidate the EF entry");
            _db.Items.Remove(_db.Items.Single(i => i.Id == Rows * 2 + 1));
            await _db.SaveChangesAsync();
            _db.ChangeTracker.Clear();
            await _dapper.InvalidateCacheAsync(CancellationToken.None);

            Console.WriteLine($"// Cache validated for {Rows} rows: same rows as the database, repeats served from cache, SaveChanges invalidates.");
        }

        private static void Check(bool condition, string failure)
        {
            if (!condition)
            {
                throw new InvalidOperationException(failure);
            }
        }

        [GlobalCleanup]
        public async Task Cleanup()
        {
            await _db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        [Benchmark(Baseline = true), BenchmarkCategory("EF")]
        public Task<List<Item>> Ef_Direct()
        {
            return _query.AsNoTracking().ToListAsync();
        }

        [Benchmark, BenchmarkCategory("EF")]
        public ValueTask<IReadOnlyList<Item>> Ef_CacheHit()
        {
            return _query.ToListCachedAsync(Expiration, CancellationToken.None);
        }

        [Benchmark, BenchmarkCategory("EF")]
        public async ValueTask<IReadOnlyList<Item>> Ef_CacheMiss()
        {
            await _query.InvalidateCacheAsync(CancellationToken.None);
            return await _query.ToListCachedAsync(Expiration, CancellationToken.None);
        }

        [Benchmark(Baseline = true), BenchmarkCategory("Dapper")]
        public async Task<List<Item>> Dapper_Direct()
        {
            return (await _connection.QueryAsync<Item>(_command)).AsList();
        }

        [Benchmark, BenchmarkCategory("Dapper")]
        public ValueTask<IReadOnlyList<Item>> Dapper_CacheHit()
        {
            return _dapper.QueryAsync(Expiration, CancellationToken.None);
        }

        [Benchmark, BenchmarkCategory("Dapper")]
        public async ValueTask<IReadOnlyList<Item>> Dapper_CacheMiss()
        {
            await _dapper.InvalidateCacheAsync(CancellationToken.None);
            return await _dapper.QueryAsync(Expiration, CancellationToken.None);
        }
    }
}
