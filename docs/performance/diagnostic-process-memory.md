# 结果诊断进程内存读取

2026-10-22；源码基线 `5c773caaec742bc55588bc9ee21c361326a732f0`，manifest 0.50.1。

## 问题与范围

回合开始的检查点、路线复用与结果日志可在主线程调用 `SolverDiagnostics.DescribeResult`。其 `Process.WorkingSet64`／`PrivateMemorySize64` 在受测 Windows 环境进入 `NtProcessInfoHelper.GetProcessInfos`；同一边界多次格式化诊断会累积明显停顿。

Runtime 新增 `ProcessMemoryUsage`，Windows 通过一次 `K32GetProcessMemoryInfo` 捕获工作集与私有内存，`DescribeResult` 保持原字段名和成功时的数值含义；原生查询失败时两字段明确写 `unavailable`，不把缺失统计伪装成零。其他平台保留原 `Process` getter。没有改搜索、模拟、根捕获、预算、回收或执行政策，也未扩展到其他进程内存查询调用点。

## 本轮源码验证

现有 `DiagnosticLogTests` 直接编译生产辅助函数，最小入口无需游戏进程：

```sh
dotnet run --project tools/testing/checks/DiagnosticLogTests/DiagnosticLogTests.csproj -c Release -- --process-memory-only --process-memory-benchmark
```

省略 `--process-memory-benchmark` 只运行合同。Windows x64／.NET 9.0.3 上，原生结构大小、各字段偏移和精确计数映射通过；工作集与私有内存使用不同且超过 4 GiB 的确定值，其他原生计数使用干扰值，检查错位、误用字段与截断。两种无效句柄返回不可用，失败后新样本正常，128 次并发调用通过。顺序读取的进程内存会随 GC／工作集调整变化，因此删除首轮使用的固定 16 MiB 差值断言；测试不以机器耗时作为正确性阈值。

生产输出合同复用现有离线宿主，先构建本体，再直接调用构建后的 `SolverDiagnostics.DescribeResult`，无需启动游戏或初始化战斗：

```sh
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet run --project tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release -- --check-result-diagnostics
```

成功快照精确输出两个大整数；Windows 无效句柄得到的空快照使两字段输出 `unavailable`，而结果正文、回合摘要与动作尾部保持完整。屏蔽仅随采样变化的进程／GC 内存字段后，成功与失败日志逐字相同；自行采样的正式入口也通过本机读取检查。生产代码仅提取无状态的计数映射与接收快照的内部格式化重载，没有增加全局测试开关或可变查询钩子。

首轮本地准备在同一检查进程内，预热后先后各测 8 次（后续本轮只加强正确性合同，没有重测基准）：

| 查询 | 中位数 | 范围 |
| --- | --- | --- |
| 原 getter 对 | 188.77425 ms | 172.9886–202.9244 ms |
| 一次新快照 | 0.0003 ms | 0.0003–0.1986 ms |

新快照首个测量为 0.1986 ms，其余为 0.0003–0.0004 ms。这些是本机内存读取子路径数据，包含调用开销，不代表游戏渲染帧时间、完整诊断耗时或通用倍数。

`dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false` 通过，0 警告、0 错误。未将其当作实机验证。工具结构与 Runtime／Search 边界检查通过；文档长度门禁曾因测试入口新增正文超过 200 行失败，详细记录移入本页后重新检查。

## 既有热补丁实机证据

此前同一天的原版 0.50.1 DLL 采样中，四次回合开始的慢查询采样跨度约为 694／470／507／487 ms，与 GC 不重合。仅替换上述两个 getter 后，后续采样覆盖七次回合开始，未再出现主线程 `NtProcessInfoHelper.GetProcessInfos`，玩家反馈抽牌结束卡顿消失或明显减轻。

该次热补丁逐个替换 getter，而当前源码实现一次获取两个计数。它证明根因与修复方向，不冒充本次源码构建的可见复测。采样跨度不是精确函数计时或渲染帧时间，也不能推导所有 Windows 机器都有相同开销。

## 未验证范围

未验证非 Windows 运行、32 位原生布局及最终源码 DLL 的可见游戏行为；未做 FPS、完整战斗或路线质量结论。本轮仅准备本地 PR 源码与提交，构建显式关闭复制，未覆盖当前游戏中的求解器与热补丁。原始 trace、完整日志、游戏 DLL 和本机依赖路径不纳入提交。
