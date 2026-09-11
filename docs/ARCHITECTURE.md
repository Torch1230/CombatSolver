# CombatSolver 架构与职责地图

本文描述当前源码的所有权边界。它面向维护者和 coding agent；玩家功能说明见根目录 `README.md`，历史重构证据见 `docs/refactoring/`。

职责迁移时优先更新本文，并同步更新 Windows 的 `tools/verify-refactor-boundaries.ps1` 与 Linux 的 `tools/verify-refactor-boundaries.sh`。历史审计记录保留当时结论，不承担当前导航职责。

## 1. 运行链

### 单一搜索预算与兼容边界

遗物计数策略由 `RelicCounterCatalog` 声明已核对的跨战斗计数，Runtime 过滤总/单项开关与当前持有对象，冻结到 `SearchPolicySnapshot.RelicTargets`。`SimulatedCombatState.RelicCounters` 只投影既有分支状态，`RelicCounterPolicy` 生成范围达标掩码和一次性 HP 额度。快照的 `StrategicHpCredit` 汇总成长与计数额度，终局/中间排序、用药审计及保路共享；真实战损早停仍须同时满足成长、药水、偷窃和已启用的遗物目标。`SolverRelicStrategyPanel` 拥有 UI 输入，Overlay 只接线，Controller 使续用失效并按原自动计算偏好重算。设置导出、归档恢复与磁盘路线键携带同一策略，旧包默认关闭。见 [完整计数清单](relic-counters.md)。

开局后续动作探针通过 `ApplyFixedPrefix(seed, prefix)` 构造真实父链，保留前置资源/药水/准备动作的动作数与状态；不得用已经回放前缀的快照伪装成 action_count=0 的根。

`SolverSettings` 将四档或自定义配置解析为一个 `Profile`，主线程冻结到 `SearchPolicySnapshot`。`CombatSearchCoordinator` 的主搜索、药水审计与恢复使用同一套预算维度；`FixedBudget` 只限制无胜利后的预算扩展，测试/API 可显式覆盖时间。Search 不再包含 Short/Deep 配置、枚举、检查点或分段累计统计；两端结构门禁禁止这些符号回流。`SearchRequestWorkTotals` 按请求累计唯一 elapsed 和工作计数。

旧设置的 deep 字段仅在反序列化边界迁移至 search 字段，保存只写新字段；旧归档读取 deepProfile，新的回放政策覆盖文件使用 profile/fixedBudget。旧无人请求的 forceShortSearchOnly 与短/深时间字段在 ProtocolHost 入口转成固定预算；已退休的阶段断言参数不再接受。UI 设置只渲染单列预算。

`CombatDiagnosticJournal` 仍按战斗保留详细诊断，额外向有界进程日志复制 GC/分配预算/主线程帧摘要，供跨战斗关联。高频显示与节点日志不复制。`SearchGcPolicy` 分别记录收集完成后的堆状态和重启 NoGC 后的状态，诊断采样不改变收集策略。

`SmartLayerMemoryForecast` 只决定有证据能改善容量的可选层间回收：完整层超过新区域容量、缺少预测或当前区域新分配低于 max(64 MiB, 区域限额/4)时沿用每批准入。`SearchGcPolicy` 的自动回收请求已确认完成的后台收集；搜索中和搜索后保持同一路径，手动回收继续强制压缩。最新实机 trace 已证明按碎片比例自动压缩会造成数秒停顿，因此碎片比例不再决定自动压缩。算法层不调用 GC。

`NodePoolSignalLifetimePatch` 补齐原版 `NodePool<T>` 递归信号清理的包装所有权：返回的 typed array 通过底层 Array 释放；字典、Variant、从原生转换得到的新 StringName 在作用域退出时释放。节点与 Callable 的目标不属于此作用域，保留原解绑条件；原版 Free 的对象池账本和 OnFreedToPool 保持原调用链。NCard 与 NGridCardHolder 的共享泛型方法分别由真实方法合同覆盖。

可选的 `src/Diagnostics/PerformanceRecording.cs` 是主线程标量采样和状态提示入口，由 Dispatcher 安装；`PerformanceSession.cs` 拥有进程级有界队列、后台文件写入及 OS/GC 采样；`PerformanceLifecycle.cs` 仅计量跑局/房间异步生命周期。节点重建复用同一进程写入器，游戏对象只以弱引用追踪。`tools/watch-performance.ps1` 在独立进程采集 EventPipe 和用户明确触发的 Heap dump；配置文件存在时才启用。诊断不修改搜索政策、GC 模式或第三方行为，详情见 [全程性能录制](performance/long-session-recording.md)。

包装登记探针只捕获 Godot 两个进程级线程安全弱登记容器，后台读取 Count，不遍历目标。watcher 用一个采集器交替运行短 GCHandle 窗口与普通段；GC 关联栈持续保留。补丁清单在同次采集内只解析一次 PatchMethod，避免重复程序集查找。采集完成与解析完整性是不同状态。

```text
Entry / turn hooks
  -> SolverController（主线程会话与请求）
  -> CombatRootSnapshot.Capture（主线程稳定根）
  -> CombatSearchCoordinator（后台主搜索与反事实审计）
  -> CombatBeamSolver（分支搜索）
  -> SolverResult
  -> SolverOverlaySnapshot.Capture（主线程 UI 投影）
  -> Overlay renderer / 原版部署入口
```

搜索 worker 接收 `CombatRootSnapshot`、`SearchPolicySnapshot`、诊断 sink、帧压力信号和取消令牌。它不读取全局设置、控制器、UI 或无人测试状态。

成长策略由 `GrowthBudgets` 随请求冻结，每次实际收益按对应来源取得 HP 额度，中间保路和终局排序沿用同一份额度；成长侧栏只编辑原有额度和忽略收益开关。`CardMechanismFacts` 提供小刀数量、攻击命中与消耗抽牌的纯值估计，`StrategicEffectModel` 消费分支状态；当前没有奖励／商店评分模块。

普通搜索在 Runtime 同时等待根回收屏障、原生动作队列及当前动作完成后捕获根；队列因等待玩家选择暂时无可执行动作时，当前动作的完成任务仍约束捕获。任何异步等待恢复后都重新进入请求校验，沿用请求身份和战斗生命周期取消；专用回合准备选牌入口先行处理。

## 2. Runtime

| 文件 | 职责 | 不负责 |
|---|---|---|
| `src/Runtime/Entry.cs` | Mod 初始化、补丁安装、战斗与回合生命周期入口、无人请求循环启动 | 搜索策略和战斗语义 |
| `src/Runtime/CombatSolverLog.cs` / `CombatDiagnosticJournal.cs` | 独立日志入口；生产线程入队不可变消息，复用后台事件文件；战斗切换摘要化、搜索日志绑定所属战斗、提交前缀冻结 | Godot 全局日志收集、搜索候选判定、后台读取 live 状态 |
| `src/Runtime/OnlinePresence.cs` | 主线程在线标量采样、共享持久安装标识和证书固定的 HTTPS 客户端；无头和多人隔离 | 搜索策略、完整路线上传、服务端历史存储 |
| `src/Runtime/RunStatistics.cs` / `RunStatisticsStore.cs` | 主线程跑局/战斗/设置/实际操作标量事件；独立有界队列，后台持久化、原生结算恢复与幂等补传；不可变提交时战绩快照 | 搜索状态键、模拟、游戏存档修改、历史求解器参与推断 |
| `src/Runtime/SolverController.cs` | 主线程搜索/续用/部署/全自动编排，结果过期与重算审计 | Beam 内部算法和 UI 布局 |
| `src/Runtime/SolverControllerSessions.cs` | combat/search/deployment 三类会话的状态与取消所有权 | 跨会话全局静态字段堆积 |
| `src/Runtime/CombatRootSnapshot.cs` | 主线程捕获完整预测根，比较捕获前后 live 状态，并向 worker 提供 Fork 根 | worker 惰性读取 live 战斗 |
| `src/Runtime/ContinuationStamp.cs` | 跨回合 live/predicted 状态文本、首个差异与完整差异；九条战斗 RNG 使用计数器与四段内部状态共同核对 | Beam 状态去重 |
| `src/Runtime/SolvedRouteCache.cs` | 主线程捕获路线记录键；后台按完整根与策略读写本地路线副本，独立于战斗会话和 SL 入口；Forecast 使用新根对象 | 搜索策略、原生存档修改、保留旧战斗对象 |
| `src/Runtime/DynamicVarCloneMetadataPatches.cs` | 模拟域精确复制 BaseLib 提示/升级及 Ritsu 提示元数据，只为已有值建立弱表项；保留 live 行为 | 通用 SpireField 工厂替换、丢弃升级值、清空全局弱表 |
| `src/Runtime/BaseLibCloneConcurrencyPatch.cs` | BaseLib 克隆扩展存在时，让原版 `MutableClone` 与内嵌模拟的模型深克隆共用窄串行边界，保护其全局弱表 | 整段搜索串行化、BaseLib 业务语义与候选政策 |
| `src/Runtime/PowerDynamicVarWarmup.cs` | 主线程根捕获时物化规范 Power 与当前战斗 Power 的显示变量 | 搜索评分、Power 语义与 worker 本地化 |
| `src/Runtime/PowerDynamicVarMaterializationGuardPatch.cs` | 搜索模拟惰性创建 Power 显示变量时立即报告根捕获缺失 | Power 语义、显示内容与搜索阶段串行化 |
| `src/Runtime/PowerAmountComparisonPatch.cs` | 将原生 `GetTypeForAmount` 中两处精确匹配的同枚举装箱比较改为整数比较；保留虚 getter、decimal 分支和调用顺序，未知 IL 原样保留 | Power 状态缓存、跳过类型 getter 或改变显示类型规则 |
| `src/Runtime/SearchGcPolicy.cs` | 管理玩家显式开关的进程级 GC 模式：开启时按原样预算建立战斗级 NoGC、执行搜索内安全检查点与引用释放后的压力回收；稳定关闭时使用 CLR 常规分代 GC 且不新增自动补账压力，从开启切换时仍结清此前义务；模式切换和手动释放与活动搜索计数共用安全边界 | Beam 剪枝、候选评分、模拟语义与同步阻塞 UI |
| `src/Runtime/SearchGcLifecycleMetrics.cs` | 记录显式回收与 NoGC 启停/丢失；在 Runtime 准入 Gate 内冻结 scope 起止，区分独占搜索与共享进程窗口；暂停最大值仅为观测值 | 线程级 CLR 事件归因与 trace 最大值 |
| `src/Runtime/ProcessWorkingSetTrimmer.cs` | Windows 手动释放在托管堆压缩后修剪当前游戏进程工作集 | GC 生命周期、搜索调度与自动触发 |
| `src/Runtime/SystemMemoryReleaseService.cs` | 等待当前进程回收完成，再通过 UAC 启动短命辅助程序清空系统工作集与待机列表 | 自动触发、修改页列表清理与搜索策略 |
| `src/Runtime/SearchMemoryPressureSignal.cs` | 将 Runtime 的进程分配边界、回收入口和低系统余量下的保守并行标记注入搜索；不让 Search 直接操作 GC 模式 | 设置读取与搜索评分 |
| `src/Runtime/SolverControllerSessions.cs` | 除会话状态外，向 UI 提供当前进程占用与活动搜索分配检查点的只读快照 | UI 样式与搜索内存政策 |
| `src/Runtime/SolverSettings.cs` | 持久化性能、执行、搜索并行度、NoGC 开关与独立预算、逐槽药水策略和搜索结束通知设置，并在主线程捕获不可变搜索 snapshot | 搜索期读取全局设置 |
| `src/Runtime/PlayerTurnSetupPatches.cs` | 准备阶段稳定根搜索与既有选择重放；原生会话独占生命周期，每次搜索独立取消并排空，页面等待后原子确定唯一 worker 所有者；结果发布结束接管标志，手动提交淘汰旧根；后续回合无既有选择时捕获准备根；进入 Play 后交给 continuation 核对 | 普通 Play 阶段搜索与动作部署 |
| `src/Runtime/NativeChoiceRuntime.cs` | 观测原生选择 Task 完成及页面序号，按卡牌语义状态匹配计划实例；搜索期间保留手动输入，实际驱动期间持有页面锁，清除尚未提交的手动勾选后选择计划实例 | 选择分支枚举和战斗结算 |
| `src/Runtime/ClientUpdateNotice.cs` | 解析现有心跳响应、严格比较三段版本、发布线程安全纯值提醒；OnlinePresence 在主线程通知 Overlay 刷新 | 网络请求调度、安装更新或战斗操作 |
| `src/Runtime/CombatBugReportExporter.cs` | 主线程冻结当前/最近战斗的实机取证状态；单消费者后台 FIFO 按检查点顺序整理并一次序列化为 UTF-8 字节，导出任务作为队列屏障等待此前记录完成 | 后台读取 live 战斗、通用 replay/native-state 导入 |
| `src/Runtime/CombatBugReportDescription.cs` | 汇总本场结构化异常、重算和战损信号，提供诊断文字与标签 | 网络字段拼装、搜索决策 |
| `src/Runtime/CombatBugReportMetadata.cs` | 主线程冻结战斗、角色及已观察怪物的稳定 ID 和显示名称；序列化 report.json v2 的身份、分类及预测战损比较 | 网络请求、搜索策略、后台读取 live 状态 |
| `src/Runtime/CombatBugReportUploader.cs` | 通过不继承游戏进程代理的专用客户端直连接收服务；校验问题包与文本上限，以 multipart 流式上传并传播取消，限制服务端响应，并以反馈编号和实收字节数确认完整接收 | 问题包内容生成、隐私脱敏、UI 单实例与确认流程 |
| `src/Runtime/SearchCompletionNotifier.cs` | 搜索成功、失败、停止或过期后按设置决定是否通知；Windows 使用原生通知和系统提示音，先核对前台进程并在非 Windows/headless 环境停止 | 搜索生命周期、跨平台伪通知和自定义声音播放 |

`SolverCombatSession` 持有本场路线、续用和重算状态；`SolverSearchSession` 持有 generation、取消、进度和帧观测；`SolverDeploymentSession` 持有部署取消。旧回调只能写回创建它的 search session。

`SearchGcPolicy` 将活动搜索期间收到的后台回收请求保存在独立的 deferred 完成链中，所有搜索退出后才提升为实际后台回收。搜索内内存检查点只等待自己能够完成的回收，不能等待以该搜索退出为前提的任务；手动工作集释放继续等待搜索后的回收链。已覆盖的取消及 GC 转换后注入失败路径会协调 CLR 实际模式与内部所有权，并落定对应完成链、释放等待屏障；这些断言不穷举 CLR 转换前失败、OOM 或日志系统异常。搜索账本的存活与运行时 GC 模式互不混用。

搜索内检查点在 Gate 外直接等待非压缩后台 Gen2 primitive，不加入上述 deferred 链。primitive 先观察最新已完成 Gen2 的 index 与新 LOH 弱哨兵，仅在上一轮已完成却未覆盖哨兵时再次请求；不按定时器盲重发。全部异步等待不捕获调用方上下文。已发出的回收不能随搜索取消而放弃：确认完成后取消才落到默认 GC，超时或确认异常先显式阻塞排空，不能提前重建 NoGC。回收开始前的手动 GC 有独立完成信号，回收确认成功但搜索取消/超时不使它误报失败；开始后的手动请求与新的引用释放义务继续等待后续安全回收。日志分开记录请求模式、实际完成类型/index、CLR Concurrent 标志及阻塞超时兜底，不承诺每次都采用并发 GC 或没有暂停。
### 2.1 战前预测 API 隔离边界

PR #43 集成修正：Mod 使用独立文件复制，游戏程序继续使用硬链接；运行目录按主进程 PID 隔离。普通退出会移除大型游戏与 Mod 副本，保留会话诊断材料。启动快照缺失时 API 显式失败。当前账号目录由游戏路径 API 解析，并映射到禁用 Steam 的 worker 账号目录。求解设置按值冻结、进入状态令牌与 worker 签名，配置改变时重建 worker 并写入捕获值；整体期限从排队前开始，显式 Stop 会取消活动请求。

`src/Api` 是供伴生 Mod 使用的公开战前边界。API v6 只暴露不可变请求选项、目标坐标、已确定的非战斗路径步骤、假设样本选项、规划快照入口、枚举、结果，以及 worker 状态和生命周期入口，不暴露 `SolverController`、live `CombatState` 或搜索内部类型。

- `PreCombatLiveStateSnapshot` 只能在主线程捕获当前单人跑局。它用 `RunManager.ToSave` 获取完整 `SerializableRun`、记录精确加载的 Mod 集合和游戏/用户目录，并生成包含规范化存档与当前房间身份的 SHA-256 状态令牌。Mod 源路径优先解析到主进程初始化时建立的独立文件副本，使内存中的已加载版本成为 worker 的权威来源。
- `PreCombatRunSerialization` 清除墙钟、平台和地图涂鸦等非语义字段；显式写出游戏序列化器会省略、但反序列化默认成 `true` 的 `can_modify:false`；只移除恢复后会从空对象变成缺失值的事件历史 `variables:{}`，保留非空变量。这样子进程恢复后的全量快照能够逐字节核对。
- `PreCombatForecastApi` 负责参数验证、相同状态确定请求的去重/缓存和返回前主线程复核。确定预测要求目标地图列坐标，避免远端战斗沿用当前位置派生怪物 RNG；可选路径步骤必须逐层连续且只能是已确定的篝火、宝箱或商店等非战斗房间，未决事件和中间战斗会被拒绝。活动跑局、战斗状态或令牌发生变化时只返回 `LiveStateChanged`。
- `SimulateAsync` 是与确定预测分开的显式假设入口。它只接受当前幕原生普通、精英或 Boss 遭遇，以当前状态和下一可用地图行建立战斗，不进入确定预测缓存；调用方提供的样本种子只在隔离进程完成精确快照恢复后替换遭遇局部 RNG 和九条战斗相关 RNG。`UpFront`、奖励、地图与主进程 RNG 不被推进。
- `SimulatePlanningAsync` 是规划状态专用的显式假设入口。worker 从调用方提供的独立 `SerializableRun` 恢复牌组、生命、药水和地图规划状态；live 跑局只用于捕获环境并在返回前做令牌复核。它允许 `Unknown` 与原生事件节点进入战斗，并在主线程按目标坐标确定第二首领；规划存档的幕、种子、角色和玩家身份不匹配时显式失败。
- 调用方可读取 worker PID、忙闲、工作集、私有内存、峰值工作集、静音标记与空闲期限，也可显式停止或重启/预热。默认空闲期限为两分钟；API 可在 worker 待命时把期限重新设为任意合法毫秒值并重新计时，或以 `null` 取消空闲关闭。单个确定请求或一个调用方组织的样本批次可以在 reusable 屏障后选择立即关闭；生命周期选择不改变结果缓存键。
- 默认调用仍共享相同请求；显式可见的手动面板可要求独占取消和强制重算。独占取消会等待其精确拥有的 worker 进程结束后才完成，不能留下后台计算。
- `PreCombatForecastWorker` 串行拥有一个 Windows 子进程会话。主进程初始化期间先在游戏目录下带所有权标记的 `.combatsolver-precombat/startup-mods` 中钉住本次会话选中的 Mod 文件，后续 worker 从这些固定文件镜像实际加载的全部 Mod；工坊目录在运行中被 Steam 替换不会改变 worker 的版本。worker 另行硬链接游戏文件、复制必要配置，并使用独立的 `APPDATA` / `LOCALAPPDATA`、关闭 Steam 和 NoGC。隔离设置中的主音量、BGM、音效和环境音均强制为零并回读验证。
- 相同游戏根、用户根和 Mod 集合的连续请求会在同一子进程中依次执行。父进程只有在收到匹配 runId 的 result，并等到子进程返回主菜单、后台活动归零及 matching ready 屏障后才允许复用；达到调用方选择的空闲期限、显式释放、失败、超时、取消或主进程退出都会关闭拥有的进程。“一直维持”仍会在显式停止、失败或主进程退出时清理，不会留下脱离所有权的进程。
- 子进程由 `ScenarioBuilder` 直接恢复完整跑局，加载资源和地图后再次规范化序列化并核对精确哈希；只有通过后才按顺序补记已确定的中间非战斗地图历史，应用可选入战 HP，并用目标坐标、房间和节点类型进入遭遇。确定预测由此保持目标 `TotalFloor`、地图坐标、怪物局部种子、正常开战 Hook、首回合初始化和搜索流程一致。假设样本则在哈希核对之后、怪物生成之前注入独立样本 RNG。中间房间的购买、奖励、锻造和其他玩家状态变化不会被擅自执行，必须由调用方标为条件场景。

`src/Api` 禁止直接调用 `SolverController.RequestSearch`、`CombatManager.SetUpCombat` 或 `RunManager.EnterRoomDebug`。这些静态边界由 Windows/Linux 两份 `verify-refactor-boundaries` 脚本共同检查。隔离 worker 内通过 `COMBATSOLVER_PRECOMBAT_WORKER=1` 关闭 API，避免加载伴生 Mod 后递归创建 worker。

`RitsuBaseLibTargetTypeLookupPatch` 属于 Runtime 的第三方适配：只在模拟隔离域缓存目标桥的精确类型查询，以 Assembly 弱键持有准确 Type/缺失；具体回调由当前桥中唯一的 Assembly→Type 签名定位，适配不匹配显式失败。`RitsuBaseLibTargetTypeResolutionPatches` 还记录原版全程序集查询正常返回 null 时的缺失证据。`AssemblyTypeAbsenceCache` 用 AssemblyLoad 代次判定静态程序集集合是否变化，并在每次复用前重新查询弱引用中的动态程序集；加载新程序集、动态晚创建目标类型、原查询失败或找到类型时均不能复用旧缺失结论。live 调用仍走原桥。它们不缓存框架是否安装、注册表或目标谓词，不持有分支模型，也不参与搜索策略。 `TARGET_TYPE_ABSENCE_CACHE scope=process_cumulative` 记录命中、原查询和旁路；对单次请求取首尾差值，不能直接累加日志。

玩家死亡被确认后，`CombatPredictionSimulator.HandlePlayerDeath` 先调用 `SimulatedCombatState.RemovePowersAfterDeath`，再清理球和宠物。敌人能力仍由原领域死亡清扫处理；玩家不能依赖仅遍历敌人的后续清扫。

`CombatPredictionSimulator.CardTargeting` 对君王之剑和小刀完整读取分支能力：能力存在时选择全体，不存在时选择单体。两侧都不能回退到可能读取实机 owner 的动态 TargetType；普通卡牌保持原生目标元数据入口。

## 3. Search

开发中的反馈修复：`GrowthValues.CaptureFatalTarget` 在主线程冻结单一原版击杀成长来源的早停目标；`SearchPolicySnapshot.GrowthTargetSatisfied` 统一比较收益完成情况。早停还要求实际用药不超出用户必要数量。偷窃分项沿既有 SimulatedCombatState 计数投影为 SimulationSnapshot → SolverSnapshot → OverlaySnapshot，只读 UI 不重新读取真实战斗。Runtime 在选牌部署失配时暂停并交还手动选择，只有退出场景才取消原生选择；缺失战斗通知的面板恢复由 MonitorCombatPresence 在稳定回合负责。
`SearchPolicySnapshot.CanStopAtHpTarget` 统一默认开启的战损目标早停与实际成长目标例外。主线程根据本场卡牌冻结 HasGrowthTargets，额度本身不代表持有对应牌。Phases 在已准入候选提交时检查完整胜利与强制用药要求，命中后排空当前父节点/并行批次，释放后续工作并从达标候选收尾；Coordinator 在补充搜索结果边界沿用同一开关与阈值。
`GrowthCostPolicy` 管理至亮之焰单场累计最大生命消耗的准入；成本属于 SimulatedCombatState 的独立分支值，从主线程原生出牌历史捕获，经 Fork 复制并进入指纹/续用文本。`ResolveRoundChoiceBranches` 与 `ResolveTurnSetupChoices` 在产出候选前统一拒绝超额分支，实际模拟仍执行原有效果。禁忌魔典的收益计数在已有 CardPowerOnPlaySupport 中记入 GrowthValues，允许额度由成长策略设置决定。

`CombatSearchCoordinator.FailureRecovery` 在请求级完成主搜索与药水审计后，管理无完整胜利的有限追加搜索。它扩大搜索配置、保留请求剩余时间并比较已有质量；交接结果优先返回，每轮内存观测独立起算。四档内置节点预算由 `SolverSettings` / `SolverSearchProfile` 声明，Custom 保留显式设置。

根创建时，`PredictionModPatchAudit` 在 Prediction 层检查已有卡牌 OnPlay 的第三方 Harmony 补丁；每根按类型去重并读取当前补丁表。它只负责未支持行为的准入边界，不执行补丁或提供第三方镜像注册，后续生成卡牌和其他方法不在此入口覆盖范围。

`BuildAcceptedEndTurnNodes` 是回合层/软时间预算收尾及普通串行回合尾的共同入口，复用 raw EndTurn 批次生成、跨回合剪枝与循环出口准入。全部直接选择分支在转置准入前结算临时观测；批次持有未转交快照，迭代器提前结束或生成失败时统一释放。

### 3.1 请求级编排

- `SearchPolicySnapshot.cs`：主线程捕获的不可变搜索设置、逐槽药水策略，以及第一/二幕与最终 Boss 各自的血量取舍；后台不读取 UI 或玩家设置。
- `SearchDiagnosticsSink.cs`：搜索日志和可选纯值路径观察出口。观察默认关闭，先按状态键过滤，命中后才复制完整动作/选择路径与政策标签；另可显式筛选外层 Prune 池，记录完整输入、真实 RankBest 的原排名/必保/路由/选中索引、当时的战术估值标量及最终仲裁集合。RankBest 内部同步借用列表，立即转成值副本；不向注入方暴露节点、模拟器或闭包，不重算估值或选择器，也不参与候选裁决。注入方负责并发和输出容量。
- `SearchFramePressureSignal.cs`：Runtime 向 worker 提供的帧压力信号；以最近 `31` 个非搜索帧中位数建立基线，压力阈值为 `max(33 ms, baseline × 1.5)`，无显示服务的 headless 请求旁路帧恢复等待。
- `SearchRequestWorkTotals.cs`：一次请求内所有正常、失败和取消 solver 的工作区间均精确记账一次，包括取消前已发生的展开、转移、选牌、耗时、分配和 GC；Smart 有限药水层之间由 coordinator 主动执行的内存整理也单独计入耗时、分配和 GC，但不伪装成额外 solver。请求总值不是完整 coordinator 外层墙钟或进程峰值，也不承担结果质量排序。
- `CombatSearchCoordinator.cs`：一次请求的搜索编排；Smart 先搜索无药基线，再根据可用药水、无药战损和药水价值门槛确定最多进入的“恰好 `N` 瓶”层。按瓶数递增搜索，同层药水共同竞争；第一层完整获胜且满足救命、节省生命或保全被盗资源条件时立即采用并停止增加药量。达到设置的可接受战损阈值也可提前结束请求，不保证遍历全部药水层或取得所有药量中的全局最优。进入下一梯度前回收上一层搜索图并重建 NoGC 区域；截止时保留已完成且符合政策的选择。跨 solver 只发布符合政策的严格改善完整路线，并透传当前 solver 已完成回合的候选。玩家可采用已显示路线或只执行当前回合。Disabled/RequireAtLeastOne 保持各自政策；实际运行的各层共享请求级时间余量并合并总指标。
- `CombatPlan.cs`：Runtime 消费的计划、结果和续用数据。结果不得保留历史 Simulator 对象图。
- `SearchReplayEvidence.cs`：最终选中路线已有父链标量与同次遗物标注回放的逐动作对账，记录首个 HP/格挡/能量/星能/手牌数差异；仅差异时生成完整回放状态文本，最终失败时保存双侧完整状态。普通候选不增加状态转储；异常路径记录失败候选前缀和尝试动作。只通过 diagnostics sink 输出不可变文字。

`ActionRelicTriggerRecorder` 仅存在于最终路线回放，附带 Damage/Heal 的来源、请求/修正数值和 HP 前后值；普通 Beam 分支保持 null，不分配取证列表。直接字段赋值等绕过 Damage/Heal 的变更尚无来源事件，不能把这份记录宣称为所有语义写点的完整追踪。

`CombatSearchCoordinator.FailureRecovery.cs` 为没有完整胜利且未耗尽预算的主搜索或 Smart 精确药水层提供一次标准窄宽度恢复。两次搜索共用原层节点和时间上限，药水约束与同一个请求截止信号保持一致；失败和取消的工作仍由 `SearchRequestWorkTotals` 精确计入。已有胜利、玩家接管、预算耗尽或原配置已不宽于标准值时直接返回。该机制缓解 Beam 宽度的非单调性，不提供完备性或“高档必然优于所有低档”的保证。

周期候选在最多 32 步的窗口内比较重复动作、控制形状及伤害发生相位，避免把较长周期中的安静阶段当成整个循环。每周期伤害数值可以变化：动作、形状和伤害相位重复且实际刷新敌人耐久低点时，可取得伤害进展证据；精确转移增量是否一致仍单独记录，不把增长伤害伪装成相同增量。已证明刷新逐敌人历史最低耐久的路线可使用独立进展通道：每个 region 每层至多一个代表，最多保留该周期余下的 31 个安静动作，且只由实际保留节点的一个直接后代消费。只有新的最低耐久能续期；普通停滞、试探和顺序选择预算不因此重置。进展准入在最终仲裁后结算，并解除已经完成目标的旧出口探针；所有动作仍逐步模拟并受请求节点与时间限制。

主 incumbent 只能由满足硬政策的完整胜利建立。无主动用药入口要求实际生效政策为 `Disabled` 或 `Smart`、最少用药数为0、候选显式用药数为0；若启用逐槽指令，还必须实际满足全部强制使用要求。正数精确药水层保留原条件：最少与最多药量相等、有已审计无药基线、未启用需另证的逐槽强制指令，且完整胜利严格改善基线主质量。未完成路线、死亡路线或仅满足中间评分的候选不能建界。

完整胜利按统一战略战损计价：累计掉血、最终最大生命缺口、路线治疗、无条件战后遗物回血和保命遗物消耗。未完成分支的乐观下界允许当前缺血全部恢复，保留已经发生的遗物消耗代价；中间最大生命缺口可能恢复，不进入下界。搜索中的路线展示、主结果剪枝、保留和最终排序共用该口径。诊断日志以 `source=no_explicit_potion` 或 `source=exact_potion_layer` 区分建界来源。

Smart 层间使用 `SmartLayerMemoryForecast` 的同窗分配和转移高水位估算下一层容量；预测超出余量、样本不完整或区域丢失时回收并重建 NoGC。回收仍遵循原有药水层准入及停止条件。

普通 Beam 保持原有评分、动作数、`OffensiveProgressValue` 初始排序及必保候选构造。在必保候选置换之后、药水配额处理之前，定位原排序中最后一个实际存活的普通候选，仅对跨越该截线且 `BeamRankScore` 与动作数都精确相等的块做有限多样性保留：同一 `PotionCount` 内按进展值分组，值从高到低轮流取代表。组内仅无既有保留路由签名的候选按当前回合和完整转置标签隔离，再以零费可执行牌数、可达手牌价值、手牌数稳定排序，写回各组原位置；带签名节点的原组内位置不动。签名存在性直接复用 `RetainedRoutingChoice`，包括其既有跨回合例外，不重新定义时效或依赖观察器。必保候选、各标签和该块各药量已有席数、其他评分块、总容量和工作预算不变；单值组、单席组、完整终局优先模式及含获胜候选的块旁路。这避免同分截线被单一进展值占满，不使用卡牌或遭遇身份，也不保证有限宽搜索完备。

### 3.2 CombatBeamSolver 分片

同回合落选队列仅在现有失败窄搜 profile 开启：完整 Prune 结算后收集无未结调度资格的实际落选叶，正常释放模拟器，保存原父链及政策/循环证据。每裁剪最多 `min(128, Beam×2)` 张票，总数最多 `min(1024, Beam×16)`，路线节点数总和最多 `min(16384, 原节点预算)`，每条最多128动作；这些是数量界，不是固定字节上限。活动队列自然清空且没有硬药水约束下的完整胜利时，轮流服务各已记录裁剪队列，原根和每个前缀动作实际开始即计入原节点预算，转移也照常计账。恢复叶只更换经状态键/快照核对的模拟器，重新完整Prune后才正常展开，不重做动作准入、不清TT或循环/有序账本、不恢复过期资格。重放和空重试不推进循环epoch；已有同回合ended集合最后统一注释。回合层和全局时间均不重置，转回合、取消、接管或返回会清空队列。v66已通过Custom目标搜索与真实部署，不保证完备或性能收益。

| 文件 | 权威职责 |
|---|---|
| `CombatBeamSolver.cs` | 构造参数、不可变根配置、`SearchRunContext` 与两个策略对象接线 |
| `CombatPlan.cs` | `SearchNode`、`SimulationSnapshot`、动作与最终计划数据 |
| `CombatBeamSolver.Models.cs` | 转置标签、`SearchFeatures`、单次运行 `SearchRunContext` |
| `CombatBeamSolver.Phases.cs` | `Solve`、阶段循环、总预算与回合层预算保留、当前回合预览、约 `200 ms` 刷新的动态推演路线，以及玩家采用路线/执行当前回合的收束检查点；动态路线显式携带战斗是否结束，未完成路线不产生整场战损数值 |
| `CombatBeamSolver.Expansion.cs` | 可执行卡牌/药水/结束回合候选展开和动作回放入口 |
| `CombatBeamSolver.RoundLifecycle.cs` | 回合推进及唯一的玩家回合开始阶段；保持选择事务、Hook/抽牌顺序与历史/死亡补偿 |
| `CombatBeamSolver.ActionPreparation.cs` | 串行／并行共用的卡牌与药水动作准备；同步消费当前值，返回独占动作元数据 |
| `CombatBeamSolver.ParallelExpansion.cs` | 固定 worker lane、原始候选物化、按输入顺序串行提交 |
| `CombatBeamSolver.AdmittedExpansion.cs` | 已准入父节点的准备、动作探测、选择准备/回放/续接、药水/目标与回合尾部作业；有界派发、快照移交、取消/异常排空 |
| `CombatBeamSolver.PrimaryChoiceReplay.cs` | 原预算保证必经的首层回放、唯一快照暂存与原序消费；动态预算和实例补充仍由一个续接作业独占 |
| `CombatBeamSolver.StandPatJobs.cs` | 对原保路规则必经的 EndTurn 探针批量求值，复用固定 lane、回传标量，缓存和选择仍由 coordinator 原序完成 |
| `CombatBeamSolver.RetentionJobs.cs` | 剪枝只读索引作业；复用空闲固定 lane，按原索引收集输出，排空后统一记账并传播取消/错误 |
| `ParallelExpansionWorkProfile.cs` | coordinator 所有的作业经过时间分布与 wave/等待/提交计时；不代表 CPU 时间 |
| `CombatBeamSolver.PathDiagnostics.cs` | 可选路径观察的值复制与边界配对；分别记录生成、两类转置、实际展开、动作准入、完整保留及回合注释，不写搜索策略或账本 |
| `CombatBeamSolver.DeferredFrontier.cs` | 失败窄搜实验中的同回合落选元数据、有限队列与逐动作有预算回放；不持有独立 live/simulator 根，不重建调度账本 |
| `CombatBeamSolver.Retention.cs` | prune/retention 调用边界与相关小型辅助 |
| `CombatBeamSolver.BeamRetentionPolicy.cs` | 状态去重、中间分数排序、多样性通道、动作/回合开始选牌保路、药水配额和小型 Pareto |
| `CombatBeamSolver.CyclePlanning.cs` | 精确动作周期、通用收益与出口探针；按周期族和回合记账的有限观察与成长预算 |
| `CombatBeamSolver.CycleRegionRetention.cs` | 合并同回合、同控制形状的动作排列；对最终存活候选事务式提交区域保留预算与进展证据 |
| `CombatBeamSolver.OrderedMutationRetention.cs` | 有序操作碰撞的谱系、租约、成对激活和预算账本；统一处理续接、到期与普通通道回退 |
| `CombatBeamSolver.FinalPlanOrdering.cs` | 终局胜负、偷窃、战损、药水、卖血和搜索边界排序 |
| `CombatBeamSolver.StateEvaluation.cs` | 搜索快照、评分、威胁、stand-pat 和状态特征；手牌可达价值的纯背包计算委托 `ReachableHandValue` |
| `CombatBeamSolver.Terminal.cs` | 终局精确回放、逐回合结果、击杀与遗物标注 |
| `StrategicEffectModel.cs` | 把 Power 的实际触发语义投影为伤害、防伤、资源、牌访问和成长效果；不决定终局胜负 |

`SearchRunContext` 只活于一次 solver：计数器、性能指标、节流器、转置表、stand-pat/威胁/coverage/路由缓存和 `OwnedExpansionBatch` 容器池均在这里。每个 lane 最多保留两个已清空 storage，单容器容量上限 4096；批次 lease 独立且 Dispose 幂等，检查点丢弃空闲池，不池化 simulator/model。根配置留在 solver，不把可变运行状态退回入口文件。 `SnapshotListBuffer<PredictedCard>` 也归各自 `_run` 所有，只缓存一个已清空、实际容量不超过 4096 的临时列表；快照内用栈上 lease，嵌套租用取独立 storage，generation 防止旧 lease 触碰新租户。牌序与 Shuffle RNG 克隆照旧，列表不得逃出 Snapshot，worker 排空后的缓存检查点丢弃空闲 storage。

`PotionStrategicCostLookup` 同样归单次 `SearchRunContext` 所有，中间保路与终局排序共用规范药水 ID/可再生条件对应的只读代价值；未命中仍调用原目录的 `Single` 查询，保留缺失/重复 ID 的失败行为。每个 worker 有独立表，不存药水实例或分支值，也不跨并发 solver 共享修改。快照内 Power 是否贡献战略估值只判定一次并暂存在当前调用的栈/数组中，需求收集与评分复用同一判定，不跨快照缓存。

`BeamRetentionPolicy` 的 `RoutingChoiceScratch` 只复用一张路由签名字典的空桶。每次 `RankBest` 新建 `RoutingChoiceNodes`，把候选有序列表和原五项代表放在同一组内；首次节点初始化代表，后续仍调用原比较规则。组不池化，归还 scratch 时清空节点引用；分组填充结束后，以原 `Max/Min` 一次性冻结组内最高 Beam 分、最高父分和最低父排名；只供该次 routing block 的族/选项/上下文排序使用，全部消费早于 `AssignRetentionRanks`。父节点排名变化后的下一次调用重新建组，不缓存单节点父链。族/选项顺序与配额照旧，`ROUTING_CHOICE_SUMMARIES scope=solver` 记录构建、复用和旁路。这些临时聚合不进入战斗状态键或续用戳。

循环调度另有三类不可重建账本，均由 `SearchRunContext` 持有并在内存检查点清理缓存后继续存活。`CycleFamily` 用回合、最小动作周期与规范动作序列识别同族，兄弟分支在相同动作深度共享已支付的观察工作，出口票据只展开一次；严格进展最多获得四级扩展，单族保留深度最多 `128`、出口探针展开最多 `256`，单个出口最多继续 `32` 个动作和两次回合转移。`CycleRegion` 不含精确动作排列，只按回合与控制形状合并组合爆炸；每区域普通保留为 `64–256`、探针保留为 `64–128`，同一回合还共享普通最多 `512`、探针最多 `256` 的总额度。进展可以扩展有限额度，不能通过制造新排列或新形状重置已消耗工作。区域进展续接仅以 `WeakReference<SearchNode>` 记录应匹配的直接父节点身份，候选自身强持有 `Parent`；每次更新新建且不再改写弱引用句柄，暂存账本与已提交账本不会互相修改目标，也不会由长期账本额外强持有旧节点链。这是所有权边界，不代表已实测的 GC 节约。

有序操作在无序结果相同但操作顺序不同时形成 `OrderedMutation` 租约。派生通道沿用碰撞根和初始通道身份：全 solver 最多 `2,048` 次有序保护准入、每层共享最多 `48`、每根基础 `128`、每初始通道基础 `64`、每派生租约 `16`；已有通道取得严格进展后，根和初始通道可分别使用一次 `64` 与 `32` 的有限尾部额度。不同碰撞根不再共享一个固定的“根个数”门槛，仍受实际保留工作总额约束。冷启动的两种顺序必须成对提交，失败不留下单边扣账；普通排名选中不等于已支付有序保护，自然入选的继承租约与额外候选进入同一个 `48` 额度服务队列，不能提前耗尽整层或提前到期。已有付费准入的同节点别名不重复占用服务；原有通用请求 `32`、成组服务 `16` 的保障份额和各原因预算不变，空余份额仍可按原规则借用。预算到期只取消调度特权，普通路线仍可参与后续保留。独立通道先选定，再结算有序操作，最后由区域事务按最终存活候选提交预算；被后续裁决淘汰的候选不能赚取进展或占用已提交额度。

普通搜索按进程可用逻辑处理器数量选择初始展开 lane：至少 16 个时默认 DOP8，4–15 个时默认 DOP4，2–3 个时默认 DOP2，只有 1 个时使用 DOP1；用户显式设置始终优先。设置中的“关闭（单线程）”映射 DOP1，数值项为 `2..16`，实际值还会按进程可用逻辑处理器钳制。DOP>1 时，同一组最多 DOP 个低优先级后台 lane 消费已准入父节点的作业，coordinator 只归并和提交。自然单父节点也使用这套调度器，没有嵌套线程池或另一套 action wave。lane 在一次 `Solve` 内复用 solver、缓存和 `SearchWorkPacer`；详细诊断和增量严格回放强制 DOP1。

父节点外层 wave 不按手牌数强制拆成 singleton。系统余量受限时从最多 2 个父节点开始，其他情况从最多 `2×DOP` 个已预约父节点开始；已完成的 multi-parent wave 未超出预约时容量倍增，超出预约时容量减半，在 `2..2×DOP` 内动态调整，singleton 不会替尚未观测的宽 wave 提前放大容量。Runtime 把玩家配置视为区域上限，并按 CLR 高内存阈值的 `95%` 安全线动态缩小实际 NoGC 申请；安全准入同时使用本轮分配余量和“区域建立时系统内存负载 + 本轮分配”的预测余量。全搜索已观测的最坏父节点分配量另加 `1.5×` 余量，并为 wave 中每个并发父节点完整预约。`SearchWaveMemoryPolicy` 统一拥有 `2×DOP` 预约上限与精确饱和倍增算术，先按实际剩余容量缩小 wave（包括 3、1 个父节点）；只有单个 parent 也不能预约时，才在已提交边界释放可重建缓存并回收。出牌深度结束先完成剪枝，清空被剪节点容器且仍有下一次准入时再检查回收；连单个父节点都无法放入预约时退回纯串行，不借 inner replay 冒险。CLR 仅因区域尺寸不受支持而拒绝 NoGC 时，Runtime 逐次减半申请，最低尝试 `512 MB`；平台不支持或区域尺寸仍无法建立时回退常规 GC，并继续按用户配置的 DOP 搜索。只有系统余量不足时才启用最多两个 lane 的保守并发限制。用户主动关闭 NoGC 时同样不施加该限制。每个已准入父节点的整体预约覆盖其全部动作/选择/药水结果及在途 seed；作业只在这个固定窗口内派发，内部不新建内存检查点。连单个 parent 都不能预约时仍走既有纯串行分支。

准备作业先冻结父节点的卡牌 action/target 与药水/target 表。各父节点轮流派发，已完成的 PendingChoice probe 优先作为独立选择链作业续接；药水完整选择链也作为独立作业运行，避免绑在回合尾部串行等待。首层回放每份作业合并 `clamp(N/DOP, 1, 4)` 次、末份截短，N≥DOP 的 singleton 仍至少有 DOP 份可派发作业，减少细碎结果反复进出邮箱。每个父节点有自己的窄 Fork gate，worker 在 gate 内串行生成 seed，离开后独占自己的分支模拟器；不同父节点不共享这个 gate。卡牌和药水在各自原序数组中归并，只有该父节点全部卡牌/选择/药水完成后，尾部作业才独占 aggregate 执行 EndTurn 并发布 stand-pat 基线。最终 TT、dominance、fallback 与接受顺序仍由 coordinator 按父节点连续前缀提交。

直接 EndTurn 的 `BuildEndTurnBranches` 枚举独占 `RoundPrefixReplayContext`，其嵌套与逐实例分支只借用同一上下文。前面回合阶段已完成且游标确为空时，`ForkCompletedRoundPrefix` 仅临时分离该空游标，仍调用严格 Fork 并在 finally 恢复；其他未完成事务不得复制。检查点通过同一 Fork 合同拥有模型、历史、RNG 与死亡集合，附带洗牌数、回合索引和额外回合标记；恢复调用唯一 `AdvancePlayerTurnStart`，随后进入原结算与快照。当前只为已知玩家开始选择机制建立检查点，不扩大选择或预算。捕获/恢复是物理计数，与原逻辑 Fork/转移分开。上下文不保存在发布快照、父链或 worker 全局中，枚举完成、失败、取消和提前 Dispose 均清除冻结图。

同一动作内的动态选择配额和物理实例补充收集器由一个续接作业独占，前一分支的未用额度仍返还给后一分支。直接首层有 N 个非空语义选择，且原最终候选额度 F 与回放额度 R 均至少为 N、N 至少为 2 时，原分支租约 `ceil(F/N), ceil(R/N)` 即使耗尽也至少给后续 N−1 个兄弟各留下一个名额。因此 `PrimaryChoiceReplayFrontier` 只提前派发每个兄弟必经的第一次回放，不增加物理回放次数。快照先由完成结果持有，再交给 frontier；全部首层作业完成后，唯一续接作业在原遍历位置取走快照并扣原逻辑额度。嵌套回放、失败分支的剩余额度返还、物理实例补充与最终候选枚举保持原序；不满足保证条件或首层之前已有挂起选择时走原完整选择链。没有按完成次序竞争共享额度，也没有改变 512 次 replay 上限、候选规则或身份补充分配。

并行搜索失败提示保留本次请求的 DOP；DOP 大于 1 时先引导上传问题包，再建议切换为“关闭（单线程）”。coordinator 消费完成邮箱、归并该 worker 的指标后才复用 lane；probe 和 raw batch 持有独立 lease。提交前完整保留已预约父节点和所有在途作业的所有权，异常停止派发，释放 dispatch sentinel 并等待全部 lane 完成，再释放未移交的 probe/batch/root。`OwnedExpansionBatch.TransferPotionTo` 与卡牌移交使用同样的先接纳、后移出规则，部分失败仍由原租约负责；旧 Dispose 不触碰后续租户。等待提交的父窗口最多 `2×DOP`，同时执行的作业最多 DOP；这是数量界和高水位预约，不是固定字节界。

保路中的待命评估只预先收集原规则会访问、尚未命中 `StandPatCache` 的状态键，保留首次出现的原代表；不合并额外候选或改变窗口。至少两个待评估状态时，`StandPatJobs` 复用当前 executor 的固定 lane，worker 执行完整 EndTurn 回放，释放临时快照后只交出 `StandPatEvaluation` 标量。coordinator 归并 worker 指标后复用 lane，全部成功后按原序写缓存并执行原选择器。DOP1 沿用逐项路径。Prune 持有候选根，批内不准入新父节点或触发 GC checkpoint；取消/异常先排空所有 lane，再传播原错误。executor 的活动引用属于 `SearchRunContext`，Dispose 清除，避免运行上下文延长 lane 的生命周期。

剪枝的路由签名、完成分组的排名摘要、逐上下文 Pareto 与有序变异 continuation 包可以在已排空的固定 lane 上计算。`RetentionJobs` 只调度本次输入中的索引，不展开模拟或预约新父节点；节点、父排名、已选集合和 lease 账本在整批完成前只读，各作业只写自己的结果槽位或独占组。coordinator 保持字典插入、拼接及观察请求的原顺序，摘要计数也统一归并；DOP1 和小集合走串行路径。成功、取消和失败都先等待全部已派发作业，完整计入后台分配后才使用结果或传播原异常。它与 `StandPatJobs` 顺序复用同一 executor，不允许同剪枝推测展开重叠或另开线程池。

路线预览继续保存并还原候选排名，刷新间隔复用 `SolverWeights.ProgressUiIntervalMilliseconds`（200ms）；强制发布和最终结果不受节流影响。

`parallel_waves / work_items` 记录已准入父 wave/parent，`parallel_action_*` 记录卡牌探测作业，`parallel_round_choice_*` 合计选择准备、首层回放、续接与完整链回退作业；不能解释为所有选择叶子并行。药水准备与首层回放的数量分别见 `SEARCH_PARALLEL_WORK kind=Potion/PrimaryReplay`，后者还记录实际 `max_concurrency`。`deferred_round_choice_*` 是命中层的调度诊断，改变调度后允许变化。`SEARCH_PARALLEL_WORK kind=StandPat/StandPatWave` 分别记录待命探针作业及其批次跨度，不增加已准入父节点计数。`SEARCH_PARALLEL_WORK kind=RoutingSignature/RoutingSummary/RoutingPareto/ContinuationPacket` 记录纯保路作业，`RetentionWave` 记录整批跨度，不增加准入父节点数。`ParallelExpansionWorkProfile` 在 coordinator 归并每个作业的 Stopwatch 经过时间，输出计数、总量、对数桶 p50/p95 上界和精确最大值；Parent 是准备派发至尾部接收的跨度，包含排队，Wave 包含等待与提交，Wait 是 coordinator 等待结果的时间。它们均不是 CPU 时间、也不能相互相加；实际用核来自线程调度运行时间或 on-CPU 采样。节点预算截断仍由 coordinator 释放未展开父节点及不会进入下一层的候选，`node_limit_snapshots_released` 记录实际释放数。

`BeamRetentionPolicy` 决定哪些中间候选继续活着；动作选牌、嵌套选牌和 `EndTurn.TurnStartChoices` 都以来源、效果、卡牌语义状态和上下文形成保路签名。`FinalPlanOrdering` 决定完整候选中最终采用哪条；完整胜利后比较扣除实际成长额度的战略战损，再比较已实现成长额度、收益次数和结束回合，药水、其他长线资源、敌方状态和分数作为后续尾键。两者不能合并成单一“总分排序”。`SearchFeatures` 是终局排序读取节点状态的只读投影。转置状态键中的九条战斗 RNG 必须包含完整内部状态；相同调用计数不能证明两个 RNG 后续等价。

`GrowthPolicy.cs` 定义八类局外收益的不可变额度/次数向量，另带 `GrowthExtras` 承载第三方来源那一半（按 id 序数升序、不存 0 值，空表为 `null`，登记表为空时行为与指纹与开这个口子之前逐位相同）。`GrowthSourceMirrors.cs` 是第三方登记表，只持有 id、延迟取牌函数与牌组判据；额度按 id 持久化，认不出的 id 原样保留并写回。Runtime 在主线程冻结额度及当前牌组是否存在成长目标，交给 `SearchPolicySnapshot`；求解器在快照中计算额度，最终排序、阶段仲裁与 Pareto 保留共同消费。`SimulatedCombatState.LongTermResources` 只记录既有结算点产生的成功收益次数，Fork 按值复制，状态指纹保留各来源计数；额度属于搜索政策。计数从每个新根的零值开始，预测分支跨回合保留；续用仍核对原来的金币、最大生命和卡牌永久变量，计数本身不进入 live `ContinuationStamp`。有目标或非零配置时停用纯 HP 的提前终止和 incumbent 下界，沿用原时间/节点预算。

「不考虑局外收益」开关由 SearchPolicySnapshot 的 EffectiveGrowthBudgets / EffectiveHasGrowthTargets 统一解释；开启后所有原版和第三方额度行置灰，原始配置保留。

跨回合例外保路以各真实“直接 `EndTurn`”分支形成的 stand-pat Pareto 质量集为相对基准，不以绝对零进展或合成的逐坐标基线判定。候选一旦在通用质量向量上离开被 stand-pat 支配的区域，就退出例外探针；观察期、探针和保留数都有固定硬上限，且该上限不能被中途普通进展重置，从而让延迟收益有界探测、真正停滞不无限续期。

卡牌候选在进入 Beam 前按即时防御、即时输出、资源循环、持续成长、控制、目标移除和生命投资建立有上限的组合覆盖，剩余名额继续按主分数填充。多次弃牌选择按整张牌而非单次弹窗共用分支预算，并保留弃牌触发、状态/诅咒清理、保留牌与牌堆取舍代表。持续 Power 的中间价值来自 `StrategicEffectModel` 对可达触发次数和当前威胁的投影；同回合减费/过牌组合另以当前资源可打出的手牌价值和零费可执行牌数保留一个战术启动代表。用药分支按已用数量和具体药水身份分别保留有上限的代表。这些投影只参与展开与保路，不进入战斗状态键，也不替代最终实际战损。

### 3.3 分支战斗状态

`RootCombatCardGenerationPoolSnapshot` 在主线程冻结原生规范角色的完整可生成池，攻击池从同一有序规范候选投影；与无色池一同由根持有，Fork 只共享只读数组。`ICombatPredictionCardGenerationPoolSnapshot` 向引擎提供完整角色池读取，`TurnStartPowerSupport` 的 CallOfTheVoid 消费该入口；完整洗牌和新卡实例仍属于分支。身份、原生程序集、不可变模型、牌池数组身份和人数约束均须匹配；未知／自定义源保留旧模型路径，不因此进入紧凑后端。

`SimulatedCombatState*.cs` 把内嵌引擎状态适配为搜索所需的战斗领域视图：

- `Fork.cs`：统一稳定边界和对象图复制；
- `MonsterAi.cs` / `MonsterState.cs`：分支行动、私有 AI、已知怪物静态值；
- `DeathLifecycle.cs`：死亡、复活与阵容事务；
- `ActionChoices.cs` / `TurnStartChoices.cs` / `AutoPlay.cs`：嵌套选择与自动出牌；
- `CardLifecycle.cs` / `CardPowerHistory.cs` / `PowerLifecycle.cs`：卡牌和 Power 跨事件状态；
- `CardEventHistory.cs` 的历史查询缓存按回合／阵营／玩家回合整体失效，独立于参与者 Power 字段；续用三个计数在新窗口显式归零。根条目匹配使用分支玩家回合号，不调用会读取 live 玩家状态的原生匹配方法；完整回合与冷根原生证据见[历史窗口报告](performance/simulation-rounds-20260911.md)。
- 凡庸在 `ShouldPlayMirrors` 使用同一分支手牌/开始次数入口约束手动与自动打牌。`_cardPlayStartsThisTurn` 包含重复播放和仍在执行的外层卡牌，根来自 CardPlaysStarted，随 Fork 复制、回合开始清零，进入 fingerprint 和 `CardEventHistory` 的 live/predicted 续用文本；不能以完成次数或手动系列数代替。
- `Relics.cs`、`PowerRelics.cs`、`ReactiveRelics.cs` 等：遗物与组合事务；
- `Potions.cs`：药水槽和使用状态。

活动 roster 只决定当前可行动、可选目标和 listener。已经捕获的怪物 AI/静态参数属于已知怪物和分支生命周期，不能在移出活动 roster 时提前删除。

`SimulatedCombatState` 将基础监听快照分为阵容/遗物/药水/根 Power 前段和球/卡牌/原生附着效果后段。卡牌或球变化只重建后段；阵容或药水变化清空基础前段。有效/活动 Power 只重写前段，沿用原完整顺序；新增 Power 找不到前段所有者锚点时回退到完整列表，保留卡牌锚点或列表尾部插入。不透明 CardModifier 根始终走完整路径，其附着监听追加器仍收到前段在内的完整列表。成功分段后的有效/活动前段及 Power 投影也可跨卡牌/球变化保留；Power 变更与基础前段变化仍完整清空这些派生缓存。无前段锚点和不透明来源不保留该投影。发布的各段不可变，拼接视图长度一次冻结；Fork 通过同一 `PredictionForkContext` 重映射接收者，并复用相同后段。`HOOK_LISTENER_SEGMENTS scope=root_cumulative` 记录基础及有效前段构建/复用与分段/完整构建，主搜与恢复数不可相加。

`EffectivePowers` 保留已知敌人尚待完成死亡结算的能力；普通 `ICombatState` / `ICombatPredictionHookListenerSource` 回调使用活动监听视图，排除所有者已离场的 Power。两种视图共用既有根与分支能力实例，活动视图随阵容和能力缓存失效，不清空死亡补偿所需的数据。

## 4. 内嵌模拟引擎

`SimulatedCombatState.Apply<T>` 共用 `PreparePowerApplication` 与 `ApplyPreparedPower`：前者保持修正与 Artifact 拦截，后者独占模型获得、数量／顺序和变更记录。临时力量入口在首次写入计数之前施加 Strength，随后按原版请求偏移与当前数量条件处理回调；叠加封顶仍传递原偏移。普通 Power 不走临时力量回调，不重复准备。
`SimPlayerCombatState.Phase` 在主线程根捕获，Fork 按值复制，阶段推进写入分支状态并进入搜索状态键。它决定 UnceasingTop 的触发窗口；续用只在稳定 Play 阶段比较，最小跨回合夹具另显式核对原生阶段。结束回合按 AutoPostPlay、BeforeSideTurnEnd、球被动、手牌回合末效果的顺序推进。

`PredictionUtils.CloneModelForSimulation` 对卡牌在 DeepCloneFields 前清除 CardModel 事件委托；原版克隆阶段会重新附着附魔并发出事件，不能让这些事件调用源卡的 UI 订阅者。深拷贝和 AfterCloned 仍使用原版实现。

### 4.1 基础层

`ValueRng.TakeDistinctIndices` 在调用者独占的 Span 中按完整池大小执行原生整池洗牌，返回值 RNG 和选中前缀长度；不持有 Model、不分配牌堆、不保存跨分支 scratch。零／负请求仍完成整池洗牌，空／单元素池不推进随机流。当前仅为紧凑随机生成原语；将它接入生成命令时，必须把 CombatCardGeneration 的全部五字段纳入可撤销值状态，并按完整冻结池映射定义，不得自行缩池。

`src/Engine/InCombat/Simulation/` 负责通用战斗命令时序、伤害、牌堆、历史、RNG、球和 Fork。它不包含单张卡、单个 Power 或具体怪物的搜索策略。历史卡牌 Started/Finished 与 DamageReceived 的卡牌来源使用不可变卡牌快照；当前动作是否开始以精确 trace-frame 身份判定，保留原生 `CardPlay` 身份，不以 Original 卡牌身份合并兄弟分支。`CombatPredictionHistory` 以不可变 prefix segment + 分支本地 mutable tail 保存事件；动作后缀消费者必须使用冻结上界的 `EntriesFrom/EntriesBetween`，不能先遍历完整 prefix 再 `Skip`，否则长线会把一次局部查询放大为随深度增长的重复工作。

`src/Engine/Common/` 提供 `PredictedCard`、`PredictionForkContext`、`PredictionStateStore` 和通用模型克隆。StateStore 直接持有可 Fork 的 state，空字典按需创建；仍在同一 context 中按原跨类型顺序 eager Fork，不能对调用者已借出的可变引用使用通用延迟 COW。一次 Fork 内的所有结构必须共享同一个 context；分支可变对象必须显式重映射。`BaseLibCloneConcurrency` 是原版与预测克隆共用的外部扩展并发边界，只包围模型深克隆阶段。

`MirroredHookListenerFilter` 为 `HookMirrors` 和原生关键字空操作判定提供静态回调位图；原生/领域监听序列完整保留。只有确认没有 `TryModifyKeywordsInCombat` 参与者时，关键字查询才直接读取本地集合。根捕获重新检查相关 AbstractModel 基方法及原生 `Hook.ModifyKeywordsInCombat` 的 Harmony 补丁，有补丁时旁路；第三方/动态类型全部保留，BaseLib 不透明 CardModifier 根也旁路。`SimulatedCombatState` 的分支监听视图沿既有失效边界清空。不可变布局只含 Type/位图：优先复用分支旧布局，失配后查询同根有界共享表，哈希只选槽，完整类型顺序相同才复用；碰撞、并发覆盖和超长列表都不能误认序列。共享表不持有任何 Model，原接收者仍来自当前分支快照；它随根回收，不进入状态键或 ContinuationStamp。`HOOK_LAYOUT_CACHE scope=root_cumulative` 记录共享查询命中、未命中、碰撞和旁路，主搜/恢复日志不能相加。合同覆盖类型顺序、重复项、跨分支接收者、哈希碰撞、并发读取、关键字原生对照及根间补丁刷新。

`CombatPredictionSimulator.TerminalStamp` 在与原版对应的完整动作/阶段安全检查点首次锁定胜负及影子玩家回合号，按值 Fork；`IsEnding` 仍是无副作用查询，不在单个 Hook 监听器之间提前终止正在结算的序列。`SimulationSnapshot` 独立保留此值，释放模拟器后，终局标注、临时结果、最终排序和已知胜利上界仍读取同一时点。`PlanAction.Turn` 只表示发起动作的回合，不能代表该动作跨回合结算后的终局回合。

`Simulation/Compact/` 已接入搜索候选，Runtime 由 `SearchBackendPolicy` 在主线程对完整根准入后选择，未迁移域使用旧模型后端。`ReversibleValueState` 独占连续值槽、撤销日志、单次 LIFO 检查点及派生页缓存；普通写入和 rollback 都将对应页失效。`ReversibleValueState.FrozenValues` 独占发布时复制的页表，共享只含位图和非零值的私有不可变 64 槽页，不持有 worker、祖先候选或旧模型。恢复要求同根且目标没有活动 checkpoint；只重写失效或不同的页，清除旧候选遗留的零槽，目标容量足够时复用恢复不分配，容量不足时由 lane 扩容。追加槽位属于当前分支；检查点保存逻辑长度，撤销删除后缀并清零，后续分支复用索引不能看见旧值。候选只复制实际使用范围的页表，恢复可跨同根不同逻辑长度，工作区余量不进入候选。事件带通过 `ReversibleValueBuffer` 索引按需分配的块：64 值叶与 32 路索引的长度、树根、高度和尾叶指针都在工作区，允许其他领域状态在事件之间追加。事件用两个值保留完整 32 位实例／目标／金额，避免生成实例编号被截断；布局不持有分支缓存。页表及恢复扫描仍随实际槽数量增长；该接口只负责值存储；生成实例由程序分配，通用跨回合语义仍未迁移。`ResumableDiscardProgram` 将牌堆、资源、显式执行帧、选择和事件游标全部写入同一值槽，并共享不可变卡牌定义。卡牌定义与实例分离，生成实例的定义／捕获 X 和六个有序牌堆使用可增长缓冲区；根实例的定义固定，模板在根捕获。生成事件保留逐实例顺序与满手转入弃牌堆的结果，生成后的洗牌按定义比较；小刀出牌次数通过原指纹字段读取。完成读取器拥有按实例／定义复用的模型池，池不进入候选。`CardEffectProgram` 私有复制有序指令数组，Prediction/Compact 将已准入原版卡牌编译为效果序列；每帧的指令索引与抽牌进度属于同一值槽。选择／自动牌结束后继续当前或下一条指令，不重放父牌前缀；生存者增加格挡后弃牌。候选支持新建工作区或 `RestoreInto` 复用已有 lane。诊断写入/事件计数仍累计在各 lane，不属于恢复的战斗状态。内核不引用原生 Model、Simulator、Task 或委托。当前扩展到抽弃牌、自动出牌、防御／后空翻格挡、洗牌、战略选牌与两个遗物的格挡触发；Shuffle 的五字段与计数进入相同撤销槽，比较矩阵保留原版同名卡排序关系。允许洗牌时最多一个 Sly 实例；容量或未支持效果明确失败。`CreatureAttackLayout` 另提供主要敌人基础 Power 域的打击、格挡／生命伤害、离场和永久死亡槽位，执行帧与事件同时保存目标。最后一击遵守原出牌区结束门，终局在完整动作后的显式安全检查点锁定；恢复／撤销包含死亡与终局。`BasicPowerLayout` 将力量、敏捷、虚弱、易伤、脆弱、中毒的数量、施加者、获得顺序与根槽退休标记纳入同一工作区；中和可创建／叠加敌方虚弱，死亡清理所属 Power，伤害与格挡保留 decimal 修正直到原标量边界。卡牌定义现明确费用形式和结果位置；付款 X、逐帧资源值和移除集合属于同一工作区，结果移动事件区分弃牌／消耗／移除。带符号的基础 Power 指令使用捕获 X；新增能力牌移除与 X 消耗流程。群体施加按稳定阵容先完成当前指令的全部目标，再执行下一指令；已离场目标由命令门排除。抽牌返回的第一张实例保存在各帧独立值槽，条件分支只判断实际抽到的牌，不把洗牌检索当抽牌；空返回直接跳过对应效果。卡牌类型与不可变虚无标记属于定义，完成事件保留原虚无历史。中毒主动触发现进入同一伤害／死亡值流程，事件保留无攻击者／无卡牌来源、无属性修正与穿透标记；存活后递减，零层退休，重新获得保留新顺序。整手弃抽捕获原手牌与数量，逐牌弃牌 Hook 后抽牌，抽牌完成后才处理捕获的 Sly 列表；洗牌返回位置随帧保存，不能重做弃牌。目标 Power 条件与存活敌人 Power 总量在执行指令时读取值槽；基础值与额外倍率由编译器捕获，求和结果再经过敏捷／脆弱和格挡取整，不能把根预览值当作分支结果。玩家下回合格挡与必备工具计数现在也使用 Power 值槽，按种类仅增加玩家槽；格挡→Power 指令传递修正后的返回值，准入按可达敏捷区间排除正小数生成零层实例的未表示语义。敌方临时力量计数使用独立 Power 槽，尖啸先执行首次 Strength 再加入计数，叠加按请求偏移处理；敌人死亡清除计数与力量，退休标记随撤销恢复。复杂 Power 和复杂死亡仍未迁移；完整回合仅限下述已准入单敌闭包，不能将其称为通用新后端。

`Prediction/Compact/CompactDiscardProjection.cs` 负责整根封闭能力准入及旧模型事件投影，`Prediction/Compact/CompactCardProgramCompiler.cs` 负责精确卡牌状态准入及不可变指令编译；当前共三十种精确卡牌类型，包含单体／群体中毒、主动中毒触发、整手弃抽、条件抽牌及防御后虚弱。未镜像 OnPlay 的补偿在原方法作用域退出后投影，间接伤害来源不能伪造为 OnPlay 内的卡牌攻击。投影仅用于完整状态／历史与原生差分，不再次执行 OnPlay、弃牌 Hook 或选择器。`Prediction/Compact/CompactDiscardReadView.cs` 在同一准入闭包内直接读取值牌堆、资源、Shuffle RNG 和已提交事件；根卡牌及其他生命周期只作已证明不变的元数据。每读取器独占一个根副本供旧公式的可变 scratch 使用，另一个初始化副本取得会惰性物化的历史初值，保留读取根的缺席／零值区别；两个副本的成本计入初始化，每叶不再 Fork 或物化旧图。风险来源由原 registry 区分未镜像与不完整镜像，经 `PredictionCoverage` 原分类／排序规范化；每读取器只缓存实际出现的组合，不预建全部子集，支持根内最多 64 个独立来源。`Prediction/Compact/CompactCardMetadataReadBinding.cs` 在读取器初始化时取得独占预览，逐叶只单向导入 X 值和移除标志、失效相关缓存；旧模型不是第二份执行权威。`CardHistoryReadValues.Exhausts` 提供当前消耗历史；卡牌集合／元数据变化时旁路卡牌和策略摘要缓存。`SimulatedCombatState.CompletedPowerReads.cs` 提供每读取器私有的 `CompletedPowerReadBinding`：初始化克隆原实例，并为非零根 Power 准备独立的规范新实例；根据撤销状态的根槽退休标记选择读取模型，使重获后的回合初始数量、跳过持续计时标记、额外 Target 与动态变量恢复原生默认值，逆向恢复仍使用原模型。两组模型仅在初始化克隆并恢复原归属；每次读取仅单向替换提供的 Power 数量／施加者、回合初始量／跳过递减标志、退休集合、获得序列和阵容，失效监听器缓存，由既有 owner-anchor 算法得出有效顺序。它不执行命令、Hook、随机数或数量通知，不回写值程序，也不逐叶克隆。

`CompactRoundLayout` 保存回合、玩家回合、当前阵营、首次终局回合和卡牌清理标志；`ResumableDiscardProgram.Rounds` 仅在完整阶段、确定性单敌 AI 及排序均准入后推进回合。手牌 flush 跳过弃牌 Hook，保留关键词与临时 Sly 分别捕获；起手抽牌／洗牌／Tools／嵌套 Sly 共用可暂停帧，抽牌事件标记是否属于起手。`SimulatedCombatState.CompletedRoundReads` 每个 lane 准备可复用的历史映射，逐叶从根形状及阶段标记导入时钟和重置；`BeginSideTurn` 在这里仅复用历史记账，不调用效果或 Hook。执行候选不保留旧读取器。手牌末尾完成后提交 `CommitPlayerTurnHistory` 值事件，再检查终局；本回合与上回合最后攻击牌由不同读取映射保存，沿用原键编码。`Testing/CompactPlanReplay` 使用正式计划的实例键、出现序号和选择顺序驱动工作区，每个桥接器只准备一次私有卡牌元数据。该 Testing 准入目前拒绝玩家中毒、待抽牌与额外行动；原始机甲完整路线及各前缀的已准入替代分支已有[原生对照](performance/simulation-full-route-20260911.md)，亡灵闭包与正式搜索后端仍需迁移。

`DeterministicMonsterAi` 私有复制整根确定性图、当前招式和日志；`DeterministicMonsterAiLayout` 将当前索引与可增长日志写入同一撤销状态。Testing 仅准入精确单体 `MechaKnight` 四节点图，AI 独立要求已准入命令体；选择边界只推进后继，不再次执行招式。`SimulatedCombatState.CompletedMonsterAiReads` 从已捕获根准备每个招式的旧记录和 lane 私有日志，`CompactMonsterAiReadBinding` 逐叶导入当前值及意图；它不运行 AI、不从 live 补捕获、不进入候选，也不逐叶创建旧记录。原生行动条目目前没有游戏语义消费者且不属于旧预测历史合同，测试单独核对其实际招式与目标；不为凑条目数改变 Snapshot 计数。完整回合时间与阶段由下述独立准入驱动承担。

`ResumableDiscardProgram.PowerPhases` 独占已准入 Power 阶段体：记录参与者的回合初始量、清除格挡并兑现下回合格挡、敌方临时力量恢复及虚弱／易伤／脆弱递减。`BasicPowerLayout` 在现有第四槽的高 32 位保存有符号初始量，低位分别保存退休／跳过标志；新建重置初始量并按玩家持续减益规则设置跳过。根上的`StratagemPower`也捕获为值槽，使其回合初始字段不再依赖旧投影；没有创建／修改`StratagemPower`的指令。Testing 独立审计回合末及 Late／格挡清除 Hook，准入标志随冻结候选保留。该接口不改变侧别、回合号、AI、阶段历史或起手，不等同于完整回合；[原生阶段证据](performance/simulation-power-phases-20260911.md)。

`BasicPowerLayout` 还捕获既有人工制品实例，数量、施加者、获得顺序及退休随值状态恢复；没有创建指令时不额外预留空实例。`PreparePower` 在零值／结束／死亡门之后处理负面施加的阻止与消耗，`CommitPower` 写入准备后的数量；临时力量先准备外层计数，再执行首次内部力量和单次计数提交。具体[原生证据](performance/simulation-compact-artifact-20260911.md)。

`RandomDrawCost` 只保存复制的根修饰前缀，`RandomDrawCostLayout` 只保存不可变值位置；按卡牌增长的完整修饰列表与费用 RNG 五字段均在撤销状态。`ValueRng` 为各流共用纯值算法，流状态独立；`Slither` 真正抽入手牌时追加本场绝对费用，付款读取当前列表末项。读取器单向导入列表并复用私有修饰对象，`CompletedStateReadView.EnergyCostRng` 进入原键的原字段位置；没有费用效果时继续使用根值。详见[原生与成本证据](performance/simulation-random-costs-20260911.md)。

生成附魔的编译折叠仅用于已核对的 `BLADE_OF_INK`／正常 1 层 `Inky` 小刀：全部生成监听器无中间观察者，附魔没有战斗历史或修改效果。最终定义仍在根捕获，新增监听器／修饰时重新证明；[原生证据](performance/simulation-inky-cards-20260911.md)。

`Search/CompletedStateReadView.cs` 是同步已完成状态的读取合同，既有 Snapshot 与 `SnapshotFromReadView` 共用 `SnapshotCore`、合法性、完整估值、投影洗牌与原键编码。`CombatBeamSolver.ReadView.cs` 持有共用的值牌堆编码与可选的根内不变特征缓存；敌人摘要、威胁焦点、卡牌估值与策略上下文仍调用原公式，只在准入证明敌人／AI、Power 与存活牌集合及元数据不变时复用。每个稳定根新建缓存，不保留跨回合或跨根条目；仅已准入的值读取入口启用对应缓存。`SimulatedCombatState.AppendFingerprint` 在原序列中替换已改变的 owner 历史项和技能集合；`CombatHistoryReadValues` 保存读取器派生的受伤集合、攻击命中对、上一张攻击与死亡阶段，编码仍由原方法负责。读取结果立即释放借用 Simulator；读取器不能逃入保留候选，也不建立旧图与值状态的双写权威。读取合同的 `EvaluationContext` 表示私有评估上下文，不再暗示其中 Power 始终停留在根值。所有未提供字段必须在准入程序内不变；因此不能将当前合同用于任意 Power 变化、复杂死亡或其他随机流写入。双端门禁只允许 `SearchBackendPolicy` 选择根及 `CompactReplay` 执行已准入值程序；阶段探针仍只属于 Testing。

生物标量现在由纯值 `CreatureVitals` 保存并统一实现扣格挡、扣血、治疗与最大生命限幅；生产 `SimCreatureState` 持有该值，负责稳定 Creature 身份、显示值和原 `DamageResult` 外壳。`CreatureValueSlots` 仅是不可变布局位置，向程序所属工作区读写 HP／MaxHp／Block／Present，生命归零不会自动移出阵容。它不包含伤害 Hook、攻击事务、历史或死亡回调。

完成状态合同另提供逐生物数值、有序敌方 roster 和显式可空的终局时点。完整快照的分布、集火、威胁与原键共用这些值；AI／沙漏计数的原键仍按活动 roster 原序编码。敌人值可变时禁用该根的敌人／焦点缓存，卡牌元数据复用仍要求自身闭包成立。`UnattendedTestRunner.CreatureValues` 用三敌 16 状态对照全部 Snapshot 属性／原键／排序，并以 12 个原生非致命伤害样例验证基础算术；这只是伤害执行迁移的基础，尚未迁移 Power／死亡历史、宠物生命周期及攻击命令。根内未迁移的 Hook／AI／领域公式也必须与已变化数值无关，不能仅凭 Power 数量未变就认定可借用根。

牌组根监听器的准入独立于战斗监听器：`CompactDiscardProjection` 按原版运行级派发检查不可变根前缀，任何覆盖均拒绝；战斗表仅允许精确已表示的类型／方法。只缓存 CLR 元数据，根值仍逐次检查。蛇之戒在当前玩家行动域不变，原始机甲 30 牌／31 监听器经过完整读取与原生差分；起手与后续回合没有因此迁移。

`ResumableDiscardProgram.HandEnd` 独占已准入手牌末尾的值执行，先处理无末尾效果的虚无牌，再按显式入场顺序处理状态牌伤害。是否准入此阶段属于不可变候选配置，未准入根调用前拒绝。入场顺序由调用者的根合同提供，不能在 worker 读取动画配置；真实 Normal／Instant 下的差异及当前生产默认路径限制见[阶段证据](performance/simulation-hand-end-20260911.md)。玩家死亡保留 roster、清理所属 Power，待失败与 Defeat 安全点分开。`CompletedStateReadView.CumulativePlayerHpLost` 进入原评分公式，`CardHistoryReadValues.StatusDraws` 进入原状态牌抽取字段；方法作用域事件保留 `OnTurnEndInHand` 来源，不增加出牌／攻击次数。该手牌阶段由 `ResumableDiscardProgram.Rounds` 组合为已准入完整回合；生产搜索和部署接线仍未完成。

`MonsterEffectProgram` 只持有复制的不可变指令，`ResumableDiscardProgram.Monsters` 复用值伤害、格挡、Power 和生成状态；负事件来源表示怪物，生成事件另存空／玩家创建者。Prediction/Compact 目前仅捕获单个机械骑士的四种指令体，伤害来自冻结怪物元数据；AI 选择及阶段顺序不由此程序承担。`CombatHistoryReadValues.CreatureAttacks` 提供完整生物攻击计数，原映射编码同时保留玩家单项替换与缺席／零值语义。读取器初始化取得历史基数，每叶仅累加事件；投影不能调用怪物效果或重新执行指令。见[指令体证据与完整回合边界](performance/simulation-monster-commands-20260911.md)。

持续减益的跳过标记由独占 Power 持有。`PowerLifecycleSupport.UsesNativeDurationSkip` 识别原生使用此字段的虚弱／易伤／脆弱；`ApplyPreparedPower` 只在新建玩家实例时设置，普通、怪物及按类型应用对这三类共用同一表示。其他旧阶段补偿的集合未扩展至这些原生字段。`GetPowerFingerprint` 与 `ContinuationStamp.AppendPowers` 共用有效标记分类，保留无标记及无关 Power 的原编码；[基线和原生生命周期证据](performance/simulation-duration-state-20260911.md)覆盖同层不同未来及被人工制品阻止后的状态。

`Testing/CompactPhaseProbe.cs` 与 `UnattendedTestRunner.CompactKernelProfile.cs` 独占原型的阶段计量和新旧 solver 缓存对照，不进入生产 Search/Runtime。直接调用 Snapshot 的实验必须进入正式 `SolveCore` 使用的 `SimulationNotificationIsolation`，否则既有第三方空能力快速路径会旁路。该作用域使用线程静态状态，必须在 await 前和原生部署前退出；恢复后的模拟重新进入。诊断输出实际线程 CPU、独立墙钟和分配，内部既有 Snapshot 指标仍是嵌套墙钟；冻结候选不保留计量器、solver 或读取视图。

通用命令和 Hook 调用遇到 `PendingChoice` 时立即向上传播未完成状态，不再执行其后的监听器、抽牌、资源变更、死亡处理或卡牌收尾。Search 为待处理选择补齐计划后，从稳定父节点精确重放该动作，按原顺序通过挂起点；未完成事务不作为可继续执行的稳定 Fork。自动出牌将外层来源与上下文身份带入 `OnPlayWrapper`，在来源牌仍位于 Play 时消费嵌套选择，等待嵌套自动出牌结束后才移动来源牌和执行费用清理。原版挂起位置、顺序与卡牌实例身份属于模拟语义，不能由 Beam 或部署层补偿。

`CombatPredictionSimulator.CardPile.cs` 的抽牌安全边界只约束当前同步调用栈：抽牌 Hook 再次自动出牌、自动出牌又抽牌时，嵌套深度最多 `100` 层，继续嵌套会明确失败，不返回部分抽牌结果。深度在 `finally` 中退出；普通动作结束后、跨回合或从稳定边界 Fork 后继续抽牌，都不因已经累计的抽牌历史而减少合法抽牌。历史记录不再承担整个分支生命周期的 `100` 次抽牌额度，正常长线与有效循环仍受 Search 的节点、时间和调度预算约束。

玩家死亡由通用伤害入口经 `ICombatPredictionEffectSink.CompletePlayerDeath` 通知 `SimulatedCombatState.DeathLifecycle`，后者复用 Power 退休／移除和死亡阶段的分支所有权。该通知发生在球与宠物清理前，不借用只遍历敌人的后续清扫。列表与单目标伤害入口都从分支读取施伤者存活状态，真实 Creature 仅作为身份。

### 4.2 Mirror

> 面向外部 Mod 作者的登记点总表、登记纪律与验收标准见
> [第三方 Mod 适配手册](THIRD_PARTY_ADAPTERS.md)。

`src/Engine/InCombat/Mirrors/` 精确实现原版 Hook、卡牌、药水、附魔和球方法。Facade 保持原版调用时序，registry 按运行时类型与方法分派。

补货在 `AfterDeathMirrors` 按原版 Hook 时点调用领域生成入口：旧个体仍在阵容中，替补生命判重读取分支最大生命和 Niche RNG；`DeathPowerSupport` 的后续清理保留死亡生命周期，生成替补由该镜像独占。

温柔在 `AfterCardPlayedMirrors` 中按每次真实分派更新既有分支计数并施加力量/敏捷损失。`TriggeredPowerSupport` 的历史扫描保留伤害补偿，温柔由镜像独占；外层出牌扫描包含内层自动牌历史时也只结算各自的 Hook。回合末恢复继续由 `EndTurnPowerSupport` 消费该计数。

苦无、手里剑和彩虹戒指的属性施加在各自 `AfterCardPlayed` 镜像内完成：在原版 `IsInProgress` 门内更新计数，按每次 `PowerCmd.Apply` 的 `IsEnding` 门决定是否施加，不能延到其他监听器之后。彩虹戒指的领域生命周期仅同步既有激活投影，不再施加属性；末击不会提前中断整组监听器。

`MethodMirrorRegistry` 同时实现 `IMethodMirrorRegistryDescriptorProvider`。`MethodMirrorRegistryDescriptor` 描述基础方法、receiver、显式 Handled/Ignored 注册和当前 inferrer；CoverageCatalog 只消费该描述符，不读取 registry 私有字段或 `MirrorMethodSpec` 内部布局。

## 5. Prediction 领域补偿

`src/Prediction/` 处理基础命令和单个 mirror 不能独立表达的领域语义：

谋杀的抽牌历史倍率由 `CalculatedVarSpecRegistry` 读取 `SimulatedCombatState.GetCardsDrawnBeforePrediction` 的冻结根计数与模拟器新增抽牌事件。根计数来自已有 `RootCombatHistorySnapshot.CardsDrawn`，随根不可变共享；实机完成回合准备或继续抽牌后，旧根和 Fork 仍使用捕获时的历史。

- 卡牌/Power/遗物/药水/球的跨 Hook 生命周期；
- 怪物行动图、随机分支、私有 AI 与召唤；
- 死亡、复活、自动出牌和嵌套选牌；
- 第三方 ModHook subscriber 的主线程捕获与分支重建；
- 覆盖分类和动态状态字段政策。

这里可以保存具体领域规则，但不能决定 Beam 配额、最终路线或 UI 显示。新增补偿前检查 mirror、spec、support 和 `SimulatedCombatState` 的完整调用链，确保只有一个权威结算点。

`PlayerTurnEndLifecycle.RunPhaseTwo` 拥有清空手牌后的玩家回合末补偿顺序：常规 Power、遗物、晚期 Power，最后规范化卡牌词条。Search、风险预估和无人差分共用此入口；每个阶段的挂起选择立即向上传播。`CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects` 仅承担常规 Power 阶段，晚期伤害在遗物之后结算。

DarkEmbrace 的延迟抽牌数由 AfterCardExhausted 镜像按实际虚无消耗事件写入 `DarkEmbracePredictionState`，根从原生内部计数捕获，StateStore/Fork 按值隔离并纳入指纹；常规 Power 回合末阶段抽牌后归零，稳定下一玩家回合不保留待抽事务。苍蓝星球的已触发标志由主线程从原生 Power 捕获至分支表，避免 Power 克隆重置内部数据后重复触发。

Nostalgia 的本回合攻击/技能开始数属于 `SimulatedCombatState`：冻结根历史初始化，开始事件递增，阵营回合开始归零，Fork COW，进入指纹及 ContinuationStamp 的 `Y` 第四项。HistoryCourse 的上一回合空值也属于物化根/分支状态，跨回合后不重新扫描实机历史。Nightmare 在选中时克隆选中牌并去除 affliction，后续原牌费用、升级和保留变化不修改该快照。ForegoneConclusion 的候选全选为原版隐式选择时，由 CardChoiceSpec 显式标记并保持来源顺序。

`Prediction/ModelPredictionStateMirrors` 拥有遗物／Modifier 的精确类型状态登记，首次根或续用捕获后冻结。
`SimulatedCombatState.MaterializeRoot` 在内置状态物化后调用捕获并释放实机源映射；状态放入现有
`PredictionStateStore`，随同一 Fork context 复制。模型克隆仅作只读身份，效果镜像通过登记入口的
`Get<TState>` 读写分支状态。`ModelPredictionStateWriter` 用同一组有序类型字段生成搜索指纹与
live/predicted continuation 文本，按所属位置绑定同类型实例。该层不拥有 Hook 时序、搜索政策或
Mod 准入，具体契约见[模型状态适配](third-party-model-state.md)。

有效 Power 的有序语义值直接进入搜索指纹，`ContinuationStamp` 的 `P` 字段按有效列表顺序输出，保留获得、移除和重新获得形成的 Hook 顺序；动态变量自身仍按无序键值集合比较。根捕获及分支监听表继续拥有顺序，指纹和续用只读取既有状态，不另设按阶段划分的顺序账本。

普通能力与多实例能力共用逐实例获得顺序表，Fork 通过同一 `PredictionForkContext.RequireRemap` 映射到子分支。重新获得已移除的普通能力时建立新实例，回合开始数量与内部状态由新实例初始化。新召唤友方归入敌方段之前，按原版友方/敌方顺序构造监听表。

## 6. UI

偷窃策略由 `TheftEncounterStrategy.CompareRecovery` 统一胜利/追回资源的排序前缀；`SolverInterimResult` 携带 TheftPolicy，展示与搜索中的候选比较按同一策略处理。保策略的终局、保路与药水审计将追回置于战损之前，纯 HP 早停要求资源已追回，HP incumbent 剪枝在保策略下停用。放走继续普通战损/药水政策。

状态摘要采用首行徽章/路线摘要/右侧详情，次行搜索上下文/耗时/统计的结构；`ShowResult` 使用已有 SummaryText 中的回合信息，不重复显示规划回合上下文。搜索中的上下文标签关闭内部换行，流式统计行负责整项换行；`SolverDetailsButton` 保留展开事件与箭头，使用轻量无背景样式。

常规设置按开始计算、出牌速度、自动执行暂停条件、幕末 Boss、显示与通知、在线统计分组；性能设置按搜索预算、搜索停止条件、内存管理及折叠自定义参数分组。`Controls.AddSettingsSection` 提供统一分组容器，输入仍使用原保存/重载事件，页面滚动沿用伸展布局。结果卡片位于状态摘要上方，以流式排列显示原快照的扣血、药水、失窃与回血；收起时迁移同一结果卡片，展开时恢复正文首位，避免重复结果状态。

`SolverActionBar` 独占底部动作行、自动模式行及收起布局，通过只读 `SolverActionBarState` 更新可见性。它不读取 Controller、搜索结果或战斗对象；Overlay 保留命令绑定、可用性判断和按钮文案。全局启停位于标题栏，偷窃策略位于路线摘要之后。全自动作为绿色主按钮固定在动作行首位，执行与采用为次级按钮；“自动开启全自动”偏好开关位于按钮行最右侧；下一行左侧为纯显示内存条，右侧为“强制释放内存”按钮。Overlay 沿用原释放流程，等待期间按钮显示进行状态并禁用重复触发。当前阶段只迁移布局所有权，未把 Runtime 操作能力重复实现为新的状态机。

`src/UI/SolverOverlaySnapshot.cs` 是结果或只读候选路线到显示数据的唯一转换边界。它在主线程复制状态、概览、详情、回合、动作标题、选牌文本、遗物标注、击杀、逐回合对敌伤害、tooltip 和视觉类别。

`src/UI/SolverText.cs` 与内嵌 `English.json` 负责中英显示文案：按游戏语言选取完整模板，再格式化插值，避免翻译玩家输入、模型名称或诊断内容。字典只加载一次；游戏名称仍在主线程从原版显示元数据获取。Search 禁止引用 SolverText，不接触语言或资源加载。简化版切换游戏语言后通过重启统一重建既有控件和路线快照。

击杀括号来源通过 `SolverDisplayNames.Capture` 在主线程捕获语言、能力/充能球标题以及稳定 ID 和类型名别名；worker 仅查询这份冻结名称表。`SolverOverlaySnapshot.CaptureAction` 独占胶囊及悬停文字，按执行顺序展示嵌套选择，并交给 UI 的 `SolverRelicEffectText` 格式化内置遗物记录的紧凑效果语法。第三方自定义摘要保持原文，日志中的原始摘要仍由 Search 输出；翻译不进入效果模拟和普通分支枚举。

卡牌热切换使用 snapshot 中独立的 `SolverActionTextIdentity`，只含稳定 ID、升级和显示摘要，不保留 PlanAction/Model 引用。`SolverUiModelNames` 在主线程投影时查询当前游戏译名并按语言缓存；`SolverLocaleRefresh` 合并语言事件，在控件存活期刷新标签并在退出树时解除登记。卡牌名不再以搜索时的格式化字符串为 UI 权威来源；搜索只携带显示升级标量，禁止引用上述 UI 服务。其余静态界面仍以重启作为完整刷新入口。

以下 renderer 只接受不可变 snapshot：

- `SolverOverlay.ShowResult(Node, SolverOverlaySnapshot)`；
- `SolverRouteRow.Populate(SolverOverlayTurnSnapshot)`；
- `SolverActionPill.Create(SolverOverlayActionSnapshot)`。

renderer 不得重新读取 `SolverResult`、`PlanAction`、`PlanCardChoice` 或 `ModelDb`。部署需要的标量由 Runtime 单独持有，不从控件反向读取。

`SolverSettingsPanel` 是设置页的单一控件所有者，按 partial 分离构建职责：主文件负责标题、常规/性能/反馈三页切换、重载、提交、恢复默认和固定状态栏；`General` 负责求解器、通知、自动执行，以及第一/二幕与最终 Boss 相互独立的血量取舍；`Performance` 负责预设、并行度、NoGC 开关、独立内存预算、排队式手动回收和折叠的自定义搜索参数；`BugReports` 负责诊断、联系方式与问题包导出/上传；`Controls` 只提供本面板共享的 Godot 控件样式、输入校验和行布局。partial 之间不建立第二份设置状态，持久化仍只写 `SolverSettingsData`。

`BossHpRelief` 只描述战斗事实：第一、二幕战后回复 80%，最终 Boss 后无后续战斗。`BossHpStrategy` 决定搜索如何使用该事实；通关优先沿用实际回复折算，最低战损把对应战斗恢复为普通 HP 权重。最终排序、智能药水开层和卖血阈值必须消费同一个有效策略，结果与诊断仍保留真实 `BossHpRelief`。

`SolverPotionStrategyPanel` 是主界面右侧独立窄浮层的逐瓶药水策略控件所有者。它只在主线程按当前槽位读取图标、标题和可搜索性，紧凑按钮在智能、保护和强制使用间循环；`SolverController` 以槽位和药水 ID 捕获不可变 `PotionStrategySnapshot`，自动计算开启时策略变化会废弃旧 continuation 并启动新搜索。新进入槽位的药水没有旧身份覆盖，默认按智能使用处理。

`SolverGrowthStrategyPanel` 拥有逐来源额外 HP 输入，原版八行之后按登记顺序追加 `GrowthSourceMirrors` 的第三方行（取牌函数抛异常时该行退化为无图标、标题显示 id 并记 warn，不连带面板失败），发布额度时把设置里尚未登记的 id 原样并回。与药水侧栏共享受视口约束的位置规则。“提前结束搜索的战损阈值”在 `SolverSettingsPanel.Performance` 的搜索停止条件分组展示，输入校验与保存仍复用设置面板的通用逻辑，沿用 `AcceptableBattleHpLoss` 存储字段。两种面板在外部鼠标点击时释放其输入框焦点，沿用失焦提交；成长 SpinBox 显式应用待输入文本。成长面板在主线程读取卡牌图像与官方标题；`SolverController.SetGrowthPolicy` 只保存成长配置、废弃旧 continuation/完整路线比较基线，并在自动计算开启时重算。`SolverSettings`、路线缓存、问题包和战前 API 设置快照共同携带成长额度。

「不考虑局外收益」开关由 SearchPolicySnapshot 的 EffectiveGrowthBudgets / EffectiveHasGrowthTargets 统一解释；开启后所有原版和第三方额度行置灰，原始配置保留。

`PhysicalMemoryUsage` 从操作系统采样实时物理内存，`SolverMemoryUsageBar` 在底栏把系统及其他程序占用显示为灰色、当前游戏进程工作集显示为彩色，剩余部分表示可用余量；文字只显示游戏进程的“当前内存占用 / 动态上限”，动态上限等于 CLR 安全总量减去系统占用。Smart 用药梯度之间释放上一层搜索图，按同窗分配预测决定是否同步回收；最终梯度或普通搜索正常结束后保留战斗级 NoGC 区域，战斗结束时等待引用释放并延时 `3–5 秒` 清理。异常耗尽、搜索内检查点和手动回收继续在各自安全边界处理。

`SolverSettingsPanel.BugReports` 持有问题包导出/上传的单实例 UI 生命周期、取消令牌、进度条和线程安全完成邮箱，并把文件发送和服务端确认显示为两个阶段。后台任务只向完成邮箱发布一次 `Succeeded / Canceled / Failed`；面板自己的 `_Process` 每帧先消费终态，再处理字节进度或取消等待，并在同一次终态消费中释放令牌、收起进度条、替换状态消息和恢复按钮。上传生命周期不依赖搜索使用的 `SolverDispatcher`。`CombatBugReportUploader` 不持有 Godot 控件，后台传输只通过 `IProgress<CombatBugReportUploadProgress>` 发布字节计数。进度到达文件总字节数只代表请求正文已经写出，只有服务端回执同时确认反馈编号和实收字节数才算上传成功。

问题包 v2 将 report.json、diagnostics/、replay/ 分开。导出时生成的 reportId 与上传 submissionId 一致；Uploader 从归档读取同一份元数据并核对编号和玩家描述，不在后台重新采样游戏。HTTPS 直连 miaovps，固定证书、名称和有效期校验，禁用重定向。离线读取器兼容旧 combat-solver/ 索引和新 replay/checkpoint.json，恢复材料路径由索引声明，详见 [报告协议](BUG_REPORT_PROTOCOL.md)。

## 7. Unattended 测试

`UnattendedTestRunner` 保留请求级编排和现有 fixture helper。新增流程应落到明确所有者：

| 组件 | 职责 |
|---|---|
| `ProtocolHost` | 请求文件循环、协议校验、进程复用、每请求测试开关、漂移注入与 reset |
| `ScenarioBuilder` | 建立跑局/遭遇、加载快照、注入牌/Power/遗物/药水/球/RNG；战前 API 模式直接恢复完整跑局并在进入目标战斗前核对规范化快照，返回 `ScenarioContext` |
| `Executor` | 分派严格差分、应用临时设置、启动搜索/全自动、等待复用/暂停/结束并恢复设置 |
| `Assertions` | 执行前边界检查和执行后的回合、生命、出牌、药水、Power 断言 |
| `Writer` | Passed/Held/Failed 公共协议字段、内存采集和结果文件原子替换 |

`UnattendedTestRunner.ReplayState.cs` 属于 `ScenarioBuilder` 的状态注入实现。它只接受同检查点的 `run-state` 与 schema 1 `replay-state` 组合，恢复后必须通过完整 `ContinuationStamp`；不能把部分字段相似的建局称为严格重放。

`UnattendedTestRunner.CheckpointArchive.RecordCheckpointModDifferencesAfterStartup` 只生成程序集清单诊断，不决定恢复能否继续。两条原包恢复入口共用此方法，Writer 保存 `modEnvironmentComparison` 的逐名称缺失/新增/构建变化；模型解码、原生录制重放和严格状态对账继续拥有实际失败判定。

NativeReplayDriver 保存开战/结束观察器抛出的原始异常，由 AdvanceAsync 的等待链中止请求，防止生命周期事件分发隔离异常后变成“边界缺失”。开战 RestoreOnly 同时读取并核对首个可操作检查点；CheckpointArchive.Prepare 在既有安全解压边界内携带其材料。模型表 hash 是诊断；ReplayAssertions 先比较二进制，旧表不可解码时明确返回未核验，只有已记录 ContinuationStamp 全部匹配才可输出 `restored_continuation`。完整恢复标志与仅状态通过分开。

`src/Replay/CheckpointArchive.cs` 是不依赖游戏的包协议读取器，负责 v2/v1 索引、旧包目录适配、材料配对校验和按原目录解包。`tools/CheckpointTool` 链接同一源文件提供离线预检，两端脚本不复制索引规则。`ScenarioBuilder` 经 `UnattendedTestRunner.CheckpointArchive.cs` 准备请求和临时材料；`Executor` 应用并恢复原包实际策略；`Writer` 输出独立的 `replayVerification`，区分材料检查、检查点恢复和后续执行。checkpoint 稳定 ID 不随六份快照的淘汰重编号。

`CombatReplayRecording` 拥有单场原生输入观察与不可变事件；战前存档在原生 RecordInitialState 边界采集，后台不读取事件的 live 对象。`CombatReplayOutcome` 单独观察玩家HP变化，不推进求解器的战损账本。`UnattendedTestRunner.NativeReplay` 属于ScenarioBuilder/Executor的恢复实现，以单人原生流程、动作和录制选择重建状态，不调用旧字段注入器；`ReplayAssertions` 提供二进制原生状态对账。`UnattendedCombatStartReplay` 只为旧包在最后一个生物加入后、开战Hook前注入已经捕获的开战状态，作用域结束即解除挂钩。

`src/Replay/AppendOnlyEventLog` 的单独后台写入器拥有临时文件，以 FIFO 屏障截取指定前缀；Runtime 只提交已冻结的事件。积压和文件大小有上限，失败与截断进入诊断状态，已保留材料仍可导出。完整快照仅保留六份并单独限制待序列化数量，历史尾片只供诊断，完整执行历史由原生事件重建。

`tools/CheckpointTool/BatchInputs` 负责 ZIP、汇总 ZIP、目录及旧目录的安全枚举和去重；`BatchRunner` 负责请求身份、断点续跑、进程调度、证据与结果口径。Windows/Linux 脚本分别维护本平台进程所有权、隔离环境和启动/停止，均不解析文本战损。`Writer` 在全局协议结果发布前写出每请求独立证据。`ProtocolHost` 只在建局前输入失败且异步静稳后允许继续复用，运行中失败退出。

不要从深层 fixture 直接写结果，不要在 entry 中重新建立战斗，也不要让断言负责执行动作。

协议 `Passed` 仅表示请求中实际启用的断言通过，不替代用户约定的更严质量验收；例如同为零损但结束回合增加，仍可能不合格。结果中的选中路线回合数不是搜索实际探索层数，后者以阶段日志单列。分配采样的加权字节不是存活堆或进程峰值，线程样本中的 `Wait` 也不是 CPU 利用率；采样配置和正常性能配置须分开记录。

使用替身描述器或选择器的单元断言，只覆盖传入候选集及委托合同，不自动证明真实 `SearchNode`、嵌套选择映射或完整路线质量。历史动作日志若未保存 `NestedChoices`，相同卡牌前缀不能称为旧路线的精确复现。

`UnattendedTestRunner.KnownRoutePathTrace.cs` 属于 Executor 的测试诊断共享实现：各样本先在正式回放中冻结已知合法前缀，再以完整动作/选择及政策标签观察原政策下的真实 coordinator；不传入固定路线或改变候选政策。灵魂枢纽、Custom 与外骨骼虫的薄入口只选样本、观察键和必要的完整性锚点；原生部署的冻结前缀类型共用，但运行路径仍分离。诊断按同一 solver/边界编号解释事件，并按固定原始敌人身份逐敌核对搜索根和实战根不变；单敌后缀别名证明明确拒绝多敌输入。Passed 不等同于路线质量、自动部署或性能通过。

外骨骼虫原路径观察入口保持第4步整池锚点；`KNOWN-EXOSKELETONS-CONTINUATION-PATH-TRACE-V0111` 复用相同24步四敌冻结证明，只把必要整池锚点移到第5步，用于检查生成分支存活后的第一动作。观察入口不提供路线或延长该分支的保留资格。

`UnattendedTestRunner.KnownRouteAliasReplay.cs` 只在测试侧验证实际生成的动作排列：从原根完整回放观察前缀，再原样追加冻结的获胜后缀，逐步核对完整状态、增量等价、累计指标和终局。完整通过后才能以同 solver、状态、政策标签及动作/选择身份作为整池锚点；它不重建 SearchNode 或循环/有序操作账本，也不证明不同排列的搜索调度资格相同。

`KNOWN-SOUL-GENERATION-SUFFIX-V0111` 将五个已记录前置选牌分别接上原样冻结的第9–26步，每步检查完整/增量及根/live不变，全部到达规定终局后才交出纯值前缀。`KNOWN-SOUL-VARIANT-PATH-TRACE-V0111` 在同一请求先完成该证明，再联合观察五条完整路线；观察只改变诊断筛选，不改变Search候选。冻结前缀不包含搜索评分或未来卖血标签，不能把部分可比字段匹配称为完整政策等价；实际当前/父政策标签须分桶报告，不能跨solver或跨标签拼接存活链。

`KNOWN-SOUL-RETAINED-PATH-TRACE-V0111` 在上述证明后选取实际存活的防御置顶变体，以第18步状态观察真实搜索，并复用共享别名回放证明“实际生成前缀+冻结末8步”。它允许已观察的动作换序，但不把转置拒绝原排列写成整条语义路径丢失，也不恢复其搜索调度资格。

`KNOWN-CUSTOM-DEFERRED-FRONTIER-V0111` 复用已严格回放的18步非终局前缀，测试有限队列、原根逐动作预算、停止/取消/异常时临时快照释放，以及恢复后原父链和政策字段复制；成功恢复后用最后一格工作量执行胜利后缀。构造的 SearchNode 政策字段属于合成合同，不能当作正式搜索曾赋予这些资格；TT断言只检查字典/条目身份及拒绝计数，不读取私有标签集合。fixture不启动Solve、不调用原版动作，也不是质量或性能证据。

`KNOWN-SOUL-GENERATION-CONTEXT-V0111` 在测试侧回放已观察的五个前置选择约束，输出生成后及执行同一个冻结过牌动作后的四牌堆完整语义token顺序；对所有已见变体逐一比较四组 `ChoiceCardKey` 数组，证明这五个上下文两两不同，而不是仅与基准比较。它不再要求生产快照附带实验派生的有序牌堆哈希；通用语义身份仍由原完整 `StateKey` 和回放差分验证，测试用token数组不替代完整状态键。每条执行完整/增量差分并验证根不变，不把牌序差异推断成保留资格。`KNOWN-EXOSKELETONS-ROUTE-REPLAY-V0111` 则严格重建旧24步约束，加上同导入根另一历史生成候选明确记录的第4步四次嵌套选择；从稳定父状态逐次重放并核对完整token、来源与上下文，不声称恢复旧选中动作字节。四个原始敌人逐一完整差分，另比较已知/活动阵容及死亡账本。其他缺少记录的选择仍显式失败，不补默认选择；两者均不启动Solve，不调用原版动作。

外骨骼虫回放可在全部前缀与最终根检查成功后一次性交出纯值冻结记录，供路径观察和 `KNOWN-EXOSKELETONS-ROUTE-NATIVE-V0111` 使用。后者先冻结24步预测，再执行原版动作；测试选择器按完整计划顺序、可用牌/来源牌堆及语义token逐实例匹配，原版ICardSelector没有SourceId/ContextId参数，不能声称直接核对了这些原生参数。独立测试观察器只属于该CombatState与原始四Creature，在真实终局清理前捕获四组完整状态，并等待相同战斗房间的CombatEnded；累计伤害、药水和洗牌事件另行核对。洗牌事件次数不混作Search按动作计的ShufflesCrossed，测试补丁在finally移除，不影响生产部署入口。

## 8. 工具与结构门禁

- `tools/run-unattended-test.ps1` / `tools/run-unattended-test.sh`：Windows / Linux 的平台原生入口，保留请求协议、精确进程生命周期、结果与静稳 ACK；同实例同时只有一个 producer。
- `tools/headless-runtime.ps1` / `tools/headless-runtime.sh`：拥有实例目录、私有游戏/Mod 内容快照与每用户主机租约。默认 exclusive，显式 parallel 最多两个游戏；CPU/内存预约随游戏进程存活，暖进程也占名额。它们不改变 Search DOP、NoGC、战斗语义或请求协议。详见 [实例与并行说明](HEADLESS_TESTING.md)。

- `tools/run-visible-steam-benchmark.ps1` / `tools/run-visible-steam-benchmark.sh`：Windows / Linux 的平台原生入口，负责正常可见 Steam 会话的搜索、GC 与帧口径。
- `tools/CoverageCatalog/Program.cs`：当前程序集和 registry descriptor 的覆盖目录生成/验证。
- `tools/verify-refactor-boundaries.ps1` / `tools/verify-refactor-boundaries.sh`：Windows / Linux 的等价门禁，阻止 Search 全局依赖、旧 controller 字段、worker live 回读、Beam 职责回流、unattended 编排回流、UI mutable 类型回流和 registry 私有反射；规则变化时必须同步维护两端。

纯职责移动至少运行 Release 编译与当前平台的结构门禁。改变语义、搜索或显示行为时，再按影响面选择严格差分、完整 headless、CoverageCatalog 或可见 Steam。

紧凑候选搜索接缝由 `CombatBeamSolver.CompactReplay.cs` 独占，`SearchPolicySnapshot.CompactRoot` 仅传递主线程捕获根，Runtime 通过 `SearchBackendPolicy` 选择。`CompactCombatRoot` 属于 Prediction；冻结候选仅持有共享根、不可变值和历史纯度标量，每 lane 独占执行／读取上下文。`SimulationSnapshot` 按需拥有派生兼容图并在释放时同时清空两种状态；`ReplayForkSeed` 移交精确不可变父候选及私有死亡集合。缺选择时冻结执行帧并由专门的 `ReadPending` 导入完整评分；快照独占请求与选项预览，释放时一并清空。完整动作和回合共享原评分及逻辑计数。根 Fork 的 COW 发布有窄锁，完整回放不串行化。完整原输入搜索路线和逻辑计数已一致，挂起选择迁移后的 headless 样本已有加速；[结果与限制](performance/simulation-search-backend-20260911.md)。

`CompactReplay` 另拥有独立 `CompactPolicyReadLane`，为同步政策消费者提供冻结值读取；不与执行 lane 共用可变模型，不让 hand/model 视图跨子回放或 yield 逃逸。动作准备、无进展抽牌数、父节点遗物和目标读取因此不再重建完整历史。合法性仍共享 `CanPlayCardAtResources` 与生命输入可显式提供的 `CombatPredictionState.IsHittable`；完成续用状态和完整搜索计数继续对账。

挂起读取只接受同根且 `NeedsChoice` 的程序；完成读取仍要求 `Complete`。选项只克隆当前来源牌，不保存 lane 模型池。计划消费共用 `TurnStartChoiceCursor`，保留来源／上下文／时点匹配与业务无效分支异常。回合开始选牌尚未结束时，AI 当前值／日志已前进，但公开意图仍读前一招式；根日志为空或末项不同于当前招式时，以捕获的根当前值为准。

串行展开与并行调度共用 `ActionPreparation`，避免保留借用手牌／模型跨子回放或 yield。`ContinuationStamp.CapturePredicted` 接受可选完成读视图，仍使用原编码器；动态生物、牌堆、卡牌计数与两条变化 RNG 来自读视图，其余值必须属于该闭包内不变或已单向导入的上下文。元数据上下文需精确匹配，返回文本可以保留，读视图不能随快照逃逸。

`Runtime/SearchBackendPolicy.cs` 独占主线程后端选择，正常根与初始准备根都调用它；初始准备／增量诊断明确保留模型后端。`CompactCombatRoot.TryCreate` 只在完整准入构造期间捕获已定义的 NotSupportedException，记录拒绝原因；执行和读取错误继续传播，不中途切换。卡牌、Power、遗物、附魔和机械骑士按精确原生类型准入，全部非空药水槽当前拒绝。准入分配纳入根捕获生命周期；日志报告后端、准备耗时与紧凑完成／挂起／物化次数。[生命周期、全自动与正常 NoGC 证据](performance/simulation-runtime-backend-20260911.md)。

精神过载与毁灭的值扩展：资源指令、阵营开始一次性标记和 Kill/Death 事件由 `Simulation/Compact` 独占；模型类型、可创建模板和能量／死亡 Hook 审计由 Prediction/Compact 独占。`CombatHistoryReadValues.DoomAppliers` 随事件窗口重置，读取器导入 lane 所属旧原键字段，不重放效果。旧精神过载由 `CardDrawCardMirrors.NeurosurgeOnPlay` 精确实现获得能量→抽牌→能力，CardEffectSpecRegistry 不再重复补偿。通用 Power 新实例按实际玩家 Debuff 设置持续跳过标记，语义指纹仍只区分三种持续减益。[边界与原生证据](performance/simulation-necro-resources-20260911.md)。

宠物身份由 `SimulatedCombatState._rootOsties` 在主线程捕获（包括空值），Fork 共享不可变身份表；`CardLifecycle.GetOsty` 优先分支生成映射，禁止回落 live。宠物死亡由通用 Damage 入口在 AfterDeath 后通知 `ICombatPredictionEffectSink.RemovePowersAfterDeath`，使用同一领域清理，DieForYou 保留阵容及自身能力。敌人死亡阶段扫描不承担宠物清理。[根隔离与五步原生证据](performance/simulation-osty-ownership-20260911.md)。

宠物值执行由 `CreatureAttackLayout` 的独立末尾槽与 `EnemyEnd` 管理，死亡保留身份；`BasicPowerLayout` 保留代伤能力，`ResumableDiscardProgram` 保存实际施伤者及双结果顺序，玩家死亡独立连带杀死存活宠物。`CompletedOstyReadBinding` 属于每 lane 的派生兼容上下文，只导入当前 HP／最大生命／格挡及原最大生命映射的缺席形状，不运行召唤、伤害或 Power 命令。`ContinuationStamp` 从读视图取得宠物 HP，其他宠物读取消费同一绑定上下文。首次创建仍在根准入时拒绝。

`ModifyUnblockedDamageTargetMirrors` 独占代伤 Hook 的精确分支状态实现，按原版链式传递目标且保留战斗结束时的分发；未知覆盖显式拒绝。怪物行动不再临时移除代伤 Power。普通玩家回合在 Hook 前冻结 `Allies` 参与者（包括死亡但保留的宠物），先快照全部能力，清完全部格挡后再逐个 AfterBlockCleared；额外玩家回合只选择玩家。召唤增长按封顶后的实际最大生命增量治疗。[原生对照与边界](performance/simulation-osty-values-20260911.md)。

`CompactRoundRoot.TurnStartSummon` 保存完整根捕获的初始遗物召唤量；Prediction 只准入带既有宠物和完整回合的精确 `BoundPhylactery`。回合驱动在 ResetEnergy 后、建立抽牌帧前调用共享 `SummonPet`，避免选择恢复重复召唤。首次战前创建已经落在根中，不由此入口补演。[三个回合与挂起对照](performance/simulation-pet-turns-20260911.md)。

`CardEffectProgram.ExhaustFromDraw` 在召唤完成后建立独立选择边界，`CompactPlanReplay` 按检索／消耗效果决定来源，不能仅按抽牌堆推断 Stratagem。选中牌复用 `ResultMoved(Exhaust)` 的有序牌堆／消耗历史语义；恢复推进下一指令，空牌堆直接继续。`ExhaustsCards` 显式关闭存活牌集合不变缓存。Prediction 编译精确 `Cleanse`／`Afterlife`，旧模拟器与投影不增加第二次效果写入。[原生与搜索验证](performance/simulation-draw-exhaust-20260911.md)。


随机生成牌继续由 Engine 的通用 `GenerateCards` 执行：`CardGenerationPlacement.RandomDraw` 使用值状态的 Shuffle RNG，逐张记录目标牌堆和插入位置，模板通过实例定义索引读取。Prediction 在根捕获挽歌所需的普通／升级灵魂模板；兼容物化只导入位置，不重新调用随机插入。`RepeatForEnergyX` 保留逐次召唤命令。Testing 的 `CompactPetCardRoutes` 共用正式路线、完整状态／续用和原生生成身份对照，`CompactDirge` 与 `CompactDrawExhaust` 只拥有各自建局和效果断言。[证据](performance/simulation-dirge-20260911.md)。


紧凑卡牌操作扩展：`LoseEnemyHp` 使用不受力量／格挡修正的伤害属性，保留施伤者与卡牌来源，不提交攻击完成；`RetrieveFromDiscard` 在攻击后独立挂起，计划来源保留弃牌堆。`Unplaced` 与 `Removed` 分开持有身份：前者保留终局生成历史，没有牌堆和移除标记，也不消费插入 RNG。兼容物化只恢复生成历史；旧历史仅保留标量卡牌快照，原生清理前观察另外核对完整模板指纹。怪物状态牌入口保留接收者存活门禁。Testing 的共享宠物路线通过现有终局观察器的可选快照回调捕获牌序／能力／生成元数据，回调只读且在原版清理之前执行。旧 `CardChoiceSupport` 的坟冢爆射规格遵循原生 `IsEnding` 门禁。[证据](performance/simulation-necro-card-operations-20260911.md)。

紧凑费用读取扩展：`BorrowedTime`／`Veilpiercer` 编译为已有能量、攻击与能力指令；费用先应用本地修饰、再加预借时间、最后按当前手牌／出牌区位置处理刺破帷幕，负基础费用与 X 跳过全局修改，结束门禁同原生。付款值保存在帧中，免费层数在出牌开始之前直接递减。`ICompletedEnergyCostReadSource` 是 Engine 的内部只读合同，由每个 `CompactCardMetadataReadBinding` 按当前实例映射到权威值程序；只挂在其私有评估 Simulator，Fork 不复制，不随候选保留。评估模型的根牌堆不能用于全局费用的位置判定。旧 Hook 派发允许两阶段同步能量费用查询在选牌挂起时读取，效果 Hook 仍默认暂停。[证据](performance/simulation-cost-powers-20260911.md)。

参数化 `CardEffectSpecRegistry.PowerEffects` 仍是旧后端这些能力效果的权威入口；每项施加检查 `simulator.IsEnding`，对应原版 PowerCmd.Apply，保留同牌其他资源／生成效果各自的命令规则。


吊杀的卡牌身份只由 Prediction 精确编译：`AttackMultiplierPower` 是不可变攻击指令元数据，Engine 在力量加值之后按目标能力乘算，普通卡牌／怪物／宠物攻击默认不携带该标记。`ApplyPowerAtLeastCurrent` 在攻击完成后读取当前目标层数；当前闭包唯一的施加修饰为人工制品，仍运行修饰再使用共享提交封顶，与原生封顶零请求仍可消耗人工制品的结果相同。未知请求量观察者仍在根准入时拒绝，不能将此等价变换扩展到开放 Hook 集合。能力继续使用原有层数／顺序／退休／回合快照槽和只读绑定。[原生与搜索证据](performance/simulation-hang-20260911.md)。


雕琢打击／响指由 Prediction 编译为攻击／奥斯蒂攻击后 `ApplyKeywordFromHand`。`CardInstanceValue` 把定义编号、捕获 X 和两种新增关键字保存在原实例槽；X 上限 999999999 占 30 位，关键字不会在付款时被覆盖。原有关键字来自不可变定义，新增虚无／保留来自撤销状态，全部费用、结束历史、手牌末尾和保留读取同一来源。关键字选择的候选先过滤；计划 token 的 OptionOccurrence 用过滤集合，SourceOccurrence 与完整来源保留独立身份。完成读视图仅修改私有模型的局部关键字并失效缓存，恢复旧候选会移除后加标记；未知全局关键字 Hook 仍拒绝。旧选牌规格为这两张牌补上原生结束门禁。[证据](performance/simulation-keywords-20260911.md)。

出牌前能力扩展：灰烬之灵／死亡之舞与刺破帷幕按当前能力获得顺序执行，查询、递减及非威力格挡都在 CardPlayStarted 之前。死亡之舞的能量门槛在根捕获为不可变配置，普通牌每次查询当前费用，X 使用捕获的付款值；旧镜像与纯值路径均遵循 GetResolved。迅速编译为末尾 DrawOnce，先将实例状态标成失效，再进入可暂停抽牌帧；恢复到该帧不会重启附魔。定义编号使用 31 位、失效状态一位、X 30 位及新增关键字两位，仍为一个可撤销实例槽。兼容投影为附魔抽牌建立独立来源作用域，读取器只导入私有预览状态、失效缓存。最后击杀后仍执行失效赋值，Draw 的结束门禁阻止抽牌与洗牌。[证据](performance/simulation-card-hooks-20260911.md)。


致死性扩展：`ResumableDiscardProgram` 使用既有预留槽保存攻击牌开始次数，根值从已冻结的当前回合历史捕获，在 CardPlayStarted 时增加、双方阵营开始时归零；暂停、冻结和撤销包含该槽。`BasicPowerLayout` 在力量加值后按卡牌主人和开始次数应用倍率，宠物实际施伤者保留自己的力量／虚弱；当前闭包仅含 Play 中首次 OnPlay，重放和外部卡牌来源仍拒绝。旧 `SimulatedCombatState` 在主线程一次捕获当前／上一玩家回合的非复制最后攻击，后续只消费分支映射；空窗口不能从 live 回合号补读。两份映射沿用统一 Fork 重映射与原键编码。[证据](performance/simulation-lethality-20260911.md)。


神气制胜的独立实例由 `PanachePowerLayout` 持有可增长的撤销缓冲区，保存每个实例的数量、施加者、获得顺序、回合初始值、倒计数、首次应用与持续标记。`BasicPowerLayout.NextOrder` 为两种能力布局提供共同的获得序列；`ResumableDiscardProgram.Panache` 在卡牌结束历史之后执行监听器，逐实例更新与非威力群体伤害、回合重置和玩家死亡清理均消费值状态。末击后的剩余监听器仍完成计数重置。Prediction 保留能力方法来源作用域；Search 的 `CompletedPowerReadBinding` 池化独立读取模型，仅在超过该 lane 历史最大实例数时扩容，恢复较少实例时停用多余模型，并保留根单槽／多实例映射。旧神气制胜施加改用 `ApplyInstancedPower`，沿用命令门禁／修改和数量回调；原键及完整续用显式读取分支 `AlreadyApplied`。[证据](performance/simulation-panache-20260911.md)。

玩家回合末第二阶段标记待失败后，原生仍切换到敌方、捕获能力起始值并清格挡，直到敌方开始安全点才提交终局。普通 Hook 分派入口遇到 IsOverOrEnding 时整批跳过（AfterCardPlayed 等原生明确例外除外）；已开始分派不能逐监听器中止。中毒和延迟格挡属于新分派，不能提前结束整个回合，也不能继续补偿这些效果。终局原生观察分别绑定胜利清理和 ProcessPendingLoss，验证清理前完整状态。

同一能力的原生 Type 与按请求量计算的 GetTypeForAmount 不能混用。人工制品按请求量判断负力量／负敏捷为减益，首次持续计数标记却依据原生 Type；两种属性能力本身仍为增益。基础值布局保留独立判定，在原生差分中覆盖归零后重获、初始负值、双方人工制品、不同施加者、附魔选择和完整回合。

命运同担由 `CompactCardProgramCompiler` 编译为玩家／目标两条有序负力量施加，沿用基础 Power 值槽、人工制品、退休／重获和读取投影；52 种精确卡牌的准入仍在 Prediction，内核不识别卡牌类型。[证据](performance/simulation-shared-fate-20260911.md)。
### 回合末卡牌 Hook 的接收者身份

`HookMirrors.BeforeSideTurnEnd` 的常规阶段先通过 `CardHookReceiver` 固定监听成员与对应分支 `PredictedCard`，再按原序读取当前 Preview。前一监听者触发 COW 时，不把已脱离牌堆的旧预览传给后一卡牌 Hook；不重新枚举成员，不保留跨阶段或跨分支接收者。

上游 0.36.0 集成后，紧凑内核的 `PlayerPhaseChanged` 事件记录玩家阶段，物化与直接读取共用 Prediction 的枚举映射；读视图每次从根阶段恢复。`CardHistoryReadValues.AttackSkillStarts` 与原历史字段一样消费开始事件和双方窗口重置，进入原键及完整续用。战略摘要缓存包含 `skillsExhaust`，消耗抽牌时序按当前手牌独立附加。[验证边界](performance/simulation-upstream-merge-20260911.md)。

书页风暴通过 `ResumableDiscardProgram.Draw` 的独立可撤销缓冲区保存父／子抽牌请求、返回数与待完成卡；全部抽牌共用 BeginDrawCard。内核只识别 Power 枚举和虚无标记，卡牌身份与 Hook 准入仍属于 Prediction。投影拥有未完成抽牌历史栈和方法来源栈，直接读取器只计真正历史事件；无相关 Hook 的根不创建该栈。Testing 的可选语义路线 helper 使用正式动作准备及选择解析器，固定节点搜索继续原政策。[原生与搜索证据](performance/simulation-pagestorm-20260911.md)。

虚空之唤的生产职责分工：`CompactCardProgramCompiler` 负责精确类型、实例状态与 `Innate` 升级准入，并把它编译成一条 `ApplyBasicPower(CallOfTheVoid)`；`BasicPowerKind.CallOfTheVoid` 复用 `BasicPowerLayout` 的数量／施加者／获得顺序／退休值槽，内核不识别卡名。`CompactRoundRoot.BeforeHandDrawPool` 保存捕获的池索引，`ResumableDiscardProgram.Rounds` 在能量重置与 `AfterEnergyResetLate` 宠物生成之后、合成起手抽牌帧之前执行整批生成；`Generated` 事件的创建者区分怪物／卡牌动作／玩家回合开始能力，回合开始批次使用 `Random` 结果类型。Pool 捕获属于 Prediction/Compact：只有根中实际出现该卡或 `CallOfTheVoidPower` 时才从 `TryGetRootEligibleCharacterCardsForCombat` 取持有人角色的完整冻结池，逐候选建立携带原生 `Ethereal` 的不可变模板并精确编译；任何不可表示候选、缺失回合闭包或缺失池索引都拒绝整根，不缩小生成池、不回退模型后端。五字段生成流由 `CardGenerationPools` 分配在 `ReversibleValueState`，`CompletedStateReadView.CardGenerationRng` 把 lane 值暴露给状态键与续用，未持有显式值的程序保持旧行为。生产准入未扩大，完整未闭包根继续显式拒绝。[记录](performance/simulation-void-generation-20260911.md)。
