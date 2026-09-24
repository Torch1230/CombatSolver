# CombatSolver 下一版本（开发中）

## 简体中文

- 感谢 [ltlly](https://github.com/ltlly) 提交的 [PR #133](https://github.com/Torch1230/CombatSolver/pull/133)：普通 Steam 启动现已支持自动接入“多核内存回收”（Server GC）。首次加载后再次启动游戏即可生效，后台自动并发清理内存，大幅降低深度搜索时的内存占用并提升长时间游玩的流畅度；设置页同步重写了内存管理文案与状态展示，清晰指示当前运行状态与下次启动配置，也可随时在设置中关闭并一键还原。
- 感谢 [AuroraAeon](https://github.com/AuroraAeon) 提交的 [PR #134](https://github.com/Torch1230/CombatSolver/pull/134)：优化了复杂战斗与长回合搜索中的候选探索策略，减少冗余探测，深层计算下的等待时间明显缩短。
- 搜索界面体验优化：
  - 顶栏在搜索进行中与完成后均显示计算速度指标（如“已查阅 1,234 条世界线（567 条/s）”与“花费了 0.5 秒，共查阅了 1,284 条世界线（2,568 条/s）”），并配备平滑滚动与缓动动画，计算节奏一目了然。
  - 彻底精简搜索期间顶栏的冗余信息，移除前沿预览中的重复战损提示与橙色过渡警告，首行布局更清爽聚焦。

## English

- [PR #133](https://github.com/Torch1230/CombatSolver/pull/133) by [ltlly](https://github.com/ltlly): Standard Steam launches now support multicore memory cleanup (Server GC) automatically. Restart the game once after installing to enable background concurrent memory cleanup, significantly lowering memory usage during deep searches and improving overall smoothness. Redesigned the memory management settings and status indicators to clearly display session states, and you can easily toggle it off to restore original launch settings at any time.
- [PR #134](https://github.com/Torch1230/CombatSolver/pull/134) by [AuroraAeon](https://github.com/AuroraAeon): Optimized route exploration in complex, multi-turn combats by streamlining candidate probes, noticeably reducing wait times during deep searches.
- Search UI improvements:
  - Added search throughput indicators both while calculating and upon completion (e.g. `Explored 1,234 timelines (567 /s)` and `Searched for 0.5 s; explored 1,284 timelines (2,568 /s)`) with smooth rolling animations to give an intuitive sense of calculation progress.
  - Cleaned up redundant warnings and candidate notices from the search header, keeping the active view clean, focused, and distraction-free.
