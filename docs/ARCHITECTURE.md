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

## 2. Runtime

| 文件 | 职责 | 不负责 |
|---|---|---|
| `src/Runtime/Entry.cs` | Mod 初始化、补丁安装、战斗与回合生命周期入口、无人请求循环启动 | 搜索策略和战斗语义 |
| `src/Runtime/SolverController.cs` | 主线程搜索/续用/部署/全自动编排，结果过期与重算审计 | Beam 内部算法和 UI 布局 |
| `src/Runtime/SolverControllerSessions.cs` | combat/search/deployment 三类会话的状态与取消所有权 | 跨会话全局静态字段堆积 |
| `src/Runtime/CombatRootSnapshot.cs` | 主线程捕获完整预测根，比较捕获前后 live 状态，并向 worker 提供 Fork 根 | worker 惰性读取 live 战斗 |
| `src/Runtime/ContinuationStamp.cs` | 跨回合 live/predicted 状态文本、首个差异与完整差异；九条战斗 RNG 使用计数器与四段内部状态共同核对 | Beam 状态去重 |
| `src/Runtime/BaseLibCloneConcurrencyPatch.cs` | BaseLib 克隆扩展存在时，让原版 `MutableClone` 与内嵌模拟的模型深克隆共用窄串行边界，保护其全局弱表 | 整段搜索串行化、BaseLib 业务语义与候选政策 |
| `src/Runtime/PowerDynamicVarWarmup.cs` | 主线程根捕获时物化规范 Power 与当前战斗 Power 的显示变量 | 搜索评分、Power 语义与 worker 本地化 |
| `src/Runtime/PowerDynamicVarMaterializationGuardPatch.cs` | 搜索模拟惰性创建 Power 显示变量时立即报告根捕获缺失 | Power 语义、显示内容与搜索阶段串行化 |
| `src/Runtime/SearchGcPolicy.cs` | 管理玩家显式开关的进程级 GC 模式：开启时按原样预算建立战斗级 NoGC、执行搜索内安全检查点与引用释放后的压力回收；稳定关闭时使用 CLR 常规分代 GC 且不新增自动补账压力，从开启切换时仍结清此前义务；模式切换和手动释放与活动搜索计数共用安全边界 | Beam 剪枝、候选评分、模拟语义与同步阻塞 UI |
| `src/Runtime/ProcessWorkingSetTrimmer.cs` | Windows 手动释放在托管堆压缩后修剪当前游戏进程工作集 | GC 生命周期、搜索调度与自动触发 |
| `src/Runtime/SystemMemoryReleaseService.cs` | 等待当前进程回收完成，再通过 UAC 启动短命辅助程序清空系统工作集与待机列表 | 自动触发、修改页列表清理与搜索策略 |
| `src/Runtime/SearchMemoryPressureSignal.cs` | 将 Runtime 的进程分配边界、回收入口和低系统余量下的保守并行标记注入搜索；不让 Search 直接操作 GC 模式 | 设置读取与搜索评分 |
| `src/Runtime/SolverControllerSessions.cs` | 除会话状态外，向 UI 提供当前进程占用与活动搜索分配检查点的只读快照 | UI 样式与搜索内存政策 |
| `src/Runtime/SolverSettings.cs` | 持久化性能、执行、搜索并行度、NoGC 开关与独立预算、逐槽药水策略和搜索结束通知设置，并在主线程捕获不可变搜索 snapshot | 搜索期读取全局设置 |
| `src/Runtime/PlayerTurnSetupPatches.cs` | 首回合原生页面出现后的 Start 根搜索；后续回合观察上一轮 `EndTurn.TurnStartChoices` 的原生页面，全自动直接可见重放，单步默认交还玩家并允许执行/全自动入口接管既有选择；进入 Play 后交给 continuation 核对；跨 Reset 的 Setup/部署延迟由 lifecycle token 取消 | 普通 Play 阶段搜索与动作部署 |
| `src/Runtime/NativeChoiceRuntime.cs` | 观测原版战斗选择请求，按卡牌语义状态匹配计划实例，并锁定、驱动真实页面控件 | 选择分支枚举和战斗结算 |
| `src/Runtime/CombatBugReportExporter.cs` | 主线程冻结当前/最近战斗的实机取证状态；单消费者后台 FIFO 按检查点顺序整理并一次序列化为 UTF-8 字节，导出任务作为队列屏障等待此前记录完成 | 后台读取 live 战斗、通用 replay/native-state 导入 |
| `src/Runtime/CombatBugReportDescription.cs` | 汇总本场结构化异常、重算和战损信号，并把自动分类附到玩家问题描述后 | 后端字段、问题包内容和搜索决策 |
| `src/Runtime/CombatBugReportUploader.cs` | 通过不继承游戏进程代理的专用客户端直连接收服务；校验问题包与文本上限，以 multipart 流式上传并传播取消，限制服务端响应，并以反馈编号和实收字节数确认完整接收 | 问题包内容生成、隐私脱敏、UI 单实例与确认流程 |
| `src/Runtime/SearchCompletionNotifier.cs` | 搜索成功、失败、停止或过期后按设置决定是否通知；Windows 使用原生通知和系统提示音，先核对前台进程并在非 Windows/headless 环境停止 | 搜索生命周期、跨平台伪通知和自定义声音播放 |

`SolverCombatSession` 持有本场路线、续用和重算状态；`SolverSearchSession` 持有 generation、取消、进度和帧观测；`SolverDeploymentSession` 持有部署取消。旧回调只能写回创建它的 search session。

`SearchGcPolicy` 将活动搜索期间收到的后台回收请求保存在独立的 deferred 完成链中，所有搜索退出后才提升为实际后台回收。搜索内内存检查点只等待自己能够完成的回收，不能等待以该搜索退出为前提的任务；手动工作集释放继续等待搜索后的回收链。已覆盖的取消及 GC 转换后注入失败路径会协调 CLR 实际模式与内部所有权，并落定对应完成链、释放等待屏障；这些断言不穷举 CLR 转换前失败、OOM 或日志系统异常。搜索账本的存活与运行时 GC 模式互不混用。

搜索内检查点在 Gate 外直接等待非压缩后台 Gen2 primitive，不加入上述 deferred 链。primitive 先观察最新已完成 Gen2 的 index 与新 LOH 弱哨兵，仅在上一轮已完成却未覆盖哨兵时再次请求；不按定时器盲重发。全部异步等待不捕获调用方上下文。已发出的回收不能随搜索取消而放弃：确认完成后取消才落到默认 GC，超时或确认异常先显式阻塞排空，不能提前重建 NoGC。回收开始前的手动 GC 有独立完成信号，回收确认成功但搜索取消/超时不使它误报失败；开始后的手动请求与新的引用释放义务继续等待后续安全回收。日志分开记录请求模式、实际完成类型/index、CLR Concurrent 标志及阻塞超时兜底，不承诺每次都采用并发 GC 或没有暂停。

## 3. Search

### 3.1 请求级编排

- `SearchPolicySnapshot.cs`：主线程捕获的不可变搜索设置、逐槽药水策略，以及第一/二幕与最终 Boss 各自的血量取舍；后台不读取 UI 或玩家设置。
- `SearchDiagnosticsSink.cs`：搜索日志和可选纯值路径观察出口。观察默认关闭，先按状态键过滤，命中后才复制完整动作/选择路径与政策标签；另可显式筛选外层 Prune 池，记录完整输入、真实 RankBest 的原排名/必保/路由/选中索引、当时的战术估值标量及最终仲裁集合。RankBest 内部同步借用列表，立即转成值副本；不向注入方暴露节点、模拟器或闭包，不重算估值或选择器，也不参与候选裁决。注入方负责并发和输出容量。
- `SearchFramePressureSignal.cs`：Runtime 向 worker 提供的帧压力信号；以最近 `31` 个非搜索帧中位数建立基线，压力阈值为 `max(33 ms, baseline × 1.5)`，无显示服务的 headless 请求旁路帧恢复等待。
- `SearchRequestWorkTotals.cs`：一次请求内所有正常、失败和取消 solver 的工作区间均精确记账一次，包括取消前已发生的展开、转移、选牌、耗时、分配和 GC；Smart 有限药水层之间由 coordinator 主动执行的内存整理也单独计入耗时、分配和 GC，但不伪装成额外 solver。请求总值不是完整 coordinator 外层墙钟或进程峰值，也不承担结果质量排序。
- `CombatSearchCoordinator.cs`：一次请求的搜索编排；Smart 先搜索无药基线，再根据可用药水、无药战损和药水价值门槛确定最多进入的“恰好 `N` 瓶”层。按瓶数递增搜索，同层药水共同竞争；第一层完整获胜且满足救命、节省生命或保全被盗资源条件时立即采用并停止增加药量。达到设置的可接受战损阈值也可提前结束请求，不保证遍历全部药水层或取得所有药量中的全局最优。进入下一梯度前回收上一层搜索图并重建 NoGC 区域；截止时保留已完成且符合政策的选择。跨 solver 只发布符合政策的严格改善完整路线，并透传当前 solver 已完成回合的候选。玩家可采用已显示路线或只执行当前回合。Disabled/RequireAtLeastOne 保持各自政策；实际运行的各层共享请求级时间余量并合并总指标。
- `CombatPlan.cs`：Runtime 消费的计划、结果和续用数据。结果不得保留历史 Simulator 对象图。

`CombatSearchCoordinator.FailureRecovery.cs` 为没有完整胜利且未耗尽预算的主搜索或 Smart 精确药水层提供一次标准窄宽度恢复。两次搜索共用原层节点和时间上限，药水约束与同一个请求截止信号保持一致；失败和取消的工作仍由 `SearchRequestWorkTotals` 精确计入。已有胜利、玩家接管、预算耗尽或原配置已不宽于标准值时直接返回。该机制缓解 Beam 宽度的非单调性，不提供完备性或“高档必然优于所有低档”的保证。

周期候选在最多 32 步的窗口内比较重复动作、控制形状及伤害发生相位，避免把较长周期中的安静阶段当成整个循环。每周期伤害数值可以变化：动作、形状和伤害相位重复且实际刷新敌人耐久低点时，可取得伤害进展证据；精确转移增量是否一致仍单独记录，不把增长伤害伪装成相同增量。已证明刷新逐敌人历史最低耐久的路线可使用独立进展通道：每个 region 每层至多一个代表，最多保留该周期余下的 31 个安静动作，且只由实际保留节点的一个直接后代消费。只有新的最低耐久能续期；普通停滞、试探和顺序选择预算不因此重置。进展准入在最终仲裁后结算，并解除已经完成目标的旧出口探针；所有动作仍逐步模拟并受请求节点与时间限制。

主 incumbent 只能由满足硬政策的完整胜利建立。无主动用药入口要求实际生效政策为 `Disabled` 或 `Smart`、最少用药数为0、候选显式用药数为0；若启用逐槽指令，还必须实际满足全部强制使用要求。正数精确药水层保留原条件：最少与最多药量相等、有已审计无药基线、未启用需另证的逐槽强制指令，且完整胜利严格改善基线主质量。未完成路线、死亡路线或仅满足中间评分的候选不能建界。

完整胜利的界为累计 HP 损失加最终最大生命缺口 `max(0, 根MaxHP - 最终MaxHP)`，并记录结束回合。未完成分支的下界只用累计 HP 损失和当前回合：损失严格大于该完整界，或损失相等但当前回合严格更晚时，才允许剪枝；同损同回合与低损晚回合仍保留。中间最大生命缺口可能恢复，不能加入未完成下界，评分、敌人血量或语义投影也不能替代它。诊断日志以 `source=no_explicit_potion` 或 `source=exact_potion_layer` 区分建界来源。

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

`SearchRunContext` 只活于一次 solver：计数器、性能指标、节流器、转置表和 stand-pat/威胁/coverage/路由缓存均在这里。根配置留在 solver，不把可变运行状态退回入口文件。

循环调度另有三类不可重建账本，均由 `SearchRunContext` 持有并在内存检查点清理缓存后继续存活。`CycleFamily` 用回合、最小动作周期与规范动作序列识别同族，兄弟分支在相同动作深度共享已支付的观察工作，出口票据只展开一次；严格进展最多获得四级扩展，单族保留深度最多 `128`、出口探针展开最多 `256`，单个出口最多继续 `32` 个动作和两次回合转移。`CycleRegion` 不含精确动作排列，只按回合与控制形状合并组合爆炸；每区域普通保留为 `64–256`、探针保留为 `64–128`，同一回合还共享普通最多 `512`、探针最多 `256` 的总额度。进展可以扩展有限额度，不能通过制造新排列或新形状重置已消耗工作。区域进展续接仅以 `WeakReference<SearchNode>` 记录应匹配的直接父节点身份，候选自身强持有 `Parent`；每次更新新建且不再改写弱引用句柄，暂存账本与已提交账本不会互相修改目标，也不会由长期账本额外强持有旧节点链。这是所有权边界，不代表已实测的 GC 节约。

有序操作在无序结果相同但操作顺序不同时形成 `OrderedMutation` 租约。派生通道沿用碰撞根和初始通道身份：全 solver 最多 `2,048` 次有序保护准入、每层共享最多 `48`、每根基础 `128`、每初始通道基础 `64`、每派生租约 `16`；已有通道取得严格进展后，根和初始通道可分别使用一次 `64` 与 `32` 的有限尾部额度。不同碰撞根不再共享一个固定的“根个数”门槛，仍受实际保留工作总额约束。冷启动的两种顺序必须成对提交，失败不留下单边扣账；普通排名选中不等于已支付有序保护，自然入选的继承租约与额外候选进入同一个 `48` 额度服务队列，不能提前耗尽整层或提前到期。已有付费准入的同节点别名不重复占用服务；原有通用请求 `32`、成组服务 `16` 的保障份额和各原因预算不变，空余份额仍可按原规则借用。预算到期只取消调度特权，普通路线仍可参与后续保留。独立通道先选定，再结算有序操作，最后由区域事务按最终存活候选提交预算；被后续裁决淘汰的候选不能赚取进展或占用已提交额度。

普通搜索按进程可用逻辑处理器数量选择初始展开 lane：至少 4 个时默认 DOP4，2–3 个时默认 DOP2，只有 1 个时使用 DOP1；用户显式设置始终优先。设置中的“关闭（单线程）”映射 DOP1，数值项为 `2..16`，实际值还会按进程可用逻辑处理器钳制。coordinator 自己执行 lane 0，其余低优先级后台 lane 在一次 `Solve` 内复用 solver、缓存和 `SearchWorkPacer`。worker 不写全局 transposition、dominance 或 fallback：它们只物化原始候选，coordinator 仍按父节点输入顺序提交，因此固定节点预算下 DOP 不改变搜索语义。详细诊断和增量严格回放强制 DOP1。

父节点外层 wave 不按手牌数强制拆成 singleton。NoGC 开启时从 2 个父节点开始；已完成的 multi-parent wave 未超出预约时容量倍增，超出预约时容量减半，在 `2..DOP` 内动态调整，singleton 不会替尚未观测的宽 wave 提前放大容量。Runtime 把玩家配置视为区域上限，并按 CLR 高内存阈值的 `95%` 安全线动态缩小实际 NoGC 申请；安全准入同时使用本轮分配余量和“区域建立时系统内存负载 + 本轮分配”的预测余量。全搜索已观测的最坏父节点分配量另加 `1.5×` 余量，并为 wave 中每个并发父节点完整预约。任一余量不足就在已提交边界释放可重建缓存、退出 NoGC、回收并按新系统余量建立区域；连单个父节点都无法放入预约时退回纯串行，不借 inner replay 冒险。CLR 仅因区域尺寸不受支持而拒绝 NoGC 时，Runtime 逐次减半申请，最低尝试 `512 MB`；平台不支持或区域尺寸仍无法建立时回退常规 GC，并继续按用户配置的 DOP 搜索。只有系统余量不足时才启用最多两个 lane 的保守并发限制。用户主动关闭 NoGC 时同样不施加该限制。自然只剩一个且预约可容纳的并行父节点时，才借用同一组空闲 lane 并发执行该父节点的 card action/target 初始 probe，不与外层并发嵌套。

某个 action 到达 `PendingChoice` 时，worker 只移交该 probe 的唯一所有权；action wave 全部到达 barrier 后，coordinator 按原 action index 构造 direct primary、Knowledge Demon 或 TurnStart/nested 的选择层，串行穿过宽度一的层，并把首个宽度至少二的可独立 frontier 独占调度到同一组 lane。direct-primary 的有限下游配额属于各自独立分支，可以并行；在 primary 之前已经出现 PendingChoice 且带有限共享配额的层仍保持原序串行。各 lane 从 coordinator 串行准备的 parent Fork seed 完整回放 resolved action，再串行处理后续选择；NoGC 剩余预算不足或只剩一个分支时也保持原序串行。结果、异常和提交均按 action index、再按 choice branch index 合并。NoGC 冷启动微批最多两个 outcome；round-choice 后续容量按单 outcome 分配高水位的至少 `1.5×` 安全余量计算，内部不建立 GC checkpoint。

并行搜索失败提示会保留本次请求的 DOP；DOP 大于 1 时先引导上传问题包，再建议切换为“关闭（单线程）”。并行阶段指标为各 lane 的累计 CPU 时间，可以超过墙钟耗时；`parallel_waves / work_items / max_concurrency`、`parallel_action_*` 与 `parallel_round_choice_*` 分别证明父节点、自然 singleton action 和宽选择层并发实际发生，`deferred_round_choice_*` 记录命中层宽与有限配额回退。一个 wave 会在提交前同时持有至多 DOP 个父节点、一个 action 微批或一个 round-choice 微批的原始候选快照；高于默认值属于用户主动的速度、CPU 与峰值内存权衡。节点预算截断时，coordinator 立即释放未展开父节点和不会进入下一层的候选模拟器，并用 `node_limit_snapshots_released` 记录实际释放数。

`BeamRetentionPolicy` 决定哪些中间候选继续活着；动作选牌、嵌套选牌和 `EndTurn.TurnStartChoices` 都以来源、效果、卡牌语义状态和上下文形成保路签名。`FinalPlanOrdering` 决定完整候选中最终采用哪条；完整胜利后先比较战略战损，战损相同立即比较结束回合，药水、长线资源、敌方状态和分数只能作为后续尾键。两者不能合并成单一“总分排序”。`SearchFeatures` 是终局排序读取节点状态的只读投影。转置状态键中的九条战斗 RNG 必须包含完整内部状态；相同调用计数不能证明两个 RNG 后续等价。

跨回合例外保路以各真实“直接 `EndTurn`”分支形成的 stand-pat Pareto 质量集为相对基准，不以绝对零进展或合成的逐坐标基线判定。候选一旦在通用质量向量上离开被 stand-pat 支配的区域，就退出例外探针；观察期、探针和保留数都有固定硬上限，且该上限不能被中途普通进展重置，从而让延迟收益有界探测、真正停滞不无限续期。

卡牌候选在进入 Beam 前按即时防御、即时输出、资源循环、持续成长、控制、目标移除和生命投资建立有上限的组合覆盖，剩余名额继续按主分数填充。多次弃牌选择按整张牌而非单次弹窗共用分支预算，并保留弃牌触发、状态/诅咒清理、保留牌与牌堆取舍代表。持续 Power 的中间价值来自 `StrategicEffectModel` 对可达触发次数和当前威胁的投影；同回合减费/过牌组合另以当前资源可打出的手牌价值和零费可执行牌数保留一个战术启动代表。用药分支按已用数量和具体药水身份分别保留有上限的代表。这些投影只参与展开与保路，不进入战斗状态键，也不替代最终实际战损。

### 3.3 分支战斗状态

`SimulatedCombatState*.cs` 把内嵌引擎状态适配为搜索所需的战斗领域视图：

- `Fork.cs`：统一稳定边界和对象图复制；
- `MonsterAi.cs` / `MonsterState.cs`：分支行动、私有 AI、已知怪物静态值；
- `DeathLifecycle.cs`：死亡、复活与阵容事务；
- `ActionChoices.cs` / `TurnStartChoices.cs` / `AutoPlay.cs`：嵌套选择与自动出牌；
- `CardLifecycle.cs` / `CardPowerHistory.cs` / `PowerLifecycle.cs`：卡牌和 Power 跨事件状态；
- `Relics.cs`、`PowerRelics.cs`、`ReactiveRelics.cs` 等：遗物与组合事务；
- `Potions.cs`：药水槽和使用状态。

活动 roster 只决定当前可行动、可选目标和 listener。已经捕获的怪物 AI/静态参数属于已知怪物和分支生命周期，不能在移出活动 roster 时提前删除。

## 4. 内嵌模拟引擎

### 4.1 基础层

`src/Engine/InCombat/Simulation/` 负责通用战斗命令时序、伤害、牌堆、历史、RNG、球和 Fork。它不包含单张卡、单个 Power 或具体怪物的搜索策略。`CombatPredictionHistory` 以不可变 prefix segment + 分支本地 mutable tail 保存事件；动作后缀消费者必须使用冻结上界的 `EntriesFrom/EntriesBetween`，不能先遍历完整 prefix 再 `Skip`，否则长线会把一次局部查询放大为随深度增长的重复工作。

`CombatPredictionSimulator.TerminalStamp` 在与原版对应的完整动作/阶段安全检查点首次锁定胜负及影子玩家回合号，按值 Fork；`IsEnding` 仍是无副作用查询，不在单个 Hook 监听器之间提前终止正在结算的序列。`SimulationSnapshot` 独立保留此值，释放模拟器后，终局标注、临时结果、最终排序和已知胜利上界仍读取同一时点。`PlanAction.Turn` 只表示发起动作的回合，不能代表该动作跨回合结算后的终局回合。

`src/Engine/Common/` 提供 `PredictedCard`、`PredictionForkContext`、`PredictionStateStore` 和通用模型克隆。一次 Fork 内的所有结构必须共享同一个 context；分支可变对象必须显式重映射。`BaseLibCloneConcurrency` 是原版与预测克隆共用的外部扩展并发边界，只包围模型深克隆阶段。

通用命令和 Hook 调用遇到 `PendingChoice` 时立即向上传播未完成状态，不再执行其后的监听器、抽牌、资源变更、死亡处理或卡牌收尾。Search 为待处理选择补齐计划后，从稳定父节点精确重放该动作，按原顺序通过挂起点；未完成事务不作为可继续执行的稳定 Fork。自动出牌将外层来源与上下文身份带入 `OnPlayWrapper`，在来源牌仍位于 Play 时消费嵌套选择，等待嵌套自动出牌结束后才移动来源牌和执行费用清理。原版挂起位置、顺序与卡牌实例身份属于模拟语义，不能由 Beam 或部署层补偿。

`CombatPredictionSimulator.CardPile.cs` 的抽牌安全边界只约束当前同步调用栈：抽牌 Hook 再次自动出牌、自动出牌又抽牌时，嵌套深度最多 `100` 层，继续嵌套会明确失败，不返回部分抽牌结果。深度在 `finally` 中退出；普通动作结束后、跨回合或从稳定边界 Fork 后继续抽牌，都不因已经累计的抽牌历史而减少合法抽牌。历史记录不再承担整个分支生命周期的 `100` 次抽牌额度，正常长线与有效循环仍受 Search 的节点、时间和调度预算约束。

### 4.2 Mirror

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

## 6. UI

`src/UI/SolverOverlaySnapshot.cs` 是结果或只读候选路线到显示数据的唯一转换边界。它在主线程复制状态、概览、详情、回合、动作标题、选牌文本、遗物标注、击杀、逐回合对敌伤害、tooltip 和视觉类别。

以下 renderer 只接受不可变 snapshot：

- `SolverOverlay.ShowResult(Node, SolverOverlaySnapshot)`；
- `SolverRouteRow.Populate(SolverOverlayTurnSnapshot)`；
- `SolverActionPill.Create(SolverOverlayActionSnapshot)`。

renderer 不得重新读取 `SolverResult`、`PlanAction`、`PlanCardChoice` 或 `ModelDb`。部署需要的标量由 Runtime 单独持有，不从控件反向读取。

`SolverSettingsPanel` 是设置页的单一控件所有者，按 partial 分离构建职责：主文件负责标题、常规/性能/反馈三页切换、重载、提交、恢复默认和固定状态栏；`General` 负责求解器、通知、自动执行，以及第一/二幕与最终 Boss 相互独立的血量取舍；`Performance` 负责预设、并行度、NoGC 开关、独立内存预算、排队式手动回收和折叠的自定义搜索参数；`BugReports` 负责诊断、联系方式与问题包导出/上传；`Controls` 只提供本面板共享的 Godot 控件样式、输入校验和行布局。partial 之间不建立第二份设置状态，持久化仍只写 `SolverSettingsData`。

`BossHpRelief` 只描述战斗事实：第一、二幕战后回复 80%，最终 Boss 后无后续战斗。`BossHpStrategy` 决定搜索如何使用该事实；通关优先沿用实际回复折算，最低战损把对应战斗恢复为普通 HP 权重。最终排序、智能药水开层和卖血阈值必须消费同一个有效策略，结果与诊断仍保留真实 `BossHpRelief`。

`SolverPotionStrategyPanel` 是主界面右侧独立窄浮层的逐瓶药水策略控件所有者。它只在主线程按当前槽位读取图标、标题和可搜索性，紧凑按钮在智能、保护和强制使用间循环；`SolverController` 以槽位和药水 ID 捕获不可变 `PotionStrategySnapshot`，自动计算开启时策略变化会废弃旧 continuation 并启动新搜索。新进入槽位的药水没有旧身份覆盖，默认按智能使用处理。

`PhysicalMemoryUsage` 从操作系统采样实时物理内存，`SolverMemoryUsageBar` 在底栏把系统及其他程序占用显示为灰色、当前游戏进程工作集显示为彩色，剩余部分表示可用余量；文字只显示游戏进程的“当前内存占用 / 动态上限”，动态上限等于 CLR 安全总量减去系统占用。Smart 用药梯度之间释放上一层搜索图并同步回收；最终梯度或普通搜索正常结束后保留战斗级 NoGC 区域，战斗结束时等待引用释放并延时 `3–5 秒` 清理。异常耗尽、搜索内检查点和手动回收继续在各自安全边界处理。

`SolverSettingsPanel.BugReports` 持有问题包导出/上传的单实例 UI 生命周期、取消令牌、进度条和线程安全完成邮箱，并把文件发送和服务端确认显示为两个阶段。后台任务只向完成邮箱发布一次 `Succeeded / Canceled / Failed`；面板自己的 `_Process` 每帧先消费终态，再处理字节进度或取消等待，并在同一次终态消费中释放令牌、收起进度条、替换状态消息和恢复按钮。上传生命周期不依赖搜索使用的 `SolverDispatcher`。`CombatBugReportUploader` 不持有 Godot 控件，后台传输只通过 `IProgress<CombatBugReportUploadProgress>` 发布字节计数。进度到达文件总字节数只代表请求正文已经写出，只有服务端回执同时确认反馈编号和实收字节数才算上传成功。

## 7. Unattended 测试

`UnattendedTestRunner` 保留请求级编排和现有 fixture helper。新增流程应落到明确所有者：

| 组件 | 职责 |
|---|---|
| `ProtocolHost` | 请求文件循环、协议校验、进程复用、每请求测试开关、漂移注入与 reset |
| `ScenarioBuilder` | 建立跑局/遭遇、加载快照、注入牌/Power/遗物/药水/球/RNG，返回 `ScenarioContext` |
| `Executor` | 分派严格差分、应用临时设置、启动搜索/全自动、等待复用/暂停/结束并恢复设置 |
| `Assertions` | 执行前边界检查和执行后的回合、生命、出牌、药水、Power 断言 |
| `Writer` | Passed/Held/Failed 公共协议字段、内存采集和结果文件原子替换 |

`UnattendedTestRunner.ReplayState.cs` 属于 `ScenarioBuilder` 的状态注入实现。它只接受同检查点的 `run-state` 与 schema 1 `replay-state` 组合，恢复后必须通过完整 `ContinuationStamp`；不能把部分字段相似的建局称为严格重放。

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

- `tools/run-unattended-test.ps1` / `tools/run-unattended-test.sh`：Windows / Linux 的平台原生入口，负责请求协议、精确进程生命周期、结果与静稳 ACK；同实例同时只能有一个 producer。
- `tools/headless-runtime.ps1` / `tools/headless-runtime.sh`：平台原生测试资源边界，独占实例目录归属、私有完整游戏/Mod 快照、内容来源身份及每用户主机预约队列。默认 exclusive，parallel 显式启用，最多两个游戏；预约随游戏进程而非启动器存活。它们不改变 Search DOP、NoGC、停止规则或任何战斗语义，也不负责游戏内请求协议。
- `tools/run-visible-steam-benchmark.ps1` / `tools/run-visible-steam-benchmark.sh`：Windows / Linux 的平台原生入口，负责正常可见 Steam 会话的搜索、GC 与帧口径。
- `tools/CoverageCatalog/Program.cs`：当前程序集和 registry descriptor 的覆盖目录生成/验证。
- `tools/verify-refactor-boundaries.ps1` / `tools/verify-refactor-boundaries.sh`：Windows / Linux 的等价门禁，阻止 Search 全局依赖、旧 controller 字段、worker live 回读、Beam 职责回流、unattended 编排回流、UI mutable 类型回流和 registry 私有反射；规则变化时必须同步维护两端。

纯职责移动至少运行 Release 编译与当前平台的结构门禁。改变语义、搜索或显示行为时，再按影响面选择严格差分、完整 headless、CoverageCatalog 或可见 Steam。
