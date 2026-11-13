# CombatSolver 测试历史卷 23

0.51.0 · 0.113 适配 Part 1（2026-11-13）。本卷保存各批次原始证据与验证范围；发布定稿沿用既有行为证据。

## 请求预算显示

2026-11-13，`REQUEST-BUDGET-DISPLAY` 在 v0.113.0 通过：真实 coordinator 的节点耗尽报告、2500+8012 节点成员累计、总预算充足时的提示、实际节点／时间上限、时间覆盖参数和 zhs/zht/eng 界面。入选成员原始节点、边界及路线身份保持。该合同验证统计和 UI 投影；可见窗口排版由现有控件渲染。

同日后续单步日志：`9b32cae92bf64cf49f32cd260dfd8e7a` 第 2 回合 exact-state 续用、0 节点仍显示原成员 TimeLimit。续用／缓存恢复的提示修正仅做 Release 编译；本轮实机交互由用户验收，既有合同结果保留其原覆盖范围。

复跑：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId REQUEST-BUDGET-DISPLAY -CharacterId IRONCLAD -InitialEnemyMaxHpsJson '[9999]' -EnemyCurrentHp 9999 -StopAfterInitialSetupAssertion -EnableNoGcRegionForTest 0 -HeadlessFastModeForTest Instant -HeadlessInstance request-budget-display -CleanupInstanceOnExit -TimeoutSeconds 120`。来源与 runId 见 `coverage/evidence/test-evidence.json`。

## v0.113.0 标准模式适配

2026-11-13，本机 public-beta `0.113.0` / `16ac0fe3`。当前 Release 主项目与 OfflineSearchHarness 构建通过。下表是本轮原生运行证据，完整 runId、程序集 hash 与结果保存在 `coverage/evidence/test-evidence.json`；每次请求上限 120 秒，实例清理结果均由启动器记录。

| 场景 | 机制与输入 | 结果 |
| --- | --- | --- |
| LAZY-RNG-FORK | 九条战斗流和玩家 NonCombatGeneration，根捕获与多代隔离 | Passed |
| STANDARD-REWORKED-CARDS | `coverage/fixtures/cards/audits/standard-reworked-cards.json` | Passed |
| STANDARD-HIT-COUNTS | `coverage/fixtures/cards/audits/standard-hit-counts.json`，15 张旧牌与 Thumbscrew | Passed |
| EXTRA-TURN-MIRROR-ORDER | 帕尔之眼已用／未用 × 三种监听位置，消费来源、Fork 与 live | Passed |
| FOOL-ARROW-LIFECYCLE | `coverage/fixtures/cards/audits/fool-arrow-lifecycle.json` | Passed |
| FOOL-ARROW-EFFECTS | `coverage/fixtures/cards/audits/fool-arrow-effects.json`，双敌、绽放射击、散雨射击、寂灭 | Passed |
| FOOL-MELODY-EFFECTS | `coverage/fixtures/cards/audits/fool-melody-effects.json` | Passed |
| FOOL-CHOICE-EFFECTS | `coverage/fixtures/cards/audits/fool-choice-effects.json`，Recall 与叽 唱诵 | Passed |
| FOOL-POTIONS | `coverage/fixtures/potions/fool-potions.json`，瓶装歌曲、幸福结晶、精密药水 | Passed |
| FOOL-SHOW-OFF | `coverage/fixtures/cards/audits/fool-show-off.json`，炫技多实例击杀成长 | Passed |
| WUPPY-DAMAGE-ROUTING | `coverage/fixtures/combat/wuppy-damage-routing.json`，存活、溢出击杀、后续攻击 | Passed |
| DUPE-CLONE-CONTRACT | 离牌堆原牌捕获、live 推进、父子／兄弟引用及状态键 | Passed |
| AIM-ATTACK-RETENTION | 攻击中叠层、保留出手身份、根与 Fork | Passed |
| NATIVE-CARD-ORDERING | 费用、重放、动态变量排序，完整 RNG，live 推进和兄弟隔离 | Passed |

复跑夹具使用 `-MonsterMoveChecksPath <上述路径> -StopAfterInitialSetupAssertion`，愚者场景增加 `-CharacterId FOOL`；双敌箭术和炫技增加 `-AdditionalMonsterId TUNNELER`，两敌最大／当前生命同时设为 999。命中次数夹具单敌最大／当前生命为 9999。药水使用 `-PotionChecksPath`。每次加 `-HeadlessInstance <独立名称> -CleanupInstanceOnExit -TimeoutSeconds 120`；启动前构建当前 DLL。

逐卡审计覆盖 84 张单人愚者卡；六张 MultiplayerOnly 卡保留原生范围说明。84 张逐卡出牌差分全部通过。失败基线：啾 尖刺缺内部 ThornsPower，补齐 TemporaryThornsPower 的施加和对方回合末回收后，四张尾段通过。旧 CARD-CLONE-IDENTITY-CONTRACT 的错位调查断言在当前建局失败，新增 DUPE-CLONE-CONTRACT 独立验证新版复制语义。两个并发请求在宿主准入队列达到超时，已清理实例，随后顺序补跑。

愚者分支合同及连续两次玩家回合结束／准备通过，覆盖箭身份、插箭引用、旋律清理、live 推进及父子／兄弟隔离；完整行动队列跨回合测试曾达 120 秒，最小生命周期合同在 35 秒内通过。炫技永久成长、Power 来源重映射、成长额度持久化及侧栏入口通过。调音／倦怠／神话形态组合的下一回合全状态差分通过。

六角色固定 1500 ms 短搜及严格增量核对通过，输入位于 `coverage/fixtures/search/beta-standard-*.json`：铁甲战士 0 损/T3、静默猎手 2 损/T5、储君 5 损/T4、亡灵契约师 0 损/T4、故障机器人 0 损/T3；愚者箭术 4 损/T2、旋律 4 损/T3、混合 0 损/T3。铁甲战士及愚者三组 Instant/0 秒原生完整部署通过，均为零计划外重算。部署产生的新版箭术 T2、混合 T3 问题包以 RestoreOnly 恢复通过，原生状态与 continuation 均核对一致。各运行的源码 hash 和 runId 分别登记；可见 Steam UI 尚未验证。

组件回复闭包按原生 MVID `ad562b4e-7352-4ca2-ae45-0ff44cd284bc` 更新，原生审计见 `coverage/evidence/sts2-0.113-healing-audit.json`；`COMPONENT-HEALING-BOUND` 的生成过滤、洗牌、回复剂量及未知来源门禁合同通过。离线铁甲战士小根 Low/500 节点/1500 ms/DOP1 哨兵在修复前后均展开 291 节点、899 转移、0 战损、相同评分，wall 约 0.74→0.68 秒；该数字覆盖此固定小根。

六行 `FOOL-HOOK-COMBINATIONS` 全状态差分通过，输入为 `coverage/fixtures/cards/audits/fool-hook-combinations.json`，覆盖双敌射箭／插箭／拔箭联动、六种旋律能力、Encore/FreeCard、Simpleton 自动牌、Joy 转换、Juggling 复制及回合准备。失败基线发现 WhistlingArrows 缺监听参与位、Yah/Lullaby 缺纯原版快捷路径复位；最终运行 `67fdcd50ee6b4b5890142c6492f701e6` 通过并清理实例。

独立合同另覆盖：`CHOICE-SHAPE-INSPECTION` 出牌前选择类型查询及 RNG、`DINGY-DAGGER-NAMES` 中英名称阈值和高升级等级、`PHANTOM-RETAIN-LIFECYCLE` 动态保留、`INFUSED-CORE-PREPARATION` 首次充能与第二回合、`STANDARD-NEW-TOKENS` 新生成牌、`STANDARD-SOURCE-LIFECYCLES` 附魔与失忆、`FOOL-ARROW-MULTIPLIERS` 箭倍率、`FOOL-TURN-CLEANUP` 回合清理、`STANDARD-CHANGED-MONSTERS` 感染棱柱／地道虫／气态炸弹。塔罗 22 张的普通和升级版本分批全部通过；命运之轮弃抽数量失败及修复保存在结构化证据中。

`FOOL-RESOURCE-BOUNDARIES` 四行分别核对 Coda 费用、插箭倍率、延迟抽牌、Byrdpip 计数和箭击杀回收，最大／当前生命均设 `[9999,9999]`；前三行与最后死亡行分批通过。`FOOL-UPGRADED-CARDS` 补齐五张升级牌，`SHOW-OFF-DAMPEN` 验证升级牌降级，`TORMENTOUS-COLLAR-PREPARATION` 在 `BYGONE_EFFIGY_ELITE` 核对首回合两层虚弱和次回合失效，`ROCKET-ATTACKS` 在 `KAISER_CRAB_BOSS` 核对四种攻击。独立夹具均以完整原生／预测状态比较；初始化参数错误、首回合夹具的回合回退及普通遭遇设置失败均记录在证据中。

`STANDARD-RNG-STREAMS` 的七行差分通过：BeatDown、Uproar、Catastrophe 普通／全不可打候选、Stampede 回合末自动牌、Entropy 抽牌后转换、ThievingHopper 排除任务牌的永久牌组偷窃。`ENTROPIC-GENERATION` 连续两次混沌药水验证空槽填充、满栏替换后的单槽填充以及玩家 NonCombatGeneration 完整状态。启动器的药水／怪物输入是分别运行的请求；Entropy 的 120 秒超时基线来自测试选择器作用域，修正后全组通过。

最终 `BETA-DEPLOY-FOOL-HOOKS` 以固定 1500 ms、严格增量核对、Instant/0 秒部署通过，原生 T2、77/77 HP、零计划外重算。路线实际使用 ExplosiveArrows、射箭前后两次 WhistlingArrows、EpicRetrieval 和下一回合续用；输入为 `coverage/fixtures/search/beta-standard-fool-hooks.json`。最终 `FOOL-TURN-LIFECYCLE` 在父子／兄弟分支中加入 Yah/Lullaby 标记，原生连续两回合推进至 T3 通过。首次部署启动在快照冻结期间检测到 DLL 变化并拒绝，重新使用稳定构建后通过。

CoverageCatalog 的 3386 条目录全量门禁通过：待分析、待实现、活动精确 Hook 运行证据缺口、必需状态写入证据缺口和分支读取风险均为 0。24 条仅根快照写入、115 条静态行动图构建单列；旧证据保留来源版本。本版运行、静态审查与历史记录的口径分别保存在源码差异索引和结构化证据中。PowerShell/Bash 结构门禁及文档、工具、覆盖目录检查通过；本机执行平台为 Windows，Linux 游戏运行和可见 Steam UI 尚未执行。

2026-10-07 PR #227–#230 的本机 `SMART-POTION-AUDIT-BUDGET`、旧报告、GC 合同，以及 O068 和固定哨兵整场部署通过。O056 最终与当前主线同为 62 战损，历史界 42 未达成；组件证明合同被 native-version 门禁拒绝。最小复跑入口、同根对照、失败及未验证范围见[整合验证](../../archive/testing/pr-integration-20261007.md)。

贡献者 2026-10-06 的 Linux [计划续搜完整胜利证明](../../performance/plan-witness-propagation-20261006.md)：原生计划循环与最终 GC 合同 Passed；政策门禁、16 Fork、DOP1/16 实际成员派发与一次压缩完整回收通过，实例清理。11 根 44 次纯交错质量及峰值通过；2 次完整诊断、9 次根审计单列。未做全29根、上传包或可见性能，历史战损差异保留。

0.50.0 定稿沿用 PR #207、PR #211 与开局药水准入的既有行为证据，来源和范围见 [历史卷 20](../../archive/testing/volume-20.md)。PR #226的Ctrl+F9快捷键、新图层、重置和监控合同的通过、失败与可见验证范围见[历史卷21](../../archive/testing/volume-21.md#ctrlf9-面板可见性pr-226)。

按改动选择最小验证层，方法见 [无人测试](../../HEADLESS_TESTING.md) 与 [社区验收](../../community/testing-guide.md)。以下命令提供当前复跑入口，不表示本轮已执行。单人共享损血剪枝的新基线验证见[策略证据](../../strategy/hp-loss-pruning/README.md#单人共享损血剪枝2026-10-05)。

历史记录见 [归档索引](../../archive/testing/README.md)，0.48.1 的验证、失败与未验证项见 [历史卷 12](../../archive/testing/volume-12.md)。

PR #213 的贡献者分阶段验证见 [Q002 历史入口](../../issues/q002-route-quality.md#0494-重新验证2026-10-05)。当前 0.50.0 主线整合使用 O003、O004、O005 同根、同政策对照，结果与复跑预算见 [当前验收](../../issues/q002-route-quality.md#0500-主线整合2026-10-05)。

## 首领卖血预算搜索

2026-10-07：`--check-boss-tempo` 12项、早回合界143项、共享资源桶20项及两平台结构门禁通过。后台女王A从combat_start同配置30秒/80000节点、DOP1对照，63损血/1药/T7→41损血/2药/T8，药水折算净改善13HP；原生基线 `d62346c8d4634d8981bde3c83f502d47` 与候选 `f011940c2e904905ac8d4aee15aedaea` 完整部署Passed、0重算，追加约6秒。女王B折算持平；沙漏两侧120秒超时；瀑布巨兽死亡→43损血/1药/T13胜路，固定胜路原生 `b134e602eb9d43cf8bcd335e955b41ea` Passed、0重算、零搜索展开。独立精英 `4c2069be5c07480bb0b4ed7937698cb9` 完整部署Passed，5损血/1药/T4、0重算，成员旁路；实例均删除。新增三包严格恢复，族母现策略5/0药/T8，仅SearchOnly；后续迭代尚未建立新净收益。完整复跑、失败与未验证范围见[策略证据](../../strategy/hp-loss-pruning/README.md#首领卖血预算遍历)。

## 社区批次 Q015：路线保留

贡献者五主题的同根短搜、原生部署、固定哨兵及失败记录见[Q015 记录](../../issues/q015-route-quality.md)。维护的开战政策输入为 [High](../../../coverage/fixtures/search/damaging-continuation-replay-policy.json)、[Medium](../../../coverage/fixtures/search/damaging-continuation-medium-replay-policy.json) 与 [Force](../../../coverage/fixtures/search/damaging-continuation-forced-dexterity-policy.json)，原生部署使用 Instant／0 秒，断言计划外重算。本机最终整合收窄破盾收尾资格，O068 保持 3 战损、0 药、T4，独立哨兵保持 5 战损、1 药、T4，均完整部署、零重算；其余贡献者旧结果保留原验证范围。旧报告默认手牌上限兼容合同通过，详细同根对照见[本机验证](../../archive/testing/pr-integration-20261007.md)。

## 社区批次 Q013 同根哨兵与部署夹具

Q013（O056–O060，#220 / PR #228）的五条同根 `SearchOnly` 质量界夹具与两条 `DeploySolver` 夹具位于 `coverage/fixtures/regressions/community/q013-*.json`；逐条结果、失败基线与未验证项见 `coverage/evidence/test-evidence.json` 的 `Q013-*` 条目与[认领者记录](../../community/drafts/2026-10-06/Q013-claim-reproduction.md)。夹具依赖不入库的玩家原包 ZIP，故本页不提供行首可执行命令（与 Q010 同口径，矩阵可复跑清单条数不变）：复跑时把夹具 JSON 字段逐个传给 `tools/testing/run-unattended-test.ps1`，显式带 `-Sts2GameRoot` / `-RitsuWorkshopRoot` / `-CleanupInstanceOnExit`，判定以 `result.json.status` 为准（带实例清理时 launcher 退出码 1 不代表失败）。

认领提交未改 `src`（`git diff --name-only 0d290fbe 0143d82f -- src` 为空），七条夹具在基点 `0d290fbe` 上按夹具本体全部 Passed；界锁各主题当时的实际产出，不构成改良值达标声明（O056/O057/O059 未达包内改良值，O060 默认预算下终值 35～43 摆动，O058 缺口在同一次请求内后续搜索的运行态）。合并 `6031debd` 后（`89798a78` 把 `max_hand_size` 写进续用指纹）七条全部在 `native_replay_events` 报 `restore_mismatch` 而未进入搜索，属旧包容兼容性边界，逐条 runId 与包内取证见[调查流水](../../archive/community/q013-claim-reproduction-20261006.md)；PR #227 的默认手牌上限兼容已进入整合，本机 O056 原生恢复通过，但历史质量界 42 未达成；其他六条夹具在最终整合上未复跑，见本页整合验证。
