# CombatSolver 下一版本（草案）

## 简体中文

- 优化了涉及卡牌变形效果的路线搜索，减少重复整理候选牌池的工作。这类复杂战斗现在可以更快完成计算；卡牌生成规则、随机结果和最终路线选择规则保持不变。
- 修正搜索进度条在计算开始后很快接近满格、随后长时间停在末端的问题。进度现在按整次计算的时间预算平稳推进，并在最终候选复核完成前保留明确余量。
- “多策略路线搜索（实验）”保持默认关闭。常规搜索结果预计损失至少 8 HP 时，求解器会提示可以在“设置 > 性能”中尝试开启；这条提示可以永久隐藏。性能预设建议同样只在预计损失至少 8 HP 时显示。
- 夸克网盘安装包不再内置 RitsuLib。使用夸克包安装时，请按依赖列表单独安装 RitsuLib；Steam 创意工坊与 GitHub 安装包保持原有依赖方式。

## English

- Optimized route search for card-transformation effects by removing repeated candidate-pool work. Complex combats that rely on these effects can now finish calculation faster, while card-generation rules, random outcomes, and final route-selection rules remain unchanged.
- Fixed the search progress bar filling almost immediately and then appearing stuck near the end. Progress now advances against the budget for the whole calculation and keeps clear room until final candidate review is complete.
- “Multi-strategy Route Search (Experimental)” remains disabled by default. When the standard search projects at least 8 HP loss, the solver suggests trying it under Settings > Performance. This notice can be dismissed permanently. The performance-preset suggestion now uses the same threshold.
- The Quark Drive package no longer bundles RitsuLib. Install RitsuLib separately when using the Quark package. Steam Workshop and GitHub packages keep their existing dependency flow.
