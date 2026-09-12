# P2 / P4：剩余候选的进入决策（2026-09-10）

[执行计划](simulation-refactor-plan-20260910.md) · [性能目录](README.md) · [结构化数据](simulation-refactor-p2-p4-decision-20260910.json)

## 结论

本轮不进入“等待选牌结果跳过完整估值”及首批紧凑 Power 状态迁移。决定基于保留 P5 后的实际成本及必须保留的读写/身份行为，并非因为用户没有授权大幅重构。前者在大场景仅占约 1.05% 请求 CPU；Plating、Strength、WitheringPresence 三类的复制合计约 2.70%。即使假设完全免费，所得空间也不足以支撑期望的大幅加速，实际替代状态和读写屏障仍有成本。

这是有证据的**不进入**结论，没有声称实现并验证了 Power COW，也没有证明任何未来紧凑表示都无效。P2 最初的投影洗牌缓存已在 [P1](simulation-refactor-p1-20260910.md)因低复用率否决；P3 的原型已撤回；[P5 检查点](simulation-refactor-p5-20260910.md)有重复配对收益并已保留。10× 目标未实现。

## 直接测量

基于 `1787f9d`，临时插桩 [Snapshot](../../src/Search/CombatBeamSolver.StateEvaluation.cs)、[ForkPower](../../src/Search/SimulatedCombatState.Fork.cs) 与 [Power 指纹](../../src/Search/SimulatedCombatState.cs)。每线程/操作/边界或类型每 64 次采样 `CLOCK_THREAD_CPUTIME_ID`，准确记录调用数，并按组内 `calls / samples` 加权 CPU 与分配样本。

| 指标 | 机甲 | 亡灵 |
| --- | ---: | ---: |
| runId | `9305c5edd5844f7188aa8f584a1bbbd4` | `c64a0b392e57424e96a06b1dc6492db3` |
| 完整请求进程 CPU | 44.64 秒 | 441.39 秒 |
| Snapshot 总调用 / CPU 占比估计 | 119,409 / 28.82% | 1,693,154 / 24.28% |
| PendingChoice Snapshot 调用 | 18,639（15.61%） | 77,288（4.56%） |
| PendingChoice Snapshot CPU 占比估计 | 4.40% | 1.05% |
| PendingChoice Snapshot 分配估计 | 78.87 MB | 384.73 MB |
| 所有 ForkPower 调用 / CPU 占比估计 | 842,453 / 4.17% | 23,809,640 / 8.16% |
| 所有 ForkPower 分配估计 | 0.319 GB | 10.037 GB |
| PowerFingerprint CPU 占比估计 | 1.76% | 5.10% |
| 平均读钟调用校准 | 624 ns | 535 ns |

CPU 占比分母包含主线程、JIT、GC 和诊断；首场包含冷 JIT。短 Power scope 的计时包含读钟和 scope 开销，未机械扣除校准，因此不能把上表直接当作无诊断的纯模型克隆占比。PowerFingerprint 已包含在 Snapshot 内，二者不可相加；ForkPower 包含映射上下文设置及注册，不是单个原生 MutableClone 的纯耗时。以上均为采样估计而非精确上界或跨场景保证。

两场完整正常 VeryHigh / DOP8 / NoGC16GB；沿用原完整输入与预算，没有短搜或增量验证。诊断版 Release 0 警告/错误；两场 Passed，88 项逻辑 RESULT 字段及完整动作、回合结果、预测均与 P5 候选相同。请求退役后提取完整计数事件，退出唯一拥有的实例，恢复四个源文件并删除诊断 helper；生产源码与提交无差异。带诊断请求墙钟 7.92 / 77.84 秒仅保留在 JSON，不作为新速度 A/B。

## P2：等待选择的结果所有权

当前 [Expansion](../../src/Search/CombatBeamSolver.Expansion.cs)会为 PendingChoice 结果计算完整 Snapshot，再由选择枚举器读取模拟器、回合与边界。真正可避免的量是上表的 PendingChoice Snapshot，不能用约 102 万次“选择分支”冒充同样数量的待选择快照。

合适的重构应将完整 `SimulationSnapshot` 与明确的未解决选择结果分型，并让 [PrimaryChoiceReplay](../../src/Search/CombatBeamSolver.PrimaryChoiceReplay.cs)、[AdmittedExpansion](../../src/Search/CombatBeamSolver.AdmittedExpansion.cs)及选择枚举器转移/释放该结果；发布到 SearchNode、Beam、转置和路线的仍必须是完整快照。不能创建带假评分或假状态键的半成品 Snapshot，也不能发布以后再借用可变模拟器估值。

还要处理完整/无效/嵌套/逐实例/首回合/药水和取消路径，并核对现有估值读取是否物化 StateStore 隐藏状态或触发显式错误。仅按当前估计、假设该工作全部消失，Amdahl 换算约为机甲 1.046×、亡灵 1.011×，尚未计入新结果协议和必要检查。与本轮重点场景及大幅加速目标不匹配，停止在测量与所有权设计，不加入新的结果层。

## P4：首批类型与必须保留的合同

亡灵中首批候选的 ForkPower 成本：

| 类型 | 调用数 | CPU 占比估计 | 分配估计 |
| --- | ---: | ---: | ---: |
| PlatingPower | 1,747,189 | 1.185% | 1.237 GB |
| StrengthPower | 2,878,392 | 0.944% | 0.967 GB |
| WitheringPresencePower | 1,747,189 | 0.574% | 1.237 GB |

三类合计 CPU 约 2.70%，分配约 3.44 GB；这是整段 scope 的当前费用，不能全部当作可省下的字节或 CPU。原生/第三方克隆链、写入和索引仍需工作。即使把所有类型的 ForkPower scope 都视为免费，按本次估计也仅对应亡灵约 1.089×，远非 10×；实际短 scope 还含明显计时开销。

[现有 Power API](../../src/Search/SimulatedCombatState.cs)返回可变 Model 借用。仅给字典加共享或在 GetPower 时延迟克隆，不能满足下面的合同；指纹与监听路径又会读取每个 Model，使“任何读取即克隆”重新执行原复制量。

| 内容 | 所有权要求 |
| --- | --- |
| 类型、ID、规范定义 | 可作为冻结元数据；不等于当前实例可共享 |
| 数量、回合开始量、duration、applier/target | 由分支权威状态持有，读写入口一致 |
| DynamicVars 与隐藏计数 | 非纯显示数据，变量还绑定模型 owner；不能共享祖先可变变量集合 |
| 同类型多实例、移除与重获 | 需要稳定的逐实例身份，新获得实例不能占用旧监听实例的身份 |
| 已捕获的监听列表 | 成员与顺序冻结，但每次调用要读取当时数量；前一个监听器可能已改变后一个实例 |
| Fork 与 StateStore | 用同一 PredictionForkContext 重映射监听引用、模型键及 alias；禁止两套可独立写入的状态图 |
| 未迁移与第三方类型 | 保留 DeepCloneFields / AfterCloned 和 BaseLib 的窄克隆并发保护 |

WitheringPresence 的计数在生成牌操作前递减，DynamicVar 在该操作完成后同步；等待选择期间两者暂时不同，不能直接删除“重复”字段。现有借用次数也不等于多少个子分支真正发生了写入。以上设计明确了需要迁移的权威入口，而不是用一层 facade 包装旧状态。

首批类型无法提供接近目标的 CPU 空间；扩大到全部 Model 和全引擎数组表示已经超出这些测量所支持的候选。按计划的进入条件，本轮不实施该存储迁移，最终集成仅验证保留的回合检查点；没有把设计工作算作性能实现。
