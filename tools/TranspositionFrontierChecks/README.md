# Transposition frontier checks

Run `dotnet run --project tools/TranspositionFrontierChecks -c Release`.

Links the actual production frontier. Compares 512,000 decisions against the frozen
List algorithm, including incomparable labels, duplicate rejection, replacement,
NaN/infinities, and collapse/re-expansion. Measures allocation for 100,000 retained
singletons; this is a representation check, not a game timing benchmark.
