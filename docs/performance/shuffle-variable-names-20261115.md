# 同名牌的洗牌排序分配

8 张富足、42 张牌的构造场景中，搜索分配约减少 8.1%。
完整根、政策、路线和工作量一致。原生排序合同通过。
#241 缺少原包与种子，本记录不能证明原报告的卡死或崩溃已修复。

## 改动与边界

基线为 `3677d4ca`，游戏 0.113.1，RitsuLib 0.6.13，Windows，.NET SDK 9.0.321。
分配采样指向 `CombatCardOrdering.Compare`：比较同名牌时反复排序动态变量名。

`CombatCardOrdering` 在单次 `List.Sort` 内按 `DynamicVarSet` 引用复用有序变量名。
0 个变量直接返回，1 个变量直接比较。
费用、重放数和变量值仍逐次读取分支状态。
缓存不跨排序、分支或搜索，也不进入战斗状态键。

卡牌比较的字段顺序和变量名的字符序（StringComparer.Ordinal）保持。
`UnstableShuffle` 的调用与 RNG 消耗保持。
本次不更改候选、评分、剪枝、预算或 PR #235 的监听表路径。

## 目标与哨兵

目标输入为 [`eight-abundance-generation.json`](../../coverage/fixtures/search/eight-abundance-generation.json)。
手牌含 8 张 ABUNDANCE 和 2 张 FLASH_OF_STEEL。
抽牌堆含 32 张生成牌或过牌。
铁甲战士，永世沙漏 80 HP，玩家 80 HP、8 点能量。

哨兵使用 [`generic-loop-bloodletting-double-pommel-quality-v0111.json`](../../coverage/fixtures/search/generic-loop-bloodletting-double-pommel-quality-v0111.json) 的输入。
重复过牌与洗牌会比较变化后的费用和动态变量，适合检查缓存边界。
本次采用下面的 Low 配置，离线宿主不执行文件内的 `expected` 断言。
该配置在当前版本的基线结果为 6 战损、1 回合；两份候选均相同。
这不是旧文件所记 3 战损、2 回合的质量验收。

每个场景按 A1/C1/C2/A2 启动独立进程。
关闭 No-GC、药水与组合；并行度（DOP）1、Beam 12、软时间 10000 ms。
目标节点上限 1024，哨兵上限 128。
不启用增量验证、阶段测量或采样工具来测性能。

| 场景 / 版本 | 搜索耗时 ms | 请求墙钟秒数 | 搜索分配字节 |
| --- | --- | --- | --- |
| 目标 A1 | 2520.57 | 3.720 | 201628048 |
| 目标 C1 | 2399.67 | 3.586 | 185250688 |
| 目标 C2 | 2339.04 | 3.482 | 185271264 |
| 目标 A2 | 2411.68 | 3.589 | 201622880 |
| 哨兵 A1 | 1060.51 | 2.313 | 3300800 |
| 哨兵 C1 | 1093.63 | 2.267 | 3306456 |
| 哨兵 C2 | 1054.32 | 2.190 | 3307096 |
| 哨兵 A2 | 1092.88 | 2.225 | 3300800 |

目标四次均展开 391 个节点、转移 3610 次、选牌回放 1549 次。
均在第 2 回合获胜，1 战损、0 药水，无时间截断。
完整根、实机根、政策、动作、全部续用、最终快照及剪枝计数逐项相同。
搜索分配平均减少约 16.36 MB，即 8.1%。
耗时只有每版 2 次，不能据此宣称稳定提速。
哨兵四次均展开 9 个节点、转移 21 次，6 战损、0 药水，第 1 回合获胜。
哨兵候选的搜索分配增加约 6 KB，搜索耗时位于基线波动范围内。

逐次目标、哨兵与采样摘要见 [JSON](shuffle-variable-names-20261115.json)。
采样估计的 `OrderedIterator<string,string>` 分配由 16194520 降至 4473080 字节。
两份 trace 均完整转换，丢失事件、缺栈及全未解析栈均为 0。
采样权重不等于精确对象字节数，不用于计时结论。

## 原生验证

`NATIVE-CARD-ORDERING` Passed，26.35 秒。
runId：`f72bd5fd308d48c9a3c2d7e502673365`。
合同分别比较 ABUNDANCE、DEFEND_IRONCLAD、FINESSE、OFFERING 的 0/1/2/3 个动态变量。
各场景比较原生 `StableShuffle` 的完整牌序与 RNG。
同时覆盖费用、重放、变量值、实机推进、父子分支隔离、后续排序及重复引用。
隔离实例已清理。

原生测试使用 PR #247 的启动器。
本机另加载 DamageMeter 1.14.7、ModConfig 0.2.3、RemoveMultiplayerPlayerLimit 0.0.5、
Rewind 0.26.19、RouteSuggest 1.9.0。
尚未在仅安装 CombatSolver 与 RitsuLib 的环境验证。

## 复跑

先构建对应源码与离线宿主。
用 `OFFLINE_HARNESS_COMBATSOLVER_DLL` 选择控制或候选 DLL。
对照双方使用同一命令、输入与环境，输出目录分别保存。

```powershell
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools/search/OfflineSearchHarness/OfflineSearchHarness.csproj -c Release
dotnet .local/tool-build/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll --request coverage/fixtures/search/eight-abundance-generation.json --label target --out .local/abundance-sort/target --profile Low --beam 12 --nodes 1024 --dop 1 --budget-ms 10000 --potion-policy Disabled
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId NATIVE-CARD-ORDERING -EncounterId FUZZY_WURM_CRAWLER_WEAK -TimeoutSeconds 120 -CleanupInstanceOnExit
```

原生排序验证与离线搜索对照分别提供证据。
未验证原报告整局、默认高预算、No-GC 开启时的驻留峰值和可见 Steam 帧时间。
