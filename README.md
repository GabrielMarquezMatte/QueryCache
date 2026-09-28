# QueryCache

In-process async query-result cache (LRU, per-entry expiry, single-flight) for EF Core and Dapper.

| Package | What |
|---|---|
| `QueryCache.Core` | `QueryCacheStore`: keyed cache + single-flight. No DB dependency. |
| `QueryCache.EFCore` | `CacheQueryBuilder<T>`: `IQueryable<T>` wrapper with `ToListAsync/FirstOrDefaultAsync/AnyAsync/...(expiration, ct)`. |
| `QueryCache.Dapper` | `DapperCacheQuery<T>`: `QueryAsync/QueryFirstOrDefaultAsync/ExecuteScalarAsync(expiration, ct)`. |

```csharp
var users = await db.Users.ToCacheQueryBuilder()
    .Where(u => u.Active)
    .ToListAsync(TimeSpan.FromMinutes(5), ct);

var row = await conn.ToCacheQuery<int>(new CommandDefinition("select id from t where x = @X", new { X = 1 }))
    .QueryFirstOrDefaultAsync(TimeSpan.FromMinutes(5), ct);

QueryCacheStore.Capacity = 1_000; // entries per result type (default 128); set at startup
```

Cache key = SQL + parameter **values** + connection string, so the same query against another database does not collide.
Empty collections are never cached. The cache is static per process and result type (no DI, no eviction API beyond `RemoveFromCache*`).
`Timeout.InfiniteTimeSpan` keeps an entry until it is removed or evicted; zero or negative expirations throw.

Metrics (`System.Diagnostics.Metrics`, meter `QueryCacheStore.MeterName` = `"QueryCache"`): `querycache.hits`, `querycache.misses`,
`querycache.fill.duration` (s), tagged with `querycache.type`. With OpenTelemetry: `.WithMetrics(m => m.AddMeter(QueryCacheStore.MeterName))`.

```
dotnet build QueryCache.slnx
dotnet test --project tests/QueryCache.Tests/QueryCache.Tests.csproj
```
