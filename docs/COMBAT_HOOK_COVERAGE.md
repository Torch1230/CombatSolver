# CombatSolver 战斗钩子覆盖目录

> CombatSolver `0.15.0`，游戏 `0.111.0`，模拟核心 `embedded`。本文件由 `tools/CoverageCatalog` 生成，不手工编辑。

## 汇总

| 分类 | 条目 | 未分析 | 待实现 | 引擎精确 | 引擎推断 | 引擎不支持 |
|---|---:|---:|---:|---:|---:|---:|
| Affliction | 3 | 0 | 0 | 0 | 0 | 1 |
| Card | 1186 | 0 | 0 | 211 | 159 | 227 |
| Enchantment | 42 | 0 | 0 | 11 | 0 | 0 |
| MonsterMove | 339 | 0 | 0 | 0 | 0 | 0 |
| Monster | 352 | 0 | 0 | 15 | 0 | 0 |
| Orb | 18 | 0 | 0 | 14 | 0 | 3 |
| Potion | 67 | 0 | 0 | 17 | 0 | 50 |
| Power | 520 | 0 | 0 | 162 | 0 | 61 |
| Relic | 508 | 0 | 0 | 82 | 0 | 9 |

## 有效支持状态

| 状态 | Hook 数 | 实机/运行时证据 | 静态证据 | 无独立证据 |
|---|---:|---:|---:|---:|
| Exact | 2302 | 1711 | 1 | 590 |
| OutOfScope | 733 | 11 | 126 | 596 |

## 主动效果运行证据

只有 `EngineMirror` 与 `SolverCompensation` 的运行时差分证据才计入本节；仅完成注册或静态分类不代表跨回合时序正确。

| 分类 | 主动 Exact | 有运行证据 | 尚无运行证据 |
|---|---:|---:|---:|
| Affliction | 1 | 1 | 0 |
| Card | 560 | 560 | 0 |
| Enchantment | 13 | 13 | 0 |
| MonsterMove | 334 | 334 | 0 |
| Monster | 2 | 2 | 0 |
| Orb | 15 | 15 | 0 |
| Potion | 66 | 66 | 0 |
| Power | 370 | 370 | 0 |
| Relic | 193 | 193 | 0 |

## 分支内计算变量

- 含 CalculatedVar 的卡牌：43
- 缺少分支内公式：0

## 持久遗物预测状态

- 缺少统一指纹/续用描述：0

## 原生结算后自动重搜

以下行为可无人值守执行，但不会在同一条静态路线中跨过原生动态结算边界。

## 明确排除范围

- Combat：415 Hook
- DeprecatedPlaceholder：1 Hook
- MultiplayerOnly：77 Hook
- OutOfCombat：166 Hook
- TestOrMock：74 Hook

## 待有效适配


## 未分析条目
