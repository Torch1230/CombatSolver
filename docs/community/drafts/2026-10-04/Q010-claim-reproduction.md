# Q010 认领者复现记录（fengzhenhong，2026-10-04）

对应批次：[Q010.md](Q010.md)（维护者本地审核稿）。本文件只记录认领者在当前源码上的实际复现与定位结果，
不修改审核稿本身。分支 `perf/batch-q010`，基点 `4533f6bb`。

## 复现方式

五个主题统一从 `combat_start` 同根起搜：

```
tools/replay/run-checkpoint-batch.ps1 -ReplayMode SearchOnly -CheckpointSelector start
```

环境：游戏 v0.111.0、RitsuLib 0.111.0、.NET SDK 10.0.400，headless 全程无需打开游戏窗口。

## 结果

| 主题 | 遭遇 | 审核稿原值 | 审核稿改善值 | 本轮实测 | boundary | 耗时 | 展开 | 性质 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| O041 | HUNTER_KILLER_NORMAL | 17 | 12 | **17** | None | 5.3s | 3062 | 已收敛，策略缺口 |
| O042 | THIEVING_HOPPER_WEAK | 5 | 1 | **5** | None | 34.8s | 50512 | 已收敛，策略缺口 |
| O043 | SPINY_TOAD_NORMAL | 12 | 8 | **18** | TimeLimit | 180.1s | 130228 | 未收敛，预算受限 |
| O044 | SLUMBERING_BEETLE_NORMAL | 8 | 5 | **5** | None | — | — | 已达目标 |
| O045 | LOUSE_PROGENITOR_NORMAL | 13 | 1 | **1** | None | 30.1s | — | 已达目标 |

本批因此分成三类，处置方式不同：

- **A 类，目标已达成**：O044、O045。当前源码同根即达改善值，只需回归夹具与前后对照。
- **B 类，已收敛但次优**：O041、O042。搜索跑完（`boundary=None`）仍得不到改善值，
  属真实搜索/策略缺口。
- **C 类，未收敛**：O043。搜索把 profile 软预算耗尽仍未收敛，当前预算下无法判定是否存在更优路线。

## A 类：O044、O045

O045 实测首段 `POMMEL_STRIKE / BURNING_PACT / IMPERVIOUS / BASH`，与审核稿改善路线同构
（审核稿记 `IMPERVIOUS / BURNING_PACT / POMMEL_STRIKE / STOKE`），终值同为 1 战损、endTurn=5；
差别仅是搜索自行重排了同一组卡的首段顺序。O044 实测 projHP=5、0 瓶、endTurn=6，
首段已含 `GENESIS / PARTICLE_WALL / VENERATE`。

这两项按回归夹具交付，不含新的生产补丁。

## B 类之一：O041

单次运行时序：

```
POLICY_BASELINE          kind=potion_free won=True  hp_deficit=17  enemy_hp=0
SEARCH_INTERIM_RESULT    potions=0  projected_battle_hp_lost=17
POLICY_BASELINE          kind=potion_free won=False hp_deficit=12  enemy_hp=70
POLICY_BASELINE          kind=potion_free won=True  hp_deficit=26  enemy_hp=0
POLICY_BASELINE_OVERRIDE kind=potion_free won=True  hp_deficit=11
SEARCH_INTERIM_RESULT    potions=1  projected_battle_hp_lost=17
SMART_POTION_GRADIENT layer=1 won=True hp_deficit=-5 saved=16 required=9 acceptable=True selected=True
SMART_POTION_GRADIENT result stop=threshold_met maximum=1 selected_potions=1
```

当前源码在同根上**已找到零药的 11 HP 路线**（`POLICY_BASELINE_OVERRIDE`，`kind=potion_free`、
`won=True`），不劣于审核稿的 12 HP 零药目标；但 `SMART_POTION_GRADIENT` 仍选了带药层作为最终结果。

需要说明：`projectedBattleHpLost=17` 与 `strategicHpDeficit=-5` 是**两个不同口径**
（前者为战斗内累计伤害，后者计入治疗），并非矛盾。
`IsBetterPotionPolicyResult` 经 `RouteQualityPolicy.Compare(..., RouteQualityProjection.PotionPolicy, ...)`
走 strategic 口径比较，因此选带药层在代码口径下自洽。

**需要维护者确认**：审核稿表格使用「原始战损 17 → 12」这一战损口径。
若按战损口径，带药层的 17 与零药的 17 相同，未带来收益；若按 strategic 口径则成立。
该口径决定 O041 的缺口性质与修复方向。

## B 类之二：O042

`boundary=None`、耗时 34.8 秒（profile 软预算 300000 ms）、展开 50512 节点
（上限 500000），**搜索已收敛但未能找到优于 5 战损的路线**。

portfolio 6 个成员中 4 个以 `MemoryHeadroomInsufficient` 跳过，
实际参与比较的是 index=0（beam=135）与 index=5（beam=54）。

实测首段：`EndTurn / FEEL_NO_PAIN / SHRUG_IT_OFF / ALCHEMIZE / TAUNT / EndTurn / EndTurn / BRAND`；
审核稿改善首段 `EndTurn / TAUNT / SHRUG_IT_OFF / DEFEND_IRONCLAD`，endTurn=6。

`MemoryHeadroomInsufficient` 让大部分 portfolio 成员无法运行，是 O042 与 O041 共同的可疑点，
但本轮尚未确认该记忆门限是否在本机环境（NoGC 区域、进程驻留）下判定过严。

## C 类：O043

对照实验（同根、同政策，只改外层超时）：

| 运行 | TimeoutSeconds | elapsed_ms | expanded | boundary | projHP |
| --- | --- | --- | --- | --- | --- |
| run 1 | 300 | 180109 | 130228 | TimeLimit | 18 |
| run 2 | 900 | 200597 | 127399 | TimeLimit | 18 |

把外层超时放大到 900 秒并不改变结果，瓶颈是该包 profile 自身的
`softTimeBudgetMilliseconds = 180000`（beam 90、maxExpandedNodes 250000、preset High）。
节点预算远未触顶（约 13 万 / 25 万），**先耗尽的是时间预算**。

已排除的两处嫌疑（负结果）：

1. **循环 region 计数**：`BEAM_WIDTH_PORTFOLIO result members=6 ran=1 compared=1`，
   本轮只有 index=0 运行，计数不跨成员累加；`CycleRegionGlobalAdmissionBudget` 的作者自检
   覆盖停滞上限与 512 硬上限，`CycleRegionRetentionTransaction` 按 turn 复制预算字典。
   计数高只因该遭遇的 region 数量本身大。
2. **portfolio 跳过 5 个成员**：`src/Search/BeamWidthPortfolioGate.cs` 第 7-9 行明确
   `FrontierExhausted` 口径为「基线没有被任何上限截断（`SearchBoundaryReason.None`）」。
   O043 基线以 `TimeLimit` 收束，跳过精炼成员、把预算留给基线是**设计行为**。

结论：O043 的 18 战损是**时间预算耗尽导致的未收敛**，不是候选被错误丢弃。
要论证能否恢复 8 战损，需在提高该包 profile 软预算的同根条件下对照；
但直接调大预算会使本次质量对照失去可比性（验收要求同政策、同预算），需与维护者确认是否在批次范围内。

## 本机 headless 环境记录

1. `run-checkpoint-batch.ps1` 默认把 RitsuLib 拼到 Steam 工坊路径
   `.../workshop/content/2868840/3747602295`。本机实际在 `E:\Slay the Spire 2\mods\STS2-RitsuLib`，
   省略 `-RitsuWorkshopRoot` 会得到 `invalid_archive`。
2. 启动器 `tools/testing/run-unattended-test.ps1:422` 硬校验 CombatSolver.dll / manifest /
   MemoryCleaner.exe 三件产物，缺 MemoryCleaner.exe 直接 throw，批处理表现为 `process_crash`。
   该 exe 是 `net48` 项目，需 .NET Framework 4.8 引用程序集；本机无 VS / SDK / winget，
   改用 NuGet `Microsoft.NETFramework.ReferenceAssemblies.net48` 解出引用程序集安装后构建通过
   （0 警告 0 错误），未改动仓库任何文件。

## 未验证项

- 尚无任何代码改动，因此没有「修改前失败 / 修改后通过」对照。
- O041 与 O042 的根因尚未定位到具体代码路径；`MemoryHeadroomInsufficient` 是否判定过严未确认。
- 五个主题均未执行 `DeploySolver` 原生部署，未做成对耗时对照。
- 两个哨兵（相邻正确场景、未改目标）尚未选定与运行。
- 未运行可见 Steam 会话，无 FPS 与帧时间结论。