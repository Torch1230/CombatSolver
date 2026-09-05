# Headless 实例与并行测试

这是测试基础设施，不是单场搜索多核优化，也不支持多可见游戏窗口。游戏内请求仍串行，由各进程的 ProtocolHost 处理；不同实例才能并行。

## 用法

先在各任务自己的 worktree 构建，禁止两个 agent 同时构建同一 worktree：

```sh
dotnet build CombatSolver.csproj -c Release -p:CopyModOnBuild=false
```

Linux 示例（各终端/agent 运行自己的请求）：

```sh
bash tools/run-unattended-test.sh --headless-instance semantic-a --headless-execution-mode parallel --headless-memory-reservation-mib 4096 --headless-cpu-reservation 2 --timeout-seconds 120 --exit-on-complete
bash tools/run-unattended-test.sh --headless-instance semantic-b --headless-execution-mode parallel --headless-memory-reservation-mib 4096 --headless-cpu-reservation 2 --timeout-seconds 120 --exit-on-complete
```

Windows 使用 PowerShell 7.4 或更新版本，参数对应 `-HeadlessInstance`、`-HeadlessExecutionMode Parallel`、`-HeadlessMemoryReservationMiB`、`-HeadlessCpuReservation`、`-HeadlessQueueTimeoutSeconds`；场景参数与已有原生启动器相同。

实例 ID 默认由 worktree 路径生成，也可显式指定（64 字符内的字母、数字、点、下划线、短横线）。同实例第二个 producer 立即拒绝，不能替换已有请求。每个实例拥有私有游戏可执行文件及 Mod 栈、APPDATA/LOCALAPPDATA 或 XDG 数据/配置/缓存、日志和协议文件。不会往源游戏目录安装临时 RitsuLib，也不会覆盖玩家存档。

默认 DLL 来自本 worktree 的 Release 产物、manifest 来自仓库根；`--combat-solver-build-dir` / `-CombatSolverBuildDir` 可以指定包含 DLL 和 manifest 的冻结构建目录（Windows 还需要该构建的 MemoryCleaner）。其他游戏文件和 Mod 从指定源游戏复制，RitsuLib 从指定依赖路径复制；完整内容身份参与复用判断。构建、依赖或源游戏变化时，只停本实例精确认领的旧游戏，再发布新快照。Linux 优先 reflink；旧游戏快照移动到实例内 retired 目录，保留可回收证据，不自动删除用户目录。

## 排队、复用与资源

- 默认 `exclusive`：等待其他已登记实例退出；`parallel` 显式启用，当前最多同时两个游戏。队列超时默认 120 秒，与实际场景的超时分开。
- 每用户主机共享协调目录：Linux `${XDG_STATE_HOME:-$HOME/.local/state}/CombatSolver/headless-host-v1`，Windows `%LOCALAPPDATA%/CombatSolver/headless-host-v1`。所有工作任务必须使用同一协调目录；不要按任务改这个路径。`COMBATSOLVER_HEADLESS_HOST_ROOT` 只供无游戏测试隔离，绕开它会失去跨任务互斥。
- CPU 默认预约 2（单核机为 1），内存默认预约 4096 MiB；累计 CPU 不超主机逻辑 CPU，内存预留至少 2 GiB 主机余量，同时考虑当前可用内存及其他尚未兑现预约。预约只是准入记账，不是 OS 硬配额，也不自动降低 DOP 或 NoGC。应按峰值、NoGC 堆预算与原生开销选择预约；4 GiB 不保证容纳所有场景。
- Passed 不代表进程可复用；仍需匹配本请求的静稳 Ready ACK。小批次可以复用同一进程，最后一项必须请求 ExitOnComplete，否则暖进程仍占名额并阻塞独占队列。Held 继续占名额且不能复用；发布本实例 release 文件后只退出本实例。
- 取消、Failed、超时只清理本实例精确 PID/出生身份。PID 复用、无法读取身份、损坏或孤儿 lease 必须封闭失败/保留，不能按进程名杀掉其他任务。
- 未登记游戏（包括旧版启动器与可见游戏）会阻止新请求准入，两个模式都一样。主机锁不能约束不使用它的第三方启动器；做性能测试前仍需协调其他任务，不能声称它是系统级排他锁。

`COMBATSOLVER_HEADLESS_ROOT` 仍可指定精确实例目录，迁移前遗留的活进程/不兼容 marker 不会冒险接管。并行结果只作正确性和总测试吞吐证据，不用于单场速度、GC 暂停或峰值内存 A/B；最终性能结论仍来自正常可见 Steam 的独占测试。

## 验证边界

`bash tools/test-headless-runtime.sh` 使用原生子进程替身验证租约、排队与隔离，不启动游戏；`--snapshots` 单独验证产物隔离，`--snapshot-failures` 单独注入哈希/复制/发布失败。Windows helper 自测为 `pwsh -File tools/test-headless-runtime.ps1`，`-ProfileOnly` 单独检查资料复制与重解析点拒绝。这些结果不等于真实双游戏、真实 Mod 加载或 Windows 进程生命周期通过；真实验收另记入 TEST_MATRIX。

GC 研究分支移植时，Linux 暖进程准入改为只预约尚未兑现的增长量（预约减已用 RSS，最小为 0），与 Windows 口径一致；总预约、CPU 和主机余量限制仍生效。`bash tools/test-headless-runtime.sh --warm-memory` 定向覆盖该边界和等待期间的租约替换。HoldAfterInitialSearch 与 StopAfterInitialSolverResultAssertion 互斥，两端入口在准入前拒绝同时使用；采样停在初次结果时使用 Hold 保持游戏存活。
