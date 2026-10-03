# CombatSolver 测试入口历史卷 06

## 0.33.3 召唤与死亡监听顺序

- `SUMMON-DEATH-POWER-ORDER` / `FOGMOG_NORMAL` 失败基线 `b61c2d6e82ce4b7cb9585e1e18e0315a`：T2 完整状态 P[0] 预测 Strength、原生 Illusion，精确复现问题包同根顺序差异。
- 修复后 `749e6fb069d6499da1e00861c2e0fcfa` Passed，34 秒；T1 至 T3 召唤、击杀与复活，每轮完整原生状态和 Fork 对账一致。
- `OVICOPTER_NORMAL`，`beff275eb28c4e52b9c0e88ebd93a615` Passed，38 秒；召唤后击杀一个蛋并推进到 T3，完整原生状态和 Fork 一致。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId SUMMON-DEATH-POWER-ORDER -EncounterId FOGMOG_NORMAL -HeadlessInstance summonorder -TimeoutSeconds 120 -ExitOnComplete`；另一场替换 EncounterId 为 `OVICOPTER_NORMAL`。
- 中间一次请求因构建尚未结束导致冻结 DLL 失败；另一次 `2ff74b0932904eebb65dc1abf745c0a1` 在原生资源预加载期间退出（0xc0000005），尚未进入目标战斗；相同构建重试通过。原问题包仅材料预检有效，未做整场路线恢复或完整发布门禁。

## 2026-09-07 图表聚合与悬停

- 后台 7 项测试通过；新增 1440 个高频交替采样合并为 144 个均值点、保留原始峰值，30 天/窄窗口降低采样密度，断档拆桶、零值和空数据。
- Playwright 对真实本地 SQLite 历史与 HTTP 服务验证聚合点数、原始峰值、断档、桌面/手机响应粒度；同一横轴顶部与底部命中相同数据，Canvas 像素检查确认垂直虚线，移出绘图区后清除悬停。未向正式库写入样例历史。

## 2026-09-07 排行与分页

- 服务端 4 项测试通过，新增 62 人分为 30/30/2 三页、全局降序名次、全局搜索、非法页参数、空结果/越界页、概览不携带名单、断线区间排除、跨次上线及服务重启后累计时长保留。
- Playwright 对真实本地服务验证首屏只请求第一页、每页最多 30 个 DOM 行、翻页、跨页搜索保留名次、恶意昵称纯文本、在线人数减少后的页码回收及桌面/手机布局。测试玩家仅写入本地临时数据库，未上传正式后台。

## 2026-09-07 后台公网 HTTPS

- 服务端原 3 项测试通过；实际公网管理入口验证证书名称/信任、页面 200、匿名 API 401、登录、Secure/HttpOnly cookie、错误协议 Origin 403、退出后会话拒绝。未上传测试玩家明细。
- 本轮只更新 Node 服务与运维配置，沿用已发布客户端；未重建或重发 Mod。

## 0.33.2 新召唤敌人行动

- `LIVING-FOG-SUMMON-INTENT`：失败基线 `9c5be94ee8ae48aeac82d4ef1b42a5d4` 精确复现 EXPLODE_MOVE 无后继异常；最终 `d54fff51d884479bb39176b0fa38d02f` Passed，34 秒。T1 BLOAT_MOVE 召唤至 T2，再推进自爆至 T3，两处完整原生状态、阵容、牌堆、AI、RNG 与 Fork 一致。中间运行的召唤数量断言修正见问题记录。
- 相邻 `RAT-SUMMON-NEXT-INTENT`，`58495b308c9448e3812284678f3cfdab` Passed，31 秒，确认需要首次 Roll 的新召唤双尾鼠仍正常生成意图。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId LIVING-FOG-SUMMON-INTENT -EncounterId LIVING_FOG_NORMAL -HeadlessInstance fogfix -TimeoutSeconds 120 -ExitOnComplete`；相邻用例替换 ScenarioId 为 `RAT-SUMMON-NEXT-INTENT`、EncounterId 为 `TWO_TAILED_RATS_NORMAL`。
- 原问题包只执行 Preflight，未作完整恢复结论。本次不扩展完整发布门禁；在线统计沿用下节同源行为证据。

## 0.33.1 在线统计

- `ONLINE-PRESENCE-CONTRACT` Passed，runId `76c3b303aee842b687562655577b551b`，23 秒。验证默认开启、关闭值序列化持久化、当前角色/楼层/战斗/未知战损标量快照、真实 .NET HTTPS 校验及错误证书指纹拒绝。网络检查发送无个人字段的空对象，预期 400；没有向正式统计写入测试玩家。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId ONLINE-PRESENCE-CONTRACT -HeadlessInstance presence -TimeoutSeconds 120 -ExitOnComplete`。要求私有端点配置；普通无人请求仍完全隔离上报。
- `tools/OnlinePresence` 的 `npm test`：3 项通过，包含字段/数值拒绝、鉴权与 Origin、安装标识去重、TTL、重启后聚合历史持久化及限流。
- Playwright 使用真实服务登录与空列表；注入页面级样例后验证桌面/手机布局、折线画布非空、搜索和昵称作为纯文本渲染。样例未发送至正式采集端。未以此声称已观察真实玩家的战斗路线或精确 Steam 人数。
- 正常 Steam 游戏启动后，正式后台收到 1 个带昵称的菜单心跳，角色为空、楼层和战损为 null；没有进入跑局。此行为检查使用版本元数据调整前的 0.33.0 测试构建，行为源码与 0.33.1 相同。

## 0.33.0 发布集成

- PR #57、#58 已合入本批。`PR57-58-STATE-CONTRACT` Passed，runId `e59d8568334c490c9ad9d488288c2ba7`，22 秒。验证污染叠加保持为 4、火花数量变化后同步为 3；隐藏状态槽排序、重复登记拒绝、根捕获委托参数分派、隐藏值变化区分指纹，以及撤销测试登记后恢复原指纹。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId PR57-58-STATE-CONTRACT -HeadlessInstance logic0907 -CardId DEFEND_IRONCLAD -TimeoutSeconds 120 -ExitOnComplete`。登记测试仅临时使用原版 StrengthPower，finally 清理测试项，不新增生产注销入口。
- PR 集成 Release 编译通过，0 警告、0 错误。旧批次沿用下节既有证据；本次未执行完整发布门禁、117 份原包完整恢复或第三方角色整场适配。隐藏状态根捕获本次验证委托分派，未声称第三方内部状态的完整捕获、Fork 或续用通过。
- 暂停范围与后续调查见 [交接文档](../issues/report-logic-bugs-20260907-handoff.md)。

## 2026-09-07：汇总日志硬逻辑批次

- `RAT-SUMMON-NEXT-INTENT`：基线 `6d34dbb147f143d58baaa535d3c4c997` 新个体下一行动预测 SCRATCH / 原生 DISEASE_BITE；修复后 `370dae413cb94195b2f5143a17132ad9` Passed。正式 EndTurn 回放对照原生下一玩家回合，完整状态、阵容数量、AI 日志与 RNG、Fork 一致。命令：`./tools/run-unattended-test.ps1 -ScenarioId RAT-SUMMON-NEXT-INTENT -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId TWO_TAILED_RATS_NORMAL -TimeoutSeconds 120 -ExitOnComplete`。
- 怪物下一行动遍历修复的相邻回归 `mercury-reattach-boundary-v0111`，`d74dcb906e9543d8b1e336456de303dc` Passed，沿用本节同名命令与完整复活/死亡差分断言；没有扩大测试超时。
- `TOASTY-MITTENS-NINE-CARD-SETUP` Passed，`8cdd213e82be4468aee65b200ffa9fe9`：SILENT 初始牌组、BAG_OF_PREPARATION / TOASTY_MITTENS 的原生开局选择与精确状态激活通过。命令：`./tools/run-unattended-test.ps1 -ScenarioId TOASTY-MITTENS-NINE-CARD-SETUP -HeadlessInstance logic0907 -CharacterId SILENT -EncounterId GLOBE_HEAD_NORMAL -RelicsJson '[{"relicId":"BAG_OF_PREPARATION"},{"relicId":"TOASTY_MITTENS"}]' -StopAfterInitialSetupAssertion -ExpectedInitialSetupChoiceSourceId TOASTY_MITTENS -ShortSearchBudgetOverrideMilliseconds 1500 -DeepSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`。未重现三份原包的全部牌组/遗物组合，不表示原报告解决。
- `FUNERARY-MASK-BEFORE-DRAW`：基线 `2cea9bef9f8d4b9198d1d3bf5859d694` 抽牌堆预测 4/原生 7；修复后 `f9fe9225531b4729930707d0688e344c` Passed，完整状态、随机插入顺序、RNG、Fork 及 turn 2 不再生成通过。命令：`./tools/run-unattended-test.ps1 -ScenarioId FUNERARY-MASK-BEFORE-DRAW -HeadlessInstance logic0907 -CharacterId NECROBINDER -EncounterId GLOBE_HEAD_NORMAL -TimeoutSeconds 120 -ExitOnComplete`。第二个回合条件通过原生 IncrementTurnNumber 注入，不代表完整两回合推进。
- 面具与筹码原生开局 `FUNERARY-MASK-TURN-SETUP`，`a7c54bdfd1e44721b30b184a0a8556b9` Passed，原生选择顺序与精确状态激活通过。命令：`./tools/run-unattended-test.ps1 -ScenarioId FUNERARY-MASK-TURN-SETUP -HeadlessInstance logic0907 -CharacterId NECROBINDER -EncounterId GLOBE_HEAD_NORMAL -RelicsJson '[{"relicId":"GAMBLING_CHIP"},{"relicId":"FUNERARY_MASK"}]' -StopAfterInitialSetupAssertion -ExpectedInitialSetupChoiceSourceId GAMBLING_CHIP -ShortSearchBudgetOverrideMilliseconds 1500 -DeepSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`。停在准备阶段，不作完整战斗结论。
- `TURN-SETUP-REFRESH-TAKEOVER`：基线 `34ed1aaed32a4deca433ce2847c43ff7` 重算期间开始执行旧计划；修复后 `94d006288b364d529ae5a579c5625855` Passed，接管排队、新计划发布、原生选择和精确状态激活通过。正常等待重算后接管的 `TURN-SETUP-REFRESH-NORMAL`，`4fc6aa624a5a46b1b061cb7ec35e362a` Passed。命令：`./tools/run-unattended-test.ps1 -ScenarioId TURN-SETUP-REFRESH-TAKEOVER -HeadlessInstance logic0907 -CharacterId SILENT -EncounterId GLOBE_HEAD_NORMAL -RelicsJson '[{"relicId":"GAMBLING_CHIP"}]' -VerifyTurnSetupManualRefresh -StopAfterInitialSetupAssertion -ExpectedInitialSetupChoiceSourceId GAMBLING_CHIP -ShortSearchBudgetOverrideMilliseconds 1500 -DeepSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`；正常流程仅替换 ScenarioId。固定短搜，停在准备阶段验收，不作整场求解质量结论。
- `CARD-ENERGY-GAIN-COMMAND`：基线 `e8b64cd4c1e44bc598e618f581c7273d` ALIGNMENT 能量预测 12/原生 10；修复后 `0647bc3be5824b7691859854e975b80a` Passed，11 张增能卡逐张完整原生差分通过，含动态增能、附加 Power 与生成牌。命令：`./tools/run-unattended-test.ps1 -ScenarioId CARD-ENERGY-GAIN-COMMAND -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId GLOBE_HEAD_NORMAL -PowerId NO_ENERGY_GAIN_POWER -PowerAmount 1 -PowerTarget Player -TimeoutSeconds 120 -ExitOnComplete`。初始建局 `c0dd9ab626ca404e985c6d9f1ee277e1` 遗漏 ALIGNMENT 的星能，原生无法出牌，未计作语义基线。此组只验证禁止回能命令路径，不宣称原报告全场回放通过。
- `SURROUNDED-STATE-IDENTITY` 扩展评分缓存检查 Passed，`99bb428e0c68471ba8a4c4b0d75d1234`：Crusher THRASH / Rocket CHARGE_UP，正式 Snapshot 左右朝向有不同指纹、预估 HP 与评分；同一 solver 先计算左再右，右值与独立 solver 一致，重新计算左值稳定。保留原朝向状态、续用、Fork 和 10/15 背击断言。命令沿用下方同名场景，增加 `-ExitOnComplete`。未执行 27 份蟹皇旧包的完整回放。
- `MELANCHOLY-OSTY-DEATH` Passed，`05b52a6c87034108996792f3e47ccbd6`：四个牌堆的升级忧郁带 SWIFT 2 / BOUND 3，奥斯提死亡后的完整原生差分、直接/分叉等价、父分支隔离及再次 Fork 通过。命令：`./tools/run-unattended-test.ps1 -ScenarioId MELANCHOLY-OSTY-DEATH -HeadlessInstance logic0907 -CharacterId NECROBINDER -EncounterId GLOBE_HEAD_NORMAL -TimeoutSeconds 120 -ExitOnComplete`。仅证明直接死亡通知，不覆盖报告 `c3f8cf86` 的最终路线回放。首次请求误用了不存在的遭遇 ID（`957f8afbf5914bc08621ee843cf5eaa7`），未进入战斗，不属于语义失败基线。
- `QUEEN-INFERNO-TERMINAL`：基线 `ace01a08d43b49ecbf358cb01fc89556` 清理前能量预测 5/原生 3；修复后 `5123f0ad07074cf9926b2c554bf8dc26` Passed，完整状态和 Fork 相等。使用与 `QUEEN-INFERNO-MINION-DEATH` 相同 CLI 参数，仅替换 ScenarioId；两个敌人均保留注入的 9 HP，通过既有原生 `EndCombatInternal` 观察者在战后回血和清理前取样并等待 CombatEnded。原先战后取样的 HP 77/80 不再作为模拟错误证据。
- `QUEEN-INFERNO-MINION-DEATH` Passed，`5e435cccefad4506a75537c2831c136f`：满血女王预置强化随从行动，炼狱击杀 9 HP 随从，原生/模拟完整状态及 Fork 一致，并检查阵容仅保留原女王实例。命令：`./tools/run-unattended-test.ps1 -ScenarioId QUEEN-INFERNO-MINION-DEATH -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId QUEEN_BOSS -CardId BLOODLETTING -ClearPlayerPiles -EnemyCurrentHp 9 -PowerId INFERNO_POWER -PowerAmount 9 -PowerTarget Player -TimeoutSeconds 120 -ExitOnComplete`。仅证明该最小路径，不证明报告 `387a2e1c` 已修复；放血能量命令修复后的相邻回归 `0a5c33b2f0f34339a14ae343338b87e1` 同样通过。
- `KNOWN-GAMEPLAY-MOD-BOUNDARY` Passed，`aa1ab43130824665a2524a107521b75b`：合成清单 ID 命中、清单改名但程序集名命中均抛出含实际 Mod ID 的 `IncompatibleGameplayModException`；明确拒绝策略优先于中性声明，空集合正常通过。命令：`tools/run-unattended-test.ps1 -ScenarioId KNOWN-GAMEPLAY-MOD-BOUNDARY -HeadlessInstance logic0907 -CharacterId DEFECT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。Release 零警告/错误并同步本地 mods；没有安装或执行第三方 Mod，也没有声称复现其改牌实现。
- `CYCLE-EXIT-REVOKED-PARENT`：基线 `4f2102226f00454396abd02b421ada79` Failed，子节点已有临时出口观测、父租约随后撤销时，生产准入条件跳过处理，观测残留。修复后 `b07bd11d4dac484390a2badc67d5c8fb` Passed，分别覆盖卡牌/药水/结束回合输入列表，清理失效观测且 tracker 不获得 envelope；沿用原混合顺序、反向顺序、64 候选单出口及其他无效观测合同。命令：`tools/run-unattended-test.ps1 -ScenarioId CYCLE-EXIT-REVOKED-PARENT -HeadlessInstance logic0907 -CharacterId REGENT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。这是合成调度元数据的生产准入条件与 materialization 合同，不是原包整场回放，也不构成 DOP 性能或整场质量结论。Release 零警告/错误并同步本地 mods，Windows 结构门禁通过。
- `NARROW-ORDERED-PILE-CAPACITY`：基线 `ec88461ccf314096955767f71754f2d1` Failed，真实 `RankBest` 抛“Beam 容量不足以保留策略必需分支”。夹具给四张偏折设置不同格挡数值，逐一回放 24 种出牌顺序并结束回合，确保至少 8 种有效状态/预计洗牌顺序，交给 6 宽 Deep 怀表通道。修复后 `808eda35b341442081083729dd9881d1` Passed，结果在既有 6–7 容量内、身份无重复；未饱和通道保留全部候选。可重跑命令：`tools/run-unattended-test.ps1 -ScenarioId NARROW-ORDERED-PILE-CAPACITY -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -CardsPath coverage/unattended/report-narrow-ordered-pile-cards.json -RelicsJson '[{"relicId":"POCKETWATCH"}]' -EnemyCurrentHp 80 -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120`。本次运行使用内容相同的内联 `CardsJson`，文件是在通过后固化的输入；这是回放生成候选后的局部政策合同，未宣称原包整场搜索通过。
- 本项短搜索回归 `FEED-THORNS-TERMINAL-TWO-CARDS` 的 `ffc089f076784745a8ccb6217b9b4221` Passed，沿用既有 1500 ms 固定预算与增量验证命令：先防御后狂宴、T1 零损胜利、6 展开/15 转移。Release 编译零警告/错误并同步本地 mods，Windows 结构门禁通过。
- `GAMBLERS-BREW-SLY-ORDER`：基线 `4dd54bb5380e4ebd84497e72ccdf2784` Failed，单张连续反弹被弃牌重抽后，预测仍在手牌，原生已经通过狡猾打出并回到弃牌堆。修复后 `e4411503e76b49fd9d76a8c2ab9a9627` Passed，原生用药的牌堆、伤害、历史与 RNG 完整差分一致。命令：`tools/run-unattended-test.ps1 -ScenarioId GAMBLERS-BREW-SLY-ORDER -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -EnemyCurrentHp 80 -PotionCheckPath coverage/unattended/report-gamblers-brew-sly.json -TimeoutSeconds 120`。首次夹具 `77d4c3ed342845809c54b3d4b9bf59d4` 因药水差分分支未采用命令行 CardId，候选缺失；改为在药水夹具中显式注入后才取得目标失败基线。
- `GAMBLING-CHIP-SLY-ORDER` Passed，`47ec379ab7f24176b8010eed8e1ac112`：开局弃牌重抽生产选择处理器与其 Fork 完整结果一致，并与原生 `CardCmd.DiscardAndDraw` 对照通过。命令：`tools/run-unattended-test.ps1 -ScenarioId GAMBLING-CHIP-SLY-ORDER -HeadlessInstance logic0907 -CharacterId SILENT -CardId RICOCHET -ClearPlayerPiles -EnemyCurrentHp 80 -TimeoutSeconds 120`。本测试冻结选择后直接对照原生组合操作，没有重放遗物 UI。
- `DISCARD-DRAW-SLY-PENDING` Passed，`3e182f6452944415b0e6f1850569e017`：抽牌已完成后才进入狡猾自动牌的挂起选择，处理器仍返回未完成。旧断言要求此时抽牌堆不动，实际固化了错误顺序，现按原版改为检查先抽牌。命令：`tools/run-unattended-test.ps1 -ScenarioId DISCARD-DRAW-SLY-PENDING -HeadlessInstance logic0907 -CharacterId SILENT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。Release 零警告/错误并同步本地 mods，Windows 结构门禁通过；原报告整场及全部 Mod 环境未回放。
- `GALVANIC-GENERATED-POWER`：基线 `4a50b407274a47e98c282bd5992c9522` Failed，生成 `AUTOMATION` 时预测没有苦难，原生为 `GALVANIZED:6`。修复后 `9ef200eadb2945e6949c1257c7987875` Passed，比较原生固定生成、Fork 后完整状态、子分支打出不污染父分支，以及原生打出的能量/Power/HP/牌堆/RNG；原有防御作为技能牌保持无此苦难。命令：`tools/run-unattended-test.ps1 -ScenarioId GALVANIC-GENERATED-POWER -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId GLOBE_HEAD_NORMAL -ClearPlayerPiles -CardId DEFEND_IRONCLAD -TimeoutSeconds 120 -ExitOnComplete`。Release 编译零警告/错误并同步本地 mods。测试直接生成原报告所选能力牌，没有重放工具箱随机选项或原包全部 Mod 整场。
- `NO-DRAW-DARK-EMBRACE`：基线 `be6fc75970c240d293327a0fae047013` Failed，先获得禁止抽牌时预测 5 / 原生 6 张手牌。修复后 `7dcb893a3c034a1d9f3d704561e88438` Passed；反向获得顺序 `DARK-EMBRACE-NO-DRAW` 的 `de4051e5c3e14b89b7bf7fc6c33ca55d` Passed，两者均比较回合末及下一回合准备的完整状态。命令：`tools/run-unattended-test.ps1 -ScenarioId NO-DRAW-DARK-EMBRACE -HeadlessInstance logic0907 -CharacterId IRONCLAD -MonsterMoveChecksPath coverage/unattended/report-no-draw-dark-embrace.json -TimeoutSeconds 120`；反向使用 `-ScenarioId DARK-EMBRACE-NO-DRAW -MonsterMoveChecksPath coverage/unattended/report-dark-embrace-no-draw.json`。
- `TURN-END-POWER-ORDER-FORK` Passed，`f4584f8b9805476aa9bfa83ad84f3d92`：相反获得顺序的指纹及续用文本不同；Fork 保留指纹、完整结算结果，子分支结束回合不改变父分支；抽牌结果分别为 1 / 0。命令：`tools/run-unattended-test.ps1 -ScenarioId TURN-END-POWER-ORDER-FORK -HeadlessInstance logic0907 -CharacterId IRONCLAD -ClearPlayerPiles -CardsJson '[{"cardId":"DEFEND_IRONCLAD","pile":"Draw"}]' -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120`。
- Power 指纹/续用改为完整有序列表后的相邻回归 `ENERGY-RESET-POWER-ORDER-REAPPLY` Passed，`5cb6fd6a83004397b413dbc71db5b59d`，覆盖移除后重新施加、Fork 与原生能量重置完整状态；命令沿用本节既有场景并加 `-ExitOnComplete`。Release 构建零警告/错误并同步本地 mods，Windows 结构门禁通过。该测试不代表其他 Power 的全部 Hook 时序已审计。
- `NO-DRAW-JOSS-END`：基线 `0b8e95d32a5d452daeb31954b38de364` Failed，下一回合手牌预测 5 / 原生 6；修复后 `53fd32235f674b708eeb851c659bdabb` Passed，回合末消耗、纸钱计数、抽牌与下一回合准备的完整状态一致。命令：`tools/run-unattended-test.ps1 -ScenarioId NO-DRAW-JOSS-END -HeadlessInstance logic0907 -CharacterId IRONCLAD -MonsterMoveChecksPath coverage/unattended/report-no-draw-joss-paper.json -TimeoutSeconds 120 -ExitOnComplete`。
- `PLAYER-END-PHASE-TWO-PENDING` Passed，`775c8f5b3a4541609857f36daf4dd968`：常规 Power 抽牌挂起时不访问后续 Power；纸钱抽牌挂起时不推进后续遗物及晚期瓦解伤害。命令：`tools/run-unattended-test.ps1 -ScenarioId PLAYER-END-PHASE-TWO-PENDING -HeadlessInstance logic0907 -CharacterId IRONCLAD -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。最后一次编译只增加测试入口与断言，生产代码沿用上条差分证据；Release 零警告/错误并同步本地 mods，Windows/Bash 结构门禁通过。原报告整场及全 Mod 环境未恢复；禁止抽牌与黑暗之拥的 Power 内部相对顺序另查。
- `BOUND-END-DRAW` 基线 `faa861a3c91747de952da67bcef4c82d` Failed：本回合束缚额度已用尽，回合末黑暗之拥抽到的首张防御预测带 BOUND，原生没有。修复后 `a8cfd38c8b74486d8dbda0763e306ff0` Passed，包含回合末抽牌及下一玩家回合准备的完整状态差分。命令：`tools/run-unattended-test.ps1 -ScenarioId BOUND-END-DRAW -HeadlessInstance logic0907 -CharacterId IRONCLAD -MonsterMoveChecksPath coverage/unattended/report-bound-end-draw.json -TimeoutSeconds 120`。
- `BOUND-ROOT-HISTORY` Passed，`315a0f6fc47e466fbba6a0a349a79f92`：捕获根前原生已经施加一次束缚，额度在根中保留，后续抽牌、回合末抽牌和下一回合准备的完整差分通过。命令沿用上条，改 `-ScenarioId BOUND-ROOT-HISTORY -MonsterMoveChecksPath coverage/unattended/report-bound-root-history.json -ExitOnComplete`。
- `BOUND-COUNTER-FORK` Passed，`bcccb419227141b587959bd39455ddcc`：父/子分支计数 1/2 互不污染，指纹区分已用额度，下一玩家回合重新计数且不修改父分支。命令：`tools/run-unattended-test.ps1 -ScenarioId BOUND-COUNTER-FORK -HeadlessInstance logic0907 -CharacterId IRONCLAD -PowerId CHAINS_OF_BINDING_POWER -PowerAmount 3 -PowerTarget Player -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。最后一次编译只新增此测试入口，前两项生产行为证据继续有效；构建已同步本地 mods，结构门禁通过。原报告的全部 Mod 和整场路线未回放。
- `PAPER-CUTS-THORNS-BOUNDARY` 基线 `78d357172eef4291927b3f9772af622e` Failed，最大生命预测 68 / 原生 70。修复后 `aaf903a158354b1c83ddbb3c097cb21d` Passed：卷轴在承伤前反伤中死亡，已开始的攻击继续命中，纸割退出普通回调；怪物行动完整状态差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId PAPER-CUTS-THORNS-BOUNDARY -HeadlessInstance logic0907 -CharacterId SILENT -EncounterId SCROLLS_OF_BITING_NORMAL -MonsterMoveChecksPath coverage/unattended/report-paper-cuts-thorns-boundary.json -TimeoutSeconds 120`。原报告整场及全部 Mod 未重放。
- 活动监听视图改动的死亡相邻回归：`DEATH-EFFECTS-ONCE` 的 `ac8a9bf19cae4502a7b3851124903b5c` Passed；`mercury-reattach-boundary-v0111` 的 `7139a36826034e0daae2f9e36c40e6ff` Passed，包含复活、再次死亡、直接/Fork/重新捕获根和清理前原生完整状态。沿用本节对应场景命令，最后一项加 `-ExitOnComplete`。Release 编译零警告/错误并同步本地 mods，结构门禁通过。
- `ENERGY-RESET-POWER-ORDER-REAPPLY` 基线 `5b0b96d193af4a7ba02fc8592f7c38d3` Failed：根中先有引雷能力，移除再获得后预测仍使用旧位置，球序继续颠倒。修复后 `017caab7d1164ae0907c7e0e1b912765` Passed，原生移除/施加、Fork 与能量重置后的完整状态一致。命令：`tools/run-unattended-test.ps1 -ScenarioId ENERGY-RESET-POWER-ORDER-REAPPLY -HeadlessInstance logic0907 -CharacterId DEFECT -CardId DEFEND_DEFECT -ClearPlayerPiles -EnemyCurrentHp 80 -TimeoutSeconds 120`。首次夹具 `412ac9f548ec45a49bb8e4a05226fa1d` 因直接施加后未结算 Power 变化事件，在 Fork 处失败；补上生产 `ResolvePowerAmountChanges` 后才取得目标失败基线。
- `ENERGY-RESET-POWER-ORDER-OVERFLOW` Passed，`a37f9fb36ac1481db00873d62a59360b`：已有闪电球，依次生成 3 个玻璃球和 1 个闪电球，覆盖满槽激发。逐球状态、敌方伤害、RNG、Power 与资源的完整原生差分及 Fork 通过。命令沿用上条并改 `-ScenarioId ENERGY-RESET-POWER-ORDER-OVERFLOW -ExitOnComplete`。
- `ENERGY-RESET-POWER-ORDER` 基线 `cb45a70d288f46a6afa301c08a721ccb` Failed：先施加 `SPINNER_POWER`、后施加 `LIGHTNING_ROD_POWER`，预测球序为闪电/闪电/玻璃，原生为闪电/玻璃/闪电。最终 `daddd03c11b54f02a99780ddabfcee0e` Passed；反向顺序 `7757429a90964e28b5b827e298e125c3` Passed。命令：`tools/run-unattended-test.ps1 -ScenarioId ENERGY-RESET-POWER-ORDER -HeadlessInstance logic0907 -CharacterId DEFECT -CardId DEFEND_DEFECT -ClearPlayerPiles -EnemyCurrentHp 80 -TimeoutSeconds 120`，反向使用 `-ScenarioId ENERGY-RESET-POWER-ORDER-REVERSE -ExitOnComplete`。
- 上述最终夹具在原生 `Hook.AfterEnergyReset` 上对照完整状态，同场覆盖 Genesis/StarNextTurn/Radiance 的星能、能量和层数变化；验证不同获得顺序具有不同分支指纹及续用文本，Fork 保持顺序。满球槽激发与重新获得能力由本节追加夹具覆盖；原包整场回放仍未运行。
- `REPLAY-START-HISTORY` 基线 `0b32fe311c324ba2a83ff026aff42b26` Failed：带 `GLAM` 的切割原生执行两次，预测 `Y=0/1`、原生 `Y=0/2`。最终 `930567f5a18640c39a78c00363d84474` Passed：完整状态差分、Fork、重新捕获根的零费攻击 2 次、出牌系列 1 次、手动操作 1 次一致。命令：`tools/run-unattended-test.ps1 -ScenarioId REPLAY-START-HISTORY -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -CardsJson '[{"cardId":"SLICE","pile":"Hand","enchantmentId":"GLAM"}]' -EnemyCurrentHp 80 -TimeoutSeconds 120 -ExitOnComplete`。
- `REPLAY-START-HISTORY-ECHO` Passed，`1c5954b23be5482497b42d66e6503899`：两层回响形态下依次打出带重放附魔和普通切割，分别执行 3 次和 2 次；逐动作完整原生差分、Fork 与重新捕获根的两种计数通过。命令：`tools/run-unattended-test.ps1 -ScenarioId REPLAY-START-HISTORY-ECHO -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -CardsJson '[{"cardId":"SLICE","pile":"Hand","enchantmentId":"GLAM"},{"cardId":"SLICE","pile":"Hand"}]' -PowerId ECHO_FORM_POWER -PowerAmount 2 -PowerTarget Player -EnemyCurrentHp 80 -TimeoutSeconds 120`。早期只移除总计数门的方案曾通过普通重放 `3d517d24a8304d0d80dda0d493ecfe85`，静态追踪发现会改变回响形态的系列计数，最终拆清语义并重新验证；不以早期通过结果代表最终代码。
- `DEATH-EFFECTS-ONCE` 基线 `840bc517634f47a3815afc1e95bb4ecf` Failed：使用不同局部集合再次通知同一死亡，尸蛞蝓力量由 4 变为 8。修复后 `f153493cbdb5422fa167c7cf384b9cba` Passed：正式打击回放、重复通知、Fork 后通知及原生打击完整状态差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId DEATH-EFFECTS-ONCE -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId CORPSE_SLUGS_WEAK -CardId STRIKE_IRONCLAD -ClearPlayerPiles -EnemyCurrentHp 6 -TimeoutSeconds 120`。该夹具直接覆盖跨调用集合的重复通知，未重放原报告的完整球动作链。
- 死亡去重的相邻复活边界 `61831e7dc4ec47b985ecc6f8590bf2cd` Passed：`REATTACH_MOVE` 与后续 `DEAD_MOVE` 的直接、Fork、重新捕获根及原生清理前完整状态差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId mercury-reattach-boundary-v0111 -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId DECIMILLIPEDE_ELITE -TimeoutSeconds 120 -ExitOnComplete`。
- `FEED-THORNS-TERMINAL-TWO-CARDS` 基线 `e81a73db90aa499b9481e90087989c76` Failed：狂宴后继续展开防御，报“回放包含已锁定战斗终局之后的动作”。修复后 `1772dde4c0234433b3c0f0ef756916a8` Passed，先防御后狂宴、T1 零损获胜，6 节点/15 转移，增量回放通过。命令：`tools/run-unattended-test.ps1 -ScenarioId FEED-THORNS-TERMINAL-TWO-CARDS -HeadlessInstance logic0907 -CharacterId IRONCLAD -ClearPlayerPiles -CardsJson '[{"cardId":"FEED","pile":"Hand"},{"cardId":"DEFEND_IRONCLAD","pile":"Hand"}]' -EnemyCurrentHp 10 -InitialPlayerHp 1 -InitialPlayerEnergy 2 -PowerId THORNS_POWER -PowerAmount 2 -PowerTarget Enemy -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialUnmirroredCount 0 -ExpectedInitialFirstActionCardId DEFEND_IRONCLAD -ExpectedInitialFinalEnemyHpAtMost 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -ExitOnComplete`。
- `FEED-THORNS-TERMINAL-DIFFERENTIAL` Passed，`4881c50d0c164d8f957bf71df61cf096`：唯一狂宴动作致命反伤后恢复正 HP，模拟保持 Defeat；原生 PendingLoss 结束战斗，结束事件中的完整状态差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId FEED-THORNS-TERMINAL-DIFFERENTIAL -HeadlessInstance logic0907 -CharacterId IRONCLAD -CardId FEED -ClearPlayerPiles -EnemyCurrentHp 10 -InitialPlayerHp 1 -InitialPlayerEnergy 1 -PowerId THORNS_POWER -PowerAmount 2 -PowerTarget Enemy -TimeoutSeconds 120`。首次差分 `9f1fb8000d2a4b38941f661f9aad7ba7` 在敌方已移出 roster 后按下标取敌人失败，修正测试为保留原始 Creature 身份；首次搜索探针 `6d1a4a7f854e4bb5b53999cf07d64328` 因 CardsJson 覆盖 CardId，仅注入防御，不能作为目标验证。
- `DISTILLED-CHAOS-VOID-FORM-BOUNDARY`：基线 `d470307bcc5b445f8f9e7546fb2bd577` 在药水后的 EndTurn Fork 报未结算结束请求；修复后 `bdd86b08c8ea41df963c0031fe8ccdcf` Passed，32 节点/50 转移、1 瓶药水、未镜像项 0，增量回放通过。命令：`tools/run-unattended-test.ps1 -ScenarioId DISTILLED-CHAOS-VOID-FORM-BOUNDARY -HeadlessInstance logic0907 -CharacterId REGENT -CardId DEFEND_REGENT -ClearPlayerPiles -CardsJson '[{"cardId":"VOID_FORM","pile":"Draw"}]' -EnemyCurrentHp 80 -InitialPlayerEnergy 0 -PotionId DISTILLED_CHAOS -PotionPolicyForTest RequireAtLeastOne -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialUnmirroredCount 0 -ExpectedInitialPotionCount 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120`。
- `potion-forced-turn-terminal-v0111` Passed，`3bd9d757e90a4e8a9e90975904a1a651`：唯一药水动作自动打出 `VOID_FORM`，T+1 沙漏击杀的根回放、增量回放、Fork、释放后快照、正式标注及原生清理前完整状态差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId potion-forced-turn-terminal-v0111 -HeadlessInstance logic0907 -CharacterId REGENT -TimeoutSeconds 120 -ExitOnComplete`。本夹具沿用既有强制结束终局差分工具，不运行正式搜索，不加入无效增量搜索开关。
- `ROOT-CAPTURE-ACTION-BARRIER`：基线 `d01150ef49014dc8ba1931a8802992e6` Failed，真实 `BeforeActionExecuted` 期间调用搜索立即建立了搜索会话。修复后 `00f8c4d6237944dbb95061f695edc44c` Passed，队列执行期间不捕获，原生防御结算后延迟请求完成搜索。命令：`tools/run-unattended-test.ps1 -ScenarioId ROOT-CAPTURE-ACTION-BARRIER -HeadlessInstance logic0907 -CharacterId SILENT -CardId DEFEND_SILENT -ClearPlayerPiles -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`。该最小合同没有重建原报告的全部 Mod、后台回收和帕尔军团动画时序。
- 材料预检：`95e19fb8` 报告为 `materials_valid`。原生 `RestoreOnly` 请求 `4f52cb972f4447c5b87dcd4b1b3a9f2e` 因 `environment_mismatch:mods` 失败；本机与原报告 Mod 集合不同，保持严格拦截，未宣称原包恢复成功。
- `SURROUNDED-STATE-IDENTITY` 基线 `bfa9cc597f6647929f193c9aac98f163` Failed：左右朝向产生相同指纹。修复后 `e25cc4a6d62841ed97bcc3b179e1361a` Passed：状态指纹与 continuation 区分朝向、Fork 修改不回写父分支、背击预测为 10/15。命令：`tools/run-unattended-test.ps1 -ScenarioId SURROUNDED-STATE-IDENTITY -HeadlessInstance logic0907 -EncounterId KAISER_CRAB_BOSS -CharacterId SILENT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120`。
- `SURROUNDED-POTION-DIFFERENTIAL` Passed，`99ee091bfceb42509e49cc62b78c3d7d`：虚弱药水转向左侧的 actual/simulated 严格差分与明确朝向断言通过。命令：`tools/run-unattended-test.ps1 -ScenarioId SURROUNDED-POTION-DIFFERENTIAL -HeadlessInstance logic0907 -EncounterId KAISER_CRAB_BOSS -CharacterId SILENT -PotionCheckPath coverage/unattended/power-lifecycle-batch-051-surrounded-potion.json -TimeoutSeconds 120 -ExitOnComplete`。
- Release 编译零警告/错误，Windows 结构门禁通过，默认构建同步本地 mods。该合同与单步差分不替代完整蟹皇战斗或整批报告验收。

## 0.32.0 定版

行为源码沿用下列成长策略与点击外部保存的通过证据，本次仅同步版本和发布文档，不重复行为场景。PR #22 按维护者决定关闭，其强制收益逻辑与精进成长未纳入版本；PR #56 既有适配入口随本版发布。最终产物从版本提交执行 Release 构建并同步本地 mods，发布使用最小 ZIP；未执行完整发布门禁或可见 Steam 人工验收。

## 2026-09-07：成长设置分离与点击外部保存

- “提前结束搜索的战损阈值”回到常规设置的求解器区域；成长侧栏只编辑成长额度。沿用原设置字段及成长优先策略。
- Release 默认构建零警告/错误，并通过项目 `CopyMod` 目标部署至本地游戏 `mods/CombatSolver`。
- `GROWTH-POLICY-FREE-FIRST` Passed，runId `c8c16e7ea30b4c3095709f79f8ab7338`，复跑使用下节同名命令并加 `-ExitOnComplete`。新增 UI 合同通过：成长输入文本设为 7，外部鼠标按下后失焦、SpinBox 应用且设置保存为 7；打开设置后将阈值输入设为 19，外部鼠标按下后失焦并保存为 19。侧栏边界、互斥、配置重载与零损优先成长检查同时通过。
- 测试调用真实面板输入处理器与 Godot 失焦信号，未进行可见 Steam 人工点击验收。

## 2026-09-07：局外成长策略（未发布）

Release 编译 `-p:CopyModOnBuild=false` 零警告/错误；Windows 结构门禁通过（`search_files=74`），两份修改过的 Bash 入口语法检查通过。所有请求使用隔离的 `growth` headless 实例，超时 120 秒，短搜预算 1500ms；搜索测试开启增量回放，时间数据不代表生产性能。

| 场景 | 结果 |
|---|---|
| `GROWTH-POLICY-FREE-FIRST` | Passed，`4545ca8b2d464137a35f567f77ccccd9`。零额度下遗传算法优先于更快的零损击杀；跨回合路线保留成长。八类配置默认值、序列化往返、不可变捕获、侧栏重载/开关/边界/互斥、分支计数隔离与累计额度检查通过。 |
| `GROWTH-POLICY-PAID` | Passed，`047d44398b1341019670ec6f18b0d756`。去掉初始格挡，零额度拒绝付血成长；额度 100 时取得成长且实际比零额度多损血，额外战损未超过额度。比较合同另检验额度边界及超额拒绝，胜利优先于成长。 |
| `GROWTH-REPLAY-COUNT` | Passed，`5075d66cd698428b8bf1fea9137102e0`。遗传算法附魔重放，两次成功成长计数为 2；先成长再击杀，T1 零损；5 节点/12 转移。 |
| `GROWTH-FATAL-PRIORITY` | Passed，`a23b3500f1a548af9564dec9f0e9162c`。贪婪之手与打击都可零损击杀时选择前者，收益次数 1；4 节点/12 转移。 |
| `GROWTH-NO-TARGET-POTION-SENTINEL` | Passed，`3d2008f608604f3785d41f8aead373cd`。无成长目标时仍按强制药水政策使用火焰药水，T1 零损、1 瓶、收益次数 0、未镜像项 0；1 节点/2 转移。 |

复跑入口：

```powershell
./tools/run-unattended-test.ps1 -ScenarioId GROWTH-POLICY-FREE-FIRST -HeadlessInstance growth -CharacterId DEFECT -ClearRunDeck -ClearPlayerPiles -CardsJson '[{"cardId":"GENETIC_ALGORITHM","pile":"Hand","treatAsDeckCard":true},{"cardId":"STRIKE_DEFECT","pile":"Hand","treatAsDeckCard":true}]' -EnemyCurrentHp 6 -InitialPlayerEnergy 1 -InitialPlayerBlock 99 -VerifyGrowthPolicy -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120
# 付费场景使用同一参数，改 ScenarioId 为 GROWTH-POLICY-PAID，InitialPlayerBlock 为 0。
./tools/run-unattended-test.ps1 -ScenarioId GROWTH-REPLAY-COUNT -HeadlessInstance growth -CharacterId DEFECT -ClearRunDeck -ClearPlayerPiles -CardsJson '[{"cardId":"GENETIC_ALGORITHM","pile":"Hand","treatAsDeckCard":true,"enchantmentId":"GLAM"},{"cardId":"STRIKE_DEFECT","pile":"Hand"}]' -EnemyCurrentHp 6 -InitialPlayerEnergy 2 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialGrowthRewardCount 2 -ExpectedInitialFirstActionCardId GENETIC_ALGORITHM -ExpectedInitialProjectedBattleHpLost 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120
./tools/run-unattended-test.ps1 -ScenarioId GROWTH-FATAL-PRIORITY -HeadlessInstance growth -CharacterId IRONCLAD -ClearRunDeck -ClearPlayerPiles -CardsJson '[{"cardId":"HAND_OF_GREED","pile":"Hand"},{"cardId":"STRIKE_IRONCLAD","pile":"Hand"}]' -EnemyCurrentHp 6 -InitialPlayerEnergy 2 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialGrowthRewardCount 1 -ExpectedInitialFirstActionCardId HAND_OF_GREED -ExpectedInitialProjectedBattleHpLost 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120
./tools/run-unattended-test.ps1 -ScenarioId GROWTH-NO-TARGET-POTION-SENTINEL -HeadlessInstance growth -CardId DEFEND_IRONCLAD -ClearRunDeck -ClearPlayerPiles -InitialPlayerEnergy 0 -EnemyCurrentHp 20 -PotionId FIRE_POTION -PotionPolicyForTest RequireAtLeastOne -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialFirstActionPotionId FIRE_POTION -ExpectedInitialPotionCount 1 -ExpectedInitialGrowthRewardCount 0 -ExpectedInitialFinalEnemyHpAtMost 0 -ExpectedInitialUnmirroredCount 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -ExitOnComplete
```

迭代中曾命中原有零损早停；已在成长目标存在时关闭。无格挡且额度 2 的初始夹具未选成长，不能作为免费收益验证，后改为注入格挡和独立付费场景。`7f4963d71e11400aa1f9aaf8ab10beec` 因选择了等待正式搜索结果的停止参数而超时，改为根断言后停止；`e34830eb59864bc280802f82bbd7465c` 因 UI 测试未创建 overlay 失败，测试入口现先创建 overlay。编译期修复了类型名遮蔽、空值注解与测试 runner 实例调用；首次 Bash 路径错误后定位实际安装位置通过。

未做八类卡牌逐一原生部署或本轮 actual/simulated 全量差分；计数合同与增量回放不替代原生结算验收。未做可见 Steam UI 检查、重启游戏后的人工设置回读或性能基准，headless 只证明结构状态与设置序列化。本批未复制开发 DLL 到正常游戏目录，未发包或上传。

## 2026-09-07：文档目录整理

L0 文档检查：64 份资料归类移动，增加 8 份导航；整理后共 103 个文档与数据文件，258 个本地文件链接均可解析，全部文件可从文档总入口到达。源码、编译配置和运行行为未变，本轮未构建或启动游戏。

## 2026-09-07：PR #56 合并验证（未发布）

- Release 编译 `-p:CopyModOnBuild=false` 零警告/错误；Windows 结构门禁通过，`search_files=73`。
- `PR56-CARD-CHOICE-REGRESSION` Passed，runId `efc7007a3dc44012b65dafcb0a3e2ff3`：原版两种可选选牌的空选严格差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId PR56-CARD-CHOICE-REGRESSION -HeadlessInstance pr56 -MonsterMoveChecksPath coverage/unattended/card-on-play-batch-042-choice-zero-optional.json -EnemyCurrentHp 100 -TimeoutSeconds 120 -ExitOnComplete`。
- 未运行第三方许愿的登记委托、三选一实际结算或原生页面部署；原版回归不等于第三方效果验收。本次仅合并源码，保持已发布 `0.31.3` 的产物与标签。

## 0.31.3 定版

PR #49 直接合同在本机 RitsuLib `0.5.19` 上通过全部 10 项：当前真实回调匹配、静态正负查询、live 旁路、动态晚创建、并发、可卸载程序集与模拟后恢复。100000 次缺失类型查询的合同测量为 `18400000 -> 0` 字节，仅表示该查询，不代表整场性能。Windows 结构门禁通过。

本版本收录 PR #49–#55 和已定版 `0.31.2` 元数据。发布源提交 `d71ca2d` 的最终 Release 构建零警告/错误。PR #50–#55 与战前 API 的既有证据见下方；未执行完整发布门禁或可见 Steam 性能 A/B。

- `PR49-FIRE-POTION-0313` Passed，runId `c5c6183f6d424fbd84596ab86e8bef74`：强制火焰药水，Short1500ms，增量回放；1 节点/2 转移，首动作使用目标药水，1 瓶、T1 敌 HP0、未镜像项0。首请求 `3e1a71fc7c24497594eeb81b165475b7` 因空 `CardId` 在建局时报错，改为零能量的防御牌后通过，行为源码未改。
- `PR49-POTION-DIFF-0313` Passed，runId `41a2a580df544528b1585143e5829f3e`：复用同一 headless 进程，火焰药水对敌目标与结算严格差分，最后请求 `-ExitOnComplete` 退出。

```powershell
./tools/run-unattended-test.ps1 -ScenarioId PR49-FIRE-POTION-0313 -HeadlessInstance release0313 -CardId DEFEND_IRONCLAD -ClearPlayerPiles -InitialPlayerEnergy 0 -EnemyCurrentHp 20 -PotionId FIRE_POTION -PotionPolicyForTest RequireAtLeastOne -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialFirstActionPotionId FIRE_POTION -ExpectedInitialPotionCount 1 -ExpectedInitialFinalEnemyHpAtMost 0 -ExpectedInitialUnmirroredCount 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120
./tools/run-unattended-test.ps1 -ScenarioId PR49-POTION-DIFF-0313 -HeadlessInstance release0313 -PotionCheckPath coverage/unattended/potion-batch-044-fire.json -TimeoutSeconds 120 -ExitOnComplete
```

## 2026-09-07：PR #50–#55 合并验证

本轮验证六条 PR 合并后的行为源码。Release 编译零警告/错误，Windows 结构门禁、Git Bash `bash -n tools/run-unattended-test.sh`、CoverageCatalog `--verify-effective --verify-pre-play-choices --verify-combat-choices` 均通过。覆盖目录检查限原版目录，生成的时间戳变化未提交。

| 场景 | 结果与证据 | 复跑参数（共同使用 `tools/run-unattended-test.ps1 -HeadlessInstance pr50-55 -TimeoutSeconds 120`） |
|---|---|---|
| `PR50-55-GAMBLERS-REGRESSION` | Passed，runId `3fc6e575501d4b9596576c5167466cdb`；赌博药水弃牌与补抽严格差分 | `-ScenarioId PR50-55-GAMBLERS-REGRESSION -PotionCheckPath coverage/unattended/potion-batch-045-gamblers.json` |
| `PR50-55-OPTIONAL-CHOICE` | Passed，runId `28167ce3490c41d2bf04bc603732c637`；两种可选选牌空选的严格差分 | `-ScenarioId PR50-55-OPTIONAL-CHOICE -MonsterMoveChecksPath coverage/unattended/card-on-play-batch-042-choice-zero-optional.json -EnemyCurrentHp 100` |
| `PR50-55-CLASH-PLAYABILITY` | Passed，runId `376b830cdecb499aa4a9c0fe9a7a527e`；先出防御再出 Clash，2 动作、2 节点/4 转移、T1 零战损、未镜像项为 0，增量回放通过 | 见下方完整参数 |

```powershell
./tools/run-unattended-test.ps1 -ScenarioId PR50-55-CLASH-PLAYABILITY -HeadlessInstance pr50-55 -TimeoutSeconds 120 -CardId "" -ClearPlayerPiles -CardsJson '[{"cardId":"CLASH","pile":"Hand"},{"cardId":"DEFEND_IRONCLAD","pile":"Hand"}]' -EnemyCurrentHp 10 -InitialPlayerEnergy 1 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialFirstActionCardId DEFEND_IRONCLAD -ExpectedInitialExecutableActionCountAtLeast 2 -StopAfterInitialSolverResultAssertion
```

首次 Clash 请求 `990db5f805574a8c8c050d44a0fa136a` 因同时要求卡牌 ID 与标题的测试参数被单独使用而失败；改为首动作与动作数断言后通过，行为源码未改。首次 Bash 语法检查使用了不存在的安装路径，定位本机 Git Bash 后通过。

上述用例证明原版相关通道回归通过。第三方战略估值委托、形态药剂、预视弃牌和未登记第三方可打出条件的专属夹具，本轮未执行；PR 作者提供的观者结果仍为作者历史证据。未运行可见 Steam 联动或完整战斗回归。测试实例已停止。

## 2026-09-07：Ritsu目标类型查询缓存

10项直接合同覆盖静态正负查询、live旁路、动态晚创建、并发和程序集卸载。固定0.31.0研究基线的Headless目标分配−34.4%、可见Steam−33.1%，完整动作/126非时序字段一致；Aeon哨兵行为一致但分配+4.5%，保留未解释限制。当前PR基于main `0552b33`，不将历史A/B标为新上游政策下的结果；详见 [验收与复现](../performance/metadata-target-type-cache-20260907.md)。

## 0.31.2 定版

收录 PR #15、#18、#43。本次仅修改版本与发布资料，沿用下列已完成的定向验证，执行最终 Release 构建；未追加完整发布门禁或可见 Steam 双 Mod 联动验收。
## 2026-09-08：SeedOracle 规划快照 API v6

伴生 SeedOracle 的 `smoke/run-planning-smoke.ps1` 调用真实独立 worker。`PLANCOMBAT01` 普通战斗、`PLANCOMBAT02 -SimulationCase unknown` 问号点战斗、`PLANCOMBAT03 -SimulationCase event -SimulationEvent DenseVegetation` 多页事件回血后战斗，均 3/3 完成且严格恢复检查通过；样本结果可写回规划，主进程状态/RNG/地图指纹不变。单样本短搜 2000ms、整体 30000ms，批次上限 120 秒。

报告保存在伴生仓库 `docs/validation/planning-combat-2026-09-08.md`。没有逐个端到端验证所有事件或作可见 Steam 性能结论；失败或未完成的样本不作为规划参照。

补测 `PLANCOMBAT04` 木偶事件战斗后原生 Resume、奖励重放与 `PLANCOMBAT05` 普通战斗各 3/3 完成；累计 15 个样本。伴生面板 Debug 自检通过。完整 AutoSlay 在 120 秒内未完成，未计为完整跑局通过；无头正常退出仍报告 Godot 资源释放告警。

## 2026-09-06：PR #43 集成

`PR43-PRECOMBAT-API-INTEGRATION`，runId `d44c83b14da04695b79f218e5056d32d`，68.0秒 Passed：规范化恢复、独立 Mod 文件、设置令牌、取消、确定/假设预测、2次 worker 创建和3次复用、静音与主跑局不变。`PR43-EXIT-CLEANUP`，runId `b2d1e2d25beb47eeb976f8062657a4a3`，18.9秒 Passed，另查正常退出后 startup-mods 已清除。Seed Oracle main `29cee875` 对新 DLL 编译通过。详见 [审查记录](../pr/pr43-review.md)；未执行可见 Steam 双 Mod 联动。作者原 0.29.x 测试数据保留为历史证据。

复跑：`tools/run-unattended-test.ps1 -ScenarioId PR43-PRECOMBAT-API-INTEGRATION -HeadlessInstance pr-api -VerifyPreCombatForecastApi -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -StopAfterInitialSolverResultAssertion -HeadlessFastModeForTest Instant -TimeoutSeconds 120 -ExitOnComplete`。Bash 对应 `--verify-pre-combat-forecast-api`，其余使用现有 kebab-case 参数。

## 2026-09-06：PR #18 第三方 OnPlay 边界

`PR18-FOREIGN-ONPLAY-BOUNDARY` Passed，runId `c374c02c64a34423b8f99a1da976a7b0`：真实 Harmony Prefix 安装/卸载，覆盖首次根捕获后新增补丁、已声明非玩法来源、未知来源、移除补丁后的正常捕获及 live 状态不变。既有 `PredictionFailureBoundaries` 和首结果增量短搜通过，3 节点/11 转移、无药零损 T1。Release 编译零警告/错误。夹具 `coverage/unattended/pr18-foreign-onplay-boundary.json`；未逐一覆盖 Prefix/Postfix/Transpiler/Finalizer，也未运行完整战斗或可见第三方 Mod 组合。

## 2026-09-06：PR #15 药水分档

`PR15-POTION-VALUE-TIERS` Passed，runId `11959921f03041e9a9f6fe7001023315`：校验 9/14/18 HP 准入门槛、Token/可再生免费、龙涎香独立计价、救命和强制用药，以及根快照中的高档成本。Short1500ms、增量验证、首结果停止；3 节点/11 转移，无药零损 T1。Release 编译零警告/错误。夹具 `coverage/unattended/pr15-potion-value-tiers.json`，不代表静态分档在所有情境都优于原规则。

## 2026-09-06：SL 路线记录

- `ROUTE-CACHE-RECORD-V0111`：`47626b91d2834703a403819e5ef2ae2e` Passed，验证独立磁盘副本、动作/选择/预测一致、策略与真实 HP 变化隔离、首次记录保留、Reset 后命中及手动重算。
- `ROUTE-CACHE-RESTORE-V0111`：新游戏进程 `5a23389a9b8e4ed485b28a912711974b` Passed，从上一进程文件恢复路线并于 T2 完成原生部署；部署过程断言没有额外搜索。结果协议的节点/耗时仍是被恢复路线的历史指标，实际恢复事件和搜索次数由 `ROUTE_CACHE_HIT` / `restored` 审计记录。
- 回合开始原生选牌：`70c2a626c1614211b05b867beac7588b` 记录、`9085ab68abd14ae49333cb51327ebd28` 新建战斗后恢复，均 Passed；恢复项断言 `InitialRouteCacheRestore`、`TurnSetupNativeChoiceOrder` 和界面恢复状态。夹具沿用 `initial-gambling-chip-397.json` 的遗物与断言，将 seed 固定为 `ROUTECACHESETUP031`，scenario 分别设为 `ROUTE-CACHE-SETUP-RECORD-V0111` / `ROUTE-CACHE-SETUP-RESTORE-V0111`，同一实例依序运行。
- 夹具：`coverage/unattended/route-cache-record-v0111.json`、`route-cache-restore-v0111.json`。在同一 headless 实例和同一 DLL 上依序运行，第一项退出进程、第二项重新启动。固定 seed `ROUTECACHE031`、敌 HP12、起始能量1、Short1500ms、Instant/0秒、每请求120秒上限。
- 早期测试两次失败来自夹具：首次删除尚不存在的缓存目录，以及未启用无人测试的后续回合自动搜索。均修正后取得上述证据。原生保存菜单和各快速 SL Mod 的按钮未逐项操作验证；这里验证同根跨进程重建和会话生命周期恢复。
- Release 编译零警告/错误、Windows 结构门禁通过。未运行 Linux 游戏或可见 UI 验收。
- 缓存命中场景使用普通搜索模式；增量语义验证及阶段性能测量显式跳过缓存读取，保证它们实际执行搜索。最后只补充了该测试模式准入条件，普通恢复的行为证据沿用上述结果。

## 0.31.0 定版

收录九个玩家 PR，逐项说明见 [0.31.0 更新日志](../../releases/0.31.0-RELEASE_NOTES.md)。本次仅变更版本和发布资料，沿用下列本会话已完成的集成与碎骨定向验证，执行一次最终 Release 构建；未追加完整发布门禁或可见性能验收。

## 2026-09-06：PR #48 碎骨

`BONE-SHARDS-OSTY-REPLAY-0300` 通过，runId `a6540dba59014aeb8ee79f00dee4f050`：奥斯提 10 HP，连续打出两张碎骨，逐动作 actual/simulated 严格差分一致；第二张不会额外加盾。夹具 `coverage/unattended/bone-shards-osty-replay-0300.json`。Release 与 CoverageCatalog `--verify-effective` 通过；未单独构造攻击触发待选牌的碎骨场景。

## 2026-09-06：八个 PR 集成验证

本轮构建、结构门禁、容器与 GC 合同、Windows headless 资源隔离、失败边界、Fork/根/控制器、DOP1/DOP2、终局增量回放、长循环与保命遗物完整自动部署通过。[直接证据与失败修正](../pr/integration-39-47-review.md) 单独记录，不覆盖下方原 PR 历史证据。新增可重跑夹具：`coverage/unattended/pr-integration-lizard-tail-rescue.json`。

> 当前发布：CombatSolver `0.31.0`、塔 2 `0.111.0`、RitsuLib 实测 `0.5.18`（清单最低 `0.5.13`）、CombatSolver 内置战斗模拟引擎。下方历史版本记录保留各自验证范围。无人测试运行隔离的原版 `--headless` 游戏进程，不使用自建 STS CLI；性能最终门槛另由 Steam 可见会话验证。完整战斗基准使用 `Instant / 0 秒` 部署。

单项启动器使用本 worktree 的构建产物与私有游戏/Mod 快照，各实例独立保存 Windows APPDATA/LOCALAPPDATA 或 Linux XDG 数据及协议。内容变化只重启当前精确认领的实例，不能按进程名结束其他任务。实例目录/主机租约由平台 `headless-runtime` helper 管理，请求及静稳 Ready ACK 仍由原启动器管理。默认 exclusive；双方显式 parallel 时，主机资源允许最多两个游戏。预约是准入记账，不是硬配额，暖进程与 Held 仍占名额。批次最后一个请求须 ExitOnComplete；取消、Failed、超时清理本实例。Linux 默认 Instant；Windows 可显式 `-HeadlessFastModeForTest Instant`。参数、资料隔离、队列规则与检查入口见 [Headless 实例与并行测试](../../HEADLESS_TESTING.md)。并行样本不能用于单场速度、GC 暂停或峰值内存 A/B。

单项启动器未请求退出时会保留 marker 精确持有的 headless 游戏，供身份兼容的请求复用；完整矩阵遵守有界生命周期组。当前实例、产物冻结、并行预约与参数见 [Headless 测试隔离](../../HEADLESS_TESTING.md)。游戏/Mod 内容、私有数据目录、实际可执行文件和进程出生身份共同约束复用，不仅依赖 PID 或版本号；Linux 另核对进程环境。只有异步工作静稳且收到匹配 `schemaVersion/runId/held` 的 Ready ACK 才能复用；Failed、超时或取消只清理精确认领的进程。Windows 使用私有 APPDATA/LOCALAPPDATA，Linux 使用私有 XDG；两端关闭 Steam，从各自冻结快照加载 RitsuLib，不临时写入源游戏目录。Linux 默认测试速度 Instant，Windows 可显式 `-HeadlessFastModeForTest Instant`。并行数据不作为单场性能 A/B。

## PR #45 原分支验收摘要（2026-09-06，历史证据）

用户本次明确允许少量战损或回合数回退，停止为恢复全部旧回合目标继续试验；验收重点是通用正确性、避免明显战损及严重性能退化。最终灵魂枢纽、Phantasmal、受感染棱镜相对原历史样本分别多损 2、5、2 HP。按用户允许小幅回退的要求，本次以所测样本最多 +5 HP 且存活获胜进行验收，并披露实际差距；并非用户指定了精确 5 HP 阈值，也不是全部场景不退化。外骨骼虫仍零损 T10，旧零损 T5 不再作为必达目标。Smart 药水价值门槛与可接受战损停止规则保持上游政策；不为追求旧零损强制额外用药。下方历史 FAIL、实验撤回和暂停结论仍按当时标准保留；协议 Passed、模拟回放通过、首结果质量、实际部署与性能证据互不替代。

最终行为源码 `6ea7dc4` 已正常合并 `upstream/main` 的 `b04d3ec`；任务改动提交 `3864f2c`。全部上游 worldline 回滚已接受，移除失去 latent 消费者的 `AttackPlays` 闭包及专属 `StrategicContext` 测试并恢复上游惰性 `Build`。最终收敛阶段只做上游集成，不再追加策略实验；v77 结果只属于合并前源码。

| 最终集成验证 | 当前直接证据 | 结论与限制 |
| --- | --- | --- |
| Release / 结构 / 入口解析 | `6ea7dc4`：Release 7.79 秒、零警告/零错误；Bash / PowerShell 66 文件门禁、PowerShell launcher 解析通过 | 静态与构建通过，不能等同完整行为门禁 |
| 定向语义与搜索 | 原 22 项为 21 Passed / 1 Failed；Kaiser 修正预期的独立请求随后 Passed | 22 项适用用例通过；唯一旧失败是 T2 必须复用断言，原 Failed 保留，不能改称原批次 22 Passed |
| 正常可见 Steam | 未验证性能；`bad1543e22c54a2a966179e3d98e498a` 根捕获被现有 Ave Mujica subscriber 保护拒绝，Solve 未开始，launcher 120 秒超时退出 1 | 非 headless、真实正常聚焦窗口；120 秒不是搜索耗时。兼容阻塞不作性能通过，不绕过现有保护 |

### 最终定向结果（行为源码 `6ea7dc4`）

以下数据取各请求结果 JSON；除明确标注原版部署/差分的项目外，质量为正常搜索首结果。展开/转移/选择均为请求级总工作，而非选中 solver 指标；不把参数标为 DOP2 或聚合字段相同自动等同于逐转移、完整动作或性能 A/B 证明。

| 项目 | 结果与 runId | 证明边界 |
| --- | --- | --- |
| 策略合同 | Passed `68de2db369034ba7aebb9126140f7516` | 实际执行 SearchPolicySnapshot 合同 |
| Kaiser 嵌套边界 | Passed `9f490fedaf5a4b0691b4cdd949c96bd0` | 最小嵌套边界，不是整场部署 |
| 长隐藏相位 / 长增长伤害 | Passed `f3a76c24d4d14d988e610fd9f2b6fb7e` / `b6cc4ef97305421da996a5974a1ad9de` | 1200 动作/1200 洗牌与 892 动作/445 洗牌，均预计零损 T1；请求工作分别 1200/2400/0 与 1784/3570/2 |
| 同回合停滞 DOP1 / DOP2 | Passed `3d719ea0f46c4b44bd8fd057bd312d97` / `6c9c76e6ea7c4a73975519c30bc6ec14` | 均 10/20/0 后有界停止，不选空转；不是用零损终局证明停滞无限正确 |
| 有限成长 / 低损优先 | Passed `385a23bdc02048fdb15aa8542b09ac1b` / `8f711cc1874e424781f7e2e7f60cd917` | 前者 32 动作、64/130/2；后者 20/57/0、零损，不采用卖血动作 |
| 跨回合正例 / 停滞对照 | Passed `1ccb5c0066a14a729a2ce7fafc795c9a` / `dd6cf5c2f2734e6cacfc4c8e6f3ea857` | 正例 513/770/0、预计零损 T17；对照 78/117/0、敌 HP57 保留且不选无收益防御 |
| Persistent DOP1 / DOP2 | Passed `7e2d2545c85f4f46b8cf03593436567d` / `57815552b7674413a688596863c34d3a` | 均零损 / 6 HP / T1 / 零药、39 动作、4439/17886/4190；DOP2 实际最大并发 2 |
| 灵魂枢纽 DOP1 / DOP2 | Passed `5e399067e94849ec90b2a0c27a970905` / `85d3c75cfb2b42cd8904aa340e04f75b` | 均损 3 / 95 HP / T7 / 1 药、9919/67097/32140；相对原损 1 多 2 HP，接受；不是 v77 的损 1 / T4 / 零药 |
| 自定义战斗正常搜索及原版部署 | Passed `545078fcfef5433c93a2f742a320a02b` | 实际 T1、损 3 / 1 HP / 1 药 / 7 洗牌 / 19 动作，UnexpectedReplans=0、Instant 速度恢复通过；总 19859/66214/7910，不能用选中层 1820 展开隐藏请求总成本 |
| 外骨骼虫 | Passed `48e0f5095716411f8203b2a65792afe1` | 损 0 / 97 HP / T10 / 零药、14672/157206/97502；T10 作为已知限制接受，未找到旧 T5 |
| Phantasmal | Passed `bc980a2eb8044d71acf92b5f4f49f5cc` | 损 5 / 5 HP / T8 / 零药、10230/124024/83377；对旧零损 T3 / 1 药多 5 HP、少 1 药，按本次放宽口径接受，不回写旧严格门槛 |
| 受感染棱镜 | Passed `f82f35236d2f4cb6bbcb5db9e75f2306` | 损 6 / 54 HP / T6 / 1 药、18441/125939/64932；对旧损 4 / T5 多 2 HP，接受 |
| Kaiser 原整场请求 | Failed `ee51e6f651244ef6b89b57285d4c1a9f` | 实际 combatEnded=true / T2；仅旧“第 2 回合必须复用”断言失败。预测为 12 张 T1 牌及 EndTurn，T2 开头 Mayhem 自动出牌已胜，无 T2 PlayCard；searches=1、reused=0、各 Unexpected=0，不是状态漂移或计划外重算 |
| Kaiser 终局部署修正验收 | Passed `f220b8f2822942e28fa48d00093597d9`，实际 combatEnded=true / T2、UnexpectedReplans=0 | 仅去掉 expectedReusedTurn 与对应复用 HP 断言；原输入/预算、expectedFinishedPlayerHpAtLeast=80、expectedFinishedTurnAtMost=2、expectedUnexpectedReplansAtMost=0 全部保留且通过。独立请求不覆写上行 Failed |
| 永世沙漏 | Passed `2cb489b2b8ad4a9d9d5ccfc869f0d7dc` | 损 14 / 40 HP / T7 / 零药、32136/641116/488387；优于原历史损 33 / T9，首结果并非原版整场部署 |
| 长线 4 GB No-GC / 常规 GC | Passed `224b2d2f1c1247f7a20bd085c8a82a0d` / `996f5a6169b342bc9fe1442265adedf5` | 两者均损 0 / 65 HP / T9 / 零药、7097/41023/18723；质量和请求总工作相同，不因此声称完整状态等价 |

### Headless 性能严重退化筛查

时间为请求级搜索计账时间，GB 为十进制累计 worker 分配，不是端到端墙钟、存活堆或进程峰值。当前与历史记录的源码、路线、工作量及冷暖/GC 条件并非全部受控一致，以下只用于排除所测样本的明显失控，不宣传性能收益百分比或受控加速倍率；可见 Steam 另列证据。

| 样本 | 本轮 秒 / GB | 历史对照与限制 |
| --- | --- | --- |
| 灵魂枢纽 DOP1 / DOP2 | 17.386 / 3.740；9.610 / 3.743 | 原 v9 分别 20.357 / 3.009、17.236 / 4.528。合并前 v77 DOP1 仅 3.828 / 0.730，本轮明显慢且分配更多；上游 worldline 已回滚，路线和工作量已变，不隐藏代价，也不称同工作量退化归因 |
| 自定义战斗 | 2.877 / 2.429 | 包含失败/恢复/药水层的全部请求工作；本轮实际部署通过，不取选中 solver 的较小指标冒充总值 |
| 外骨骼虫 | 15.989 / 8.820 | 原 v9 17.698 / 7.032；v77 25.165 / 12.507。当前分配仍高于原 v9，不声称所有维度改善 |
| Phantasmal | 9.365 / 6.562 | 原 v9 14.921 / 6.399；本轮战损多 5、药量少 1，不能当同质量 A/B |
| 受感染棱镜 | 10.822 / 6.905 | 原 v9 19.592 / 10.538；本轮战损多 2，不能称无质量代价优化 |
| 永世沙漏 | 73.548 / 37.642 | 原历史 84.281 / 41.718、损 33 / T9；本轮损 14 / T7，仍是高分配长搜，不推广为低内存或无卡顿 |
| 长线 4 GB No-GC / 常规 GC | 16.511 / 5.578；29.178 / 5.570 | 原同根 v0272 72.151 / 27.102、97.927 / 27.161，均损 9；当前均零损，搜索工作不同，不宣传加速倍率 |

本轮长线 4 GB No-GC 的累计/最大 GC 暂停为 `125.682 / 61.900 ms`，常规 GC（No-GC 关闭）的对应值为 `12213.517 / 180.877 ms`。两侧请求工作一致，仅记录本次观察，不等于重复受控基准，也不替代可见帧卡顿与峰值内存测量。

### 正常可见 Steam：现有兼容保护阻塞

runId `bad1543e22c54a2a966179e3d98e498a` 由真实 Steam AppId `2868840` 启动，PID `409793`，X11 `WM_CLASS=Slay the Spire2`、`WM_STATE=Normal` 且聚焦，没有 `--headless`。约 78.67 秒进入初始结果断言前发生 `SEARCH_SETUP_FAILURE stage=combat_root_snapshot`：`AveMujica.AveMujicaCode.Ftue.DreamspinFtue` 是尚未支持的 Ave Mujica gameplay ModHelper subscriber。对应 `PredictionModHookSubscriberCapture` guard 相对上游无改动；搜索根未建立、Solve 未开始。

launcher 在 120 秒超时退出 1，不是 solver 搜索 120 秒，也不构成可见性能或卡顿通过。玩家原 DLL 与 manifest 已由清理恢复并记录 `PLAYER_MOD_RESTORED`，完整游戏日志保留。本次不改变兼容策略、不绕过保护、不扩大第三方适配范围。正常可见性能仍未验证；headless 数据仅用于严重退化筛查。

据用户放宽后的质量要求、定向行为结果和上述 headless 基准提交正式 PR，公开质量差距与可见性能限制。完整发布门禁、全量 CoverageCatalog 和 Windows 原生游戏验收未在本轮完成。旧版最小事务/Fork/终局差分、v52 GC 合同与固定工作量 A/B、v67 恢复合同、v66 自动部署均按各自源码阶段引用。PR 交付说明见 [通用搜索与正确性后续 PR](../pr/generic-search-correctness-followup.md)。

### 合并前 v77 摘要（历史证据）

| 项目 | 合并前直接证据 | 结论与限制 |
| --- | --- | --- |
| v77 L0 / 策略合同 | Release 零警告/零错误；Bash / PowerShell 66 文件门禁；`373de14258414197b22e534152302b47` | 通过。验证普通同分排序的完整政策标签隔离与既有路由位置不动，不等同完整行为门禁 |
| v77 灵魂枢纽 DOP1 / DOP2 | `45cd4d4d81ee42498d9efd83e9e8cf9f` / `feefa42bb2d54ea99b1eb8ac08e4e54d`；均损 1 / T4 / 零药 | 原配置首结果通过；2112/13480/6996 工作量、80 项结果字段、26 日志动作、4 回合结果一致。不同 GC/冷暖条件，非性能 A/B、逐转移严格等价或本轮整场部署 |
| v77 外骨骼虫 | `00d1a845cfa74866a4f7e12910db2e18`；损 0 / T10 / 零药，13746/208100/147857 | 通过当前首结果哨兵；T10 为用户放宽后接受的质量限制，不声称旧 T5 已恢复 |
| v77 自定义战斗 | `9bc0a475157246e4a351a149e5ca012b`；损 3 / T1 / 1 药 / 7 洗牌 / 19 动作 | 原配置首结果通过；请求 28726/97786/10486，不把选中 solver 工作量当全部工作。旧 v66 部署证据并非本轮复跑 |
| v77 有序派生键清理 | `02354eecb8c145368efdf5ca083c3f08`；26 基础前缀与五个牌序变体 | 完整/增量与根/live 不变通过；完整 StateKey / Continuation 保留。不启动 Solve，不作独立性能收益结论 |

以上均为合并前证据，不构成 `6ea7dc4` 的最终验证；历史质量限制也不能覆盖合并后产生的新结果。

## 通用搜索、语义与 Headless 验证记录（开发中）

以下逐项记录保留对应 vN 阶段的证据与当时判定；当前交付口径以上方摘要为准。v66 同回合落选续搜已通过 Custom 首结果与实际部署，v67 已补边界合同。仅现有失败窄搜开启；前缀根/动作工作计入原节点预算，不能把单出边回放当成新的完整节点展开。

| 验证 | 当前证据 | 边界 |
| --- | --- | --- |
| 无保留路由普通同分排序 | v77策略合同 `373de14258414197b22e534152302b47`；Soul `45cd4d4d81ee42498d9efd83e9e8cf9f` 正常1损/97HP/T4/零药 | 原20秒/DOP1/GC、2112/13480/6996，旧质量审计通过；同Turn/full6D各自原位置排序，带旧保留路由的组内位置固定，不改必保/席数/预算。Release与两端66门禁通过，非本轮部署证明 |
| v77 Exoskeletons非退化哨兵 | `00d1a845cfa74866a4f7e12910db2e18` 零损T10、13746/208100/147857，当前v66基线审计通过 | 原VeryHigh/DOP8/NoGC16/Smart；旧T5质量审计仍失败，不把当前非退化当作全部目标达成。headless共享进程数据不作最终性能结论 |
| v77 Soul DOP1/2正常一致性 | DOP2 `feefa42bb2d54ea99b1eb8ac08e4e54d`，同1损/97HP/T4/零药和2112/13480/6996；实际最大并发2 | 80项非时序/非调度RESULT、26条日志动作、4条回合结果及政策路线一致；DOP2 NoGC4保持/rollover0。原断言、GC和冷暖配置不同，非性能A/B或完整PlanAction字节等价 |
| v77 Custom恢复非退化 | `9bc0a475157246e4a351a149e5ca012b` 原配置短质量通过，3损/1HP/T1/1药/7洗牌/19动作 | 获胜solver仍10742/39529/3802，请求28726/97786/10486略变；不宣称全部工作量等价，未重复v66原生整场部署 |
| 实验有序派生键清理 | v77 `02354eecb8c145368efdf5ca083c3f08`，26基础前缀及五变体第11/12步完整/增量、根/live通过 | 生产只去掉实验派生哈希及其冗余核验，完整StateKey/Continuation不变；Testing对每个已见变体逐一比较四牌堆token数组，不反向代替通用状态键。不启动Solve，未测量独立性能收益 |
| 新鲜生成粗族单席补位（否决实验） | v75 `7610a80fe425460c8917dfa14689a2ca` 合同通过；正常Exo `65db7899b0424deba05e9eca2f0058ad` 损1/T4 Failed | Release零警告错误、两端66门禁通过；4803/99681/75856，原VeryHigh/DOP8/NoGC16/Smart。提前结束不能代替零损目标；完成拒绝版诊断后已撤回生产因素及专属合同/门禁 |
| v75拒绝版第4步保留及第5步断点 | `5196a01b963a4e558a1808160c6bb00b` 6407事件NoDrops，完整四敌/根/live证明通过 | 4 raw2000→selected90→Final→Expanded，旧required/routing/容量不变；5生成/准入后Prune11丢失，该次不含5整池。诊断工作量同正常4803/99681/75856、仍损1/T4；不作质量或性能通过 |
| v76拒绝版第5步完整候选池 | `46ec3b33c3e34e889448b1b557f429b9` 3997事件NoDrops，四敌24前缀/根/live通过 | 目标raw257无必保/路由/选中；135全局选中均在153最终集合。33节点来源族有11个最终存活，完整生成签名未变；同父攻击敌3/2存活，敌1/4落选，战术前三键相同但防守投影不同。仍损1/T4，不支持延长生成保护；生产v75已撤回 |
| 普通同分截线同政策战术排序合同（已撤回实验） | v74 `f82e142ca2e64d52933ceeda728c1491` SearchPolicySnapshot通过，Release零警告错误、两端66门禁通过 | 真实9/7目标换入、完整6D+Turn逐维交错隔离、稳定词典序、必保原位及全部既有旁路；邻接质量回退后，仅撤回本因素及专属合同/门禁 |
| v74 Soul目标通过但外骨骼虫回退（已撤回） | Soul `72efaeef12f64c2b85b0a83b08120b64` 1损/T4通过；Exo `de76b95d766e4398b444010ed98421b0` 0损/T11，质量审计失败 | 原v9输入/预算/Smart。Soul2112/13477/6994；Exo14485/204622/142069，相对当前v66的0损T10回退且旧T5未达。无观察器，只验首结果，不是实际部署或Steam性能结论；未跑DOP2/全矩阵 |
| Soul实际换序代表与第18步整池 | v73 `8591487e906b426383ed306696ba6ecb` Passed，1406事件/1StrictAliasAnchor | 真实18前缀+原样末8步逐步完整/增量、根/live通过，1损97HP/T4；18 raw35无routing，普通同分块9选7遗漏目标。只证明战斗后缀及实际剪枝，不证明调度等价；原质量仍19损T7。Release零警告错误、两端66门禁通过 |
| 外骨骼虫第4步真实整池 | v72 `2bef44a967284a4c9bf39e9404b4295d` Passed，6400事件NoDrops | 真Exo solver第10边界：3109排序、91必保/135限额、96路由/54配额；目标raw2000无routing/required/selected/Final。完整保留池连续索引通过。复用进程日志须按Sample/solver分开；原质量0损T10、13746/208100/147857未变，非性能证据 |
| 五生成上下文完整后缀与联合路径观察 | v72 `adb95fc0a6f046afbe3a33ec1278b19f` Passed；五条26步各1损/97HP/T4/零药，626事件NoDrops | 仅第8步重绑定，后续全部冻结；完整/增量及根/live通过。按完整动作与实测政策分桶，防御变体12真实routing10/quota13、selected47并展开，准确展开到14；15为TT拒绝且有同状态别名，不作全路径丢失结论。正式搜索仍19损T7；Release零警告错误/两端66门禁通过，无生产保路变更或性能结论 |
| v71全部选牌输出细分普通席（已撤回） | 正常 `bf3b1d42f48344ec9e27c0c2d9873635` 损43/T11；诊断 `48fd2f875573474d8ca7739a8f900a1f` 第11步整池断言Failed | 正常主6926/45680/22819，请求8480/54547/25635原20秒；诊断110事件，两solver准确1–7 Expanded，8准入后Prune丢失，11未到达。仅通过Release/两端66门禁，未跑新静态合同/Exo哨兵；全部357行新普通席因素已撤回 |
| 外骨骼虫已知早胜路线原版严格对照 | v70 `9c8e6cf093bd40aa8149e9d225d66c44` 通过，实际97/103HP、0损、0药、T5 | `KNOWN-EXOSKELETONS-ROUTE-NATIVE-V0111`：24完整预测先冻结；24原版动作、6Primary/4Nested/4EndTurn逐敌StateDiff/Continuation、阵容/死亡/行动及累计伤害/药水/洗牌事件一致。真实清理前四敌取证并等待CombatEnded；不Solve、不代表生产UI部署。最终Release零警告错误、两端66文件门禁通过 |
| 外骨骼虫已知早胜路线首次丢路 | v70 `f538b44ac3d045f8a07a01f563abe7cc` 27事件NoDrops；准确第4步首次Prune丢失 | 原策略DOP1/NoGC16；1–3真正Expanded，4横祸完整Nested已Generated/TT接受/动作准入但无PruneFinal。实际仍0损T10、13746/208100/147857，与v66工作量相同；诊断非质量或性能通过，下一次整池锚点设4 |
| 生成上下文普通席合同（已撤回实验） | v69 `e4981f5eb9494a26bcd65cc438849e78` SearchPolicySnapshot通过，Release零警告错误、两端66文件门禁通过 | 原路由/必留/额度未变；替身合同验证无碰撞旁路、完整标签隔离、必留槽位、细上下文去重、公平与确定性。因真实质量未达标，筛选及专属合同已撤回 |
| v69普通席Soul质量与拒绝版诊断 | 正常 `872c23e436754eb9af4a1ff2c3272513` 损17/T11；诊断 `4c30e68dcffd4b4fbd5a857a952584a3` 首次丢失准确第12步 | 正常主7399/69026/43629，请求7964/72340/44590耗满原20秒；诊断333事件NoDrops，11从普通席selected38真正Expanded，12生成/TT/动作准入后Prune丢失。无12整池原因证明。诊断损24/T7受时限影响，不当作正常质量或性能证据；未跑Exo哨兵 |
| 新有序牌堆键真实上下文合同 | v69 `ab2936c3280941148ff2b9d19d4f8a92` 通过 | `KNOWN-SOUL-GENERATION-CONTEXT-V0111`在第11/12步各得到五个不同有序键、相同无序键；26已知前缀及五变体完整/增量、根/live不变。无Solve或原版动作 |
| 外骨骼虫早胜约束完整回放 | v69 `9739c9b9d20f4924986e8ba0357935cf` 全24步通过，模拟97HP/0损/T5/0药 | v9的24步约束加同导入根v31真实生成候选的第4步4Nested；6Primary/4EndTurn，逐敌完整/增量、阵容与死亡账本、root/live不变。不是v9 PlanAction字节恢复，不是原版或搜索发现证明 |
| Soul生成上下文具体牌序 | v68 `8fc546a16f184e5f95c08a3b1d42e6bc` 通过，26已知前缀及五变体11/12步完整/增量、根不变 | `KNOWN-SOUL-GENERATION-CONTEXT-V0111`；差异仅Hand语义token顺序，其他三堆相同；同一冻结过牌动作后仍如此。不Solve，不把差异本身视为胜负或调度证明 |
| 外骨骼虫旧零损早胜约束重建 | v68 `34f71157c08f4711a02e4d842e86ab0c` 前3/24步通过，第4步横祸因未知Nested明确失败 | `KNOWN-EXOSKELETONS-ROUTE-REPLAY-V0111`，逐敌完整/增量及根不变；Source=CATASTROPHE/Hand/AutoPlayRepeated/必选1/四候选，旧ACTION无记录，不能默认选牌。旧0损T5在当前引擎尚未证明合法；无Solve/原版动作 |
| 同回合落选恢复边界合同 | v67 `589c10309b54482397fdd66162811c91` 通过；Release零警告错误、两端66文件门禁通过 | `KNOWN-CUSTOM-DEFERRED-FRONTIER-V0111`不Solve；19个严格前缀及18步恢复+末步，三次选择/一药。九种预算、停止、取消、异常及成功路径均完成；默认关/仅窄搜开合同通过。合成政策元数据不证明实际调度资格，TT不检查私有标签内容 |
| 置顶有序谱系Soul首结果 | v67 `5e037748973c4057b9118fc738855857` 损2/96HP/T7/零药，严格损1目标失败；因素已撤回 | 原20秒/VeryHigh/DOP1/GC，无观察器；5109/33202/16639，比原损19改善但尚未达损1/T4；有序保留用满2048。不改Smart/可接受战损停止规则 |
| 置顶有序谱系Exoskeletons哨兵 | v67 `b365cdf18acf4287a49ccb4a44c4fe4d` 0损T11，晚于当前v66 T10，因素撤回 | 13727/155781/97093，协议Passed只代表零损断言；不以此覆盖结束回合回退。只撤回置顶builder因素，保留v66恢复与新边界合同 |
| v67拒绝版Soul路径诊断 | `69e7e9443f4e4bf190145257b634fd7d`，419事件无丢弃，26前缀及根不变通过 | 准确第11步raw59/parent47/routing13/quota13，leader仍未进入最终Prune；未实际展开后缀。诊断结果损2/T7、5109/33202/16639与无观察器相同，非质量通过 |
| Custom同回合落选续搜 | v66 `f5685edaae3c416ab1ccd09c1746002c` 原配置首结果通过，T1/1HP/损3/1药/19动作 | 未注入已知路线；用药恢复77叶、77根+570前缀动作计入10742/12000节点额度。全部76快照值恢复核对；请求28736展开工作/97719转移/10447选择，包含无药失败恢复成本，非性能收益声明 |
| Custom新路线实际部署 | v66 `6d25ca7a3e6a4252adcc0d5fe902bf3b` 原生T1结束战斗、火焰药水使用、UnexpectedReplans=0 | 同配置自行搜索后正常部署19动作与三次燃烧契约选择；Instant/0秒与速度恢复检查通过。不是显式已知路线，也不是每前缀全状态差分证明 |
| Exoskeletons当前基线哨兵 | v66 `d698c918a51f462eb93a6dec1adb7753` 0损T10、13746/208100/147857，与post0300/v35同聚合质量和工作量 | 未启用窄搜/落选恢复。旧T5目标未完成；较早T9不是当前合并后基线，v62 T10不能据此再算当前回归 |
| 有界剪枝恢复合同（v54 原型） | `5b7cd7d6882b40eda490bf166bed78e2` 三组入口全部通过；Release 0警告错误，两端门禁66 | `BEAM-CUT-RECOVERY-V0111`：值存储/分页/公平/祖先/容量；协调器替身累计预算/零工作/取消；真实根上的合成节点保护与最终别名计数。没有正式Solve或原版动作，不构成Custom质量通过；同进程后续目标搜索结果另记 |
| Custom 剪枝恢复 v54 | 质量失败 `718a49fbfc5647aba0a656e9b17ad3ce`，HP0/敌347/4洗牌，未部署 | 原配置/首结果停止；总29994展开/112117转移/15259选择，6.614秒、4,189,131,112 B、GC253.254毫秒；共享合同进程峰值3,855,784 KiB。恢复两层分别花满12000展开，用药层2256候选无普通席。原型未达标，不以新增展开证明改善 |
| v55同药量quota替换 | 合同 `96268fdb5d184077a50ccb74fb7cb222` 通过；Custom `350a83e241b545faba1c06670c9abc17` 质量仍失败，原型撤回 | HP0/敌347/4洗牌；29994展开/112192转移/15259选择、6.372秒、4,193,335,816 B、GC6.964毫秒；共享峰值5,026,748 KiB。用药层no_seats=0，却只服务cut17→14第一页，97准入/64实际展开；没有后续哨兵或部署。代码和夹具不再位于生产/Testing入口 |
| 当前引擎Soul已知路线约束重建 | v56 `3cd33a96dd544421b8585c6a6566e420` 全26前缀通过，预计97HP/损1/T4胜利 | `KNOWN-SOUL-ROUTE-REPLAY-V0111`，原导入根，五次真实主选择绑定，完整/增量StateDiff与root/live不变，风险门通过；无额外选择。非旧PlanAction字节复原，未Solve/原版动作/性能，metrics为空；原生对照见v58 |
| 灵魂枢纽已知路线原版严格对照 | v57末击失败；v58 `3a85ce2f16654b6daf976fff1307b688` 全26前缀通过，实际T4/97HP/损1/零药水 | `KNOWN-SOUL-ROUTE-NATIVE-V0111`；先冻结26个完整预测，再执行26个原版动作、5次严格实例选牌、3次原版EndTurn；末击清理前取证并等待CombatEnded。不是搜索发现、生产UI自动部署或性能通过 |
| 灵魂枢纽已知路线纯值追踪 | v59 `4ad27f7d9345427097003a0130d6bb67`，131事件无丢弃；搜索仍损19/T7 | `KNOWN-SOUL-PATH-TRACE-V0111`；原20秒/VeryHigh/DOP1/Smart/GC，前10步生成并展开，第11步类星体选择深谋远虑在外层Prune首次丢失。按完整动作/选择及政策标签区分同状态历史，不向Solve传入已知前缀；仅诊断完整性通过，非质量或性能通过 |
| 灵魂枢纽首次裁剪整池 | v60 `a444d99408ed4145ac060f8fd2c27d36`，98输入/98真实排名/54最终保留、381完整事件；仍损19/T7 | 同入口及预算，仅新增指定外层池观察。目标零基raw67、routing60/quota13，非leader；低于普通截线且不在路由配额，required15/54未满。五个不同前序放回选择具有相同无序生成上下文；不当作策略已修复 |
| 生成选项有序分组 v61 | 灵魂枢纽DOP1/2损1/T4通过，但Exoskeletons损1/T4退化，方案撤回 | Soul `7fbba3fcc7aa4cf09e83b689ef4f63ba` / `a505c55d01b64ce39f3fa58830f5bfc6`：27日志动作、80项非时序/非调度字段相同，实际并发2；日志未序列化全部PlanAction字段。Exoskeletons `3605b08cd8214adb87b58323b40bea74` 零损断言失败，不用Soul单项成功覆盖哨兵回归，不保留旧leader被替换的分组方案 |
| 同分生成上下文伙伴 v62 | Soul损13/T7，Exoskeletons零损T10，方案及有序键已撤回 | Soul `9206ac3b8b0149f1b544c5b82848628b` 的第11步存活、第12步裁剪；Exoskeletons `7f3de2d204ad4fb0b0b056a32638312c` 协议Passed但旧零损T5质量审计退出1，27.528秒/12.178GB分配。v66已纠正当前合并后基线也是T10，不再按更早T9声称其回合数回退。只保留测试共享helper，不保留伙伴保路策略 |
| 自定义战斗已知路线纯值追踪 v63 | `bbb40f801677463ba44047a4b04b3a38` 诊断通过、128完整事件；实际搜索仍死亡/敌347/4洗牌 | `KNOWN-CUSTOM-PATH-TRACE-V0111`，原政策/DOP1；19个冻结前缀回放、shadow/live根不变，同一用药solver准确生成并展开首步。135宽前9个目标状态存活，第10步外层Prune丢失；60宽第6步丢失。第3步准确路线及第5步其他排列的TT拒绝均有同状态同6维标签代表继续展开，不当作故障；第3步Traits已不同，尚未证明别名完整后缀或调度历史等价 |
| 自定义第10步别名后缀及整池 v64 | `49a3fb9418d644d3bb4f158b8e0911ec` 诊断通过；实际搜索质量仍失败 | 1个真实Generated别名原根回放及9步原样后缀逐步全状态/增量等价，T1/HP1/损3/1药胜利；非调度历史等价。1024无丢弃事件，355输入/排名、135全局/177最终保留，目标raw172未进route96/quota69，required90；同上下文raw17以更多即时伤害先入。原搜索9412/35298/4699未改变；构建及两端65项门禁通过 |
| 持久选择上下文SetupFirst v65 | Custom `127761331d114c96b870d61786e6991c` 质量失败，实验撤回 | 原配置/DOP8/NoGC16/无观察器/首结果停止；仍死亡/敌347/4洗牌，9353展开/35073转移/4755选择。只改同context候选顺序且保持集合/评分/预算，不足以恢复完整解；未跑哨兵或部署 |
| 遗物属性在末击后的命令边界 | v58 `b9edcf8d2c3a416baec2caa6505ea610` 两个最小边界、三遗物均通过 | `relic-stat-terminal-v0111`；苦无/手里剑/彩虹戒指在非致死动作正常加属性，致死动作仍递增计数但不施加属性；原版与全根/增量完整状态一致。每个根只打一张牌，不运行Solve |
| Windows helper 预约自测 | `tools/test-headless-runtime.ps1` 通过：双 parallel、exclusive、资源不足、未知游戏、归属、stale、warm | Linux 上的 PowerShell 替身测试；非 Windows 游戏进程或快照实测 |
| Windows资料复制边界 | `-ProfileOnly` 通过：私有拷贝、源资料不变、重解析点拒绝 | Linux上PowerShell文件系统验证，不是Windows游戏验证 |
| 两端结构门禁 | Bash / PowerShell 均 `REFACTOR_BOUNDARIES_OK search_files=64` | 只证明结构边界 |
| Linux helper 原生子进程生命周期 | 11项通过：并发、同实例拒绝、排队、独占、warm、取消/超时、pending/孤儿、stale、PID出生及未知进程 | 真实辅助层 + 私有原生sleep；枚举限定测试域，非游戏协议 |
| Linux 快照隔离 | `--snapshots` 4项通过：A/B不同DLL内容、A更新不改B/源树、旧快照保留、活进程拒绝替换 | 文本DLL替身，不证明实际程序集加载 |
| Linux 快照故障注入 | `--snapshot-failures` 9项通过：find、中间SHA、rm/mkdir/cp、retired mktemp/mv、publish mv、ID mv | 错误显式传播，不越界移动、不形成新game/旧ID错误缓存；旧树仍可恢复 |
| 真实双 headless | PID3841962 / PID3842057 的请求区间重叠约23.7秒；Fork通过 `3ca7afc55dc44476bf13f0ebf2ab6a7b`，Start旧DLL按预期失败 `bd5e0bbd45cd4698aa090070c67fa333`，各自退出 | Linux私有游戏/Mod/协议；不是单场性能对比，也不声称两个语义fixture都通过 |
| 真实静稳复用 | v43差分Passed→Ready→同PID3847699最小根检查Passed并ExitOnComplete；后者 `7ebee077e8e544cc8d0f1e713ea6816e` | 未真实测试Held或Windows；取消/故障互不误杀的细分证据来自原生替身 |
| 矩阵实例传递与清理 | 两端 `test-headless-matrix-runtime` 通过实例/参数传递、暖实例尾部stop、外来身份拒绝、同实例及取消隔离 | mock场景入口；PowerShell取消为适配器测试，不等于Windows原生Ctrl+C |
| stop-only真实入口 | Linux原生替身通过只停本实例、peer保留、stale/absent幂等、未知/PID复用/无marker/已有producer拒绝；PowerShell入口通过stale/absent/noPID/foreign-pwsh边界 | 缺DLL/依赖仍不创建request/profile/snapshot或启动游戏；不是Windows游戏生命周期实测 |
| 真实暖游戏stop-only | v49合同请求Passed→Ready后，精确停止PID4049888；故意指定不存在的构建/源游戏/依赖路径仍成功 | 旧结果 `be1553a145b64023b7d44ceab1ff1456` 保持，PID和marker消失，未创建指定目录；不再发布游戏请求，不代表Windows实机通过 |
| 回手 Start 根修正 | v43 `bf4fa60a07764a28ab452b93d203f9b3` 7项严格状态对照通过 | 包含三次PreDrawStartRoot与原版/连续/Fork/中途根，不是整场搜索 |
| 水银沙漏/千足虫死亡状态投影 | v44失败 `65d54627f8f3435da583ef159763e24a` 仅全灭后的MS0/1误分类；v45修后 `f654b4ad4a1c429f88e4739ff82a8b1a` 8项通过 | 一段1HP、两段原生复活资格；REATTACH→0/22/22不赢，DEAD→全灭；直接/Fork/重捕获根严格状态对照，非搜索排序或错误胜利复现 |
| 跨回合计划终局回合数 | v45基线 `0ae11f4b7f584d819d60d33cb928d276` 错报T1；v46 `8d53449e35b4407fa0a4d4c03164a955` 正确T2；闪电球末尾对照 `bf20264541ab457f975151c5846f4973` 仍T1 | 正式短搜+增量回放；标注、排序共用原版安全点锁定的玩家回合号，不统一给EndTurn加一 |
| 千足虫终局标记与严格状态 | v46 `15b7787fa6ba4dd6bc041fc79cbab81a` 两阶段8项通过 | 一段1HP、两段原生复活资格；额外断言终局Fork与首次锁定不覆盖；非整场性能/搜索质量结论 |
| 敌方开局中毒终局对照 | v45 `2ac6c15441594cb199551ead687fd534` 与 v46 `fd2a5d8612344f18a5a8304de814ae31` 都返回T1 | 修后正式短搜+增量回放，没有因EndTurn误加一；与沙漏/闪电球共享短请求进程，非独立性能A/B |
| 普通打牌强制结束后的终局 | v47 `2414e7137a554f54b8037df5b8adb89c` 6项通过 | 唯一出牌动作原版/根回放/增量回放严格对照，T动作触发T+1沙漏击杀；正式增量断言比较终局标记，Fork保持，释放快照后正式标注仍T+1；无搜索展开/性能结论 |
| Soul 语义修后搜索基线 | v47 `1f43a8bab8fd402baedcf600d4c70397` 质量失败：掉血19、79 HP、T7 | 原20秒/DOP1/NoGC关闭/VeryHigh；总8598展开/64393转移/31687选择；未再出现旧牌堆标注差分，但未达到历史掉血1目标，后续策略实验以此当前基线单因素对照 |
| Custom 语义修后搜索基线 | v47 `d0b4125e662f40ccabed12604aac9ebf` 仍只有死亡路线、敌剩347 HP、洗牌4次；未部署 | 原VeryHigh/DOP8/NoGC16，9412展开/35298转移/4699选择；搜索4.641秒、分配1,403,332,960 B、GC132.458毫秒、VmHWM2,285,236 KiB；未达到历史T1获胜目标 |
| 新鲜选牌父名次同分实验 | v48局部节点合同通过，但Soul质量退化至掉血47/T9（`70f18bd55eda4f7a8f4e6662ef3241c3`）；方案撤回 | 只作失败实验记录，不作为现行策略或通过证据 |
| 拥挤策略与普通候选仲裁实验 | v49合同通过 `be1553a145b64023b7d44ceab1ff1456`；Custom仍死亡/敌347/4洗牌（`c16bbcbbc2564e3eb3b21365d70607fe`），未解决目标，方案撤回 | 9142展开/34420转移/4689选择；4.069秒、分配1,355,445,536 B、GC146.625毫秒、VmHWM2,339,800 KiB；不将少量加速当修复，不执行后续Soul哨兵 |
| 当前引擎重建Custom已知解 | v50 `d519b3ca52f947c89d0ace26dbe9ede6` 通过19个前缀严格增量/全根回放，预计1 HP/损3/T1/敌灭/7洗牌/1火焰药 | 依据v29动作与实际三次单选记录，在当前原归一化根绑定完整卡牌/选择身份；不是旧PlanAction逐字反序列化。原模拟根及实战完整状态保持不变，未Solve、未原生部署、metrics为空；证明当前模拟器能表达该解，不证明搜索已找回它 |
| Custom已知解预测风险门 | v51 `fc247b55b2dc4a04990e1deda3cb773d` 全19前缀及终局通过，额外要求 `HasRisk=false` 且无未补偿PredictionGap | 补强模拟可行性证据；仍未Solve或原生部署，不计作搜索质量成功或性能数据 |
| Custom已知解原版严格对照 | v52 `83d25eef53f847df81d98f0cf18aea4d` 全19个原版动作/冻结预测前缀通过，实际T1胜利、1 HP、损3 | `KNOWN-CUSTOM-ROUTE-NATIVE-V0111`；同一归一化根，原版ManualPlay/EnqueueManualUse，三次选择核对完整实例状态与两个游标，末击原版清理前取证；头部无Solve指标，不当作搜索已找到路线或自动部署通过 |
| 后台Gen2检查点生命周期 | v52 `6c1ef5e88b67423b9e6a30406e089b50` 8组合同通过，复用PID4085610后退出 | `GC-CHECKPOINT-BACKGROUND-V0111`；正常确认/重建、同步上下文、取消与晚manual/引用释放、注入超时排空、旧早manual/开始前失败/epoch捕获前后；确认窗口可暂停但不控制CLR mark。普通完成实测background，超时兜底blocking；非长线性能A/B |
| Custom固定工作量回收A/B（1 GB） | v51同步 `841a2372b6bc4436b34b14d455fc0e02` / v52后台 `e9c77ba6a6394a6bbf604ebae20d018d`；两侧原胜利断言仍失败 | 同根/VeryHigh/DOP8/Smart；52项非时序字段、9412展开/35298转移/4699选择及13条日志动作一致（日志未序列化全部PlanAction字段）。22检查点均重建成功，新版22次background；计账5.481→5.301秒，GC累计/最长1601.244/104.588→46.818/4.977毫秒；峰值1,631,036→1,774,676 KiB（+140.3 MiB）。不当作已找到合法胜利或Windows长线性能通过 |

维护时默认使用分层快速回归：普通语义改动跑单效果严格差分；Fork、跨回合历史和续用改动补一个最小两回合或最早复用边界；搜索/部署改动的最终候选才运行必要的完整自动场。快速 unattended 请求总超时不超过 `120` 秒，超时后缩小 fixture 或记为未验证，不在同一轮延长等待。下方完整矩阵是发布门禁和专项审计入口，不是每次修复都要执行的默认清单。
