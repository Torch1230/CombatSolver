# 动态目标类型读取实机状态

## 证据

报告 `949705efb1644dbcbc9532a2a2b419b9` 声明 CombatSolver 0.34.7 / 游戏 0.111.0。generation=25 在 T3 捕获搜索根，玩家没有 SEEKING_EDGE_POWER，手中有 SEEKING_EDGE。失败候选的预测 T17 / action_count=56，尝试 SOVEREIGN_BLADE，TargetIndex=-1、TargetCombatId=null；`BespokeCardMirrors.SovereignBladeOnPlay` 走单体分支读取 `context.Target`，抛出 `CardPlay has no target creature`。失败路线前缀没有打出 SEEKING_EDGE。稍后的实机 T4 状态含 SEEKING_EDGE_POWER:1。

日志没有直接记录目标 getter 每次读取的时刻，因此实机能力变动与失败候选之间的精确交错仍未原包重放。当前源码和下述最小合同直接证明该状态来源缺口。

## 根因

`GetTargetType` 原先仅在分支对应能力大于 0 时覆盖君王之剑/小刀为 AllEnemies。能力不在分支中时，switch 落入 `card.Preview.TargetType`。游戏 0.111.0 的原版动态 getter 通过卡牌 owner 的 Creature 查询 HasPower，调用者不能在后台目标枚举入口依赖它总是在正确的影子上下文中执行。

这造成负分支绕过已捕获状态：实机后来获得能力、预测路线未获得时，搜索可能生成不带单体目标的攻击；实际预测 OnPlay 按分支能力仍执行单体攻击，直接失败。小刀的 FanOfKnives 走同样路径，即使当前代表报告只有君王之剑，也需要同步修复这一相同缺口。

## 修复与验证

两个特定卡型完整依据分支能力选择 AnyEnemy / AllEnemies，不再在“无能力”一侧调用原生 getter。其他卡型和非 SimulatedCombatState 仍保持原入口，没有删除目标缺失异常、跳过候选或改变能力结算。

37 项合同直接链接生产选择器；旧代码在“分支无能力、实机有能力”时复现错误。修复后两张卡均覆盖实机变化、分支能力增减、其他 owner、独立分支与原回退。用例为确定性模型/状态替身，不验证真正的 Fork 或伤害管线。原生差分、完整游戏搜索和原报告回放尚未执行。

本轮检查的“终局后仍有动作”报告中发现 Regret 伤害来源，但没有建立新的独立根因；未修改终局校验，也不将这组报告计入当前修复。
