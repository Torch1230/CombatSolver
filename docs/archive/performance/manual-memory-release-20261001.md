# 手动释放后自动执行内存回涨（2026-10-01）

## 请求与来源

用户原始描述：世界线计算完成后手动释放到较低内存，再点击自动运行会把内存重新加载，可能达到100%并卡死；储君生成流尤其严重。没有提供该卡死现场的战斗包。本轮先审计并合并 [PR #147](https://github.com/Torch1230/CombatSolver/pull/147)，再基于最终合并源码排查。

PR固定head `d5100d1c8b79cb82259ca9f151f774c802f5fc01`，合并 `c6e60681`。源码核对单次构造的令牌引用复用、费用修改顺序/有效期、普通准入与循环/有序变更路径。Windows Release零警告/错误，结构检查 `search_files=238`，原生费用11组、组合1200组/5069条通过。runId分别为 `839136cb2faf4485bd82cf2f591ef910`、`1bdb389c145747a0a36e272b49562876`；两请求复用同一隔离实例，最后已清理。贡献者的广泛搜索和整场矩阵是既有证据，本轮没有重跑这些矩阵或声称整搜提速。

## 确认的原因

旧手动流程执行Forced压缩GC，再调用当前游戏的 `EmptyWorkingSet`；管理员辅助程序接着执行全系统 `MemoryEmptyWorkingSets` 和待机列表清理。活跃页面被移出物理内存，并未失去所有权，执行/GC访问时会读回。Microsoft [工作集说明](https://learn.microsoft.com/windows/win32/memory/working-set)明确说明此行为。ServerGC也可能保留空闲的提交空间；[Aggressive模式](https://learn.microsoft.com/dotnet/api/system.gccollectionmode?view=net-9.0)才明确请求尽量归还空闲提交内存。

本机已有日志（2026-09-29，进程59780，手动释放事件时间戳1790696308908）记录：工作集 `2919890944→98062336` 字节，私有内存 `3822051328→3777961984`，托管live `567047096→319360808`。它证明低物理读数与仍被占有的大块内存可同时出现；这份日志没有记录用户本次100%卡死，不能称为该现场复现。

## 修复

- 手动回收使用完整、阻塞、压缩的 `GCCollectionMode.Aggressive` 归还空闲堆页面；GC生命周期计数仍记录这一次收集。
- 删除游戏进程回收中的单独工作集修剪。首轮将辅助程序缩减为只清理待机缓存；用户明确要求加回系统和其他进程清理后，恢复 `MemoryEmptyWorkingSets→MemoryPurgeStandbyList` 及工作集失败退出码20。先归还游戏空闲堆页，再通过原系统入口清理所有进程工作集与待机列表。
- 沿用原活动搜索退出、回收完成、重复请求与错误收尾所有权；自动回收仍沿用原后台非压缩模式。
- 中英文按钮提示恢复完整系统工作集和待机列表清理说明。日志区分 `blocking_aggressive_decommit` 与后台回收，并增加 `managed_committed_after`。
- 计算完成路线继续由原会话持有；全自动沿用当前路线，无需恢复搜索对象或重开搜索。

## 真实CLR合同

`tools/testing/checks/CombatSolver.GcPolicyChecks/GcManualMemoryReleaseChecks.cs` 直接链接生产GC策略。固定1 GiB NoGC预留，200 MiB短命搜索缓冲与64 MiB活数据，手动释放后重新访问活数据。NoGC用于明确建立空闲提交空间，不把这份数字当作游戏ServerGC搜索的峰值。

字节数为各自单次进程的实际采样；工作集不是堆大小，private不是live。修复前后各模式独立进程，时间未用来作性能结论。

| 模式 | 版本 | 释放前private | 释放后private | 释放前工作集 | 释放后工作集 | 访问活数据后工作集 |
|---|---|---:|---:|---:|---:|---:|
| 普通GC | 旧 | 1205862400 | 281784320 | 299347968 | 11091968 | 78290944 |
| ServerGC | 旧 | 1214337024 | 1199464448 | 300232704 | 11354112 | 78528512 |
| 普通GC | 最终 | 1205559296 | 76849152 | 298516480 | 92352512 | 92352512 |
| ServerGC | 最终 | 1214345216 | 84799488 | 300220416 | 94908416 | 94908416 |

旧实现明确在活页面修剪断言失败。最终同时断言：短命缓冲不可达、Aggressive完成、NoGC已退出、private至少归还512 MiB、活数据再次访问的回涨小于32 MiB。最终托管提交为67215360/67219456字节。日志故障的 `diagnostic-failure` 8项通过。最终Mod Release零警告/错误，Windows结构检查通过；PowerShell启动同时输出一条 `Import-Clixml: Root element is missing.`，门禁本身正常报告完成并返回0，未调查用户shell启动缓存。

```powershell
dotnet run --project tools\testing\checks\CombatSolver.GcPolicyChecks\CombatSolver.GcPolicyChecks.csproj -c Release -- manual-release
$env:DOTNET_gcServer = '1'
dotnet .local\tool-build\CombatSolver.GcPolicyChecks\bin\Release\net9.0\CombatSolver.GcPolicyChecks.dll manual-release
dotnet run --project tools\testing\checks\CombatSolver.GcPolicyChecks\CombatSolver.GcPolicyChecks.csproj -c Release -- diagnostic-failure
```

## 原生释放后自动部署

runId `00f77f0bead44df99cd3de3e80093ce0`，Windows自有ServerGC隔离实例，Runtime事件实际记录 `server_gc=True`。储君手牌3张类星体，抽牌堆3张打击；选中“类星体生成亮剑→亮剑击杀”2步路线，5节点/33转移/20选择分支，DOP2实际最大并发2。完成搜索后调用生产手动进程回收，核对保留结果引用及完整live文本不变，再经正式全自动入口执行。

结果Passed：`ManualMemoryReleaseAuto:retainedPlan:liveUnchanged:searches=1:noNewSearch`、`UnexpectedReplans:0`，原生75/75 HP、第1回合胜利。请求总19.06秒，Instant/0秒，启动器已删除整个实例。证据在忽略目录 `.local/manual-memory-release/native/`。

```powershell
pwsh -NoProfile -File tools\testing\run-unattended-test.ps1 -ScenarioId MANUAL-MEMORY-RELEASE-AUTO-CONTRACT -CharacterId REGENT -Seed RELEASEQUASAR20261001 -EnemyCurrentHp 1 -InitialPlayerEnergy 8 -InitialPlayerStars 10 -ClearPlayerPiles -CardsPath coverage\unattended\manual-memory-release-regent-cards.json -PerformancePresetForTest High -SearchBeamWidthForTest 16 -SearchMaxExpandedNodesForTest 1000 -SearchMaxDegreeOfParallelismForTest 2 -FixedSearchBudget -SearchBudgetOverrideMilliseconds 10000 -ExpectedInitialFirstActionCardId QUASAR -ExpectedInitialExecutableActionCountAtLeast 2 -ExpectedInitialProjectedBattleHpLost 0 -ExpectedUnexpectedReplansAtMost 0 -ExpectedFinishedTurnAtMost 2 -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -HeadlessFastModeForTest Instant -RuntimeProfile server-generational -HeadlessInstance memory-release-auto-20261001 -EvidenceDirectory .local\manual-memory-release\native -TimeoutSeconds 120 -CleanupInstanceOnExit
```

## 验证边界

已修复并验证游戏进程回收阶段的空闲堆提交归还；用户要求恢复的全系统工作集清理仍会让活跃页面在访问时回载。本次恢复只修改辅助程序、提示和文档，复用前述未改变的游戏GC/部署合同；Release构建及原调用顺序静态核对完成，管理员系统清理未实测。未复现可见Steam会话的100%内存卡死，未测完整重型生成流的系统峰值或帧时间，Linux未执行。原生夹具是最小进程回收与部署边界，没有执行UAC辅助程序，不能称为完整系统清理或全部高分支内存问题验收。本次排查修复保留下一版本开发记录，未提升版本、发包或上传渠道。
