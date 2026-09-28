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
| `Largesse` | `OnPlay` 从目标玩家可解锁无色池消耗 `CombatCardGeneration` 生成牌；升级版升级生成牌，加入目标玩家手牌；`base.Owner` 是 `AddGeneratedCardToCombat` 的创建者参数 | 生成池、目标牌主、创建者、RNG |
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

原版目标与受益归属复核：15 张 `AnyAlly` 牌的手动目标均须是另一名存活玩家，依据 `NTargetManager.AllowedToTargetCreature`；`CardModel.CanPlayTargeting` 本身不排除出牌者，不能单独用于搜索候选。8 张 `AllAllies` 牌的 `GetTeammatesOf` 包括出牌者。`ImitationLearning`、`Intercept`、`Mimic` 有自身收益，`DemonicShield` 有自身掉血成本；`Largesse` 给队友生成牌，`Tutor` 需要队友原生选牌。这是原版源码核对，运行证据按下文实际探针分别记录。

`Largesse` 四人代表：本地 0 号对 3 号队友原生出牌，显式断言生成牌进 3 号手牌且牌主为 3 号，所有玩家状态与完整 RNG 对齐，Passed：`.local/multiplayer-p2/largesse-four-target-seat3-eabd09421fe94b42985aeda221619b15/peer-0/result.json`。基础／升级的双人即时差分与余费支援原生路线证据见下文和规划记录；四人这一请求只验基础版单次出牌。

生产控制器另以本地 `Largesse` 指向队友原生执行，生成牌归目标队友、随机流正常前进且没有旧预测偏差误报，Passed：`.local/multiplayer-p4/own-largesse-rng-final-1127aa6c05374ca293827af5f3f55a4f/peer-0/result.json`。队友反向对本地玩家打 `Largesse` 的交错动作中，目标玩家取得生成牌，控制器观测到外部 RNG 变化并重评估后继续合法攻击，Passed：`.local/multiplayer-p4/mid-deploy-rng-fixed-d1f774413f044e81a46697a326d4a5b0/peer-0/result.json`。即时入手位置由前述内容差分验证；控制器回合结束后的归属断言仅检查目标玩家持有该牌。

同一四人目标身份再组合本地自用 `StrengthPotion`：搜索候选只含持有人自用的单项，原生自用后完整状态／RNG 与模拟一致，随后 `Largesse` 仍把新牌给 3 号队友、牌主为该队友，Passed：`.local/multiplayer-p2/four-self-potion-largesse-ed2714885b2248118fb8d5ef70ec32a3/peer-0/result.json`。这只证明玩家目标药水的候选与自用代表，不代表所有药水已建模或求解器已实际部署药水。

玩家目标自用按机制追加格挡和能量：四人 `BlockPotion` `.local/multiplayer-p2/self-block-potion-four-cbc90f92c6f64218a5bd0cda95b38ab1/peer-0/result.json`、双人 `EnergyPotion` `.local/multiplayer-p2/self-energy-potion-two-a56b08d453e743228b3ce67070940b77/peer-0/result.json`，均核搜索只出现本地持有者的一个候选、原生自用即时全状态／完整 RNG 对齐。与力量药水合起来覆盖三种不同自用机制；选牌、治疗、球及被动触发等药水机制仍未收口。

## 已确认的关联调用链

- 抽牌、生成与格挡 Hook 在现有模拟中分别有 `AfterCardDrawnMirrors`（`CacophonyPower`）、`AfterCardGeneratedForCombatMirrors`（`SoulboundPower`）、`AfterBlockGainedMirrors`（`BeaconOfHopePower`）；当前还需核对这些镜像是否以多人状态和同一个 Fork 上下文结算。
- `BeforeCardPlayedMirrors`、`AfterCardPlayedMirrors` 处理 `ImitationLearningPower`；`AfterDamageGivenMirrors` 处理 `ConcoctPower`、`UnderworldPower`；`ModifyCardPlayCountMirrors` 处理 `TagTeamPower`。对应私有状态、来源和目标仍待多人差分。
- 现有 `CardDrawCardMirrors` 明确登记 `Constellation`、`HuddleUp`，`CardGenerationCardMirrors` 明确处理 `Largesse`；`CalculatedVarSpecRegistry` 列有 `GangUp`、`Mimic`、`DemonicShield`；`CardResultLocationMirrors` 登记 `TheBall`。其他卡牌可能由通用 spec 或 support 结算，须沿调用链逐项确认唯一入口。
- `MassiveScroll` 是已识别的多人专用遗物：`IsAllowed` 要求玩家数大于 1，`AfterObtained` 从角色与无色池的 `MultiplayerOnly` 卡中提供三选一；这是战前牌组来源，战斗中仍需覆盖其产生的牌。`InterceptPower`／`GuardedPower`、临时力量／敏捷、`FrostOrb`、`Shiv`、`Soul`、`Osty` 和生成池是上表直接依赖，不能只验卡牌主效果。

目前能定位到的显式模拟入口如下；已通过的卡牌即时差分范围见下文，其余入口仍待多人差分：`CardOnPlayMirrors` 登记 `Constellation`、`HuddleUp`、`Ignition`、`Largesse`；`AfterBlockGainedMirrors` 登记 `BeaconOfHopePower`；`AfterCardDrawnMirrors` 登记 `CacophonyPower`；`AfterDamageGivenMirrors` 登记 `ConcoctPower`、`UnderworldPower`；`BeforeCardPlayedMirrors` 和 `AfterCardPlayedMirrors` 登记 `ImitationLearningPower`，后者还登记 `SneakyPower`；`ModifyCardPlayCountMirrors` 登记 `TagTeamPower`；`AfterCardGeneratedForCombatMirrors` 登记 `SoulboundPower`；`AfterCardExhaustedMirrors` 登记 `Midnight`；`AfterPlayerTurnStartMirrors` 登记 `HibernatePower`；`FrostOrbMirrors`、`CardResultLocationMirrors` 分别处理球与 `TheBall` 去向；`CalculatedVarSpecRegistry` 登记 `GangUp`、`Mimic`、`DemonicShield`。其余主效果要逐项补入权威入口，不能仅靠关联 Hook 已存在判定可用。

## 普通内容的多人差异扫描

本机 0.111.0 原版 Cards 目录定向扫描后，普通卡的实际调用点分为：`Stoke`、`Splash`、`WhiteNoise`、`Metamorphosis`、`Quasar`、`Jackpot`、`JackOfAllTrades`、`ManifestAuthority`、`MadScience`、`InfernalBlade`、`BundleOfJoy`、`Distraction`、`Discovery`、`Abundance` 使用 `CardMultiplayerConstraint` 过滤战斗生成池；`Omnislice` 用受击敌人的存活队友二次分配伤害；`BeatDown`、`BouncingFlask` 消耗共享 `CombatTargets` 随机敌人流。原先搜索结果中的 `IAmInvincible`、`HowlFromBeyond`、`SovereignBlade`、`Soul`、`ThrummingHatchet`、`RocketPunch`、`Shiv`、`Bombardment`、`Bolas`、`ByrdonisEgg`、`FlakCannon` 仅因 `using ...Players` 命中，不构成额外多人分支；其普通效果仍受通用玩家所有权和目标规则约束。以上是源码分类，相关模拟入口和差分仍待 P2 核对。

`Fasten` 的 `CardMultiplayerConstraint` 仅用于 `ExtraHoverTips` 选择展示哪张防御牌，`OnPlay` 只给出牌者施加 `FastenPower`；它不是战斗生成池分支。此项由原版 `Fasten.cs` 直接调用位置核对，未做本轮原生差分。

普通生成池首批：`InfernalBlade`、`JackOfAllTrades`、`Metamorphosis` 在虚拟双人基础／升级各一次，逐张即时原生／模拟全状态及完整 RNG 差分 Passed：`.local/multiplayer-p2/ordinary-generation-base-0c7da3f98c164553b69268974a58fc30/peer-0/result.json`、`.local/multiplayer-p2/ordinary-generation-upgrade-caed2c83160540dbbe9d99fc15e0d7b6/peer-0/result.json`。三张牌分别覆盖角色攻击池免费本回合、无色池多张生成、角色攻击池随机插入抽牌堆；只证明这组输入实际抽到的生成结果，不覆盖全部可解锁池或后续打出生成牌。

普通生成池第二批：`BundleOfJoy`、`Distraction`、`WhiteNoise` 在虚拟双人基础／升级逐张即时全状态及完整 RNG 差分 Passed：`.local/multiplayer-p2/ordinary-generation-hand-base-f0686a01182f4a6c94632392cab215a7/peer-0/result.json`、`.local/multiplayer-p2/ordinary-generation-hand-upgrade-8b164232336d4b52ba80637493c81fe0/peer-0/result.json`。本批覆盖无色多张入手、角色技能／能力随机入手及本回合免费；仍只对应这些输入实际生成的对象。

普通生成池第三批：`Jackpot`、`ManifestAuthority` 在虚拟双人基础／升级逐张即时全状态及完整 RNG 差分 Passed：`.local/multiplayer-p2/ordinary-generation-attack-base-e550f1c902f648c89132e47b785f5618/peer-0/result.json`、`.local/multiplayer-p2/ordinary-generation-attack-upgrade-c8716d189491435b8d77890fe79188a9/peer-0/result.json`。覆盖攻击后生成零费角色牌及自身格挡后生成无色牌；后续使用生成牌与更多随机池结果未覆盖。

普通生成池选择批：测试器先在同根 Fork 读取原版镜像生成的三个候选，再从新 Fork 和原版选牌界面选择相同的第一个候选。`Discovery` 基础版 `.local/multiplayer-p2/ordinary-choice-discovery-base-2cc263cdf62146e68a62ce728dc0b893/peer-0/result.json`、`Abundance`／`Quasar`／`Splash` 基础版 `.local/multiplayer-p2/ordinary-choice-rest-base-7aca4e54e83d4f9a904b430d6bde89c3/peer-0/result.json`，四张升级版 `.local/multiplayer-p2/ordinary-choice-upgrade-4bae4dbe2a0245c9b50d836a7468ace9/peer-0/result.json` 均 Passed，即时全状态及完整 RNG 对齐。只覆盖所选选项，未覆盖跳过、其他候选或跨回合使用。

`Stoke` 在手牌另有三张 `DefendIronclad` 时将其消耗，再按数量从多人过滤后的角色池生成；基础版 `.local/multiplayer-p2/ordinary-stoke-base-4fca535f6f224b06a11baa7bcf8d4b23/peer-0/result.json`、升级版 `.local/multiplayer-p2/ordinary-stoke-upgrade-687fefeb63a14764a0e8637f9e8ae571/peer-0/result.json` 即时全状态／RNG 差分 Passed。`MadScience` 使用原版可达的 Skill／Chaos 组合，基础版 `.local/multiplayer-p2/ordinary-mad-science-base-34c7be43f6a7487fb15be48380e94904/peer-0/result.json`、升级版 `.local/multiplayer-p2/ordinary-mad-science-upgrade-357fe34b0b5a45f9b0a0c70b76231391/peer-0/result.json` 即时全状态／RNG 差分 Passed；其他类型／Rider 不在本批生成池分支证据内。

上述 14 张实际在战斗结算中使用 `CardMultiplayerConstraint` 的普通牌均取得基础／升级一次即时差分；它们的随机池多种结果、生成牌后续动作、选牌分支及相关 Hook 仍需按机制补足。`Fasten` 只在悬停说明使用该约束，已单独归类。

普通共享目标机制首批：双敌 `CULTISTS_NORMAL`，`Omnislice` 命中目标后对另一敌分配伤害、`BeatDown` 从弃牌堆自动打出两张攻击、`BouncingFlask` 按共享 `CombatTargets` 随机选择多次敌人。三张牌基础／升级逐张即时全状态与完整 RNG 差分 Passed：`.local/multiplayer-p2/ordinary-shared-target-base-9221f0b073ec4ee5b3e654ae761ec833/peer-0/result.json`、`.local/multiplayer-p2/ordinary-shared-target-upgrade-5602bef999cd4eeba20e79f044afe779/peer-0/result.json`。本次只有双敌一种状态和一组随机流，未覆盖敌人中途死亡、不同自动牌目标类型或更多敌人数。

药水目录中有 51 个类型直接声明 `TargetType.AnyPlayer`。按当前产品边界，本地玩家持有的这些药水只以自己为目标，不枚举队友；敌人目标药水仍按原版合法目标枚举。当前 `CombatBeamSolver.Expansion.Candidates.TargetsForPotion` 对 `AnyPlayer`／`Self` 只产生一个本地自用候选，模拟 `PotionOnUseSupport.Use` 将空目标解析为持有人，原版 `PotionModel.EnqueueManualUse` 也这样解析；生产多人门禁仍在；本地自用代表的原生证据见下文。P0/P2 核对本地自用效果、多人生成池、共享 RNG 和队友已有被动触发，按机制选代表验收目标限制，不逐瓶测试不存在的队友投药路径。

敌人目标另验四人双敌 `FirePotion`：本地持有者的候选正好为两名存活敌人，没有玩家目标，Passed：`.local/multiplayer-p2/enemy-potion-targets-0f2fa7cb78094b5ca2b982b876d05897/peer-0/result.json`。本次只验候选枚举，未验原生投药效果。

一瓶 `StrengthPotion` 已在虚拟双人完成本地持有者自用的原生／模拟全状态与 RNG 差分，证据 `.local/multiplayer-p2/self-potion-060b3eb9d3de4f8c958b1ab84c19a4d4/peer-0/result.json`。这只代表玩家目标自用入口；其余药水机制、生成池及正式执行仍待验。

Power 目录中直接遍历玩家集合／队友的战斗候选为 `BeaconOfHopePower`、`HammerTimePower`、`TankPower` 和 `PlatingPower`；球目录有 `FrostOrb`。`DoomPower` 的 `GetTeammatesOf` 位于死亡特效等待，结算仍须按全阵营 Doom 生命周期验收；`ReattachPower` 的队友是蜈蚣怪物分段，按怪物死亡／复活验收。怪物目录的结算候选为 `ToughEgg`（卵孵化 HP 缩放）、`WaterfallGiant`／`KnowledgeDemon`（治疗随玩家数变化）、`TheObscura`／`Queen`（怪物同伴能力）、`KinPriest`／`Ovicopter`／`TwoTailedRat`／`LivingShield`／`Fabricator`（同伴存活与召唤条件）、`DecimillipedeSegment`（玩家数与分段 HP／复活）、`TestSubject`（重生 HP 缩放）、`GremlinMerc`（逐玩家创建目标型 `ThieveryPower`）。`Parafright`／`EyeWithTeeth` 的 `GetTeammatesOf` 命中动画死亡条件，不是战斗结算分支。这些实际调用条件与模拟入口仍须逐项验收。

遗物目录直接涉及人数或战斗生成池的入口包括 `MassiveScroll`（多人专属牌来源）、`Toolbox`、`VexingPuzzlebox`、`OrangeDough`、`ChoicesParadox`（战斗生成池），以及 `BigHat`、`Crossbow`、`ScrollBoxes`、`DustyTome`、`DistinguishedCape`、`NeowsBones`（战前／局外池）。`WingedBoots` 和 `SilverCrucible` 只允许单人，`LastingCandy` 读取局外玩家集合；`WhisperingEarring` 自动用玩家目标药水时指向持有人。战斗生成物和多人可达牌进入 P2；局外获得路径只登记来源，本批不扩展为战前求解器。

`GremlinMerc` 的入场 `ThieveryPower` 按每名玩家创建一个实例，首回合 `GIMME_MOVE` 后对每个实例调用 `Steal`。模拟曾只读取首个实例；按原版逐实例扣对应玩家金币并更新每条能力的已偷金币后，双人和四人到第二回合完整状态／RNG 差分 Passed，证据见[规划 0.2 节](MULTIPLAYER_PLAN.md)。其他招式及死亡链路的证据见下文。

第二回合 `DOUBLE_SMASH_MOVE` 又发现虚弱应施给所有存活玩家；模拟由本地单目标改为遍历全体玩家，第三回合完整状态／RNG 差分 Passed：`.local/multiplayer-p2/gremlin-merc-second-fixed-31e35320566f4014b7c3f198bcb80012/peer-0/result.json`。第三招与偷窃返还的证据见下文。

第三回合 `HEHE_MOVE` 的攻击、敌人力量 2 与每名玩家逐实例失去累计 60 金币，在第四回合完整状态／RNG 差分 Passed：`.local/multiplayer-p2/gremlin-merc-third-move-54881f1b85174c2b9c8c329b66fb19af/peer-0/result.json`。死亡链路的证据见下文；玩家中途死亡仍待验。

首轮偷窃后第二回合由本地玩家击杀 `GremlinMerc`，其 `SurprisePower` 生成胖／鬼祟地精；胖地精两条 `HeistPower` 逐条绑定原被偷玩家、金额各 20，原生和预测完整状态／RNG 差分 Passed：`.local/multiplayer-p2/gremlin-merc-death-fixed-stamp-8bc637ffb298497ba5d8ed6e9da0c927/peer-0/result.json`。胖地精后续死亡返还见下文；玩家中途死亡未验。

继续击杀胖地精，原版 `CombatRoom.ExtraRewards` 给两名目标玩家各加入 20 金币追回奖励，同时预测与原版战斗状态／九条 RNG 对齐，Passed：`.local/multiplayer-p2/heist-recovery-c59c5d5a889143068996ab4b51bf28ca/peer-0/result.json`。模拟本身只覆盖战斗内续用，不把原版房间奖励复制进战斗快照；战后领取未验。

`WaterfallGiant` 双人固定根前四个敌方回合实测：`STOMP` 的 `WeakPower` 按原版覆盖所有存活目标玩家后，第四回合完整状态／RNG Passed：`.local/multiplayer-p2/waterfall-giant-rounds-fixed-5354778cf43b427eb2324cb7fa11d66b/peer-0/result.json`。第四招 `SIPHON` 前使敌人损失 40 HP，原版按两名玩家份额治疗，预测及全状态／RNG 到第五回合 Passed：`.local/multiplayer-p2/waterfall-siphon-explicit-e1c7efeb896f4076a1b2418f991a5a69/peer-0/result.json`。其他阶段未验。

`OvicopterNormal` 首回合生成三只 `ToughEgg` 的双人初始 HP、`HatchPower` 与完整 RNG 已差分通过：`.local/multiplayer-p2/ovicopter-egg-round-6fd0947191ef4bba8ec663af8f1c4009/peer-0/result.json`。卵在该检查点尚未孵化，`ToughEgg.Hatch()` 的随机 HP 重设仍待单独验证。

随后推进第二个敌方回合，原版三只卵全部孵化、`HatchPower` 消失并按人数缩放随机重设 HP；显式孵化断言与完整续用状态／RNG 差分 Passed：`.local/multiplayer-p2/ovicopter-hatch-assert-f2e4b9fea5b04323b5da3cba71aff9d7/peer-0/result.json`。死亡前孵化及其他种子未覆盖。

通用多人缩放覆盖原版 `CombatState.AddMonster` 的新怪 HP、`MultiplayerScalingModel` 的敌方来源格挡，以及 `PowerCmd.Apply` 对敌方新施加能力的幅度。`ShouldScaleInMultiplayer=true` 的原版能力为 `PlowPower`、`PlatingPower`、`SlipperyPower`、`CurlUpPower`、`ReattachPower`、`FlutterPower`、`SkittishPower`、`RegenPower`、`RampartPower`、`ShriekPower`、`HardenedShellPower`、`ArtifactPower`；其中 `PlatingPower` 另在施加后把递减值设为玩家数。`BufferPower` 虽覆盖缩放函数，但其 `ShouldScaleInMultiplayer` 沿用默认 false，不进入该路径。当前模拟对新施加敌方能力调用原版缩放函数；首批四人原生差分核对 `ArtifactPower`、`PlatingPower`、`SlipperyPower`、`SkittishPower`、`CurlUpPower` 五项，覆盖默认倍率、三种特殊公式及附属递减值。其余七项的即时差分见下文；触发生命周期及怪物 HP 缩放仍待验证。

本轮四人把其余七项 `Plow`、`Reattach`、`Flutter`、`Regen`、`Rampart`、`Shriek`、`HardenedShell` 也逐项做新根原生施加与预测 Fork 的完整状态／RNG 差分，连同前五项同批 12／12 Passed：`.local/multiplayer-p2/all-enemy-power-scaling-3eb5d0a936b342a08bd0970baa5e09fc/peer-0/result.json`。这封闭上述 12 项的即时缩放路径，不封闭各自后续监听与怪物专属用法。`DecimillipedeElite` 三段敌人首轮另发现 `CONSTRICT_MOVE` 的虚弱应给全体存活玩家，修正后到第二回合完整差分 Passed：`.local/multiplayer-p2/decimillipede-round-fixed-b5a8732d07ee4bb7a06e59d920a237ad/peer-0/result.json`；分段死亡与重附由下一个独立场景验证。

分段死亡与重附另在双人原生根通过：先击杀一段，原版首个敌方回合保持死亡，下一敌方回合 `REATTACH_MOVE` 复活；三个稳定边界的完整玩家／敌人状态及九条 RNG 与预测一致：`.local/multiplayer-p2/segment-reattach-full-14c99fc3007b47a08ab9846392e3a262/peer-0/result.json`。三段全部死亡及整场结束仍未覆盖。

`TheObscuraNormal` 前两次敌方行动及伙伴 `Parafright` 的双人全状态／RNG 到第三回合 Passed：`.local/multiplayer-p2/obscura-second-round-35ee5bad5bbb4718aed927b9cd2325a1/peer-0/result.json`；伙伴死亡和幻象后续分支未验。`QueenBoss` 的 `PUPPET_STRINGS_MOVE` 原版给所有目标玩家束缚，`YOU_ARE_MINE_MOVE` 同样给全体三种异常状态；修正模拟后双人前三招到第四回合的全部状态／RNG Passed：`.local/multiplayer-p2/queen-third-round-fddc55be530542df8dbbb00efbb3d3ac/peer-0/result.json`。女王死亡与后续条件分支未验。

`TwoTailedRatsNormal` 双人原生战斗中的 `SCREECH_MOVE` 给全部存活目标玩家施加脆弱；模拟修正后连续两个敌方回合的所有玩家／敌人状态和完整 RNG 差分 Passed：`.local/multiplayer-p2/two-tailed-rats-round-fixed-9193a1f28b1c4cb6bb0ea3d40685386c/peer-0/result.json`。召唤分支及其后续回合未验。

原版 `MonsterModel.PerformMove` 把 `CombatState.PlayerCreatures` 全部传给怪物招式。逐项核对直接接收 `targets` 的 `PowerCmd.Apply`、`CardPileCmd.AddToCombatAndPreview` 和显式 `foreach` 后，修正 29 处单玩家减益结算、14 类状态牌分发、力量／敏捷／收缩等单玩家能力、`TheInsatiable` 的逐玩家沙坑与逃生牌、`Aeonglass` 的逐玩家枯萎升级和 `ThievingHopper` 的逐玩家偷牌。源码核对只证明目标范围与结算顺序，逐招效果尚未全部差分。代表性原生双人差分 Passed：`TheKinBoss` 前两招 `.local/multiplayer-p2/the-kin-round-6ed76c8562964caf8d05f9e560ae4660/peer-0/result.json`、`HauntedShipNormal` 群体减益加塞牌 `.local/multiplayer-p2/haunted-ship-round-0653a8921b9249158aeee7769dd155da/peer-0/result.json`、`AeonglassBoss` 到第四回合 `.local/multiplayer-p2/aeonglass-third-93674076c0b3426ca8d9210d192407fa/peer-0/result.json`、`SoulFyshBoss` 到第四回合 `.local/multiplayer-p2/soul-fysh-third-9b2f8e71a58846eeba17ced810298d01/peer-0/result.json`、`TheInsatiableBoss` 首轮 `.local/multiplayer-p2/insatiable-liquify-eb2b7791f691445b8da3d897e97d3492/peer-0/result.json`。`ThievingHopperWeak` 首轮另显式断言两名玩家各被偷一张自己的牌、敌人有两条对应 `SwipePower`，全状态／RNG 差分 Passed：`.local/multiplayer-p2/thieving-hopper-explicit-29c1d391025548ccacad6a46fca9b27c/peer-0/result.json`。其他种子、死人目标、战后归还与未运行招式仍未验。

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

`CacophonyPower` 抽牌计数与阈值触发已补原生差分：`Cacophony`→`HuddleUp` 常规计数，及原版计数置 2 后跨阈值的随机伤害、重置和第二回合，均在双人完整状态／RNG 对账 Passed；证据见[规划 0.6 节](MULTIPLAYER_PLAN.md)。

`ImitationLearningPower` 的目标玩家打普通能力牌触发已补原生差分：队友 `Inflame` 后复制到本地持有者并自动打出，能力递减及第二回合完整状态／RNG Passed；证据见[规划 0.6 节](MULTIPLAYER_PLAN.md)。选择型能力与多次耗尽仍待验。

`HammerTimePower` 的锻造监听已补代表差分：持有者打 `TheSmith`，原版和模拟均为所有存活玩家生成或加强 `SovereignBlade`，完整状态／RNG Passed；证据见[规划 0.6 节](MULTIPLAYER_PLAN.md)。

`TheBall` 同一实例二次打出、逐次增伤与跨玩家转移；`LegionOfBone` 群体召唤后到第二回合的伙伴状态，均通过双人完整状态／RNG 差分，证据见[规划 0.6 节](MULTIPLAYER_PLAN.md)。
