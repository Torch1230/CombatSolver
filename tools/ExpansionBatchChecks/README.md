# Expansion batch storage checks

```bash
dotnet run --project tools/ExpansionBatchChecks/ExpansionBatchChecks.csproj -c Release
```

This standalone .NET 9 tool compiles the actual `src/Search/OwnedExpansionBatch.cs` source. It does not load the game or build the Mod.

Seven checks cover cross-thread return, reuse followed by disposal of an old lease, a successful transfer followed by a failed transfer, ownership already transferred to a caller, the two-storage bound and checkpoint clearing, independent rejection of excessive capacity in all five containers, concurrent renters, and collection of references previously stored in all containers. Faults propagate; no ownership implementation is duplicated in the test.

Passed on 2026-09-05. Production wiring and game semantics are validated separately by the main build and the fixed-workload search checks. This tool provides no performance claim.
