# QueryCache

[![CI](https://github.com/GabrielMarquezMatte/QueryCache/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/GabrielMarquezMatte/QueryCache/actions/workflows/ci.yml)
[![License](https://img.shields.io/github/license/GabrielMarquezMatte/QueryCache.svg)](LICENSE)

In-process query-result cache for EF Core and Dapper on .NET 10.

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
// SaveChanges touching Users or Roles drops that entry; ExecuteUpdate/raw SQL need active.InvalidateCache()
```

```csharp
using QueryCache.Dapper;

var row = await conn.ToCacheQuery<int>(new CommandDefinition("select id from t where x = @X", new { X = 1 }))
    .QueryFirstOrDefaultAsync(TimeSpan.FromMinutes(5), ct);
```

| Package | What |
|---|---|
| `QueryCache.Core` | `QueryCacheStore`: cache keyed by `QueryKey`, single-flight, tag invalidation. No DB dependency. |
| `QueryCache.EFCore` | `IQueryable<T>` extensions: `ToListCachedAsync/ToDictionaryCachedAsync/FirstOrDefaultCachedAsync/FirstCachedAsync/AnyCachedAsync/CountCachedAsync(expiration, ct)`, `InvalidateCache()`, and `UseQueryCacheInvalidation()` for `SaveChanges`. |
| `QueryCache.Dapper` | `DapperCacheQuery<T>` (`connection.ToCacheQuery<T>(command)`): `QueryAsync/QueryFirstOrDefaultAsync/ExecuteScalarAsync(expiration, ct)` and `InvalidateCache()`. |

## Benchmarks

Direct query against a cache hit, on in-memory SQLite (no network or disk, so these ratios are the floor; against a real database server the gap is far larger):

| Library | Rows | Direct | Cache hit | Speedup |
|---|---:|---:|---:|---:|
| EF Core | 10 | 22.74 μs, 9.46 KB | 5.39 μs, 4.75 KB | ~4.2x |
| EF Core | 1,000 | 458.36 μs, 280.44 KB | 5.53 μs, 4.75 KB | ~83x |
| Dapper | 10 | 8.99 μs, 3.23 KB | 0.29 μs, 688 B | ~31x |
| Dapper | 1,000 | 399.69 μs, 135.14 KB | 0.29 μs, 688 B | ~1,360x |

A hit costs the same whatever the row count. A miss adds ~27 μs for EF Core and ~2–11 μs for Dapper over the direct query. Methodology, miss costs and how to run them: [Benchmarks](docs/performance/benchmarks.md).

## Notes

- Any LINQ/EF operator can come before the cached call. EF results are loaded with no tracking. Relational providers only (the key comes from `CreateDbCommand`).
- Lists are read-only (`IReadOnlyList<T>`) because every caller gets the same instance; do not mutate the entities either.
- **Key**: SQL + parameter **values** (compared exactly, arrays item by item) + connection string without password.
- **Transactions**: reads inside a transaction (EF, Dapper `command.Transaction`, or `TransactionScope`) skip the cache.
- **`SaveChanges`** (with `UseQueryCacheInvalidation()`): drops entries that read the written tables, and again on commit. `ExecuteUpdate`, `ExecuteDelete`, raw SQL and Dapper writes are not seen; call `InvalidateCache()` for those.
- **No rows** (empty collections, `null`, `0`, `false`): returned, never cached.
- **Single-flight**: concurrent misses run the query once and share its result or its exception.
- `QueryCacheStore.Capacity` sets the entries kept per result type (default 128); set it at startup. `Timeout.InfiniteTimeSpan` keeps an entry until removed or evicted; zero or negative expirations throw.
- To skip the cache, call EF/Dapper directly.
- Metrics (`System.Diagnostics.Metrics`, meter `QueryCacheStore.MeterName` = `"QueryCache"`): `querycache.hits`, `querycache.misses`, `querycache.fill.duration` (s), tagged with `querycache.type`. With OpenTelemetry: `.WithMetrics(m => m.AddMeter(QueryCacheStore.MeterName))`.

## Build

```bash
dotnet restore QueryCache.slnx
dotnet build QueryCache.slnx --configuration Release
dotnet test --project tests/QueryCache.Tests/QueryCache.Tests.csproj --configuration Release
dotnet run --project benchmarks/QueryCache.Benchmarks/QueryCache.Benchmarks.csproj --configuration Release -- --filter *
```

## License

QueryCache is licensed under the MIT License. See [LICENSE](LICENSE).
