# 测试记录 · 卷 26

0.51.3，2026-11-13 定稿。

## 0.51.3 同牌多减益与部署异常

2026-11-13，游戏 0.113.0。以下三项复用一个 headless 实例，每请求上限 120 秒；最后一项清理实例成功。

| 场景 | 本轮结果 | 范围 |
|---|---|---|
| `LAMP-HOBBLING-SHOT` | `f5f4fc2679534eb7803d7b24857a8616` Passed | 升级 HOBBLING_SHOT 同牌两个减益翻倍，敌方回合末 Weak3／Vulnerable3，完整原生差分。失败定位来自报告 `1a585d8b` 的 Vulnerable1／3；本轮未运行修改前夹具。 |
| `LAMP-INDIRECT-TEMPORARY-STRENGTH` | `60b34bb7f25e4aaca673f201d63ca724` Passed | 间接减益保持油灯次数，随后直接减益翻倍，完整续用状态。 |
| `MOON-NORMALITY-SHIV` | `e878d8dc31904844a00f2f605075821c` Passed | THE_MOON+／NORMALITY／SHIV／VITRIOL+ 的逐牌原生差分；飞刀正常造成伤害并消耗。保留为生成牌和出牌次数限制的组合哨兵。 |

前两项 fixture 和间接减益合同对应本轮模拟修复；第三项仅验证其最小组合在当前原生环境正常。报告 `f2d069a5` 的原始飞刀异常仍待异常栈定位。部署对 `GameAction.Exception` 的检查与失败暂停采用静态调用链核对和 Release 编译，未执行原生故障注入。未恢复八份玩家整场环境，未跑全量回归。

两份 JSON 夹具的复跑入口如下；月亮组合将 ScenarioId 换为 `MOON-NORMALITY-SHIV`，路径换为 `coverage/fixtures/cards/moon-normality-shiv.json`。间接减益合同使用 `LAMP-INDIRECT-TEMPORARY-STRENGTH`、角色 `SILENT`，省略 MonsterMoveChecksPath 与 StopAfterCombatRootSnapshotAssertion。

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId LAMP-HOBBLING-SHOT -CharacterId FOOL -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage/fixtures/relics/lamp-hobbling-shot.json -StopAfterCombatRootSnapshotAssertion -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
```

```bash
./tools/testing/run-unattended-test.sh --scenario-id LAMP-HOBBLING-SHOT --character-id FOOL --enemy-current-hp 999 --monster-move-checks-path coverage/fixtures/relics/lamp-hobbling-shot.json --stop-after-combat-root-snapshot-assertion --enable-no-gc-region-for-test 0 --timeout-seconds 120 --cleanup-instance-on-exit
```

