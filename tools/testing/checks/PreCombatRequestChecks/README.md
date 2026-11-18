# Pre-combat request checks

```sh
dotnet run --project tools/testing/checks/PreCombatRequestChecks/PreCombatRequestChecks.csproj -c Release
```

Requires .NET 9; the command is identical on Windows and Linux. No game installation or external test packages are needed. An optional trailing `-- "test name substring"` selects a check.

The project compiles the production API, contracts, and `PreCombatForecastWorker.Idle.cs` directly. Stubs replace game state, the main-thread dispatcher, platform availability, the worker process, and session data. Controlled worker completions verify request sharing, refresh, cache-hit lifetime dispatch, cancellation ownership, and stale-result rejection without timing a real search.

Idle checks execute the production timer, `SemaphoreSlim` wait, cancellation, and disposal paths. A callback holds `Cancel` open while the production completion path runs on another thread; the source must remain usable until its cancellation owner returns. Other checks cover expiry, queued cancellation, replacement sessions, cancellation before task startup, and indefinite lifetime.

These checks do not validate real process startup/teardown, game serialization, combat semantics, or the full Mod build. Those still require the supported game and Windows worker environment.
