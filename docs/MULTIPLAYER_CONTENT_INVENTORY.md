# 多人原版内容工作清单

状态：P0 盘点中，尚未封闭。原版依据为本机只读反编译 `sts2-v0.111.0/MegaCrit/sts2/Core/Models/`，游戏运行版本也由 P0 原生请求核为 `0.111.0`。本表记录源码调查，不表示预测实现或差分已经通过。正式验收逐项补齐“原版入口 → 唯一模拟入口 → 状态所有者／Fork → 基础及升级差分 → 实际结果”。

## 直接声明 MultiplayerOnly 的 37 张卡

原版入口列只列会改变战斗的关键入口；常规出牌、费用、升级及牌堆生命周期仍须验收。每张卡的模拟入口、基础／升级原生差分当前均待核对，不能凭登记名称判定支持。

| 卡牌类型 | 原版入口与关联状态／内容 | 机制批次 |
|---|---|---|
| `BeaconOfHope` | `OnPlay` 施加 `BeaconOfHopePower`；`AfterBlockGained` 给其他存活玩家格挡，Power 内有防止互相递归的私有标志 | 格挡监听、跨玩家传播 |
| `BelieveInYou` | `OnPlay` 给指定玩家能量 | 指定队友资源 |
| `BladeSymphony` | `OnPlay` 为所有存活玩家逐张生成 `Shiv` 到各自手牌 | 群体生成、牌主 |
| `Blaze` | `OnPlay` 给指定玩家 `StrengthPower`，施加者为出牌者 | 指定队友能力 |
| `Cacophony` | `OnPlay` 施加 `CacophonyPower`；抽牌计数归零后用 `CombatTargets` 随机敌人并造成伤害 | 抽牌监听、私有计数、RNG |
| `Concoct` | `OnPlay` 给指定玩家 `ConcoctPower`；其攻击破防时施毒，敌方回合结束移除 | 来源与目标、回合生命周期 |
| `Constellation` | `OnPlay` 给指定玩家抽牌、能量和格挡，另有星能费用 | 指定队友资源、抽牌 |
| `Coordinate` | `OnPlay` 给指定玩家 `CoordinatePower`（临时力量） | 指定队友能力、跨回合 |
| `DemonicShield` | `OnPlay` 自身掉血，再按自身格挡给目标玩家格挡 | 自身成本、跨玩家格挡 |
| `EnergySurge` | `OnPlay` 给所有存活玩家能量 | 群体资源 |
| `Fade` | `OnPlay` 给指定玩家 `FadePower`（临时敏捷） | 指定队友能力、跨回合 |
| `Flanking` | `OnPlay` 给敌人 `FlankingPower`；除施加者之外的玩家攻击增伤，敌方回合后移除 | 来源身份、攻击修正 |
| `GangUp` | `CalculatedDamageVar` 按本回合其他同阵营来源对目标造成的攻击伤害历史加伤 | 历史、来源身份 |
| `GlimpseBeyond` | `OnPlay` 为各存活玩家生成 `Soul` 并随机插入其抽牌堆 | 群体生成、牌堆顺序、RNG |
| `HammerTime` | `OnPlay` 施加 `HammerTimePower`；持有者锻造时给其他存活玩家锻造 | 锻造监听、跨玩家传播 |
| `Hibernate` | `OnPlay` 施加 `HibernatePower` 并充能 `FrostOrb`；该 Power 由 Frost 球读取，持有者回合开始递减 | 球、Power、跨回合 |
| `HuddleUp` | `OnPlay` 对所有存活玩家调用 `DrawWithoutBlockingOnOtherPlayers` | 群体抽牌、选择等待 |
| `Ignition` | `OnPlay` 给指定玩家充能 `PlasmaOrb` | 球归属与队列 |
| `ImitationLearning` | `OnPlay` 按目标玩家叠加／施加 `ImitationLearningPower`；Power 保存目标玩家引用及原牌／复制牌配对，监听目标能力牌并由持有者自动打出复制品 | 跨玩家引用、私有状态、嵌套出牌 |
| `Intercept` | `OnPlay` 自身得格挡、目标得 `CoveredPower`；关联 `InterceptPower` 记录受保护者，涉及施加者死亡和敌方回合结束 | 跨玩家引用、死亡、保护 |
| `Knockdown` | `OnPlay` 攻击并施加 `KnockdownPower`；其他玩家攻击增伤，敌方回合后移除 | 来源身份、攻击修正 |
| `Largesse` | `OnPlay` 从目标玩家可解锁无色池消耗 `CombatCardGeneration` 生成牌；升级版升级生成牌，加入目标手牌 | 生成池、牌主、RNG |
| `LegionOfBone` | `OnPlay` 给每个存活玩家调用 `OstyCmd.Summon` | 群体伙伴、召唤 |
| `Lift` | `OnPlay` 给指定玩家格挡 | 指定队友格挡 |
| `Midnight` | `OnPlay` 攻击；入场时按历史消耗次数减费，之后每次消耗继续减费 | 历史、费用、跨回合 |
| `Mimic` | `CalculatedBlockVar` 读取目标格挡，`OnPlay` 给出牌者等量格挡 | 指向队友、自身收益 |
| `OneForAll` | `OnPlay` 给所有玩家 `OneForAllPower`；该 Power 增加其持有者零费攻击伤害 | 群体能力、伤害修正 |
| `Outrage` | `OnPlay` 攻击后为所有存活玩家生成本卡克隆并放入各自弃牌堆 | 群体生成、牌主、牌堆 |
| `Plot` | `OnPlay` 给所有存活玩家 `DrawCardsNextTurnPower` | 群体能力、跨回合抽牌 |
| `Rally` | `OnPlay` 给所有存活玩家格挡 | 群体格挡 |
| `Sneaky` | `OnPlay` 施加 `SneakyPower`；其他玩家打攻击牌时给持有者格挡 | 他人动作监听 |
| `Soulbound` | `OnPlay` 给指定玩家 `SoulboundPower`；施加者生成 `Soul` 时目标也生成，Power 有防递归私有标志 | 生成监听、跨玩家引用 |
| `TagTeam` | `OnPlay` 攻击并施加 `TagTeamPower`；其他玩家下一张合条件攻击追加出牌次数后移除 | 出牌次数、来源身份 |
| `Tank` | `OnPlay` 施加 `TankPower`；给其他存活玩家 `GuardedPower`，持有者受到攻击时增伤 | 群体保护、伤害修正 |
| `TheBall` | `OnPlay` 攻击并累加逐实例伤害；`GetResultLocationForCardPlay` 用 `CombatTargets` 随机转给另一玩家，弃牌改为随机插入其抽牌堆；降级还原内部增伤 | 私有计数、所有权转移、RNG |
| `Tutor` | `OnPlay` 由目标玩家从其抽牌堆原生选择一张进手牌 | 队友原生选牌等待 |
| `Underworld` | `OnPlay` 施加 `UnderworldPower`；其他玩家或其伙伴的攻击造成伤害后叠加 `DoomPower`，敌方回合结束移除 | 来源身份、伙伴、伤害监听 |

## 已确认的关联调用链

- 抽牌、生成与格挡 Hook 在现有模拟中分别有 `AfterCardDrawnMirrors`（`CacophonyPower`）、`AfterCardGeneratedForCombatMirrors`（`SoulboundPower`）、`AfterBlockGainedMirrors`（`BeaconOfHopePower`）；当前还需核对这些镜像是否以多人状态和同一个 Fork 上下文结算。
- `BeforeCardPlayedMirrors`、`AfterCardPlayedMirrors` 处理 `ImitationLearningPower`；`AfterDamageGivenMirrors` 处理 `ConcoctPower`、`UnderworldPower`；`ModifyCardPlayCountMirrors` 处理 `TagTeamPower`。对应私有状态、来源和目标仍待多人差分。
- 现有 `CardDrawCardMirrors` 明确登记 `Constellation`、`HuddleUp`，`CardGenerationCardMirrors` 明确处理 `Largesse`；`CalculatedVarSpecRegistry` 列有 `GangUp`、`Mimic`、`DemonicShield`；`CardResultLocationMirrors` 登记 `TheBall`。其他卡牌可能由通用 spec 或 support 结算，须沿调用链逐项确认唯一入口。
- `MassiveScroll` 是已识别的多人专用遗物，原版效果与模拟入口待核对。`InterceptPower`／`GuardedPower`、临时力量／敏捷、`FrostOrb`、`Shiv`、`Soul`、`Osty` 和生成池是上表直接依赖，不能只验卡牌主效果。

## 普通内容的多人差异扫描

本机 0.111.0 原版的 Cards／Powers／Relics／Potions／Orbs／Monsters 目录中，对玩家枚举、队友目标、多人约束及 `CombatTargets` 的定向搜索已经列出候选，尚未沿调用链封闭。普通卡至少需核对 `Stoke`、`Splash`、`IAmInvincible`、`HowlFromBeyond`、`Fasten`、`WhiteNoise`、`SovereignBlade`、`Metamorphosis`、`Quasar`、`Jackpot`、`Soul`、`JackOfAllTrades`、`ThrummingHatchet`、`RocketPunch`、`Shiv`、`ManifestAuthority`、`MadScience`、`InfernalBlade`、`Omnislice`、`BundleOfJoy`、`Distraction`、`Discovery`、`BeatDown`、`Abundance`、`BouncingFlask`、`Bombardment`、`Bolas`、`ByrdonisEgg`、`FlakCannon`。这些是待调查名单，不表示每项存在模拟缺口。

对应检索还命中普通遗物、药水、能力、球与怪物；后续盘点必须区分实际多人分支、共享 RNG、只用于展示的引用以及普通单人路径。当前不能宣称原版内容清单已经封闭，也没有任何卡牌的多人 actual/simulated 通过记录。
