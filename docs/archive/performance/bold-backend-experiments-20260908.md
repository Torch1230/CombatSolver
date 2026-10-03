# 较大范围的后端性能实验（2026-09-08）

[返回性能目录](../../performance/README.md) · [结构化数据](bold-backend-experiments-20260908.json)

用户要求大胆优化后，本轮实际实现并测量了五项候选，涉及快照计算时机、RNG Fork 表示和状态容器。它们均未显示足以保留的速度收益，全部撤回；最终生产代码仍为 `36aef6a`。大幅提速目标没有达成，本轮不计新增优化。

后续[CPU 分析](cpu-microarchitecture-20260908.md)测到同版首尾约 5.3% 的时间漂移。本页 1–2% 的小差异应继续解释为收益未建立，不能确认真实回退；对缓存命中、失效和具体抵消项的归因也仍未完成。

## 固定工作量结果

基线和每个候选各用独立 headless 进程，先一次相同短搜预热，再一次正式短搜。沿用死灵法师药水投影输入：`NECROBINDER`、`AEONGLASS_BOSS`、A10/Act2、526 敌 HP、41/76 玩家 HP、38 张牌、20 件遗物、2 瓶药水，要求至少用一瓶。极高预设、DOP8、NoGC16GB、详细诊断与增量关闭；每请求 120 秒上限，首结果停止。

全部正式请求 Passed，均为 Short/NodeLimit、10,000 展开、144,368 转移、101,808 选择分支、投影战损 3，NoGC rollover 为 0。这里的聚合结果只校验工作量，没有逐字段证明完整状态等价。

| 实现 | 正式耗时秒 | 相对耗时 | worker 分配 GB | 相对分配 |
|---|---:|---:|---:|---:|
| 现有实现 36aef6a | 5.6707 | +0.00% | 7.3964 | +0.00% |
| 延迟手牌估值 | 6.6062 | +16.50% | 7.4100 | +0.18% |
| 九路 RNG 不可变值槽 | 5.9428 | +4.80% | 7.3120 | -1.14% |
| 单条目紧凑 COW 字典 | 5.7457 | +1.32% | 7.3859 | -0.14% |
| 三条目紧凑 COW 字典 | 5.7928 | +2.15% | 7.3223 | -1.00% |
| 无序牌堆指纹缓存 | 5.7442 | +1.30% | 7.4105 | +0.19% |

每组只有一个正式样本；约 1–2% 的时间差不足以确定真实回退或提速。分配量是累计 worker 分配，不是占用内存。独立进程 RSS 峰值与全部预热/正式 runId、搜索指标一并保留在 JSON；候选源码的局部补丁及临时测试留在本地实验目录，不进入产品或源码提交。

## 五项尝试与撤回理由

1. **延迟手牌估值**：让快照先持有模拟器，读取时计算可达手牌价值与零费可出牌数，并在 Fork、跨线程交接和释放前物化，丢弃节点可跳过未查询值。实际耗时增加 16.5%。计算可能从 worker 移到了串行消费路径，但本轮没有 CPU 采样证明该归因；复杂生命周期不值得保留。
2. **RNG 值槽**：九路流在 Fork 时冻结 Counter 与四个状态字；子分支按需创建原生 RNG，状态键与续用直接读冻结值，投影洗牌取独立克隆。分配减少约 1.14%，耗时增加约 4.80%，撤回。补写的独立 RNG 合同草稿没有构建或运行，不计通过。
3. **单条目紧凑字典**：0/1 条目直接放入 COW storage，第二个不同键开始永久使用原生 Dictionary，避免小表的桶数组和条目数组。耗时差 +1.32%、分配仅 −0.14%，没有明确收益，撤回。
4. **三条目紧凑字典**：小表共享三槽条目数组，移除后的空位按原生 LIFO 顺序复用，第四个条目时永久升级原生 Dictionary；COW 复制按原生复制构造器压紧有效条目。耗时差 +2.15%、分配 −1.00%，同样撤回。
5. **无序牌堆指纹缓存**：在 SimCardPile 的已有指纹失效机制内增加无序指纹，未变牌堆及其 Fork 可复用；卡牌/牌堆写入失效，外部可变附属模型仍旁路缓存。耗时差 +1.30%，分配反增约 0.19%，撤回。新增 Fork 合同草稿未构建或运行，不计通过。

紧凑字典两版均实际运行独立 .NET 9 对照：9 万次随机操作、最多 32 个父子分支，使用现有原生 Dictionary 包装实现作 oracle；覆盖自定义比较器、哈希冲突、插入/覆写/删除/清空、顺序、异常、枚举器、捕获的 Keys/Values 和原键对象身份。三条目最终测试还检查在 GetEnumerator 后、首次 MoveNext 前新增条目的失效时机。测试只是候选容器合同，没有替代游戏 Fork/原生差分。

## 可重跑的基线输入

```bash
./tools/run-unattended-test.sh \
  --scenario-id BOLD-BACKEND-BASELINE \
  --character-id NECROBINDER --seed SEARCH_PERF_NECROBINDER_POTION \
  --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2 \
  --enemy-current-hp 526 --initial-player-hp 41 --initial-player-max-hp 76 \
  --clear-run-deck --cards-json '[]' \
  --run-cards-path coverage/unattended/search-performance-necrobinder-projected-run-cards.json \
  --relics-path coverage/unattended/search-performance-necrobinder-projected-relics.json \
  --potions-path coverage/unattended/search-performance-necrobinder-projected-potions.json \
  --potion-policy-for-test RequireAtLeastOne \
  --performance-preset-for-test VeryHigh --search-max-degree-of-parallelism-for-test 8 \
  --enable-no-gc-region-for-test 1 --no-gc-region-budget-gigabytes-for-test 16 \
  --enable-detailed-diagnostic-logs-for-test 0 --force-short-search-only \
  --stop-after-initial-solver-result-assertion --timeout-seconds 120 \
  --keep-game-open --headless-instance bold-backend-benchmark \
  --combat-solver-build-dir "$benchmark_artifact" \
  --evidence-directory "$benchmark_evidence"
```

`benchmark_artifact` 指向固定构建，`benchmark_evidence` 指向新证据目录。每版新进程先预热一次，再换 scenario/evidence 名运行同一命令，正式完成后停止该实例。撤回的原型需要本地保存补丁，当前仓库命令只重跑现有实现。

没有保留行为改动，因此未扩大到正常配置 A/B、DOP 合同、原生差分、整场部署、可见 Steam、Windows 或完整发布门禁。恢复原生产源码后重新 Release 构建，防止工作区 DLL 留在被否决的实验版；未部署、提升版本或发包。
