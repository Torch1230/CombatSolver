# 搜索保留表规模与上限决策（2026-09-19）

本文件先记录测量与取舍，后续「已实施的默认上限」一节记录原 PR #114 的实现。达到上限会改变搜索决策；原固定根不能给出广泛整场质量保证。

## 已完成的零决策代价分配削减

在测量保留表之前，先把卡牌生成池的重复工作去掉了；这些改动不改变候选、RNG 消耗或保路规则：

- `JackOfAllTrades`、`Largesse` 改用已有的根级无色牌池快照，调用方自己的 `is not JackOfAllTrades` 谓词仍位于随机选择之前。
- 新增根级“全部可生成角色牌”快照，`Abundance`、`Discovery`、`Distraction`、`Jackpot`、`WhiteNoise`、`TinkerTime.Chaos`、`Stoke`、`Calamity`、攻击/技能/能力药水与 Orobic Acid 等路径统一复用。
- 复用的只是解锁与战斗/人数过滤后的有序候选数组；`TakeRandom` / `NextItem`、升级和加入手牌仍逐分支执行。

真实离线 A/B（同请求、同 seed、Beam 16、`Evaluate`、DOP 1；基线为对应改动前的 DLL）：

| 场景 | selected worker 分配基线 → 候选 | 墙钟基线 → 候选 |
|---|---:|---:|
| `sel-silent-elite-07`（Discovery 路线） | 1,130,646,328 → 908,226,240 B（−19.7%） | 8.91 s → 7.67 s |
| `sel-silent-monster-08`（Calamity 路线） | 1,212,297,976 → 1,109,011,824 B（−8.5%） | 9.35 s → 8.73 s |
| `sel-ironclad-elite-02`（Jackpot 路线） | 756,786,104 → 713,995,056 B（−5.7%） | 7.73 s → 7.27 s |
| `sel-ironclad-elite-16`（Stoke 路线） | 447,258,872 → 416,732,144 B（−6.8%） | 5.07 s → 4.50 s |
| `sel-defect-monster-13`（White Noise 路线） | 428,532,536 → 401,699,856 B（−6.3%） | 5.20 s → 4.73 s |
| `sel-defect-elite-02`（无色池，前一轮已缓存） | 245,180,664 → 245,212,592 B（噪声） | 3.59 s → 3.24 s |

`tools/OfflineSearchHarness/compare_results.py` 对这 6 个根比较 539 个非时间/非内存字段，`mismatched_roots=0`、无缺根；选中路线、全部 `cachedContinuations` 文本、展开/转移、分数与预计战损逐项一致。

## 保留表当前规模

长搜样本：`sel-regent-monster-11`、Beam 60、`--nodes 60000`、`Evaluate`、DOP 1、无 NoGC；墙钟 93.8 s，最终 `NodeLimit`，selected worker 累计分配 23.43 GB，最终托管 live 191.5 MB。新增的 `SEARCH_PHASE` 计数给出：

| 表 | 条目数（60,000 展开时） | 每展开节点 |
|---|---:|---:|
| `Transpositions` | 318,265 | 5.30 |
| `ExpandedTranspositions` | 58,622 | 0.98 |
| `StandPatCache` | 51,354 | 0.86 |
| `ThreatProjectionCache` | 172,500 | 2.88 |
| `CoverageCache` | 11,012 | 0.18 |

中途 gcdump 的已分配类型可直接对上其中一部分：

- `TranspositionFrontier` 对象 326,290 个 × 56 B ≈ 18.3 MB；
- `Dictionary<StateFingerprint, TranspositionFrontier>` 的 Entry 数组 10.38 MB + 2.41 MB；
- `Entry<(StateFingerprint, int), ThreatProjection>[]` 7.51 MB；
- `Entry<StateFingerprint, StandPatEvaluation>[]` 3.02 MB；
- `Entry<PredictionRiskSignature, CoverageSummary>[]` 0.70 MB。

按线性外推，纯看 `Transpositions + ExpandedTranspositions` 两个语义表：500,000 展开约有 3.1 M 条目，1 M 展开约有 6.3 M 条目；即使按当前对象的保守字节数，也会到数百 MB 级。它们正是 `ResetReclaimableCaches` 不会释放的部分，也是 VeryHigh 长时间运行时少数会随展开数持续增长、不会在内存检查点归零的结构。

`StandPatCache` / `ThreatProjectionCache` / `CoverageCache` 是纯 memo，现有内存检查点本来就会清空它们；清空只增加重算，不改变任何决策。问题在于：VeryHigh 故障中 NoGC 检查点已经反复清理这些纯缓存，剩下的长期增长主要来自两张转置表。因此本节的决策点是转置表，而不是继续优化纯缓存。

## 消融已给出质量代价上界

既有消融（`docs` 与提交 `6cabc8a`，60 场）把转置支配剪枝整个关掉：

- 工作量均值 +2.9%～3.3%，中位数 1.000，p90 1.125；
- 该样本里质量代价均值 0.034 HP/场，59 场中 1 场受影响；
- 45% 场次的发布路线改变，但最终质量几乎不变。

给表设上限的直接效果是停止记录新状态，已有条目继续剪枝；它控制两表的字典条目数，但不保证路线质量或工作量一定落在「全关剪枝」与「不设上限」之间。有预算的搜索会因候选竞争与路径顺序变化而走向不同结果，上述消融只能说明那 60 场的实测规模。潜在收益是让两张转置表在超长搜索中停止增加新的状态键，而非整个请求的硬字节上限。

## 已实施的默认上限（2026-09-19，用户授权“按你觉得好的方式来做”）

按上面第 3 条建议实现，并把默认值写进生产路径：

- `SearchPolicySnapshot.TranspositionEntryLimit` 默认 `DefaultTranspositionEntryLimit = 1_000_000`（0 = 不设上限，仅实验用）。两张表**共用**同一预算：`Transpositions` + `ExpandedTranspositions` 条目数之和达到上限后，新状态不再写入、直接按准入处理；已有条目继续参与支配剪枝，不做淘汰、不改变已有条目的语义。
- 新诊断计数 `transposition_limit_bypass` 进入 `SEARCH_PHASE` 行；离线宿主新增 `--transposition-entry-limit <N>`（0 = 不设上限，缺省 = 生产默认）。

### 证据

1. **低于上限逐项不变**：20,000 节点根在默认上限下 `transposition_limit_bypass=0`，展开/转移/分数/战损与不设上限的基线完全相同（defect 10,814/42,752/2 HP；necrobinder 7,999/27,255/17 HP；regent 20,000/113,906/0 HP，两表合计 106,033）。
2. **上限生效时行为符合设计**：regent、20,000 节点、`--transposition-entry-limit 20000` → 两表恰好 16,381 + 3,619 = 20,000，`transposition_limit_bypass=82,314`，分数与战损与不设上限完全相同（10,002,177,986 / 0），墙钟 8.62 → 9.40 s（+9%）。
3. **长搜 A/B**（`sel-silent-boss-01`、DOP16、NoGC 16 GB，两次都自然终止于约 25,500 展开）：

| 组 | 墙钟 | 分配 | 峰值 RSS | 展开 | 转移 | GC 暂停 | 分数 | 战损 | 两表合计 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 不设上限 | 30.6 s | 31.01 GB | 14.55 GB | 25,516 | 655,638 | 12 ms | 10001699951 | 6 | 489,707 |
| 上限 150,000 | 29.6 s | 29.65 GB | 13.78 GB | 25,957 | 636,681 | 3,916 ms | 10001699952 | 6 | 149,999 |

上限把两张表从 48.97 万条压到 15.00 万条（−69%），分配 −4.4%、峰值 RSS −5.3%、墙钟 −3.3%，战损相同、分数只差 1（1e10 量级）。GC 暂停反而升高是 16 GB 区域在两组里都会被顶掉、重建时点不同造成的，不是上限的系统性代价。

### 结论与限制

- 预算是**每次求解运行**（每个 `SearchRunContext`）的，不是整个请求的：组合路径下 5～6 个成员各自最多 100 万条，因此请求级上界是成员数 × 100 万条，仍随成员数有界、不再随展开数线性增长。
- 默认 100 万条只覆盖长搜：本批实测要 25,500 展开才到 48.97 万条，所以普通搜索和 20,000 节点跑批里 `bypass=0`、逐项不变；它只在 VeryHigh 预设（500,000 节点 / 300 s）那种数千万级展开里真正生效。
- 收益随展开数线性放大：49 万 → 15 万条只换来 5% 峰值 RSS，因为这两张表在 14 GB 峰值里占比很小；本轮没有跑到数百万条的量级（单次 30 s 已到本机 50,000 节点上限并自然终止）。
- 没有做 60 场级整场质量对照；既有的完全关闭剪枝消融（该样本工作量 +2.9～3.3%、质量代价 0.034 HP/场）只能作为参考，不能当作设上限后的全局上界。上限后仍对已有状态剪枝，但预算和候选次序变化可能改变最终路线。
