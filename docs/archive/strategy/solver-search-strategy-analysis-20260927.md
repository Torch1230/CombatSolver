# CombatSolver 求解与搜索策略深度分析

本文回答一个问题：**求解器在一场战斗里到底怎样“想”下一步，为什么留下某条路线，又为什么明明有更好的打法却可能搜不到。** 它按当前源码逐层说明评分、展开、保路、剪枝、预算、药水策略与最终选路，并在末尾指出结构性盲点与核对方法。

**源码快照：2026-09-27，分支 `main`，已提交 `8915a7c0`。** 这是对源码的静态阅读与分析，不是行为改动，也没有新增算法验证；所有“实际收益”结论以源码、测试矩阵和发布日志为准。本文接续并更新早期说明 [搜索逻辑详解（2026-09-12）](search-logic-explained-20260912.md)：2026-09-12 之后新增的前两回合深入探索、能力牌逐卡估值、精确 N 瓶用药梯度、格挡药插入等已并入本文。方法名与文件是定位锚点，用量而非行号，避免后续重构使行号漂移。

```text
Entry / 回合 Hook
  -> SolverController（主线程会话、续用、部署）
  -> CombatRootSnapshot.Capture（主线程稳定根 + 冻结策略）
  -> CombatSearchCoordinator（请求级编排：主搜、药水审计、补充搜索）
  -> CombatBeamSolver（分阶段 Beam 分支搜索）
  -> SolverResult
  -> SolverOverlaySnapshot.Capture / 原版部署入口
```

---

## 1. 阅读前提：四种“评分”和三种“数量”

**不要把求解器理解成“给每张牌打分，再从高到低出牌”。** 它真正分叉状态、逐动作模拟、再保留一部分；评分只决定谁活下来。四个判断层必须分开看：

| 判断层 | 它回答什么 | 主要依据 | 不能代表什么 |
|---|---|---|---|
| 选牌估值 `CardValue` / `RemovalPriority` | 选牌窗口先试哪张、移除哪张 | 基础伤害、格挡、抽牌、牌类别与移除修正 | 这张牌在整副牌里的真实价值 |
| 状态基础分 `Snapshot.Score` | 做完这一步后局面“眼下”多好 | 威胁投影 HP、累计战损、敌 HP、成长/遗物额度等 | 整场最终战损 |
| 中途排名 `BeamRankScore` + 保路 | 预算有限，哪些中间局面值得留着搜 | 基础分 + 持续效果、能源、未来资源、多样性通道 | 最终必须采用的路线 |
| 完整路线排序 `FinalPlanOrdering` | 已搜出的完整路线里采用哪条 | 胜负、存活、政策、战略战损、目标、回合等逐项比较 | 所有合法路线中的全局最优 |

另有三种数量经常被混用：

- **展开节点**：选中一个状态并尝试向外生成动作的次数；
- **状态转移**：每次真实模拟卡牌、药水或回合推进的步数；
- **选牌分支**：一次动作内部遇到选牌时尝试的不同选择。

一次请求的总工作量要读 `SearchRequestWorkTotals`（展开、转移、选牌、耗时、分配、GC、循环回放额度），不能只看最终被采用那一轮的 `ExpandedNodes`。

---

## 2. 一局求解的生命周期（请求级编排）

编排入口是 [`CombatSearchCoordinator.Solve`](../../../src/Search/CombatSearchCoordinator.cs)。一次请求不是“跑一次 Beam”，而是**多个求解共享一份时间/节点/内存预算的流水线**。

### 2.1 主搜索

1. 主线程捕获 [`CombatRootSnapshot`](../../../src/Runtime/CombatRootSnapshot.cs) 并冻结 [`SearchPolicySnapshot`](../../../src/Search/SearchPolicySnapshot.cs)；后台只收到不可变根与策略。
2. `SolveCore` 决定起始用药策略：
   - 存在**逐槽强制指令**且全局策略为 `Smart` 时，先生成一份“只使用被强制药水”的**强制药基线策略**；
   - 无强制指令且策略为 `Smart` 时，主搜先用**无药覆盖**（`Disabled`）跑，得到无药基线。
3. `RunPrimary` 跑主搜索：启用 `UseNoveltyPortfolio` 时走新颖性+Beam 组合，否则走普通 Beam（可能内部是宽度组合）。
4. 若强制药基线抛出“药水政策无法满足”，回退到可选用药重跑主搜索。
5. 主搜索完成后，进入一连串**开局补充搜索**（见 2.3），然后交给药水审计。

### 2.2 药水审计与精确 N 瓶梯度

`RunSupplementalAudits` 是药水相关搜索的总入口：

- **必需药审计**（`AuditRequiredPotionUse`）：`RequireAtLeastOne` 且尚未用药时，先跑无药，再为开局用药、双药配对与“药水后防御”建立后验。
- **Smart 用药梯度**（`SearchSmartPotionGradient`）：按“恰好 N 瓶”分层搜索。第 N 层强制 `minimumPotionUses == maximumPotionUses == 强制瓶数 + N`，即**精确用量层**，让“少用一瓶”和“多用一瓶”在同一口径下竞争。达到 `HasReachedAcceptableBattleHpLoss` 或预算耗尽即停，不保证遍历所有药量。
- **开局药水前缀后验**：对开局必须用的药水、格挡药、生成牌药水、`SetFreeThisCombat` 类药水，构造有界前缀并完整续搜；涉及 `BLOCK_POTION`/`SetFreeThisCombat` 时前缀上限 12，否则 8。
- **能力×药水协同前缀**：取前 3 张可打能力，各配每槽首个药水与手牌增量。

**层间内存**：`SmartLayerMemoryForecast` 只决定“这一层边界是否值得做一次可选回收”，依据新区域容量、转移高水位与安全系数（约 1.5），不改变各层可行性判定。

### 2.3 补充审计与救援

除药水外，主搜完成后还有多条有界补充路径（均为 `fixedPrefixActions` 从原根严格重放）：

- 零费开局（首回合直接结束且仍有战损时，最多四步零净费用动作）；
- 免费进攻后的手牌整理/换手；
- 开局目标变体、多目标集火；
- 延后开场能力（首回合空过、下回合再开）；
- 帝皇蟹转火、复制作战药提前、中期精炼、回合末选择后验；
- 能力开局路线组合（见 2.5）；
- 无药即死时的双药开局对、死亡救援、近零战损精炼。

### 2.4 无胜利时的预算升级

[`CombatSearchCoordinator.FailureRecovery`](../../../src/Search/CombatSearchCoordinator.FailureRecovery.cs) 在“主搜 + 审计后仍无完整胜利”且有剩余时间时，扩大搜索面重搜：

- 每轮把 Beam 与节点各乘 `NoVictoryEscalationFactor = 2`（第 1 轮 ×2、第 2 轮 ×4），最多 `MaximumNoVictoryEscalations = 2` 轮；
- Beam 硬上限 `512`，各类分支硬上限 `100`；
- 预算不足、或各维度都已触顶（没有真正变大）就停止；
- 每轮从原根重搜，且必须**严格改善既有主质量**才替换，更宽不等于更优。

`policy.FixedBudget`（测试/API）禁止这类升级。

### 2.5 组合与实验开关

| 机制 | 默认 | 作用 |
|---|---|---|
| 宽度组合 `BeamWidthPortfolio` | 开（`UseBeamWidthPortfolio`） | 在同一根上依次跑多宽度/多保留策略成员，共享节点预算，整条取优 |
| 能力成员 | 根含已登记能力牌即运行 | 同宽度 + 激进承诺席位；至少取得请求节点上限的 1/5 专用预留 |
| 有界新颖性 `UseNoveltyPortfolio` | 关 | 先做有界 BFWS 探索，再把剩余预算交给 Beam，两结果只按终局政策取优 |
| 前两回合深入探索 `UseEarlyTurnExploration` | 关 | 常规搜索后保留第 1/2 回合末各最多 24 个状态，从原根完整续搜 |
| 能力前缀组合 `RunOpeningPowerRoutePortfolio` | 主搜成功时自动 | 对每张可打能力跑固定前缀完整搜索（≤12 前缀），按前缀数选 4 或 2 种 Beam 变体 |
| 格挡药插入 `BlockPotionInsertion` | Smart 无强制时自动 | 对首个预计掉血 ≥ 9 的回合，把格挡药插到结束回合前，逐动作重放验证 |
| 学习型组合选择器 `BeamPortfolioSelector` | 关 | 仅环境注入模型时启用，决定“某一成员不跑”，不改评分/保路/终局 |

---

## 3. 搜索树与展开

### 3.1 节点与边

节点是 [`SearchNode`](../../../src/Search/CombatPlan.cs)（`internal sealed record`），表示“从根执行一串动作后的状态”，同时保存：动作、回合、动作数、用药数/用药战略成本、路线特征、`StateKey`（战斗状态指纹）、边界原因、是否终态、父节点、模拟快照与战斗进度。父链用于恢复完整动作序列、识别开局谱系、循环与保路上下文。

边界原因 `SearchBoundaryReason`：`None / Shuffle / NoCards / UnsupportedEffect / DynamicResolution / PendingChoice / EventDefeat / TurnLimit / NodeLimit / TimeLimit / MemoryNoProgress`。`IsTerminal` 在构造时按“玩家死 / 敌人全灭 / 边界非 None”计算。

### 3.2 动作生成

[`CombatBeamSolver.Expansion`](../../../src/Search/CombatBeamSolver.Expansion.cs) 的 `Expand` 遍历手牌：

- 用 `CanPlayCard` 过滤实际可打；
- 对**语义等价的可打实例去重**（`BuildPlayableCardKey` 含 ID、升级、能量/星能消耗、重放次数、附魔、灾厄、动态变量等），保留身份差异；
- 按目标类型枚举目标（`TargetsForEnemy` 型对每个可命中敌人各一个目标）；
- 药水还要通过空槽、可模拟、`AllowsPotionUse`、用药额度等检查；
- 结束回合候选单独构造，含 `TurnStartChoices` 时走选择解析。

### 3.3 单节点分支上限与代表

`SelectActionCandidates` 先按节点分降序排序，再取 `MaxCardBranchesPerNode`：

- 必保：偷窃资源恢复目标、当前回合路由选择、复活窗口（可溢出 limit）；
- 七个动作类别各保留一个代表（即时防御、即时输出、资源循环、持续成长、控制、目标移除、生命投资）；
- 每个有正伤害的目标组保留一个代表；
- 其余按分填满。

### 3.4 选择枚举与回放预算

具体选择（弃牌、检索、置顶、本回合免费、生成、消耗目标等）由 [`CardChoiceSupport.BuildChoices`](../../../src/Search/CardChoiceSupport.cs) 生成：

- 手牌选择用 `MaxHandChoiceBranchesPerAction`，牌堆选择用 `MaxPileChoiceBranchesPerAction`；
- 精确单卡路由（`MaxCount == 1` 且移动类效果）扩大到覆盖全部选项；
- 手牌多张弃牌组合上限 `min(256, limit × 8)`；
- 身份敏感选择额外保留 2 个物理实例代表；
- 回放工作额度：`ResolveFiniteChoiceReplayAttemptLimit = min(512, max(1, 保留叶子 × 4))`，即**每个保留叶子 4 次、全局硬上限 512**；
- [`PrimaryChoiceReplay`](../../../src/Search/CombatBeamSolver.PrimaryChoiceReplay.cs) 在前缀可保留时，为每个语义兄弟预派发一次“必经”回放，保证原预算偶数分配不因完成次序被某一支独占；
- 请求级另有 `MaximumCycleReplayActions = 4096`（所有子策略共享的有界终局探测额度）。

### 3.5 开局前缀

[`CombatBeamSolver.Expansion.Opening`](../../../src/Search/CombatBeamSolver.Expansion.Opening.cs) 提供一组“从原根真实回放过的前缀”构造函数，供协调器组合：

- 能力：开局可打能力、检索出的能力、升级后再打的能力、白噪声生成的能力、双能力续接；
- 药水：开局必需药、格挡药、生成牌药、`TouchOfInsanity`、赌徒特酿全换；
- 手牌与资源：手牌整理（弃牌/弃后抽）、手牌循环、加分类资源牌；
- 后续：进攻续接、免费进攻、换手、防御续接、开局 setup、噩梦复制目标、提前复制药水等，全部按“每个目标/每类取有限代表”封顶。

前缀必须由 `ApplyFixedPrefix` 建立**真实父链**（不能用已回放快照伪装成 `action_count = 0` 的根），含回合准备时还要逐一验证每个 `TurnSetupRoots()` 可回放。

### 3.6 回合层调度与预算

`SolveCore` 主循环把前沿分成“本回合进行中”和“已结束回合”，并给每个回合层分配工作量：

```text
剩余预留层数      = max(1, 预留层数 − 已搜索层数)
当层节点额度      = max(500, 剩余节点 / 剩余预留层数)
当层时间额度      = max(250 ms, 剩余时间 / 剩余预留层数)
```

- 预留层数：普通房 4、Boss 房 8（与“压制地平线”复用同一常量），早回合侦察时为侦察深度；
- 条件满足时切层：还有后续预留层、当前回合已有可推进的结束回合候选、且当层节点或时间额度用尽。三层 Boss 特化旁路时间切层、仍用节点切层；
- 无限循环探测、跨回合无进展、`SetupValueHorizonTurns = 16`：它是**最小跨回合无进展视野**与 UI 投影视野，不是总回合上限；每次历史战局改善都会重启窗口；
- 增量严格验证另有 32 回合上限。

**动态路线预览**按 `ProgressUiIntervalMilliseconds = 200 ms` 节流刷新（进度 UI 另有 100 ms 节流），强制定稿与最终结果不受节流影响。

### 3.7 循环、有序变异与新颖性

- **循环族 `CycleFamily`**：按回合、最小动作周期与规范动作序列识别同族；兄弟分支在相同动作深度共享已支付的观察工作。严格进展最多四级扩展，单族保留深度 ≤128、出口探针展开 ≤256、单个出口 ≤32 动作与 2 次回合转移；单回合预算 `clamp(节点上限/128, 64, 256)`。
- **循环区域 `CycleRegion`**：只按回合与控制形状合并组合爆炸（不含精确排列）；每区域 64–256、探针 64–128，同回合共享普通 ≤512、探针 ≤256。
- **有序操作 `OrderedMutation`**：动作集合相同但顺序不同时保留有界租约；全 solver 最多 2048 次准入、每层 48、每根基础 128、初始通道 64、每派生租约 16，另有 16 次受保护准入。
- **有界新颖性 `BfwsPackedNovelty`**：特征来自当前影子快照（牌区/升级/数量、抽牌前缀、能力、资源、Power），按 `(EnemyHp/10, PotionCount)` 分区；open 上限 2048、历史条目上限 1,000,000、宽 2、`FamiliarAllowance = 0`（无额外逃逸预算）。新颖性表只属于新颖性搜索，普通 Beam 不创建。

---

## 4. 状态基础分 `Snapshot.Score`

基础分只用于中间判断与去重，不是显示的 HP。权重常量集中在 [`SolverWeights`](../../../src/Search/SolverWeights.cs)，计算在 [`StateEvaluation`](../../../src/Search/CombatBeamSolver.StateEvaluation.cs)。

### 4.1 常量全表（源码逐字值）

| 常量 | 值 | 含义 |
|---|---:|---|
| `DeathPenalty` | −1,000,000,000,000 | 死亡 / 投影致死 |
| `VictoryBonus` | 10,000,000,000 | 确定的完整胜利 |
| `Hp` | 30,000 | 每点 HP（注释：稳健预设下 1 HP ≈ 3 点即时伤害） |
| `EnemyHp` | −10,000 | 每点敌方有效 HP |
| `RiskPenalty` | −2,000 | 风险标记 |
| `ActionPenalty` | −1 | 每步动作 |
| `VulnerableAttackWindowCap` | 24 | 易伤可利用攻击窗口上限 |
| `VulnerableAttackMultiplierBeamValue` | 5,000 | 集火目标每层易伤 |
| `OffTargetVulnerableAttackMultiplierBeamValue` | 1,000 | 非集火目标每层易伤 |
| `CurrentEnergyBeamCap` / `CurrentEnergyBeamValue` | 6 / 60,000 | 当前能量（仅 Beam） |
| `PersistentBuffDeltaBeamCap`（Boss）/ `Standard…` | 32 / 4 | 持续效果正增量上限 |
| `PersistentBuffDeltaBeamValue`（Boss）/ `Standard…` | 50,000 / 150,000 | 持续效果每点 |
| `LatentSetupBeamCap` / `LatentSetupBeamValue` | 24 / 12,000 | 未落地铺垫潜力 |
| `ReplayPotentialBeamCap` / `ReplayPotentialBeamValue` | 64 / 10,000 | 重复利用牌潜力 |
| `LongTermResourceBeamValue` / `LongTermResourceBeamCap` | 25,000 / 25,000 | 长期资源（封顶低于 1 HP 权重） |
| `AngerCopyBeamPenalty` | −15,000 | 生成的愤怒复制 |
| `RetainedAttackGrowthBeamCap` / `…Value` | 16 / 20,000 | 保留攻击价值正增量 |
| `FutureResourceBeamValue` | 10,000 | 后续资源 |
| `DelayedDamageBeamValue` | 10,000 | 延迟伤害 |
| `SandpitTurnBeamValue` | 30,000 | 沙坑剩余 |
| `EnemyStrengthSuppressionBeamCap` / `EnemyWeakTurnsBeamCap` | 16 / 16 | 敌方力量压制 / 虚弱层 |
| `Standard/BossEnemyStrengthSuppressionHorizon` | 4 / 8 | 压制估计窗口 |
| `Standard/BossEnemyWeakExpectedHpSaved` | 1 / 2 | 虚弱预计省血 |
| `LiveDeckClutterPenalty` | −8,000 | 活动牌库杂质 |
| `OutstandingStolenResourcePenalty` | −1,000,000 | 未追回被偷资源（保留资源模式） |
| `SoldHpPenalty` | −20,000 | 主动卖血（叠加在 30,000 之上） |
| `PotionMinimumHpSaved` | 9 | 基准瓶机会成本 |
| `PotionHighValueHpSaved` | 18 | 高价值瓶 |
| `PotionElevatedValueHpSaved` | 14 | 较高价值瓶（13.5 向上取整） |
| `DeathSavePremiumPercent` | 900 | 一次性保命溢价（复活血量的 9 倍） |
| `SetupValueHorizonTurns` | 16 | 跨回合无进展视野 / UI 投影视野 |
| `MinimumTurnLayerExpandedNodes` | 500 | 当层节点下限 |
| `NoVictoryEscalationFactor` / `MaximumNoVictoryEscalations` | 2 / 2 | 无胜利升级 |
| `MaximumEscalatedBeamWidth` / `MaximumEscalatedBranchesPerAction` | 512 / 100 | 升级硬上限 |

### 4.2 基础分公式（按代码顺序）

```text
score = 死亡 ? DeathPenalty
              : projectedHp × 30,000
      + (player.MaxHp − root.InitialPlayerMaxHp) × 30,000
      − cumulativeHpLost × 30,000
      − DeathSaveBeamCost(死亡豁免回复量) × 30,000       // DeathSaveBeamCost ≈ 10 × 复活血量
      + min(realizedLongTermResourceValue × 25,000, 25,000)
      + growthHpCredit × 30,000
      + (relicCounters.HpCredit + relicCounters.HealingHpCredit) × 30,000
      + relicCounters.SatisfiedPriority × 0.1 − relicCounters.Distance × 0.001
      + angerCopiesGenerated × (−15,000)
      + (won && !uncertainVictory ? VictoryBonus : 0)
      + enemyHp × (−10,000)                              // 含待复活敌人
      + liveDeckClutter × (−8,000)
      + (保留资源模式 ? outstandingStolenResource × (−1,000,000) : 0)
      + 易伤项（见下）
      + actionCount × (−1)
      + (risk ? −2,000 : 0)
```

易伤项：

```text
vulnerableAttackWindow = min(24, retainedAttackValue)
score += focusTargetVulnerableTurns × vulnerableAttackWindow × 5,000
      +  max(0, vulnerable − focusTargetVulnerableTurns) × vulnerableAttackWindow × 1,000
```

要注意两点：

- **投影 HP 与累计战损会同时进入基础分**，所以不能概括成“1 血等于 3 伤害”；回血、根时点、保命资源都会改变实际差值。
- `recoveredPlayerHp`、格挡、`MaximumHp` 缺口等**不直接进基础分**；格挡只通过投影 HP 间接体现，超过总来袭伤害的格挡价值为 0。
- 主动卖血惩罚（`SoldHpPenalty`）不在 `Snapshot.Score` 内，而在出牌/回放后由 `ApplySoldHpPenalty` 叠加。

### 4.3 威胁投影 `ProjectHpAfterThreat`

它是“如果现在结束回合，敌人意图结算后还剩多少 HP”的快速估计（不是对后续若干回合完整最优防守的预测）：

- 遍历当前怪物行动，跳过已死、跳过“下回合跳过”的敌人；
- 普通攻击逐次命中，经伤害修正、格挡吸收、Osty 转移、瓶中精灵/蜥蜴尾复活顺序处理；
- 已知偏差：只有 `EXPLODE_MOVE` 的强制行动会被投影，其他强制行动直接跳过；与原生 intent 面板的一致性尚未证明；
- 死亡预防（瓶中精灵按槽数、未使用蜥蜴尾）从 0 起算，不修改分支 Power；
- 同一状态键 + 回合索引有缓存；胜利或已到边界时直接返回当前 HP。

### 4.4 持续能力与成长估值

能力的中间价值分三块：

1. **已落地效果**：[`StrategicEffectModel`](../../../src/Search/StrategicEffectModel.cs) 把 Power 投影为五类潜力之和 `RetentionValue = Damage + Prevention + Resource + CardAccess + Scaling`。举例：力量按攻击命中次数、敏捷按格挡技能机会、余像按可打出牌数、墨水按消耗次数、恶魔形态按剩余回合的递增攻击。上下文（剩余回合、攻击/技能/能力机会、抽牌、平均攻击价值、来袭伤害）由当前牌组与威胁推导。
2. **尚未落地的铺垫潜力** `latentSetupValue`：按牌面静态潜力（如 Echo Form 12、Buffer 8、部分集中来源 6、普通能力 2）。
3. **后续资源潜力** `futureResourceValue`：下回合能量 ×16、抽牌 ×8、星能 ×8、保留手牌 ×4，加召唤物、保留牌与免费机会。

另有一套独立的[能力牌逐卡估值注册表](../../../src/Search/PowerCardValuation)：六个卡池共 104 张单人能力牌（铁甲战士 19、静默猎手 17、故障机器人 20、储君 18、亡灵契约师 18、无色 12）。公共层只持有卡池无关的描述符（卡池、稳定 `CardId`、机制族、路线准入策略），`Cards/<Pool>` 保存逐卡模型与投影。**当前生产搜索实际消费的是**：路线准入策略、开局投影、触发证据、以及“对当前可打能力的固定前缀完整后验”；`Reward/Penalty/Timing` 评估接口目前主要是合同与文档层。承诺席位由 `PowerCommitmentSeatPolicy` 按 Beam 宽度分配。

---

## 5. 中间排名与保路

### 5.1 `BeamRankScore`

[`BeamRetentionPolicy.Ranking`](../../../src/Search/CombatBeamSolver.BeamRetentionPolicy.Ranking.cs) 在节点基础分上加一层只服务中间搜索的估值：

```text
BeamRankScore = baseScore
  + min(6, Energy) × 60,000
  + min(persistentCap, max(0, PersistentBuffValue − 初始值)) × persistentValue
  + （Boss 或多敌 ? min(24, LatentSetupValue) × 12,000 : 0）
  + （Boss ? FutureResourceValue × 10,000 : 0）
  + min(64, ReplayPotentialValue) × 10,000
  + RetainedAttackGrowth × 20,000
  + DelayedDamageValue × 10,000
  + SandpitRemaining × 30,000
  + min(16, max(0, 敌方力量压制增量)) × 压制窗口 × 30,000
  + min(16, max(0, 虚弱增量)) × 预计省血 × 30,000
```

其中 `persistentCap/persistentValue`：Boss 房为 32 / 50,000，其他为 4 / 150,000；排序先按 Beam 分降序，再动作数升序，再 `OffensiveProgressValue` 降序。`BaseScoreOnly` 成员直接返回 `node.Score`，不叠加这些项。

一个实验性修正 `ContinuousThreatRanking` 会在“已结束回合但仍会投影致死且有可打手牌”的中间节点，把死亡惩罚替换为连续 HP 赤字，仅影响中间排序。

**近视来源**：开能力会立刻花能量、降低能量分；如果收益没被模型识别或已封顶，开能力后的中间状态可能输给保费或打防。

### 5.2 状态键去重

普通路径按精确 `StateKey` 去重，同键留节点分更高者、同分偏好动作少者。最终质量优先路径（`finalQualityFirst`）**不去重**，保留相同模拟状态下不同累计战损/政策历史，因为终局排序要按政策前缀比较。

### 5.3 代表通道（保路不是取分前 N 名）

`RankBest` 在初排后按类别挑代表，组装必保候选、路由候选、牌序代表，再过容量与药水配额。主要通道（数量级）：

- 完整胜利按用药数分组，每组 1；
- 跨回合 stand-pat 资源与 Scaling/Resource/Control 泳道，各 1；
- 开局动作谱系：`clamp(limit/8, 4, 16)`，每谱系保留最佳分/防御/进攻/setup；
- 有序牌堆与洗牌前缀：战术组 → 节奏桶 → 节奏族，每代表受 `ExactStatesPerProjectedShuffleOrder = 1`、`OrderedPileVariantsPerTacticalState = 24` 限制；
- 路由选择：上限 `RoutingChoiceLimit = 96`，族/选项/上下文分别按父名次、分与 setup 代表轮转；
- 药水谱系、每用药数组基本代表、目标多样性、setup 特征、Art of War、DeclinedExtraTurn 等；
- 全局 Pareto cohort：按 `(敌方战斗分布, 敌方控制分布, 无序牌堆键)` 分组，每组最多 3 条；
- 能力承诺席位（见 5.6）；
- 开发策略脚本保留 ≤1 条。

**“必留”是当前仲裁里的优先加入机制，不是永远免剪枝**；外层 `Prune` 之后仍可能被配额与候选人仲裁抵消。

### 5.4 药水配额与路由签名

- 药水配额 `FeasiblePotionUseQuotas(limit)`：`used = limit < 4 ? 1 : max(2, limit/3)`，其余留作无药；只在使用药水政策非 `Disabled` 且池中两种路线都存在时启用。
- 路由签名只接受“会持久改变牌区/手牌的可选项”（移动到手牌、置顶、弃牌、弃后抽、本回合免费、生成到手牌），多卡选择折叠为基数；检索窗口受 8 回合与 Art of War 触发影响。

### 5.5 转置与两种支配剪枝

- **转置表**：键为战斗状态指纹，值为 `TranspositionFrontier`，标签为 `(PotionCount, PotionStrategicCost, FutureSoldHp, CumulativePlayerHpLost, ActionCount, Score)`；单标签内联、出现第二个互不支配标签才分配列表，缩回单标签即释放。候选准入表与展开准入表共用默认 **1,000,000** 条预算，满了只对新状态停止记账并放行。
- **动作级支配**：要求纯动作、同循环/出口证据族、同健康风险桶、同卡类型/目标/选项族/能量星能消耗，再比较伤害、格挡、HP、最大生命、累计损失、长期资源、成长/遗物额度、愤怒复制；各项不差且至少一项严格更优。
- **多目标 Pareto**：先要求 `(敌方战斗分布, 敌方控制分布, 无序牌堆键)` 相同，再比较约 35 个维度（HP、资源、能力、延迟伤害、压制、费用、星能、未来资源、保留攻击、牌库杂质、药水、卖血、动作数等）。

支配建立在所选特征与上下文上，不是对未来所有打法的数学最优证明。

### 5.6 外层 `Prune` 与能力席位

[`CombatBeamSolver.Retention`](../../../src/Search/CombatBeamSolver.Retention.cs) 的 `Prune` 在 `RankBest` 结果之上追加循环组合、循环出口、跨回合探针、有序变异与循环区域通道，再做 `SortRetained` 与 `FinalizePrunedSelection`，最后按 `PrimaryIncumbentBound`（战略战损下界）删枝。最终保留规模可以超过 `BeamWidth`，因为各 portfolio 是追加的；`RankBest` 内部的 `effectiveLimit` 也会因路由与有序牌堆扩宽。

能力席位配额 `PowerCommitmentSeatPolicy.SeatQuota(beamWidth, aggressive)`：

```text
ordinaryFloor     = (beamWidth + 1) / 2
maximumPowerSeats = beamWidth − ordinaryFloor
requested = aggressive ? min(beamWidth/2, max(4, (beamWidth+2)/3))
                       : clamp(beamWidth/12, 2, 12)
quota     = min(maximumPowerSeats, requested)
```

能力承诺由模拟历史识别（自动/手动打出的已登记能力），以节点级租约保存（默认 2 次、激进成员 3 次回合转移），投资按能量 ×8 与前两回合的楼层折减计算。

---

## 6. 药水策略与机会成本

[`PotionUsePolicy`](../../../src/Search/PotionUsePolicy.cs) + [`PotionStrategySnapshot`](../../../src/Search/PotionStrategySnapshot.cs) 定义三层策略：

- **全局 `SolverPotionPolicy`**：`Disabled` / `Smart` / `RequireAtLeastOne`（默认 `Smart`）。
- **逐槽指令 `SolverPotionDirective`**：`Smart` / `Force` / `Disabled`；UI 预设 `AllSmart / AllProtected / AllForced / OnlyForced`。
- **每槽解析**：命中指令用指令值，未命中按全局策略。

机会成本分档（`StrategicHpCost`）：

| 档 | HP | 药水 |
|---|---:|---|
| 免费 / 可再生 | 0 | Token 稀有度；石化蟾蜍在场时的“可再生瓶形石” |
| 基准 | 9 | 其余（`PotionMinimumHpSaved`） |
| 较高 | 14 | 蒸馏混沌、清晰、辉光酊剂、万能药、液态记忆、瓶装潜能、疯狂之触 |
| 高 | 18 | 发光水、迅捷、赌徒特酿、复制、奥罗巴斯酸、食尸鬼药 |

特殊规则：

- **龙涎香**：不看 9/18 档，要求净回血 ≥ `ceil(maxHp × 0.40)`；有效成本另加 `ambergris数 × (ceil(maxHp×0.40) − 9)`。
- **生成 / 免费药**：混沌药水生成的药水、石化蟾蜍下的瓶形石在模拟中记 `StrategicHpCost = 0`；混沌药水本身仍计 9。
- **替代抵扣** `ApplyReplacementCredit`：药水栏已满、Sozu 未阻断、且战后掉药前景确定为“掉落”时，把预测药水成本抵到本路线，路径级只扣一次、下界 1 HP。
- **开局必须用药**：敏捷、集中、鱼油、液态青铜、玛扎蕾丝礼物、容量药水、士兵炖肉、力量药共 8 种，只在开局使用。
- **准入** `IsEligible`：`Disabled` 要求 0 用药；`RequireAtLeastOne` 要求 ≥1 瓶且用药路线在必要时获胜；`Smart` 允许 0 瓶，或用药路线唯一生还，或省血 ≥ 门槛（按 Boss 血量政策放大）。

**战后掉药预测** `PotionRewardOutlook` 在主线程根捕获时克隆奖励 RNG，按原版顺序重放掉落判定与药水抽取，得到确定的掉落结论与药水身份；最终 Boss 无奖励、教学奖励集不镜像。

---

## 7. 终局排序与政策准入

### 7.1 准入门（排序前硬过滤）

[`FinalPlanOrdering`](../../../src/Search/CombatBeamSolver.FinalPlanOrdering.cs) 先过滤：

1. **强制用药全部完成**；
2. **显式用药数 ≥ 最低要求**；
3. **软性药水政策**通过，或满足“严格优于无药基线”，或保留资源模式下少丢被偷资源；
4. **龙涎香限制**通过（净回血达 40%maxHp，或严格更优/少丢资源）。

没有政策合格路线时明确抛错（区分强制药、至少一瓶、无路线），不暗中忽略玩家要求。`Smart` 审计捕获该异常后回落到主结果。

### 7.2 有序比较键（字典序）

| # | 键 | 方向 |
|---|---|---|
| 1 | 完整胜利 | 优先 |
| 2 | 非胜利时不死亡 | 优先 |
| 3 | 投影保命资源使用次数 | 少 |
| 4 | 未追回被偷资源（保留资源模式） | 少 |
| 5 | `StrategicHpDeficit`（战略战损） | 少 |
| 6 | 策略目标 HP 额度 | 多 |
| 7 | 策略目标完成数 | 多 |
| 8 | 战斗结束回合 | 早 |
| 9 | 含用药折算的政策战损 | 少 |
| 10 | 实际健康资源成本 | 少 |
| 11 | 长期资源 | 多 |
| 12 | 愤怒复制数 | 少 |
| 13 | 搜索边界等级 | 好 |
| 14 | 可选药水数 | 少 |
| 15 | 累计主动卖血 | 少 |
| 16 | 敌方剩余 HP | 少 |
| 17 | 节点 Beam 评分 | 高 |
| 18 | 动作数 | 少 |

### 7.3 战略战损

```text
StrategicHpDeficit = cumulativeHpLost
                   + max(0, 初始最大生命 − 当前最大生命)
                   − 有持久价值的回血
                   + 一次性保命溢价
                   − 玩家策略 HP 额度
```

- **有持久价值的回血**按 Boss 血量政策折算：普通战 1:1、过幕回血只算 1/5、最终战回血视为 0；
- **一次性保命溢价** = 复活血量 × 9（加上被计入的复活本身，代价约 10 倍）；
- **玩家策略 HP 额度** = 成长额度 + 遗物额度（含肉骨头的阈值回血额度）；“不考虑局外收益”开关把额度置零。

`HealthResourceCost`（第 10 键）不做回血修正，是开战到结束的裸 HP 成本。

### 7.4 Boss 血量政策

`BossHpRelief` 三种：`None`（普通战）、`ActClearHeal`（第一、二幕过幕回血 80%）、`RunEnding`（本局最后一战，只要活下来）。双 Boss 的第一个按 `None` 带入第二个；玩家可把过幕/终局策略改为“最小战损”来关闭对应折算。

### 7.5 成长、遗物与偷窃目标

- **成长来源**：当前源码有 **10 个原版来源**（贪婪之手、狩猎、进食、版税、炼金、遗传算法、巨镰、黏糊、禁忌魔典、疯狂科学）+ 第三方扩展。额度向量单独维护，`IgnoreLongTermRewards` 置零但不丢玩家配置。
- **目标捕获** `GrowthOpportunityPolicy`：致命来源竞争、动态复制风险、牌组版本要求等会写成“不可证明”，不可证明时禁止纯 HP 早停。
- **遗物计数**：11 个内置计数（快乐花、假花、钟摆、花粉核、笔尖、双节棍、音叉、纸钱、铁棒、星河之尘、肉骨头），有周期、上下限与 HP 额度；`SatisfiedPriority` 打包进目标向量。
- **偷窃**：仅对绿皮佣兵/窃贼萤等适用；`PreserveResources` 模式下未追回资源越少越好，Beam 内以 −1,000,000 惩罚确保压过任何可存活 HP 交换。

### 7.6 提前停止

`HasReachedAcceptableBattleHpLoss` 要求同时满足：完整胜利、预计战损 ≤ 用户阈值、成长目标（若有则必须有界且达标）、遗物目标达标、偷窃恢复达标、未消耗保命资源、用药数恰为当前最低必要数、强制药全部使用。达到后搜索排空当前父节点/批次再收尾。另有“可证明主要质量下界”的停止判断，条件更严。

---

## 8. 预算、并行与内存

### 8.1 四档预设

| 预设 | Beam | 单次展开节点 | 每节点卡牌分支 | 牌堆选牌 | 手牌选牌 | 软时间 |
|---|---:|---:|---:|---:|---:|---:|
| 低 | 45 | 60,000 | 24 | 12 | 16 | 60 s |
| 中（默认） | 60 | 120,000 | 32 | 18 | 24 | 120 s |
| 高 | 90 | 250,000 | 48 | 28 | 36 | 180 s |
| 极高 | 135 | 500,000 | 72 | 42 | 54 | 300 s |

自定义保留显式设置；节点预算至少 100，Beam ∈ [1, 512]，各类分支 ∈ [1, 100]。

### 8.2 并行

- 初始展开 lane 按可用逻辑处理器选择：≥16 → DOP8，4–15 → DOP4，2–3 → DOP2，1 → DOP1；用户显式设置优先，上限 16。
- 固定 worker lane 并行模拟候选，协调端按确定顺序提交；并行数不是 Beam 宽度。
- 同一已准入父节点共享一个窄 Fork gate，离开后各自独占分支模拟器；`StandPatJobs`、`RetentionJobs` 复用同一 executor，不会另开线程池或与推测展开重叠。
- 详细诊断与增量严格回放强制 DOP1。

### 8.3 内存

- 父节点 wave 从 `2×DOP` 预约上限起，按实际余量与转移高水位动态增减；冷估计每父节点 `64 MiB × 1.5`，取得观测后按整次搜索最大实测分配的 1.5 倍预约，并留 96 MiB 突发余量。
- 连单个父节点都放不进预约时退回纯串行，并在已排空边界释放可重建缓存后回收。
- NoGC 区域、后台回收续搜与手动工作集释放由 Runtime 的 GC 策略拥有；Search 只消费内存压力信号，不直接操作 GC 模式。
- 内存回收只能减少存量对象，**不能把已被质量剪枝丢弃的路线找回来**。

---

## 9. 实验性 / 默认关闭的机制

以下机制在当前源码中存在但默认不启用；阅读结果与日志时必须区分：

- `UseNoveltyPortfolio`（有界新颖性组合）、`UseEarlyTurnExploration`（前两回合深入探索）；
- `AdaptiveNoveltyRefinement`（先跑原 Beam 再追加探索）、`BoundedOffensiveRefinementPortfolio`、`OffensiveRefinementPortfolio`、`ReallocatedRefinementPortfolio`；
- `ContinuousThreatRanking`、`BaseScoreTacticalTies`、`ContextualRanking`、`BeamWeightPerturbation`；
- `BeamPortfolioSelector`（学习型组合选择器，仅环境注入模型时启用）；
- `TranspositionPruningDisabledMask`、`NoveltySearchOptions` 的非默认字段等离线隔离项。

---

## 10. 为什么仍会漏掉好路线

以下是当前结构的系统性来源（不是某个具体包的根因证明）：

1. **前期成本立刻可见，后期收益依赖估值。** HP、能量、累计损失立即改分，跨多步的联动可能尚未体现。
2. **通用卡牌估值偏粗。** 点烧、检索、手牌潜力共享基础变量估值，缺少统一的“牌组角色/启动必需性”判断。
3. **能力潜力有识别范围与封顶。** 模拟支持某种效果，不等于估值完整；封顶轴无法区分半成品与完整引擎。
4. **每一步都要活着走出队列。** 最终能赢的十步组合，可能第二步就被剪掉，而终局排序无法恢复它。
5. **保路通道多而分散。** 某通道选中的节点，外层仍可能被后续配额抵消。
6. **后置补充启动较晚、覆盖有限。** 不是所有能力链和药水组合都有对应补充入口；主搜耗尽时间时后置路径可能没机会。
7. **目标与牌序代表有限。** 不同集火次序、抽牌排列与消耗组合不可能全部保留。
8. **“只有死亡路线”的含义是**：在当前预算与保留规则下没找到可用胜利解，而不是证明玩家无论如何都会死。搜索是有界启发式，不是穷举证明器。
9. **旧报告差额不等于当前缺口。** 人工起点、政策、已用药、预算和旧版本必须对齐；旧路线合法也不代表当前同预算能搜到。

---

## 11. 怎样核对一次“丢路线”

对一条玩家好路线，应逐层问：

1. 动作是否合法？
2. 模拟结果是否与原版一致（严格差分）？
3. 目标状态是否被生成？
4. 是否通过单节点分支上限与支配剪枝？
5. 是否进入 Beam 队列并被保留为下一层？
6. 在哪一次 `Prune` / 转置准入 / 配额仲裁处消失？
7. 是否产生了完整候选？
8. 最终为什么没被采用（哪一条排序键）？

同状态不同动作顺序可能被合并；此时应追踪实际保留的等价代表，而不是仅看原动作串。质量结论必须比较同根、同政策、同预算下的完整胜负、战损、药水与关键启动回合；路径诊断通过只证明诊断合同，不等价于整场质量或原生部署成功。

---

## 12. 源码阅读入口

| 想核对的内容 | 文件 / 方法 |
|---|---|
| 真实状态怎么冻结 | [`CombatRootSnapshot`](../../../src/Runtime/CombatRootSnapshot.cs) |
| 玩家政策与预算快照 | [`SearchPolicySnapshot`](../../../src/Search/SearchPolicySnapshot.cs) |
| 请求内主搜、药水与补充探索 | [`CombatSearchCoordinator`](../../../src/Search/CombatSearchCoordinator.cs) 及 `.FailureRecovery`、`.PowerRoutes`、`.NoveltyPortfolio`、`.EarlyTurnExploration` |
| 回合层与预算推进 | [`CombatBeamSolver.Phases`](../../../src/Search/CombatBeamSolver.Phases.cs) 的 `SolveCore` |
| 动作候选与支配 | [`CombatBeamSolver.Expansion`](../../../src/Search/CombatBeamSolver.Expansion.cs)、[`Expansion.Candidates`](../../../src/Search/CombatBeamSolver.Expansion.Candidates.cs) |
| 开局前缀 | [`CombatBeamSolver.Expansion.Opening`](../../../src/Search/CombatBeamSolver.Expansion.Opening.cs) |
| 选牌预算与回放 | [`CardChoiceSupport`](../../../src/Search/CardChoiceSupport.cs)、[`Expansion.Choices`](../../../src/Search/CombatBeamSolver.Expansion.Choices.cs)、[`PrimaryChoiceReplay`](../../../src/Search/CombatBeamSolver.PrimaryChoiceReplay.cs) |
| 威胁、快照与基础分 | [`CombatBeamSolver.StateEvaluation`](../../../src/Search/CombatBeamSolver.StateEvaluation.cs) |
| 持续效果与联动价值 | [`StrategicEffectModel`](../../../src/Search/StrategicEffectModel.cs)、[`PowerCardValuation`](../../../src/Search/PowerCardValuation) |
| 中间排名与保路 | [`BeamRetentionPolicy`](../../../src/Search/CombatBeamSolver.BeamRetentionPolicy.cs)、[`Ranking`](../../../src/Search/CombatBeamSolver.BeamRetentionPolicy.Ranking.cs)、[`Routing`](../../../src/Search/CombatBeamSolver.BeamRetentionPolicy.Routing.cs) |
| 外层保路仲裁 | [`CombatBeamSolver.Retention`](../../../src/Search/CombatBeamSolver.Retention.cs) |
| 转置与支配 | [`Transpositions`](../../../src/Search/CombatBeamSolver.Transpositions.cs) |
| 循环、有序变异、跨回合 | [`CyclePlanning`](../../../src/Search/CombatBeamSolver.CyclePlanning.cs)、[`OrderedMutationRetention`](../../../src/Search/CombatBeamSolver.OrderedMutationRetention.cs)、[`CrossTurnPlanning`](../../../src/Search/CombatBeamSolver.CrossTurnPlanning.cs) |
| 药水策略与机会成本 | [`PotionUsePolicy`](../../../src/Search/PotionUsePolicy.cs)、[`PotionStrategySnapshot`](../../../src/Search/PotionStrategySnapshot.cs)、[`PotionRewardOutlook`](../../../src/Search/PotionRewardOutlook.cs) |
| 终局政策与顺序 | [`FinalPlanOrdering`](../../../src/Search/CombatBeamSolver.FinalPlanOrdering.cs)、[`ActEndingBossPolicy`](../../../src/Search/ActEndingBossPolicy.cs)、[`SolverInterimResultOrdering`](../../../src/Search/SolverInterimResultOrdering.cs) |
| 成长、遗物、偷窃目标 | [`GrowthPolicy`](../../../src/Search/GrowthPolicy.cs)、[`GrowthOpportunityPolicy`](../../../src/Search/GrowthOpportunityPolicy.cs)、[`RelicCounterPolicy`](../../../src/Search/RelicCounterPolicy.cs)、[`TheftEncounterStrategy`](../../../src/Search/TheftEncounterStrategy.cs) |
| 宽度组合与门控 | [`BeamWidthPortfolio`](../../../src/Search/BeamWidthPortfolio.cs)、[`BeamWidthPortfolioGate`](../../../src/Search/BeamWidthPortfolioGate.cs)、[`PowerCommitmentPortfolioGate`](../../../src/Search/PowerCommitmentPortfolioGate.cs) |
| 新颖性搜索 | [`CombatBeamSolver.NoveltySearch`](../../../src/Search/CombatBeamSolver.NoveltySearch.cs)、[`NoveltySearchOptions`](../../../src/Search/NoveltySearchOptions.cs)、[`NoveltyPortfolioBudget`](../../../src/Search/NoveltyPortfolioBudget.cs) |
| 前两回合深入探索 | [`CombatSearchCoordinator.EarlyTurnExploration`](../../../src/Search/CombatSearchCoordinator.EarlyTurnExploration.cs)、[`CombatBeamSolver.EarlyTurnFrontier`](../../../src/Search/CombatBeamSolver.EarlyTurnFrontier.cs) |
| 权重常量 | [`SolverWeights`](../../../src/Search/SolverWeights.cs)、[`SolverSearchProfile`](../../../src/Search/SolverSearchProfile.cs) |
| 节点、动作与结果数据 | [`CombatPlan`](../../../src/Search/CombatPlan.cs) |

---

## 附：与 2026-09-12 版的差异（要点）

- 预算从“低/中/高/极高 + Short/Deep 双阶段”收敛为单次预算、四档节点 60k/120k/250k/500k；
- 新增能力牌逐卡估值注册表（104 张）与承诺席位；
- 新增精确 N 瓶智能用药梯度、开局药水前缀后验、格挡药自动插入；
- 新增能力开局路线组合（≤12 前缀 × 4/2 变体）与无胜利预算升级（×2，最多 2 轮，Beam≤512）；
- 新增默认关闭的有界新颖性组合与前两回合深入探索；
- 成长来源从文档旧口径的“八类”扩到源码的十类 + 第三方扩展；
- 多个卖血硬门槛已移除，改为战略战损逐项计价。

本文是静态阅读结论；未运行构建、测试或实机验证。需要行为结论时，按 [测试矩阵](../../TEST_MATRIX.md) 与 [开发笔记](../../DEVELOPMENT_NOTES.md) 的证据为准。
