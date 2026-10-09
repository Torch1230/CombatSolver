# Testing 源码

公共无人测试框架、原生回放、API worker 和离线宿主共用这里的实现；文件归类沿用同一程序集与 partial 类型，不改变状态所有权。

| 目录 | 内容 |
| --- | --- |
| Host/ | 请求协议、循环、建局、执行、断言、结果输出、选择器及离线入口 |
| Support/ | 共享 fixture、完整状态差分、球/药水/回合与冻结快照辅助 |
| Replay/ | 检查点导入、原生事件回放、录制状态与回放断言 |
| Contracts/Combat/ | 卡牌、Power、遗物、RNG、生命周期、Fork 与执行续接机制 |
| Contracts/Search/ | 政策、预算、保路、质量和搜索并发合同 |
| Contracts/Runtime/ | 根捕获、会话、部署所有权、内存与进程边界 |
| Contracts/UI/ | 本地化、路线行与显示身份合同 |
| Contracts/ThirdParty/ | 登记接口及第三方调用边界合同 |
| Regressions/Community/ | 社区问题的独立机制回归 |
| Regressions/Reports/ | 已固定报告的机制检查与保留的原生已知路线回归 |

测试选择与平台命令见 [无人测试](../../docs/HEADLESS_TESTING.md)，当前最小哨兵见 [测试矩阵](../../docs/TEST_MATRIX.md)。长期测试有明确断言、最小入口或 fixture；同一机制优先扩展已有合同。

`Contracts/Combat/UnattendedTestRunner.HandDrawRelicQuery.cs` 通过检查点 `RestoreOnly` 与场景名 `HAND-DRAW-RELICS-PROBE` 复用原生回放建局，逐一核对8种抽牌遗物在回合1～4及已有计数下的原生命令、冻结查询、Fork/RNG/父分支和live隔离；它只验证查询，完整生命周期沿用对应遗物合同。

`Contracts/Search/UnattendedTestRunner.PotionCostIncumbent.cs` 使用 `POTION-COST-INCUMBENT` / IRONCLAD / NIBBITS_WEAK / 120秒，验证实际完整14/9成本胜利下同回合和更晚回合的等HP保路及原生EndTurn/用药完整状态。`ZeroAllowanceRelicIncumbent.cs` 使用 `ZERO-ALLOWANCE-RELIC-INCUMBENT` / 同角色遭遇及上限，验证真实完整胜利在零战损让步遗物目标下的本地及协调器后续HP界，保留同HP、正额度、成长和追回，原生HP消耗前缀严格差分。

一次性调查放 .local/tool-tasks/<任务>/，验证时显式接入，结束清理代码、路由、参数、输入和产物。普通构建排除 .local 源码。新增正式文件按上表收纳；根目录只保留本入口。

`Contracts/Search/UnattendedTestRunner.NoveltySearch.cs` 的 `GENERATED-NOVELTY-SEARCH` 支持完整请求对照及固定胜路验证：`research-options.json` 选择 `request` 或 `replay`；后者读取 `frozen-plan.json` 并要求零展开、完整终局一致，可配合 `native.flag` 验证原生部署。复跑参数和范围见[首领策略证据](../../docs/strategy/hp-loss-pruning/README.md#原生兑现与独立包复测)。

旧 Soul/Custom/外骨骼虫路径追踪及 ACT3 硬编码路线观察入口已退出当前树，调查代码见 [固定提交](https://github.com/Torch1230/CombatSolver/tree/fe3edd2f7b4f3a92b266e6b13293810d31ce2e1b/src/Testing)。原生已知路线回归、生成上下文合同及其公共快照辅助继续维护。历史质量缺口与失败记录保持原结论，源码精简不代表问题修复。
