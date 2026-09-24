# CombatSolver 下一版本（开发中）

## 简体中文

- 感谢 [s1f102500012](https://github.com/s1f102500012) 提交的 [PR #131](https://github.com/Torch1230/CombatSolver/pull/131)：主窗口靠近屏幕右侧时，药水、成长和遗物策略面板会优先向左展开，尽量不遮挡战斗路线；两侧空间都不足时，会贴近空间较大的一侧屏幕边缘。
- 感谢 [ltlly](https://github.com/ltlly) 提交的 [PR #132](https://github.com/Torch1230/CombatSolver/pull/132)：新增[可选的 ServerGC 启动方式](../performance/server-gc-launch-profile-20260924.md)。运行库确认启用后，本次游戏的搜索改用常规内存回收，设置页会显示状态；普通启动仍使用原先保存的 NoGC 设置。此模式作用于整个游戏进程，可能增加 CPU 占用；部分战斗可能变慢，路线和战损也可能不同。

## English

- [PR #131](https://github.com/Torch1230/CombatSolver/pull/131) by [s1f102500012](https://github.com/s1f102500012): When the main panel is near the right edge of the screen, the Potion, Growth, and Relic strategy panels now open to its left where possible, keeping combat routes visible. If neither side has enough room, the panels move toward the roomier screen edge.
- [PR #132](https://github.com/Torch1230/CombatSolver/pull/132) by [ltlly](https://github.com/ltlly): Added an [optional ServerGC launch mode](../performance/server-gc-launch-profile-20260924.md). When the runtime confirms it is active, searches use normal garbage collection for that game session and the settings page shows the active mode; ordinary launches still use the saved NoGC setting. This mode affects the whole game process and may use more CPU; some combats may run slower, and routes and HP loss may differ.
