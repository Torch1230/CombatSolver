# CombatSolver 测试入口历史卷 12

## 0.48.1 全平台发布定稿（2026-10-03）

本次发布统一纳入下文已有修复与测试维护，复用对应源码上的手操用药反馈、倾泻原生差分、效果作用域相邻合同、金纸、压缩及费用层验证。当前定稿提交进行一次带私有连接配置的正式 Release 构建、五文件最小 ZIP 和本地部署，再由统一脚本发布三个渠道。实际完成以本轮命令及发布状态为准；没有执行完整发布门禁、整场质量验收或可见 Steam 测试。以下阶段证据保持原范围及未验证项。

## 0.48.1 手操药水折算与报告（2026-10-03）

`dotnet run --project tools/testing/checks/DiagnosticLogTests/DiagnosticLogTests.csproj -c Release -- --manual-projection-only` 通过9组门槛与序列化合同：一瓶省8/9/10 HP、两瓶省17/18 HP、无新增用药、原计划用药、零收益和战损上升。该命令只运行本次比较合同；日志工具原完整路径曾停在既有journal写入断言，不将它计为本轮完整通过。

`MANUAL-POTION-FEEDBACK` 的 `8c5dc107ca694cb4814f0b8f15990e22` Passed（24.0秒），穿过Controller会话与真实overlay结构：不足药水成本时不显示反馈，达到每瓶9 HP时显示，原无新增用药反馈仍显示；实例由启动器清理。复跑：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId MANUAL-POTION-FEEDBACK -VerifyControllerSessionLifecycle -TimeoutSeconds 120 -ExitOnComplete -CleanupInstanceOnExit`；Linux使用同名场景及GNU参数。

Q007规则夹具从公开包读取五份原始比较，在测试侧注入已知新增用药数量后，分类工具排除三份折算负值、保留两份无新增用药候选；没有原包恢复或完整质量验收。日志服务21项协议/Agent API合同通过，覆盖上传公式拒绝、负值/零/未知、筛选排序、CSV/JSON及旧包；Windows退出时SQLite临时文件清理有占用提示，服务器Python3.12容器同组合同正常通过。客户端Release零警告/错误，工具和文档门禁通过；可见游戏未验证。

## 倾泻与手空效果边界（2026-10-03）

`CASCADE-EMPTY-HAND-NATIVE` 使用报告 d9c106 的倾泻前牌堆顺序及 Shuffle 完整内部状态，单独保留倾泻+和无尽陀螺；无需恢复原包中的重生个体及历史 Power 施加者。未改行为源码上的 `cc37414e00274b6baaf0d877a60e3ac9` 出现原生/模拟手牌偏差；选择痛击的 `e3bac4a083574686b1e9d018ccc23f80` 复现 `NativeChoicePlanMismatchException`，计划 BASH+1、原生仅 STRIKE_IRONCLAD。修复后 `53ba69afa54544f3a1322b42367d5e90` Passed：嵌套坚毅原生页面完成，完整 continuation（有序牌堆、逐实例状态、Power、怪物和九条 RNG）一致；完整动作回放与执行检查点恢复、完成后 Fork、live 不变对账通过。该场景不运行 Solve，不带增量搜索开关，不代表原包整场部署通过。

复跑：`tools/testing/run-unattended-test.ps1 -ScenarioId CASCADE-EMPTY-HAND-NATIVE -EnemyCurrentHp 1000 -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit`；Linux 入口使用同名 scenario 的 GNU 风格参数。详细基线和局限见 [报告记录](../../issues/axebot-reports-20261003.md)。

`EFFECT-SCOPE-ADJACENT-CONTRACT` 的 `260511f79ef34d9393ef2d5f6b07f672` Passed（37.4秒）。同请求完成无尽陀螺配合Havoc、普通牌、重放牌的三项原生完整状态/RNG差分和效果内普通 Fork 拒绝；五种嵌套执行续接（Havoc/Cascade/DrawPrefix/Repeat/Decisions）的重捕获、全候选、DOP2、原生状态；手动自身选牌的兄弟修改/取消/异常与原生差分；九种药水的原生完整 continuation 和正式候选续接合同。复跑使用相同参数，仅替换 ScenarioId。未运行完整 Solve 或整场自动部署。

## 巨斧机器人近期报告（2026-10-03）

当前主线 `7df1f048` / 0.48.0 上建立三项失败基线。最终通过五项原生完整状态/RNG差分及一项根/Fork合同：金纸+音乐盒在回合末生成虚无复制牌；压缩+两张间隔状态牌的燃料顺序；子弹时间后的 FOLLY 临时星能清理；原力+逐张变牌；SEANCE 抽牌堆选牌变换；金纸延迟计数在根、live推进、父子/兄弟、指纹和续用中的隔离。

| 场景 | 最终 runId | 范围 |
|---|---|---|
| AXEBOT-JOSS-FINAL | `26b7755643d241f091c62f57c9f75a3a` | 原生回合末完整状态/RNG |
| AXEBOT-COMPACT-FINAL | `b45e21f166d042f1ae59079d001e375d` | 原生有序手牌/生成牌/状态/RNG |
| AXEBOT-FOLLY-FINAL | `896d94be19b9473e988aa997fc30c2c7` | 原生回合末完整费用层/状态/RNG |
| AXEBOT-TRANSFORM-FINAL | `8512e36aecd74d93a15f3adb34502190` | 原力与SEANCE两项原生完整差分 |
| AXEBOT-JOSS-DEFERRED-FORK | `e7c0363924e74211b043ebfaca50ac0c` | 根冻结、live推进、指纹/续用、父子/兄弟及逐分支消费 |

复跑在仓库根目录使用 `tools/testing/run-unattended-test.ps1`：前三项分别传 `coverage/fixtures/regressions/axebot/axebot-joss-late-ethereal.json`（IRONCLAD）、`axebot-compact-order.json`（DEFECT）、`axebot-folly-star-cleanup.json`（SILENT）至 `-MonsterMoveChecksPath`；变牌哨兵使用 `axebot-transform-sentinel.json`（IRONCLAD）。均为 `-EncounterId MockMonsterEncounter -TimeoutSeconds 120 -ExitOnComplete -CleanupInstanceOnExit`。Fork 合同使用精确 `-ScenarioId AXEBOT-JOSS-DEFERRED-FORK -RelicsPath coverage/fixtures/regressions/axebot/axebot-joss-deferred-relics.json`，其他参数相同。没有运行搜索，因此不带增量搜索开关。

Release 编译零警告/错误，`REFACTOR_BOUNDARIES_OK search_files=246`。该战斗修复阶段的覆盖工具曾拒绝既有 `PassedWithDocumentedBoundaries` / `PassedWithDocumentedPerformanceRegression` 状态，临时补足解析后又遇 `InfusedCore` 重复构造；当时临时修改已撤回。后续覆盖工具修复结果见下节。完整问题包部署、全场零重算和性能未验证；首轮倾泻包中途缺历史 applier ID 1、原生录制恢复停在输入26，最小重建请求120秒超时，未扩大时间帽；后续单动作修复见上节。详细失败基线及材料边界见 [报告记录](../../issues/axebot-reports-20261003.md)。本轮创建的无头实例均按清理开关删除。

## 文档维护验证（2026-10-03）

多人分支归档前的整理检查：主线 419 份 Markdown、1573 个本地链接和锚点通过；多人分支 400 份、1545 个链接通过；独立日志服务 14 份、30 个链接通过。当前指南与活动记录的长度门槛通过。主线与多人分支 Release 构建均为零警告/错误，主线 Windows 结构门禁通过（246 个 Search 文件）。后续第三方目录归并与多人规划归档后，主线 420 份、1574 个链接通过；归档分支停止维护。

两条分支 CoverageCatalog 实际生成，`--verify-state-fields --verify-branch-state-reads` 通过。主线仍有注能核心一个 active exact Hook 缺运行证据；多人分支该目录未报运行证据缺口。目录生成消费历史证据，本次没有运行原生战斗、性能或全量 verify，不扩大旧结果的适用范围。

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

## 工具目录与在线服务迁移验证（2026-10-03）

工具按九类职责归并，35 个保留项目全部完成 Release 编译。首次全量编译的六处旧源码路径、排除路径和 RitsuLib 引用问题已修正，仅重编失败项目。主项目 Release 零警告/错误；Windows 与 Git Bash 结构门禁通过（246 个 Search 文件）。CheckpointTool self-test 35 项断言、GC 检查 26 项及 Windows `test-headless-runtime.ps1 -ProfileOnly` 通过。Windows 矩阵替身检查通过实例复用、参数转发、异主/旧标记拒绝及取消适配；没有启动游戏或跑战斗/性能场景，未验证原生 Linux/macOS 启动。

在线监控迁至独立私有仓库。Node 23.9.0 下服务测试 22 项、日活 3 项、Edge 浏览器 10 项通过；文档链接检查通过。线上 17 个业务文件与新仓库一致，只同步文档、测试入口和来源记录，未重启、未修改数据库或客户端最新版本提示。

## coverage 目录归并验证（2026-10-03）

覆盖材料分为 catalog、evidence、fixtures、corpora 和 archive。15 份旧临时问题输入归档，4 个已结束等价批次的专用脚本退役；912 份输入逐项与整理前 JSON 对比，只改变路径，语义内容一致。两组历史对照的数字和原始哈希保留为当时证据。

CoverageCatalog 与 CheckpointTool Release 编译零警告/错误。覆盖目录从新入口实际生成，`--verify-state-fields --verify-branch-state-reads` 通过；十份生成快照与整理前对账，除材料路径外一致。3035 个 Hook 的分类与验证等级保持原样，仍有一个主动 Exact Hook 和一个必需写状态 Hook 缺运行证据；本轮没有修复或补跑该缺口。

覆盖结构/引用检查通过（932 份 JSON/save、747 份现行 fixture），文档链接与工具语法检查通过，Windows 结构门禁通过（246 个 Search 文件）。批量回放的空输入 Preflight 验证启动器定位与环境采集，不代表包恢复或战斗通过。未启动游戏，未重新跑历史等价批次或原生 Linux/macOS 启动。

## coverage 精简与 Testing 整理（2026-10-03）

删除410份数据：275份结束批次或重复fixture、116份不再使用的完整等价池请求/规格、15份旧临时配置、4份旧GC开发输入。当前522份数据含472份fixture与30份语料；归档保存摘要，原材料改用固定提交。502份保留输入的JSON语义与整理前一致；十份生成快照只改变路径与随独立任务更新的当前版本，3035个Hook分类及证据等级一致，仍有一个active exact Hook和一个必需写状态Hook缺运行证据。CoverageCatalog的状态字段与分支读取门禁通过。

Testing从218份C#归类为213份：删除6份旧调查分片，提取1份仍被调用的共享快照辅助，退役11个专用调查入口，减少约千行调查代码；公共框架、原生已知路线及机制合同保留。210份移动源码仅改变路径或保持原文，另外两份只删除调查方法/派发；并行任务的药水比较修改按其独立提交保留。主项目、CoverageCatalog、OfflineSearchHarness Release零警告/错误；Windows和Git Bash结构门禁、工具与文档检查通过。

`CASCADE-EMPTY-HAND-NATIVE` 的 `78c67f0b7c9641a4bf8a91948d982030` Passed（21.35秒）：原生完整状态/RNG、完整回放与执行续接、完成后Fork。协议批次先完成根合同 `4a8ad989965f45b6948d65fc854ab0bf`，坏ZIP的 `41c45d454ecc4cb796f19f644bdd6a1b` 按预期Failed且收到可复用ACK，随后 `PROCESS-DIAGNOSTICS` 的 `99194f27ffd048b0991c8f9cdd606730` Passed；三项同PID41896。独立Held请求 `17bd3d7dfff34f56907f50efcb86a927` 收到暂停ACK，释放后启动器正常退出。全部实例由启动器清理，临时验证代码与输入退出工作区。

首次从冷启动实例直接提交坏包 `e4df47031894476ea56b954fbac32c9e` 返回Failed但就绪ACK超时；另一次 `0aa2952965894323b707494340eb2987` 因并行任务改变manifest重启实例，不能算同进程恢复证据，也遇冷启动ACK超时。该边界未修复。Held临时请求最初使用低于启动器下限的16节点，参数校验拒绝后改为合法100节点；未扩大120秒请求上限。没有重跑完整战斗、历史60根池、可见游戏或原生Linux/macOS启动。
