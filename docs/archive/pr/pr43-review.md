# PR #43 审查与集成

审查基准：PR `138f6ad9df4f26d30924df30cae91d280a9e9d84`；Seed Oracle 当前 main `29cee8752e599c7f97d9c5b6d40e3fe428b128df`（0.1.18）。结论：修正以下问题后接受，保留 API v5；不以作者回复作为合并前提。

## 发现与处理

1. **P1，Mod 硬链接不能隔离写入。** 原版 `PreCombatForecastWorker.cs:838` 的 LinkFile 被用于 Mod 文件，原地更新和 worker 写入都会修改同一个文件内容；已有测试只证明原子替换目录项有效。改为 Mod 独立复制，游戏程序继续硬链接；启动快照缺失时显式失败，取消回退到可能已经更新的源目录。加入源文件原地更新、worker 写入、原子替换三个边界测试。
2. **P1，求解设置没有进入缓存及 worker 状态。** 原版 `PreCombatForecastApi.cs:280` 的缓存键只含跑局和选项，`PreCombatForecastWorker.cs:679` 只在创建 worker 时复制磁盘设置。更改药水策略或搜索设置后可能复用旧结果，ForceRefresh 也可能仍使用旧 worker 设置。现在捕获内存设置，将其纳入状态令牌和会话签名，向 worker 写入本次捕获值；设置改变时重建 worker。
3. **P1，固定 default/1 不代表当前 Steam 账号。** 原版 `PreCombatForecastWorker.cs:669` 只复制 default，实际 Steam 账号目录不同。现在通过游戏的 UserDataPathProvider 捕获当前账号路径，映射到关闭 Steam 的 worker 账号目录。
4. **P2，停止与总超时的范围不正确。** 原版 `PreCombatForecastWorker.cs:425` 的 Stop 先等待 Gate，正在运行的请求不会被主动取消；期限到第258行才开始，未覆盖排队和创建阶段。现在 Stop 先取消活动请求，期限在排队前建立，失败时清理当前 worker。同步文件复制期间不提供逐文件实时抢占，但取消后会在进程启动前再次检查。
5. **P1，嵌入式游戏的正常退出没有触发预期的 ProcessExit 清理。** 新增退出检查实际发现副本残留；原版仅在第28行依赖该事件。补充 NGame.TreeExiting 回调，并与进程创建共享退出状态保护；正常退出清除大型副本，诊断材料保留。强制终止整个父进程的崩溃恢复不在本轮证明范围。

原始文件可从 [PR 固定提交](https://github.com/SlimoonLee/CombatSolver/tree/138f6ad9df4f26d30924df30cae91d280a9e9d84/src/Api) 对照，以上行号属于原始 PR。

## 对 Seed Oracle 的判断

- 已阅读其 README、CombatSolverAdapter、战前面板与缓存/模拟调用。它直接编译引用 API v5，明确提供手动计算、确定路线和假设样本；并不是尚无使用方的接口提案。
- 保留 ForecastAsync、SimulateAsync 及 worker 控制接口，避免为了缩小首版接口而使现有调用端失效。当前 Seed Oracle 源码使用合并后的 DLL 编译成功，零警告/错误。
- 独立进程适合复用原生开战流程；它会增加启动、内存和文件复制成本。headless 隔离的是进程和指定目录，不能作为任意第三方 Mod 的操作系统沙箱承诺。
- 条件路线仍要求调用方标注沿途未实际执行的购买、奖励、休息等假设。Seed Oracle 的当前说明已经作了区分。

## 本轮证据

- `PR43-PRECOMBAT-API-INTEGRATION`，runId `d44c83b14da04695b79f218e5056d32d`，68.0 秒 Passed：规范化存档、文件写入隔离、设置令牌失效、主动取消、确定预测重复一致、假设 RNG 样本、worker 复用、静音及保活设置、主跑局状态不变、样本后关闭。
- 正常退出清理首次检查失败；补充 TreeExiting 后，`PR43-EXIT-CLEANUP`，runId `b2d1e2d25beb47eeb976f8062657a4a3`，18.9 秒 Passed，进程退出后目录检查确认 startup-mods 已移除。最后的启动/退出共享锁只做编译核对，未穷举所有取消竞态。
- CombatSolver 与 Seed Oracle Release 编译零警告/错误；Windows 结构门禁通过。没有重跑作者报告的十 Mod 组合、Steam 可见双 Mod 联动或 Linux 游戏。

## 合并与通知

保留主线 0.31.1 版本号、现有检查点恢复与无人测试隔离工具；作者 0.29.x 本地扩展日志作为历史记录。此次只合并主线，不发布工坊新版。合并后通知作者接口已兼容，工坊 0.31.1 仍未包含 API，应等待后续正式版本再切换依赖。
