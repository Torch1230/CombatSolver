# 虚空之唤生产编译与回合开始执行（2026-09-11）

`CallOfTheVoid` 从只在测试值层存在的生成命令推进到生产编译／回合执行接线：编译器把原生 `OnPlay` 编译为一条有序 Power 指令，回合驱动在原生 `BeforeHandDraw` 时点用分支 RNG 和冻结完整池执行整批选择，Projection 只在根中实际存在该卡或能力时捕获完整角色池并逐候选精确编译。生产准入仍然失败关闭：本批没有缩小生成池，也没有让当前不完整的亡灵根通过。

## 所有权与实现分工

- 编译器（Prediction/Compact）：`CallOfTheVoid` 进入精确类型集合，只接受原生的 `Innate` 升级关键字，其它实例状态（附魔、异常关键字、费用形式等）继续显式拒绝；编译结果是一条 `ApplyBasicPower(CallOfTheVoid)`，数量取 `DynamicVars.Cards.BaseValue`。抽弃牌专用域（无 Power 布局）明确拒绝该牌，不回退成惰性空效果。升级前后的指令完全相同，编译不把 `Innate` 当成临时状态，也不丢弃升级状态。
- Power 生命周期（Engine）：`BasicPowerKind.CallOfTheVoid` 复用既有基础 Power 值槽，数量、施加者、获得顺序、根槽退休、回合初始量与持续跳过标记都在同一撤销状态；它不是持续时间能力、不是减益、不参与出牌前监听顺序，因此不误归到其它 Power。卡牌施加经过既有 `PreparePower`／`CommitPower` 入口，人工制品不消耗（原生为自施增益）。
- 回合时点（Engine）：`CompactRoundRoot.BeforeHandDrawPool` 保存捕获的池索引；`ResumableDiscardProgram.Rounds` 在能量重置与 BoundPhylactery 的 `AfterEnergyResetLate` 宠物生成之后、合成起手抽牌帧建立之前执行生成，批次事件因此排在起手抽牌之前。数量为 0 时整条效果与随机流都不推进；构造期要求池、池索引和玩家 `CallOfTheVoid` 槽同时存在，缺一即 `NotSupportedException`。
- 生成事件协议：`Generated` 事件的创建者区分怪物、卡牌动作和玩家回合开始能力（`MonsterCreator`／`CardActionCreator`／`TurnStartPowerCreator`）。回合开始批次使用 `CardGenerationResultKind.Random`（与旧 `TurnStartPowerSupport` 分支一致），卡牌模板生成保持 `Fixed`。整批先选择再逐张入堆、满手溢出到弃牌堆、逐实例身份递增、批次间允许重复、不触发 `AfterCardDrawn`，与原生 `CardCmd.ApplyKeyword`＋`CardPileCmd.AddGeneratedCardsToCombat` 的结果一致。
- 生成 RNG 所有权：五字段 `CombatCardGeneration` 槽由 `CardGenerationPools` 分配在 `ReversibleValueState`，冻结／撤销／工作区天然覆盖。物化、直接读视图、状态键与续用文本都读该 lane 值（`CompletedStateReadView.CardGenerationRng`），未持有显式值的程序保持旧行为。

## 冻结池接线与失败关闭

- `CompactDiscardProjection` 只在根中出现 `CallOfTheVoid`（战斗牌或 `CallOfTheVoidPower`）时捕获池；池来自 `TryGetRootEligibleCharacterCardsForCombat` 的冻结规范顺序，保留全部候选，不按当前已支持类型筛选。跟随原生，池是**持有人角色**的池。
- 每个候选建立不可变模板：克隆规范卡、附加原生选择后才应用的 `Ethereal`（沿用 BladeOfInk 最终模板先例），并把定义索引写入池映射。模板和根定义一起交给编译器精确编译；任何候选的编译、状态或后续闭包不支持时整根抛出明确 `NotSupportedException`，不落到模型后端、不静默跳过、不只按卡名记录。
- 没有准入回合闭包（单回合投影）时，含有该卡／能力的根直接拒绝，避免悄悄丢掉回合开始效果；完整角色池本身未闭包时，拒绝发生在第一个不可表示候选，而不是缩池后放行。
- 生成实例仍按定义索引解析模板，兼容物化只导入位置，不重新抽取 RNG；`Unplaced`／终局门禁沿用既有生成路径。

## 验证

最终产物 `.local/compact-void-production-20260911/artifact`（与仓库 Release 构建同一源码），四个 headless 场景全部 Passed：

| 场景 | runId | 耗时 | 证据 |
| --- | --- | ---: | --- |
| `COMPACT-GENERATION-CLOSURE-AUDIT` | `ae2f88366cdc46489d381beeea855581` | 24.03 s（含启动） | `.local/compact-void-production-20260911/audit-v2` |
| `COMPACT-CALL-OF-THE-VOID-GENERATION` | `46c0ff46f6b74e66ba551d5deb2e63ee` | 3.89 s（复用进程） | `.local/compact-void-production-20260911/cotv-v2` |
| `COMPACT-CALL-OF-THE-VOID-ADMISSION`（新） | `094fe477c6494317848cda39bfe0ca98` | 23.95 s（含启动） | `.local/compact-void-production-20260911/admission-v2` |
| `COMPACT-SEARCH-LIFECYCLE`（回归哨兵） | `d1f51f1cd8614f41b2ed7c0ad9fd517f` | 5.11 s（复用进程） | `.local/compact-void-production-20260911/lifecycle-v1` |

- `COMPACT-GENERATION-CLOSURE-AUDIT` 沿用原始 38 牌、19 件遗物注入、实际 20 件遗物与两瓶药，两个完整池仍为 78／50 个候选，`FullRootExplicitlyRejected` 继续成立。
- `COMPACT-CALL-OF-THE-VOID-GENERATION` 在 78 个冻结候选上新增编译器与闭包普查：指令为单条 `ApplyBasicPower(CallOfTheVoid)`、升级与未升级指令相同、未表示状态与抽弃牌专用域被拒绝，78 个候选中 18 个可精确编译、60 个不可（首个 `BANSHEES_CRY`）。原生／旧链／紧凑三方每批有序 ID、五字段 RNG、计数增量 308／616／924、第三批两份满手溢出、12 张虚无、逐实例身份、撤销与确定性重放全部一致。
- `COMPACT-CALL-OF-THE-VOID-ADMISSION` 用同一套已准入 30 牌原始机甲根先证明对照根通过完整准入，再在实机施加 4 层 `CallOfTheVoidPower`：同一根在池候选 `ABRASIVE` 处被拒绝（78 个候选 16 个可编译、62 个不可），拒绝既没有归因到根内卡牌，也没有缩小池；同一根的单回合投影在回合闭包要求处被拒绝；实机状态未变。这直接证明生成池被完整捕获并逐候选编译，而不是过滤到已支持类型后放行。
- `COMPACT-SEARCH-LIFECYCLE` 作为已准入门槛哨兵：旧／紧凑 DOP1、紧凑 DOP2 全部政策结果一致，并发 2、取消／失败排空、根复用、未迁移药水明确拒绝与实机不变全部通过，说明新增 Power 槽、读取合同和状态键字段没有改变现有已准入域的搜索结果。

纯值合同 `tools/CompactCreatureChecks`（23 项全通过）新增 `COMPACT_GENERATION_POOL_BEFORE_HAND_DRAW_OK`：施加并叠加的真实计数、施加者与获得顺序、生成事件排在起手抽牌之前、五字段 RNG 消耗 `count×(pool−1)`、撤销与八工作区恢复、零层不推进随机流、满手溢出按选择顺序落到弃牌堆，以及缺池／缺玩家槽／越界池索引三种构造拒绝。Release 构建 0 警告 0 错误，`./tools/verify-refactor-boundaries.sh` 通过（96 个 Search 文件）。

夹具输入错误保留为失败基线：首次 `COMPACT-CALL-OF-THE-VOID-ADMISSION` 未带既有 30 张 RunCards，对照根在空战斗牌集合处被 `Compact prototype capacity exceeded` 拒绝；同一输入的 `COMPACT-SEARCH-LIFECYCLE` 探针也因牌组不足 30 张失败。改用 `coverage/unattended/compact-void-admission-silent-run-cards.json` 后两项通过，失败 run 不计入最终证据。

## 仍未完成

- 完整池闭包：亡灵池 60 个、潜行者池 62 个候选仍不可精确编译（首个分别为 `BANSHEES_CRY`／`ABRASIVE`），另有未迁移的遗物、药水与 AEONGLASS AI；因此**生产紧凑准入仍然对含 CallOfTheVoid 的根失败关闭**，池模板构建的正向路径（池全闭包后真正执行一次生产生成）本批没有可执行的样例，只由纯值合同和拒绝路径覆盖。
- 本批未运行完整部署、正常 NoGC 性能基准、Windows 门禁或任何加速结论；`COMPACT-CALL-OF-THE-VOID-ADMISSION` 只停在准入构造与拒绝，不启动搜索。
