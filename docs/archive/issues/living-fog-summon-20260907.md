# 新召唤自爆敌人导致搜索失败

原包：`CombatSolver-LIVING_FOG_NORMAL-20260907-202430-817.zip`，bundle `5663b1c69ed34cca83f18b47e22a94b6`。环境记录 CombatSolver `0.33.0.0`、RitsuLib `0.5.19.0`。Preflight 为 materials_valid，未执行原包精确 RestoreOnly 或完整路线回放；本机材料保留于 `.local/issue-bundles/living-fog-20260907/raw/`。

首个异常：搜索 generation 18，T2、action_count 4、EndTurn，`BranchMonsterAi.ResolveFollowUp` 抛出“行动 EXPLODE_MOVE 没有后继状态”；手动重算 generation 19 同样失败。

回归来源：`3af25e4` 将下一行动准备从本轮执行过的怪物扩展到当前全阵容，修复新召唤双尾鼠尚未选择初始意图的问题；但 GasBomb 的状态机已经初始化为 EXPLODE_MOVE，因此它没有 NeedsInitialRoll，又尚未在本轮行动，被错误推进到不存在的后继。

原版状态机在首次行动执行前保留已经确定的行动。修复在 NeedsInitialRoll 分支之后要求存在本轮执行记录才推进其余 AI。已确定的首次行动保留，需要 Roll 的新个体继续 Roll，死亡/离场及其他状态边界沿用既有逻辑。

最小失败基线 `9c5be94ee8ae48aeac82d4ef1b42a5d4` 通过正式 EndTurn 回放准确复现同一异常。用例从 LIVING_FOG_NORMAL 的 BLOAT_MOVE 开始，依次比较召唤后的下一玩家回合、再下一回合自爆后的完整原生状态，并验证两处 Fork。第一次修复后运行 `e2d1be3c5d1147f9ba7b39acb74720bd` 已通过首回合完整差分，但测试额外断言误把首次召唤数量写为 2；已改为读取原版 BloatAmount，不修改生产行为。

最终通过证据与复跑命令见 [测试矩阵](../../TEST_MATRIX.md)。
