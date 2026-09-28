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

    [Fact]
    public void Anonymous_parameters_hash_by_value()
    {
        Assert.Equal(ParameterHasher.Hash(new { Id = 1, Ids = new[] { 1, 2 } }), ParameterHasher.Hash(new { Id = 1, Ids = new[] { 1, 2 } }));
        Assert.NotEqual(ParameterHasher.Hash(new { Id = 1 }), ParameterHasher.Hash(new { Id = 2 }));
    }

    [Fact]
    public void DynamicParameters_hash_by_value()
    {
        var a = new DynamicParameters(new { Id = 1 });
        var b = new DynamicParameters(new { Id = 1 });
        Assert.Equal(ParameterHasher.Hash(a), ParameterHasher.Hash(b));
    }

    [Fact]
    public void DynamicParameters_with_different_template_values_hash_differently()
    {
        Assert.NotEqual(ParameterHasher.Hash(new DynamicParameters(new { Id = 1 })), ParameterHasher.Hash(new DynamicParameters(new { Id = 2 })));
    }

    [Fact]
    public void Dictionary_parameters_hash_by_value()
    {
        static Dictionary<string, object?> Params(int id) => new(StringComparer.Ordinal) { ["Id"] = id };
        Assert.Equal(ParameterHasher.Hash(Params(1)), ParameterHasher.Hash(Params(1)));
        Assert.NotEqual(ParameterHasher.Hash(Params(1)), ParameterHasher.Hash(Params(2)));
    }

    [Fact]
    public void DbString_parameters_hash_by_value()
    {
        Assert.Equal(ParameterHasher.Hash(new { Name = new DbString { Value = "a" } }), ParameterHasher.Hash(new { Name = new DbString { Value = "a" } }));
        Assert.NotEqual(ParameterHasher.Hash(new { Name = new DbString { Value = "a" } }), ParameterHasher.Hash(new { Name = new DbString { Value = "b" } }));
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
        Assert.True(query.RemoveFromCache(nameof(query.QueryFirstOrDefaultAsync)));
        Assert.Equal(0, await query.QueryFirstOrDefaultAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Cache_false_always_hits_the_database()
    {
        await using var connection = await NewConnection("create table t(id int); insert into t values (1);");
        var query = Query(connection, new CommandDefinition("select id from t"));

        await query.QueryAsync(Minute, CancellationToken.None);
        await connection.ExecuteAsync("delete from t");

        Assert.Empty(await query.QueryAsync(cache: false, Minute, CancellationToken.None));
        Assert.True(query.RemoveFromCache(nameof(query.QueryAsync)));
        Assert.Throws<InvalidOperationException>(() => query.RemoveFromCache("Other"));
    }

    [Fact]
    public async Task ExecuteScalarAsync_is_served_from_cache_until_removed()
    {
        await using var connection = await NewConnection("create table t(id int); insert into t values (9);");
        var query = Query(connection, new CommandDefinition("select max(id) from t"));

        Assert.Equal(9, await query.ExecuteScalarAsync(Minute, CancellationToken.None));
        await connection.ExecuteAsync("delete from t");

        Assert.Equal(9, await query.ExecuteScalarAsync(Minute, CancellationToken.None));
        Assert.True(query.RemoveFromCache(nameof(query.ExecuteScalarAsync)));
        Assert.Equal(0, await query.ExecuteScalarAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_token_reaches_the_database_without_cache()
    {
        await using var connection = await NewConnection("create table t(id int);");
        var query = Query(connection, new CommandDefinition("select id from t"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await query.QueryAsync(cache: false, Minute, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await query.QueryFirstOrDefaultAsync(cache: false, Minute, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await query.ExecuteScalarAsync(cache: false, Minute, cts.Token));
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
