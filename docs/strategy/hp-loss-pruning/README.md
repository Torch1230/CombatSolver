# Shared HP-loss incumbent pruning

This single-player change targets repeated work after an eligible complete victory is known: unavoidable early damage followed by safe turns, and fast victories rediscovered by portfolio members. Baseline: upstream 5d28a1cfa (0.49.3).

## Scope and safety

PrimaryIncumbentTable stores completed, hard-policy-compliant victories by outstanding stolen resources, explicit potion uses, per-source growth count vector, and satisfied relic target mask. Different growth sources and relic combinations remain separate; scalar HP witnesses cannot replace resource-target witnesses.

Coordinator members share pure-value bounds through frozen policy, retaining independent simulators, frontiers and transpositions. Live combat sessions carry one executable potion-free witness only when the full root continuation stamp, damage ledger and policy match. Changed input invalidates it. Victory publication occurs at the serial commit boundary.

Potion buckets are consumed only when explicit use is closed by member policy or the maximum-use limit. HP lower bounds retain future healing, death protection, post-combat healing and boss HP relief. Unknown sources keep the upstream conservative allowance. Stolen healing cards remain possible recovery sources.

PreserveResources looks up a lower bound on final unrecovered resources, subtracting resources recoverable from living enemies. It does not use the current missing-card count directly. Missing compatible witnesses preserve expansion.

Growth caps require a separate narrow original-content closure: exhausting growth sources, audited basic cards/statuses/curses, selected relics and powers. Generation, exhaust recovery, unknown callbacks and live growth-copying opportunities reject certification. Opportunity metadata alone is not a cap.

For pure growth targets, each possible final source-count vector consumes its own witnessed victory. Shared expansion stops only when every possible vector is bounded. Missing witnesses or more than 256 combinations preserve expansion. Growth/relic mixtures retain the optimistic final-bucket path, without claiming complete independent enumeration of relic outcomes.

DualWield remains uncertified while a growth object can be copied. Once no usable growth object remains, copying ordinary cards cannot reopen growth, and branch certification can resume. Fork isolation is tested.

## Equality tradeoff

Eligible unfinished, risk-free branches may stop when their optimistic strategic HP deficit equals a compatible completed victory. This prioritizes reduced search work over finding an earlier victory with identical resource outcome and HP loss. It is not a proof that the original complete ordering or every finite-Beam result is preserved. New equality pruning leaves completed candidates intact.

No action commutativity prediction, pile-order masking or multiplayer pruning is included.

## Reproduction

Build CombatSolver.csproj and tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj in Release. Installed game/RitsuLib paths come from local.props; personal paths do not belong in committed requests.

Run the harness with --check-primary-incumbents for shared table contracts, or --check-early-turn-continuation-bound for existing continuation contracts.

Set OFFLINE_HARNESS_RESOURCE_BUCKET_CHECKS=1 and run --request coverage/fixtures/scenarios/state/royalties-resource-0170.json --milestone M1 for resource contracts. Set OFFLINE_HARNESS_THEFT_BUCKET_CHECKS=1 on an Ironclad combat for theft contracts. These are shadow-state assertions, not native actual/simulated acceptance.

OFFLINE_HARNESS_RESOURCE_SETTINGS reads a test-only JSON containing GrowthBudgets, RelicStrategyEnabled and RelicCounterRules, for example {"growthBudgets":{"royalties":5}}. Other settings stay CLI-controlled; player settings are not modified.

--disable-shared-incumbents retains new member-local equality behavior, so it is not the entire upstream baseline. --verify-shared-incumbent-reuse checks same-root reuse and policy invalidation on small Coordinator requests. --verify-incremental performs complete prefix replay and is excluded from performance samples.

## Evidence and limits

Rebased validation is recorded in [the test matrix](../../TEST_MATRIX.md). Historical 0.49.1 results included expanded nodes 28,956 to 17,802 on the restored Infested Prisms root and 5,669 to 3,964 on a growth-copy fixture. These are prior-version evidence, not new 0.49.3 measurements.

Offline comparisons do not prove native automatic deployment, visible Steam frame time, all growth sources, all positive potion tiers or global optimality. The upstream comparison harness receives the same test-only resource-settings loader; upstream production source is unchanged.

## 同根成长路线续用

完整零药胜利以完整结果参与下一次同根请求的选优，成长与遗物收益沿原政策比较；纯 HP 剪枝资格单独判断。固定成长根的敌方生命为 18，需要跨回合获胜，用于覆盖携带成长见证后的再次搜索。

```powershell
$env:OFFLINE_HARNESS_RESOURCE_SETTINGS = 'coverage/fixtures/search/shared-growth-incumbent-settings.json'
dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --request coverage/fixtures/search/shared-growth-incumbent-reuse.json --label growth-reuse --out .local/growth-reuse --beam 45 --nodes 20000 --budget-ms 20000 --dop 1 --potion-policy Disabled --search-mode Coordinator --use-portfolio --verify-shared-incumbent-reuse
```

`02cf2283` 的该根第一次为第 2 回合零损胜利，第二次为未完成路线。修复后同根续用和政策变化失效通过。原生 `PRIMARY-INCUMBENT-REUSE` 连续三次请求、完整质量、严格增量回放和 live 隔离通过，直接证据见[测试矩阵](../../archive/testing/volume-16.md#同根成长胜利续用2026-10-05)。续用模式累计多次请求的执行时间，只用于正确性检查。

## 单人共享损血剪枝（2026-10-05）

基线 `5d28a1cfa`（0.49.3），游戏 0.111.0 / RitsuLib 0.6.5。候选主项目及离线宿主 Release 构建均 0 警告、0 错误。初次构建缺 net48 引用程序集，使用本机已有 NuGet 引用包的 FrameworkPathOverride 后构建成功；未修改上游构建配置。

本轮共享表20项、既有早回合界143项、成长资源35项、偷窃边界10项全部通过。成长小根的增量完整前缀回放及同根共享见证续用、政策变化失效检查通过。这些不是原生 actual/simulated 整场验收。

独立 .NET 进程、Coordinator及组合开启、Disabled、DOP1、牌堆掩码0、No-GC关闭；两侧均 Boundary=None。上游仅在测试宿主移入相同 resource-settings 读取方法，生产源码保持基线；没有用关闭共享表代替整个上游基线。

| 固定根 | 相同终局 | 展开：上游 → 候选 | 转移：上游 → 候选 | 单次搜索秒：上游 → 候选 |
| --- | --- | --- | --- | --- |
| Royalties 成长 | 胜利、收益1次/额度5、战损0、零药、第1回合 | 7572 → 11 | 20336 → 43 | 5.66 → 0.60 |
| NotYet 回血哨兵 | 胜利、先回血再击杀、战损0、零药、第1回合 | 243 → 243 | 527 → 527 | 0.62 → 0.72 |

成长根：REGENT / FUZZY_WURM_CRAWLER_WEAK / GROWTHBUCKET20261004，飞升0、敌HP6、玩家75/75、能量3、原生遗物。清空牌组与牌堆，手牌永久Royalties、2张StrikeRegent、4张DefendRegent；抽牌堆5张StrikeRegent。测试设置 `{"growthBudgets":{"royalties":5}}`。Beam45、20000节点、20000ms。回血根使用 `coverage/fixtures/scenarios/state/not-yet-heal-resource-0170.json`，Beam20、12000节点、20000ms。完整命令与边界入口见[复跑说明](#reproduction)。

耗时是单次离线观察，回血哨兵本次多0.10秒，不能称为所有场景提速。未执行原问题包恢复、完整原生自动部署、可见Steam性能、全部成长来源及正数药水档整场验证。本机产物保存在忽略目录 `.local/pr-validation/`。

## 本分支与PR #207的整合边界

当前分支复用此共享表。组件证书根的等HP（含更晚回合）比较还须核对实际已用药水成本；缺失见证成本保留分支。无遗物目标时还可计入剩余必须显式用药次数乘以根冻结的最低药水成本；只有组件生成闭包排除新增药水来源才消费此界，未知分支保留。组件证书根的新共享消费严格分支回复证明，未知分支不借启发式重新认证；旧遭遇认证和其他已知原版根继续上游既有政策，不能写成新增严格证明。零HP额度遗物目标保留原有本地严格HP界，同HP目标路线继续搜索；正额度、成长和追回仍有原边界。原生证据及当前上游对照见[0.49.4整合记录](../../performance/pr207-upstream-0494-integration-20261005.md)。

## 首领卖血预算遍历

原版首领房间默认启用，包含第三幕双首领第一场。主搜索及原后处理完成后追加原配置节点和软时间的各1/5；达成既有完整HP目标时可以跳过。额外工作进入请求总账本，取消、接管及内存无进展沿原边界结束。时间是软限额，单次展开和最终回放可能超过截止点。

侦察以追加额度的至多1/3、3次排序偏差枚举首回合边界。当前回合动作和有正净潜力的强能力承诺先行，再按HP归一的中途分排序。动作Top-K和Beam席位由待处理栈替代，选牌枚举受原预算限制；独立转置键包含完整状态和剩余偏差，六维精确支配标签沿原实现。回调只冻结动作、状态键、消灭敌人数、用药量和排名，原模拟器随侦察释放。用药档0/1/2/3及以上各保留至多6个代表，优先已经兑现全部强制用药指令的前缀，再按消灭敌人数、用药量及排名选至多两个前缀，固定回放后由原Beam完成后续；两阶段共用追加余量。有限额度下仍可能漏解，不作全局最优或收敛保证。

生存按真实HP、回血和复活判断。累计损血可能因治疗超过开战HP，固定累计上限会剪掉这些可生存路线；预算界使用已有完整胜利和剩余治疗下界，并按用药、成长、遗物及追回资源分桶。等战损后续候选继续探索。新成员中途排序消除累计损血的重复计价和额外卖血罚分；致死投影的活节点继续参与出牌，实际死亡保留原判定。终局仍使用既有实际政策轴，跨成员的候选池相关Score尾键归零后比较。

2026-10-07 Windows验证使用游戏0.111.0、版本0.50.1，基线为同源码关闭成员。12项新合同、143项早回合界及20项共享资源桶合同通过。首次整场DFS实现（`599d84fc`）中，低生命注入试验预测/部署战损差35：建局80→45的注入损血已记入搜索统计，部署账本从45起算；后续原生夹具从80/80起始。人工DemonForm低生命根仍死亡；多个小根双方战损相同，这些离线探索不作原生正确性或收益证据。以下O056、直接遍历及精英哨兵是该首次实现的证据。

O056使用Q013已发布原包 `837d5e68074e4f0ab7df9efe56dfcfd9`，从combat_start恢复；同政策Smart、固定30秒/80000节点、DOP1、默认GC。关闭成员runId `25648dd4884c410bb69cf9a8256da6d0`，启用成员runId `2fef17121e0346e991c25db9f5b55384`，两侧SearchOnly及完整根隔离通过、实例清理。两侧均36战损、1药、T8。成员追加7982展开/35554转移、6006ms，3轮，因时间结束，未改善；这是配置6000ms软额度。当前政策与此前Q013历史零药结果不同，不能相互比较。

请求总展开16895→24877、转移73233→108787、搜索时间13962.0476→19755.8871ms、worker分配3462369256→5668148704B。两侧各一个独立进程样本；时间差约5.79秒是追加成本，不能据此给出普遍耗时或可见帧时间结论。

原生遍历使用维护的 [卖血手牌](../../../coverage/fixtures/search/boss-tempo-native-cards.json)，由 `GENERATED-NOVELTY-SEARCH` 执行严格增量回放和完整部署。复跑时创建自有EvidenceDirectory，其中 `research-options.json` 写 `{"scheduler":"tempo"}` 并创建 `native.flag`；IRONCLAD / AEONGLASS_BOSS / Act2，seed `BOSSTEMPOBLOOD20261007`，敌HP28、玩家80/80、能量3，ClearPlayerHand/Piles，Beam1、1000节点、5000ms、DOP1、Disabled、NoGC0、Instant/0秒、Fixed、VerifyIncremental、Timeout120、CleanupInstanceOnExit。`tempo-portfolio` 配合 `smart.flag` 验证协调器追加额度，`beam` 配合该标记执行关闭成员的同条件对照。

最终runId `3947418a8da94178816436921629ddd2` Passed：第4回合胜利，25损血（其中主动掉血6）、0药，严格增量完整回放、live/shadow根隔离、原生结果对账与0计划外重算通过，实例删除。界和计价纯合同证明对应谓词；该原生根只覆盖一条卖血路线，治疗/复活完整整场、额外成员实际改善及全部药水桶并未由它证明。

独立精英哨兵使用 [damaging-continuation-sentinel](../../../coverage/fixtures/search/damaging-continuation-sentinel.json)，仅mode改Deploy；Custom、Beam60、120000节点、30000ms、DOP2、Smart、默认GC、Instant/0秒、Fixed、Timeout120、CleanupInstanceOnExit。runId `2ef59dba98d14145a8746dcbde7b8d33` Passed，5损血/1药/T4、65/70HP、0计划外重算，首领成员旁路（诊断为空），实例删除；保持此前主线整合哨兵质量。本轮搜索7942.8618ms，仅单次正确性哨兵，不作性能改善结论。

报告 `5364035fe83c48799f1d9b419e406b9a`（#225）在日志站404，已发布世界线索引及issue正文未找到该报告或session `f63e2ded496a4a75981fe54a23956582`，该包未复测。全部角色/首领及可见Steam性能仍未验证。

### 后台首领包迭代（2026-10-07）

后台按Boss、BetterWorldline、药水折算改善降序固定前三份原版报告，Preflight及从combat_start的原生严格RestoreOnly均通过。保留报告的药水、成长、遗物和组合开关，显式覆盖为30秒/80000节点、DOP1、Fixed、默认GC；原报告使用更大预算，包内旧数字仅为参照。测试 `research-options.json` 用 `{"scheduler":"request","bossTempoSearch":false}` 或true，并创建 `smart.flag`，此模式保留录制的Novelty/Beam组合开关。`native.flag` 执行完整部署，Instant/0秒、120秒上限、CleanupInstanceOnExit。开始于开战根，输入通过CheckpointArchivePath、CheckpointSelector=start、ReplayMode=SearchOnly及明确ReplayPolicyOverridePath传入。

女王/静默猎手 `7cb7fff7e8c04ab5a25cc8dc48a51098`：关闭成员 `46f6916d817348f58be32fe493ffc7f5` 为63损血/1药/T7，独立原生基线 `d62346c8d4634d8981bde3c83f502d47` 完整部署Passed；首次整场DFS和中途排序单项调整仍为63/1，三轮均未产生合规结果。首回合侦察接原后续搜索后自主找到41损血/2药/T8，最终原生 `f011940c2e904905ac8d4aee15aedaea` Passed，追加12394展开/52697转移、6064ms，完整根隔离、原生实际损血及2瓶药、0重算通过。原生搜索根77HP，之前已发生3点损血；根后实际掉38，合计41。药水机会成本按9计算，63+9→41+18，改善13HP。

同包原生参照 `e0048b56dc8c4316bc4ab43cae3acafc`：从第二回合检查点恢复录制前缀，逐动作增量/完整回放53步及保存后缀部署通过，整场3损血/1力量药/T12，0额外搜索/重算。该证据证明路线可执行，不能称为当前搜索自主找到3损血。当前相对参照仍多38损血和1药，折算差47；按逐包40分钟窗口收口，保留缺口后继续独立根。

首个原生自主路线验收 `c2ee794f454c4f5abaea2f7ab8034445` Failed：实际根后38损血/2药、存活获胜、0重算，与预测的根后范围一致；测试将根后forensic账本错误对比整场41。修正两个原生账本都比较根后期望，另保存整场期望及根前损血，最终原生验收通过。该失败属于测试范围错误，保留原始失败身份。

女王/静默猎手独立根 `b7078a023b0547a89e853f3426d23490`：关闭成员 `1d2f7e19c6c24b829e4ed2cf9da68a89` 为52损血/0药/T10，启用 `094da7e7ad4c439bb9810872ab6b4db9` 为43损血/1药/T11。两侧SearchOnly及完整根隔离Passed，实例删除。追加13243展开/59378转移、6056ms。药水按9HP折算后均52，净持平，不能计作质量收益；该独立根未执行完整原生部署。

永世沙漏/故障机器人 `d317fdd742634f1f9e33bdcfffdac31d`：RestoreOnly通过；关闭 `36b453ea3700469eb74deb9891be5845` 与启用 `3db6ea9cef734949b13977e2b0681d6a` 均在120秒请求上限超时，没有result/research，未产生可比较结果。现有证据不足以归因搜索阶段；两个实例均由启动器删除，停止该包。

瀑布巨兽/铁甲战士 `0cb0b266bcd5407a8ed37aae490ef90c`：Preflight及严格RestoreOnly通过，开战根55/80HP；变大药水、灰水禁用，精炼混沌强制使用。关闭 `268313a6560d4497a348128123a37a04` 为死亡/55损血/1药；启用但尚未优先兑现Force的 `3e4be7360712413d8bbcb7816ce72034` 结果相同，追加16000展开/67511转移、4837ms。优先调度已满足全部强制用药指令的前缀后，`18bc679459f44b23b635b303d3946e40` SearchOnly及完整根隔离Passed：预测43损血/1药/T13胜利，追加16000展开/64249转移、4432ms，停止原因为node_limit。原生部署请求 `3eec97dffd384f61ac840a81f9b1cc67` 再次找到相同路线，但120秒内未完成部署，未产生最终原生账本，实例已删除；后续完整原生证据见下节。

| 同配置独立请求 | 总展开：关闭 → 启用 | 总转移：关闭 → 启用 | 搜索毫秒：关闭 → 启用 | worker分配字节：关闭 → 启用 |
| --- | ---: | ---: | ---: | ---: |
| 女王A，两侧完整原生部署 | 56807 → 70821 | 251886 → 311297 | 36365.0862 → 42380.5495 | 14102354928 → 17263937280 |
| 女王B，SearchOnly | 50696 → 64020 | 233374 → 293240 | 28120.1246 → 33795.4812 | 11449742344 → 14365665512 |
| 瀑布巨兽，SearchOnly | 76075 → 92075 | 320864 → 385113 | 26531.5491 → 30628.4461 | 12434826272 → 14820742304 |

各侧为一个独立进程样本，候选拥有配置20%的追加额度；未作总额度相同的普通Beam对照，不能据此证明通用性能收益或把改善全部归于排序因素。后台筛选与原始证据保存在忽略目录 `.local/tool-tasks/boss-tempo-iteration/`，报告ID及明确短搜政策用于复跑。

当前独立精英哨兵 `4c2069be5c07480bb0b4ed7937698cb9` 完整原生部署Passed：5损血/1药/T4、65/70HP、0重算，Boss成员旁路、诊断为空，实例删除；总17483展开/68030转移、7372.5186ms，保持既有质量。12项Boss合同、143项早回合界、20项共享表合同及两平台结构门禁通过；可见Steam性能、全部角色/首领及#225原包未验证。

Release构建0警告、0错误，文档、工具、覆盖材料及diff门禁通过。本地Executor含用户另项调查的未完成探针；本轮构建使用忽略目录中的临时targets，以分支HEAD的Executor副本替代该文件编译，用户文件原样保留并排除本次提交。原生测试和本地部署使用这一隔离构建，普通无覆盖构建未验证。

### 原生兑现与独立包复测

游戏0.111.0、RitsuLib 0.6.6。`GENERATED-NOVELTY-SEARCH` 的 `replay` 模式读取EvidenceDirectory中的 `frozen-plan.json`（既有 `research.json`），完整回放动作，核对胜利、风险、整场战损/药水/回合、动作数及零展开。`research-options.json` 写 `{"scheduler":"replay"}`，配合 `native.flag` 原生部署；原生进度写入 `native-progress.json`。冻结路线用于可执行性验证，自主发现证据仍来自原请求。

瀑布巨兽固定路线请求 `4c772d669e784397ba7b83e441f320dd` 120秒超时，进度显示第11回合、10次续用、0重算、未暂停。显式指定 `HeadlessFastModeForTest=Instant`、`DeploymentFastModeForTest=Instant`、动作间隔0后，`b134e602eb9d43cf8bcd335e955b41ea` Passed：43损血/1瓶精炼混沌/T13，完整根隔离、原生两个账本与预测一致、0重算；终局账本55→12HP，战后遗物回复后18/80HP。请求26.20秒含建局，搜索0展开/63转移、245.54ms，实例删除；未扩大120秒上限。

新增三个独立报告均从combat_start通过Preflight、严格ContinuationStamp及原生状态RestoreOnly；使用相同30秒/80000节点、Beam90、DOP1、Fixed、默认GC，保留各包用药/成长/遗物和组合开关。当前实现基线来自已部署的 `afbe0d2f` 构建，实验分别只改变一个因素；以下未完成路线的临时损血不作整场质量比较。

| 报告与当前基线 | 实验及结果 | 后续状态 |
| --- | --- | --- |
| 储君/永世沙漏 `c8e4870b7fed46b5b50a2664bdc1b698`；`6abea22565014a4daca848b98f90bf62` 未找到完整胜路，死亡62/0药 | 能力代表区分 `221859885fd5426084a067e9f14efc00`、后验Beam30 `8a5d26c291ac49a9b11305918379ecb1`、无胜利时最优先行 `efe587aa0ce74cf0bebdbee8e2f3d514` 均未找到完整胜路；追加各约6秒 | 保留长线缺口 |
| 静默猎手/实验体 `a8b0e42a8b18483abf5e29c857b36962`；`f34d4d264885406dab3e5ccb3748c39a` 未找到完整胜路 | 均分续搜额度 `6b0e4f65cc794cd7a8edeb4dd0671456`、重建前缀调度基线 `996dd2f8d62e4e449b9aba6f1ebfbd9b`、无胜利时最优先行 `fc0a7d166b494c9b93417bb7e148f390` 均未找到完整胜路；续搜主要停在T3～T4/TimeLimit | 保留兑现深度缺口 |
| 故障机器人/乐加维林族母 `d61a9b3277144076b16a01be3c776b00`；`b6dfcbab5e784b61a77c28a769e55390` 为5/0药/T8，SearchOnly | 两回合侦察 `cbfe368d84d2466a8902dd87c062d4e3`、普遍最优先行 `3f100580e87c44d29a2e1bcc7b51209e` 均变为12/0药/T5，多损7 | 保持现策略；完整原生及人工0损参照未验证 |

上述实验未进入当前搜索规则。输入、结果及启动器清理证据保存在 `.local/tool-tasks/boss-tempo-round2/` 和 `.local/checkpoint-batch/boss-tempo-round2-restore/`，原包入口为 `.local/issue-bundles/boss-tempo-round2/raw/`。当前独立样本不足以给出普遍首领收益、总额度相同的性能结论或全局最优证明。

交接时最终隔离Release构建0警告/0错误，PowerShell结构门禁及文档/工具/覆盖材料/diff检查通过。本轮Bash结构门禁因WSL缺少rg未完成；此前两平台结构通过记录仍作为历史证据。全部测试实例由启动器清理，原始问题包和研究输出由Git忽略。

### 后续改进方向

优先追踪代表前缀在哪个动作/回合停止，使用新增 `BOSS_TEMPO_CONTINUATION` 的 searched_turns、boundary和expanded诊断；第一条前缀常用完续搜余量，但均分额度尚无收益证据。同一药水档内的长期能力集合、首回合之后的兑现窗口和前缀调度基线仍需完整胜路验证。女王A的3损/1药可执行参照是已确认的主要缺口。补齐总额度相同的普通Beam对照、独立首领保留集、治疗/复活与资源桶整场验收，再决定默认启用和预算分配。
