# Transposition frontier checks

Run `dotnet run --project tools/TranspositionFrontierChecks -c Release`.

Links the actual production frontier. Compares 512,000 decisions against the frozen
List algorithm, including incomparable labels, duplicate rejection, replacement,
NaN/infinities, and collapse/re-expansion. Measures allocation for 100,000 retained
singletons; this is a representation check, not a game timing benchmark.

额外核对每一步标签数，并检查触顶节点、跨重建峰值、旁路计数透传、标签分布和无上限观测。低上限计数夹具只验证诊断，不作为生产默认上限触顶的证据。
