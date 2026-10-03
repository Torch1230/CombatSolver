# 常驻策略迭代会话

此入口仅供本地问题包排查。所有会话共用仓库内的固定实例 `strategy-development`；`start` 启动游戏并提交轻量就绪请求，不跑测试战斗。后续 `run` 直接把问题包交给常驻进程，跳过包预检和游戏快照扫描。主 DLL 或游戏依赖变化时先 `stop` 再 `start`；启动器只替换固定副本中变化的文件。`stop` 停止游戏和监控进程，保留游戏副本供下次启动。

`start` 默认打开独立只读监控窗口；游戏仍以 headless 运行。窗口每秒刷新一次，显示包、请求、PID、脚本与参数版本、阶段、时限、节点、速率、前沿、当前最好预计战损与用药，以及错误或超时原因。关闭窗口不会停止游戏；在仍运行的会话上再次执行 `start` 可重开窗口。`start --no-monitor` 关闭该会话的监控，供批量或性能对照使用。Windows 使用独立 WPF 窗口；Linux 使用可用的终端模拟器与同一状态文件，本轮未运行 Linux 门禁。

Windows 示例（在仓库根目录执行）：

```powershell
pwsh -NoProfile -File tools/replay/strategy-session.ps1 start ability-work --game-root 'D:\Steam\steamapps\common\Slay the Spire 2' --ritsu-root 'D:\Steam\steamapps\workshop\content\2868840\3747602295'
pwsh -NoProfile -File tools/replay/strategy-session.ps1 run ability-work '.local/issue-bundles/strategy-0923/raw/<报告 ID>.zip' --script 'tools/replay/strategy-example.cs' --params '.local/strategy-parameters.json'
pwsh -NoProfile -File tools/replay/strategy-session.ps1 run ability-work '.local/issue-bundles/strategy-0923/raw/<报告 ID>.zip' --early-turns 2
pwsh -NoProfile -File tools/replay/strategy-session.ps1 status ability-work
pwsh -NoProfile -File tools/replay/strategy-session.ps1 stop ability-work
```

Linux 入口为 `tools/replay/strategy-session.sh`，命令和选项相同。这里保留跨平台入口；本轮没有运行 Linux 门禁。

脚本是普通 C# 源文件，实现公开的 `IDevelopmentSearchStrategy`；编译只作用于这个脚本项目。参数文件是数值 JSON 对象，例如 `{"persistentBuffWeight": 2}`。`run` 在提交请求前复制脚本和参数，并按脚本内容及主 DLL 身份缓存编译结果。下一次请求读取新文件；已开始的搜索沿用自己的版本。脚本实现可以通过四个钩子调整候选优先级、中途评分、一个有界保路代表及现有搜索组合成员的编排，也可以在脚本中组合只读特征生成新的估值维度。新游戏状态或新模拟原语仍需扩展主程序；最终胜负、战损和资源排序不交给脚本。

默认 `VeryHigh`、180 秒、DOP 8。内存紧张时可在 `start` 指定 `--host-memory-mib 2560` 调整无头宿主的准入预留；这不是游戏内存上限，也不改变搜索配置。到时的包记 `timeout` 和 `exceeded_180_seconds_package_discarded`，跳到下一包；不把超时结果写成路线收益。监控开启时，超时前的最后一份本请求状态写入请求目录的 `timeout-progress.json`，并附在 `session-result.json` 的 `timeoutProgress` 中；没有本请求快照时不推断搜索阶段。每次结果在 `.local/strategy-sessions/<会话>/requests/<请求>/session-result.json`，含原包、模式、主 DLL 哈希、脚本和参数哈希、PID、复用标记、墙钟与求解指标；总索引为 `results.jsonl`。编译失败留 `script-build.log`，请求记 `strategy_or_input_failed`；游戏内脚本异常由无人请求写入 Failed，不使用旧结果。

离线追加搜索可用 `run --early-turns 1` 或 `2` 显式开启：常规搜索结束后，从前一或前两回合的真实回合末状态各保留至多 24 个不同开局与状态，再逐条完整续搜。追加阶段共享最多 100 万展开节点和内存压力检查；每条续搜使用请求剩余时间，没有独立短时限。开启时整个包默认限时 2400 秒，也可用 `--deadline-seconds 15..2400` 缩短；到期记明确的 `timeout`，不把未打完的路线作为胜利。普通 `run` 仍为 180 秒。游戏内同一功能放在“设置 > 性能”的第三个实验开关，默认关闭；玩家显式开启后追加搜索也使用最多 40 分钟，仍可取消。

开发会话 `run` 固定 `SearchOnly`；`--selector` 默认 `start`，搜索质量比较须保持 `combat_start` 同根。`--policy` 可传已有的回放政策覆盖文件。`status` 报告固定进程是否存活；`run` 直接复用已启动的进程，并核对进程归属和出生时间。包材料和恢复错误由游戏请求结果报告。
