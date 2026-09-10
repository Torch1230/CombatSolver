# 生物值合同

`dotnet run --project tools/CompactCreatureChecks/CompactCreatureChecks.csproj -c Release`

直接链接真实值内核，不需要游戏。九个手写伤害边界包含小数、全挡／破防、穿透、过量伤害、零生命和数值上限；另测治疗与最大生命限幅。跨 64 槽页的两生物状态覆盖嵌套撤销、零生命仍在阵容、显式离场，以及八个工作区反复恢复冻结候选。

游戏差分入口是 `COMPACT-CREATURE-VALUES-NATIVE`，使用 `CORPSE_SLUGS_NORMAL`／SILENT，120 秒 headless 上限。其完整评估和原生伤害证据、适用范围见[阶段报告](../../docs/performance/simulation-creature-values-20260911.md)。这些合同不验证攻击命令、伤害来源历史或死亡 Hook 的紧凑执行。
