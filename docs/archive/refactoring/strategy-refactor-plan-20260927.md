# CombatSolver 策略与搜索大重构计划

本文把 [策略优化方向与架构不足分析](../strategy/strategy-optimization-directions-20260927.md) 从“方向”落成**可执行的阶段计划**：目标架构、迁移方法、逐阶段改动点、结构门禁、验证与回滚。它是提案，不是已批准的改造；每一阶段执行前按 [架构边界重构 skill](../../../.agents/skills/architecture-boundary-refactor/SKILL.md) 与 [搜索性能优化 skill](../../../.agents/skills/search-performance-optimization/SKILL.md) 立项。

**源码快照：2026-09-27，分支 `main`，已提交 `8915a7c0`。** 前几轮结构重构（第一至第六轮）见 [滚动重构路线](../../refactoring/refactor-roadmap.md)；本计划是**下一轮战略级重构**，规模与风险都高于以往批次，必须严格分批。

---

## 0. 计划摘要

**核心目标**：把“Beam + 十几种按包特例补充 + 多份比较器”的现状，收敛为**一个统一搜索管线 + 一份可解释质量模型 + 一套数据驱动登记表**，让后续策略优化从“每个报告加一条分支”变成“加登记项/调参数/扩框架”。

| 阶段 | 名称 | 性质 | 目标架构产出 |
|---|---|---|---|
| P0 | 行为基线与黄金语料 | 工具/证据 | 固定根语料 + 逐位对照器 |
| P1 | 统一路线质量模型 | 纯重构 | `RouteQuality` + 单一比较入口 |
| P2 | 统一搜索请求管线 | 结构重构 | `SearchRequestPipeline` + `SearchBudgetLedger` |
| P3 | 前沿续搜框架 | 结构重构 | `FrontierContinuationScheduler` |
| P4 | 开局/药水/目标登记表 | 结构重构 | `OpeningActionRegistry`、`PotionValuationRegistry`、`TargetPlanRegistry` |
| P5 | 串行/并行路径统一 | 结构重构 | 单一 `ExpansionPlan` + 双执行器 |
| P6 | 计划/目标层 | 行为增强 | `PlanCommitment` + `PlanSearchPass` |
| P7 | 评估校准 + 药水价值 + 集火 | 行为增强 | 校准闭环、推导药水价值、目标代表 |
| P8 | 性能归因 + 鲁棒性 + 工具 | 工程收尾 | 归因账本、失败分层、离线 ABBA |

**依赖关系**：P0 → P1 → P2 → P3 → P4 →（P5、P6）→ P7 → P8。P1 是纯重构，必须先做且逐位等价；P2/P3 依赖 P1；P6 依赖 P2/P3；P7 依赖 P0 的语料。

**硬约束（沿用仓库与路线图的明确不做）**：不引入 DI 容器/事件总线/多程序集；不重写为 ECS；不支持多人；不重新依赖 RandomForeseer；**不用扩大预算掩盖模拟偏差**；未知语义显式失败；Search 不读取 Runtime 全局、UI 或 Testing。

---

## 1. 目标架构（End State）

### 1.1 一次请求 = 一条管线

```text
SearchRequestPipeline.Run(root, policy, budgetLedger)
  = 依序执行若干 SearchPass，共享一份预算账本与一个质量模型
      BeamPass              // 主搜索（内部可再跑宽度组合）
      PotionAuditPass       // 强制药基线 / 精确 N 瓶梯度 / 开局药后验
      ContinuationPass×N    // 由 FrontierContinuationScheduler 派发的前沿续搜
      PlanPass×N            // 引擎/复制/药水链计划子搜索
      NoveltyPass           // 可选
      EarlyTurnPass         // 可选
  -> 每个 Pass 返回 SearchPassResult { RouteQuality, WorkTotals, Termination }
  -> 管线用 RouteQuality 统一取优，到达目标或预算耗尽即停
```

### 1.2 关键新类型（**具体类型 + 窄接口，不用 DI**）

| 类型 | 职责 | 替代 |
|---|---|---|
| `RouteQuality`（record struct） | 生存、战略战损、目标、药水成本、回合、动作数的统一质量向量 | 分散的 `Compare*` 字段枚举 |
| `RouteQualityPolicy` | 从快照计算 `RouteQuality`；提供唯一 `Compare` | `ComparePrimaryQuality`、`CompareFinalCandidates`、`ComparePotionFreePolicyBaselines`、`CompareCompletedResultPrimaryQuality` 的重复实现 |
| `SearchBudgetLedger` | 请求级时间/节点/转移/选牌/分配/GC 的统一记账与分配 | 各 `min(80_000,…)`、`min(30_000,…)` 手写切片 |
| `SearchPassResult` / `SearchPassContext` | 每个子搜索的结果契约与输入快照 | 各方法临时约定 |
| `ContinuationSearchRequest` / `IFrontierContinuationSource` | “从某前缀/某边界做有界完整续搜”的统一请求与来源枚举 | 40 处 `fixedPrefixActions:` 直接构造 |
| `FrontierContinuationScheduler` | 前缀去重、优先级、有界派发、统一取优 | `RunSupplementalAudits` 内的逐模式函数 |
| `OpeningActionRegistry` / `PotionValuationRegistry` / `TargetPlanRegistry` | 开局动作/药水/目标的分类登记与候选生成 | `Expansion.Opening` 的逐卡 `BuildOpening*` |
| `PlanCommitment` | 泛化能力/引擎/药水链/跨回合的计划租约 | `PowerCommitment`（能力专用） |
| `SearchLossQuery` | 回答“这条路线在哪一层消失”的聚合查询 | 人工读 path diagnostics |

**注意**：这些类型拥有真实状态、消除真实重复；不做“只转发旧单体”的 facade。

---

## 2. 迁移方法（必须遵守）

1. **行为冻结优先**：P1/P2/P3/P4/P5 是纯或准纯重构，必须逐位保持终局路线、动作序列、expanded/transitions/choice branches、剪枝计数和协议 schema 不变。
2. **黄金语料**：P0 产出的固定根语料是唯一“未退化”判据；每个阶段都要跑语料并输出差异表。
3. **一个提交一个边界**：每批只迁移一个可解释边界，先修 `docs/ARCHITECTURE.md` 与结构门禁，再实现、验证、提交；不等全部设计完。
4. **门禁双端同步**：任何新约束同时写入 `tools/verify-refactor-boundaries.ps1` 与 `tools/verify-refactor-boundaries.sh`，规则等价。
5. **旧路径删除**：迁移后删除旧所有权与双写路径，不长期并存；并存期必须用允许清单+计数守卫，不得无界积累。
6. **fail-fast 与日志不变**：stage 名、结构化日志、请求/结果 schema 在纯重构中保持不变；诊断模式名保留以便对照。
7. **停损条件**：任一阶段若无法在语料上证明行为不变（纯重构）或严格不退化（行为增强），停止并回退，不带着可疑差异继续。

---

## P0 · 行为基线与黄金语料

**目标**：建立可重复的“未退化”判据，避免大重构把优化成果改回去。

**步骤**：
1. 选固定根语料：
   - 已追平/已改好的代表包（如 1/8/9/17/24/26/32/34/35/36/37/43/54/56/57/58/59/63/79/80/81/82/85/89/95）；
   - 未追平包（61/77/84/90/97/98/100/101）作为“已知缺口”基线；
   - 常规哨兵：一个有药、一个无药、一个跨回合、一个多敌。
2. 用[离线搜索宿主](../../OFFLINE_SEARCH_HARNESS.md)与常驻会话批量运行，记录：完整胜负、战损、用药、结束回合、最终 HP、expanded/transitions/choice branches、关键动作序列、墙钟。
3. 写一个**逐位对照器**：同输入两次运行的路线与记账差异；以及“重构前 vs 重构后”的差异表。
4. 语料与脚本放入仓库可复现目录；**不提交玩家数据**，只提交 fixture 定义与期望值摘要（玩家原始包保留在忽略目录）。

**验证**：同一基线连续两次运行结果逐位一致（排除墙钟/分配等非确定字段）。

**退出标准**：对照器能对任意源码版本输出“哪些根变好/变差/不变”。

**工作量**：约 1–2 周。**风险**：低；但 fixture 选取决定后续所有结论的可信度。

---

## P1 · 统一路线质量模型（纯重构）

**目标**：消除终局/主要质量比较器族的分叉，建立唯一 `RouteQuality`。

**范围**：[`SolverInterimResultOrdering`](../../../src/Search/SolverInterimResultOrdering.cs)、[`FinalPlanOrdering`](../../../src/Search/CombatBeamSolver.FinalPlanOrdering.cs)、[`BeamRetentionPolicy.Ranking`](../../../src/Search/CombatBeamSolver.BeamRetentionPolicy.Ranking.cs) 的 `CompareFinalCandidates`、[`CombatSearchCoordinator`](../../../src/Search/CombatSearchCoordinator.cs) 的 `CompareCompletedResultPrimaryQuality`，以及 cycle/region/crossturn 的专用比较器。

**步骤**：
1. 定义 `RouteQuality`（字段与来源显式），由 `RouteQualityPolicy.Compute(root, policy, snapshot, features)` 产出；
2. 让上述所有比较器改为**对同一质量向量做定义好的投影**：
   - 终局选路 = 完整键；
   - cycle/region/crossturn = 各自的子集投影（保留现有键次序差异，先不合并语义）；
3. 保留硬准入门（强制药、最低用药、龙涎香、保留资源）为独立步骤；
4. 用 P0 语料证明：重构前后终局路线与各比较器结果逐位一致。

**结构门禁**（新增）：
- 只允许 `RouteQualityPolicy.Compare` 作为终局质量比较入口；禁止再出现新的 `Compare*Quality` 私有实现（允许清单逐步缩减）。

**退出标准**：Release 编译 + 结构门禁 + 语料逐位一致 + 一个穿过要求的代表场景。

**回滚**：纯新增类型 + 调用替换分提交；任一差异即回退该调用替换。

**工作量**：约 2–3 周。**风险**：中；难点是 cycle/region 比较器键次序不同，不能“因字段相似合并”。

---

## P2 · 统一搜索请求管线与预算账本

**目标**：把主搜、药水审计、补充搜索、无胜利升级纳入一个管线，共享预算与结果契约。

**范围**：`CombatSearchCoordinator` 及其 `.FailureRecovery`、`.PowerRoutes`、`.NoveltyPortfolio`、`.EarlyTurnExploration`。

**步骤**：
1. 定义 `SearchPassContext`（root、policy、budget、diagnostics、cancellation）与 `SearchPassResult`（`RouteQuality`、work totals、termination、可选预览）；
2. 定义 `SearchBudgetLedger`：请求级统一记账，取代各方法内手写的时间/节点切片；预算分配策略显式化（先主搜、再审计、再补充、最后升级）；
3. 先用 **adapter** 把现有 `RunPrimary`/`RunSupplementalAudits`/`EscalateSearchWhenNoVictory` 包成 Pass，**行为不变**；
4. 再把逐模式补充拆成独立 Pass（为 P3 铺路）；
5. 保留 `SolverResult` 与协议 schema 不变。

**结构门禁**（新增）：
- 请求级预算必须经 `SearchBudgetLedger`；禁止 coordinator 内新增裸的 `Math.Min(<常数>, profile.…)` 预算切片（允许清单过渡）。

**退出标准**：语料逐位一致；诊断模式名与输出保持；`SEARCH_SESSION` 汇总口径不变。

**回滚**：adapter 层可整体回退到旧 `SolveCore`。

**工作量**：约 3–4 周。**风险**：中高；预算语义必须严格等价，否则会大面积退化。

---

## P3 · 前沿续搜框架

**目标**：用一个调度器替代约 40 处 `fixedPrefixActions:` 直接构造与十几条补充通道。

**范围**：`CombatSearchCoordinator` 内所有 `new CombatBeamSolver(..., fixedPrefixActions: …)` 调用点、[`Expansion.Opening`](../../../src/Search/CombatBeamSolver.Expansion.Opening.cs)、[`Expansion.Replay`](../../../src/Search/CombatBeamSolver.Expansion.Replay.cs) 的 `ApplyFixedPrefix`、[`BlockPotionInsertion`](../../../src/Search/CombatBeamSolver.BlockPotionInsertion.cs) 的 `ReplayAdjustedRoute`。

**步骤**：
1. 定义 `ContinuationSearchRequest`：`Root`、`Prefix`、`Trait`、`PotionPolicyOverride`、`PotionBounds`、`BudgetSlice`、`Purpose`（枚举，替代字符串模式）；
2. 定义 `IFrontierContinuationSource.Enumerate()`：由登记的前缀生成器产出候选（P4 之前先用现有 `BuildOpening*` 适配）；
3. `FrontierContinuationScheduler`：按 `(Purpose, PrefixKey)` 去重、按优先级有界派发、统一用 P1 的质量模型取优、统一账本扣费；
4. 逐个把 coordinator 的模式迁移为“来源 + 参数”，删除被替代的专用函数；**保留诊断标签**（`Purpose.ToDiagnosticLabel()`）；
5. `BlockPotionInsertion` 的确定性重放保留为独立的“结构调整”，但它也必须经统一账本报告。

**结构门禁**（新增）：
- coordinator 不得再直接 `new CombatBeamSolver(... fixedPrefixActions: …)`（允许清单过渡），必须经 `FrontierContinuationScheduler`；
- 补充搜索必须声明 `Purpose`，不得新增裸诊断字符串分支。

**验证**：语料逐位一致（迁移期）；对 89/95/100 等未追平包确认新框架至少覆盖现有前缀集。

**退出标准**：coordinator 行数显著下降（目标：`CombatSearchCoordinator.cs` 从约 3,223 行降到 1,200 行以内），且语料不退化。

**回滚**：按模式逐个迁移，单模式可独立回退。

**工作量**：约 4–6 周。**风险**：高；这是本轮最大的一次结构迁移。

---

## P4 · 开局/药水/目标登记表（去硬编码）

**目标**：把逐卡/逐药分支降为登记项，扩展成本从 O(报告数) 降到 O(登记项)。

**范围**：`Expansion.Opening`、[`PotionUsePolicy`](../../../src/Search/PotionUsePolicy.cs)、`SimulatedCombatState.Potions`、目标枚举、以及 `src/Search` 内 400+ 硬编码 ID。

**步骤**：
1. `PotionValuationRegistry`：每瓶药的分类、机会成本推导（见 P7）、开局必须/可用时机、生成链规则；`PotionUsePolicy` 只按分类分派；
2. `OpeningActionRegistry`：把 `BuildOpening*` 拆成“触发条件（模拟事实）+ 候选生成 + 保路策略”的登记项；Search 不识别角色/卡池枚举；
3. `TargetPlanRegistry`：目标选择的分类与代表策略；
4. ID 字面量集中到 registry/目录；迁移现有常量，未登记保持既有行为；
5. 同步更新 [`THIRD_PARTY_ADAPTERS.md`](../../THIRD_PARTY_ADAPTERS.md) 的登记点与纪律。

**结构门禁**（新增，参考现有 cycle planning 的 pattern 检查）：
- 除 registry 文件外，Search 不得新增 `"[A-Z][A-Z0-9_]{4,}"` 形式的卡/药/怪 ID（允许清单 + 计数守卫，迁移期逐步收紧）。

**验证**：语料逐位一致；新增一个测试用登记项能产生行为而不改 Search 分支。

**退出标准**：`Expansion.Opening` 不再逐卡增长；硬编码 ID 计数进入下降通道。

**工作量**：约 3–4 周。**风险**：中；登记面必须与文档/门禁同步，避免变成新的漂移源。

---

## P5 · 串行/并行路径统一

**目标**：消除 `Expansion.Choices`、`ParallelExpansion`、`AdmittedExpansion`、`PrimaryChoiceReplay` 等处的串行/并行双实现。

**范围**：13 个含 serial/parallel 双路径痕迹的文件。

**步骤**：
1. 先抽出**单一 `ExpansionPlan`**：把动作准备、选牌枚举、药水准备、回放调度做成与执行器无关的纯计划；
2. 定义 `IExpansionExecutor`：`SerialExecutor` 与 `ParallelExecutor` 只负责消费同一计划并按相同顺序提交；
3. 逐类迁移（先选牌、后药水、再卡牌），保持选择预算、回放 512、occurrence 代表、原序提交不变；
4. 清理只服务一条路径的旧分支。

**结构门禁**（新增）：同一语义不得同时保留两套候选构造；并行执行器只允许消费 `ExpansionPlan`。

**验证**：语料 DOP1 与 DOP8 结果一致；增量严格回放仍强制 DOP1。

**实施核对（2026-09-28）**：P4 基线的 GA-SILENT-BOSS-00 在 DOP1、DOP8 下均胜利且战损 44，但第 6 回合动作次序和 expanded／transitions 已不同。P5 的纯重构逐位门槛按相同 DOP 分别对 P4 基线执行；跨 DOP 比较胜负与最终质量，旧有动作／工作量差异单列，不当作 P5 回归。

**实施状态（2026-09-28）**：串行与并行普通卡牌选择现消费同一份 `PreparedCardAction`，不再由串行路径在回放后重复读取牌型选择要求；回合尾部候选的转置准入和批次快照移交也收敛到一处。`IExpansionExecutor` 已统一父节点的子节点接收与完成提交合同，串行即时迭代与并行固定 lane 继续消费原有动作计划。同根 DOP1 全字段、DOP8 的 122 个非时序字段及 #81 无头开战根逐位结果分别与本边界改动前一致。选择／药水／回合尾部作业的派发状态机仍未统一，P5 尚未退出。

**收口记录（2026-09-28）**：`AdmittedJobScheduler` 现在从同一 `AdmittedParent` 状态派发准备、卡牌、挂起选择、药水与尾部作业；串行保留逐子节点交付和最后预算槽行为，并行保留固定 lane 与原序提交。#24/#37/#81/#89 与两个生成根的 DOP1 对 P4 同政策基线，在排除后来新增的 P8a 工作归因字段后，动作、续用、终局、工作量及剪枝逐位相同。GA-SILENT-BOSS-00 的 DOP8 对自身 P4 同 DOP 基线 122 个非时序字段全同。P5 的作业调度边界已收口；串行与并行的执行载体仍按各自原方式工作。

**退出标准**：双路径消除，行为等价。

**工作量**：约 3–5 周。**风险**：高；并行/内存所有权敏感，必须在 P3 之后做。

---

## P6 · 计划/目标层（引擎启动为一等对象）

**目标**：让“先铺垫、后收获”的跨回合计划由受保护子搜索验证，而不是靠中间分“恰好”活下来。

**范围**：[`PowerCardValuation/Commitments`](../../../src/Search/PowerCardValuation/Commitments)、[`CrossTurnPlanning`](../../../src/Search/CombatBeamSolver.CrossTurnPlanning.cs)、[`Phases`](../../../src/Search/CombatBeamSolver.Phases.cs)、[`EarlyTurnFrontier`](../../../src/Search/CombatBeamSolver.EarlyTurnFrontier.cs)。

**步骤**：
1. 泛化 `PowerCommitment` → `PlanCommitment`（能力、引擎、复制、药水链、跨回合收益）；
2. 新增 `PlanSearchPass`：对每个识别出的计划做**有界子搜索**（从原根或计划起点完整续搜），终局由 P1 质量模型裁决；
3. 地基驱动地平线：有明确计划的路线获得续期，无计划的按现有 16 回合/探针规则收敛；
4. 复用 P2 账本与 P3 调度，不新增独立预算池。

**验证**：对 100、84、101 等未追平包，在固定短搜预算下证明能找回玩家路线或明确报告边界；已达标包不退化。

**退出标准**：至少两个“引擎启动”缺口包在固定预算内追平/改善，且无哨兵退化。

**收口记录（2026-09-28）**：首回合复制计划与末段第二回合能力计划在相同开战根、政策和 VeryHigh／180 秒／DOP8 下分别改善 #101（死亡→58 战损胜利）和 #100（41→22 战损）。已达标 #90 及 GA-SILENT-BOSS-00 的动作、质量与工作量对各自基线不变。能力启动、复制、免费药链及跨回合能力有类型化 `PlanCommitment`，由模拟效果或登记模型提名，经原请求账本与前沿调度器完整续搜。计划成员的额外一个牌堆周期上限合同已验证；这些目标根未触发续期，不宣称它带来上述改善。能力的因果收益证据尚未接入生产提名，旧 `PowerCommitment.RealizedEvidence` 只是战术进展，不能冒充该证据。

**工作量**：约 4–6 周。**风险**：高；会与主搜抢预算，必须有明确优先级与证据。

---

## P7 · 评估校准 + 药水价值 + 集火表示

可并行三子批，均依赖 P0 语料与 P1 质量模型。

### P7a 评估校准闭环
- 用 P0 语料做权重/阈值灵敏度矩阵；
- 中间评分保持启发式、**不是可采纳界**；拟合只作用于中间保留，不进状态键、转置支配与终局政策；
- 输出“改动 → 各根质量变化”表，作为默认值调整证据。

**实施状态（2026-09-28）**：已支持单项权重扰动及多结果矩阵汇总；当前源码的生成根对 `EnemyHp:0.8`、`PersistentBuffDelta:1.2` 均保持 44 战损／0 药，但末位 score 低 1。此前 `CurrentEnergy:0.8` 在生成根及 #24 均质量不变。样本仍不足以校准默认值，阈值矩阵尚未做。

### P7b 药水价值与联合搜索
- 机会成本从固定 9/14/18 改为**先验 + 根上下文推导**，保留档位作下界；
- 生成链（混沌/蒸馏混沌/熵酿）建模为可搜索序列，经 P3 调度；
- 验证 61/90 等生成链包。

**实施状态（2026-09-28）**：混沌药生成链已使用分支免费来源标记和 P3 续搜调度；#90 在相同开战根从 3 战损／最终 46 HP 改善到 0 战损／最终 49 HP，原有付费药仍为迅捷与混沌两瓶。已达标生成根逐位相同。先验加根上下文的动态成本及其他生成链来源尚未实施。

### P7c 集火/目标表示
- 为每个“可能优先击杀的目标”保留独立代表；转火时机由模拟决定；
- 验证 77/97/98。

**实施状态（2026-09-28）**：当前主保路已有按 `FocusTargetCombatId` 分组的目标代表，但在较晚的必保阶段加入。把每目标代表提前预约的试验让 #97 从 21 降到 9 战损，同时使已达标的多敌 #81 从同源码基底的 8 升到 9 战损，故已撤回。P7c 尚未完成；后续先借 P8 分层诊断确定目标候选的实际去向，再改席位政策。

**验证**：每个子批一个目标包 + 一个不退化哨兵；语料整体不退化。

**工作量**：每子批约 2–3 周。**风险**：中。

---

## P8 · 性能归因 + 鲁棒性 + 工具

- **P8a 性能归因**：统一请求账本按机制维度归因；对 13 个 180 秒超时包定位节点/时间/转置/预算互抢主因；不改变正确性口径。

  实施状态（2026-09-28）：常驻会话已在新发生的超时请求上保留最后一份同报告、同请求时间窗的监控快照，补充当前成员节点上限、展开／结束节点和回合层。请求总账本按 `ContinuationPurpose` 记录续搜成员，并用 `DirectSearchPurpose` 区分主 Beam、宽度精炼、药水审计／梯度、回合发现、侦察和新颖性成员；未标注的直接求解器仍列 `UnattributedDirect`。生成根的 70,460 个展开节点归为主搜 9,017、精炼 15,983、能力成员 45,460；#81 的 161,521 个展开节点归为主搜 22,981、Smart 药水梯度 50,000、能力成员 88,540。历史 13 包缺少末段证据，尚不能逐个判定单一主因；只在特定开关下运行的成员仍需实际触发验证。
- **P8b 鲁棒性分层**：把“不可执行/未支持”判定提前到组合构造阶段，失败只淘汰该候选；禁止静默回退与宽泛捕获。

  源码核对（2026-09-28）：提案所举 #44／#66／#68 是历史故障，现有零费开局来源从同一模拟分支连续生成动作，其他组合前缀使用回放合法性检查；抽弃牌状态和结束回合后续动作也已有专门修复。本轮未发现新的具体组合失败，不新增通用异常吞掉或跳过路径；P8b 的新场景验收仍未完成。
- **P8c 工具自动化**：模型编号映射、检查点恢复前置检查、一条命令回答“路线在哪层消失”、离线 ABBA 批量。

  实施状态（2026-09-28）：现有离线 `first_loss.py` 改为按求解器身份和边界编号配对，区分全局 Beam 与后续仲裁落选；小型构造测试覆盖多个成员的同号边界。`route_divergence.py` 可从两份同根、同政策的无头结果提出待追踪的首个不同动作；#97 的较优实验路线第 2 步先打致死性，旧路线先打灵体，早于不同攻击目标。它仍未证明搜索内的丢路层，玩家 ZIP 一条命令定位、模型映射和离线 ABBA 尚未实现。

**验证**：超时包给出单一主因；构造“两个各自合法但组合冲突”的场景不再整请求失败；给定问题包输出分层结果。

**工作量**：约 3–4 周（可分散进行）。**风险**：中低。

---

## 3. 依赖与里程碑

```text
P0 ──> P1 ──> P2 ──> P3 ──> P4 ──> P5
                            └──> P6 ──> P7 ──> P8
```

- **里程碑 M1**（P0+P1）：质量模型统一、语料可用；此时可安全做后续结构改动。
- **里程碑 M2**（P2+P3）：搜索收敛为一条管线 + 续搜框架；coordinator 大幅瘦身。这是本轮最重要的里程碑。
- **里程碑 M3**（P4+P5）：登记表与执行器统一；扩展成本与维护成本进入下降通道。
- **里程碑 M4**（P6+P7）：行为增强开始；以未追平包为验收目标。
- **里程碑 M5**（P8）：工程收尾与证据自动化。

粗估总周期约 6–9 个月（单人全职口径），可按子批并行压缩；每个里程碑都应可独立发布一次内部质量基准。

---

## 4. 结构门禁与文档同步清单

每阶段提交时必须同步：

1. `tools/verify-refactor-boundaries.ps1` 与 `.sh`（等价规则）：新增对应“禁止回流”检查；
2. `docs/ARCHITECTURE.md`：更新所有权与职责地图；
3. `.agents/skills/architecture-boundary-refactor/SKILL.md` 中对应的边界条款；
4. `docs/refactoring/refactor-roadmap.md`：追加本轮批次状态；
5. `docs/TEST_MATRIX.md`：新增可重跑场景；
6. 行为增强阶段（P6/P7）另更新 `docs/DEVELOPMENT_NOTES.md`，玩家可感知时走 [release-gate skill](../../../.agents/skills/release-gate/SKILL.md)。

**建议新增的门禁条目**：
- 唯一终局质量比较入口；
- 请求预算必须经 `SearchBudgetLedger`；
- 补充搜索必须经 `FrontierContinuationScheduler` 并声明 `Purpose`；
- Search 不得新增卡/药/怪 ID（允许清单 + 计数守卫）；
- 执行器只消费 `ExpansionPlan`。

---

## 5. 风险登记与缓解

| 风险 | 概率 | 影响 | 缓解 |
|---|---|---|---|
| P1/P2 重构改变终局路线 | 中 | 高 | P0 语料逐位对照；纯重构单提交回退 |
| P2 预算语义不等价导致大面积退化 | 中高 | 高 | adapter 包旧流程；预算分配显式化并逐项对照 |
| P3 迁移丢诊断/丢前缀导致个别包退化 | 中 | 中 | 保留 Purpose→诊断标签；逐模式迁移与单模式回退 |
| P4 登记面成为新漂移源 | 中 | 中 | 登记与文档/门禁同提交；未登记显式边界 |
| P5 并行所有权引入偶发问题 | 中 | 高 | DOP1 与 DOP8 双向对照；强制增量严格回放走 DOP1 |
| P6 计划子搜索抢预算 | 高 | 中 | 统一账本 + 显式优先级 + 固定短搜预算验收 |
| P7 校准过拟合到玩家报告集 | 中 | 中 | 语料只作方向参考；不进状态键/终局政策；多分布抽查 |
| 总周期长、中途断层 | 中 | 中 | 每里程碑独立可交付；先做 M1/M2 取得结构性收益 |

---

## 6. 完成定义与度量

重构完成以**结构指标 + 行为指标**共同判定：

**结构指标（目标值）**
- `CombatSearchCoordinator.cs` 行数 ≤ 1,200；
- `src/Search` 内大写常量式 ID 字面量在 registry 外的数量进入下降通道（当前 413 个不同值）；
- 终局质量比较入口唯一；存在串行/并行双实现的语义点归零；
- 补充搜索 100% 经统一调度器，均声明 `Purpose`。

**行为指标**
- P0 语料：已达标根结果不变；未追平根逐步改善或明确报告边界；
- 13 个超时包逐个给出单一主因；
- 新增策略机制不需要在 coordinator 写新分支。

**质量红线（任一不满足即不算完成）**
- 语料出现非预期退化且无解释；
- 为通过把预算/Beam 无证据扩大；
- 引入静默回退或宽泛捕获。

---

## 7. 每阶段执行模板（供后续立项复用）

1. **立项**：写明迁移对象、当前/目标所有权、必须保持的不变量、允许/禁止依赖、代表场景；
2. **基线**：跑 P0 语料并保存对照；
3. **实现**：一个提交一个边界；纯重构保持方法体/顺序/可见性；
4. **门禁**：更新双端结构门禁；
5. **验证**：Release 编译 + 结构门禁 + 一个穿过边界代表场景 + 语料对照；
6. **记录**：更新 ARCHITECTURE/roadmap/TEST_MATRIX 并直接提交；
7. **停损**：出现未解释差异即回退，不带着可疑状态前进。

---

本计划是大重构提案；执行顺序、工作量估算与目标值需按实际验证调整。开始任一阶段前，先完成 P0 并在对应 skill 下立项。
