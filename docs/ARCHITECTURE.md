# CombatSolver 架构与职责地图

本文描述当前源码的所有权边界。它面向维护者和 coding agent；玩家功能说明见根目录 `README.md`，历史重构证据见 `docs/refactoring/`。

职责迁移时优先更新本文，并同步更新 Windows 的 `tools/verify-refactor-boundaries.ps1` 与 Linux 的 `tools/verify-refactor-boundaries.sh`。历史审计记录保留当时结论，不承担当前导航职责。

## 1. 运行链

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

普通搜索在 Runtime 同时等待根回收屏障、原生动作队列及当前动作完成后捕获根；队列因等待玩家选择暂时无可执行动作时，当前动作的完成任务仍约束捕获。任何异步等待恢复后都重新进入请求校验，沿用请求身份和战斗生命周期取消；专用回合准备选牌入口先行处理。

## 2. Runtime

| 文件 | 职责 | 不负责 |
|---|---|---|
| `src/Runtime/Entry.cs` | Mod 初始化、补丁安装、战斗与回合生命周期入口、无人请求循环启动 | 搜索策略和战斗语义 |
| `src/Runtime/CombatSolverLog.cs` / `CombatDiagnosticJournal.cs` | 独立日志入口；生产线程入队不可变消息，复用后台事件文件；战斗切换摘要化、搜索日志绑定所属战斗、提交前缀冻结 | Godot 全局日志收集、搜索候选判定、后台读取 live 状态 |
| `src/Runtime/OnlinePresence.cs` | 主线程在线标量采样、持久安装标识、可取消 HTTPS 心跳；无头和多人隔离 | 搜索策略、完整路线上传、服务端历史存储 |
| `src/Runtime/SolverController.cs` | 主线程搜索/续用/部署/全自动编排，结果过期与重算审计 | Beam 内部算法和 UI 布局 |
| `src/Runtime/SolverControllerSessions.cs` | combat/search/deployment 三类会话的状态与取消所有权 | 跨会话全局静态字段堆积 |
| `src/Runtime/CombatRootSnapshot.cs` | 主线程捕获完整预测根，比较捕获前后 live 状态，并向 worker 提供 Fork 根 | worker 惰性读取 live 战斗 |
| `src/Runtime/ContinuationStamp.cs` | 跨回合 live/predicted 状态文本、首个差异与完整差异；九条战斗 RNG 使用计数器与四段内部状态共同核对 | Beam 状态去重 |
| `src/Runtime/SolvedRouteCache.cs` | 主线程捕获路线记录键；后台按完整根与策略读写本地路线副本，独立于战斗会话和 SL 入口；Forecast 使用新根对象 | 搜索策略、原生存档修改、保留旧战斗对象 |
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
| `src/Runtime/PlayerTurnSetupPatches.cs` | 首回合原生页面出现后的 Start 根搜索；后续回合观察上一轮 `EndTurn.TurnStartChoices` 的原生页面，全自动直接可见重放，单步默认交还玩家并允许执行/全自动入口接管既有选择；进入 Play 后交给 continuation 核对；跨 Reset 的 Setup/部署延迟由 lifecycle token 取消 | 普通 Play 阶段搜索与动作部署 |
| `src/Runtime/NativeChoiceRuntime.cs` | 观测原版战斗选择请求，按卡牌语义状态匹配计划实例，并锁定、驱动真实页面控件 | 选择分支枚举和战斗结算 |
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

## 3. Search

根创建时，`PredictionModPatchAudit` 在 Prediction 层检查已有卡牌 OnPlay 的第三方 Harmony 补丁；每根按类型去重并读取当前补丁表。它只负责未支持行为的准入边界，不执行补丁或提供第三方镜像注册，后续生成卡牌和其他方法不在此入口覆盖范围。

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
| `CombatBeamSolver.ParallelExpansion.cs` | 固定 worker lane、卡牌/药水动作准备与原始候选物化、按输入顺序串行提交 |
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

`SimulatedCombatState*.cs` 把内嵌引擎状态适配为搜索所需的战斗领域视图：

- `Fork.cs`：统一稳定边界和对象图复制；
- `MonsterAi.cs` / `MonsterState.cs`：分支行动、私有 AI、已知怪物静态值；
- `DeathLifecycle.cs`：死亡、复活与阵容事务；
- `ActionChoices.cs` / `TurnStartChoices.cs` / `AutoPlay.cs`：嵌套选择与自动出牌；
- `CardLifecycle.cs` / `CardPowerHistory.cs` / `PowerLifecycle.cs`：卡牌和 Power 跨事件状态；
- 凡庸在 `ShouldPlayMirrors` 使用同一分支手牌/开始次数入口约束手动与自动打牌。`_cardPlayStartsThisTurn` 包含重复播放和仍在执行的外层卡牌，根来自 CardPlaysStarted，随 Fork 复制、回合开始清零，进入 fingerprint 和 `CardEventHistory` 的 live/predicted 续用文本；不能以完成次数或手动系列数代替。
- `Relics.cs`、`PowerRelics.cs`、`ReactiveRelics.cs` 等：遗物与组合事务；
- `Potions.cs`：药水槽和使用状态。

活动 roster 只决定当前可行动、可选目标和 listener。已经捕获的怪物 AI/静态参数属于已知怪物和分支生命周期，不能在移出活动 roster 时提前删除。

`SimulatedCombatState` 将基础监听快照分为阵容/遗物/药水/根 Power 前段和球/卡牌/原生附着效果后段。卡牌或球变化只重建后段；阵容或药水变化清空基础前段。有效/活动 Power 只重写前段，沿用原完整顺序；新增 Power 找不到前段所有者锚点时回退到完整列表，保留卡牌锚点或列表尾部插入。不透明 CardModifier 根始终走完整路径，其附着监听追加器仍收到前段在内的完整列表。成功分段后的有效/活动前段及 Power 投影也可跨卡牌/球变化保留；Power 变更与基础前段变化仍完整清空这些派生缓存。无前段锚点和不透明来源不保留该投影。发布的各段不可变，拼接视图长度一次冻结；Fork 通过同一 `PredictionForkContext` 重映射接收者，并复用相同后段。`HOOK_LISTENER_SEGMENTS scope=root_cumulative` 记录基础及有效前段构建/复用与分段/完整构建，主搜与恢复数不可相加。

`EffectivePowers` 保留已知敌人尚待完成死亡结算的能力；普通 `ICombatState` / `ICombatPredictionHookListenerSource` 回调使用活动监听视图，排除所有者已离场的 Power。两种视图共用既有根与分支能力实例，活动视图随阵容和能力缓存失效，不清空死亡补偿所需的数据。

## 4. 内嵌模拟引擎

`SimulatedCombatState.Apply<T>` 共用 `PreparePowerApplication` 与 `ApplyPreparedPower`：前者保持修正与 Artifact 拦截，后者独占模型获得、数量／顺序和变更记录。临时力量入口在首次写入计数之前施加 Strength，随后按原版请求偏移与当前数量条件处理回调；叠加封顶仍传递原偏移。普通 Power 不走临时力量回调，不重复准备。

### 4.1 基础层

`src/Engine/InCombat/Simulation/` 负责通用战斗命令时序、伤害、牌堆、历史、RNG、球和 Fork。它不包含单张卡、单个 Power 或具体怪物的搜索策略。历史卡牌 Started/Finished 与 DamageReceived 的卡牌来源使用不可变卡牌快照；当前动作是否开始以精确 trace-frame 身份判定，保留原生 `CardPlay` 身份，不以 Original 卡牌身份合并兄弟分支。`CombatPredictionHistory` 以不可变 prefix segment + 分支本地 mutable tail 保存事件；动作后缀消费者必须使用冻结上界的 `EntriesFrom/EntriesBetween`，不能先遍历完整 prefix 再 `Skip`，否则长线会把一次局部查询放大为随深度增长的重复工作。

`src/Engine/Common/` 提供 `PredictedCard`、`PredictionForkContext`、`PredictionStateStore` 和通用模型克隆。StateStore 直接持有可 Fork 的 state，空字典按需创建；仍在同一 context 中按原跨类型顺序 eager Fork，不能对调用者已借出的可变引用使用通用延迟 COW。一次 Fork 内的所有结构必须共享同一个 context；分支可变对象必须显式重映射。`BaseLibCloneConcurrency` 是原版与预测克隆共用的外部扩展并发边界，只包围模型深克隆阶段。

`MirroredHookListenerFilter` 为 `HookMirrors` 和原生关键字空操作判定提供静态回调位图；原生/领域监听序列完整保留。只有确认没有 `TryModifyKeywordsInCombat` 参与者时，关键字查询才直接读取本地集合。根捕获重新检查相关 AbstractModel 基方法及原生 `Hook.ModifyKeywordsInCombat` 的 Harmony 补丁，有补丁时旁路；第三方/动态类型全部保留，BaseLib 不透明 CardModifier 根也旁路。`SimulatedCombatState` 的分支监听视图沿既有失效边界清空。不可变布局只含 Type/位图：优先复用分支旧布局，失配后查询同根有界共享表，哈希只选槽，完整类型顺序相同才复用；碰撞、并发覆盖和超长列表都不能误认序列。共享表不持有任何 Model，原接收者仍来自当前分支快照；它随根回收，不进入状态键或 ContinuationStamp。`HOOK_LAYOUT_CACHE scope=root_cumulative` 记录共享查询命中、未命中、碰撞和旁路，主搜/恢复日志不能相加。合同覆盖类型顺序、重复项、跨分支接收者、哈希碰撞、并发读取、关键字原生对照及根间补丁刷新。

`CombatPredictionSimulator.TerminalStamp` 在与原版对应的完整动作/阶段安全检查点首次锁定胜负及影子玩家回合号，按值 Fork；`IsEnding` 仍是无副作用查询，不在单个 Hook 监听器之间提前终止正在结算的序列。`SimulationSnapshot` 独立保留此值，释放模拟器后，终局标注、临时结果、最终排序和已知胜利上界仍读取同一时点。`PlanAction.Turn` 只表示发起动作的回合，不能代表该动作跨回合结算后的终局回合。

`Simulation/Compact/` 是尚未接入生产搜索的紧凑执行实验。`ReversibleValueState` 独占连续值槽、撤销日志、单次 LIFO 检查点及派生页缓存；普通写入和 rollback 都将对应页失效。`ReversibleValueState.FrozenValues` 独占发布时复制的页表，共享只含位图和非零值的私有不可变 64 槽页，不持有 worker、祖先候选或旧模型。恢复要求同根且目标没有活动 checkpoint；只重写失效或不同的页，清除旧候选遗留的零槽，目标容量足够时复用恢复不分配，容量不足时由 lane 扩容。追加槽位属于当前分支；检查点保存逻辑长度，撤销删除后缀并清零，后续分支复用索引不能看见旧值。候选只复制实际使用范围的页表，恢复可跨同根不同逻辑长度，工作区余量不进入候选。事件带通过 `ReversibleValueBuffer` 索引按需分配的块：64 值叶与 32 路索引的长度、树根、高度和尾叶指针都在工作区，允许其他领域状态在事件之间追加。事件用两个值保留完整 32 位实例／目标／金额，避免生成实例编号被截断；布局不持有分支缓存。页表及恢复扫描仍随实际槽数量增长；该接口只负责值存储；生成实例由程序分配，通用跨回合语义仍未迁移。`ResumableDiscardProgram` 将牌堆、资源、显式执行帧、选择和事件游标全部写入同一值槽，并共享不可变卡牌定义。卡牌定义与实例分离，生成实例的定义／捕获 X 和六个有序牌堆使用可增长缓冲区；根实例的定义固定，模板在根捕获。生成事件保留逐实例顺序与满手转入弃牌堆的结果，生成后的洗牌按定义比较；小刀出牌次数通过原指纹字段读取。完成读取器拥有按实例／定义复用的模型池，池不进入候选。`CardEffectProgram` 私有复制有序指令数组，Testing 将已准入原版卡牌编译为效果序列；每帧的指令索引与抽牌进度属于同一值槽。选择／自动牌结束后继续当前或下一条指令，不重放父牌前缀；生存者增加格挡后弃牌。候选支持新建工作区或 `RestoreInto` 复用已有 lane。诊断写入/事件计数仍累计在各 lane，不属于恢复的战斗状态。内核不引用原生 Model、Simulator、Task 或委托。当前扩展到抽弃牌、自动出牌、防御／后空翻格挡、洗牌、战略选牌与两个遗物的格挡触发；Shuffle 的五字段与计数进入相同撤销槽，比较矩阵保留原版同名卡排序关系。允许洗牌时最多一个 Sly 实例；容量或未支持效果明确失败。`CreatureAttackLayout` 另提供主要敌人基础 Power 域的打击、格挡／生命伤害、离场和永久死亡槽位，执行帧与事件同时保存目标。最后一击遵守原出牌区结束门，终局在完整动作后的显式安全检查点锁定；恢复／撤销包含死亡与终局。`BasicPowerLayout` 将力量、敏捷、虚弱、易伤、脆弱、中毒的数量、施加者、获得顺序与根槽退休标记纳入同一工作区；中和可创建／叠加敌方虚弱，死亡清理所属 Power，伤害与格挡保留 decimal 修正直到原标量边界。卡牌定义现明确费用形式和结果位置；付款 X、逐帧资源值和移除集合属于同一工作区，结果移动事件区分弃牌／消耗／移除。带符号的基础 Power 指令使用捕获 X；新增能力牌移除与 X 消耗流程。群体施加按稳定阵容先完成当前指令的全部目标，再执行下一指令；已离场目标由命令门排除。抽牌返回的第一张实例保存在各帧独立值槽，条件分支只判断实际抽到的牌，不把洗牌检索当抽牌；空返回直接跳过对应效果。卡牌类型与不可变虚无标记属于定义，完成事件保留原虚无历史。中毒主动触发现进入同一伤害／死亡值流程，事件保留无攻击者／无卡牌来源、无属性修正与穿透标记；存活后递减，零层退休，重新获得保留新顺序。整手弃抽捕获原手牌与数量，逐牌弃牌 Hook 后抽牌，抽牌完成后才处理捕获的 Sly 列表；洗牌返回位置随帧保存，不能重做弃牌。目标 Power 条件与存活敌人 Power 总量在执行指令时读取值槽；基础值与额外倍率由编译器捕获，求和结果再经过敏捷／脆弱和格挡取整，不能把根预览值当作分支结果。玩家下回合格挡与必备工具计数现在也使用 Power 值槽，按种类仅增加玩家槽；格挡→Power 指令传递修正后的返回值，准入按可达敏捷区间排除正小数生成零层实例的未表示语义。敌方临时力量计数使用独立 Power 槽，尖啸先执行首次 Strength 再加入计数，叠加按请求偏移处理；敌人死亡清除计数与力量，退休标记随撤销恢复。复杂 Power、复杂死亡与回合推进尚未迁移，不能将其称为通用新后端。

`Testing/CompactDiscardProjection.cs` 负责整根封闭能力准入及旧模型事件投影，`Testing/CompactCardProgramCompiler.cs` 负责精确卡牌状态准入及不可变指令编译；当前共二十九种精确卡牌类型，包含单体／群体中毒、主动中毒触发、整手弃抽、条件抽牌及防御后虚弱。未镜像 OnPlay 的补偿在原方法作用域退出后投影，间接伤害来源不能伪造为 OnPlay 内的卡牌攻击。投影仅用于完整状态／历史与原生差分，不再次执行 OnPlay、弃牌 Hook 或选择器。`Testing/CompactDiscardReadView.cs` 在同一准入闭包内直接读取值牌堆、资源、Shuffle RNG 和已提交事件；根卡牌及其他生命周期只作已证明不变的元数据。每读取器独占一个根副本供旧公式的可变 scratch 使用，另一个初始化副本取得会惰性物化的历史初值，保留读取根的缺席／零值区别；两个副本的成本计入初始化，每叶不再 Fork 或物化旧图。风险来源由原 registry 区分未镜像与不完整镜像，经 `PredictionCoverage` 原分类／排序规范化；每读取器只缓存实际出现的组合，不预建全部子集，支持根内最多 64 个独立来源。`Testing/CompactCardMetadataReadBinding.cs` 在读取器初始化时取得独占预览，逐叶只单向导入 X 值和移除标志、失效相关缓存；旧模型不是第二份执行权威。`CardHistoryReadValues.Exhausts` 提供当前消耗历史；卡牌集合／元数据变化时旁路卡牌和策略摘要缓存。`SimulatedCombatState.CompletedPowerReads.cs` 提供每读取器私有的 `CompletedPowerReadBinding`：初始化克隆原实例，并为非零根 Power 准备独立的规范新实例；根据撤销状态的根槽退休标记选择读取模型，使重获后的回合初始数量、跳过持续计时标记、额外 Target 与动态变量恢复原生默认值，逆向恢复仍使用原模型。两组模型仅在初始化克隆并恢复原归属；每次读取仅单向替换提供的 Power 数量／施加者、退休集合、获得序列和阵容，失效监听器缓存，由既有 owner-anchor 算法得出有效顺序。它不执行命令、Hook、随机数或数量通知，不回写值程序，也不逐叶克隆。

`BasicPowerLayout` 还捕获既有人工制品实例，数量、施加者、获得顺序及退休随值状态恢复；没有创建指令时不额外预留空实例。`PreparePower` 在零值／结束／死亡门之后处理负面施加的阻止与消耗，`CommitPower` 写入准备后的数量；临时力量先准备外层计数，再执行首次内部力量和单次计数提交。具体[原生证据](performance/simulation-compact-artifact-20260911.md)。

`RandomDrawCost` 只保存复制的根修饰前缀，`RandomDrawCostLayout` 只保存不可变值位置；按卡牌增长的完整修饰列表与费用 RNG 五字段均在撤销状态。`ValueRng` 为各流共用纯值算法，流状态独立；`Slither` 真正抽入手牌时追加本场绝对费用，付款读取当前列表末项。读取器单向导入列表并复用私有修饰对象，`CompletedStateReadView.EnergyCostRng` 进入原键的原字段位置；没有费用效果时继续使用根值。详见[原生与成本证据](performance/simulation-random-costs-20260911.md)。

生成附魔的编译折叠仅用于已核对的 `BLADE_OF_INK`／正常 1 层 `Inky` 小刀：全部生成监听器无中间观察者，附魔没有战斗历史或修改效果。最终定义仍在根捕获，新增监听器／修饰时重新证明；[原生证据](performance/simulation-inky-cards-20260911.md)。

`Search/CompletedStateReadView.cs` 是同步已完成状态的读取合同，既有 Snapshot 与 `SnapshotFromReadView` 共用 `SnapshotCore`、合法性、完整估值、投影洗牌与原键编码。`CombatBeamSolver.ReadView.cs` 持有共用的值牌堆编码与可选的根内不变特征缓存；敌人摘要、威胁焦点、卡牌估值与策略上下文仍调用原公式，只在准入证明敌人／AI、Power 与存活牌集合及元数据不变时复用。每个稳定根新建缓存，不保留跨回合或跨根条目；非实验入口不启用缓存。`SimulatedCombatState.AppendFingerprint` 在原序列中替换已改变的 owner 历史项和技能集合；`CombatHistoryReadValues` 保存读取器派生的受伤集合、攻击命中对、上一张攻击与死亡阶段，编码仍由原方法负责。读取结果立即释放借用 Simulator；读取器不能逃入保留候选，也不建立旧图与值状态的双写权威。读取合同的 `EvaluationContext` 表示私有评估上下文，不再暗示其中 Power 始终停留在根值。所有未提供字段必须在准入程序内不变；因此不能将当前合同用于任意 Power 变化、复杂死亡或其他随机流写入。双端门禁仍禁止生产 Search/Runtime 调用实验入口；生产后端与调度未切换。

生物标量现在由纯值 `CreatureVitals` 保存并统一实现扣格挡、扣血、治疗与最大生命限幅；生产 `SimCreatureState` 持有该值，负责稳定 Creature 身份、显示值和原 `DamageResult` 外壳。`CreatureValueSlots` 仅是不可变布局位置，向程序所属工作区读写 HP／MaxHp／Block／Present，生命归零不会自动移出阵容。它不包含伤害 Hook、攻击事务、历史或死亡回调。

完成状态合同另提供逐生物数值、有序敌方 roster 和显式可空的终局时点。完整快照的分布、集火、威胁与原键共用这些值；AI／沙漏计数的原键仍按活动 roster 原序编码。敌人值可变时禁用该根的敌人／焦点缓存，卡牌元数据复用仍要求自身闭包成立。`UnattendedTestRunner.CreatureValues` 用三敌 16 状态对照全部 Snapshot 属性／原键／排序，并以 12 个原生非致命伤害样例验证基础算术；这只是伤害执行迁移的基础，尚未迁移 Power／死亡历史、宠物生命周期及攻击命令。根内未迁移的 Hook／AI／领域公式也必须与已变化数值无关，不能仅凭 Power 数量未变就认定可借用根。

牌组根监听器的准入独立于战斗监听器：`CompactDiscardProjection` 按原版运行级派发检查不可变根前缀，任何覆盖均拒绝；战斗表仅允许精确已表示的类型／方法。只缓存 CLR 元数据，根值仍逐次检查。蛇之戒在当前玩家行动域不变，原始机甲 30 牌／31 监听器经过完整读取与原生差分；起手与后续回合没有因此迁移。

`Testing/CompactPhaseProbe.cs` 与 `UnattendedTestRunner.CompactKernelProfile.cs` 独占原型的阶段计量和新旧 solver 缓存对照，不进入生产 Search/Runtime。直接调用 Snapshot 的实验必须进入正式 `SolveCore` 使用的 `SimulationNotificationIsolation`，否则既有第三方空能力快速路径会旁路。该作用域使用线程静态状态，必须在 await 前和原生部署前退出；恢复后的模拟重新进入。诊断输出实际线程 CPU、独立墙钟和分配，内部既有 Snapshot 指标仍是嵌套墙钟；冻结候选不保留计量器、solver 或读取视图。

通用命令和 Hook 调用遇到 `PendingChoice` 时立即向上传播未完成状态，不再执行其后的监听器、抽牌、资源变更、死亡处理或卡牌收尾。Search 为待处理选择补齐计划后，从稳定父节点精确重放该动作，按原顺序通过挂起点；未完成事务不作为可继续执行的稳定 Fork。自动出牌将外层来源与上下文身份带入 `OnPlayWrapper`，在来源牌仍位于 Play 时消费嵌套选择，等待嵌套自动出牌结束后才移动来源牌和执行费用清理。原版挂起位置、顺序与卡牌实例身份属于模拟语义，不能由 Beam 或部署层补偿。

`CombatPredictionSimulator.CardPile.cs` 的抽牌安全边界只约束当前同步调用栈：抽牌 Hook 再次自动出牌、自动出牌又抽牌时，嵌套深度最多 `100` 层，继续嵌套会明确失败，不返回部分抽牌结果。深度在 `finally` 中退出；普通动作结束后、跨回合或从稳定边界 Fork 后继续抽牌，都不因已经累计的抽牌历史而减少合法抽牌。历史记录不再承担整个分支生命周期的 `100` 次抽牌额度，正常长线与有效循环仍受 Search 的节点、时间和调度预算约束。

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

有效 Power 的有序语义值直接进入搜索指纹，`ContinuationStamp` 的 `P` 字段按有效列表顺序输出，保留获得、移除和重新获得形成的 Hook 顺序；动态变量自身仍按无序键值集合比较。根捕获及分支监听表继续拥有顺序，指纹和续用只读取既有状态，不另设按阶段划分的顺序账本。

普通能力与多实例能力共用逐实例获得顺序表，Fork 通过同一 `PredictionForkContext.RequireRemap` 映射到子分支。重新获得已移除的普通能力时建立新实例，回合开始数量与内部状态由新实例初始化。新召唤友方归入敌方段之前，按原版友方/敌方顺序构造监听表。

## 6. UI

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

`SolverGrowthStrategyPanel` 拥有逐来源额外 HP 输入，原版八行之后按登记顺序追加 `GrowthSourceMirrors` 的第三方行（取牌函数抛异常时该行退化为无图标、标题显示 id 并记 warn，不连带面板失败），发布额度时把设置里尚未登记的 id 原样并回。与药水侧栏共享受视口约束的位置规则。“提前结束搜索的战损阈值”由 `SolverSettingsPanel.General` 管理，沿用 `AcceptableBattleHpLoss` 存储字段。两种面板在外部鼠标点击时释放其输入框焦点，沿用失焦提交；成长 SpinBox 显式应用待输入文本。成长面板在主线程读取卡牌图像与官方标题；`SolverController.SetGrowthPolicy` 只保存成长配置、废弃旧 continuation/完整路线比较基线，并在自动计算开启时重算。`SolverSettings`、路线缓存、问题包和战前 API 设置快照共同携带成长额度。

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
