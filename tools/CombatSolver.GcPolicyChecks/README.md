# GC 与并发决策检查

独立 .NET 9 工具，直接编译生产 `SearchGcPolicy`、scope/暂停计数、`SearchMemoryPressureSignal` 和 Smart 预测源码；日志与请求活动 tracker 使用最小替身，不需要游戏依赖。实验性自适应并发控制器也在此验证。

从仓库根分别执行：

```bash
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- scopes
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- parallelism
```

本轮分别通过基础/预测 19 项、scope 8 项、并发控制 15 项。基础检查覆盖同窗预测、有限预算与截断样本、饱和余量、NoGC 启停/丢失计数、最大单段暂停及取消。scope 检查使用实际准入/退出代码，覆盖准入等待排除、冻结后新事件不污染、默认 GC 重叠窗口、并发重复 Dispose 和取消等待；低于最低区域尺寸的 case 不调用 NoGC 申请。并发检查覆盖有界升/降核探测、可比较窗口、退化恢复、复杂度变化、冷却与取消。

这些检查没有主动执行强制 GC 或真正建立 NoGC region，不能替代游戏内生命周期、真实暂停与峰值验证。正常 CLR 的自动 GC 仍由运行时决定。真实游戏证据及未采用的实验见 [研究结论](../../docs/performance/gc-issue36-implementation.md)。
