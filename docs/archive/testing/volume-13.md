# 0.48.0 硬错误批次验证

基线源码13f37989，正式版本0.48.1。以下为本批实际取得的最小合同证据；原包整场、可见Steam与性能未验收。每项总超时120秒，串行运行，实例已由启动器删除。

| 场景 | 最终runId | 请求秒数 | 主要断言 |
| --- | --- | ---: | --- |
| `NATIVE-CHOOSE-OPEN-GATE` | `99fcd144f0a54855b91b17d2c523bc91` | 23.50 | NativeChoose:ReopenedPage:NativeOpenGate:AcceptedIdentity；NativeChoose:CandidateOrderMismatch:PageRetained；NativeChoose:Skip:NativeAccepted |
| `CALCULATED-GAMBLE-ORDERED-DISCARD` | `fde97e1259f14f3785da09d1d3b1c8b6` | 32.01 | CalculatedGamble:OrderedDiscard:NineCardHand:FourteenCardDraw:NestedSly:Relics:TwoExhaustedInstances:Native:Fork |
| `FROZEN-LIGHTNING-CHANNELS` | `2b84ba4f29e040beb90d67c8c4135e5f` | 25.66 | FrozenLightning:RootBeforeNativeAdvance:ThreeBranchChannels:Voltaic:NativeFullState:Fork |
| `REPORT-ROUND-IMBALANCED` | `9a57db29c3074e77a3eed74a61d24a9a` | 32.68 | REPORT-ROUND-IMBALANCED |
| `SECOND-WIND-REPORT-ROOT` | `72d8b0a8afca4b34bfffd3b62934f2bf` | 32.67 | SecondWind:NestedDraw:ThreeEnemies:Reattach:Native:Fork |
| `GROUP-DEBUFF-REACTIVE-DRAW` | `749d07146e794926b05aab629d136865` | 61.16 | GroupDebuff:FourSources:Vicious:Hellraiser:PerTargetEffects:Native:Fork |
| `CONSTRUCT-REAPER-ARTIFACT` | `d57b75f8d6d643b48f6316af432c0481` | 39.98 | ReaperForm:Osty:Artifact:PenNib:MultiTargetDoom:TimesUp:RouteRisk13:NativeTurn:Fork |
| `FIXED-PREFIX-POTION-POLICY` | `5e650223cc7546d896f206c10bd98f8a` | 22.28 | FixedPotionPrefix:Disabled:Quota:EarliestTurn:Target:Protect:RequiredPair |
| `FIXED-PREFIX-TURN-OUTCOMES` | `75437f09fab94d62b82336e2cc2af221` | 44.71 | FixedPrefixOutcomes:NodeOwnedBeforeProjection:RawFallbackSoldDelta:ExistingOutcomePreserved；FixedPrefixContinuations:IndependentPrefixOracle:FullStateText:Turn:ForecastOffset:Order；FixedPrefixOutcomes:ThreeTurns:ZeroAndPositiveLoss:PartialTail:Terminal:Empty:InvalidSuffix:RootUnchanged；FixedPrefixOutcomes:SevenTables:CacheRoundtrip:InvalidCachePreserved:ImportAndContinuationRejected:MissingLossNotZero；FixedPrefixOutcomes:NativeThreeTurnContinuation:LiveEndTurnRiskMatches:ReuseAndDisplayTotals；FixedPrefixContinuations:N4:IndependentPrefixOracle:FullStateText:Turn:ForecastOffset:RootUnchanged；FixedPrefixContinuations:N8:IndependentPrefixOracle:FullStateText:Turn:ForecastOffset:RootUnchanged；FixedPrefixContinuations:N17:IndependentPrefixOracle:FullStateText:Turn:ForecastOffset:RootUnchanged |
| `MIRRORED-HOOK-FILTER` | `dd238590831d4fc5b0a862ea2589ea07` | 47.64 | MirroredHookFilter:Methods=62:Models=1671:OrderDuplicatesExternalForkInvalidationPatchRefreshSharedLayoutsSegmentsNoAnchorEffectivePrefixReuse |
| `FROZEN-ROOT-LISTENERS` | `92375939aa64442b8b489ec81f5f9624` | 45.21 | FrozenRootRunListeners:ParentChildGrandchild:CardMutation:PowerRemap |
| `CALCULATED-HISTORY-FREEZE` | `0e251ee661f24c399ed18697c9e66ee3` | 23.41 | CalculatedHistory:SixReaders:NativeAdvance:Parent:Sibling:Fork:BranchIncrement |
| `VOID-FORM-TURN-CHOICES` | `52eb373400cf4154959105a39ebac915` | 31.60 | VoidForm:ForcedTurn:Tyranny:Stratagem:FullReplay:ExecutionContinuation:Native |
| `FLATTEN-MUSIC-BOX-ENTRY` | `85219286175447c8bb0a28b4e371e66e` | 23.17 | FlattenMusicBox:NativeCloneEntry:OrderedCostLayers:Fork |
| `SEEKER-ORDERED-OPTIONS` | `2fa365c9cfb346cb946b356030be11d7` | 23.49 | SeekerStrike:RandomSample:FilteredPileOrder:DuplicateInstances:Native:Fork |
| `RITUAL-TEMPORARY-STRENGTH` | `e6dbee4c63964b12b2de0493dde79f73` | 27.02 | Ritual:TemporaryStrength:BothListenerOrders:Native:Fork |
| `NOXIOUS-RAMPART-ORDER` | `d3bf363e7ede4c329633476dcdbb2f35` | 25.52 | NoxiousFumes:SleightOfFlesh:PerTargetDamage:BeforeRampart:Native:Fork |
| `EVIL-EYE-EXHAUST-HISTORY` | `e82fd9610dbc46b8883ee8aa3609e322` | 25.72 | EvilEye:ExhaustedHistory:SingleAndDoubleBlock:Native |
| `BLOCK-SPEC-CARD-PLAY-IDENTITY` | `b87b54a38fb04bc6bc6267e56a44e371` | 25.84 | BlockSpecIdentity:DeathsDoor:Glitterstream:Unmovable:Native:Fork |
| `ROUTE-ADOPTION-LIFETIME` | `602a34d3a6954e7987515ad8e003a213` | 23.14 | RouteAdoption:PublishedPreview:ExpiredPass:ActiveRequest:ExactActions |
| `PAELS-LEGION-FINISHED-REFERENCE` | `efa60d3c846f49ffa55c8d513bb3e2fa` | 23.64 | PaelsLegion:FinishedNativeReference:FrozenRoot:LaterBlock:Cooldown:Native:Fork |
| `END-TURN-RISK-LOSS-ACCOUNTING` | `561bb22639aa446d91d3d531711e573e` | 27.46 | EndTurnRisk:DamageLossAxis:Revival:FinalHp:Native |
| `MAKE-IT-SO-FULL-HAND` | `0c83cce4e34244ca9af503848b4709db` | 25.37 | MakeItSo:MusicBox:FullHand:OrderedReturns:Native:Fork |
| `CARD-COST-IDENTITY-CONTRACT` | `65bcb61bc6d34f5f8d04dd21b64e6aa0` | 45.39 | CardCostIdentity:comparisons=11:nativeTurnAndPlayCleanup:stars:fork:choice:continuation |
| `DAMPEN-REACTIVE-ROCKET-PUNCH` | `2180067a1f424092b3617d4da6c59a77` | 前段记录 | 原生完整状态及Fork；实例已清理 |
| `DAMPEN-REACTIVE-MELANCHOLY` | `1eb75a3bf3144624a8c744445ed4e4f8` | 前段记录 | 原生完整状态及Fork；实例已清理 |
| `RADIANT-PEARL-ENTRY` | `afd4056a2fff4fb4aef3a17d76496efd` | 前段记录 | 原生完整状态及Fork；实例已清理 |

## 关键失败基线

三选一9ddd9db4268546d8a9901df6b6e2c6a1在重新开页后提前返回，原生任务仍未接受选择；最终99fcd144f0a54855b91b17d2c523bc91通过开页门、候选失配保留页面与跳过合同。

群体减益a6c4558ef91441ab8a3b9734006f5d58预测Thunderclap后敌HP12、原生42；最终72d8b0a8afca4b34bfffd3b62934f2bf为42/42，四来源邻接合同另通过。

苦难830f843415c940d4a7943cb53399cbf9攻击后错误传播本次新增Doom36；最终d57b75f8d6d643b48f6316af432c0481的路线、风险复核与原生跨回合均损13HP。

仪式89ac521a2a794c35b80b15278fb18d6d、毒雾f1713e9840ec4e84bbcc6ca273889a59、额外格挡c40a9d0550b64e04bf0fc7b43a53044c分别复现有序Power、敌HP及同次CardPlay差异，最终证据见上表。

固定药水前缀ea980c051f3a44929ebaf9c25ab25aee接受禁药前缀；佩尔军团d6c44ccfbe9e4296bcf14558b3472dc0拒绝已完成引用的Fork；复活风险37bdabd521cf4fd7ba7579ef15cb9162比较HP净变化与受伤量；满手返牌778939bdc0aa4bb3bf7fb375f91306f6保留错误同名实例。各机制最终合同已通过。

精巧谋划9手/14抽完整邻接合同通过，不能据此关闭原报告消耗牌首差；未验证项与保留ID见[问题记录](../../issues/0.48.0-hardbugs-20261003.md)。
