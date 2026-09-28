using System.Data.Common;
using System.Diagnostics;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QueryCache.Dapper;
using QueryCache.EFCore;

namespace QueryCache.IntegrationTests;

public sealed class Item
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Code { get; set; }
    public List<Tag> Tags { get; } = [];
}

public sealed class Tag
{
    public int Id { get; set; }
    public int ItemId { get; set; }
}

public sealed class CommandCounter : DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}

public sealed class TestDb(DbContextOptions options) : DbContext(options)
{
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Tag> Tags => Set<Tag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Item>().Property(i => i.Id).ValueGeneratedNever();
        modelBuilder.Entity<Tag>().Property(t => t.Id).ValueGeneratedNever();
    }
}

/// <summary>The same scenarios against every real database engine: things SQLite cannot show.</summary>
public abstract class CacheIntegrationTests<TServer>(TServer server) where TServer : DatabaseServer
{
    protected static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    protected TServer Server => server;

    protected TestDb NewDb(string connectionString, CommandCounter? counter = null)
    {
        var builder = server.Use(new DbContextOptionsBuilder<TestDb>(), connectionString).UseQueryCacheInvalidation();
        if (counter is not null)
        {
            builder.AddInterceptors(counter);
        }
        return new TestDb(builder.Options);
    }

    protected async Task<string> NewDatabaseAsync(params Item[] items)
    {
        var connectionString = await server.CreateDatabaseAsync();
        await using var db = NewDb(connectionString);
        await db.Database.EnsureCreatedAsync();
        db.Items.AddRange(items);
        await db.SaveChangesAsync();
        return connectionString;
    }

    [Fact]
    public async Task Repeats_are_served_from_cache_until_SaveChanges_writes_the_table()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1 });
        var counter = new CommandCounter();
        await using var db = NewDb(connectionString, counter);

        await db.Items.ToListCachedAsync(Minute, CancellationToken.None);
        await db.Items.ToListCachedAsync(Minute, CancellationToken.None);
        Assert.Equal(1, counter.Count);

        db.Items.Add(new Item { Id = 2 });
        await db.SaveChangesAsync();

        Assert.Equal(2, (await db.Items.ToListCachedAsync(Minute, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Writes_to_an_included_table_invalidate_the_query()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1 });
        await using var db = NewDb(connectionString);
        var withTags = db.Items.Include(i => i.Tags);
        Assert.Empty(Assert.Single(await withTags.ToListCachedAsync(Minute, CancellationToken.None)).Tags);

        db.Tags.Add(new Tag { Id = 1, ItemId = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Single(Assert.Single(await withTags.ToListCachedAsync(Minute, CancellationToken.None)).Tags);
    }

    [Fact]
    public async Task Contains_over_a_new_list_each_call_hits_the_cache()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1 }, new Item { Id = 2 }, new Item { Id = 3 });
        var counter = new CommandCounter();
        await using var db = NewDb(connectionString, counter);

        for (var i = 0; i < 3; i++)
        {
            var ids = new List<int> { 1, 3 };
            Assert.Equal(2, (await db.Items.Where(item => ids.Contains(item.Id)).ToListCachedAsync(Minute, CancellationToken.None)).Count);
        }
        var other = new List<int> { 2 };
        Assert.Single(await db.Items.Where(item => other.Contains(item.Id)).ToListCachedAsync(Minute, CancellationToken.None));

        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public async Task Concurrent_misses_query_the_database_once()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1 });
        var counter = new CommandCounter();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            await using var db = NewDb(connectionString, counter);
            return await db.Items.ToListCachedAsync(Minute, CancellationToken.None);
        }));

        Assert.All(results, rows => Assert.Single(rows));
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task Ef_reads_inside_a_transaction_are_not_cached()
    {
        var connectionString = await NewDatabaseAsync();
        await using var db = NewDb(connectionString);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Items.Add(new Item { Id = 1 });
            await db.SaveChangesAsync();
            Assert.Single(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();

        Assert.Empty(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Dapper_reads_inside_a_transaction_are_not_cached()
    {
        var connectionString = await NewDatabaseAsync();
        await using var connection = Server.Connect(connectionString);
        await connection.OpenAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync("INSERT INTO \"Items\" (\"Id\", \"Name\", \"Code\") VALUES (1, 'a', 0)", transaction: transaction);
            Assert.Single(await connection.ToCacheQuery<int>(new CommandDefinition("SELECT \"Id\" FROM \"Items\"", transaction: transaction)).QueryAsync(Minute, CancellationToken.None));
            await transaction.RollbackAsync();
        }

        Assert.Empty(await connection.ToCacheQuery<int>(new CommandDefinition("SELECT \"Id\" FROM \"Items\"")).QueryAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Password_dropped_from_an_opened_connection_does_not_change_the_key()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1 });
        await using var connection = Server.Connect(connectionString);
        var before = connection.ConnectionString;
        var command = new CommandDefinition("SELECT \"Id\" FROM \"Items\"");

        Assert.Single(await connection.ToCacheQuery<int>(command).QueryAsync(Minute, CancellationToken.None));
        await using (var writer = Server.Connect(connectionString))
        {
            await writer.ExecuteAsync("DELETE FROM \"Items\"");
        }
        await connection.OpenAsync();

        Assert.NotEqual(before, connection.ConnectionString, StringComparer.Ordinal);
        Assert.Single(await connection.ToCacheQuery<int>(command).QueryAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_stops_a_running_query()
    {
        var connectionString = await NewDatabaseAsync();
        await using var connection = Server.Connect(connectionString);
        await connection.OpenAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<Exception>(async () => await connection.ToCacheQuery<int>(new CommandDefinition(Server.SlowScalarSql)).ExecuteScalarAsync(Minute, cts.Token));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"The query ran for {elapsed.Elapsed} instead of being cancelled.");
    }
}

public sealed class PostgresTests(PostgresServer server) : CacheIntegrationTests<PostgresServer>(server)
{
    [Fact]
    public async Task Commit_invalidates_what_other_connections_cached_while_the_transaction_was_open()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1 });
        await using var writer = NewDb(connectionString);
        await using var reader = NewDb(connectionString);
        await using var transaction = await writer.Database.BeginTransactionAsync();
        writer.Items.Add(new Item { Id = 2 });
        await writer.SaveChangesAsync();

        Assert.Single(await reader.Items.ToListCachedAsync(Minute, CancellationToken.None));
        await transaction.CommitAsync();

        Assert.Equal(2, (await reader.Items.ToListCachedAsync(Minute, CancellationToken.None)).Count);
    }
}

public sealed class SqlServerTests(SqlServerServer server) : CacheIntegrationTests<SqlServerServer>(server);
