---
name: search-performance-optimization
description: Tune or optimize CombatSolver's HotSearch planner, evaluator, pruning, beam retention, rollout evaluation, potion policies, or parallel search runtime latency.
---

# Search Performance & Quality Optimization

调整搜索质量、求解速度、束宽/节点配额、转置去重、局面评分或药水/成长策略时使用本指南。**一切调优以成对 A/B 对比和实测数据为依据，不做盲调与局部过拟合。**

## 1. 模块边界（`src/HotSearch/` + `src/Search/`）

| 文件 | 职责 |
| --- | --- |
| `src/HotSearch/HotSearchCoordinator.cs` | 多配置搜索入口、单束/多束编排、药水梯度尝试与时间/节点预算控制 |
| `src/HotSearch/HotParallelExpander.cs` | 确定性多线程扩展与归并（严格保证 `DOP=1 == DOP=N`） |
| `src/HotSearch/HotBeamRetention.cs` | 束宽筛选、局部多样性保留与跨回合前沿收敛 |
| `src/HotSearch/HotEvaluator.cs` / `HotRouteQuality.cs` | 分支局面评分与最终路线比较（含掉血、击杀、成长与药水净值） |
| `src/HotSearch/HotRolloutEvaluator.cs` / `HotEndTurnRiskEvaluator.cs` | 回合边界 Rollout 前瞻与结束回合即时风险评估 |
| `src/HotSearch/HotTranspositionTable.cs` / `HotNodeArena.cs` | 开放定址转置支配表与节点/状态池化复用 |
| `src/HotSearch/HotPotionPolicy.cs` | 药水使用机会成本、血瓶 MaxHp 折算、省血阈值（默认 9 HP）与奖励抵扣 |
| `src/Search/*.cs` | 轻量只读策略快照与权重 DTO（`SearchPolicySnapshot`、`SolverSearchProfile`、`SolverWeights`、`GrowthPolicy`、`PotionUsePolicy`、`RelicCounterPolicy`、`ActEndingBossPolicy`、`TheftEncounterStrategy`） |

## 2. 核心不变量

- **严格多线程确定性**：无论调整任务切片、转置支配还是归并逻辑，`DOP=1` 与 `DOP=8` 必须产出一致路线与评分。
- **零托管热路径分配**：扩展循环内只操作值类型状态，复用 `HotNodeArena`、`PackedStateArena` 与 `HotExecScratch`，避免在 `HotSearch` 中引入列表/字典分配或 `MegaCrit.Sts2.Core.Models`、`SolverSettings.Current`、`Entry.Logger` 依赖。
- **职责不越界**：`HotEvaluator` 专注局面估值，`HotBeamRetention` 专注前沿裁剪，`HotSearchCoordinator` 专注预算编排，避免把特例硬编码塞进统一排序里。

## 3. 成对 A/B 验证工作流

每次调整策略权重、剪枝逻辑或搜索参数前，先在相同提交与条件下用 `OfflineSearchHarness` 导出基线，再用同一份脚本/指令集跑候选版做成对对比（详见 [OFFLINE_SEARCH_HARNESS.md](../../../docs/OFFLINE_SEARCH_HARNESS.md)）：

```powershell
# 1) 策略端到端快速自检 + DOP 确定性校验
OfflineSearchHarness.exe --verify-strategy-policies
OfflineSearchHarness.exe --verify-hot-parity --dop 8

# 2) 真实现场根语料（建议 dev + holdout 分离，防过拟合）
OfflineSearchHarness.exe --bench-corpora --tier dev --profile Medium --dop 8 --dump-outcomes .local/tmp/base-dev.json
OfflineSearchHarness.exe --bench-corpora --tier holdout --profile Medium --dop 8 --dump-outcomes .local/tmp/base-hold.json

# 3) 高分支选牌与玩家击败语料（含 PB-* 典型弱点样本）
OfflineSearchHarness.exe --bench-choices --profile Medium --dop 8 --dump-outcomes .local/tmp/base-choices.json
OfflineSearchHarness.exe --bench-player-corpus <manifest.json> --split all --profile Medium --dop 8 --dump-outcomes .local/tmp/base-pb.json

# 4) 成对 A/B 对比（关注净胜/战损、经济价值 V、平均与 p90 耗时、胜率变化）
OfflineSearchHarness.exe --compare-outcomes .local/tmp/base-dev.json .local/tmp/cand-dev.json --gate
```

- **性能与卡顿排查**：修改扩展循环、转置或 Rollout 时，除对比搜索均值与 `p90` 耗时外，建议配合 `--soak-deploy --fights 200 --pool high-risk` 确认不会因路线不稳定导致实机部署阶段重算上升。
- **并发提示**：若通过多个 Worker 并行跑基准，每个 Worker 固定 `--dop 4` 且将 `--dump-outcomes` / `--out` 指向各自隔离目录，先统一串行编译好二进制再分派任务。
