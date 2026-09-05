# issue #36 第二轮：分配采样与低风险候选

研究起点为本分支 `7d724d6`；首轮结论见 [实施报告](gc-issue36-implementation.md)。本轮重新运行起点 DLL，不把首轮不同时间/版本的中位数混入增量收益。公开 fixture、节点预算与平台入口沿用 [重现说明](gc-issue36-reproduce.md)。原始 trace、gcdump、完整日志与 DLL 仅在忽略目录保留；可分享结果见 [结构化证据](gc-issue36-round2-results.json)。

## Snapshot 临时列表

`SearchRunContext` / 每个 worker lane 独占一个 `SnapshotListBuffer<PredictedCard>`，最多缓存一个已清空、实际容量不超过 4096 的列表。`Snapshot` 用栈上 lease 取得列表，按原 Discard → Draw → Hand 顺序填充，继续使用原稳定排序和克隆的 Shuffle RNG。列表只供本次同步特征计算；`StrategicEffectContext.Build` 返回标量，返回的 `SimulationSnapshot` 只保存相关计数和指纹。

`using` 保证正常和异常退出均清空引用；嵌套 Snapshot 使用独立 storage。storage 的租用代次同时校验访问与归还，复制的旧 lease 无法清空已经借给后来 Snapshot 的列表。checkpoint 在 worker 排空后丢弃空闲 storage；没有池化 simulator/model，也没有新增静态缓存。

[独立检查](../../tools/SnapshotListBufferChecks/README.md) 直接编译生产 helper，覆盖嵌套、异常填充、单槽/reset、4096 容量边界、旧 lease 与复制 lease、不同 owner、弱引用回收。它证明容器契约，不替代游戏评分或 RNG 等价。

三次交替冷进程对照，全部在 headless 并行设施移植前完成。Silent 固定每 solver 2500 节点、普通 GC / DOP4；Necrobinder 固定每 solver 576 节点、Smart / NoGC 4 GB / DOP4。前者请求累计 5000 展开 / 19065 转移，后者 1728 / 22541。所有完整 ACTION/TURN、评分、工作量和非时序剪枝逐项一致。

| 场景 | worker 分配中位数 | 搜索时间中位数 | GC 总暂停中位数 | VmHWM 中位数 |
| --- | ---: | ---: | ---: | ---: |
| Silent：起点 → 列表复用 | 1.830 → 1.768 GB（−3.39%） | 10,783.5 → 10,385.6 ms | 1,256.8 → 1,249.9 ms | 1.706 → 1.698 GB |
| Necrobinder：起点 → 列表复用 | 1.289 → 1.271 GB（−1.34%） | 4,766.7 → 4,917.8 ms | 0 → 0 ms | 2.827 → 2.801 GB |

保留此候选的依据是实际分配下降及所有权检查，而非稳定提速：Silent 耗时中位数少 3.69%，Necrobinder 多 3.17%，各轮存在波动，主机其他非游戏负载未控制。VmHWM 包含启动和建局，变化很小。Silent 选中层 projected_shuffle 分配从约 32 MB 降至约 0.6 MB，Fork 基本保持约 404 MB；阶段计数存在嵌套，不能相加成独占总量。

最终候选在新实例入口下通过 `SearchPolicySnapshot` / `ForkBoundaries`，runId `5171caca9cf84baaa3f48884644f3b07`；覆盖 DOP1/DOP2 完整路线与非时序工作量、历史/根/Fork 边界，实际并发为 2。该检查启用 parallel，不使用其时间或分配作为性能证据。

## 被否决的提前写入假设

`AfterCardEnteredCombat` 虽然先取得 `MutablePreview` 才判断部分牌型，但实际生成路径的 `PredictedCard.Create → FromGenerated(card, card)` 已独占预览。`GenerateToHand` 先克隆选中牌，gameplay clone 也已独占；等待回合开始选择的状态不能 Fork。入口仅覆盖 None → 战斗牌堆，普通抽/弃牌移动不会再次进入。

所以这处与首轮永世沙漏“整副共享牌都先 COW 再判型”的问题不同。当前证据支持重复缓存失效，未支持高频 Model 克隆。没有为它修改生产逻辑，也没有人为制造真实路径不可达的共享 wrapper 来证明优化。

## 分配与存活对象采样

采样独立于计时：`dotnet-trace` / `dotnet-gcdump` 均为本任务本地安装的 `9.0.661903`，未更改全局 GC 设置。[GcTraceAnalysis](../../tools/GcTraceAnalysis/README.md) 用 TraceEvent `3.1.23`，按真实方法名建立搜索栈锚点，分开搜索、根捕获、lane 基础设施、其他活动和无法归因部分。

第一次 trace 随游戏退出结束，采集器退出码虽为 0，严格转换仍报流尾截断；显式抢救出 26,774 条 allocation tick，且全部无法解析方法。该文件不能提供搜索调用点归因，也不进入性能 A/B。类型标签包含启动等活动，仅作为调查线索。

补采采用 Held 保持游戏存活。首次误将 Hold 与 StopAfterInitialSolverResultAssertion 同时启用，后者提前返回，ProtocolHost 随后拒绝进入 Held；不能把中间 Passed 文件当作整个采集成功。两端 launcher 现已提前拒绝该互斥组合。

去掉提前返回标志后的采集保持了游戏存活，但重定向 stdin 时换行不触发 dotnet-trace 停止，runner 等待 45 秒超时。异常清理发送给采集器的 SIGTERM 是该 CLI 支持的取消方式，会停止 session 并排空事件；产物最终严格转换成功，有可解析的方法和 rundown。runner 整体超时仍保留记录，不写成整套采集自动化通过。CLI 行为参考该工具版本的 [CollectCommand](https://github.com/dotnet/diagnostics/blob/d7b455b/src/Tools/dotnet-trace/CommandLine/Commands/CollectCommand.cs) 与 [ProcessTerminationHandler](https://github.com/dotnet/diagnostics/blob/d7b455b/src/Tools/Common/ProcessTerminationHandler.cs)；后续使用定时结束或受支持的取消信号，不向重定向 stdin 发送换行。

可归到搜索的样本窗口为 28.424–39.252 秒：18,572 条 allocation tick，权重估计 1,983.53 MB；缺栈、完全无法解析和报告丢失事件均为 0，19 条仍含部分未解析帧。样本权重不等于实际线程分配计数，内联也会改变方法分类。

| 互斥调用栈类别 | 样本数 | 权重占搜索样本比例 |
| --- | ---: | ---: |
| Fork | 8,176 | 43.93% |
| Snapshot / StateEvaluation | 2,919 | 15.72% |
| History | 108 | 0.58% |
| 明确命中 ProjectedShuffle 方法 | 60 | 0.32% |
| 其他搜索调用 | 7,309 | 39.45% |

最明确的栈为 `SimCardPile.Fork → CombatPredictionState.Fork → CombatPredictionSimulator.Fork → Replay → ReplayAction → EvaluateRawExpansion → ParallelExpansionExecutor.Execute`。3377 个 PredictedCard 搜索样本中 3376 个来自 Fork。Fork 内 PredictedCard / AbstractModel[] / PredictedCard[] 三类合计占其采样权重约 75.3%。这为下一轮逐牌 wrapper 与牌堆数组研究提供依据；不是浅共享 wrapper 的安全证明。

整个搜索的 AbstractModel[] 权重约 415 MB，横跨 Fork、威胁投影与其他调用。它提示继续检查 listener 重建与复制，但不能恢复首轮已经在真实长搜退化的 slot 方案。ContinuationStamp 字符串构造也有明确栈，优先级低于 Fork；全部字符串/Builder 样本不能都归到 stamp。

独立 gcdump 在记录到 67 个 `SEARCH_WAVE_MEMORY` 日志时触发，报告堆内存 138,493,362 B / 953,244 个对象。对象计数包括 `PredictedCard` 56,542、preview storage 581、`SimulationSnapshot` 263、history 143。它是一次由采集触发 GC 后的存活图观察，不是分配累计量、RSS 峰值、搜索独占内存或 dominator retained bytes。报告的 Object Bytes 列也不能直接当作每类型总驻留量。未完成 root/dominator 归因，不能据此把整个对象图都归给历史、frontier 或缓存。

## Smart 层间内存实验

[独立实验补丁](../../tools/ExperimentalSmartSoftLimit/README.md) 在完成层且有下一层时，追加已分配字节软阈值。保留原预测/硬压力优先级，只使用原 Runtime 回收入口。以下每组一个新进程，移植后的新入口显式 exclusive；均固定 Necrobinder / 576 节点 / DOP4 / NoGC 4 GB，完整动作、评分、工作量和非时序剪枝仍相同。

| 软阈值 | 额外层间回收 | 搜索时间 | GC 总 / 最大观察暂停 | VmHWM |
| --- | ---: | ---: | ---: | ---: |
| 关闭（当前候选） | 0 | 4,742.9 ms | 0 / 0 ms | 2.823 GB |
| 512 MiB | 1 | 5,451.5 ms | 185.2 / 185.2 ms | 2.247 GB |
| 192 MiB | 2 | 5,050.0 ms | 152.2 / 84.7 ms | 1.989 GB |

三组 forced/start/end/restart/loss 分别为 `0/1/0/0/0`、`1/2/1/1/0`、`2/3/2/2/0`，触发层次符合设计。512 MiB 单样本峰值降低约 20.4%、耗时增加约 14.9%；192 MiB 降低约 29.5%、耗时增加约 6.5%。这是暂停/内存交换的单轮证据，不是稳定阈值排名；没有据此增加默认回收或提交用户设置。

本轮保留补丁与数据，生产接线已撤销。现有 signal 计数在请求准入时重置，跨请求复用 NoGC 区域要由 Runtime 提供真实 region epoch。软阈值还允许越过一整层，不能宣传为严格内存上限。要进入默认策略，仍需多状态、跨请求和可见 Steam 证据。

## 合入的 headless 并行设施

按用户要求从“小循环研究”的 `CombatSolver-generic-loop-planning` 工作区移植平台 helper、单项/矩阵入口与说明；来源是当时尚未提交的测试设施，不合入该任务的战斗、循环或评分变化。保留本分支已有 replay-state/Boss 参数。新入口默认读取当前 worktree 的 Release DLL，A/B 必须显式传冻结 build-dir，不能继续只替换源游戏 mods 里的 DLL。

每实例拥有私有完整游戏与 Mod 快照、数据、日志及协议；每用户主机租约支持显式 parallel、最多两个游戏，并保留 exclusive。暖进程继续占用名额。移植时修正 Linux 暖进程准入重复计算已兑现 RSS 的问题，与 Windows 的剩余预约口径一致，并复核等待期间本租约 token/进程出生身份。说明见 [Headless 实例与并行测试](../HEADLESS_TESTING.md)。

Linux helper 13 组租约/排队检查、4 组快照隔离及 9 组发布失败注入通过；两端脚本 AST/Bash 语法及结构门禁通过，Windows 游戏未执行。两次真实独立游戏建局/退出均 Passed，runId `3f8b0b3f6bcc47ca82528f8528ccb486` / `a97addbd1bf34400afe13f33342013bf`，请求存活区间重叠 23.47 秒。这只证明并行生命周期和建局，未请求额外 root 断言，也不用于单场性能对照。

## 后续优先级

后续较大的机会是减轻 Fork 的卡牌包装与引用数组复制。应先在真实调用点计数基础上设计稳定卡牌身份与分支可写值的分离，再选择小范围原型；已有对外可变引用、统一 remap context、历史与 listener 身份使通用 COW 不能直接替换当前存储。单凭独立内核的 bytes/Fork 不足以推动整套模拟器迁移。

本轮没有改搜索候选、排序、节点/时间预算、NoGC 硬预算或搜索 DOP。所有性能结论限于隔离 Linux headless 固定 fixture；Windows、正常可见 Steam 帧时间、完整战斗质量仍未验证。
