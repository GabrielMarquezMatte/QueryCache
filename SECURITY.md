# Security Policy

## Supported Versions

Only the latest published release of the `QueryCache.*` packages receives security fixes. Older
releases are not patched retroactively; upgrade to the latest version before reporting an issue.

## Reporting a Vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Report vulnerabilities privately via
[GitHub Private Vulnerability Reporting](https://github.com/GabrielMarquezMatte/QueryCache/security/advisories/new).

Include, where possible:
- The package(s) involved (`QueryCache.Core`, `QueryCache.EFCore`, `QueryCache.Dapper`) and the cache
  in use (in-process, or which `HybridCache` implementation)
- A minimal code sample that reproduces it
- The impact you observed (one caller or tenant seeing another's data, stale data after a write,
  secrets reaching the cache, excessive memory/CPU, etc.)

You should receive an initial response within 5 business days. If the report is confirmed, a fix
will be prepared and released before any public disclosure.
