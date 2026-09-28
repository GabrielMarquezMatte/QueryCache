using System.Data.Common;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(QueryCache.IntegrationTests.PostgresServer))]
[assembly: AssemblyFixture(typeof(QueryCache.IntegrationTests.SqlServerServer))]
[assembly: AssemblyFixture(typeof(QueryCache.IntegrationTests.RedisServer))]

namespace QueryCache.IntegrationTests;

/// <summary>
/// One container per database engine for the whole run. Each test asks for its own database, so tests stay isolated
/// and, because the cache key includes the connection, never share cache entries.
/// </summary>
public abstract class DatabaseServer : IAsyncLifetime
{
    private int _counter;

    protected abstract IDatabaseContainer Container { get; }

    /// <summary>A statement that runs for about 30 seconds and returns one scalar.</summary>
    public abstract string SlowScalarSql { get; }

    public abstract DbContextOptionsBuilder Use(DbContextOptionsBuilder builder, string connectionString);

    public abstract DbConnection Connect(string connectionString);

    protected abstract string ForDatabase(string connectionString, string database);

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"qc_test_{Interlocked.Increment(ref _counter)}";
        await using var connection = Connect(Container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {name}";
        await command.ExecuteNonQueryAsync();
        return ForDatabase(Container.GetConnectionString(), name);
    }

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Container.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

public sealed class PostgresServer : DatabaseServer
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    protected override IDatabaseContainer Container => _container;

    public override string SlowScalarSql => "SELECT 1 FROM pg_sleep(30)";

    public override DbContextOptionsBuilder Use(DbContextOptionsBuilder builder, string connectionString)
    {
        return builder.UseNpgsql(connectionString);
    }

    public override DbConnection Connect(string connectionString)
    {
        return new NpgsqlConnection(connectionString);
    }

    protected override string ForDatabase(string connectionString, string database)
    {
        return new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ConnectionString;
    }
}

public sealed class RedisServer : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:8-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }
}

public sealed class SqlServerServer : DatabaseServer
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    protected override IDatabaseContainer Container => _container;

    public override string SlowScalarSql => "WAITFOR DELAY '00:00:30'; SELECT 1";

    public override DbContextOptionsBuilder Use(DbContextOptionsBuilder builder, string connectionString)
    {
        return builder.UseSqlServer(connectionString);
    }

    public override DbConnection Connect(string connectionString)
    {
        return new SqlConnection(connectionString);
    }

    protected override string ForDatabase(string connectionString, string database)
    {
        return new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database, TrustServerCertificate = true }.ConnectionString;
    }
}
