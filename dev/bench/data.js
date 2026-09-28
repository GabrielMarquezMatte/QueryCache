window.BENCHMARK_DATA = {
  "lastUpdate": 1790617516539,
  "repoUrl": "https://github.com/GabrielMarquezMatte/QueryCache",
  "entries": {
    "Benchmark": [
      {
        "commit": {
          "author": {
            "email": "gabrielandremarquez.matte@gmail.com",
            "name": "Gabriel Matte",
            "username": "GabrielMarquezMatte"
          },
          "committer": {
            "email": "noreply@github.com",
            "name": "GitHub",
            "username": "web-flow"
          },
          "distinct": true,
          "id": "936613c4585efc43c2f29153991e8c001f6e63c1",
          "message": "Merge pull request #2 from GabrielMarquezMatte/develop\n\nRaise test coverage to 98% and drop dead SaveChangesFailed handling",
          "timestamp": "2026-09-28T14:41:51-03:00",
          "tree_id": "2750193335f171e81067a95dba4d160f1f7f543d",
          "url": "https://github.com/GabrielMarquezMatte/QueryCache/commit/936613c4585efc43c2f29153991e8c001f6e63c1"
        },
        "date": 1790617515468,
        "tool": "benchmarkdotnet",
        "benches": [
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Dapper_Direct(Rows: 10)",
            "value": 11212.615591430664,
            "unit": "ns",
            "range": "± 89.80740169150204"
          },
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Dapper_CacheHit(Rows: 10)",
            "value": 331.64808597564695,
            "unit": "ns",
            "range": "± 3.6804126747788226"
          },
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Dapper_CacheMiss(Rows: 10)",
            "value": 12879.234721374512,
            "unit": "ns",
            "range": "± 98.90005810033414"
          },
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Dapper_Direct(Rows: 1000)",
            "value": 586290.68359375,
            "unit": "ns",
            "range": "± 1117.0374897687457"
          },
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Dapper_CacheHit(Rows: 1000)",
            "value": 330.2584095597267,
            "unit": "ns",
            "range": "± 1.4224889027534955"
          },
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Dapper_CacheMiss(Rows: 1000)",
            "value": 599030.8571289063,
            "unit": "ns",
            "range": "± 2691.1226364960844"
          },
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Ef_Direct(Rows: 10)",
            "value": 19254.696362304687,
            "unit": "ns",
            "range": "± 226.18892817459215"
          },
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Ef_CacheHit(Rows: 10)",
            "value": 5575.247887505426,
            "unit": "ns",
            "range": "± 22.63723823352478"
          },
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Ef_CacheMiss(Rows: 10)",
            "value": 41462.63466796875,
            "unit": "ns",
            "range": "± 245.62900017541068"
          },
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Ef_Direct(Rows: 1000)",
            "value": 652319.9388020834,
            "unit": "ns",
            "range": "± 1026.8272065544377"
          },
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Ef_CacheHit(Rows: 1000)",
            "value": 5582.5284774780275,
            "unit": "ns",
            "range": "± 31.811390146264333"
          },
          {
            "name": "QueryCache.Benchmarks.CacheBenchmarks.Ef_CacheMiss(Rows: 1000)",
            "value": 702191.023046875,
            "unit": "ns",
            "range": "± 46441.548604825475"
          },
          {
            "name": "QueryCache.Benchmarks.EfMissBreakdownBenchmarks.Direct",
            "value": 37750.29633789063,
            "unit": "ns",
            "range": "± 90.11095413697892"
          },
          {
            "name": "QueryCache.Benchmarks.EfMissBreakdownBenchmarks.CommandAndKey",
            "value": 7774.347288343642,
            "unit": "ns",
            "range": "± 32.14976166729082"
          },
          {
            "name": "QueryCache.Benchmarks.EfMissBreakdownBenchmarks.TableTagsOnly",
            "value": 3467.087242126465,
            "unit": "ns",
            "range": "± 15.377709829485587"
          },
          {
            "name": "QueryCache.Benchmarks.EfMissBreakdownBenchmarks.InvalidateOnly",
            "value": 9977.987683105468,
            "unit": "ns",
            "range": "± 60.31834508938907"
          },
          {
            "name": "QueryCache.Benchmarks.EfMissBreakdownBenchmarks.StoreMissAroundDirect",
            "value": 41141.662434895836,
            "unit": "ns",
            "range": "± 215.09921017813494"
          },
          {
            "name": "QueryCache.Benchmarks.EfMissBreakdownBenchmarks.DirectAfterCommand",
            "value": 48090.53413085938,
            "unit": "ns",
            "range": "± 185.20841434463398"
          },
          {
            "name": "QueryCache.Benchmarks.EfMissBreakdownBenchmarks.DirectVarying",
            "value": 61375.28816731771,
            "unit": "ns",
            "range": "± 173.13513627917143"
          },
          {
            "name": "QueryCache.Benchmarks.EfMissBreakdownBenchmarks.MissVarying",
            "value": 100463.29821777344,
            "unit": "ns",
            "range": "± 411.8732484553886"
          },
          {
            "name": "QueryCache.Benchmarks.EfMissBreakdownBenchmarks.FullMiss",
            "value": 78771.47186279297,
            "unit": "ns",
            "range": "± 142.25128005717193"
          }
        ]
      }
    ]
  }
}