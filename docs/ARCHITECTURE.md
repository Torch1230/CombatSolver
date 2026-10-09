# CombatSolver 架构与职责地图

本文只维护当前所有权、依赖边界和源码入口。实现过程、固定根结果和性能数字见 [历史资料](archive/README.md)。持续工作规则见 [AGENTS.md](../AGENTS.md)。

## 1. 运行链

主线程捕获稳定战斗根 → 冻结请求政策 → 后台分支搜索 → 主线程接收结果 → 原版公开入口部署当前回合 → 续用戳核对或重新捕获。

后台只读取根与分支状态；真实战斗对象只用作稳定身份或只读模型元数据。未知语义形成明确边界或失败。

## 2. Runtime

| 入口 | 所有权 |
| --- | --- |
| `src/Runtime/Entry.cs` | 初始化、战斗生命周期接线 |
| `SolverController.cs` | 主线程请求、结果接收、续用、部署和自动执行 |
| `SolverControllerSessions.cs` | 战斗、搜索、部署会话生命周期 |
| `CombatBugReportUploadPolicy.cs` / `CombatBugReportDescription.cs` | 当前战斗内容的上传引导资格与真实错误分类；搜索会话单独记录期间玩家输入 |
| `CombatRootSnapshot.cs` | 在主线程捕获并核对稳定根 |
| `ContinuationStamp.cs` | live / predicted 跨回合一致性和字段差异 |
| `SearchGcPolicy.cs` | 进程 GC、NoGC 和跨战斗回收协调 |
| `SearchMemoryPressureSignal.cs` | 注入 Search 的分配边界与回收续搜信号 |
| `NativeChoiceRuntime.cs` | 原生选牌观察与逐实例计划匹配 |
| `PlayerTurnSetupPatches.cs` | 原生回合准备、选择和部署交接 |

政策由主线程从设置冻结到 `SearchPolicySnapshot`。后台通过请求参数、诊断 sink 与压力信号消费外部能力；算法不读取设置单例或操作 GC。Power 显示变量在主线程根捕获时物化；worker 只消费已物化值。克隆并发边界由 `BaseLibCloneConcurrencyPatch` 与原生克隆隔离规则维护。

常规 GC 搜索的系统余量、分配限额和 Gen2 回收由 Runtime 作用域持有，Search 只在排空后的提交边界消费压力信号。常规检查点刷新请求限额；不可分割提交通过显式退出将分配交给 CLR，下一请求重新建立限额。Runtime 在限额和诊断成功后登记作用域，入口失败释放信号并传播原异常；退出时按作用域清理计数和信号。

`SearchGcRuntimeInfo` 在 Runtime 一次探测按类型查询 GC 信息的能力。具备完整信息时保留后台回收与完成索引核对；检查点重建 NoGC 时直接请求一次压缩完整回收，确认新的压缩完成信息及释放的弱哨兵后才重建。Mono 等运行库使用同步完整回收和弱引用完成哨兵，不因 NoGC 可用而读取不支持的详细信息。暂停观测通过压力信号传递为可空值，Search 消费实际观测并保留其他工作量；检查点及回收日志明确标记不可用的暂停统计。

## 3. Search

| 入口 | 所有权 |
| --- | --- |
| `CombatSearchCoordinator` 与各分片 | 主 Pass、药水审计、组合成员及后处理编排 |
| `SearchRequestPipeline` | 请求级阶段顺序与停止条件 |
| `SearchPassContext` / `SearchPassResult` | 单轮冻结输入及返回合同 |
| `SearchBudgetLedger` / `SearchRequestWorkTotals` | 请求时间、额度和唯一工作量累计 |
| `FrontierContinuationScheduler` | 固定前缀成员派发与用途归因 |
| `CombatBeamSolver.cs` / `.Models.cs` | 不可变根配置、策略接线、节点和运行上下文 |
| `.Phases.cs` / `.Expansion.cs` | Solve 阶段和动作回放入口 |
| `CombatSearchCoordinator.BossTempo.cs` / `CombatBeamSolver.BossTempo.cs` | 首领追加额度、首回合偏差侦察、纯值前缀与后续搜索 |
| `.ParallelExpansion.cs` / `.AdmittedExpansion.cs` | 固定 worker lane、已准入作业及确定性提交 |
| `.PrimaryChoiceReplay.cs` | 原预算必经的首层选择回放与暂存 |
| `.BeamRetentionPolicy.cs` | 去重、Beam、多样性、选择保路、药水配额与 Pareto |
| `.FinalPlanOrdering.cs` / `RouteQualityPolicy` | 完整路线质量与各既有投影顺序 |
| `.StateEvaluation.cs` / `.Terminal.cs` | 评分特征、终局回放和回合结果 |
| `SimulatedCombatState*.cs` | 分支战斗状态与动作语义 |
| `PrimaryIncumbentTable` / `ResourceIncumbentPolicy` | 同根同政策的资源桶见证、未来收益上界与认证回退 |

成员共享原请求账本；预算准入、候选合法性和取优由所属策略决定。分支状态不承担 Beam 政策；中途保路和终局比较保持明确入口。药水反事实与强制用药的硬准入先于质量比较。终局可选用药遵守机会成本门槛，获胜且减少复活消耗或追回资源时保留对应政策例外；`RouteQualityPolicy.PotionPolicy` 为审计、组合成员和后置搜索统一比较战略战损加可选药水成本，成长与回合数在其后比较。固定前缀构造实际父链，EndTurn 从模拟前后状态生成 `TurnOutcome`。

`CombatSearchCoordinator.Audits` 的智能开局药水补搜逐成员扣除请求剩余节点和时间。开局成员沿用智能梯度同一次请求计算出的用药准入上限，首回合及损血边界续搜也受该上限约束；保留上游净差、必然受击及整场战损三轴准入。无药路线只有死亡、尚无完整胜利时，最后一个生成能力成员可复用同药水选择成员自生的完整首回合动作，并在其他复合前缀前调度；缓存只存纯值动作，不持有节点或实机状态。仍占原成员名额，沿既有前缀回放重建调度基线、普通排序及药水资格比较，不增加配置容量。

取得安全、最多两瓶且仍损血的完整胜利后，仅一个剩余复合成员可复用该路线最后损血回合之前的完整动作；边界由结果已有的逐回合损血摘要确定，前缀须含登记能力且结束于上一回合。消费原成员宽度，并从共享剩余节点/时间中按剩余名额分配一份有界续搜额度；其他成员保持原前缀和政策。全程只使用当前结果的纯值动作与摘要，最终仍按整场政策取优，不解析报告、不持有节点图或读取实机。

`PowerCommitmentRetention` 按机制族、登记能力集合、药水数量与回合选择有界代表；能力激活顺序不重复占席，不同能力集合不因族相同而合并。Beam 只保护实际代表，其他能力节点不预占代表配额；替换保持容量和既有必保节点。完整状态键、转置支配及终局政策不消费这种启发式分组。显式路径观察只复制纯值承诺及独立字符串数组，不保留节点或模拟器。

`CombatSearchCoordinator.PowerRepair` 在既有强制用药能力成员中选取已满足全部 Force 的回合边界。`CombatBeamSolver.CanContinueAtPrefix` 从冻结根完整回放该前缀，要求动作全部应用、仍存活、战斗继续且处于无风险稳定状态；随后发生的死亡由后续搜索处理。协调器只保留纯值前缀，沿现有成员容量继续求解。

组合补搜入口按基线完成情况与节点、时间余量准入。每个成员在 `CombatBeamSolver.Phases` 的提交边界预约内存，Runtime 检查点执行回收和区域重建，连续无进展时结束当前成员；成员可跨多个区域完成，累计分配量由组合诊断记录。

资源桶按失窃量、用药量、成长次数向量、遗物目标组合分层。CombatRootSnapshot 冻结根成长上界，ResourceIncumbentPolicy 另在保路时只读认证分支剩余上界；不改变分支状态键或续用戳。纯成长目标在已实现次数至分支上界之间逐桶消费独立战损基准，所有可能目标均无改进空间才停止公共展开。目标集合过大或上界未知保留搜索；混合遗物目标沿用最乐观最终资源桶。Runtime 会话只携带已保留的完整零药路线及对应桶，不跨根或政策沿用数值界。 已有剩余治疗界且没有成长目标/奖励、全部遗物计数HP额度为零时，标量界可跨满足掩码剪掉严格更差HP；同HP晚回合继续保留。资格、局部收紧及实际执行入口使用同一规则，付费计数与成长仍按资源桶处理。

预览路线的采用回放持有请求级取消令牌，单轮搜索结束与用户取消请求分别判断。强制结束回合的卡牌动作只消费自身选择，后续回合选择由AdvanceRound持有。固定前缀在仍进行的稳定父状态继续，药水统一沿正式候选政策准入。

`StrategicHpRecoveryBound` 在根快照中冻结回复环境资格，由 `.Remaining` 维护已审计来源闭包和分支剩余上界，未知来源保持无限上界。`.KnownSources` 单独提供当前原版已知来源策略，忽略尚未生成的随机药水回复；该策略资格不构成严格闭包证书。已有完整合规胜利可以沿既有 Retention 和组合成员入口提供界；严格组件根的后续计划也消费当前完整胜利，调用者保留胜利并按原质量政策择优。主搜索之前的计划安排仍只对严格认证根开放，其余根保留原阶段顺序。

`.Components` 组合逐项审查的卡牌、生成池、Power、遗物、药水和敌人证明，并锁定审计的原生 MVID。Runtime 在稳定根捕获证书及拒绝原因；模拟状态只提供已捕获的战斗、永久牌组和全局监听前缀，不读取 live。资格随根冻结，分支上界重新检查牌堆、待返回牌、层数和用药记录，未知来源不调用已知来源估计来收紧无限界。Smart 的精确用药层及开局用药后续搜索复用同一节血门槛；完整无药胜利基线经既有 continuation 请求传递，成长、遗物、强制用药、资源追回和保命资源门禁仍保留。证书是搜索元数据，不进入战斗指纹或续用文本。

Smart 药水梯度在主成员耗尽时间时拥有一个独立成员时间：仅对无强制指令、无显式用药的完整搜索结果和可搜索药水启用，首个可选层使用软预算的 1/5（1–30 秒）及 1/6 收尾窗口。梯度显式限制层数；后置开局前缀仍按请求账本准入。该成员的工作进入原请求总账本。

首领成员在原请求的主搜和后处理完成后运行，按 `EncounterRoomType.Boss` 准入。节点和软时间分别追加原配置的1/5，工作累计到同一个请求账本。侦察最多使用追加额度的1/3，允许3次排序偏差，以深度优先枚举首回合边界；独立转置键包含完整状态与剩余偏差，六维支配标签沿原实现。边界回调只复制动作、状态键、消灭敌人数、用药数和中途排名，按用药档最多保留24个纯值前缀。有强制用药指令时，已兑现全部指令的前缀优先；随后按消灭敌人数、用药量及排名选取至多两个前缀，固定回放后由原Beam搜索完成后续，所有成员共享追加余量及纯值资源桶界。中途HP单次计价，侦察及后验均保留等损续路；最后只接纳安全完整胜利，沿既有结果政策取优。`Phases` 提供内存预约、进度、取消与接管边界，待处理快照由侦察成员释放。

## 4. 模拟与 Prediction

| 位置 | 职责 |
| --- | --- |
| `src/Engine/InCombat/Simulation/` | 通用命令时序、历史、RNG、牌堆、伤害和 Fork |
| `src/Engine/InCombat/Mirrors/` | 原版 Hook / Model 方法镜像 |
| `MethodMirrorRegistryDescriptor` | 向 CoverageCatalog 暴露 registry 支持元数据 |
| `src/Prediction/` | 怪物 AI、隐藏状态、生命周期、死亡/召唤、选择与 subscriber 捕获 |

每项战斗语义只有一个权威结算实现。可变值由根快照、影子状态、克隆 Model 或 `PredictionStateStore` 持有。一次 Fork 共用 `PredictionForkContext`，引用随同一上下文重映射；COW 取得可写所有者后再写。

Fork 发生在动作、选牌、Power、死亡和出牌事务允许复制的稳定边界。玩家记录的卡牌/药水嵌套效果作用域由引擎持有；选牌检查点保存不可变身份序列，恢复建立分支独占列表。最外层效果结束后再检查手空。稳定根及跨回合状态的该作用域为空，普通 Fork 要求为空。

未知 gameplay subscriber 显式拒绝；已支持来源在主线程捕获，并在分支中消费隔离状态。登记合同与封闭入口见 [第三方适配手册](third-party/README.md)。

资源初始化冻结玩家基础最大能量，最大能量与抽牌按分支监听表、回合号及遗物隐藏状态结算；准备根和普通跨回合都一次性消费延迟能量。玩家 Hook 活动标志由引擎在死亡清理时关闭，Fork 继承并进入状态键，金币回调据此筛选成员。攻击意图由分支当前怪物 AI 行动派生，行动替换即时生效。

回合末自动出牌先于 BeforeSideTurnEndEarly，PAELS_EYE 的手牌消耗由该 Hook 镜像拥有；额外回合资格在阶段结算后判断。部署会话区分动作、玩家结束／敌方阶段与下一玩家回合；最后一个结束阶段选择确认前解除旧会话归属。UI 的步骤和完成回调核对所属回合及当前路线快照。

额外回合的判断与后置效果由 `ExtraTurnMirrors` 登记原版和第三方单项语义，`HookMirrors` 按原生监听顺序派发。后置回调使用固定成员快照，选牌暂停沿动作重放恢复；分支 Power 与帕尔之眼使用状态继续由 `SimulatedCombatState` 持有。

金币命令由 `GoldGainSupport` 串联标准镜像：修改使用跑局前缀和战斗监听表，获得后的回调使用原生 null-child 跑局作用域。`SimulatedCombatState.GoldHooks` 在主线程冻结活动成员与已克隆全局来源，Fork 只读共享；遗物、药水、金币和 HP 仍从所属分支读取。未知金币 override 显式拒绝。监听参与位图为三个金币方法共用一位，只形成保守成员超集，精确方法仍由 registry 区分；不溢出或复用其他 Hook 位。`CombatPredictionSimulator.GainMaxHp` 单独实现实际封顶增量与后续 Heal，Feed、FruitJuice 和 DragonFruit 共用这一权威入口。

## 5. UI

`SolverOverlaySnapshot.Capture` 是结果到展示的唯一投影边界，可读取 `SolverResult` 与显示元数据。`SolverOverlay`、`SolverRouteRow`、`SolverActionPill` 只渲染只读 snapshot。

路线身份、选择、目标与显示值由主线程投影；UI 不持有搜索分支或从 `ModelDb` 重新解释路线。设置输入由相应面板持有，Controller 负责政策冻结和续用失效。中英文本同时维护。

## 6. Testing

源码按 [Testing 入口](../src/Testing/README.md) 收纳：Host 持有编排与协议，Support 持有共享差分辅助，Replay 持有恢复；Contracts 按 Combat/Search/Runtime/UI/ThirdParty 分组，Regressions 保存社区和报告回归。各目录沿用原程序集与 partial 类型。

组件剩余回复上界和 Smart 用药资格合同位于 `Contracts/Search`；根状态与完整原生差分仍由既有 Support/Runtime 合同提供，不改变 partial 方法、请求路由或状态所有权。

| 入口 | 所有权 |
| --- | --- |
| `UnattendedTestRunner` | 请求级编排与共享 fixture helper |
| `ProtocolHost` | 请求循环与每请求开关 |
| `ScenarioBuilder` | 建局与状态注入 |
| `Executor` | 差分、搜索、部署执行及临时设置 |
| `Assertions` | 执行前后断言 |
| `Writer` | 结果协议和原子写入；求解侧实测值（含 `UnavoidableHpLost`）在此进入 `UnattendedSolverMetrics`，两平台 launcher 的 `ExpectedInitial*` 参数只做透传，断言落在 Contracts |

原生与模拟核对完整状态、顺序、引用、RNG 和续用合同。离线宿主只产搜索指标；headless 不证明真实可见布局或帧时间。入口见 [无人测试](HEADLESS_TESTING.md)、[离线宿主](OFFLINE_SEARCH_HARNESS.md)、[测试证据](TEST_MATRIX.md)。

检查点完整搜索 profile 由 `ProtocolHost` 在请求内持有，Runtime 捕获政策时读取其不可变记录；请求结束清除。显式 CLI 扰动沿既有入口覆盖单项，不将测试 profile 写入玩家设置或后台读取器。

保存预测路线的读取、严格回放、路径观察及原生终局断言属于Testing；生产Search不识别报告或测试场景。通用`RecordedPlan`与`KnownRoutePathTrace`消费归档中的完整路线作为只读观察目标，严格回放先核对原生身份、完整状态及增量等价；路径观察运行正常协调器，不注入固定前缀。Q002专属一次性路线/成员诊断已移除，机制合同和历史证据保留。部署沿现有Runtime入口，跨战斗结束的真实后台搜索代次只通过`SolverController.Testing`只读暴露；不以会话清零计数或已清空的临时账本推断终局。

能力路线组合的候选与名额选择仍归`CombatSearchCoordinator.PowerRoutes`：安全且有明显损血的无新增用药胜利可将首个单能力组合的已有宽、次排序及最后成员用于自身首回合结束处续搜，动作来自当前搜索结果。宽成员可追加边界上真实可打、按登记优先级选择的能力；组合一旦准入，后续成员资格不会因中间胜利低于原触发门槛而失效。保留原四成员容量、各自宽度/预算和次排序配置。既有调度器负责严格前缀回放与启发式基线重建，最终整场政策比较不变。

既有成员时间低于`MinimumPowerRouteMilliseconds`时，自生首回合续搜也可先追加边界上可用的最高登记优先级能力；短成员无需重新搜索该铺垫。仍沿同一构建入口执行合法性检查，不追加成员、提高宽度或预算；未取得安全胜利、能力不可打或未满足原准入时不改变原规则。

旧批次的硬编码路径调查退出当前树，仍被原生回归、生成上下文与搜索合同调用的快照辅助保留在 Support。一次性验证代码由 .local/tool-tasks 持有并在任务结束清理，普通构建显式排除 .local 源码。

## 7. 工具与维护

工具按 [职责目录](../tools/README.md) 管理。testing 持有无人实例与生产回归检查，replay 持有包恢复与会话，search/performance 持有离线指标与采样，inspection 持有目录和结构检查；build/release/community 分别维护构建、发布与社区流程。Windows MemoryCleaner 由 tools/runtime 提供，在线监控后台由独立私有仓库 combatsolver-presence-service 持有，本仓库仅维护模组端上报和提醒。

tools/Directory.Build.props 统一工具项目的仓库根与构建产物路径，生成内容放在 .local/。`tools/inspection/verify-refactor-boundaries.ps1` 和 `.sh` 维护同一职责边界。CoverageCatalog 从公开描述与结构化证据生成覆盖报告，报告的生成版本与测试来源分别说明。

[coverage](../coverage/README.md) 持有手工分类、证据、复用输入、固定语料和归档摘要。CoverageCatalog 读取 catalog/classifications.json 与 evidence/test-evidence.json，替换 catalog/generated 的现行快照；候选 fixture 写入 .local/coverage-fixtures。证据按完整仓库相对路径读取，缺失材料显式失败。脚本与完整运行产物分别属于 tools 和 .local。

修改职责时在同一提交替换本文对应章节，并同步相关 skill 与结构门禁。开发进度写入当前开发记录，测试细节写入证据，历史报告冻结归档。
