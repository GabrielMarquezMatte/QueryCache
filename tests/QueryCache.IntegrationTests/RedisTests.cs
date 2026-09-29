using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Medallion.Threading;
using Medallion.Threading.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using QueryCache.Dapper;
using QueryCache.EFCore;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace QueryCache.IntegrationTests;

/// <summary><see cref="QueryCacheStore.HybridCache"/> is process-wide, so these tests run alone.</summary>
[CollectionDefinition(nameof(RedisTests<>), DisableParallelization = true)]
public sealed class RedisTestsGroup;

/// <summary>
/// Each <see cref="NewInstance"/> is one application instance: its own HybridCache (own local layer) over the shared Redis.
/// The database is PostgreSQL.
/// </summary>
[Collection(nameof(RedisTests<>))]
public abstract class RedisTests<TSelf>(PostgresServer postgres, RedisServer redis) : IAsyncDisposable
{
    protected static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly List<ServiceProvider> _instances = [];

    protected string Redis => redis.ConnectionString;

    /// <summary>Serializer options for the instances created after it is set; <see langword="null"/> keeps the cache's default.</summary>
    protected JsonSerializerOptions? Json { get; set; }

    protected abstract void AddHybridCache(IServiceCollection services);

    protected HybridCache NewInstance()
    {
        var services = new ServiceCollection();
        AddHybridCache(services);
        var provider = services.BuildServiceProvider();
        _instances.Add(provider);
        return provider.GetRequiredService<HybridCache>();
    }

    public async ValueTask DisposeAsync()
    {
        QueryCacheStore.HybridCache = null;
        QueryCacheStore.DistributedLock = null;
        foreach (var instance in _instances)
        {
            await instance.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_miss_waits_for_the_instance_holding_the_redis_lock_and_reuses_its_result()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(Redis);
        var locks = new RedisDistributedSynchronizationProvider(multiplexer.GetDatabase());
        QueryCacheStore.DistributedLock = locks;
        QueryCacheStore.HybridCache = NewInstance();
        var otherInstance = NewInstance();
        var key = new QueryKey(nameof(A_miss_waits_for_the_instance_holding_the_redis_lock_and_reuses_its_result), Guid.NewGuid());
        var calls = 0;
        var held = await locks.AcquireLockAsync(QueryCacheStore.LockName<string>(key));

        var miss = QueryCacheStore.GetOrAddAsync(key, Minute, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("mine");
        }, CancellationToken.None).AsTask();
        await Task.Delay(500);
        Assert.False(miss.IsCompleted);
        await otherInstance.SetAsync(QueryCacheStore.HybridKey<string>(key), "theirs");
        await held.DisposeAsync();

        Assert.Equal("theirs", await miss);
        Assert.Equal(0, calls);
    }

    protected TestDb NewDb(string connectionString, CommandCounter? counter = null)
    {
        var builder = postgres.Use(new DbContextOptionsBuilder<TestDb>(), connectionString).UseQueryCacheInvalidation();
        if (counter is not null)
        {
            builder.AddInterceptors(counter);
        }
        return new TestDb(builder.Options);
    }

    protected async Task<string> NewDatabaseAsync(params Item[] items)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = NewDb(connectionString);
        await db.Database.EnsureCreatedAsync();
        db.Items.AddRange(items);
        await db.SaveChangesAsync();
        return connectionString;
    }

    private async Task<(TestDb Db, CommandCounter Counter)> NewOrderDbAsync()
    {
        var connectionString = await NewDatabaseAsync();
        await using (var seed = NewDb(connectionString))
        {
            seed.Orders.Add(new Order { Id = 1, Lines = [new OrderLine { Id = 1 }, new OrderLine { Id = 2 }] });
            await seed.SaveChangesAsync();
        }
        var counter = new CommandCounter();
        return (NewDb(connectionString, counter), counter);
    }

    [Fact]
    public async Task Cyclic_graphs_survive_redis_with_reference_preservation()
    {
        Json = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve };
        var (db, counter) = await NewOrderDbAsync();
        await using var _ = db;
        var query = db.Orders.Include(o => o.Lines);

        QueryCacheStore.HybridCache = NewInstance();
        await query.ToListCachedAsync(Minute, CancellationToken.None);
        QueryCacheStore.HybridCache = NewInstance();
        var order = Assert.Single(await query.ToListCachedAsync(Minute, CancellationToken.None));

        Assert.Equal(1, counter.Count);
        Assert.Equal(2, order.Lines.Count);
        Assert.All(order.Lines, line => Assert.Same(order, line.Order));
    }

    [Fact]
    public async Task Cyclic_graphs_the_serializer_rejects_are_still_returned()
    {
        var (db, counter) = await NewOrderDbAsync();
        await using var _ = db;
        var query = db.Orders.Include(o => o.Lines);

        for (var i = 0; i < 2; i++)
        {
            QueryCacheStore.HybridCache = NewInstance();
            Assert.Equal(2, Assert.Single(await query.ToListCachedAsync(Minute, CancellationToken.None)).Lines.Count);
        }
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public async Task Another_instance_is_served_from_redis_without_querying_the_database()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1, Name = "a", Code = 5 }, new Item { Id = 2, Name = "b", Code = 7 });
        var counter = new CommandCounter();
        await using var db = NewDb(connectionString, counter);
        var codes = db.Items.Select(i => i.Code);

        QueryCacheStore.HybridCache = NewInstance();
        await db.Items.OrderBy(i => i.Id).ToListCachedAsync(Minute, CancellationToken.None);
        await codes.SumCachedAsync(Minute, CancellationToken.None);
        QueryCacheStore.HybridCache = NewInstance();
        var rows = await db.Items.OrderBy(i => i.Id).ToListCachedAsync(Minute, CancellationToken.None);
        var sum = await codes.SumCachedAsync(Minute, CancellationToken.None);

        Assert.Equal(["a", "b"], rows.Select(r => r.Name), StringComparer.Ordinal);
        Assert.Equal(12, sum);
        Assert.Equal(2, counter.Count);
    }

    [Fact]
    public async Task Dapper_invalidation_on_one_instance_reaches_the_others()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1 });
        await using var connection = postgres.Connect(connectionString);
        var query = connection.ToCacheQuery<int>(new CommandDefinition("SELECT \"Id\" FROM \"Items\" WHERE \"Id\" > @Min", new { Min = 0 }));

        QueryCacheStore.HybridCache = NewInstance();
        Assert.Single(await query.QueryAsync(Minute, CancellationToken.None));
        await connection.ExecuteAsync("DELETE FROM \"Items\"");
        QueryCacheStore.HybridCache = NewInstance();
        Assert.Single(await query.QueryAsync(Minute, CancellationToken.None));
        await query.InvalidateCacheAsync(CancellationToken.None);

        QueryCacheStore.HybridCache = NewInstance();
        Assert.Empty(await query.QueryAsync(Minute, CancellationToken.None));
    }

    [Fact]
    public async Task Redis_keys_hold_no_sql_or_connection_details()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1 });
        await using var db = NewDb(connectionString);
        QueryCacheStore.HybridCache = NewInstance();
        await db.Items.ToListCachedAsync(Minute, CancellationToken.None);
        db.Items.Add(new Item { Id = 2 });
        await db.SaveChangesAsync();
        await db.Items.ToListCachedAsync(Minute, CancellationToken.None);

        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(Redis);
        var keys = new List<string>();
        await foreach (var key in multiplexer.GetServer(multiplexer.GetEndPoints()[0]).KeysAsync())
        {
            keys.Add(key.ToString());
        }

        var database = db.Database.GetDbConnection().Database;
        Assert.Contains(keys, key => key.Contains("querycache:", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, key => key.Contains(database, StringComparison.Ordinal) || key.Contains("Items", StringComparison.Ordinal));
    }
}

/// <summary>
/// Microsoft's HybridCache over Redis. Its <c>RemoveByTagAsync</c> is not seen by other instances (checked with 10.10.0),
/// so <c>SaveChanges</c> invalidation stays local; key removal (<c>InvalidateCacheAsync</c>) does reach them.
/// </summary>
public sealed class MicrosoftHybridCacheRedisTests(PostgresServer postgres, RedisServer redis)
    : RedisTests<MicrosoftHybridCacheRedisTests>(postgres, redis)
{
    protected override void AddHybridCache(IServiceCollection services)
    {
        services.AddStackExchangeRedisCache(options => options.Configuration = Redis);
        var builder = services.AddHybridCache();
        if (Json is not null)
        {
            builder.AddSerializerFactory(new JsonSerializerFactory(Json));
        }
    }

    /// <summary>The way to give Microsoft's HybridCache System.Text.Json options.</summary>
    private sealed class JsonSerializerFactory(JsonSerializerOptions options) : IHybridCacheSerializerFactory
    {
        public bool TryCreateSerializer<T>([NotNullWhen(true)] out IHybridCacheSerializer<T>? serializer)
        {
            serializer = new Serializer<T>(options);
            return true;
        }

        private sealed class Serializer<T>(JsonSerializerOptions options) : IHybridCacheSerializer<T>
        {
            public T Deserialize(ReadOnlySequence<byte> source)
            {
                var reader = new Utf8JsonReader(source);
                return JsonSerializer.Deserialize<T>(ref reader, options)!;
            }

            public void Serialize(T value, IBufferWriter<byte> target)
            {
                using var writer = new Utf8JsonWriter(target);
                JsonSerializer.Serialize(writer, value, options);
            }
        }
    }
}

/// <summary>FusionCache over Redis with a Redis backplane: tag invalidation reaches every instance.</summary>
public sealed class FusionCacheRedisTests : RedisTests<FusionCacheRedisTests>
{
    public FusionCacheRedisTests(PostgresServer postgres, RedisServer redis) : base(postgres, redis)
    {
        Json = new JsonSerializerOptions { PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate };
    }

    protected override void AddHybridCache(IServiceCollection services)
    {
        services.AddFusionCache()
            .WithSerializer(new FusionCacheSystemTextJsonSerializer(Json))
            .WithDistributedCache(new RedisCache(new RedisCacheOptions { Configuration = Redis }))
            .WithBackplane(new RedisBackplane(new RedisBackplaneOptions { Configuration = Redis }))
            .AsHybridCache();
    }

    [Fact]
    public async Task SaveChanges_on_one_instance_invalidates_the_entry_for_a_new_instance()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1 });
        await using var db = NewDb(connectionString);

        QueryCacheStore.HybridCache = NewInstance();
        Assert.Single(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));
        db.Items.Add(new Item { Id = 2 });
        await db.SaveChangesAsync();

        QueryCacheStore.HybridCache = NewInstance();
        Assert.Equal(2, (await db.Items.ToListCachedAsync(Minute, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task SaveChanges_on_one_instance_evicts_the_local_copy_of_the_others()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1 });
        await using var db = NewDb(connectionString);
        var first = NewInstance();
        var second = NewInstance();
        QueryCacheStore.HybridCache = second;
        Assert.Single(await db.Items.ToListCachedAsync(Minute, CancellationToken.None));

        QueryCacheStore.HybridCache = first;
        db.Items.Add(new Item { Id = 2 });
        await db.SaveChangesAsync();

        QueryCacheStore.HybridCache = second;
        var count = 0;
        for (var attempt = 0; attempt < 50 && count != 2; attempt++)
        {
            await Task.Delay(100);
            count = (await db.Items.ToListCachedAsync(Minute, CancellationToken.None)).Count;
        }
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Included_collections_survive_redis()
    {
        var connectionString = await NewDatabaseAsync(new Item { Id = 1, Tags = { new Tag { Id = 1 }, new Tag { Id = 2 } } });
        await using var db = NewDb(connectionString);
        var query = db.Items.Include(i => i.Tags);

        QueryCacheStore.HybridCache = NewInstance();
        await query.ToListCachedAsync(Minute, CancellationToken.None);
        QueryCacheStore.HybridCache = NewInstance();

        Assert.Equal(2, Assert.Single(await query.ToListCachedAsync(Minute, CancellationToken.None)).Tags.Count);
    }
}
