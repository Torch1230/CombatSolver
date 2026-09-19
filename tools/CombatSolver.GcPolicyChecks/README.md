# GC 与并发决策检查

独立 .NET 9 工具，直接编译生产 GC policy、Recovery、scope/暂停计数、内存压力信号和 Smart 预测源码；日志与请求活动 tracker 使用最小替身，不需要游戏依赖。实验性并发控制器的检查仍可通过 `parallelism` 单独运行。

从仓库根执行，省略模式为基础检查：

```bash
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- admission
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- scopes
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- checkpoint
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- recovery
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- recovery-lifecycle
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- commit-window
```

2026-09-13：基础20项、scope8项、检查点1项、恢复状态机6项、恢复生命周期2项通过。基础与scope覆盖预测、暂停归属、准入、重叠、取消和重复Dispose；恢复检查覆盖完成证据只消费一次、观察/退避、每scope三次上限、物理余量、默认回退和信号断开。

2026-09-17：新增 `admission` 6项，基础运行同时包含（无参数=基础26项），覆盖No-GC区域准入下限：系统余量把区域压到配置预算一半以下时拒绝进入（含问题包原文的12GiB→2 967 362 558），部分缩水、小机器预算与阈值等号仍进入，且拒绝后内存压力信号必须释放分配限额（`RemainingBytes == long.MaxValue`）使检查点在构造上无法触发。准入判定只对 headroom 缩水生效；平台SOH预留上限造成的缩水在尺寸回退循环之前捕获标志，照旧建立区域。

`checkpoint`、`recovery-lifecycle` 与 `commit-window` 会执行真实CLR收集。`recovery-lifecycle` 实际建立1GB NoGC，以测试主动GC制造意外退出，再穿过生产检查点和恢复入口，断言恢复自身一次预留、零额外强制收集，并验证取消、退出请求和Dispose不能复活旧区域。`commit-window` 使用女王日志的853,962,871字节区域和513,894,480字节预留，验证检查点不重新建立仅剩55MB可用窗口的区域、释放限额、不误降并行度，并在需求变小时通过原冷却机制恢复。需有足够可用内存，不适合与性能采样同时运行。状态机检查不等于真实游戏或Windows的性能证明。

本轮游戏对照及失败夹具见[NoGC回退恢复报告](../../docs/performance/queen-gc-recovery-20260913.md)；旧研究见[GC与并发调查](../../docs/performance/gc-issue36-implementation.md)。
