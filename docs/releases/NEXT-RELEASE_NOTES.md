# CombatSolver 下一版本（开发中）

## 简体中文

- 感谢 [ltlly](https://github.com/ltlly) 提交的 [PR #133](https://github.com/Torch1230/CombatSolver/pull/133)：普通 Steam 启动现在会自动准备多核内存回收，首次加载后再启动一次游戏才会生效。它可能降低搜索时的内存占用，但会影响整个游戏和其他 Mod，也可能增加处理器占用、让部分战斗搜索变慢或改变路线。可在设置中关闭；卸载 Mod 前请先关闭，以恢复原来的启动设置。内存设置现会说明本次是否生效，以及搜索时暂缓回收的选项何时可用。
- 感谢 [AuroraAeon](https://github.com/AuroraAeon) 提交的 [PR #134](https://github.com/Torch1230/CombatSolver/pull/134)：调整了复杂战斗中的路线探索，部分长搜索的等待时间可能缩短。搜索路线和战损也可能变化，个别战斗可能多损失生命。
- 搜索时会在已查阅的世界线数量旁显示每秒查阅速度，并精简路线预览中的重复提示。

## English

- [PR #133](https://github.com/Torch1230/CombatSolver/pull/133) by [ltlly](https://github.com/ltlly): Normal Steam launches now prepare multicore memory cleanup automatically. Restart the game once after the mod first loads for it to take effect. It may reduce memory use during search, but affects the whole game and other mods; it may also use more CPU, slow some searches, or change routes. You can turn it off in settings. Do so before uninstalling the mod to restore the previous startup setting. The memory settings now show whether the mode is active this session and when pausing cleanup during search is available.
- [PR #134](https://github.com/Torch1230/CombatSolver/pull/134) by [AuroraAeon](https://github.com/AuroraAeon): Adjusted route exploration in complex combats. Some long searches may finish sooner. Routes and HP loss may also change, and a few combats may lose more HP.
- While searching, the number of explored timelines now includes a per-second rate. Repeated notices in route previews have been reduced.
