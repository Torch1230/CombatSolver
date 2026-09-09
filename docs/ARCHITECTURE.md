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

`RitsuBaseLibTargetTypeLookupPatch` 属于 Runtime 的第三方适配：只在模拟隔离域缓存 Ritsu BaseLib 目标桥的静态程序集元数据查询回调。缓存以 Assembly 弱键持有准确 Type/缺失，不缓存框架整体缺失、注册表或目标谓词；保留外层扫描顺序，新程序集不复用旧条目，动态程序集与live调用旁路。它不持有分支状态，也不参与 Search 策略。具体回调必须由当前桥中唯一的 Assembly→Type 签名定位，适配不匹配显式失败。

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
| `CombatBeamSolver.Models.cs` | `SearchNode`、`SimulationSnapshot`、转置标签、`SearchFeatures`、单次运行 `SearchRunContext` |
| `CombatBeamSolver.Phases.cs` | `Solve`、阶段循环、总预算与回合层预算保留、当前回合预览、约 `100 ms` 刷新的动态推演路线，以及玩家采用路线/执行当前回合的收束检查点；动态路线显式携带战斗是否结束，未完成路线不产生整场战损数值 |
| `CombatBeamSolver.Expansion.cs` | 可执行卡牌/药水/结束回合候选展开和动作回放入口 |
| `CombatBeamSolver.ParallelExpansion.cs` | 固定 worker lane、父节点原始候选并发物化、自然单例父节点的有界 card action/target 回放、按输入顺序串行提交与快照所有权 |
| `CombatBeamSolver.PathDiagnostics.cs` | 可选路径观察的值复制与边界配对；分别记录生成、两类转置、实际展开、动作准入、完整保留及回合注释，不写搜索策略或账本 |
| `CombatBeamSolver.DeferredFrontier.cs` | 失败窄搜实验中的同回合落选元数据、有限队列与逐动作有预算回放；不持有独立 live/simulator 根，不重建调度账本 |
| `CombatBeamSolver.Retention.cs` | prune/retention 调用边界与相关小型辅助 |
| `CombatBeamSolver.BeamRetentionPolicy.cs` | 状态去重、中间分数排序、多样性通道、动作/回合开始选牌保路、药水配额和小型 Pareto |
| `CombatBeamSolver.CyclePlanning.cs` | 精确动作周期、通用收益与出口探针；按周期族和回合记账的有限观察与成长预算 |
| `CombatBeamSolver.CycleRegionRetention.cs` | 合并同回合、同控制形状的动作排列；对最终存活候选事务式提交区域保留预算与进展证据 |
| `CombatBeamSolver.OrderedMutationRetention.cs` | 有序操作碰撞的谱系、租约、成对激活和预算账本；统一处理续接、到期与普通通道回退 |
| `CombatBeamSolver.FinalPlanOrdering.cs` | 终局胜负、偷窃、战损、药水、卖血和搜索边界排序 |
| `CombatBeamSolver.StateEvaluation.cs` | 搜索快照、评分、威胁、stand-pat 和状态特征 |
| `CombatBeamSolver.Terminal.cs` | 终局精确回放、逐回合结果、击杀与遗物标注 |
| `StrategicEffectModel.cs` | 把 Power 的实际触发语义投影为伤害、防伤、资源、牌访问和成长效果；不决定终局胜负 |

`SearchRunContext` 只活于一次 solver：计数器、性能指标、节流器、转置表、stand-pat/威胁/coverage/路由缓存和 `OwnedExpansionBatch` 容器池均在这里。每个 lane 最多保留两个已清空 storage，单容器容量上限 4096；批次 lease 独立且 Dispose 幂等，检查点丢弃空闲池，不池化 simulator/model。根配置留在 solver，不把可变运行状态退回入口文件。 `SnapshotListBuffer<PredictedCard>` 也归各自 `_run` 所有，只缓存一个已清空、实际容量不超过 4096 的临时列表；快照内用栈上 lease，嵌套租用取独立 storage，generation 防止旧 lease 触碰新租户。牌序与 Shuffle RNG 克隆照旧，列表不得逃出 Snapshot，worker 排空后的缓存检查点丢弃空闲 storage。

循环调度另有三类不可重建账本，均由 `SearchRunContext` 持有并在内存检查点清理缓存后继续存活。`CycleFamily` 用回合、最小动作周期与规范动作序列识别同族，兄弟分支在相同动作深度共享已支付的观察工作，出口票据只展开一次；严格进展最多获得四级扩展，单族保留深度最多 `128`、出口探针展开最多 `256`，单个出口最多继续 `32` 个动作和两次回合转移。`CycleRegion` 不含精确动作排列，只按回合与控制形状合并组合爆炸；每区域普通保留为 `64–256`、探针保留为 `64–128`，同一回合还共享普通最多 `512`、探针最多 `256` 的总额度。进展可以扩展有限额度，不能通过制造新排列或新形状重置已消耗工作。区域进展续接仅以 `WeakReference<SearchNode>` 记录应匹配的直接父节点身份，候选自身强持有 `Parent`；每次更新新建且不再改写弱引用句柄，暂存账本与已提交账本不会互相修改目标，也不会由长期账本额外强持有旧节点链。这是所有权边界，不代表已实测的 GC 节约。

有序操作在无序结果相同但操作顺序不同时形成 `OrderedMutation` 租约。派生通道沿用碰撞根和初始通道身份：全 solver 最多 `2,048` 次有序保护准入、每层共享最多 `48`、每根基础 `128`、每初始通道基础 `64`、每派生租约 `16`；已有通道取得严格进展后，根和初始通道可分别使用一次 `64` 与 `32` 的有限尾部额度。不同碰撞根不再共享一个固定的“根个数”门槛，仍受实际保留工作总额约束。冷启动的两种顺序必须成对提交，失败不留下单边扣账；普通排名选中不等于已支付有序保护，自然入选的继承租约与额外候选进入同一个 `48` 额度服务队列，不能提前耗尽整层或提前到期。已有付费准入的同节点别名不重复占用服务；原有通用请求 `32`、成组服务 `16` 的保障份额和各原因预算不变，空余份额仍可按原规则借用。预算到期只取消调度特权，普通路线仍可参与后续保留。独立通道先选定，再结算有序操作，最后由区域事务按最终存活候选提交预算；被后续裁决淘汰的候选不能赚取进展或占用已提交额度。

普通搜索按进程可用逻辑处理器数量选择初始展开 lane：至少 4 个时默认 DOP4，2–3 个时默认 DOP2，只有 1 个时使用 DOP1；用户显式设置始终优先。设置中的“关闭（单线程）”映射 DOP1，数值项为 `2..16`，实际值还会按进程可用逻辑处理器钳制。coordinator 自己执行 lane 0，其余低优先级后台 lane 在一次 `Solve` 内复用 solver、缓存和 `SearchWorkPacer`。worker 不写全局 transposition、dominance 或 fallback：它们只物化原始候选，coordinator 仍按父节点输入顺序提交，因此固定节点预算下 DOP 不改变搜索语义。详细诊断和增量严格回放强制 DOP1。

父节点外层 wave 不按手牌数强制拆成 singleton。系统余量受限时从最多 2 个父节点开始，其他情况从配置 DOP 开始；已完成的 multi-parent wave 未超出预约时容量倍增，超出预约时容量减半，在 `2..DOP` 内动态调整，singleton 不会替尚未观测的宽 wave 提前放大容量。Runtime 把玩家配置视为区域上限，并按 CLR 高内存阈值的 `95%` 安全线动态缩小实际 NoGC 申请；安全准入同时使用本轮分配余量和“区域建立时系统内存负载 + 本轮分配”的预测余量。全搜索已观测的最坏父节点分配量另加 `1.5×` 余量，并为 wave 中每个并发父节点完整预约。`SearchWaveMemoryPolicy` 先按实际剩余容量缩小 wave（包括 3、1 个父节点）；只有单个 parent 也不能预约时，才在已提交边界释放可重建缓存并回收。出牌深度结束先完成剪枝，清空被剪节点容器且仍有下一次准入时再检查回收；连单个父节点都无法放入预约时退回纯串行，不借 inner replay 冒险。CLR 仅因区域尺寸不受支持而拒绝 NoGC 时，Runtime 逐次减半申请，最低尝试 `512 MB`；平台不支持或区域尺寸仍无法建立时回退常规 GC，并继续按用户配置的 DOP 搜索。只有系统余量不足时才启用最多两个 lane 的保守并发限制。用户主动关闭 NoGC 时同样不施加该限制。自然只剩一个且预约可容纳的并行父节点时，才借用同一组空闲 lane 并发执行该父节点的 card action/target 初始 probe，不与外层并发嵌套。

某个 action 到达 `PendingChoice` 时，worker 只移交该 probe 的唯一所有权；action wave 全部到达 barrier 后，coordinator 按原 action index 构造 direct primary、Knowledge Demon 或 TurnStart/nested 的选择层，串行穿过宽度一的层，并把首个宽度至少二的可独立 frontier 独占调度到同一组 lane。direct-primary 的有限下游配额属于各自独立分支，可以并行；在 primary 之前已经出现 PendingChoice 且带有限共享配额的层仍保持原序串行。各 lane 从 coordinator 串行准备的 parent Fork seed 完整回放 resolved action，再串行处理后续选择；NoGC 剩余预算不足或只剩一个分支时也保持原序串行。结果、异常和提交均按 action index、再按 choice branch index 合并。NoGC 冷启动微批最多两个 outcome；round-choice 后续容量按单 outcome 分配高水位的至少 `1.5×` 安全余量计算，内部不建立 GC checkpoint。

并行搜索失败提示会保留本次请求的 DOP；DOP 大于 1 时先引导上传问题包，再建议切换为“关闭（单线程）”。并行阶段指标为各 lane 的累计 CPU 时间，可以超过墙钟耗时；`parallel_waves / work_items / max_concurrency`、`parallel_action_*` 与 `parallel_round_choice_*` 分别证明父节点、自然 singleton action 和宽选择层并发实际发生，`deferred_round_choice_*` 记录命中层宽与有限配额回退。外层 wave 按输入序号等待已完成的连续前缀，立即提交并释放它的 raw batch 和父模拟器，后续 lane 可以继续运行；异常仍先排空全部已派发工作再退出。单个 parent 内 action/round-choice aggregate 仍可能持有完整原始候选；高于默认值属于用户主动的速度、CPU 与峰值内存权衡。节点预算截断时，coordinator 立即释放未展开父节点和不会进入下一层的候选模拟器，并用 `node_limit_snapshots_released` 记录实际释放数。

`BeamRetentionPolicy` 决定哪些中间候选继续活着；动作选牌、嵌套选牌和 `EndTurn.TurnStartChoices` 都以来源、效果、卡牌语义状态和上下文形成保路签名。`FinalPlanOrdering` 决定完整候选中最终采用哪条；完整胜利后比较扣除实际成长额度的战略战损，再比较已实现成长额度、收益次数和结束回合，药水、其他长线资源、敌方状态和分数作为后续尾键。两者不能合并成单一“总分排序”。`SearchFeatures` 是终局排序读取节点状态的只读投影。转置状态键中的九条战斗 RNG 必须包含完整内部状态；相同调用计数不能证明两个 RNG 后续等价。

`GrowthPolicy.cs` 定义八类局外收益的不可变额度/次数向量，另带 `GrowthExtras` 承载第三方来源那一半（按 id 序数升序、不存 0 值，空表为 `null`，登记表为空时行为与指纹与开这个口子之前逐位相同）。`GrowthSourceMirrors.cs` 是第三方登记表，只持有 id、延迟取牌函数与牌组判据；额度按 id 持久化，认不出的 id 原样保留并写回。Runtime 在主线程冻结额度及当前牌组是否存在成长目标，交给 `SearchPolicySnapshot`；求解器在快照中计算额度，最终排序、阶段仲裁与 Pareto 保留共同消费。`SimulatedCombatState.LongTermResources` 只记录既有结算点产生的成功收益次数，Fork 按值复制，状态指纹保留各来源计数；额度属于搜索政策。计数从每个新根的零值开始，预测分支跨回合保留；续用仍核对原来的金币、最大生命和卡牌永久变量，计数本身不进入 live `ContinuationStamp`。有目标或非零配置时停用纯 HP 的提前终止和 incumbent 下界，沿用原时间/节点预算。

「不考虑局外收益」开关由 SearchPolicySnapshot 的 EffectiveGrowthBudgets / EffectiveHasGrowthTargets 统一解释；开启后所有原版和第三方额度行置灰，原始配置保留。

跨回合例外保路以各真实“直接 `EndTurn`”分支形成的 stand-pat Pareto 质量集为相对基准，不以绝对零进展或合成的逐坐标基线判定。候选一旦在通用质量向量上离开被 stand-pat 支配的区域，就退出例外探针；观察期、探针和保留数都有固定硬上限，且该上限不能被中途普通进展重置，从而让延迟收益有界探测、真正停滞不无限续期。

卡牌候选在进入 Beam 前按即时防御、即时输出、资源循环、持续成长、控制、目标移除和生命投资建立有上限的组合覆盖，剩余名额继续按主分数填充。多次弃牌选择按整张牌而非单次弹窗共用分支预算，并保留弃牌触发、状态/诅咒清理、保留牌与牌堆取舍代表。持续 Power 的中间价值来自 `StrategicEffectModel` 对可达触发次数和当前威胁的投影；同回合减费/过牌组合另以当前资源可打出的手牌价值和零费可执行牌数保留一个战术启动代表。用药分支按已用数量和具体药水身份分别保留有上限的代表。这些投影只参与展开与保路，不进入战斗状态键，也不替代最终实际战损。

### 搜索目标

`SearchObjective.cs` 持有纯值的四种目标、累计战损/结束血量约束、路线培养点数目标与只读收益结果。平衡沿用逐次成长的 HP 额度；保命取消成长信用；永久培养与净收益先比较完整胜利、约束满足情况、目标值，再比较战损。纯 HP 提前停止和 incumbent 在收益目标下关闭，Smart 用药梯度按目标改善选取且不在第一条省血路线处结束。原有禁药/指定药水规则继续约束合法动作。

`SimulatedCombatState.LongTermResources` 在既有四个结算入口记录最大生命、永久伤害、永久格挡的实际增量，独立于旧触发次数；Fork 按值复制，历史收益进入状态指纹，根从零开始。金币与药水库存基准由主线程 `CombatRootSnapshot` 冻结，Snapshot 只读取分支当前值；净收益分包含已获得与已知待结算金币、额外选牌奖励估值、药水库存估值差和一次性保命消耗，排除普通随机战后奖励。没有增加 live `ContinuationStamp` 的派生评分字段。

收益结果随 SimulationSnapshot、最终快照与临时结果传播，终局选择、Beam 保路、Pareto 和跨搜索比较共享目标前缀。培养上限按本次搜索根的实际成长点数饱和；重新搜索重新计量。未证明满足限制的完整胜利时停止全自动，保留人工可查看的回退路线。`SolverGrowthStrategyPanel` 编辑策略；`SearchObjectiveText` 仅把结果投影成中英文说明。设置、问题包、回放和路线缓存携带目标配置，改变目标会废弃旧 continuation。

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

`EffectivePowers` 保留已知敌人尚待完成死亡结算的能力；普通 `ICombatState` / `ICombatPredictionHookListenerSource` 回调使用活动监听视图，排除所有者已离场的 Power。两种视图共用既有根与分支能力实例，活动视图随阵容和能力缓存失效，不清空死亡补偿所需的数据。

## 4. 内嵌模拟引擎

### 4.1 基础层

`src/Engine/InCombat/Simulation/` 负责通用战斗命令时序、伤害、牌堆、历史、RNG、球和 Fork。它不包含单张卡、单个 Power 或具体怪物的搜索策略。历史卡牌 Started/Finished 与 DamageReceived 的卡牌来源使用不可变卡牌快照；当前动作是否开始以精确 trace-frame 身份判定，保留原生 `CardPlay` 身份，不以 Original 卡牌身份合并兄弟分支。`CombatPredictionHistory` 以不可变 prefix segment + 分支本地 mutable tail 保存事件；动作后缀消费者必须使用冻结上界的 `EntriesFrom/EntriesBetween`，不能先遍历完整 prefix 再 `Skip`，否则长线会把一次局部查询放大为随深度增长的重复工作。

`src/Engine/Common/` 提供 `PredictedCard`、`PredictionForkContext`、`PredictionStateStore` 和通用模型克隆。StateStore 直接持有可 Fork 的 state，空字典按需创建；仍在同一 context 中按原跨类型顺序 eager Fork，不能对调用者已借出的可变引用使用通用延迟 COW。一次 Fork 内的所有结构必须共享同一个 context；分支可变对象必须显式重映射。`BaseLibCloneConcurrency` 是原版与预测克隆共用的外部扩展并发边界，只包围模型深克隆阶段。

`CombatPredictionSimulator.TerminalStamp` 在与原版对应的完整动作/阶段安全检查点首次锁定胜负及影子玩家回合号，按值 Fork；`IsEnding` 仍是无副作用查询，不在单个 Hook 监听器之间提前终止正在结算的序列。`SimulationSnapshot` 独立保留此值，释放模拟器后，终局标注、临时结果、最终排序和已知胜利上界仍读取同一时点。`PlanAction.Turn` 只表示发起动作的回合，不能代表该动作跨回合结算后的终局回合。

通用命令和 Hook 调用遇到 `PendingChoice` 时立即向上传播未完成状态，不再执行其后的监听器、抽牌、资源变更、死亡处理或卡牌收尾。Search 为待处理选择补齐计划后，从稳定父节点精确重放该动作，按原顺序通过挂起点；未完成事务不作为可继续执行的稳定 Fork。自动出牌将外层来源与上下文身份带入 `OnPlayWrapper`，在来源牌仍位于 Play 时消费嵌套选择，等待嵌套自动出牌结束后才移动来源牌和执行费用清理。原版挂起位置、顺序与卡牌实例身份属于模拟语义，不能由 Beam 或部署层补偿。

`CombatPredictionSimulator.CardPile.cs` 的抽牌安全边界只约束当前同步调用栈：抽牌 Hook 再次自动出牌、自动出牌又抽牌时，嵌套深度最多 `100` 层，继续嵌套会明确失败，不返回部分抽牌结果。深度在 `finally` 中退出；普通动作结束后、跨回合或从稳定边界 Fork 后继续抽牌，都不因已经累计的抽牌历史而减少合法抽牌。历史记录不再承担整个分支生命周期的 `100` 次抽牌额度，正常长线与有效循环仍受 Search 的节点、时间和调度预算约束。

### 4.2 Mirror

> 面向外部 Mod 作者的登记点总表、登记纪律与验收标准见
> [第三方 Mod 适配手册](THIRD_PARTY_ADAPTERS.md)。

`src/Engine/InCombat/Mirrors/` 精确实现原版 Hook、卡牌、药水、附魔和球方法。Facade 保持原版调用时序，registry 按运行时类型与方法分派。

苦无、手里剑和彩虹戒指的属性施加在各自 `AfterCardPlayed` 镜像内完成：在原版 `IsInProgress` 门内更新计数，按每次 `PowerCmd.Apply` 的 `IsEnding` 门决定是否施加，不能延到其他监听器之后。彩虹戒指的领域生命周期仅同步既有激活投影，不再施加属性；末击不会提前中断整组监听器。

`MethodMirrorRegistry` 同时实现 `IMethodMirrorRegistryDescriptorProvider`。`MethodMirrorRegistryDescriptor` 描述基础方法、receiver、显式 Handled/Ignored 注册和当前 inferrer；CoverageCatalog 只消费该描述符，不读取 registry 私有字段或 `MirrorMethodSpec` 内部布局。

## 5. Prediction 领域补偿

`src/Prediction/` 处理基础命令和单个 mirror 不能独立表达的领域语义：

- 卡牌/Power/遗物/药水/球的跨 Hook 生命周期；
- 怪物行动图、随机分支、私有 AI 与召唤；
- 死亡、复活、自动出牌和嵌套选牌；
- 第三方 ModHook subscriber 的主线程捕获与分支重建；
- 覆盖分类和动态状态字段政策。

这里可以保存具体领域规则，但不能决定 Beam 配额、最终路线或 UI 显示。新增补偿前检查 mirror、spec、support 和 `SimulatedCombatState` 的完整调用链，确保只有一个权威结算点。

`PlayerTurnEndLifecycle.RunPhaseTwo` 拥有清空手牌后的玩家回合末补偿顺序：常规 Power、遗物、晚期 Power，最后规范化卡牌词条。Search、风险预估和无人差分共用此入口；每个阶段的挂起选择立即向上传播。`CorePowerSupport.TriggerPlayerRegularSideTurnEndEffects` 仅承担常规 Power 阶段，晚期伤害在遗物之后结算。

有效 Power 的有序语义值直接进入搜索指纹，`ContinuationStamp` 的 `P` 字段按有效列表顺序输出，保留获得、移除和重新获得形成的 Hook 顺序；动态变量自身仍按无序键值集合比较。根捕获及分支监听表继续拥有顺序，指纹和续用只读取既有状态，不另设按阶段划分的顺序账本。

普通能力与多实例能力共用逐实例获得顺序表，Fork 通过同一 `PredictionForkContext.RequireRemap` 映射到子分支。重新获得已移除的普通能力时建立新实例，回合开始数量与内部状态由新实例初始化。新召唤友方归入敌方段之前，按原版友方/敌方顺序构造监听表。

## 奖励与商店建议

`src/Advice/RunAdvice.cs` 只接收不可变的值输入，输出边际收益排序、可用性、置信度与理由；不依赖搜索模拟器、RNG 或 UI。`RunAdviceCapture` 在主线程读取当前玩家与 MerchantEntry，保留当下价格和移除候选身份，不执行购买。`Runtime/RunAdvicePatches` 负责战斗外单人页面准入、原生奖励刷新与商店购买完成后刷新；`UI/RunAdviceBadge` 负责本地化、父节点内标签与生命周期。该建议不进入 CombatRootSnapshot、状态指纹、搜索预算或自动部署。

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
