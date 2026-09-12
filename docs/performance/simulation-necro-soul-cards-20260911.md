# 亡灵核心牌与灵魂生成的生产编译准入（2026-09-11）

本批把亡灵池中的八张原生卡（`BURY`、`REAP`、`PARSE`、`POKE`、`REANIMATE`、`PULL_AGGRO`、`GRAVE_WARDEN`、`REAVE`）逐张定向反编译后迁入 `CompactCardProgramCompiler`。八张牌的完整 `OnPlay` 都能由既有指令域精确表示，所以全部准入；其中灵魂生成引入了“随机插入 + 升级模板 + 原生终局门禁”这一新的指令形状。没有放宽未知语义、没有按卡名回退，也没有缩小 CallOfTheVoid 的 78 候选池。

## 逐张语义（本机 `sts2.dll` 定向反编译）

| 卡牌 | 原生 OnPlay | 编译指令 | 升级 |
| --- | --- | --- | --- |
| `BURY` | 4 费攻击，`AnyEnemy`：单次 `DamageCmd.Attack(52)` | `AttackTarget(52)` | 伤害 52→63 |
| `REAP` | 3 费攻击，`AnyEnemy`：单次攻击；规范关键字 **Retain** | `AttackTarget(27)` + `Retain` 标记 | 伤害 27→33 |
| `PARSE` | 1 费技能，`Self`：`CardPileCmd.Draw(Cards)`；规范关键字 **Ethereal** | `Draw(3)` + `Ethereal` 标记 | 抽牌 3→4 |
| `POKE` | 0 费攻击，`AnyEnemy`：`Osty.CheckMissingWithAnim` 未缺失时 `Attack(OstyDamage).FromOsty(owner.Osty…)` | `PetAttackTarget(6)` | 伤害 6→9 |
| `REANIMATE` | 3 费技能，`Self`：`OstyCmd.Summon(20)`；规范关键字 **Exhaust** | `SummonPet(20)` | 召唤 20→25 |
| `PULL_AGGRO` | 2 费技能，`Self`：先 `Summon(4)`，再 `CreatureCmd.GainBlock(7)` | `SummonPet(4)` → `GainBlock(7)` | 召唤 4→5、格挡 7→9 |
| `GRAVE_WARDEN` | 1 费技能，`Self`：先 `GainBlock(8)`，再把 1 张 `Soul` 随机插入抽牌堆 | `GainBlock(8)` → `GenerateCards(1, Soul, RandomDraw)` | 格挡 8→11（灵魂数不变） |
| `REAVE` | 1 费攻击，`AnyEnemy`：先攻击，再生成 1 张 `Soul`（升级时对每张执行升级命令）随机插入抽牌堆 | `AttackTarget(10)` → `GenerateCards(1, Soul[+1], RandomDraw)` | 伤害 10→13、灵魂升为升级版 |

关键所有权与时序：

- `Poke` 的伤害来自真实宠物：`AttackCommand.FromOsty` 把 `Attacker` 设为奥斯蒂生物（并校验 `osty.Monster is Osty`），所以力量与攻击者侧修正都取宠物值；宠物缺席或死亡时 `Osty.CheckMissingWithAnim` 让整条命令不执行。紧凑指令 `PetAttackTarget` 以宠物为 dealer，并在宠物不在场或已死亡时跳过，与 `Snap`／`Unleash` 走同一条已验证路径。本轮补上该跳过语义的原生差分：缺席根在准入时拒绝且原生 Poke 为空操作、已死捕获宠物上的 Poke 合法但整条命令跳过（无伤害、无攻击完成历史）、`Reanimate` 复活同一身份后 Poke 由真实宠物施伤。
- `Reap` 的 Retain 与 `Parse` 的 Ethereal 都是规范关键字：它们不属于 `OnPlay`，而是进入实例关键字状态。紧凑值层在玩家回合末 flush 时保留 Retain 牌、在 `EndHandEffects` 把 Ethereal 牌送入消耗堆；这两条分别由专门的回合末夹具与纯值回合合同覆盖。
- `GraveWarden`／`Reave` 的灵魂生成走原生 `CardPileCmd.AddGeneratedCardsToCombat(..., PileType.Draw, owner, CardPilePosition.Random)`：每张牌一次生成历史、一次 `Add`，随机落点消耗 `RunState.Rng.Shuffle.NextInt(count + 1)`，空抽牌堆也消耗一次。紧凑指令沿用 `Dirge`／`CaptureSpirit` 已验收的 `Placement.RandomDraw` 形状。
- `Reave` 升级时对每张生成牌执行升级命令，但该命令在战斗已进入结束阶段时**直接返回**：`CombatManager.IsEnding` 在最后一个主敌人死亡当刻即为真，而牌堆插入的结束门禁同时拒绝入堆，于是这张灵魂只留在生成历史里且保持未升级。紧凑指令因此为升级版 `Reave` 携带独立的“结束变体”模板，而不是把一种变体折叠到两条路径上。

## 准入闭包与未表示项

- `CardInstruction` 新增 `EndingCardTemplate`（缺省 `-1`）。只有生成指令可以携带它，两种变体必须不同，主模板与结束变体都必须落在生成槽区间 `[rootCardCount, definitions.Length)`（根牌索引与越界索引都在 `ResumableDiscardProgram` 构造期拒绝，结束变体的这半个区间由本轮评审补齐）；执行器在 `Ending` 为真且存在结束变体时按变体建立实例身份，否则沿用主模板。这是对原生升级命令结束门禁的精确表达，不是近似。
- `CompactDiscardProjection` 的灵魂模板捕获扩展为：`GraveWarden`／`Reave`／`CaptureSpirit`／未升级 `Dirge` 需要普通模板，升级 `Dirge`／升级 `Reave` 需要升级模板（升级 `Reave` 同时需要普通模板作为结束变体）；模板仍按“生成与升级观察者缺席”的既有前提折叠最终变体。
- 兼容投影的 OnPlay 方法作用域改为按“推断镜像 + 补全表生成”的形状：卡牌生成从不属于推断镜像，而是由旧补全表在推断作用域结束后执行，因此投影在该生成事件处结束 OnPlay 作用域。原实现只对 `CloakAndDagger` 生效，现在对所有 `MethodMirrorIncomplete` 生成者一致。
- 旧模型链的 `Reave` 补全修正为在分支 `IsEnding` 为真时跳过升级（原生升级命令的门禁），不再无条件升级生成的灵魂；该修正只读取分支状态，不读实机。
- 八张牌加入编译器的“需要生物／能力布局”拒绝列表，抽弃牌专用域（`includeAttacks: false`）继续显式拒绝；永恒等未表示实例状态继续拒绝；两张模板依赖牌在没有捕获生成闭包时明确拒绝。
- 本批没有新增 Hook、选牌、递归或 RNG 写入语义：`ModifySummonAmount`、`AfterSummon`、`AfterOstyRevived`、`AfterCardGeneratedForCombat`、`AfterCardEnteredCombat` 都已在该闭合域内且没有额外观察者。

## 已取得的证据

最终产物 `.local/compact-necro-soul-cards-20260911/artifact`（与仓库最终 Release 源码逐字节一致）。全部请求 120 秒上限、Instant，建局由测试内部控制。本轮评审补测使用独立产物 `.local/next-necro-review/artifact`（`CombatSolver.dll` SHA-256 `584a7061ddc8acdfd0af7bd4697c953d87a8d888eaa2c05e427b46d4060f8f4a`，与仓库 Release 构建输出逐字节一致）。

| 场景 | runId | 耗时 | 证据 |
| --- | --- | ---: | --- |
| `COMPACT-NECRO-SOUL-CARDS-NATIVE` | `99a1dffc4d284ae086241c355559f5bd` | 7.85 s（复用进程） | `.local/compact-necro-soul-cards-20260911/final-cards` |
| `COMPACT-NECRO-HAND-END-NATIVE` | `96061cdd3a914638b09bd5bf69945b3e` | 3.95 s | `.local/compact-necro-soul-cards-20260911/final-hand-end` |
| `COMPACT-REAVE-TERMINAL-NATIVE` | `03a7074467944531afea88b0539a271f` | 3.66 s | `.local/compact-necro-soul-cards-20260911/final-reave-terminal` |
| `COMPACT-POKE-PET-STATE-NATIVE`（本轮评审补测） | `8afb816dd7b04e839f724726a8aae7a0` | 24.67 s（含建局与进入遭遇） | `.local/next-necro-review/poke-pet-state` |
| `COMPACT-GENERATION-CLOSURE-AUDIT`（回归） | `e9d62783e1594f1e83300820f0cfa5ca` | 24.10 s | `.local/compact-necro-soul-cards-20260911/final-closure` |
| `COMPACT-CALL-OF-THE-VOID-GENERATION`（普查回归） | `0a57c23ed22c4fe2bf380dd6dbc960c5` | 3.61 s（复用进程） | `.local/compact-necro-soul-cards-20260911/final-void` |
| `COMPACT-CARD-HOOKS-NATIVE`（投影作用域回归） | `00ce3062a7ec4b44861bf51212ee2cb8` | 10.71 s | `.local/compact-necro-soul-cards-20260911/final-card-hooks` |
| `COMPACT-DOOM-CARDS-NATIVE`（上一批回归） | `f5d4979c0f7a46dc816331d72996c876` | 7.06 s | `.local/compact-necro-soul-cards-20260911/final-doom-cards` |
| `COMPACT-DOOM-ROSTER-NATIVE`（三敌目标域回归） | `c16f73aa73e944279bf479aa65aaa624` | 24.19 s | `.local/compact-necro-soul-cards-20260911/final-doom-roster` |

- `COMPACT-NECRO-SOUL-CARDS-NATIVE`：两种升级各一条八步出牌路线加两个完整回合、10 个原生动作、14／15 分支、3 个省略选择边界。生产编译器、紧凑 lane、物化投影与实机在同一批动作上逐对比：Bury／Reap 的单体攻击与力量加值、Reap 的 Retain 定义、Parse 的精确抽牌数与 Ethereal 定义、Poke 的宠物 dealer（用宠物 2 点力量而非玩家 11 点）、PullAggro 的“先召唤后格挡”事件次序、Reanimate 的召唤量与消耗结果牌堆、GraveWarden／Reave 的随机插入消耗一次洗牌流并生成普通／升级灵魂变体，以及攻击开始计数。全部状态键、估值、能力元数据、九条 RNG、逆序读取、八工作区与实机后冻结根一致。
- 同一产物的更早两次运行（其中一次只差编译器注释，另一次只差测试检查标签）也全部 Passed：`66af117d9b6442f985dc3cced9d2fdaa`／`b91ebd745bd64e1dae6230ad7421bc9d`（本场景）、`38458f1d96ec4732bf21ccd6976f5ca5`／`c11d157be1274ced885df4f51841b3eb`（回合末）、`eff78d8b935448e8a8a595e458068009`／`7cadef54057c48d1a98c99472d53c78d`（终局）、`8a543a0eb17b4e6b9bb2659b6a9db4dc`／`77b2983e031a4e97ad4bf27da6695e1b`（普查）、`5ae5bfb6b17347b299c5164a90570c40`／`e861317d046344bfb8f948d9b73c2646`（审计）、`4391230fa7a6432aa2197761de626d0d`／`e85739a1a1804adeb3ffbce1dcffe575`（投影回归）、`0f935413ee374537b2cacc1e1661c4e6`／`69151074bcae4d1ba3911d07494bb1c0`（上一批回归）。最终产物与仓库 Release 构建逐字节一致（`3374b054ef82489b627a6c62695bc5ceb4c19a994b65bfff2808bf378dec7404`）。
- `COMPACT-NECRO-HAND-END-NATIVE`：手牌只留未打出的 `REAP` 与 `PARSE`，走一次真实玩家结束回合。flush 后手牌恰好剩一张 Retain 牌（攻击指令 27 的 `Reap`），`Parse` 进入消耗堆；完整状态键、估值、RNG、八工作区与实机同一回合后的快照一致。
- `COMPACT-REAVE-TERMINAL-NATIVE`：升级 `Reave` 的自身攻击击杀最后一个主敌人。紧凑 lane 落到胜利终局、敌人离场、生成身份进入 `Unplaced`、洗牌流未消耗；物化投影的未入堆牌指纹（升级等级）与实机同一动作生成的灵魂一致，实机在结束窗口确实没有升级该灵魂——这条直接证明结束变体模板不是过度设计。
- `COMPACT-POKE-PET-STATE-NATIVE`（无宠物 `SILENT`／MECHA_KNIGHT_ELITE、Cards=[]、敌人 300 HP、Instant）：三种捕获宠物状态各走一次真实动作。缺席时同一根被生产准入拒绝（`Compact summoning requires a captured pet identity`），原生 Poke 作为合法空操作留下敌人 300 HP／3 格挡、0 条生物攻击历史、0 条受伤历史且未创建宠物；已死宠物由夹具真实击杀后再捕获，Poke 打出后紧凑 lane 无 `Damage`／`AttackFinish` 事件、原生攻击与受伤历史不变、敌人仍 300／3（1 原生动作、1 分支）；`Reanimate` 复活同一身份（20／20）后的 Poke 由宠物实际施伤，原生 `CreatureAttackedEntry` 的 actor 即该宠物、敌人 300→297（3 格挡＋3 生命损耗），紧凑事件 dealer 为宠物槽（2 原生动作、2 分支）。两条路线逐动作比对完整状态键、估值、能力元数据、九条 RNG、逆序读取、八工作区与实机后冻结根。
- 纯值 `tools/CompactCreatureChecks` 新增 `COMPACT_SOUL_GENERATION_CHECKS_OK`：结束变体选择（普通模板）、非结束主模板（升级模板）、随机插入消耗、空落点位置、未入堆身份与无 RNG 消耗、撤销、八工作区，指令域拒绝（非生成指令带结束变体、两种变体相同），以及真实构造器对根牌索引与 `definitions.Length` 两个结束变体索引的拒绝。输出尾部为 `domain_rejections=true ending_template_range=true`。
- Release 构建 0 警告／0 错误；`./tools/verify-refactor-boundaries.sh` 通过（96 个 Search 文件）。

## 闭包普查变化与仍未完成

- 亡灵角色池仍为 78 个冻结候选，没有缩池：默认模板口径下可精确编译从 23 增到 **29**，不可表示从 55 降到 **49**，首个不可表示候选仍是 `BANSHEES_CRY`；`COMPACT-GENERATION-CLOSURE-AUDIT` 的“类型集合成员资格”口径从 25 增到 **33**（CallOfTheVoid 池 78 个候选）。
- 需要捕获生成模板才算准入的候选从两类增到四类：`DIRGE`、`CAPTURE_SPIRIT`、`GRAVE_WARDEN`、`REAVE`。供给模板后 78 池可编译 **33**、不可 **45**；该第二口径只描述“编译器在有模板时的表达力”，不代表生产根可以跳过模板捕获。
- `FullRootExplicitlyRejected` 继续成立（原始 38 牌、19 件注入遗物、实际 20 件遗物与两瓶药的完整亡灵根仍在首个未迁移药水处拒绝），本批没有因此准入任何完整根，也没有启动搜索。
- 仍未完成：剩余 45 个默认口径候选类型、未迁移遗物／药水、AEONGLASS 及原始亡灵完整输入；生成池模板的正向执行路径仍只在纯值层与拒绝路径上取证。没有运行 Steam、干净安装、打包、发布或远端操作。
- 失败基线保留（全部为夹具或旧模型偏差，不是紧凑值层偏差）：
  1. 首次普查断言把“供给模板后新增准入”写成 +2，实际为 +4（`DIRGE`、`CAPTURE_SPIRIT` 也依赖模板），修正为四类集合。
  2. 首条路线的 `Reap` 断言错误地要求“打出后仍在手牌里”，Retain 只在回合末 flush 生效，改为断言打出实例的定义与结果牌堆。
  3. `Parse` 同类错误：抽牌后牌已离开手牌，改为从打出实例读取定义。
  4. `GraveWarden`／`Reave` 首次原生差分在“生成历史的方法作用域”上不一致：旧补全表在推断作用域结束后生成，投影当时仍留在 OnPlay 作用域内；修正投影作用域形状并加 `COMPACT-CARD-HOOKS-NATIVE` 回归。
  5. 回合末夹具最初带回合遗物，`ToolsOfTheTrade` 的回合开始弃牌会合法地丢掉被保留的牌，导致断言不稳；改用无回合遗物的夹具。
  6. 结束变体首次实现按实例写入主模板索引（`CardInstanceValue(template)`），导致未入堆身份仍是升级版；改为写入实际选择的定义索引。该失败由 `COMPACT-REAVE-TERMINAL-NATIVE` 捕获。
  7. 旧模型链 `Reave` 补全无条件升级灵魂，与原生结束门禁冲突，物化投影对账在未入堆身份处失败；修正旧模型为按分支 `IsEnding` 跳过。
  8. 结构门禁先因编译器注释里出现原生命令字面量而失败（`card admission must compile definitions without executing effects`）；只改写注释，未修改门禁。
  9. 结束变体的构造期范围检查原先只拒绝 `>= definitions.Length`，根牌索引会被当作生成变体；`SoulGenerationChecks` 加入真实构造器用例后补齐为与主模板相同的生成槽区间，保留 `-1` 哨兵。
 10. 首个宠物状态夹具用亡灵开局，`BoundPhylactery` 在战斗开始即召唤奥斯蒂，缺席根无法成立；改用 `OSTY-STATE-LIFECYCLE` 的无宠物 `SILENT`／机械骑士夹具，未放宽任何准入。

本批没有新增性能比较。请求耗时含建局与复用进程，不作加速结论。
