# CombatSolver 测试入口

按改动选择最小验证层，方法见 [无人测试](HEADLESS_TESTING.md) 与 [社区验收](community/testing-guide.md)。以下命令提供当前复跑入口，不表示本轮已执行。

历史记录见 [归档索引](archive/testing/README.md)，0.48.1 的验证、失败与未验证项见 [历史卷 12](archive/testing/volume-12.md)。

## Q003 检查点政策与比较（2026-10-03）

`CheckpointTool self-test` 的失败基线为 `different_switch_not_comparable:predictPotionReward`，最终 `archive_contract_tests_passed assertions=59`，含缺失/不同派生上下文、未来字段及预设标签比较。`REPLAY-BOUNDARY-CONTRACT` / `b4c0cbeb24964595b3aed0cbb74b4417` Passed：true/false 均覆盖与本机相反的四开关、精确预算、输入不变与缺项拒绝，并保留既有回放边界合同。

O008 / `f92387e90f7745cfb5ec74f55f46dcea` 验证有效 `:3` 检查点的 16 项事件、完整 continuation 与原生状态；`c3a2b0d8add74484ba310e5e539ac182` 从 `:1` 以固定短预算执行到第 3 回合，原生存活、`UnexpectedReplans:0`。只覆盖最早续用，不代表整场胜利。政策恢复后的 O007/O008/O010 开战搜索记录原预算与开关，O008 返回部分路线；有效第 3 回合搜索只找到死亡路线。Release 零警告/错误、结构门禁通过（246 个 Search 文件），自有实例已清理；未进行五主题整场验收或可见性能测试。复现命令、源码/DLL 来源和材料限制见 [Q003 记录](issues/q003-checkpoint-policy-20261003.md)。

第三阶段 O009 独立冷启动成对样本：基线 `1ae32d83b86f42c3b8cd3bb83cd72f64`（`556e729`），候选 `49d4b5073799448ab996d93b00ce1bbd`（`f17f4d9` 行为源码）。原生根及 continuation 通过，执行政策、完整动作序列与预测 snapshot 相同；均为 120000 节点/481427 转移、预测战损85、药水0、第14回合。总搜索工作 29008.463→29793.7552 ms（+2.7%），仅一对样本，未外推普遍性能。O010 `51c242c740dc4363a043f76d021ba441` 完成原设置整场原生部署：第10回合存活结束、战损43、治疗1、药水0、计划外重算0；战斗末HP18与夹具战后HP24分别记录。真实执行政策包含派生上下文，缺项在开战报告侧；不将缺项视为默认值。上述第三阶段代表证据来自 `f17f4d9`；随后合并上游 `2ead87d`，保留其 0.48.1 发布归档与工具布局。合并后的入口合同验证另行记录，不把旧证据写成新版重新通过。

合并上游后，`a2d685a` 的 Release 零警告/错误，59 项工具断言及 `REPLAY-BOUNDARY-CONTRACT` / `bf7ccd4e164b4a428d57cf41bc13eb59` Passed，实例已清理；结构、文档、工具与覆盖材料门禁通过。旧整场/性能样本仍按原提交归属，未冒充合并后重跑。

## 当前矩阵命令

矩阵启动器只读取本节的平台原生命令；历史记录不作为自动执行清单。以下两项分别检查倾泻手空边界与相邻效果作用域，不运行整场搜索。需要当前游戏及 RitsuLib 路径，矩阵启动器统一管理实例并在结束时清理。

```powershell
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId CASCADE-EMPTY-HAND-NATIVE -EnemyCurrentHp 1000 -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId EFFECT-SCOPE-ADJACENT-CONTRACT -EnemyCurrentHp 1000 -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120
```

```bash
./tools/testing/run-unattended-test.sh --scenario-id CASCADE-EMPTY-HAND-NATIVE --enemy-current-hp 1000 --headless-fast-mode-for-test Instant --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 120
./tools/testing/run-unattended-test.sh --scenario-id EFFECT-SCOPE-ADJACENT-CONTRACT --enemy-current-hp 1000 --headless-fast-mode-for-test Instant --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 120
```
