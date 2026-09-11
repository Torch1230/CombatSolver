# 跨跑局卡顿：本地进程性能录制

这是用于复现长期卡顿的 Windows 诊断构建。启用文件是游戏实际加载的 CombatSolver DLL 同目录下的 `performance-recording.json`；没有该文件时不启动录制器、额外线程、采集进程或生命周期补丁。此次保持搜索、GC 政策和其他 Mod 的行为；尚未据此确认快速 SL、薄荷尖塔或其他 Mod 是原因。

## 玩家这两局怎么打

1. 正常从 Steam 启动，看到左上角绿色“性能录制中（状态日志＋调用栈）”再开始。保持平常的 Mod 与求解器设置，按平常方式使用 SL。
2. 尽量在同一个游戏进程内连续打两局；第一局结束后在主菜单停留约 15 秒，再开第二局，以便记录空闲和新建跑局的差别。
3. **当前停止启用 Ctrl＋F8 内存转储，配置中的 DumpToolPath 留空。** 2026-09-10 的玩家长局中，转储未完成并伴随整进程挂起；微型进程测试不能覆盖该场景。先使用已有的连续轨迹和状态日志诊断，事件经过见 [玩家实录诊断](player-lag-diagnosis-20260910.md)。
4. 最后正常退出整个游戏，等采集器收尾。保留本次进程目录中的全部文件；只给普通 `godot.log` 会缺少调用栈和内存证据。红色提示表示记录失败；黄色常见于启动或分段切换，持续黄色表示录制中断。

内存快照可包含该进程当时的完整数据，用于本地对象引用分析。正在录制的段仍使用原始 trace；段完成后，由单独的低优先级进程无损压缩为 `.nettrace.zip`，ZIP 完整落盘后移除同段冗余原文件。压缩与下一段录制并行，所有段的事件均保留。实际玩家样本 280,502,570 字节压为 26,455,804 字节，约缩小 90.6%；不能保证所有场景都有相同比例。采集器每两秒检查磁盘，剩余不足 5 GB 时明确停止。移除启用文件并重启游戏即可关闭诊断。

## 配置与产物

配置字段：`TraceToolPath` 指向 dotnet-trace 9.0.661903，`DumpToolPath` 保持 null，`WatcherScriptPath` 指向本仓库 `tools/watch-performance.ps1`，`OutputDirectory` 是本地输出目录，`SourceRevision` 记录源码身份。`SegmentSeconds` 正常使用 300，`HandleWindowSeconds` 默认 10（允许 0—30；实际不超过周期的一半）。单采集器交替录制 10 秒句柄窗口和 290 秒普通段，约每五分钟一次句柄窗口；设 0 恢复普通分段。每段的实际模式、provider 和时长写在 collector.jsonl，不按文件编号猜测采集范围。启动记录另外保存实际 DLL SHA-256，避免把诊断 DLL 和同版本正式 DLL 混淆。

每个进程目录包含：

| 文件 | 内容 |
| --- | --- |
| `timeline.jsonl` | 进程全程状态、搜索及部署原始事件、帧时间直方图、长帧、生命周期耗时、程序集及 Harmony 补丁清单 |
| `collector.jsonl` | 采集开始、每段起止、文件增长、采集器自身 CPU/内存、磁盘余量、内存快照起止、失败或完成 |
| `trace-*.nettrace.zip` | 完整保留同段原始 trace；解压后读取托管线程采样、GC、分配抽样、异常、锁竞争、线程池与符号。当前录制中的段暂存为 `.nettrace` |
| `*.compression.*` | 压缩起止、原始/压缩字节数与工具输出；失败保留原始 trace，collector 同时记录压缩退出码 |
| `trace-*.stdout/stderr.txt` | 工具实际启用的 providers、收尾、错误等原始输出 |
| `heap-*.dmp` | 用户 Ctrl＋F8 明确触发的 Heap dump，用于 dumpheap/gcroot；不主动强制 GC |
| `collector-health.json` | 状态提示的数据源；进程退出且段收尾完成后为 `complete` |
| `*FAILED.txt` | 记录器、采集器或内存快照失败；不能把这些会话报告成完整记录 |
| `godot.log` | 正常收尾时复制的游戏主日志 |

`tools/export-performance.ps1 -SessionDirectory <目录>` 在采集和压缩收尾完成后创建相邻 ZIP。会话目录保留。异常退出、工具报错时保留已有目录供分析，不伪造正常完成标记。

## 采集范围与口径

- 主线程：每帧心跳；每秒帧数、时间分布与最大帧；超过 100 ms 的帧单独记时；求解器 dispatcher 耗时/分配；主线程总分配。上下文每秒采样，瞬时部署以原始 `DEPLOY_START/ACTION/FINISH` 事件为准。
- 场景：跑局、战斗、房间类型、楼层、遭遇、搜索/部署/全自动、窗口焦点、暂停、限帧和时间倍率。RunManager 的新建、读取、进入房间/章节、退出房间、胜利、放弃、清理及淡入淡出有配对跨度；异步跨度覆盖整个 Task，而不是仅计到第一个 await。
- 内存：进程工作集、私有字节、峰值；系统可用物理内存/提交额度；累计缺页（含软缺页，不能直接称为磁盘换页）；进程 I/O；托管已分配量、堆大小、碎片、已提交量、各代回收次数、暂停、固定对象与终结队列、GC 模式。
- 包装登记：每秒从线程安全的 GodotObjectInstances/OtherInstances 读取数量，写入 `wrapperRegistry`，不枚举或持有弱引用目标；未提供探针的独立测试进程记录 null。登记数量不等于 GC 句柄总数。
- 句柄窗口：保留 GCHandle 创建/销毁事件，结合分配抽样与可用事件栈追查来源。窗口间的事件没有采集，句柄 ID 会重用，不能用跨窗差值当作全程泄漏量。并非每条句柄事件都有可解析栈；分析必须给出实际栈覆盖率。
- CPU/线程：进程用户态/内核态累计 CPU；每五秒各 OS 线程 CPU 和等待状态；线程池排队/完成、活动 Timer、句柄数。主线程原生 ID 写在启动记录中。
- 引擎：Godot 提供的节点、孤立节点、资源、纹理/显存、绘制调用、物理及管线编译等 monitors。发行版不提供的 monitor 可能是 0，不能据此断言对应占用为零。
- 引用：仅用弱引用追踪见过的跑局、战斗、结果，记录存活时间与经历的 Gen2 数。最多 256 个槽，容量淘汰数量显式记录。存活不是泄漏定论；内存 dump 才能追到具体 GC root。记录器本身不强持有这些游戏对象。
- Mod：程序集版本/路径与 Harmony 原方法、补丁方法、owner、优先级每分钟取一次。调用栈与这份清单用于归属，安装某 Mod 或它出现在栈中本身不证明它导致卡顿。

采样线程时间包含等待，**不等于 CPU 占比**；与线程 CPU 增量、帧和锁竞争联合判断。此配置不是 Windows 内核调度/GPU 驱动跟踪，纯原生热点不保证有完整符号。分段退出/重新附加有空窗，采集器记录段起止，分析时必须报告覆盖空窗和 EventPipe 丢事件数。诊断有自身成本：主线程捕获、清单捕获、后台采样耗时及采集器 CPU/内存均留证，不能用这次带诊断的跑局直接宣称正式版帧率。

GC 发起者要读取 `GCTriggered` 的关联栈，不能因为 `GCStart` 没有栈就认定未采集。EventPipe 将关联栈保存在 stack blocks；独立 `ClrStackWalk` 事件数为零不等于没有栈。`PerformanceRecordingTests trace-stacks <nettrace 或 etlx>` 验证关联栈并输出触发时间和来源；`trace-handles <nettrace>` 验证创建/销毁事件。关键词依据 [Microsoft GC Handles 说明](https://devblogs.microsoft.com/dotnet/?p=26461) 和 [官方 TraceEvent 解析器](https://github.com/microsoft/perfview/blob/main/src/TraceEvent/Parsers/ClrTraceEventParser.cs.base)。Stack 位持续开启，GCHandle 位仅在窗口开启；这不是堆转储，不能承诺完整强引用根链。

Collector 的 complete 仅表示采集工具退出、文件与压缩收尾，`traceIntegrity=requires_parser_validation` 明确保留完整性验证。文件是否截断、是否丢事件仍须实际解析，尤其是进程退出时的最后一段。

## 所有权与故障处理

进程级写入器及采集器跨 UI 节点重建继续工作，节点重新接入同一会话；根视口或进程退出才收尾。每秒刷新文件，主线程阻塞时后台仍记录心跳年龄与进程指标。写队列有界，溢出计数和写入失败会变成明确红色状态，不阻塞游戏线程，也不悄悄当成成功。生命周期补丁只记录时间和 Task 状态，不捕获游戏参数，不改变返回值或异常。

验证证据与局限见 [测试矩阵](../TEST_MATRIX.md)。本轮工具验证不等于已复现两局后的卡顿。
