# 玩家世界线优化交接 Prompt（2026-09-06）

请接手 CombatSolver 的策略优化。先读取下面的规则和证据，恢复包 1、包 2 已验收的策略改动，再按清单快速推进。不要重新做整套计划，不要再次花大量时间纠缠少数几点战损。

## 工作位置与分支

- 工作目录：`D:\Desktop\sts2mod\CombatSolver`。
- 本次已从 `d8e7648` 创建并切换到 `strategy/worldline-resume`，后续在此分支工作，不在 `main` 修改或直接推送 `main`。
- 起点提交为 `Release 0.31.2 with community contributions and precombat API`。当前已有新的日志回放、缓存和社区贡献，必须保留。不要退回旧版本覆盖整个文件。
- 本交接只新增文档并建立分支，没有恢复策略代码，也没有重新运行包 1、包 2。下文结果都是历史证据。
- 根目录未跟踪的发布 ZIP 和 `outputs/` 是已有产物，不删除、不纳入提交。
- 完整读取 `AGENTS.md`。使用 `.agents/skills/issue-bundle-triage/SKILL.md`、`strategy-replay-iteration/SKILL.md`、`search-performance-optimization/SKILL.md`；语义修改另读 `combat-semantic-change/SKILL.md`，职责重构另读 `architecture-boundary-refactor/SKILL.md`，推送和发版读 `release-gate/SKILL.md`。最新用户要求优先于历史文档中的旧目标。

## 最新目标：达到即可停，达不到就跳过

用户最新原话：

> 对于死局，优化到能活并且得到不错的战损就行。软baseline是玩家优化的战损的一半。得到这个目标就可以停下了。如果这个也追不上就跳过。

- 原来死亡的路线，优先修到实际存活完战且战损合理；不再要求追平人工。死亡罚分不能当成真实 HP 战损计算改善比例。“不错的战损”没有额外指定数值，结合实际剩余 HP、资源和人工路线判断并记录，不自行加一个必须追平人工的门槛。
- 可按同范围 HP 对照的包，软目标是达到玩家**减损幅度的一半**，不是将玩家最终战损减半。原始战损 O、人工 H，则玩家减损 D=O-H，目标为新结果 S <= O-ceiling(D/2)。例如原来 100、人工 40，求解器做到 70 即可停止本包。
- 达标后完成必要验证并推进下一包；已有当前基线达标的，直接记录，无须为了修改而修改。
- 有限投入后仍达不到软目标，记录最好结果、根因或阻塞并跳过。无需证明“无论如何也追不上”，不反复增加 Beam、预算或超时。
- 建议节奏：一次当前基线，围绕明确根因做少量候选，只对最终候选做完整部署；没有新证据就停止该包。单请求沿用最多 120 秒，不自动加时。这里不新增固定试验次数作为硬门槛。
- 能力牌跨回合收益、准备动作与组合启动仍是重点。修语义或保留有真实后续收益的分支；Beam 扩大必须有剪枝位置、收益与成本证据。

## 先按 Git 恢复包 1、包 2 的已验收改动

`4ea8999` 回退了原策略批次。不要直接撤销整个回退提交：它还会重新引入旧导入器、旧测试脚本、版本和文档，冲掉后来的日志重构。也不要恢复包 2、包 3 被否决的 stash 实验。

历史来源如下，按最终净差异恢复，不单独停留在早期评分版本：

| 提交 | 应恢复或参考的内容 |
| --- | --- |
| `00bcaf7` | 能力收益及持久准备特征：格挡引擎、未来选牌、Barricade/Rage/Unmovable 等的效果估计；只是初版，必须结合后两个提交的修正。 |
| `1e9f676` | 每个药水层保留较安全的 HP 投资路线；收回虚高的潜在收益，留牌收益不再重复乘剩余回合，Barricade 仅计现有格挡。 |
| `e49aa18` | Rage 使用实际能力数值和可达攻击次数估计，Prolong 使用当前格挡，均有上限。此提交还含旧状态导入代码，后者不整批移植。 |
| `0cb5bcf` | Smart 药水搜索在预算内继续比较后续合格组合，不遇到第一个合格层就停；修复 Unmovable 的卡牌格挡事件计数及 Fork 复制，排除遗物额外格挡误计。 |
| `31899af` | 包 2 的最终验收及原报告政策恢复。没有单独的新 Search 评分修复；包 2 使用共用改动和正确政策取得结果。当前导入器已演进，参考政策和证据，不覆盖新导入器。 |
| `afb732c` | 更早修正了从人工中途状态续局冒充整场结果的问题。仅作证据口径参考。 |

可直接从 Git 生成下面的生产代码净补丁，免去重写旧修复。先检查当前实现是否已有等价修复，再在本分支应用并解决与新代码的冲突。`git apply --3way` 可能留下需要处理的冲突，不能将旧版本整文件选为冲突解决结果。

```powershell
git diff --binary --output=.local/worldline-accepted-strategy.patch 927bb73 31899af -- src/Search/CombatBeamSolver.BeamRetentionPolicy.cs src/Search/CombatBeamSolver.StateEvaluation.cs src/Search/CombatPlan.cs src/Search/StrategicEffectModel.cs src/Search/CombatSearchCoordinator.cs src/Engine/Common/PredictionForking.cs src/Engine/InCombat/Simulation/CombatPredictionSimulator.Block.cs src/Engine/InCombat/Simulation/CombatPredictionSimulator.Card.cs src/Search/SimulatedCombatState.CardLifecycle.cs src/Search/SimulatedCombatState.CardPowerHistory.cs
git apply --3way .local/worldline-accepted-strategy.patch
```

该补丁只覆盖上述生产文件，测试支持需要按当前协议迁移。重点检查新的药水分层逻辑和 Beam 保留逻辑，避免覆盖社区 PR。Unmovable 修复必须连同历史写入、消费时机和 Fork 一起检查。

从 `0cb5bcf` 参考两个针对性 fixture 及其协议支持，按当前接口接入：`coverage/unattended/smart-potion-qualified-layer-continuation-0300.json`、`coverage/unattended/unmovable-relic-block-history-0300.json`。不要盲目复制旧无人测试执行器。恢复后重新构建，用相关语义差分和两个代表包验收，再提交这一批。

## 包 1、包 2 的真实历史结果

以下编号均为监控清单的“有玩家备注”分组；汇总包均为 `combatsolver-reports-20260905-104714.zip`。

| 包 | 日志 ZIP | 报告原始 → 人工 | 历史完整部署 | 条件 | 历史相对人工 |
| --- | --- | --- | --- | --- | --- |
| 1 | `CombatSolver-AEONGLASS_BOSS-20260903-153416-774.zip` | 110 → 6 | 3 HP / 4 药，T7 完战 | VeryHigh、DOP4、原包四药 Force、减战损优先；计划外重算 0 | +3 |
| 2 | `CombatSolver-AEONGLASS_BOSS-20260903-152708-532.zip` | 123 → 55 | 50 HP / 1 药，T10 完战 | VeryHigh、DOP4；两项 Boss 政策均 BossProgressionFirst；slot0 StableSerum Force，其余三槽 Disabled；计划外重算 0 | +5 |

历史证据：

- 包 1：`.local/strategy-batch/results/20260905-rank1-reported-policy-accepted/`，runId `ff728e5661394c9e8a77c7b03a404a48`。
- 包 2：`.local/strategy-batch/results/20260905-rank2-reported-policy-accepted/`，runId `beb01c29aa57492db5c55ce103a593c2`。
- 证据中有 `result.json` 和 `godot.log`，读取后对照当前执行政策。包 1 另测 Smart 为预测 8 HP / 3 药，不是完整部署验收。包 2 用 MinimizeHpLoss 的死亡路线不是原政策下的对照。
- 包 2 早期“人工后 T7 续局 0 HP、相对人工 +55”已被撤销，不引用。这里的 50 HP 才是后来的整场证据。
- 历史结果不能直接写成恢复后当前版本已通过。原始数值若是死亡罚分或不同区间，同样不能机械计算软目标。

## 队列与需要避开的重复工作

用户监控清单：`docs/strategy/player-worldlines-20260905.md`。共 460 条，有备注 129、无备注 331；进入处理 312，减损小于 5 HP 暂缓 148。只维护“汇总”下面的表格，不重建清单、不加顶部长篇维护内容。

先有备注，再无备注，各自按原始减损降序。小于 5 HP 只列清单。**有备注包 4 用户明确跳过，继续跳过。**

- 包 3 `TEST_SUBJECT_BOSS-20260902-133704-483`：61 → 3，软目标 32 HP。历史已提交策略曾预测 22 HP，未提交试验为 20 HP，均未做完整部署验收。新目标下先复测当前基线，可能无需新增策略就能达标。旧根因是自损抽牌后能力降费启动链被剪掉，不重做大量准备组配额试验。
- 包 7 `QUEEN_BOSS-20260903-173813-639`：50 → 5，软目标 27 HP。历史预测 23 HP，仅是预测；优先检查当前能否实际完成，不继续为追 5 HP 展开长线调查。
- 包 8 `AEONGLASS_BOSS-20260904-053711-747`：人工记录 20 HP 时敌人还有 420 HP，不能作为人工整场 20 HP 对照。
- 其他旧缺材料、政策不全、超时的条目，先用新兼容入口做小批预检。不要直接照搬旧导入失败结论，也不要为单个缺项无限修工具。
- 原 ZIP 位于 `D:\Download\Edgedownload`，已整理材料在 `.local/issue-bundles/20260905/`。证据与旧 manifest 在 `.local/strategy-batch/`。这些是本地忽略文件，同机器新窗口可直接访问。
- 旧 `.local/strategy-batch/handoff-prompt-20260905.md` 只作历史证据索引；其中“必须追平人工”、旧分支/版本/dirty 文件状态及继续死磕的要求已经过时。

表格状态写清“存活达标”“软目标达标”“跳过：未达软目标”或具体技术阻塞，并记录当前构建、政策、预测与实际值、证据目录。可以在处理状态内记录软目标，不必扩张整张清单。

“优化后相对于人工”仍为人工实际战损减求解器实际战损，例如人工 6、求解器 3，记 `+3`；严格小于人工战损才记“是否更优=是”，追平为 `0/否`。达到一半改善通常仍差于人工，不能把达标写成更优。未验证的整场对照留待验证，报告值差距可在处理状态中明确标为参考。

## 使用现在的日志工具验收

先读 `docs/CHECKPOINT_REPLAY.md` 和实际脚本参数；该文档开头的 0.30.0 是日志功能落地版本，不代表当前仓库版本。新旧包共用 `tools/run-checkpoint-batch.ps1`，优先此入口，不重新围绕旧 AutoTurnStart 扫描脚本搭流水线。

```powershell
pwsh -NoProfile -File tools/run-checkpoint-batch.ps1 -InputPath <原包.zip> -ReplayMode Preflight -OutputDirectory <独立目录>
pwsh -NoProfile -File tools/run-checkpoint-batch.ps1 -InputPath <原包.zip> -CheckpointSelector start -ReplayMode RestoreOnly -Sts2GameRoot <实际游戏目录> -OutputDirectory <独立目录>
```

恢复通过后显式选择 SearchOnly 定位、DeploySolver 验收；开战用 `start`，不能用回合数为 1 猜起点。DeploySolver 使用 Instant/0 秒并检查计划外重算。不要把 SearchOnly 预测或人工后续局当成完整部署结果。新代码已有路线缓存，确认结果来自本次目标构建和输入，记录是否命中缓存。

原包实际政策优先；缺失字段通过 `-ReplayPolicyOverridePath` 明确补齐，记录原值和执行值，不偷偷使用默认政策。旧包缺完整录制会限制 ReplayRecorded，不等于不能从开战部署求解器。严格的自动整场“相对人工”还要求已验证人工录制；无法满足时保留历史证据与报告参考值，不修改校验器放行。

只保存有用的失败基线和最终证据；输入、根、政策、构建不同不能当成同一对照。技术失败归技术失败，不归策略差距。新修改按风险运行构建、相关 fixture、边界检查和必要整场部署，已通过且代码未变的检查不重复跑。

## 提交与交付节奏

第一批恢复并验证包 1、包 2 的已有生产修复，随后按清单小批推进。每批把代码、相关验证和监控表格一起提交并推送 `strategy/worldline-resume`。保留当前新日志系统和社区贡献，不恢复旧包 3 未验收试验。

旧任务的 0.30.0 批次已经结束，当前起点为 0.31.2；本次策略行为进入下一版本开发中，具体版号未指定，不自行猜测，也不回写已发布版本。本次交接不要求再次发布日志版本。此前“全部推送并发布工坊”针对日志改进交付，不能据此在策略实验阶段直接发布。

接手后先确认分支及工作区变化，按 Git 恢复已验收净改动、验证并提交第一批，然后应用新的停止标准推进队列。完成软目标就收工这个包，达不到就登记跳过。
