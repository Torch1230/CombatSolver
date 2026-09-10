# 可增长紧凑工作区合同

直接链接当前紧凑内核，无游戏程序集依赖：

```bash
dotnet run --project tools/CompactGrowthChecks/CompactGrowthChecks.csproj -c Release
```

六组页边界合同覆盖追加槽位、嵌套撤销、索引复用清零、非法访问、外根与活动事务、不同长度冻结结果恢复和八个独立 worker。600 步固定种子操作使用独立列表作逐槽 oracle。512 次连续执行在事件间插入额外领域槽位并越过旧事件容量，在第 256 次冻结，再比较恢复续执行与直接执行的全部值。

这是存储与现有执行器的合同，不证明生成卡牌、Power、死亡、完整紧凑跨回合或生产 Beam 已迁移。结构性能仍由 [CompactCandidateChecks](../CompactCandidateChecks/README.md) 测量；本工具不输出速度结论。

`BufferContracts` 以三个独立列表作 oracle，覆盖 800 步交错分配／写入／嵌套撤销／冻结恢复，八工作区逆序读取，64／2048／65536 附近的索引层级边界，截短后较高索引树的叶复用与撤销，以及事件中完整有符号 32 位实例、目标、金额和标记的往返。见[索引缓冲区报告](../../docs/performance/simulation-indexed-buffer-20260911.md)。
