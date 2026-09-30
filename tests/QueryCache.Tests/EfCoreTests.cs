using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QueryCache.EFCore;
using QueryCache.EFCore.Keys;

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

    internal sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count;

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    public sealed class Note
    {
        public int Id { get; set; }
    }

    internal sealed class Db(SqliteConnection connection, CommandCounter counter)
        : DbContext(new DbContextOptionsBuilder<Db>().UseSqlite(connection).AddInterceptors(counter).UseQueryCacheInvalidation().Options)
    {
        public DbSet<Item> Items => Set<Item>();
        public DbSet<Note> Notes => Set<Note>();
        public CommandCounter Commands => counter;

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    internal static Task<Db> NewDb(params Item[] items)
    {
        return NewDb($"{Guid.NewGuid():N};Mode=Memory;Cache=Shared", items);
    }

    private static async Task<Db> NewDb(string dataSource, params Item[] items)
    {
        var db = await OpenDb(dataSource);
        await db.Database.EnsureCreatedAsync();
        db.Items.AddRange(items);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private static async Task<Db> OpenDb(string dataSource)
    {
        var connection = new SqliteConnection($"Data Source={dataSource}");
        await connection.OpenAsync();
        return new Db(connection, new CommandCounter());
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
        await query.InvalidateCacheAsync(CancellationToken.None);
        Assert.Empty(await query.ToListCachedAsync(Minute, CancellationToken.None));
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
        await query.InvalidateCacheAsync(CancellationToken.None);
        Assert.Null(await query.FirstOrDefaultCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task FirstCachedAsync_returns_the_first_row()
    {
        await using var db = await NewDb(new Item { Id = 1 });

        Assert.Equal(1, (await db.Items.FirstCachedAsync(Minute, CancellationToken.None)).Id);
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
        await db.Items.InvalidateCacheAsync(CancellationToken.None);
        Assert.False(await db.Items.AnyCachedAsync(Minute, CancellationToken.None));
        Assert.Equal(0, await db.Items.CountCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Sum_Max_and_Count_over_the_same_query_are_cached_separately_until_invalidated()
    {
        await using var db = await NewDb(new Item { Id = 1, Code = 3 }, new Item { Id = 2, Code = 4 });
        var codes = db.Items.Select(i => i.Code);

        Assert.Equal(7, await codes.SumCachedAsync(Minute, CancellationToken.None));
        Assert.Equal(4, await codes.MaxCachedAsync(Minute, CancellationToken.None));
        Assert.Equal(2, await codes.CountCachedAsync(Minute, CancellationToken.None));
        await db.Items.ExecuteDeleteAsync();

        Assert.Equal(7, await codes.SumCachedAsync(Minute, CancellationToken.None));
        Assert.Equal(4, await codes.MaxCachedAsync(Minute, CancellationToken.None));
        Assert.Equal(2, await codes.CountCachedAsync(Minute, CancellationToken.None));
        await codes.InvalidateCacheAsync(CancellationToken.None);
        Assert.Equal(0, await codes.SumCachedAsync(Minute, CancellationToken.None));
        Assert.Null(await codes.Select(c => (int?)c).MaxCachedAsync(Minute, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await codes.MaxCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Sum_of_an_unsupported_type_is_rejected()
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () => await Enumerable.Empty<string>().AsQueryable().SumCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task SingleOrDefaultCachedAsync_does_not_reuse_the_first_row_entry()
    {
        await using var db = await NewDb(new Item { Id = 1 }, new Item { Id = 2 });

        Assert.NotNull(await db.Items.FirstOrDefaultCachedAsync(Minute, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await db.Items.SingleOrDefaultCachedAsync(Minute, CancellationToken.None));

        var one = db.Items.Where(i => i.Id == 1);
        Assert.Equal(1, (await one.SingleOrDefaultCachedAsync(Minute, CancellationToken.None))?.Id);
        await db.Items.ExecuteDeleteAsync();
        Assert.Equal(1, (await one.SingleOrDefaultCachedAsync(Minute, CancellationToken.None))?.Id);
        await one.InvalidateCacheAsync(CancellationToken.None);
        Assert.Null(await one.SingleOrDefaultCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public void Array_parameters_are_keyed_by_value()
    {
        static SqliteCommand Command(byte[] value)
        {
            var command = new SqliteCommand("select @p");
            command.Parameters.AddWithValue("@p", value);
            return command;
        }

        Assert.Equal(DbCommandKey.Of(Command([1, 2]), "list"), DbCommandKey.Of(Command([1, 2]), "list"));
        Assert.NotEqual(DbCommandKey.Of(Command([1, 2]), "list"), DbCommandKey.Of(Command([2, 1]), "list"));
    }

    [Fact]
    public async Task Cached_lists_are_read_only()
    {
        await using var db = await NewDb(new Item { Id = 1 });

        var items = await db.Items.ToListCachedAsync(Minute, CancellationToken.None);

        Assert.Throws<NotSupportedException>(() => ((IList<Item>)items).Add(new Item { Id = 2 }));
    }

    [Fact]
    public async Task Reads_inside_a_transaction_are_not_cached()
    {
        await using var db = await NewDb();
        var transaction = await db.Database.BeginTransactionAsync();
        db.Items.Add(new Item { Id = 1 });
        await db.SaveChangesAsync();

        Assert.Single(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));
        await transaction.RollbackAsync();
        await transaction.DisposeAsync();
        db.ChangeTracker.Clear();

        Assert.Empty(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task SaveChanges_invalidates_cached_queries_that_read_the_changed_table()
    {
        await using var db = await NewDb(new Item { Id = 1 });
        var withTags = db.Items.Include(i => i.Tags);
        Assert.Empty(Assert.Single(await withTags.ToListCachedAsync(Minute, CancellationToken.None)).Tags);
        Assert.Single(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));

        db.Add(new Tag { Id = 1, ItemId = 1 });
        db.Items.Add(new Item { Id = 2 });
        await db.SaveChangesAsync();

        Assert.Single(Assert.Single(await withTags.ToListCachedAsync(Minute, CancellationToken.None), i => i.Id == 1).Tags);
        Assert.Equal(2, (await db.Items.ToListCachedAsync(Minute, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task SaveChanges_keeps_cached_queries_on_other_tables()
    {
        await using var db = await NewDb(new Item { Id = 1 });
        await db.Items.ToListCachedAsync(Minute, CancellationToken.None);
        await db.Items.ExecuteDeleteAsync();

        db.Notes.Add(new Note { Id = 1 });
        await db.SaveChangesAsync();

        Assert.Single(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_invalidates_what_other_connections_cached_while_the_transaction_was_open(bool sync)
    {
        var file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        try
        {
            await using var writer = await NewDb(file, new Item { Id = 1 });
            await using var reader = await OpenDb(file);
            await using var transaction = await writer.Database.BeginTransactionAsync();
            writer.Items.Add(new Item { Id = 2 });
            if (sync)
            {
                writer.SaveChanges();
            }
            else
            {
                await writer.SaveChangesAsync();
            }

            Assert.Single(await reader.Items.ToListCachedAsync(Minute, CancellationToken.None));
            if (sync)
            {
                transaction.Commit();
            }
            else
            {
                await transaction.CommitAsync();
            }

            Assert.Equal(2, (await reader.Items.ToListCachedAsync(Minute, CancellationToken.None)).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Synchronous_SaveChanges_invalidates_cached_queries()
    {
        await using var db = await NewDb(new Item { Id = 1, Name = "a" });
        var named = db.Items.Where(i => i.Name == "a");
        Assert.Single(await named.ToListCachedAsync(Minute, CancellationToken.None));

        db.Items.Add(new Item { Id = 2, Name = "a" });
        db.SaveChanges();

        Assert.Equal(2, (await named.ToListCachedAsync(Minute, CancellationToken.None)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rolled_back_saves_leave_the_cache_matching_the_database(bool sync)
    {
        await using var db = await NewDb(new Item { Id = 1 });
        Assert.Single(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));
        var transaction = await db.Database.BeginTransactionAsync();
        db.Items.Add(new Item { Id = 2 });
        await db.SaveChangesAsync();

        if (sync)
        {
            transaction.Rollback();
        }
        else
        {
            await transaction.RollbackAsync();
        }
        await transaction.DisposeAsync();

        Assert.Single(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_saves_keep_cached_entries(bool sync)
    {
        await using var db = await NewDb(new Item { Id = 1 });
        Assert.Single(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));
        db.Items.Add(new Item { Id = 1 });

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            if (sync)
            {
                db.SaveChanges();
            }
            else
            {
                await db.SaveChangesAsync();
            }
        });

        var before = db.Commands.Count;
        Assert.Single(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));
        Assert.Equal(before, db.Commands.Count);
    }

    [Fact]
    public async Task ToDictionaryCachedAsync_with_a_value_selector_uses_the_key_comparer()
    {
        await using var db = await NewDb(new Item { Id = 1, Name = "a", Code = 7 });

        var codeByName = await db.Items.ToDictionaryCachedAsync(i => i.Name, i => i.Code, StringComparer.OrdinalIgnoreCase, Minute, CancellationToken.None);

        Assert.Equal(7, codeByName["A"]);
    }

    [Fact]
    public async Task ToHashSetCachedAsync_shares_the_list_entry_and_applies_the_comparer()
    {
        await using var db = await NewDb(new Item { Id = 1, Name = "a" }, new Item { Id = 2, Name = "A" });
        var names = db.Items.Select(i => i.Name);

        var exact = await names.ToHashSetCachedAsync(Minute, CancellationToken.None);
        await db.Items.ExecuteDeleteAsync();
        var ignoringCase = await names.ToHashSetCachedAsync(StringComparer.OrdinalIgnoreCase, Minute, CancellationToken.None);

        Assert.Equal(2, exact.Count);
        Assert.Single(ignoringCase);
        Assert.NotSame(exact, await names.ToHashSetCachedAsync(Minute, CancellationToken.None));
        await names.InvalidateCacheAsync(CancellationToken.None);
        Assert.Empty(await names.ToHashSetCachedAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task ToDictionaryCachedAsync_uses_the_key_comparer()
    {
        await using var db = await NewDb(new Item { Id = 1, Name = "a" });

        var byName = await db.Items.ToDictionaryCachedAsync(i => i.Name, StringComparer.OrdinalIgnoreCase, Minute, CancellationToken.None);

        Assert.Equal(1, byName["A"].Id);
    }

    [Fact]
    public void Expressions_without_an_entity_query_root_have_no_tags()
    {
        Assert.Empty(TableTags.ForQuery(System.Linq.Expressions.Expression.Constant(1), "scope"));
    }
}
