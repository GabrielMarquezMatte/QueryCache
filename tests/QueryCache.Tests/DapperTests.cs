using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using QueryCache.Dapper;

namespace QueryCache.Tests;

public sealed class DapperTests
{
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
    public async Task Second_call_is_served_from_cache()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await connection.ExecuteAsync("create table t(id int); insert into t values (1);");
        var query = () => new DapperCacheQuery<int>(connection, new CommandDefinition("select id from t where id = @Id", new { Id = 1 }), NullLogger<DapperCacheQuery<int>>.Instance);

        var first = (await query().QueryAsync(TimeSpan.FromMinutes(1), CancellationToken.None)).ToList();
        await connection.ExecuteAsync("delete from t");
        var second = (await query().QueryAsync(TimeSpan.FromMinutes(1), CancellationToken.None)).ToList();

        Assert.Equal([1], first);
        Assert.Equal(first, second);
    }
}
