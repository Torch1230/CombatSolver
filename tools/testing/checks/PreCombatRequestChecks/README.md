# Pre-combat request checks

```sh
dotnet run --project tools/testing/checks/PreCombatRequestChecks/PreCombatRequestChecks.csproj -c Release
```

Requires .NET 9; the command is identical on Windows and Linux. No game installation or external test packages are needed. An optional trailing `-- "test name substring"` selects a check.

The project compiles the production API and contracts directly. Stubs replace game state, the main-thread dispatcher, platform availability, and the isolated worker. Controlled worker completions verify request sharing, refresh, cache-hit lifetime dispatch, cancellation ownership, and stale-result rejection without timing a real search.

These checks do not validate the worker's semaphore/process teardown, real game serialization, combat semantics, or the full Mod build. Those still require the supported game and Windows worker environment.
