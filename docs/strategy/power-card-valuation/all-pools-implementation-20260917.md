# 全卡池能力牌建模实施记录与交接

日期：2026-09-17
分支：`feat/power-card-optimization-20260917`
基线：`b0f6d4bb`
状态：代码框架、五个卡池登记/证据/投影、纯合同检查、Release 编译、PowerShell 结构门禁与五个角色的短场景集成验收均已完成；逐卡玩家复核尚未完成，状态保持 `QuantifiedDraft`。已提交为本分支 `3aee52b6`。

本文既是一次实施的过程记录，也是防止上下文丢失的落盘计划。后续编码代理先读本页的“剩余步骤”，再按顺序完成；不要重做已完成部分。

## 1. 目标与边界（来自用户指令）

- 完成静默猎手之外全部单人能力牌建模：铁甲战士、故障机器人、储君、亡灵契约师、无色。
- 静默猎手 17 张生产实现只作范例，不推倒重做。
- MultiplayerOnly 只保留资料，不进入单人求解器模型。
- 能力估值只负责候选准入、保路、专搜优先级与后验探索；不得进入终局胜负/战损排序。
- 不按遭遇硬编码；不用“卡面数值 × 回合数”替代真实阈值/资源转换/跨回合收益。
- 未登记能力保持原有行为；无登记能力牌时不承担逐节点扫描与开局 Replay 成本。
- 不提升版本、不打包、不发布、不推送远端；不修改用户 `.gitignore` 改动。

## 2. 已完成

### 2.1 公共接口重构

| 文件 | 变化 |
|---|---|
| `src/Search/PowerCardValuation/PowerCardValuationContracts.cs` | 新增 `PowerRoutePriority`、`PowerRouteAdmissionPolicy`；`PowerCommitmentDescriptor` 改为 `(PowerCardPool Pool, string CardId, PowerCommitmentFamily Family, PowerRouteAdmissionPolicy Admission)`；删除公共层 `SilentPowerCardIdentity`；扩展机制族/需求/时机/上下文（球、集中、星、召唤、灾厄、虚无、熔炉、覆甲、活力等）。`PowerCardValuationRequirements` 改为 `ulong` 底层。 |
| `PowerCardValuationRegistry.cs` | 卡池无关；`TryGetPool`、`RegisteredCardIds`、`ContainsCardId`；`TryGetCommitmentDescriptor` 对 `Family.None` 或 `NoInCombatCommitment` 明确返回 false。 |
| `PowerRouteAdmission.cs`（新） | 静默猎手第二批准入顺序提为公共 `PowerRouteAdmission.Evaluate`，逐卡差异只由政策数据表达；`PreferSlyActivation` 泛化为 `PreferFreeActivation`；`MasterPlanner` 特判泛化为 `RequirePositiveProjection`。 |
| `IPowerCardValuationModel.cs` | 模型新增 `CommitmentFamily`、`AdmissionPolicy`。 |
| `PowerCardValuationRegistration.cs`（新） | `DelegatingPowerCardValuationModel<TCard>` + `PowerCardModelRegistration.Register<TCard>`，逐卡只登记数据与公式。 |
| `PowerCardValueFacts.cs`（新） | 非静默卡池共用尺度与有界公式原语。 |
| `Commitments/PowerCommitment.cs` | 存 `Priority` 与 `IReadOnlyList<string> Cards`，`HasCard`。 |
| `Commitments/PowerCommitmentLifecycle.cs` | 支持多卡 OR 家族、优先级取高、卡牌去重。 |
| `Commitments/PowerCommitmentRetention.cs` | 改用 `commitment.Priority`。 |
| `Commitments/PowerCommitmentEvidence.cs` | 按池分发 progress/realized；无专用证据时回退通用状态改善。 |
| `Commitments/PowerCardMechanismDispatch.cs`（新） | 按 `descriptor.Pool` 路由触发证据/投影地板/开局投影；公共层不依赖角色枚举。 |

### 2.2 无登记快速旁路

- `CombatBeamSolver.cs`：新增 `_hasRegisteredPowerCards = root.PlayerCardIds.Any(Registry.ContainsCardId)`。
- `PowerCommitmentPolicy.AttachPowerCommitment`：无登记能力时直接置空并返回。
- `CombatBeamSolver.BeamRetentionPolicy.cs`：`AdmitPowerCommitmentRepresentatives` 在 `_run.PowerCommitmentsCreated == 0` 时跳过席位扫描。
- `CombatSearchCoordinator.PowerRoutes.cs`：`RunOpeningPowerRoutePortfolio` 在根牌区无已登记能力时直接返回基线，不做前缀构造与试放。
- 泛能力组合成员原本已由 `hasReachablePower` 门控，保持。

### 2.3 投影工具与机制事实

- `Projection/PowerCardProjectionSupport.cs`（新）：从 Silent 文件上移 `BuildCurrentHandOptions`、`BuildProjectedCardOptions`、`MarginalFrontierValue`、`EstimateRemainingTurns`、`ForecastIncomingDamage`、`SaturatingProduct`、`SaturatingPowerCommitmentAdd`；新增 `BoundedPrevention`、`PowerGrowthFrontierPotential`、`PowerPerTurn*`、`PowerPerTrigger*` 有界评估器。
- `Projection/PowerCardMechanismFacts.cs`（新）：只读冻结状态的牌区/球/集中/星/奥斯提/灵魂/灾厄/虚无/费用等事实读取。
- `Projection/PowerTurnFrontier.cs`：新增 `damagePerAttack`（力量类属性成长真实兑现）。
- Silent 原投影文件改为复用上移工具，行为不变。

### 2.4 五个卡池登记（共 87 张单人能力牌）

每个卡池目录结构：`<Pool>PowerRoutePolicy.cs`、`<Pool>PowerTriggerEvidence.cs`、`<Pool>PowerOpeningProjection.cs`、`<Pool>PowerCardValuationModels.cs`（注册入口）、若干机制族 `*PowerCardValuationModels.cs`。

| 卡池 | 单人登记数 | 排除 MultiplayerOnly | 机制族文件 |
|---|---|---|---|
| 铁甲战士 | 19 | TANK | 力量/消耗/防御/触发伤害/牌流 |
| 故障机器人 | 20 | ONE_FOR_ALL | 球与集中/成长与费用/牌流与状态 |
| 储君 | 18 | HAMMER_TIME | 星星铸造生成/防御控制资源 |
| 亡灵契约师 | 18 | CACOPHONY、SOULBOUND | 灾厄召唤/能量手牌生成 |
| 无色 | 12 | BEACON_OF_HOPE | 防御成长/牌流生成延迟伤害 |

- `ROYALTIES`、`FORBIDDEN_GRIMOIRE`：纯战后收益，登记资料与估值，但 `NoInCombatCommitment` 不创建战斗内承诺。
- 修正：故障机器人 `WhiteNoise` 实为 `CardType.Skill`，不属能力牌范围（原 `defect.md` 把它算作能力牌，需在文档修订）。

### 2.5 静默猎手兼容

- `SilentPowerRoutePolicy` 改为按 CardId 查表；17 张的家族/优先级/准入数值与旧版逐项一致。
- 新增 `SilentPowerCardValuationModel<TCard>` 基类，17 个模型改继承它。
- `SilentPowerTriggerEvidence.cs`、`SilentPowerOpeningProjection.cs`、`SilentPowerCommitmentEvidence.cs` 改为字符串 CardId，逻辑等价。

### 2.6 验证现状

- `dotnet build CombatSolver.csproj -c Release`：通过（0 错误，0 警告）。
- `dotnet run --project tools/PowerCardValuationChecks/PowerCardValuationChecks.csproj -c Release`：通过，输出 `POWER_CARD_VALUATION_CHECKS_OK total=104 silent=17 ironclad=19 defect=20 regent=18 necrobinder=18 colorless=12`。
- `pwsh -NoProfile -File tools/verify-refactor-boundaries.ps1`：通过，输出 `REFACTOR_BOUNDARIES_OK search_files=191`；门禁已同步为多卡池承诺边界。按用户约束未运行 Bash 门禁。
- 集成验收：`coverage/novelty-search/dev-00-ironclad-elite`、`dev-01-silent-elite`、`dev-02-defect-elite`、`dev-03-regent-elite`、`dev-04-necrobinder-elite` 五个短场景全部 `Passed`、`error=null`；均使用 `-GeneratedScenarioPath` + `-EvidenceDirectory` + `-CleanupInstanceOnExit`，最终 `headless-instances` 为空。
- 文档：五份卡池逐卡建模表、总登记状态表、待玩家复核表、`docs/ARCHITECTURE.md`、`docs/DEVELOPMENT_NOTES.md`、`docs/TEST_MATRIX.md` 已更新。
- 未验证：逐卡玩家复核；复杂机制的逐卡专用兑现证据；可见 Steam 会话下的实际战损对照。

## 3. 剩余步骤（已完成项标记）

1. ~~重写纯合同检查~~ 已完成。
2. ~~结构门禁~~ 已完成（PowerShell；Bash 按约束未运行）。
3. ~~（可选）无头集成~~ 已完成五个角色短场景。
4. ~~文档~~ 已完成逐卡文档、状态表、复核表、ARCHITECTURE、DEVELOPMENT_NOTES、TEST_MATRIX。
5. **提交**：只暂存本任务文件，排除 `.gitignore` 用户改动、构建产物、发布包。不提升版本、不打包、不打标签、不推送。
6. **后续**：玩家逐卡复核后把 `QuantifiedDraft` 升级为 `Modeled`，并补复杂机制的逐卡专用兑现证据。

## 4. 已知不确定项（不得编造）

- 各卡池复杂机制（球被动/激发、星星花费、奥斯提、灾厄结算）的远期价值使用有界保守代理，标注 Draft，需玩家复核。
- 新池兑现证据统一走通用状态改善回退（`GenericPowerCommitmentEvidence`），尚未像静默猎手那样逐卡专用；文档必须如实标注。
- 运行时 CardId 由类型名 SCREAMING_SNAKE 推导（与现有静默猎手一致，已被生产验证），未逐卡读取游戏 ID 表。
- 部分原版效果含选择/随机目标/重放顺序，投影只保证“存在值得搜索的路线”，不预测精确战损。
- 本批未启动可见 Steam；无头数据不能外推为可见性能收益。
