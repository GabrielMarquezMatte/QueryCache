using Dapper;
using Microsoft.Data.Sqlite;
using QueryCache.Dapper;

namespace QueryCache.Tests;

public sealed class DapperTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static async Task<SqliteConnection> NewConnection(string setupSql)
    {
        var connection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        await connection.ExecuteAsync(setupSql);
        return connection;
    }

    private static DapperCacheQuery<int> Query(SqliteConnection connection, CommandDefinition command)
    {
        return new DapperCacheQuery<int>(connection, command);
    }

    private static QueryKey Key(object parameters)
    {
        return new QueryKey(string.Empty, ParameterValues.Of(parameters));
    }

    [Fact]
    public async Task Reads_inside_a_transaction_are_not_cached()
    {
        await using var connection = await NewConnection("create table t(id int);");
        var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("insert into t values (1)", transaction: transaction);

        Assert.Equal([1], await Query(connection, new CommandDefinition("select id from t", transaction: transaction)).QueryAsync(Minute, CancellationToken.None));
        await transaction.RollbackAsync();
        await transaction.DisposeAsync();

        Assert.Empty(await Query(connection, new CommandDefinition("select id from t")).QueryAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Cached_lists_are_read_only()
    {
        await using var connection = await NewConnection("create table t(id int); insert into t values (1);");

        var rows = await Query(connection, new CommandDefinition("select id from t")).QueryAsync(Minute, CancellationToken.None);

        Assert.Throws<NotSupportedException>(() => ((IList<int>)rows).Add(2));
    }

    [Fact]
    public void Anonymous_parameters_are_keyed_by_value()
    {
        Assert.Equal(Key(new { Id = 1, Ids = new[] { 1, 2 } }), Key(new { Id = 1, Ids = new[] { 1, 2 } }));
        Assert.NotEqual(Key(new { Id = 1 }), Key(new { Id = 2 }));
    }

    [Fact]
    public void DynamicParameters_are_keyed_by_value()
    {
        var a = new DynamicParameters(new { Id = 1 });
        var b = new DynamicParameters(new { Id = 1 });
        Assert.Equal(Key(a), Key(b));
    }

    [Fact]
    public void DynamicParameters_with_different_template_values_are_keyed_differently()
    {
        Assert.NotEqual(Key(new DynamicParameters(new { Id = 1 })), Key(new DynamicParameters(new { Id = 2 })));
    }

    [Fact]
    public void Dictionary_parameters_are_keyed_by_value()
    {
        static Dictionary<string, object?> Params(int id) => new(StringComparer.Ordinal) { ["Id"] = id };
        Assert.Equal(Key(Params(1)), Key(Params(1)));
        Assert.NotEqual(Key(Params(1)), Key(Params(2)));
    }

    [Fact]
    public void DbString_parameters_are_keyed_by_value()
    {
        Assert.Equal(Key(new { Name = new DbString { Value = "a" } }), Key(new { Name = new DbString { Value = "a" } }));
        Assert.NotEqual(Key(new { Name = new DbString { Value = "a" } }), Key(new { Name = new DbString { Value = "b" } }));
    }

    [Fact]
    public async Task Second_call_is_served_from_cache()
    {
        await using var connection = await NewConnection("create table t(id int); insert into t values (1);");
        var command = new CommandDefinition("select id from t where id = @Id", new { Id = 1 });

        var first = (await Query(connection, command).QueryAsync(Minute, CancellationToken.None)).ToList();
        await connection.ExecuteAsync("delete from t");
        var second = (await Query(connection, command).QueryAsync(Minute, CancellationToken.None)).ToList();

        Assert.Equal([1], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ToCacheQuery_extension_builds_a_cached_query()
    {
        await using var connection = await NewConnection("create table t(id int); insert into t values (3);");
        var query = connection.ToCacheQuery<int>(new CommandDefinition("select id from t"));

        await query.QueryAsync(Minute, CancellationToken.None);
        await connection.ExecuteAsync("delete from t");

        Assert.Equal([3], await query.QueryAsync(Minute, CancellationToken.None));
        Assert.True(query == Query(connection, new CommandDefinition("select id from t")));
    }

    [Fact]
    public async Task DynamicParameters_with_different_values_return_their_own_rows()
    {
        await using var connection = await NewConnection("create table t(id int); insert into t values (1), (2);");

        var one = await Query(connection, new CommandDefinition("select id from t where id = @Id", new DynamicParameters(new { Id = 1 }))).QueryAsync(Minute, CancellationToken.None);
        var two = await Query(connection, new CommandDefinition("select id from t where id = @Id", new DynamicParameters(new { Id = 2 }))).QueryAsync(Minute, CancellationToken.None);

        Assert.Equal([1], one);
        Assert.Equal([2], two);
    }

    [Fact]
    public async Task Unbuffered_query_returns_all_rows_on_miss_and_hit()
    {
        await using var connection = await NewConnection("create table t(id int); insert into t values (1), (2), (3);");
        var query = Query(connection, new CommandDefinition("select id from t", flags: CommandFlags.None));

        var first = (await query.QueryAsync(Minute, CancellationToken.None)).ToList();
        var second = (await query.QueryAsync(Minute, CancellationToken.None)).ToList();

        Assert.Equal([1, 2, 3], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task QueryFirstOrDefaultAsync_is_served_from_cache_until_removed()
    {
        await using var connection = await NewConnection("create table t(id int); insert into t values (5);");
        var query = Query(connection, new CommandDefinition("select id from t"));

        Assert.Equal(5, await query.QueryFirstOrDefaultAsync(Minute, CancellationToken.None));
        await connection.ExecuteAsync("delete from t");

        Assert.Equal(5, await query.QueryFirstOrDefaultAsync(Minute, CancellationToken.None));
        await query.InvalidateCacheAsync(CancellationToken.None);
        Assert.Equal(0, await query.QueryFirstOrDefaultAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidateCache_removes_list_and_single_value_entries()
    {
        await using var connection = await NewConnection("create table t(id int); insert into t values (4);");
        var query = Query(connection, new CommandDefinition("select id from t"));

        await query.QueryAsync(Minute, CancellationToken.None);
        await query.QueryFirstOrDefaultAsync(Minute, CancellationToken.None);
        await connection.ExecuteAsync("delete from t");

        await query.InvalidateCacheAsync(CancellationToken.None);
        Assert.Empty(await query.QueryAsync(Minute, CancellationToken.None));
        Assert.Equal(0, await query.QueryFirstOrDefaultAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Results_without_rows_are_not_cached()
    {
        await using var connection = await NewConnection("create table t(id int);");
        var first = Query(connection, new CommandDefinition("select id from t"));
        var scalar = Query(connection, new CommandDefinition("select max(id) from t"));

        Assert.Equal(0, await first.QueryFirstOrDefaultAsync(Minute, CancellationToken.None));
        Assert.Equal(0, await scalar.ExecuteScalarAsync(Minute, CancellationToken.None));
        await connection.ExecuteAsync("insert into t values (6)");

        Assert.Equal(6, await first.QueryFirstOrDefaultAsync(Minute, CancellationToken.None));
        Assert.Equal(6, await scalar.ExecuteScalarAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteScalarAsync_is_served_from_cache_until_removed()
    {
        await using var connection = await NewConnection("create table t(id int); insert into t values (9);");
        var query = Query(connection, new CommandDefinition("select max(id) from t"));

        Assert.Equal(9, await query.ExecuteScalarAsync(Minute, CancellationToken.None));
        await connection.ExecuteAsync("delete from t");

        Assert.Equal(9, await query.ExecuteScalarAsync(Minute, CancellationToken.None));
        await query.InvalidateCacheAsync(CancellationToken.None);
        Assert.Equal(0, await query.ExecuteScalarAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Equality_operators_handle_null()
    {
        await using var connection = await NewConnection("select 1");
        var query = Query(connection, new CommandDefinition("select 1"));
        DapperCacheQuery<int>? none = null;

        Assert.False(none == query);
        Assert.False(query == none);
        Assert.True(query != none);
        Assert.True(query == Query(connection, new CommandDefinition("select 1")));
    }
}
