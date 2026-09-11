# Adapted OnPlay 合同检查

独立进程，不启动游戏、不部署 DLL，不访问玩家状态。

```sh
dotnet run --project tools/AdaptedOnPlayChecks/AdaptedOnPlayChecks.csproj -c Release -p:Sts2DataDir=/path/to/game/data
dotnet run --project tools/AdaptedOnPlayChecks/AdaptedOnPlayChecks.csproj -c Release -p:Sts2DataDir=/path/to/game/data -- --empty
```

使用目标游戏自带的 `0Harmony.dll`。链接生产补丁审计、组合登记、标准方法 registry 和 descriptor；`TestContracts.cs` 只提供游戏模型、Mod 来源、trace 与模拟器的最小外壳。真实 Harmony 对托管卡牌安装／卸载补丁，核对替换和前后缀组合效果、精确匹配、配置变更与冻结分派。内层补丁、同类重复方法及 async MoveNext 属拒绝合同。

不验证真实战斗数值、游戏 async 调度、完整 simulator Fork 或控制器实际部署；配置失配的检测通过标记变化验证，Runtime 接线由结构门禁约束。不含计时或性能 A/B。
