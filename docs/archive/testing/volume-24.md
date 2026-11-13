# 测试记录 · 卷 24

0.51.1 · 0.113 适配 Part 2，2026-11-13 定稿。

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

