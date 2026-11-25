# CombatSolver 架构与职责地图

本文只维护当前所有权、依赖边界和源码入口。实现过程、固定根结果和历史性能数字见 [历史资料](archive/README.md)，协作与工作原则见 [AGENTS.md](../AGENTS.md)。

## 1. 运行链

主线程捕获稳定战斗根（`CombatRootSnapshot` + `HotRootBundle`）→ 冻结请求政策（`SearchPolicySnapshot`）→ 后台 `HotSearchCoordinator` 在纯值 `CombatHotState` 上并行搜索 → `HotPlanMaterializer` 物化 `CombatPlan` → 主线程 `SolverController` 接收结果并通过原版公开入口部署当前回合 → `HotContinuationStamp` / `ContinuationStamp` 核对跨回合一致性并在失配时重新捕获。

后台只读取纯值内核状态与冻结政策，绝不接触 live 战斗对象、设置单例或 Godot API；未知语义在根捕获或模拟边界显式失败。

## 2. Runtime

| 入口 | 所有权 |
| --- | --- |
| `src/Runtime/Entry.cs` | 初始化、战斗生命周期接线与全局事件入口 |
| `src/Runtime/SolverController.cs` | 主线程请求调度、结果接收、续用判断、部署与自动执行 |
| `src/Runtime/SolverControllerSessions.cs` | 战斗、搜索、部署会话代次与取消令牌生命周期 |
| `src/Runtime/CombatRootSnapshot.cs` | 主线程稳定根捕获、`HotRootBundle` 打包与跨回合快照校验 |
| `src/Runtime/ContinuationStamp.cs` | live / predicted 跨回合连续性与字段差分对账 |
| `src/Runtime/CombatBugReportUploadPolicy.cs` / `CombatBugReportDescription.cs` | 战斗问题上传资格、错误分类与搜索期玩家输入记录 |
| `src/Runtime/NativeChoiceRuntime.cs` | 原生选牌观察与逐实例计划匹配 |
| `src/Runtime/PlayerTurnSetupPatches.cs` | 原生回合准备、开局选择与部署交接 |
| `src/Runtime/SolverSettings.cs` | 玩家设置持久化与配置冻结输入 |
| `src/Api/PreCombatForecastWorker.cs` / `.Idle.cs` | 预战独立子进程会话、请求编排与空闲计时器所有权 |

政策由主线程从设置冻结到 `SearchPolicySnapshot` 与 `SolverSearchProfile`，再经 `SearchDiagnosticsSink` 传递诊断。HotKernel v2 架构下搜索热路径保持纯值低分配，Runtime 不再持有 NoGC 区域、ServerGC 启动配置、GC 恢复状态机或 Harmony 克隆隔离补丁。

## 3. HotKernel（纯值战斗内核）

| 目录 | 职责 |
| --- | --- |
| `src/HotKernel/State/` | 非托管值类型核心状态：`CombatHotState`、`HotColdSidecar`、`HotExecScratch`、`HotStructs`、`HotLimits`、`HotKeywords`、`HotHash`、`HotStatePacker` 与 `PackedStateArena` |
| `src/HotKernel/Rules/` | 静态规则与数据目录：`HotRules`、`HotDefinitions`、`HotEnums`、`HotHookMask`、`HotCombatRulesSnapshot` 与 `HotCatalog.{Cards,Monsters,Potions,Powers,Relics}.cs` |
| `src/HotKernel/Import/` | 主线程根导入器：`HotRootBuilder`、`HotImportSupport`、`HotContinuationStamp`、`VanillaRootGate`（**唯一允许读取 `MegaCrit.Sts2.Core.Models` 的内核子层**） |
| `src/HotKernel/Sim/` | 纯值模拟器：`HotCombatEngine*.cs`（出牌、药水、伤害、格挡、Power、球、回合推进、选牌、字节码、怪物 AI）、`HotCostModifiers`、`HotPiles`、`HotRng`、`HotHookArgs` 及 `Content/{HotMonsters,HotRelics,HotPotions,HotRegentNecro,HotFoolTarot}.cs` |

硬边界：
- `src/HotKernel/` 禁止引用 `SolverSettings.Current`、`Entry.Logger`、`SolverController`、`SolverOverlay` 与 `UnattendedTestRunner`。
- `src/HotKernel/State/` 与 `src/HotKernel/Sim/` 禁止引用 `MegaCrit.Sts2.Core.Models`；所有原版对象元数据在主线程由 `HotKernel/Import/` 转为静态 ID 与紧凑值数组。
- 分支拷贝直接值复制或使用 `HotStatePacker` 紧凑快照，不共享可变托管引用。

## 4. HotSearch 与 Search 外层合同

| 入口 | 所有权 |
| --- | --- |
| `src/HotSearch/HotSearchCoordinator.cs` | 协调主搜、药水梯度与多配置入口（`Solve`、`SolveSingleBeam`、`CaptureQuality`） |
| `src/HotSearch/HotParallelExpander.cs` | 确定性多线程并行展开与结果归并（`DOP=1` 与 `DOP=N` 严格同序同结果） |
| `src/HotSearch/HotBeamRetention.cs` | 束宽保留、转置去重、多样性与跨回合前沿管理 |
| `src/HotSearch/HotEvaluator.cs` / `HotRouteQuality.cs` | 分支状态评分与路线质量比较（含药水机会成本与成长/遗物政策） |
| `src/HotSearch/HotRolloutEvaluator.cs` / `HotEndTurnRiskEvaluator.cs` | 回合边界 Rollout 前瞻与结束回合即时风险评估 |
| `src/HotSearch/HotPotionPolicy.cs` | 药水机会成本、血瓶 MaxHp 折算、省血阈值与掉药收益抵扣 |
| `src/HotSearch/HotPlanMaterializer.cs` | 将最优动作轨迹物化为对外 `CombatPlan` 与 `SolverResult` |
| `src/HotSearch/HotNodeArena.cs` / `HotTranspositionTable.cs` | 池化节点/状态仓库与开放定址转置表 |
| `src/Search/` | 外层轻量 DTO 与策略合同：`CombatPlan`、`SearchPolicySnapshot`、`SolverSearchProfile`、`SearchDiagnosticsSink`、`SearchTransitionException`、`StateFingerprint`、`SolverWeights`、药水/成长/遗物/首领/失窃策略 |
| `src/Prediction/` | 根捕获共享辅助：`CombatRulesSnapshot`、`IntentForecaster`、`RelicCounterCatalog`、`IncompatibleGameplayModException` |
| `src/Engine/Common/PredictionUnsupportedException.cs` | 未支持战斗语义显式异常合同 |

硬边界：
- `src/HotSearch/` 与 `src/Search/` 禁止引用 `SolverSettings.Current`、`Entry.Logger`、`SolverController`、`SolverOverlay` 与 `UnattendedTestRunner`。
- `src/HotSearch/` 禁止引用 `MegaCrit.Sts2.Core.Models`（外层物化仅通过 `HotRootBundle` 已捕获元数据）。
- 不允许在 `HotKernel`/`HotSearch` 中使用宽泛 `catch (Exception)` 吞错或用默认值掩盖未知战斗语义。

## 5. UI

`SolverOverlaySnapshot.Capture` 是结果到展示的唯一投影边界，可读取 `SolverResult` 与显示元数据。`SolverOverlay`、`SolverRouteRow`、`SolverActionPill` 只渲染只读 `SolverOverlaySnapshot`，不得直接接触搜索内核、分支状态或从 `ModelDb` 重新解释路线。设置面板持有输入状态，Controller 负责政策冻结和续用失效；UI 文案同时维护中英双语。

## 6. Testing

源码按 [Testing 入口](../src/Testing/README.md) 组织，沿用主程序集与 `UnattendedTestRunner` partial 类型：

| 目录 / 入口 | 所有权 |
| --- | --- |
| `src/Testing/Host/` | `UnattendedTestRunner` 请求编排、`ProtocolHost` 循环、`ScenarioBuilder` 建局、`Executor` 搜索/部署执行、`Assertions` 断言、`Writer` 结果写入、`Offline` 宿主入口 |
| `src/Testing/Support/` | 共享原生 fixture 与出牌辅助（`UnattendedTestRunner.Fixtures.cs`、`NativeCardPlay.cs`、`NativeTestCardSelectors.cs`） |
| `src/Testing/Replay/` | 检查点导入、原生事件回放、录制状态恢复与回放断言 |
| `src/Testing/Contracts/Combat/` | 生成场景检查与日志上报合同 |
| `src/Testing/Contracts/Runtime/` | `CombatRootSnapshot` + `HotRootBundle` 打包/解包与 `HotContinuationStamp` 往返合同、原生选牌面、进程诊断与战绩 worker 失败合同 |
| `src/Testing/Contracts/UI/` | 路线行复用与召唤物显示名合同 |

无人实机测试见 [无人测试](HEADLESS_TESTING.md)，纯 .NET 离线工具见 [离线宿主](OFFLINE_SEARCH_HARNESS.md)。

## 7. 工具与维护

工具按 [职责目录](../tools/README.md) 管理：
- `tools/inspection/verify-refactor-boundaries.ps1` 与 `.sh`：维护两平台等价架构边界检查。
- `tools/search/OfflineSearchHarness/`：直接接入 `HotSearchCoordinator` 与 `HotRootBuilder`，提供 layout/coverage/parity/policy 快速自检、覆盖引导模糊测试、部署压测、成对 A/B 评测与玩家包锁定回放。
- `tools/testing/run-independent-checks.py`：运行无需游戏进程的独立单元测试。
- `tools/inspection/verify-tools.py`、`verify-documentation.py`、`verify-coverage.py`：维护工具、文档与语料目录一致性。
- `tools/Directory.Build.props`：统一将构建产物写入 `.local/tool-build/`。

调整层间所有权时，同步更新本文、相关 Skill 与双平台边界检查脚本。
