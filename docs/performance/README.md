# 性能研究与复现

- [洗牌、战略选择与不变特征复用](simulation-expanded-chain-20260911.md)：扩展原生两回合缩减样例，含每批准入 3.31×／分配 −80.65%；回合推进仍由旧引擎负责，生产 Beam 未启用。

- [紧凑状态直接读取与复杂状态补测](simulation-read-view-20260911.md)：共用完整 Snapshot／原状态键，删除逐叶旧图物化；原生 Power／召唤／死亡净变化及交错成本。

[返回文档导航](../README.md)

每份报告只证明其中注明的版本、场景和测量条件。当前测试入口见 [测试矩阵](../TEST_MATRIX.md)，架构约束见 [架构地图](../ARCHITECTURE.md)。

## 专题

- [候选冻结恢复与剩余瓶颈评估](simulation-candidate-storage-20260911.md)：不可变值页和同根工作区恢复已实现；交错存储对照、长历史/密集退化、当前 DOP 曲线与原生完整对照。此前方案统一见[决策账本](simulation-strategy-ledger-20260911.md)。

- [紧凑原型下一瓶颈](simulation-evaluation-bottleneck-20260911.md)：校正测试遗漏的正式模拟隔离；完整估值约占 51%–53% CPU，旧图转换约占 63%–66% 分配，给出完整读取边界与冻结候选的迁移顺序。

- [可恢复紧凑执行原型 R0–R2](simulation-kernel-prototype-20260910.md)：34 个真实选择叶子的完整状态/估值对照与原生嵌套执行通过；历史性能样本遗漏正式隔离作用域，最新校正见下一瓶颈报告，尚未进入生产迁移。

- [新一轮大幅重构调研](simulation-redesign-research-20260910.md)：显式续执行、紧凑权威状态与撤销日志的组合原型；复算转移成本，核对六次药水搜索与状态键排序依赖，区分已测证据、旧实验和未验证目标。
- [完整搜索性能重构最终结果](simulation-refactor-result-20260910.md)：保留检查点、完整 headless A/B 与原生部署、各阶段取舍及未达到的目标。
- [P2 / P4 剩余候选进入决策](simulation-refactor-p2-p4-decision-20260910.md)：等待选择快照与 Power 复制 CPU、所有权合同及不进入理由。
- [P5 选择前缀测量与回合检查点](simulation-refactor-p5-20260910.md)：线程 CPU 诊断、阶段提取与检查点验证；[结构化数据](simulation-refactor-p5-20260910.json)。
- [P3：归一化失效标记原型已撤回](simulation-refactor-p3-20260910.md)：相关 Fork/原版差分通过，但未建立稳定提速且分配略增；保留桌面启动干扰、冷预热超时和未完成交错序列。

- [P1：复用率、归一化与 Power 类型成本](simulation-refactor-p1-20260910.md)：两场完整诊断不支持投影洗牌缓存；定位保留首次入场语义的归一化候选，记录实际 Power 克隆类型和变量读写。

- [完整搜索的模拟性能重构计划](simulation-refactor-plan-20260910.md)：分阶段实施与取舍；用户追加授权大幅重构，后续重点为可恢复执行、紧凑分支状态及快照成本，10× 为挑战方向而非已得结果。

- [极高配置完整搜索的模拟热点](simulation-profile-20260910.md)：基于 0.34.6 的两场正常整场路线搜索，分开采集 CPU 与分配；模拟加 Fork 占搜索 CPU 约 59%–62%，细分回合生命周期、快照、事件回调及 Power 克隆，未改算法。

- [快照与重放热点复查及 PR 收口](snapshot-replay-followup-20260909.md)：排序原型均值变化小于基线漂移，已撤回；合并最新上游并核对目标 57 条、哨兵 9 条完整动作。

- [perf-2 选择性合入与后续热点试验](perf2-integration-20260909.md)：适配固定lane的只读保路作业，当前基线八次交错正常搜索均值−1.43%、Short−2.98%，完整保留GC波动、质量合同与撤回试验。

- [回合结束探针与元数据热路径](standpat-and-metadata-20260909.md)：原保路探针共用固定 lane，规范药水查找与只读监听类型布局复用；记录 30% 耗时目标的独立交错对照及验证边界。

- [已准入父节点内的动作与选择作业](admitted-expansion-jobs-20260908.md)：共用固定 lane，原预算保证首层回放准入并按原序续接；交错正常搜索样本均值减少10.93%，分配增加0.90%，包含漂移和所有权验证边界。

- [CPU 微架构、并行利用率与优化复盘](cpu-microarchitecture-20260908.md)：DOP 扩展曲线、PMU/分支采样和 JIT 汇编；定位平均用核不足，纠正计时口径，保留逐位相同的指纹寄存器计算。

- [较大范围的后端性能实验](bold-backend-experiments-20260908.md)：延迟快照估值、RNG 值槽、两种紧凑 COW 字典和牌堆缓存均未显示明确提速，全部撤回。

- [按真实CPU热点优化路由聚合](backend-hotspot-optimization-20260908.md)：六张重复查表合为一张，短搜/正常配置单样本耗时减少2.96%/1.26%；完整指标与路线一致，其他原型撤回。

- [后端真实CPU热点与局部修复](backend-cpu-hotspots-20260908.md)：用Linux perf纠正线程采样口径，互斥区分回放/快照/Fork，修复SwordSage根基线；尚无明显新提速。

- [现有战斗后端架构审查](backend-architecture-audit-20260908.md)：临时计数确认1.69亿Hook位置检查、4380万三段归一化卡访问，给出沿用现有引擎的局部优化顺序及一处根所有权问题；未实现新后端。

- [极高配置状态与缓存实验](veryhigh-state-experiments-20260908.md)：四项原型均因收益不足撤回，回放重复率约2.52%，本轮未实现大幅加速。

- [极高配置有界父节点队列](veryhigh-parent-queue-20260908.md)：同工作量单组正式样本耗时减少12.58%，峰值RSS增加9.49%；保留按序提交，未达到再翻倍。

- [极高配置路由上下文去重优化](veryhigh-routing-order-20260908.md)：保持原顺序移除平方级扫描，高压力单样本耗时减少12.49%，分配减少0.52%，整场哨兵质量不变。

- [极高配置的其他战斗压力筛查](veryhigh-pressure-survey-20260908.md)：10组输入、两项正常配置复测，定位药水高分支/GC与超大牌堆压力。

- [极高配置的空回调与并行度优化](veryhigh-hook-dispatch-20260908.md)：第二轮 headless 固定工作量约 2.18 倍、RSS 约 10–12% 成本及验证限制。

- [极高配置下的等质量热路径优化](veryhigh-quality-preserving-20260908.md)：当前上游 A/B、原版费用对照与可见 Steam 验证。

- [Ritsu 目标类型查询缓存](metadata-target-type-cache-20260907.md)：PR #49 的合同、复现条件与性能证据范围。
- [Issue #36 研究与首轮基线](gc-issue36-research.md)。
- [Issue #36 固定工作量复现](gc-issue36-reproduce.md)。
- [Issue #36 静态审计](gc-issue36-code-audit.md)。
- [Issue #36 候选实现与验证](gc-issue36-implementation.md)。
- [Issue #36 第二轮实验](gc-issue36-round2.md)。
- 结构化结果：[首轮](gc-issue36-results.json)、[第二轮](gc-issue36-round2-results.json)。

## 历史资料

- [性能与掉帧复盘](PERFORMANCE_AND_STUTTER_DIAGNOSIS.md)：早期版本结论与演进记录。
- [Fork 性能结果](RF_FORK_PERFORMANCE_RESULTS.md)：历史固定场景的性能与正确性数据。
- [旧性能样例](PERFORMANCE_FIXTURES.md)：保留复现资料；后续策略批次使用仓库的 strategy-replay-iteration skill，不继续维护此旧样例清单。
