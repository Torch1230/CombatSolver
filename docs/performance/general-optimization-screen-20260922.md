# 通用优化的 20% 门槛筛查（2026-09-22）

> 后续进展：本报告保留首次筛查时的事实。之后已执行全部17根调查，并完成[搜索进度引用释放](progress-retirement-20260922.md)；20%结论限于新报告定义的结束后托管保留量，不追溯改写这里被否决候选的收益。

[返回性能目录](README.md) · [结构化证据](general-optimization-screen-20260922.json) · [诊断探针补丁](general-optimization-screen-20260922-probe.patch)

**本轮未达到目标，没有保留新的生产改动。** 用户接受速度、内存占用任一项显著改善，或有充分反例检查的显著决策改善；同时要求通用实现、控制复杂度和扩大测试面。上一轮怪物指纹的小收益不能算本轮 20% 收益。本报告记录筛查失败，不能用作完成目标的证明。

## 基线与实际执行范围

基线为已拉取上游 `6922828d` 后的 `510dd703`，工作分支 `perf/bounded-hotpath-20260922`。Linux、.NET 9.0.120，普通 GC；完整 Coordinator、Smart 药水、开启组合，High 配置并固定 Beam 90、每成员节点配置 6000、DOP 1、请求时间 60000 ms、进程超时 120 秒。请求总工作量包含所有组合和后续审计，不能将 6000 当请求总节点数。

预先选定 17 个输入：五角色各精英、首领、普通遭遇，加弃牌/药水哨兵。**实际仅执行两个代表根，各一次 EventPipe 采样和一次重复评估探针；其余 15 根没有执行。** 未选出值得上线的候选，因此未进行广泛候选 A/B、原生差分、DOP 等价或可见 Steam 验收。不得将准备输入写成通过测试。

| 实际输入 | 来源 | 完整请求展开/转移 | 预测累计战损 |
| --- | --- | ---: | ---: |
| Defect 精英 | `coverage/novelty-search/dev-02-defect-elite.json` | 12000 / 47382 | 4 |
| Regent 首领 | `coverage/novelty-search/dev-08-regent-boss.json` | 41692 / 192475 | 40 |

输入种子、解析后的装备、模型目录指纹及完整指标保存于 JSON。两次探针与对应采样的搜索摘要、剪枝计数相同，均未命中时间边界；这只检查诊断没有改变这些观测，不证明完整语义等价。

## 采样与候选取舍

使用 `dotnet-trace` 的 `dotnet-sampled-thread-time` 与 `Microsoft-Windows-DotNETRuntime:0x1:5`，以 `GcTraceAnalysis` 分类。确认搜索栈的分配样本分别估算约 2.056 GB、7.503 GB；它们是采样累计分配，**不是峰值内存占用**。线程采样包含 JIT、等待及 GC，不能称为精确 CPU 或墙钟百分比；带采样器的耗时不进入性能 A/B。

主要成本分散在 Fork、执行、快照及排序。源码检查未发现可以直接删掉的大份无消费者诊断历史：活动 trace 出栈会清除，历史已有不可变前缀和标量卡牌记录，生成选项仍被选择续执行消费。卡牌 wrapper、监听数组、可变 Power 的复制承担分支隔离和引用重映射；不能仅依据分配排行共享这些可变对象。紧凑 COW、评分缓存等已有[否决实验](bold-backend-experiments-20260908.md)，本轮没有将旧方案当作新成果。

决策侧复查已有实验：

- `ContinuousThreatRanking` 的单 solver 验证已有两例胜转败，不默认启用。
- `BoundedOffensiveRefinement` 在 32 个可比 validation 根未增加决策收益，转移增加约 3.36%。
- `AdaptiveNovelty` 在 32 根中仅改善 1 HP，且增加成本。
- `ReallocatedRefinementPortfolio` 已是当前基线的一部分，其原报告的转移约 −6.52%、分配约 −5.97% 不能再次计作本轮优化。

这些是[既有研究](../strategy/contextual-ordering-20260922.md)及[组合再分配证据](../strategy/contextual-portfolio-reallocation-20260922-evidence.json)的复查，没有在本轮重跑或重新主张其性能。

## 新探针：跨成员 StandPat 标量复用

`ComputeStandPat` 为保路执行一次 EndTurn，返回胜负、延迟伤害、预测 HP 和资源价值四个标量。现有缓存属于每个 solver。本轮临时增加请求进程内的 `(StateFingerprint, node.Turn)` 字典，**仍执行每次原回放**，记录重复、值不一致、该回放耗时及线程分配。没有实现跳过回放的缓存优化。

| 输入 | 回放数 | 重复数 | 重复返回值不一致 | 重复回放占请求实测时间 | 重复回放占累计分配 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Defect 精英 | 1828 | 9 | 0 | 0.0072% | 0.0150% |
| Regent 首领 | 37309 | 13895 | 0 | 5.5096% | 7.2330% |

Regent 的 37.24% 回放重复率并不等于整搜可提速 37.24%。按已观测回放时间直接估算，消除全部重复只涉及约 5.5% 请求时间；该估算还没有加入缓存查询、容量与同步成本，也没有计量改变分配后的间接 GC 效应。两根的零值冲突不是通用键充分性的证明；生产实现还需核对根/政策域、回合上下文、挂起选择、并行发布及诊断/预算记账。

数据不足以支持 20% 全请求收益或峰值内存改善，因此撤回临时探针，不保留新的缓存、开关或生命周期复杂度。这里没有发生以质量换性能的生产取舍。

## 复现与收尾

诊断补丁只用于一次请求、DOP 1 的独立进程；静态字典不支持并行写入，也不跨请求清空，禁止作为产品改动使用。应用补丁后先构建主项目和离线宿主。为每个源 spec 建请求包装：

```json
{"scenarioId":"GENERAL-PERF-PROBE","characterId":"REGENT","generatedScenarioPath":"<仓库绝对路径>/coverage/novelty-search/dev-08-regent-boss.json"}
```

```bash
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request <请求包装路径> --label standpat-probe --out <证据目录> \
  --profile High --beam 90 --nodes 6000 --dop 1 --budget-ms 60000 \
  --search-mode Coordinator --use-portfolio
```

采样使用未应用诊断补丁的固定基线 DLL，通过 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 指定，在同一命令前使用 `dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x1:5 --profile dotnet-sampled-thread-time --output <trace路径> --`。每次独立进程，构建和其他负载不与采样并行。

探针构建及撤回后的主项目、离线宿主 Release 构建均为零警告、零错误。原生产文件已恢复；最后只提交本报告、探针复现附件和索引。未推送、部署、提升版本或发布。20% 收益及用户要求的广泛最终验收仍未完成。
