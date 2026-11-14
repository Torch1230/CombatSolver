# 测试记录 · 卷 28

## 0.51.5 结算顺序与运行时交接

全部原生请求限制 120 秒，最后一次由启动器成功清理 `.local/headless-instances/reports-20261114`。已有成功结果仅在受后续行为变动影响时重跑。

| 场景 | 本轮结果 | 证明范围 |
|---|---|---|
| `CombatSolver.GcPolicyChecks -- portable-runtime` | 6 项通过 | 真实 CLR 便携分支完整回收、取消、默认 GC、NoGC 重启和手动释放 |
| `TURN-POWER-ORDERING` / FOOL / NIBBITS_WEAK | `69137d6f4f1f457190fd70aa97f46b24` Passed，31.07s | 准备时间／潜伏根系两种顺序、狂喜批量变牌有序手牌、再生先于灾厄；完整状态及 RNG |
| `BEAT-DOWN-DYNAMIC-TARGET` / REGENT / NIBBITS_WEAK | `d6cf2a6b8dae48a6a6f32d703138c864` Passed，8.16s | 狠揍自动打出群攻君王之剑，多敌完整状态与 RNG |
| `TERMINAL-POWER-PREFIX` / IRONCLAD / FUZZY_WURM_CRAWLER_WEAK | `c7a09847bd174965a3661922bbd9edd4` Passed，7.27s | 普通可用能力与提前胜利后的固定前缀探测 |
| `DOOM-RETAINED-BLOCK` / NECROBINDER / DECIMILLIPEDE_ELITE | `3dab74c121ba4e2fb825025d03b755a6` Passed，23.51s | 覆甲先于灾厄，死亡留场体保留格挡；全敌完整状态与死亡后处理 |
| `TURN-SETUP-UI-PEEK` / SILENT / GREMLIN_MERC_NORMAL | `4878c27d572f492e9012d8c2d1e2615b` Passed，9.60s | 停止／重新计算、窥视模式后接管手牌选择、原生进入 Play、忙碌状态清除 |
| `TOASTY-QOL-SINGLE-STEP-EXECUTE` / SILENT / GREMLIN_MERC_NORMAL | `ffe96049dff34492a7f98ffcb9f7ed1a` Passed，20.23s | 单步至第二回合，原生选择后续用并开始部署；`Reused=true`、`UnexpectedReplans=0` |

复跑入口：`tools/testing/run-unattended-test.ps1 -ScenarioId <场景> -CharacterId <角色> -EncounterId <遭遇> -EnemyCurrentHp 200 -TimeoutSeconds 120 -CleanupInstanceOnExit`；留场格挡用 `-EnemyCurrentHp 100`。Bash 使用同名 GNU 参数。GC 使用 `dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- portable-runtime`。

两个 UI 场景使用 `TOASTY_MITTENS` 遗物（`addWithoutObtainedEffects=true`）、敌方 HP 999、`ForceShortSearchOnly`。窥视场景短搜 1500ms；第二回合场景短搜 3000ms，并传 `SingleStepAfterInitialSearch`、`SingleStepResumeModeForTest=ExecuteCurrentTurn`。既有完整请求见 `coverage/fixtures/ui/toasty-qol-single-step-execute.json`。

额外执行 `TURN-SETUP-CONTROLS-MANUAL` + `VerifyTurnSetupControlsDuringInitialSearch`：`af2a7f943df04c78b88b79c882aa3fed` Passed（9.34s），实际覆盖初次搜索期间接管控件，证明范围限该入口。留场格挡的两次夹具失败和未验证项见[问题记录](../../issues/reports-20261114-logic.md#验证与实际失败)。原报告的 Mayhem 自动牌组合未恢复，完整整场、可见 UI 与 Unix 原平台未验证。

