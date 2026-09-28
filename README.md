# QueryCache

In-process async query-result cache (LRU, per-entry expiry, single-flight) for EF Core and Dapper.

| Package | What |
|---|---|
| `QueryCache.Core` | `QueryCacheStore`: keyed cache + single-flight. No DB dependency. |
| `QueryCache.EFCore` | `IQueryable<T>` extensions: `ToListCachedAsync/ToDictionaryCachedAsync/FirstOrDefaultCachedAsync/FirstCachedAsync/AnyCachedAsync/CountCachedAsync(expiration, ct)` and `InvalidateCache()`. |
| `QueryCache.Dapper` | `DapperCacheQuery<T>` (`connection.ToCacheQuery<T>(command)`): `QueryAsync/QueryFirstOrDefaultAsync/ExecuteScalarAsync(expiration, ct)` and `InvalidateCache()`. |

```csharp
var active = db.Users.Where(u => u.Active).Include(u => u.Roles).AsSplitQuery();
var users = await active.ToListCachedAsync(TimeSpan.FromMinutes(5), ct);
active.InvalidateCache(); // after writing users

var row = await conn.ToCacheQuery<int>(new CommandDefinition("select id from t where x = @X", new { X = 1 }))
    .QueryFirstOrDefaultAsync(TimeSpan.FromMinutes(5), ct);

QueryCacheStore.Capacity = 1_000; // entries per result type (default 128); set at startup
```

Any LINQ/EF operator can come before the cached call. EF results are loaded with no tracking and shared between callers: do not mutate them.
Relational providers only (the key comes from `CreateDbCommand`).

Cache key = SQL + parameter **values** + connection string, so the same query against another database does not collide.
Results with no rows (empty collections, `null`, `0`, `false`) are returned but never cached. To skip the cache, call EF/Dapper directly. The cache is static per process and result type (no DI; evict with `InvalidateCache()`).
`Timeout.InfiniteTimeSpan` keeps an entry until it is removed or evicted; zero or negative expirations throw.

Metrics (`System.Diagnostics.Metrics`, meter `QueryCacheStore.MeterName` = `"QueryCache"`): `querycache.hits`, `querycache.misses`,
`querycache.fill.duration` (s), tagged with `querycache.type`. With OpenTelemetry: `.WithMetrics(m => m.AddMeter(QueryCacheStore.MeterName))`.

```
dotnet build QueryCache.slnx
dotnet test --project tests/QueryCache.Tests/QueryCache.Tests.csproj
```
