# GC 与并发决策检查

`portable-runtime` 注入 .NET 9 Mono 按类型查询 GC 信息时的 `PlatformNotSupportedException`，直接执行生产回收路径。5 项合同覆盖检测后 API 不再调用、默认 GC 检查点及不可分割提交续行、取消后完整回收和准入释放、自动及手动阻塞回收、暂停观测不可用以及其他异常继续传播；共享 20 秒截止时间。实际回收使用本机 CLR，Android 原机执行仍须单独验证。

```bash
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- portable-runtime
```

`default-entry` 在默认 GC 的限额和入口诊断各注入一次异常，断言原异常传播、压力信号释放以及下一次独占搜索准入；每项最多 12 秒。实际回收续搜由 `B013-DEFAULT-GC-LIMIT` 原生合同覆盖。

`default-commit` 通过真实 CLR 验证普通 GC 请求的回收与不可分割提交：常规检查点刷新限额，显式退出后由 CLR 接管分配，下一请求重新建立限额；取消保留当前准入。每项最多 15 秒。

独立 .NET 9 工具，直接编译生产 GC policy、Recovery、scope/暂停计数、内存压力信号和 Smart 预测源码；日志与请求活动 tracker 使用最小替身，不需要游戏依赖。

从仓库根执行，省略模式为基础检查：

```bash
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- admission
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- scopes

dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- default-entry

dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- default-commit
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- checkpoint
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- recovery
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- recovery-lifecycle
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- manual-release
```

2026-09-13：基础20项、scope8项、检查点1项、恢复状态机6项、恢复生命周期2项通过。基础与scope覆盖预测、暂停归属、准入、重叠、取消和重复Dispose；恢复检查覆盖完成证据只消费一次、观察/退避、每scope三次上限、物理余量、默认回退和信号断开。

2026-09-17：新增 `admission` 6项，基础运行同时包含（无参数=基础26项），覆盖No-GC区域准入下限：系统余量把区域压到配置预算一半以下时拒绝进入（含问题包原文的12GiB→2 967 362 558），部分缩水、小机器预算与阈值等号仍进入，且拒绝后内存压力信号必须释放分配限额（`RemainingBytes == long.MaxValue`）使检查点在构造上无法触发。准入判定只对 headroom 缩水生效；平台SOH预留上限造成的缩水在尺寸回退循环之前捕获标志，照旧建立区域。

`checkpoint` 与 `recovery-lifecycle` 会执行真实CLR收集；后者实际建立1GB NoGC，以测试主动GC制造意外退出，再穿过生产检查点和恢复入口，断言恢复自身一次预留、零额外强制收集，并验证取消、退出请求和Dispose不能复活旧区域。需有足够可用内存，不适合与性能采样同时运行。状态机检查不等于真实游戏或Windows的性能证明。

本轮游戏对照及失败夹具见[NoGC回退恢复报告](../../../../docs/archive/performance/queen-gc-recovery-20260913.md)；旧研究见[GC与并发调查](../../../../docs/archive/performance/gc-issue36-implementation.md)。

2026-09-21：`diagnostic-failure` 直接链接生产GC策略，覆盖检查点、后台启动/结束、区域退出、请求登记、deferred登记/提升及操作与收尾双重异常，共8项。注入只使用工具侧日志替身；验证原异常传播、完成链终结、排队手动请求及下一次搜索可进入。每项12秒截止时间，失败不得永久阻塞后续清理。

```bash
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- diagnostic-failure
```

同一命令可在Linux或Windows .NET 9运行。详细范围与原生/搜索验证见[GC完成链修复与优化筛选](../../../../docs/archive/performance/gc-completion-allocation-20260921.md)。

2026-10-01：`manual-release` 使用1 GiB真实NoGC预留、200 MiB短命搜索缓冲及64 MiB保留数据，验证手动释放归还空闲提交空间、保留数据访问的工作集稳定和预留退出。Windows普通/ServerGC失败基线及原生生成路线验收见[报告](../../../../docs/archive/performance/manual-memory-release-20261001.md)。该模式主动触发真实GC；Linux尚未执行。
