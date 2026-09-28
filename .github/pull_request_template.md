## What does this change?

<!-- One or two sentences: what changed and why. -->

## Checklist

- [ ] `dotnet build QueryCache.slnx --configuration Release` builds clean (warnings are errors)
- [ ] `dotnet test --project tests/QueryCache.Tests/QueryCache.Tests.csproj --configuration Release` passes
- [ ] If it touches the database or cache paths: `dotnet test --project tests/QueryCache.IntegrationTests/QueryCache.IntegrationTests.csproj --configuration Release` passes (needs Docker)
- [ ] If this changes the public API: `PublicAPI.Unshipped.txt` updated (`python3 .github/scripts/add_missing_public_api.py`)
- [ ] Tests added/updated for the behavior change
- [ ] One focused change — unrelated fixes are in a separate PR

## Test plan

<!-- How did you verify this works? -->
