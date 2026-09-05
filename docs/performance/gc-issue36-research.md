# Issue #36：GC 研究工作区与首轮基线

日期：2026-09-05。分支：`perf/gc-research`。上游固定基线：`5c4b69d91bcc049781e9de6ddf043799a82c849b`，版本元数据 `0.30.0`。

本文件保留实现之前的首轮 pilot。之后已按用户授权实施 GC 与存储候选，并进行固定工作量 A/B；当前实现、最终取舍与验证见 [实现及实验结论](gc-issue36-implementation.md)。下文的“尚未修改”与待办均描述首轮研究时点，不代表后续状态。

需求参考 [issue #36](https://github.com/Torch1230/CombatSolver/issues/36)；当前源码逐项定位、已完成机制、P1 对象图路径及风险见 [静态审计](gc-issue36-code-audit.md)。Issue 的历史用时、分配及收益不能作为当前上游基线。

## 隔离与协作

- worktree 名称为 `CombatSolver-gc-research`，与原仓库、小循环研究目录并列。所有本任务命令在此目录运行，不切换其他 worktree 分支、不搬入未提交改动。
- 本地 `local.props` 设置 `CopyModOnBuild=false`；构建命令也显式传同名属性，防止覆盖其他任务或可见游戏加载的 DLL。
- `.local/gc-research/game` 使用独立可执行文件和独立 `mods/CombatSolver`；只读游戏资源、运行库链接到本机游戏安装。RitsuLib 的临时 headless 投影也在这个独立 mods 目录。
- `.local/gc-research/runtime-silent`、`runtime-necro` 分别保存 XDG 数据、请求、结果、日志和进程 marker。沿用现有 launcher 对其他游戏进程的拒绝机制；冲突时等待空闲，不终止其他任务的进程。
- worktree 隔离不能隔离 CPU、内存带宽和系统 GC 压力。正式 A/B 必须在机器空闲时串行执行；本轮只是单次冷进程 pilot。
- 小循环、评分、保路、转置规则和路线质量优化属于另一任务。GC 实验保持其固定基线，合并策略改动后重新建立受影响的性能基线。

## 本轮直接证据

环境：Linux `7.1.11-arch1-1` x86_64，AMD Ryzen 7 7840H，16 逻辑处理器，.NET SDK `9.0.120`，塔 2 `0.111.0`。Release 构建成功，0 警告、0 错误；未部署到可见 Steam 游戏。

两个样本都使用 DOP1、Custom、每个 solver 最多 250 节点、30 秒搜索兜底、仅 Short、120 秒请求超时，在首个结果断言后退出。均达到 `NodeLimit`，未触发 Deep。这是诊断工作量，不是完整预设或整场质量验收；请求可能包含多个 solver，250 不是请求总展开上限。

| 指标 | Silent 大牌组，普通 GC | Necrobinder 药水，Smart / NoGC 4 GB |
| --- | ---: | ---: |
| scenarioId | `GC36-SILENT-250-GC` | `GC36-NECRO-250-SMART-NOGC4` |
| runId | `2f721baf127c41aaa0646dc50e46e754` | `129de2c8d3ea47649c61c5b6eb865566` |
| 请求状态 | Passed | Passed |
| selected 展开 / 转移 / 选牌 | 250 / 1,426 / 737 | 250 / 2,494 / 1,515 |
| 请求累计展开 / 转移 / 选牌 | 500 / 2,510 / 1,251 | 750 / 7,540 / 4,646 |
| selected worker 分配 B | 147,266,464 | 128,231,688 |
| 请求累计 worker 分配 B | 265,265,168 | 432,608,568 |
| 累计 worker B / 累计转移 | 105,683 | 57,375 |
| 请求累计搜索时间 ms | 3,743.424 | 5,211.553 |
| 请求累计 GC pause ms | 138.993 | 102.383 |
| 各代计数 delta，Gen0 / 1 / 2 | 33 / 14 / 1 | 4 / 4 / 4 |
| 结果采样 working set B | 1,534,021,632 | 1,688,358,912 |
| selected 玩家 HP / 敌人 HP / 预计战损 | 27 / 512 / 38 | 32 / 526 / 9 |
| selected 战斗结束回合 | 无，未完成路线 | 无，未完成路线 |

表中两个样本输入和 GC 配置均不同，不构成开关 A/B，也不能用它们之间的差值推导收益。每个配置只有一次样本；冷启动/JIT、系统背景负载和合成根限制仍在。请求累计搜索时间不含全部建局和编排；worker 分配不等于进程总分配；working set 是结果时刻快照，未采集峰值。未做 trace、完整动作等价、Windows 构建或可见 Steam 帧时间验证。headless 的帧间隔不作为玩家卡顿证据。

### Smart 层间回收已在当前版本复现

NoGC 实际建立预算为 `4,000,000,000 B`（十进制 4 GB），搜索分配限额为 `2,666,666,664 B`。原始日志的两个 `POTION_GRADIENT_MEMORY_RESET`：

| 层边界 | 回收前分配 B | 限额 B | 边界 GC pause ms | 边界墙钟 ms |
| --- | ---: | ---: | ---: | ---: |
| 0 → 1 | 130,198,088 | 2,666,666,664 | 52.8 | 55.8 |
| 1 → 2 | 146,835,064 | 2,666,666,664 | 49.6 | 51.9 |

两次边界累计暂停约 `102.4 ms`；所选 solver 自身记录 `gc_pause_ms=0`。因此只看 selected 指标会漏掉层间开销。结果 JSON 的 `noGcRegionRolloverCount=0`，也不能用它表示没有重启：它统计的是另一种跨搜索 rollover。

上述数据证明“预算尚未用尽也会回收”，没有证明取消回收一定更快或不会增加峰值。边界分配来自进程口径，请求 worker 分配来自搜索线程口径，不能相减作为精确余量。应在独立改动中测量跳过边界后是否发生额外 checkpoint、区域意外退出或峰值回退。

### 初步分配定位

所选 Silent solver 的 `fork` 阶段为 `60,146,768 B`，约占该 solver worker 分配的 40.8%；`action` 为 `34,859,432 B`。所选 Necrobinder solver 的 `fork` 为 `32,649,600 B`，`action` 为 `49,857,792 B`。阶段会嵌套，不把这些字段相加当作总分配。

这支持继续给 Fork 建立按类型的分配账本。静态审计还发现 `CardPlayStarted/Finished` 历史事件可能经卡牌 observer 保留祖先分支，以及 inner 微批仍把完整候选累积到 aggregate；两者尚需 heap/分配 trace 验证实际 retained bytes，不能凭静态路径给收益估值。

## 重现设置与入口

两个输入复用 [已有公开性能 fixture](../PERFORMANCE_FIXTURES.md)，没有导入玩家私人问题包。固定设置保存在 [gc-issue36-pilot-settings.json](../../coverage/unattended/gc-issue36-pilot-settings.json)。本轮本地完整命令保存在 `.local/gc-research/run-pilot.sh`，原始产物留在 Git 忽略目录。

先完成隔离构建和游戏目录准备：

```bash
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
```

将该构建的 `.godot/mono/temp/bin/Release/CombatSolver.dll`、根目录 `CombatSolver.json` 和 `THIRD_PARTY_NOTICES.md` 复制到研究游戏目录的 `mods/CombatSolver`。游戏可执行文件必须有自己的路径和 mods 目录，不把研究目录的 mods 指向正常安装。只读资源可链接；本机游戏或 Mod 依赖更新后需要另记新基线。

从 worktree 根运行以下命令，假设隔离游戏已放在 `.local/gc-research/game`；现有 launcher 会为两个 runtime 初始化各自的游戏配置，固定搜索设置提前写入各自数据目录：

```bash
pilot_root="$PWD/.local/gc-research"
for case_name in silent necro; do
  mkdir -p "$pilot_root/runtime-$case_name/data/SlayTheSpire2"
  cp coverage/unattended/gc-issue36-pilot-settings.json \
    "$pilot_root/runtime-$case_name/data/SlayTheSpire2/combat_solver_settings.json"
done
common=(--sts2-game-root "$pilot_root/game"
  --search-max-degree-of-parallelism-for-test 1
  --performance-preset-for-test Custom --force-short-search-only
  --measure-search-phases --enable-detailed-diagnostic-logs-for-test 0
  --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0
  --expected-initial-executable-action-count-at-least 1
  --stop-after-initial-solver-result-assertion --timeout-seconds 120 --exit-on-complete)

COMBATSOLVER_HEADLESS_ROOT="$pilot_root/runtime-silent" \
  ./tools/run-unattended-test.sh "${common[@]}" \
  --scenario-id GC36-SILENT-250-GC --character-id SILENT \
  --seed SEARCH_PERF_SILENT_LARGE_DECK --encounter-id AEONGLASS_BOSS \
  --ascension 5 --act-index-for-test 2 --enemy-current-hp 512 \
  --initial-enemy-move-ids-json '["EBB_MOVE"]' \
  --initial-player-hp 65 --initial-player-max-hp 65 --initial-player-energy 3 \
  --clear-player-piles \
  --cards-path coverage/unattended/search-performance-silent-large-deck-cards.json \
  --potion-policy-for-test Disabled --enable-no-gc-region-for-test 0

COMBATSOLVER_HEADLESS_ROOT="$pilot_root/runtime-necro" \
  ./tools/run-unattended-test.sh "${common[@]}" \
  --scenario-id GC36-NECRO-250-SMART-NOGC4 --character-id NECROBINDER \
  --seed SEARCH_PERF_NECROBINDER_POTION \
  --run-snapshot-path coverage/unattended/search-performance-necrobinder-potion-heavy-run-snapshot.json \
  --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2 \
  --enemy-current-hp 526 --initial-player-hp 41 --cards-json '[]' \
  --potion-policy-for-test Smart --enable-no-gc-region-for-test 1 \
  --no-gc-region-budget-gigabytes-for-test 4
```

正式复测使用新命名的输出目录保存各次结果，不覆盖历史证据。Windows 使用仓库已有 `.ps1` 入口及等价 PascalCase 参数，并单独隔离游戏与用户数据；本轮没有执行 Windows 命令，不能将 Linux 成功视为 Windows 通过。

## 后续实验的边界

1. 补齐 forced collect、NoGC 生命周期、每波分配/余量、raw aggregate 高水位；区分累计暂停、observed max 和 trace max。
2. 外层 parent wave 先按余量缩容，仅一个 parent 也放不下才回收；独立验证输入顺序、转移数和非时序剪枝不变。
3. Smart 层预测性回收单独 A/B；当前 250 节点 pilot 可作快速触发点，再补压力更高的代表样本，记录预测误差和实际峰值。
4. P1 优先用 heap 路径量化历史保留和 raw aggregate，再选择存储改动；P2 内核方案暂不定型。

正式性能结论需同源码、同根、同 seed、固定工作量、同配置进行至少三次独立复测，报告中位数，并在多个代表场景中检查路线、战损、回合、暂停与内存回退。保持纯存储/GC 改动的确定性要求；不通过改变搜索质量取得性能数字。

## 本地产物

- `.local/gc-research/build.log`：本轮 Release 构建。
- `.local/gc-research/run-pilot.sh`、`settings.json`：本轮执行命令和设置。
- `.local/gc-research/silent-run.log`、`necro-run.log`：launcher 输出。
- `.local/gc-research/runtime-*/godot-headless.log`：包含 RESULT、SEARCH_PHASE、层间回收和路线的原始游戏日志。
- `.local/gc-research/runtime-*/data/SlayTheSpire2/combat_solver_test_result.json`：结构化 Passed 结果。
- `.local/gc-research/pilot-summary.json`：由上述结果提取的本地研究摘要与机器信息。

这些原始日志、游戏二进制、Profiler 与本地路径均不进入源码提交。
