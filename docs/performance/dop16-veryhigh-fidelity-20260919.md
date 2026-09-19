# 极高预设 16 并行的离线基线与阶段归因（2026-09-19）

本文件记录本轮 16 并行 / 极高（VeryHigh）/ No-GC 16 GB 的多场景基线，以及为拿到可信数字先修掉的离线宿主保真与诊断缺陷。**没有新的生产搜索优化**：本轮的“更快”来自让离线宿主跑与实机相同的路径，而不是改变了搜索本身。

## 1. 离线宿主此前没有冻结 RitsuLib 内容注册（保真缺陷）

`SimulationCardPileLookupPatch` 的无分配快速路径（[源码](../../src/Runtime/SimulationCardPileLookupPatch.cs)）只在 `ModCardPileRegistry.IsFrozen` 为真且没有任何已注册模组牌堆时生效。RitsuLib 的[内容注册文档](https://sts2-ritsulib.ritsukage.com/guide/content-packs-and-registries)说明：**它会在游戏完成模型初始化前冻结内容注册**，所以实机在进入任何战斗前该条件已经成立，快速路径是生效的。

离线宿主此前没有这一步：`ModRuntime.Initialize` 只装 Harmony 补丁，从不冻结注册表。实测 `mod_card_piles=frozen=False definitions=0`，于是每次搜索都退回原版 `CardModel.get_Pile`——即 `Player.Piles` 的 `Enumerable.Concat` + `Func<CardPile,bool>` 谓词 + 数组/接口枚举器，每次查询都分配。

分配 trace（`--profile gc-verbose`，DOP16，4,000 展开，未冻结）里这些类型合计约 240 MB / 4.09 GB（5.9%）：

| 类型 | 估计字节 | 来源栈 |
|---|---:|---|
| `SZGenericArrayEnumerator<CardPile>` | 84.9 MB | `get_Pile` → `Player.get_Piles()` → `Concat` |
| `Func<CardPile,bool>` | 79.3 MB | 同一路径的 `TryGetFirst` 谓词 |
| `Concat2Iterator<CardPile>` | 75.5 MB | `Enumerable.Concat` |
| `MegaCrit...KeywordSources` | 57.5 MB | `GetKeywordsWithSources`（内部会问 `get_Pile`） |

修复：宿主在装补丁前调用 `ModCardPileRegistry.FreezeRegistrations("OfflineSearchHarness")`（与实机同一时点：无任何注册牌堆），并把 `mod_card_piles` 与冻结结果打进 M0.3 行走行，便于以后核对。

## 2. 诊断缺陷（会把真正的搜索失败盖掉）

1. **日志尾部丢失**：模组日志由后台写线程持有，进程直接退出时最后一段（阶段表正好在末尾）从未落盘。宿主的 `--measure-phases` 跑批里 `process.jsonl` 恰好停在 128 KiB（两个 64 KiB 缓冲），阶段表永远看不到。现在宿主在退出前用日志自己的 FIFO 快照屏障（`CaptureAsync`）等所有已入队条目写完，并把阶段表写进 `harness-result.json` 的 `phasePerformance`。
2. **二手异常盖住原错误**：lane 失败时仍然合并它的阶段指标，`DrainFrom` 因未收口的测量帧抛错，真正的原因再也看不到。现在失败的 lane 不合并指标（`outcome.Error != null` 时跳过），原错误优先抛出。
3. **测量帧在 finally 里被跳过**：`Replay` 里 `card_exec` / `card_post` / `potion_exec` 的 `finally` 先调用可能抛错的 `simulatedCombat.EndActionChoices()`，再 `End` 测量帧；前者一旦抛错，帧就留在 lane 上。现在 finally 一律**先收口测量帧**，`Action` 帧也放进自己的 `try/finally`。
4. **报错不含阶段名**：LIFO 与合并异常现在直接列出未收口的阶段名与帧号（另有 `COMBATSOLVER_MEASUREMENT_TRACE=1` 可记录建帧栈，默认关闭）。

修复前的可复现故障：`sel-regent-monster-11`、20,000 节点、DOP16、`--measure-phases` 在 1.2 s 内以 `不能合并仍有未结束阶段测量的 worker 指标` 终止（3/3 复现）；打开原错误后看到真正原因是被掩盖的 `phase=Action frame=... active=...</...>`。修复后同一命令完整跑完（8.6 s），展开/转移/分数/战损与不开阶段时逐项相同。

## 3. 16 并行 / 极高 / No-GC 16 GB 基线（4 根 × 3 次，取中位）

命令形状（每根三次，顺序固定）：

```bash
dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request .local/learned-selector/data3/requests/<root>.json \
  --profile VeryHigh --nodes 20000 --dop 16 --budget-ms 120000 \
  --search-mode Evaluate --enable-no-gc-region --no-gc-region-budget-gigabytes 16 --milestone M2
```

| 根 | 墙钟（旧 → 新） | selected worker 分配（旧 → 新） | 峰值 RSS（新） | 展开 | 战损 |
|---|---:|---:|---:|---:|---:|
| `sel-defect-elite-02` | 4.48 s → **4.39 s**（−2.0%） | 1,895 MB → **1,847 MB**（−2.5%） | 2.10 GB | 10,814 | 2 |
| `sel-necrobinder-elite-14` | 3.30 s → **3.21 s**（−2.7%） | 1,376 MB → **1,328 MB**（−3.5%） | 1.56 GB | 7,999 | 17 |
| `sel-regent-monster-11` | 8.88 s → **8.62 s**（−2.9%） | 5,073 MB → **4,886 MB**（−3.7%） | 5.20 GB | 20,000 | 0 |
| `sel-silent-boss-01` | 29.34 s → **26.57 s**（−9.4%） | 28,061 MB → **26,129 MB**（−6.9%） | 12.27 GB | 20,000 | 6 |

“旧”为宿主的 `bench-runs-2`（未冻结，2026-09-19 12:39–12:42），三次样本的墙钟离散度 ≤0.5%。四个根的展开数、战损与分数逐项相同：冻结只去掉原版查询的分配，不改变任何候选或决策。

## 4. 排他阶段表（冻结后，20,000 展开 / DOP16）

分配为各 lane 合并后的排他增量，时间是各线程经过时间之和（可超过墙钟）。

`sel-silent-boss-01`（25.66 s 墙钟、26.15 GB、544,653 转移）：

| 阶段 | 分配 | 占比 | 线程时间和 |
|---|---:|---:|---:|
| `fork` | 7,896 MB | 30.2% | 29.0 s |
| `round_player_start` | 5,200 MB | 19.9% | 34.0 s |
| `snapshot` | 1,674 MB | 6.4% | 23.1 s |
| `prune` | 1,606 MB | 6.1% | 5.0 s |
| `round_player_powers` | 1,194 MB | 4.6% | 9.4 s |
| `card_exec` | 672 MB | 2.6% | 9.0 s |
| `round_enemy_start` + `round_enemy_powers` | 1,329 MB | 5.1% | 9.3 s |
| `threat` + `combat_fingerprint` + `card_miss` | 865 MB | 3.3% | 14.0 s |

`sel-regent-monster-11`（8.58 s 墙钟、4.91 GB）：

| 阶段 | 分配 | 占比 |
|---|---:|---:|
| `fork` | 1,435 MB | 29.2% |
| `card_exec` | 750 MB | 15.3% |
| `snapshot` | 533 MB | 10.9% |
| `round_enemy_moves` | 311 MB | 6.3% |
| `prune` | 153 MB | 3.1% |

结论没有变化：分配由模拟自身的 fork 与回合推进主导（两根均约 30% + 20%），搜索侧记账（`prune` 3–6%、快照族 10–16%）都是长尾。没有出现单个可安全砍掉的支配项；能改变量级的手段仍是保留表上限（待用户决策）与 fork/回放结构，不属于“零决策代价”改动。

## 5. 一个实机相关的观察

`sel-silent-boss-01` 在 16 GB No-GC 预算下仍然测得 **2,399 ms GC 暂停**：这次搜索分配 26.1 GB，超过区域预算，中途必须退出并重建 No-GC 区域，节省下来的暂停被吃掉一部分。也就是说这类根的性能不只是“分配多少”，还包括“区域装不装得下”；提高预算与降低分配都能改善，前者要用户确认内存上限。

## 6. 限制与未做项

- 全部数字来自离线宿主（普通 .NET 进程，无 Godot 渲染），不是可见 Steam 帧时间；按当前批次约束未启动可见游戏。
- 保真修复改变的是**离线读数**。实机此前就走在快速路径上，因此不能把这 2–9% 当作玩家端收益。
- 修复后仍有 4/13 个搜索补丁在离线宿主未装上（`patches_applied=9/13`，均为 RitsuLib 自由游玩/目标类型相关的隔离补丁），未在本轮排查；离线数据在这条路径上仍可能偏悲观。
- 转置表上限仍未写入生产默认，等待用户对 [保留表规模与上限决策](search-retention-bounds-20260919.md) 的决定。
