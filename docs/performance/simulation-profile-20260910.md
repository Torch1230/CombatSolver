# 极高配置完整搜索的模拟热点（2026-09-10）

[性能目录](README.md) · [结构化结果与完整命令](simulation-profile-20260910.json)

基于上游 `a6bc386` / `0.34.6`，本轮只采样和分析。已删除本地已合并的 `perf/veryhigh-quality-preserving`，新分支为 `perf/simulation-profile-20260910`；远端分支按用户要求留给用户手动删除。

## 结论

模拟及其状态处理确实是搜索的大头。互斥归类后，动作、回合与其余回放工作占两场搜索 CPU 的 **44.82% / 47.99%**；加上分叉复制为 **59.33% / 61.74%**。模拟内部最重的是跨回合结算，其次是出牌；快照估值也是独立大项，不能全部归到原版效果模拟。

以下百分比分母为各自可归因搜索的 on-CPU 样本，包含完整请求中的主搜索及药水审计。不是游戏整体 CPU/FPS，也不是阶段 Stopwatch 时间除以墙钟。

| 互斥路径 | 亡灵契约师 / AEONGLASS | 静默猎手 / 机甲骑士 |
|---|---:|---:|
| 回合推进 `AdvanceRound` | 20.26% | **33.52%** |
| 快照与估值 `Snapshot` | **28.25%** | 24.43% |
| 手动出牌结算 `ManualPlay` | 16.34% | 8.94% |
| 分叉状态复制 `Fork` | 14.52% | 13.75% |
| 其余回放与边界处理 | 6.87% | 5.53% |
| 药水效果执行 | 1.35% | 0.00% |
| 其余保路排序 | 3.09% | 2.14% |
| 其他搜索工作 | 9.33% | 11.69% |

归类优先级为 Fork → Snapshot → AdvanceRound → ManualPlay → 药水执行 → 其余 Replay → 保路 → 其他。回合中的自动出牌属于回合，保路探针内部的模拟和快照归入对应模拟/快照项；因此“其余保路”不包含这些成本。

## 模拟内部具体耗在哪里

- **回合生命周期**：[AdvanceRound](../../src/Search/CombatBeamSolver.Expansion.cs) 包含玩家回合结束、手牌弃置/结算、敌人行动、玩家回合开始、抽牌及自动牌。亡灵契约师的回合开始自动牌约占全部搜索 CPU **3.13%**，敌人行动 **2.81%**，玩家结束阶段 **2.46%**，抽牌前处理 **2.43%**；机甲骑士场景分别以敌人行动 **6.16%**、抽牌 **4.17%**、玩家结束阶段 **3.79%** 较明显。没有一个敌人 AI 函数独占整个模拟成本。
- **出牌与事件回调**：亡灵契约师的 `ManualPlay` 内，`AfterCardPlayed` 连同其子调用占该阶段 **21.82%**，镜像 registry 调用占 **41.00%**。全搜索的 Hook 回调约 **12.37% / 7.03%**，监听列表构建、过滤及重映射约 **7.43% / 6.34%**。这些 inclusive 数字相互重叠，不能相加为“可省掉的分发开销”。真实效果执行也包含在回调里。
- **全牌堆归一化**：[NormalizeCardAfflictions / NormalizePowerAfflictions](../../src/Search/SimulatedCombatState.PowerLifecycle.cs) 等路径约占全部搜索 CPU **3.89% / 4.92%**。它们反复维护污染与生成牌入场状态，成本来自扫描、身份查找和条件处理；没有相关 Power 也不能直接删除生成牌的首次入场记账。
- **状态复制**：[ForkPower](../../src/Search/SimulatedCombatState.Fork.cs) 占亡灵契约师 Fork CPU 的 **36.78%**，监听缓存恢复占 **14.63%**。前者进入原版 `PowerModel.DeepCloneFields → DynamicVarSet.Clone`，后者遍历缓存并按同一 Fork context 重映射。通用 `PredictionStateStore.Fork` 仅占该 Fork CPU 的 **4.00%**，不是最大复制子项。
- **快照估值**：[StateEvaluation](../../src/Search/CombatBeamSolver.StateEvaluation.cs) 的状态键构建约占全搜索 **6.55% / 4.98%**，投影洗牌 **3.45% / 3.53%**，可达手牌估值 **3.37% / 2.46%**。这些是搜索为了比较下一步而反复计算的成本。

上述子路径都是包含子调用的观察值；内联和未解析的叶帧会影响精细函数归因。后续优先细化回合边界重复维护、快照计算与 Power 克隆的可复用部分，保持牌序、RNG、Hook 顺序和分支所有权；本轮没有实施优化或承诺收益。

## 内存分配

分配单独使用完整正常搜索采集。`AllocationTick` 按字节权重估算，比例不是精确对象数或存活堆大小。

| 互斥分配路径 | 亡灵契约师 | 机甲骑士 |
|---|---:|---:|
| 分叉复制 | **34.19%** | 25.14% |
| 回合推进 | 19.11% | **39.67%** |
| 出牌结算 | 14.89% | 7.97% |
| 快照估值 | 9.98% | 8.48% |

Fork 内，Power 模型克隆占分配权重 **32.69% / 20.67%**，牌堆与 `PredictedCard` wrapper 占 **18.87% / 21.88%**。`DynamicVarSet.Clone` 跨全请求占分配权重 **8.93% / 4.73%**，包含于模型克隆，不能重复相加。

精确 worker 累计分配分别为 **86.683 GB / 5.707 GB**，不是同时占用这些内存。两份 trace 的已归因权重分别为 87.725 / 5.760 GB；与精确计数的差异保留，不能把采样值伪装成精确统计。完整转换成功、丢失事件均为 0，分别有 **815,473 / 53,600** 条可归因搜索分配样本。

## 条件与直接结果

- Linux、Ryzen 7 7840H、游戏 `0.111.0`（`41cef1ea`）、Release `0.34.6`。
- **VeryHigh、DOP8、NoGC 配置 16 GB**；正常搜索整场路线，未启用 `ForceShortSearchOnly`，未覆盖时间、节点、Beam 或分支预算，未运行增量验证。首个正常搜索请求完成后停止，未实际部署整场战斗。
- 亡灵契约师使用仓库 projected 输入的 38 张牌、19 遗物、两瓶药，药水政策 `RequireAtLeastOne`；包含正常的后续药水审计。机甲骑士使用维护中的 `performance-veryhigh-mecha-native.json` 的全部 30 张牌及附魔、固定种子、Smart 政策；它是明确建局的基准，不冒充旧部分存档的精确恢复。
- CPU 使用 `perf record -k 1 -e cpu-clock:u -F 199 --call-graph fp -p PID`，开启 .NET perf map/JIT dump，随后 `perf inject --jit` 与 `perf script --ns`。分配使用 `dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x1:5`，目标保持存活直至 rundown 结束。
- 正式分配采样进程先运行完整正常搜索预热；第一份 CPU trace 前有短搜预热。初期短搜采集链路检查全部留存，但不进入以上正式结论。

| 正式请求 | runId | 累计展开 / 转移 / 选择 | 搜索记录秒 | 预计结果 |
|---|---|---|---:|---|
| 亡灵契约师 CPU | `e4d7d876fdf043679e33b9d0236ae49d` | 150,035 / 1,693,024 / 1,024,228 | 80.848 | 战损 9 / T13 / 2 药 |
| 亡灵契约师分配 | `4f810c9cfe014539bce8ed2884b53dd8` | 同上 | 80.479 | 同上 |
| 机甲骑士 CPU | `418816fd5abc4e0f957f9ab0cf07adb7` | 14,611 / 119,407 / 67,273 | 3.888 | 战损 6 / T7 / 0 药 |
| 机甲骑士分配 | `5fd38f44839842f083546fcc91bb310e` | 同上 | 3.736 | 同上 |

四项均 Passed。CPU/分配对照只确认上述工作量和结果标量，没有做逐动作严格等价。耗时带有各自 profiler 开销及 GC 波动，不能作速度 A/B。亡灵契约师分配请求记录 GC 总暂停 2,379.696 ms、observed max 2,037.767 ms；不能因 CPU 阶段热点较分散就忽略长暂停。

结果中的 `phase=Short` 是选中 solver 的耗时派生标签，不表示开启了短搜开关。完整命令与请求累计工作量保存在 JSON；例如亡灵契约师选中 solver 只有 32,593 展开 / 409,290 转移，不能把它当作整个请求的 150,035 / 1,693,024。已有 `SEARCH_PHASE` 也只对应选中 solver，未当作全请求阶段总量。

## 复现与限制

先 `dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false -o .local/simulation-profile-20260910/artifact`，将根 manifest 与 NOTICE 放入该目录。机甲牌组文件由 JSON 的 `supportingFixture.contents` 写到其 `generatedFile`，内容与维护中原 fixture 的 `runCards` 一致。依次运行结构化结果内的完整命令，预热与测量复用同一隔离实例，采集器在测量请求前附加、请求结束后关闭；最终使用 `--stop-instance` 停止自己的实例。

本次 Mod Release 构建 0 警告、0 错误；分配解析工具构建成功，临时细分工具有 NU1900 包漏洞源不可达警告。未改生产行为，不追加语义回归或发包。原始 profiler、命令、结果和日志留在 `.local/simulation-profile-20260910/`。

亡灵契约师 CPU 共 85,388 样本，其中 67,667 可归因搜索；机甲骑士共 4,281，其中 3,455 可归因搜索。前者 31 条、后者 1 条调用栈达到 127 帧；缺失栈分别 1 / 0。约 79.25% / 80.71% 的进程样本可归因搜索，其余包括 GC、JIT、其他游戏工作及未归因栈，不擅自归到模拟。较少的机甲样本不用于判断微小百分比差异。

第一次 perf 包装脚本把正常停止信号 `SIGINT` 返回码误判为失败，但 perf 已完整写出 85,388 样本；本轮复用原数据完成符号解析，没有重跑该 CPU 场景。该次退出留下截断的战斗 JSONL，未作为完整动作证据；后续日志通过正常请求退役刷出。

旧 `mecha-knight-memory-run-snapshot.json` 建局失败（runId `1c1134cbba904d8c995745f1e4247cd2`），因缺少角色 ID 在 `Player.FromSerializable` 抛出异常；该请求未进入搜索，已排除。后来改用维护中的原生建局 fixture，未通过删牌、跳过机制或改低预算让旧输入通过。

本轮环境未连接正常可见桌面，以上属于 **完整搜索的 headless 热点诊断**，没有新的正常可见 Steam 帧率、完整 Mod 栈占用或整场原生部署结论。
