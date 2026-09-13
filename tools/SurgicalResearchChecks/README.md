# 精简优化研究分配探针

从仓库根运行：

```bash
dotnet run --project tools/SurgicalResearchChecks -c Release
```

直接链接生产 `PredictionStateStore.cs`，以最小模型替身测量空迭代器与简单标量 Peek/TryGet 路径。替身 Fork 方法显式失败；这里不验证原生游戏、Fork、别名重映射或 Hook 语义。另测 `Dictionary<Type,int>` 构造分配，用于研究辅助表预分配，未修改生产实现。

每项预热 10,000 次，三块各 100,000 次；报告当前线程累计分配，不报告吞吐或 RSS。委托/结果序列化在测量外。`results.json` 为 Linux x64 .NET 9.0.19 本轮结果；其他运行时布局可能不同。简单 int 状态的 24 B 不能套用于全部状态类型。

[研究报告](../../docs/performance/surgical-research-20260912.md) 包含源码切口、适用条件和未验证范围。
