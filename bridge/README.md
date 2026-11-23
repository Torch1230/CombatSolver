# Sts2Sim 桥接

CombatSolver 维护 UTF-8 JSON ABI、战斗预测、路线政策和打包清单。独立的 `Sts2Sim.Forecast` 项目把消费端策略和锁定版本的通用 Solver 源码编入包内的 `Sts2Sim.Solver.dll`。桥接通过 Core 的公共 API 恢复游戏状态。Godot Mod 保持零编译依赖，运行时校验清单、SHA-256 和 MVID。

## 构建

从包含本 PR 的工作树运行以下命令，依赖 .NET 9 SDK 和 Git。构建游戏 Mod 还需项目现有的本地游戏引用。

```sh
git submodule update --init vendor/sts2-sim
dotnet test bridge/tests/Sts2Sim.PreCombatBridge.Tests -c Release
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
```

打包目标校验子模块检出与 gitlink 一致，并把 Core DLL 内嵌的完整源码提交写入 `sts2sim-bundle.manifest.json` 的 `simulatorCommit`。开发者可显式传入 `-p:Sts2SimRoot=/path/to/checkout` 验证另一个已提交的版本。只构建宿主可指定 `-p:StageSts2SimBridgeEnabled=false`。直接运行桥接测试时，开发源码参数名为 `Sts2SimSourceRoot`。

## 依赖升级

过渡 gitlink 使用经维护者同意的 `iRyougi/sts2-ai` 确切提交，需要该仓库的读取权限。公开 `iRyougi/sts2-sim` 的 `v0.113.1.4` 仅发布 Core。维护者合入恢复接口、导出通用 Solver 并发布新标签后，单独提交依赖升级 PR，更新 `.gitmodules` 的公开 URL 与 gitlink。检验干净检出、两条 ABI 和包内清单后再发布。[跨仓跟踪](https://github.com/iRyougi/sts2-ai/issues/1083)

主模组消费旧 ABI 时仍按协商结果选择内置或遗留后端。模拟器仓库保留 Core、标准模式一致性和通用 Solver，CombatSolver 专用的费用估值、组合与调度在本目录维护。

### 战前 ABI 的模式

schema-21 请求通过 `normalizedSaveJson.game_mode` 指定 `standard`、`blitz` 或 `dread`。Blitz 支持进阶 0–5，Standard 和 Dread 支持 0–10。未知模式返回 `CapabilityMiss/non_standard_mode`，越界进阶返回 `Failed` 并标示 `ascension`。模式与 Dread 领域参与非标准模式的预测状态键。Blitz 药水展望使用独立奖励 RNG，按卡牌、幕强化、药水的顺序预测。Dread 的饥饿领域伤害在敌人攻击意图前按无来源伤害结算。

主 Mod 是否把真实对局送入桥接由现有 eligibility 入口决定。战前 ABI 的能力与战斗内 live 接口分别验收。
