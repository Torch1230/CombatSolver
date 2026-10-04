# 0.49.2 内容性 Mod 提示（2026-10-04）

## 来源与行为

日志站静态分诊固定两份 0.49.1 报告：

- `23b7eded27f940fcb0970bdd5f5d9639`：首个搜索失败为 `BeforeSideTurnStart` 缺少 `HextechRunes.UniversalScopeRune` 镜像。回合阶段门禁抛普通 `NotSupportedException`，原先进入计算失败及未支持效果分类，并引导上传。
- `bc6ab827557b4dbe8d898e00a3bbdc89`：记录 SearchFailure ×4、UnsupportedCombatSemantic ×4，异常来自 `SearchGcPauseSnapshot.Capture` 的 `PlatformNotSupportedException`。它继承 `NotSupportedException`，此前被账本归入战斗效果。此次只校正分类，内存回收首因未复现、未修复。

这些材料只作静态定位，未恢复或重放原包。第三方来源依据失败入口的模型类型及游戏加载的程序集映射，而不是已安装 Mod 名单或后台标签。

Engine 的未支持内容异常工厂在既有拒绝入口生成来源分类。覆盖回合阶段、金币回调、搜索支持表外药水和计算型变量；原版、来源未知及求解器自身保留诊断失败。计算型变量优先使用卡牌来源，共享 `ComputedDynamicVar` 包装器由卡牌提供内容来源。

Runtime 消费已有来源异常，统一提示“求解器暂未适配此内容性 Mod：名称，无法求解。”。中文与英文同步，Mod 名称插入前转义富文本方括号；报告账本只记录 `IncompatibleGameplayMod`，显示为“内容性 Mod 暂未适配”，这个类别不触发上传提醒。运行库平台错误保留主失败与诊断上传。

## 验证

`CONTENT-MOD-FAILURES` 与 `VerifyPredictionFailureBoundaries` 在同一原生无人进程 Passed：runId `849fb38580474f7881c05d113beef5d3`，24.505 秒。覆盖八个未适配 Hook、CalculatedVar、自定义计算变量、共享框架包装器、包装异常、eng/zhs/zht、Mod 来源及富文本转义、四类失败账本和普通诊断上传。原生未知回调和计算器均未执行。

测试时使用最终行为源码、0.49.1 版本元数据；后续只同步 0.49.2 与文档，最终 Release 构建承担版本定稿。实例启动器成功删除 `.local/headless-instances/wt-f9fe55e52824479a`。未进行可见 Steam 弹窗排版验收或第三方原包整场回放。

复跑入口及断言范围见 [测试矩阵](../../TEST_MATRIX.md)。
