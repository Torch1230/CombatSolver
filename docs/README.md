# CombatSolver 文档导航

玩家安装、操作与兼容性说明见 [项目 README](../README.md)。源码规则见 [AGENTS.md](../AGENTS.md)。

## 当前文档

| 要查什么 | 入口 |
|---|---|
| 女王原包为何恢复失败、为何频繁GC及本轮优化取舍 | [恢复与性能报告](performance/queen-replay-optimization-20260913.md) |
| 未变状态共享、标签/大牌堆容器优化与女王GC问题 | [调查与实测](performance/state-sharing-20260913.md) |
| 内存主要花在哪里，什么条件才能缩至十分之一 | [分配与峰值研究](performance/memory-tenfold-20260913.md) |
| 后续六方向的当前诊断、逐项实现与对照 | [六方向开发记录](performance/six-directions-20260913.md) |
| 当前性能PR全部改动、撤回方案与各轮指标 | [累计记录](performance/surgical-pr-summary-20260913.md) |
| 提前计算回合尾部、所有权合同与16线程对照 | [尾部并行报告](performance/early-tail-parallelism-20260913.md) |
| 普通Power免锁、理论收益模型与16并行实测 | [Power克隆并行](performance/power-clone-parallelism-20260913.md) |
| 克隆优化后剩余Power锁、同父Fork和回合尾部并行瓶颈 | [并行瓶颈定位](performance/parallel-bottlenecks-20260913.md) |
| 真实 BaseLib 下原版卡牌克隆并行、保守回退与验证限制 | [克隆并行边界](performance/native-clone-parallelism-20260913.md) |
| 当前精简 fork、旧移植暂停、实测收益与小优化候选 | [手术刀式移植](performance/surgical-fixes-20260912.md) |
| 盛碗虫群小区域并行预约、实测提速与边界验证 | [父节点预约优化](performance/bowlbugs-wave-admission-20260912.md) |
| 盛碗虫群慢搜索：原包分析、诊断恢复与药水历史分配优化 | [慢搜索原包分析](performance/bowlbugs-slow-search-20260912.md) |
| 通用选牌令牌计数、卡牌/药水生成查询的迁移与搜索耗时 | [选牌小优化验证](performance/choice-migration-20260912.md) |
| 全量选牌来源、间接印牌/自动出牌放大器与逐项优化判断 | [213项来源盘点](performance/choice-source-inventory-20260912.md) |
| 印牌长战斗、历史反向查询与累计分配验证 | [生成历史查询](performance/generation-history-20260912.md) |
| 同次快照按需读取关键字，保持原模拟器与评分 | [快照内部局部复用](performance/snapshot-reuse-20260912.md) |
| 当前 CPU 热点、快照释放优化与交错对照 | [热点可消除工作](performance/hotspot-cuts-20260912.md) |
| 已开发的空状态清理、排名预计算及真实对照 | [精简优化开发结果](performance/surgical-development-20260912.md) |
| 合并到上游后的新性能分支、热点与优化对照 | [合并后的性能探索](performance/hotspot-exploration-20260913.md) |
| 最终代码在 VeryHigh 极高负载样例中的结果 | [VeryHigh 最终压力测试](performance/veryhigh-final-20260913.md) |
| 五个后续候选的工作量、原型与采用/撤回决策 | [五候选成本与验证](performance/five-candidates-20260913.md) |
| 小改动优化文献、源码切口与独立分配探针 | [精简优化深入研究](performance/surgical-research-20260912.md) |
| 遗物独立开关、目标范围、血量额度与早停 | [战斗末遗物计数策略](relic-counters.md) |
| 组件职责、状态所有权和调用链 | [架构与职责地图](ARCHITECTURE.md) |
| 当前搜索、卡牌评分、保路剪枝与最终选路 | [搜索逻辑详解（2026-09-12 开发快照）](strategy/search-logic-explained-20260912.md) |
| 当前 UI 重设计、按钮区整理与 Gemini 建议审计 | [UI 建议复核与重构方案](audits/ui-redesign-gemini-review-20260911.md) |
| 本批未发布改动、版本演进与开发记录 | [开发笔记](DEVELOPMENT_NOTES.md) |
| 已执行测试、复跑方式和未验证范围 | [测试矩阵](TEST_MATRIX.md) |
| 跨跑局卡顿、第三方 Mod 性能取证 | [进程全程性能录制](performance/long-session-recording.md) |
| 三层秒级卡顿与自动回收 | [2026-09-11 实录诊断](performance/player-lag-diagnosis-20260911.md) |
| 最新快照/重放复查与 PR 验证 | [快照与重放热点复查](performance/snapshot-replay-followup-20260909.md) |
| 保路元数据合入证据 | [perf-2 选择性合入与后续热点试验](performance/perf2-integration-20260909.md) |
| 上一轮性能目标与逐轮证据 | [回合结束探针与元数据热路径](performance/standpat-and-metadata-20260909.md) |
| 无人测试环境与请求协议 | [无头测试](HEADLESS_TESTING.md) |
| 在线状态、隐私设置与管理后台 | [在线统计](ONLINE_STATISTICS.md) |
| 在线监控工作台、筛选与刷新行为 | [监控工作台](ONLINE_WORKBENCH.md) |
| 两个在线服务的权威源码与部署来源 | [在线服务维护入口](ONLINE_SERVICES.md) |
| 跑局胜负、连胜、历史快照与筛选 | [跑局战绩](RUN_STATISTICS.md) |
| 创意工坊中英文介绍与语言字段 | [创意工坊介绍](workshop/README.md) |
| 玩家问题包、检查点恢复与回放 | [检查点回放](CHECKPOINT_REPLAY.md) |
| 2026-09-09 22:56 的 34 份实验体报告 | [实验体批次修复与未定位项](issues/test-subject-reports-20260909.md) |
| 问题包目录、提交元数据和后台筛选口径 | [报告协议](BUG_REPORT_PROTOCOL.md) |
| 188 份计划外重算报告的分类与高频修复 | [2026-09-08 重算分诊](issues/report-replans-20260908.md) |
| 0.33.0 修复批次的剩余问题与交接 | [2026-09-07 修复交接](issues/report-logic-bugs-20260907-handoff.md) |
| 第三方卡牌、Power、药水等登记入口 | [第三方 Mod 适配手册](THIRD_PARTY_ADAPTERS.md) |
| 第三方 Power 的搜索估值 | [战略估值登记](third-party-strategic-effects.md) |
| 遗物与 Modifier 的捕获、Fork 与续用状态 | [模型状态适配](third-party-model-state.md) |
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
