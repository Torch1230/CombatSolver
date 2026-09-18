# T1 泛化验证报告：三新包已知路线在 depth-3 截断（R1，2026-09-18 凌晨）

> 状态：**探索产出，待用户 2026-09-18 早间确认**。分支 `explore/r1-20260918`，起点 `da62d23`。
> 结论：三个 0.40.2 实机报告包的「记录路线」在全新搜索中均在 depth-3（playDepth=3）保留边界被截断；其中 WF 案例为 win-vs-loss 级 materiality（记录线获胜，搜索判 onlyDeath）。按预注册规则 ≥2/3 同源 → **GO 跨层机制**。

## 1. 方法（可复现）

1. 从每个报告包内 `replay/current/recording/events.jsonl` 还原全程行动（`PlayCardAction`/`UsePotionAction`/`EndPlayerTurnAction`，含选牌 `ChoiceContext` 与目标），生成 `KnownRouteTraceConfig`（`.local/regression/inputs/{wf,ts,qn}-recording*.json`）。
2. 以包原策略从开战根（`-CheckpointSelector <session>:0` 或 `:1`，cursor=0 无需原生事件回放）运行：
   `tools/run-unattended-test.ps1 -ReplayMode SearchOnly -ScenarioId KNOWN-CONFIG-ROUTE-TRACE-V0111 -KnownRouteTraceConfigPath <config> -CheckpointArchivePath <zip> -CombatSolverBuildDir .local/regression/build-r1 ...`
3. 读取 mod 日志 `Roaming/SlayTheSpire2/logs/CombatSolver/<pid>/combat-*.jsonl` 中的 `KNOWN_CONFIG_PREFIX`（每步冻结前缀状态）与 `PATH_TRACE_SUMMARY`（每步在搜索中的 `Generated/Expanded/RetentionPoolInput/GlobalRetention/PruneFinal` 阶段与 `StateOnly` 计数）。
4. 观察所有候选搜索（beam 90/135/203/270 + Smart 用药梯度层）中，记录线第 k 步是否被生成/展开/进入外层保留池。

## 2. 结果

| 包 | 遭遇 / 角色 | 路线 | 记录线成绩 | 全新搜索成绩 | 最深生成 | 最深展开 | step3 保留池（真实排名） |
| --- | --- | --- | --- | --- | --- | --- | --- |
| WF `1a3f84ff` | WATERFALL_GIANT / 铁甲 | 38 动作 / 8 回合 / 2 药 | **获胜，战损 22**（完整模拟回放通过） | `onlyDeathRoutes=True`，预计战损 48，玩家 0 HP | step4（admitted 后未展开），step5+ 从未 | step3（宽 beam 到 step4） | 池 392，**RawRank 42，SelectedIndex 42（过 depth-3）** |
| TS `840ca923` | TEST_SUBJECT / 铁甲 | 8 动作 / 1 回合 / 2 药 | 战损 32（-1） | 战损 33，T8 胜 | step2（第二瓶药，admitted 后未展开；同键兄弟 `rejected_dominated`），step3+ 从未 | step1 | 2 药梯度层 `route_missing` |
| QN `01f1707e` | QUEEN / 机械师 | 全长 116 动作（重放受阻）；前缀 42 动作 | 无 BetterWorldline 声明（SearchResultStale） | （前缀评估） | step5（admitted 后未展开），step6+ 从未 | step4（宽 beam） | 池 249，**RawRank 52，SelectedIndex 52（过 depth-3）** |

**停点定量结论**：三个包都不是「step3 被单层排名直接丢掉」——WF/QN 的 step3 实际通过 depth-3（RawRank 42/392、52/249 并被选中），真正的截断发生在**更深边界**：前缀状态 admitted 后拿不到展开（WF step4、TS step2、QN step4-5），更深的动作动作根本不生成。这与「前缀无法跨边界存续到兑现」一致，支持跨层机制（推荐线后验重排/深度租约），而不是在单层增加席位。

- WF 是决定性的：同一构建里，记录的完整路线在严格模拟回放中获胜（敌方 0、玩家 26 HP、`RootUnchanged/LiveUnchanged` 全过），而全新搜索（约 12.7 万展开）只找到必败线。**搜索漏掉了一条可胜线。**
- TS/QN 的 step3 及更深前缀在搜索中不存在；QN 的 step3 状态确实到达外层保留池但没被选中——与原始 T1 签名（step3 入池被层间排名截断）同型。
- QN 全长重放失败于同名牌副本解析歧义（`DEFEND_DEFECT#0`、`GO_FOR_THE_EYES#1` 双副本），前缀评估已足以定位截断。
- 用药层补充观测（TS）：`SMART_POTION_GRADIENT layer=1/2 route_missing=true` 并非「用药不可用」——同一根的 layer3 找到了 3 药获胜路线（战损 29）。恰好 1/2 药的候选无法通过终局政策门，与「记录线（恰好 2 药、战损 32）被截断」一致；不能把 layer1/2 缺失简单归因于药水策略禁用。

## 3. 包侧恢复兼容性（与求解器质量无关，但影响复现）

0.40.2 包的原生事件回放恢复存在两个失败模式（同一 DLL 在 0.39 包上可用）：
- `UsePotionAction.ToString()` → `GetPotionAtSlotIndex(2)`：玩家仅 2 槽时记录事件引用 slot 2/3（WF、QN 到 cursor>0 时必现）；
- `CombatReplayEvent.Deserialize_Patch1` → `Received net action of type 11 that does not map to any type`（TS）。
绕行：只从 cursor=0 的根（`:0`/`:1`）起跑，不做原生回放；记录路线从包内 recording 还原（而非 replay-state 直恢复）。

## 4. 复现命令（WSL，一次一例）

```bash
export PATH=/root/.dotnet:$PATH
# 构建（HEAD）
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false \
  -p:SteamRoot=/mnt/d/Steam \
  -p:Sts2DataDir='/mnt/d/Steam/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64'
cp .godot/mono/temp/bin/Release/CombatSolver.dll .local/regression/build-r1/
cp CombatSolver.json .local/regression/build-r1/ && cp .local/regression/build-cur/CombatSolver.MemoryCleaner.exe .local/regression/build-r1/
```

```powershell
$env:COMBATSOLVER_HEADLESS_ROOT='D:\CombatSolver-headless\r1trc-wf2'
& 'D:\0_git\CombatSolver\tools\run-unattended-test.ps1' `
  -CheckpointArchivePath 'C:\Users\Admin\Desktop\CombatSolver-BugReports\CombatSolver-0.40.2-WATERFALL_GIANT_BOSS-1a3f84ff88a74a84bafe100bd605b563.zip' `
  -CheckpointSelector '3cec50689a8e4636867009f8543180c6:0' -ReplayMode SearchOnly `
  -ScenarioId 'KNOWN-CONFIG-ROUTE-TRACE-V0111' `
  -KnownRouteTraceConfigPath 'D:\0_git\CombatSolver\.local\regression\inputs\wf-recording.json' `
  -HeadlessInstance r1trc-wf2 -Sts2GameRoot 'D:\Steam\steamapps\common\Slay the Spire 2' `
  -CombatSolverBuildDir 'D:\0_git\CombatSolver\.local\regression\build-r1' `
  -EvidenceDirectory 'D:\0_git\CombatSolver\.local\checkpoint-batch\r1-trc-wf2' `
  -ExitOnComplete -TimeoutSeconds 300
```

证据目录：`.local/checkpoint-batch/r1-trc-wf2` / `r1-trc-ts` / `r1-trc-qn5`（日志在无头实例 `Roaming/.../logs/CombatSolver/`）。

## 5. 影响与建议

- 截断不是「单层席位不够」，而是**每层剪枝都会重新按短期排名裁决**，深前缀在任一深度都可能被丢掉；提高 beam 宽度（203/270）仅多保 1 步，未改变结论。
- 建议方向（详见 `.local/T1-CROSS-LAYER-DESIGN-20260918.md`）：
  1. **推荐线后验重排**：剪枝时收集有界「深前缀见证」，主搜索后重放延续并按真实终局重排；
  2. **深度前缀租约**：让同一 lineage 的深前缀跨剪枝存续，边界数/席位数有界。
- 在方向落地前，建议先把 WF 案例作为公开可复现反例（win-vs-loss）加入 PR/issue 讨论，以便与作者对齐修复边界。
