# CombatSolver 测试入口

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

这些场景各自停止在共享首因或必要跨回合边界。完整runId、失败基线和未验证项见[历史卷13](archive/testing/volume-13.md)，逐包分类见[问题记录](issues/0.48.0-hardbugs-20261003.md)。

```powershell
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId NATIVE-CHOOSE-OPEN-GATE -CharacterId SILENT -EncounterId CHOMPERS_NORMAL -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CALCULATED-GAMBLE-ORDERED-DISCARD -CharacterId SILENT -EncounterId CHOMPERS_NORMAL -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId FROZEN-LIGHTNING-CHANNELS -CharacterId DEFECT -EncounterId MECHA_KNIGHT_ELITE -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId REPORT-ROUND-IMBALANCED -CharacterId NECROBINDER -EncounterId BOWLBUGS_NORMAL -InitialPlayerHp 200 -InitialPlayerMaxHp 200 -ClearPlayerHand -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId SECOND-WIND-REPORT-ROOT -CharacterId IRONCLAD -EncounterId DECIMILLIPEDE_ELITE -InitialRoundNumber 3 -InitialPlayerTurnNumber 3 -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId GROUP-DEBUFF-REACTIVE-DRAW -CharacterId NECROBINDER -EncounterId CHOMPERS_NORMAL -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CONSTRUCT-REAPER-ARTIFACT -CharacterId NECROBINDER -EncounterId CONSTRUCT_MENAGERIE_NORMAL -InitialRoundNumber 3 -InitialPlayerTurnNumber 3 -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId FIXED-PREFIX-POTION-POLICY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId FIXED-PREFIX-TURN-OUTCOMES -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId MIRRORED-HOOK-FILTER -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId FROZEN-ROOT-LISTENERS -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CALCULATED-HISTORY-FREEZE -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId VOID-FORM-TURN-CHOICES -CharacterId REGENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId FLATTEN-MUSIC-BOX-ENTRY -CharacterId NECROBINDER -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId SEEKER-ORDERED-OPTIONS -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId RITUAL-TEMPORARY-STRENGTH -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId NOXIOUS-RAMPART-ORDER -CharacterId IRONCLAD -EncounterId TURRET_OPERATOR_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId EVIL-EYE-EXHAUST-HISTORY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId BLOCK-SPEC-CARD-PLAY-IDENTITY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId ROUTE-ADOPTION-LIFETIME -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId PAELS-LEGION-FINISHED-REFERENCE -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId END-TURN-RISK-LOSS-ACCOUNTING -CharacterId DEFECT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId MAKE-IT-SO-FULL-HAND -CharacterId REGENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CARD-COST-IDENTITY-CONTRACT -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId DAMPEN-REACTIVE-ROCKET-PUNCH -CharacterId DEFECT -EncounterId KNIGHTS_ELITE -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId DAMPEN-REACTIVE-MELANCHOLY -CharacterId NECROBINDER -EncounterId KNIGHTS_ELITE -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId RADIANT-PEARL-ENTRY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit
```

```bash
./tools/testing/run-unattended-test.sh --scenario-id NATIVE-CHOOSE-OPEN-GATE --character-id SILENT --encounter-id CHOMPERS_NORMAL --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id CALCULATED-GAMBLE-ORDERED-DISCARD --character-id SILENT --encounter-id CHOMPERS_NORMAL --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id FROZEN-LIGHTNING-CHANNELS --character-id DEFECT --encounter-id MECHA_KNIGHT_ELITE --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id REPORT-ROUND-IMBALANCED --character-id NECROBINDER --encounter-id BOWLBUGS_NORMAL --initial-player-hp 200 --initial-player-max-hp 200 --clear-player-hand --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id SECOND-WIND-REPORT-ROOT --character-id IRONCLAD --encounter-id DECIMILLIPEDE_ELITE --initial-round-number 3 --initial-player-turn-number 3 --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id GROUP-DEBUFF-REACTIVE-DRAW --character-id NECROBINDER --encounter-id CHOMPERS_NORMAL --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id CONSTRUCT-REAPER-ARTIFACT --character-id NECROBINDER --encounter-id CONSTRUCT_MENAGERIE_NORMAL --initial-round-number 3 --initial-player-turn-number 3 --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id FIXED-PREFIX-POTION-POLICY --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id FIXED-PREFIX-TURN-OUTCOMES --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id MIRRORED-HOOK-FILTER --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id FROZEN-ROOT-LISTENERS --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id CALCULATED-HISTORY-FREEZE --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id VOID-FORM-TURN-CHOICES --character-id REGENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id FLATTEN-MUSIC-BOX-ENTRY --character-id NECROBINDER --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id SEEKER-ORDERED-OPTIONS --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id RITUAL-TEMPORARY-STRENGTH --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id NOXIOUS-RAMPART-ORDER --character-id IRONCLAD --encounter-id TURRET_OPERATOR_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id EVIL-EYE-EXHAUST-HISTORY --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id BLOCK-SPEC-CARD-PLAY-IDENTITY --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id ROUTE-ADOPTION-LIFETIME --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id PAELS-LEGION-FINISHED-REFERENCE --character-id SILENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id END-TURN-RISK-LOSS-ACCOUNTING --character-id DEFECT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id MAKE-IT-SO-FULL-HAND --character-id REGENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id CARD-COST-IDENTITY-CONTRACT --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id DAMPEN-REACTIVE-ROCKET-PUNCH --character-id DEFECT --encounter-id KNIGHTS_ELITE --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id DAMPEN-REACTIVE-MELANCHOLY --character-id NECROBINDER --encounter-id KNIGHTS_ELITE --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id RADIANT-PEARL-ENTRY --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit
```

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
