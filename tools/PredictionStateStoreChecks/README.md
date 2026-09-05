# PredictionStateStore contract checks

Run `dotnet run --project tools/PredictionStateStoreChecks/PredictionStateStoreChecks.csproj -c Release` from the repository root. An optional final argument sets the number of Fork iterations (default `20000`).

The harness compiles the production `PredictionStateStore.cs` directly. Its small test contracts replace game model identities and the fork remapping context, so it needs neither game initialization nor game assemblies. Checks cover read/capture behavior, borrowed mutable references across Fork, branch isolation, insertion and interleaved-type Fork order, removal, model aliases, required remapping, shared state identity, and explicit transaction/factory failures.

Allocation output measures the store and state objects for empty, populated and aliased stores. The test remap context is reused and warmed before measurement; these bytes are **not** complete simulator or search allocation. Elapsed times are small diagnostic samples, not a search performance result.

To compare an older source file with the same core checks, pass `-p:StateStoreSource=/absolute/path/to/PredictionStateStore.cs`. This override omits the new `HasEntries` API checks, so older baseline sources remain compilable. The default source also checks type counts; pass `--entry-count-only` to run just those checks. Keep extracted baseline sources under an ignored local directory. Simulator/Fork and actual-versus-predicted tests remain required before accepting gameplay correctness.
