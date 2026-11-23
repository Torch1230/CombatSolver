# CombatSolver 测试历史卷 15

归档日期：2026-10-05。保留移动运行库回收批次的原验证范围。

## 移动运行库内存回收（2026-10-04）

`portable-runtime` 先在原回收逻辑复现 Mono 同形的 API 拒绝，修复后 5 项 Passed。直接链接生产代码并注入被拒绝的按类型 GC 信息接口，验证一次检测后不再调用、普通检查点及不可分割提交续行、取消、自动及手动真实阻塞回收、不可用暂停观测和其他异常继续传播。

```bash
dotnet run --project tools/testing/checks/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- portable-runtime
```

桌面实际 CLR 相邻合同 `default-commit` 2 项、`checkpoint` 1 项、`diagnostic-failure` 8 项、`recovery-lifecycle` 3 项 Passed。模式均由同一 GC 工具运行，方法见工具入口（`tools/testing/checks/CombatSolver.GcPolicyChecks/README.md`）。

原生 `B013-DEFAULT-GC-LIMIT` Passed，runId `ac4ee495b5ad48158c0d709a49b0abd2`，22.883 秒；包含限额、真实回收续行、退出和暂停观测缺失时的工作量累计。PowerShell 入口为 `tools/testing/run-unattended-test.ps1 -ScenarioId B013-DEFAULT-GC-LIMIT -EnemyCurrentHp 1000 -TimeoutSeconds 120 -CleanupInstanceOnExit`；Bash 对应 `tools/testing/run-unattended-test.sh --scenario-id B013-DEFAULT-GC-LIMIT --enemy-current-hp 1000 --timeout-seconds 120 --cleanup-instance-on-exit`。实例已删除。

原 Android 设备与原包整场回放未执行；来源、失败基线及验证范围见[开发记录](../development/volume-15.md#移动运行库内存回收2026-10-04)。
