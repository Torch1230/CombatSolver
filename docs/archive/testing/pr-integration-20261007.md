# 社区 PR 整合验证（2026-10-07）

范围：[PR #227](https://github.com/Torch1230/CombatSolver/pull/227)、[PR #228](https://github.com/Torch1230/CombatSolver/pull/228)、[PR #229](https://github.com/Torch1230/CombatSolver/pull/229)、[PR #230](https://github.com/Torch1230/CombatSolver/pull/230)。主线基点 `6031debd`，行为源码 `5d9a2d92`。逐次结果、失败和完整比较字段见[配套数据](pr-integration-20261007.json)。这是 Windows 本机整合验证，贡献者原有平台结果保持各自来源。

## 最终行为

- Search 在既有名额内保留破盾后有合法攻击、且敌方剩余 HP 不超过可执行手牌估值的收尾续路。估值只决定保路资格，攻击继续由模拟器结算。限定弃牌的安全进攻、延迟伤害结束回合探测、死亡增援窗口继续沿所属通道执行。
- Testing 在完整原生检查点核验后迁移旧报告缺失的默认手牌上限，其他续用字段继续严格比较。Q013 的维护夹具使用 120 秒启动器上限；历史 300 秒结果保留原口径。
- 严格组件认证根将实际完整胜利交给后续计划；Runtime 在 NoGC 重建检查点确认一次压缩完整回收。认证与详细 GC API 的能力门禁保持独立。
- Smart 药水审计的独立时间只提供给一个合规可选层。资格要求独立成员时间为正，层数由显式参数限制，关联令牌提供收尾窗口；成员自身的软预算保留结果。

## 本机结果

所有原生请求的启动器上限为 120 秒，实例均由启动器删除。原包 ZIP 保持原始字节，从 `start` 恢复；性能与逐转移正确性分别采样。

| 范围 | 结果 |
| --- | --- |
| Smart 预算耗尽合同 | `b6331d73efae4f4f8ead3eca0fd34061` Passed：实际无药搜索 21 战损，耗尽账本后单药 0 战损，只执行一个成员；Disabled、RequireAtLeastOne、Force、回合准备资格保持；完整预测回放与 live 隔离通过。 |
| 旧报告兼容合同 | `c38e6549d8b8494c8be1fcb5a23e7c7c` Passed：默认上限迁移需要原生核验；非默认、显式冲突、重复、错位、牌与 RNG 差异拒绝。 |
| 原生 GC 生命周期 | `c0c563197f084cffb8e1825c34fc7f3e` Passed，含确认、取消、超时排空、手动请求及战后释放 epoch，9 条完成检查。 |
| 真实 CLR GC 合同 | `portable-runtime` 6 项、`checkpoint` 1 项通过，实际建立 1 GB NoGC 并确认一次压缩完整收集。 |
| 组合合同 | `BeamWidthPortfolioChecks` 118 项通过。 |
| O068 原包整场部署 | `ccc373dd614f4be083ab80b762f6c02b` Passed：3 战损、0 药、T4、57/75 HP，计划外重算 0；原生状态、续用和旧默认手牌上限核验通过。 |
| O056 当前主线对照 | 对照原质量界 42 的请求 Failed，实际完整胜利为 62 战损、0 药、T13。对照只在 `6031debd` 的 Testing 中接入旧包兼容，生产逻辑保持。 |
| O056 最终整合 | `06aea9a0d5914c2f90ebb1e5f20ccae7` Passed（当前主线界 62）：62 战损、0 药、T13。与对照的完整根、政策、runtime、动作、最终快照、质量及续用边界逐项相等。原夹具历史界 42 仍未达成。 |
| 固定独立哨兵整场部署 | 对照 `677caeb54591414a9373fc0cd53f38bd` 与最终 `d56ff887ec4d413eb662f8091d14690a` 均 Passed：5 战损、1 药、T4、65/70 HP、计划外重算 0；完整根、政策、runtime、动作、快照、质量及续用边界相等。 |

固定哨兵使用维护的 `damaging-continuation-sentinel.json`，仅 mode 改为 Deploy：Custom、Beam60、120000 节点、30000 ms、DOP2、Smart、默认 GC、诊断关闭、Instant／0 秒。对照搜索 8999.8481 ms、2424296008 B，最终 7527.2486 ms、2438494760 B；两边各一个独立进程样本，分配约增加 0.59%，不外推普遍提速。每个结果采用请求总搜索指标。

## 失败与定位

- 原始新增伤害续路必保使 O056 从当前主线 62 变为 67，完整根、政策与 runtime 相同，无成长抵扣。仅限破盾时为 65；去掉该代表时 O056 恢复 62，但 O068 从 3 变为 7。最终收尾资格同时保留 O056 的 62 与 O068 的 3；前述失败和单因素对照保留在配套数据中。
- 本机 `COMPONENT-SMART-BOUND` 请求 `f2e5871333214ca28b5d38ae30fd8e0e` Failed，原因 `native-version`，未进入证明交接合同。原生 MVID 认证继续采用已有审计身份。贡献者 Linux 计划循环与性能结果不能写成本机通过。
- 新预算夹具曾因枚举与命名空间引用错误编译失败；首次建局允许更优等待路线，未满足预定 21 战损基线。补入即时威胁后取得上述最终合同。构建失败时一次启动仍加载旧夹具，结果同样失败。
- 一次哨兵建局缺少 EvidenceDirectory，另一次私有快照冻结期间 DLL 更新被拒绝，两次均未进入搜索。Q013 对照复制兼容源码后，增量构建曾复用旧 DLL，恢复阶段仍报字段差异；显式 Rebuild 后进入实际同根搜索。上述启动、构建问题与质量对照分别判断。

## 验证入口与范围

```powershell
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId SMART-POTION-AUDIT-BUDGET -EnemyCurrentHp 1000 -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId REPLAY-BOUNDARY-CONTRACT -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId GC-CHECKPOINT-BACKGROUND-V0111 -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit
```

O068 与哨兵的维护输入及政策参数见 [Q015 入口](../../issues/q015-route-quality.md#可重跑入口)。O056 使用 [Q013 夹具](../../../coverage/fixtures/regressions/community/q013-o056-same-root.json) 的原包、`start` 与录制政策；本机同条件对照关闭 NoGC，比较主线实际界 62，历史界仍为 42。

未验证：本机组件证明交接、最终整合上的其他 Q013 根及 O066／O067／O069／O070、O049 原包长预算结果、最终 Linux／Android 运行、可见 Steam 帧时间和完整性能分布。归档中的贡献者旧结果保留原来源和范围。
