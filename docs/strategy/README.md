# 策略与搜索研究

[返回文档导航](../README.md)

当前搜索职责见 [架构地图](../ARCHITECTURE.md)，实际测试与未验证范围见 [测试矩阵](../TEST_MATRIX.md)。

- [策略优化日志](STRATEGY_OPTIMIZATION_LOG.md)：样例、策略认识与数值记录。
- [当前搜索逻辑详解](search-logic-explained-20260912.md)：2026-09-12 开发快照，解释评分、保路、剪枝、预算与最终排序，并区分未提交实验。
- [有界新颖性与 Beam 组合](bounded-novelty-search-20260916.md)：默认关闭的实验开关、紧凑增量新颖性、共享预算、选型反例和本轮验证。
- [能力牌逐卡估值与搜索优化计划](power-card-valuation-plan-20260917.md)：用户逐卡定义、统一奖励/惩罚接口、六卡池目录、后续独立搜索成员和清理规定。
- [Beam 宽度组合](beam-width-portfolio.md)：共享节点预算的多宽度选优、精炼门控四条、开关与请求字段、成员默认值与数据来源。
- [玩家世界线研究](player-worldlines-20260905.md)：2026-09-05 批次。
- [有界搜索恢复研究](SEARCH_RECOVERY_RESEARCH.md)：已否决并撤回的 v54/v55 原型，保留研究证据。
- [0.17.0 原始需求](0.17.0-raw-requirements.md)。
- [0.17.0 优化规格](0.17.0-optimization-plan.md)。

原始需求和历史候选设计保留其当时语境，采用情况应结合开发笔记与源码判断。
