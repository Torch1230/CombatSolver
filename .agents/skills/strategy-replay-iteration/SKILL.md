---
name: strategy-replay-iteration
description: Iterate and validate CombatSolver player-beat corpora, potion opportunity-cost policies, and out-of-combat growth strategies using OfflineSearchHarness and replay batches.
---

# Strategy & Replay Corpus Iteration

针对“玩家击败求解器”真实语料、药水省血/奖励抵扣策略或局外成长（如金币、MaxHp 成长、特定遗物计数、小偷遭遇等）进行策略调优时使用本指南。

## 1. 核心数据与策略入口

- **玩家真实语料清单**：位于 `coverage/player-beats/`（如 `curated_manifest.json` 划分 `dev` 与 `holdout`），集中记录玩家实战比求解器更优的 `PB-*` 样本。
- **策略实现层**：
  - `src/HotSearch/HotPotionPolicy.cs`：药水机会成本、`BLOOD_POTION` 按 `MaxHp` 动态估值、可配省血阈值（默认 9 HP）及战斗掉药净抵扣；
  - `src/HotSearch/HotEvaluator.cs`、`HotRouteQuality.cs`、`HotRolloutEvaluator.cs`：长远血量/成长/斩杀与回合边界估值；
  - `src/Search/`：`GrowthPolicy.cs`、`GrowthOpportunityPolicy.cs`、`PotionUsePolicy.cs`、`RelicCounterPolicy.cs`、`ActEndingBossPolicy.cs`、`TheftEncounterStrategy.cs` 等策略配置。

## 2. 推荐迭代闭环

```powershell
# 1) 策略回归自检（校验 11 项局外成长、成长开关、卡遗物/肉块及 BrightestFlame 等）
OfflineSearchHarness.exe --verify-strategy-policies

# 2) 单条样本深度剖析（定位为什么求解器没走出玩家路线：剪枝、估值还是预算）
OfflineSearchHarness.exe --replay-bundle <sample-zip> --dump-diff .local/tmp/pb-diff.json

# 3) 玩家击败语料批量跑分与成对 A/B 对比
OfflineSearchHarness.exe --bench-player-corpus coverage\player-beats\curated_manifest.json --split dev --profile Medium --dop 8 --dump-outcomes .local/tmp/pb-base-dev.json
OfflineSearchHarness.exe --bench-player-corpus coverage\player-beats\curated_manifest.json --split holdout --profile Medium --dop 8 --dump-outcomes .local/tmp/pb-cand-hold.json
OfflineSearchHarness.exe --compare-outcomes .local/tmp/pb-base-dev.json .local/tmp/pb-cand-dev.json --gate
```

- **先查因再调参**：先看样本是在候选中被截断（搜索深度/束宽），还是进入了候选但评分输给了更保守/短视路线（估值权重或药水折损）。
- **防过拟合**：至少同时看 `dev`、`holdout` 与通用真实根 `--bench-corpora`，并顺手确认 `--soak-deploy` 无新增意外重算。
- 如需补做实机检查点全套验证，可调用 `tools/testing/run-checkpoint-batch.ps1`（或 `.sh`）。
