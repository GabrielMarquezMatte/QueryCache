# Benchmarks

Throwaway numbers go stale; these are regenerated from the benchmark suite in
`benchmarks/QueryCache.Benchmarks`.

## Benchmarks

Benchmarks were run with BenchmarkDotNet v0.15.8 (default job) on Windows 10 (22H2), AMD Ryzen 7 5700X, .NET 10.0.11 (SDK 10.0.400).
`benchmarks/QueryCache.Benchmarks/BenchmarkDotNet.Artifacts/` is `.gitignore`-excluded, so raw results are not in the repository.

**Benchmark methodology.** `CacheBenchmarks` queries an in-memory SQLite database (`Mode=Memory;Cache=Shared`) holding `2 × Rows` items, of which the query matches `Rows`. Each library is measured three ways against the same query:

- **Direct**: the library's own call, no cache (`AsNoTracking().ToListAsync()` for EF Core, `QueryAsync<T>()` materialized to a `List<T>` for Dapper). This is the baseline.
- **Cache hit**: `ToListCachedAsync` / `DapperCacheQuery<T>.QueryAsync` with a warm entry.
- **Cache miss**: `InvalidateCache()` followed by the cached call, so every iteration runs the query and fills the entry. The invalidation is part of the measured time.

In-memory SQLite has no network and no disk, so the direct query is as cheap as it gets. **The hit ratios below are the floor**: against SQL Server or PostgreSQL over a network, the direct query costs milliseconds while a hit costs the same few microseconds.

Before measuring, `GlobalSetup` validates the cache and aborts the run if any check fails: the cached rows match the database, a repeated call returns the same instance (served from the cache, not re-queried), and `SaveChanges` with `UseQueryCacheInvalidation()` drops the EF entry.

### EF Core

| Scenario | 10 rows | 1,000 rows |
|---|---:|---:|
| Direct (baseline) | 22.74 μs, 9.46 KB | 458.36 μs, 280.44 KB |
| Cache hit | 5.39 μs, 4.75 KB | 5.53 μs, 4.75 KB |
| Cache miss | 50.20 μs, 23.31 KB | 484.39 μs, 294.55 KB |

A hit is ~4.2x faster than the direct query at 10 rows and ~83x at 1,000 rows, and costs the same ~5.5 μs and 4.75 KB whatever the row count. Most of that fixed cost is EF building the command (`CreateDbCommand`) to get the SQL and parameter values the key is made of: the query is never executed, but EF still has to look up its translation.

A miss costs a roughly constant ~26–27 μs over the direct query (2.2x at 10 rows, 1.06x at 1,000 rows). It pays for the invalidation and the key, and for walking the expression to find the tables the entry depends on, which only happens on a miss. Past a few hundred rows the query itself dominates and the overhead fades.

### Where an EF Core miss goes

`EfMissBreakdownBenchmarks` times each part of a miss on the same 10-row query:

| Part | Mean | Allocated |
|---|---:|---:|
| Direct query (baseline) | 22.77 μs | 9.31 KB |
| `CreateDbCommand` + key | 5.08 μs | 4.59 KB |
| `CreateDbCommand` right before running the query | +7.78 μs | +4.39 KB |
| Tables the query reads (tags) | 2.11 μs | 3.48 KB |
| Cache store around the query | +2.31 μs | +0.62 KB |
| `InvalidateCache()` (measured with every miss here) | 5.30 μs | 4.59 KB |
| Whole miss, invalidation included | 49.37 μs | 23.35 KB |

Most of the fixed cost is EF itself: `CreateDbCommand` translates (or looks up) the query to get the SQL the key is made of, and the benchmark pays it twice, once for the key and once inside `InvalidateCache()`. QueryCache's own work (tags and store) is ~4.4 μs. The parts add up to ~40 μs, so ~9 μs of the whole miss is not attributed to any single part. Building the query anew on every call, as application code does, does not change the picture: the miss still costs ~25 μs over the direct query (65.35 μs vs. 39.89 μs).

### Dapper

| Scenario | 10 rows | 1,000 rows |
|---|---:|---:|
| Direct (baseline) | 8.99 μs, 3.23 KB | 399.69 μs, 135.14 KB |
| Cache hit | 0.29 μs, 688 B | 0.29 μs, 688 B |
| Cache miss | 10.93 μs, 4.89 KB | 410.67 μs, 136.80 KB |

A hit is ~31x faster than the direct query at 10 rows and ~1,360x at 1,000 rows, allocating ~200x less at 1,000 rows. Its ~0.29 μs and 688 B are building the key: the parameter object is read through reflection into a list of names and values.

A miss costs ~2 μs over the direct query at 10 rows (1.22x) and ~11 μs at 1,000 rows (1.03x), within 3% of the direct query once the result has a few hundred rows.

### EF Core against Dapper

A Dapper hit is ~19x cheaper than an EF hit (0.29 μs vs. 5.39 μs), because Dapper already has the SQL text and EF has to generate it. Both are negligible next to a real database round trip.

Run the benchmarks locally:

```bash
dotnet run --project benchmarks/QueryCache.Benchmarks/QueryCache.Benchmarks.csproj --configuration Release -- --filter *
```
