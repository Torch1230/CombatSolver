# 草稿 PR：缓存 ModelDb.GetId 的纯类型映射，消除卡池枚举的正则重复计算

> **状态：草稿。** 未推送、未在远端创建 PR。本文件是可整段复制的 PR 说明；行为改动已在 `experiment/learned-selector` 上提交（`cef937c`、`7827276`），文档提交见文末清单。

## 标题

```
perf: memoize the pure ModelDb.GetId type-to-ModelId mapping
```

## 概要

两个行为改动，都不改变搜索策略：

1. **`ModelDbGetIdCachePatch`（新增）**：缓存原生 `ModelDb.GetId(Type)` 的 `Type → ModelId` 纯值映射。
2. **`Crossbow` 遗物**接上已审计的根生成池 helper（与既有 14 张生成牌 / 6 种药水 / 4 个回合开始 Power 同一入口）。

不按卡牌、遗物或遭遇添加搜索特判；不改 Beam、节点/时间预算、评分、保路、Pareto、终局政策与 No-GC 预算。旧行为在缓存未命中、门禁不过或参数为 null 时原样保留。

## 根因：飞溅这类「印牌」被 vanilla 的重复正则放大

原生 `ModelDb.GetId(Type)` 每次调用都重算：

```
GetId(type) = new ModelId(
    ModelId.SlugifyCategory(GetCategoryType(type).Name),   // 生成正则 + 去 "_MODEL"
    StringHelper.Slugify(type.Name))                        // CamelCase/Whitespace/SpecialChar 三条生成正则
```

结果只取决于 `type`（`GetCategoryType` 只沿 `BaseType` 上溯到 `AbstractModel`；`Slugify` 用 `ToUpperInvariant`，culture 无关；`ModelId` 是不可变 record），但 **vanilla 不缓存**。

**飞溅（`SPLASH`）** 把这笔成本放大了几百倍：`CardGenerationCardMirrors.SplashOnPlay` 是全仓唯一枚举 `UnlockState.CharacterCardPools`（其它角色卡池）的地方，每次打出都要对每个池跑

```
CardPoolModel.GetUnlockedCards → FilterThroughEpochs → Epoch.get_Cards()
  → ModelDb.Card<T>() → ModelDb.Get<T>() → ModelDb.GetId(Type) → 三条正则
```

而搜索对同一张牌在不同分支上反复重放（100k 展开 / 611k 转移 ≈ 6 次回放每节点），于是：

| 分配 trace（sel-defect-elite-01 @20k，全栈聚合） | 估计字节 | 占比 |
|---|---:|---:|
| 经 `StringHelper.Slugify` / `ModelId.SlugifyCategory` | 12.31 GiB | 已覆盖栈的 69%，confirmed search 的约 59% |
| 其中 `System.Int32[]`（RegexRunner 内部数组） | 7.49 GiB | |
| `System.String` | 2.20 GiB | |
| `Regex+Runner` | 1.80 GiB | |
| `Regex.Match` | 1.68 GiB | |

按发起方：`PredictionExtensions.GetUnlockedCards` 6.39 GiB、`SplashOnPlay` 直接调用 5.78 GiB。

同类「印牌」来源（无色池 / 自己角色池 / 角色攻击牌 / 变形 / 药水 / 回合开始 Power）在之前各轮已改走根生成池快照；本轮把剩下的**遗物**站点也逐项核对，结论是只有 `Crossbow`（无回合守卫、**每回合**触发）有量级；5 个 `turn <= 1` 的站点（`ChoicesParadox`/`VexingPuzzlebox`/`BigHat`/`OrangeDough`/`Toolbox`）在搜索里根本不执行（搜索根在玩家第一回合 Play 阶段捕获）。

## 收益

命令模板（两侧同一宿主二进制，仅用 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 切换模组 DLL）：

```bash
TMPDIR="$PWD/.local/tmp" timeout 900 dotnet tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll \
  --request "$PWD/.local/learned-selector/data3/requests/<root>.json" --label <label> \
  --out .local/learned-selector/memory-e2e/<label> \
  --profile VeryHigh --nodes 100000 --dop 16 --budget-ms 600000 --search-mode Evaluate \
  --enable-no-gc-region --no-gc-region-budget-gigabytes 12 --milestone M2
```

### `sel-defect-elite-01` @100k，Evaluate，交错 3+3

| 指标 | 基线中位（范围） | 候选中位（范围） | 变化 |
|---|---:|---:|---:|
| 墙钟 s（16 GB 区域） | 67.46（66.78~67.86） | **32.19**（31.96~32.82） | **−52.3%** |
| selected 分配 GiB（16 GB） | 90.83（88.86~91.04） | **33.47** | **−63.2%** |
| KB/转移（16 GB） | 155.9 | **54.3** | **−65.1%** |
| 峰值 RSS GiB（16 GB） | 28.68 | **14.03** | −51.1% |
| 墙钟 s（**No-GC 12 GB**，生产区域预算） | 70.26 | **30.45** | **−56.7%** |
| selected 分配 GiB（12 GB） | 92.70 | **31.99** | **−65.5%** |
| 峰值 RSS GiB（12 GB） | 21.32 | **10.54** | −50.6% |
| CPU s（user+sys） | 426.4 | 270.7 | −36.5% |
| Gen2 | 20 | 6 | −70% |
| 展开 / score / 战损 / finalHp / 结束回合 / planActions | 100000 / 10000514973 / 29 / 46 / 6 / 27 | 同 | 全同 |

### 生产默认路径 `--search-mode Coordinator --use-portfolio` @100k（12 GB，交错 3+3）

| 指标 | 基线中位（范围） | 候选中位（范围） | 变化 |
|---|---:|---:|---:|
| **请求级墙钟 s** | 233.25（206.31~266.09） | **74.81**（73.37~74.95） | **−67.9%** |
| 全进程分配 GiB | 312.80（292.40~335.07） | **98.44** | **−68.5%** |
| 搜索分配 GiB | 118.42 | **30.54** | **−74.2%** |
| 峰值 RSS GiB | 35.47 | **20.78** | −41.4% |
| GC 暂停 ms | 4393 | 2207 | −49.8% |
| 选中成员展开 / score / 战损 / planActions / cachedContinuations | 100000 / 10000374964 / 31 / 36 / 8 | 同 | 全同 |
| 被跳过成员 / 其中 `MemoryHeadroomInsufficient` | 4 / 0 | 4 / 0 | 0 |

搜索之外残差（含主线程根捕获、建局、成员调度）：分配 194.38 → **67.90 GiB**、墙钟 159.98 → **46.45 s** —— 缓存没有把成本挪到主线程。

### `Crossbow`（构造根，@20k，12 GB，交错 3+3）

| 指标 | 基线中位 | 候选中位 | 变化 |
|---|---:|---:|---:|
| 分配 GiB | 6.7347 | **5.8182** | **−13.56%** |
| KB/转移 | 55.53 | 47.89 | −13.56% |
| 峰值 RSS GiB | 7.06 | **6.13** | −13.05% |
| 墙钟 s | 13.25 | 12.79 | −3.46% |

## 决策等价证据

| 口径 | 结果 |
|---|---|
| Evaluate @20k **DOP1**（无并行调度自由度） | 转移 113,574、选择分支 11,460、全部非时序剪枝计数（dominated 1527 / duplicateCard 2842 / transposition 6219 / reusedNodeSnapshots 20547 / standPatProbes 4015 / 其余 0）、score/战损/finalHp/结束回合/planActions **逐位相同** |
| 生产路径 **DOP1** @20k | planActions(36) 与全部 `cachedContinuations` 文本、score、战损、成员结构与跳过明细**逐项相同** |
| 生产路径 @100k DOP16 ×3 | score / 战损 / planActions(36) / cachedContinuations(8) 全同；成员集合与跳过原因全同 |
| 哨兵 `sel-defect-elite-02`、`sel-silent-boss-01` @20k | 转移与选择分支**逐位相同**（42752 / 2422；544653 / 465076），决策全同 |
| Crossbow 构造根 DOP1 | 转移 131,514、分支 8,612、全部剪枝计数与决策逐位相同 |
| `Crossbow` 构造根 DOP16 ×3 | 转移 127,180 六次全同 |

**受益范围**：语料 300 根里 17 根（5.7%）含 `SPLASH`，覆盖全部五个角色与三种遭遇类型；实测 **8/8 含 SPLASH 的根**分配下降 10.7%~72.8%（defect-boss-12 −66.8%、defect-elite-01 −72.8%、ironclad-elite-03 −52.9%、silent-boss-15 −33.4%、necrobinder-elite-01 −27.7% 等），**12/12 不含 SPLASH 的根**在 ±2% 内。`SPLASH` 是无色牌，任何角色都可能拿到，所以这不是 Defect 专属问题。

**DOP16 下的轨迹差异**：`sel-defect-elite-01` 转移 611,114 → 645,830、`parallel_waves` 7,820 → 3,690。并行外层准入按「实测父分配 × 安全系数 + 突发余量」预约内存；父分配真实下降约 65% 后每个准入窗口装下更多父节点，节点上限处被展开的集合随之变化。DOP1 逐位相同证明这不是候选/剪枝/保路的变化。

## 被否决的尝试（含数字）

| 尝试 | 实测 | 处理 |
|---|---|---|
| 5 个 `turn <= 1` 遗物站点接缓存（ChoicesParadox/VexingPuzzlebox/BigHat/OrangeDough/Toolbox） | 含遗物语料首领根 @20k/DOP1：分配 −0.25%~−0.01%（最大那根 148,872 转移）；Toolbox 全栈 trace 仅 0.0002 GiB / 已覆盖 4.12 GiB | **已回退**（等价性成立、收益不成立；原因是搜索根在玩家第一回合 Play 阶段捕获，这些块不执行） |
| 其它角色卡池根快照（覆盖飞溅剩余约 10%） | 未实现 | **评估后放弃**：会把枚举搬进主线程根捕获（方向与「主线程卡顿优先」相反）；上限仅约 −10% 分配 / −1~2% 墙钟且只对 5.7% 的根有效；证明成本大于收益 |

## 风险与未验证

1. 全部性能数据来自离线无头宿主，不是可见 Steam 会话；不外推帧时间或玩家可感知卡顿。生产预设的 500,000 节点 / 300 s 未运行。
2. 补丁对 live 路径同样生效（`ModelDb.GetId` 是全局方法）；可见会话、Windows 构建、第三方模组共存未验证。
3. 缓存不做淘汰：键是模型 `Type`（原生 1,660 个模型类型 + 模组注入类型），没有观察到无界增长的调用方，但不是硬上限保证。
4. 未执行：`--verify-incremental-search`、完整部署与原生重放。
5. 组合器在内存富余时会启用更多成员：`sel-defect-boss-12` 与 `sel-ironclad-elite-03` 两侧成员运行集合不同（基线因 `MemoryHeadroomInsufficient` 跳过、候选跑了），它们的墙钟不可直接比；路线与续用戳仍全同，已如实记录、未调参凑对比。

## 提交清单

```
cef937c perf: memoize the pure ModelDb.GetId type-to-ModelId mapping
c8c0c09 docs: record the No-GC 12 GB result and the SPLASH-scoped reach of the GetId cache
7827276 perf: reuse the root generation pool for the Crossbow relic
aec373a docs: confirm the dormant turn<=1 relic sites on heavy boss roots
936957c docs: record the production Coordinator/portfolio verification and drop the other-character pool snapshot
```

Release 构建 0 警告 / 0 错误；`./tools/verify-refactor-boundaries.sh` 输出 `REFACTOR_BOUNDARIES_OK search_files=193`。本批不提升版本、不打标签、不发包、不推送。

## 详细证据

- [ModelDb.GetId 记忆化：归因、A/B、生产路径](../performance/defect-modeldb-getid-cache-20260919.md)
- [遗物印牌站点复用与方案 B 留档](../performance/relic-generation-pool-reuse-20260919.md)
- [开发笔记](../DEVELOPMENT_NOTES.md) / [测试矩阵](../TEST_MATRIX.md)
