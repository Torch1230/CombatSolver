# No-GC 区域准入：只在区域值得进入时才建立（2026-09-17）

## 结论

0.40.2 的玩家问题包显示：搜索在内存紧张的机器上会稳定落入「建立 No-GC 区域 → 搜索耗尽它 → 检查点拆除区域并强制回收 → 重建区域」的循环。本轮在准入处加了一道判定：**当系统余量把区域压到配置预算的一半以下时，不再假装预留成立，直接改用默认 GC 跑完本次搜索。**

改动只落在 `src/Runtime/SearchGcPolicy.cs`（职责边界不变），并为 `tools/CombatSolver.GcPolicyChecks` 增加 6 项检查。

## 根因：准入没有下限，而且降级路径必然进入

问题包（`0.40.2-TEST_SUBJECT_BOSS`）的关键行：

```
GC_ALLOCATION_CAPACITY physical_load=26523082752 system_limit=29228695790 effective_budget=2967362558 capped=true
GC_SEARCH_ALLOCATION_LIMIT limit=401838160 configured_budget=12000000000 ...
```

配置 12 GiB，实际拿到 2 967 362 558 字节（24.75%）。这条路径的判定链是：

```csharp
EffectiveNoGcRegionBudget effectiveBudget = ResolveEffectiveNoGcRegionBudget(...);   // headroom 钳制 → Capped = true
NoGcRegionStartOutcome startOutcome = effectiveBudget.CanStart
    ? TryStartNoGcRegionWithSizeFallback(ref effectiveBudget, restartRequested)      // 还可以继续对半砍
    : NoGcRegionStartOutcome.SystemHeadroomInsufficient;
_noGcRegionActive = startOutcome == NoGcRegionStartOutcome.Started;                  // 只要 Started 就进
```

两个缺陷叠加：

1. **`TryStartNoGcRegionWithSizeFallback` 最多把预算对半砍 12 次**，下限是 `MinimumNoGcRegionBudgetBytes = 512 MiB`，并对每次成功置 `Capped = true`。也就是说区域小到 512 MiB 也会被建立。
2. **准入只是一个布尔量** `enableNoGcRegion`，没有任何「这台机器实际能给出多少」的下限判定。

而 `UseDefaultGcFallback` 的概念**已经存在**，但只在**重启失败之后**才被调用（`restartOutcome` 为 `SystemHeadroomInsufficient` / `InsufficientMemory` 时）。恢复路径里作者也留下了同源注释：

> Keep the recovered reservation ceiling for this scope. Immediately growing back to the original request recreates the pressure episode.

即「不要立刻长回原始请求，否则会重现压力事件」这一判断已经存在。本轮只是把它**延伸到首次准入**。

## 循环的代价（问题包实测）

| 指标 | 值 |
|---|---|
| `SEARCH_MEMORY_CHECKPOINT` | 33 次（28 次在 `after_serial_parent`）|
| `HEAP_RECLAIM reason=in_search_memory_checkpoint` | 362 次 |
| 单次回收耗时 | 中位 1296 ms，p90 2355 ms，最大 5152 ms |
| 单次回收实得工作集 | 中位 **0 MiB**，最大 1384 MiB |
| GC 暂停合计 | 83.3 s |
| 回收累计 | 578.6 s |
| 末 97 s 推进 | 6173 节点 = 63.6 节点/秒 |
| 导出 | `current-route.txt` = 当前没有已完成的求解路线 |

每次检查点的动作序列是 `EndNoGcRegion()`（阻塞 GC）+ 同步等待强制后台 Gen2 + 重进区域（再一次 GC），搜索线程全程 park。同时**全部 409 次回收都是非紧凑的**（`compacting: false`）——代码里存在紧凑路径 `full_blocking_compacting`，但只挂在玩家手动的「释放内存」上，in-search 自动检查点从不使用。在一个 52% 碎片的堆上做非紧凑回收，回收量中位为 0 是必然的。

也就是说：**这条循环不可能收敛**，它的成本随运行时间线性累积，而收益为零。

## 改动

新增常量与谓词（`src/Runtime/SearchGcPolicy.cs`）：

```csharp
private const int MinimumNoGcRegionBudgetPercent = 50;

internal static bool IsNoGcRegionBudgetWorthEntering(
    long configuredBudgetBytes, long achievedBudgetBytes)
    => achievedBudgetBytes
        >= Math.Max(
            MinimumNoGcRegionBudgetBytes,
            configuredBudgetBytes / 100 * MinimumNoGcRegionBudgetPercent);
```

两处调用点（首次准入、检查点重启）：

```csharp
bool headroomLimitedBudget = effectiveBudget.Capped;   // 必须在尺寸回退循环之前捕获
... TryStartNoGcRegionWithSizeFallback(ref effectiveBudget, ...) ...
if (startOutcome == NoGcRegionStartOutcome.Started
    && headroomLimitedBudget
    && !IsNoGcRegionBudgetWorthEntering(noGcRegionBudgetBytes, effectiveBudget.TotalBytes))
{
    EndNoGcRegion();
    Entry.Logger.Info($"[CombatSolver/Test] GC_NO_GC_REGION_DECLINED ...");
    startOutcome = NoGcRegionStartOutcome.SystemHeadroomInsufficient;
}
```

设计要点：

- **只对 headroom 造成的缩水生效。** `Capped` 在 `ResolveEffectiveNoGcRegionBudget` 里等价于 `effectiveBudget < configuredBudgetBytes`，即「机器给不出你要的」。而尺寸回退循环是为**平台 SOH 预留上限**（macOS/regions GC 上限不可查询）准备的合法机制，它设置的 `Capped` 发生在捕获之后。因此在循环**之前**捕获该标志，平台上限缩水的区域照旧建立 —— macOS 玩家不会因为这条判定失去 NoGC。
- **判定尺度无关。** 用「配置预算的百分比」而不是绝对下限：小型机器配置 2 GiB、拿到 1.8 GiB 仍然进入。
- **拒绝即停摆检查点。** 拒绝后走既有 `else` 分支 → `UseDefaultGcFallback(true, allowNoGcRecovery: true)` → `DisableLimits()` 把 `_allocationLimitBytes` 置为 `long.MaxValue`。**分配限额被释放，检查点在构造上不可能再触发**，回收与区域拆建一并消失。同时 `systemHeadroomConstrained: true` 会要求保守并发，`allowNoGcRecovery: true` 保留 3 次有界恢复探测：内存真的释放出来后 NoGC 会回来。
- **改动是减法。** 没有新增状态机、没有新增线程或计数器；复用既有 outcome 与既有回退路径。

## 验证

`tools/CombatSolver.GcPolicyChecks` 直接编译生产源码（含本次修改的 `SearchGcPolicy.cs`），可无头运行。新增 `GcRegionAdmissionChecks`（6 项）：

```
GC_REGION_ADMISSION_OK declined the reported 12GiB-to-2.97GiB region;
partial caps, small-machine budgets and the inclusive threshold passed.
```

| 场景 | 断言 |
|---|---|
| 问题包数值 12 GiB → 2 967 362 558 | 拒绝进入 |
| 12 GiB → 8 GiB | 仍然进入（部分缩水不等于失效）|
| 2 GiB → 1800 MiB | 仍然进入（尺度无关）|
| 4 GiB → 300 MiB | 拒绝（低于可启动下限）|
| 8 GiB → 4 GiB | 进入（阈值含等号）|
| 拒绝后信号状态 | `!IsLimitReached() && RemainingBytes == long.MaxValue`，检查点无法触发 |

全套复跑无回归：

| 模式 | 结果 |
|---|---|
| base（无参数） | 26 项通过（原 20 项 + 新增 6 项）|
| `scopes` | 8 项通过 |
| `recovery` | 6 项通过 |
| `recovery-lifecycle` | 2 项通过 |
| `memory` | 1 项通过 |
| `parallelism` | 15 项通过 |

Release 构建 0 警告、0 错误。

## 未验证 / 不宣称

- **没有实机内存压力下的端到端 A/B。** 本机内存充裕，`Capped` 不会为真，因此拒绝分支在真实运行中未被触发过。收益量级由问题包的循环计数（33 次检查点、362 次回收、578.6 s 回收、83.3 s GC 暂停）与代码路径推断得出，**不是实测加速比**。
- **没有测帧时间代价。** 改用默认 GC 意味着搜索的回收不再被区域挡在游戏之外，理论上可能出现更多可见停顿；反过来搜索不再被 park、运行时间大幅缩短。方向相反，需实机权衡。
- **阈值 50% 是工程判据而非实测最优值。** 依据是问题包 24.75% 的实测比值与「区域必须能吸收本次搜索相当一部分分配」的定性要求；常量独立可调。
- 未启动可见 Steam。未提升版本、未发包、未推送。
