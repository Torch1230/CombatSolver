# 策略重构待测清单（2026-09-27）

> 本文件保留 P2 抽取时的待测快照，文中的“尚未部署”和 P3–P6 待验项均为当时状态。当前收口结果见[实施总结](strategy-search-refactor-summary-20260928.md)及[测试矩阵](../../TEST_MATRIX.md)。

背景：P2 外层补搜抽取时用户正在运行游戏，因此当时只执行 Release 编译与 Windows 结构门禁。游戏进程退出后，P2 收口时已对固定语料运行一次对照；后续阶段按用户要求保持少量、直接的验证。

分支：`refactor/strategy-search-p0-20260927`。

## 本轮已改（P2 外层补搜 Pass 抽取）

把外层 `Solve` 的补搜块抽到 `CombatSearchCoordinator.PostSearch.cs`，并把 `RunEarlyTurnExploration` 改为接收 `SearchPassContext`：

- `RunEarlyPotionPairRescue`（已有，本轮确认仍在 PostSearch）
- `RunForcedPotionOpeningRescue`
- `RunTurnBoundaryRescue`
- `RunZeroCostOpeningRescue`
- `RunMidCombatRefinement`
- `RunTurnEndChoicePosterior`
- `RunEarlierCopyDelayedDamage`
- `RunEarlyTurnExploration` 改为 `(SearchPassContext, SolverResult)` 形式

抽取方式：纯移动，方法体、分支条件、派发顺序、诊断标签与预算读取时点保持不变；仅把 `enrichedProgressCallback` 改名为局部 `progressCallback`，并把 `RunEarlyTurnExploration` 的散参数收敛为 `context`。

## 本轮已取得的直接证据

- `dotnet build CombatSolver.csproj -c Release`：0 警告 0 错误。
- `pwsh -NoProfile -File tools/verify-refactor-boundaries.ps1`：`REFACTOR_BOUNDARIES_OK search_files=219`。
- `CombatSearchCoordinator.cs`：约 3,032 → 2,596 行；`CombatSearchCoordinator.PostSearch.cs` 709 行。

这三项只证明“能编译、结构门禁未破”，**不是行为等价证据**。

## P2 收口证据

`coverage/strategy-refactor-p2/corpus.json` 中的 #24、#37、#81、#89 和两个生成场景均可比较；相对于 `baseline-0471`，动作、续用、结果、工作量和剪枝计数逐位相同。对照文件为 `.local/strategy-refactor-p2/compare-p2-20260928/comparison.md`，无头实例已清理。#79、#85 的限时基线继续不参与逐位门槛。

本地 Mod 尚未部署；在最终源码完成后覆盖已确认的五个 Mod 文件。Linux 门禁按用户要求不运行。

## 后续阶段待测（占位）

- **P3 续搜框架**：六根固定语料已对 P2 结果逐位相同；协调器主文件约 1,000 行。未在该语料触发的个别模式只保留静态和已取得的 #17 双药定向证据，不宣称普遍运行等价。
- **P4 登记表**：迁移对象行为等价；新增一个测试登记项不修改搜索主流程即可产生行为。
- **P5 展开统一**：DOP1 与 DOP8 路线一致，选择预算、512 回放、原序提交不变。
- **P6 计划层**：同根同预算下 `#100`、`#101`（不足两包时加 `#84`）至少两包优于当前求解器，并保留已达标哨兵；不作预算扩张凑结果。

## 记录规则

每项实测通过后才写入“实测证据 + 命令”；不把构建或静态门禁当行为通过；无法执行时明确写未验证。纯重构出现无法解释的差异时停在该边界，不带入下一阶段。
