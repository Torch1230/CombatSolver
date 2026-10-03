# 搜索无进展内存截断与排他分配（2026-09-19）

本记录覆盖两项改动和一次受控端到端验证：

1. `MemoryNoProgressRecoveryLimit`（默认 0=关闭）：搜索内每次成功重建 No-GC 区域的回收若只拿回 `< 1 MiB`，记为一次无进展；连续达到上限时该成员提前收手，设 `SearchBoundaryReason.MemoryNoProgress`，由既有收尾发布当前前沿 incumbent，组合器标记 `MemoryTruncated` 且不参与成员间整条选优。
2. 阶段指标改为排他归属，并据此做一处可独占归因的分配削减：不再在每个回合边界为整条 frontier 预拼 `ContinuationStamp`，改为最终路径构造 `CachedContinuation` 时按需 `Replay`。

## 受控内存压力端到端验证

离线宿主新增测试专用入口：

- `--enable-no-gc-region`：让 Runtime 的 `SearchGcPolicy` 真正生效；此前离线宿主硬编码关闭 No-GC。
- `--no-gc-region-budget-gigabytes <double>`：No-GC 区域预算；受生产设置下限约束（≥ 1 GB）。
- `--signal-ballast-mb <int>`：进入 No-GC scope 后先持有一段活对象；它只制造“回收腾不出余量”，不会释放或伪造统计。
- `--memory-no-progress-limit <int>`：待验证的截断上限。

测试根为默认 `FUZZY_WURM_CRAWLER_WEAK`、Beam 135、5000 节点、预算 30 秒。No-GC 预算 1 GB，压力为进入 scope 后持有 600 MiB 活字节。`EnterSearchScope` 先配置真实分配限额，随后的第一次真实强制回收因为这段活压力而拿回不足 1 MiB。

| 场景 | 命令要点 | 结果 |
|---|---|---|
| 截断 | `--memory-no-progress-limit 1 --enable-no-gc-region --signal-ballast-mb 600` | `boundary=MemoryNoProgress`、`expanded=1`、`route_len=2`；路线首个动作为可执行 `DEFEND_IRONCLAD`，末动作为 `EndTurn` |
| 默认关闭对照 | 同上但 `--memory-no-progress-limit 0` | `boundary=None`、`expanded=599`；路线与未开 No-GC/未加球压的原始 599 节点基线逐字节相同 |
| 组合成员标记 | `--search-mode Coordinator --use-portfolio --memory-no-progress-limit 1` | 成员 0 `Termination=MemoryNoProgress`、`Compared=false`、`SkippedReason=MemoryTruncated`；选中回退结果仍产出上述 2 步路线 |

本地结构化产物位于 `.local/learned-selector/memory-e2e/final/`（截断/off/组合三个目录）。该压力是受控注入而不是原始 VeryHigh 问题包复现：球压保证第一次真实回收的无进展状态可重复；`expanded=1` 只证明“截断后仍能发布可执行 incumbent”，不证明正常搜索质量。

## 阶段指标的排他归属

`SearchPerformanceMetrics` 原先分别记录每段 `Begin/End` 的 inclusive 增量；`Action` 包含 `CardExecution`、`RoundAdvance`，`Fingerprint` 包含 `ProjectedShuffle` / pile 子段，所以各项相加大于总分配，也无法判断一段分配属于谁。现在 `Begin` 压入测量帧，`End` 从父帧扣除已结束子帧的时间与分配，输出真正的排他增量；调用顺序不是严格后进先出时直接抛错，而不是输出错账。

以 `sel-defect-elite-02`（Beam 24 组合、选中成员 851 节点）为例：

| 阶段 | 原 inclusive | 排他 |
|---|---:|---:|
| action | 79,713,352 B | 728,776 B |
| round | 36,134,584 B | 274,048 B |
| snapshot | 21,423,464 B | 11,135,736 B |
| fingerprint | 6,179,464 B | 447,600 B |
| card_exec | —（原在 action 内） | 42,706,480 B |
| fork | 36,538,864 B | 36,538,864 B |
| prune | 27,786,184 B | 4,595,928 B |
| final | 1,086,808 B | 204,368 B |

排他阶段合计约 142.5 MB，而同一成员 `allocated_bytes=168.8 MB`；差额约 26.3 MB 不属于任何已测阶段。EventPipe 分配采样在该根上的 `Other` 最高调用栈是 `ContinuationStamp.CapturePredicted -> StringBuilder.ToString`，前四条相关栈估计约 23.2 MB（采样估算，不是精确计数器）。这给出了下一步削减目标：续用戳是纯值元数据，只在最终选中路线构造 `CachedContinuation` 时消费，搜索过程中不会回读。

## 续用戳延迟捕获：分配与等价

实现只删除回合边界上的 eager `CaptureContinuation(frontier)`，并让 `BuildContinuations` 对选中路径按完整动作前缀 `Replay` 重建戳记；不再需要快照上的 `Continuation` 缓存。搜索的决策输入、Beam 排名、状态键和续用比较文本均未改变。

### 固定根 ABBA（`sel-defect-elite-02`、Beam 16、`Evaluate`、`--dop 1`）

四进程顺序 B-C-C-B，同一请求与预算：

| 轮次 | 墙钟 ms | selected worker 分配 B |
|---|---:|---:|
| B | 3304.2 | 279,623,440 |
| C | 3250.6 | 270,820,456 |
| C | 3241.3 | 270,816,712 |
| B | 3270.8 | 279,644,288 |

候选平均少 8.8 MB（−3.15%）分配，墙钟平均少约 41.5 ms（−1.26%）；每对的实际路线、`cachedContinuations` 每个字段、除 `replayCount` 外的全部剪枝计数逐项相同。`replayCount` 从 2 增至 5，增加量正是最终路径重建续用戳所需的额外 Replay，不参与决策。

### 组合路径（`sel-defect-elite-02`、Beam 24、`Coordinator --use-portfolio`）

同一根单进程对照（同源码、同请求、同 seed）：

- 6 个组合成员的 `ExpandedNodes` / `TransitionCount` / `BattleHpLost` 全部相同。
- 选中路线动作与 6 条 `cachedContinuations` 全部相同。
- selected member `WorkerAllocatedBytes` 168,840,616 → 163,515,064；请求 `TotalWorkerAllocatedBytes` 2,950,179,064 → 2,842,728,888（−3.64%）。
- 该次墙钟 13.42 s → 12.94 s，仅作单样本记录，不作为稳定提速结论。

### 批量等价

`tools/OfflineSearchHarness/compare_results.py` 在 5 个不同生成场景根上比较同一份基线/候选 DLL，`mismatched_roots=0`、无 `left_only`/`right_only`、`comparedFields=413`，覆盖路线、根 continuation 与 catalog。另 5 个不同角色/战斗根按相同方式逐项比较，选中路线与全部 `cachedContinuations` 文本完全一致；selected worker 分配逐根减少 2.2–16.9 MB。

## 验证与限制

- Release 构建 0 警告、0 错误；Linux `tools/verify-refactor-boundaries.sh` 输出 `REFACTOR_BOUNDARIES_OK search_files=193`。
- `tools/CombatSolver.GcPolicyChecks recovery` 输出 `GC policy checks passed: 9 scenarios.`，包含无进展计数、阈值、按成员重置等合同。
- 上述性能数字全部来自本机 headless 离线宿主，不是可见 Steam 帧时间或玩家感知延迟；批量对照为单机样本，未做多机器或 Windows 验证。
- 内存截断端到端使用外部活球压和固定短搜，不能替代原始 VeryHigh 问题包或真实长时间搜索的质量回归。
