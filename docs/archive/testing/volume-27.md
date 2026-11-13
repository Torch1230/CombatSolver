# 测试记录 · 卷 27

0.51.4，2026-11-13 定稿。

## 0.51.4 战斗逻辑与选牌续接

5 项定向原生合同通过，runId、实际失败、待验证项见[本批证据](../../issues/reports-20261113-logic.md#原生证据)。三份 JSON 夹具使用下列入口；洪流巨人改用 `WATERFALL-ARROW-TRANSITION` / `WATERFALL_GIANT_BOSS` / `coverage/fixtures/monsters/waterfall-arrow-transition.json`，横祸改用 `CATASTROPHE-EMPTY-DRAW` / `FUZZY_WURM_CRAWLER_WEAK` / `coverage/fixtures/cards/catastrophe-empty-draw.json`。

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId TIMBER-KILL-SPLASH -CharacterId FOOL -EncounterId CHOMPERS_NORMAL -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage/fixtures/cards/timber-kill-splash.json -StopAfterCombatRootSnapshotAssertion -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId CROSS-CHARACTER-ARROW -CharacterId IRONCLAD -EnemyCurrentHp 999 -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId CARD-CONTINUATION-ENCORE -CharacterId SILENT -EnemyCurrentHp 999 -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
```

```bash
./tools/testing/run-unattended-test.sh --scenario-id TIMBER-KILL-SPLASH --character-id FOOL --encounter-id CHOMPERS_NORMAL --enemy-current-hp 999 --monster-move-checks-path coverage/fixtures/cards/timber-kill-splash.json --stop-after-combat-root-snapshot-assertion --enable-no-gc-region-for-test 0 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id CROSS-CHARACTER-ARROW --character-id IRONCLAD --enemy-current-hp 999 --enable-no-gc-region-for-test 0 --timeout-seconds 120 --cleanup-instance-on-exit
./tools/testing/run-unattended-test.sh --scenario-id CARD-CONTINUATION-ENCORE --character-id SILENT --enemy-current-hp 999 --enable-no-gc-region-for-test 0 --timeout-seconds 120 --cleanup-instance-on-exit
```

