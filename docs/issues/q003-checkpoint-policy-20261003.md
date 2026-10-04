# Q003 检查点政策与路线比较（2026-10-03）

对应 [Q003 / #183](https://github.com/Torch1230/CombatSolver/issues/183)，包含 O006–O010；源码基线为 `556e72994303e45ca2b2833aa09ba793d1b096cb` / 0.48.0。本记录覆盖测试入口修复、材料调查与代表验证，整批质量验收仍未完成。

## 已修复的问题

报告政策包含 `predictPotionReward`、`useNoveltyPortfolio`、`useBeamWidthPortfolio`、`useEarlyTurnExploration`，恢复入口此前没有复制它们，搜索因而继承本机设置。实际证据中 O007 的药水奖励预测由 true 变成 false，O008/O010 的新奇度组合由 true 变成 false，不能把这些运行当作报告原条件下的质量基线。

现在四个开关按记录的 true/false 恢复，旧 settings 导入和显式政策覆盖也支持这些字段。搜索/部署缺少开关时返回 `missing_recorded_policy`，不静默猜测；单独恢复状态和回放录制输入仍可核验。实际数值 profile 保持原预算，恢复后的内部预设名为 Custom，标签差异不代表搜索预算变化。

批量工具此前只比较部分政策，可能把不同搜索开关、成长/遗物政策、局外收益或可接受战损停止条件视作等价，输出未经证明的人工路线收益。比较现在包含这些字段；必要字段缺失或为 null 时不产生收益字段，明确可空的 `brightestFlameMaxHpLossLimit` 可以为 null。其他已记录字段也必须两侧存在且相等，只排除内部预设标签。仍需同时满足原有根状态、输入身份、完整战斗和原生存活结果要求。

开战报告的政策只记录设置与数值 profile，缺少 `includeTurnSetup`、`act3BossStrategy`、早期探索预算、新奇度预算及成长目标等派生上下文；实际执行摘要包含这些字段。第三阶段重新核对原始 JSON 后修正了此前对缺项方向的判断。四个开关和数值预算一致不等于所有原记录政策已核验；新比较门槛会保守拒绝这类不完整报告，不能从当前执行反推原先未记录的值。

本任务改动位于 `src/Testing` 与 `tools/replay/CheckpointTool`，未修改生产搜索算法或战斗语义，也未独立提升版本。首次交付合并上游 `2ead87d9c9e35b1588a760efff0bd6154545a77c` 的 0.48.1；随后为解决 PR #206 冲突合入 `4533f6bb8c9b049aa27693e38da0f2854b31dd36`，继承上游 0.49.1 元数据。发布归档保持冻结，本任务归入下一版本开发记录。下列历史代表证据保留各自源码来源，当前复现命令使用维护路径。协议与兼容说明见 [检查点回放指南](../CHECKPOINT_REPLAY.md)。

## 首次直接验证（历史来源）

| 检查 | 结果与范围 |
|---|---|
| Release 构建 | CombatSolver 与 CheckpointTool 均零警告、零错误 |
| 结构门禁 | `REFACTOR_BOUNDARIES_OK search_files=246` |
| 文档门禁 | `DOCUMENTATION_OK files=421 links=1579 errors=0` |
| 工具合同失败基线 | 新增相反政策夹具在旧比较实现上失败：`different_switch_not_comparable:predictPotionReward` |
| 工具合同最终结果 | `archive_contract_tests_passed assertions=59`；包括不同开关无人工收益、成长/遗物差异、必要字段缺失/null、派生上下文、未来字段、预设标签和统一预算兼容 |
| `REPLAY-BOUNDARY-CONTRACT` | `b4c0cbeb24964595b3aed0cbb74b4417` Passed，19.9 秒；相反本机开关的 true/false 恢复、精确预算、输入不变、缺项拒绝及既有回放边界 |
| O008 有效录制前缀 | `f92387e90f7745cfb5ec74f55f46dcea`；检查点 `9a3541d8ef3b4a27b04626ea32f958d3:3` 的 16 项事件回放，完整 continuation 与原生状态核验通过 |
| O008 最早续用 | `c3a2b0d8add74484ba310e5e539ac182` Passed，28.9 秒；从 `:1` 的第 2 回合执行到第 3 回合，`UnexpectedReplans:0`，原生仍存活，战斗未结束 |

政策合同取得证据后只增加旧 settings/显式覆盖入口的字段登记，恢复核心与夹具未再改变；最终字段登记由实际包搜索和原生续用覆盖。最终游戏测试 DLL SHA-256 为 `0497C669BB809FB06D2EAB520CB591AE2019EDE02083FD364D82F0FE6DEFF2E2`。本轮游戏为 0.111.0，RitsuLib 为本机 0.6.3；报告中的完整 Mod 清单并未全部安装，不扩大核验范围到整个原始 Mod 栈。

无人请求总超时均不超过 120 秒。每轮游戏实例已退出并清理；原始问题包、完整日志、游戏 DLL 与本机路径不进入源码提交。

## 首次三阶段的五主题进度（历史来源）

| 主题 | 已取得的证据 | 未完成项 |
|---|---|---|
| O006 | 第一阶段录制前缀 9 项事件通过。固定短预算 SearchOnly 可返回路线：预测战损 22、玩家 HP54、敌方 HP2，未结束战斗 | 原设置开战搜索仍是第一阶段的 120 秒超时；短预算只区分可返回与持续等待，不证明超时根因或整场胜利 |
| O007 | 修复政策后开战搜索预测胜利，战损 33、药水 0、第 8 回合；总搜索工作 7.860 秒 | 历史模型 ID 映射缺失，原生状态仍为不可比；录制人工使用了策略禁用的力量药水，不能拿历史 15 战损直接验收 |
| O008 | 有效第 3 回合录制前缀通过；第 2→3 回合短路线原生续用通过。原设置开战搜索返回未完成的存活路线；有效第 3 回合根搜索只找到死亡路线 | 最新快照与事件历史不连续；开战路线未完成，且第 3 回合仍无获胜路线。没有同根、同资源、完整原生人工胜利参照 |
| O009 | 第一阶段状态/11 项事件前缀通过；第三阶段冷启动基线与候选的根、执行政策、完整动作序列和预测终态相同，预测战损 85、药水 0、第 14 回合 | 未进行搜索优化或整场原生部署；历史 74 战损预测来自其他检查点 |
| O010 | 修复政策后开战搜索预测战损 43；第三阶段原设置整场原生部署通过，第 10 回合存活结束，原生战损 43、药水 0、计划外重算 0 | 原生整场已通过，但历史预测/用药区间不构成同根人工胜利参照，未证明质量目标达到或战损改善 |

上表的“预测胜利”来自搜索模型，不表示已完成原生整场部署。第一阶段未受改动影响的材料与前缀证据未重复运行，也不冒充本轮重新通过。

### 第三阶段代表验证

O009 采用两个独立冷启动进程，同机、同原包开战根、同实际政策与原预算。基线由 `556e729` 源码构建，缺失的 net48 引用通过本机既有缓存补齐；没有安装开发包。候选为 `f17f4d9` 的行为源码，两次均通过完整 continuation 与原生根核验。`rootContinuationStamp`、完整动作序列、预测 snapshot 及实际执行政策逐字段一致，均为 NodeLimit、120000 节点、481427 次转移、预测战损 85、药水 0、第 14 回合、HP7/敌方 HP0。

| O009 同条件冷启动 | runId | 总搜索工作 |
|---|---|---|
| 基线 | `1ae32d83b86f42c3b8cd3bb83cd72f64` | 29008.463 ms |
| 候选 | `49d4b5073799448ab996d93b00ce1bbd` | 29793.7552 ms（+2.7%） |

这是一组成对样本，仅支持该根的模型路线质量一致和本次时间差，不证明普遍性能无退化。第一阶段 21022.9167 ms 样本来自复用进程的批次后段，预热条件不同，不拿它作本次冷启动耗时基线。没有测试可见帧时间或 FPS。

O010 的 `51c242c740dc4363a043f76d021ba441` Passed：从 combat_start 按原设置、Instant/0 秒完成第 10 回合，`UnexpectedReplans:0`；原生 outcome 记录初始 HP60、战斗末 HP18、累计战损43、治疗1、药水0、敌方HP0，存活结束。夹具随后观察到战后 HP24，区分战斗结果与战后恢复，不把后者当作战斗减损。该验证不证明优于历史人工路线。

复现 O009 成对样本时，分别从基线和候选源码构建，通过 `-CombatSolverBuildDir <对应构建目录>` 指定 DLL/manifest/MemoryCleaner；每项单独启动，运行 `SearchOnly/start`，原政策不覆盖、超时 120 秒并清理实例。O010 使用原包 `DeploySolver/start`，增加 `-ExpectedUnexpectedReplansAtMost 0 -ExpectedFinishedPlayerHpAtLeast 17`，保持 Instant/0 秒、120 秒及清理开关。

### O008 的材料边界

原包 `:1` 为事件游标 8、第 2 回合 HP62；`:3` 为游标 16、第 3 回合 HP55。后续 `:5/:7/:9` 同为游标 16，却返回第 2 回合 HP62。按完整 16 项事件回放到 `:3`，continuation 与原生状态均相符；与最新 `:9` 比较则出现回合、HP、牌堆、Power、充能球与 RNG 差异。

这证明该包后段快照与事件序列不连续，不能据此断言某个 Mod 或读档功能是原因，也不能修宽断言来取得绿色结果。后续路线调查应使用已核验的早期检查点。`:3` 内的既有预测还带有 `StartTurnNumber=2`，不能当作第 3 回合当前根的同条件质量参照。

政策恢复后的开战 SearchOnly 使用报告原预算：总搜索工作 41.124 秒、328582 个节点、1280919 次状态转移，返回 HP57、敌方 HP305、未结束路线；其中预测战损 6 不能表述为整场改善。有效第 3 回合原设置搜索总工作 7.811 秒，最终 HP0、敌方 HP52，只找到死亡路线。

原生最早续用使用固定短预算覆盖，仅验证第 2→3 回合执行边界。1500ms / 3000 节点是单成员 profile 限制；组合搜索仍有多个成员，实测总工作 8.916 秒、39000 个节点、171884 次转移。不能称为全请求 1500ms / 3000 节点。

## 复现入口

在有合法游戏和 RitsuLib 的本机配置 `local.props`，关闭 `CopyModOnBuild`，按 [无人测试指南](../HEADLESS_TESTING.md) 配置环境。原包从任务附件取得并留在忽略目录；下列 `<...>` 替换为本机路径。

```powershell
dotnet build CombatSolver.csproj -c Release
dotnet build tools/replay/CheckpointTool/CheckpointTool.csproj -c Release
dotnet .local/tool-build/CheckpointTool/bin/Release/net9.0/CheckpointTool.dll self-test
pwsh -NoProfile -File tools/inspection/verify-refactor-boundaries.ps1
python tools/inspection/verify-documentation.py
python tools/inspection/verify-tools.py
python tools/inspection/verify-coverage.py
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId REPLAY-BOUNDARY-CONTRACT -EncounterId MockMonsterEncounter -TimeoutSeconds 120 -CleanupInstanceOnExit
```

有效 O008 前缀与原设置短搜：

```powershell
dotnet .local/tool-build/CheckpointTool/bin/Release/net9.0/CheckpointTool.dll batch <O008报告目录> --mode ReplayRecorded --selector '9a3541d8ef3b4a27b04626ea32f958d3:3' --game-root <游戏目录> --ritsu-root <Ritsu目录> --output <证据目录> --timeout 120
dotnet .local/tool-build/CheckpointTool/bin/Release/net9.0/CheckpointTool.dll batch <O008报告目录> --mode SearchOnly --selector '9a3541d8ef3b4a27b04626ea32f958d3:3' --game-root <游戏目录> --ritsu-root <Ritsu目录> --output <另一证据目录> --timeout 120
```

最早续用的覆盖 JSON 从 `:1` 的原 profile 复制全部字段，仅将 `softTimeBudgetMilliseconds=1500`、`maxExpandedNodes=3000`，并设置 `fixedBudget=true`，不覆盖四个政策开关或资源策略。执行：

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -CheckpointArchivePath <O008报告ZIP> -CheckpointSelector '9a3541d8ef3b4a27b04626ea32f958d3:1' -ReplayMode DeploySolver -ReplayPolicyOverridePath <短预算JSON> -ExpectedReusedTurn 3 -StopAfterExpectedReuse -ExpectedUnexpectedReplansAtMost 0 -Sts2GameRoot <游戏目录> -RitsuWorkshopRoot <Ritsu目录> -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120 -EvidenceDirectory <证据目录> -CleanupInstanceOnExit
```

## 0.48.1 合并交付验证（历史来源）

当时行为源码为 `a2d685a`（合并上游 `2ead87d`），manifest 继承 0.48.1，本任务没有定版或发布。两处当前文档冲突已解决，上游历史卷保持冻结；新增合同移入 `Contracts/Search`。旧工具目录只残留本次构建缓存，已移到忽略目录，未保留旧源码入口。

- Release 构建：CombatSolver 与 MemoryCleaner 零警告、零错误。
- 当前 `tools/replay/CheckpointTool`：`archive_contract_tests_passed assertions=59`。
- 当前无人入口：`REPLAY-BOUNDARY-CONTRACT` / `bf7ccd4e164b4a428d57cf41bc13eb59` Passed，覆盖四开关相反默认值、true/false、精确预算、输入不变和缺项拒绝，保留原有回放边界；实例已退出并清理。
- 静态门禁：结构246个 Search 文件；文档418份/1595链接；工具284文件/35项目；覆盖材料522项/472夹具/37组，均通过。覆盖材料门禁不等于全部原生覆盖测试通过。

该轮只对合并后受影响的入口合同重新验证。该次上游搜索文件仅更新工具路径注释；既有 O009 成对样本与 O010 整场证据仍对应 `f17f4d9`，不冒充在合并后的 0.48.1 重新执行。正式本机 Mod 部署仅使用当时成功构建的五个内容文件；没有启动可见正式游戏或测量 FPS。

## 十阶段验收：第 3–5 阶段补充证据

这组证据来自 `4cccf33` / 行为源码 `a2d685a`，与前述首次三阶段工作分别记账。第 3 阶段 O008 使用临时探针验证指定历史完整路线，探针随后撤回；第 4–5 阶段复用生产 DLL。每阶段两次游戏请求、每次上限 120 秒，实例和专用输入已清理，没有安装历史 Mod。

| 阶段 / 主题 | runId 与结果 | 保留限制 |
| --- | --- | --- |
| 3 / O008 | `d4e1e993e91e4f87b374d941f0aa85e8`：从有效第 2 回合根执行固定历史路线，第 16 回合原生胜利；77 个非终局完整状态印记一致，战损 53、原有迅捷药水 1、计划外重算 0 | 强制路线仅证明可执行，不证明正常搜索能发现；最新人工材料不连续，未建立同根合法质量参照 |
| 4 / O006 | `943f77b3fae04de6b1e8ab6ec141db9d`：可执行 `:2` 根正常求解，第 4 回合原生胜利；战损 0、治疗 1、最大生命损失 2、后缀用药 0、计划外重算 0；全场已有第 1 回合强固药水 1 | `9182fa56abfe40a5a20ef084b8ee96b2` 的开战原预算请求仍 120 秒超时；预测战损 19 尚非完整原生比较基线，不能宣称整场减损 19 |
| 5 / O007 | `e31cac93b442485d9fdd5452a3967082`：原政策原生胜利，第 8 回合、战损 33、药水 0；`1ebc2dc6000b47c9b2537b93ff4fbd97`：人工前缀后正常求解，第 6 回合胜利、战损 15、力量药水 1；均计划外重算 0 | 原政策禁用力量药水，差额扣 9 后也不构成合法质量收益；历史模型 ID 映射缺失，continuation 通过但原生二进制状态比较未能核验 |

O007 所需材料是与历史哈希 `2940893119` 绑定的完整有序模型 ID 表；当前哈希 `1568834832` 不能代替。开战记录仅有 19 项有效政策字段，8 项派生上下文缺失；两次执行匹配已记录字段不等于完整历史政策已核验。五主题质量目标继续保持未完成。

## PR #206 冲突修复与 0.49.1 集成验证

行为源码 `946591ec86c8cb0173d483ebc08d4be74009d66c` 合入上游 `4533f6b`。三处冲突只涉及当前开发笔记、测试矩阵和问题索引；保留双方有效记录，删除重复开发标题与“暂无新增行为变化”，未改写上游发布档案。Q003 的四开关恢复、严格政策比较及合同保留。继承上游 0.49.1，不新增版本、标签或发布包。

- CombatSolver、MemoryCleaner、CheckpointTool 的 Release 构建零警告/错误；`CheckpointTool self-test` 59 项断言通过。
- `REPLAY-BOUNDARY-CONTRACT` / `a141c6b4b37446c49bf127f4224980fe` Passed，约 20.1 秒；覆盖相反本机四开关、true/false、精确预算、输入不变、缺项拒绝及既有回放边界。自有实例已退出并清理。
- 结构 247 个 Search 文件、工具 290 文件/37 项目、覆盖材料 522 项/472 夹具/37 组门禁通过。文档检查 440 份/1723 链接报 3 项错误：上游 `docs/community/drafts/2026-10-04/Q008.md`、`Q009.md`、`Q010.md` 引用的本机忽略目录 ZIP 未提供；本次修改的文档没有报错，全仓库文档门禁仍未通过。覆盖材料检查不等于全量原生覆盖或整批验收。

本次只补一次受影响入口合同，不重跑历史整场和性能样本，也不把这些旧样本写成 0.49.1 验收通过。该请求计入十阶段计划总上限 26 次，累计 7 次、剩余最多 19 次；计划仍在第 5 阶段后暂停，第 6 阶段等待用户指令。

正式本机 `mods/CombatSolver` 已精确覆盖 manifest、当前 Release DLL、Windows MemoryCleaner 和两份许可文件；部署来源为 `946591ec`。后续仅补充文档记录，复用该构建；没有启动可见游戏、制作 ZIP、提升版本、创建标签或发布渠道。

## 后续验收

先建立有效检查点、同预算/同资源的完整路线比较，再选择有证据支持的搜索修复；不能用不同根的预测、部分路线或额外药水宣称收益。已取得 O009 代表搜索成对证据及 O010 整场原生部署；其他整场质量目标、历史未记录政策及材料限制仍需处理。现阶段提交应保留 Draft，使用 `Refs #183`，不关闭整批任务。
