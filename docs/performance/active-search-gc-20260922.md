# 计算过程中回收已结束的子搜索（2026-09-22）

## 结果与取舍

在预先选定、全部纳入的17个完整请求中，默认16 GB NoGC配置下，逐场工作集峰值降幅平均
**36.44%**，采样托管占用峰值降幅平均 **41.34%**；完整请求耗时平均 **增加1.56%**。
这次统计的是搜索请求期间的峰值，收益来自同一请求仍在继续计算时的回收；不计上一批搜索结束后释放引用的收益。

17对完整路线、质量结果、根状态和政策均相等。展开与转移等工作量一致；16对全部搜索/剪枝字段相等，
`holdout-04-necrobinder` 仅 `threatProjectionCacheCount`（23760→33042）与
`coverageCacheCount`（632→812）不同。GC检查点的位置影响缓存重置次数，不把缓存数量等同于路线变化。

这是一项以少量计算时间和更频繁GC换取较大内存下降的优化，没有减少候选、缩小NoGC/节点/时间预算，
没有改评分或增加角色、牌名、遭遇分支。普通GC及没有后续子搜索的请求跳过该入口。
较小NoGC预算不保证同样收益，补充对照中的不利结果单独保留。

## 调研与实现

上游 `6922828d` 已拉取，工作分支 `perf/bounded-hotpath-20260922`，本次基线 `ffb6a6e8`。
前面的[热点筛查](general-optimization-screen-20260922.md)及活动堆采样表明：
小集合排序、跨成员StandPat复用、转置/威胁缓存压缩的请求级收益空间不足20%；重写整个快照表示风险和范围过大。

Coordinator 的组合成员及后续补充搜索串行执行，但其间共享一个NoGC scope。
前一个求解器已结束，产生的大量不可达对象仍可一直积累到区域容量检查点。
因此选择在后续 `CombatBeamSolver.Solve()` 开始、前一个求解器及其固定lane已排空时，
通过Runtime注入的信号尝试既有后台非压缩回收，并按相同配置预算重开NoGC。

生产修改仅涉及三份文件：

- `SearchMemoryPressureSignal`：同一scope首次调用跳过，后续调用可选回收；实际回收低于既有1 MiB进展阈值后，
  该scope不再尝试搜索间回收。这个抑制跨单次solver的统计重置保留，Disable清除回调及状态。
- `SearchGcPolicy`：复用既有256 MiB分配阈值与 `ReclaimWithinSearch` 完成链。
  在同一个Gate内取得唯一所有权；无法立即取得时跳过。沿用原取消、手动请求、deferred、回退和恢复处理。
  实际NoGC scope仍串行准入，排队请求不能使当前检查点等待自己退出。
- `CombatBeamSolver.Phases`：仅调用注入信号，不操作GC模式。检查点处于原请求计时范围；
  即使收集后取消，也在finally记录已经发生的暂停；跳过入口时清零本次暂停，避免跨solver误归因。

这没有建立另一套GC状态机或新增用户设置。初版大语料之后只补了取消/跳过路径的暂停统计；
最终二进制另跑3对代表性重复对照、8对配置/截止时间补充，以及合同和原生检查。

## 主实验

Linux / .NET SDK 9.0.120，独立进程串行运行，每个根交替A/B与B/A。
High、beam90、节点配置6000、DOP1、Coordinator、既有portfolio、请求预算60000 ms、
NoGC 16,000,000,000 B（当前默认值）；每个进程120秒上限。五角色各覆盖精英、首领与留出场景，
再加弃牌、药水哨兵。期间不并行运行构建、测试、Profiler或其它性能任务。

峰值来自宿主100 ms采样：`peakWorkingSetBytes`为进程RSS，`peakManagedLiveBytes`实际是
`GC.GetTotalMemory(false)`，包含尚未回收的垃圾，不是严格可达对象图大小。
采样窗口从根捕获前到结果指标整理完成，包括少量收尾；不是整段游戏会话，也不是精确瞬时峰值。
`peakManagedHeapBytes`只反映最近一次完成GC的堆，在NoGC期间严重滞后，未拿它作验收指标。
采样不强制收集；新回收日志均为下一次搜索前的 `trigger=between_searches`。

MB为十进制，负变化表示候选更少/更快。

| 场景 | RSS A→B MB | RSS变化 | 托管峰值变化 | 请求耗时变化 |
|---|---:|---:|---:|---:|
| dev-00-ironclad-elite | 5256.5→3056.1 | -41.86% | -44.79% | +2.42% |
| dev-01-silent-elite | 12926.2→8169.7 | -36.80% | -43.27% | +1.06% |
| dev-02-defect-elite | 2334.7→1402.0 | -39.95% | -45.54% | +2.14% |
| dev-03-regent-elite | 5838.1→2889.0 | -50.51% | -53.79% | +4.51% |
| dev-04-necrobinder-elite | 3429.4→2041.5 | -40.47% | -45.03% | +1.38% |
| dev-05-ironclad-boss | 5738.4→3472.4 | -39.49% | -42.20% | +2.74% |
| dev-06-silent-boss | 6970.6→2782.5 | -60.08% | -75.59% | +2.32% |
| dev-07-defect-boss | 3258.7→1766.0 | -45.81% | -58.38% | -0.43% |
| dev-08-regent-boss | 7807.3→3403.7 | -56.40% | -66.59% | +3.71% |
| dev-09-necrobinder-boss | 4880.0→2838.8 | -41.83% | -45.10% | +2.93% |
| holdout-00-ironclad | 1447.3→1026.5 | -29.07% | -36.39% | +2.55% |
| holdout-01-silent | 861.3→859.4 | -0.22% | -0.19% | -0.29% |
| holdout-02-defect | 3035.9→1254.1 | -58.69% | -64.87% | +4.62% |
| holdout-03-regent | 7971.0→5196.6 | -34.81% | -36.69% | +1.22% |
| holdout-04-necrobinder | 11136.6→6266.0 | -43.74% | -44.63% | +0.29% |
| sentinel-discard | 2258.3→2262.5 | +0.19% | +0.20% | -4.28% |
| sentinel-potions | 930.1→930.2 | +0.01% | +0.01% | -0.40% |

主结论采用 `mean(1-B_i/A_i)`，每个场景权重相同，含3个无搜索间回收的场景。
另一口径 `1-sum(B_i)/sum(A_i)`：RSS **42.36%**、托管峰值 **48.05%**，总请求耗时增加 **1.61%**。
前者与后者不能混写；二者均超过20%的内存门槛。全进程累计分配基本不变：合计增加0.0078%，逐场变化平均+0.035%。
这是更及时回收不可达对象，并未减少模拟器本身的分配。

所有主样本完成，未发现请求内 `TimeLimit` / `TURN_LAYER_BUDGET reason=time`，且完整路线与工作量可对账。
不能仅凭宿主 `timeBoundaryObserved=false` 判定Coordinator没有截止时间截断，补充检查了进程日志及成员终止。
17对每臂各1次，墙钟小差异没有统计显著性承诺；内存收益远大于采样/进程启动波动，最终版本还有重复对照。

## GC成本

17个请求的Gen2次数合计8→140，候选执行70次搜索间回收。原基线在两场内部容量检查点回收，
候选更早回收使这些内部检查点未再发生。搜索期GC总暂停合计2261.12→3286.44 ms（+45.3%）；
最大单次观察值1139.56→229.98 ms。后者受两个大场景的基线长暂停影响，不承诺所有场景的最大暂停下降。
原本零GC的许多请求现在会有暂停，单场新增最高约220 ms级；约1.6%墙钟代价与36.4%平均RSS峰值下降构成本次取舍。

日志中一次显式后台回收可能对应多个CLR Gen2计数（含区域重建），不能把70个检查点写成70次Gen2。
宿主部分 `GcLifecycle` 字段未包住外层scope，报告使用实际日志和 `TotalGen2Collections` / 暂停指标。
未测可见Steam帧时间、Windows或完整Mod栈，不能由headless数据宣称没有界面卡顿。

## 补充与正确性验证

最终版本另有11对请求，全部完整路线、质量、根、政策及去掉工作计数后的搜索结果相等。
其中3对是主配置的预定代表性重复：RSS平均−43.23%、托管峰值平均−45.97%、耗时平均+2.35%。
下面8对覆盖并行、小区域、普通GC和短截止时间，不混入主17根汇总。

| 配置/场景 | RSS变化 | 托管峰值变化 | 请求耗时变化 |
|---|---:|---:|---:|
| deadline-dev-03-regent-elite | -9.86% | -10.83% | +0.01% |
| deadline-dev-06-silent-boss | -34.99% | -38.00% | +2.06% |
| dop4-dev-03-regent-elite | -28.49% | -30.71% | -0.86% |
| dop4-dev-06-silent-boss | -39.50% | -41.21% | +3.63% |
| normal-gc-dev-03-regent-elite | +1.71% | +0.87% | -0.58% |
| normal-gc-dev-06-silent-boss | +1.97% | +7.32% | -2.53% |
| small-region-dev-03-regent-elite | +5.70% | -5.78% | +1.72% |
| small-region-dev-06-silent-boss | -10.76% | -4.14% | +2.71% |

- DOP4两场路线相同；Regent仅回放前缀捕获/实际Fork计数变化，展开与转移不变。
- 1 GB区域两场存在缓存计数差异，路线相同，时间增加1.72%/2.71%；Regent的RSS增加5.70%，
  Silent下降10.76%。这类本来就频繁内部回收的配置没有20%收益承诺。
- 普通GC两场完整工作计数相等，搜索间回收次数均为0；RSS增加1.71%/1.97%，时间降低0.58%/2.53%，
  不把普通GC波动解释为本入口的收益。
- 8秒软预算两场都有工作量差异，最终路线仍相同；Regent展开6467→6446。
  Silent完整请求13.65→13.93秒，原有补充搜索会超出该软预算，因此未称为严格8秒截止，也不混入固定工作量计时。
- 总计28对/56次请求的完整路线、质量及去掉计数后的搜索结果全等；这是已测集合的结论，不承诺所有墙钟截断下恒等。

GC工具直接编译生产源码，基础26、scope8、检查点1、新搜索间检查点7、恢复9、恢复生命周期2、
诊断失败8，共**61项通过**。新合同包含未配置/首次/小分配跳过、成功/拒绝计数、异常与取消、暂停保留与清零、
无进展抑制、Disable重开，以及真实1 GB NoGC中第二scope排队时首个检查点仍能完成并重开区域。
初版夹具误以为NoGC scope允许并行准入，触发5秒超时；核对既有串行准入契约后修正夹具，未为测试改动生产准入。
最终测试以真实等待日志确认已排队，不把未获线程调度误当成排队证据。

Release主项目及GC工具均零警告/错误，Bash结构门禁通过（208份Search文件）。
以下原生请求均Passed、error=null，120秒上限，独立实例；三次均由启动器确认清理。

| 原生检查 | runId | 实际覆盖 |
|---|---|---|
| 首次搜索跳过 | `61f2ce1a8f4a43dc98ec857871b3c553` | Regent / NoGC16 GB / DOP2；实际全局预算1秒，245展开，0回收 |
| 严格增量回放 | `58fad8e9b366401ca20dccabe5bc728d` | Defect / 普通GC / DOP1，verify-incremental-search开启，300节点完成，无不一致断言 |
| 活动请求内回收 | `eda4cb6d8d9f4e6bb194197ef975d045` | Regent / NoGC16 GB / DOP2 / 30秒，4000展开；强制回收1、区域重开1、区域丢失0；仍处于NoGC |

首次Regent请求被启动器默认全局1000 ms覆盖，仅证明首个入口跳过；追加明确全局30000 ms的输入才覆盖实际回收。
启动前还修正一次DLL/manifest目录选择，失败发生于preflight，未启动游戏。没有将这些不足的尝试冒充多solver验收。
原生检查止于首个结果，未执行完整战斗、可见交互或发布流程。


## 复现与证据

17个场景配置在 `coverage/novelty-search/`，临时请求仅包含scenarioId、characterId和对应配置的绝对路径。
完整命令、指标、日志摘要、对等检查和运行标识见[结构化证据](active-search-gc-20260922.json)。
本机原始结果保留在 `.local/active-memory/{corpus,extra,contracts}`，完整日志与二进制不提交源码。

```bash
OFFLINE_HARNESS_COMBATSOLVER_DLL=/absolute/path/CombatSolver.dll \
  dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request /absolute/path/request.json --out /absolute/path/output \
  --profile High --beam 90 --nodes 6000 --dop 1 --budget-ms 60000 \
  --search-mode Coordinator --use-portfolio \
  --enable-no-gc-region --no-gc-region-budget-gigabytes 16

dotnet run --project tools/CombatSolver.GcPolicyChecks -c Release -- between-searches
```
