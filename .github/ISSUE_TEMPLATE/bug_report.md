---
name: Bug report
about: Something isn't working as expected
title: ""
labels: bug
---

**Describe the bug**
A clear description of what's wrong: stale data after a write, a result that should have been
cached and wasn't (or the reverse), an exception, etc.

**Package(s) and cache involved**
QueryCache.EFCore / QueryCache.Dapper / QueryCache.Core — in-process, or which `HybridCache`
(Microsoft, FusionCache...) over which distributed cache.

**Reproduction**
Minimal code sample: the query, how it is cached, and the write or call sequence that shows the
problem. Mention transactions (`BeginTransaction`, `TransactionScope`) if any are involved.

```csharp
// minimal repro here
```

**Expected behavior**
What you expected to happen.

**Actual behavior**
What actually happened — include the full exception message/stack trace if there is one.

**Environment**
- QueryCache version:
- Database and provider (e.g. SQL Server + Microsoft.EntityFrameworkCore.SqlServer 10.0.3):
- .NET version:
- OS:

**Note on security issues:** if this bug could expose one caller's data to another, or put secrets
in the cache, please do **not** open a public issue — see [SECURITY.md](../../SECURITY.md) for the
private reporting channel instead.
