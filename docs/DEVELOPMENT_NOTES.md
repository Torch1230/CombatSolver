# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

0.51.0 的标准模式适配、请求预算显示和路线优化见[历史卷 23](archive/development/volume-23.md)，玩家说明见[0.51.0 更新日志](releases/0.51.0-RELEASE_NOTES.md)。

0.51.1 的恐惧／极速模式适配与实例状态修复见[历史卷 24](archive/development/volume-24.md)，玩家说明见[0.51.1 更新日志](releases/0.51.1-RELEASE_NOTES.md)。

0.51.2 的属性削减、诅咒持续时间和仪式兽修复见[历史卷 25](archive/development/volume-25.md)，玩家说明见[0.51.2 更新日志](releases/0.51.2-RELEASE_NOTES.md)。

0.51.3 的同牌多减益与部署异常修复见[历史卷 26](archive/development/volume-26.md)，玩家说明见[0.51.3 更新日志](releases/0.51.3-RELEASE_NOTES.md)。

0.51.4 的战斗逻辑与选牌续接修复见[历史卷 27](archive/development/volume-27.md)，玩家说明见[0.51.4 更新日志](releases/0.51.4-RELEASE_NOTES.md)。

0.51.5 的结算顺序与运行时交接修复见[历史卷 28](archive/development/volume-28.md)，玩家说明见[0.51.5 更新日志](releases/0.51.5-RELEASE_NOTES.md)。

## 下一版本（开发中）

- #239：为地精佣兵战增加“基础金币奖励被放弃”的独立结果。结果由分支逃跑名单派生，参与状态键、续用、资源排序、早停和剪枝分桶。“保牌/保钱”保留基础奖励，“放走”保持原政策。路线摘要同步中英文奖励损失提示。
- 评审跟进：终局与保路的资源平局比较统一带上基础金币标记，保序突变战术键补入该标记。恢复判断、终局资格和资源分桶的新增标记参数改为必填。
- 最小原生场景已覆盖 3 张攻击牌击杀、无偷钱逃跑的 0.5 奖励比例和同根政策哨兵。测试入口与数字见[测试矩阵](TEST_MATRIX.md#地精佣兵的基础金币奖励239)。原始 0.111.0 玩家包尚未在 0.113.1 完整回放。

### 独立检查与 CI

- 恢复 `CardHookReceiverChecks` 与 `TurnPhaseMirrorChecks` 的 Fork 接口替身。诊断日志检查改为断言进程生命周期事件的内容、顺序和冻结前缀。
- 新增跨平台独立检查入口和 Windows/Linux CI，覆盖 25 个无需游戏安装的 .NET 检查。范围与证据见[测试入口](TEST_MATRIX.md#独立检查与-ci)。
- 独立检查入口先扫描项目目录，拒绝未分类项目、缺失登记和异常项目布局；3 个排除项分别注明依赖。CI 对同一 PR 只保留最新运行。

### 结果诊断的 Windows 进程内存读取

`SolverDiagnostics.DescribeResult` 改为一次捕获进程内存快照。Windows 直接读取当前进程的工作集与私有内存计数，避免 `Process` 内存 getter 填充进程／线程信息；其他平台保留原 getter。原有诊断字段名保持，Windows 原生查询失败时明确写 `unavailable`。改动仅在 Runtime 诊断层，不调整搜索、模拟、预算或 GC 政策。

新增验证复用 `DiagnosticLogTests`，用确定值检查原生布局与字段映射，覆盖无效句柄、失败后恢复、并发调用与可选独立基准，不以顺序内存样本的差值作为通过条件。`OfflineSearchHarness --check-result-diagnostics` 直接调用构建后的生产格式化函数，检查成功数值、失败标记及后续结果／动作日志完整性。原版 0.50.1 的既有可见采样、本轮源码验证及未验证范围分开记录在[诊断性能记录](performance/diagnostic-process-memory.md)。

- 无头启动器按 RitsuLib manifest 选择不高于游戏版本的最新兼容目标，修复 0.113.1 游戏配合 0.113.0 API 包时被路径检查拒绝的问题。Windows/Linux 同步维护，依赖模块仍由原加载器验证。
- 预测洗牌在单次排序内复用动态变量名的有序数组。0 个变量直接返回，1 个变量直接比较；费用和变量值仍逐次读取分支状态。
- 扩展 `NATIVE-CARD-ORDERING`，覆盖 0/1/2/3 个变量、后续排序、重复引用及父子分支隔离。
- 新增 8 张富足、42 张牌的固定输入。搜索分配约减少 8.1%；路线与工作量一致，原报告崩溃仍未复现。见[验证记录](performance/shuffle-variable-names-20261115.md)。

- 修复预战 worker 空闲取消源的取消与释放竞争。取走源的取消者负责释放，后台仅释放仍持有的当前源。
- 发布取消源前保存 token，避免尚未启动的计时器读取已释放的源。
- `PreCombatRequestChecks` 直接编译生产空闲生命周期源码，18 项请求与生命周期检查通过。

### 可见测试按输入模式读取快照

Windows 可见 Steam 启动器改用迁移后的机甲骑士快照路径。两端仅在默认模式读取该快照；完整请求、日志夹具和检查点回放不再依赖默认快照。

Windows 可见 `UI-PRIORITY-FEEDBACK` 请求通过。两端请求准备的 8 项检查通过，Linux 原生启动未验证。结果与复跑入口见[测试矩阵](TEST_MATRIX.md#可见测试输入选择)。
