# 紧凑候选存储与恢复检查

直接链接 `Simulation/Compact/` 当前源码，不引用游戏程序集。覆盖固定 34 叶抽牌/弃牌程序，以及 0/1,024/8,192 个既有历史值、8/512/1,024 槽写入、128 个同时保留候选的结构负载。历史值只是整数槽，不是 Power、RNG、死亡或完整战斗语义。

```bash
dotnet run --project tools/CompactCandidateChecks/CompactCandidateChecks.csproj -c Release -- \
  .local/compact-candidates/checks.json current
```

输出完整逐次 CPU、墙钟、分配和去重后的保留 payload/页表字节；后者不含 CLR 对象头、对齐、根独占页或工作区，不是存活堆、RSS 或 GC trace。项目关闭 tiered compilation，每个模式预热后测四次，每次先回收，窗口内自然 GC 保留。当前线程 CPU 使用已有跨平台时钟 helper。新建工作区与复用恢复分开测量，不能用后者冒充前者。

七组容量合同覆盖 0/1/63/64/65/573/1,024 槽：完整有符号值、页边界、非零转零、失效缓存、嵌套 rollback 后冻结、暂停状态、活动事务/外根拒绝，以及 8 worker 独立恢复。所有 128 个保留分支逐槽对照，不只比较 checksum；计时 checksum 仅防止丢弃工作。

`CompactKernelRoot` 可指向从历史提交导出的内核目录，构建同一探针的旧版本对照。旧版本没有 `Restore` 或页存储统计时，反射在计时外检测 API 并省略该项，不虚构对应操作。本轮 A 从 `2d7a837` 导出原两个内核文件，B 为当前源码，分别构建到 `.local/compact-candidates-20260911/tool-baseline` 和 `tool-candidate`；预定 A–B–B–A 各运行一次。源码与输入未变时无需重复取证。

[方案账本](../../docs/performance/simulation-strategy-ledger-20260911.md)保存历史取舍；[本轮报告与数据](../../docs/performance/simulation-candidate-storage-20260911.md)保留全部样本、密集退化和失败修正。实际游戏语义另由 `COMPACT-KERNEL-NATIVE` 验证，本工具不能替代。
