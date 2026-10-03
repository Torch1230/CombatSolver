# perf-2 选择性合入与后续热点试验（2026-09-09）

本次以当前分支 `a85f3e7` 为基线，审查 `CombatSolver-perf-2` 的 `HANDOVER-perf-2-20260909.md` 及固定提交 `cad22cc374175240cb55145014c44bff3de10342`。保留四类已有候选的只读保路并行、容量算术和路线预览节流；外层父窗口维持 `2×DOP`。这是一次代码适配提交，没有整体合并旧调度器或采用源工作区未提交的计时插桩。

八个预定交错正式样本的正常搜索均值 **23.6271→23.2887 秒（耗时减少 1.43%）**；固定 Short 均值减少 2.98%。这是本轮相对 `a85f3e7` 的变化，不叠加前一轮的 32.37%，也不把源分支相对 `984fa59` 的倍率再加一次。所有正式结果 Passed，88 个非时序 RESULT 字段及完整动作一致。单样本范围重叠，GC 暂停仍明显；数据只支持本机固定 headless 首结果场景。

## 1. 哪些源改动进入当前分支

| 来源 | 本次处理 | 原因与适配 |
|---|---|---|
| `0832a1c`、`34362b9`、`54580d9` | 保留思路并适配 | 路由签名、完整组排名摘要、上下文 Pareto、continuation 包通过 `RetentionJobs` 复用已排空的固定 lane；分组、输出拼接与观察请求沿原序执行。当前组内摘要缓存继续沿用本次调用的有效期。 |
| `8620167` | 部分采用 | `SearchWaveMemoryPolicy` 统一队列上限与饱和倍增；仍为 `2×DOP`。修正原 helper 的奇数上限与零值：`GrowCapacity(3,7)=6`、`GrowCapacity(0,1)=0`，并覆盖大整数溢出。 |
| `88f11c7` | 采用 | 路线预览跟随既有进度 UI 的 200ms 间隔；保留强制发布、最终结果及排名保存/还原。 |
| `817fb11` | 已有对应实现 | 当前分支已有监听分段、有效前段与 Power 投影的失效/复用，不重复移植较早版本。 |
| `a64a471` | 试验后撤回 | `8×DOP` 在当前动作/选择作业调度下，长搜收益没有超过前后基线漂移，RSS 增加；保留 `2×DOP`。 |
| `e470e32`、`1986c5e`、`f7d786e` | 本次未采用 | 全节点父链/用药谱系缓存需要另外验证 `SearchNode` record 的相等、`with` 复制与生命周期。当前没有足够的额外收益证据来扩大这一验证面。 |
| `2577809` | 未采用 | 剪枝已有并行 EndTurn 探针，推测展开会争用同一 executor；被放弃的推测作业也必须完整计入分配。这里没有引入未准入展开。 |

源实现中的 `Parallel.For` 没有使用当前求解器的 DOP/lane 边界，也不能把移到其他线程的分配视为消失。本次只读作业写各自索引或独占组；coordinator 等待全批、合并所有成功和失败分配后才提交结果或传播原异常。父排名、已选集合和 lease 账本在这个窗口内只读，预算、转置、支配、候选配额与动作接收次序均保持原规则。

## 2. 固定输入与测量口径

- 场景：`NECROBINDER / AEONGLASS_BOSS`，固定种子、38 张牌、19 件遗物、2 瓶药水及 `RequireAtLeastOne`。原忽略目录输入与三个已提交的 `search-performance-necrobinder-projected-*` JSON 逐项相等。
- 固定 VeryHigh、DOP8、NoGC 配置 **16,000,000,000 字节**，每请求 120 秒内且首结果即停。CLI 使用十进制 GB；早期本地计划文字中的 GiB 是单位笔误，数值参数从未改变。实际区域容量仍由原 Runtime 内存政策决定，没有固定或扩大实际申请。
- 正式顺序预先写定：B1–C1–C2–B2–B3–C3–C4–B4。每个样本启动新进程，先做一次不计时的 Short 预热，再测一次 Short 和一次正常搜索；全部样本保留。基线复用已冻结的 `a85f3e7` 产物，候选为本次最终 Release 产物。
- 正常搜索始终使用 Deep profile。`phase/deep_triggered` 是跨过 20 秒检查点的耗时标签；`CombatSearchCoordinator.cs` 与基线完全相同，原始 90 字段比较和两个标签均保存在 JSON。固定工作量比较只排除这两个标签，保留另外 88 项，含 7 项 deferred-round 指标。
- 普通模式不启用详细阶段计时、增量回放或 CPU profiler。线程运行/等待时间每 50ms 采样、RSS 每 100ms 采样；峰值是采样峰值。GC 的总暂停和 observed max 分别报告，后者不是 trace 逐事件最大值。

正常搜索固定 **46,239 展开 / 636,428 转移 / 431,140 选择分支 / 64 条完整预测动作**；Short 固定 **10,000 / 144,368 / 101,808 / 59 条动作**。正常场景仍为 `onlyDeathRoutes=true`、预测整场战损 9；这不是整场胜利或原生部署质量的结论。

## 3. 全部正式长搜样本

| 样本 | runId | 搜索秒 | 分配 GB | 采样峰值 RSS GB | GC 总暂停 ms | GC observed max ms |
|---|---|---:|---:|---:|---:|---:|
| b1 | `d9d99bd4b8964e3b8714cbfa732f7679` | 23.4715 | 29.5588 | 15.088 | 1725.507 | 1692.302 |
| c1 | `9a061facda21464bb3b4c153505dcaac` | 23.8896 | 29.6005 | 16.948 | 2031.404 | 1988.728 |
| c2 | `bc03268fff1f4e55a0213e033758e7bf` | 23.1965 | 29.5997 | 15.217 | 1715.370 | 1687.497 |
| b2 | `aae22b235656470aaa5b762f7422d6f6` | 23.7455 | 29.5635 | 17.075 | 48.912 | 29.283 |
| b3 | `d3c727fce1d745f78c1e71996cefc0be` | 23.6132 | 29.5666 | 17.040 | 54.506 | 33.516 |
| c3 | `0e42eaf6f2c84ca287e1241fd1a4adb8` | 23.0687 | 29.6016 | 17.163 | 50.114 | 30.348 |
| c4 | `253669e6211b44db967621fb01a325e4` | 23.0001 | 29.6023 | 17.277 | 53.774 | 28.005 |
| b4 | `f791f15127194eee9088821a2b8c4186` | 23.6784 | 29.5647 | 17.185 | 42.963 | 26.144 |

均值与范围：

| 指标 | 基线 B | 候选 C |
|---|---:|---:|
| 正常搜索均值 / 范围（秒） | 23.6271 / 23.4715–23.7455 | 23.2887 / 23.0001–23.8896 |
| Short 均值 / 范围（秒） | 4.6127 / 4.5739–4.6519 | 4.4754 / 4.4322–4.4950 |
| 正常分配（GB） | 29.5634 | 29.6011 |
| 采样峰值 RSS 均值（GB） | 16.597 | 16.651 |
| 搜索相关线程平均用核 | 4.707 | 4.817 |
| 搜索相关线程 CPU 秒 | 111.395 | 112.517 |

按预定编号配对的长搜耗时减少依次为 **-1.78%, +2.31%, +2.31%, +2.86%**。不删除较慢候选，也不以最优样本代表整组。总分配变化为 +0.127%，RSS 均值变化为 +0.329%；本次主要改变串行等待，而非大幅减少模拟或分配。平均用核来自 lane 与线程池的调度运行时间，包含相关 Runtime 回调，只作采样窗口内的近似归因。

## 4. 保留全部探索结果

| 原型 / 基线 | Short 秒 | 正常秒 | 正常分配 GB | 采样峰值 RSS GB |
|---|---:|---:|---:|---:|
| `baseline1` | 4.6043 | 23.9378 | 29.5659 | 17.168 |
| `queue8` | 4.4876 | 23.8616 | 29.5723 | 17.969 |
| `baseline2` | 4.7869 | 24.3706 | 29.5649 | 17.089 |
| `retention-jobs` | 4.7046 | 23.9669 | 29.6297 | 17.191 |
| `retention2` | 4.4644 | 22.8231 | 29.6301 | 17.348 |
| `baseline3` | 4.6822 | 23.6427 | 29.5693 | 17.185 |
| `retention3` | 4.5013 | 23.1248 | 29.6314 | 17.379 |
| `baseline4` | 4.7811 | 24.3698 | 29.5608 | 15.112 |
| `hook-index` | 4.5841 | 23.2683 | 29.7640 | 17.448 |

- `queue8` 前后基线是 `baseline1/2`。它的 Short 单样本较快，长搜约 1.21% 的差异落在两次基线约 1.81% 的漂移内，RSS 约增加 4.90%，因此撤回。
- `retention-jobs` 首样本差异很小，随后预先安排 `retention2–baseline3–retention3–baseline4` 对照。全部四次基线和三次候选的探索均值约减少 3.22%；最终结论以第 3 节独立正式组为准。
- 新试验 `hook-index` 给现有不可变类型布局增加逐 Hook 接收者位置表，保留原接收者顺序及复合 mask 扫描。与纯保路候选探索均值相比，没有建立额外耗时收益，正常分配增加约 134 MB，已连同索引专属测试原型撤回。该原型的目标 Short/正常工作字段和动作一致；未把未执行的索引原生合同写成通过。

各原型的 runId、完整原始指标、比较结果与撤回理由都在 [结构化记录](perf2-integration-20260909.json) 中。Profiler、完整日志及 DLL 仅留在忽略目录。

## 5. 继续研究得到的热点

在 `a85f3e7` 上另做一次 Linux `perf cpu-clock:u`、199Hz、JIT 符号解析的诊断：26,230 全进程样本中有 21,965 个含搜索栈。互斥分类为回放（排除快照/Fork）40.66%、快照 31.66%、Fork（排除快照）15.58%、纯保路 3.29%、其余 8.81%。诊断时间不进入 A/B。

快照下还需细分：`BuildStateKey` 1,423 个样本、`CalculateReachableHandPotential` 962、投影洗牌 898、威胁投影 592。全搜索中费用 Hook 的 inclusive 样本为 640（2.91%）；它包含回调和上下文成本，不等于本次索引原型可省下的扫描成本。全搜索中的 `PredictionStateStore.Fork` 仅 147 个样本（0.67%），因此不能用整个 Fork 的 15.58% 代替这个小容器的收益上限。

下一步优先量化同一稳定快照内的费用/可玩性重复计算、投影洗牌中排序与指纹混合各自的成本，再判断是否值得复用。任何费用缓存都必须覆盖 Hook 状态、卡牌变化与第三方行为；本次没有引入跨快照可玩性缓存。源交接里继续拆分已准入动作的方向，已由当前分支的动作/选择作业实现覆盖。线程采样计数和 Stopwatch 经过时间不构成物理 CPU 上限证明，不能据此断言全局最多只能加速某个固定倍数。

## 6. 最终验证

| 检查 | 结果与直接证据 |
|---|---|
| Release | 本次最终构建成功，0 警告 / 0 错误；没有改版本或打包。 |
| 纯容量合同 | 剩余预算/部分 wave 与 10,000 个容量样本；精确饱和增长、奇数上限、零和另 10,000 个大整数样本通过。 |
| 并行与搜索政策 | `2cfdd334274942abbe817d1c670faa07` Passed，请求 57.55 秒。新合同在两个真实固定 lane 上验证 257 个槽位各一次、原异常/取消 token、在途排空、失败后复用，成功与失败分配共记账 235,536 字节。既有 Deep 1,000 节点合同实际 641 次待命探针，DOP1/2 完整结果、评分、动作和非时序指标一致，取消后复用原根。 |
| Short 哨兵 | `e7bae27e545942799fe3335c9673c6a6` Passed；与 `a85f3e7` 既有基线 `9664c01a18084c92afe111f9d47ce7e8` 的全部 90 字段 / 9 动作一致。 |
| 1GB NoGC 回收 | `3f2ab070c3fa4f88bfe29f0cbe1ae173` Passed；52 次 NoGC 重启、0 次丢失，与既有 `a85f3e7` 基线 `2345c8785fac4c8fb6d9b81e72aa43ae` 的全部 90 字段 / 64 动作一致。搜索 36.33 秒、请求 39.96 秒；此请求只用于回收正确性，不作跨批次速度比较。 |
| 结构门禁 | Bash 与 PowerShell 均通过，`search_files=83`；职责地图、两项 skill 与两端门禁同步更新。 |

未做本轮可见 Steam、Windows 游戏或整场原生部署测试；沿用此前暂不运行可见性能的安排。原生 Hook 索引原型已经撤回，最终提交只改变 Search/测试及对应文档，没有保留 Engine 语义改动。

## 7. 复跑

先在要测量的提交上生成独立 Release 目录并复制 manifest；基线选 `a85f3e7`，候选选本提交。每次请求显式指定产物目录，禁止在不同 DLL 间复用已加载进程。

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false -o .local/perf2-repro-artifact
cp CombatSolver.json .local/perf2-repro-artifact/
./tools/run-unattended-test.sh --scenario-id PERF2-REPRO --character-id NECROBINDER --seed SEARCH_PERF_NECROBINDER_POTION --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2 --enemy-current-hp 526 --initial-player-hp 41 --initial-player-max-hp 76 --clear-run-deck --run-cards-path coverage/unattended/search-performance-necrobinder-projected-run-cards.json --relics-path coverage/unattended/search-performance-necrobinder-projected-relics.json --potions-path coverage/unattended/search-performance-necrobinder-projected-potions.json --cards-json '[]' --potion-policy-for-test RequireAtLeastOne --performance-preset-for-test VeryHigh --search-max-degree-of-parallelism-for-test 8 --enable-no-gc-region-for-test 1 --no-gc-region-budget-gigabytes-for-test 16 --enable-detailed-diagnostic-logs-for-test 0 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --keep-game-open --headless-instance perf2-repro --combat-solver-build-dir .local/perf2-repro-artifact --evidence-directory .local/perf2-repro-normal
./tools/run-unattended-test.sh --headless-instance perf2-repro --stop-instance
```

正式组在同一进程中先用相同参数加 `--force-short-search-only` 预热一次，再以另一个 evidence 目录测一次 Short，最后用上方正常参数测长搜，随后停止实例。DOP、预设、快照和预算必须保持一致。压力验证只把 NoGC 配置改为 `1`，不引用其性能倍率。

```powershell
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false -o .local/perf2-repro-artifact
Copy-Item CombatSolver.json .local/perf2-repro-artifact/
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId PERF2-REPRO -CharacterId NECROBINDER -Seed SEARCH_PERF_NECROBINDER_POTION -EncounterId AEONGLASS_BOSS -Ascension 10 -ActIndexForTest 2 -EnemyCurrentHp 526 -InitialPlayerHp 41 -InitialPlayerMaxHp 76 -ClearRunDeck -RunCardsPath coverage/unattended/search-performance-necrobinder-projected-run-cards.json -RelicsPath coverage/unattended/search-performance-necrobinder-projected-relics.json -PotionsPath coverage/unattended/search-performance-necrobinder-projected-potions.json -CardsJson '[]' -PotionPolicyForTest RequireAtLeastOne -PerformancePresetForTest VeryHigh -SearchMaxDegreeOfParallelismForTest 8 -EnableNoGcRegionForTest 1 -NoGcRegionBudgetGigabytesForTest 16 -EnableDetailedDiagnosticLogsForTest 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -KeepGameOpen -HeadlessInstance perf2-repro -CombatSolverBuildDir .local/perf2-repro-artifact -EvidenceDirectory .local/perf2-repro-normal
pwsh -NoProfile -File tools/run-unattended-test.ps1 -HeadlessInstance perf2-repro -StopInstance
```

最小并行合同沿用相同 fixture 的 `-ScenarioId STAND-PAT-PROBE-BATCHES -VerifySearchPolicySnapshot -StopAfterCombatRootSnapshotAssertion`（移除首结果停止），每请求仍为 120 秒。容量合同：`dotnet run --project tools/CombatSolver.WavePolicyChecks -c Release`。两端结构门禁分别为 `./tools/verify-refactor-boundaries.sh` 与 `pwsh -NoProfile -File tools/verify-refactor-boundaries.ps1`。
