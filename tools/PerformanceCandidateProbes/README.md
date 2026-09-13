# Bounded performance prototypes (415da12)

These standalone probes freeze the pre-experiment fingerprint and comparison rules. They are not production implementations. `TypeCounts.cs` is the small-map prototype, while the separate ref-update benchmark measures an ordinary dictionary. See [the decision report](../../docs/performance/five-candidates-20260913.md) for adoption decisions and real-game evidence.

Build with `dotnet build tools/PerformanceCandidateProbes/PerformanceCandidateProbes.csproj -c Release`. Run the resulting DLL with one of:

- `count`: randomized count/Fork checks and copy/ref-update measurements.
- `sort docs/performance/experiments/deferred-inputs-20260913.json`: exact old-output checks, heap mismatches and conservative tie fallback.
- `fingerprint`: every-prefix equality and scalar/inline/two-chain SIMD measurements.
- `string`: separate local-copy string fingerprint experiment.

The recorded standalone measurements used `DOTNET_TieredCompilation=0` and .NET 9.0.19. This setting was **not** changed in the game host. The 48-byte result-record allocation is measurement overhead. Elapsed microbenchmarks do not establish whole-search or visible-game speedups. SIMD runs only when AVX2 is available; the JIT may use newer supported instructions on that host.
