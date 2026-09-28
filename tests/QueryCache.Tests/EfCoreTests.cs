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
    public async Task Second_call_is_served_from_cache_until_invalidated()
    {
        await using var db = await NewDb(new Item { Id = 1, Name = "a" });
        var query = db.Items.Where(i => i.Id == 1);

        var first = await query.ToListCachedAsync(Minute, CancellationToken.None);
        await db.Items.ExecuteDeleteAsync();
        var second = await query.ToListCachedAsync(Minute, CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);
        Assert.True(query.InvalidateCache());
        Assert.Empty(await query.ToListCachedAsync(Minute, CancellationToken.None));
        Assert.False(query.InvalidateCache());
    }

    [Fact]
    public async Task Cached_entities_are_not_tracked()
    {
        await using var db = await NewDb(new Item { Id = 1 });

        await db.Items.ToListCachedAsync(Minute, CancellationToken.None);

        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Same_query_on_different_databases_is_cached_separately()
    {
        await using var db1 = await NewDb(new Item { Id = 1, Name = "db1" });
        await using var db2 = await NewDb(new Item { Id = 1, Name = "db2" });

        var first = await db1.Items.ToListCachedAsync(Minute, CancellationToken.None);
        var second = await db2.Items.ToListCachedAsync(Minute, CancellationToken.None);

        Assert.Equal("db1", Assert.Single(first).Name);
        Assert.Equal("db2", Assert.Single(second).Name);
    }

    [Fact]
    public async Task Native_Include_and_AsSplitQuery_are_honored()
    {
        await using var db = await NewDb(new Item { Id = 1, Tags = { new Tag { Id = 1 } } });
        db.Commands.Count = 0;

        var items = await db.Items.Include(i => i.Tags).AsSplitQuery().ToListCachedAsync(Minute, CancellationToken.None);

        Assert.Single(Assert.Single(items).Tags);
        Assert.Equal(2, db.Commands.Count);
    }

    [Fact]
    public async Task Value_type_projections_are_cached()
    {
        await using var db = await NewDb(new Item { Id = 1, Code = 7 });
        var query = db.Items.Select(i => i.Code);

        await query.ToListCachedAsync(Minute, CancellationToken.None);
        await db.Items.ExecuteDeleteAsync();

        Assert.Equal([7], await query.ToListCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task ToDictionaryCachedAsync_with_different_key_selectors_uses_each_selector()
    {
        await using var db = await NewDb(new Item { Id = 1, Code = 100 });

        var byId = await db.Items.ToDictionaryCachedAsync(i => i.Id, Minute, CancellationToken.None);
        var byCode = await db.Items.ToDictionaryCachedAsync(i => i.Code, Minute, CancellationToken.None);
        var doubled = await db.Items.ToDictionaryCachedAsync(i => i.Code, i => i.Id * 2, Minute, CancellationToken.None);

        Assert.Equal([1], byId.Keys);
        Assert.Equal([100], byCode.Keys);
        Assert.Equal(2, doubled[100]);
    }

    [Fact]
    public async Task FirstOrDefaultCachedAsync_is_served_from_cache_until_invalidated()
    {
        await using var db = await NewDb(new Item { Id = 1, Name = "a" });
        var query = db.Items.Where(i => i.Id == 1);

        await query.FirstOrDefaultCachedAsync(Minute, CancellationToken.None);
        await db.Items.ExecuteDeleteAsync();

        Assert.NotNull(await query.FirstOrDefaultCachedAsync(Minute, CancellationToken.None));
        Assert.True(query.InvalidateCache());
        Assert.Null(await query.FirstOrDefaultCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task FirstCachedAsync_throws_on_empty()
    {
        await using var db = await NewDb();

        Assert.Null(await db.Items.FirstOrDefaultCachedAsync(Minute, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Items.FirstCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Results_without_rows_are_not_cached()
    {
        await using var db = await NewDb();

        Assert.Null(await db.Items.FirstOrDefaultCachedAsync(Minute, CancellationToken.None));
        Assert.False(await db.Items.AnyCachedAsync(Minute, CancellationToken.None));
        Assert.Equal(0, await db.Items.CountCachedAsync(Minute, CancellationToken.None));
        db.Items.Add(new Item { Id = 1 });
        await db.SaveChangesAsync();

        Assert.NotNull(await db.Items.FirstOrDefaultCachedAsync(Minute, CancellationToken.None));
        Assert.True(await db.Items.AnyCachedAsync(Minute, CancellationToken.None));
        Assert.Equal(1, await db.Items.CountCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task AnyCachedAsync_and_CountCachedAsync_are_served_from_cache_until_invalidated()
    {
        await using var db = await NewDb(new Item { Id = 1 }, new Item { Id = 2 });

        Assert.True(await db.Items.AnyCachedAsync(Minute, CancellationToken.None));
        Assert.Equal(2, await db.Items.CountCachedAsync(Minute, CancellationToken.None));
        await db.Items.ExecuteDeleteAsync();

        Assert.True(await db.Items.AnyCachedAsync(Minute, CancellationToken.None));
        Assert.Equal(2, await db.Items.CountCachedAsync(Minute, CancellationToken.None));
        Assert.True(db.Items.InvalidateCache());
        Assert.False(await db.Items.AnyCachedAsync(Minute, CancellationToken.None));
        Assert.Equal(0, await db.Items.CountCachedAsync(Minute, CancellationToken.None));
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
