# 日志导入与批量验证

新包和旧包共用入口，恢复、录制回放、预测和实际部署分别报告结果。当前版本以仓库 manifest 为准；历史问题包按保存材料和格式分别验收。

## 入口

Windows：

```powershell
pwsh -NoProfile -File tools/replay/run-checkpoint-batch.ps1 -InputPath <包.zip或目录> -ReplayMode Preflight -OutputDirectory .local/checkpoint-batch/preflight
pwsh -NoProfile -File tools/replay/run-checkpoint-batch.ps1 -InputPath <包.zip或目录> -ReplayMode RestoreOnly -Sts2GameRoot <游戏目录> -OutputDirectory .local/checkpoint-batch/restore
```

Linux：

```bash
./tools/replay/run-checkpoint-batch.sh <包.zip或目录> --mode Preflight --output .local/checkpoint-batch/preflight
./tools/replay/run-checkpoint-batch.sh <包.zip或目录> --mode RestoreOnly --game-root <游戏目录> --output .local/checkpoint-batch/restore
```

单包仍可使用 `run-unattended-test.ps1 -CheckpointArchivePath <ZIP> -EvidenceDirectory <目录>` 或 Linux 对应参数。`CheckpointTool` 与游戏共享相同的包读取代码。

| 模式 | 验证范围 |
| --- | --- |
| Preflight | 不启动游戏，校验四份材料配对、路径、索引、原生事件连续性，列出检查点 |
| RestoreOnly（默认） | 恢复选择的检查点，严格比较完整 ContinuationStamp 与原生二进制状态 |
| ReplayRecorded | 按记录输入从选定检查点重放，默认从开战开始；显式选择中途检查点时只验证对应录制前缀，不启动搜索 |
| SearchOnly | 已验证的根上运行求解，保存预测和指标 |
| DeploySolver | 从已验证根执行求解器路线，Instant/0 秒，断言计划外重算为 0，保存实际结果 |

所有问题包 fixture 默认选择 `start`，从明确的 combat_start 恢复；SearchOnly/DeploySolver 的开战选牌由求解器接管。`latest` 只用于显式要求从最近稳定可搜索位置继续的诊断，`end` / `recorded` 选择战斗结束位置，也可传稳定检查点 ID；模式不会暗中改写显式选择器。开战 RestoreOnly 先校验开战，再推进录制的首次可操作入口；动作中途和等待选牌时的导出仍保留上下文，但不会成为默认质量搜索起点。

`start` 的 RestoreOnly 会同时对账开战及首个可操作检查点，`readyCheckpointVerified` 表示后者的 ContinuationStamp 已通过；`comparisonScope=checkpoint`，不是整场回放结论。旧历史 `Y` 的两项/三项格式逐项比较全部已记录计数，新格式四项同样全部比较。

原生模型表 hash 不同只作诊断。原生二进制相同时仍可完整验证；旧包二进制不同且没有保存原编号映射时，不能使用本机表解码。若全部已记录战斗状态对账通过，返回 `restored_continuation`、`continuationVerified=true`，同时保留 `restorationVerified=false`、`nativeStateVerified=false` 和 `legacy_model_id_mapping_not_recorded`。这表示状态恢复可用，但不称完整原生恢复验收；批量工具也不会把该状态算作完整验证成功。录制回放对应状态为 `recorded_continuation_only`。

旧包未记录后来新增的 `FlameHp` 时，只允许迁移实际值为零的字段，且必须位于原生开局边界、首回合事件游标为零的可操作点，或已通过完整原生二进制状态校验。中途检查点缺少原生状态或编码不可比较时不适用。非零计数、位置错误、重复字段及任何已记录字段差异仍拒绝；迁移不补造历史或改变战斗状态。

## 旧包

0.48.0 前录制的选牌/continuation 可能缺少派生 `cost-state` 文本，而详细 `replay-state` 已保存完整费用层。仅在游戏模块匹配、目标材料包含全部有序费用字段时，允许按旧格式比较选择的已记录字段；目标必须同时通过原始 native-state、全部 continuation 和逐牌有序能量/星能费用对账。缺失字段、错误费用或失效条件仍失败；新格式始终精确比较，不修改原 ZIP 或生产状态键。结果用 `legacyCostLayersVerified`、`legacyChoiceCostEvents` 明确迁移范围。临时星能费用的嵌套字段单独解析费用值及出牌/回合末清除标志，保留顺序。

兼容旧 v1 索引、无索引 ZIP、已解压包和汇总 ZIP。保持 metadata、replay-state、native-state、run-state 原有目录，分别校验，不再同名覆盖。旧开战包从原生跑局存档加载，在首次抽牌前恢复检查点，到原始导出生命周期再对账。

0.33.8 起外层问题包采用 [报告协议 v2](BUG_REPORT_PROTOCOL.md)：根目录 report.json、diagnostics/、replay/。索引入口为 replay/checkpoint.json，旧包仍从 combat-solver/checkpoint.json 读取。检查点索引自身仍为 schemaVersion 2；路径变化不改变原生事件或恢复语义。批量下载含 index.json、反馈汇总.csv 和 reports/<报告ID>.zip；CheckpointTool 同时接受新旧汇总包与解压目录。

旧包没有完整输入记录时 `ReplayRecorded` 返回 `missing_native_event_recording`，仍可尝试 RestoreOnly、SearchOnly、DeploySolver。缺失的历史或复杂内部状态不能凭计数补造；导入不一致时保留首个差异。旧包兼容不代表所有历史包都已逐包验证。

旧包政策从 settings 和 searchProfiles 恢复。`missingPolicyFields` 列明缺项，搜索/部署需用 `-ReplayPolicyOverridePath <JSON>` / `--policy <JSON>` 明确补齐。当前覆盖字段：`potionPolicy`、`potionDirectives`、`growthBudgets`、`relicStrategyEnabled`、`relicCounterRules`、`brightestFlameMaxHpLossLimit`、`actTransitionBossHpStrategy`、`finalBossHpStrategy`、`acceptableBattleHpLoss`、`stopAtAcceptableBattleHpLoss`、`searchMaxDegreeOfParallelism`、`profile`、`fixedBudget`、`act3BossStrategy`、`useNoveltyPortfolio`、`useBeamWidthPortfolio`、`useEarlyTurnExploration`、`predictPotionReward`。旧政策中的 `shortProfile`、`deepProfile`、`forceShortOnly` 由兼容读取器归一化；不要将它们当作当前覆盖文件字段。覆盖文件保留在结果目录；原值、覆盖值和实际执行值分别记录。

`profile` 按完整不可变记录注入冻结搜索政策，包括排序与组合字段，不仅恢复 Beam、节点和时间。显式无人测试 CLI 的能量权重扰动仍优先于 profile 内的同项设置。profile 由当前请求持有，结束、失败或下一请求前清除；普通游戏政策不受影响。搜索组合、早期探索和药水奖励预测的已记录开关同时恢复，显式 false 不被本机默认值覆盖；未记录这些字段的旧包保留原有本机设置回退，不能据此宣称已补齐缺失政策。覆盖文件可显式补齐或选择诊断政策。检查实验是否生效时读取 `executedPolicy`，不能仅以 `policyOverrides` 中存在参数为依据。

## 批量与证据

默认一个隔离游戏进程串行执行，复用原版启动成本。实例和完整游戏/Mod 快照位于当前仓库 `.local/headless-instances/<实例>`，不写入用户目录；建局前可清理的输入失败经过静稳检查后复用，运行中失败、崩溃和超时终止所持有进程，下包重新启动。单请求默认上限 120 秒，不自动延长或提高预设；批次结束关闭工具持有的测试进程并删除实例。

`-Resume` / `--resume` 复用输入内容、检查点、模式、政策、工具、启动器、游戏和 Mod 构建身份一致的结果；失败默认也复用，加 `-RetryFailures` / `--retry-failures` 才重试。中断的 JSONL 尾记录另存，保留已完成项。每次重试有独立目录。

每请求保存 preflight、request、result、policy、timings、difference、game.log、日志截断信息、启动器输出和 batch-result。汇总实时写 results.jsonl，并更新 results.json、results.csv、results.md；每行明确列出 selector、检查点标签、稳定 ID 和事件游标，避免把中途起点误读成整场起点。材料不足、环境不匹配、恢复不一致、录制动作不一致、执行失败、超时、崩溃优先显示；存在预测数字不会掩盖技术失败。

清单 JSON 可为数组或 `{ "manifest": [...] }`，条目含 reportId 或 archivePath、note、originalLoss、manualLoss、comparisonCheckpointId。备注优先、已知减战损降序。表格中的原始数字默认视为报告预测；只有明确同根同区间时才计算预测差距。

整场“优化后相对于人工”和“是否更优”只在同包的纯玩家录制路线已重放验证、同开战起点、同政策且求解器实际存活完战时填写。混合录制保留 relativeToRecorded，预测放独立字段。回合号不决定对照范围。

## 采集协议

v2 索引保存稳定战斗/检查点 ID、永久递增编号、原生事件位置、材料路径、实际搜索政策与计划、游戏及 Mod 构建身份。材料齐全与恢复验证通过是独立字段。保留开战、首次可操作、比较根、首次错误前、最近可操作和结束等关键角色，完整快照最多六份。

原生战前 SerializableRun 加原生 GameAction、PlayerChoice、Hook、Resume 事件是新包恢复来源。动作使用游戏原生实例编号；选择补充候选顺序、实例编号、升级和状态键。系统动作自动执行并核对，外部输入由测试器注入。回放首个差异保存字段、预期/实际及动作窗口。

主线程冻结输入，后台顺序写临时文件；事件积压上限 8 MiB、事件文件上限 32 MiB，快照后台积压最多六份，单份四材料总计最多 8 MiB。快照只保留最近 16 条诊断历史，完整历史由原生事件重建。相同原生事件位置和完整状态的搜索结果复用已有快照，轻量搜索结果独立保留。临时文件随录制所有者退出关闭删除。

结构化搜索结果另限 8 MiB/2048 条，候选上下文限制 256 项，错误详细信息保留前 16 条并记录额外计数。达到上限进入显式诊断状态。

采集失败、超限和截断显式写入索引及 manifest；已有材料仍可导出，但标记 diagnosticOnly，预检拒绝称为完整可恢复包。联系人字段采用统一设置白名单排除。现有上传 128 MiB 限制保留。

## 验证

完整预测路线可用无人测试场景 `CHECKPOINT-RECORDED-PLAN-DEPLOYMENT` 验证：传原 ZIP、含获胜预测的检查点 selector、`ReplayMode=DeploySolver`、显式政策文件和 EvidenceDirectory。测试按事件游标及起始回合选择最后一条完整获胜预测，保留所有动作、完整牌身份与选择，逐步对账增量/完整回放，再通过正常原生部署入口执行。断言真实胜负、战损、药水数量/身份及终局回合；后台搜索代次跨战斗结束保留，用于确认执行期间没有额外搜索。此模式证明保存预测的可执行性，不能称为纯玩家录制通关或搜索自主发现。

历史预测数字与当前严格动作回放不同时，先保留该失败及 `recorded-plan-replayed-outcome.json`，再用显式诊断场景 `CHECKPOINT-RECORDED-PLAN-CURRENT-OUTCOME` 比较当前预测和原生结果。它仍要求完整根、所有原始动作身份、逐步增量/完整状态等价、完整胜利及零额外搜索/重算；只有历史战损/终局回合作为比较数据，不要求与当前预测相等。`recordedPredictionOutcomeMatched=false` 和专用 comparisonScope 明确保留历史差异，不能将此诊断 Passed 当作旧数字成立或搜索自主发现。普通部署和路径场景继续严格拒绝历史预测差异。

同一输入使用 `CHECKPOINT-RECORDED-PLAN-PATH`、`ReplayMode=SearchOnly`，将冻结路线作为只读观察目标运行正常协调器；不把参照动作注入候选或评分。`RecordedPrediction-path-trace.json` 保存准确动作和完整状态的生成、转置、保留与展开事件，观察器丢事件时显式失败。路径诊断耗时不能作正常性能证据。两种模式都要求原生录制和对应检查点的完整获胜预测，旧身份不匹配时失败，不删去费用层或改写录制内容。Power Potion 等已录制前缀与未来预测用药分别计算，不能漏掉前缀消耗。

需要定位药水生成选项的剪枝时，使用 `CHECKPOINT-RECORDED-PLAN-POTION-RETENTION-PATH`（同样 `SearchOnly`）。它对严格回放路线中的首个带牌选择的主动药水动作观察完整外层剪枝池，输出排名、路由签名、选项代表、必保/路由/选中位置和估值；不增加生产容量，也不注入路线。无此药水选择或观察事件溢出时明确失败。相近前缀存活不等于后缀可替代，须另外严格验证后续动作与终局。

一般动作边界使用 `CHECKPOINT-RECORDED-PLAN-RETENTION-PATH-N`，`N`为严格回放路线从1开始的动作序号；例如末尾`-8`观察第8步所在完整剪枝池。非正整数或超过路线长度明确失败。它与药水场景共用同一只读观察器及严格回放，不改变候选、评分、预算或默认路径场景。

当派生前缀改变当前终局数字时，可显式使用 `CHECKPOINT-RECORDED-PLAN-CURRENT-PATH` 或 `CHECKPOINT-RECORDED-PLAN-CURRENT-RETENTION-PATH-N`，同样为 `SearchOnly`。两者保留历史数字不符标志，只按当前严格获胜动作构建观察目标，不改写归档预测，也不证明自主发现或原生部署。整池的 `Transition` 仅复制父子快照的手牌、敌人生命/格挡、攻击价值和距路由选择的动作数；无路由选择时动作数为 `int.MaxValue`。默认关闭观察器时不创建这些诊断值。

`SearchScope` 复制每个搜索成员的有效药水政策、胜利所需的最少显式用药数、最大显式用药数、能力偏好标记及用途；所有者开始搜索时冻结，展开worker随SolverId继承同一纯值，不以worker局部参数替代；不经过Solve的诊断构造器也分配独立身份。同一SolverId必须约束一致。允许用药层里的零药中间节点，不一定能作为该成员的完整胜利：必须对照成员最少用药要求，不能把跨层的前缀保留当成零药目标仍在搜索。

原生录制顺序与旧预测不同时，可以使用 `CHECKPOINT-NATIVE-INPUT-IDENTITIES`、`ReplayMode=RestoreOnly` 和明确的中途检查点。严格执行原生前缀后，`native-input-identities.json` 输出每次主动输入前的完整手牌身份、同名／同状态出现序号、实际选中手牌位置及敌方编号；采集前后续用戳必须一致，最终原生状态必须已严格验证，编码不可比较时失败。它只帮助构造独立参照夹具，不能把身份采集当作模拟等价、完整胜利或自主搜索证据；原始包保持不变，派生动作须重新逐步严格回放。

Q002 专属固定路线/成员诊断已在任务收尾移除；失败证据与[历史用法](archive/testing/q002-pre-0492-validation-20261004.md#一次性诊断入口的历史用法)保留，当前使用上述通用保存预测及正常搜索/部署入口。

包协议与顺序文件：`dotnet run --project tools/replay/CheckpointTool/CheckpointTool.csproj -c Release -- self-test`。边界门禁使用 `verify-refactor-boundaries.ps1` / `.sh`。

可见采集测量：`run-visible-steam-benchmark.ps1 -LoggingFixture -TimeoutSeconds 120 -EvidenceDirectory <目录>`，Linux 为 `--logging-fixture --timeout-seconds 120 --evidence-directory <目录>`。该短原生战斗另存 ZIP，索引提供采集累计/最大时间和积压，session.json 提供材料大小。headless 只用于导入和吞吐测量。具体证据及未覆盖场景见 TEST_MATRIX.md。

可见原包恢复可使用同一脚本的 `-CheckpointArchivePath <ZIP> -CheckpointSelector start -ReplayMode RestoreOnly`；Linux 提供同名 kebab-case 参数。程序集清单差异只写入 `replayVerification.modEnvironmentComparison`，逐项列出 `missing`、`extra`、`build_changed`，不凭清单不同中止恢复，也不把差异自动认定为冲突。清单包含外观 Mod、加载器和依赖库，不能代表战斗语义。

恢复继续执行游戏构建、模型解码、原生事件、完整 ContinuationStamp 与可比较的 native-state 校验。缺少实际使用的模型、事件无法解码或状态不同仍按具体错误失败；只有完整校验通过才标记 `restorationVerified=true`。求解器已有的第三方不兼容门禁保持独立。

游戏模块标识（MVID）仅记录在 `replayVerification.gameModuleComparison` 的 `expected`、`actual` 与 `matches` 中，不因标识不同提前拒绝恢复。同一版本的不同平台构建可以有不同MVID；兼容性由实际模型/事件解码和状态对账决定，标识相同也不跳过对账。旧包缺少模型编号映射且编号表不同时，原生二进制仍标为不可比较，只有全部已记录ContinuationStamp字段匹配才报告 `restored_continuation`，不宣称完整原生状态恢复。
