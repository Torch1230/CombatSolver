# Ritsu 目标类型查询缓存：验收记录

未加载 BaseLib 时，RitsuLib 0.5.18 在药水目标检查中反复扫描程序集，查询不存在的 `BaseLib.Patches.Features.CustomTargetType`，造成重复反射分配。补丁只在模拟隔离域缓存静态程序集的精确查询结果，保留原枚举顺序、目标谓词、live/动态程序集旁路，通过 ConditionalWeakTable 支持卸载。

## 来源与范围

PR基于main `0552b33`，仅提取缓存代码、直接合同、职责约束与本文；没有包含前期搜索诊断和撤回实验，不改变上游版本号或药水政策。

下列游戏数据来自固定上游 `518186939e57087e14e7f2b42f285eaef6b7444c`（v0.31.0）上的本地研究：A冻结源码 `158ad68b86088e41384f7290c2f9b2c07f98d530`，B冻结源码 `195ba83d71b7f58c346e28d318f567e426c9f449`。A/B具有相同研究观测，B只增加本补丁。这里的研究源码身份是历史记录，研究分支、游戏文件、玩家资料和完整日志未包含在PR中。**不把这些数字视为最新main的新药水政策、战前worker或路线缓存的验收。**

## 当前PR最小检查

当前PR基于main `0552b33`：10项直接合同通过，Release编译0警告/0错误，Windows结构检查通过（search_files=70）。新工作树首次完整构建在MemoryCleaner的net48引用导入尚未生效时失败；NuGet还原产生导入文件后重新加载项目构建成功，未修改构建源码。生产缓存源文件和直接合同与已测研究补丁相同，Entry只新增一条显式注册。未在这个新的上游组合上重跑游戏A/B。

```powershell
# local.props指定本机Sts2DataDir、RitsuLibDir，并设置CopyModOnBuild=false。
dotnet run --project tools/RitsuTargetTypeLookupChecks -c Release -p:NuGetAudit=false
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false -p:NuGetAudit=false
pwsh -NoProfile -File tools/verify-refactor-boundaries.ps1
```

合同直接patch本机Ritsu实际回调，覆盖精确原语义、静态正负查询、live旁路、动态晚创建、2000次并发检查和可卸载程序集。原研究预热后10万次缺失查询分配18400000→0 B；首次冷路径断言因448 B运行时开销失败，失败保留后改用预热同工作量对照。

## 固定研究基线的游戏结果

本机Windows 11、i7-12700H、约16 GB RAM、Turbo电源方案；游戏0.111.0/Steam build24724944、RitsuLib0.5.18、SDK9.0.304。全部受控A/B独占串行，明确冻结构建目录；DOP4且实际并发4、普通GC，关闭No-GC、详细路径观察和增量完整回放。可见对照从正常Steam启动，记录渲染器、非零窗口句柄与真实加载路径，受控Mod栈为RitsuLib、CombatSolver、PRTSCursor，无BaseLib。临时部署和玩家资料均已恢复。

| 场景 | A→B 累计搜索分配 | A→B 计账耗时 | 行为对账 |
|---|---:|---:|---|
| Headless目标 | 652654160→428051824 B（−34.4%） | 2976.1→2831.6 ms | 完整10动作/126非时序字段、输入和政策一致 |
| 正常可见Steam目标 | 643988976→430794160 B（−33.1%） | 3263.2→3041.0 ms | 同上，实际Mod栈一致 |
| Headless Aeon哨兵 | 625688048→654033072 B（+4.5%） | 1726.7→1748.6 ms | 完整8动作/126非时序字段、输入和政策一致 |

目标固定576节点、13383转移、11133选择；0损/1药，玩家80 HP、敌942 HP。10动作包括9个可执行动作和结束回合。两边为NodeLimit，没有TimeLimit、超时或时间切层标记。

公开目标建局：IRONCLAD / FUZZY_WURM_CRAWLER_WEAK，种子 `M0_PUBLIC_CHOICE_HAND_POTIONS_0111`，A0/Act0；玩家80/80 HP、0格挡、20能量，敌999/999 HP、0格挡、INHALE。清空跑局牌组、战斗牌堆与Power；手牌DUAL_WIELD、BASH、HIDDEN_DAGGERS、DEFEND、STRIKE、PURITY、ANGER、IRON_WAVE各1张，抽牌堆BASH；药水GAMBLERS_BREW、ASHWATER。RequireAtLeastOne政策，Custom使用仓库 `coverage/unattended/gc-issue36-benchmark-settings.json`，固定576节点，ForceShortSearchOnly、首结果停止。卡牌/药水来自现有公开choice-hand和potion夹具。每次运行须指定各自冻结的CombatSolverBuildDir及新输出目录。

Aeon哨兵固定576/1883/61、Smart政策；已有公开63牌组 `search-performance-complex-random-aeonglass-cards.json`。该根仍是死亡路线，不能作为胜利或原版严格语义证明。增加的分配主要在回合开始（约24.53 MB），药水执行两边均0 B；尚未严格排除这项性能回退。

可见GC Gen0 52→35、Gen1 20→18、Gen2 1→1；共享窗口累计暂停458.399→399.685 ms，最长暂停18.618→22.313 ms；最大帧间隔613.3→628.5 ms，>50 ms帧均1。终点工作集2073362432→2144178176 B。累计分配收益不等于峰值驻留或长帧收益。没有长期温度/频率曲线，单组短搜耗时下降6.8%不代表长期平均提速。

## 原始运行索引摘要

完整原文和冻结构建保留在研究机器，未随PR上传；下表保留每次游戏运行及不合格证据，不只列成功样本。

| 本地run目录 | 结果/用途 |
|---|---|
| perf-choice-profile-01 | 游戏Passed；辅助日志读取遇共享锁，trace尾截断 |
| perf-choice-profile-02 | 仅启动采样；游戏清理Failed，不作搜索性能证据 |
| perf-choice-profile-03 | 磁盘不足，未启动游戏 |
| perf-choice-profile-04 | 搜索采样成功；游戏清理Failed，仅定位热点 |
| perf-choice-baseline-01 | Passed，22dfdc6aaae84220b27d885c9d3caccc |
| perf-choice-metadata-cache-01 | Passed，e14f2947838741c4a95055d61b872f11 |
| perf-aeon-baseline-01 | Passed，69f02cd266044befb5a13fc3c3b07fdc |
| perf-aeon-metadata-cache-01 | Passed，db5028b0f7db4af6a387e9c9f9b5d771 |
| perf-visible-baseline-01 | 游戏Passed，e3b8b96a8d124f1aa66b4de98f2afe5e；首版launcher漏枚举嵌套manifest，额外加载BaseLib/MultiEnchantmentMod，排除出受控对照 |
| perf-visible-baseline-02 | Passed，848f697967ce478ca610264bf92d812e |
| perf-visible-metadata-cache-01 | Passed，a1b90640246f47d0b042740274562056 |

profile02/04使用未优化A，清理失败来自原游戏 `MaxEnumValueCache.Get<T>` 非并发Dictionary的并发访问。无采样器的正式对照清理全部成功，但没有宣称修复该异常。磁盘不足后，对已结束运行的相同私有PCK逐一SHA256确认再在研究归档内硬链接去重，释放59.33 GiB，未连接Steam源或删除失败日志。

范围限制：真实BaseLib已加载的最终补丁组合、其他Ritsu版本、长期热稳态、峰值内存和长帧改善均未验证。收益主要针对BaseLib缺失的重复扫描；已成功解析BaseLib时不预期相同幅度。
