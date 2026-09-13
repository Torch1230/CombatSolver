# Beam 分数预计算排序合同

从仓库根运行 `python3 tools/BeamRankSortChecks/run.py`。需要 Python 3 与 .NET 9。

脚本从当前生产文件提取完整的 `SortByBeamRank`、`BeamRankScore`、`RetainedAttackGrowth` 和 `CompareBeamRankOrder`，链接生产 SolverWeights；临时编译产物只写入 `.local`。输入模型只保存公式需要的不可变标量，并核对原 Snapshot 属性仍为 int。

将候选与原来的“每次比较重算分数 + List.Sort”逐槽按引用身份对照：空表、小表、跨越插入排序阈值的大表、同分、已排序、逆序、重复对象、NaN/无穷/正负零，以及 boss/敌人数/根初始值组合。共 720 组、167,280 个输入条目。检查的是完整顺序，不只是 top-k 集合。

这项合同不模拟游戏，也不证明 Snapshot 在真实排名期间不被修改；该所有权由生产代码审计及实际固定工作量 A/B 共同验证。性能结论来自单独的真实搜索对照，不能由这项测试推断。
