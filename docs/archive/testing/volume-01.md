# CombatSolver 测试入口历史卷 01

## 0.47.2 发布定版（2026-09-28）

用户在版本与日志登记后授权全渠道发布。本次只更新文档中的开发状态，复用 `1b910969` 的成功 Release 构建和本地五文件部署；构建输入及行为源码未变。最小 ZIP 使用该构建及已提交的 manifest、两份许可文件，创意工坊更新说明由同版本中英日志完整转换。标签指向本次定版提交；统一脚本在发布前验证连接元数据，并按渠道记录结果到 `releases/CombatSolver-0.47.2.publish-state.json`。沿用下文已取得的定向行为证据，不重复构建、部署或行为测试，不运行 Linux 与完整发布门禁。

## 0.47.2 版本与日志登记（2026-09-28）

本轮仅修改版本与文档，行为源码及依赖保持 `3ad5ec33` 的已验证状态。复用下文 #140 原生生命周期、#143 四项原生合同、内存条 ServerGC 开／关和相关登记合同，不重复运行行为测试。核对 manifest/csproj 版本一致、中英条目和贡献者链接对应、相对文档链接及 diff；按版本变化执行一次 Release 构建与本地五文件部署，不触发完整门禁、Linux、打包、标签或渠道上传。

结果：版本／日志检查通过（中英各 7 条，5 个 PR 的作者及链接对应），diff 检查通过；从 `1b910969` 构建 Release，0 警告／0 错误，manifest、DLL、Windows MemoryCleaner 和两份许可文件已一次复制至确认的游戏 `mods/CombatSolver` 目录。该登记阶段未打包、建标签或上传渠道；随后发布授权与定版见上节。

## PR #144 正文更新后的复审（2026-09-28）

远端 head 保持 `6498169c`；以 `main@1471c296` 集成后运行候选的 `CombatSolver.GcPolicyChecks -- recovery`（9 项）、`-- recovery-lifecycle`（2 项，实际 CLR starts=1/restarts=1/forced=0）、`-- checkpoint`（1 项），全部通过。覆盖已有恢复、取消、显式退出和释放边界；未声称覆盖 RegionSizeUnsupported/PlatformUnsupported 后重试，或证明取消每 scope 三次限制的收益。没有重跑全部八套件。候选仍未合入，具体调用链审计见 [合并审计](../refactoring/merge-audit-20260928.md)。

## PR #140 资源恢复后的原生复审（2026-09-28）

当前 main `afeb0e01` 上集成原候选；Release 0/0、Windows 边界 238 通过。`PR140-PRECOMBAT-WORKER -VerifyPreCombatForecastApi -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120` 请求 `0a76d46e5d9545d4816840bbd80f0d0f`，75.3 秒 Passed。覆盖完整预测、同进程复用、显式中断活动请求、随后新 worker 模拟成功、自动关闭、Mod 写入隔离、设置令牌失效，以及原跑局/RNG不变；WorkerStarts=2、WorkerReuses=3。启动器已删除实例 `.local/headless-instances/audit-pr140-recheck`，原始证据 `.local/audit-recheck-20260928/pr140-worker`。11 项请求工具合同复用前轮同源码结果，不重复运行；未运行 Linux 或可见性能测试。

## 内存条与 PR 阻塞验证（2026-09-28）

`MEMORY-DISPLAY-CONTRACT` 在两个自有隔离游戏进程通过：`cad74c8a0c664b3d89679cbc967e7469` 实际 ServerGC=False，`90a36a763671460eaed1bf5a2da535fd` 实际 ServerGC=True；各一节点／四转移，25.5/24.8 秒，实例均删除。真实采样的物理已用超过 GC 压力阈值，已用＋可用等于物理总量。固定快照覆盖空闲、搜索、回收、超阈值、未知物理数据和 GC 阈值不改变物理条形；回收显示合同不代表人为制造高压回收。

OfflineSearchHarness 环境变量 `OFFLINE_HARNESS_MEMORY_DISPLAY_CHECKS=1`，`--milestone M1 --language eng|zhs|zht` 分别通过；设为 `baseline` 并通过 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 指向旧 DLL，生产入口直接复现 6 GB／4 GB。Release 与离线宿主 0/0、Windows 边界 238 通过。没有 Linux／可见性能结论。

首轮 #140 修复后的工具合同 11 项通过，原生 worker 生命周期当时受资源阻塞；后续复审已通过并合入，见本文件上方记录。#144 候选在原三次尝试上限合同失败，未合入；最终 main 的 GC `recovery` 9 项、`recovery-lifecycle` 2 项通过，不混用两个版本的结论。详情及证据边界见 [合并审计](../refactoring/merge-audit-20260928.md)。

## PR #143 合并验证（2026-09-28）

本轮最终候选 Release 0/0、Windows 边界 238 通过。离线 Infused Core 14 项与 N4/N8/N17 独立前缀 oracle 通过。资源恢复后原生 FIXED-PREFIX-TURN-OUTCOMES、OPENING-DISCARD-CHOICE-VALUE、TURN-SETUP-FIXED-PREFIX-STAMPEDE、INITIAL-TOOLBOX-INFUSED-CORE 四项通过，分别 47.9/28.6/30.2/26.6 秒；包括固定前缀三回合实际续用、七表缓存、完整续用戳、弃牌 DOP1/DOP2 和初始原生选择。runId 与具体边界见 [合并审计](../refactoring/merge-audit-20260928.md)。实例全部清理，未跑全量 CoverageCatalog 或性能大样本。

## PR #142 合并验证（2026-09-28）

`dotnet run --project tools/ModelIdCacheChecks/ModelIdCacheChecks.csproj -c Release`：7 项通过。链接原 main 生产补丁时明确失败于注册前无前缀缓存；最终补丁核对原版、两个动态程序集同名类型、注册前／后、晚加载、并发与 null 原生入口。替身只提供模型 ID 和补丁元数据，缓存逻辑直接链接生产文件。主项目及离线宿主 Release 0/0，Windows 门禁通过。`PR142-MODEL-REGISTRY` 在主机准入阶段超时、实例删除，Windows 原生初始化事件尚未实测。

## PR #139 合并验证（2026-09-28）

`TurnPhaseMirrorChecks` 的默认／`--seal`／`--start`／`--start --seal`／`--after-player-start`／`--after-player-start --seal` 六组分别 28、3、27、2、52、5 项通过。合并时补齐具体模型忽略登记与复合登记原子性：红灯为 `Ignored accepted an abstract model`；绿灯覆盖抽象、无关类型、重复、失败后 Early 正常登记及派发。Release 0/0；Windows 结构门禁通过。未运行原生第三方 Mod 或 Linux 门禁。

## PR #138 合并验证（2026-09-28）

Release 0/0、Windows 结构门禁 238；GA-SILENT-BOSS-00 与本轮重构基线逐位相同，证据 `.local/audit-20260928/pr138-comparison`。`GENERIC-LOOP-HELLRAISER-PILLAGE-SINGLE-CURRENT-V0111` 原生请求 `0eaff0f74d134a0aa7e4e4bb453cbc8e` Passed，固定 5 秒搜索、DOP1、增量验证，首动作 PILLAGE、0 战损、首回合击杀；1 展开／2 转移，覆盖动作内部循环，不覆盖循环租约。120 秒请求内完成、实例删除。未运行批量性能或 Linux 门禁。

## 合并审计：并行失败作业记账（2026-09-28）

- 最终源码 `StrategyCorpus/run.py --manifest coverage/strategy-refactor-p2/corpus.json --case ga-silent-boss --out .local/audit-20260928/refactor-sentinel` 为 comparable；与 `.local/strategy-refactor-p6/final-sentinel` 比较逐位相同。最终 Windows 结构门禁 238 通过。

- `pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId ADMITTED-JOB-FAILURE-ACCOUNTING -EnemyCurrentHp 999 -VerifyPredictionFailureBoundaries -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -HeadlessInstance <独立实例> -EvidenceDirectory <证据目录> -CleanupInstanceOnExit`。
- 未修复生产代码时 `8a6d697c8a404adeb39e6ac12c9a2018` Failed：worker 分配 67,108,888 字节，请求仅记录 116,856。修复后 `747fc356ae864a31b69459e1627ef400` Passed，覆盖取消、原异常、已发生分配记账、排空及同根后续 DOP2；普通预测失败边界也通过。实例均已删除。原始证据 `.local/audit-20260928/refactor-red`、`refactor-green`。
- Release 构建 0/0；Windows 结构门禁 238；StrategyCorpus 比较工具 5 项、first_loss 1 项、PowerCardValuationChecks 104 项通过。历史 P4/P5 六根原始结果仅排除后加归因字段后逐位一致，本次未重跑六根。Linux 与可见性能未执行。

## 策略重构 P7a 单项持续效果权重（2026-09-28）

- `pwsh -NoProfile -File tools/run-unattended-test.ps1 -CheckpointArchivePath .local/issue-bundles/worldline-top150-20260926/raw/88619c63f91b48998737d7a9d623e2df.zip -CheckpointSelector start -ReplayMode SearchOnly -FixedSearchBudget -PerformancePresetForTest VeryHigh -SearchBudgetOverrideMilliseconds 180000 -SearchMaxDegreeOfParallelismForTest 8 -EnableNoGcRegionForTest 0 -BeamWeightTermForTest PersistentBuffDelta -BeamWeightScaleForTest 1.5 -TimeoutSeconds 240 -EvidenceDirectory .local/strategy-refactor-p7a/persistent-1p5-100 -CleanupInstanceOnExit` Passed，实例清理。对 `.local/strategy-refactor-p6/cross-turn-100`，根戳记及除该扰动外的政策相同；战损 22→55 HP，药水 2→2，结束回合 17→25。默认值不变，不追加同系数扫描；本轮未运行 Linux 门禁或其他权重组合。

## 策略重构 P6 收口对照（2026-09-28）

- #90、#100、#101 均用 `pwsh -NoProfile -File tools/run-unattended-test.ps1 -CheckpointArchivePath <对应原包> -CheckpointSelector start -ReplayMode SearchOnly -FixedSearchBudget -PerformancePresetForTest VeryHigh -SearchBudgetOverrideMilliseconds 180000 -SearchMaxDegreeOfParallelismForTest 8 -EnableNoGcRegionForTest 0 -TimeoutSeconds 240 -EvidenceDirectory <对应目录> -CleanupInstanceOnExit` 顺序运行，均 Passed、实例清理。证据为 `.local/strategy-refactor-p6/potion-plan-90`、`final-100`、`final-101`。#90 对 P7b 原结果的动作、质量、续用、剪枝和请求展开／转移／选择分支全同，0 战损／最终 49 HP；#100、#101 对先前 P6 同根结果的这些字段全同，分别 22 战损／2 药、58 战损／1 药。对 P6 前同根同政策基线，#100 为 41→22 战损，#101 为死亡→胜利。跨回合计划类型改动后单独重跑 #100，证据 `.local/strategy-refactor-p6/cross-turn-100`，动作、质量、续用、剪枝与工作量继续全同。
- `python tools/StrategyCorpus/run.py --manifest coverage/strategy-refactor-p0/corpus.json --out .local/strategy-refactor-p6/final-sentinel --case ga-silent-boss` 为 `comparable`；`python tools/StrategyCorpus/compare.py --left .local/strategy-refactor-p5/final-shared-scheduler-dop1 --right .local/strategy-refactor-p6/final-sentinel --out .local/strategy-refactor-p6/final-sentinel-comparison` 对 GA-SILENT-BOSS-00 判定“逐位相同”。左侧另外五根未在右侧运行，比较器标注“缺少一侧”，不参与本轮哨兵判定。最终源码 Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=238`；Linux 门禁未运行。

## 策略重构 P6 免费药计划证据（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release` 成功，0 警告、0 错误；`pwsh -NoProfile -File tools/verify-refactor-boundaries.ps1` 输出 `REFACTOR_BOUNDARIES_OK search_files=238`。未运行 Linux 门禁。#90 同根和已达标哨兵尚未执行：用户游戏进程正在运行，依约不启动无头实例。本次改变计划成员的地平线资格，静态与编译结果不能证明行为或质量保持。

## 策略重构 P6 延后复制效果登记（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release` 成功，0 警告、0 错误；`pwsh -NoProfile -File tools/verify-refactor-boundaries.ps1` 输出 `REFACTOR_BOUNDARIES_OK search_files=238`。未运行 Linux 门禁。用户的游戏进程仍在运行，未启动无头实例；#101 同根行为对照及已达标哨兵仍待执行，编译和结构门禁不构成行为等价证据。

## 策略重构 P6 共用计划成员派发（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release` 成功，0 警告、0 错误；`pwsh -NoProfile -File tools/verify-refactor-boundaries.ps1` 输出 `REFACTOR_BOUNDARIES_OK search_files=237`。未运行 Linux 门禁。现有游戏进程运行且主机可用内存不足以取得无头实例租约，未执行 #100／#101 同根结果对照；当前仅有静态与编译证据，不宣称行为逐位一致。

## 策略重构 P6 类型化收益证据（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。#101 首次同根请求在写结果前以 `-1073741819` 退出，证据 `.local/strategy-refactor-p6/typed-payoff-101/launcher-result.json`，无崩溃堆栈，原因未定位；启动器清理了实例。同源码同配置重试 Passed，根戳记、执行政策、完整动作、冻结质量与 `.local/strategy-refactor-p6/semantic-copy-101` 全同，均为胜利、58 战损／1 药，证据 `.local/strategy-refactor-p6/typed-payoff-101-retry`。重试进程日志中出现 Godot 的 `Invalid Task ID` 与对象终结器断开信号错误，但仍完成请求，无法据此归因首次崩溃。Linux 门禁未运行。

## 策略重构 P6 复制效果提名（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release --no-restore` 成功，0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。#101 `a422c1c56022446c85f6ce00962019c4` 的 `combat_start` 在 VeryHigh／180 秒／DOP8 下严格恢复并 Passed；与 `.local/strategy-refactor-p6/horizon-101` 相比，根戳记、执行政策、完整动作与冻结质量全同，均为胜利、58 战损／1 药、第 16 回合结束。新证据 `.local/strategy-refactor-p6/semantic-copy-101`；实例由启动器清理。尚无新增优化量，未运行 Linux 门禁。

## 策略重构 P5 共享调度收口（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release --no-restore` 成功，0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。`python tools/StrategyCorpus/run.py --manifest coverage/strategy-refactor-p0/corpus.json --out .local/strategy-refactor-p5/final-shared-scheduler-dop1 --case report-24 --case report-37 --case report-81 --case report-89 --case ga-ironclad-elite --case ga-silent-boss` 六根均 `comparable`。对 `.local/strategy-refactor-p4/after-p4-20260928` 同政策基线，排除后加的 P8a `searchWorkAttributions` 后，六根完整动作、续用、终局、工作量及剪枝逐位相同；原始比较证据 `.local/strategy-refactor-p5/final-shared-scheduler-comparison`。GA-SILENT-BOSS-00 的 DOP8 对 `.local/strategy-refactor-p5/executor-after-dop8` 的 122 个非时序字段、动作和续用全同；搜索耗时 24,053.0502→24,091.1489 ms，worker 分配 11,885,994,992→11,879,940,960 字节，均仅作单样本观测。DOP8 证据 `.local/strategy-refactor-p5/final-shared-scheduler-dop8`；无头实例已清理。Linux 门禁依用户要求未运行。

## 策略重构 P5 回合尾部作业（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。GA-SILENT-BOSS-00 在 VeryHigh／25,000 节点／110 秒配置下，DOP1 对 P4 同政策基线排除后加归因字段后逐位相同；DOP8 对 `.local/strategy-refactor-p5/executor-after-dop8` 的 122 个非时序字段、动作和续用全同，均为 44 战损、69,257 展开、222,131 转移。证据 `.local/strategy-refactor-p5/serial-tail-job-dop1` 与 `tail-job-dop8`。其余玩家根与生成根、Linux 门禁未运行。

## 策略重构 P5 药水作业（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。#24 `f88c625680e64a2c99a2ab8844abcbdd` 的 `combat_start` 严格恢复后，VeryHigh／25,000 节点／DOP1／110 秒搜索 `comparable`；同 P4 基线排除后加归因字段后，完整动作、续用、终局、工作量、剪枝逐位相同。证据 `.local/strategy-refactor-p5/serial-potion-jobs-report24`。DOP8 药水根、其余语料、Linux 门禁均未运行；P5 尚未收口。

## 策略重构 P5 串行挂起选择作业（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。#89 `10d01cc2d1f7445c8ff72e76e783aeb0` 严格恢复 `combat_start` 后，以 VeryHigh／25,000 节点／DOP1／110 秒固定配置取得 `comparable`；同 P4 基线比较，排除后加的 `searchWorkAttributions` 后完整动作、续用、终局、工作量及剪枝逐位相同。证据 `.local/strategy-refactor-p5/serial-choice-jobs-report89`。未跑其余语料、DOP8 或 Linux 门禁；药水与尾部作业尚未迁移。

## 策略重构 P5 串行卡牌作业（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。GA-SILENT-BOSS-00 的 DOP1、VeryHigh／25,000 节点／110 秒搜索 `comparable`；同 P4 同政策基线比较，排除后来新增的工作归因数组后，完整动作、续用、终局、工作量及剪枝逐位相同。证据 `.local/strategy-refactor-p5/serial-card-jobs-dop1`。只覆盖串行卡牌作业；未跑其余五根、DOP8 或 Linux 门禁，P5 未收口。

## 策略重构 P5 作业状态所有权（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release --no-restore` 成功，0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。GA-SILENT-BOSS-00 在 VeryHigh／25,000 节点／DOP8 下同既有 `.local/strategy-refactor-p5/executor-after-dop8` 比较，122 个非时序字段、路线与续用全同：44 战损、69,257 展开、222,131 转移。证据 `.local/strategy-refactor-p5/admitted-parent-outside-executor-dop8`。该结果只验证状态所有权搬迁；串行接入和 P5 全语料尚未执行，Linux 门禁依要求不运行。

## 策略重构 P5 卡牌回放入口（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release --no-restore` 通过，0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。`python tools/StrategyCorpus/run.py --manifest coverage/strategy-refactor-p0/corpus.json --out .local/strategy-refactor-p5/choice-dispatch-after-dop1 --case ga-silent-boss` 为 comparable。对 `.local/strategy-refactor-p4/after-p4-20260928` 同根 DOP1 基线比较时，新增的 P8a 归因数组是唯一协议字段差异；排除该后加字段后，质量、完整动作、续用、全部其余非时序指标和剪枝逐位相同。证据 `.local/strategy-refactor-p5/choice-dispatch-compare-dop1`。未跑其余五根、DOP8 或 Linux 门禁；P5 尚未收口。

## 策略重构 P8c 同根路线首分歧（2026-09-28）

- `python tools/StrategyCorpus/route_divergence.py --baseline .local/strategy-refactor-p7c/baseline-97 --witness .local/strategy-refactor-p7c/target-reps-97-retry --out .local/strategy-refactor-p7c/route-divergence-97.json` 成功；两份旧实验结果的根戳记和执行政策相同，质量顺序判定见证路线更好。共同前缀为首张精神过载，第 2 步从灵体变为致死性；旧路线 21 战损／0 药，见证路线 9 战损／0 药。与 #81 不同根配对时明确拒绝。该命令只读取已有证据，未启动游戏、未验证当前源码可重现 9 战损，也未定位搜索内的首个丢路阶段；Linux 门禁未运行。

## 策略重构 P5 执行器提交合同（2026-09-28）

- Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=237`。GA-SILENT-BOSS-00 同根、VeryHigh／25,000 节点／110 秒：DOP8 对上次源码的 122 个非时序字段及动作全同，均胜利、44 战损／0 药、总展开 69,257、转移 222,131；DOP1 的质量、动作、续用及非时序指标全同。#81 `4eb25e79483c462089f9c6088d650c77` 开战根的无头恢复与固定预算搜索 `comparable`，对上一轮同根源码的所有逐位字段一致，均为胜利、31 战损／0 药／最终 55 HP、NodeLimit。证据 `.local/strategy-refactor-p5/executor-after-dop8`、`executor-after-dop1`、`executor-report81` 及 `executor-report81-comparison`；实例由运行器清理。未运行 Linux 门禁或整批语料。

## 策略重构 P8a 直接成员归因（2026-09-28）

- Release 构建 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=236`。#81 `4eb25e79483c462089f9c6088d650c77` 的 `combat_start` 在 VeryHigh／25,000 节点／110 秒／DOP1 下严格恢复并得到 `comparable`；请求总展开 161,521、转移 680,415、选择分支 0，分项为 `PrimaryBeam` 22,981／93,916、`SmartPotionGradient` 50,000／224,589、`OpeningPowerRouteMember` 88,540／361,910，合计逐项相等。证据 `.local/strategy-refactor-p8a/direct-attribution-report81`，实例由运行器清理。这只直接验证当前触发的三个类别；新颖性、前两回合侦察等未启用成员本轮未运行，旧 13 个超时包未重跑，Linux 门禁未运行。

## 策略重构 P5 回合尾部准入（2026-09-28）

- Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=236`。GA-SILENT-BOSS-00 同根 VeryHigh／25,000 节点／110 秒，DOP8 与本轮改动前的路线及 122 个非时序字段全同，均胜利、44 战损／0 药、请求总展开 69,257、转移 222,131；DOP1 的质量、动作、续用和非时序指标全同。证据 `.local/strategy-refactor-p5/choice-plan-after-dop8`、`endturn-admission-after-dop8`、`choice-plan-after-dop1`、`endturn-admission-after-dop1`。该场景不证明周期出口批次路径命中；未跑 Linux 门禁或其他包。

## 策略重构 P5 普通卡牌选择计划（2026-09-28）

- 改动前单次采集 GA-SILENT-BOSS-00 的 DOP8 固定根；改动后同根、VeryHigh、25,000 节点、110 秒，DOP8 的路线与 122 个非时序字段一致，均胜利、44 战损／0 药、请求总展开 69,257、转移 222,131。当前源码 DOP1 对前次同源码构建的基线，质量、动作、续用和非时序指标全同。证据 `.local/strategy-refactor-p5/choice-plan-baseline-dop8`、`choice-plan-after-dop8`、`choice-plan-after-dop1`。离线与语料比较器已将新工作归因数组里的 GC 次数／暂停作为波动字段排除；首次未经排除的对照只在这些字段报差异，没有重跑搜索。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=236`；Linux 门禁依用户要求不运行。

## 策略重构 P8a 主 Beam 工作归因（2026-09-28）

- `dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false --no-restore` 成功，0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=236`。离线 GA-SILENT-BOSS-00 当前源码单次请求 `comparable`，70,460 个展开节点归为 `PrimaryBeam` 9,017、`RefinementBeam` 15,983、`OpeningPowerRouteMember` 45,460，合计与请求总数一致。与 `.local/strategy-refactor-p8a/attribution-silent` 相比，身份、动作、质量、结果、旧非时序指标及剪枝计数完全相同，只有 `searchWorkAttributions` 分类变化；证据 `.local/strategy-refactor-p8a/primary-attribution-silent` 与 `primary-attribution-comparison`。未运行 Linux 门禁或超时包。

## 策略重构 P7a 多项权重矩阵（2026-09-28）

- `python -m py_compile tools/StrategyCorpus/matrix.py` 通过。GA-SILENT-BOSS-00 使用 `.local/strategy-refactor-p8a/attribution-silent` 的当前源码无扰动基线，分别单次运行 `EnemyHp:0.8`、`PersistentBuffDelta:1.2`，两次均为 `comparable`；矩阵工具核对同根和除扰动外相同政策。基线和两次扰动均为胜利、44 战损／0 药／最终 26 HP，第 9 回合结束；两项扰动的终局 score 都从 9999299954 降到 9999299953，故按冻结质量比较为变差。证据 `.local/strategy-refactor-p7a/matrix-current-v2`。未扩展到整批语料，未调整生产权重；本次只有 Python 工具与文档改动，未重复 C# 构建或运行 Linux 门禁。

## 策略重构 P8c 丢路查询（2026-09-28）

- `python tools/ContextualOrdering/test_first_loss.py` 通过 1 项构造测试：两个不同求解器均有编号 1、2 的保路边界，编号 1 的目标前缀分别在全局 Beam 与后续仲裁落选，编号 2 作为各自的截断末边界忽略；查询输出两条独立结果和同状态别名。未运行玩家 ZIP 自动采集或真实路径诊断；Linux 门禁依用户要求不运行。

## 策略重构 P8a 超时进度取证（2026-09-28）

- 请求工作归因：GA-SILENT-BOSS-00 的固定生成根在当前源码可比较，总展开 70,460 = `OpeningPowerRouteMember` 45,460 + `UnattributedDirect` 25,000；总转移 225,665 = 146,815 + 78,850；总选牌 12,296 = 8,874 + 3,422。总搜索耗时约 35,950.7 ms 等于两分项之和。相对 `.local/strategy-refactor-p7b/ga-silent-sentinel`，动作、结果和旧非时序指标一致，只有新增归因字段不同；证据 `.local/strategy-refactor-p8a/attribution-silent` 与 `attribution-comparison`。没有逐个跑 13 个超时包。
- 新超时请求会在停止常驻实例后、覆盖会话监控状态前，将报告 ID 和更新时间均匹配本请求的最近监控快照保存至请求证据；快照补充当前成员节点上限、结束节点和已完成回合层。仅执行 CheckpointTool Release 编译及 Windows 结构门禁；尚未实际制造一次超时，不能声称运行时取证已通过。旧 13 个超时包没有这些新字段，不从历史 `timeout` 状态推断单一主因。Linux 门禁依用户要求不运行。

## 策略重构 P7c 目标代表试验（2026-09-28）

- #97 `5b37246d49354de9a2e8523e8a2c62c8` 的 `combat_start`、VeryHigh／180 秒／DOP 8：旧策略完整胜利 21 战损／0 药／最终 35 HP；提前预约每目标代表的实验为 9 战损／0 药／最终 47 HP，请求总展开 301,145→479,327，搜索耗时约 82.0→133.8 秒。证据 `.local/strategy-refactor-p7c/baseline-97` 与 `target-reps-97-retry`；首次试验启动被私有游戏路径校验拒绝，未进入搜索，实例已清理。
- 已达标多敌 #81 在相同根和政策下，实验为 9 战损／0 药，同源码基底撤下试验后为 8 战损／0 药；证据 `.local/strategy-refactor-p7c/sentinel-81` 与 `sentinel-81-current-baseline`。该 1 HP 退化导致实验源码撤回。两次完整请求均 Passed，实例已清理；Linux 门禁依用户要求不运行。

## 策略重构 P7b 混沌药生成链（2026-09-28）

- #90 `945939a12ac944999302b9b7f1cb34ea` 同一 `combat_start`、VeryHigh／180 秒／DOP 8、强制使用迅捷与混沌的记录政策：基线完整胜利 3 战损／2 瓶原有药／最终 46 HP，当前完整胜利 0 战损／2 瓶原有药加 2 瓶免费生成药／最终 49 HP；请求总展开 460,810→500,000，总搜索耗时约 145.0→157.7 秒。当前路线第 2 回合连续使用四瓶药，结束于第 6 回合。证据 `.local/strategy-refactor-p7b/baseline-90` 与 `chain-attack-90`。中间仅固定原首回合的生成链实验为 3 战损、第 2 回合结束；加合法进攻跟进后才追平人工。两个无头实例均已清理。
- GA-SILENT-BOSS-00 同政策、25,000 节点／110 秒、DOP1，P7a 无扰动基线对当前源码的动作、结果及非时序计数逐位相同，均为 44 战损／0 药；证据 `.local/strategy-refactor-p7a/generated-baseline` 与 `.local/strategy-refactor-p7b/ga-silent-sentinel`。Release 编译和 Windows 结构门禁通过；Linux 门禁依用户要求不运行。未执行完整自动部署，不能据此宣称实机计划回放通过。

## 策略重构 P7a 权重敏感度（2026-09-28）

- Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=235`；Python 脚本语法检查通过。`run.py --case` 分别对 #24 玩家根与 GA-SILENT-BOSS-00 采集无扰动基线及 `CurrentEnergy:0.8`，四次均 `comparable`。`sensitivity.py` 接受两组同根对照且核对其余政策相同；#24 前后均胜利、0 战损／1 药／最终 57 HP，生成根前后均胜利、44 战损／0 药／最终 26 HP。两根动作与工作量有差异，质量分类均为不变。证据在 `.local/strategy-refactor-p7a/`；无头实例已由运行器清理。默认权重未调整；Linux 门禁依用户要求不运行。

## 策略重构 P6 首回合计划（2026-09-28）

- 计划地平线：`dotnet run --project tools/PowerCardValuationChecks/PowerCardValuationChecks.csproj -c Release` 通过，覆盖未兑现计划不续期、兑现后在第 16 至 20 个无进展回合续期及第 21 回合结束续期（普通上限 16、牌堆周期 5）。Release 编译及 Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=235` 通过。#101 与 #100 最终源码同根复测均 Passed，战损／用药／最终 HP 与接入前相同，请求总 expanded／transitions／choice branches 也相同；证据 `.local/strategy-refactor-p6/horizon-101` 与 `horizon-100`。这两根未直接触发续期，长线搜索触发效果尚未有实战样本；Linux 门禁未运行。
- 跨回合能力计划：#100 `88619c63f91b48998737d7a9d623e2df` 的 `combat_start`、VeryHigh／180 秒／DOP 8，修改前完整胜利 41 战损／2 药、最终 29 HP；修改后完整胜利 22 战损／2 药、最终 48 HP。`PLAN_SEARCH_DISCOVERY count=0` 后，末段 `DEFERRED_POWER_PLAN` 选中第二回合飞刀扇前缀，成员展开 53,316。证据 `.local/strategy-refactor-p6/baseline-100` 与 `.local/strategy-refactor-p6/deferred-100-final-pass`。
- 已达标哨兵 GA-SILENT-BOSS-00：DOP8、VeryHigh、25,000 节点／110 秒，修改前后 121 个非时序字段全同，完整胜利 44 战损／0 药；证据 `.local/strategy-refactor-p5/potion-admission-dop8` 与 `.local/strategy-refactor-p6/deferred-sentinel-dop8`。P6 尚需计划驱动地平线，不能据此宣称阶段全部完成。
- #101 `a422c1c56022446c85f6ce00962019c4`：同一 `combat_start`、VeryHigh／180 秒／DOP 8，基线死亡、预计战损 70／0 药；计划成员完整胜利、战损 58／1 药、最终 12 HP。完整证据在 `.local/strategy-refactor-p6/baseline-101` 与 `.local/strategy-refactor-p6/plan-101-after-gradient`。
- #100 `88619c63f91b48998737d7a9d623e2df`：首回合计划入口阶段结果与基线同为胜利 41 战损／2 药、最终 29 HP；放在 Smart 审计中间的 44 战损跨回合实验已撤回，证据分别在 `.local/strategy-refactor-p6/baseline-100`、`plan-100-v1`、`plan-100-target-payoffs`。末段入口的最终收益见本节首项。
- 当前源码 Release 编译成功，Windows 结构门禁返回 `REFACTOR_BOUNDARIES_OK search_files=234`。未运行 Linux 门禁或整批语料；构造 P6 计划入口后的哨兵尚未重测。

## 策略重构 P5 共享候选准备（2026-09-28）

- 药水候选准入合并：Release 编译 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=234`。离线 GA-SILENT-BOSS-00 同政策 25,000 节点／110 秒，当前 DOP1 对既有 P5 DOP1、当前 DOP8 对既有 P4 DOP8，各比较 121 个非时序字段全同；两者均完整胜利、44 战损／0 药。证据在 `.local/strategy-refactor-p5/potion-admission-dop1` 与 `potion-admission-dop8`。Linux 门禁依用户要求不运行。
- `ExpansionPlan` 同时供串行展开与并行准备读取卡牌、药水候选。`dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false --no-restore` 成功，0 警告、0 错误；`pwsh -NoProfile -File tools/verify-refactor-boundaries.ps1` 返回 `REFACTOR_BOUNDARIES_OK search_files=232`。尚未运行搜索语料或 DOP1／DOP8 对照，不能据此认定行为等价。Linux 门禁按用户要求不运行。
- 卡牌／药水子节点字段抽入共享工厂：Release 编译 0 警告、0 错误，Windows 门禁返回 `REFACTOR_BOUNDARIES_OK search_files=232`；本次只核对字段来源及调用位置，尚未运行固定根。Linux 门禁按用户要求不运行。
- 普通卡牌选择分派改走同一入口：Release 编译 0 警告、0 错误，Windows 门禁返回 `REFACTOR_BOUNDARIES_OK search_files=232`；尚未运行阶段语料。Linux 门禁按用户要求不运行。
- 离线 GA-SILENT-BOSS-00：当前 DOP8 与临时 P4 提交 `e248b6e3` 的 DOP8，同政策、25,000 节点、110 秒，`compare_results.py` 的 121 个非时序字段全同，战损 44，expanded 69,257，transitions 222,131。当前 DOP1 对 P4 DOP1 的早期边界对照也全同；但此后共享选择分派发生源码变化，DOP1 最终对照仍待运行。P4 自身的 DOP1／DOP8 已有动作次序与计数差异，两边战损同为 44。Linux 门禁未运行。
- 父节点入场准入合并后，Release 编译 0 警告、0 错误，Windows 门禁 `REFACTOR_BOUNDARIES_OK search_files=232`；仍待最终源码的固定根对照。Linux 门禁不运行。
- 卡牌候选准入与快照所有权合并：Release 编译 0 警告、0 错误，Windows 门禁 `REFACTOR_BOUNDARIES_OK search_files=232`；GA-SILENT-BOSS-00 当前 DOP8 对 P4 同 DOP 基线使用 `compare_results.py` 比较 121 字段全同，战损 44、expanded 69,257、transitions 222,131。其余根和 DOP1 最终源码对照尚未运行。
- P5 候选语义阶段对照：`python tools/StrategyCorpus/run.py --manifest coverage/strategy-refactor-p2/corpus.json --out .local/strategy-refactor-p5/after-p5-20260928` 的四个玩家根和两个生成场景均 `comparable`；`compare.py --left .local/strategy-refactor-p4/after-p4-20260928 --right .local/strategy-refactor-p5/after-p5-20260928 --out .local/strategy-refactor-p5/compare-p5-20260928` 六根逐位相同。P5 执行器接口和串行／并行调度统一尚未实施，不以该对照宣称 P5 全部完成。

## 策略重构 P4 登记表（2026-09-28）

- 药水成本档位及开局使用类型移至 `PotionValuationRegistry`：`dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false --no-restore` 成功，0 警告、0 错误；`pwsh -NoProfile -File tools/verify-refactor-boundaries.ps1` 返回 `REFACTOR_BOUNDARIES_OK search_files=229`。未运行无头语料；语义逐位对照留到 P4 收口。Linux 门禁按用户要求不运行。
- 白噪声、夜魇与复制药水的开局身份匹配移至 `OpeningActionRegistry`：Release 编译 0 警告、0 错误，Windows 门禁返回 `REFACTOR_BOUNDARIES_OK search_files=230`。未运行无头语料；Linux 门禁按用户要求不运行。
- 开局目标变体与每目标进攻代表移至 `TargetPlanRegistry`：Release 编译 0 警告、0 错误，Windows 门禁返回 `REFACTOR_BOUNDARIES_OK search_files=231`。原始 3 目标、前 3 次目标动作、最多 2 次改目标以及目标排序保持原值；语料对照尚未运行。Linux 门禁按用户要求不运行。
- P4 收口：`python tools/StrategyCorpus/run.py --manifest coverage/strategy-refactor-p2/corpus.json --out .local/strategy-refactor-p4/after-p4-20260928` 单次采集四个玩家根和两个生成场景，全部 `comparable`；`python tools/StrategyCorpus/compare.py --left .local/strategy-refactor-p3/after-p3-20260928 --right .local/strategy-refactor-p4/after-p4-20260928 --out .local/strategy-refactor-p4/compare-p4-20260928` 六根均逐位相同。无头实例由运行器清理；未运行 Linux 门禁。

## 策略重构 P2 外层补搜迁移（2026-09-28）

- 强制用药开局、回合边界、零费开局、战斗中精炼、回合末选牌和提前复制补搜已从外层 `Solve` 移至 `PostSearch`，前两回合探索改用 `SearchPassContext`。本边界沿用原调用顺序、预算读取与诊断标签。
- 本次只取得 Release 编译和 Windows 结构门禁证据；用户游戏运行期间未启动无头实例。行为逐位对照仍待执行，不能据此宣称搜索结果等价。Linux 门禁按用户要求未运行。
- 强制用药开局补搜三个成员的预算切片迁入 `SearchBudgetWindow`；本轮只检查原公式与调用位置、Release 编译及 Windows 结构门禁。该模式尚无行为对照，统一留到 P2 收口验证。
- 其余 `PostSearch` 成员的预算切片迁入相同窗口；长整型时间预检保留原位。仅做 Release 编译与 Windows 结构门禁，固定语料行为对照待 P2 收口一次运行。
- 请求管线现统一派发后处理 Pass。只检查 Release 编译、Windows 结构门禁和原调用顺序；游戏实例未启动，完整结果、接管时机与工作量的逐位对照仍待执行。
- 主 Pass 六处固定前缀成员预算切片改走账本窗口，保留原采样顺序与常数；仅做 Release 编译及 Windows 结构门禁，实际成员派发与结果对照合并到 P2 收口。
- 夜魇开局成员改走 `ProfileWindow`，仍使用配置时间帽和请求剩余节点；仅做 Release 编译及 Windows 结构门禁，实际路线对照留到 P2 收口。
- P2 收口：`python tools/StrategyCorpus/run.py --manifest coverage/strategy-refactor-p2/corpus.json --out .local/strategy-refactor-p2/after-p2-20260928` 运行一次，#24、#37、#81、#89 与两个生成场景均为 `comparable`，无头实例已清理。`python tools/StrategyCorpus/compare.py --left .local/strategy-refactor-p2/baseline-0471 --right .local/strategy-refactor-p2/after-p2-20260928 --out .local/strategy-refactor-p2/compare-p2-20260928` 报告六根逐位相同。最终行为源码的 Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=219`；Linux 门禁按用户要求未运行。
- P3 双药开局来源迁移：本边界只执行 Release 编译与 Windows 结构门禁；#17 两种顺序和六根语料的行为对照保留至 P3 收口一次运行，未把 P2 的旧证据算作新源码通过。
- P3 提前复制药水来源迁移：用药数仍在逐候选派发时读取，静态检查其捕获时机；行为对照留到 P3 收口，不额外启动一个游戏实例。
- P3 零费开局来源迁移：调度器按来源声明保留重复前缀，八次实际尝试上限仍在模式中；仅做 Release 编译与 Windows 结构门禁，行为对照留到 P3 收口。
- P3 双药调度定向验证：#17 `b8bafe147e09452b97111fb036a619ca` 的 `start` 以 VeryHigh、180 秒、DOP 8 运行 SearchOnly，请求 `98e0aa939d8648cf9b9c790559c16f9c` Passed。诊断依次记录 `BLOCK_POTION+SWIFT_POTION`（选中）与 `SWIFT_POTION+BLOCK_POTION`（未选中），均完整胜利、预计战损 69 HP；最终用药 2 瓶。实例已清理。这只验证双药前缀派发和原结果，不代替 P3 全阶段逐位对照。
- P3 回合末选牌两条固定前缀通道迁移：本边界只执行 Release 编译与 Windows 结构门禁；候选顺序和结果逐位对照合并到 P3 收口。
- P3 回合边界首轮锚点迁移：静态核对旧键去重、八个原序锚点和唯一可跳过的药水业务失败；Release 编译与 Windows 结构门禁后，行为对照仍合并到 P3 收口。
- P3 固定前缀请求构造迁移：静态核对求解器构造位于可选药水异常捕获之外，仅 `Solve` 期间的已定义异常记录原诊断。只执行 Release 编译与 Windows 结构门禁，不另跑一份问题包。
- P3 回合边界后续两条前缀迁移：原预算、可回放检查和八次续搜上限保留；本次仅执行 Release 编译与 Windows 结构门禁，行为对照待 P3 收口。
- P3 强制用药两条前缀迁移：静态核对强制用药基线、上下界、Boss 最早回合和原 `try/catch` 范围；本次仅执行 Release 编译与 Windows 结构门禁，行为对照待 P3 收口。
- P3 战斗中精炼两条前缀迁移：静态核对可选用药异常仍只覆盖 `Solve`、第二条用药数从更新后的 `selected` 读取；本次仅执行 Release 编译和 Windows 结构门禁，行为对照留到 P3 收口。
- P3 主 Pass 六种开局前缀迁移：静态核对每条请求覆盖原 `beamPolicy` 并保留预算、profile 标志与候选顺序；本次仅执行 Release 编译和 Windows 结构门禁，行为逐位对照留到 P3 收口。
- P3 开局能力审计四条前缀迁移：静态核对 `ResetFixedPrefixSchedulingBaseline=false`、可选用药诊断与总计调用顺序；仅执行 Release 编译和 Windows 结构门禁，行为对照留到 P3 收口。
- P3 强制至少用药审计三条前缀迁移：静态核对 `RequireAtLeastOne`、`primary.PotionCount` 上限及不重置调度基线；仅执行 Release 编译与 Windows 结构门禁，行为对照待 P3 收口。
- P3 Smart 开局药水前缀迁移：静态核对原 8／12 候选上限、药水数量和可选后验诊断，双端结构门禁禁止协调器主文件新增直接固定前缀构造；只运行 Release 编译与 Windows 门禁，行为对照待 P3 收口。
- P3 夜魇与能力路线两条前缀迁移：静态核对夜魇可选用药诊断、能力成员的专用进度阶段及工作量／时间采样位置；仅执行 Release 编译与 Windows 结构门禁，行为对照待 P3 收口。
- P3 前两回合实验续搜迁移：静态核对独立时间/追加节点额度与 `EARLY_TURN_CONTINUATION` 的可选用药诊断；仅执行 Release 编译与 Windows 门禁，默认关闭模式不纳入普通语料质量结论。
- P3 宽度组合纯移动：`RunBeamWidthPortfolioPass`、单成员结果包装与成员遥测整段迁入独立 partial 文件；只执行 Release 编译及 Windows 结构门禁，动作与工作量逐位对照并入 P3 收口。
- P3 补充审计纯移动：三种审计、Smart 梯度及内存检查整段迁入独立 partial 文件；只执行 Release 编译与 Windows 结构门禁，动作、诊断和工作量逐位对照并入 P3 收口。
- P3 收口：`python tools/StrategyCorpus/run.py --manifest coverage/strategy-refactor-p2/corpus.json --out .local/strategy-refactor-p3/after-p3-20260928` 的四个玩家根和两个生成场景均为 `comparable`。`python tools/StrategyCorpus/compare.py --left .local/strategy-refactor-p2/after-p2-20260928 --right .local/strategy-refactor-p3/after-p3-20260928 --out .local/strategy-refactor-p3/compare-p3-20260928` 报告六根逐位相同；无头实例已清理。最终 Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=228`；Linux 门禁按用户要求未运行。

## 策略重构 P2 请求级预算所有权（2026-09-27）

- 合入 0.47.1 后，`python tools/StrategyCorpus/run.py --manifest coverage/strategy-refactor-p2/corpus.json --out .local/strategy-refactor-p2/baseline-0471` 一次采集 #24、#37、#81、#89 与两个生成场景，六根均为 `comparable`。原始包与完整证据留在 `.local`，实例由启动器清理。
- `python tools/StrategyCorpus/run.py --manifest coverage/strategy-refactor-p2/corpus.json --out .local/strategy-refactor-p2/ledger-outer` 后，`python tools/StrategyCorpus/compare.py --left .local/strategy-refactor-p2/baseline-0471 --right .local/strategy-refactor-p2/ledger-outer --out .local/strategy-refactor-p2/compare-ledger-outer`：六根动作、续用、结果、expanded、transitions、choice branches 和剪枝计数逐位相同。
- 运行器修复后 `python -m py_compile tools/StrategyCorpus/run.py tools/StrategyCorpus/compare.py tools/StrategyCorpus/test_compare.py` 通过；本次 Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=215`。按用户要求未运行 Linux 门禁；未做完整自动战斗或大批量回归。
- 补充审计改用 `SearchPassContext` 后，#24 `start` 严格恢复与 15 秒配置的 SearchOnly 请求 `286768afb4d14118863cdf5e79239cdd` Passed，实例已清理。此请求只验证上下文边界可执行，不与 110 秒固定语料比较，也不声明整场质量等价。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=216`；未运行 Linux 门禁。
- `SearchPassResult` 接管轮次停止状态后，#24 同根 SearchOnly 请求 `7ba221e6f4ef4d48926992768476e17b` Passed，实例已清理；与上一条的预计战损同为 1 HP，请求总展开 157004、转移 460496、选择分支 21493 均一致。这只覆盖普通返回路径，不代替接管或无胜利升级验证。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=217`；未运行 Linux 门禁。
- 无胜利升级改用 `SearchPassContext` / `SearchPassResult` 后，#24 `start` 严格恢复与 SearchOnly 请求 `9bcf08de582f4c67a0b6ea61cd03eba0` Passed。非固定预算 110 秒、首轮节点帽 5000；实际日志 `NO_VICTORY_ESCALATION start attempt=1 beam=135->270 nodes=5000->10000`，随后 `won=False improved=False`，保留首轮路线，实例已清理。60 秒配置的诊断请求没有进入升级，因为首轮约 29 秒、下一轮估计约 58 秒超过剩余时间；未把它算作升级路径验证。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=217`；未运行 Linux 门禁。
- 主搜索改为直接消费 `SearchPassContext` 后，#24 `start` 严格恢复与 15 秒配置的 SearchOnly 请求 `6e9c6c8bfa854a39bd610037c42fb536` Passed，实例已清理；这是执行路径检查，不是整批逐位对照。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=217`；未运行 Linux 门禁。
- 主 Pass 内六处前缀补搜预算读取迁入账本后，Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=217`。上一条短场景没有触发这六处前缀通道；此边界的行为对照留到 P2 收口的固定语料，本条只记录构建与结构证据。按用户要求未运行 Linux 门禁。
- `SearchRequestPipeline` 接管请求级首轮与升级派发后，#24 `start` 严格恢复与 15 秒固定预算 SearchOnly 请求 `23eaac6ccffc4e96896cdeb2aa272227` Passed，实例已清理。该请求覆盖首轮与固定预算返回，不把它写作本次无胜利升级的独立验证。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=218`；未运行 Linux 门禁。
- 开局能力／夜魇和宽度／新颖性成员改用 Pass 上下文后，#81 同根 SearchOnly 请求 `9af903cb5fdb4b1f941ea892863b7082` Passed，实例已清理。与 `baseline-0471/report-81` 同为 VeryHigh、Beam 135、25,000 节点、DOP 1、110 秒固定预算；动作与搜索结果仅有运行环境的 `savedNoGcRegionEnabled` 标记不同，归一化后的 `solverMetrics` 和执行政策无差异。先前请求漏传预设而用了包内 Beam 300，属于不可比较的诊断运行，不计入回归。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=218`；未运行 Linux 门禁。
- 三个补充审计入口共用 Pass 上下文后，#81 同根同政策 SearchOnly 请求 `b00a458217a840b8a4be98e773a75242` Passed，两层 Smart 药水梯度均执行，实例已清理。与 `baseline-0471/report-81` 的执行政策、`search-result.json` 全字段及剔除时间／分配／GC 的 `solverMetrics` 无差异。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=218`；未运行 Linux 门禁。
- Smart 药水梯度的层转移与回收开销改由账本读取后，#81 同根同政策 SearchOnly 请求 `65baeec98b724b2daa8ab9c2ea8894b6` Passed，实例已清理；与 `baseline-0471/report-81` 的执行政策、`search-result.json` 全字段及剔除时间／分配／GC 的 `solverMetrics` 无差异。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=218`；未运行 Linux 门禁。
- 双药死亡路线补搜移入 `PostSearch` 后，#17 `start` 同根 SearchOnly 请求 `7da93842f7d340358962d6c2d7267cd5`（60 秒）及 `cc2b89675b0e46ec80b58809d0d7cd12`（120 秒）均 Passed，实例清理；两次都是 5,000 节点、DOP 1、固定预算，所选仍为死亡路线，日志没有 `EARLY_POTION_PAIR`。因此这两份只证明请求通过，**不证明双药候选派发等价**；不能拿它们与历史 180 秒 / DOP 8 的双药胜利数值对照。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=219`；未运行 Linux 门禁。
- 双药 Pass 使用 `SearchBudgetWindow` 后，Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=219`。上条短请求没有实际双药候选，当时仅取得公式与结构证据；实际候选验证见下一条。未运行 Linux 门禁。
- 随后 #17 按原记录的 VeryHigh／180 秒／DOP 8／非固定预算执行 SearchOnly，请求 `9d5bdd2a400549ad8276bffb234493ac` Passed、实例清理。日志依次出现 `EARLY_POTION_PAIR` 的 `BLOCK_POTION+SWIFT_POTION`（选中）与 `SWIFT_POTION+BLOCK_POTION`（未选中），两条均完整胜利、预计战损 69 HP；最终用药 2 瓶。与历史报告的该机制结果一致，但历史 Mod 版本不同，不宣称完整工作量逐位相等。
- `SearchPassResult` 加入质量、累计工作量和轮次终止状态后，#24 `start` 15 秒固定预算请求 `2fdcb2d77b414b25b7a1b17d9127e46c` Passed，与先前 `pipeline-representative` 的执行政策、完整搜索结果和剔除时间／分配／GC 的指标无差异。另以 VeryHigh／110 秒／5,000 节点／DOP 1 非固定预算请求 `6b9d1d1f3aab4f59a0e6bf86808c0427` 验证升级：日志出现 135→270 Beam、5,000→10,000 节点，`won=False improved=False`，结果保留首轮路线；与此前同政策升级请求的完整搜索结果一致。升级轮能力成员的时间额度因实测时钟相差 127 毫秒，不计入逐位一致。两请求 Passed、实例清理；未传 VeryHigh 的一次诊断请求不参与对照。Release 编译 0 警告、0 错误，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=219`，未运行 Linux 门禁。

## 策略重构 P0/P1 固定根对照（2026-09-27）

- P0 对六个 `combat_start` 玩家根及两个生成场景各运行一次 VeryHigh / 每 solver 25,000 节点 / DOP 1 基线；P1 最终源码对同八根各运行一次。六个玩家根的严格恢复、continuation 与原生状态均通过。两轮原始动作及完整证据留在 `.local/strategy-refactor-p0/`；启动器清理无头实例。
- 基线的战损／用药依次为 #24 0/1、#37 1/0、#79 50/0、#81 31/0、#85 74/0、#89 9/0；生成场景 `GA-IRONCLAD-ELITE-00` 74/1、`GA-SILENT-BOSS-00` 44/0。这是固定短搜口径，不能与历史 180 秒策略成果直接比较。
- `python tools/StrategyCorpus/compare.py --left .local/strategy-refactor-p0/baseline --right .local/strategy-refactor-p0/after --out .local/strategy-refactor-p0/comparison`：#24、#37、#81、#89 与两个生成场景的动作、结果、续用、expanded、transitions、choice branches 及剪枝逐位相同。#79、#85 的 P0 基线分别用时 109,992 和 109,985 毫秒，贴近 110,000 毫秒限时；两根标为不可比较，不将工作量漂移算作纯重构差异。四个有效玩家根已达到最低门槛，备用 #56/#63 未运行。
- `python tools/StrategyCorpus/test_compare.py` 的分类、根身份、时限合同通过；最终 Release 构建 0 警告、0 错误。Windows/Linux 结构门禁均通过，Linux 侧 `rg` 解包于系统临时目录运行，无系统安装。未做完整自动战斗或大批量回归。
## GetId 缓存与模组注册时序（#141，2026-09-27）

- macOS 克隆游戏 + 隔离 HOME + `--force-steam=off`，mod_list 为 RitsuLib → CombatSolver → 探针。探针是最小 RitsuLib 内容模组：一张普通卡，在 `ModelRegistryInitializedEvent` 里打印 `GetId`；另编一个含与原版同名 `Leap` 卡的版本。不装求解器：`CARD.GET_ID_PROBE_CARD_PROBE_UNIQUE`，同名版正常启动；工坊 0.47.0：`CARD.PROBE_UNIQUE`，同名版 `DuplicateModelException` 启动失败；修复版：两种都与不装求解器一致。
- 控制器会话合同新增：进入战斗时注册表初始化信号已送达，且门控对原版类型始终放行、对模组类型只在信号后放行。该合同需要 Linux/Windows 无头入口，本机未重跑；macOS Release 构建 0 警告、0 错误，结构门禁通过。
## 下一版本（开发中）：最终续用单次回放（2026-09-27）

- 基线 `72e0f799`，所有搜索DOP1。抽弃牌／故障机器人两根各四次独立进程ABBA，固定预算且无时间边界，完整根／政策、动作含嵌套选牌、续用文本、非时序指标与剪枝计数一致。根回放计数按真实工作分别4→2、8→2单独断言；展开／转移维持1564／34802及2000／7783。计时区间重叠，不称稳定整体提速。
- `OFFLINE_HARNESS_FIXED_PREFIX_CONTINUATIONS=1` 的最终单牌输入：4／8／17回合固定终局前缀，四个独立进程ABBA，每case预热一次＋测量三次。36份计时结果、完整输出跨版本对账及计时外独立前缀oracle通过；核对完整StateText、回合、offset、数量、顺序和live根不变。17回合18.052→6.899ms只属于人工固定前缀Solve，实际搜索展开0。原始／汇总见 `.local/fixed-dop-20260927/long-prefix-final/` 与[报告](../performance/fixed-dop-20260927.md)。早期多牌误注入批次作废，不计最终证据。
- 原生 `FIXED-PREFIX-TURN-OUTCOMES` 已补充同一独立oracle和三个长路线case，原三回合actual/predicted验证保留。**本轮未执行**：`linear-replay-prefix` 启动前检测到玩家 `SlayTheSpire2.exe` 会话，120秒准入超时；没有停止其他进程或扩大超时，启动器输出 `UNATTENDED_INSTANCE_REMOVED`。forced-end／setup／adoption路径仅静态审阅，未称原生通过。
- Windows Release构建0警告0错误，两端结构门禁均 `REFACTOR_BOUNDARIES_OK search_files=212`；Bash门禁在Windows Git Bash运行，不是Linux游戏验收。只改离线helper输入后重编该宿主，生产DLL未变，因此复用已有普通搜索及构建证据。不运行完整自动部署、可见性能或发布门禁。

## 下一版本（开发中）：注能核心首回合产球（2026-09-27）

- 失败来源为0.47.1／`36372d40`的问题包 `0762b1da246243f1936a1e8750be8588`，工具箱选牌完成后第一次差异是预测0球／原生3个闪电球（4/9）。同版本游戏的 `InfusedCore.AfterSideTurnStart` IL核对了参与者、首回合条件及3次产球；原包未恢复。
- `tools/OfflineSearchHarness/InfusedCoreChecks.cs` 用生产DLL创建根，注入分支空球队列后调用真实遗物Hook。`OFFLINE_HARNESS_INFUSED_CORE_CHECKS=1`、DEFECT、`--milestone M1`：修改前失败 `first turn must channel three orbs; actual=0`；修改后14项Passed，覆盖3球、被动4／激发9、历史新增3次、T2空球不再产球、T2已有球不重复触发、持有者未参与不触发，以及根、父子、兄弟和球Model独占。证据 `.local/issue-bundles/0762b1da246243f1936a1e8750be8588/fix/{baseline,final}/`。这是离线诊断，不是原生actual/simulated验收。
- 新增 [INITIAL-TOOLBOX-INFUSED-CORE](../../../coverage/unattended/initial-toolbox-infused-core.json)：原生开局注能核心＋工具箱、1500ms固定搜索、增量等价，在首次准备结果完整状态匹配及原生选择顺序断言后停止，总超时120秒。**本轮未执行**：实际游戏进程仍运行，既有无头准入门禁禁止并行启动；未关闭用户游戏、绕过门禁或创建无头实例。
- 旧 `RELIC-HOOKS-BATCH-054` 从已完成原生产球的Play状态取根，只证明既有球与未来回合，不再作为空球准备根的首次产球证据。覆盖分类已改为显式模拟补偿，保留原生验收未完成的说明。
- Windows Release构建0警告0错误，修改文件JSON解析及格式检查通过。CoverageCatalog原有工程缺少RitsuLib分程序集引用，先因`GetOriginalIl`／`HarmonyIl`编译失败；使用仅本地的额外引用后构建成功，但`--verify-runtime-evidence`在读取既有`LOOP-FINAL-20260921.status=PassedWithDocumentedBoundaries`时抛JsonException，未完成覆盖门禁或重新生成派生目录。此问题不归因于本次产球修复，不伪造Passed状态。本轮不提升版本、不打包或发布；不宣称完整战斗、实机选牌部署或其他Mod组合已验收。

2026-09-29 续验：原生 DEFECT 首回合 `InfusedCore`＋`Toolbox` 选牌入口 Passed，`.local/multiplayer-p2/infused-core-initial-d62e9e529f204c0cbceb6cac8a86ad30/result.json`；该请求只核选牌顺序，没有明确的三球全状态差分。随后虚拟双人第一回合独立调用原版与模拟 `InfusedCore.AfterSideTurnStart`，持有人三颗闪电球、队友零球，全部玩家／敌人状态和九条 RNG 严格一致，Passed：`.local/multiplayer-p2/infused-core-two-5863c058d6364850b690835ce97e76be/peer-0/result.json`。覆盖目录中该 Hook 的运行证据据此更新；真实开战时的完整三球状态差分仍未验证。

## PR #143 合并上游 0.47.1（2026-09-27）

- 合并基线为上游 `7d9b4bed`，包含 `a59d319d`。手工解决 Opening、Phases 和两份记录文档冲突，保留上游终局／边界候选门禁、前缀异常清理及准备阶段整体补充审计旁路，同时保留本分支选择时点估值、终局前缀统计和七表／缓存校验。代码审阅未发现阻断问题。
- 合并后Windows Release构建0警告0错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=212`，冲突标记与未合并索引清除，`git diff --check` 通过。
- 原生六场最小集在第一场启动前因实机游戏进程仍运行而被启动器的未知游戏进程门禁排队，120秒准入超时；未启动无头游戏，不计行为失败或通过，也未关闭实机游戏、绕过门禁或扩大超时。自有实例 `pr143-merge-20260927` 由清理入口输出 `UNATTENDED_INSTANCE_REMOVED`。证据 `.local/pr143-merge-20260927/prefix-launcher.log` 与 `cleanup.log`；以下此前原生测试均为合并前证据。
- 改用不启动Godot的离线Coordinator／Smart哨兵：故障机器人精英、Low、Beam24、每成员2000节点、DOP1、20秒，完成10000总展开／40482转移、10 HP／0药。与此前同根结果106项非时间字段、完整动作路线、根／续用状态和策略配置一致，均未触发时间边界。证据 `.local/pr143-merge-20260927/offline/defect/` 与 `offline-comparison.json`；只证明该离线根，不替代原生整合或可见性能验收。
- 准备根合并后遵循上游提前返回，不再要求出现此前的Smart补充梯度日志；原始六场集保存在本地运行脚本。未执行合并后的原生六场、Linux或可见Steam验收，没有性能结论。

## 下一版本（开发中）：开局弃牌选择时点估值（2026-09-27）

- 失败基线来自本机 `MYTES_NORMAL` / `057a700fd80b4a65ac7f8641b4ad2cbf` 第1回合generation15，18:40:19.858智能用药审计的 `OpeningDiscardChoiceCardValue` 抛 `FLICK_FLACK+0 source=Hand action=ACROBATICS`。根牌组没有该牌；同战另一成功候选记录攻击药水生成该牌、临时0费与后续恢复1费，不把它当作失败分支完整回放。
- 新增 [OPENING-DISCARD-CHOICE-VALUE](../../../coverage/unattended/opening-discard-choice-value.json)。杂技从抽牌堆抽到两张同名同升级、伤害7／17的临时零费 `FLICK_FLACK` 及升级的中和；逐分支确认弃牌自动打出后旧完整状态键已不在四个牌堆，而估值仍等于选择时的正确物理实例。全部兄弟只建立一份纯值表，命中缓存后伪造状态键或选择上下文继续明确失败。
- `ea5c7b5bb1b34591b58539e8e9dac298` Passed：真实 `BuildOpeningHandSetupActions` 在DOP1及DOP2配置下成功，DOP1启用增量回放；搜索前后live不变，完成分支Fork状态不变，原生杂技选择和弃牌自动打出结束后完整ContinuationStamp与预测一致。这里的DOP2是入口配置覆盖，不宣称该局部Expand实际双lane并发；没有运行整场搜索质量或性能对照。
- Windows Release构建0警告0错误、结构门禁 `REFACTOR_BOUNDARIES_OK search_files=212`、静态代码审阅及 `git diff --check` 通过。实例使用仓库 `.local/headless-instances/opening-discard-value-1`，已由启动器输出删除成功；证据 `.local/opening-discard-value-20260927/native-1/`。未恢复原玩家战斗、未覆盖所有第三方及嵌套前置选择组合，未启动可见Steam。

## 下一版本（开发中）：跨回合固定前缀结果完整性（2026-09-27）

- 原始失败证据是旧日雕像50,537展开的持久路线：预测Continuation为T3 HP70→T4 HP61，末态HP43／累计掉血27，但七张逐回合结果表仅有T4–6。UI求和显示18，结束回合保护缺键读0。没有恢复原玩家战斗或取得已经退休的该场完整live日志，不据此宣称另有怪物／药水模拟偏差。
- 新增 [FIXED-PREFIX-TURN-OUTCOMES](../../../coverage/unattended/fixed-prefix-turn-outcomes.json)，通过现有无人ScenarioId入口运行。最小构造三次EndTurn固定前缀与第四回合后续搜索，内部DOP1／最多100展开／5秒，开启增量等价；验证每个前缀节点在结果投影前已持有Outcome、显式零及正战损、末回合部分前缀／终局前缀、空前缀、不合法回合和终局后动作仍拒绝，live根不变。独立原节点后备投影验证非零卖血差额、原分数不变和既有比较标注优先。
- 对同一真实求解结果逐项移除战损、回血、敌方损血、卖血、最大／实际格挡与能量表的第三回合键：七种磁盘缓存均按未命中处理且内容未改，录像导入拒绝；生成结果序列化、内存续用及结束回合预计值查询均拒绝缺项。正确结果序列化往返通过，未删除玩家缓存。
- 最终 `ea17a1f38de84a3b9b32797923ca1892` Passed，原生从T1逐次推进到T4，每次等待明确的EndPlayerTurnAction完成，完整ContinuationStamp与预测一致，LiveEndTurnRiskEvaluator与计划该回合损失一致，复用及UI求和包含前缀损失。只跑至最早覆盖三段前缀的边界，未执行SolverController全自动停机分支或整场自动部署。证据 `.local/fixed-prefix-outcomes-20260927/prefix-3/`。
- 首次 `d67f326496fa4f749b9c68d990f39bea` 在新配置的首次洗牌教程等待至120秒，未记通过，启动器停止并清理实例。只在后续私有实例中导入进度模板并关闭教程，不改实机设置；`6feda5c2bbae43649327d0645302ba8f` 通过后，因新增节点所有权及精确异常断言运行最终夹具，未扩大超时。所有实例均在仓库 `.local/headless-instances/`，启动器分别输出删除成功。
- 终局归属哨兵 `TERMINAL-TURN-PLAYER-START-V0111` / `6b4fb4ff7e594ebc9741e71155942a28` Passed：T1 EndTurn在T2准备阶段通过 `MERCURY_HOURGLASS` 获胜，增量验证／0战损／终局T2及动作回合结果完整性通过，避免按终局T2误要求第二回合动作统计。其后仅增强测试代码，生产源码未变，不重复该哨兵。
- 最终Windows Release构建0警告0错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=212`，代码审阅通过。未运行Linux、原玩家存档重放或可见Steam测试；不将该最小合同当作全部卡牌语义或全量质量验收。

## 下一版本（开发中）：回合准备固定前缀边界（2026-09-27）

- 失败基线来自本机原生 `GAMBLING_CHIP` 开局日志：`CombatBeamSolver.SolveCore → CombatSearchCoordinator.RunSearchPass` 抛 `include_turn_setup=True prefix=+STAMPEDE`，对应延后能力 `[EndTurn, STAMPEDE]`。未恢复完整玩家存档；不把后续同遭遇重试当成同一根。
- 原生短场景先用较强牌组验证页面顺序，`df377b96d5a14c718adbdc21ed3732cd` Passed；该根提前取得零战损，所以另用含6张伤口的 [准备阶段回归夹具](../../../coverage/unattended/turn-setup-fixed-prefix-stampede.json) 覆盖仍需补充搜索的承伤根。最终 `0c96548a196e449e9fa98a7acd6e7422` Passed：12张牌、`GAMBLING_CHIP` 与惊逃，DOP2、20秒固定时间预算，完整胜利投影21 HP／0药、5回合，总展开8,549、转移17,429；原生 `Visible → SearchStarted → PlanReady → Selected` 顺序通过，在首个准备结果与选择执行后停止，没有部署整场战斗。
- 最终日志包含一次 `OPENING_PREFIX_REFINEMENT skipped reason=TurnSetupRoot`，仍进入正常 Smart 梯度（本根无药，返回 `no_potion_acceptable`）；没有把准备前动作送入可选固定前缀成员。夹具中的性能档位／Beam／节点测试覆盖在准备结束后才应用，本次准备搜索实际基线为 Beam60／120,000节点，不能按请求中的Low／24／6,000解释。证据 `.local/turn-setup-fixed-prefix-20260927/native-setup-loss/`。
- 首次启动在游戏请求提交前因无默认离线 `settings.save` 失败，未计行为验证；只向新建的自有隔离实例复制当前Steam设置作为模板后继续，未修改实机配置或存档。首次失败和两次完成均由启动器输出 `UNATTENDED_INSTANCE_REMOVED` 删除整个实例；所有实例位于仓库 `.local/headless-instances/`。
- 普通 Play 根哨兵复用本轮修改前保存的故障机器人精英 Coordinator / Smart 输入与政策：Low、Beam24、2,000节点、DOP1、20秒。修复后总展开10,000、转移40,482、10 HP／0药，与基线106项非时间对照、完整 `route.json`（包括选择）、根续用文本、后续续用及策略文件相同，均无时间截断。该对照仅证明此根的原路径保持，不宣称准备根的搜索质量不变或性能改善。证据 `.local/turn-setup-fixed-prefix-20260927/play-sentinel-comparison.json`。
- Windows Release 构建0警告、0错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=212`，代码审阅通过。未运行Linux、完整自动部署或可见游戏验收；实机由用户验证。

## 0.47.1 紧急回归修复（2026-09-27）

- `FIXED-PREFIX-TURN-LOSS` / `01ed4188c8804eedab36e7658de62a57` Passed：固定前缀先掉血再结束首回合，所选路线该回合标注与模拟累计掉血一致；实例已清理。
- 永世沙漏报告 `dfcce7302842419987d9039968a76412` / `607138e217574725855adbb6d83b925e`：`start` 严格恢复与 SearchOnly 通过，120 秒上限内实际搜索约 83 秒，所选路线首回合标注 14 HP。原报告是 0→25 HP 的复核暂停；本次路线与搜索上限不同，只验证缺失标注已出现，不称为原路线逐位复现。实例已清理。
- 无厌沙虫报告 `fbb5f72709ba412890063c7df907785d` / `d83be75b0e104d00882b08903058bd9b`：`combat_start` 严格恢复与 15 秒 SearchOnly 通过；准备选牌场景 `NOVELTY-TURN-SETUP-CHOICE-0400` / `2c288a63cd634a6693f50850b456f998` Passed。两实例已清理。
- 蜂群术士报告 `932f3cf624854741a4e5dddd3cb7cdc8` / `4967277af82843dfb28423777c048ec0`：`start` 严格恢复与 15 秒 SearchOnly 通过；此前开局选牌前缀在候选审计中失败。实例已清理。
- `LAMP-INKY-SHIV` / `0acb7fded0ba419eab9b2f64a63eec5f` Passed：墨刃生成的小刀触发不安油灯，逐动作完整续用状态与实机一致；实例已清理。
- 未运行 Linux 门禁；尚未验证所有上报的结束回合复核、选牌及计算失败根因。

## 0.47.0 前两回合实验开关（2026-09-27）

- `NOVELTY-PORTFOLIO-SETTINGS` / `9b087e2edcc54785aeb3922bd80dbb5d` Passed：新安装默认关闭，设置页第三个实验开关、持久化和请求冻结通过；开启时深度 2、整次探索期限 2400000 ms，关闭时深度 0。
- `UI-LOCALIZATION` / `b1fb101ce25c4b4aaa764552645cbd7c` 在 `BYGONE_EFFIGY_ELITE` 的怪物生成阶段报 `No valid next state found`，早于本次新增文案检查；改用既有有效遭遇 `PHROG_PARASITE_ELITE` 后，`3b8cecc6f8f24a1e9f04c88c29ce373b` Passed，eng/zhs/zht 共 451 条文本目录与设置控件检查通过。两次测试实例均由启动器删除；未做可见 UI 排版验收。
- 第 89 包 `10d01cc2d1f7445c8ff72e76e783aeb0`：`combat_start` 严格恢复，开启两回合追加搜索各保留 24 个状态，完整胜利仍为 7 HP / 0 瓶，与此前默认结果相同；总墙钟 413 秒，请求总展开 1110752。证据 `.local/strategy-sessions/worldline-20260925/requests/20260927T0512186253886-run`。
- 第 100 包 `88619c63f91b48998737d7a9d623e2df`：用户指令停止时仍在运行，本地人工终止游戏；工具记 `process_crash` 只是缺少结果文件，不能计为自然崩溃或质量结果。证据 `.local/strategy-sessions/worldline-20260925/requests/20260927T0520096682165-run`。

## 离线前两回合追加搜索（2026-09-27）

- 骑士精英 `fd3b6e70cb9340a3bad6aae94d5b6b0b`：同一 `combat_start`，普通搜索 12 HP / 2 瓶；`--early-turns 2 --deadline-seconds 300` 完整胜利 1 HP / 2 瓶，实际展开 184237 个追加节点、续搜 5 条，仍按药水成本比人工 9 HP / 1 瓶落后 1 HP。证据 `.local/strategy-sessions/worldline-20260925/requests/20260927T0441084227454-run`。
- 夜魇包 `a422c1c56022446c85f6ce00962019c4`：300000 追加节点的诊断搜索完成，但只找到未结束战斗的路线，未计入优化；之后另一请求在原有 `NO_VICTORY_ESCALATION` 阶段发生游戏原生访问冲突。首份转储异常为 `0xC0000005`、执行地址 0；原生间接调用的具体对象缺少符号，不能认定新追加搜索是唯一原因。异常结果选择路径中的候选快照释放缺口已修正，原生崩溃仍需单独定位。证据 `.local/strategy-sessions/worldline-20260925/requests/20260927T0430183686575-run`、`20260927T0437190817753-run`。
