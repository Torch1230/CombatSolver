# CombatSolver 开发笔记

这里只记录当前未发布的行为变化。发布定稿后将该批次完整移入历史卷。

0.50.1 本地定稿的智能药水修复及 PR #213、#224、#226 见[历史卷22](archive/development/volume-22.md)，玩家说明见[0.50.1 更新日志](releases/0.50.1-RELEASE_NOTES.md)。外部发布状态以对应标签及发布记录为准。

0.50.0 的 PR #207、PR #211、智能药水与组合补搜定稿及性能研究见[历史卷20](archive/development/volume-20.md)。其他记录见[归档索引](archive/development/README.md)。

## 下一版本（开发中）

### 回合开始里结束回合

原版 `StartTurn` 在 Start 阶段先清空 `PlayersReadyToEndTurn`，再跑发牌和 AutoPrePlay；这之后的 `PlayerCmd.EndTurn` 照常生效，回合开始一结束整个回合就结束，玩家一张牌都打不了。混乱打出虚空形态、历史课重放观者 Mod 的结末都是这样。搜索的三处入口（整轮推进、回合检查点恢复、回合开始选牌恢复）在回合开始之后把这个请求丢掉了，于是以为那一回合还能出牌，实机却跳过，路线每次都对不上；玩家报的是历史课加结末「反复跳过回合」。

Simulation 现在在回合开始先清请求，对应原版那次清空；回合开始跑完还有请求，就接着推进下一轮。被跳过的回合不留动作，和打出结束回合牌之后的表示一致，选牌接在同一个游标后面。连续 64 次视为不会终止，直接抛出。

Runtime 的回合准备接管 AutoPrePlay 时会等这一回合进入可出牌状态；回合开始里结束回合会禁用玩家操作，而回合结束要等 AutoPrePlay 跑完，两边互等 30 秒后 `TimeoutException` 打死回合循环，战斗卡住。本地玩家已经准备结束回合时直接放手。

验证：`tools/testing/run-unattended-test.ps1 -ScenarioId MAYHEM-VOID-FORM-TURN-START-END -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -ForceShortSearchOnly -SearchMaxDegreeOfParallelismForTest 1 -InitialEnemyCurrentHpsJson '[57]' -ClearPlayerPiles -InitialPlayerEnergy 3 -PowerId MayhemPower -PowerAmount 1 -PowerTarget Player -CardsJson '[{"cardId":"STRIKE_IRONCLAD","pile":"Hand","count":3},{"cardId":"STRIKE_IRONCLAD","pile":"Draw","count":5},{"cardId":"VOID_FORM","pile":"Draw","count":1},{"cardId":"STRIKE_IRONCLAD","pile":"Draw","count":5}]' -ExpectedInitialCombatEndedTurn 4 -ExpectedFinishedTurn 4 -ExpectedUnexpectedReplansAtMost 0`。虚空形态是抽牌堆第 6 张，第 2 回合摸完 5 张后由混乱打出，整回合跳过。只装求解器时本分支 Passed（`e06cbaa1628844da8c1c3f5b9d3c2589`），`origin/main` 挂在首轮预计第 3 回合打完（`392ee2c06cdf452a97a89412474fb8b8`）；只有模拟修复、没有 Runtime 修复时，整场部署在第 2 回合 AutoPrePlay 超时卡住。

### Q015 路线保留（PR #227）

在既有 Beam 名额内，按用药数量保留已降低敌方格挡、仍有合法攻击且敌方剩余生命不超过可执行手牌估值的收尾续路；合法攻击数沿现有手牌遍历冻结。该估值用于保路资格，实际攻击由模拟器结算。Discard／DiscardAndDraw 上下文优先保留预计安全的进攻续路。Control 在原有结束回合探测名额中保留一个可能由延迟伤害获胜的代表，再由实际试算决定。旧敌退出且新敌入场的 AliveEnemyMask 变化进入 RevivalWindow 通道。状态等价、终局政策、预算和战斗语义沿用原实现。

Testing 在完整原生检查点已核验、当前手牌上限为默认 10 时迁移旧报告缺失的字段，其他续用字段继续严格比较。录制预测路径诊断支持显式指定候选池观察步数。贡献者的五主题自主搜索、原生部署、固定哨兵及未验证范围见[Q015 记录](issues/q015-route-quality.md)。

### 计划续搜复用完整胜利证明（PR #229）

严格组件认证根将已有及首成员新发现的完整胜利传给后续计划，沿原回复上界与政策门禁剪枝。Runtime 在重建 NoGC 的检查点请求一次压缩完整回收，确认新的完成信息与弱引用哨兵后重建区域。贡献者的 Linux 11 根交错性能数据、原生合同、失败及平台限制见[性能记录](performance/plan-witness-propagation-20261006.md)。

### Smart 药水审计独立成员时间（PR #230）

Smart 无强制用药、主结果无药且根有可搜索药水时，在主搜索耗尽时间后为首个可选药水层提供软预算的 1/5（1–30 秒）。该层按软预算保留结果，关联取消令牌提供 1/6 收尾时间，显式层数上限固定为一层；其他政策继续沿原入口执行。后置开局前缀补搜使用请求账本。已耗尽请求会增加这一层的实际耗时。根因、原包数据与验证范围见[Q011 记录](issues/q011-worldlines-20261006.md)。
