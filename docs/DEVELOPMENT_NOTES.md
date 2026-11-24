# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

## 1.0.2（开发中）

### 战斗内核语义、死循环与零重算修复（Wave 2–4）

- **费用、变形与关键词生命周期**：在 `HotCostModifiers.UpgradeEnergyCostBy` 中按原版 `CardEnergyCost.UpgradeBy` 钳制超上限的 `Absolute` 费用修饰符（修复 `BlessingOfTheForge` / `Apotheosis` 与 Snecko/Slither 费用差分）；`HotCombatEngine.CardPlay` 调整为先触发 `FireAfterCardExhausted` 再调用 `ExpireAfterPlay`（修复 `BurningSticks` 克隆漏继承 `WhenPlayed` 降费/免星）；完善 `DingyDagger` 渐进升级 `Var0Delta` 与克隆清除 `DampenOrigUpg`；`UnlockedTokenCardPool` + 按 `Domain` 分发变形池（修复 `SLOTH -> THE_EMPRESS`）；`HistoryCourse` 与 `MourningBlow -> Sadness` 防止复用 `CardUid` 覆盖快照；`HotCardFlags.FetchPlayedThisTurn` 改由 `RecordCardPlayFinished` 无条件记录、根导入从 `History.CardPlaysFinished` 补种，并在全部克隆入口去标。
- **回合流转、能力与跨触发边界**：`HotPotions.UsePotion` 与 `HotCombatEngine.Turn`（`RunAutoPrePlayPhase` / `RunPlayerTurnEnd`）统一消费或清理 `ClearPlayerTurnEndRequest` / `ConsumePlayerTurnEndRequest`（修复 `DISTILLED_CHAOS` 自动打出 `VOID_FORM` 后漏结束回合）；单目标攻击入口（`ExecuteSingleTargetAttack`、`ExecuteDealerSingleTargetAttack`、`ExecutePlayedCardSingleAttack` 等）改在命中循环内校验存活、循环外仍执行 `BeforeAttack`/`AfterAttack`（修复 `FiendFire` + `CharonsAshes` 击杀原目标后漏消耗 `Vigor`）；`ToolsOfTheTradePower` 与 `TyrannyPower` 抽牌改走 `HandleModifyHandDraw`（避免误触 `SpeedsterPower`）；`HotCombatEngine.Powers` 对 `AfterTurnEnd` 快照后被移除的临时属性能力补跑退还结算（`CeremonialBeast`）；`ExecuteTurnEndInHandCard` 先将牌由 `Hand` 移入 `Play` 再执行效果（`Beckon` + `CentennialPuzzle`）；消除 `Pillage` / `StormOfSteel`+`Reflex` / `EchoingSlash` / `HellraiserPower` 死循环，并补全 `ExplosivePower`（`SteamEruption`）、`WaterfallGiant` 毒杀防复活、`CorruptionPower`、`AsleepPower` vs `PlatingPower`、`PaelsEye` 额外回合与 `EmotionChip` 致命复活边界。
- **怪物、召唤与实机选牌续接**：`ToughEgg` 孵化 `RemoveAllPowersExceptMinion` 保留 `ImpaledPower`；`ForceMonsterMove` 新增 `forceTransition` 参数尊重 `MustPerformOnceBeforeRepeat`（`DecimillipedeSegment` `STUNNED`），同时修正 `HotRootBuilder.ComputeLiveDeathPhase` 依据原版 `ShouldAllowHitting` 等接口判定 `Reviving`；`CrushUnder` / `DyingStar` 改为攻击前快照存活敌人；`NativeChoiceRuntime` 与 `HotPlanMaterializer` 补强 `StateKey` 消歧、空提示保护与降级匹配，清零高风险实机 `soak` 重算与卡死。

### 搜索、评估、剪枝与药水策略调优（Wave 2–4）

- **协调与分期预算 (`HotSearchCoordinator`)**：保留 Stage 3 `DeathSaveUseCount > 0` 逃生路线导向（`PB-026` 避免死亡救赎）；完善多轮传递 `BoundaryReason`（`WithBoundaryReason`）避免掩盖 `TimeLimit`/`NodeLimit`；为 Stage 2B 增加高伤无星减损战抗饿死配额，同时将常规 Boss `baseTurnShare` 与非药水轮 `earlyTurnBoundaryWidth` / `nonPruneRolloutCap=32` 恢复稳态防止早截断。
- **评估与展开 (`HotEvaluator` / `HotParallelExpander` / `HotRolloutEvaluator` / `HotBeamRetention` / `HotTranspositionTable` / `HotPotionPolicy`)**：`HotEvaluator` 引入 Poison 48% 上限与复活怪半折、`FEED` `EffectiveGrowthBudgets` 上限、`Buffer` 每段与回合末自伤及 1 回合衰变精度，并保持核心 `BossHpRelief` / `enemyDebuffValue` / 跨回合 setup 平衡；`HotBeamRetention` 启用 `SelectTopKUnmarked` O(1) `heap[0]` 过滤、`MinZeroPotionReservePercent >= 25%` 兜底、`ChoiceLookahead` >=2 保护、`OptionalPaidPotionCount` 分桶与非胜利节点禁用无条件回血扣除；`HotTranspositionTable` 启用 `InsertAdmitted` 槽位复用与优先级链驱逐；`HotRolloutEvaluator` 实施懒规范化、`CanSkipPreStepCopy` 与未来抽牌堆/保留牌选牌修正；`HotParallelExpander` 加入引导分块、StandPat 池化与防弱牌排序；`HotPotionPolicy` 将 `BLOOD_POTION` 成本按 `MaxHp` 动态缩放。
- **已知后续跟进项**：170-Root 中 `PB-052-IRONCLAD-DECIMILLIPEDE_ELITE` 出现单例 `W->L`（0 药水第 3 回合落败，`PB-097` 同步实现 `L->W` 抵消净胜场），以及 `holdout` 41 局均值 V 小幅回退 `-0.51`，留待后续轮次继续收敛。

1.0.1 的遗物计数／局外成长全链路恢复与高风险重算清零见[历史卷 34](archive/development/volume-34.md)，玩家说明见[1.0.1 更新日志](releases/1.0.1-RELEASE_NOTES.md)。

1.0.0 的 HotKernel v2 新一代高速引擎、药水省血阈值与 0.113.1 全量适配见[历史卷 33](archive/development/volume-33.md)，玩家说明见[1.0.0 更新日志](releases/1.0.0-RELEASE_NOTES.md)。

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
