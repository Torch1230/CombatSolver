# Branch-local card targeting checks

```sh
dotnet run --project tools/CardTargetingChecks -c Release
```

Compiles the actual `CombatPredictionSimulator.CardTargeting.cs` against small model/state doubles. Requires .NET 9; does not load or start the game.

Covers SovereignBlade and Shiv with absent, present, removed and unrelated-owner branch powers while live powers change independently; the native getter throws if accessed inside these prediction cases. Also checks independent branch states, unrelated card fallback and non-shadow fallback. This validates the selector's state source, not actual root capture/Fork or native damage execution.

An older selector can be supplied via `-p:CardTargetingSource=/absolute/path/to/old-source.cs`. The old implementation fails when the branch lacks SeekingEdge but the live owner has it.
