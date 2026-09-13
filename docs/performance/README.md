# 性能研究与复现

[返回文档导航](../README.md)

每份报告只证明其中注明的版本、场景和测量条件。当前测试入口见 [测试矩阵](../TEST_MATRIX.md)，架构约束见 [架构地图](../ARCHITECTURE.md)。

## 当前工作重点

`perf/surgical-fixes-20260912` 基于上游 `eff8cf4`；旧 `perf/simulation-profile-20260910` 已暂停开发。只接入独立小修复，不继续 Compact／战斗执行流程移植。

- [精简移植、验证与性能研究](surgical-fixes-20260912.md)。

## 专题

- [五项后续候选：工作量、原型与取舍](five-candidates-20260913.md)：Windows先行部署、三槽计数与窄生成入口复用，以及top-k/JIT/SIMD原型的取舍。

- [三层卡顿与重复回收实录](player-lag-diagnosis-20260911.md)：秒级长帧与 GC 事件对账，大碎片、跨跑局对象存活、层间低收益重复回收及证据边界。

- [玩家长期卡顿实录诊断](player-lag-diagnosis-20260910.md)：SpeedX 对缺失 Rewind 的每帧探测造成主要空闲分配；另列 GC 退化、旧战斗存活及诊断转储挂起的证据边界。

- [跨跑局卡顿的进程全程录制](long-session-recording.md)：全程时间线、线程/GC 轨迹、Mod 补丁清单和按需内存现场。

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
