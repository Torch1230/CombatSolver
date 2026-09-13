# 日志导入与批量验证

当前版本 0.30.0。新包和旧包共用入口，恢复、录制回放、预测和实际部署分别报告结果。本轮修改日志及测试工具，保持求解策略。

## 入口

Windows：

```powershell
pwsh -NoProfile -File tools/run-checkpoint-batch.ps1 -InputPath <包.zip或目录> -ReplayMode Preflight -OutputDirectory .local/checkpoint-batch/preflight
pwsh -NoProfile -File tools/run-checkpoint-batch.ps1 -InputPath <包.zip或目录> -CheckpointSelector start -ReplayMode RestoreOnly -Sts2GameRoot <游戏目录> -OutputDirectory .local/checkpoint-batch/restore
```

Linux：

```bash
./tools/run-checkpoint-batch.sh <包.zip或目录> --mode Preflight --output .local/checkpoint-batch/preflight
./tools/run-checkpoint-batch.sh <包.zip或目录> --selector start --mode RestoreOnly --game-root <游戏目录> --output .local/checkpoint-batch/restore
```

单包仍可使用 `run-unattended-test.ps1 -CheckpointArchivePath <ZIP> -EvidenceDirectory <目录>` 或 Linux 对应参数。`CheckpointTool` 与游戏共享相同的包读取代码。

| 模式 | 验证范围 |
| --- | --- |
| Preflight | 不启动游戏，校验四份材料配对、路径、索引、原生事件连续性，列出检查点 |
| RestoreOnly（默认） | 恢复选择的检查点，严格比较完整 ContinuationStamp 与原生二进制状态 |
| ReplayRecorded | 按记录输入重放；默认选择结束检查点，否则只验证录制前缀，不启动搜索 |
| SearchOnly | 已验证的根上运行求解，保存预测和指标 |
| DeploySolver | 从已验证根执行求解器路线，Instant/0 秒，断言计划外重算为 0，保存实际结果 |

选择器 `latest` 是最近稳定可搜索位置，`start` 是明确开战位置，`end` 是战斗结束位置，也可传稳定检查点 ID。开战 RestoreOnly 先校验开战，再推进录制的首次可操作入口；SearchOnly/DeploySolver 的开战选牌由求解器接管。动作中途和等待选牌时的导出保留上下文，默认入口指向此前稳定位置。

`start` 的 RestoreOnly 会同时对账开战及首个可操作检查点，`readyCheckpointVerified` 表示后者的 ContinuationStamp 已通过；`comparisonScope=checkpoint`，不是整场回放结论。旧历史 `Y` 的两项/三项格式逐项比较全部已记录计数，新格式四项同样全部比较。

原生模型表 hash 不同只作诊断。原生二进制相同时仍可完整验证；旧包二进制不同且没有保存原编号映射时，不能使用本机表解码。若全部已记录战斗状态对账通过，返回 `restored_continuation`、`continuationVerified=true`，同时保留 `restorationVerified=false`、`nativeStateVerified=false` 和 `legacy_model_id_mapping_not_recorded`。这表示状态恢复可用，但不称完整原生恢复验收；批量工具也不会把该状态算作完整验证成功。录制回放对应状态为 `recorded_continuation_only`。

旧包未记录后来新增的 `FlameHp` 时，只允许迁移实际值为零的字段，且必须位于原生开局边界、首回合事件游标为零的可操作点，或已通过完整原生二进制状态校验。中途检查点缺少原生状态或编码不可比较时不适用。非零计数、位置错误、重复字段及任何已记录字段差异仍拒绝；迁移不补造历史或改变战斗状态。

## 旧包

兼容旧 v1 索引、无索引 ZIP、已解压包和汇总 ZIP。保持 metadata、replay-state、native-state、run-state 原有目录，分别校验，不再同名覆盖。旧开战包从原生跑局存档加载，在首次抽牌前恢复检查点，到原始导出生命周期再对账。

0.33.8 起外层问题包采用 [报告协议 v2](BUG_REPORT_PROTOCOL.md)：根目录 report.json、diagnostics/、replay/。索引入口为 replay/checkpoint.json，旧包仍从 combat-solver/checkpoint.json 读取。检查点索引自身仍为 schemaVersion 2；路径变化不改变原生事件或恢复语义。批量下载含 index.json、反馈汇总.csv 和 reports/<报告ID>.zip；CheckpointTool 同时接受新旧汇总包与解压目录。

旧包没有完整输入记录时 `ReplayRecorded` 返回 `missing_native_event_recording`，仍可尝试 RestoreOnly、SearchOnly、DeploySolver。缺失的历史或复杂内部状态不能凭计数补造；导入不一致时保留首个差异。旧包兼容不代表所有历史包都已逐包验证。

旧包政策从 settings 和 searchProfiles 恢复。`missingPolicyFields` 列明缺项，搜索/部署需用 `-ReplayPolicyOverridePath <JSON>` / `--policy <JSON>` 明确补齐。允许字段：`potionPolicy`、`potionDirectives`、`actTransitionBossHpStrategy`、`finalBossHpStrategy`、`acceptableBattleHpLoss`、`searchMaxDegreeOfParallelism`、`shortProfile`、`deepProfile`、`forceShortOnly`。覆盖文件保留在结果目录；原值、覆盖值和实际执行值分别记录。

## 批量与证据

默认一个隔离游戏进程串行执行，复用原版启动成本。建局前可清理的输入失败经过静稳检查后复用；运行中失败、崩溃和超时终止所持有进程，下包重新启动。单请求默认上限 120 秒，不自动延长或提高预设；批次结束关闭工具持有的测试进程。

`-Resume` / `--resume` 复用输入内容、检查点、模式、政策、工具、启动器、游戏和 Mod 构建身份一致的结果；失败默认也复用，加 `-RetryFailures` / `--retry-failures` 才重试。中断的 JSONL 尾记录另存，保留已完成项。每次重试有独立目录。

每请求保存 preflight、request、result、policy、timings、difference、game.log、日志截断信息、启动器输出和 batch-result。汇总实时写 results.jsonl，并更新 results.json、results.csv、results.md。材料不足、环境不匹配、恢复不一致、录制动作不一致、执行失败、超时、崩溃优先显示；存在预测数字不会掩盖技术失败。

清单 JSON 可为数组或 `{ "manifest": [...] }`，条目含 reportId 或 archivePath、note、originalLoss、manualLoss、comparisonCheckpointId。备注优先、已知减战损降序。表格中的原始数字默认视为报告预测；只有明确同根同区间时才计算预测差距。

整场“优化后相对于人工”和“是否更优”只在同包的纯玩家录制路线已重放验证、同开战起点、同政策且求解器实际存活完战时填写。混合录制保留 relativeToRecorded，预测放独立字段。回合号不决定对照范围。

## 采集协议

v2 索引保存稳定战斗/检查点 ID、永久递增编号、原生事件位置、材料路径、实际搜索政策与计划、游戏及 Mod 构建身份。材料齐全与恢复验证通过是独立字段。保留开战、首次可操作、比较根、首次错误前、最近可操作和结束等关键角色，完整快照最多六份。

原生战前 SerializableRun 加原生 GameAction、PlayerChoice、Hook、Resume 事件是新包恢复来源。动作使用游戏原生实例编号；选择补充候选顺序、实例编号、升级和状态键。系统动作自动执行并核对，外部输入由测试器注入。回放首个差异保存字段、预期/实际及动作窗口。

主线程冻结输入，后台顺序写临时文件；事件积压上限 8 MiB、事件文件上限 32 MiB，快照后台积压最多六份，单份四材料总计最多 8 MiB。快照只保留最近 16 条诊断历史，完整历史由原生事件重建。相同原生事件位置和完整状态的搜索结果复用已有快照，轻量搜索结果独立保留。临时文件随录制所有者退出关闭删除。

结构化搜索结果另限 8 MiB/2048 条，候选上下文限制 256 项，错误详细信息保留前 16 条并记录额外计数。达到上限进入显式诊断状态。

采集失败、超限和截断显式写入索引及 manifest；已有材料仍可导出，但标记 diagnosticOnly，预检拒绝称为完整可恢复包。联系人字段采用统一设置白名单排除。现有上传 128 MiB 限制保留。

## 验证

包协议与顺序文件：`dotnet run --project tools/CheckpointTool/CheckpointTool.csproj -c Release -- self-test`。边界门禁使用 `verify-refactor-boundaries.ps1` / `.sh`。

可见采集测量：`run-visible-steam-benchmark.ps1 -LoggingFixture -TimeoutSeconds 120 -EvidenceDirectory <目录>`，Linux 为 `--logging-fixture --timeout-seconds 120 --evidence-directory <目录>`。该短原生战斗另存 ZIP，索引提供采集累计/最大时间和积压，session.json 提供材料大小。headless 只用于导入和吞吐测量。具体证据及未覆盖场景见 TEST_MATRIX.md。

可见原包恢复可使用同一脚本的 `-CheckpointArchivePath <ZIP> -CheckpointSelector start -ReplayMode RestoreOnly`；Linux 提供同名 kebab-case 参数。程序集清单差异只写入 `replayVerification.modEnvironmentComparison`，逐项列出 `missing`、`extra`、`build_changed`，不凭清单不同中止恢复，也不把差异自动认定为冲突。清单包含外观 Mod、加载器和依赖库，不能代表战斗语义。

恢复继续执行游戏构建、模型解码、原生事件、完整 ContinuationStamp 与可比较的 native-state 校验。缺少实际使用的模型、事件无法解码或状态不同仍按具体错误失败；只有完整校验通过才标记 `restorationVerified=true`。求解器已有的第三方不兼容门禁保持独立。
