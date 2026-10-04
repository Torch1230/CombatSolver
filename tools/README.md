# CombatSolver 工具

这里只保存仍有明确用途、可以重复运行的工具。一次性排查脚本放在 `.local/tool-tasks/<任务>/`，用完删除；旧实验源码可从 Git 历史取得，不保留在工具目录。

| 目录 | 用途与入口 |
| --- | --- |
| [build](build/) | `build-local-stack.ps1` / `.sh`：构建本地依赖与 Mod |
| [release](release/) | `publish-release.ps1`：渠道发布；[WorkshopMetadata](release/WorkshopMetadata/README.md)：工坊标题与介绍成对维护；连接元数据与夸克包检查 |
| [testing](testing/) | 无人启动、实例所有权、矩阵及 `checks/` 中的生产回归检查 |
| [replay](replay/) | 检查点批量恢复、CheckpointTool、常驻会话及开发监控 |
| [search](search/) | 离线宿主、固定根语料、生成场景、排序与组合模型分析 |
| [performance](performance/) | trace 分析、录制、固定条件 A/B、GC 启动配置 |
| [inspection](inspection/) | 覆盖目录、原版调用扫描、本地化、代码审计及结构检查 |
| [community](community/) | 问题归类、代表包导出与清理、认领同步 |
| [runtime](runtime/) | 随 Mod 部署的 Windows MemoryCleaner |

在线监控由独立私有仓库 [combatsolver-presence-service](https://github.com/Torch1230/combatsolver-presence-service) 维护。历史结果见 [文档归档](../docs/archive/README.md)，不从旧实验代码判断当前行为。

## 常用入口

```powershell
pwsh -NoProfile -File tools/build/build-local-stack.ps1 -Configuration Release
python tools/inspection/verify-tools.py
python tools/inspection/verify-tools.py --build
python tools/inspection/verify-documentation.py
python -B tools/inspection/verify-coverage.py
pwsh -NoProfile -File tools/inspection/verify-refactor-boundaries.ps1
pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 <fixture参数> -CleanupInstanceOnExit
pwsh -NoProfile -File tools/replay/run-checkpoint-batch.ps1 -InputPath <问题包> -ReplayMode Preflight
```

Linux 的 `.sh` 是原生入口；无人测试必须带 `--cleanup-instance-on-exit`。测试、回放和性能方法由 [无人测试](../docs/HEADLESS_TESTING.md)、[检查点回放](../docs/CHECKPOINT_REPLAY.md)、[离线宿主](../docs/OFFLINE_SEARCH_HARNESS.md) 和 [性能指南](../docs/performance/README.md) 维护。

## 构建与产物

所有工具项目从 `Directory.Build.props` 取得仓库根与统一输出位置：`.local/tool-build/<项目名>/bin/<配置>/<框架>/`，中间文件在同项目的 `obj/`。工具目录保存源码、必要样例和简短用法；问题包、日志、trace、测量结果和临时项目写入 `.local/`。

同一职责先扩展现有工具或 fixture。新增可复用工具需说明用途、输入输出、依赖及可重跑命令，并通过结构检查；退役时删除实现和依赖，更新当前入口，历史证据链接到最后保存的源码提交。
