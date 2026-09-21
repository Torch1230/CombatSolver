# 小循环的质量、性能与展示落点调研（2026-09-21）

本文件是只读调研结果，不是实现方案批准书。分支 `feat/loop-optimization-20260921`，HEAD `3f4002bd`。
所有结论都带 `文件:行`；标注「假设」的条目是尚未用夹具或实机证实的推断。

## 0. 用户提出的三个问题

1. **质量**：打完放血（自伤换能量）后，把能量花在防御上，而当前格挡已经能挡住对面全部伤害；这笔能量若去打输出本已斩杀。
2. **性能／上限**：打循环要打很多张牌，搜索缓慢或撞上限。例：每打出 10 张牌造成 6 点伤害，如果这回合能打出这个循环几百上千次，无论对面多少血都能杀。
3. **展示**：现在固定逐张显示，重复的循环（例如 8 张牌一组）把界面刷满。

用户假设「2 和 3 是同一个问题：识别到可重复循环后直接复制、多打几次」。

**调研结论（先说结果）**：

- 2 和 3 共享同一个根因：**从搜索到 UI，循环都没有一个「紧凑表示」**——搜索里每次重复都是一个真实节点、计划里每次重复都是一条真实 `PlanAction`，UI 自然只能逐张显示。但两者的修法可以完全独立：3 可以只改显示层（零 Search 改动），2 必须动搜索。
- 用户的「复制并多打几次」在**逐张真实回放**的意义上是安全且可行的（这就是下面推荐的 P2 方案）；在**解析外推**的意义上（不模拟、直接算 N 次的总和）目前**不安全**，因为代码里已经存在两处状态键缺口，且有一类计数会让每轮增量本身变化。
- 1 的根因不是「不会算斩杀」，而是**过量格挡在搜索里没有代价、反而被当成进展**：`UsefulDefensiveBlockReserve` 只按最大生命截断，与来袭伤害无关，却被用作循环区域进展、生存并列比较和必留挑选（`src/Search/CombatBeamSolver.CyclePlanning.cs:650-659`、`src/Search/CombatBeamSolver.CycleRegionRetention.cs:907`）。
- 用户「没有现成战斗场景」的判断不成立：仓库里**已经有**覆盖这三类问题的夹具（见第 7 节），包括 2000 HP、需要 1200+ 动作的「每 3 张技能牌造成伤害」长循环，和明确写着「防御价值饱和后仍在改变状态」的停滞格挡循环。

## 1. 现状：循环机制到底做了什么

### 1.1 识别：结构周期 + 伤害相位 + 两窗口一致

- 周期识别最多回看 **32 个动作**（`src/Search/CombatBeamSolver.CyclePlanning.cs:5`），先做零哈希的结构形状比较，只有真正递归的路线才付动作键哈希（同文件 `:44-64`）。
- **形状键刻意不看可变计数**：`CycleShapeKey` 只含四个牌堆的「卡牌 ID + 升级等级」形状、存活敌人掩码、药水库存和边界（`src/Search/CombatBeamSolver.StateEvaluation.cs:596-610`、`:623-649`，注释在 `:628-630` 明确说明这是为了让「只有打 N 次后才兑现的 setup 循环」也能被观察到）。
- 「可重复」的判据是二者之一（`CyclePlanning.cs:265-270`）：
  1. 相邻两个窗口的 `CycleTransitionDelta` **完全相等**（`:118-121`、`:173-175`）；或
  2. 有新的敌方耐久进展 **且** 伤害相位在两个窗口里逐位对齐（`:272-282`）。
- `HasExactStateChange = ancestor.StateKey != child.StateKey`（`:140`、`:199`、`:244`）说明体系**已经知道**「结构周期、整键不同」是常态。
- 周期证据只用于调度，不构成数学上的无限判定（`src/Search/CombatPlan.cs:1039-1041`、`docs/DEVELOPMENT_NOTES.md:1437`）。

### 1.2 预算与门控（四档数值实际相同）

| 门控 | 公式 | 数值（epoch 0→4） | 位置 |
|---|---|---|---|
| `CycleFamilyDepthBudget` | `clamp(2×period,8,32) << epoch`，封顶 128 | period 8：16→128 | `CyclePlanning.cs:671-684`、`:2250-2252` |
| `CycleRepetitionBudget` | `max(2, (clamp(4×period,32,64) << epoch)/period)`，封顶 512 | period 8：4→64 | `:713-727`、`:2499-2503` |
| `CycleFamilyProbeExpansionBudget` | `64 << epoch`，封顶 256 | 64→256 | `:701-705`、`:2306-2329` |
| `CycleExitProbeActionBudget` | `8 << epoch`，封顶 32 | 8→32 | `:707-711` |
| `CyclePlanningPerTurnBudget` | `clamp(MaxExpandedNodes/128,64,256)` | **四档都是 256** | `:2344-2348` |
| `CycleRegionAdmissionBudget` | `min(256, 64+32×min(6,epochs))`，**per-region per-run 累计** | 64→256 | `CycleRegionRetention.cs:1218-1223`、`:1331` |
| `CycleRegionGlobalAdmissionBudget` | `min(512, clamp(MaxExpandedNodes/32,256,512)+32×min(6,epoch))` | **四档 baseline 都是 512** | `:1233-1261` |
| 回合层节点 | `max(500, 剩余节点/保留层数)`，保留层普通 4／Boss 8 | — | `src/Search/CombatBeamSolver.Phases.cs:1466-1472`、`:1519-1544` |

### 1.3 关键分水岭：这个循环每轮有没有净伤害

`RequiresBoundedCyclePlanning`（`CyclePlanning.cs:2463-2468`）要求 `LastDelta.EnemyHp >= 0 && EnemyBlock >= 0 && !HasNewEnemyDurabilityProgress && AliveEnemyCount >= 0`。

- **无净伤害的循环**才受 family 深度、重复数、探针等循环预算约束；命中后节点被丢弃并计 `CycleContinuationsStopped`（`:2513-2540`、`:2530`）。
- **有净伤害的循环**完全绕过这些约束，`ShouldStopUnproductiveCycle` 也不会触发（`:735` 早退）。它只受回合层节点／时间预算、全局节点／时间预算、Beam 和保留通道约束。

这解释了为什么「500000 HP、超过 256 次周期」的夹具能在 0.766 s 内跑完（892 个动作、1784 次展开，`docs/TEST_MATRIX.md:2088`）：它走的是「有净伤害」通道，循环预算对它不生效。

### 1.4 全链路没有任何外推

`src/Search/CombatPlan.cs:1039-1041` 与 `docs/DEVELOPMENT_NOTES.md:1437` 明确写着：进入 frontier 的每条边都由模拟器精确执行一次，不使用宏动作或直接倍增伤害。史诗级长循环只能靠**真实回放 `N×period` 条边**得出结论。

## 2. 问题 1（质量）：根因与证据

### 2.1 过量格挡没有代价，反而被当成进展（最可能解释「格挡够了还起防」）

- 基础分公式里**没有格挡项**（`src/Search/CombatBeamSolver.StateEvaluation.cs:162-236`）；格挡只通过 `ProjectedPlayerHp` 间接有价值，而 `ProjectHpAfterThreat`（`:1276-1345`）会逐击扣减 `block -= min(block, hit)`（`:1389-1390`），超过总来袭伤害的部分价值恒为 0——既不加分也不减分。
- 但 `UsefulDefensiveBlockReserve`（`CyclePlanning.cs:650-659`）= `min(PlayerBlock, PlayerMaxHp)`，上限是**最大生命**而不是**来袭伤害**，注释自称「有限 reserve」，实际没有任何威胁量参与。
- 这个函数被当成**正向进展／加分维度**用在至少四处：
  - 循环出口质量 `CycleExitQuality.PlayerBlockGain`（`CyclePlanning.cs:624-625`，字段 `CombatPlan.cs:608`），并作为 Pareto 维度参与 `DominatesOrEquals`（`CombatPlan.cs:628-657`）；
  - 循环区域严格进展 `HasStrictCycleRegionProgress`（`CycleRegionRetention.cs:896-909`，尤其 `:907`）——它直接决定 `ProgressEpochs`，而 `ProgressEpochs` 决定该区域还能不能再拿准入预算（`:1218-1223`、`:1250-1261`）；
  - 区域生存并列比较（`:361-373`、`:973-979`）；
  - ordered-mutation 近端进展（`src/Search/CombatBeamSolver.OrderedMutationRetention.cs:505-512`）与 `IsBetterDefensive` 必留挑选（`src/Search/CombatBeamSolver.BeamRetentionPolicy.Ranking.cs:26-36`）。
- 另外 `Defend` 只要 `block > 0` 就必然拿到 `ImmediateDefense` 族名额，而每个族在动作准入里保底一个名额（`src/Search/CombatBeamSolver.Expansion.Candidates.cs:235-241`、`:151-170`）。

**合起来的效果**：多打一张「格挡已经溢出」的防御牌，在模型里既不是负分，还能把循环区域的进展 epoch 顶上去（从而换到更多搜索预算），并在防守泳道里占一个必留位。这条线因此能活到部署。

### 2.2 能量的机会成本只存在于同层 Beam 排序

能量只在 `BeamRankScore` 里按 60 000/点（上限 6 点）计价（`src/Search/CombatBeamSolver.BeamRetentionPolicy.Ranking.cs:862-863`、`src/Search/SolverWeights.cs:23-24`）；那是**同回合同层**的量。回合结束后能量被原生重置、格挡被清（`src/Search/CombatBeamSolver.RoundTransition.cs:47-68`），而最终比较键里**没有能量和格挡维度**（`Ranking.cs:41-127`、`src/Search/CombatBeamSolver.FinalPlanOrdering.cs:257-285`）。`unspent_energy_penalty=false` 只出现在初始化日志串里（`src/Runtime/Entry.cs:93`），全仓库没有该惩罚的任何实现。

### 2.3 斩杀没有被「取」的原因不在比较层

- 胜利判定 `won` 需要 `TerminalStamp is Victory` 且无 pending choice（`StateEvaluation.cs:94-97`）；存活判定用 `EffectiveEnemyHp`，多形态 TestSubject、会复活的敌人、蒸汽爆发期都会让 `AllEnemiesDead` 为假（`:81-87`、`src/Search/SimulatedCombatState.cs:1417-1426`）。未补偿的 Death 缺口还会让 `uncertainVictory` 吞掉 `VictoryBonus`（`:100-103`、`:211-212`）。
- 动作支配 `Dominates` **根本没有斩杀维度**（`Expansion.Candidates.cs:411-460`）；唯一的斩杀型剪枝要求先存在完整胜利上界（`src/Search/CombatBeamSolver.Retention.cs:285-379`），且按 `StrategicHpLowerBound` 剪，HP／回合相同的非斩杀分支不会被它剪掉。
- 所以「有斩杀不去打」最可能发生在「模型不认为这是斩杀」或「那条候选/路线没被生成或被预算截断」这一层。**这条也直接连到问题 2**：如果循环需要再多打几轮才能斩杀，而搜索在到达之前就切了回合层，那斩杀线根本不存在于候选集合里。

### 2.4 支配剪枝为什么救不了

`Dominates` 要求两边 `IsPure`、`CardType`、`TargetCombatId`、选项族、能量/星能消耗等逐项相容（`Expansion.Candidates.cs:419-439`）。

- 「Defend」与「攻击」在 `CardType` 就返回 false（`:432`）；与「放血」（`EnergySpent=0`、`HpInvestment+Resource`）也在 `:435-436` 返回 false。
- 即便同类同费，`Block` 是单调正向维度（`:441-442`、`:452-453`），**格挡多永远「不差」**，代码里没有「超过来袭伤害就不算收益」的截断。
- 与「结束回合」比较**不可能**：结束回合节点走 `ExpansionBatch.EndTurns`，从不进入动作支配候选表（`src/Search/CombatBeamSolver.ParallelExpansion.cs:913-945`、`Expansion.cs:493-503`）。

### 2.5 威胁投影本身的两处可疑（未证实）

- 每击伤害取捕获时的静态 `AttacksByMove`（`src/Prediction/BranchMonsterAi.cs:129-149`）再加 3 个特例与 `ModifyDamage`（`StateEvaluation.cs:1330-1342`、`SimulatedCombatState.cs:1330-1342`）；原生 UI 的 intent 走 `IntentForecaster.GetAttackHits` 会评估 `GetSingleDamage`/`DamageCalc`（`src/Prediction/IntentForecaster.cs:94-137`）。两条路径对动态伤害可能不一致。
- 强制行动只要不是 `EXPLODE_MOVE` 就整段跳过（`StateEvaluation.cs:1302-1308`），会低估来袭伤害，与上一条方向相反。
- 两者都需要实测对照，现有 `ThreatProjectionMetric` 只有耗时/分配（`src/Search/SearchPerformanceMetrics.cs:124-131`）。

### 2.6 现有夹具已经覆盖了「防御价值饱和」这个场景

`coverage/unattended/generic-loop-stagnant-block-draw-v0111.json` 的描述就是：

> A zero-cost draw/block recurrence keeps changing exact pile, history, and block state **after its defensive value is saturated**. Bounded planning must sample the recurrence and then stop without relying on a card-specific rule.

断言 `expectedInitialCycleContinuationsStoppedAtLeast: 1`、`expectedInitialExpandedNodesAtMost: 80`。也就是说「停滞型格挡循环必须被采样后停住」已有门禁；用户报告的现象更可能是**混合循环**（放血＋防御＋攻击）里的同一机制，那条路径不在这个夹具的断言范围内。

## 3. 问题 2（性能／上限）：预算链与谁先截断

### 3.1 成本模型

- 每个循环动作都是一次真实展开：`Expansion.Candidates.cs:93` 的 `limit = Math.Min(_profile.MaxCardBranchesPerNode, candidates.Count)`——**每次展开都可能模拟手牌级别的候选**（低 24／中 32／高 48／极高 72 张分支上限，`src/Search/SolverSearchProfile.cs:30-36`、`src/Runtime/SolverSettings.cs:176-201`）。
- 因此 N 次迭代 ≈ `N × 手牌候选数` 次模拟 + N 个节点预算。2000 HP／每 3 张技能 3 点伤害的循环需要约 2000 个动作；低档 60 000 节点、高档 250 000 节点的请求级上限很容易被吃光，随后就是 `TURN_LAYER_BUDGET` 强制结束回合并返回未完成路线。
- 已记录的经验：一场 8 回合 Boss 战 85 次回合层截断**全部** `reason=nodes`（`src/Search/CombatSearchCoordinator.FailureRecovery.cs:24-30`）；历史上零费抽牌引擎曾把整份节点预算烧在同一个回合（`docs/DEVELOPMENT_NOTES.md:911`、`docs/strategy/search-logic-explained-20260912.md:257`）。

### 3.2 谁先截断

**有净伤害的循环**（用户举的「每 10 张牌 6 点伤害」属于这一类）：循环预算全不生效，链式约束依次是
回合层节点 `max(500, 剩余/4 或 8)` → 全局 `MaxExpandedNodes`（60k/120k/250k/500k）→ 软时间（60/120/180/300 s）→ 保留通道（Beam 60–135 + ≤6 条循环租约 + 区域准入 512/回合）→ 精确状态重复时的转置去重。

**无净伤害的循环**：先撞 `CycleFamilyDepthBudget`（epoch 0 = `clamp(2×period,8,32)` 个不同 `ActionCount`；period 8 相当于只允许 2 次重复），再撞 `CyclePlanningPerTurnBudget=256`、区域准入 512/回合、探针预算。要涨到 128 深度必须靠进展证据拿 epoch（`CyclePlanning.cs:2181-2228`、`:888-894`），真正无进展的循环永远停在 epoch 0。

### 3.3 识别本身的两个硬限制

- **32 动作窗口**：`MaximumDetectedCyclePeriodActions = 32`（`CyclePlanning.cs:5`），动作键最多取 `2×周期`（`:66-84`）。
- **伤害相位必须与形状周期对齐**：`HasMatchingCycleDamagePhases`（`:272-282`）要求「哪些动作位置造成伤害」的布林模式每隔 p 个动作重复一次。
  - 8 张牌的循环 + 每 3 张触发一次的效果：合成周期 24 ≤ 32，**可以**被证明（代码会尝试 24 这个更长的周期，`SelectPreferredConsistentCycleEvidence` 优先带伤害凭证的候选，`:250-260`）。
  - 8 张牌的循环 + 每 10 张触发：合成周期 40 > 32，**证明不了**，只能落到 `fallbackAncestor` 的弱证据（`:214-247`）。
  - 代价：识别一次要哈希最多 64 个动作键（`:66-84`），且每个节点都做一遍形状比较。
- 这两条正好对应仓库设计说明里「最多 8 动作周期、有限出口视野，因此不数学完备」的历史记录（`docs/strategy/STRATEGY_OPTIMIZATION_LOG.md:92`）。

### 3.4 遥测缺口

`CycleContinuationsStopped` 把「无产出」「重复预算」「family 深度」三种拒绝合并成一个计数（`CyclePlanning.cs:2530`），区域侧只有 `CycleRegionCandidatesDropped`（`CycleRegionRetention.cs:120-121`、`:694-695`）。结果里**没有回合层截断计数**，只有 `TURN_LAYER_BUDGET` 日志行（`Phases.cs:1535-1541`）。要判断「这次到底撞了哪道界」，现有计数不够。

### 3.5 本机实测尝试（未完成，如实记录）

- 本机具备条件：游戏本体在 `~/.local/share/Steam/steamapps/common/Slay the Spire 2`，RitsuLib 在创意工坊目录，.NET 9 SDK 可用；`dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false` 与 `tools/OfflineSearchHarness` 均 0 警告 0 错误构建通过。
- 离线宿主**不能**直接喂自定义夹具：它只接受带 `generatedScenarioPath` 的请求，而生成场景是随机配装（`tools/OfflineSearchHarness/GeneratedScenarioSetup.cs:52-55`、`src/Testing/GeneratedCombatScenario.cs:27-50`）。
- 用 `tools/run-unattended-test.sh` 重建了 2000 HP 的信封开启者长循环（命令行见第 7 节），实例正常创建、`UNATTENDED_STARTED`，但在 `--timeout-seconds` 默认 120 s 处被启动器超时终止，**未产出结果**。按 AGENTS.md §8 不在同一轮把超时继续放大，因此这一项记为未验证。

## 4. 问题 3（展示）：从搜索到界面的断链

### 4.1 元数据在搜索出口被丢弃

循环元数据只活在搜索里：`CycleSearchState` 挂在 `SearchNode.Cycle`（`src/Search/CombatPlan.cs:1042-1057`、`:1076`、`:1085`）。而构造最终计划时 `SelectedSearchPlan` 只保留 `Actions/ActionCount/Score`（`CombatPlan.cs:1359-1362`，构造点 `src/Search/CombatBeamSolver.Phases.cs:617-620`）——**周期、重复次数在这一步全部丢掉**。`src/Runtime/` 与 `src/UI/` 全目录 grep `Cycle|循环` 只命中 `src/Runtime/SolverDiagnostics.cs:132-148` 的日志计数。

### 4.2 显示层现状

- 所有 Overlay 代码在 `src/UI/`（不在 Runtime）：`SolverOverlaySnapshot.cs`（唯一的搜索结果→UI 转换边界）、`SolverRouteRow.cs`、`SolverActionPill.cs`。
- `SolverOverlayActionSnapshot`（`src/UI/SolverOverlaySnapshot.cs:24-33`）只有 `Title/TargetName/ChoiceText/RelicLabels/Kills/Tooltip/VisualKind/ReplayCount/TextIdentity`；`SolverOverlayTurnSnapshot`（`:45-54`）没有任何折叠/重复字段。
- **回合行硬上限 16**：`SolverWeights.UiTurnRows = SetupValueHorizonTurns = 16`（`src/Search/SolverWeights.cs:114-118`），`SolverOverlay.cs:974-978` 越界直接 `continue`，**没有任何「+N 回合」提示**——第 17 回合起静默丢失。这是当前唯一的静默截断。
- 回合内动作数**没有上限**（`SolverRouteRow.cs:148-153`）。
- **部署 1:1 不变量**：`SolverOverlay.ShowDeploymentStep`（`SolverOverlay.cs:1045-1066`）在 `RouteRows[0].DeploymentActionCount != actionCount` 时抛异常，`actionCount` 来自本回合可执行动作数（`src/Runtime/SolverController.cs:2753-2754`）。折叠当前回合行必须同时处理这条断言与 `SetDeploymentProgress`（`SolverRouteRow.cs:184-210`）的下标映射。
- 现成的 `×N` 只有 `ReplayCount`，语义是**单张牌的额外重放次数**（`SolverActionPill.cs:55-64`、`src/UI/English.json:86`），已参与搜索身份（`CyclePlanning.cs:2774` 注释、`src/Search/CombatBeamSolver.BeamRetentionPolicy.OrderedMutationScheduling.cs:1593`），**不能挪用作循环次数**。

### 4.3 两条可行路线

- **显示层折叠（零 Search 改动）**：在 `SolverOverlaySnapshot.CaptureTurn`（`:344-394`）里用搜索同一套动作键元组（`CyclePlanning.cs:2757-2785`：`Kind/CardId/CardOccurrence/TargetIndex/TargetCombatId/PotionId/PotionSlot/NestedChoicesBeforePrimary/Choice/NestedChoices/TurnStartChoices`，**故意不含** `CardStateKey/CardStateOccurrence` 与 `ReplayCount`）重新识别重复子序列，给两层 snapshot 加折叠字段，在 `SolverRouteRow.Populate` 渲染成「循环 … ×N」胶囊。代价：三条 capture 路径（`Capture`、`CaptureCurrentTurn`、`CaptureSpeculativeRoute`/`BuildOverlayTurn`）要一致；行复用比较 `HasSameActions` 要纳入新字段；当前回合行要么不折叠、要么实现区间级部署高亮。
- **权威元数据（建议在实现 P2 后走这条）**：让循环加速步产出的宏节点携带 `{起始动作下标, PeriodActions, Repetitions, Turn}` 描述，经 `SelectedSearchPlan`/`SolverResult` 带到 snapshot。注意 `SearchNode.Cycle.PriorCycleEndpoint` 是节点引用，映射回平坦下标需要新增区间映射；搜索中预览路径 `SolverFrontierTurn`（`src/Runtime/SolverProgress.cs:168-213`）也要同步，否则预览与最终结果折叠不一致；`src/Runtime/SolvedRouteCache.cs:114-118` 会序列化 `SolverResult`（缓存身份含 `ModuleVersionId`，构建即失效）。
- 另外：循环目前**只限同回合**（`CyclePlanning.cs:38`、`:56-57`），「每回合重复同样 8 张牌 ×20 回合」没有任何周期元数据；若要折叠跨回合重复，需要新检测，或先用「×N 回合／+N 回合」提示处理 16 行截断。

## 5. 「直接复制多打几次」的真正难点：计数分类

结论：**逐张真实回放是安全的，解析外推目前不安全。** 逐条依据如下。

### 5.1 已经足够精确、可闭式的计数（A 类）

- **全部逐回合出牌类计数**：`_cardsPlayedThisTurn`、`_manualCardsPlayedThisTurn`、`_cardPlayStartsThisTurn`、`_attackPlayStartsThisTurn`、`_attackSkillStartsThisTurn`、`_zeroCostAttackStartsThisTurn`、`_cardPlaySeriesStartedThisTurn`、`_skillCardsPlayedThisTurn`、`_blockCardsPlayedThisTurn`、`_shivsPlayedThisTurn`、`_attacksPlayedThisTurn`、`_cardsExhaustedThisTurn`、`_cardsDiscardedThisTurn`、`_creatureAttacksThisTurn`、`_energySpentThisTurn`、`_starsGainedThisTurn`、`_nonHandDrawsThisTurn`、`_statusCardsDrawnThisTurn`。容器 `src/Search/SimulatedCombatState.cs:203-226`，每回合复位 `:1221-1251`，**全部进 StateKey** `:2248-2266` 与 `src/Search/SimulatedCombatState.CardLifecycle.cs:576-577`，Fork 逐项复制 `SimulatedCombatState.Fork.cs:32-52`。
- **跨战斗遗物计数**（IronClub 4／Nunchaku 10／TuningFork 10／PenNib 10／JossPaper 5／GalacticDust 10，周期表 `src/Prediction/RelicCounterCatalog.cs:12-22`）：闭式 `(X₀+N·d) mod Q`，触发次数 `⌊(X₀+N·d)/Q⌋−⌊X₀/Q⌋`；而且搜索本来就把它们当「周期内取值」用（`SimulatedCombatState.RelicCounters.cs:18` 的 `% Period`、`src/Search/RelicCounterPolicy.cs:48` 的回绕距离）。
- **回合键控计数**（HappyFlower／FakeHappyFlower／Pendulum／PollinousCore 的 `TurnsSeen`、`shufflesCrossed`）：回合内循环完全不动。
- 六个整场历史计数本身已精确增量维护（`src/Engine/InCombat/Simulation/CombatHistoryCounters.cs:10-27`、`CombatPredictionHistory.cs:435-436`、Fork 按值 `:491`）。

### 5.2 会破坏加速的（B 类）

1. **数值被当作幅度读的整场历史读者牌**：GoldAxe→已完成出牌数、Supermassive→生成牌数、Voltaic→闪电充能、TearAsunder→受未格挡伤害次数、PullFromBelow→虚无出牌数、Murder→抽牌数（`src/Prediction/CalculatedVarSpecRegistry.cs:98,106,107,111,127,132`）。第 i 次迭代的贡献是 `a+i·b`，N 次是 `N·a + b·N(N−1)/2`——**需要二次闭式，而现有 `HasConsistentDelta` 必然判为不一致**（`CyclePlanning.cs:118-121`），只会落到弱 fallback。
2. **循环内读「本回合已出牌/已攻击」当幅度或当门的卡与能力**：Finisher、LunarBlast、GangUp、MementoMori、HelixDrill、Normality，以及 SlowPower 的承伤倍率（`src/Engine/InCombat/Mirrors/ModifyDamageMirrors.cs:167-178`，同一敌人越打越疼）。
3. **触发副作用会改循环本身**：IronClub/JossPaper 抽牌（改手牌与牌堆顺序，可能触发洗牌与 RNG）、Nunchaku 能量、Kunai/Shuriken/OrnamentalFan 力敏、BrilliantScarf 改费用、VelvetChoker/Sloth/Normality/RingingPower 改**出牌合法性**（`src/Engine/InCombat/Mirrors/ShouldPlayMirrors.cs:57-92`）。「8 张牌一组、牌与能量中性」的前提在这些触发下会逐轮漂移。
4. **触发会挂起为选择**：IronClub/JossPaper 的抽牌可能在 `HasPendingChoice` 时提前 return，而计数**已经推进**（`AfterCardPlayedMirrors.cs:225-230`、`AfterCardExhaustedMirrors.cs:115-119`）。加速步不能跨越决策边界。
5. **读写时序耦合**：MakeItSo 的显式 `+1` 补偿（`AfterCardPlayedMirrors.cs:906-910`）、PenNib 跨 hook 读（写 `BeforeCardPlayedMirrors.cs:324-337`、读 `ModifyDamageMirrors.cs:288-299`）、`CompensateHistorySince` 系列（`src/Prediction/TriggeredPowerSupport.cs:10-38`）依赖**逐事件历史**；不写历史事件会让这些补偿链失真。
6. **按抽牌而非出牌键控**：AutomationPower（`AfterCardDrawnMirrors.cs:241-247`，进键 `SimulatedCombatState.cs:2407-2409`）周期与出牌周期无关。
7. **施加时才记基线的能力**：FeralPower/JugglingPower 在施加后记录 `ZeroCostAttacksPlayed`/`AttacksPlayedThisTurn`（`SimulatedCombatState.CardPowerHistory.cs:42-62`），循环中途新施加会吃掉之前的计数。

### 5.3 两处已存在的状态键缺口（外推会直接踩到）

- `BansheesCry` 读整场虚无出牌数（`SimulatedCombatState.CardEventHistory.cs:152-159`），但**不在** `CombatHistoryCounterKey.CardIds` 白名单里（`src/Search/CombatHistoryCounterKey.cs:28-34`）——只差该计数的两条分支会哈希相同并被精确去重合并。
- `_cumulativeHpLost` / `_recoveredHp` 进评分与 `CycleTransitionDelta`（`SimulatedCombatState.cs:210-211`、`CombatPlan.cs:547,576`），但**不在** `AppendFingerprint`（`SimulatedCombatState.cs:2215-2303`）。「每轮掉 3 血又补 3 血」的循环会让 HP 与键都不变而这两个累计每轮 +3。
- 这与 `docs/strategy/state-key-history-counters-20260919.md:23` 记录的历史缺口同类；该文档的「无条件追加」实验显示改键会显著扰动路线（46/50 根路线变化、4 根胜负翻转），因此**扩键必须单独做同工作量质量对照**。

## 6. 候选方案（分层，按风险从低到高）

### P1 质量：把格挡价值上限化到「来袭伤害 + 真正会保留的格挡」

- 落点：`UsefulDefensiveBlockReserve`（`CyclePlanning.cs:650-659`）。当前 `min(block, maxHp)` 改为「本回合来袭伤害中尚未被覆盖的部分 + 明确会跨回合保留的格挡（Barricade/Calipers/Blur 之类，走已有特征而不是名称分支）」。
- 影响面要一起看：循环出口 Pareto（`CombatPlan.cs:608`）、区域进展 epoch（`CycleRegionRetention.cs:907`）、生存并列（`:361-373`）、ordered-mutation 进展（`OrderedMutationRetention.cs:511-512`）、`IsBetterDefensive`（`Ranking.cs:26-36`）。任何一处不改，过量格挡仍能从别的通道换到预算。
- 反向风险：注释里已经写明保留 reserve 是为了「后续攻击与保留格挡」；如果一刀切按本回合来袭伤害截断，会低估 Barricade 类与「下回合还有第二波」的路线。所以要么区分「会保留」与「不保留」，要么保留一个有界但不为零的远期项。
- 验收：`generic-loop-stagnant-block-draw`（现状已断言停滞循环被停住）＋新增「格挡 ≥ 来袭仍继续起防」的反例夹具；记录 `EnergyLeftByTurn`/`ActualBlockByTurn`（`src/Runtime/SolverDiagnostics.cs:267-269` 一行可得）与 `CycleRegionProgressEpochs`、`CycleRegionCandidatesAdmitted`。

### P2 性能：确定性序列重复（推荐先做，逐张真实回放）

核心：**不解析外推，只把「N 次带分支的展开」换成「1 次无分支的确定性回放」。**

- 在已有循环证据的节点上新增**一个**候选：把已认证的动作序列重复 k 次，通过真实模拟器顺序回放；任何一步非法（牌不在手、能量不足、敌人已死、出现挂起选择）就立即停止宏并交回普通搜索。
- k 的来源：用 `CycleTransitionDelta` 的每轮伤害估计到斩杀所需的轮数，回放后校验；不足则按残差再来一轮（常数伤害下 1–2 轮收敛），而不是枚举每个前缀深度。
- 为什么安全：模拟器是权威，计数、遗物、能力、RNG、洗牌全部照旧精确结算；宏只是**省掉了分支与保留开销**，不产生任何新的等价性主张。这也满足 `CombatPlan.cs:1039-1041`「每条边仍被真实执行」的现有设计约束。
- 收益量级：一次展开最多模拟 `MaxCardBranchesPerNode` 个候选（24–72），宏把 N 次展开压成 1 次回放，同时把 N 个节点预算压成 1 个——直接解决「撞上限」。纯模拟成本仍是 O(N)，但分支因子从「手牌数」降到 1。
- 必须一并解决的设计问题：
  1. **不能吃掉替代出口**。宏跳过中间迭代会丢掉「打 3 轮后改打终结牌」这类分支。做法：回放过程中在迭代边界做轻量检查（记录状态键/耐久），只把最好的少数出口作为候选发布，而不是每个边界都分支。
  2. **计划表示**。`PlanActionKind` 只有 `PlayCard/UsePotion/EndTurn`（`CombatPlan.cs:8-13`）；建议保持 `SearchNode.Actions` 物化出**真实扁平动作**（部署与 UI 1:1 断言继续成立），另加一个紧凑描述给展示用（见 P3）。
  3. **确定性／DOP 等价**：k 必须只由状态决定；现有仓库对 DOP 等价有强制要求（`docs/TEST_MATRIX.md:2221`）。
  4. **识别门槛**：宏的种子可以放宽到「任意结构递归」（`shapeRecursAt`，period ≤ 32），是否真的可重复由回放裁决——这正好绕过 5.2 里「合成周期 > 32 无法认证」的问题（用户举的 8 张牌 + 每 10 张触发属于这一类）。
  5. **遥测**：给三种循环拒绝分因计数（现在只有合并的 `CycleContinuationsStopped`），否则无法证明收益来自哪条路径。

### P3 展示：折叠显示

- 若 P2 落地，直接消费宏的 `{起始下标, PeriodActions, Repetitions}`——权威且零重复检测。
- 若先做独立的小改动，则在 `SolverOverlaySnapshot.CaptureTurn` 用第 4.3 节的动作键元组做显示层折叠，**当前回合行先不折叠**以避开部署 1:1 断言。
- 顺带修 16 行静默截断（`SolverOverlay.cs:974-978`）加「+N 回合」提示；新增文案必须在调用点写中文模板并在 `src/UI/English.json` 逐字加 key，否则 `SolverText` 会抛 `KeyNotFoundException`（`src/UI/SolverText.cs:14`）。

### 备选（不建议先做）：宏动作直接倍增／闭式外推

只有在补齐 5.3 的两处键缺口、并为 5.2 的每一类都建立「不可加速」排除条件之后才值得讨论。历史上「固定重复 16 次 + 按名称放宽」的 PR #28 已被撤回（`docs/DEVELOPMENT_NOTES.md:1436`），且结构门禁会拒绝重新引入内容特判——任何加速都必须完全通用。

## 7. 可以直接用的验证夹具（不需要用户提供战斗）

| 夹具 | 覆盖什么 | 关键期望 |
|---|---|---|
| `coverage/unattended/generic-loop-stagnant-block-draw-v0111.json` | **问题 1 的停滞型格挡循环**：防御价值饱和后仍在改变状态 | `CycleContinuationsStopped ≥ 1`、展开 ≤ 80 |
| `coverage/unattended/generic-loop-bloodletting-double-pommel-quality-v0111.json` | **卖血＋有限重复链**：立即斩杀要付 6 HP，等一回合只需 3 HP | 战损 3、T2、敌方 HP ≤ 0 |
| `coverage/unattended/generic-loop-long-damage-hidden-phase-v0111.json` | **问题 2 的核心**：2×急躁 + 信封开启者，3 张技能 3 点伤害打 2000 HP | 可执行动作 ≥ 1200、洗牌 ≥ 1200、T1 击杀 |
| `coverage/unattended/generic-loop-long-growing-damage-v0111.json` | 50 万 HP、>256 次周期、每周期伤害不同 | 可执行动作 ≥ 800、T1 击杀；已记录 892 动作/1784 展开/0.766 s（`docs/TEST_MATRIX.md:2088`） |
| `coverage/unattended/generic-loop-letter-opener-hidden-phase-v0111.json` | 隐藏相位（第三次技能才兑现） | T1 击杀、`CycleShapesDetected ≥ 1` |
| `coverage/unattended/generic-loop-rampage-dynamic-growth-positive-v0111.json` | 动态成长循环、DOP 等价 | 32 动作、T1、DOP1/DOP2 工作量和动作一致 |

复跑（Linux，本机已具备游戏与 RitsuLib）：

```bash
cd CombatSolver-loop-optimization
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
# 信封开启者 2000 HP（问题 2 的最强反例；本次因 120 s 启动器超时未完成）
./tools/run-unattended-test.sh \
  --scenario-id LOOP-LETTER-OPENER-LONG-RESEARCH \
  --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK \
  --seed LOOPLETTEROPENERPHASE0111 \
  --initial-enemy-max-hps-json '[2000]' --initial-enemy-current-hps-json '[2000]' \
  --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 0 \
  --clear-player-piles --clear-all-powers \
  --cards-json '[{"cardId":"IMPATIENCE","pile":"Hand"},{"cardId":"IMPATIENCE","pile":"Discard"}]' \
  --relics-json '[{"relicId":"LETTER_OPENER"}]' \
  --force-short-search-only --short-search-budget-override-milliseconds 20000 \
  --search-max-degree-of-parallelism-for-test 1 --measure-search-phases \
  --cleanup-instance-on-exit
```

注意：`tools/OfflineSearchHarness` 只支持随机生成场景的请求，跑不了上面这些自定义夹具（`GeneratedScenarioSetup.cs:52-55`）；要批量跑这些夹具，需要先用 `run-unattended-test.sh` 的 CLI 组装等价请求，或在宿主里增加「接受完整 request JSON」的入口（属于工具改动，需单独决定）。

## 8. 未验证 / 需要决策的点

1. 用户报告的「放血→防御」具体案例没有原始日志包；现有 `stagnant-block-draw` 与 `bloodletting-double-pommel` 夹具是最接近的替代，但**不能证明**用户看到的那一次就是同一根因。建议先取一份该场次的路线日志/问题包。
2. 2000 HP 信封开启者夹具本轮未跑完（启动器 120 s 超时），「当前构建是否仍满足 ≥1200 动作的断言」未验证。
3. `ProjectHpAfterThreat` 与原生 intent 面板的一致性未验证（动态伤害、非 EXPLODE 强制行动两处已知偏差方向相反）。
4. P2 的收益需要量化：需要固定根对照（动作数、展开量、墙钟、分配），以及「宏是否吃掉替代出口」的质量对照。
5. 是否允许把「计划里的循环」表示成新的 `PlanActionKind` 或紧凑描述，会牵动 `SolverResult` 缓存格式、UI 部署 1:1 断言与结构门禁，需要单独决策。
6. 5.3 的两处状态键缺口是否在本批一起修（会扰动路线，需同工作量对照）。
