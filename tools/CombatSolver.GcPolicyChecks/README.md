# GC 与并发决策检查

独立 .NET 9 工具，直接编译生产 GC policy、Recovery、scope/暂停计数、内存压力信号和 Smart 预测源码；日志与请求活动 tracker 使用最小替身，不需要游戏依赖。实验性并发控制器的检查仍可通过 `parallelism` 单独运行。

从仓库根执行，省略模式为基础检查：

```bash
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- admission
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- scopes
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- checkpoint
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- between-searches
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- recovery
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- recovery-lifecycle
```

2026-09-13：基础20项、scope8项、检查点1项、恢复状态机6项、恢复生命周期2项通过。基础与scope覆盖预测、暂停归属、准入、重叠、取消和重复Dispose；恢复检查覆盖完成证据只消费一次、观察/退避、每scope三次上限、物理余量、默认回退和信号断开。

2026-09-17：新增 `admission` 6项，基础运行同时包含（无参数=基础26项），覆盖No-GC区域准入下限：系统余量把区域压到配置预算一半以下时拒绝进入（含问题包原文的12GiB→2 967 362 558），部分缩水、小机器预算与阈值等号仍进入，且拒绝后内存压力信号必须释放分配限额（`RemainingBytes == long.MaxValue`）使检查点在构造上无法触发。准入判定只对 headroom 缩水生效；平台SOH预留上限造成的缩水在尺寸回退循环之前捕获标志，照旧建立区域。

`checkpoint` 与 `recovery-lifecycle` 会执行真实CLR收集；后者实际建立1GB NoGC，以测试主动GC制造意外退出，再穿过生产检查点和恢复入口，断言恢复自身一次预留、零额外强制收集，并验证取消、退出请求和Dispose不能复活旧区域。需有足够可用内存，不适合与性能采样同时运行。状态机检查不等于真实游戏或Windows的性能证明。

`between-searches` 是搜索间检查点的有界合同：覆盖首次/未配置/小分配跳过、回调拒绝与计数、取消和异常后的状态恢复、无收益抑制、Disable及新scope重开、独立scope不互等；真实部分遵守进程级NoGC串行准入，在第一个1GB scope活动时确认第二个只排队，第一个超过256MiB分配后完成一次真实Gen2/区域重开且不等待排队请求，释放后再验证第二个scope准入。它使用工具侧回调模拟部分门控，不是性能或真实游戏收益证明；需要足够可用内存，也不应与性能采样或其它NoGC检查并行运行。

2026-09-22：`between-searches` 7项及基础/准入26、scope8、checkpoint1、恢复9、恢复生命周期2、诊断失败8，共61项通过。初版夹具错误要求并行NoGC准入，5秒超时后按生产既有串行契约修正；最终夹具以真实等待日志确认排队。完整性能、原生证据和局限见[计算过程中的搜索间回收](../../docs/performance/active-search-gc-20260922.md)。

此前游戏对照及失败夹具见[NoGC回退恢复报告](../../docs/performance/queen-gc-recovery-20260913.md)；旧研究见[GC与并发调查](../../docs/performance/gc-issue36-implementation.md)。

2026-09-21：`diagnostic-failure` 直接链接生产GC策略，覆盖检查点、后台启动/结束、区域退出、请求登记、deferred登记/提升及操作与收尾双重异常，共8项。注入只使用工具侧日志替身；验证原异常传播、完成链终结、排队手动请求及下一次搜索可进入。每项12秒截止时间，失败不得永久阻塞后续清理。

```bash
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- diagnostic-failure
```

同一命令可在Linux或Windows .NET 9运行。详细范围与原生/搜索验证见[GC完成链修复与优化筛选](../../docs/performance/gc-completion-allocation-20260921.md)。
