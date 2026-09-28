using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QueryCache.EFCore;

namespace QueryCache.Tests;

public sealed class EfCoreTests
{
    public sealed class Item
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class Db(SqliteConnection connection) : DbContext(new DbContextOptionsBuilder<Db>().UseSqlite(connection).Options)
    {
        public DbSet<Item> Items => Set<Item>();
    }

    [Fact]
    public async Task Second_call_is_served_from_cache_and_scopes_by_connection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new Db(connection);
        await db.Database.EnsureCreatedAsync();
        db.Items.Add(new Item { Id = 1, Name = "a" });
        await db.SaveChangesAsync();

        var builder = new CacheQueryBuilder<Item>(db.Items).Where(i => i.Id == 1);
        var first = await builder.ToListAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        await db.Items.ExecuteDeleteAsync();
        var second = await builder.ToListAsync(TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);
        Assert.True(await builder.RemoveFromCacheAsync("ToListAsync", CancellationToken.None));
        Assert.Empty(await builder.ToListAsync(TimeSpan.FromMinutes(1), CancellationToken.None));
    }
}
