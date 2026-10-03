# 后端真实 CPU 热点与本轮局部修改（2026-09-08）

[返回性能目录](../../performance/README.md) · [结构化证据](backend-cpu-hotspots-20260908.json)

本轮先尝试了两个监听索引原型和一个归一化跳过条件。用户指出应先用性能工具采样热点；复核后确认，此前以遍历次数和有缺陷的线程采样口径优先选择修改点，依据不足。现在以 Linux `perf` 的实际 CPU 样本重新排序。没有实现新后端，没有改变极高预设的预算、评分、搜索配额或 GC 配置。

## 先纠正采样口径

此前25秒 EventPipe trace 中有296,480个`External`线程样本，仅10个`Managed`样本。旧 reader 只按函数名排除部分等待，剩下的样本不能称作真实CPU样本。例如旧摘要把`WaitForNextOutcome`列为约9.14%的自身样本，已经说明分母含等待污染。调用次数、线程驻留、分配字节和实际CPU时间必须分别解释。

旧原始数据保留，不倒推这些External样本都是等待，也不把仅10个Managed样本当成有效CPU基准。前几轮未插桩A/B的耗时与工作量证据仍成立；旧分配采样也不因线程样本口径问题自动失效。已在相关历史报告标注纠正。

微软说明了[EventPipe线程时间的启发式分类](https://github.com/microsoft/perfview/blob/main/src/TraceEvent/Computers/SampleProfilerThreadTimeComputer.cs)，Linux实际CPU采样使用[perf及.NET符号](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/trace-perfcollect-lttng)。本次不再用函数名排除法模拟CPU采样。

## 本次工具与输入

- `perf 7.2.2`：`cpu-clock:u`，199Hz，实际被调度执行的用户态CPU；采样同时覆盖搜索协调线程和工作线程。
- `dotnet-trace 9.0.661903`：`Microsoft-Windows-DotNETRuntime:0x4001:5`，采集GC、分配与锁竞争。
- TraceEvent 3.1.23读取事件；`/proc/PID/task/TID/stat`每250ms记录线程用户态/内核态CPU；FlameGraph输出独立SVG。
- 工具解压/安装到`.local/profilers/`，无系统权限调整、无生产DLL部署。

沿用死灵法师药水投影输入：38张牌、20件遗物、2瓶药水，要求至少用一瓶，`AEONGLASS_BOSS`、A10/Act2、526敌HP、41/76玩家HP。可移植输入为`coverage/unattended/search-performance-necrobinder-projected-{run-cards,relics,potions}.json`。两次请求均先在独立进程做短搜预热，再运行正常极高配置；DOP8、NoGC16GB、详细诊断/增量验证关闭、120秒上限、首个结果停止。

采样产物固定为`17abf8b`加SwordSage根值修复，不含索引原型或跳过归一化的候选。第一次CPU采集的DWARF展开和JIT时钟配置不足，仅保留其CPU叶地址作为辅助证据；同一次GC/锁/分配trace可正常解析。随后只补采CPU，使用`perf record -k 1 --call-graph fp`、`DOTNET_PerfMapEnabled=1`、`DOTNET_EnableWriteXorExecute=0`及`perf inject --jit`解析托管调用链。诊断环境不写入生产设置，两次采样耗时不作优化A/B。

| 采样请求 | runId | 搜索耗时（含诊断开销） | 工作量 |
|---|---|---:|---|
| CPU叶地址 + GC/分配/锁 | `bfe36e31c3854ce094d5954b27675200` | 43.369秒 | 46,239展开 / 636,428转移 / 431,140选择 |
| 可展开CPU调用链 | `042976f414ce4392a4e03905827c475f` | 42.416秒 | 同上 |

均Passed、Deep、投影战损9、仅死亡路线，未部署整场。相同工作量/投影结果不单独证明完整状态等价。

## 实际 CPU 热点

共29,570个CPU样本，其中23,915个包含已解析的`CombatBeamSolver`调用链。下表分母仅为这23,915个搜索样本；先归快照，再Fork，再回放，再保留排序，**互斥分组，不重复累加子调用**。这是多个线程累计的用户态CPU分布，不能直接乘墙钟时间得到各阶段耗时。

| 类别 | 搜索CPU样本占比 |
|---|---:|
| 动作回放，排除Fork和快照 | 46.30% |
| 快照构建，含内部调用 | 29.68% |
| Fork，排除快照内调用 | 11.33% |
| 其他搜索工作 | 7.48% |
| 保留/排序，排除上述路径 | 5.21% |

需要继续下钻的具体函数（inclusive占比有重叠，不能求和）：

| 函数 | 含子调用占比 | 说明 |
|---|---:|---|
| `GetEffectiveHookListeners` | 7.96% | 主要看监听列表重建及Power替换，不能与其调用方EffectivePowers重复计数 |
| `BuildStateKey` | 6.16% | 状态指纹及其领域状态读取 |
| `CalculateReachableHandPotential` | 5.17% | 可出牌性、费用与可达手牌估值 |
| `NormalizePowerAfflictions` | 4.41% | 包含读取有效Power列表的成本，不能把全量视作遍历牌的成本 |
| `MirroredHookListenerFilter.Filter` | 3.82% | 类型布局构建/匹配及过滤视图 |
| `BuildProjectedShuffleOrder` | 3.32% | 投影洗牌及卡牌指纹 |
| `NormalizeSwordSageReplays` | 0.27% | 前一轮优先选择它不符合CPU收益排序 |

自身热点中，`IsInstanceOfClass`为4.37%，约90%的这些样本紧邻有效/活动/基础监听列表及Power列表读取；`StateFingerprintBuilder.Add`为3.50%，散布于循环牌形、Power、怪物及完整状态指纹。不能简单归因于“AddRange慢”或“完整Model复制占大多数CPU”。普通`List.AddRange`本轮inclusive仅约1.23%；不同采样/内联边界也不能把旧排名直接换算成优化收益。

解析限制保留在分母中：13.84%的搜索叶样本为未解析的`libcoreclr.so`函数，符号服务器返回503；约2.85%为未解析JIT/桩叶符号。全进程仅2个样本无调用栈，7个栈达到127帧上限。不将unknown当零成本。局部火焰图在`.local/backend-cpu-profile-20260908/search-cpu.svg`，原始trace/符号/火焰图不进入源码提交。

## 分配、GC与锁是另一组指标

GC/锁trace覆盖约50.798秒，包含建局与搜索后的尾部。搜索指标独立记录累计GC暂停1,984.703ms，最长1,927.362ms；trace对应最长暂停1,927.429ms。全部trace暂停总计约2,980.862ms，其中包括搜索结束后的约693ms收集，不能混入搜索暂停。长暂停是明确卡顿来源，但不是四十多秒搜索的全部解释。

- 记录34,613次完成的锁竞争，累计约1,143.436ms**线程等待时间**；多个线程可重叠，不是墙钟暂停。主要来自BaseLib目标类型桥，另有弱表和镜像惰性初始化；此窗口证据不支持先给所有模拟加锁或优先重写调度器。
- GCAllocationTick采样估计全部分配36.108GB，其中带搜索栈35.956GB；搜索自身计数为35.452GB。两者口径不同，不要求逐字节相等。
- 搜索分配估计互斥分组：回放34.33%、Fork31.57%、快照13.93%、保留10.32%、其他9.85%。Fork在**分配**上的占比明显高于**CPU**，不能混用两个百分比。
- 具体分配站点包含牌包装、监听数组、Power动态变量、字典和枚举器；一个站点的采样字节也不能代表该对象类型的全部分配。详细站点、锁栈和暂停区间见JSON。

## 本轮修改与撤回的原型

保留的生产变更仅在`SimulatedCombatState.PowerLifecycle.cs`：

1. `NormalizeSwordSageReplays`的初始基线从已捕获的`_rootPowerAmounts`读取，消除worker读取live Power层数的路径。后来生成的牌仍以0为已应用基线。
2. 当前/根加成都为0且没有已记录加成时跳过此族遍历；字典延迟到首张符合条件的牌才创建。以后获得Power仍扫描，移除已应用加成仍处理，不新增分支状态、缓存或新后端。

短搜各用独立进程预热后一次正式样本，均10,000展开/144,368转移/101,808选择，投影战损3：

| 候选 | 秒 | 分配GB | 决定 |
|---|---:|---:|---|
| 仅根值修复的对照 | 6.0548 | 7.4152 | 对照 |
| 每布局至多4个惰性回调索引 | 5.9959 | 7.4457 | 收益不足且增加分配，撤回 |
| 单份非空回调位置表 | 5.9519 | 7.4356 | 收益不足且增加分配，撤回 |
| SwordSage零加成跳过 | 6.0261 | 7.4024 | 保留简单的无效工作消除，不宣称明显提速 |

零加成候选只减少约12.77MB分配，耗时差约0.47%，未证明显著速度提升；CPU采样也证实它只是小项。没有新翻倍结论，不能把4380万归一化访问或1.69亿Hook位置检查换算为加速倍数。

根所有权失败基线`08fae83abbc3476ab75c4c6ff6afdd84`在捕获后改变live层数时明确失败；仅根值修复`630b137688304d3196be5c10a874c6bc`通过。最终`97c88a05ed9342528dda268da91836d6`通过冻结live、分支增量、首次归一化前移除、从零获得、父子隔离、生成牌及幂等性合同，日志含`SWORD_SAGE_ROOT_BASELINE_OK`。Release零警告/错误。

可重跑最小根合同（普通root夹具无该Power时不会运行此扩展，因此必须保留注入条件）：

```bash
./tools/run-unattended-test.sh --scenario-id BACKEND-SWORD-SAGE-ROOT --character-id REGENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --seed BACKEND_ROOT_SWORD_SAGE --clear-run-deck --clear-player-piles --cards-json '[{"cardId":"SOVEREIGN_BLADE","pile":"Hand","treatAsDeckCard":true}]' --powers-json '[{"powerId":"SWORD_SAGE_POWER","target":"Player","amount":2}]' --verify-combat-root-snapshot --stop-after-combat-root-snapshot-assertion --timeout-seconds 120
```

本轮未改并行、评分或分支保留规则，也未新增结构所有权；不追加无关整场或全覆盖门禁。没有可见Steam、Windows游戏、完整原生战斗或增量搜索验收。

## 后续优化的证据门槛

优先分解约30%的快照成本，以及横跨回放/快照的有效监听列表重建；先确认重复输入、失效边界及其CPU/分配量，再选单个局部因素。保留完整状态键语义、第三方回调顺序、Fork隔离和搜索质量；不用缩减预算换速度。先做相同固定工作量A/B，只有收益成立才扩大最终验证。大范围Power/COW、可恢复调用栈或动作撤销仍不在当前任务范围。
