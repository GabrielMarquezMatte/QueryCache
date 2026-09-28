# QueryCache

In-process async query-result cache (LRU, per-entry expiry, single-flight) for EF Core and Dapper.

| Package | What |
|---|---|
| `QueryCache.Core` | `QueryCacheStore`: keyed cache + single-flight. No DB dependency. |
| `QueryCache.EFCore` | `CacheQueryBuilder<T>`: `IQueryable<T>` wrapper with `ToListAsync/FirstOrDefaultAsync/AnyAsync/...(expiration, ct)`. |
| `QueryCache.Dapper` | `DapperCacheQuery<T>`: `QueryAsync/QueryFirstOrDefaultAsync/ExecuteScalarAsync(expiration, ct)`. |

```csharp
var users = await new CacheQueryBuilder<User>(db.Users)
    .Where(u => u.Active)
    .ToListAsync(TimeSpan.FromMinutes(5), ct);

var row = await new DapperCacheQuery<int>(conn, new CommandDefinition("select id from t where x = @X", new { X = 1 }), logger)
    .QueryFirstOrDefaultAsync(TimeSpan.FromMinutes(5), ct);
```

Cache key = SQL + parameter **values** + connection string, so the same query against another database does not collide.
Empty collections are never cached. The cache is static per process and result type (no DI, no eviction API beyond `RemoveFromCache*`).

```
dotnet build QueryCache.slnx
dotnet test --project tests/QueryCache.Tests/QueryCache.Tests.csproj
```
