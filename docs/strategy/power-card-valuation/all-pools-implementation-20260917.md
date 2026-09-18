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

## 5. 玩家联合评审采纳（2026-09-17）

玩家对 [逐卡复核表](player-review-20260917.md) 中 Gemini 建议逐条给出最终意见，原则是“以玩家意见为准”。实现按此更新了 87 张牌的路线优先级、专搜标记与部分数值口径：

- 专搜（`PreferDedicatedSearch`）新增：铁甲战士 AGGRESSION、BARRICADE、CRIMSON_MANTLE、DARK_EMBRACE、DEMON_FORM、PYRE、RUPTURE、UNMOVABLE；故障机器人 BIASED_COGNITION、CAPACITOR、CREATIVE_AI、MACHINE_LEARNING、STORM、SUBROUTINE；储君 ARSENAL、CHILD_OF_THE_STARS、GENESIS、ORBIT、PALE_BLUE_DOT、SPECTRUM_SHIFT、SWORD_SAGE、THE_SEALED_THRONE、TYRANNY；亡灵契约师 CALL_OF_THE_VOID、COUNTDOWN、DEMESNE、FRIENDSHIP、NEUROSURGE、SENTRY_MODE；无色 AUTOMATION、CALAMITY、MAYHEM、NOSTALGIA、STRATAGEM。
- 专搜移除：HELLRAISER、JUGGLING、LOOP、FURNACE、PANACHE、ROLLING_BOULDER、NECRO_MASTERY。
- 优先级调整：BARRICADE、VICIOUS、ORBIT、PILLAR_OF_CREATION、AUTOMATION、FASTEN、ENTROPY、NOSTALGIA、STRATAGEM 上调；CRUELTY、JUGGERNAUT、JUGGLING、HELLRAISER、BIASED_COGNITION、CREATIVE_AI、SPINNER、CONSUMING_SHADOW、COOLANT、CALAMITY、ROLLING_BOULDER、NECRO_MASTERY 下调。
- 数值口径：AUTOMATION 改为按预计剩余回合的实际抽牌量折算“每10抽返1能量”；ORBIT 改为按每回合最大能量估算每4费返能；VICIOUS 的抽牌按群体/重复易伤叠加；ITERATION 按能力实际抽牌量而非固定值；消耗暗影与冷却剂改为“仅免费或有余费时开”。
- 专搜说明：本实现已对“当前可打出的每张已登记能力”从同一根运行固定前缀完整后验，因此这批评审标记的牌本来就会获得专搜；`PreferDedicatedSearch` 作为评审优先级标记保留。实测把它接入前缀构造顺序或承诺席位排序会让铁甲战士 `BARRICADE` 场景从 43 战损劣化为 62，故不改变路由，只作标记与文档。
- 明确不动：`ROYALTIES`、`FORBIDDEN_GRIMOIRE` 完全保持 `NoInCombatCommitment`，不得为局外金币/删牌承担任何战损（玩家特别指令）。
- 时序核对：`LETHALITY` 按源码作用于打出当回合的第一张攻击（能力牌本身不占首攻），不是从下回合开始；模型沿用。

验证：Release 编译 0 错误 0 警告；纯合同 `POWER_CARD_VALUATION_CHECKS_OK total=104 ...`；PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=191`。回归哨兵：铁甲战士 `dev-00-ironclad-elite` 短场景在改动前后均为 43 战损（降级并回退前缀排序后确认），说明这批评审没有劣化最终选路；其余四角色沿用先前通过的短场景，未在本次重跑。

## 6. 审计修正（2026-09-17，审 `b0f6d4bb..540e4cb6`）

外部静态审计提出的问题与处理：

| 级别 | 问题 | 处理 |
|---|---|---|
| P1 | 统一估值接口没有生产消费者，`TryEvaluate` 与 `PowerCardValuationContext` 只被合同工具使用 | 明确标注为**合同与文档层**：`IPowerCardValuationModel` 与 `TryEvaluate` 增加 XML 说明，文档不再声称“已投入生产的量化”。暂不接入生产，因为额外准入信号会改变保路（`BARRICADE` 前缀排序实验已从 43 劣化到 62）。 |
| P1 | 非静默卡池的通用回退把普通攻击/手牌/预计生命进展都算成能力收益 | 曾试 `NewPoolPowerRealizedEvidence` 机制族归因与按池隔离（见 7.1），单因素回退证明是错误方向：把通用战术进展也当成能力收益会让承诺黏到租约结束。**已撤回**，恢复 `540e4cb6` 的通用进展释放，并把该证据注释为“路线进展／解除保护信号”，不宣称收益由能力造成。 |
| P2 | 根级快速旁路只按根牌区冻结，战斗中生成的能力牌拿不到旁路 | 保留根级冻结的 `_hasRegisteredPowerCards`。曾试“迟到启用 + `HasRegisteredPowerPlay`”并随 7.1 一起撤回；已知限制是根内无能力牌时中途生成的能力不创建承诺，已在代码注释与本文标注。 |
| P2 | `PowerLiveCards` 含消耗堆，已消耗牌继续提供触发证据 | `PowerLiveCards` 只取手牌、抽牌堆、弃牌堆；消耗堆不参与未来牌源。 |
| P2 | 群星之子投影把星能、星能牌数和星能再次相乘，高星能平方级高估 | 改为线性：触发次数 = `min(当前星能, 可花费星能点数)`（手牌星能牌的实际费用求和，X 费按剩余星能、受星能上限约束），格挡 = 层数 × 触发次数，并抽成 `PowerCardProjectionMath.ChildOfTheStarsBlock`。 |
| P2 | 缓冲按总量抵消多段攻击、凶恶把所有易伤来源乘敌人数 | 缓冲改为按平均单次伤害 × 层数（`PowerCardProjectionMath.BufferPrevention`）；凶恶改为只按易伤来源数（`ViciousDrawTriggers`）。自动化与环绕轨道改用纯数学，并注明既有内部进度不可读、按无进度保守估计。 |
| P2 | 纯合同没有覆盖生产投影 | 新增 `Projection/PowerCardProjectionMath.cs`（纯静态），把自动化、环绕轨道、群星之子、缓冲、凶恶的公式从 solver partial 抽出，并加入合同断言。 |
| — | 中文 XML 注释乱码、注释被塞进正文、`</summary>` 缺 `<` | 用同一 .NET GBK 码页反向恢复整棵估值树的注释（先后恢复 35 + 7 个文件），再拆分被塞进正文的 `///` 行并补回丢失字符；乱码与 `<` 缺失扫描归零。 |

未完成/保留：统一估值的 Reward/Penalty 仍未接入生产；承诺证据只作路线进展／解除保护用，不做逐卡因果归因。

## 7. 审计后验证（2026-09-17）

审计修正曾走过一版“按卡池隔离 + 逐卡因果兑现”，实测会让承诺黏到租约结束，已单因素回退到 `540e4cb6` 的通用进展释放。以下为真实数据。

### 7.1 因果兑现版（已撤回）短哨兵

| 角色 | 场景 | 战损 | 对照 |
|---|---|---:|---|
| 铁甲 | `dev-00-ironclad-elite` | 62 | 基线 43，劣化 |
| 静默 | `dev-01-silent-elite` | 40 | 一致 |
| 故障 | `dev-02-defect-elite` | 13 | 一致 |
| 储君 | `dev-03-regent-elite` | 52 | 基线 45，劣化 |
| 亡灵 | `dev-04-necrobinder-elite` | 25 | 一致 |

结论：回归来自“非静默能力失去通用解除信号、承诺黏到租约结束”，不是能力收益计算错误。

### 7.2 单因素回退后

| 项 | 结果 |
|---|---|
| Release 编译 | 0 错误 |
| 纯合同 | `POWER_CARD_VALUATION_CHECKS_OK total=104 silent=17 ironclad=19 defect=20 regent=18 necrobinder=18 colorless=12` |
| 结构门禁 | `REFACTOR_BOUNDARIES_OK search_files=192` |
| 铁甲 `dev-00` | 43（恢复） |
| 储君 `dev-03` | 45（恢复） |
| 故障 `dev-02` | 13（已补跑） |
| 亡灵 `dev-04` | 25（已补跑） |
| 静默 `dev-01` | 40（行为未变，沿用 7.1） |

五个短哨兵全部与原结果一致。未启动后台 8 包，未启动可见 Steam，未打包、未推送。
