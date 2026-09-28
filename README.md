# QueryCache

In-process async query-result cache (LRU, per-entry expiry, single-flight) for EF Core and Dapper.

| Package | What |
|---|---|
| `QueryCache.Core` | `QueryCacheStore`: cache keyed by `QueryKey`, single-flight, tag invalidation. No DB dependency. |
| `QueryCache.EFCore` | `IQueryable<T>` extensions: `ToListCachedAsync/ToDictionaryCachedAsync/FirstOrDefaultCachedAsync/FirstCachedAsync/AnyCachedAsync/CountCachedAsync(expiration, ct)`, `InvalidateCache()`, and `UseQueryCacheInvalidation()` for `SaveChanges`. |
| `QueryCache.Dapper` | `DapperCacheQuery<T>` (`connection.ToCacheQuery<T>(command)`): `QueryAsync/QueryFirstOrDefaultAsync/ExecuteScalarAsync(expiration, ct)` and `InvalidateCache()`. |

```csharp
services.AddDbContext<AppDb>(o => o.UseSqlServer(cs).UseQueryCacheInvalidation());

var active = db.Users.Where(u => u.Active).Include(u => u.Roles).AsSplitQuery();
var users = await active.ToListCachedAsync(TimeSpan.FromMinutes(5), ct);
// SaveChanges touching Users or Roles drops that entry; ExecuteUpdate/raw SQL need active.InvalidateCache()

var row = await conn.ToCacheQuery<int>(new CommandDefinition("select id from t where x = @X", new { X = 1 }))
    .QueryFirstOrDefaultAsync(TimeSpan.FromMinutes(5), ct);

QueryCacheStore.Capacity = 1_000; // entries per result type (default 128); set at startup
```

Any LINQ/EF operator can come before the cached call. EF results are loaded with no tracking. Lists are read-only
(`IReadOnlyList<T>`) because every caller gets the same instance; do not mutate the entities either. Relational providers only.

- **Key**: SQL + parameter **values** (compared exactly, arrays item by item) + connection string without password.
- **Transactions**: reads inside a transaction (EF, Dapper `command.Transaction`, or `TransactionScope`) skip the cache.
- **`SaveChanges`** (with `UseQueryCacheInvalidation()`): drops entries that read the written tables, and again on commit.
- **No rows** (empty collections, `null`, `0`, `false`): returned, never cached.
- **Single-flight**: concurrent misses run the query once and share its result or its exception.
- `Timeout.InfiniteTimeSpan` keeps an entry until removed or evicted; zero or negative expirations throw.
- To skip the cache, call EF/Dapper directly.

Metrics (`System.Diagnostics.Metrics`, meter `QueryCacheStore.MeterName` = `"QueryCache"`): `querycache.hits`, `querycache.misses`,
`querycache.fill.duration` (s), tagged with `querycache.type`. With OpenTelemetry: `.WithMetrics(m => m.AddMeter(QueryCacheStore.MeterName))`.

```
dotnet build QueryCache.slnx
dotnet test --project tests/QueryCache.Tests/QueryCache.Tests.csproj
```
