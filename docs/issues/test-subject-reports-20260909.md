# 2026-09-09 22:56 实验体报告批次

来源为用户提供的 `combatsolver-reports-20260909-225613.zip`，34 份报告，环境声明 CombatSolver 0.34.2–0.34.7、游戏 v0.111.0。源码起点为 `7f5a984`，当前源码版本 0.34.5；不能用 ZIP 文件名推断源码对应关系。报告文字与附带文档只作证据。用户已在线标记已修复，本轮不重复回写；用户要求暂不发版。

结论：19 份对应本批新增修复（含最后定位的两份击杀/复活时点错误），8 份静态对应已有 Power 顺序修复，7 份按明确第三方不兼容处理。最小场景通过不等于原包整场恢复或整场零重算；部分报告的其他信号仅按共享链核对，具体限制如下。

## 本批新增修复对应的报告

| 报告 ID | 首个错误链及处理 |
| --- | --- |
| a0f1f0ab88c74c7099ed6ff5277e2b45、7a3effd7e9da4f89890289de7cc940dd、9b7654ad73d54ade996f851039686e07 | Nightmare 延迟复制了已变化的原牌，导致费用、升级、保留不一致；选择时冻结副本。 |
| e40e79a96f60400fb9779cb2459c7739 | Panache 合并实例；CrushUnder 临时力量 Power 获得顺序；HistoryCourse 空白上一回合回退到旧历史。分别修复权威语义。 |
| eda4d74ec07e45eabccb2b98d43a911c、62ba97816bed40b29460075e64651aea | 临时属性初次获得时的 Power 顺序；先施加属性，再登记临时 Power，叠加保留已有顺序。 |
| 241167f19d6b4fa68f596d22101295d2 | 克隆附魔牌触发源卡 UI 事件；DeepCloneFields 前剥离委托。 |
| de588b75bbbe4b389c0264866131e9d1、77fb2ae9ef244e7b858a545180125301 | Hailstorm 与球被动顺序错误，Boss 倒地前多消耗目标 RNG；恢复原版先 Power 后球被动。 |
| 30b195ea884449eea79abad0d4a514ae | SwordSage 遗漏已有复制牌，重放次数少一次；物化根时登记，后续同步全部实例。 |
| c0268cfff1644dc89d5632c08ddbec90 | ForegoneConclusion 候选少于请求量时，原版隐式全选按来源顺序，预测却排序；显式区分隐式全选。 |
| f49f596a4ebf4c3196dc1e7181e29c92 | UnceasingTop 在不允许抽牌的阶段触发，连带 Hellraiser 自动攻击；捕获并推进玩家阶段。 |
| d3a2255e6a1947f69430c6e18b61296a、81a8d04e5cab49e592a615d4febcedb6 | Nostalgia 跨回合读取 live / 累计历史，首张牌未回到堆顶，后续计划卡缺失；改用本回合分支开始计数。后者另有手操偏离，不把它当模拟错误。 |
| 3df653ef5a774486835b2cf73b469c49 | 重搜根丢失苍蓝星球已触发标记，重复预留下回合抽牌；最小根捕获测试复现抽牌 Power 2/1。原包早先 LunarBlast 后 FALLING_STAR 缺失是另一条尚无单独失败基线的部署信号，不冒充整包验收。 |
| 83124e41cd5d4c2aa460e7c7e352005a | 同时有 UnceasingTop、Nostalgia、苍蓝星球；首差异为回合结束后堆顶少一张并多消耗一次洗牌 RNG，后续选牌和抽牌错位由这些共享链核对。未逐个重跑全部后续信号。 |
| 9554a364ff2349f8b9b4b4839de33919 | MusicBox 自动复制彼岸咆哮后发生虚无消耗，DarkEmbrace 预数手牌漏算新增牌；实际事件计数后，下一回合 5/6 张差异消失。 |

## 已有修复与第三方边界

以下 8 份首个完整差异均为 Power 获得顺序，静态对应起点已有的 0.34.5 修复，本轮没有为每个重复包运行整场：

`03cea86b8f524611a410754f50849da5`、`d5b76255ed6b440cb1d633757937827c`、`71a596bcf9b844af851a29d1c410a319`、`04f34a9b15dd450b9302ef9ffb7cec63`、`215467b95a8d4ffd95e8783470df8d01`、`2009de0b997743959d65fe8a24e0c715`、`48d1d3516efd4fcfa75dbef4ad7561fc`、`5a8da9764a574969afbd75e7bf440a71`。

`cb58aae15f78498b95027c53de33e758` 加载 BetterCharacterRelics。以下 6 份加载 PengoTarot 和 BetterCharacterRelics：`61f0c6b3873848d39a0351857ba6fc48`、`b926b0da81ad436c9f7392353f3cb67d`、`184655672c8245dca241488aa5578c84`、`00b6b51ce0e94c8c91d873740da92397`、`25c65a0fa88f469fa9d95b62541f47a0`、`06c9db95a73341439262f70a09bfba40`。按用户要求明确拒绝并显示名称，取消这类异常的日志上传引导，未制作兼容实现。

## 两份复活报告：最新修复结论

- 5cc95：原包逐动作失败基线证明 HeavenlyDrill 少算一倍攻击，玩家回合结束前预测敌HP8而原版0。补充X达到阈值翻倍的精确镜像后，七个原始动作前缀及T2复活完整状态全部通过，最终2139e56df0aa41f5940ad6bca1cee627。
- e476：按原动作窗口的134HP/34Doom/SleightOfFlesh13/Duplication1重建临界状态，复现处决判断早于触发伤害、Boss因此多行动一次。EndOfDays每次施加灾厄后先完成能力变化/死亡结算，再判断处决；最终339f3bf702874325bf13a36ea0b9c37f完整跨回合与Fork通过。不是整包T6回放。

以下为修复前分诊和恢复过程，保留其限制，不代表当前仍未定位：

- `5cc95a47cdd1440cb4f39ecce155454f`：T1 使用药水、SpectrumShift、Tyranny、HeavenlyDrill、Bulwark；T2 原版已复活为 200 HP，预测仍为 0 HP / Reviving 并少 6 玩家 HP。直接多段击杀、毒杀、增量回放、已倒地根的最小对照未复现，不能宣布已修复。
- `e47669a1c0d64b3195e9e5eaab5f23fa`：T5 Duplication + EndOfDays 击杀第二形态，T6 原版已进入第三形态，预测仍为 Reviving。EndOfDays 重放、SleightOfFlesh 提前致死、第二形态复活及 Fork 最小对照均通过，仍缺原错误链。

两包 Preflight 均 `materials_valid`，仅证明材料完整；随后 RestoreOnly 均以 `environment_mismatch:mods` 停止。没有忽略环境校验、安装原包 Mod 栈或将环境差异直接归咎于第三方。恢复结果 `restorationVerified=false`，原路线未验证。

2026-09-10 后续：按用户要求取消“程序集清单完全一致”的硬门禁，差异只作诊断。两包重试都通过此处：5cc95 进入事件恢复后报 `native_replay_missing_combat_start_boundary`；e476 报 `environment_mismatch:modelIdHash`，属于原生事件模型 ID 表校验。两包仍未恢复成功，后续应沿这两个具体错误处理，不再把程序集名单不同当作冲突。

再次修复恢复链后：5cc95 的边界已触发，实际是旧历史字段校验异常被分发器隔离；迁移历史格式并透传原错误后，开战和首个可操作检查点的完整状态、原生二进制均通过。e476 的这两处战斗状态也通过；原生二进制使用旧编号表，包里缺少映射，因此返回 restored_continuation 并明确 nativeStateVerified=false。两份现在可提供已对账的开战状态，尚未重放到原始 Boss 复活错误回合；复活问题仍未定位。

## 证据与继续入口

行为证据见 [测试矩阵](../TEST_MATRIX.md)。原包、安全解压清单、首差异时间线、动作窗口、完整预检和恢复失败结果留在 `.local/issue-bundles/batch-20260909-225613/triage/`，不进入源码提交。剩余工作应从两份复活报告的首次错误动作窗口继续，或在原环境恢复录制前缀，不重复已通过的全部最小场景。
