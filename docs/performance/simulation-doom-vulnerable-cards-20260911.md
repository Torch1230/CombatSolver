# 毁灭／虚弱／易伤卡牌的生产编译准入（2026-09-11）

本批把亡灵池中的五张原生卡（`DEATHBRINGER`、`NEGATIVE_PULSE`、`SCOURGE`、`PUTREFY`、`FEAR`）逐张定向反编译后迁入 `CompactCardProgramCompiler`。五张牌的完整 `OnPlay` 都能由既有 `ApplyBasicPower`、`GainBlock`、`Draw`、`AttackTarget` 精确表示，所以全部准入；没有放宽未知语义、没有按卡名回退，也没有缩小 CallOfTheVoid 的 78 候选池。

## 逐张语义（本机 `sts2.dll` 定向反编译）

| 卡牌 | 原生 OnPlay | 编译指令 | 升级 |
| --- | --- | --- | --- |
| `DEATHBRINGER` | 2 费技能，`AllEnemies`：对 `HittableEnemies` 整体施加 `DoomPower`，再整体施加 `WeakPower` | `ApplyBasicPower(Doom, AllEnemies)` → `ApplyBasicPower(Weak, AllEnemies)` | 毁灭 21→26 |
| `NEGATIVE_PULSE` | 1 费技能，`AllEnemies`：先 `CreatureCmd.GainBlock`，再逐个敌人施加 `DoomPower` | `GainBlock(5)` → `ApplyBasicPower(Doom, AllEnemies)` | 格挡 5→6、毁灭 7→11 |
| `SCOURGE` | 1 费技能，`AnyEnemy`：先对目标施加 `DoomPower`，再 `CardPileCmd.Draw` | `ApplyBasicPower(Doom, ChosenEnemy)` → `Draw(1)` | 毁灭 13→16、抽牌 1→2 |
| `PUTREFY` | 1 费消耗技能，`AnyEnemy`：同一 `"Power"` 动态变量依次施加 `WeakPower`、`VulnerablePower` | `ApplyBasicPower(Weak, ChosenEnemy)` → `ApplyBasicPower(Vulnerable, ChosenEnemy)` | 两者 2→3 |
| `FEAR` | 1 费虚无攻击，`AnyEnemy`：先 `DamageCmd.Attack`，再施加 `VulnerablePower` | `AttackTarget` → `ApplyBasicPower(Vulnerable, ChosenEnemy)` | 伤害 7→8、易伤 1→2 |

关键所有权与时序：

- 原生 `PowerCmd.Apply` 的重载按目标顺序逐个完成同一条命令，下一条命令在整批目标之后才开始。因此每张牌编译成“每条指令一整轮名单”的序列，而不是“每个目标执行完所有指令”；这正是三敌名单夹具能区分 `AllEnemies` 与 `ChosenEnemy` 的原因。
- `DoomPower` 是 `Counter` 计数能力而非持续时间能力：只在敌方阵营结束前（`BeforeSideTurnEnd`）触发所属方死亡，玩家阵营结束后（`AfterSideTurnEnd`）触发玩家方，攻击生命阈值包含等号、保留格挡且不产生伤害历史。值层原本已有这些相位，本批只新增“卡牌施加的毁灭”入口与敌方毁灭槽。
- `VulnerablePower` 是持续时间减益：受击方在其阵营结束时递减；倍率由捕获的 `DamageIncrease`=1.5 进入不可变指令元数据，出牌当场施加的易伤不参与同一张牌的这次攻击。
- `Artifact` 按请求量拦截整条减益命令（含毁灭），被拦截时不产生毁灭施加历史；这一条由新的纯值合同覆盖。

## 准入闭包与未表示项

- `CardEffectProgram` 的 `ApplyBasicPower` 域新增 `Doom` 与 `Vulnerable`，两者都要求敌方目标、非负请求量且 `EnergyXMultiplier == 0`；自施、负值与 X 缩放继续显式拒绝。
- `CompactDiscardProjection` 只在实际存在可施加毁灭的来源时给敌人建立毁灭模板槽：捕获到 `Deathbringer`／`NegativePulse`／`Scourge`，或根中存在 `CallOfTheVoid`（冻结池可能生成其中之一）。玩家毁灭槽仍只由 `Neurosurge` 建立；没有来源时不新增槽位。
- 五张牌加入编译器的“需要生物／能力布局”拒绝列表，抽弃牌专用域（`includeAttacks: false`）继续显式拒绝；附魔、异常关键字、永恒等未表示实例状态继续拒绝。
- 本批没有实现任何额外效果：`GainsBlock` 只被 `Nimble` 附魔读取，而该附魔仍在拒绝集合内；没有新增 Hook、选牌、生成或 RNG 语义。

## 已取得的证据

最终产物 `.local/compact-doom-cards-20260912/artifact`（与仓库 Release 构建同一源码）。全部请求 120 秒上限、Instant、SILENT 之外的建局由测试内部控制。

| 场景 | runId | 耗时 | 证据 |
| --- | --- | ---: | --- |
| `COMPACT-DOOM-CARDS-NATIVE` | `5193feebe1d947ad8de1015470d0ae60` | 7.30 s（复用进程） | `.local/compact-doom-cards-20260912/cards` |
| `COMPACT-DOOM-CARD-KILL-NATIVE` | `d7b250f96c4c4f15956f4c05645dea31` | 24.44 s | `.local/compact-doom-cards-20260912/kill` |
| `COMPACT-CALL-OF-THE-VOID-GENERATION`（普查回归） | `56bd91d7145b432ab3a82d3d7c5ad1c5` | 3.61 s（复用进程） | `.local/compact-doom-cards-20260912/void` |
| `COMPACT-GENERATION-CLOSURE-AUDIT`（`FullRootExplicitlyRejected`） | `a1a4963b61784a0485fd969a8cb2f24e` | 3.47 s（复用进程） | `.local/compact-doom-cards-20260912/closure` |

- `COMPACT-DOOM-CARDS-NATIVE`：两种升级各一条六步出牌路线、8 个原生动作、14 分支、4 个省略选择边界。生产编译器、紧凑 lane、物化投影与实机在同一批动作上逐对比：毁灭 21/26+7/11+13/16、易伤与虚弱的施加顺序、消耗与虚无结果位置、Scourge 抽牌数量、Fear 先生成攻击再施加易伤、以及后续攻击消费 1.5 倍易伤、每个完整回合只在敌方阵营结束递减持续时间。全部状态键、估值、能力元数据、九条 RNG、逆序读取、八工作区与实机后冻结根一致。
- `COMPACT-DOOM-CARD-KILL-NATIVE`：实机打出 `DEATHBRINGER` 后敌人仍存活，只有真实 `BeforeSideTurnEnd` 才让生命低于等于毁灭层数的敌人直接死亡；保留格挡、没有伤害历史、能力退休与终局锁定与模型／物化两侧完整一致，撤销不残留死亡状态。
- `COMPACT-DOOM-ROSTER-NATIVE`（`3c3af9d6368e4441b6f893b1d9b33fe6`，3.66 秒，复用进程；证据 `.local/compact-doom-roster-20260912/final-roster`）：单人敌人根无法区分 `AllEnemies` 与 `ChosenEnemy`，该场景在三个存活主敌人加已捕获奥斯蒂的名单上逐步执行 `DEATHBRINGER`、`NEGATIVE_PULSE`、`SCOURGE(目标 2)`、`PUTREFY(目标 1)`、`FEAR(目标 3)`。紧凑 lane 的 `PowerChange` 事件逐条比对（毁灭 21→三名敌人、虚弱 1→三名敌人、格挡 5 后毁灭 7→三名敌人、毁灭 13→仅目标敌人、虚弱 2 与易伤 2→仅目标敌人、攻击 7 后易伤 1→仅目标敌人），每一步的物化结果与读视图都先与 lane 一致，再与实机打完同一动作后的完整快照一致；撤销与实机后冻结候选重放一致。该场景区分了“每条指令整轮名单”和“每个目标全套指令”，而单敌夹具区分不了。
- 纯值 `tools/CompactCreatureChecks` 新增 `COMPACT_DOOM_VULNERABLE_CHECKS_OK`（24 项全通过）：指令域拒绝、两条原生命令的名单顺序与施加历史、人工制品整条拦截、易伤倍率、同牌内攻击顺序、敌方阵营结束的持续时间递减、包含等号的毁灭阈值、格挡保留、撤销与八工作区。
- Release 构建 0 警告／0 错误（5.27 秒）；`./tools/verify-refactor-boundaries.sh` 通过（96 个 Search 文件），并新增结构断言锁定“每条 Power 指令完成整轮名单”的编译形状。

## 闭包普查变化与仍未完成

- 亡灵角色池仍为 78 个冻结候选，没有缩池：可精确编译从 18 增到 **23**，不可表示从 60 降到 **55**，首个不可表示候选仍是 `BANSHEES_CRY`。审计的“类型集合成员资格”口径从 20 增到 25。
- `FullRootExplicitlyRejected` 继续成立：原始 38 牌、19 件注入遗物、两瓶药的完整亡灵根仍在首个未迁移药水处被拒绝，本批没有因此准入任何完整根，也没有启动搜索。
- 仍未完成：剩余 55 个候选类型、未迁移遗物／药水、AEONGLASS 及原始亡灵完整输入；生成池模板的正向执行路径仍只在纯值层与拒绝路径上取证。没有运行 Steam、安装、打包、发布或远端操作。
- 失败基线保留：首次 `COMPACT-DOOM-CARDS-NATIVE` 把 Scourge 的抽牌数写死为 1（升级版抽 2 张），修正后通过；`COMPACT-DOOM-CARD-KILL-NATIVE` 先因对全体目标牌传入单体目标被值层拒绝、再因模型 oracle 缺少终局安全点而评分相差一个胜利常量，两处都是夹具错误而非模拟偏差。

本批没有新增性能比较。请求耗时含建局与复用进程，不作加速结论。
