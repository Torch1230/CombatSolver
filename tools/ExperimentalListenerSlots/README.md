# 未采用：listener 槽位更新与延迟分页

候选经过 helper 和实际游戏 Fork/DOP 等价检查，但没有进入最终生产源码。576 节点大牌组分配减少约 15.5%；更长的 2500 节点交替 A/B（每侧三次，同为5000展开/19065转移）却从约 2.805 GB 增至 3.389 GB，暂停约3.007→3.375秒，耗时约13.67→14.16秒。因此保留完整可复现补丁作为反例，恢复原 listener 实现。具体记录见 [研究结论](../../docs/performance/gc-issue36-implementation.md)。

`ImmutableListenerList.cs` 是该实验唯一的容器实现，仍由 [helper检查](../HookListenerSnapshotChecks/README.md) 编译。`enable.patch` 包含 observer/slot、三种视图与游戏内测试的候选接线。补丁只适用于本研究分支，不能不经审查套到后续上游。

在独立、无其他修改的实验 worktree 中，从仓库根启用：

```bash
git apply tools/ExperimentalListenerSlots/enable.patch
cp tools/ExperimentalListenerSlots/ImmutableListenerList.cs src/Search/ImmutableListenerList.cs
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
```

先退出加载该 DLL 的隔离游戏，再撤销本补丁：

```bash
git apply --reverse tools/ExperimentalListenerSlots/enable.patch
rm src/Search/ImmutableListenerList.cs
```

不要把实验 DLL 复制到正在运行的可见安装。该代码不改变搜索评分，但扩大缓存状态与表示的验证面；短搜分配优势不能覆盖长搜反例。完整重建、preview 密集更新和多分支 Fork 的净成本须先证明，才考虑下一版批量脏更新设计。
