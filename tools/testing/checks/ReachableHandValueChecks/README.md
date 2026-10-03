# Reachable hand value checks

Run with .NET 9 on Windows or Linux:

```sh
dotnet run --project tools/testing/checks/ReachableHandValueChecks/ReachableHandValueChecks.csproj -c Release
```

Links the production value calculation, compares it exactly with the original two-dimensional recurrence, then measures fixed-input allocation. Covers free cards, both resources, empty hands, large tables, duplicate entries, and value-sum overflow. The original model/cost-hook collection path is not exercised here.

Timing is a local microbenchmark, not evidence of full-search throughput or visible game performance. The managed allocation assertion applies only to the tested small DP tables, not the entire snapshot.
