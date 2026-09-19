# 《杀戮尖塔 2》源码严格对照审计：未适配实体清单 (纯净程序集数据)

> **历史快照（CombatSolver v0.6.0），不代表当前覆盖率。** 本文保留当时的审计内容；当前职责见 [架构地图](docs/ARCHITECTURE.md)，覆盖目录与可重跑证据见 [战斗 Hook 覆盖](docs/COMBAT_HOOK_COVERAGE.md) 和 [测试矩阵](docs/TEST_MATRIX.md)。不要按本文的“未适配”列表判断 0.41.0 或后续版本。

> **数据基准**：严格基于《杀戮尖塔 2》官方游戏程序集 `sts2.dll`（v0.111.0）反射元数据直接生成。  
> **对比范围**：`CombatSolver` (v0.6.0) 源码 (`CorePowerSupport.cs`, `MonsterMoveEffects.cs`, `CardChoiceSupport.cs`)。  
> **数据总览**：`sts2.dll` 中包含 **603 个 CardModel**、**283 个 PowerModel**、**126 个 MonsterModel**、**66 个 PotionModel**。

> [!WARNING]
> **免责声明 / 注意事项**：本报告包含由 AI 辅助分析生成的实体反射对照清单，仅供开发参考。可能会有 AI 幻觉或反射过滤规则差异导致的不准确之处，具体请以《杀戮尖塔 2》游戏本体源码与实机测试为准自行核实。

---

## 1. 实体统计对照

| 实体类别 | STS2 程序集总数 | 排除 Mock/Test 后的真实实体数 | CombatSolver 目前已处理数 | 未适配 / 缺口数 | 覆盖率 |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **PowerModel (Buff/Debuff)** | 283 | 265 | 12 | **253** | **4.5%** |
| **MonsterModel (敌人)** | 126 | 102 | 15 | **87** | **14.7%** |
| **CardModel (卡牌)** | 603 | 601 | ~280 | **~321** | **46.4%** |
| **PotionModel (药水)** | 66 | 64 | 0 | **64** | **0.0%** |

---

## 2. 怪物体系 (MonsterModel) 未适配清单

### 2.1 CombatSolver 目前已适配的 15 种怪物 (在 `MonsterMoveEffects.cs` 中)：
`SludgeSpinner`, `Flyconid`, `FrogKnight`, `BowlbugSilk`, `HauntedShip`, `Wriggler`, `FlailKnight`, `Exoskeleton`, `BowlbugNectar`, `Nibbit`, `CorpseSlug`, `SoulFysh`, `TheLost`, `PhantasmalGardener`, `WaterfallGiant`.

### 2.2 STS2 中尚未适配的 87 种真实怪物类名 (按字母排序，仅列出英文类名)：

* `Aeonglass`
* `Architect`
* `AssassinRubyRaider`
* `Axebot`
* `AxeRubyRaider`
* `BattleFriendV1`
* `BattleFriendV2`
* `BattleFriendV3`
* `BigDummy`
* `BowlbugEgg`
* `BowlbugRock`
* `BruteRubyRaider`
* `BygoneEffigy`
* `Byrdonis`
* `Byrdpip`
* `CalcifiedCultist`
* `CeremonialBeast`
* `Chomper`
* `CrossbowRubyRaider`
* `Crusher`
* `CubexConstruct`
* `DampCultist`
* `DecimillipedeSegmentBack`
* `DecimillipedeSegmentFront`
* `DecimillipedeSegmentMiddle`
* `DevotedSculptor`
* `Entomancer`
* `EyeWithTeeth`
* `Fabricator`
* `FakeMerchantMonster`
* `FatGremlin`
* `Fogmog`
* `FossilStalker`
* `FuzzyWurmCrawler`
* `GasBomb`
* `GlobeHead`
* `GremlinMerc`
* `Guardbot`
* `HunterKiller`
* `InfestedPrism`
* `Inklet`
* `KinFollower`
* `KinPriest`
* `KnowledgeDemon`
* `LagavulinMatriarch`
* `LeafSlimeM`
* `LeafSlimeS`
* `LivingFog`
* `LivingShield`
* `LouseProgenitor`
* `MagiKnight`
* `Mawler`
* `MechaKnight`
* `MysteriousKnight`
* `Myte`
* `Noisebot`
* `Osty`
* `Ovicopter`
* `OwlMagistrate`
* `PaelsLegion`
* `Parafright`
* `PhrogParasite`
* `PunchConstruct`
* `Queen`
* `Rocket`
* `ScrollOfBiting`
* `Seapunk`
* `SewerClam`
* `ShrinkerBeetle`
* `SkulkingColony`
* `SlimedBerserker`
* `SlitheringStrangler`
* `SlumberingBeetle`
* `SnappingJaxfruit`
* `SneakyGremlin`
* `SoulNexus`
* `SpectralKnight`
* `SpinyToad`
* `Stabbot`
* `TerrorEel`
* `TestSubject`
* `TheAdversaryMkOne`
* `TheAdversaryMkThree`
* `TheAdversaryMkTwo`
* `TheForgotten`
* `TheInsatiable`
* `TheObscura`
* `ThievingHopper`
* `Toadpole`
* `TorchHeadAmalgam`
* `ToughEgg`
* `TrackerRubyRaider`
* `Tunneler`
* `TurretOperator`
* `TwigSlimeM`
* `TwigSlimeS`
* `TwoTailedRat`
* `Vantom`
* `VineShambler`
* `Zapbot`

---

## 3. Power 体系 (PowerModel) 未适配清单

### 3.1 CombatSolver 目前已模拟的 12 种 Power (在 `CorePowerSupport.cs` 中)：
`StrengthPower`, `DexterityPower`, `WeakPower`, `VulnerablePower`, `FrailPower`, `IntangiblePower`, `PoisonPower`, `NoDrawPower`, `CrimsonMantlePower`, `RavenousPower`, `SteamEruptionPower`, `ArtifactPower`.

### 3.2 STS2 中尚未适配的 253 种真实 Power 类名 (按字母排序，仅列出英文类名)：

* `AccelerantPower`
* `AccuracyPower`
* `AdaptablePower`
* `AfterimagePower`
* `AggressionPower`
* `AmbergrisPower`
* `AnticipatePower`
* `ArsenalPower`
* `AsleepPower`
* `AutomationPower`
* `BackAttackLeftPower`
* `BackAttackRightPower`
* `BarricadePower`
* `BattlewornDummyTimeLimitPower`
* `BeaconOfHopePower`
* `BiasedCognitionPower`
* `BlackHolePower`
* `BlockNextTurnPower`
* `BlurPower`
* `BorrowedTimePower`
* `BufferPower`
* `BurrowedPower`
* `BurstPower`
* `CacophonyPower`
* `CalamityPower`
* `CalcifyPower`
* `CallOfTheVoidPower`
* `ChainsOfBindingPower`
* `ChildOfTheStarsPower`
* `ClarityPower`
* `ColossusPower`
* `ConcoctPower`
* `ConfusedPower`
* `ConquerorPower`
* `ConstrictPower`
* `ConsumingShadowPower`
* `CoolantPower`
* `CoordinatePower`
* `CorrosiveWavePower`
* `CorruptionPower`
* `CountdownPower`
* `CoveredPower`
* `CrabRagePower`
* `CreativeAiPower`
* `CrueltyPower`
* `CrushUnderPower`
* `CuriousPower`
* `CurlUpPower`
* `DampenPower`
* `DanseMacabrePower`
* `DarkEmbracePower`
* `DarkShacklesPower`
* `DebilitatePower`
* `DemesnePower`
* `DemisePower`
* `DemonFormPower`
* `DevourLifePower`
* `DieForYouPower`
* `DisintegrationPower`
* `DoomPower`
* `DoubleDamagePower`
* `DrawCardsNextTurnPower`
* `DuplicationPower`
* `DyingStarPower`
* `EchoFormPower`
* `EnergyNextTurnPower`
* `EnfeeblingTouchPower`
* `EnragePower`
* `EntropyPower`
* `EnvenomPower`
* `EscapeArtistPower`
* `FadePower`
* `FanOfKnivesPower`
* `FastenPower`
* `FeedingFrenzyPower`
* `FeelNoPainPower`
* `FeralPower`
* `FlameBarrierPower`
* `FlankingPower`
* `FlexPotionPower`
* `FlutterPower`
* `FocusedStrikePower`
* `FocusPower`
* `ForbiddenGrimoirePower`
* `ForegoneConclusionPower`
* `FreeAttackPower`
* `FreePowerPower`
* `FreeSkillPower`
* `FriendshipPower`
* `FurnacePower`
* `GalvanicPower`
* `GenesisPower`
* `GigantificationPower`
* `GravityPower`
* `GuardedPower`
* `HailstormPower`
* `HammerTimePower`
* `HangPower`
* `HardenedShellPower`
* `HardToKillPower`
* `HatchPower`
* `HauntPower`
* `HeistPower`
* `HelicalDartPower`
* `HelloWorldPower`
* `HellraiserPower`
* `HexPower`
* `HibernatePower`
* `HighVoltagePower`
* `HotfixPower`
* `HyperbeamFocusDownPower`
* `IllusionPower`
* `ImbalancedPower`
* `ImitationLearningPower`
* `ImprovementPower`
* `InfernoPower`
* `InfestedPower`
* `InfiniteBladesPower`
* `InterceptPower`
* `IterationPower`
* `JuggernautPower`
* `JugglingPower`
* `KnockdownPower`
* `LeadershipPower`
* `LethalityPower`
* `LightningRodPower`
* `LoopPower`
* `MachineLearningPower`
* `MagicBombPower`
* `ManglePower`
* `MasterPlannerPower`
* `MayhemPower`
* `MindRotPower`
* `MinionPower`
* `MonarchsGazePower`
* `MonarchsGazeStrengthDownPower`
* `MonologuePower`
* `NecroMasteryPower`
* `NemesisPower`
* `NeurosurgePower`
* `NightmarePower`
* `NoBlockPower`
* `NoEnergyGainPower`
* `NostalgiaPower`
* `NoxiousFumesPower`
* `OblivionPower`
* `OneForAllPower`
* `OneTwoPunchPower`
* `OrbitPower`
* `PagestormPower`
* `PainfulStabsPower`
* `PaleBlueDotPower`
* `PanachePower`
* `PaperCutsPower`
* `ParryPower`
* `PersonalHivePower`
* `PhantomBladesPower`
* `PiercingWailPower`
* `PillarOfCreationPower`
* `PlatingPower`
* `PlowPower`
* `PossessSpeedPower`
* `PossessStrengthPower`
* `PrepTimePower`
* `PyrePower`
* `RadiancePower`
* `RagePower`
* `RampartPower`
* `ReaperFormPower`
* `ReattachPower`
* `ReboundPower`
* `ReflectPower`
* `RegenPower`
* `ReptileTrinketPower`
* `RetainHandPower`
* `RingingPower`
* `RitualPower`
* `RollingBoulderPower`
* `RoyaltiesPower`
* `RupturePower`
* `SandpitPower`
* `SeekingEdgePower`
* `SelfFormingClayPower`
* `SentryModePower`
* `SerpentFormPower`
* `SetupStrikePower`
* `ShacklingPotionPower`
* `ShadowmeldPower`
* `ShadowStepPower`
* `ShriekPower`
* `ShrinkPower`
* `ShroudPower`
* `SicEmPower`
* `SignalBoostPower`
* `SkittishPower`
* `SleightOfFleshPower`
* `SlipperyPower`
* `SlothPower`
* `SlowPower`
* `SlumberPower`
* `SmoggyPower`
* `SmokestackPower`
* `SneakyPower`
* `SoarPower`
* `SoulboundPower`
* `SpectrumShiftPower`
* `SpeedPotionPower`
* `SpeedsterPower`
* `SpinnerPower`
* `SpiritOfAshPower`
* `StampedePower`
* `StarNextTurnPower`
* `StockPower`
* `StormPower`
* `StranglePower`
* `StratagemPower`
* `SubroutinePower`
* `SuckPower`
* `SummonNextTurnPower`
* `SurprisePower`
* `SurroundedPower`
* `SwipePower`
* `SwordSagePower`
* `SynchronizePower`
* `TagTeamPower`
* `TaintedPower`
* `TangledPower`
* `TankPower`
* `TenderPower`
* `TerritorialPower`
* `TheBombPower`
* `TheGambitPower`
* `TheHuntPower`
* `TheSealedThronePower`
* `ThieveryPower`
* `ThornsPower`
* `ThunderPower`
* `ToolsOfTheTradePower`
* `ToricToughnessPower`
* `TrackingPower`
* `TrashToTreasurePower`
* `TyrannyPower`
* `UnderworldPower`
* `UnmovablePower`
* `VeilpiercerPower`
* `ViciousPower`
* `VigorPower`
* `VitalSparkPower`
* `VoidFormPower`
* `WasteAwayPower`
* `WeakPower`
* `WellLaidPlansPower`
* `WitheringPresencePower`
* `WraithFormPower`

---

## 4. 卡牌体系 (CardModel) 选牌黑名单

在 [`CardChoiceSupport.cs`](file:///d:/Desktop/sts2mod/CombatSolver/CardChoiceSupport.cs#L18-L26) 中，以下 **18 张卡牌** 经 `sts2.dll` 反射确认真实存在于 STS2，当前被硬编码列入黑名单直接跳过出牌：

1. `Seance`
2. `SculptingStrike`
3. `Purity`
4. `HeirloomHammer`
5. `Snap`
6. `Guards`
7. `Nightmare`
8. `Transfigure`
9. `HiddenDaggers`
10. `HandTrick`
11. `Begone`
12. `DualWield`
13. `Charge`
14. `Cleanse`
15. `DecisionsDecisions`
16. `Brand`
17. `Tutor`
18. `BurningPact`

---

## 5. 药水体系 (PotionModel) 全量清单 (共 66 款，目前覆盖率 0%)

* `Ambergris`
* `Ashwater`
* `AttackPotion`
* `BeetleJuice`
* `BlessingOfTheForge`
* `BlockPotion`
* `BloodPotion`
* `BoneBrew`
* `BottledPotential`
* `Clarity`
* `ColorlessPotion`
* `CosmicConcoction`
* `CunningPotion`
* `CureAll`
* `DexterityPotion`
* `DistilledChaos`
* `DropletOfPrecognition`
* `Duplicator`
* `EnergyPotion`
* `EntropicBrew`
* `EssenceOfDarkness`
* `ExplosiveAmpoule`
* `FairyInABottle`
* `FirePotion`
* `FlexPotion`
* `FocusPotion`
* `Fortifier`
* `FoulPotion`
* `FruitJuice`
* `FyshOil`
* `GamblersBrew`
* `GhostInAJar`
* `GigantificationPotion`
* `GlowwaterPotion`
* `HeartOfIron`
* `KingsCourage`
* `LiquidBronze`
* `LiquidMemories`
* `LuckyTonic`
* `MazalethsGift`
* `OrobicAcid`
* `PoisonPotion`
* `PotionOfBinding`
* `PotionOfCapacity`
* `PotionOfDoom`
* `PotionShapedRock`
* `PotOfGhouls`
* `PowderedDemise`
* `PowerPotion`
* `RadiantTincture`
* `RegenPotion`
* `ShacklingPotion`
* `ShipInABottle`
* `SkillPotion`
* `SneckoOil`
* `SoldiersStew`
* `SpeedPotion`
* `StableSerum`
* `StarPotion`
* `StrengthPotion`
* `SwiftPotion`
* `TouchOfInsanity`
* `VulnerablePotion`
* `WeakPotion`
