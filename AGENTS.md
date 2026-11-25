# CombatSolver 工程与协作指南

CombatSolver 是《杀戮尖塔 2》（Slay the Spire 2）战斗最优解辅助 Mod，当前基于 `1.0.x` 架构：**HotKernel v2**（纯值低分配战斗模拟内核）+ **HotSearch**（确定性并行束搜索）+ **Runtime / UI**（主线程根捕获、部署交接与只读叠层渲染）。

## 1. 核心工程原则

- **正确性优先**：数值、结算顺序、RNG 与原版保持一致；对尚未支持的原版/第三方机制显式报错，不用默认估值默默兜底。
- **问题与数据驱动**：从用户目标、真实复现包或可量化的基准偏差切入；不做漫无目的的全库静态泛审，也不靠堆砌繁文缛节代替有效验证。
- **按风险选择验证**：根据改动影响范围选取最小充分验证（改哪层、验哪层），并用实际跑出的结果说话。

## 2. Agent 协作与汇报规范

### 并行开发与资源隔离

- **互斥文件分工**：多代理或 Swarm 并行开发时，按互斥文件清单切分所有权，每个 Worker 明确负责范围与验收目标，互不踩踏同一文件。
- **串行构建、隔离验证**：并行改代码期间不在子代理内执行 `dotnet build` 或启动 `OfflineSearchHarness`；由主协调器统一回收改动并串行构建一次后，再按需并发运行只读测试。
- **离线宿主并发上限**：多个 Agent 并发跑 `OfflineSearchHarness` 时，单个进程限制 `--dop 4`，输出目录隔离在 `.local/tool-tasks/<task>/`。任务收尾时及时停止子代理并清理 `.local/tmp/`、`.local/tool-tasks/` 等临时目录。

### 实机进程与环境安全

- 不强杀正在运行的 `SlayTheSpire2.exe` 进程。
- 执行本地 Mod 部署前先检查游戏目录下的目标 DLL 是否被占用；若被锁定，跳过复制并如实告知用户待关闭游戏后重新部署。

### 进度更新与收尾汇报

- **过程中同步**：多步任务在开工、取得关键发现、遇到阻塞/需拍板时用 1–2 句中文简要同步，不刷屏流水账。
- **结果汇报**：简明交代改了哪些模块、运行了哪些验证与关键实测指标（如耗时、战损、通过率、差分次数）、未覆盖部分及建议后续动作；严格区分本轮实测数据与历史基线。

## 3. 任务路由（Skills）

| 场景 | 说明文档 |
| --- | --- |
| 玩家问题包 / 战斗日志排查 | [issue-bundle-triage](.agents/skills/issue-bundle-triage/SKILL.md) |
| 卡牌 / 遗物 / 药水 / Power / 怪物 / RNG 语义修改 | [combat-semantic-change](.agents/skills/combat-semantic-change/SKILL.md) |
| HotSearch 搜索质量、评分、剪枝与性能优化 | [search-performance-optimization](.agents/skills/search-performance-optimization/SKILL.md) |
| HotKernel / HotSearch / Runtime / UI / Testing 边界重构 | [architecture-boundary-refactor](.agents/skills/architecture-boundary-refactor/SKILL.md) |
| 玩家击败语料与局外成长/药水策略迭代 | [strategy-replay-iteration](.agents/skills/strategy-replay-iteration/SKILL.md) |
| UI 文本、设置面板与中英双语本地化 | [ui-localization](.agents/skills/ui-localization/SKILL.md) |
| 准备发版、本地出包、打标与多渠道发布 | [release-gate](.agents/skills/release-gate/SKILL.md) |
| GitHub 社区任务筛选、归档与出题 | [combatsolver-community-tasks](.agents/skills/combatsolver-community-tasks/SKILL.md) |

## 4. 架构与核心不变量

完整职责地图见 [ARCHITECTURE.md](docs/ARCHITECTURE.md)，其余文档入口见 [docs/README.md](docs/README.md)。游戏反编译只读源码位于 `.local/decompiled/`，核对原版行为时以该目录为准。

- **HotKernel（`src/HotKernel/`）**：纯值紧凑状态（`CombatHotState` + `HotColdSidecar`），热路径避免托管分配；仅 `HotKernel/Import/` 允许读取 `MegaCrit.Sts2.Core.Models`，`State/` 与 `Sim/` 零模型依赖。若新增跨回合或持久状态字段，需同步维护 `HotStatePacker` 往返与 `HotContinuationStamp`。
- **HotSearch（`src/HotSearch/`、`src/Search/`）**：保证 `DOP=1` 与 `DOP=N` 严格确定性；禁止引用 `SolverSettings.Current`、`Entry.Logger`、`SolverController`、`SolverOverlay`、`UnattendedTestRunner` 及游戏模型类；`HotKernel` 与 `HotSearch` 内不使用宽泛 `catch (Exception)` 吞错。
- **Runtime 与 UI（`src/Runtime/`、`src/UI/`）**：主线程捕获稳定根并冻结策略为 `SearchPolicySnapshot`；`SolverOverlaySnapshot` 为 UI 渲染的唯一只读投影边界；界面新增或修改文案需同步中英双语（`SolverText` 与 `English.json`）。

## 5. 常用构建与验证工具（按需选用）

```powershell
# 编译 Mod（不自动部署）与离线搜索宿主
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
dotnet build tools\search\OfflineSearchHarness\OfflineSearchHarness.csproj -c Release
```

常用工具入口（完整离线命令行见 [OFFLINE_SEARCH_HARNESS.md](docs/OFFLINE_SEARCH_HARNESS.md)，无头实机见 [HEADLESS_TESTING.md](docs/HEADLESS_TESTING.md)）：

- **结构布局与覆盖审计**：`OfflineSearchHarness.exe --check-hot-layout`、`--audit-hot-coverage`
- **内核确定性与策略自检**：`OfflineSearchHarness.exe --verify-hot-parity --dop 8`、`--verify-strategy-policies`
- **单包复现与锁定差分**：`OfflineSearchHarness.exe --replay-fuzz <repro.json>`、`--replay-bundle <bundle.zip>`
- **规模回归与 A/B 对比**：`--fuzz-combat`、`--soak-deploy`、`--bench-corpora` / `--bench-choices` / `--bench-player-corpus` + `--compare-outcomes`
- **完整 Godot / UI / Mod 生命周期测试**：`pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 ... -CleanupInstanceOnExit`（Linux/macOS 用 `.sh`）
- **独立单元测试与架构/文档抽检**：`python -B tools\testing\run-independent-checks.py`、`pwsh -NoProfile -File tools\inspection\verify-refactor-boundaries.ps1`、`python -B tools\inspection\verify-documentation.py`

## 6. 本地部署与发布边界

- **本地部署**：先检查游戏 Mod 目录（如 `<GameDir>\mods\CombatSolver`）DLL 是否未被游戏占用，再将构建产物（`CombatSolver.dll`、`CombatSolver.pck`、`mod_manifest.json` 及本地化文本）复制到目标目录。
- **发版与归档**：日常开发与发布流程见 [release-gate](.agents/skills/release-gate/SKILL.md)。严格区分 **准备发版**（改版本号、更新日志与归档、验证、打 `dist/` 最小 4 文件包并提交 `main`）与 **发版**（打标签并推送创意工坊 / GitHub / 夸克等外部渠道），绝不在用户未指令时擅自发布；同时尊重用户当次显式跳过的发布渠道。`docs/DEVELOPMENT_NOTES.md` 与 `docs/TEST_MATRIX.md` 保持精简（200 行以内），发布定稿后及时归档至 `docs/archive/`。
