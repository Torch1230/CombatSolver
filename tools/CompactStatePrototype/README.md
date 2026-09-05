# 紧凑状态 P2 原型

这是独立的 **synthetic kernel prototype**，用于判断表示方式的适用边界。它没有引用游戏程序集，没有实现真实卡牌、Hook、选牌、历史、怪物 AI 或并行 worker，不能将下面的数字解释为 CombatSolver 整体加速。

当前结论：**暂不把 P2 接入生产引擎。** 稀疏标量写入时，page COW 确实能降低持久分支分配；undo journal 的主要收益来自可以立即回滚的深度优先工作。保留多个 frontier 时，journal 仍需要独立快照；密集写入时，两者都有明显成本。真实引擎尚需解决模型身份、Hook 状态和副作用的统一所有权，当前 P0/P1 的真实场景采样更能直接指导下一步。

## 运行和证据

从仓库根运行，不启动游戏，不构建 Mod：

```bash
dotnet run --project tools/CompactStatePrototype/CompactStatePrototype.csproj -c Release -- \
  --entities 256 --depth 6 --fanout 4 --repetitions 5 \
  --output tools/CompactStatePrototype/results-linux-net9.json
```

提交的 [原始结果](results-linux-net9.json) 来自 2026-09-05、Arch Linux、.NET 9.0.19、x64、16 个逻辑处理器。项目为此微基准关闭 tiered compilation；否则很短的样本会混入不同编译层级。每种组合执行 5 个样本，轮换实现顺序；每个样本在计时前执行一次完整预热和一次 Gen2 回收。每个样本固定 5460 次 transition，最后一层有 4096 个分支。

时间是 Stopwatch 测量的墙钟，分配是当前线程的 `GC.GetAllocatedBytesForCurrentThread()` 差值。根构造、一次预热和显式采样前回收不在窗口内；窗口中的自然 GC 包含在时间中。journal 的复用缓冲区在预热期间完成扩容。所有策略都执行同一 transition 和同序号的分支，并检查相同工作数与 checksum。

这里不是隔离机器上的稳定性能承诺：没有测真实帧、严格 GC trace、SOH/LOH 分区、峰值存活图或多核吞吐；完整逐次样本保留在 JSON 中。全状态正确性检查与轻量计时 checksum 分离，避免基准每次都复制全部状态。

下表是每 transition 的**分配字节 / 中位耗时纳秒**：

| 工作形态 | 写入模式 | Deep copy 参考 | Undo journal | Page COW |
| --- | --- | ---: | ---: | ---: |
| 深度优先，访问后立即回滚 | 稀疏 | 8248 / 1606 | 0 / 147 | 1393 / 661 |
| 深度优先，访问后立即回滚 | 密集 | 8248 / 5096 | 0 / 7216 | 9336 / 12125 |
| 保留整层 frontier | 稀疏 | 8594 / 10976 | 8474 / 10117 | 1737 / 5228 |
| 保留整层 frontier | 密集 | 8594 / 15442 | 8474 / 15510 | 9680 / 30201 |

这个配置中，保留稀疏分支的 page COW 分配少约 80%；密集写时，COW 的分配反而多约 13%。journal 在深度优先场景预热后不分配，但密集写仍慢于深拷贝。保留 frontier 的 journal 也要复制全部标量；它比参考少的约 120 B/transition 来自事务栈元素布局不同，不是 journal 消除了状态快照。

## 表示和语义检查

- `RootSnapshot` 私有持有不可变实体定义和初始值；调用者只拿值类型 `EntityId` / `EntityDefinition`，初始数组通过拷贝交付。非法 ID 显式失败。
- 分支只有连续 `long` 标量：RNG 状态与调用计数、回合、transition 数，以及每实体 HP、格挡、能量、出牌次数。稳定 ID 通过 root 转成槽位；没有可变对象引用。
- `DeepCopyState` 提供简单完整数组拷贝参考。
- `UndoJournalState` 按写入记录旧值，支持嵌套事务。内层 commit 仍可被外层 rollback 撤销；持久 Fork 必须复制标量数组。
- `PageCowState` 按 64 个 `long` 一页共享，写入首次触碰的共享页时复制。页表也需复制。`Shared` 标志单向置位，不做引用计数，因此不能因旧分支已释放自动恢复原地写入。原型只支持单线程；没有提供生产级并发发布协议。
- 两种工作形态分别是深度优先回滚与保存整层子节点；后者没有 Beam 剪枝，目的是暴露多个可继续分支必须同时存在的成本。

`PrototypeChecks` 已通过以下 7 组检查：

1. 根快照不变、稳定 ID 映射与非法 ID 拒绝。
2. 持久 sibling Fork 隔离：子、父、另一子分支分别写入，同页数据与 RNG 都不能串扰。
3. 第三次写入之后抛出异常，完整标量与 RNG 恢复。
4. 第三次写入之后取消，完整标量与 RNG 恢复。
5. 嵌套 commit/rollback、同一字段多次写入、缓冲区复用。
6. 混合稀疏/密集的 64 次连续动作：每一步对比全部状态，而非仅比较 hash。
7. 三种实现的完整 DFS 节点状态相同；所有保留叶节点都与从根按同一路径重放的状态相同。

这证明的是该标量模型自身的分支语义。没有验证原版游戏调用、后台模型克隆、deferred 选牌、真实跨回合 continuation 或任何完整战斗。

## 与真实引擎的差距和迁移路径

当前生产边界决定了不能把 `long[]` 直接替换进 `SimulatedCombatState`：

1. **先定义完整状态映射。** 根捕获仍归 `CombatRootSnapshot`；逐一列出 `SimulatedCombatState`、`PredictedCard`、牌堆、Power/遗物/球和 `PredictionStateStore` 的可变字段。卡、Creature、Power 等引用需统一稳定 ID，所有引用重映射通过同一个 `PredictionForkContext` 语义完成。原型没有可变牌堆顺序、附魔和隐藏模型字段。
2. **保持一个权威写入口。** 原生镜像目前通过 `MutablePreview`、克隆 Model 和 support 方法修改状态；紧凑页必须接管这些写入，同时保留 fingerprint、选择键与 Hook listener cache 的失效时点。只复制部分标量而留下旁路模型写入，会出现无法回滚或未进入状态键的状态。
3. **保留事务与事件身份。** `CardPlay` 的活动身份、自动出牌、deferred draw/generation、Power 与死亡事务都需在稳定边界结束后才 Fork。journal 只能撤销已经登记的影子写入，不能替代异步/外部副作用隔离。多分支 Beam 与并行 lane 也不能共享一个正在变动的 journal 所有者。
4. **切断剩余对象图必须处理真实身份。** 当前 P1 将出牌与伤害历史的 `PredictedCard` 改为既有快照，解除历史到 wrapper、owner pile、mutation observer 的确定持有。原生 `CardPlay` 身份仍保留。预测召唤 `Creature.CombatState = this` 仍可由 `CardPlay.Target`、卡牌 `CurrentTarget`、伤害 Receiver/Dealer/Result 或 Trace 中的 Power.Owner 回指创建分支；Fork 自己也保留这些 Creature 身份。单独压缩历史字段无法解决这一整条所有权问题。
5. **按真实语义分段迁移和验证。** 可先选已证明分配占比高、写入集中且语义闭合的一族字段；双实现只作验证对照，不能各自结算一次。需要 actual/simulated 最小差分、Fork 双分支隔离、两回合生命周期、选择/死亡/召唤、全部相关 RNG、fingerprint 与 continuation 等价，再考虑接入搜索。
6. **最后判断收益。** 先用真实固定工作量记录分配类型、每转移成本和保留图；生产 P1 已在移除明显历史持有，独立 StateStore 改动也在减少 Fork 元数据。只有剩余成本仍集中在可紧凑化字段时，P2 的迁移成本才有依据。最终收益仍需正常可见 Steam 场景验证，不能套用本工具的倍数。

保留这个可审计原型作为后续表示研究的参考是合理的。现在直接采用完整 P2 会扩大正确性验证面，也没有证据表明原生 Model/Hook 适配成本能够被这些标量内核收益抵消。
