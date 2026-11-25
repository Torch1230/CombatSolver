# 战斗路线求解器 / Combat Solver

Combat Solver 是面向《杀戮尖塔 2》单人模式（标准 / 恐惧 / 极速）的战斗路线求解器。进入战斗后，它在主线程捕获稳定局面，在后台独立模拟手牌、牌堆、敌人意图、药水、遗物与跨回合效果，并给出推荐路线与预计整场战损。你可以仅参考建议、执行当前回合，或连续全自动接管；搜索绝不修改游戏 RNG 或后台操作真实战斗。

- **发布与更新**：[GitHub Release](https://github.com/Torch1230/CombatSolver/releases/latest) · [版本更新日志](docs/releases/README.md)
- **参与贡献**：[社区任务入口](https://github.com/Torch1230/CombatSolver/issues/171) · [贡献指南](CONTRIBUTING.md) · [夹具与测试指南](docs/community/testing-guide.md)

> **English Quick Guide:** The overlay switches to English automatically when the game language is not Simplified/Traditional Chinese (restart to refresh existing panels). Use **Play turn** for one turn, **Auto: On** (or **Auto-enable auto-play**) for continuous play, **Recalculate** / **Stop search** / **Use candidate** / **Freeze route** to control execution, and `Ctrl+F9` to toggle the overlay. Configure **Potions**, **Growth**, **Relics**, and **Settings** (**General**, **Performance**, **Reports > Upload report** / **Export report**) from the header. Single-player only.

## 核心功能

- **高速跨回合引擎**：持续模拟后续抽牌、洗牌、敌人行动与状态结算，按生存、胜利与整场战损排优，而非只看眼前一回合。普通战斗瞬间完成，复杂战斗在时限内自动逐级深化。
- **清晰路线与灵活接管**：按回合展示出牌、目标、选牌、药水与关键遗物触发，顶部汇总本局已扣血、预计总扣血与回血。支持仅查看、**重新计算**、**执行本回合**、**全自动**（右侧可勾选**自动开启全自动**）；搜索中可**停止计算**或**采用当前路线**，出解后可**冻结路线**暂停自动重算；遭遇偷窃类敌人时可切换**保牌/保钱**或**放走**；按 `Ctrl+F9` 随时显隐界面。
- **原生选牌与路线复用**：开局选牌、回合初选牌与抽弃牌均保留游戏原生页面与动画；实机状态与预测一致时跨回合直接复用路线，仅在状态偏离时重新计算。

## 策略侧栏

主界面顶栏提供三个独立策略侧栏，配置均自动保存：

- **药水（Potions）**：实时列出当前药水图标与槽位，支持快捷预设（全部智能 / 全部保护 / 全部强制 / 仅用已强制）；每瓶可独立切换 **智能使用 / 强制使用 / 禁用保护**。智能模式下可逐瓶设定 **省血阈值**（按药水类型保存，支持单瓶「默认」与一键「重置阈值」，修改后立即重算；新获得药水默认智能使用）。
- **遗物（Relics）**：支持 10 种跨战斗循环计数遗物（如开心小花、摆动球、钢笔尖、双截棍等）配置 **结束计数范围**、**愿付额外战损** 与 **优先级（1–3，3 最高）**，并自动按优先级协调同周期错位遗物；同时支持 **带骨肉** 半血回血目标（按净战损权衡是否值得额外卖血）。可勾选「显示未持有」提前配置，或展开查看其他不可卡数遗物说明。
- **成长（Growth）**：可为 11 类原版局外成长来源（及第三方已登记来源）逐项设置每次收益允许的额外战损（默认 `0` 也会在同战损下优先拿收益）；提供 **不考虑局外收益** 总开关（关闭额度与保路，保留同战损平局优先），以及 **至亮之焰** 单场最大生命消耗上限开关与额度。

## 安装与兼容性

运行要求（声明值以 [CombatSolver.json](CombatSolver.json) 为准）：

- 《杀戮尖塔 2》最低 `0.114.0`（已完整适配 `0.114.0` 单人标准、恐惧与极速模式）
- [RitsuLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3747602295) `0.6.16` 或更高版本
- 仅支持单人战斗，不支持多人模式

推荐通过 Steam 创意工坊订阅。若通过 GitHub Release 手动安装，将以下四个文件放入游戏目录的 `mods/CombatSolver/`：

```text
CombatSolver.dll
CombatSolver.json
LICENSE
THIRD_PARTY_NOTICES.md
```

启用 RitsuLib 与 Combat Solver 后进入单人战斗，待发牌及回合开始效果结算完毕，覆盖层即自动开始计算。

**求解边界**：搜索基于时间上限的启发式探索，展示的是预算内找到的最佳路线，不承诺数学意义上的全局最优解。遇到未适配的第三方内容性 Mod 或未知战斗语义时，求解器会明确停止在不支持边界，不会误报胜利。覆盖范围见 [战斗 Hook 覆盖报告](docs/COMBAT_HOOK_COVERAGE.md) 与 [适配验证记录](docs/ADAPTATION_VERIFICATION.md)。

## 设置与性能

顶栏点击 **设置** 可切换三个分页：

### 常规（General）

- **开始计算**：求解器总开关与自动计算开关（关闭自动计算后改为手动点击开始）。
- **搜索停止条件**：默认开启「达到战损目标后停止搜索」，阈值默认 `0` HP（零损）；仅当完整胜利、未多用药水且当前在场的成长／遗物目标均满足时才会提前结束。
- **出牌速度与自动执行暂停**：自动出牌速度可选 **跟随游戏（默认）/ 正常 / 快速 / 瞬间**，并支持 `0–3` 秒牌间额外停顿；可分别设置在预计结束战斗、死亡回合或重算后战损增加时自动暂停交还控制权。
- **幕末 Boss**：第一、二幕与最终 Boss 分别可选 **通关优先（默认）**（一二幕按战后回血 80% 折算血量并优先保药，终战保活优先留资源）或 **最低战损**。
- **药水奖励预测**：默认关闭；开启后提前显示战后药水掉落并在药水栏已满时纳入智能用药考量。
- **显示与通知**：默认 **深色主题** 与 **`65%` 覆盖层透明度**（范围 `25%–100%`，可切换浅色主题，立即生效）；支持 Windows 搜索结束系统通知与提示音（关闭 / 仅游戏不在前台 / 始终）。
- **在线统计**：默认开启，每 30 秒发送心跳并同步跑局统计；设置中可随时关闭（停止上报，最迟 90 秒离线）。详见 [在线统计](docs/ONLINE_STATISTICS.md) 与 [跑局战绩](docs/RUN_STATISTICS.md)。

### 性能（Performance）

新一代高速引擎大幅降低了内存占用，不再需要任何 NoGC、多核回收或内存清理工具，性能页仅保留两项设置：

- **战斗最大求解时长**：默认 `30` 秒（范围 `1–600` 秒）。普通战斗仍会瞬间完成；遇到复杂或高战损战斗时在设定时限内自动逐级深化。
- **最大并发线程数**：默认 **自动**（16 核及以上 `8` 线程、4–15 核 `4` 线程、2–3 核 `2` 线程，其余单线程），可手动设为关闭（单线程）或 `2–16` 上限。若疑为并行异常，请先上传问题包，再切换为单线程重试。

### 反馈（Reports）

遇到错误路线、计划外重算、执行中止或搜索失败时，请在「设置 > 反馈」点击 **上传问题包**（自动附带版本与问题分类），或点击 **导出问题包** 保存至桌面 `CombatSolver-BugReports` 手动提交。页面还提供选填联系 QQ、搜索分支调试日志及保留本进程全部战斗日志开关。

## 战前预测 API 与第三方适配

`CombatSolver.Api.PreCombatForecastApi`（当前版本 `v6`，仅支持 Windows 单人非战斗状态）为地图信息类 Mod 提供战前模拟能力。它在独占的隔离无头游戏进程中加载与主进程一致的 Mod 副本并校验状态令牌（主跑局或 RNG 变动时返回 `LiveStateChanged`）：`ForecastAsync` 用于确定性战前预测并复用同参结果缓存；`SimulateAsync` 用于指定怪组与样本种子的假设评估；`SimulatePlanningAsync` 允许传入独立 `SerializableRun` 规划存档。隔离进程默认空闲 2 分钟自动关闭，并可通过 `SetWorkerIdleTimeoutAsync`、`RestartWorkerAsync`、`StopWorkerAsync` 与 `GetWorkerStatus` 控制生命周期。

```csharp
if (PreCombatForecastApi.IsAvailable)
{
    PreCombatForecastResult result = await PreCombatForecastApi.ForecastAsync(
        run, encounter, targetActFloor, targetMapColumn,
        PreCombatRoomKind.Normal, PreCombatMapPointKind.Normal);
}
```

主项目与社区任务面向原版游戏内容，不主动实现修改战斗语义的第三方 Mod 适配；第三方作者可查阅 [第三方 Mod 适配手册](docs/third-party/README.md) 了解登记入口、语义门禁与验证标准。

## 构建与开发

项目基于 C# / .NET 9 / Godot，构建脚本会自动探测常见 Steam 目录（如需自定义路径，请复制 `local.props.example` 为已被忽略的 `local.props`）：

```powershell
# Windows (PowerShell 7)
pwsh -NoProfile -File tools/build/build-local-stack.ps1 -Configuration Release
```

```bash
# Linux (Bash)
./tools/build/build-local-stack.sh --configuration Release
```

无人测试入口为 `tools/testing/run-unattended-test.ps1` 与 `tools/testing/run-unattended-test.sh`（UI 布局、动画与实机帧率仍需在可见 Steam 会话验证）。更多开发指引见 [文档总目录](docs/README.md)、[架构地图](docs/ARCHITECTURE.md)、[开发笔记](docs/DEVELOPMENT_NOTES.md)、[测试矩阵](docs/TEST_MATRIX.md) 与 [重构路线](docs/refactoring/refactor-roadmap.md)。

## 开源、许可与代码来源

Combat Solver 的内置战斗模拟核心使用并改造了由 **hotwords123** 创建的 **Random Foreseer**（MIT License），涵盖战斗状态、牌堆、RNG、Fork、History 与 Mirror 等基础逻辑；Combat Solver 在此基础上持续重构并扩展了跨回合求解、路线复用、自动执行与原生选牌流程。Combat Solver 不加载或分发 Random Foreseer 程序集作为运行时依赖，运行时分离不改变上述代码来源关系。

- [Random Foreseer GitHub 仓库](https://github.com/hotwords123/StS2.RandomForeseer)
- [Random Foreseer Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3747531952)
- [Combat Solver GitHub 仓库](https://github.com/Torch1230/CombatSolver)

感谢 **hotwords123** 与 Random Foreseer 的工作。完整来源关系、署名与早期书面许可记录见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)，并随每个二进制发布包一同提供。本项目采用 [MIT License](LICENSE)。
