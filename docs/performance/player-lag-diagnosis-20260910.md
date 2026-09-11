# 2026-09-10 玩家实录：SpeedX 分配热点与内存转储挂起

## 结论范围

本轮依据进程 `29876` 的已有记录诊断，没有再次启动游戏、触发内存快照或修改第三方 Mod。确认一个持续制造大量垃圾的具体缺陷：**皮皮极速 SpeedX 0.11.11 在跑局中显示悬浮面板时，每帧探测未安装的 Rewind；失败重试又触发 JmcModLib 的程序集扫描。** 这条路径是非搜索阶段的主要分配来源。

GC 暂停随跑局推进明显变长，旧战斗对象长期存活也已观察到。无有效 heap dump，尚不能确认是谁持有这些旧对象，也不能认定 SpeedX 单独解释全部跨跑局退化。没有同等级证据将本次主要热点归到 QuickSL 或 MintySpire2。

Ctrl＋F8 后的持续挂起发生在诊断内存转储阶段。之前的微型进程转储测试不足以覆盖这次长期运行的游戏环境，现已将本机 `DumpToolPath` 置空，停止启用该快捷键。

## 证据位置及完整性

- 玩家会话：`outputs/performance-sessions/player/20260910-072056-29876/`。
- 派生分析：`outputs/performance-diagnosis-20260910/`；保留 `trace2-attribution.json`、`trace3-partial.json`、`timeline-summary.json`、相关类型反编译及独立分析器源码。
- 第一、二段已正常封口并无损压缩；第二段转换 ETLX 成功，EventsLost=0，115720 个唯一采样/分配调用栈。
- 第三段未完整封口，标准 topN 报 `Read past end of stream`。流式解析保留已成功读出的事件，最后时间为 15:32:07.821；明确标记为部分数据，不当作完整 trace。
- `timeline.jsonl` 有 645 条进程样本、645 个帧窗口、3913 条求解器事件、174 次上下文变化；写队列 dropped=0。进程挂起后没有正常 writer_end，不能称作正常收尾。

## SpeedX 的具体缺陷

实际已加载版本：SpeedX manifest 0.11.11、QuickSL 1.8.0、BaseLib 3.4.5、RitsuLib 0.5.20；程序集清单没有 Rewind。不要用 DLL 统一的 AssemblyVersion 1.0.0 替代 SpeedX manifest 版本。

反编译实际安装 DLL 后，调用链与轨迹一致：

```text
SpeedX.Scripts.TurboOverlay._Process
  -> UpdateRewindStatsText
  -> RewindStatsBridge.TryGetOverlayState / ResolveIfNeeded
  -> Type.GetType("Rewind.Scripts.RunStatsTracker, Rewind")
     Type.GetType("Rewind.Scripts.RewindStats, Rewind")
     Type.GetType("Rewind.Scripts.RewindButton, Rewind")
  -> JmcModLib.Bootstrap.BootstrapDependencyResolver.ResolveAssembly
  -> FindLoadedAssembly / AppDomain.GetAssemblies / Assembly.GetName
```

`ResolveIfNeeded` 仅在成功得到委托后停止探测。当前 Rewind 缺失，三个查找每帧重新发生；JmcModLib 通过 AssemblyLoadContext.Resolving 和 AppDomain.AssemblyResolve 重复扫描已加载程序集。大量字符串、AssemblyName、Version、字节数组在这条链上创建。

15:29:00—15:30:00 的两个完整 30 秒窗口：

| 分配抽样归因 | 约 MB |
| --- | ---: |
| 全进程 | 2919.4 |
| 调用栈含 SpeedX | 2695.8 |
| 调用栈含 CombatSolver | 49.1 |
| 调用栈含 MintySpire2 | 0.3 |

这段没有搜索；SpeedX 约占 92.3%，约 44.9 MB/s。数字来自 GCAllocationTick 的抽样分配金额，不是精确逐对象记账，也不是 CPU 时间占比。整个第二段中，该链在搜索和空闲窗口均持续出现。

`TurboOverlay._Process` 仅在 `OverlayVisible && IsInRun()` 时调用此统计刷新。关闭 SpeedX 悬浮显示从源码上能避开这条路径，不要求关闭加速本身；本轮未做关闭前后实机 A/B，不将其写成整局卡顿已经解决。

## 卡顿怎样随跑局加重

以下均取自按键前的进程样本，且对应窗口没有搜索：

| 窗口 | 观测时长 | 托管分配速率 | 累计 GC 暂停 | 样本中最近一次 GC 暂停中位数 |
| --- | ---: | ---: | ---: | ---: |
| 前期商店附近 | 44.50 s | 62.2 MB/s | 0.383 s | 1.354 ms |
| 15:29—15:30 非战斗阶段 | 58.68 s | 48.4 MB/s | 14.497 s | 36.397 ms |
| 最后休息处 | 6.424 s | 40.1 MB/s | 2.572 s | 87.117 ms |

中位数是每秒采到的“最近一次 GC 暂停”，不是所有 GC 事件的精确分位数。分配速率没有随进度增长，回收成本却大幅上升；持续分配与更贵的回收共同造成长帧。

末尾托管估计存活量约 1.615 GB、进程工作集约 4.52 GB、私有字节约 8.73 GB；物理可用内存仍约 10.60 GB，不能解释成物理内存耗尽。托管堆前期约 0.26 GB，后期明显增大。10 个见过的战斗对象仍存活，最早一个经历 81 次 Gen2；这是需继续追根的保留现象，弱引用本身不证明持有者。

Godot 资源数量约 7100—7200，节点大致 1.3—1.5 万，没有与卡顿同步的数量级暴涨。当前数据不支持直接把主要问题说成贴图或节点无限累积。也不能凭这些计数排除全部原生资源问题。

此前发现的 OnlinePresence 保留上一份 SolverResult 是独立已确认的强引用路径；本次末尾仅见一份结果存活，不能用这一条路径直接解释所有旧战斗对象或全部堆增长。

## Ctrl＋F8 后的挂起

- 15:32:06.246：玩家标记；位于 TotalFloor=32 的 RestSiteRoom，未搜索、未部署，GC 模式 Interactive。
- 15:32:07.152：最后一条后台进程样本。
- 15:32:07.821：第三段中最后可解析事件；此后游戏内线程采样和日志均停止推进。
- 转储工具已启动，`heap-1789025526246.dmp` 始终为 0 字节，没有有效内存快照可分析。
- 15:33:31 左右：游戏进程退出，trace 结束但尾部不完整。
- 15:34:03：采集器报告 `Memory snapshot did not finalize after game exit`。

时间线明确把本次整进程挂起定位到诊断转储操作附近，而原有 GC 卡顿在此前已存在。由于转储未生成、trace 尾部截断，不能进一步声称已确定 CLR 内部哪把锁或哪条线程形成死锁。诊断快捷键已停用，现有数据保留。

## 后续进程 26156：首回合发牌暂停

本节使用新的 `outputs/performance-sessions/player/20260910-080450-26156/`，不沿用上一进程的归因比例。派生数据位于 `outputs/first-draw-diagnosis-20260910/`。第三段正常解析；第四段尾部截断，恢复为部分 ETLX，仅分析成功读取区间，并用同进程第三段的精确代码地址补充符号，不将采集器 complete 字样当作文件完整的证明。

- 16:22:04.229—16:22:06.305：进入房间耗时 2076 ms；16:22:06.380 记录主线程帧间隔 1858 ms。进程样本最近一次 GC 暂停为 1763 ms。
- 16:22:09.166 才捕获战斗根（3.389 ms），16:22:09.169 启动搜索，16:22:09.175 进入 NoGCRegion。因此不能把此前发牌暂停归为本次搜索计算。
- 16:22:04.220 的 HeapStats 记录 5,563,233 个 GC 句柄及约 1.43 GB Gen2；句柄是全进程总数，不能将全部数量精确归属某个类型。回收暂停集中在实际 GC 执行，线程停止协调只有微秒量级。
- 第三段弱登记相关分配抽样中，RichTextSine 对应约 42.74 MB 字典节点、25.37 MB WeakReference；RichTextFlyIn 对应约 37.65 MB 字典节点、24.41 MB WeakReference。Godot 的 Env getter 每次创建拥有原生引用的 Dictionary 包装，并登记 WeakReference；原版这两个回调未 Dispose，释放依赖终结线程。明确释放会同时撤销登记并抑制终结。

修复只补齐这两个原版文字特效回调所拥有的临时包装生命周期，保留原算法及第三方 Mod 行为。正常可见 Steam 合同确认原版/修复版输出一致，4 万次回调后的登记数不增长。这个验证证明该路径不再堆积待释放登记；尚未重跑玩家两层进度后的同等长会话，不宣称全部长期卡顿消失或给出整体 FPS 提升比例。

## 后续进程 21752：残杀千足虫战斗主动回收过密

来源为 `outputs/performance-sessions/player/20260910-090025-21752/`，DECIMILLIPEDE_ELITE 上下文区间为 UTC 毫秒 1789031584568—1789031719221。第三段 trace 完整解析，EventsLost=0；目标区间内记录 36 次搜索内主动回收，以及 102 个 SuspendForGC 暂停区间（总计约 6244.8 ms，最大约 861.8 ms）。派生材料保留在 `outputs/centipede-gc-20260910/`。

首次区域容量约 8.05 GB，扣除原大对象份额与安全余量后分配限额约 5.37 GB。首次后台回收将托管存活量从约 5.71 GB 降到 1.52 GB，但进程工作集仍约 8.02 GB。之后 HeapSize=5,885,806,336、Fragmented=4,364,757,576，说明释放空间仍在堆内；旧公式只计系统上限减当前负载，区域缩水到 2.94 GB，分配限额只剩 1.96 GB。

后续一次约 1.86 GB 分配期间，真实物理用量约 19.08 GB，进程工作集约 8.02 GB，几乎没增长；旧信号却把它全部加到起始内存负载，预测值涨到约 20.77 GB。面板进程上限约 10.79 GB，显示余量与搜索限额不是同一口径。是自身的重复记账与预留缩减造成过早回收，不能归责于此时系统已经用满。

0.35.1 将可复用堆空间计入分配准入，并在 Windows 使用实时物理压力；已有安全比例、配置预算及 CLR 区域申请规则继续生效。独立生产代码合同验证上述数值和上限边界，尚未重跑这场实际战斗，不给出修复后的回收频率。
