# CombatSolver 文档导航

玩家安装、操作与兼容性说明见 [项目 README](../README.md)。源码规则见 [AGENTS.md](../AGENTS.md)。

## 当前文档

| 要查什么 | 入口 |
|---|---|
| 组件职责、状态所有权和调用链 | [架构与职责地图](ARCHITECTURE.md) |
| 全部性能方案取舍、剩余评估与已实现的冻结恢复 | [方案账本](performance/simulation-strategy-ledger-20260911.md)、[候选存储结果](performance/simulation-candidate-storage-20260911.md) |
| 紧凑原型估值瓶颈、测试上下文校正与下一迁移边界 | [估值与读模型调研](performance/simulation-evaluation-bottleneck-20260911.md) |
| 紧凑可恢复内核的实现、原生对照与迁移门槛实测 | [R0–R2 原型结果](performance/simulation-kernel-prototype-20260910.md) |
| 本批未发布改动、版本演进与开发记录 | [开发笔记](DEVELOPMENT_NOTES.md) |
| 已执行测试、复跑方式和未验证范围 | [测试矩阵](TEST_MATRIX.md) |
| 更大范围的执行/状态内核重写方案、进入条件与证据缺口 | [大幅重构再调研](performance/simulation-redesign-research-20260910.md) |
| 模拟性能重构的阶段、工作量与验收标准 | [完整搜索重构计划](performance/simulation-refactor-plan-20260910.md) |
| 重构首轮细分结果与候选取舍 | [P1 复用率与类型成本](performance/simulation-refactor-p1-20260910.md) |
| 完整搜索性能重构最终交付、headless 收口与各阶段取舍 | [最终报告](performance/simulation-refactor-result-20260910.md) |
| P2 / P4 等待选择快照及 Power 复制的 CPU 与所有权取舍 | [剩余候选决策](performance/simulation-refactor-p2-p4-decision-20260910.md) |
| 线程 CPU 诊断、阶段提取与检查点验证状态 | [P5 选择前缀测量与回合检查点](performance/simulation-refactor-p5-20260910.md) |
| 归一化原型验证、撤回与测量干扰 | [P3 实验记录](performance/simulation-refactor-p3-20260910.md) |
| 最新极高配置整场搜索的 CPU 与分配热点 | [完整搜索模拟采样](performance/simulation-profile-20260910.md) |
| 最新快照/重放复查与 PR 验证 | [快照与重放热点复查](performance/snapshot-replay-followup-20260909.md) |
| 保路元数据合入证据 | [perf-2 选择性合入与后续热点试验](performance/perf2-integration-20260909.md) |
| 上一轮性能目标与逐轮证据 | [回合结束探针与元数据热路径](performance/standpat-and-metadata-20260909.md) |
| 无人测试环境与请求协议 | [无头测试](HEADLESS_TESTING.md) |
| 在线状态、隐私设置与管理后台 | [在线统计](ONLINE_STATISTICS.md) |
| 创意工坊中英文介绍与语言字段 | [创意工坊介绍](workshop/README.md) |
| 玩家问题包、检查点恢复与回放 | [检查点回放](CHECKPOINT_REPLAY.md) |
| 问题包目录、提交元数据和后台筛选口径 | [报告协议](BUG_REPORT_PROTOCOL.md) |
| 188 份计划外重算报告的分类与高频修复 | [2026-09-08 重算分诊](issues/report-replans-20260908.md) |
| 0.33.0 修复批次的剩余问题与交接 | [2026-09-07 修复交接](issues/report-logic-bugs-20260907-handoff.md) |
| 第三方卡牌、Power、药水等登记入口 | [第三方 Mod 适配手册](THIRD_PARTY_ADAPTERS.md) |
| 第三方 Power 的搜索估值 | [战略估值登记](third-party-strategic-effects.md) |
| 战斗语义适配与验证方法 | [适配验证](ADAPTATION_VERIFICATION.md) |
| 原版 Hook 支持和覆盖证据 | [战斗 Hook 覆盖目录](COMBAT_HOOK_COVERAGE.md) |

战前预测 API 的公开调用方式与最低依赖版本见 [项目 README](../README.md#战前预测-api面向-mod-开发者)。开发分支的新入口以适配手册中的发布状态为准。

## 专题目录

| 目录 | 内容 |
|---|---|
| [releases/](releases/README.md) | 按版本整理的玩家更新日志与历史草案 |
| [pr/](pr/README.md) | PR 审查、集成修正与验证记录 |
| [refactoring/](refactoring/README.md) | 滚动重构路线与核验记录 |
| [issues/](issues/README.md) | 玩家问题批次、分诊与修复计划 |
| [strategy/](strategy/README.md) | 策略需求、优化日志与搜索研究 |
| [performance/](performance/README.md) | 性能实验、复现方法、结果数据与历史样例 |
| [audits/](audits/README.md) | 历史仓库、架构和 UI 审计及处理记录 |

## 维护约定

- 当前架构和支持范围以源码、当前文档及可重跑证据为准；专题报告保留各自的基线、日期和验证限制。
- 新改动先写入开发笔记的“下一版本（开发中）”。更新日志统一放在 `releases/<版本>-RELEASE_NOTES.md`；有日志文件不等于该版本已经发布。
- 历史审计、建议和已撤回实验保留原结论，不作为当前任务指令或当前测试成绩。
- 新增专题文档时更新对应索引；移动文件时同步 Markdown 链接、脚本、skill 和结构化证据中的路径。
- `COMBAT_HOOK_COVERAGE.md` 等工具生成文档保留固定入口，内容由对应工具维护。
