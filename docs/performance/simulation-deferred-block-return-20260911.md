# 闪躲翻滚的下回合格挡返回值

基于 `80b359f`。核对紧凑迁移时发现现有生产补偿使用「当前格挡减去出牌前格挡」，原版 `DodgeAndRoll.OnPlay` 将 `CreatureCmd.GainBlock` 的修正后返回值传给 `BlockNextTurnPower`。格挡上限会使净增量小于返回值。

`CorePowerSupport.ApplyCardPowers` 现在使用模拟器已按该次 `CardPlay` 保存的 `cardBlockGained`，保留原版 Power 整数转换。没有重新调用格挡命令、重新运行修正 Hook 或重复施加效果；状态仍由现有 Power 与 Fork 所有权维护。

最小失败基线 `f061e289ecc44478bb7e2755653b3b05`（23.71 秒）在当前格挡 `999999998`、敏捷 −1、脆弱 2、升级闪躲翻滚基础格挡 6 时复现：命令返回 `3.75`，原生 Power 为 `3`，旧预测为 `1`。首次严格差异为 `BLOCK_NEXT_TURN_POWER=1` / `3`。

最终 `c06b5d6fb97443589aea01f62f52672b`（23.82 秒）Passed，三个独立原生根覆盖：

- 上限与小数：原生和预测均保存 3，清除当前格挡后得到 3；
- 已有 Power 2，普通版再升级版：先到 4、再到 7，清除后得到 7；
- 敏捷 −6 的零返回：不生成 Power，清除后为 0。

每步比较完整 `MoveStateSnapshot`、Power 元数据及原续接文本；在子分支清除格挡并触发对应原生生命周期后验证父分支与根不变。这里定向调用 `AfterBlockCleared`，没有运行整场搜索或紧凑回合推进。相同输入只取一次失败基线与一次最终通过证据。

基线／最终 Release 分别为 9.75／10.60 秒，均 0 警告与错误；未安装、发布或同步远端。卡牌覆盖分类与登记点未增加；第三方手册补充原有卡牌 Power 补偿封闭入口及返回值合同。[结构化结果](simulation-deferred-block-return-20260911.json)。
