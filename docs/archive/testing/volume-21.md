# PR #224 与 PR #226 合并验证（2026-10-06）

保留合并时的证据、失败与未验证项；智能药水修复的本轮验证见[当前记录](../../issues/potion-opportunity-20261006.md)。

## Ctrl+F9 面板可见性（PR #226）

`OVERLAY-VISIBILITY-LIFECYCLE` 在原生单人战斗中验证快捷键输入、已有及新建 CanvasLayer 的隐藏状态、禁用／手动／搜索中／停止显示、监控刷新，以及 `BeginCombat` 重置后的初始化消费与恢复显示。初始化置位在重置返回时断言，可操作边界的初始化完成在等待旧会话释放后断言。使用现有停止开关在初始合同后结束，搜索状态显示通过 UI 入口注入。

PowerShell：`pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId OVERLAY-VISIBILITY-LIFECYCLE -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -StopAfterCombatRootSnapshotAssertion -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit`；Bash：`./tools/testing/run-unattended-test.sh --scenario-id OVERLAY-VISIBILITY-LIFECYCLE --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --stop-after-combat-root-snapshot-assertion --enable-no-gc-region-for-test 0 --timeout-seconds 120 --cleanup-instance-on-exit`。

2026-10-06 本轮失败证据：PR 头 `0725fe21` 加入新图层断言后，runId `2f55f493930144c6800f6835042f134a` 在新建 CanvasLayer 默认可见边界 Failed。直接应用快捷键隐藏意图后，runId `53fc7f11eff84cb4b2d6b7e9cb27ddc4` 通过快捷键、新图层及各显示入口，随后重置断言 Failed：等待旧会话释放期间监控已消费初始化请求。合同按实际生命周期在重置返回时检查置位，在释放后检查完成状态。

最终 runId `37ee21f7569a43c3b5fed01a4e5b4d28` Passed（22.66 秒），三组界面合同全部通过；玩家原生结果保持回合 1、80/80 HP。全部三次请求均完成实例目录清理。本轮 .NET SDK 9.0.300 Release 构建为 0 警告、0 错误，结构门禁、工具检查和文档检查通过。可见 Steam 人工操作、完整 SL 场景和 Linux 运行未验证；贡献者提供的实机记录保留在 PR 正文。

## 手牌上限状态一致性（PR #224）

贡献者[测试记录](https://github.com/tianyilt/HextechSolverCompat/blob/main/docs/TESTING-PR-HAND-LIMIT-20261003.md)来自 0.48.1：可选 BaseLib `IMaxHandSizeModifier` 的上限 13→16→13 验证旧根／兄弟隔离、新根指纹区分与续用戳恢复，Dredge 13、CrashLanding 5 完整实际／预测状态通过，兼容层同项修复关闭。基线 `2ead87d9c9e35b1588a760efff0bd6154545a77c`，候选 SHA-256 `04c70a0c0ff4d9169a8184a327beae1e246bca75179ed8bd521331e08d384d1c`。耗时门槛 NotPassed：中位数 17.0191→24.3435 ms，保留 76.4900 ms 尾项，CPU 负载未测，因果归属未知。重定基至 0.50.0 `0d290fbee7e2779d2cebd8b8f652d82d00b6e8fc` 后仅构建通过（SDK 9.0.318、RitsuLib 0.6.5、游戏 0.111.0、零警告／错误、关闭自动部署与祖先 props/targets 导入），原生与耗时证据仍属 0.48.1。

当前原版根／Fork 复跑：PowerShell 使用 `tools/testing/run-unattended-test.ps1 -ScenarioId HAND-LIMIT-ROOT-CONSISTENCY -EnemyCurrentHp 1000 -VerifyCombatRootSnapshot -StopAfterCombatRootSnapshotAssertion -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit`；Bash 使用 `./tools/testing/run-unattended-test.sh --scenario-id HAND-LIMIT-ROOT-CONSISTENCY --enemy-current-hp 1000 --verify-combat-root-snapshot --stop-after-combat-root-snapshot-assertion --enable-no-gc-region-for-test 0 --timeout-seconds 120 --cleanup-instance-on-exit`，两端默认 IRONCLAD／FUZZY_WURM_CRAWLER_WEAK。

2026-10-06 合并验证：SDK 9.0.300 Release 构建零警告／错误；上述原版合同 runId `63b5712a8b034b8388dbf4d71f42c311` Passed（22.56 秒），核对基础手牌上限、根与 Fork 的 live/predicted 续用文本及捕获隔离，实例目录已删除。动态上限 13→16、Dredge／CrashLanding 与耗时对照本轮未复测，历史 NotPassed 保留。
