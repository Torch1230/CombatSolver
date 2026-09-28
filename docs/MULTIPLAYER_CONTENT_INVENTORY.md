# 多人原版内容工作清单

状态：P0 盘点中，尚未封闭。原版依据为本机只读反编译 `sts2-v0.111.0/MegaCrit/sts2/Core/Models/`，游戏运行版本也由 P0 原生请求核为 `0.111.0`。本表记录源码调查，不表示预测实现或差分已经通过。正式验收逐项补齐“原版入口 → 唯一模拟入口 → 状态所有者／Fork → 基础及升级差分 → 实际结果”。

## 直接声明 MultiplayerOnly 的 37 张卡

原版入口列只列会改变战斗的关键入口；常规出牌、费用、升级及牌堆生命周期仍须验收。下方另列已找到的显式模拟入口；没有显式登记的牌仍可能进入现有简单效果推断器，但推断器只识别攻击、格挡和自身抽牌，不能据此认为其他效果已实现。37 张卡已取得基础／升级的即时结算差分，见下文；跨回合与关联 Hook 尚未因此通过。

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
- `MassiveScroll` 是已识别的多人专用遗物：`IsAllowed` 要求玩家数大于 1，`AfterObtained` 从角色与无色池的 `MultiplayerOnly` 卡中提供三选一；这是战前牌组来源，战斗中仍需覆盖其产生的牌。`InterceptPower`／`GuardedPower`、临时力量／敏捷、`FrostOrb`、`Shiv`、`Soul`、`Osty` 和生成池是上表直接依赖，不能只验卡牌主效果。

目前能定位到的显式模拟入口如下；已通过的卡牌即时差分范围见下文，其余入口仍待多人差分：`CardOnPlayMirrors` 登记 `Constellation`、`HuddleUp`、`Ignition`、`Largesse`；`AfterBlockGainedMirrors` 登记 `BeaconOfHopePower`；`AfterCardDrawnMirrors` 登记 `CacophonyPower`；`AfterDamageGivenMirrors` 登记 `ConcoctPower`、`UnderworldPower`；`BeforeCardPlayedMirrors` 和 `AfterCardPlayedMirrors` 登记 `ImitationLearningPower`，后者还登记 `SneakyPower`；`ModifyCardPlayCountMirrors` 登记 `TagTeamPower`；`AfterCardGeneratedForCombatMirrors` 登记 `SoulboundPower`；`AfterCardExhaustedMirrors` 登记 `Midnight`；`AfterPlayerTurnStartMirrors` 登记 `HibernatePower`；`FrostOrbMirrors`、`CardResultLocationMirrors` 分别处理球与 `TheBall` 去向；`CalculatedVarSpecRegistry` 登记 `GangUp`、`Mimic`、`DemonicShield`。其余主效果要逐项补入权威入口，不能仅靠关联 Hook 已存在判定可用。

## 普通内容的多人差异扫描

本机 0.111.0 原版 Cards 目录定向扫描后，普通卡的实际调用点分为：`Stoke`、`Splash`、`Fasten`、`WhiteNoise`、`Metamorphosis`、`Quasar`、`Jackpot`、`JackOfAllTrades`、`ManifestAuthority`、`MadScience`、`InfernalBlade`、`BundleOfJoy`、`Distraction`、`Discovery`、`Abundance` 使用 `CardMultiplayerConstraint` 过滤战斗生成池；`Omnislice` 用受击敌人的存活队友二次分配伤害；`BeatDown`、`BouncingFlask` 消耗共享 `CombatTargets` 随机敌人流。原先搜索结果中的 `IAmInvincible`、`HowlFromBeyond`、`SovereignBlade`、`Soul`、`ThrummingHatchet`、`RocketPunch`、`Shiv`、`Bombardment`、`Bolas`、`ByrdonisEgg`、`FlakCannon` 仅因 `using ...Players` 命中，不构成额外多人分支；其普通效果仍受通用玩家所有权和目标规则约束。以上是源码分类，相关模拟入口和差分仍待 P2 核对。

药水目录中有 51 个类型直接声明 `TargetType.AnyPlayer`。按当前产品边界，本地玩家持有的这些药水只以自己为目标，不枚举队友；敌人目标药水仍按原版合法目标枚举。当前 `CombatBeamSolver.Expansion.Candidates.TargetsForPotion` 对 `AnyPlayer`／`Self` 只产生一个本地自用候选，模拟 `PotionOnUseSupport.Use` 将空目标解析为持有人，原版 `PotionModel.EnqueueManualUse` 也这样解析；生产多人门禁仍在；本地自用代表的原生证据见下文。P0/P2 核对本地自用效果、多人生成池、共享 RNG 和队友已有被动触发，按机制选代表验收目标限制，不逐瓶测试不存在的队友投药路径。

一瓶 `StrengthPotion` 已在虚拟双人完成本地持有者自用的原生／模拟全状态与 RNG 差分，证据 `.local/multiplayer-p2/self-potion-060b3eb9d3de4f8c958b1ab84c19a4d4/peer-0/result.json`。这只代表玩家目标自用入口；其余药水机制、生成池及正式执行仍待验。

Power 目录中直接遍历玩家集合／队友的战斗候选为 `BeaconOfHopePower`、`HammerTimePower`、`TankPower` 和 `PlatingPower`；球目录有 `FrostOrb`。`DoomPower` 的 `GetTeammatesOf` 位于死亡特效等待，结算仍须按全阵营 Doom 生命周期验收；`ReattachPower` 的队友是蜈蚣怪物分段，按怪物死亡／复活验收。怪物目录的结算候选为 `ToughEgg`（卵孵化 HP 缩放）、`WaterfallGiant`／`KnowledgeDemon`（治疗随玩家数变化）、`TheObscura`／`Queen`（怪物同伴能力）、`KinPriest`／`Ovicopter`／`TwoTailedRat`／`LivingShield`／`Fabricator`（同伴存活与召唤条件）、`DecimillipedeSegment`（玩家数与分段 HP／复活）、`TestSubject`（重生 HP 缩放）、`GremlinMerc`（逐玩家创建目标型 `ThieveryPower`）。`Parafright`／`EyeWithTeeth` 的 `GetTeammatesOf` 命中动画死亡条件，不是战斗结算分支。这些实际调用条件与模拟入口仍须逐项验收。

遗物目录直接涉及人数或战斗生成池的入口包括 `MassiveScroll`（多人专属牌来源）、`Toolbox`、`VexingPuzzlebox`、`OrangeDough`、`ChoicesParadox`（战斗生成池），以及 `BigHat`、`Crossbow`、`ScrollBoxes`、`DustyTome`、`DistinguishedCape`、`NeowsBones`（战前／局外池）。`WingedBoots` 和 `SilverCrucible` 只允许单人，`LastingCandy` 读取局外玩家集合；`WhisperingEarring` 自动用玩家目标药水时指向持有人。战斗生成物和多人可达牌进入 P2；局外获得路径只登记来源，本批不扩展为战前求解器。

通用多人缩放覆盖原版 `CombatState.AddMonster` 的新怪 HP、`MultiplayerScalingModel` 的敌方来源格挡，以及 `PowerCmd.Apply` 对敌方新施加能力的幅度。`ShouldScaleInMultiplayer=true` 的原版能力为 `PlowPower`、`PlatingPower`、`SlipperyPower`、`CurlUpPower`、`ReattachPower`、`FlutterPower`、`SkittishPower`、`RegenPower`、`RampartPower`、`ShriekPower`、`HardenedShellPower`、`ArtifactPower`；其中 `PlatingPower` 另在施加后把递减值设为玩家数。`BufferPower` 虽覆盖缩放函数，但其 `ShouldScaleInMultiplayer` 沿用默认 false，不进入该路径。当前模拟对新施加敌方能力调用原版缩放函数；四人原生差分已核 `ArtifactPower`、`PlatingPower`、`SlipperyPower`、`SkittishPower`、`CurlUpPower` 五项，覆盖默认倍率、三种特殊公式及附属递减值。其他能力共享该通用入口，但其触发生命周期及怪物 HP 缩放仍待差分。

## 已通过的卡牌即时差分

虚拟双人 37 张多人专用卡的基础版和升级版，逐张对出牌后所有玩家、敌人、卡牌归属与牌堆、能力、球、资源及完整 RNG 的原生／模拟续用戳。按机制分批，仅对需要的状态设置前置值；以下证据只覆盖即时效果，不覆盖后续回合的能力触发、死亡、网络执行或搜索。

| 批次 | 卡牌 | 基础版／升级版证据目录 |
|---|---|---|
| 指定队友资源与能力 | `BelieveInYou`、`Lift`、`Blaze`、`Coordinate`、`Fade` | `.local/multiplayer-p2/targeted-simple-base-46267a63117142dfaa004cae2528db17/peer-0/`；`.local/multiplayer-p2/targeted-simple-upgrade-498a99ba00744db59f9e274737685995/peer-0/` |
| 群体资源、格挡、能力、生成 | `EnergySurge`、`Rally`、`Plot`、`OneForAll`、`BladeSymphony` | `.local/multiplayer-p2/group-simple-base-c52fbf802a544b99acd2dd7dece05b66/peer-0/`；`.local/multiplayer-p2/group-simple-upgrade-58ceacf88a56479b997969bd4f978aa3/peer-0/` |
| 抽牌、球、成本与复制格挡 | `Constellation`、`HuddleUp`、`Ignition`、`Mimic`、`DemonicShield` | `.local/multiplayer-p2/targeted-mixed-base-07fb67d8db5d4196bcd2cfa2ec4b40cc/peer-0/`；`.local/multiplayer-p2/targeted-mixed-upgrade-73d2febc7736446ca06e48c0e055bcb3/peer-0/` |
| 队伍生成与随机插牌 | `Outrage`、`GlimpseBeyond` | `.local/multiplayer-p2/team-generation-fixed-922d9fc0e55e4c3f8d91d0c6b3ee4203/peer-0/`；`.local/multiplayer-p2/team-generation-upgrade-c09706f8751f4a849b753f9ae7cc7421/peer-0/` |
| 既有生成、攻击、能力与转移 | `Largesse`、`GangUp`、`Knockdown`、`TheBall` | `.local/multiplayer-p2/existing-mixed-fixed-fab775350ee34ad186388567371b1e5e/peer-0/`；`.local/multiplayer-p2/existing-mixed-upgrade-d51e5bbf2b954ed2b8edc7e4d49f91dd/peer-0/` |
| 持续能力施加 | `BeaconOfHope`、`Cacophony`、`Concoct`、`Flanking`、`HammerTime` | `.local/multiplayer-p2/power-cards-base-b5ad7774b536489880d127f95f753795/peer-0/`；`.local/multiplayer-p2/power-cards-upgrade-c3a608ff94234c9ea09d0cb7dab80edc/peer-0/` |
| 球、保护、来源关联 | `Hibernate`、`Intercept`、`Sneaky`、`Soulbound`、`TagTeam` | `.local/multiplayer-p2/linked-powers-fixed2-af6ed1cf93f64d2ebdff45e80069e8e3/peer-0/`；`.local/multiplayer-p2/linked-powers-upgrade-466a2c781b7c445bb19a36770002765b/peer-0/` |
| 守护与伤害监听 | `Tank`、`Underworld` | `.local/multiplayer-p2/tank-underworld-fixed-5f4f59ef644a4679abcdc574da7f9661/peer-0/`；`.local/multiplayer-p2/tank-underworld-upgrade-d76ee709aadb42a8bbde0092a1e516c5/peer-0/` |
| 伙伴召唤与高费攻击 | `LegionOfBone`、`Midnight` | `.local/multiplayer-p2/summon-midnight-base-6133af7bf51440e0a1af112390da4dcc/peer-0/`；`.local/multiplayer-p2/summon-midnight-upgrade-5e9ac4a9ba9e4eeca1894b8e53f23f3a/peer-0/` |
| 目标玩家能力引用 | `ImitationLearning` | `.local/multiplayer-p2/imitation-base-5917619007294ba7a7499b7a2883e2d1/peer-0/`；`.local/multiplayer-p2/imitation-upgrade-8c82f81ae4114de398198a2175eca041/peer-0/` |
| 队友原生选牌 | `Tutor` | `.local/multiplayer-p2/tutor-base-a97afa936b3c4b3b94b418eb882736f5/peer-0/`；`.local/multiplayer-p2/tutor-upgrade-481f242390d34d10bef949f5d08734d9/peer-0/` |

上述二十二次成功请求状态均为 Passed。上述即时批次未包含 `GangUp` 的队友先行攻击历史和 `Knockdown` 的队友后续攻击；`TheBall` 此批只打出一次；`Midnight` 没有消耗历史；`ImitationLearning` 没有后续复制能力牌。持续能力仅验施加后的状态，不代表格挡、抽牌、锻造、死亡、打牌、伤害或球的后续监听通过。`Tutor` 已验队友原生选牌的结果，但搜索仍无法评价队友的未知选牌，遇到该候选明确失败。另有四人敌方能力缩放代表差分，见上文。37 张专用卡的即时效果已有基础／升级代表证据，内容清单、关联普通卡、怪物、遗物与药水尚未封闭。

后续机制代表补了 `BeaconOfHope` 格挡传播、`Soulbound` 生成联动、`GangUp` 队友伤害历史、`Concoct`／`Underworld`／`Flanking`／`Knockdown`／`TagTeam` 与队友攻击相互作用，以及 `Hibernate`／`Plot`／`Tank`／`Underworld` 到第二回合的结算；均通过所构造虚拟双人原生差分，具体证据见[规划 0.6 节](MULTIPLAYER_PLAN.md)。这并未覆盖所有叠加、死亡、选牌、特殊回合或怪物组合。
