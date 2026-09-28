# Contributing

Thanks for considering a contribution. This project takes small, focused pull requests over large
rewrites. The [README](README.md) describes what the cache guarantees (key, transactions, invalidation,
empty results); a change that weakens one of those guarantees needs to say so explicitly.

## Build expectations

- **Warnings are errors.** `Directory.Build.props` sets `TreatWarningsAsErrors`, with a curated
  `AnalysisMode=All` analyzer set (NetAnalyzers plus Meziantou). A PR that doesn't build clean
  locally won't build clean in CI either — run a full build before pushing:

  ```bash
  dotnet build QueryCache.slnx --configuration Release
  ```

- **Public API changes require a `PublicAPI.Unshipped.txt` entry.** `Microsoft.CodeAnalysis.PublicApiAnalyzers`
  is active and fails the build on any unrecorded public member. If you add, change, or remove
  anything public, update `src/<package>/PublicAPI/PublicAPI.Unshipped.txt`
  (`python3 .github/scripts/add_missing_public_api.py` does it for you). A bot promotes
  `Unshipped` → `Shipped` automatically after each release — don't edit `Shipped.txt` by hand.

- **Tests are required for behavior changes.** Run the unit suite before opening a PR:

  ```bash
  dotnet test --project tests/QueryCache.Tests/QueryCache.Tests.csproj --configuration Release
  ```

  Changes to keys, invalidation, transactions or the `HybridCache` path also need the integration
  suite, which runs SQL Server, PostgreSQL and Redis through Testcontainers (Docker required):

  ```bash
  dotnet test --project tests/QueryCache.IntegrationTests/QueryCache.IntegrationTests.csproj --configuration Release
  ```

  A cache bug usually shows up as a *wrong* answer rather than a crash, so a test should assert
  the data the caller gets back (or how many times the database was queried), not just that no
  exception was thrown.

- **Benchmarks** live in `benchmarks/QueryCache.Benchmarks`. A new benchmark class needs a group in
  `benchmark-groups.json`, or CI fails. A PR claiming a performance change should include
  before/after numbers.

## Commit messages

Use [Conventional Commits](https://www.conventionalcommits.org/): `type(scope): summary`, imperative
mood, lowercase summary, no trailing period. Common types: `feat`, `fix`, `refactor`, `perf`, `test`,
`docs`, `chore`, `ci`, `build`. The scope is usually the package or area touched (`core`, `efcore`,
`dapper`, `hybrid`). Example: `fix(efcore): invalidate again when an ambient transaction commits`.

## Pull requests

- Open pull requests against `develop`; `master` receives releases.
- One focused change per PR — don't batch unrelated fixes into one commit or one PR.
- If a change is user-visible (new API, behavior change, performance claim), mention it in the PR
  description; release notes are written separately, not as part of every PR.
- CI runs on Linux, Windows, and macOS — a change that only builds on one OS isn't ready to merge.

## Reporting bugs / requesting features

Use GitHub Issues for bugs and feature requests. For suspected security vulnerabilities, do **not**
open a public issue — see [SECURITY.md](SECURITY.md) for the private reporting channel.

## Code of conduct

This project follows the [Code of Conduct](CODE_OF_CONDUCT.md).
