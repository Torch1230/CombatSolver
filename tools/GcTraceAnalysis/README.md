# GC allocation trace analysis

An independent .NET 9 console tool for `dotnet-trace` EventPipe recordings. It uses
TraceEvent 3.1.23 (also used by dotnet-trace 9.0.661903), converts the trace to ETLX,
and resolves each `GCAllocationTick` event's `CallStack()` from that log. It does
not reference or build the game mod, start a game, or download symbols.

```sh
dotnet build tools/GcTraceAnalysis/GcTraceAnalysis.csproj -c Release
dotnet tools/GcTraceAnalysis/bin/Release/net9.0/GcTraceAnalysis.dll \
  --input /path/to/completed/allocations.nettrace \
  --output /path/to/allocation-analysis.json \
  --top 50
```

The report, its `.etlx` conversion and `.conversion.log` remain together. Wait for
the collector to finish and close the recording before analysis. An existing
ETLX can be supplied as `--input` to change report filters without conversion;
its original conversion completeness cannot be inferred from ETLX alone.
Optional `--process-id`, `--start-ms`, and `--end-ms` filters apply to allocation
events; times are milliseconds from trace start and both bounds are inclusive.
All filters and the pre-filter allocation event count appear in the JSON.

Conversion fails on errors by default. If a recording is truncated, an explicit
`--allow-incomplete true` permits TraceEvent to salvage the valid prefix. Use a
new report output path after a failed conversion. The report then says
`PartialConversionExplicitlyAllowed`, includes the converter's truncation flag
when available, and preserves the conversion log. This is diagnostic evidence
only: missing tail events or rundown can distort totals and symbol resolution.
It must not be presented as a complete recording. The original trace is never
modified. Do not reuse the ETLX from a failed strict conversion.

## Reading the report

- `sampleCount` counts allocation tick events, **not objects**.
- `estimatedBytes` sums `AllocationAmount64`; older events use the 32-bit weight
  and are counted separately under `byteWeightSources`. Each tick labels one
  sampled object's type, while its weight covers allocation since the prior
  tick. Type totals are weighted estimates; they are not exact allocation
  counters, and small or rare types can be missing or noisy.
- `allIncludedSamples` includes startup and other activity inside the chosen
  filters. `confirmedSearch` requires a resolved search stack anchor.
- `scopes` separates root capture, expansion work, solver search, coordinator
  search requests, lane infrastructure, unanchored simulation, other activity,
  absent stacks, and wholly unresolved stacks. Coordinator requests include
  coordination work; lane infrastructure is excluded from confirmed search.
  A thread ID or a generic simulator/Fork frame never establishes search by
  itself. Unanchored simulation is not claimed to be startup.
- `searchCategories` is exclusive, in precedence order: Fork,
  ProjectedShuffle, SnapshotStateEvaluation, History, Other. For example,
  history allocation during a simulator Fork is counted under Fork.
  `inclusiveSearchTags` exposes overlap; **do not sum its rows**.
- `topSearchTypes`, `topSearchCategoryTypes`, and `topSearchStacks` help identify
  candidates. Stack frames are leaf first; each entry records the frame that
  established search ownership. `topOtherStacks` helps inspect exclusions.
- Every aggregate records missing stacks, wholly unresolved stacks, partially
  unresolved stacks, and first/last sample time. `traceEventsLost`, completeness
  metadata and the conversion log describe additional limitations. Zero lost
  events does not prove that an explicitly partial recording is complete.

The classifier follows this repository's actual names: code under `src/Search`
uses namespace `CombatSolver`, and state evaluation is implemented by
`CombatBeamSolver.Snapshot`. There is no `CombatSolver.Search` namespace or
`StateEvaluation` type to match. It recognizes compiler-generated iterator and
lambda names without depending on their numeric suffixes. The JSON includes
the exact anchor rules so attribution remains reviewable when source changes.

Collect allocation traces separately from timing benchmarks. Compare weighted
samples with the game's exact `SEARCH_PHASE` allocation counters; profiler
timing is not benchmark timing, and inclusive phase counters must not be added
as if they represented disjoint work. Neither allocation ticks nor this report
measure retained/live memory; a GC heap capture is a separate observation.

For automated `dotnet-trace 9.0.661903` collection, a redirected stdin ignores
Enter. Use a bounded `--duration`, or the collector's supported SIGINT/SIGTERM
cancellation on Linux, and wait for collector completion while the target remains
alive so rundown can finish. The game's HoldAfterInitialSearch option cannot be
combined with StopAfterInitialSolverResultAssertion. The second-round report
records the failed stop wait and the subsequently parseable artifact separately;
strict conversion and resolved frames do not turn a failed runner into a passing
end-to-end capture test.
