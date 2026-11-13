# CombatSolver 测试入口

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

2026-10-07 PR #227–#230 的本机 `SMART-POTION-AUDIT-BUDGET`、旧报告、GC 合同，以及 O068 和固定哨兵整场部署通过。O056 最终与当前主线同为 62 战损，历史界 42 未达成；组件证明合同被 native-version 门禁拒绝。最小复跑入口、同根对照、失败及未验证范围见[整合验证](archive/testing/pr-integration-20261007.md)。

贡献者 2026-10-06 的 Linux [计划续搜完整胜利证明](performance/plan-witness-propagation-20261006.md)：原生计划循环与最终 GC 合同 Passed；政策门禁、16 Fork、DOP1/16 实际成员派发与一次压缩完整回收通过，实例清理。11 根 44 次纯交错质量及峰值通过；2 次完整诊断、9 次根审计单列。未做全29根、上传包或可见性能，历史战损差异保留。

0.50.0 定稿沿用 PR #207、PR #211 与开局药水准入的既有行为证据，来源和范围见 [历史卷 20](archive/testing/volume-20.md)。PR #226的Ctrl+F9快捷键、新图层、重置和监控合同的通过、失败与可见验证范围见[历史卷21](archive/testing/volume-21.md#ctrlf9-面板可见性pr-226)。

按改动选择最小验证层，方法见 [无人测试](HEADLESS_TESTING.md) 与 [社区验收](community/testing-guide.md)。以下命令提供当前复跑入口，不表示本轮已执行。单人共享损血剪枝的新基线验证见[策略证据](strategy/hp-loss-pruning/README.md#单人共享损血剪枝2026-10-05)。

历史记录见 [归档索引](archive/testing/README.md)，0.48.1 的验证、失败与未验证项见 [历史卷 12](archive/testing/volume-12.md)。

PR #213 的贡献者分阶段验证见 [Q002 历史入口](issues/q002-route-quality.md#0494-重新验证2026-10-05)。当前 0.50.0 主线整合使用 O003、O004、O005 同根、同政策对照，结果与复跑预算见 [当前验收](issues/q002-route-quality.md#0500-主线整合2026-10-05)。

## 首领卖血预算搜索

2026-10-07：`--check-boss-tempo` 12项、早回合界143项、共享资源桶20项及两平台结构门禁通过。后台女王A从combat_start同配置30秒/80000节点、DOP1对照，63损血/1药/T7→41损血/2药/T8，药水折算净改善13HP；原生基线 `d62346c8d4634d8981bde3c83f502d47` 与候选 `f011940c2e904905ac8d4aee15aedaea` 完整部署Passed、0重算，追加约6秒。女王B折算持平；沙漏两侧120秒超时；瀑布巨兽死亡→43损血/1药/T13胜路，固定胜路原生 `b134e602eb9d43cf8bcd335e955b41ea` Passed、0重算、零搜索展开。独立精英 `4c2069be5c07480bb0b4ed7937698cb9` 完整部署Passed，5损血/1药/T4、0重算，成员旁路；实例均删除。新增三包严格恢复，族母现策略5/0药/T8，仅SearchOnly；后续迭代尚未建立新净收益。完整复跑、失败与未验证范围见[策略证据](strategy/hp-loss-pruning/README.md#首领卖血预算遍历)。

## 社区批次 Q010 与组合补搜

现行组合入口按共享节点与时间准入，执行期间在每批提交边界处理内存预约、回收和停止。原包 SearchOnly、Beam 组合检查及真实 CLR 合同的结果、失败与未验证项见 [历史卷 20](archive/testing/volume-20.md#社区批次-q010-与组合补搜)。

## 0.50.1 智能药水机会成本

终局单药／双药低收益拒绝与 Force、奖励抵扣、原价高收益哨兵由 `SMART-OPENING-POTION-ADMISSION` 覆盖；跨成员比较由 `python -B tools/testing/checks/BeamWidthPortfolioChecks/run.py` 覆盖（118项通过）。原生 runId `d9629992ac31423586c7465510c18f6d` Passed（23.63秒，含建局），实例已清理。失败基线、同根材料与未验证范围见[问题记录](issues/potion-opportunity-20261006.md)。

## 社区批次 Q015：路线保留

贡献者五主题的同根短搜、原生部署、固定哨兵及失败记录见[Q015 记录](issues/q015-route-quality.md)。维护的开战政策输入为 [High](../coverage/fixtures/search/damaging-continuation-replay-policy.json)、[Medium](../coverage/fixtures/search/damaging-continuation-medium-replay-policy.json) 与 [Force](../coverage/fixtures/search/damaging-continuation-forced-dexterity-policy.json)，原生部署使用 Instant／0 秒，断言计划外重算。本机最终整合收窄破盾收尾资格，O068 保持 3 战损、0 药、T4，独立哨兵保持 5 战损、1 药、T4，均完整部署、零重算；其余贡献者旧结果保留原验证范围。旧报告默认手牌上限兼容合同通过，详细同根对照见[本机验证](archive/testing/pr-integration-20261007.md)。

## 社区批次 Q013 同根哨兵与部署夹具

Q013（O056–O060，#220 / PR #228）的五条同根 `SearchOnly` 质量界夹具与两条 `DeploySolver` 夹具位于 `coverage/fixtures/regressions/community/q013-*.json`；逐条结果、失败基线与未验证项见 `coverage/evidence/test-evidence.json` 的 `Q013-*` 条目与[认领者记录](community/drafts/2026-10-06/Q013-claim-reproduction.md)。夹具依赖不入库的玩家原包 ZIP，故本页不提供行首可执行命令（与 Q010 同口径，矩阵可复跑清单条数不变）：复跑时把夹具 JSON 字段逐个传给 `tools/testing/run-unattended-test.ps1`，显式带 `-Sts2GameRoot` / `-RitsuWorkshopRoot` / `-CleanupInstanceOnExit`，判定以 `result.json.status` 为准（带实例清理时 launcher 退出码 1 不代表失败）。

认领提交未改 `src`（`git diff --name-only 0d290fbe 0143d82f -- src` 为空），七条夹具在基点 `0d290fbe` 上按夹具本体全部 Passed；界锁各主题当时的实际产出，不构成改良值达标声明（O056/O057/O059 未达包内改良值，O060 默认预算下终值 35～43 摆动，O058 缺口在同一次请求内后续搜索的运行态）。合并 `6031debd` 后（`89798a78` 把 `max_hand_size` 写进续用指纹）七条全部在 `native_replay_events` 报 `restore_mismatch` 而未进入搜索，属旧包容兼容性边界，逐条 runId 与包内取证见[调查流水](archive/community/q013-claim-reproduction-20261006.md)；PR #227 的默认手牌上限兼容已进入整合，本机 O056 原生恢复通过，但历史质量界 42 未达成；其他六条夹具在最终整合上未复跑，见本页整合验证。

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
