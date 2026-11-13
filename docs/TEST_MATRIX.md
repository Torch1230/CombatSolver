# CombatSolver 测试入口

以下实例状态／RNG 修复及恐惧／极速专题纳入 0.51.1。行为证据来自版本标记为 0.51.0 的开发构建；版本号同步后复用已有行为验证。

## 0.113 实例状态与 RNG 问题包

来源为 `godot2026-11-13T14.39.12.log` 与 `combatsolver-reports-20261113-053952.zip` 的八个报告。静态定位包括 Glam 已删除字段、雕刻师常量读取、音乐盒复制牌关键词来源、狂喜空候选随机消耗及沙漏随机插牌。

最小入口：`V0113-INSTANCE-RNG-REPORTS`，角色 FOOL、遭遇 `DEVOTED_SCULPTOR_WEAK`，`MonsterMoveChecksPath=coverage/fixtures/scenarios/state/v0113-instance-and-rng-reports.json`，`StopAfterCombatRootSnapshotAssertion`，120 秒及实例清理。八项依次覆盖雕刻师仪式、Glam 重放状态、附魔复制牌连续打出、复制牌回合末消耗、谋划专家奇巧标记、狂喜空／非空候选和标准沙漏；沿既有差分核对有序牌堆、逐实例状态、Power、历史和完整 RNG。runId `d93fc01c328f4d64b26f7b5b25be033b` Passed，八项原生差分完成，实例已清理。Release 构建零警告／错误。首轮 `8d7d177df0834cd6b80dedcfc0cfe31c` 在预期 Power 的类名／模型 ID 混用处失败，修正夹具为 `RITUAL_POWER` 后通过。原始玩家环境和整包路线未回放。

## 0.113 Part 2：恐惧与极速

2026-11-13，v0.113.0 public-beta；本批使用独立任务分支，以下仅记录已执行结果。

- `MODE-ROOT-BLITZ`：runId `e9244fce25494a85851bbd7f799e75cd`，Blitz 5 根捕获合同 Passed。
- `MODE-ROOT-DREAD`：runId `c2daf3be2f5e45aaa4025708943a3e84`，Dread／仪式兽领域根捕获合同 Passed。
- `DREAD-DOMINION-POWERS`：runId `a7279864f95d4f73a1004fd2d7851738`，`coverage/fixtures/powers/dread-dominion-lifecycle.json` 的三个领域 Power、回合结束和下回合准备全状态差分 Passed。
- `DREAD-DOMINION-HOOKS`：`ece726eaeecb483bb0a5390b1cbfc714` 完成 Aeonglass、CeremonialBeast、Insatiable、KaiserCrab、Kin 后在知识恶魔测试选项不一致处失败；`0dc1c6870c0348cfa018323a561ada41` 完成其余七种领域、知识恶魔两个选择及领域移除。父子／兄弟 Fork、实机推进后的隔离、抽牌前及生物入场 Hook 均使用原生对照。
- `MODE-RULES-ISOLATION`：`96f6c22a0e3d4560881dff2834808be9` Passed，极速 0–5 级、并发根、嵌套同步作用域及实机规则保持。

- 极速变牌 `2dae09d17e1a4b0bae25915b9b586fbc` Passed，6 张手牌对照原生工厂及完整 RNG。生成缓存 `9f4675209cb940a38f5b331141e41daa`（16 组）和回合生成缓存 `96ab91f1e3334853b674ae35c6af9ea1`（27 组）Passed，含有序候选、分支状态、历史与隔离。
- 极速奖励：三幕普通房间、弱怪、精英、前幕与终幕首领各 8 个原生奖励样本通过，药水数量／身份一致且预测保持 live RNG。各 runId 见 `coverage/evidence/test-evidence.json` 的 `BLITZ-REWARD-ROOMS`。
- 女王恐惧开场及强化：`1bc10bb1222d47ec96ef9b7a6398409c` Passed，使用 `coverage/fixtures/monsters/dread-queen.json` 全状态差分。
- 恐惧怪物：沙漏随机插牌 `507ed19acbcc4d8b95cf8bf55a50b0df`、灵魂鱼额外手牌 `e0270a617c7142c3a91d2e3c28a8d5f7`、知识恶魔连续三次 8/9/10 伤害选择 `38c25eb4ee324c4eb4c2774046d9e2fb`、女王能力跨回合 `f6fd3993d43747edbc42a846620f550b` Passed。
- 增援／死亡：爆炸死亡 `027255c288f64c529653d6cc7f04b1c5`、蛋增援先领域后 Minion `86675f349be64513a596704e549c5caf`、气态炸弹先 Minion 后领域 `425bb8479a1044d69c16ed9ab49ce5e5` Passed。使用 `dread-explosive-death`、`dread-reinforcements`、`dread-minion-entry` 夹具对账阵容、Power、AI、牌堆、历史与 RNG。
- 怪物参数：`7f5d979813bd423bb2bf3d75e1a754ff` 的前十二项通过；其第十三项在人工创建瀑布巨人时缺原生房间初始化而失败。第十三项拆为原生首领房间夹具，`16b588b3a7d64437963b60a6469dda7f` Passed。极速地精佣兵三个动作 `29cb94499f8f4116824dfc16b219b674` Passed。
- 回复上界：恐惧 `5682f66324eb47cf9566a40c0a210666`、极速 `abb60af868654402a32e199e5798d0b9` 的 `COMPONENT-HEALING-BOUND` Passed，覆盖再生、生成、原生差分及 Fork／live 隔离。
- 六角色 × 两模式短搜：`coverage/fixtures/search/modes-{dread|blitz}-{character}.json` 十二组 Passed，固定 1000 ms，启用增量／完整回放核对，全部 boundary=None。逐组 runId 和实际数字见结构化证据的 `MODES-SHORT-*`；这些耗时包含增量校验。
- 完整部署：恐惧原生铁甲起手、知识恶魔领域及首次选择，`2d33cb20fa314a8ead52ead727b5f341` 六回合胜利、64/80 HP；极速愚者八牌夹具 `8747ce84ce8d4e1b98dad32e54ad81b1` 四回合胜利、61/77 HP。均为固定 1000 ms、Instant／0 秒，计划外重算为 0。首次选择独立合同 `23f9c964d4a14488817ba25087661c9f` 通过原生页面等待启动与两条选择分支。
- 新包恢复：恐惧 `1280e7132ba4455baeabcf938b369936`、极速 `1f4358ea699340e8a7694415e7197649`，`RestoreOnly/start` 的 native-state、continuation 与 ready checkpoint 均 Passed。合成旧格式极速包 `40b52f8505764b4a93473c319f2d4cf0` 同样通过，`legacyRunRulesFromSaveVerified=true`；夹具从新包移除模式元数据及续用尾段，保留原生存档与事件。
- 路线缓存：恐惧 `3f96159381f845b3a421e9523ad46008`、极速 `7a96fb7a94a24eef9638888ed98bfd8d` 的 `ROUTE-CACHE-RECORD-V0111` Passed，覆盖写盘、会话重建恢复、状态／策略隔离与手动重算。固定 1000 ms，小怪 HP 18。
- 极速奖励组合：第二幕 WingCharm → FresnelLens `c1974e9f01ee4383a7b01f5e335ff3f3`、第三幕 Glitter → WingCharm `ae749c1d3cc249c995c3c4ef8289da41` Passed；各八个原生样本，满槽换药收益与原生药水价值一致，预测保持 live RNG。
- 标准模式哨兵：抽牌前／洗牌前／抽牌后嵌套选择 `294d068bdf07477b9af1873b42c5b153`，回手顺序 `6738177e49134aa39b6e6baeeff86d05`，Fabricator 召唤死亡顺序 `19d733c382fe49e3bc3788fde454d6dc` Passed。
- 单步接管：恐惧 `4bc8313a435a4681a41fa38070f6a43f`、极速 `1a7bea7d65dd414e9c29f0a68ad43d1d` Passed，原生铁甲牌组与 ToastyMittens，第二回合原生页面等待；分别接管为 ExecuteCurrentTurn／FullAuto，复用成功且计划外重算为 0。恐惧请求写出 Passed 后等待宿主复用超时，finally 已停止并清理实例。
- 首次选择停止／重启：`d1bba17979674972bb66cdc486f89cf3` Passed，`TURN-SETUP-UI-STOP-RESUME`、恐惧知识恶魔领域，完成停止首次搜索、重算、再次停止、继续到原生 Play，忙碌标志释放。
- 本地化：`d75cb731c874485c86c0ad3f475c552e` Passed，`UI-LOCALIZATION`、`PHROG_PARASITE_ELITE`，核对三语言模板、动态名称、召唤站位／Fork／死亡身份及控件投影。

实际失败：领域合同 `6a5a9e51e1584ede841c79bbf883fe6b` 枚举包含编译器生成类型。变牌旧请求 `63d7d69026ee4f9197243a9bc62c3c04`、`636a3972e33f48deb704c8647645fbfe`、`65a3c516bdb046f1a41b3490c6083cef`、`24d1931169bd401e9e1703ddf10848d0` 超时，已确认 `StopAfterInitialSetupAssertion` 等待初始求解结果；纯合同改用 `StopAfterCombatRootSnapshotAssertion` 后通过。生成缓存 `68836507d3b847dd9e3d83eeca8e76df` 的测试候选缺少模式过滤，修正为原生资格核对后通过。女王首次 `387e1f7ff6874caeb7edb6e56705b8cc` 加载前一构建，证明开场效果缺失。沙漏 `eec634823b034196ab4f59d1a269c1d9` 缺普通牌输入；`1ad65d2bb0fc405796a0afb11d8ef0ab` 发现随机插入少消耗 3 次洗牌 RNG，修复后通过。

夹具边界失败：召唤合同 `4c6f6176dd5b426ca97a54296be2b7e5` 使用默认遭遇缺少增援；改用 Fabricator 后通过。本地化合同 `f130739e31c94380bcafb56956545069` 使用默认遭遇缺少蠕虫站位，改用 `PHROG_PARASITE_ELITE` 后通过。启动器曾偶发进程路径身份读取失败，已补充实际路径与 PID 诊断，具体原因尚未确定。

验证范围：可见 UI 交由用户实机体验；本批未作同根基线搜索性能比较。跨模式缓存身份由根规则字段与续用字段共同隔离，已静态核对身份写入并分别验证两模式缓存生命周期。每个已结束实例均由启动器清理。Release、工具构建、Windows／Linux 结构门禁已通过；覆盖工具识别 RunModel、领域及实际继承覆写，3437 条，未分析、runtime 证据缺口及必需状态写入证据缺口均为 0。

复跑沿用 `tools/testing/run-unattended-test.ps1/.sh`，模式参数为 `-GameMode`／`--game-mode`，可指定 `-DreadBossEncounterId`／`--dread-boss-encounter-id`；领域子集使用 `-DominionIdsForTest`／`--dominion-ids-for-test`。所有请求带实例清理，单次上限 120 秒；纯 Hook 差分使用上述场景，Power 差分传 `-MonsterMoveChecksPath`。

## 请求预算显示

0.51.0 定稿的验证结果、复跑入口、失败与未验证项见[历史卷 23](archive/testing/volume-23.md#请求预算显示)。

## v0.113.0 标准模式适配

0.51.0 定稿的验证结果、复跑入口、失败与未验证项见[历史卷 23](archive/testing/volume-23.md#v01130-标准模式适配)。

## 首领卖血预算搜索

0.51.0 定稿的验证结果、复跑入口、失败与未验证项见[历史卷 23](archive/testing/volume-23.md#首领卖血预算搜索)。

## 社区批次 Q010 与组合补搜

现行组合入口按共享节点与时间准入，执行期间在每批提交边界处理内存预约、回收和停止。原包 SearchOnly、Beam 组合检查及真实 CLR 合同的结果、失败与未验证项见 [历史卷 20](archive/testing/volume-20.md#社区批次-q010-与组合补搜)。

## 0.50.1 智能药水机会成本

终局单药／双药低收益拒绝与 Force、奖励抵扣、原价高收益哨兵由 `SMART-OPENING-POTION-ADMISSION` 覆盖；跨成员比较由 `python -B tools/testing/checks/BeamWidthPortfolioChecks/run.py` 覆盖（118项通过）。原生 runId `d9629992ac31423586c7465510c18f6d` Passed（23.63秒，含建局），实例已清理。失败基线、同根材料与未验证范围见[问题记录](issues/potion-opportunity-20261006.md)。

## 社区批次 Q015：路线保留

0.51.0 定稿的验证结果、复跑入口、失败与未验证项见[历史卷 23](archive/testing/volume-23.md#社区批次-q015路线保留)。

## 社区批次 Q013 同根哨兵与部署夹具

0.51.0 定稿的验证结果、复跑入口、失败与未验证项见[历史卷 23](archive/testing/volume-23.md#社区批次-q013-同根哨兵与部署夹具)。

## 开局药水补搜准入

固定两槽满栏，使用原版奖励 RNG 捕获确定掉药／不掉药的搜索根。`SMART-OPENING-POTION-ADMISSION` 覆盖低损共同准入与换药抵扣，`SMART-OPENING-POTION-VALUE` 覆盖原价省血边界。失败基线、最终结果与验证范围见 [历史卷 20](archive/testing/volume-20.md#开局药水补搜准入)。

```text
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId SMART-OPENING-POTION-ADMISSION -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
./tools/testing/run-unattended-test.sh --scenario-id SMART-OPENING-POTION-ADMISSION --character-id SILENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --enable-no-gc-region-for-test 0 --timeout-seconds 120 --cleanup-instance-on-exit
```

将 ScenarioId 改为 `SMART-OPENING-POTION-VALUE` 可单独执行原价省血边界。

## 0.49.1 日志站硬逻辑（2026-10-04）

强制结束回合选牌、群体 Power、死亡金币回调、延迟能量／等离子球、开局小刀、回合末自动出牌、行动意图、资源隔离、反应格挡和界面归属的原生差分见 [逐类验收](issues/0.49.1-hardbugs-20261004.md#原生验收证据)。该记录保留失败基线、runId、复跑入口、第三方边界及未验证项；生产部署合同包含增量验证和计划外重算断言。

0.49.2 内容性 Mod 失败分类的原生合同、完整命令及未验证范围见[历史卷19](archive/testing/volume-19.md)。

## 0.49.1 内存提交回归（2026-10-04）

`CombatSolver.GcPolicyChecks` 直接编译生产 Runtime 内存代码。`default-commit` 在未改生产代码时 Failed：显式回退后分配上限仍有效；修复后 2 项 Passed，覆盖普通检查点保留限额、不可分割提交完成回收后续行、后续请求重新建立限额与取消保持原准入。

```bash
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- default-commit
```

相邻真实 CLR 回归：`default-entry` 2 项、`recovery-lifecycle` 3 项 Passed，包含实际 NoGC 退出与恢复、显式退出持续生效及诊断失败清理。每项设有 15 或 20 秒截止时间。未重放 18 份原包整场，也没有可见 Steam 或低内存宿主性能结论；[排查记录](issues/0.49.0-memory-commit-regression-20261004.md)保留固定报告身份与触发窗口。

## PR #203 的机制合同

金币、最大生命回复与遗物间接回复的贡献者证据见 [专题记录](performance/gold-max-hp-healing-20261003.md)。独立场景为 `GOLD-HEALING-MECHANISMS`、`MAX-HP-HEALING-CALLBACKS`、`FEED-MAX-HP-CAP`、`RELIC-MAX-HP-HEALING-BOUNDS` 和 `AXEBOT-JOSS-DEFERRED-FORK`，均从平台原生无人入口运行，使用 IRONCLAD、FUZZY_WURM_CRAWLER_WEAK、120 秒上限及实例清理。原作者结果与本轮合并验证分别记账。

金币和回复合同同时传 `-EvidenceDirectory .local/validation/pr203/<场景>`（Bash：`--evidence-directory`），初始生命为 50/80。金纸合同还传 `-RelicsPath coverage/fixtures/regressions/axebot/axebot-joss-deferred-relics.json`（Bash：`--relics-path`）。搜索阶段顺序合同为 `KNOWN-HEALING-MEMBERS` 和 `KNOWN-HEALING-OPENING`，使用 SILENT；受影响的固定前缀合同使用 IRONCLAD，强制结束回合牌合同使用 REGENT。

## 当前矩阵命令

矩阵启动器只读取本节的平台原生命令；历史记录不作为自动执行清单。以下两项分别检查倾泻手空边界与相邻效果作用域，不运行整场搜索。需要当前游戏及 RitsuLib 路径，矩阵启动器统一管理实例并在结束时清理。

```powershell
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CASCADE-EMPTY-HAND-NATIVE -EnemyCurrentHp 1000 -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId EFFECT-SCOPE-ADJACENT-CONTRACT -EnemyCurrentHp 1000 -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120
```

```bash
./tools/testing/run-unattended-test.sh --scenario-id CASCADE-EMPTY-HAND-NATIVE --enemy-current-hp 1000 --headless-fast-mode-for-test Instant --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 120
./tools/testing/run-unattended-test.sh --scenario-id EFFECT-SCOPE-ADJACENT-CONTRACT --enemy-current-hp 1000 --headless-fast-mode-for-test Instant --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 120
```


## 0.48.0 硬错误机制合同

历史结果和复跑命令见[历史卷 22](archive/testing/volume-22.md)。

## 社区批次 B013 回归夹具（2026-10-03，Refs #172）

以下为贡献者在 PR #204 记录的结果，与本轮验证分别记账。五个主题的独立机制回归在 `src/Testing/Regressions/Community/B013*Checks.cs`，命令统一为
`tools/testing/run-unattended-test.ps1 -ScenarioId <ID> -TimeoutSeconds 120 -CleanupInstanceOnExit`（角色/遭遇按夹具要求，详见 PR #204）：

- `B013-BLOCK-DECIMAL-BOUNDARY`（T010，格挡乘法 Decimal 边界）：修改前 `dec22b8f` Failed（栈同报告）→ 修改后 `e2757311`/`a23dcf21` Passed；代表包 DOP1 哨兵路线与展开/转移一致。
- `B013-FIXED-PREFIX-TERMINAL-BOUNDARY`（T008，固定前缀越过锁定终局）：`971ccdd1` Failed → `0b3f96a6` Passed；代表包哨兵 22809/129762、终局一致。
- `B013-FIXED-PREFIX-TURN-END-CARD`（T007，结束回合卡固定前缀）：`eb64242d` Failed → `899ef881` Passed。
- `B013-RADIANT-PEARL-HAND-DRAW`（T006，抽牌前遗物生成对账）：`cd227ff6` Failed → `15c80923` Passed；代表包 SearchOnly 失配 → `TURN_SETUP_STATE_MATCH`。
- `B013-DEFAULT-GC-LIMIT`（T009，默认 GC 请求分配边界）：`6ac0f466` Failed（`limit=long.MaxValue`）→ `7c6f3b83` Passed。

变基到 0.48.1（`2ead87d9`）后五夹具复跑 Passed：`0f19174f` / `57e2086d` / `9d7173af` / `3892e307` / `fdfc8734`。代表包哨兵在新基线：T010 DOP1 与旧基线逐字段一致（展开 7426、转移 77129、4 回合胜、finalHp 71），耗时 3458.9ms；T009/T006 代表包 7 回合胜、finalHp 88、boundary None（展开 26194、转移 190351，路线随上游搜索改动微调）。

完整修改前后、哨兵与未验证项见 PR #204；批次状态与剩余项（T007 第二样本未复现）同样记录在该 PR。

## PR #203、#204 合并验收（2026-10-04）

以下为本轮直接运行结果；全部使用120秒上限和实例清理，已通过的请求未重复运行。无头验证停在对应机制边界。

| 场景 | runId | 结果与范围 |
| --- | --- | --- |
| `GOLD-HEALING-MECHANISMS` | `3bc7928290c343cca88248c0c28dd95e` | Passed；金币修正、三个回调、完整原生状态/RNG和父分支隔离 |
| `MAX-HP-HEALING-CALLBACKS` | `cd6549478924451882fc600e67724a53` | Passed；最大生命实际增量、封顶与回复回调的原生差分 |
| `FEED-MAX-HP-CAP` | `ddcf661e391c4013a0aa87861f8542f3` | Passed；两次致命出牌的实际增量与成长计数 |
| `RELIC-MAX-HP-HEALING-BOUNDS` | `bee94b31824d449e8f7e9fa55cbe94f5` | Passed；两项直接原生遗物回调与熔化来源排除 |
| `AXEBOT-JOSS-DEFERRED-FORK` | `0fd9902a8a594e36a0a9fbf533ceee65` | Passed；根计数、零状态指纹、父子/兄弟Fork与逐分支消费 |
| `KNOWN-HEALING-MEMBERS` | `fab7a8170a3942e9920e09fa466aa34e` | Passed；严格增量、DOP2、控制质量、成长门与live隔离 |
| `B013-FIXED-PREFIX-TERMINAL-BOUNDARY` | `5b097fd5f17745a0868a6b49d7af3b37` | Passed；终局前缀截断与可交付胜利 |
| `B013-FIXED-PREFIX-TURN-END-CARD` | `c778f285a52a4b9fbbe0e370688e6ec1` | Passed；强制结束回合卡的固定前缀推进 |
| `B013-RADIANT-PEARL-HAND-DRAW` | `ca904a20eafd43b690d8c824f60e2069` | Passed；抽牌前生成的原生数量、升级与归属对账 |
| `B013-DEFAULT-GC-LIMIT` | `3e440c65f9564b5ea6a65d094521b8d7` | Passed；限额、真实Gen2回收续搜、退出清理与CLR模式 |
| `KNOWN-HEALING-OPENING` | `3fecd35f80a34cb792a43ca3b5bc3b1c` | Passed；非认证根延后计划、单次执行与控制质量 |
| `FIXED-PREFIX-TURN-OUTCOMES` | `b18d23d1829b4438a557a5f75acf6425` | Passed；独立前缀完整状态oracle、多回合结果与终局截断 |
| `B013-BLOCK-DECIMAL-BOUNDARY` | `b556026e627c4153bb3d2bf23948a095` | Passed；原生虚弱倍率、完整状态/RNG；模拟96/95/200层与零格挡 |

真实CLR工具：`default-entry` 2项、`diagnostic-failure` 8项、`scopes` 8项 Passed。入口失败基线为日志抛错后信号仍启用；修复后同异常传播、信号清理及后续独占准入均通过。

失败记录：首次无头启动在私有进程身份检查处失败并清理，未进入游戏测试，原因未确定；金币首轮14项对账已通过，但缺EvidenceDirectory导致产物写入失败；金纸首轮缺遗物输入。补齐请求参数后仅重跑失败请求，成功记录在上表。固定前缀09f94286012d420d81242f480ebd1803仍执行旧的终局拒绝断言，合同更新为开局探测和完整搜索共同截断，并断言终局动作数及回合。新增格挡夹具初次编译因原生调用参数及私有setter失败，修正后Release零警告/错误。

L0：原生回复审计工具迁入tools/inspection后构建和真实DLL扫描通过。CoverageCatalog重新生成3035项目录，状态字段未分类为0；`--verify-state-writes`仍因既有InfusedCore.AfterSideTurnStart缺运行证据失败（1项）。PowerShell结构检查247文件通过，工具检查290文件/37项目通过，文档427文件/1635链接及覆盖目录检查通过。额外Bash结构检查因运行耗时停止，未完成；两平台脚本静态语法检查通过。

范围：本轮没有重跑作者长预算全根性能筛查，没有验证低内存玩家宿主、原生整场部署或可见Steam性能；原作者失败与未验证项保留在所属报告。


## 性能研究分支

既有组件、药水、魂枢及未达标原型的合同与历史结果见[历史卷18](archive/testing/volume-18.md)。PR #207最新上游整合、逐次ABBA、23根品质回归及NoGC波动/超时限制见[当前验收](performance/pr207-upstream-0494-integration-20261005.md)。

## 手牌上限状态一致性（PR #224）

原版根／Fork 合同与贡献者的动态上限及耗时证据见[历史卷21](archive/testing/volume-21.md#手牌上限状态一致性pr-224)。
