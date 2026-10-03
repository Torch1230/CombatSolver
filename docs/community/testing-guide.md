# 夹具与开发脚手架指南

在仓库根目录执行命令。环境见 [CONTRIBUTING.md](../../CONTRIBUTING.md)，职责见 [ARCHITECTURE.md](../ARCHITECTURE.md)。下面的命令按当前参数与源码入口静态核对；本次社区资料整理没有运行游戏复现。

## 玩家问题包

代表 ZIP 放 `.local/issue-bundles/<issue>/raw/`。先读 `report.json`，再按 `diagnostics/logs/index.json` 指向的本场 JSONL 查首个异常、traceId、动作和状态差异；`history.json` 是历史摘要。新回放索引为 `replay/checkpoint.json`，现有读取器也支持旧包。

```powershell
pwsh -NoProfile -File tools/replay/run-checkpoint-batch.ps1 -InputPath .local/issue-bundles/CASE/raw -ReplayMode Preflight -OutputDirectory .local/checkpoint-batch/CASE-preflight
pwsh -NoProfile -File tools/replay/run-checkpoint-batch.ps1 -InputPath .local/issue-bundles/CASE/raw -ReplayMode RestoreOnly -CheckpointSelector start -Sts2GameRoot '<游戏目录>' -OutputDirectory .local/checkpoint-batch/CASE-restore
```

```bash
./tools/replay/run-checkpoint-batch.sh .local/issue-bundles/CASE/raw --mode Preflight --output .local/checkpoint-batch/CASE-preflight
./tools/replay/run-checkpoint-batch.sh .local/issue-bundles/CASE/raw --mode RestoreOnly --selector start --game-root '<游戏目录>' --output .local/checkpoint-batch/CASE-restore
```

| 模式 | 验证范围 |
| --- | --- |
| `Preflight` | 静态材料配对、索引与事件连续性 |
| `RestoreOnly` | 选定检查点恢复与状态对账；旧包可能只有 ContinuationStamp 验证 |
| `ReplayRecorded` | 选定起点后的录制输入 |
| `SearchOnly` | 恢复后的根上搜索，保存预测与指标 |
| `DeploySolver` | 原生执行求解器路线，保存实际结果与重算情况 |

质量比较从 `start` 指向的 `combat_start` 开始；中途检查点只说明对应区间。药水、成长、遗物计数、治疗保护和预算保持可比，缺失政策用显式覆盖文件补齐。预测 `before − after` 是排序线索；人工路线合法、同根同区间、求解器实际存活完战后，才能报告整场收益，额外药水成本单独记录。

批量工具负责批末退出与实例清理，保存逐请求证据和 JSONL/JSON/CSV/Markdown。`-Resume` / `--resume` 复用身份一致的结果；确需重试失败项时使用 `-RetryFailures` / `--retry-failures`。详细限制见 [CHECKPOINT_REPLAY.md](../CHECKPOINT_REPLAY.md)。

## 最小差分夹具

`coverage/fixtures/` 包含多种 JSON：卡牌列表、怪物动作检查、药水检查、生成配置、完整请求。先核对消费者。`MonsterMoveChecksPath` 接受怪物动作检查数组，`CardsPath` 接受牌组注入数组；协议在 [UnattendedTestProtocol.cs](../../src/Testing/Host/UnattendedTestProtocol.cs)。

已有[尖啸生命周期夹具](../../coverage/fixtures/cards/card-on-play-batch-035-piercing-wail-lifecycle.json)设置招式、插入一张牌、执行并检查力量恢复与能力移除，通过 `RunMonsterMoveDifferentialAsync` 比较实际与模拟状态：

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-035-PIERCING-WAIL -CharacterId IRONCLAD -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage/fixtures/cards/card-on-play-batch-035-piercing-wail-lifecycle.json -Sts2GameRoot '<游戏目录>' -RitsuWorkshopRoot '<RitsuLib工坊目录>' -EvidenceDirectory .local/community-check/wail -TimeoutSeconds 120 -CleanupInstanceOnExit
```

```bash
./tools/testing/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-035-PIERCING-WAIL --character-id IRONCLAD --enemy-current-hp 50 --monster-move-checks-path coverage/fixtures/cards/card-on-play-batch-035-piercing-wail-lifecycle.json --sts2-game-root '<游戏目录>' --ritsu-workshop-root '<RitsuLib工坊目录>' --evidence-directory .local/community-check/wail --timeout-seconds 120 --cleanup-instance-on-exit
```

复制已有夹具到自己的忽略目录，缩小到首个错误动作，固定状态与断言。修改前实际失败、修改后同输入通过，再提交有价值的最小 JSON。核对 `EncounterId`（遭遇）与 `MonsterId`（单怪模型）。同 ID 多实例、目标、牌堆顺序、私有状态和 RNG 一并保留；只有实际启动搜索的夹具才启用增量搜索等价验证。

特殊场景按 `ScenarioId` 显式派发，普通动作检查数组走通用差分入口。新增 C# 场景按以下职责落地：

| 责任 | 当前入口（`src/Testing/`） |
| --- | --- |
| 请求字段 | `UnattendedTestProtocol.cs` |
| 请求循环、每请求开关 | `UnattendedTestRunner.ProtocolHost.cs` |
| 建局、加载与注入 | `UnattendedTestRunner.ScenarioBuilder.cs`、fixture helper |
| 差分、搜索与部署 | `UnattendedTestRunner.Executor.cs` |
| 条件与结果判定 | `UnattendedTestRunner.Assertions*.cs` |
| Passed/Held/Failed 输出 | `UnattendedTestRunner.Writer.cs` |

先找已有入口。fixture helper 提供动作与观察，结果由统一 Writer 输出；职责迁移同步架构地图和结构门禁。

## 生成场景

复制 `tools/search/GeneratedCombatScenarios/specified.json`，指定角色、遭遇、种子、牌、遗物、药水和 `Setup/Search/Deploy`，用于构造已定位机制的回归。

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -GeneratedScenarioPath tools/search/GeneratedCombatScenarios/specified.json -ScenarioId COMMUNITY-GENERATED -Sts2GameRoot '<游戏目录>' -RitsuWorkshopRoot '<RitsuLib工坊目录>' -EvidenceDirectory .local/community-generated -TimeoutSeconds 120 -CleanupInstanceOnExit
```

```bash
./tools/testing/run-unattended-test.sh --generated-scenario-path tools/search/GeneratedCombatScenarios/specified.json --scenario-id COMMUNITY-GENERATED --sts2-game-root '<游戏目录>' --ritsu-workshop-root '<RitsuLib工坊目录>' --evidence-directory .local/community-generated --timeout-seconds 120 --cleanup-instance-on-exit
```

随机生成后用 `generated-scenario.resolved.json` 重跑并保存实际开局与政策；仅同种子不足以证明同根。格式见 [GENERATED_COMBAT_SCENARIOS.md](../GENERATED_COMBAT_SCENARIOS.md)。

## C# 策略脚本与常驻开发会话

[tools/strategy-example.cs](../../tools/replay/strategy-example.cs) 实现 `IDevelopmentSearchStrategy`，提供 `Rank`（评分）、`Prioritize`（动作优先级）、`Retain`（有界保路代表）、`OrganizeMembers`（既有组合成员编排）。复制到 `.local/`，编辑脚本并传数值 JSON 参数。新战斗状态或模拟原语修改主程序；终局胜负、战损与资源排序继续由主程序决定。

```powershell
New-Item -ItemType Directory -Force .local | Out-Null
Copy-Item tools/replay/strategy-example.cs .local/community-strategy.cs
'{"persistentBuffWeight":2}' | Set-Content -Encoding utf8 .local/community-parameters.json
pwsh -NoProfile -File tools/replay/strategy-session.ps1 start COMMUNITY --game-root '<游戏目录>' --ritsu-root '<RitsuLib工坊目录>' --no-monitor
pwsh -NoProfile -File tools/replay/strategy-session.ps1 run COMMUNITY '.local/issue-bundles/CASE/raw/REPORT.zip' --script .local/community-strategy.cs --params .local/community-parameters.json
pwsh -NoProfile -File tools/replay/strategy-session.ps1 stop COMMUNITY
pwsh -NoProfile -File tools/replay/remove-strategy-instance.ps1 -Instance strategy-development -SourceGameRoot '<游戏目录>'
```

Linux 用 `tools/replay/strategy-session.sh` 的相同子命令与选项；收尾用 `tools/replay/remove-strategy-instance.sh strategy-development`。

专项会话默认普通请求 180 秒，显式 `--early-turns` 的追加搜索有自己的时限，用于策略探索。常规贡献验证按 120 秒最小请求和固定测试预算选择。所有会话共用 `strategy-development`，同主机协调使用；主 DLL 或依赖改变后 stop 再 start。热更新只在下一请求生效，当前请求保存脚本、参数和 DLL 身份。stop 保留副本，任务结束用专用移除入口清理。

脚本异常、超时与恢复失败写入结果。示例的单卡条件演示 API；正式改进按通用效果与兑现时机提炼。玩家路线帮助判断候选在哪个阶段消失。完整口径见[常驻策略迭代会话](../strategy/development-session.md)。

## 离线宿主与收口

`tools/search/OfflineSearchHarness/` 在普通 .NET 进程量搜索指标，支持生成场景与新跑局。玩家 checkpoint 恢复使用游戏入口；正确性验收走严格差分或原生部署，见 [OFFLINE_SEARCH_HARNESS.md](../OFFLINE_SEARCH_HARNESS.md)。

普通语义修复选单效果差分；跨回合/Fork/续用选最小边界；搜索优化固定目标和回归哨兵。完整部署采用 Instant/0 秒并核对计划外重算。PR 列代码版本、输入、命令、状态、比较区间和未验证项；实例生命周期见 [HEADLESS_TESTING.md](../HEADLESS_TESTING.md)。

## 最终 PR 哨兵验收

认领者自行推进定位、实现和验证，建议每批集中在一个 PR 内，范围与方案写进同一 PR 供审阅。可以提前开 Draft PR，按主题组织 commit、进度和验证记录，全批完成后转为 Ready for review。跨模块、镜像登记、模拟生命周期或搜索架构属于完成主题所需的实现范围时，按既有职责边界直接处理。任务目标存在实质歧义时再澄清；阶段进度和方案讨论采用异步记录。

涉及搜索或模拟执行路径的最终 PR，使用目标最小证据和少量固定代表哨兵约束结果：

| 验收项 | 提交的证据 | 通过条件 |
| --- | --- | --- |
| 目标正确性 | 同输入修改前失败/修改后通过；完整相关状态与 RNG 对账 | 根因得到验证，原版语义、部署与状态所有权正确 |
| 质量无退化 | 基线已正确执行的哨兵，固定开局、牌序/RNG、药水/成长政策和搜索预算 | 胜负、战损、药水及政策排序无退化；新增失败、部署漂移和计划外重算单列 |
| 耗时无明显增加 | 同机、同并行度、同预热与诊断模式的原始搜索耗时、展开/转移量和成对样本 | 无超出同批基线波动的稳定耗时增加；不能通过减少有效搜索或改变预算换取通过 |

贡献者根据受影响调用链固定哨兵：普通修复覆盖相邻正确路径，搜索策略改动增加一个代表性的未改目标。最终候选做一次必要的成对对照，记录波动；已经通过且输入未变的验证继续复用，避免每个实现步骤重复整场测试。性能采样采用正常搜索模式，增量回放验证单独作为正确性证据；搜索耗时可以使用 headless，显示帧时间和 FPS 结论另需可见游戏数据。

固定时间预算已经耗尽的样本，单看相同 wall time 不能证明性能未退化，还须核对实际工作量与路线质量。旧基线因 Bug 没有正确执行的样本用于证明修复，耗时和质量回归另选正确基线。哨兵通过的结论限定在列出的输入和机制范围；超时、失败或缺少环境明确记录为未验证，现有证据持续提交到同一批次 Draft PR。
