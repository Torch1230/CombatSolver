# 测试历史卷 30

## 0.51.7 薄荷尖塔兼容与诊断取证

- 游戏 `v0.113.1 d09d48e5`，无头快照加载实际 Workshop 薄荷尖塔 1.3.1、BaseLib 和 RitsuLib。0.51.6 基线运行 `5a490a4269d842fa85e8353393c291fe` 在根捕获触发 `Unreviewed native Heal patch combination.`。
- 修复后 `COMBAT-DIAGNOSTIC-LOG` 运行 `b339744d6e014199a97af7820bcbe2a0` 通过（24.49 秒，含启动）。覆盖正常治疗与伤害的原生差分、路线回放和独立日志归档。注入未知 Heal postfix 后，搜索根明确拒绝，诊断 ZIP 保留日志及拒绝原因，生产 multipart 客户端经测试 HTTP handler 收到回执。线上服务与玩家机器未复测。
- 合同入口为 `tools/testing/run-unattended-test.ps1 -ScenarioId COMBAT-DIAGNOSTIC-LOG -TimeoutSeconds 100 -CleanupInstanceOnExit`。本次组合验证在独立实例中额外加载了上述 Workshop 组件，启动器已清理实例。
