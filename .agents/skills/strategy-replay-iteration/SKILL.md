---
name: strategy-replay-iteration
description: 批量回放 CombatSolver 的“找到更优世界线”报告，筛出同根、可比较且扣除药水成本后仍成立的策略缺口，并按小批次完成定位、改进和数字记录；不用于普通语义错误分诊或全量性能审计。
---

# CombatSolver 策略样例快速迭代

## 适用边界

本 skill 接续 `issue-bundle-triage` 已整理好的新旧报告，处理搜索质量。包协议和旧包恢复问题先修复日志/Testing 入口；出现 actual/simulated 差异、根状态漂移或动作无法合法回放时，转 `combat-semantic-change`；确认是展开、保路、终局排序或预算问题后，按 `search-performance-optimization` 的职责边界改动。

用户结束当前样例批次时立即停止，不继续挖掘未处理报告。

## 1. 小批次入口

- 每轮默认取排序最靠前的 `3` 份，先 `Preflight` 再 `RestoreOnly`，需要搜索或完整部署时显式选择 `SearchOnly` / `DeploySolver`。不并发启动游戏。
- 统一入口 `tools/replay/run-checkpoint-batch.ps1` / `.sh`。输入接受 ZIP、目录、汇总 ZIP 和已解压旧包。
- 原包政策默认生效；缺项由显式政策文件补齐。每请求最多 `120 s`，不自动提高档位、Beam 或预算。
- 工具按玩家备注优先、已知减战损降序排队。排除包与小于 5 HP 的策略样例由当前任务清单决定，不硬编码玩家包 ID。
- 输出写入 `.local/checkpoint-batch/`，JSONL、JSON、CSV、Markdown 和逐请求证据同时保存；`-Resume` 复用身份一致的已完成请求，`-RetryFailures` 重试失败项。原始包、结果和日志不提交。

示例：

```powershell
pwsh -NoProfile -File tools/replay/run-checkpoint-batch.ps1 `
  -InputPath .local/issue-bundles/<batch>/raw/reports `
  -ManifestPath .local/strategy-batch/<batch>.json `
  -MaxReports 3 `
  -ReplayMode Preflight
```

执行恢复时改为 `-ReplayMode RestoreOnly` 并提供 `-Sts2GameRoot`。整场入口用 `-CheckpointSelector start`，不能从回合数猜测。完整使用方法见 `docs/CHECKPOINT_REPLAY.md`。

## 2. 有效策略缺口

只有以下条件同时成立，才进入求解器优化：

- 报告恢复到同一检查点，完整 `ContinuationStamp`、牌序和 RNG 严格一致；
- 求解器与人工数字覆盖同一比较区间；中途根不冒充整场战损；
- 人工路线合法并实际存活，备注不是无效强制路线或错误人工计算；
- 人工路线的优势扣除额外药水后仍成立。

药水默认按每瓶 `9 HP` 计机会成本。人工多用 `N` 瓶时，只有省血至少 `9 × N` 才算更优；持有石化蟾蜍后每场生成的石头药按可再生资源处理，不要求为它保留 `9 HP`。比较时记录实际药水身份和数量，不能只看最终 HP。

预检阻塞、严格状态不一致、超时、无效人工基线和扣除药水成本后不成立的结果只记状态，然后继续下一份。遇到首个有效质量缺口后停止本轮余下样例，先完成一次可解释改进。

## 3. 定位与改进

当前逐包优化每次只处理一个包。从开始排查该包起累计计时；40 分钟仍未追平人工时，记录已有数字和未解决的问题，标记跳过并继续下一包。超时包直接跳过。此上限覆盖诊断、修改和验证，不因重新启动搜索而重置。

按目标路线消失的位置处理：

- 合法动作未进入候选：检查展开与单节点分支容量；
- 候选已出现但中途丢失：检查显式策略通道、Beam 保路、转置和支配；
- 完整路线仍在但没被选中：检查终局实际胜负、战损、药水与卖血排序；
- 只有提高预设才能找到：记录为预算差异，再判断是否属于档位容量，而不是先改权重。

能力、跨回合资源、卡牌联动、药水窗口和卖血必须用可兑现的后验验证。前验只负责让代表路线活到兑现点；最终结果继续按真实整场结果比较。优先修共享机制，不按遭遇或单卡硬编码路线，也不靠单纯拉宽 Beam 掩盖错误通道。
玩家的出牌记录用于定位遗漏的机制。不得把某份包的具体多牌出牌顺序或固定卡名组合写成生产搜索规则；按效果、状态、费用和兑现时机提炼能覆盖同类卡牌的候选或保路条件，再用完整路线验证。

每轮只保留一个可解释因素。目标改善后跑同一目标和一个受影响的不可退化哨兵；失败实验撤回，不累积互相抵消的参数。

## 4. 数字记录与收口

`docs/archive/strategy/STRATEGY_OPTIMIZATION_LOG.md` 只维护两张表：

- 汇总：日期、样例、玩家备注、优化前求解器、当前求解器、人工、优化幅度、相对人工、是否更优；
- 待处理：样例、当前数字或阻塞证据、状态。

不写正文复盘，不维护 `docs/performance/PERFORMANCE_FIXTURES.md`。搜索行为变化同步 `docs/DEVELOPMENT_NOTES.md` 与 `docs/TEST_MATRIX.md`；提交只包含本轮源码、最小 fixture 和文档。下一批从上批未处理项继续，不重跑已经取得直接证据的样例。
