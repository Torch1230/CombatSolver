# CombatSolver 测试入口

## 未发布：战前 Sts2Sim 桥接入口

- 独立检查：`dotnet run --project tools/testing/checks/PreCombatRequestChecks/PreCombatRequestChecks.csproj -c Release` 通过 29 项（含共享 `abi-v1-fixtures.json` 有效/负向响应与存档载荷校验，及隔离加载器错误拒绝与真实包预热）；`dotnet run --project tools/testing/checks/PreCombatRoutingChecks/PreCombatRoutingChecks.csproj -c Release` 通过 7 项（在生产路由器上验证 Windows 能力缺失交接与连续忙碌租约、跨字段 ABI 失败阻断与零缓存污染、混合池强制药水选死路合法放行与零回退、取消/超时/无关取消区分、缓存后端溯源隔离、`dread_mode` 零激活直退及重启取消）；`bash tools/testing/test-headless-runtime.sh --snapshots` 通过 5 项（含 `sts2sim/` 依赖移除、有到无与无到空目录转换）；`Sts2SimBridgeStager` CLI 直接符号链接与祖先别名拒绝、目标符号链接拒绝、失败保留原目录与原子替换自检通过；`verify-refactor-boundaries.sh` 与 `.ps1` 通过（`search_files=266`）；`verify-tools.py`、`verify-documentation.py`、`verify-coverage.py` 与 `run-independent-checks.py --check-catalog`（`registered=26 excluded=3`）通过。
- Linux 真机无头验证（`v0.113.1@d09d48e5`，实例均已清理）：最新复测通过全功能桥接与边界合同 `PRECOMBAT-STS2SIM-BRIDGE-CONTRACT`（`2494adf8895748968e7533c44c2feff9`）与同源默认 `UseBeamWidthPortfolio=true` 哨兵对拍 `PRECOMBAT-STS2SIM-PARITY-CONTRACT`（铁甲战士无能力 55HP `adb2177339554a9e9f2455ced8a30639`：`BridgeLoss=15=NativeLoss`，955.1ms / 1228.8ms；含 `INFLAME` 55HP `d8e1fa5ec9574836b2c0387d1dd8ba63`：`BridgeLoss=10 < NativeLoss=15`，959.4ms / 2868.0ms；含 `INFLAME` 80HP `55912b807cd049e98952de6c791191c7`：`BridgeLoss=10=NativeLoss`，904.0ms / 3308.3ms）；此前基线完成验证的无 `sts2sim/` 缺席场景（标准 `e3c37e5305164946a7486fa0e7f73417`、恐惧 `4e2253107e214691a03e37270ba38b6b`）、多段 `SlipperyPower` / `BeatingRemnant` 威胁扣减 `HP-MODIFIER-COLLECTIONS`（`180041d4b27a499e87283b1a906b4e69`，见 Issue `#267`）、恐惧直退及恐惧宿主配标准规划存档与损坏包零激活检查 `PRECOMBAT-DREAD-ROUTING`（`3a6c846f2bd445c994c1afc16e68db5f`）、标准 API 生命周期 `PRECOMBAT-STANDARD-API`（`06ec0c1451c0477d8042c23f114cefe4`，`WorkerStarts=0`），以及静默猎手 `1478df23d793477cb23ef9d0acc26954` 与故障机器人 `13d6e2f5de5649a4a38374dd40de6c3f`（均为 `0=0`）。
- Windows 真机与端到端验证（`dy Windows`，`v0.113.1@d09d48e5`，实例与临时工作目录均已清理）：原生 Release 构建通过（含 `CombatSolver.MemoryCleaner.exe` 与 `Sts2SimBridgeStager` 驱动器根目录修正），`PreCombatRequestChecks` 通过 29 项、`PreCombatRoutingChecks` 通过 7 项、`Sts2Sim.PreCombatBridge.Tests` 通过 114 项；启用 `sts2sim/`（`InProcess`）无头 7 项通过（`PRECOMBAT-STANDARD-API` `bc698fccdcb14dceaa0d8e732d3996b5` 5.6s、`PRECOMBAT-DREAD-ROUTING` `995eb80905934581a7efe9e5298c22e0` 直退 `LegacyProcess` 77.8s、`PRECOMBAT-STS2SIM-BRIDGE-CONTRACT` `8990664ef4ea4d25bec167337d9e59c9`、`FUZZY` 55HP `ce64568e16bb440d8d9730084580a29e` 冷/热 450.2/425.9ms、`MAWLER` 无能力 55HP `ddef7da567a1433f974ef6d7546db4b8` 585.2/582.2ms、`MAWLER+INFLAME` 55HP `73ef927f614b490d8a05802bb0a9f31b` 600.6/600.3ms 战损 `10 < 15`、80HP `b037442fdf194a0aa0134c35a83a405b` 582.9/583.0ms）；无 `sts2sim/` 对照（`LegacyProcess`）6 项通过（`PRECOMBAT-STS2SIM-ABSENT-CONTRACT` `1095581b05c545ed943bb5f5c44c92c3`、`PRECOMBAT-STANDARD-API` `973275102add489892151f0a3f0e57de` 53.0s、`FUZZY` `14a932ba7a6440a399792f024bbf076c` 冷/热 25.44s/3.17s、`MAWLER` 55HP `8a594f8f13a64f7eacae19e0d8b26071` 26.24s/3.17s、`MAWLER+INFLAME` 55HP `f4125e34c3824fd3a885ad8e9bbfded4` 27.51s/4.19s 战损 15、80HP `dc81183af5ed4cf5be934aec21b0e94e` 29.48s/4.39s，子进程工作集约 `1.44–1.46 GiB`）。
- 范围：可见 Steam 会话帧时间、长时间游玩 GC 暂停采样与多 Mod 手动交互回归尚未在本轮无头矩阵中采样；Windows 上 `verify-refactor-boundaries.ps1` 因宿主机未安装 `rg` 而跳过（同一源码树已在 Linux 上通过 `search_files=266` 检查）。

## 0.51.9 愚者能力牌路线优化

能力牌登记、万事无忧的触发合同及愚者整场对照见[历史卷 32](archive/testing/volume-32.md)。

## 0.51.8 治疗补丁启动修复

治疗后置补丁加载时的求解、治疗原生差分和日志包回执合同见[历史卷 31](archive/testing/volume-31.md)。

## 0.51.7 薄荷尖塔兼容与诊断取证

真实薄荷尖塔与 BaseLib 的原生差分、诊断 ZIP 和上传回执合同见[历史卷 30](archive/testing/volume-30.md)。

## 0.51.6 社区 PR 与工具整合

原生差分、离线合同、独立检查和测试工具的复跑入口见[历史卷 29](archive/testing/volume-29.md)。

## 0.51.5 结算顺序与运行时交接

定向原生差分、第二回合续用、便携 GC 合同及未验证项见[历史卷 28](archive/testing/volume-28.md#0515-结算顺序与运行时交接)。

## 0.51.4 战斗逻辑与选牌续接

5 项定向原生验证、复跑入口和未验证项见[历史卷 27](archive/testing/volume-27.md#0514-战斗逻辑与选牌续接)。

## 0.51.3 同牌多减益与部署异常

本轮三项定向原生差分、复跑入口及待定位的原生飞刀异常见[历史卷 26](archive/testing/volume-26.md#0513-同牌多减益与部署异常)。

## 0.51.2 属性削减与诅咒持续时间

0.51.2 的族母、诅咒持续时间、仪式兽最小差分及验证范围见[历史卷 25](archive/testing/volume-25.md#0512-属性削减与诅咒持续时间)。

## 0.113 实例状态与 RNG 问题包

0.51.1 的最小差分、失败记录与验证范围见[历史卷 24](archive/testing/volume-24.md#0113-实例状态与-rng-问题包)。

## 0.113 Part 2：恐惧与极速

0.51.1 的领域、怪物、随机生成、六角色集成、续用与恢复证据见[历史卷 24](archive/testing/volume-24.md#0113-part-2恐惧与极速)。

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
