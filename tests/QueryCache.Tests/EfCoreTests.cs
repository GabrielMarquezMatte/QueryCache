using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QueryCache.EFCore;
using QueryCache.EFCore.Comparers;

namespace QueryCache.Tests;

public sealed class EfCoreTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

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
        public Item? Item { get; set; }
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class Db(SqliteConnection connection, CommandCounter counter) : DbContext(new DbContextOptionsBuilder<Db>().UseSqlite(connection).AddInterceptors(counter).Options)
    {
        public DbSet<Item> Items => Set<Item>();
        public CommandCounter Commands => counter;

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private static async Task<Db> NewDb(params Item[] items)
    {
        var connection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        var db = new Db(connection, new CommandCounter());
        await db.Database.EnsureCreatedAsync();
        db.Items.AddRange(items);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    [Fact]
    public async Task Second_call_is_served_from_cache_until_removed()
    {
        await using var db = await NewDb(new Item { Id = 1, Name = "a" });
        var builder = new CacheQueryBuilder<Item>(db.Items).Where(i => i.Id == 1);

        var first = await builder.ToListAsync(Minute, CancellationToken.None);
        await db.Items.ExecuteDeleteAsync();
        var second = await builder.ToListAsync(Minute, CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);
        Assert.True(await builder.RemoveFromCacheAsync("ToListAsync", CancellationToken.None));
        Assert.Empty(await builder.ToListAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task ToCacheQueryBuilder_extension_builds_a_cached_query()
    {
        await using var db = await NewDb(new Item { Id = 1 }, new Item { Id = 2 });
        var builder = db.Items.ToCacheQueryBuilder().Where(i => i.Id == 2);

        await builder.ToListAsync(Minute, CancellationToken.None);
        await db.Items.ExecuteDeleteAsync();

        Assert.Equal(2, Assert.Single(await builder.ToListAsync(Minute, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task Same_query_on_different_databases_is_cached_separately()
    {
        await using var db1 = await NewDb(new Item { Id = 1, Name = "db1" });
        await using var db2 = await NewDb(new Item { Id = 1, Name = "db2" });

        var first = await new CacheQueryBuilder<Item>(db1.Items).ToListAsync(Minute, CancellationToken.None);
        var second = await new CacheQueryBuilder<Item>(db2.Items).ToListAsync(Minute, CancellationToken.None);

        Assert.Equal("db1", Assert.Single(first).Name);
        Assert.Equal("db2", Assert.Single(second).Name);
    }

    [Fact]
    public async Task Cache_false_always_hits_the_database()
    {
        await using var db = await NewDb(new Item { Id = 1 });
        var builder = new CacheQueryBuilder<Item>(db.Items);

        await builder.ToListAsync(Minute, CancellationToken.None);
        await db.Items.ExecuteDeleteAsync();

        Assert.Empty(await builder.ToListAsync(cache: false, Minute, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AsSplitQuery_is_applied_with_and_without_cache(bool cache)
    {
        await using var db = await NewDb(new Item { Id = 1, Tags = { new Tag { Id = 1 } } });
        db.Commands.Count = 0;

        var items = await new CacheQueryBuilder<Item>(db.Items).Include(i => i.Tags).AsSplitQuery().ToListAsync(cache, Minute, CancellationToken.None);

        Assert.Single(Assert.Single(items).Tags);
        Assert.Equal(2, db.Commands.Count);
    }

    [Fact]
    public async Task ToDictionaryAsync_with_different_key_selectors_uses_each_selector()
    {
        await using var db = await NewDb(new Item { Id = 1, Code = 100 });
        var builder = new CacheQueryBuilder<Item>(db.Items);

        var byId = await builder.ToDictionaryAsync(i => i.Id, Minute, CancellationToken.None);
        var byCode = await builder.ToDictionaryAsync(i => i.Code, Minute, CancellationToken.None);
        var names = await builder.ToDictionaryAsync(i => i.Code, i => i.Id * 2, Minute, CancellationToken.None);

        Assert.Equal([1], byId.Keys);
        Assert.Equal([100], byCode.Keys);
        Assert.Equal(2, names[100]);
    }

    [Fact]
    public async Task FirstOrDefaultAsync_is_served_from_cache_until_removed()
    {
        await using var db = await NewDb(new Item { Id = 1, Name = "a" });
        var builder = new CacheQueryBuilder<Item>(db.Items).Where(i => i.Id == 1);

        await builder.FirstOrDefaultAsync(Minute, CancellationToken.None);
        await db.Items.ExecuteDeleteAsync();

        Assert.NotNull(await builder.FirstOrDefaultAsync(Minute, CancellationToken.None));
        Assert.True(await builder.RemoveFromCacheAsync("FirstOrDefaultAsync", CancellationToken.None));
        Assert.Null(await builder.FirstOrDefaultAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task FirstAsync_throws_on_empty_even_after_FirstOrDefaultAsync_cached_null()
    {
        await using var db = await NewDb();
        var builder = new CacheQueryBuilder<Item>(db.Items);

        Assert.Null(await builder.FirstOrDefaultAsync(Minute, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await builder.FirstAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task AnyAsync_is_served_from_cache_until_removed()
    {
        await using var db = await NewDb(new Item { Id = 1 });
        var builder = new CacheQueryBuilder<Item>(db.Items);

        Assert.True(await builder.AnyAsync(Minute, CancellationToken.None));
        await db.Items.ExecuteDeleteAsync();

        Assert.True(await builder.AnyAsync(Minute, CancellationToken.None));
        Assert.False(await builder.AnyAsync(cache: false, Minute, CancellationToken.None));
        Assert.True(await builder.RemoveFromCacheAsync("AnyAsync", CancellationToken.None));
        Assert.False(await builder.AnyAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task RemoveFromCacheAsync_rejects_unknown_operation()
    {
        await using var db = await NewDb();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new CacheQueryBuilder<Item>(db.Items).RemoveFromCacheAsync("CountAsync", CancellationToken.None));
    }

    [Fact]
    public async Task ThenBy_requires_a_preceding_OrderBy()
    {
        await using var db = await NewDb(new Item { Id = 1, Code = 2 }, new Item { Id = 2, Code = 1 });
        var builder = new CacheQueryBuilder<Item>(db.Items);

        Assert.Throws<InvalidOperationException>(() => builder.ThenBy(i => i.Id));
        Assert.Throws<InvalidOperationException>(() => builder.ThenByDescending(i => i.Id));
        var ordered = await builder.OrderBy(i => i.Name).ThenBy(i => i.Code).ToListAsync(Minute, CancellationToken.None);
        Assert.Equal([2, 1], ordered.Select(i => i.Id));
    }

    [Fact]
    public async Task ThenInclude_requires_a_preceding_Include()
    {
        await using var db = await NewDb();

        Assert.Throws<InvalidOperationException>(() => new CacheQueryBuilder<Item>(db.Items).Include(i => i.Tags).Where(i => i.Id > 0).ThenInclude<Tag, Item>(t => t.Item));
    }

    [Fact]
    public void Array_parameters_hash_by_value()
    {
        static SqliteCommand Command(byte[] value)
        {
            var command = new SqliteCommand("select @p");
            command.Parameters.AddWithValue("@p", value);
            return command;
        }

        Assert.Equal(DbCommandHasher.Hash(Command([1, 2])), DbCommandHasher.Hash(Command([1, 2])));
        Assert.NotEqual(DbCommandHasher.Hash(Command([1, 2])), DbCommandHasher.Hash(Command([2, 1])));
    }
}
