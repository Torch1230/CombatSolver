# Smart 层间软阈值实验

这是独立冷进程实验补丁，未接入生产。保持原 NoGC 硬预算、下一层预测安全检查、搜索预算及候选顺序；仅在原策略允许继续、仍有下一层且当前信号累计分配超过阈值时，额外做一次已有 Runtime 层间回收。硬压力/预测回收原因优先，最后一层不新增回收。软阈值只在层间观察，允许超过一层分配量，不是严格峰值上限。

`enable-512mib.patch` 阈值为 512 MiB；`enable-192mib.patch` 为较积极对照。一次只应用其中一个补丁。对照组不应用补丁。三组均固定 Necrobinder 公开药水 fixture、每 solver 576 节点、DOP4、NoGC 4 GB、60 秒搜索/120 秒请求，在初次结果处退出。完整参数见 [重现说明](../../docs/performance/gc-issue36-reproduce.md)，本轮结果见 [第二轮报告](../../docs/performance/gc-issue36-round2.md)。

在无其他修改的独立 worktree 根目录应用：

```sh
git apply tools/ExperimentalSmartSoftLimit/enable-512mib.patch
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
```

退出持有实验 DLL 的隔离游戏后，用 `git apply --reverse` 对同一补丁撤销接线。Windows 使用相同 Git/.NET 命令与原生 `.ps1` 测试入口。不要部署到别的任务的游戏目录。

`SearchMemoryPressureSignal.AllocatedBytes` 在请求准入和回收时重新计数，因此本补丁只适合每轮新建进程。生产的跨请求 NoGC 区域复用必须由 Runtime 注入真实 region epoch 与自该 epoch 起的分配数；不能把请求级计数或 `GC.GetTotalMemory(false)` 当成精确活对象量，也不能从硬 NoGC 预算扣减估算垃圾。冷进程结果不能证明跨请求行为。

本轮每组一个独占 headless 样本，关闭/512/192 MiB 分别触发 0/1/2 次层间回收。相同完整路线与工作量下，VmHWM 为 2.823/2.247/1.989 GB，搜索时间为 4.743/5.452/5.050 秒。降低内存会增加暂停与耗时；单次不足以决定默认阈值，生产已撤销接线。
