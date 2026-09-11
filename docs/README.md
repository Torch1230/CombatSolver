# CombatSolver 文档导航
- [致死性与攻击历史窗口](performance/simulation-lethality-20260911.md)：首次攻击倍率和当前／上一回合最后攻击的冻结所有权。
- [灰烬之灵、死亡之舞与迅速](performance/simulation-card-hooks-20260911.md)：出牌前顺序、当前费用与一次性附魔的分支恢复。
- [雕琢打击、响指与动态关键字](performance/simulation-keywords-20260911.md)：过滤后的实例身份、生成牌标记、X 费与跨回合恢复。
- [吊杀的卡牌倍率与层数](performance/simulation-hang-20260911.md)：特定来源倍率、能力封顶与跨回合值状态。
- [预借时间与刺破帷幕费用](performance/simulation-cost-powers-20260911.md)：当前分支费用、选牌挂起时的只读查询与终局能力门禁。
- [亡灵卡牌操作与终局生成](performance/simulation-necro-card-operations-20260911.md)：直接失血、弃牌回收、鬼火与虚无，区分生成历史和实际入堆。
- [挽歌与灵魂的值执行](performance/simulation-dirge-20260911.md)：逐次 X 召唤、随机插入、升级模板与跨回合生成身份对照。
- [洁净与来生的值执行](performance/simulation-draw-exhaust-20260911.md)：召唤后的抽牌堆消耗选择、空牌堆、隐式选择和两回合对照。
- [奥斯蒂每回合召唤](performance/simulation-pet-turns-20260911.md)：初始遗物的后置能量重置时点、三个回合与选牌中间态。
- [奥斯蒂值执行与搜索](performance/simulation-osty-values-20260911.md)：承伤、复活、攻击和两回合原生对照，最大生命封顶与回合参与者修正。
- [奥斯蒂根身份与死亡清理](performance/simulation-osty-ownership-20260911.md)：实机首次召唤后的空根隔离、普通能力退休和连续复活。
- [精神过载与毁灭](performance/simulation-necro-resources-20260911.md)：资源／选牌时序、死亡相位、原生差分与搜索生命周期。

- [紧凑后端运行时准入与 NoGC 验收](performance/simulation-runtime-backend-20260911.md)：整场零重算、未迁移域归属、正常 NoGC 与普通 GC 口径。
- [紧凑候选、挂起选择与完整搜索 A/B](performance/simulation-search-backend-20260911.md)：原始机甲路线、政策读取与零兼容物化。
- [原始机甲完整原生路线](performance/simulation-full-route-20260911.md)：44 原生动作及 424 个替代分支。

- [紧凑完整回合与历史窗口](performance/simulation-rounds-20260911.md)：完整阶段、起手／嵌套选择、跨阵营历史清理与原生终局。

- [紧凑确定性怪物 AI](performance/simulation-monster-ai-20260911.md)：机甲招式循环、增长日志、当前意图与冻结恢复。

- [紧凑 Power 阶段体](performance/simulation-power-phases-20260911.md)：回合初始字段、力量恢复、持续递减和下回合格挡，独立准入与原生对照。

- [持续减益的跳过递减状态](performance/simulation-duration-state-20260911.md)：统一应用入口的分支 Power 字段，修复原键和续用遗漏。

- [紧凑机械骑士指令体](performance/simulation-monster-commands-20260911.md)：怪物攻击／格挡／力量、无创建者灼伤生成、完整历史和原生对照。

- [紧凑灼伤与手牌末尾阶段](performance/simulation-hand-end-20260911.md)：显式入场顺序、玩家失败、状态牌抽取与完整原生对照。

- [玩家死亡清理与施伤者分支存活状态](performance/simulation-player-death-20260911.md)：原生致死差分与冻结根隔离修复。

- [紧凑原始机甲首回合根](performance/simulation-full-root-20260911.md)：完整 30 牌／31 监听器、蛇之戒与原生分支对照。

玩家安装、操作与兼容性说明见 [项目 README](../README.md)。源码规则见 [AGENTS.md](../AGENTS.md)。

## 当前文档

| 要查什么 | 入口 |
|---|---|
| 临时力量首次施加、正负偏移与计数封顶 | [原生差分与修正](performance/simulation-temporary-strength-20260911.md) |
- [紧凑人工制品与 Power 准入顺序](performance/simulation-compact-artifact-20260911.md)
- [紧凑抽牌随机费用与第二条 RNG](performance/simulation-random-costs-20260911.md)
- [紧凑附魔生成牌](performance/simulation-inky-cards-20260911.md)：固定模板折叠条件、完整差分与边界。
- [紧凑生成卡牌与小刀实例](performance/simulation-generated-cards-20260911.md)
- [交错分配的紧凑索引缓冲区](performance/simulation-indexed-buffer-20260911.md)
- [紧凑临时力量与尖啸](performance/simulation-compact-temporary-strength-20260911.md)
| 紧凑下回合格挡、必备工具计数与零层实例准入 | [下回合计数结果](performance/simulation-compact-deferred-powers-20260911.md) |
| 闪躲翻滚在格挡上限处的下回合格挡修正 | [返回值差分](performance/simulation-deferred-block-return-20260911.md) |
| 目标 Power 条件与存活敌人求和格挡 | [Power 表达式结果](performance/simulation-power-expressions-20260911.md) |
| 中毒主动触发、整手弃抽／Sly 和 Power 重获 | [中毒与弃抽结果](performance/simulation-poison-discard-20260911.md) |
| 有序群体中毒、条件抽牌返回值与虚无历史 | [群体与条件指令结果](performance/simulation-conditional-powers-20260911.md) |
| 能力牌移除、消耗与 X 费用／完成读取 | [生命周期阶段结果](performance/simulation-card-lifecycle-20260911.md) |
| 有序卡牌效果、嵌套选择与指令恢复 | [指令阶段结果](performance/simulation-effect-program-20260911.md) |
| 基础 Power 值、原生攻击／格挡边界与单向评估读取 | [Power 值迁移结果](performance/simulation-compact-powers-20260911.md) |
| 组件职责、状态所有权和调用链 | [架构与职责地图](ARCHITECTURE.md) |
| 全部性能方案取舍、剩余评估与已实现的冻结恢复 | [方案账本](performance/simulation-strategy-ledger-20260911.md)、[候选存储结果](performance/simulation-candidate-storage-20260911.md) |
| 紧凑洗牌／战略选择、原生两回合与特征复用 | [执行扩展结果](performance/simulation-expanded-chain-20260911.md) |
| 紧凑状态直接读取、完整语义对照与复杂状态净变化 | [直接读取结果](performance/simulation-read-view-20260911.md) |
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
