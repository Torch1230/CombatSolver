# 结束回合循环出口准入遗漏

## 报告证据

本轮读取的 146 份计算失败报告中，32 份包含 `临时循环出口 observation 越过了 action admission frontier`。这是症状计数，不能直接算成同根已修复数量。

代表 `4a9d9d3a83d04cc682550e9fbf8e6725` 的环境声明为 CombatSolver 0.35.1.0 / 游戏 0.111.0。失败候选为 T5、44 个动作，末动作是 T4 EndTurn；下一次 `TryPrepareParallelExpansion` 在 `TryMarkExpandedState` 检出残留 observation。前面的 `TURN_LAYER_BUDGET reason=nodes` 记录 completed_turns=3、play_depth=15、expanded=1479、node_budget=1475、forced_end_turn=43，与第四回合预算结束入口吻合。

## 根因与修复

`SolveCore` 的回合层预算和软时间预算分支直接调用 `BuildAcceptedEndTurnNodes`。这个入口调用 `AttachCycleSchedulingEvidence` 产生临时出口观测，但原来只有 `CommitCycleExitObservation`，它处理已传播观测，不处理 `PendingCycleExitObservation`；随后直接通过转置准入，把节点交给下一回合。

上游 d3683683 修复了普通串行/并行提交在父租约撤销后的准入判断，但没有覆盖这个独立入口。不能以已有父租约撤销补丁认定当前症状已经解决。

修复复用 `GenerateRawEndTurnCandidates`、`PruneCommittedCrossTurnCandidates` 和既有 materialization：同父节点的全部结束回合选择先完成批次准入，再交给转置表，最多签发一个出口资格。拒绝或未转交快照由 `OwnedExpansionBatch` 释放。不删除展开入口校验，不额外增加循环预算，也不改原版回合结算。

## 验证与边界

`tools/EndTurnAdmissionChecks` 提取并编译实际入口及相关生产管线方法，链接真实批次容器。旧入口在相同输入下复现 pending observation 越界；修复后 33 项检查通过，包括多选择的单一出口、父租约撤销、终结边界、剪枝/转置拒绝以及异常和提前停止的所有权。

模拟、出口质量/单张票据签发、转置判断是测试替身。报告日志、当前调用链与最小管线失败相符，但未运行原包恢复、原生差分或完整预算搜索；不将全部 32 份报告批量认定为已解决。完整日志和问题包保持在本地忽略目录。
