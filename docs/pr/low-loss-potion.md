# 本场少量省血用药偏好

[返回 PR 索引](README.md)

药水面板的“省血少也允许用药”默认关闭，仅本场有效。它与“全部智能／全部保护／全部强制／仅用已强制”批量预设共存；预设管理逐瓶指令，偏好管理少量省血门槛。它降低的是单瓶药水的最低省血要求，不限制无药后续战损上限。掉药概率由玩家判断；求解器不读取或推算掉药概率。

## 玩家行为

| 当前条件 | 开启后的行为 |
| --- | --- |
| 无药完整获胜，后续战略战损大于 0，本场尚未用药 | 从可搜索、未保护药水中比较恰好一瓶的路线，至少省 1 点生命损失才放宽采用 |
| 无药 13 损，单瓶后 5 损或 12 损 | 分别省 8 点或 1 点，都可放宽采用；无药损失没有上限 |
| 已经掉 10 点，后续仍预计掉 3 点 | 按后续 3 点判断，不把已发生掉血加入门槛 |
| 无药零损、药水不能省血、全部受保护或未找到合格路线 | 保留原路线，不要求必须交药 |
| 无药无法获胜或本场已经用过药 | 沿用普通智能策略 |
| 玩家指定某瓶强制使用 | 执行原强制指令，不额外开放放宽机会 |

保牌／保钱开启时，先比较未追回的被盗资源，追回相同时再比较战损。不能为省血放宽而丢失更多被盗资源；原来为追回资源承受更多战损或使用多瓶药的路线仍可参与。符合放宽条件的单瓶已经找到、但追回目标尚未满足时，继续原有后续药量层。

本场最多放宽一瓶。预览、取消、重新计算及开关切换都不消耗或刷新机会；实际用药后，由已有本场用药记录关闭放宽资格。开关跨回合保留，开始新战斗或重建战斗会话时关闭。切换使旧策略的搜索与续用来源失效；暂停自动计算时等待手动搜索，部署期间不能切换。

## 搜索口径与边界

`CombatSearchCoordinator` 从无药完整路线冻结 `LowLossPotionAllowance`，包括战略战损、移除战略额度后的生命损益、未追回资源及保资源模式。其资格仅传入“恰好一瓶”层，即使普通药量准入为零也可进入；进入该层的各种药水共同竞争，贵重药水仍由玩家逐瓶保护。确定性格挡药水插入成功时，请求直接结束，不追加单瓶搜索。

合格候选必须完整获胜、明确使用一瓶，战略损益和生命损益都至少改善 1 点，保资源模式下追回结果不能变差。回血与 Boss 折算沿用现有口径；纯成长／遗物额度或仅缩短回合不能单独取得放宽资格。零成本药与改善追回结果的路线保留原准入。

终局保路、排序、搜索中展示及最终采用共享同一资格。合格候选只把本次政策的有效省血门槛记为 1 HP；规范药水成本、药水效果、分支战斗状态、RNG、Fork、状态键与 `ContinuationStamp` 保持原语义。第二瓶没有低收益放宽。符合条件的已找到路线沿用现有排序，不承诺穷举或全局最优。

正数“可接受战损”不能跳过或提前结束这次比较；满足成长、遗物与追回目标且未消耗复活资源的合格零损单瓶胜利仍可早停。各层使用所选搜索配置的节点上限，共享请求剩余时间及取消、接管和内存边界；请求总工作量包含所有实际执行的层。

## 会话与兼容

`SolverCombatSession.LowLossPotionEnabled` 由主线程捕获到不可变的 `SearchPolicySnapshot`，后台不读取实时设置，开关不写入全局持久化配置。磁盘路线键区分开关；问题包有效策略写入 `lowLossPotionEnabled`，回放恢复缺少该字段的旧记录时默认关闭。公开 API 和药水策略枚举保持不变。

读档重建会话后，开关仍默认关闭。再次开启时，由 `PotionPreferenceChanged` 请求先查找当前根与当前策略完全匹配的磁盘路线；命中直接恢复，未命中才搜索。关闭开关同样可恢复对应的无放宽路线。恢复保留原路线的放宽标记与 1 HP 有效门槛，并绑定新根的预测。玩家直接点击“重新计算”仍绕过缓存；暂停或关闭自动计算时，切换开关本身不发起请求。增量验证与性能测量请求也保持原有缓存旁路。

内部 `LowLossPotion*` 类型、字段与测试参数表示少量省血放宽。磁盘键包含 DLL 身份，避免跨构建复用缓存路线。

无人测试的 Windows 参数为 `-LowLossPotionForTest`，Linux 参数为 `--low-loss-potion-for-test`，取值均为 `-1/0/1`：`-1` 使用问题包记录（缺省关闭），`0/1` 显式覆盖。协议字段为可空布尔 `lowLossPotionForTest`。验证场景及实际结果见下文。

## 兼容范围

基于 0.39.0（`7f806de`）。药水面板保留“全部智能／全部保护／全部强制／仅用已强制”四个预设；单瓶层使用当前 `CombatBeamSolver` 入口。路线精炼、选牌续执行、生存与复活资源优先规则继续生效。

## 验证

以下验证于 2026-09-15 在 Windows 执行。Release 编译通过，0 警告、0 错误；结构门禁通过，`search_files=106`；Bash 无人测试入口语法检查通过。

### 最小回归场景

| 场景 | 验证内容 | 结果／runId |
| --- | --- | --- |
| `LOW-LOSS-POTION` | 13→5、13→12、正常 20→5；正数早停、保护／强制／已用药边界、已发生掉血、显示门槛、策略记录、增量回放及偷窃策略哨兵 | Passed，23.83 秒；`c067c0a351fd428e9c854ac43408b9e1` |
| `LOW-LOSS-POTION-UI` | eng／zhs／zht 标签与提示、四个预设事件与偏好控件共存、预设规则与序列化、部署禁用 | Passed，20.38 秒；`5c947a0a40b3438c8936ca438b2700f4` |
| `LOW-LOSS-POTION-CACHE` | 会话重建后开关切换恢复 4 次、搜索 0 次；缓存未命中与显式重算执行搜索，暂停自动计算时等待 | Passed，7.61 秒；`770872c96ac54469b41f9bc48f4bbd40` |
| `LOW-LOSS-POTION-DEPLOY` | 首回合单瓶 8→0、增量回放、第二回合原生严格续用，计划外重算 0 | Passed，7.20 秒；`461eb9d51f9d4be1a9300bae4afd1b30` |

搜索与缓存场景使用固定 512 节点／3000ms、DOP1、NoGC 关闭。部署场景使用固定 3000ms、Instant／0 秒，在第二回合完成续用验证后停止。

### 存档回归：稳定血清与遗物计数目标

场景为静默猎手、进阶 10、第二幕残杀千足虫精英战，战前 HP 40。药水槽位 0 为稳定血清（`STABLE_SERUM`），槽位 1 为迅捷药水（`SWIFT_POTION`）。测试使用同一标准化存档和 DLL；存档标准化只处理时间、读档次数、平台标识、地图涂画与空元数据，牌组、生命、药水、遗物及 RNG 保持一致。恢复通过 `DirectRunSnapshot:ExactStateRestored` 后进入目标房间。

搜索配置为 VeryHigh、每层 100000 节点、16 线程、300 秒软预算、NoGC 配置 128 GB。每个无人测试请求的超时为 120 秒，在首个最终搜索结果处停止。两组均保护迅捷药水，启用开心小花计数目标 0～0、额外战损额度 0、优先级 1，仅切换少量省血偏好。

| 少量省血偏好 | 预计整场战损 | 用药与预计获胜回合 | 结果／runId |
| --- | --- | --- | --- |
| 关闭 | 17 | 无药，第 9 回合获胜 | Passed，46.14 秒；`f15f771493f84c5f91e1ccd9bed09226` |
| 开启 | 13 | 第 4 回合用稳定血清，第 7 回合获胜 | Passed，32.64 秒；`0fe6235146d947a09277f7707b8c6c55` |

两组无药主搜索均展开 100000 节点／527479 次转移，单瓶层均展开 100000 节点／579309 次转移，请求合计 200000 节点／1106788 次转移。开启组记录 `Potion=1 Saved=4/1`，即节省 4 点战损并满足 1 点门槛。各层以 NodeLimit 完成，选中路线均为完整胜利路线。

两组实际最大并发均为 16，结果时 NoGC 区域分别为 128000000000／115584063633 字节。开启组有一次 Runtime 回收重启，未发生 NoGC 意外丢失。耗时包含不同的启动与 GC 成本，不作性能对照。

### 未设置遗物计数目标的对照

同一存档和搜索配置下，`relicCounterRules=[]` 时结果如下：

| 偏好与药水指令 | 预计整场战损 | 用药 | 结果／runId |
| --- | --- | --- | --- |
| 关闭，两瓶智能 | 20 | 无 | Passed，77.22 秒；`99da229b754242688b504ae623a767e0` |
| 开启，两瓶智能 | 5 | 第 1 回合用迅捷药水 | Passed，31.09 秒；`f37b63d7a71c428290f9595805a29655` |
| 开启，保护迅捷药水 | 14 | 第 6 回合用稳定血清 | Passed，45.14 秒；`7dce27af175642b48933117a970bd614` |

三组无药基线均为 20，主搜索均展开 100000 节点／506737 次转移。两瓶智能的开启组记录 `saved=15 required=1 low_loss_applied=True selected=True`；保护迅捷药水的开启组记录 `Potion=1 Saved=6/1`。结果时有效 NoGC 区域分别约为 81.16、98.80、128 GB。

遗物计数目标参与候选评分、同战略战损下的终局排序和早停条件，会改变有限节点预算下保留和找到的路线。额外战损额度为 0 时，卡数不提供战损抵扣，选中路线也不保证满足卡数目标。17→13 与 20→14 分别对应有、无开心小花计数目标的测试条件。

### 验证范围

存档回归验证到最终搜索结果，未实际部署整场路线；原生部署与跨回合续用由最小两回合场景覆盖。NodeLimit 下的结果不代表穷举最优。UI 无头验证覆盖结构与事件；可见排版、Linux 游戏运行和完整发布验收未执行。

## 复跑入口

### 最小场景

按安装位置提供 `-Sts2GameRoot`／`-RitsuWorkshopRoot`（Linux 对应 `--sts2-game-root`／`--ritsu-workshop-root`）。新 Windows 隔离实例需要 `default/1/settings.save`；`$fixtureProgress`／`$fixture_progress` 指向已完成教程的原生 `progress.save`。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId LOW-LOSS-POTION -HeadlessInstance low-loss-potion -ProgressSnapshotPath $fixtureProgress -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId LOW-LOSS-POTION-CACHE -HeadlessInstance low-loss-potion -FixedSearchBudget -SearchBudgetOverrideMilliseconds 3000 -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId LOW-LOSS-POTION-UI -HeadlessInstance low-loss-potion -TimeoutSeconds 120 -ExitOnComplete
```

```bash
./tools/run-unattended-test.sh --scenario-id LOW-LOSS-POTION --progress-snapshot-path "$fixture_progress" --enable-no-gc-region-for-test 0 --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id LOW-LOSS-POTION-CACHE --fixed-search-budget --search-budget-override-milliseconds 3000 --enable-no-gc-region-for-test 0 --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id LOW-LOSS-POTION-UI --timeout-seconds 120 --exit-on-complete
```

最小部署使用 `LOW-LOSS-POTION-DEPLOY`，在短预算入口加 `-PotionPolicyForTest Smart -LowLossPotionForTest 1 -FixedSearchBudget -SearchBudgetOverrideMilliseconds 3000 -SearchMaxDegreeOfParallelismForTest 1 -VerifyIncrementalSearch -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -ExpectedInitialPotionCount 1 -ExpectedInitialProjectedBattleHpLost 0 -ExpectedUsedPotionId BLOCK_POTION -ExpectedReusedTurn 2 -ExpectedReusedProjectedBattleHpLost 0 -ExpectedUnexpectedReplansAtMost 0 -StopAfterExpectedReuse`；Bash 使用对应 kebab-case 长参数。

### 存档场景

存档回归需要另行提供相同测试存档，不能仅凭场景参数重建牌组、遗物、牌序和 RNG。存档与完整日志不随仓库分发。

| 参数 | 值 |
| --- | --- |
| `CharacterId`／`EncounterId` | `SILENT`／`DECIMILLIPEDE_ELITE` |
| `ActIndexForTest`／`TargetActFloor`／`TargetMapColumn` | `1`／`11`／`1` |
| `TargetRoomType`／`TargetMapPointType` | `Elite`／`Elite` |
| 快照与执行 | `RunSnapshotPath` 指向标准化存档；启用 `LoadRunSnapshotDirectly`、`PreserveNativeCombatStateForTest`、`FixedSearchBudget`、`StopAfterInitialSolverResultAssertion` |
| 预算 | `PerformancePresetForTest=VeryHigh`、`TimeoutSeconds=120`，不覆盖原生 300 秒软预算 |
| NoGC | `EnableNoGcRegionForTest=1`、`NoGcRegionBudgetGigabytesForTest=128` |
| 对照变量 | `LowLossPotionForTest=0/1` |

当前脚本的显式线程参数最多接受 8。16 线程测试通过隔离设置中的 `searchMaxDegreeOfParallelism=16` 使用正常设置捕获入口。第二瓶保护配置为 `potionDirectives=[{"slot":1,"potionId":"SWIFT_POTION","directive":"Disabled"}]`。

开心小花目标配置为 `relicStrategyEnabled=true`、`relicCounterRules=[{"id":"HappyFlower","enabled":true,"minimum":0,"maximum":0,"hpAllowance":0,"priority":1}]`。无遗物目标的对照使用空规则数组。该场景持有的可卡数遗物只有开心小花。
