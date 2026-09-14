# Model prediction state contract checks

Run these commands from the repository root, in separate processes because registration freezes on first capture:

```sh
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release -- --empty
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release -- --cards
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release -- --allocation
```

The harness links the production adapter registry, typed field writer, state store and fingerprint source.
`--cards` also links the production reference resolver, collection remapper and position writer. It checks ordered/unordered relations, nulls, multiplicity, missing mappings, parent/child/sibling isolation and live/predicted registered relic/Modifier state. It does not measure performance. Real preview COW and complete simulator Fork are covered by the separately compiled `MODEL-STATE-INTEGRATION` game fixture, not by the managed identity doubles here.
Game model identities, the simulator shell and reference-remapping context are substituted. No game or Godot initialization is performed.

Checks cover empty/unmatched compatibility, exact runtime types, duplicate/late registration, frozen root values, same-type instance swaps, zero-valued counters, collection order, null/empty strings, detached Forks, required reference remapping, pending transactions, invalid Fork results, live/predicted continuation text, recapture, invariant formatting and concurrent reads.

These are state contract checks, not a native game differential, full simulator Fork test, turn-lifecycle replay or performance measurement. See [the adapter contract](../../docs/third-party-model-state.md) for the required game-level follow-up.

`--allocation` warms lookup and equality before checking that 1,000 hash-only traversals with scalar/indexed callbacks allocate no managed objects. This isolates the adapter path, not the full game. `--fork-type` isolates the runtime-type slicing regression, also covered by the default contract suite.
