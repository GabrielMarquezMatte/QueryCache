# QueryCache

[![CI](https://github.com/GabrielMarquezMatte/QueryCache/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/GabrielMarquezMatte/QueryCache/actions/workflows/ci.yml)
[![CodeQL](https://github.com/GabrielMarquezMatte/QueryCache/actions/workflows/codeql.yml/badge.svg?branch=master)](https://github.com/GabrielMarquezMatte/QueryCache/actions/workflows/codeql.yml)
[![Release](https://github.com/GabrielMarquezMatte/QueryCache/actions/workflows/release.yml/badge.svg)](https://github.com/GabrielMarquezMatte/QueryCache/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/QueryCache.EFCore.svg)](https://www.nuget.org/packages/QueryCache.EFCore)
[![codecov](https://codecov.io/gh/GabrielMarquezMatte/QueryCache/branch/master/graph/badge.svg)](https://codecov.io/gh/GabrielMarquezMatte/QueryCache)
[![Benchmarks](https://img.shields.io/badge/benchmarks-GitHub%20Pages-informational)](https://gabrielmarquezmatte.github.io/QueryCache/dev/bench/)
[![License](https://img.shields.io/github/license/GabrielMarquezMatte/QueryCache.svg)](LICENSE)

Query-result cache for EF Core and Dapper on .NET 10, in process or in any `HybridCache` (Redis, FusionCache...).

QueryCache keeps query results in memory, keyed by the SQL, its parameter values and the database it ran against. A cache hit costs a few microseconds and never touches the database. Concurrent misses for the same query run it once. With EF Core, `SaveChanges` drops the entries that read the tables it wrote to.

## Install

```bash
dotnet add package QueryCache.EFCore   # or QueryCache.Dapper
```

```csharp
using QueryCache.EFCore;

services.AddDbContext<AppDb>(o => o.UseSqlServer(cs).UseQueryCacheInvalidation());

var active = db.Users.Where(u => u.Active).Include(u => u.Roles).AsSplitQuery();
var users = await active.ToListCachedAsync(TimeSpan.FromMinutes(5), ct);
// SaveChanges touching Users or Roles drops that entry; ExecuteUpdate/raw SQL need await active.InvalidateCacheAsync(ct)

var revenue = await db.Orders.Where(o => o.Paid).Select(o => o.Total).SumCachedAsync(TimeSpan.FromMinutes(1), ct);
```

```csharp
using QueryCache.Dapper;

var row = await conn.ToCacheQuery<int>(new CommandDefinition("select id from t where x = @X", new { X = 1 }))
    .QueryFirstOrDefaultAsync(TimeSpan.FromMinutes(5), ct);
```

| Package | What |
|---|---|
| `QueryCache.Core` | `QueryCacheStore`: cache keyed by `QueryKey`, single-flight, tag invalidation, in-process LRU or `HybridCache`. No DB dependency. |
| `QueryCache.EFCore` | `IQueryable<T>` extensions: `ToListCachedAsync/ToDictionaryCachedAsync/ToHashSetCachedAsync/FirstOrDefaultCachedAsync/FirstCachedAsync/SingleOrDefaultCachedAsync/AnyCachedAsync/CountCachedAsync/SumCachedAsync/MaxCachedAsync(expiration, ct)`, `InvalidateCacheAsync(ct)`, and `UseQueryCacheInvalidation()` for `SaveChanges`. |
| `QueryCache.Dapper` | `DapperCacheQuery<T>` (`connection.ToCacheQuery<T>(command)`): `QueryAsync/QueryFirstOrDefaultAsync/ExecuteScalarAsync(expiration, ct)` and `InvalidateCacheAsync(ct)`. |

### Distributed cache

By default entries live in process. To share them (and their invalidation) between instances, hand QueryCache a `HybridCache` at startup. For several instances use [FusionCache](https://github.com/ZiggyCreatures/FusionCache) with a Redis backplane:

```csharp
builder.Services.AddFusionCache()
    .WithSerializer(new FusionCacheSystemTextJsonSerializer(new JsonSerializerOptions { PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate }))
    .WithDistributedCache(new RedisCache(new RedisCacheOptions { Configuration = redis }))
    .WithBackplane(new RedisBackplane(new RedisBackplaneOptions { Configuration = redis }))
    .AsHybridCache();
var app = builder.Build();
QueryCacheStore.HybridCache = app.Services.GetRequiredService<HybridCache>();
```

Keys and tags are SHA-256 digests (no SQL or connection string in Redis). Tested against Redis with two caches per test standing in for two instances:

| | FusionCache + backplane | Microsoft `AddHybridCache()` |
|---|---|---|
| Another instance reads the entry from Redis | yes | yes |
| `InvalidateCacheAsync` reaches other instances | yes | yes |
| `SaveChanges` (tag) invalidation reaches other instances | yes, even their local copy | **no**: other instances keep serving the old entry until it expires |

So with Microsoft's `HybridCache`, only rely on `SaveChanges` invalidation within one instance, or keep expirations short. Other trade-offs:

- A hit deserializes, so it costs more than the in-process hit and grows with the row count; callers get their own copies.
- Single-flight is per process unless you add a distributed lock (below).

#### Distributed lock

Without a lock, instances that miss the same entry at the same time each run the query. With one, the instance that misses takes a lock named after the entry, reads the cache again in case another instance filled it meanwhile, and only then runs the query:

```csharp
// DistributedLock.Redis package
QueryCacheStore.DistributedLock = new RedisDistributedSynchronizationProvider(redisConnection.GetDatabase());
QueryCacheStore.DistributedLockTimeout = TimeSpan.FromSeconds(10);   // default; set it above your slowest cached query
```

Any `IDistributedLockProvider` from [DistributedLock](https://github.com/madelson/DistributedLock) works (PostgreSQL, SQL Server, Azure blobs...). The lock only applies with a `HybridCache`, and it never fails a query: a lock that times out or errors lets the query run unlocked and counts it on `querycache.lock.failures`. A miss costs one more cache read and a lock round trip. Results with no rows are never stored, so instances waiting on one run the query in turn.

#### Navigation properties

In process nothing is serialized, so any entity graph is cached as is. In a distributed cache the result goes through the cache's serializer (System.Text.Json by default), and navigations are where that breaks. Projections (`Select` into a DTO or record) avoid all of it. For entities:

| Shape | What to configure |
|---|---|
| No cycle, collection with a setter | Nothing. |
| No cycle, get-only collection (`public List<Tag> Tags { get; } = []`) | `PreferredObjectCreationHandling = Populate`, or the collection comes back empty. |
| Cycle: `Include` makes EF set the back-reference (`Order.Lines` ↔ `OrderLine.Order`) | `ReferenceHandler.Preserve` and collections **with setters**: the graph comes back whole, back-references included. `Populate` cannot be combined with any `ReferenceHandler`, and `Preserve` leaves get-only collections empty. |

A result the serializer rejects (a cycle without `Preserve`) is still returned, just not stored. Microsoft's `HybridCache` drops it silently; with FusionCache QueryCache records it on `querycache.store.failures`. Either way that query runs against the database every time, so watch that counter or `querycache.misses` without hits.

Serializer options with each cache:

```csharp
var json = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve };

// FusionCache
builder.Services.AddFusionCache().WithSerializer(new FusionCacheSystemTextJsonSerializer(json)) /* ... */;

// Microsoft HybridCache: a serializer factory, such as JsonSerializerFactory in tests/QueryCache.IntegrationTests/RedisTests.cs
builder.Services.AddHybridCache().AddSerializerFactory(new JsonSerializerFactory(json));
```

## Benchmarks

Direct query against a cache hit, on in-memory SQLite (no network or disk, so these ratios are the floor; against a real database server the gap is far larger):

| Library | Rows | Direct | Cache hit | Speedup |
|---|---:|---:|---:|---:|
| EF Core | 10 | 22.74 μs, 9.46 KB | 5.39 μs, 4.75 KB | ~4.2x |
| EF Core | 1,000 | 458.36 μs, 280.44 KB | 5.53 μs, 4.75 KB | ~83x |
| Dapper | 10 | 8.99 μs, 3.23 KB | 0.29 μs, 688 B | ~31x |
| Dapper | 1,000 | 399.69 μs, 135.14 KB | 0.29 μs, 688 B | ~1,360x |

A hit costs the same whatever the row count. A miss adds ~32 μs for EF Core and ~2–11 μs for Dapper over the direct query. Methodology, miss costs and how to run them: [Benchmarks](docs/performance/benchmarks.md).

## Notes

- Any LINQ/EF operator can come before the cached call. EF results are loaded with no tracking. Relational providers only (the key comes from `CreateDbCommand`).
- Lists are read-only (`IReadOnlyList<T>`) because every caller gets the same instance; do not mutate the entities either.
- **Key**: SQL + parameter **values** (compared exactly, arrays item by item) + connection string without password.
- **Transactions**: reads inside a transaction (EF, Dapper `command.Transaction`, or `TransactionScope`) skip the cache.
- **`SaveChanges`** (with `UseQueryCacheInvalidation()`): drops entries that read the written tables, and again on commit. `ExecuteUpdate`, `ExecuteDelete`, raw SQL and Dapper writes are not seen; call `InvalidateCacheAsync(ct)` for those.
- `SumCachedAsync` / `MaxCachedAsync` work on a projection: `query.Select(x => x.Price).SumCachedAsync(...)`. Each operator has its own entry, so `First` never answers `Single`.
- **No rows** (empty collections, `null`, `0`, `false`): returned, never cached.
- **Single-flight**: concurrent misses in a process run the query once and share its result or its exception; across instances with `QueryCacheStore.DistributedLock`.
- `QueryCacheStore.Capacity` sets the in-process entries kept per result type (default 128); set it at startup. `Timeout.InfiniteTimeSpan` keeps an entry until removed or evicted; zero or negative expirations throw.
- To skip the cache, call EF/Dapper directly.
- Metrics (`System.Diagnostics.Metrics`, meter `QueryCacheStore.MeterName` = `"QueryCache"`): `querycache.hits`, `querycache.misses`, `querycache.fill.duration` (s), `querycache.store.failures`, `querycache.lock.failures`, tagged with `querycache.type`. With OpenTelemetry: `.WithMetrics(m => m.AddMeter(QueryCacheStore.MeterName))`.

## Build

```bash
dotnet restore QueryCache.slnx
dotnet build QueryCache.slnx --configuration Release
dotnet test --project tests/QueryCache.Tests/QueryCache.Tests.csproj --configuration Release
dotnet test --project tests/QueryCache.IntegrationTests/QueryCache.IntegrationTests.csproj --configuration Release   # needs Docker: SQL Server, PostgreSQL and Redis via Testcontainers
dotnet run --project benchmarks/QueryCache.Benchmarks/QueryCache.Benchmarks.csproj --configuration Release -- --filter *
```

Public API changes need an entry in `src/*/PublicAPI/PublicAPI.Unshipped.txt` (the build fails with RS0016 otherwise); `python3 .github/scripts/add_missing_public_api.py` adds them. A release moves them to `PublicAPI.Shipped.txt`. New benchmark classes need a group in `benchmarks/QueryCache.Benchmarks/benchmark-groups.json`.

## License

QueryCache is licensed under the MIT License. See [LICENSE](LICENSE).
