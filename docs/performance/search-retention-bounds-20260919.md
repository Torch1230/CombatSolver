# 搜索保留表规模与上限决策（2026-09-19）

本文件只记录测量和建议，**没有实现会改变搜索决策的表上限**。需要用户决定是否接受少量路线变化来换取有界内存。

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
- 质量代价均值 0.034 HP/场，59 场中 1 场受影响；
- 45% 场次的发布路线改变，但最终质量几乎不变。

给表设上限的决策代价不会超过“整个关掉”这一上界，因为上限之后只是新状态不再进入剪枝，已有条目仍然工作；预计远小于 3% 工作量增量。收益是让两张转置表在超长搜索中不再线性吃内存。

## 建议的下一步实验（待批准后再做）

1. 只加测试入口：`--transposition-entry-limit <N>`，达到 N 后新状态直接视为准入，不再写入；`ExpandedTranspositions` 是否共用同一上限待定。
2. 同根固定节点 A/B 对照，记录 `expanded/transitions/score/HP/route/cachedContinuations`、两张表的峰值条目数、峰值托管堆和墙钟；先在 60 场级根集合上验证“没有明显质量下降”。
3. 只有在 60 场对照证明质量变化落在消融上界内，才考虑把默认上限设为生产值；阈值建议先做 1,000,000 组合条目这一档，它能覆盖绝大多数普通搜索（本样本 60k 展开只到 37.7 万条目），只在 VeryHigh 长搜中起作用。

需要用户决定：

- 是否接受“极少数场次路线变化、质量变化小于整个关掉转置剪枝的 3%/0.034 HP”来换取超长搜索的有界内存；
- 上限设在组合 1,000,000 条，还是其他数量；
- 是否同时限制 `ExpandedTranspositions`。

在得到决定前，不把上述上限写入生产默认路径。
