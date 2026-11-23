# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

## 未发布：战斗内 Sts2Sim 求解接入、战前桥接、多核并行、ABI v2 组合策略透传与界面引擎标识

- 战斗内 Beam 根据敌方力量、虚弱和目标受伤修正计算中途评分。战前预测沿用基础攻击意图评估中途格挡与失血，使已验证的窄 Beam 路线得以保留。两个入口均按修正后的逐段伤害预测 HP 和计算 Pareto 生存余量。负层数能力按 Core 的实际增益或减益类型计分。幕末 Boss 的新颖性探索时限按遭遇确定，独立于战损偏好。

- 战斗内根快照用原生 decimal 精度传递 `TheShot` 的消耗计数及 `MonologuePower` 的两项力量数值。Core 的恢复接口兼容旧桥接发出的整数字段。

- 预测 ABI 与搜索子项目由 CombatSolver 维护。模组经清单装载程序集，模拟器源码由 Git 子模块锁定。暂存会对照 Core DLL 内嵌的提交号，并将其写入清单。

- 桥接预测逐段核对致死、复活与伤后保护的先后顺序。自然致死的一段会跳过伤后回调；下一段仍可使用相应保护。奥斯提溢出伤害按玩家实际承受的一段处理。

- 路线面板显示所选路线的死亡回合和已知损血，预算截断的存活路线标为部分路线。路线回血只汇总从当前回合起的预测值。

- 战斗内 Sts2Sim 在主线程捕获好根和桥接请求后即可与首次面板挂载并行。模拟器缩短冷启动、分支克隆、状态描述和评分，在父任务充足时按原顺序截断同一动作的选牌重放。线程队列满时，其他线程可接手多出的独立动作。有界搜索的首选若已死亡，且同一策略候选池内存在已结算敌方回合、投影可存活的路线，优先给出可执行的存活前沿。[女王战入口基准](performance/sts2sim-incombat-entry-costs.md)
- 搜索进度保留界面上已绘制的前沿路线。动作回合、药水、战损、风险或语言有变化时重新生成；仅候选代次和工作计数前进时直接更新读数与接管控件。开始新搜索或重建界面会清除已绘制状态。
- 同一次战斗内搜索中，Sts2Sim 可能连续报告同一条最优路线。宿主按动作、嵌套选牌、目标、药水、逐回合结算和终局逐项比对，只复用已验证的预览。每个进度事件仍更新工作计数和接管代次，采用路线与搜索完成时各自执行权威回放。
- 战斗内 Sts2Sim 桥接随 `sts2-ai` 的 `b12bb3d3` 缩短大牌组长搜的状态保留时间。已出队作业释放整批候选，后续节点只保存还原路线需要的动作和指纹；费用查询在早期没有回调时复用同一份已校验监听器快照。Windows 女王战固定工作量的路线与逐回合结算一致。[测量与限制](https://github.com/iRyougi/sts2-ai/pull/1069)
- `PreCombatSts2SimBridge` 提供可选静态入口 `GetCapabilitiesJson()`，通过不可变 `BridgeAvailabilitySnapshot` 向主线程无锁暴露 `IsCorrupt`、`IsInCombatUnavailable`、`GetActiveCapabilities()` 与缓存查询 `TryGetCachedCapabilities()`（缺失时合成 `DefaultV1` 保持向后兼容）；战前与战斗内 `ValidatePortfolioPolicyOptions` 校验有效 `PortfolioOptionsVersion` 信封（缺省视作 v1 并在 v1 信封出现非默认 v2 选项时拒绝），`Sts2SimPortfolioPolicyBridge` 按协商能力逐项门禁并透传 `BeamWidthPortfolio` 全量成员选项（含 `ReallocatedRefinementPortfolio` / `StopPortfolioAtHpTarget`）、新颖性组合（独立 `NoveltySearch` / 非默认 `NoveltyBudget` / `AdaptiveNoveltyRefinement`）、早期回合探索（深度、预算与并行度调度模式 `Parallel` / `Sequential`）及 `PotionFreeBeamWidth` / `PotionBeamWidth`，且对仅面向局部能力集的插件仅发送已协商支持的字段组；`SolverController` 将首次战斗内捆绑包校验与 `AssemblyLoadContext` 加载推迟至后台工作线程执行（主线程仅做宿主判定、缓存能力快路径检查、纯内置门槛校验与实机状态采集），在启用早期回合探索且插件支持时扩展请求总超时窗口，并在 `TrySolveWithSts2Sim` 期间将调用线程优先级临时设为 `ThreadPriority.Normal`（`finally` 恢复）；`InCombatSts2SimResultMaterializer` 与 `Sts2SimAttemptWorkTracker` 遵循 ABI v2 扩展计数器回落合同（负值或缺失分别回退 `ExpandedNodes`、`0`、`0` 与有效并行度，放开 `ObservedPeakParallelism` 任意正值上限，分别维护实测并行峰值、权威 `EffectiveDegreeOfParallelism` 回落与临时进度回落以防止临时请求并发度覆盖真实有效并行度并保留已测得并行峰值），将 `"Action"` 时点选择按主选择位置拆分出 `NestedChoicesBeforePrimary`（保留出牌重复手选 `SourceId` 以供 `ExternalChoiceBinding` 逐项验证绑定并按执行顺序还原）并分离回合开始／结束选择，由 `CombatBeamSolver.VerifyAndAnnotateExternalRoute` 在每次出牌重放前写出经验证的 `CardStateKey` 与 `CardStateOccurrence`，并让 `SolverRouteAdoptionSeed.Actions` 按需直接读取完整验证路线，累计单次尝试总工作量并在中途采用及空回退内置模拟器时单次并入请求总计；`SolverOverlay` 与 `English.json` 在中英双语界面中完整保留并显示 `ActivePhaseLabel`。
- `SolverController` 与 `Sts2Sim.PreCombatBridge`（战斗内 ABI v1/v2）打通战斗内路线求解：在开启 `UseSts2SimInCombat`（默认开启）且满足标准单人原版准入（战斗内 `AssessMandatoryLiveHost(..., forInCombat: true)` 改用 `IsCombatInert`、统一使用 `HasUnsupportedExtraFields` 并额外拒绝未建模 `CombatState` 战斗订阅者）时，主线程采集实机战斗根、牌堆、私有状态、历史计数与 9 条战斗 RNG，后台在 `NoGcRegion` 之外按设定并行度运行 `Sts2Sim` 多回合求解并支持第 N 回合中途移植、流式进度推送以及接管/停搜返回最佳中间结果；任何不兼容、能力未命中或捕获异常均 fail-closed 自动回退至内置模拟器并更新 `InCombatSolverBackendState`。
- `SolverSettingsPanel`（性能页）新增“战斗内优先使用 Sts2Sim 模拟器”开关；`SolverOverlay` 标题栏徽章在战斗内直接展示当前求解引擎（`Sts2Sim（N 线程）` / `计算中` / `就绪` / `内置模拟器` / `异常`）及中英双语悬停详情（含战前引擎摘要）。
- `PreCombatForecastApi`（`ApiVersion = 7`）在标准模式且满足强制宿主排除、原版修饰性/统计 `ExtraFields` 放行、语义级战前 Mod 与 BuiltIn 同口径关键 Harmony 审计（放行战前惰性与辅助功能 Mod，拒绝 `RunState` 战斗订阅者、自定义最大手牌/BaseLib 卡牌修饰器、`MegaCrit.Sts2.Core.Random.*` 全部非原版 RNG 补丁、怪物 `GenerateMoveStateMachine`/`AttackIntent`（含 `SingleAttackIntent`/`MultiAttackIntent` 构造）与 `GetSingleDamage`/具体原版卡牌非纯展示成员（含玩法覆写、状态机及非虚辅助方法与属性）/未封印且不依赖 `AdaptedCardOnPlayMirrors` 的可达牌组 `OnPlay` 补丁、未建模卡池/药水池及 Ritsu 运行时全局治疗/能力监听器，同时放行 `ExtraRunAssetPaths`、`HasBuiltInOverlay`、`AddExtraArgsToDescription`、`OnEnqueuePlayVfx`、`SpawnVfx`、`GetVfxNode`、`RemoveSovereignBladeNode`、`GetPortraitFilename`、`LoadPortrait` 等纯展示成员）及存档资格时，通过先读入校验 SHA-256 与 MVID 字节数组再由 `IsolatedBridgeLoadContext` 以 `LoadFromStream` 延迟加载的隔离上下文与清单 `sts2sim/sts2sim-bundle.manifest.json` 调用同进程 `Sts2Sim.PreCombatBridge` 完成战前预测；ABI v1 支持 `1..64` 并行度上限并校验实际有效线程数不超出请求上限，桥接搜索按父节点波次并行展开且并发执行组合策略成员；规划请求独立评估规划存档内容，同时继承并先决执行宿主强制排除。
- 恐惧模式（`GameMode.Dread`）及其他不兼容宿主条件固定通过 `dread_mode` 等编码在探测或激活 `sts2sim/` 前直接回退至既有普通求解器路径（Windows 独立子进程，Linux 按既有平台支持口径返回不支持）。
- `PreCombatForecastWorker` 通过统一单 `Gate` 调度器、`admissionEpoch` / `_stopEpoch` 截止栅栏、请求中断与严格期限分离，以及由生产者持有的完成票据管理同进程会话、遗留回退、互斥锁、重启状态与空闲释放；在交接层完成药水标题本地化与默认 `UseBeamWidthPortfolio`（含通用 `CardType.Power` 能力承诺成员）路由，并向界面提供活动与最近后端、有效并行线程数及分流/回退代码快照。
- `SolverOverlay` 标题栏新增战前预测后端标识徽章（`PreCombatBackendBadge`）及中英双语悬停详情（`PreCombatBackendDisplaySnapshot`），实时区分同进程 `Sts2Sim`（含最近有效并行线程数或就绪状态）、旧版独立子进程、异常及不可用状态。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

0.51.0 的标准模式适配、请求预算显示和路线优化见[历史卷 23](archive/development/volume-23.md)，玩家说明见[0.51.0 更新日志](releases/0.51.0-RELEASE_NOTES.md)。

0.51.1 的恐惧／极速模式适配与实例状态修复见[历史卷 24](archive/development/volume-24.md)，玩家说明见[0.51.1 更新日志](releases/0.51.1-RELEASE_NOTES.md)。

0.51.2 的属性削减、诅咒持续时间和仪式兽修复见[历史卷 25](archive/development/volume-25.md)，玩家说明见[0.51.2 更新日志](releases/0.51.2-RELEASE_NOTES.md)。

0.51.3 的同牌多减益与部署异常修复见[历史卷 26](archive/development/volume-26.md)，玩家说明见[0.51.3 更新日志](releases/0.51.3-RELEASE_NOTES.md)。

0.51.4 的战斗逻辑与选牌续接修复见[历史卷 27](archive/development/volume-27.md)，玩家说明见[0.51.4 更新日志](releases/0.51.4-RELEASE_NOTES.md)。

0.51.5 的结算顺序与运行时交接修复见[历史卷 28](archive/development/volume-28.md)，玩家说明见[0.51.5 更新日志](releases/0.51.5-RELEASE_NOTES.md)。

0.51.6 的社区 PR 整合与日志上传修复见[历史卷 29](archive/development/volume-29.md)，玩家说明见[0.51.6 更新日志](releases/0.51.6-RELEASE_NOTES.md)。

0.51.7 的薄荷尖塔兼容与诊断取证修复见[历史卷 30](archive/development/volume-30.md)，玩家说明见[0.51.7 更新日志](releases/0.51.7-RELEASE_NOTES.md)。

0.51.8 的治疗补丁启动修复见[历史卷 31](archive/development/volume-31.md)，玩家说明见[0.51.8 更新日志](releases/0.51.8-RELEASE_NOTES.md)。

0.51.9 的愚者能力牌路线优化见[历史卷 32](archive/development/volume-32.md)，玩家说明见[0.51.9 更新日志](releases/0.51.9-RELEASE_NOTES.md)。
