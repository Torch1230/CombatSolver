# ServerGC 启动配置与 Steam 自动接入（2026-09-24）

本改动基于上游 `3d45d78f`，提供显式选择的 `server-generational` 启动配置：游戏进程请求 ServerGC，Mod 在确认运行库实际启用后，让搜索使用常规分代回收。首轮显式启动实验保留普通启动行为；后续新增的自动接入见下节。保存的 NoGC 设置不改写；搜索算法、评分、配置的节点／时间上限和药水策略不变。

## Steam 自动接入（后续追加）

包含本次追加的 Mod 加载后，默认开启自动启动配置：首次启动仅准备文件，当前搜索仍按实际 CLR 模式运行；玩家下次照常点击 Steam“开始游戏”，无需启动参数、脚本、PowerShell 或新的快捷方式。PR #133 已合入 `main`，本次设置文案调整未发布到工坊；订阅版本是否包含该行为应以实际已发布版本为准。

Mod 定位已加载游戏程序集旁的 `runtimeconfig.json`，设置 `System.GC.Server=true` 和 `CombatSolver.RuntimeProfile=server-generational`，并在 `CombatSolver.PreviousServerGc` 保存该字段原先为缺省／true／false。下一进程通过运行库启动时加载的 `AppContext` 标记及实际 `GCSettings.IsServerGC` 激活；不是读取刚改写的磁盘文件假装本次已生效。显式环境 profile 优先于配置标记。

- 只编辑游戏这一份启动文件；同目录临时文件刷盘后原子替换，不写穿硬链接，不修改注册表、全局环境、Steam 启动项、EXE 或其他 Mod。保留其他 JSON 字段；已配置时不反复写入。
- 设置页可关闭自动配置：只恢复本 Mod 记录的 GC 原值并移除自身标记，下次正常启动生效。本次已初始化的 GC 模式不变。未知／损坏标记或发现 GC 字段被外部修改时拒绝覆盖；检测到并发文件变化也拒绝写入。
- 无写权限、缺失或非法配置时记录失败，继续按实际原模式运行，不要求管理员权限、不阻止游戏启动。更新或验证完整性恢复原配置后，Mod 下次加载会重新准备，之后一次启动生效。
- **直接退订或禁用 Mod 不会自动恢复文件**，因为 Mod 已不能执行清理；应先在设置页关闭自动配置。若已经退订，可恢复该字段或验证游戏文件。该模式影响整个游戏及其他 Mod，也存在下方列出的 CPU、速度和决策取舍。
- 专用环境 profile 与无头测试默认不自动改配置，保留 A/B 测试隔离。仅在自有隔离实例验证时使用 `COMBATSOLVER_TEST_AUTO_GC_CONFIG=1` 穿过相同初始化路径；它不请求 GC 模式，实际切换仍由下一进程读取文件完成。

实现边界：这是下一次启动自动接入，不能让已经运行的 CLR 热切换。没有启动可见 Steam，因此完整工坊更新、Steam 界面、云存档及可见帧时间仍未验收；不将无头验证称为 Steam 端到端通过。首轮性能数字属于原五场样本，本次不重新声称新的提速幅度。

开发者复现：`dotnet run --project tools/RuntimeGcProfileChecks -c Release` 检查配置字段和三个真实 CLR 启动；Windows 用 `tools/test-runtime-gc-startup.ps1 -GameRoot <游戏目录> -RitsuWorkshopRoot <依赖目录> -Build <构建目录> -Output <新证据目录>` 验证隔离原生进程的三次启动，并自动清理私有实例。测试入口需要 PowerShell，玩家自动接入不需要。

### 自动接入验证

Linux 与 Windows 的 `RuntimeGcProfileChecks` 均通过 54 项检查，包括三个真实 CLR 进程的准备／激活／恢复、空环境优先级、GC 原值缺省／false／true、无关字段保留、幂等与无效／外部变更拒绝。两端最终行为源码 Release 构建无警告／错误，结构门禁与新增中英文键检查通过。

Windows 原生 CLR 9.0.7 三个有效请求分别验证：首次 Default／NoGC=true 并准备配置；下一次 Active／NoGC=false 并恢复原字段；恢复后 Default／NoGC=true。runId 和配置字段见[自动接入证据](server-gc-auto-startup-20260924.json)。正式游戏配置未改，所有私有实例已清理；最终本地 Mod 已部署。

中间失败保留：第一轮暴露空环境屏蔽自动配置，修正后通过；第二轮第三次启动遇到既有 `MainModule.FileName` 读取空值，启动器改用已有进程句柄查询 API，恢复验证在新隔离实例单独补跑，先确认已恢复 JSON 与原配置各字段相等。不把分段验收描述为一轮无失败的三连启动。原生测试通过修改隔离保存设置模拟关闭，未实测可见设置开关交互。

## 专用启动器（开发／显式覆盖）

先安装包含本改动的 Mod，退出已运行的游戏，再从仓库根目录执行。把下面的示例路径替换为实际游戏可执行文件路径。

Windows 使用 PowerShell 7.4 或更新版本，直接启动游戏 EXE：

```powershell
pwsh -NoProfile -File .\tools\start-server-gc.ps1 -GameExecutable "C:\Games\Slay the Spire 2\SlayTheSpire2.exe"
```

Linux：

```bash
bash tools/start-server-gc.sh "/path/to/Slay the Spire 2/SlayTheSpire2"
```

脚本可以在可执行文件路径后附加游戏参数。它们只给新游戏子进程设置 `DOTNET_gcServer=1`、兼容前缀 `COMPlus_gcServer=1` 和 `COMBATSOLVER_RUNTIME_PROFILE=server-generational`，不改全局环境、注册表或游戏 `runtimeconfig.json`，不结束已经运行的游戏。不要用向已运行的 Steam 发送 `-applaunch` 替代这里的直接启动：不能据此认定环境已传入游戏。

设置页“内存管理”显示本次配置状态。生效时 NoGC 开关及预算输入暂不可编辑，并说明保存的设置未改变。若未启用自动配置，退出游戏后恢复普通启动即可恢复原有行为；自动配置已写入时，应先在设置页关闭该选项，再正常重启。

ServerGC 是整个游戏进程共用的回收模式，也影响游戏和其他 Mod 的托管分配。它可能改变 CPU 总用量、暂停频率和内存占用，不能从搜索结束更快推导画面更流畅。本轮没有可见游戏帧时间证据。

## 激活与回退边界

`RuntimeGcProfile` 在首次读取时冻结环境请求（无环境请求时读取运行库 AppContext 标记）与 `GCSettings.IsServerGC`。只有识别到 `server-generational` 且实际 ServerGC 为真才激活；未请求、未知值或 CLR 未启用时均保留保存的 NoGC 选择。后两种失败状态会写明确警告，并在设置页说明配置未生效。

生效时，`SolverSettings.Capture()` 仅将不可变搜索快照的有效 NoGC 开关覆盖为 false，不改 `SolverSettings.Current`、设置迁移或保存流程。显式启动配置的本次优先级在界面可见；不能在界面勾选一个会被静默忽略的开关。GC 生命周期继续使用已有的关闭 NoGC 路径，没有修改回收算法或搜索的分配检查点。

启动日志 `RUNTIME_GC_PROFILE` 记录实际 CLR、ServerGC、请求值、解析状态、保存的 NoGC 和有效 NoGC。应以实际状态为准，不能只凭环境变量存在断言配置生效。

微软文档确认 Windows 与其他系统均支持这些运行库环境选项，GC 初始化时读取它们，修改已运行进程的环境不会切换该进程的 GC 模式；该配置属于进程级。[GC 运行时配置](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)。ServerGC 使用多个专用回收线程，收益和资源成本取决于工作负载；本实现不固定堆数或假设其必定等于逻辑处理器数。[Workstation 与 Server GC](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/workstation-server-gc)。

## 无人宿主与证据

现有 Windows 无人入口增加 `-RuntimeProfile server-generational`，Linux 对应 `--runtime-profile server-generational`；默认值为 `default`。默认模式清除子进程的 Mod profile 请求，并保留调用环境已有的 CLR GC 开关，不强制改成 WorkstationGC。其余建局、预算、隔离和清理参数沿用[无人测试说明](../HEADLESS_TESTING.md)。PowerShell 的测试并行度参数接受 1–16，与已有核心上限一致。

无人进程 marker 保存实际准备传入的三个环境值；请求改变或旧 marker 缺字段时，必须在原有进程所有权核验后重启自有实例，不能继续复用旧 GC 模式。不会因此认领或终止其他游戏进程。

只有请求指定 `EvidenceDirectory` 时，Writer 才额外输出 `search-result.json`，包含完整 actions、snapshot、result scope、boundary、有效 policy，以及实际 CLR／GC／profile 和保存／有效 NoGC。写入位于搜索计时结束后，仍属于整个测试进程的开销。`runtime-events.json` 通过日志 FIFO 快照屏障导出当前战斗事件，保留同一 generation 的 `SEARCH_WORKER_START`／`SEARCH_GC_LIFECYCLE` 时间戳，避免退出时丢失尚在缓冲区中的日志。单次搜索结果文件不等于整场部署证明；质量比较应连同请求、原有成员／工作量指标、药水使用和时间边界对账。

profile 生效后的有效 NoGC 为 false；若测试另显式要求 `EnableNoGcRegionForTest=true`，既有配置断言会报告冲突，不把该断言放宽为成功。

## 已有最小合同

```bash
dotnet run --project tools/RuntimeGcProfileChecks/RuntimeGcProfileChecks.csproj -c Release
bash tools/test-runtime-profile-launchers.sh
pwsh -NoProfile -File tools/test-runtime-profile-launchers.ps1
```

- 纯值检查 25 项通过：普通启动、保存关闭、成功激活、CLR 未启用、未知值及不可变选择隔离；不读取测试机器的真实 GC 模式来决定预期结果。
- 两个启动器合同在 Linux 通过，PowerShell 为 7.6.6；PowerShell 合同也已在用户的 Windows 上通过。临时控制台 fixture 验证空格路径、特殊／空参数、子环境隔离、退出码、既有进程拒绝；抽取实际无人入口的纯环境与已获所有权后的 marker 分支，验证相同／变化／缺字段三种复用决策，停止操作使用记录桩。
- 三条新增界面提示有中英映射，相关空白检查通过。这些工具合同不证明游戏中的 UI 实际排版或性能收益；原生宿主和设置文件检查见下文。

## 本轮原生宿主结果

本轮使用 Windows／i7-14700KF（28 个逻辑处理器）／32 GB 内存的实际 Godot 游戏宿主，CLR 为游戏内置 **9.0.7**。两种模式使用同一份 Release DLL，算法基线为上游 `3d45d78f`；没有混入旧研究分支改动，也不将旧 `b41e533d` 离线结果计作本次收益。显卡不参与搜索。

预先选定五角色、两个精英根和三个首领根，其中女王场景包含初始牌组加 32 张角色牌、2 张无色牌、十余件遗物（含沙漏）、三个药水槽。定义位于 `coverage/runtime-gc-profile/`。运行独立冷进程，按根交替 AB／BA；VeryHigh、300 秒／500,000 节点，DOP 分别为 8、16、8、16、8，普通完整协调器及原有药水策略保持一致，性能样本不开严格增量检查。每个场景仅测一个 DOP，本轮不评价同场景从 8 到 16 并行的扩展性。

A 为实际 WorkstationGC＋启用 NoGC（配置预算 16 GB，实际区域可按余量缩小）；B 为实际 ServerGC＋本次有效 NoGC 关闭，保存的 NoGC 仍为 true。每次均核对实际 CLR／模式、正常退出、设置文件字节不变、同 DLL／输入／并行度；严格比较器要求完整 actions、snapshot、policy、作用域、边界、生成开局／牌组／遗物／药水、总展开／转移／选择以及每个成员的身份、预算、工作与选择状态相等，才输出同工作量比率。时间边界或不一致会使比较器退出失败。下表仍保留所有预选场景的观测耗时；被拒绝的两对明确标注，不从总体中删除，也不改写为等价通过。

主指标是同一 generation 的实际 worker 起止窗口（包括 GC 准入及退出），而不是各搜索成员耗时之和。Windows 每 100 ms 采样后按该窗口截取 RSS 最大值及 CPU 累计值之差；这是搜索期间整个游戏进程的占用，并非独占搜索线程的成本。采样有调度开销，可能漏掉短暂尖峰和首尾小段；完整误差字段和每场指标随结构化报告提供。GC 最大暂停只称观测值；没有测量可见游戏帧率。

| 场景 | DOP | worker 秒 A → B | 耗时变化 | 搜索窗 RSS GB A → B | 投影战损 A → B | 严格对账 |
|---|---:|---:|---:|---:|---:|---|
| 君主·首领 | 8 | 39.025 → 23.824 | -39.0% | 9.32 → 1.90 | 40 → 40 | 全量相等 |
| 静默·精英 | 16 | 64.852 → 38.677 | -40.4% | 9.36 → 4.24 | 40 → 41 | 路线与工作不同 |
| 死灵·精英 | 8 | 16.679 → 21.719 | +30.2% | 9.22 → 2.60 | 35 → 35 | 路线相同、工作增加 |
| 故障·首领 | 16 | 9.084 → 9.648 | +6.2% | 6.81 → 1.69 | 74 → 74 | 全量相等 |
| 战士·女王 | 8 | 40.329 → 28.249 | -30.0% | 9.28 → 2.42 | 66 → 66 | 全量相等 |

五场总 worker 耗时 **169.969 → 122.117 秒，降低 28.2%**。按五根等权的耗时比几何平均，改善为 **18.8%**，未达到该口径的 20%。三个请求加快、两个变慢；不将总和与几何平均互换，也不声称普适 20%+。

搜索窗采样 RSS 峰值比的等权几何平均降低 **72.1%**（逐场峰值的算术平均降低 70.8%，不是并发总内存）。总进程 CPU 时间 **535.688 → 620.031 CPU 秒，增加 15.7%**；各根 CPU 比几何平均增加 19.2%。这是一项以更多 CPU 换取较低内存和部分长请求延迟的可选配置。

三对完整导出结果与工作均相等；死灵场景完整结果相等，但搜索转移从 222,446 增至 441,900，耗时增加 30.2%。静默场景双方都在第 4 回合获胜、均不用药，新配置多损失 **1 HP**。五对药水的 ID、槽位、使用回合与数量均相同；收益没有来自额外用药。因而本轮**不满足“所有场景决策不退化”**，也不能把两场额外工作解释成严格相同工作的加速。

原因边界：普通 GC 下现有 `SearchMemoryPressureSignal` 不施加 NoGC 区域剩余额度，部分原本 `MemoryHeadroomInsufficient` 的成员得以运行。静默的 1 HP 差异不能仅归因于初始选优；普通模式的一条能力前缀内还发生了缓存清理与 NoGC 余量不足回退，新模式没有该检查。它是后续调查线索，现有数据尚不足以证明最终差异的精确因果。本 PR 保留既有搜索机制，不按场景强制成员或波次。

每场的观测 GC 暂停、采样间隔／边缘遗漏、运行 ID、DLL 与完整动作／快照／策略的哈希、全部成员工作和被拒绝的比较字段见[结构化记录](server-gc-launch-profile-20260924.json)。只有每场一对数据，不提供统计显著性或其它工作负载保证。

### 复现

在 Windows 构建本分支，并将 manifest 放在输出目录（Windows 构建同时产出 MemoryCleaner）。每次指定一个未存在的输出目录：

```powershell
pwsh -NoProfile -File tools/PerformanceBenchmarks/run-windows.ps1 `
  -GameRoot "C:/Games/Slay the Spire 2" `
  -RitsuWorkshopRoot "C:/Steam/steamapps/workshop/content/2868840/3747602295" `
  -Build "C:/Build/CombatSolver" `
  -Scenario coverage/runtime-gc-profile/dev-08-regent-boss.json `
  -Output .local/gc-ab/regent-A -RuntimeProfile default -Dop 8
# 换为新输出 regent-B 和 -RuntimeProfile server-generational，保持其它参数相同。
python tools/PerformanceBenchmarks/compare-runtime-profile.py .local/gc-ab/regent-A .local/gc-ab/regent-B
```

测量脚本只创建和清理带所有权标记的私有实例；`MemoryReservationMiB` 默认 12288 是无人宿主的资源预约，不改变搜索／NoGC 预算或限制进程实际峰值。宿主预约曾因本机可用内存不足而排队失败，未启动搜索的尝试不计入矩阵。早期采样器保活／日志缓冲问题已通过正常退出与显式证据屏障解决，不将其不完整输出纳入数字。

### 正确性范围与限制

- 最终源码 Linux／Windows Release 均为 0 警告、0 错误；结构门禁 `search_files=208` 通过。
- 五根十个正式游戏进程均正常退出、测试 Passed；实际模式及有效／保存 NoGC 核验通过，十份私有设置文件的字节均未改变，实例完成后清理。
- 额外初始牌组普通战斗在 ServerGC 下通过严格增量回放：`strict-starter.json`，DOP1／3 秒／300 节点，run `70254312cac74e14b8b9201594cfe3f5`；不将这项时间计入性能矩阵。
- 比较器在真实样本的六种单字段变异中均拒绝错误 profile、错误 PID、TimeLimit、不同动作、不同快照和不同成员节点预算。原有两对不等价样本仍保留 `validPair=false`。
- 最终源码 Mod 已精确部署至本地 Linux 游戏目录；Windows 验证使用私有冻结游戏，未修改其安装目录／运行库配置，未启动可见 Steam，会话帧率和视觉布局未验收。

摸底时，Regent 随机根的严格增量验证在两种 GC 模式下都出现同一 `FURNACE` 回放能量／手牌差异；固定预算也复现。静态线索是能力前缀临时 seed 已含前缀状态，但动作链为空，严格完整回放可能遗漏前缀。本 PR 未改该算法，也未证明该路径严格正确；普通 A/B 结果相等不能替代这一失败的正确性验收。失败发生于同一搜索算法／profile 代码、最终证据导出完善前的宿主构建，单列记录以免将全部验证写成通过。

该配置为显式选择，普通启动沿用原行为；ServerGC 影响整个进程，在其它硬件、Mod 栈、内存压力和可见渲染负载下仍可能有不同收益。这里不承诺所有场景逐项提升 20%，也不将多用药或少搜索计作加速。
