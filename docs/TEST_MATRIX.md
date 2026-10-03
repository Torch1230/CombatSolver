# CombatSolver 测试入口

按改动选择最小验证层，方法见 [无人测试](HEADLESS_TESTING.md) 与 [社区验收](community/testing-guide.md)。以下记录保留取得证据时的源码和范围，不能视作本轮重新通过。

历史记录见 [归档索引](archive/testing/README.md)。

## Q003 检查点政策与比较（2026-10-03）

`CheckpointTool self-test` 的失败基线为 `different_switch_not_comparable:predictPotionReward`，最终 `archive_contract_tests_passed assertions=59`，含缺失/不同派生上下文、未来字段及预设标签比较。`REPLAY-BOUNDARY-CONTRACT` / `b4c0cbeb24964595b3aed0cbb74b4417` Passed：true/false 均覆盖与本机相反的四开关、精确预算、输入不变与缺项拒绝，并保留既有回放边界合同。

O008 / `f92387e90f7745cfb5ec74f55f46dcea` 验证有效 `:3` 检查点的 16 项事件、完整 continuation 与原生状态；`c3a2b0d8add74484ba310e5e539ac182` 从 `:1` 以固定短预算执行到第 3 回合，原生存活、`UnexpectedReplans:0`。只覆盖最早续用，不代表整场胜利。政策恢复后的 O007/O008/O010 开战搜索记录原预算与开关，O008 返回部分路线；有效第 3 回合搜索只找到死亡路线。Release 零警告/错误、结构门禁通过（246 个 Search 文件），自有实例已清理；未进行五主题整场验收或可见性能测试。复现命令、源码/DLL 来源和材料限制见 [Q003 记录](issues/q003-checkpoint-policy-20261003.md)。

## 倾泻与手空效果边界（2026-10-03）

`CASCADE-EMPTY-HAND-NATIVE` 使用报告 d9c106 的倾泻前牌堆顺序及 Shuffle 完整内部状态，单独保留倾泻+和无尽陀螺；无需恢复原包中的重生个体及历史 Power 施加者。未改行为源码上的 `cc37414e00274b6baaf0d877a60e3ac9` 出现原生/模拟手牌偏差；选择痛击的 `e3bac4a083574686b1e9d018ccc23f80` 复现 `NativeChoicePlanMismatchException`，计划 BASH+1、原生仅 STRIKE_IRONCLAD。修复后 `53ba69afa54544f3a1322b42367d5e90` Passed：嵌套坚毅原生页面完成，完整 continuation（有序牌堆、逐实例状态、Power、怪物和九条 RNG）一致；完整动作回放与执行检查点恢复、完成后 Fork、live 不变对账通过。该场景不运行 Solve，不带增量搜索开关，不代表原包整场部署通过。

复跑：`tools/run-unattended-test.ps1 -ScenarioId CASCADE-EMPTY-HAND-NATIVE -EnemyCurrentHp 1000 -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit`；Linux 入口使用同名 scenario 的 GNU 风格参数。详细基线和局限见 [报告记录](issues/axebot-reports-20261003.md)。

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

复跑在仓库根目录使用 `tools/run-unattended-test.ps1`：前三项分别传 `coverage/unattended/axebot-joss-late-ethereal.json`（IRONCLAD）、`axebot-compact-order.json`（DEFECT）、`axebot-folly-star-cleanup.json`（SILENT）至 `-MonsterMoveChecksPath`；变牌哨兵使用 `axebot-transform-sentinel.json`（IRONCLAD）。均为 `-EncounterId MockMonsterEncounter -TimeoutSeconds 120 -ExitOnComplete -CleanupInstanceOnExit`。Fork 合同使用精确 `-ScenarioId AXEBOT-JOSS-DEFERRED-FORK -RelicsPath coverage/unattended/axebot-joss-deferred-relics.json`，其他参数相同。没有运行搜索，因此不带增量搜索开关。

Release 编译零警告/错误，`REFACTOR_BOUNDARIES_OK search_files=246`。该战斗修复阶段的覆盖工具曾拒绝既有 `PassedWithDocumentedBoundaries` / `PassedWithDocumentedPerformanceRegression` 状态，临时补足解析后又遇 `InfusedCore` 重复构造；当时临时修改已撤回。后续覆盖工具修复结果见下节。完整问题包部署、全场零重算和性能未验证；首轮倾泻包中途缺历史 applier ID 1、原生录制恢复停在输入26，最小重建请求120秒超时，未扩大时间帽；后续单动作修复见上节。详细失败基线及材料边界见 [报告记录](issues/axebot-reports-20261003.md)。本轮创建的无头实例均按清理开关删除。

## 文档维护验证（2026-10-03）

多人分支归档前的整理检查：主线 419 份 Markdown、1573 个本地链接和锚点通过；多人分支 400 份、1545 个链接通过；独立日志服务 14 份、30 个链接通过。当前指南与活动记录的长度门槛通过。主线与多人分支 Release 构建均为零警告/错误，主线 Windows 结构门禁通过（246 个 Search 文件）。后续第三方目录归并与多人规划归档后，主线 420 份、1574 个链接通过；归档分支停止维护。

两条分支 CoverageCatalog 实际生成，`--verify-state-fields --verify-branch-state-reads` 通过。主线仍有注能核心一个 active exact Hook 缺运行证据；多人分支该目录未报运行证据缺口。目录生成消费历史证据，本次没有运行原生战斗、性能或全量 verify，不扩大旧结果的适用范围。
