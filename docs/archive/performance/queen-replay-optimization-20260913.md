# 女王原包恢复与性能优化（2026-09-13）

后续已实现有界NoGC回退恢复，实测GC频率与总暂停下降，并记录峰值和最大暂停代价；见[后续报告](queen-gc-recovery-20260913.md)。下文保留恢复修复阶段的历史结果。

本轮修复同版本跨平台问题包被MVID提前拒绝的问题，实际加载用户女王包后定位分配热点，并只保留经过验证、成本合理的优化。当前分支基于上游 `bcc15da`，此前已提交的分配优化在 `1816778`；本轮最终增加无色药水生成入口的既有根候选池复用。没有调整生产搜索预算、排序、RNG或GC政策。

## 恢复修复与证明范围

包 `bc494904718141aab26bf81fa1ab1a25` 与本机游戏都是v0.111.0、release_info源码提交41cef1ea；Windows MVID为73b63ee0-6c0a-47bb-b0d1-b21f6d94222e，Linux为8a76776c-0ce1-4d4f-90bd-8cce653dad8e。MVID标识编译产物，不能单独证明存档不兼容。原实现只要两值不同就在PrepareCheckpointRequest抛出environment_mismatch，根本没进入恢复。

修改后，expected/actual/matches写入replayVerification.gameModuleComparison；仍执行原生跑局重建、事件重放及全部已记录ContinuationStamp字段和可比较native-state对账。缺少标识输出matches=null；标识相同也不能跳过状态验证。入口由Windows/Bash共用，没有另加跳过校验参数。

原包replay索引还声明solverInformationalVersion为0.37.0+83eb9f5fde7b5874264d5415726849b30274bcc6；这是包内声明，未拿原DLL独立核验。前轮仅从report/environment读取身份，现补充这项来源。

- 原包RestoreOnly c739d8b31f4c4479af58fb526ad00478 Passed，状态restored_continuation，完整已记录战斗状态匹配。
- 原模型编号表hash237224105，本机1568834832。旧包没有保存编号映射，原生二进制仍为not_comparable/legacy_model_id_mapping_not_recorded；nativeStateVerified及restorationVerified均为false，不冒充完整原生验证。
- 负向夹具只把本地副本最新metadata与配对replay-state的预期HP85改为84，保留原生跑局材料。cb3cc9d0ed354ee2ba4fd0b1ed89123b按预期Failed，首差异hp expected84/actual85；不是靠忽略真实状态差异恢复。
- 两项单请求120秒，原包和派生夹具均在.local，未提交。上一轮3750da4d990f4bf59374d5ccb96f8558的提前拒绝保留为修复前证据，不重复跑相同失败。

## 原包为什么慢、为什么频繁回收

35张静默猎手牌，Beam512、100000节点/300000ms、分支100/42/54、DOP16/NoGC16GB。牌堆不大，但抽弃牌和生成选牌分支多，每个分支仍需独占可变战斗状态。Windows原日志中约46.6秒内完成7次安全回收，随后系统物理占用约29.29GB达到策略阈值约29.229GB，NoGC重启返回system_headroom_insufficient。约236.9秒累计进程分配90.699GB、GC暂停130.923秒，搜索从约60388节点只推进到76609。详见[上一轮完整GC时序](state-sharing-20260913.md)。进程分配含其他Mod/渲染，不全部归因于solver。

源码确认：重启失败后清除当前NoGC区域配置，UseDefaultGcFallback在本次搜索内保持自动GC，以避免不断重复失败的预留循环。因此日志后半段是自动GC持续处理高分配负载，不是每次暂停都由求解器主动强制回收。恢复NoGC重试需要新的余量/冷却/生命周期设计，不能仅删除fallback锁定；当前Linux实验不证明Windows物理压力下的效果，本轮不改变这一政策，也不强制压缩或提高NoGC预算。

## 在实际恢复战斗上采样

固定每Solve10000节点，保留原Beam512、分支100/42/54、300000ms和药水/成长/战损政策，FixedBudget只用于测试，正常coordinator与药水审计共30000展开、578800转移、337802选牌分支。所有运行使用Linux headless新进程，DOP16/NoGC16GB。

诊断248b58026f8c40e3b94fe5325f2355cd Passed：32.105秒、27.0985GB worker累计分配。AllocationTick收集器退出0，261578个事件，252876个搜索相关事件，零缺栈、EventsLost=0。搜索栈加权分配27.4154GB，比精确计数约高1.17%；采样只用于归因，不用于正式测速。

| 首匹配分配调用族（不重叠） | 采样占比 |
| --- | ---: |
| Fork | 29.35% |
| 其他回放 | 22.65% |
| 回合推进 | 18.41% |
| 其他搜索 | 12.05% |
| 快照 | 11.61% |
| 保路/裁剪 | 4.27% |
| 手动出牌 | 1.67% |

更细的包含式调用栈中，DynamicVarSet.Clone约9.08%、ForkPower约8.98%、基础监听列表约3.95%；这些有父子重叠，不能相加。SimulationSnapshot对象本体约1.32%，不会靠缩小单个快照外壳就解决整体内存问题。未发现占绝大多数的单个临时容器；这与2305卡夹具中监听数组主导的情况不同。

## 原型、取舍与合同

保留无色药水与宇宙药剂（CosmicConcoction）生成入口复用既有根无色候选池。根已按解锁、角色/池身份及单人约束捕获原序候选；调用沿用GetDistinct/TakeRandom，生成卡与升级仍独占，未持有simulator的预览保持原筛选。此次没有新建跨分支结果缓存。

POTION-GENERATION-CACHE / eb117f8356294d50abf2afe45a045099 Passed：两类药水、4个RNG起点共8组，比较候选顺序和完整卡牌指纹、5字段RNG、升级与返回形态、可变模型独立性及父分支/子分支/live状态不变；同时调用已有根池门禁合同。

空DynamicVarSet克隆也做过原型：保留独立集合/字典和owner初始化，只省略空枚举，在既有按隔离域刷新、精确核对原版克隆阶段的证明内执行。MODEL-CLONE-CONCURRENCY / b0c51f73d66a4967af8df592108d0eab Passed，包括非空变量/外部元数据、持真实BaseLib锁的并发克隆、空集独立存储、后续变非空、null-owner以及Clone/Values/构造器/初始化补丁的跨域失效。

但将它与药水池优化组合的四次ABBA没有时间收益：平均31.0935→32.1553秒（+3.415%），分配27.1256→26.6444GB（−1.774%），GC暂停2164.240→3471.415ms。84字段、28动作及其余结果文本一致。单独药水池探针f01002e0c45c4c79aa4b39029a00c081为30.9269秒/26.8022GB/GC2247.424ms；空集原型在它之上仅再省约0.16GB，与额外框架补丁和校验维护成本不相称，已经撤回。单个隔离样本不作为独立提速证明；原型补丁和全部数据留在.local，未把撤回实现带入PR。

进一步共享可变Power/变量、跨分支省去选择回放或自动重启NoGC，均需要新的所有权、等价性或压力恢复协议。不能因为同一条牌路反复出现，就把不同RNG/选择/历史状态当成同一回放。本轮不实施高成本重写。

## PR全部保留改动相对上游的正式对照

A为隔离checkout的bcc15da，仅加相同MVID诊断修复以允许原包加载；B为1816778加本轮恢复修复和药水候选池复用。两边仅在捕获结果后写出细节，搜索中无诊断插桩。实际ABBA四次独立进程；与上方相同10000节点固定profile。每次均30000展开/578800转移/337802选择，84项非时序与非调度字段、28步完整动作及其余结果文本逐项相同。预测边界NodeLimit，最终HP13/敌HP516、预计战损72、零药；不是完整胜利或部署结果。

| 样本/runId | 搜索秒 | 累计worker分配GB | GC暂停ms | 采样峰值RSS GB |
| --- | ---: | ---: | ---: | ---: |
| A1 / 72077bb3ac5e488291fedd90312e79ea | 31.4074 | 27.89752 | 2207.019 | 13.2466 |
| B1 / 959850cae45b440c90ae4fcc3e965fc3 | 31.1902 | 26.79565 | 2203.417 | 13.1471 |
| B2 / 70c2c8fd813a42baa3bdcea4e06c5100 | 30.9591 | 26.80909 | 2182.825 | 13.1431 |
| A2 / a040a88400214e45b242b8a8b6efdf41 | 30.0250 | 27.86846 | 97.459 | 14.1142 |

平均分配27.88299→26.80237GB，减少1.08062GB/3.8756%，两对均下降。平均耗时30.7162→31.0747秒（+1.167%），两对方向不同，不宣称稳定提速；GC暂停均值1152.239→2193.121ms，不能说GC或Windows卡顿已经改善。最末A2的GC暂停仅97.459ms，与其他样本约2.2秒不同，完整保留该波动。

这里的3.88%是整个PR在这个女王输入上的累计分配收益；先前另一场景的11.13%标签收益或超大牌堆26.81%容量收益不能套用。更小累计分配也不自动等于更小存活堆或峰值RSS。GB均为十进制，RSS按100ms采样整个进程，包含建局及恢复。

## 验证与复跑入口

最终正常Release构建10.31秒、0警告/错误，Bash与PowerShell结构门禁均通过（89个Search文件）。所有游戏请求上限120秒；无可见Steam或Windows实机重测。

原包使用两端run-unattended-test入口的CheckpointArchivePath、CheckpointSelector=latest、ReplayMode=RestoreOnly/SearchOnly。短搜覆盖文件只设profile的maxExpandedNodes=10000与fixedBudget=true，其他profile值来自包内记录。原值、覆盖值、实际配置与每份结果均保留在.local/queen-optimization-20260913。

[结构化结果、采样与撤回数据](queen-replay-optimization-20260913.json)。完整原始ZIP、trace、原型补丁和完整日志不提交。

最终正常构建另跑原包原始profile（Beam512/100000节点/300000ms、无FixedBudget）：2fd12be960ad45afbd6b74dedfe7f422在120秒请求上限内未返回，启动器明确记录超时并停止游戏；含启动/清理总耗时134.031秒，采样峰值RSS22.06464GB。没有完整搜索指标，不补造速度或质量结果。本轮修复了恢复误拦并减少分配，但没有解决原始大预算慢搜，也未证明Windows频繁GC已解决。
