# CombatSolver 架构与职责地图

本文只维护当前所有权、依赖边界和源码入口。实现过程、固定根结果和性能数字见 [历史资料](archive/README.md)。持续工作规则见 [AGENTS.md](../AGENTS.md)。

## 1. 运行链

主线程捕获稳定战斗根 → 冻结请求政策 → 后台分支搜索 → 主线程接收结果 → 原版公开入口部署当前回合 → 续用戳核对或重新捕获。

后台只读取根与分支状态；真实战斗对象只用作稳定身份或只读模型元数据。未知语义形成明确边界或失败。

## 2. Runtime

| 入口 | 所有权 |
| --- | --- |
| `src/Runtime/Entry.cs` | 初始化、战斗生命周期接线 |
| `SolverController.cs` | 主线程请求、结果接收、续用、部署和自动执行 |
| `SolverControllerSessions.cs` | 战斗、搜索、部署会话生命周期 |
| `CombatRootSnapshot.cs` | 在主线程捕获并核对稳定根 |
| `ContinuationStamp.cs` | live / predicted 跨回合一致性和字段差异 |
| `SearchGcPolicy.cs` | 进程 GC、NoGC 和跨战斗回收协调 |
| `SearchMemoryPressureSignal.cs` | 注入 Search 的分配边界与回收续搜信号 |
| `NativeChoiceRuntime.cs` | 原生选牌观察与逐实例计划匹配 |
| `PlayerTurnSetupPatches.cs` | 原生回合准备、选择和部署交接 |

政策由主线程从设置冻结到 `SearchPolicySnapshot`。后台通过请求参数、诊断 sink 与压力信号消费外部能力；算法不读取设置单例或操作 GC。Power 显示变量在主线程根捕获时物化；worker 只消费已物化值。克隆并发边界由 `BaseLibCloneConcurrencyPatch` 与原生克隆隔离规则维护。

常规 GC 搜索的系统余量、分配限额和 Gen2 回收由 Runtime 作用域持有，Search 只在排空后的提交边界消费压力信号。常规检查点刷新请求限额；不可分割提交通过显式退出将分配交给 CLR，下一请求重新建立限额。Runtime 在限额和诊断成功后登记作用域，入口失败释放信号并传播原异常；退出时按作用域清理计数和信号。

## 3. Search

| 入口 | 所有权 |
| --- | --- |
| `CombatSearchCoordinator` 与各分片 | 主 Pass、药水审计、组合成员及后处理编排 |
| `SearchRequestPipeline` | 请求级阶段顺序与停止条件 |
| `SearchPassContext` / `SearchPassResult` | 单轮冻结输入及返回合同 |
| `SearchBudgetLedger` / `SearchRequestWorkTotals` | 请求时间、额度和唯一工作量累计 |
| `FrontierContinuationScheduler` | 固定前缀成员派发与用途归因 |
| `CombatBeamSolver.cs` / `.Models.cs` | 不可变根配置、策略接线、节点和运行上下文 |
| `.Phases.cs` / `.Expansion.cs` | Solve 阶段和动作回放入口 |
| `.ParallelExpansion.cs` / `.AdmittedExpansion.cs` | 固定 worker lane、已准入作业及确定性提交 |
| `.PrimaryChoiceReplay.cs` | 原预算必经的首层选择回放与暂存 |
| `.BeamRetentionPolicy.cs` | 去重、Beam、多样性、选择保路、药水配额与 Pareto |
| `.FinalPlanOrdering.cs` / `RouteQualityPolicy` | 完整路线质量与各既有投影顺序 |
| `.StateEvaluation.cs` / `.Terminal.cs` | 评分特征、终局回放和回合结果 |
| `SimulatedCombatState*.cs` | 分支战斗状态与动作语义 |

成员共享原请求账本；预算准入、候选合法性和取优由所属策略决定。分支状态不承担 Beam 政策；中途保路和终局比较保持明确入口。药水反事实与强制用药的硬准入先于质量比较。固定前缀构造实际父链，EndTurn 从模拟前后状态生成 `TurnOutcome`。

预览路线的采用回放持有请求级取消令牌，单轮搜索结束与用户取消请求分别判断。强制结束回合的卡牌动作只消费自身选择，后续回合选择由AdvanceRound持有。固定前缀在仍进行的稳定父状态继续，药水统一沿正式候选政策准入。

`StrategicHpRecoveryBound` 在根快照中冻结回复环境资格，由 `.Remaining` 维护已审计来源闭包和分支剩余上界，未知来源保持无限上界。`.KnownSources` 单独提供当前原版已知来源策略，忽略尚未生成的随机药水回复；该策略资格不构成严格闭包证书。已有完整合规胜利可以沿既有 Retention 和组合成员入口提供界；主搜索之前的计划安排仍只对严格认证根开放，其余根保留原阶段顺序。

## 4. 模拟与 Prediction

| 位置 | 职责 |
| --- | --- |
| `src/Engine/InCombat/Simulation/` | 通用命令时序、历史、RNG、牌堆、伤害和 Fork |
| `src/Engine/InCombat/Mirrors/` | 原版 Hook / Model 方法镜像 |
| `MethodMirrorRegistryDescriptor` | 向 CoverageCatalog 暴露 registry 支持元数据 |
| `src/Prediction/` | 怪物 AI、隐藏状态、生命周期、死亡/召唤、选择与 subscriber 捕获 |

每项战斗语义只有一个权威结算实现。可变值由根快照、影子状态、克隆 Model 或 `PredictionStateStore` 持有。一次 Fork 共用 `PredictionForkContext`，引用随同一上下文重映射；COW 取得可写所有者后再写。

Fork 发生在动作、选牌、Power、死亡和出牌事务允许复制的稳定边界。玩家记录的卡牌/药水嵌套效果作用域由引擎持有；选牌检查点保存不可变身份序列，恢复建立分支独占列表。最外层效果结束后再检查手空。稳定根及跨回合状态的该作用域为空，普通 Fork 要求为空。

未知 gameplay subscriber 显式拒绝；已支持来源在主线程捕获，并在分支中消费隔离状态。登记合同与封闭入口见 [第三方适配手册](third-party/README.md)。

金币命令由 `GoldGainSupport` 串联标准镜像：修改使用跑局前缀和战斗监听表，获得后的回调使用原生 null-child 跑局作用域。`SimulatedCombatState.GoldHooks` 在主线程冻结活动成员与已克隆全局来源，Fork 只读共享；遗物、药水、金币和 HP 仍从所属分支读取。未知金币 override 显式拒绝。监听参与位图为三个金币方法共用一位，只形成保守成员超集，精确方法仍由 registry 区分；不溢出或复用其他 Hook 位。`CombatPredictionSimulator.GainMaxHp` 单独实现实际封顶增量与后续 Heal，Feed、FruitJuice 和 DragonFruit 共用这一权威入口。

## 5. UI

`SolverOverlaySnapshot.Capture` 是结果到展示的唯一投影边界，可读取 `SolverResult` 与显示元数据。`SolverOverlay`、`SolverRouteRow`、`SolverActionPill` 只渲染只读 snapshot。

路线身份、选择、目标与显示值由主线程投影；UI 不持有搜索分支或从 `ModelDb` 重新解释路线。设置输入由相应面板持有，Controller 负责政策冻结和续用失效。中英文本同时维护。

## 6. Testing

源码按 [Testing 入口](../src/Testing/README.md) 收纳：Host 持有编排与协议，Support 持有共享差分辅助，Replay 持有恢复；Contracts 按 Combat/Search/Runtime/UI/ThirdParty 分组，Regressions 保存社区和报告回归。各目录沿用原程序集与 partial 类型。

| 入口 | 所有权 |
| --- | --- |
| `UnattendedTestRunner` | 请求级编排与共享 fixture helper |
| `ProtocolHost` | 请求循环与每请求开关 |
| `ScenarioBuilder` | 建局与状态注入 |
| `Executor` | 差分、搜索、部署执行及临时设置 |
| `Assertions` | 执行前后断言 |
| `Writer` | 结果协议和原子写入 |

原生与模拟核对完整状态、顺序、引用、RNG 和续用合同。离线宿主只产搜索指标；headless 不证明真实可见布局或帧时间。入口见 [无人测试](HEADLESS_TESTING.md)、[离线宿主](OFFLINE_SEARCH_HARNESS.md)、[测试证据](TEST_MATRIX.md)。

旧批次的硬编码路径调查退出当前树，仍被原生回归、生成上下文与搜索合同调用的快照辅助保留在 Support。一次性验证代码由 .local/tool-tasks 持有并在任务结束清理，普通构建显式排除 .local 源码。

## 7. 工具与维护

工具按 [职责目录](../tools/README.md) 管理。testing 持有无人实例与生产回归检查，replay 持有包恢复与会话，search/performance 持有离线指标与采样，inspection 持有目录和结构检查；build/release/community 分别维护构建、发布与社区流程。Windows MemoryCleaner 由 tools/runtime 提供，在线监控后台由独立私有仓库 combatsolver-presence-service 持有，本仓库仅维护模组端上报和提醒。

tools/Directory.Build.props 统一工具项目的仓库根与构建产物路径，生成内容放在 .local/。`tools/inspection/verify-refactor-boundaries.ps1` 和 `.sh` 维护同一职责边界。CoverageCatalog 从公开描述与结构化证据生成覆盖报告，报告的生成版本与测试来源分别说明。

[coverage](../coverage/README.md) 持有手工分类、证据、复用输入、固定语料和归档摘要。CoverageCatalog 读取 catalog/classifications.json 与 evidence/test-evidence.json，替换 catalog/generated 的现行快照；候选 fixture 写入 .local/coverage-fixtures。证据按完整仓库相对路径读取，缺失材料显式失败。脚本与完整运行产物分别属于 tools 和 .local。

修改职责时在同一提交替换本文对应章节，并同步相关 skill 与结构门禁。开发进度写入当前开发记录，测试细节写入证据，历史报告冻结归档。
