# 未采用：普通 GC 自适应并发

纯决策控制器与真实 wave 接线作为研究原型保存，未编入生产。它只在普通 GC、非详细诊断、非增量回放时启用；NoGC 使用生产中的余量准入策略。

每个窗口至少 100 ms / 128 转移，只比较填满目标宽度的波次。连续高 GC duty 或系统内存压力触发降核探测；探测要求吞吐收益或接近原吞吐并减少压力，否则回退。恢复升核、指数冷却、bytes/transition 复杂度变化与取消均有合成检查。降到单 parent 时同时停用该 parent 内隐藏的 action/choice 并行，避免名义降核但实际仍满核。

实际接线只测量已完成 wave 的相同起止区间，排除零工作、无效系统内存信息、取消/异常和自然不足宽度。`GC_PARALLELISM_SUMMARY` 每 solver 在 finally 输出样本/完整窗口/探测/中断计数，不能只凭没有决策日志认定运行过有效采样。`UsedParallelism` 表示派发 parent 数，并非同时执行线程的测量；bytes/transition 相近也不足以完全消除跨深度 CPU 工作变化。

两个固定公开 fixture 各一次真实 headless 搜索：大牌组每 solver2500节点，共55完整窗口，没有触发降核；药水每 solver576节点，共35窗口，最后层触发4→2，观测吞吐比0.638、GC duty约0.258→0.146，控制器拒绝该探测并恢复4。所有动作、评分、工作量、非时序剪枝与同代码未启用对照一致。整请求耗时分别相对同代码对照+6.01%（对照3轮中位数）和+1.72%（对照1轮）。这些单轮不足以证明稳定性能变化，也没有支持默认启用的收益。

[机器证据](../../docs/performance/gc-issue36-results.json) 保存逐轮数值、runId与窗口计数；[重现说明](../../docs/performance/gc-issue36-reproduce.md) 给出输入。没有 Windows/可见 Steam 性能证据。

独立、无其他修改的实验 worktree 中从仓库根启用：

```bash
cp tools/ExperimentalAdaptiveGc/SearchParallelismController.cs src/Search/SearchParallelismController.cs
git apply tools/ExperimentalAdaptiveGc/enable.patch
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
```

PowerShell使用 `Copy-Item` 替代 `cp`，其余命令相同。先退出持有实验DLL的隔离游戏，再用 `git apply --reverse tools/ExperimentalAdaptiveGc/enable.patch` 撤销接线，并删除复制的 `src/Search/SearchParallelismController.cs`。不要部署到另一任务的安装。

15项纯决策检查直接编译此处唯一控制器源码：

```bash
dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- parallelism
```
