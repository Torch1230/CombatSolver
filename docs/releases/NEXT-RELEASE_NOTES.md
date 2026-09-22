# CombatSolver 下一版本更新日志（草稿，版本号待定）

## 简体中文

- 修复部分战斗在回合开始生成卡牌、触发能力变化后，路线计算失败的问题。
- 修复智能用药尝试插入格挡药水后，因手牌状态变化或战斗提前结束而导致计算失败的问题。调整后的出牌顺序无法继续执行时，会保留原来的有效路线。
- 感谢 [ltlly](https://github.com/ltlly) 提交的 [PR #123](https://github.com/Torch1230/CombatSolver/pull/123)：优化部分重复出牌循环的计算，更快找到可执行的击杀路线；改善过量临时格挡对路线选择的干扰，并修正部分依赖整场出牌历史及后续生成卡牌的路线判断。
- 感谢 [ltlly](https://github.com/ltlly) 提交的 [PR #123](https://github.com/Torch1230/CombatSolver/pull/123)：重复出牌序列合并显示，标注循环次数，并用蓝色虚线标出范围，让长路线更易阅读；未展开的后续回合会提示剩余数量。
- 感谢 [ltlly](https://github.com/ltlly) 提交的 [PR #124](https://github.com/Torch1230/CombatSolver/pull/124)：找到满足已开启战损目标的完整路线后，更及时结束追加计算，同时保留成长、遗物、偷窃、用药目标和已知治疗机会的判断。调整默认搜索组合，减少重复计算并改善部分高压局面的路线。
- 感谢 [s1f102500012](https://github.com/s1f102500012) 提交的 [PR #125](https://github.com/Torch1230/CombatSolver/pull/125)：减少路线计算中的重复处理开销。这项优化保持原有路线选择规则，实际耗时改善因战斗而异。

## English

- Fixed route calculation failures in some fights when cards generated at the start of a turn trigger Power changes.
- Fixed calculation failures when smart potion use tries inserting a Block Potion that changes card states or ends the fight earlier. If the adjusted action sequence can no longer be executed, the solver keeps the original valid route.
- [PR #123](https://github.com/Torch1230/CombatSolver/pull/123) by [ltlly](https://github.com/ltlly): Improved calculation for some repeating card-play loops, helping the solver find executable winning routes sooner. Reduced the influence of excess temporary Block on route selection and corrected some route decisions involving combat-wide card-play history and cards generated later in the fight.
- [PR #123](https://github.com/Torch1230/CombatSolver/pull/123) by [ltlly](https://github.com/ltlly): Repeating action sequences are grouped with a repeat count and a blue dashed underline, making long routes easier to read. Routes that extend beyond the displayed turns now show how many turns remain hidden.
- [PR #124](https://github.com/Torch1230/CombatSolver/pull/124) by [ltlly](https://github.com/ltlly): Additional searches stop sooner after finding a complete route that meets the enabled HP-loss target, while continuing to account for growth, relic, stolen-resource and potion goals, as well as known healing opportunities. The default search combination reduces redundant calculation and improves routes in some high-pressure fights.
- [PR #125](https://github.com/Torch1230/CombatSolver/pull/125) by [s1f102500012](https://github.com/s1f102500012): Reduced redundant work during route calculation while preserving the existing route-selection rules. The effect on calculation time varies by fight.
