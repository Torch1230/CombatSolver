# 重构路线状态

策略与搜索重构于 2026-09-28 收尾：P0～P6 已按固定根与对应边界收口。P7 已有权重探针和免费生成药链，默认权重未校准；目标代表试验因哨兵退化撤回。P8 只有局部归因与诊断工具，尚未达到退出标准。P7/P8 的新增工作已停止，不沿旧计划继续推进。

职责与当前实现见 [架构地图](../ARCHITECTURE.md)。阶段验收、源码基线和剩余局限见 [实施总结](../archive/refactoring/strategy-search-refactor-summary-20260928.md)；旧路线全文见 [历史路线](../archive/refactoring/refactor-roadmap-before-20261003.md)。后续整合证据见 [2026-10-03 合并审计](../archive/refactoring/merge-audit-20261003.md)。

新重构只围绕当前任务的明确边界展开，取得对应最小证据后替换本状态，不将阶段流水账追加到路线首页。

coverage 与 Testing 已按当前职责入口精简：历史批次材料由固定提交保存，公共测试框架与有效回归继续维护，一次性调查代码退出正式源码树。维护证据见 [测试矩阵](../TEST_MATRIX.md)，目录边界见 [Testing 入口](../../src/Testing/README.md)。
