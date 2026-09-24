# 新鲜资源保路通道的探测上限（2026-09-24）

基线为上游 `4bfb4407`（0.46.2）。本文件记录一个会改变搜索决策的 Work 削减：`CombatBeamSolver.BeamRetentionPolicy.Ranking.cs`
的 `FindBestFreshResourceStandPat` 探测集合从「整组无上限」改为「beam rank 前缀 64」。结构化样本见
[`fresh-resource-standpat-probe-cap-20260924.json`](fresh-resource-standpat-probe-cap-20260924.json)。

## 位点与机制

同一个保留表循环（`CombatBeamSolver.BeamRetentionPolicy.cs:1280` 起）按药水数分组，对每组调用四个 roll-out
选择器：

```csharp
AddRequired(required, FindBestFreshResourceStandPat(group), limit);            // 无上限
AddRequired(required, FindBestStandPat(group, SearchRouteTraits.Scaling), limit);
AddRequired(required, FindBestStandPat(group, SearchRouteTraits.Resource), limit);
AddRequired(required, FindBestStandPat(group, SearchRouteTraits.Control), limit);
```

后三者各自 `Take(8)`，只有第一条没有上限：它把该组内所有「相对父节点新长出 `FutureResourceValue` 或
`ResourcePotential`」的节点全部结算一遍。`EvaluateStandPat` 按 `node.StateKey` memo，但实测旗舰根
`standPatProbes` 与 `StandPatCache` 条目数相等，说明跨探测批次没有可复用条目——探测数就是实际工作量，
且随 beam 宽度与满足资源筛选的节点数增长。VeryHigh（beam 135）下这条通道是保留表阶段最贵的单项。

## 实现

`nodes` 在进入该函数前已按 beam rank 排好（`SortByBeamRank`，`GroupBy` 保持源序），所以取前缀就是这条通道
自身排序下最靠前的探测点，不引入新排序键、不改比较器、不改资源筛选谓词：

```csharp
const int probeLimit = 64;
List<SearchNode> probes = nodes.Where(node => node.Parent is { } parent
             && (node.Snapshot.FutureResourceValue > parent.Snapshot.FutureResourceValue
                 || node.Snapshot.StrategicEffects.ResourcePotential
                    > parent.Snapshot.StrategicEffects.ResourcePotential))
    .Take(probeLimit)
    .ToList();
```

## 证据

### 旗舰根 ABBA（`EQ-IRONCLAD-ELITE-00`，DOP1、串行、240 s 预算，执行前固定 A B B A A B B A，4+4 次全部保留）

| 指标 | 无上限基线 | 上限 64 | 变化 |
|---|---:|---:|---:|
| 墙钟（均值） | 12.448 s | 10.488 s | **−15.7%** |
| 墙钟（区间） | 11.769 – 13.005 | 9.711 – 11.191 | 两臂区间不重叠 |
| `standPatProbes` | 6269 | 3687 | **−41.2%** |
| `totalExpanded` | 10834 | 8371 | −22.7% |
| `totalTransitions` | 54919 | 39693 | −27.7% |
| `forkCount` | 55860 | 40645 | −27.2% |
| selected worker 累计分配 | 2.197 GB | 1.587 GB | −27.8% |

8 次的预计战损、分数与终止边界逐项相同（52 / 9999279964 / `boundary=None`）。

探测数随上限的形状（同一根各 1 次，只用来看趋势，不作计时结论）：无上限 6269 → 64 档 3687 → 32 档 2766。

### 60 根 equivalence 语料的上限扫描

配置：VeryHigh / beam 135 / nodes 60000 / DOP1 / Smart / 60 s 预算，workers 4。判决只取两臂都 `valid` 且
`boundary=None` 的根；`projectedBattleHpLost` 上升为更差。所有被测上限都同时改变探测集合，因此没有一根是
「零决策代价」，下表的意义在于找出决策代价可接受的位置。

| 臂 | 实现 | 可比根 | 净战损 | 更差 | 更好 | 存活/阵亡翻转 | `standPatProbes` | `totalExpanded` |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| B | 上限 8，按 (ProjectedPlayerHp, Score) 预排 | 59 | +7 | 3 | 6 | 0 | −33.9% | −6.4% |
| C | 上限 8，按相对父节点资源增量预排 | 58 | −45 | 1 | 6 | **1** | −32.3% | −4.8% |
| D | 上限 8，两种预排各 8 名额取并集 | 59 | +20 | 5 | 4 | **1** | −31.3% | −5.5% |
| E | **上限 64，beam rank 前缀（采用）** | 58 | −7 | 1 | 2 | **0** | −13.7% | −2.7% |
| F | 上限 32，beam rank 前缀 | 58 | −53 | 4 | 3 | **2** | −22.1% | −2.3% |

- 三种 8 名额方案（B/C/D）各丢掉不同的路线：B 在 `FULL-REGENT-BOSS-00` 上 55→67、`FULL-REGENT-ELITE-03`
  59→68；C 和 D 把 `FULL-SILENT-BOSS-02` 从 69 战损存活变成只剩阵亡路线。D 想同时保住两类探测点，结果
  净战损反而更差（+20），因为保留名额是共享的（`AddRequired` 受 `limit` 约束），多占一个探测位不等于多留一条路。
- F 的净战损最好（−53，主要来自 `EQ-REGENT-BOSS-00` 72→7），但同时出现 2 根存活/阵亡翻转
  （`EQ-REGENT-BOSS-00`、`FULL-SILENT-BOSS-02`）。同理，C 的 −45 主要来自 `FULL-REGENT-BOSS-00` 55→38 与
  `EQ-REGENT-BOSS-00` 72→50，净 HP 会被单根大幅摆动主导，不能单独作为接受条件；存活路线是否存在才是。
- E 是唯一既没有翻转、净战损又不变差的档位，因此取 64。它改变的 3 根：`EQ-DEFECT-ELITE-00` 0→2（更差）、
  `EQ-NECROBINDER-BOSS-00` 7→2、`EQ-REGENT-BOSS-00` 72→68（更好）。

### 逐字段口径（`tools/OfflineSearchHarness/compare_results.py`，A 臂 vs E 臂）

60 根全部对齐、无缺根，比较 6447 个与时间/内存/GC 无关的字段：

| 组 | 差异字段数 | 涉及根数 |
|---|---:|---:|
| `rootState`（根续用戳记） | 0 | 0 |
| `catalog`（场景目录指纹） | 0 | 0 |
| `route`（选中路线逐项 turn/kind/cardId/potionId/targetCombatId/cardStateKey） | 231 | 15 |
| `continuations` | 15 | 15 |
| `solverMetrics`（非时间字段） | 399 | 34 |

根状态与目录指纹一致，说明两臂确实从同一检查点、同一场景集比较；路线与续用在 15 根上改变，是本改动
固有的决策代价，不是环境漂移。

## 复现

```bash
python tools/OfflineSearchHarness/run_plan.py --plan <plan.json> --workspace <ws> --workers 4
python tools/OfflineSearchHarness/compare_results.py --left <ws>/left/runs --right <ws>/right/runs \
  --left-prefix A --right-prefix E --out compare.json
```

两个使用注意（本轮踩过，属工具而非求解器）：

1. `compare_results.py` 在 `roots=0` 时仍打印 `=> IDENTICAL` 并 exit 0——空集会被当成通过，调用方要自己断言
   `roots` 数量。
2. 两侧不能指向同一个 `runs` 目录：不匹配前缀的标签会在两侧自己和自己对齐，60 根批次会报出 `roots=108`。
3. Windows 下宿主脚本需 `PYTHONUTF8=1`，否则个别 fixture 写 `search-messages.json` 时按 GBK 编码报错、整根作废。

## 结论与限制

- 收益集中在探测集合真被截断的宽保留层：旗舰根 −41.2% 探测、−15.7% 墙钟；整个 60 根语料平均只有
  −13.7% 探测、−2.7% 展开，因为多数批次本就不足 64 个。不声称普适百分比，也不外推为可见帧时间或 FPS。
- 上限是每次 `FindBestFreshResourceStandPat` 调用的前缀长度，不是整场预算；beam 更宽或更窄时受影响的批次数
  不同，收益形状随之变化。
- 语料只有 `coverage/equivalence` 的 60 根，`FULL-SILENT-ELITE-03` 两臂均 `TimeLimit`、
  `FULL-DEFECT-ELITE-00` E 臂 `TimeLimit`，这 2 根不计入判决。
- **未验证**：游戏内 `UnattendedTestRunner.StandPatProbes` 契约（双车道探测批次、注入异常传播、并行与串行
  等价）需要实机无人测试，本轮只跑了离线宿主；玩家检查点批量回放（`.local/` 无问题包）；DOP>1 与组合
  （Coordinator/portfolio）路径；可见 Steam 帧时间与 GC 暂停；No-GC 区域行为。
- 与本轮无关但顺带确认：`tools/BeamRankSortChecks` 在未改动的 `main` 上即报
  `Update probe for changed snapshot field: PlayerDead`（`BeamRankScore` 已读 `Snapshot.PlayerDead`，其桩件
  仍把所有字段声明为 `int`）。本轮未修改它，只在 PR 中登记。
