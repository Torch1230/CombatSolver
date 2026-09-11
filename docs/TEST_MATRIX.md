# CombatSolver 测试清单
- 命运同担三根两回合原生对照 Passed：21 原生动作／42 分支／18 挂起、双方人工制品与负力量重获；完整状态、键、续用、九 RNG 及八工作区。[逐项范围与 runId](performance/simulation-shared-fate-20260911.md)。
- 神气制胜独立实例验证：三根 24 原生动作／45 分支／18 挂起、六回合及 250 节点旧新／串并行；独立施加、两类末击和玩家死亡按原生完整状态对照。[逐项范围与 runId](performance/simulation-panache-20260911.md)。

- 致死性与历史课窗口验证 Passed：27 原生动作、60 分支、21 挂起、九回合及 250 节点旧新／串并行；历史课两原生自动阶段对照覆盖根前攻击与空窗口。完整状态、键、续用、九 RNG、八工作区和冻结根一致。[逐项范围与 runId](performance/simulation-lethality-20260911.md)。

- 出牌前能力与迅速六项验证 Passed：三根两回合、嵌套 Sly、满手／零层／已失效、终局、死亡之舞原生费用基线修复与 250 节点旧新串并行。完整状态／来源／估值／键／续用及八工作区一致；纯值 72 组编码和生成 X 暂停合同通过。[逐项 runId 与范围](performance/simulation-card-hooks-20260911.md)。

- 动态关键字六项验证 Passed：雕琢打击／响指三根两回合、生成牌、空过滤、两种终局与250节点旧新串并行；同名牌实例、X／费用、完整状态／续用及八工作区。[逐项 runId、范围与失败基线](performance/simulation-keywords-20260911.md)。

- `COMPACT-HANG-NATIVE`／`HANG-CAP-NATIVE`／`COMPACT-HANG-TERMINAL`／`COMPACT-HANG-SEARCH`：`b4e7e3f4b225421e8bb208cfe8582239`／`0eb616cb7eea425daf617758199a573b`／`2067198c357a4a938e88df85cc4c6617`／`07239771f5f34edb9d623a896e207aa8` Passed；两根16原生动作28分支8挂起、四组封顶、最后击杀及250节点串并行，完整状态／原键／续用／九RNG／八工作区。[范围、失败迭代与复现](performance/simulation-hang-20260911.md)。

- `COMPACT-FULL-ROUTE-NATIVE`：`c42c12e1eb8b42ddbdf6cbe6b717bc80` Passed；原始 30 牌／31 监听器，44 原生动作、424 已准入替代分支，第 8 回合胜利、59/65 HP。完整状态／键／历史／Power／九 RNG／逐实例牌堆、双工作区和原生结束后冻结重放；原正式搜索生成路线，尚非紧凑生产搜索。[范围与复现](performance/simulation-full-route-20260911.md)。 六个适配文件移入 Prediction 后，代表请求 `ec8fa69959f742e4b8c1390566b2fc0a` Passed，完整路线取证对象与迁移前相同。

- `COMPACT-ROUND-NATIVE`：`43aec28b796547a7b3d107003021bd01` Passed；3 根／20 分支／7 原生动作（6 次完整结束回合），覆盖起手洗牌、Tools、临时／永久 Sly、Power、AI、生成牌、保留与敌方中毒终局；完整状态／历史／原键／估值／九 RNG、冷根历史及八工作区恢复。[证据](performance/simulation-rounds-20260911.md)。

- `COMPACT-MONSTER-AI-NATIVE`：`b7de315897af4c2782ce92a17f5c3cd7` Passed；`--character-id SILENT --encounter-id MECHA_KNIGHT_ELITE --enemy-current-hp 300 --cards-json '[]'`，3 根／10 分支／16 原生动作，完整 AI／状态／历史／九 RNG／估值／原键、逆序恢复及八工作区；纯值 300 次增长日志。原生侧调用 PerformMove 和 RollMove，尚非完整回合。[证据](performance/simulation-monster-ai-20260911.md)。

- `COMPACT-POWER-PHASES-NATIVE`／`COMPACT-SHUFFLE-POWER-VALUES-NATIVE`：`1c179c6437ae46a0b09144cbe6de666a`／`d73115d6b55b4602810ee3e921a4353b` Passed；五路线／22 前缀／16 原生阶段及出牌步骤，完整 Power 字段、倒计时／恢复／重获、原键／全估值／九 RNG／八工作区；洗牌 24 分支与原生两回合对照（回合边界仍由旧引擎执行）。[阶段证据](performance/simulation-power-phases-20260911.md)。

- `POWER-DURATION-KEYS-NATIVE`／`POWER-DURATION-APPLICATION-NATIVE`：最终 `683bb123f72d4b5f86ac5fdfaf2a8bc8`／`54cd9ef622764de4845909b9f8abc3ad` Passed；三种减益同层不同未来的键与续用区分、中毒等价，三个应用入口／16 原生步骤、叠加／递减／重获／人工制品阻止、完整 Power 字段和冻结重放。[失败基线与最终证据](performance/simulation-duration-state-20260911.md)。

- `COMPACT-MONSTER-COMMANDS-NATIVE`：`1d46526236cc45f2bdb2ca0beee0600e` Passed；三根／十三分支／十五原生动作，四种机械骑士指令、伤害与格挡修正、施加者、无创建者生成、溢出洗牌抽灼伤、小刀混合与致死结束门，完整历史／原键／Snapshot／九 RNG 和八工作区。中毒哨兵 `4bcff2265a254ddca7fa26e9d6c30bcd` Passed；[范围与证据](performance/simulation-monster-commands-20260911.md)。完整回合与 AI 推进仍待迁移。

- `COMPACT-HAND-END-NATIVE`：最终 Instant `537ef792140f40368dc7b30b0b328a00`／Normal `a80cf738cde74758a4ceabbf6b01905e` Passed；共六根／十二分支、八工作区、虚无先消耗、状态抽取、格挡／失血／致死、出牌区和完整历史／原键／Snapshot／九 RNG、原生死亡后冻结重放。攻击哨兵 `92409a0ec76245eaad053b899a7f91b2` 通过；[调度边界与证据](performance/simulation-hand-end-20260911.md)。完整回合与生产模式接线仍待迁移。

- `PLAYER-DEATH-POWERS-NATIVE`（`9215d87712ed4b01befaae18c76ee62b`）Passed：灼伤致死后三个 Power 清理、完整状态、根／兄弟隔离、列表／单目标伤害的反向 live／分支存活状态及待失败→Defeat。保留两条失败证据，[报告](performance/simulation-player-death-20260911.md)。

- `COMPACT-FULL-ROOT-NATIVE`（`7ef49b1ef6de4b56a82c6e6c180754b3`）Passed：原始机甲 30 牌／31 牌组监听器／蛇之戒，9 分支／八工作区／六次原生出牌；完整 Snapshot、键、历史、九 RNG、缓存开关和原生推进后根隔离一致，仅首回合行动边界。[证据](performance/simulation-full-root-20260911.md)。

- `COMPACT-ARTIFACT-NATIVE`（`10f13f49a6a24b03bd3d61bba40c19f2`）Passed：两根／15 分支／八工作区／九次原生出牌，首次／叠加临时效果阻止、负属性、X=0、正增益、非默认字段／顺序／退休和死亡清理，完整 Snapshot／原键／历史／九 RNG。纯值合同及普通 Power／费用哨兵 `7cbc93b5ccd9442496f85ec7e51df4fd` 通过，[证据](performance/simulation-compact-artifact-20260911.md)。

- `COMPACT-RANDOM-COSTS-NATIVE`（`98dee34da8de40eb8313653b87ddb9e9`）Passed：两根／13 分支／八工作区／九次原生出牌、三次重抽／付款、完整修饰前缀／列表、满手门、Shuffle 和 CombatEnergyCosts 五字段、全部 Snapshot／原键／九 RNG 及原生推进后的根隔离。纯值 130 次重抽通过；附魔生成 `22d19af3b151495cba380e6873724b0c` 和选择链 `ea9a51de7d294b9cad9405a57c5e9d1e` 同产物通过，[证据与边界](performance/simulation-random-costs-20260911.md)。

- `COMPACT-INKY-CARDS-NATIVE`（`942a23f0b0e1423d9ea58cd91f04adbd`）Passed：两根／13 分支／八工作区／八次原生出牌，普通与附魔生成混合、满手溢出、洗牌、消耗及目标死亡后跳过虚弱，完整 Snapshot／原键／历史／逐卡指纹／九 RNG；[证据与模板折叠条件](performance/simulation-inky-cards-20260911.md)。

- `COMPACT-GENERATED-CARDS-NATIVE`（`230fb3cfae674da49a61751b4f23da55`）Passed：两根／12 分支／八工作区／九次原生出牌，生成次序、满手溢出、小刀消耗／洗牌／死亡、全部 Snapshot／原键／历史／九 RNG；300 实例、索引截短复用的纯值合同通过。原选择链 `57b5d857e4354a63b1022738090d8fbf` Passed，成本与迭代失败见[报告](performance/simulation-generated-cards-20260911.md)。

- 索引缓冲区合同 Passed：三列表／800 步交错分配、64／2048／65536 边界、八工作区、事件完整 32 位字段；512 次执行中穿插领域槽位。最终 `COMPACT-EFFECT-PROGRAM-NATIVE`（`70f16b4d11984d1c98d46718324c2034`）和中毒终局 `24a06feb94e54f17aa05414434226290` Passed；同 JIT 原产物成本请求 `2321744580d545658aa1abec3536982c` 保留作对照，[额外成本与证据](performance/simulation-indexed-buffer-20260911.md)。

- `COMPACT-TEMPORARY-STRENGTH-NATIVE`（`73cfb42540b941beb3603a22310c1712`）Passed：两个原生根／9 分支／八工作区／五次出牌，尖啸首次、叠加封顶、力量退休重获、Power 非默认字段与敌人死亡，完整 Snapshot／原键／历史／九 RNG。共用 helper 的下回合计数哨兵 `b171994244b54caaafb6e479dca94365` Passed；侧回合末仍由旧引擎与原生定向回调对照，[证据](performance/simulation-compact-temporary-strength-20260911.md)。

- `TEMPORARY-STRENGTH-ORDER-NATIVE`（`f2309b7ed0b1467e87d45a4d77f741ac`）与 `TEMPORARY-STRENGTH-CAP-NATIVE`（`fdcb17d80e1b4106870c33f5a98f04fb`）Passed：两条失败基线对应顺序与封顶偏移；最终覆盖尖啸、Artifact、+5／−2／−3、已有封顶与首次超上限、完整 Power 顺序／快照、Fork 和原生侧回合末恢复。普通 Power／中毒哨兵 `d44af93c1ae845358d699f44961cb116` 通过；[产物范围与证据](performance/simulation-temporary-strength-20260911.md)。

- `COMPACT-DEFERRED-POWERS-NATIVE`（`6f3117a761c04843a78aa6aae746b538`）与新增 Power 槽后的中毒／表达式哨兵（`0ea7d01ece354b858df12471eb6ba5d4`）Passed：三个原生根、16 分支／八工作区、上限与小数返回、必备工具计数和移除；完整状态／历史／原键／Snapshot。兑现由旧引擎与原生定向触发 `AfterBlockCleared`，不是紧凑跨回合；[证据](performance/simulation-compact-deferred-powers-20260911.md)。

- `DEFERRED-BLOCK-RETURN-NATIVE`：失败基线 `f061e289ecc44478bb7e2755653b3b05`（下回合格挡 1／3），最终 `c06b5d6fb97443589aea01f62f52672b` Passed。三个原生根覆盖上限与小数、叠加、零值，完整快照／Power 元数据、Fork 隔离和定向 `AfterBlockCleared`；[证据](performance/simulation-deferred-block-return-20260911.md)。

- `COMPACT-POWER-EXPRESSIONS-NATIVE`（`fbb8b2f392274928b5d27bc48ed4e9b8`）Passed：13 分支、八工作区、原生十步；中毒存在／归零／重获、存活敌人求和、基础值与倍率、敏捷／脆弱取整、升级消耗、完整 Snapshot／原键／历史／九流 RNG 及终局清理前状态。纯值工具与 Linux 门禁通过；[失败夹具记录与最终证据](performance/simulation-power-expressions-20260911.md)。

- `COMPACT-POISON-TRIGGER-NATIVE`（`22e557b94d464cdc9b0e45713ff8be85`）、`COMPACT-DISCARD-DRAW-NATIVE`（`a7496c81ae34441987e89f369789116a`）、普通抽牌哨兵（`3e816af38b8c4ef196ec034482652464`）和 `COMPACT-POWER-REACQUIRE-NATIVE`（`c16dead0c81c4818bf9996b6ecf898b1`）Passed：中毒 11 分支／五步击杀与清理前终局、24 弃抽分支／重新抽到的 Sly、空手牌、非默认回合初始值／计时／Target 重获、八工作区、完整状态／历史／Snapshot／原键。纯值合同与结构门禁通过；[证据与边界](performance/simulation-poison-discard-20260911.md)。

- `COMPACT-CONDITIONAL-POWERS-NATIVE`（`0dda50623c92431687817928bbf2744f`）与 `COMPACT-DRAW-RETURN-NATIVE`（`e318d438c9d84b0fab75d17b48db46d8`）Passed：14 分支、八工作区、原生九步和四种原生抽牌根；群体中毒／虚弱顺序、条件格挡、固定施加者／死亡清理、虚无历史、全部 Snapshot／原键／九流 RNG 一致。编译器迁移后的旧生命周期（`2a8ad6be6fd8438bb89415106515af8c`）通过；[失败记录和适用范围](performance/simulation-conditional-powers-20260911.md)。

- `COMPACT-CARD-LIFECYCLE-NATIVE`（`aa9d057433054489875622e192cb566a`）Passed：12 组合分支、六种风险来源、八工作区、原生九步覆盖能力牌移除、X=3／X=0、消耗历史与完整状态／Snapshot／原键。v5 终局缓存条件补充后的攻击哨兵（`7f451fc9bc8a4598875fccf00c3b70eb`）与 v4 嵌套选牌哨兵（`707c28338b8a452ab228ed9111d61b5c`）Passed；[证据与边界](performance/simulation-card-lifecycle-20260911.md)。

- `COMPACT-EFFECT-PROGRAM-NATIVE`（`fd79e13365d845308fff3c6f825b78cf`）Passed：生存者→Sly 早有准备→部分抽牌／洗牌的 24 分支、完整状态／Snapshot／原键／历史／九流 RNG、八工作区及原生后空翻续接。原洗牌链（`df473de5246d45feb750f0510c97bf07`）与 Power／七步击杀链（`277e239f863f424c810cbe8ed161ddfb`）同产物通过。三组纯值工具通过；[证据与范围](performance/simulation-effect-program-20260911.md)。

- `COMPACT-POWERS-NATIVE`（`6b2f26c48d4f4934b8959175a94271c1`）与 `COMPACT-POWER-BOUNDARIES-NATIVE`（`afe4886edcc945278a931a04dd758a31`）均 Passed：21 分支、五种基础 Power、原生七步击杀、八工作区，以及负属性／小数边界三步和旧引擎显式定向 Power。最终 Release `artifact-v8`；原生洗牌哨兵 `18e523f6b1b242ea91acae8073f90bbc`、混合 Power 顺序 `f6160c9671fa4d9785b37b9792edfc16` 均 Passed；[证据和边界](performance/simulation-compact-powers-20260911.md)。

- `COMPACT-ATTACKS-NATIVE`：SILENT／CORPSE_SLUGS_NORMAL，120 秒 headless，最终 `c8bd3d9d609c4023a187a4a0479ce1a9` Passed。18 分支的全状态、完整评分／原键／来源历史，八工作区独立执行与读取，原生五步部分／最后击杀及 teardown 前完整终局对账；[证据与范围](performance/simulation-compact-attacks-20260911.md)。`CompactCreatureChecks` 增补目标、取消撤销、暂停恢复与结束门合同通过；洗牌原生哨兵 `6660eab2ce51440b9dcecb097d7541e1` Passed。

## 2026-09-11：生物值与完整估值读取

- [CompactCreatureChecks](../tools/CompactCreatureChecks/README.md) 九个边界伤害向量、治疗／限幅、跨页撤销及八工作区恢复通过。
- `COMPACT-CREATURE-VALUES-NATIVE` 最终 `f79411f25e10403cab705060cd22d350` Passed，23.64 秒：三敌 16 状态的全部 Snapshot 属性／原键／排序／根恢复，12 个玩家与敌人原生伤害的完整状态。另单列 `SandpitPower` 评分 helper 的 owner／target／amount／分支 HP 读取，未把它计作完整 Power＋离场闭包。既有 24 分支、九 RNG 和两回合洗牌原生 `310ff556730f4086affc538afc587915` Passed。完整原输入、退化样本、失败原因及最终构建／门禁证据见[报告](performance/simulation-creature-values-20260911.md)。

## 2026-09-11：可增长紧凑工作区

- [CompactGrowthChecks](../tools/CompactGrowthChecks/README.md) 八组合同通过：600 步独立列表 oracle、页边界／不同长度恢复、嵌套撤销、八路隔离，以及第 256 次冻结后继续至 512 次执行并逐槽对照。现有结构工具四个候选各 ABBA 的 34 叶／逐槽合同通过；数据包含未保留实验。
- 最终洗牌／战略选择原生 `a198b630720f4cfdb6c5bdac5655bfc1` 和 34 叶原生哨兵 `2de8f6b9cdca477ab8787dda5f2ca17c` 均 Passed。相同 JIT 的旧／新产物成本请求也均通过，完整 ID、成本退化与未迁移范围见[报告](performance/simulation-growable-state-20260911.md)。Release 0 警告／错误，Linux 门禁通过，PowerShell 未运行；测试实例已停止。

## 2026-09-11：卡牌基础估值缓存完整复评（撤回）

- 最小合同 `562da65df4654273bd870a0d5de7a622` Passed，覆盖升级、动态变量、COW 分支、并行读取和不透明模型旁路。两场完整原输入 ABBA 的八次测量均 Passed，88 项逻辑字段和完整路线一致；无预算覆盖或增量验证。约 2% 耗时差异不足以支持加入该缓存，分配略增且 GC 未稳定改善，生产源码与专用测试入口已撤回。具体 runId、命令、全部数据和局限见[报告](performance/simulation-card-value-cache-20260911.md)。

## 2026-09-11：紧凑洗牌／战略选择与估值复用

- `COMPACT-SHUFFLE-POWER-NATIVE` / `caf3dd7642e04d678278d1d30bdc0e5d` Passed，28.20 秒，T1→T2；24 个嵌套选择样本的完整状态／ContinuationStamp、九流五字段 RNG、原键、全部估值属性和历史来源对账；缓存开关一致，卡牌元数据不变，完整撤销，8 worker 从暂停洗牌候选独立恢复。原生再执行后空翻，旧引擎推进回合后，新根紧凑执行防御。已验证效果不等于完整紧凑跨回合、Power 数量变化或死亡。
- 原 34 叶哨兵 `ce69f05dcfd04e2fb4be938848967191` Passed，4.83 秒，使用 v3；完整属性／原键／排序、冷暖 solver、根隔离和下一动作等继续通过。最终 v4 仅进一步缓存类型审计元数据并计入每批准入，未知 Power／临时费用的拒绝由 v4 原生场景覆盖。
- 四组交错对照：含准入 3.31×，分配为旧重放的 19.35%；缓存本身 CPU −9.56%。数据、初次 RNG 测试表达式和目录写出失败、冷初始化成本及边界见[报告](performance/simulation-expanded-chain-20260911.md)／[原始数字](performance/simulation-expanded-chain-20260911.json)。Release 0 警告／错误，Linux 边界门禁通过；PowerShell 未执行，未跑完整战斗、增量搜索、Steam 或生产 Beam 性能。

## 2026-09-11：紧凑完成状态直接读取

- `COMPACT-KERNEL-NATIVE` / `0c14283a61414f3eacb81ebfe7f7f277` Passed，29.60 秒；根与 34 叶全部 Snapshot 属性、原键、投影洗牌／保留排序、卡牌元数据及根不变、外根／挂起拒绝、直接下一动作通过。原完整状态／ContinuationStamp／有序来源历史、冻结恢复的 8 worker 和原生嵌套链继续通过。最终四组交错完整批次约 2.87×，分配为原来的 16.53%，CPU 3× 联合门槛未通过。Release 0 警告／错误，Linux 门禁 `REFACTOR_BOUNDARIES_OK search_files=87`，实例已停止。
- `MIXED-POWER-ACQUISITION-ORDER` / `c6987417df75415fa60154e7f089051a` Passed，4.08 秒，混合 Power／卡牌联动／重获六个稳定边界；`SUMMON-DEATH-POWER-ORDER` / `a48e596bf32c495db7516f36c91016f0` Passed，4.29 秒，T1→T3，原生召唤、击杀、Fork、完整状态和 MonsterAi／Niche 精确 RNG。使用本轮 v2，两个夹具后续没有语义改动；观察器默认关闭，净字段变化不冒充紧凑槽写入或瞬态 journal。
- v1 `f35a810d682d4ac49bc29ba66c288c55` 因遗漏技能集合导致 StateKey 差分失败；v2 `7b18485c879541f7b699cf4c002025c8`、v3 `c811984ef331420088f55fce6c06dca4` 已通过各自启用的断言。v3 进一步覆盖根／外根／挂起拒绝与直接下一动作，之后只将共用牌堆遍历改为索引以消除新增装箱分配；最终受影响夹具重测记在上项。
- Linux／PowerShell 门禁同步；最终验证结果及完整交错样本见[报告](performance/simulation-read-view-20260911.md)和[原始数字](performance/simulation-read-view-20260911.json)。头次门禁指出新的 ReadView partial 未列入白名单，已同步双端职责清单。未执行 PowerShell、完整战斗、增量、可见 Steam 或新后端 DOP 性能测试。

## 2026-09-11：剩余瓶颈评估与紧凑候选存储

- [存储探针](../tools/CompactCandidateChecks/README.md)直接链接内核：七组容量合同、零值与完整有符号值、页边界、嵌套回滚后冻结、活动事务/外根拒绝及 8 worker 复用恢复通过。0/1,024/8,192 历史值、8/512/1,024 核心写入的 128 个保留分支全部逐槽一致；这些是结构负载，不是 Power/RNG/死亡语义。A–B–B–A 固定测量完成，保留分配收益与密集退化；最初测试表达式优先级错误及修正见报告。
- `COMPACT-KERNEL-NATIVE` / `ed3c059cd1a643b0aed19a65ab53597a` Passed，30.22 秒：同一 lane 恢复全部 34 个候选，8 worker 各四次恢复暂停候选；完整状态/ContinuationStamp、原键、Snapshot 全属性与顺序、历史及下一动作续接、原生嵌套链均通过。保持正式通知隔离与 await/原生部署边界。Release 0 警告/错误，未运行新后端整场或 headless 增量验证。
- 当前原生产构建固定 Short 的 DOP8/1/2/4/8 分别为 `0c364b56a13f49c593fbac24b0476023`、`d24e093fddb54e4d934a21345090ad66`、`0f73e4cbd7054e568ac7edc750a74a73`、`d1b1ec9dc87a49fc8f98df13fa666349`、`28001fde029d42779321a25a9609d9c5`，均 Passed。34 项逻辑/质量指标相同，DOP2/4/8 最大并发达到 2/4/8；本轮没有比较全部动作或逐状态。首尾漂移、实际线程 CPU 与采样窗口局限保留，未据此修改调度。
- Linux 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=85`；PowerShell 对应规则同步但未在本机执行。两个实例已停止。完整命令、CPU/分配样本、保留 payload、GC 证据边界及后续进入条件见[本轮报告](performance/simulation-candidate-storage-20260911.md)和[方案账本](performance/simulation-strategy-ledger-20260911.md)。

## 2026-09-11：紧凑估值诊断与模拟上下文校正

- `COMPACT-KERNEL-NATIVE` / `22e98eed11404f2cbb9287d29c7e889a` Passed，30.48 秒。测试与正式搜索共用通知隔离上下文，八任务 await 前与原生部署前显式退出；34 叶完整状态、ContinuationStamp、原状态键、全部 Snapshot 属性、有序历史、冻结续执行和原生嵌套链通过。
- 新增预热 solver / 每 34 叶新建 solver 的全属性与排序对照，四组交错样本，每样本 256×34 叶；24 个唯一威胁缓存键，保留同批重复状态。线程 CPU、墙钟、分配与嵌套内部阶段分开记录。CPU 采样仅纳入诊断栈，不代表完整搜索、可见性能或 DOP8 合同。
- 前两次 `810165f0484549aa98b845ff06769596` / `3612a4288755423e9cf0707c4f1c192b` 也通过其启用的断言，但遗漏正式隔离，第一轮另有计时漂移。它们与前一原型的 1.53× 样本保留为历史，不作为正确上下文的瓶颈依据。最终 Release 0 警告/错误；双端结构规则同步，实际执行 Linux 版。全部实例已停止。详见[报告与全部样本](performance/simulation-evaluation-bottleneck-20260911.md)。

## 2026-09-10：紧凑可恢复执行原型

- `COMPACT-KERNEL-NATIVE` / `c5c109a3766249aa8885298e5ebdc60c` Passed，隔离 headless 总时长 25.26 秒，120 秒上限未扩。30 张真实卡、34 个物理叶子：完整状态/ContinuationStamp、原 StateKey、全部 Snapshot 属性与历史来源配对一致；覆盖嵌套撤销、错误 LIFO/外根/重复消费、取消、冻结候选的八个独立任务与下一手动动作。一个原生嵌套链通过 actual/predicted；不是整场或 DOP8 全搜索验证。
- 同请求内完成预热及四组正反交错固定工作量样本，完整模式包括冻结、旧模型投影、完整 Snapshot 和相同保留排序，CPU 均值约 1.53×、分配下降 4.54%；R2 门槛未通过。纯内核单列，不当作完整后端收益。原完整基准未缩减/运行；洗牌、RNG 写入、生成与跨回合尚未迁移。
- 最终行为源码 Release 0 警告/错误，Linux 门禁 `REFACTOR_BOUNDARIES_OK search_files=85`；PowerShell 规则同步但未执行。三次开发失败及 v3 失败退出时的进程崩溃保留，成功后显式停止拥有的实例。命令、全部样本和范围见[结果报告](performance/simulation-kernel-prototype-20260910.md)及其 JSON。

## 2026-09-10：大幅重构再调研（离线）

- 基于 `6161e74` 源码与已完成的 P5 证据，离线复算四个 B 样本的请求均值、分配/转移；从亡灵 `43544da114824e169031be158500422d` 日志与 Coordinator 调用链核对六次 solver，提取 22 处 StateKey 排序源码行。JSON 中保留来源、原事件和计算口径，详见[调研报告](performance/simulation-redesign-research-20260910.md)。
- 本轮验证仅 L0：新增 JSON、相对链接与差异格式。没有新编译、游戏请求或语义/性能测试；没有实现通用选择续执行、紧凑状态或撤销日志。旧测试不计作本轮通过，3×/分配≤20% 原型门槛与 10× 方向均未验证。

## 2026-09-10：性能重构最终 headless 集成

- 用户明确取消可见测试，以 headless 完成。本轮没有继续启动 Steam；隔离入口使用 `--headless --force-steam=off`。
- 机甲 `170c7f252d1d4aa6b46a2a5f0b586b76`、亡灵 `dbc41031ba1e42e8b6933f1fe62edbf2` 完整原生部署均 Passed：VeryHigh / DOP8 / NoGC16GB，无搜索预算覆盖或增量验证，Instant / 0 秒部署；T7 / T13、实际结束 HP ≥59 / ≥32、零计划外重算。请求总时长 44.17 / 107.88 秒，均在 120 秒内。
- 初始搜索 88 项逻辑字段及完整动作/回合结果/预测和 P5 候选全同；其后 6 / 12 个 `SEARCH_REUSED` 均为 `exact_state_text`，复用 RESULT 的展开与转移为零。日志退役后已停止拥有的实例。完整命令、请求、结果、逐次复用及取消前可见尝试的局限见[最终报告](performance/simulation-refactor-result-20260910.md)。行为构建和结构门禁复用 P5 已通过的相同冻结产物，收口只修改文档，未重跑同一成功行为验证。

## 2026-09-10：P2 / P4 决策诊断

- `1787f9d` 上临时测量 Snapshot / ForkPower / PowerFingerprint 的线程 CPU 与分配，Release 0 警告/错误。机甲 `9305c5edd5844f7188aa8f584a1bbbd4`、亡灵 `c64a0b392e57424e96a06b1dc6492db3` 两场完整正常请求 Passed；88 项逻辑字段及完整动作、回合结果/预测相同。
- 没有短搜、预算覆盖或 headless 增量验证；源码已精确恢复、helper 已移除、拥有的实例已停止。CPU 采样和读钟开销的局限见[P2/P4 决策](performance/simulation-refactor-p2-p4-decision-20260910.md)。本轮不进入这两项 API/存储迁移，不声称通过了未实现的 COW 或待选择结果协议。

## 2026-09-10：P5 回合检查点

- Fork 合同 `9b709fcb76cf48c7979233b42981075b`，原生 Tools / Mayhem `94203d60bb9a4a38b612d400ada429c9` / `fc88e983d1f74a33b9a7cb2eeb8c1b45`，DOP1/DOP2 / 取消 / 失败排空 / 复用 `f9e7de49655940e1a92ea14a9f6fbf07` 均 Passed。七组影子检查点对原完整重放逐分支比动作、状态键、评分、完整状态、RNG/洗牌和逻辑工作；覆盖父子/live 隔离、提前释放与终局不捕获。
- 冻结前 Release 0 警告/错误，Linux 结构门禁 85 个 Search 文件；双端规则同步，未执行 PowerShell。没有 headless 增量验证。
- 两场原完整 VeryHigh / DOP8 / NoGC16GB 的 ABBAABBA 全部完成，每进程相同两场预热、16 次测量，131 项字段分类比较，88 项逻辑字段和 40/85 条完整动作零差异。机甲四对耗时下降 12.87%–18.80%、分配 −14.62%；亡灵四对下降 2.16%–9.52%、分配 −1.52%，同期基线漂移明显。采样 RSS 均值增加、最坏 GC 未改善，完整证据与局限见[P5 报告](performance/simulation-refactor-p5-20260910.md)。完整原生部署已由 P6 headless 补齐，用户取消可见帧验证。

## 2026-09-10：P5 回合阶段职责迁移

- Release 0 警告/错误，Linux 门禁 `REFACTOR_BOUNDARIES_OK search_files=85`。双端门禁同步文件集、阶段入口与禁止结算回流规则；本机没有执行 PowerShell。
- 完整正常机甲场景 `03bc4c371cb04593abef0f4098af2bb6` Passed；VeryHigh / DOP8 / NoGC16GB，无预算覆盖、无增量验证。88 项逻辑字段与 40 条完整动作、7 项回合结果、7 项预测和本批基线相同；请求后退出拥有的实例。
- 仅提取原回合推进及玩家开始阶段，没有新增状态、检查点、原生语义修复或性能结论。原始命令、完整结果和比较保留在 `.local/simulation-refactor-20260910/p5-phase-*`。

## 2026-09-10：模拟性能重构 P3（原型撤回）

- 候选 Release 0 警告/错误；Linux 结构门禁通过（84 个 Search 文件）。ForkBoundaries `27ab4415388d4b0aaa42cbe02eef6e02`，VitalSpark / Galvanic / Smoggy 原版严格差分 `7a6a4da5e99143058161d9d014cb2d90` / `4b6f4fd28e934e61969449d86111b7ac` / `796f720131104c93b7e28f53fe63f9a0` 均 Passed。
- 预定完整 VeryHigh/DOP8/NoGC16GB 的 ABBAABBA，只完成前四个进程的两场各四个测量。全部 131 项 RESULT 字段比较，88 项逻辑字段与 85/40 条完整动作相同；调度、耗时、GC、内存差异另列。A4 遇桌面/Steam 启动干扰，A5 冷预热 `e0f73ea0c63046ac9632fad0673cbd08` 超过 120 秒、无 result.json、进程已停止；不是完成的稳定性能对照，不能把原始均值约 −11% 写成加速。
- 原型及专用测试撤回，未保留生产变更；不追加 DOP/增量/整场部署/可见回归。已做与未做项、全部 runId、GC 与峰值 RSS、命令见[P3 报告和 JSON](performance/simulation-refactor-p3-20260910.md)。

## 2026-09-10：模拟性能重构 P1（临时诊断）

- 诊断版 Release 0 警告 / 0 错误。两场仍使用完整 VeryHigh / DOP8 / NoGC16GB，无预算覆盖或增量验证：亡灵契约师 `147c10b1ade1470181726cde97954d7e`、机甲骑士 `c8a2065b2632499ab50200e8444bf674` 均 Passed，九项工作/终局标量与 P0 相同；不作为逐动作等价或提速证据。
- 记录真实投影签名复用、维护成本、归一化访问与首次入场、逐类型 ForkPower 分配。首场包装脚本日志路径错误发生在请求成功之后，已恢复同次完整计数，保留截断尾行；机甲请求正常退役日志。实例均已停止、临时源码已恢复，详见[P1 报告及数据](performance/simulation-refactor-p1-20260910.md)。
- 最终只提交诊断报告、JSON 和计划状态，执行 L0 路径/链接、JSON 与 diff 检查；P3–P6 尚未完成，不追加未改行为的游戏回归。

## 2026-09-10：模拟性能重构计划（仅文档）

- 新增[分阶段执行计划](performance/simulation-refactor-plan-20260910.md)，约定完整正常配置的交错 A/B、严格差分、Fork/并行合同及最终可见验证。以上均为后续验收要求，不计为本轮通过。
- 本轮仅做 L0 新增链接/路径及 diff 检查，未修改行为源码、重跑游戏、构建或新增性能结论。现有直接采样证据见下节。

## 2026-09-10：0.34.6 极高配置完整搜索热点采样

- 上游 `a6bc386`，Release 构建 0 警告 / 0 错误，`CopyModOnBuild=false`。两场均 VeryHigh、DOP8、NoGC 配置 16 GB，正常搜索整场路线并在首个完整请求返回后停止；不覆盖时间、节点、Beam 或选择预算，不启用 `ForceShortSearchOnly` 或增量验证。CPU 与分配分别采集，耗时包含 profiler 开销，不作速度 A/B。

| 场景 | CPU runId / 分配 runId | 直接结果 |
| --- | --- | --- |
| 亡灵契约师 / AEONGLASS，38 牌、19 遗物、2 药，RequireAtLeastOne | `e4d7d876fdf043679e33b9d0236ae49d` / `4f810c9cfe014539bce8ed2884b53dd8` | 均 Passed；累计展开 150,035 / 转移 1,693,024 / 选择 1,024,228；预计战损 9、T13、2 药 |
| 静默猎手 / 机甲骑士，维护中原生建局 fixture 的完整 30 牌及附魔，Smart | `418816fd5abc4e0f957f9ab0cf07adb7` / `5fd38f44839842f083546fcc91bb310e` | 均 Passed；累计展开 14,611 / 转移 119,407 / 选择 67,273；预计战损 6、T7、0 药 |

- Linux perf 的可归因搜索样本分别 67,667 / 3,455；分配 trace 完整转换，事件丢失均为 0。CPU/分配对照只确认上述工作量与结果标量，未做逐动作严格等价。第一份 CPU 请求的退出日志有截断，不当作完整动作证据；perf 数据已成功解析，未因此重复场景。
- 初期短搜采集链路检查不进入正式结论；旧部分机甲存档 `1c1134cbba904d8c995745f1e4247cd2` 在建局时因缺少角色 ID 失败，未进入搜索，排除后使用维护中的完整建局 fixture。采样实例已停止；原始证据保留在 `.local/simulation-profile-20260910/`，完整条件与命令见[采样报告](performance/simulation-profile-20260910.md)及其 JSON。
- 只改诊断文档，执行 L0 文档链接、结构化结果及 diff 检查；没有算法修改、新语义回归、整场原生部署或正常可见 Steam FPS 结论。

## 0.36.1：遗物计数

- 删除遗物早停说明行：纯展示删除，按 L0 执行 Release 编译，未重跑战斗。

- 遗物总开关默认值调整：仅修改设置数据的初始值，新配置及缺少该字段的配置默认开启，显式保存的关闭状态仍保留；单项默认值和归档快照默认值保持原行为。本次按 L0 选择 Release 编译，不重跑战斗。

- 本次 UI 文案：`UI-LOCALIZATION` / `9bd2e76b41bf446485b32dbc78f5fef8` Passed（25.59 秒），eng/zhs/zht、400 项目录与语言往返通过。

- 三策略面板排版：`RELIC-COUNTER-POLICY` / `eaba35f068ce47b09eca0b8c51990814` Passed（25.82 秒），覆盖原生遗物图标、独立开关、面板互斥/边界，并新增三个面板背景与文字调制均完全不透明、数值输入 16 号字断言；原计数与早停合同保持。未进行可见游戏人工排版验收。

- `SEARCH-HP-TARGET-STOP` / `fdc28b23b38741e3b87c2a143664b7fc` Passed，23.45 秒：零损、累计阈值、并行波次排空、成长存在/不存在、击杀成长兑现、强制一药与保留备用药、至少一药策略。第一次启动器在取得 PID 30748 的可执行路径前失败，尚未进入 fixture；新隔离实例 `relic-early-stop` 完成上述验证，未提高超时。

- `RELIC-COUNTER-POLICY` / `5d123007ff00488fa48ad0278513ccdf` Passed，25.36 秒：十项计数根捕获、Fork、live 后续改变隔离、总/单项开关、设置往返、UI 标题顺序/面板边界/互斥；带增量校验的免费及 3 HP 付费路线，原生末击计数一致。达标早停 2 节点，关闭后 62 节点。初次 `e46d16d197f9426abc54e583f6f2de50` 明确 Failed，暴露补充搜索的前缀父链丢失；修复后通过，未关闭验证。
- `UI-LOCALIZATION` / `d545270315244f54a07f1964f5bb4d74` Passed，25.62 秒：eng/zhs/zht、394 项文本目录、模板及原动态控件语言往返。没有可见人工排版验收。
- Windows 结构门禁通过；Bash 同步新增策略、早停、缓存与前缀所有权规则，未在 Linux 启动游戏。

## 0.36.0：统一搜索预算与进程诊断

### 26356 三层卡顿修复

后续 6020 录制修复：`DYNAMIC-VAR-METADATA` / `57b289699aa045f39cf330a4b157eee7` Passed（22.72 秒）。隔离游戏源补入玩家 BaseLib，验证真实 Clone 的 live 空登记基线、模拟连续 2000 次零空登记、自定义提示、升级数值、两代克隆及父子独立。首个 `18c136b84cbe4cd3b2bbbbdab8980335` 因隔离环境缺 BaseLib 明确 Failed，补齐依赖后运行。复跑命令使用 `-ScenarioId DYNAMIC-VAR-METADATA -Sts2GameRoot <含BaseLib的隔离游戏源> -HeadlessInstance dynamic-var-metadata -TimeoutSeconds 120`；不可把无 BaseLib 环境当该合同通过。独立 `GcPolicyChecks memory`、`checkpoint` 通过，自动模式要求后台请求并确认完成，原高碎片自动压缩已撤回。以下压缩验证为此前历史证据；本轮没有修复后三层可见对照。

- 录制升级独立目标运行 36 秒，测试周期 20 秒（10 秒句柄/10 秒普通段），watcher 三段收尾并压缩，目标 writer/drain/停顿心跳合同通过。第一段 17,338 次句柄创建、17,274 次销毁，EventsLost=0；第二段没有句柄事件，窗口隔离有效。外层临时 PowerShell 包装误把未设置的 LASTEXITCODE 当失败；collector-health 为 complete，目标 stdout 为 PASS，随后按实际产物解析验证，未重复录制。
- EventPipe 栈验证初版错误地要求独立 ClrStackWalk 非零，已修正为读取 ETLX 关联栈：第一段 32/17,338 条句柄创建有栈，540/540 条 GCTriggered 有栈，不能声称每个句柄都有栈。旧第 7 段 102/102 条 GCTriggered 有栈。新增 `trace-stacks` 输出触发时间/来源；句柄事件验证与栈关联验证分别执行。
- `PerformanceRecordingTests registry` 验证后台计数落入 timeline；`PROCESS-DIAGNOSTICS` / `8c08835ad3e64332b3085eb19de2b858` Passed，22.92 秒，真实 Godot 两个弱登记容器可在后台读取数量，原跨战斗摘要与读档心跳合同保持。没有新的玩家长局性能或完整 GC root 证据。

- `NODE-POOL-LIFETIME` / `374081022814401586f01f02b9e8db49` Passed，23.01 秒。实际调用 NCard/NGridCardHolder 的已打补丁泛型方法，各复用 200 次；验证出站、入站、子节点递归、离树目标保留以及包装登记无正增长（-2071 / 0，首项包含同期终结器清理，不能解释成精确释放数量）。没有用静态 helper 替代生产入口。
- `SEARCH-HP-TARGET-STOP` / `9f80fe8fcdba48e7b82aad50b49b965d` Passed，8.58 秒，零损/阈值/成长/药水早停合同保持。没有运行完整三层可见 A/B。
- `CombatSolver.GcPolicyChecks` 默认 20 项通过，覆盖刚回收后少量分配、整层超过区域容量、有效预测可回收、缺失预测、NoGC 丢失及累计指标；`memory` 通过玩家物理压力样本、碎片选择及真实一次压缩/常规收集。碎片选择使用玩家数值作为输入，实际收集发生在小测试进程，不据此推断大堆暂停收益。`scopes` 8 项通过。
- 独立 `checkpoint` 通过生产 1 GB NoGC 区域的建立、回收后续用、收集中取消、确认排空、恢复普通 GC 与完成计数。
- 完整 `GC-CHECKPOINT-BACKGROUND-V0111` / `084d4e27093a445c87e21cd788547d53` 在 120 秒超时，由启动器停止自己的 headless 进程；没有结果文件、最新日志停在创建战斗房间，不能认定夹具断言已经执行。没有增加超时或原样重跑，改用上述独立进程最小合同。完整夹具的延迟手动请求/跨引用释放 epoch 部分本轮未验证。

- SINGLE-SEARCH-PROFILE / `2fb11ffdc6c649bca3838cab87dffbf0` Passed，22.68 秒：旧 deep 自定义参数迁移、保存重载、四档预算、单搜索进度、请求工作累计与固定小预算。
- SEARCH-HP-TARGET-STOP / `8313b1d83703473bb5dc1c751bd2700b` Passed，7.75 秒：零损、阈值、并行、成长目标、必要药水和额外药水保留。
- THEFT-RECOVERY-POLICY / `88dca2a1337d47b3bfef439169922998` Passed，7.27 秒：策略合同与固定小预算搜索。没有据此宣称完整玩家战斗路线质量或内存收益。
- UI-LOCALIZATION / `b794090ac5584fa8a06ab8f18c31844e` Passed，9.78 秒：设置、动态状态和中英切换。PROCESS-DIAGNOSTICS / `75fe89832e6b472c88f82ebec29df2a4` Passed，22.33 秒：模拟 State 存在而 NetService 未就绪的读档窗口，调用真实心跳 Process；验证切换战斗后 GC 摘要仍在进程日志、高频显示采样未被复制。
- CheckpointTool self-test 31 项断言通过，包括新单配置政策比较、旧政策保留及不同代预算不冒充同一政策；Windows 结构门禁通过。Bash 入口同步了协议与结构约束，未在 Linux 实际启动游戏。
- 最新可见进程 27996 的最后一场 combat_ended 回收：managed live 2.157→2.057 GB、private 15.581→14.451 GB，working set 6.746→6.740 GB。原日志删除了前序战斗，不能从该样本证明长局卡顿根因；未开启新 trace 或可见性能测试。

## 0.35.5：偷窃策略

- 定位证据：本机进程 31712 的战斗日志 `combat-7165739b37ab40ecae1120a90a34198a.jsonl` 中 SEARCH_REQUEST 与最终结果均为 PreserveResources，最终零损、outstanding_stolen_resource=20；多次点击保策略也仍为该枚举，按钮没有接反。旧代码的审计合同明确要求先比较战损，已按用户新确认的保资源优先语义修正。
- `THEFT-RECOVERY-POLICY`：地精 `1934513228be4d6eb86333192f568332` Passed，23.08 秒；偷窃草蜢 `fa18ef06b5b340db9e565d79f44c02bc` Passed，7.34 秒。合同覆盖两药/15 战损追回优于零损丢失、放走反向选择、候选展示可接受追回带来的战损增加、失败不能优于胜利、未追回不得 HP 早停；分别跑两种策略的 256 节点/1500 ms 短搜。短搜不是原玩家整场回放，也不证明所有局面都能击杀逃跑怪。
- 普通早停哨兵 `SEARCH-HP-TARGET-STOP` / `3928bbf0e4df41b28700274cd04abaa6` Passed，7.75 秒，零损/阈值/成长与药水数量合同通过；同一 headless 进程复用，末次退出。Release 编译 0 警告 / 0 错误，结构门禁通过。

## 0.35.5：UI 操作区重排

- 搜索摘要取消世界线计数前的强制换行：仅改显示连接符，未改计数或搜索逻辑；本轮验证 Release 编译，未重跑游戏场景或实机视觉验收。

- 状态摘要字号调整：仅将五处字体统一为 16；Release 编译 0 警告 / 0 错误，差异检查通过。没有变更状态/事件逻辑，本轮未重跑游戏场景，实际字号与长文本排版交由用户视觉验收。

- 红框状态摘要局部整理：`UI-PRIORITY-FEEDBACK` / `ca19e6d8275241b5b32abca92ad4c173`，Passed，22.60 秒；既有结果显示、收起恢复、设置伸展与失焦保存合同通过。Release 编译 0 警告 / 0 错误。根据截图调整状态卡片，未改动作列表；未进行新版实机视觉验收。

- 设置与结果摘要第二阶段：`UI-PRIORITY-FEEDBACK` / `7d6ea6dbffc942a194742ce16e440f84`，Passed，22.55 秒。性能页战损阈值失焦保存为 19、切页后恢复；设置高度伸展；结果卡片收起迁入主栈、展开恢复正文首位；失窃仍位于战损之前，新搜索清除旧提示。Release 编译 0 警告 / 0 错误，结构门禁通过，英文词典无重复键。未做实机视觉验收，也未改变搜索或出牌政策。

- 采用顺序与搜索入口简化：`UI-PRIORITY-FEEDBACK` / `34b3478a49a5480bba243234d57a8c46`，Passed，22.51 秒。采用控件固定第二位、搜索时执行控件隐藏，原展开/收起与设置合同通过；Release 编译 0 警告 / 0 错误。未做实机视觉验收。

- 独立释放按钮与右侧自动偏好布局：`UI-PRIORITY-FEEDBACK` / `ee426c9635624b9bbd7ca51578a3e63f`，Passed，22.54 秒；内存条恢复 Pass，释放按钮与内存条同父且位于右侧；展开/收起、主开关和设置伸展合同通过。Release 编译 0 警告 / 0 错误；未进行实机视觉及管理员清理验证。

- 内存条释放入口整合：`UI-PRIORITY-FEEDBACK` / `e74200fa4e634b00b190421b2ac5d739`，Passed，22.46 秒。确认内存条接收鼠标事件、位于主操作区，原布局/启停/设置伸展合同通过。Release 编译 0 警告 / 0 错误。本轮未实际触发管理员授权和系统内存释放，未做真实鼠标点击或视觉验收；该测试不作为系统清理效果证据。

- 用户校正后的最终验证：`UI-PRIORITY-FEEDBACK` / `a9ee0cb8abc34cb384231bc81485b4fa`，Passed，22.61 秒。全自动固定为动作行第一项，展开/收起均保持位置；内存释放入口归属主界面。原有启停、收起战损/失窃和设置伸展合同通过。Release 编译 0 警告 / 0 错误；未做实机视觉验收。以下保留初版证据。

- `UI-PRIORITY-FEEDBACK`：`5708336d44454fc9b073371c0c4e048a`，Passed，22.69 秒。覆盖动作区展开/收起、搜索/空闲、采用入口组合；全自动控件在模式行与紧凑动作行之间移动；标题栏启停事件、维护按钮归属、偷窃策略位置，以及原有 SL 面板恢复、收起战损/失窃和设置页伸展合同。
- Release 编译 0 警告 / 0 错误，结构门禁通过（85 个 Search 文件）。初次编译发现 Godot 控件缺少 partial，补齐后通过；没有使用失败构建的旧产物进行验证。
- 本轮不改搜索、采用和部署命令实现；未进行实机视觉验收，未宣称具体窗口尺寸下的遮挡或帧率已经验证。没有发布创意工坊或推送远端。

## 0.35.5：P0 / P1 反馈

| 场景 | 最终 runId | 结果与范围 |
| --- | --- | --- |
| NATIVE-HAND-CHOICE-REPLAY | f56df4c5816d490fabbd1ebd8172c8bb | Passed，27.38 秒。燃烧契约原生选牌、投斧两次重放；主动构造选择计划失配，保留原生手牌选择，手动完成后具体出牌动作正常结束。 |
| TURN-SETUP-UI-TOOLS | 2080e6ad6c7e4907b80ecddb919ce9cc | Passed，30.64 秒。必备工具 T2 准备选牌中停止、重算、再次停止、继续原生选择，返回 Play 后忙碌状态和输入锁均清除；短/深预算各 1000 ms。 |
| UI-PRIORITY-FEEDBACK | 166804bf580b428dac522d4dca0fddcc | Passed，23.00 秒。主面板启停事件；清理会话且没有 TurnStarted 时恢复面板并保留手动计算；收起时财物提示在战损左侧、新搜索清除旧提示；设置面板从 440 增至 660 高度，内部滚动区增高至少 180，切页正常。 |
| SEARCH-HP-TARGET-STOP | 60ec179b65e7413a8f3d6b3480d3c60e | Passed，23.50 秒。保留既有零损/阈值/开关/DOP2/禁忌魔典哨兵；狩猎兑现收益后零损停搜（1 节点）；指定一瓶与全局至少一瓶均以一瓶零损获胜并保留额外药水；可重复击杀来源在 3 敌人时保持目标 3。 |

- 所有请求使用独立 headless 实例、120 秒上限并在完成后退出。结构门禁 `search_files=85`，Release 编译 0 警告 / 0 错误。
- 失配基线 `3d174aeadc8942abbe94d88764b30f48` 明确失败于“原生选择被取消”；修复后手动恢复合同通过。此前初版 fixture `d2aca6c8ae944e63915a4b74c6505258` 错把动作队列临时空闲当作重放完成，改为复用生产 GameAction.CompletionTask 捕获后普通/投斧场景 `263cacd38bb74b19a43af1b8a4485927` 已通过；该初版错误不是产品卡死证据。一次编译失败后的旧产物测试启动被中止，未计为验证。
- 未复现反馈中的所有“整个游戏进程无响应”情况；也未实际调用 BetterSpire2 的 SL 操作。无可见布局/鼠标验收，没有整场性能或所有第三方组合兼容结论。财物显示合同验证投影和布局，不新增怪物偷窃效果语义结论。

复跑入口为 `tools/run-unattended-test.ps1 -ScenarioId <上述ID> -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance <独立名称> -ExitOnComplete -TimeoutSeconds 120`；TOOLS 使用 SILENT，并附 `-ShortSearchBudgetOverrideMilliseconds 1000 -DeepSearchBudgetOverrideMilliseconds 1000 -ForceShortSearchOnly`。

## 0.35.4：战损目标早停

- `SEARCH-HP-TARGET-STOP` 最终 `f6b4b94d8a6641bf8a6ec2cdd953776b` Passed，22.85 秒；固定 128 节点、1500 ms 短搜，后台独立实例 `hp-target`，120 秒请求上限，完成后退出。
- 缺少对应卡牌而保存禁忌魔典额度 12 时，HasGrowthTargets=false，默认早停仍有效。同根 1 HP 敌人、打击/防御/痛击/燃烧：开启展开 1 个节点，关闭展开 4 个，均完整零损胜利。此数值仅是最小功能对照，不代表整场性能。
- 合同注入已累计损失 3 HP，阈值 3 达标、阈值 0 不达标、开关关闭不达标；用于检查整场累计口径，不声称原生受伤差分。12 HP 敌人场景实际最大并发 2、展开 5 个节点，排空后返回完整零损胜利。
- 加入禁忌魔典后恢复成长例外，即使能立即零损击杀仍取得 1 次删牌收益；忽略局外收益时恢复早停。默认值和关闭后的序列化往返通过。初轮 `f705ffede7d6435b952a0d6c2b453bd6` 已通过基础合同，最终扩展了真正双 lane 场景并覆盖补充搜索出口源码改动后的运行。
- Release 编译 0 警告 / 0 错误，结构门禁 search_files=85。没有可见游戏测试、完整跑局或独立多药水后验场景结论。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId SEARCH-HP-TARGET-STOP -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance hp-target -ExitOnComplete -TimeoutSeconds 120
```

## 0.35.4：按钮区与收起显示

- `UI-COMPACT-QOL` / `fe936f87a2764f329398710cbc41c885` Passed，22.42 秒，专用 headless 实例 `compact-qol`，120 秒上限，完成后退出。
- 补齐浅色主题开关状态颜色同步后，最终 0.35.4 构建同场景 `063d30e4cb70455a856524bfabfd0aed` Passed，22.59 秒；构建来源 `a72a669`。设置刷新触发开关外观更新，仅实际偏好变化写盘。
- 检查开关默认关闭、切换事件更新设置及序列化往返、下场开启、自动计算关闭时仍接入全自动、手动停止后刷新不重开、下一场重开、关闭偏好后下一场关闭。
- 直接渲染只读路线投影：收起后原 Label 可见且 Body 隐藏，7 HP 显示危险色，原位更新 0 HP 变为成功色，展开后归位，新搜索隐藏旧战损，未知投影保持问号和灰色。
- Release 编译 0 警告 / 0 错误，结构门禁 `search_files=85`。未做可见布局/鼠标验收；该 fixture 不证明整场自动部署或开局选牌完整链。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId UI-COMPACT-QOL -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance compact-qol -ExitOnComplete -TimeoutSeconds 120
```

### 古代卡牌成长策略与累计成本（本批此前证据）

- `GROWTH-ANCIENT-POLICY` / `f9031db3e06441dcaa38a76db4c8b693` Passed，26.09 秒，IRONCLAD / FUZZY_WURM_CRAWLER_WEAK。专用后台实例、120 秒上限、1500 ms 短搜；测试完成后退出。
- 真实 CardModel/原生出牌与模拟完整 MoveStateSnapshot 对照：ECHO_FORM_POWER 重放 BRIGHTEST_FLAME 两次，共记 4 点最大生命消耗；随后 FORBIDDEN_GRIMOIRE 记 1 次删牌收益，独立额度 12 HP。原生历史重新捕获保持 4，Fork 增量不污染父分支，BeginSideTurn 保持累计成本。
- 实际搜索及增量回放：已有 4 点消耗、上限 4、手牌含 BRIGHTEST_FLAME / FORBIDDEN_GRIMOIRE / STRIKE_IRONCLAD / CASCADE，抽牌堆含 BRIGHTEST_FLAME。结果获得完整胜利及 1 次删牌收益，路线不使用至亮之焰或会自动打出它的 CASCADE。还检查不限、0、等于上限及手动已超额的准入口径，以及设置往返和成长行重载。
- 初轮 `4ee0b6363f77406aae14eea99c03425c` 已通过前段原生对照，在补充能力审计的 RankFinal 遇到重复对象键；修正对象身份去重后最终场景通过。中间一次启动器未取得已退出进程的 executable path，尚未提交请求；同一 DLL 重新启动后成功。未扩大超时。
- 没有可见鼠标/布局验收，没有完整跑局质量结论，也未验证其他 Mod 修改至亮之焰最大生命变量的历史回写语义。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId GROWTH-ANCIENT-POLICY -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -HeadlessInstance ancient-growth -ExitOnComplete -TimeoutSeconds 120
```

## 0.35.4：PR #83 合并检查与预设节点上限

- `python -X utf8 tools/NoVictoryRecoveryChecks/run.py`：通过 PR 自带 `AssertNoVictoryEscalationPolicy` 的全部策略断言与新增 8 项请求流程检查。直接编译生产 BuildNoVictoryEscalationProfile / EscalateSearchWhenNoVictory；原入口先复现“追加搜索丢弃明确采用结果”，修正后覆盖接管、已有胜利不重搜、胜利退出、停止、拒绝更差结果、两轮封顶、第二轮饱和及仅分支增长。结果、质量排序和根采用确定性替身，未声明整场搜索验证。
- `python -X utf8 tools/NoVictoryRecoveryChecks/presets.py`：直接编译生产四档声明及 SolverSearchProfile，核对节点加倍、时间及 Beam 保持原值。低/中/高/极高 Short 为 2400/4800/10000/20000，Deep 为 12000/24000/50000/100000。
- PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。未启动游戏，本轮没有新增原包恢复、可见 UI 或实战战损验收；下方 PR 附带的检查点记录来自作者此前验证。

## 无胜利路线时的搜索面升级（贡献者记录）

同一个玩家问题包 `CombatSolver-0.35.3-CEREMONIAL_BEAST_BOSS-610658da…`，观者 A9 第一幕 Boss
仪式兽，玩家 34/79、Boss 262/262、两瓶药（稳定血清、缚魂药水），恢复方式 `native_events`
（`materials_valid` / `canSearch=true`，原生二进制编码未校验）。三次都固定
`-NoGcRegionBudgetGigabytesForTest 10`，避免 NoGC 区域建不起来变成隐藏变量。

| 场景 | runId | 结果 |
|---|---|---|
| 第 1 回合检查点，录制的 High 策略，改动前 | 无（对照，未记录 runId） | `OnlyDeath=True`、战损 34、第 6 回合死、用药 0、`searched_turns=6`、17.0 秒。药水梯度三层 `saved=0`，`selected_potions=0` |
| 第 1 回合检查点，录制的 High 策略，改动后 + `-VerifySearchPolicySnapshot` | `76ed67cdcd504d30b451b230246b667c` | Passed，41.7 秒。一次升级（Beam 90→180、节点 25,000→50,000、出牌分支 48→96）后 `layer=2 won=True`：两瓶药、第 7 回合斩杀、战损 30、`OnlyDeath=False`、`Unmirrored=0`。同一次跑通过 `SearchPolicySnapshot` 全组结构断言（含新增的升级政策纯函数断言） |
| 第 3 回合检查点（改动前就能获胜），不可退化哨兵 | `09f0f4a5ea7541fa818c5389c03fb059` | Passed，16.6 秒。`NO_VICTORY_ESCALATION` 出现 **0 次**；`Potion=2, Saved=5/18, Rejected=170, Turns=4, 战损 29, CombatEndedTurn=6, SoldHp=0/15` 与改动前实机日志逐项相同 |

Beam 与节点是乘的关系，只抬一边都不够，四组顶格实测（`b2131b2c54b243d7a65c7ccffb58e17b`
为其中 Beam 135 / 100,000 那一组）：

| Beam | 节点上限 | 结果 | 胜利层实际展开 |
|---|---|---|---|
| 90 | 25,000 | 输 | 主搜索 2,701–5,206（前沿走空，花不掉预算） |
| 90 | 50,000 | 输 | **与上一行逐个相同**，只抬节点无效 |
| 135 | 50,000 | 输 | 需要 83,423，不够 |
| 135 | 100,000 | 赢（第 9 回合、战损 33） | 83,423 |
| 512 | 100,000 | 赢（第 8 回合、战损 31） | 26,671 |

`tools/verify-refactor-boundaries.ps1`：`REFACTOR_BOUNDARIES_OK search_files=84`。
`dotnet build CombatSolver.csproj -c Release`：0 警告 0 错误。

边界：这三次都是单一检查点的搜索质量证据，不是整包回放、不是完整自动部署，也不覆盖原版角色。
默认小遭遇战（`FUZZY_WURM_CRAWLER_WEAK` + 起始牌组）跑 `-VerifySearchPolicySnapshot` 会在
既有的「节点上限释放快照」断言上失败（`dop1=0/3`），改动前后一致，属于该组门禁的场景依赖，
与本改动无关。

## 0.35.3：PR 合并与监控版本提醒（本轮验证）

- 合并后的 `dotnet run --project tools/CardHookReceiverChecks -c Release`：78 项通过；`python -X utf8 tools/EndTurnAdmissionChecks/run.py`：33 项通过；`dotnet run --project tools/CardTargetingChecks -c Release`：37 项通过；`python -X utf8 tools/DamageDealerChecks/run.py`：101 项通过；`python -X utf8 tools/PlayerDeathChecks/run.py`：42 项通过。伤害合同替身有一条 CS0649 未赋值警告；共 291 项是生产方法链接合同，尚未执行游戏原包或完整原生结算差分。
- `dotnet run --project tools/ClientUpdateChecks -c Release`：35 项通过。验证三段数字比较（包括 0.35.10）、相等/更旧/主版本升级、204 旧服务、非法/缺失 JSON、HTTP 失败、取消、服务回撤版本及关闭提醒；失败不覆盖最后有效状态。
- 已部署工作台基线 5611856 同步后，在 `tools/OnlinePresence` 执行 `npm test`：17 项通过，含管理员登录/Origin、采集端隔离、严格版本验证、旧客户端心跳、设置更新/清除/重启持久化及已有统计逻辑。Node 23.9.0 提示 SQLite experimental warning；线上使用 Node 24.15.0。
- 同目录 `$env:BROWSER_CHANNEL='msedge'; node --test browser.test.mjs`：9 项通过。新增版本维护表单实际 DOM 保存/关闭、刷新不覆盖输入，同时验证现有登录恢复、筛选、布局、延迟请求及统计恢复。默认 Playwright 浏览器未安装导致首轮启动失败，改用已安装的 Edge 后通过；未启动可见浏览器或游戏。
- 下方 PR 附带检查属于贡献者原始记录。本轮不将独立替身合同表述为游戏状态差分或实机修复的完整验收。

## 2026-09-10：玩家死亡能力清理

- Release 编译 0 警告/0 错误；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。没有安装或启动游戏。

- `python3 tools/PlayerDeathChecks/run.py`：42 项通过，提取编译生产 HandlePlayerDeath 和 RemovePowersAfterDeath；旧入口在宠物死亡回调观察到残留能力，失败基线已复现。
- 覆盖有/无奥斯提、宠物清理 pending、先清能力再清球/宠物、正负层数移除、允许死后存续能力、其他 owner、重复清理及现有 Illusion 移除否决规则。
- 测试状态/能力模型/宠物 Kill 为替身；不构成真实根/Fork、AfterRemoved 回调覆盖、死亡阻止或原报告完整回放验收。原版 CreatureCmd 的清理位置及 Creature.RemoveAllPowersAfterDeath 已定向核对。



## 2026-09-10：伤害来源生命状态隔离

- Release 编译通过，0 警告/0 错误；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。未安装或启动游戏。

- `python3 tools/DamageDealerChecks/run.py`：101 项生产伤害入口检查通过。旧代码在分支已死、实机存活的输入下仍产生伤害结果，最小基线失败。
- 编译生产全部 Damage 重载及 DamageSingleTarget；交错实机/分支死亡状态，覆盖禁止 live getter、独立分支、复活、null 来源、空目标及死亡来源不进入伤害/后续 Hook。
- 状态和逐目标/后续 Hook 为确定性替身；没有验证真实 Fork、伤害计算、原生死亡时序或报告中完整递归抽牌链。保留原递归安全上限，不把本合同写成整场验收。



## 2026-09-10：动态目标类型分支隔离

- Release 编译 0 警告/0 错误；Bash 结构门禁通过，`search_files=84`。同步更新两端门禁的分支模式，PowerShell 因当前环境缺少 `pwsh` 未运行。未安装或启动游戏。

- `dotnet run --project tools/CardTargetingChecks -c Release`：37 项通过，直接编译生产 `CombatPredictionSimulator.CardTargeting.cs`；旧源码复现分支无能力时误用实机全体目标。
- 两张动态目标牌分别覆盖分支能力缺失/0/1/2/移除、实机能力有无交错、其他角色能力隔离、独立分支，以及普通卡和非影子状态回退。影子状态用例禁止读取原生目标 getter。
- 游戏 0.111.0 的原版 Shiv / SovereignBlade 目标 getter 已定向核对：对应能力决定 `AnyEnemy` / `AllEnemies`。测试模型和状态为替身，不构成真实根捕获、完整 Fork、原生伤害或原包回放验收。



## 2026-09-10：结束回合循环出口准入

- Release 编译通过，0 警告/0 错误；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。未安装或启动游戏。

- `python3 tools/EndTurnAdmissionChecks/run.py`：33 项通过。直接编译生产结束回合入口、raw 生成、剪枝、准入谓词、materialization 循环和批次所有权容器；旧 `BuildAcceptedEndTurnNodes` 在相同输入下因未结算观测到达转置准入而失败。
- 覆盖有效/撤销/无父租约、多选牌子分支单一出口、终结边界、剪枝、转置拒绝、stand-pat 发布，以及提前退出/生成失败时快照释放。
- 质量计算、单张出口票据签发、模拟与转置判断使用确定性替身；不构成游戏原生差分、预算触发的完整搜索或原报告回放验收。既有展开入口 fail-fast 和循环预算保持不变。



## 下一版本（开发中）：回合末卡牌 Hook 的 COW 接收者

束缚清除可能替换共享卡牌预览，后续Regret Hook 持有旧接收者并找不到手牌，漏记失血张数。常规 BeforeSideTurnEnd 派发先固定卡牌 wrapper 和监听顺序，执行时跟随当前预览，保留原先的挂起检查和非卡牌身份。

本轮生产 COW/牌堆源文件的独立检查：旧派发模式复现失败，修复后 78 项断言通过。Release 构建 0 warnings / 0 errors，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。没有启动游戏、恢复原报告或执行原生伤害/正式搜索差分。报告计数与证明范围见 [专项分诊](issues/regret-bound-hook-receiver-20260910.md)。


## 0.35.2：回合开始选牌生命周期

最终行为源码使用同一 DLL 完成以下 12 项后台原生页面回归，均 Passed。场景结果位于本地 `outputs/turn-setup-ui/verified/<ScenarioId>/result.json`，同目录保留请求和命令记录。后台实例已退出，性能录制保持关闭。

| ScenarioId | runId | 验证边界 |
| --- | --- | --- |
| TURN-SETUP-UI-TOASTY | f4772d52214c4a028c8e72d1b814d2ba | 初次停止、重算中停止、再次重算、执行进入 Play |
| TURN-SETUP-UI-GAMBLING-PARTIAL | a9bb1d4e4b3d4538b7754e99d47fb801 | 同上，额外覆盖未确认的手动勾选后计划部署 |
| TURN-SETUP-UI-TOOLBOX | abd258ec251f40eebf88d11841c00295 | TOOLBOX 原生选择页停止与恢复 |
| TURN-SETUP-UI-PARADOX | d907f709caba4366b916de1033f630d0 | CHOOSE_A_PARADOX 原生选择页停止与恢复 |
| TURN-SETUP-UI-TOOLS | a15eba75865b41acac5183e7f32c46ee | T2 TOOLS_OF_THE_TRADE，无既有续用选择 |
| TURN-SETUP-UI-TYRANNY | 85ca20ed4d9040d494742e32b1103963 | T2 TYRANNY，无既有续用选择 |
| TURN-SETUP-UI-MIXED | 957c52a9ba574a2480b87d44ad9d7e68 | TOOLBOX、GAMBLING_CHIP、TOASTY_MITTENS 连续选择 |
| TURN-SETUP-UI-TOASTY-MANUAL | 28e4d6508dc04d33a71360f52afa9bd1 | 搜索中手动确认，旧搜索退出且旧结果不安装 |
| TURN-SETUP-UI-TOASTY-FULL-AUTO | efc0e0d46575422dbe09006e0ab4f02f | 停止后立即开启全自动，排空后重算并驱动原生选择 |
| INITIAL-TOASTY-MITTENS-SEARCH-CONTROLS-REGRESSION | 9511fb1ec2b045c89951b43051f93306 | 搜索中采用、执行及手动重算入口，T1 26 动作 |
| TURN-SETUP-STOP-CANDIDATE | dc2da1d6fe0e4ad794da077ebdcc17e6 | 保留停止候选并采用，忙碌标志清除，T1 24 动作 |
| TURN-SETUP-APPLY-CURRENT | 5a0a705091fc46b8a78ed46c6d4bc841 | 应用当前回合，T1 23 动作 |

- 使用 SILENT / GREMLIN_MERC_NORMAL、2000 ms 短搜、`PreserveNativeCombatStateForTest`、单请求 120 秒上限。UI 场景在首次目标 Play 状态停止，断言忙碌标志清除、页面遮挡消失、搜索失败为空；部分勾选场景同时断言计划外重算为 0。
- 迭代中的手动确认 fixture 曾超时，暴露页面等待后的搜索所有者竞争；恢复原子状态转换后通过最终手动确认及全自动场景。未延长超时。
- 这些是后台原生控件与生命周期验证，没有打开可见 Steam 游戏，不代表人工鼠标/动画验收或所有场景完整战斗战损差分。

## 2026-09-10：PR #74 / #75 / #77 合并验证

- #74：直接编译 PR 中生产 profile 与 BuildNarrowBeamRecoveryProfile，剩余 49000 节点/299000 ms 和 2000 节点/1000 ms 正确；预算耗尽与普通 Beam 不产生救援。复用同一行为源码审计阶段的结果，不声明实战战损改善。
- #75：`pwsh -NoProfile -File tools/verify-unattended-map-points.ps1` Passed，4 种输入；从生产请求哈希表的 AST 取出真实字段表达式，检查省略、空数组、单点、多点的 JSON 往返形状，拒绝 null 和嵌套数组。原 PR 的显式空数组和非空数组均已复现多包一层。
- #77：`MODEL-STATE-INTEGRATION` / `444ddec2f3bf49568ece16acf9be80f5` Passed，27.93 秒，T1 → T2。真实游戏身份、CombatRootSnapshot、完整 CombatPredictionSimulator.Fork 和原版回合推进；专用测试为 BurningBlood 与 BigGameHunter 登记状态，持有可变列表及预测卡牌引用。父子对象/列表/卡牌隔离，子分支变更改变指纹与续用文本而父分支保持原值；原生和预测下一回合完整快照（含 continuation）一致。不是第三方 Mod 效果镜像通用正确性的证明。
- 原 PR 独立 32 项状态合同、3 项空登记及 1000 次 0 字节指纹分配检查在审计阶段通过；同一接口行为不重复。合并后的 Release 编译 0 警告/0 错误，PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。没有打开可见 Steam 游戏，没有发布。

专用模型状态合同必须使用新后台实例，并在请求后退出，避免已冻结的测试登记污染其他请求：

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId MODEL-STATE-INTEGRATION -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -PreserveNativeCombatStateForTest -ModifierId BIG_GAME_HUNTER -EnemyCurrentHp 500 -HeadlessInstance model-state-integration -ExitOnComplete -TimeoutSeconds 120
```

## 遗物与 Modifier 通用状态接口（开发中）

- `ModelPredictionStateChecks --empty`：Passed，3 项；空登记表保持原指纹与 continuation 文本。
- `ModelPredictionStateChecks`：Passed，32 项；捕获后 live 变化隔离、同类型实例交换、零值与集合顺序、父子分支隔离、引用重映射、事务边界、错误 Fork 拒绝（包含派生运行时状态被切成基类）、缺失/重复捕获、重复/迟到登记、精确类型、区域设置、并发读取，以及独立 live/predicted 更新和重新捕获后的等价描述。
- `--fork-type` 在修正前拒绝断言失败：派生状态被复制为声明的基类且未报错，虚属性从 2 变为 1。修正要求实际运行时类型一致，已由上述 32 项合同覆盖。
- `--allocation` 在修正前检测到 1,000 次无文本指纹调用分配 32,088 字节（其中包括首次比较初始化）；改为下标遍历玩家列表后，预热比较和查询的最终检查 Passed，1,000 次调用分配 0 字节。这是固定调用的分配合同，不是游戏或搜索耗时 A/B。
- 检查直接链接生产 registry、writer、store 和 fingerprint，游戏身份、模拟器外壳与 Fork context 使用替身。没有启动 Godot，也没有据此声称完整模拟器 Fork、原生结算或两回合差分通过。
- 当前游戏 0.111.0 的 macOS ARM64 引用下 Release 构建通过，`CopyModOnBuild=false`，0 错误；最终 `--no-restore` 构建有 1 条 `NU1900` 警告，来自无法访问 NuGet 漏洞数据源的缓存恢复记录。
- Bash 结构门禁通过：`REFACTOR_BOUNDARIES_OK search_files=84`。PowerShell 对应规则已同步，未执行（本机无 `pwsh`）。未执行游戏内单效果/两回合差分、部署、性能 A/B；具体适配的语义验收仍需这些针对性差分。

```sh
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release -- --empty
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release
dotnet run --project tools/ModelPredictionStateChecks/ModelPredictionStateChecks.csproj -c Release -- --allocation
```

接口及手工验收范围见[模型状态适配](third-party-model-state.md)。此记录仅对应本项开发改动，不复用下方历史游戏场景作为本轮证据。

## 0.35.1：回收后堆空间复用

- `dotnet run --project tools/CombatSolver.GcPolicyChecks/CombatSolver.GcPolicyChecks.csproj -c Release -- memory`：Passed，直接链接生产策略及信号；玩家采样剩余可复用空间 4,172,872,440 字节，区域容量 6,938,945,322 字节。验证复用空间不重复增加物理用量、三 GB 波次可准入、物理上限立即阻止准入、用户预算上限、回收委托、Disable 探针清理及无实时探针模式。
- 同工具默认入口：19 项 GC 策略检查 Passed。Release 编译通过，0 警告/0 错误；版本元数据变化后的最终发布构建不重跑这些合同。
- 证据：`outputs/centipede-gc-20260910/checkpoints.json`、`trace3-raw.json`。原始第三段完整解析，EventsLost=0；目标战斗 36 次主动回收、102 个 SuspendForGC 暂停区间，总暂停约 6244.8 ms、最大约 861.8 ms。此为旧版现场，不能作为修复后的结果。
- 按用户要求没有启动实机或无人游戏；没有实际回收次数下降、FPS 或路线质量的新结论。

## 0.35.0：发布验证

- 复用下列文字特效与战斗速度证据及明确的未验证边界。SpeedX 提示增加中英设置指引，路径与当前控件标签逐项核对，英文 JSON 做语法及文案键匹配检查。
- 遵照用户指令不再启动实机测试，不执行完整发布门禁。发布从本次版本提交执行一次 Release 构建和最小 ZIP，发布包包含许可声明与 Windows 内存清理工具。

## 2026-09-10：文字特效释放与战斗瞬间速度（未发布）

- 正常可见 Steam `COMBAT-TIMING-LIFETIME` / `5e72da97870d452c821f1b46110b0437` Passed，23.38 秒，T1 到 T2。原版 reverse patch 与生产回调对照涵盖两种特效、开关及三个动画时点，比较返回值、颜色、位移、变换和可见性；4 万次回调的 Godot 临时对象登记为 14178 → 14178，原环境字典仍可读。
- 原生 Cmd.Wait 在战斗 Instant 下同步完成，在 FollowGame / Normal 下真实等待；执行原生结束回合动作并完成怪物回合后，战斗仍为 Instant，全局偏好保持 Normal。
- 后续追加战斗结束后的局外等待检查，首次 fixture 直接 Kill 后缺少 CheckWinCondition，120 秒超时；已补齐胜负结算调用。用户要求停止实机测试，修正后的局外检查未执行，不将其记为通过。生产行为与已通过测试相同，最后变更仅为测试补充。
- 最终 Release 编译通过，0 警告、0 错误。本机更新开发 DLL，未发版。未重现玩家完整两层后的长会话，不声明整体卡顿全部消失或提供 FPS 增幅。可复跑输入为 `coverage/unattended/combat-timing-lifetime.json`。

## 2026-09-10：SpeedX 横幅告知

- UI 低影响提示变更：检测已加载程序集名 SpeedX，在原有反馈横幅加中文/英文性能提醒；既有异常信息继续保留。检测结果通过一次枚举和程序集加载事件维护，不在每帧扫描程序集。没有增加 SpeedX 修补逻辑。
- 验证采用 Release 编译；本轮未进行实机横幅排版验证，不声明已修复 SpeedX 或长期卡顿。

## 2026-09-10：进程全程性能诊断（未发布）

- 自动压缩追加合同：`outputs/performance-compression-contract/` 连续 3 段全部成功采集并压缩，原始总计 8,408,334 字节转为 1,458,408 字节；collector 正常 complete。解压第一段后解析出 10043 个线程采样、8707 个分配事件、538 次 GC start，EventsLost=0。玩家已有段一次性从 280,502,570 字节压为 26,455,804 字节；此次只修改外部存储脚本，不改变 DLL、采样配置或搜索行为。

- 独立 `tools/PerformanceRecordingTests` 合同：36 秒进程采样，刻意停止主线程心跳 3 秒；验证后台仍写入、线程/内存/GC 数据、队列无丢弃、退出排空。10 秒分段连续采集并正常退出；带空格输出路径通过。修正 Windows PowerShell 的 File.Replace 空备份路径及 Process.ExitCode 句柄生命周期问题后最终合同通过。
- EventPipe 显式使用 runtime `0x100003C01D:5`，避免多个 profile 合并保留 Informational 而漏掉分配事件。独立样本解析出 10118 个采样事件、8696 个分配事件、538 次 GC start，EventsLost=0；topN 能解析出测试计算方法。采样含等待时间，不当作 CPU 占比。
- Heap dump 合同：独立进程收到现场请求，dotnet-dump exitCode=0，dumpheap 成功读取 PerformanceSession 等对象；连续轨迹继续分段并收尾。对应本地证据 `outputs/performance-snapshot-contract/`。
- 正常可见 Steam 短战斗：`f5cf09910c8b46548444922d768076f4` / `VISIBLE-LOGGING-CAPTURE-0300` Passed，31.75 秒，T6 结束，零计划外重算。时间线包含完整搜索/部署事件、16 个生命周期开始事件、引擎资源与主线程样本；dropped=0。抽检游戏轨迹段 63069 个采样事件、3720 个分配事件、8 个竞争事件，EventsLost=0。
- 最终进程所有权边界：`f6a6db1e04d04bbb9bb19c9b89d0b014` / `PERFORMANCE-RECORDING-LIFETIME` Passed，37.75 秒；生产录制节点移除/重新安装仍引用同一 PerformanceSession。实际时间线只有一个 start、一个 host_reattached、一个 process_recording_end 和 writer_end，采集器 complete，dropped=0。抽检段 67889 个采样、6375 个分配、13 个竞争事件，EventsLost=0。退出后的 ZIP 导出成功。
- 最终 Release 编译 0 警告/0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。本机诊断配置最终使用 300 秒分段；不重复五分钟等待验证仅时间参数变化。
- 复跑节点边界：先在测试安装启用诊断配置，再运行 `tools/run-visible-steam-benchmark.ps1 -RequestFixturePath coverage/unattended/performance-recording-lifetime.json -TimeoutSeconds 120`。这是诊断管线验证，不是对具体快速 SL Mod 的兼容承诺；未复现用户完整两局后的持续卡顿，也未建立诊断开销的完整 A/B 或正式版 FPS 结论。

## 0.34.10：撤回新增搜索目标

删除 0.34.9 的目标模式、目标面板、独立收益排序与达标停止，恢复原成长策略；保留牌堆缓存和组合估值。五项游戏回归通过，单请求 120 秒：

| 场景 | runId | 验证范围 |
| --- | --- | --- |
| GROWTH-POLICY-FREE-FIRST | `d174c57bb03b47e787a827f29a9c9107` | 零额度优先免费成长、忽略收益开关、原侧栏、逐次额度、Fork 与增量回放；旧三种目标配置保留已有额度 |
| GROWTH-POLICY-PAID | `32b7f8852606422f873228bcb2209fa1` | 零额度拒绝付血；允许额度内实际付血成长，超额拒绝，完整获胜优先 |
| PROFILE-STRENGTH-SHIV-DEPLOY | `9806fb4ae82a4c3489ad8cafb92cf649` | 保留力量小刀估值，T1 无损部署、零计划外重算 |
| PROFILE-EXHAUST-DRAW-DEPLOY | `50d71e6fcea34248b7590091e971d543` | 保留消耗抽牌估值，T1 无损部署、零计划外重算 |
| SMART-POTION-INVENTORY-FULL | `d5e87bc97a74438fa75e80092907c4bd` | 满栏按原门槛保留药水，预测战损 3 |

- 复用实例中的首个付血场景 `efae88b299b34e7ca7c06dee7e7dd4c9` 在侧栏开关／边界测试断言失败，尚未进入该场景的战斗搜索；独立实例同时通过 UI 与付血搜索合同。未定位重复使用 UI 测试时的状态干扰，不将其写成生产战斗语义缺陷或已修复项。
- 旧配置读取使用现有反序列化入口测试：忽略已移除的 objective 字段，保留 geneticAlgorithm=7 及原忽略收益设置。没有添加新的迁移默认值或覆盖用户配置。
- 行为构建 0 警告／0 错误；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`；CoverageCatalog `--verify-effective --verify-state-fields --verify-state-writes --verify-branch-state-reads` 3035 项通过。目标专属 SearchObjectiveChecks 随功能删除；调用旧入口报项目不存在，该检查不再适用，由上述原成长合同覆盖。
- 回归之后只改版本与发布文档，执行最终 Release 构建，不重复行为测试。0.34.9 的以下发布和测试记录均保留为历史。玩家更新日志见 [0.34.10](releases/0.34.10-RELEASE_NOTES.md)。

## 0.34.9 发布依据

发布行为源码为 `ceea8ef`：保留本批战斗搜索改动，撤回满栏药水优惠。复用下方集成回归及撤回后的药水、部署、UI 配置恢复证据；本次只修改版本与发布文档，执行一次最终 Release 构建，不重复游戏测试。玩家更新日志见 [0.34.9](releases/0.34.9-RELEASE_NOTES.md)。

## 2026-09-10：撤回满栏药水优惠

- 用户要求撤回满栏药水策略。终局准入、反事实省血门槛和药水搜索容量恢复原规则，删除 PotionInventoryValue 及其专属纯函数测试。更新满栏和部署夹具：保留药水、预测损血 3，部署结束生命至少 67、零计划外重算。
- 最终 `SMART-POTION-INVENTORY-OPEN` / `64ce2ff94ddd404d89513635bde6e351`、`SMART-POTION-INVENTORY-FULL` / `b7eefa6fe2cd463ebfbdf29c8d50e9da`、`SMART-POTION-INVENTORY-NO-BENEFIT` / `22d1c780d5b749008c1c97c1ae1678a6` 均 Passed；实际部署 `SMART-POTION-INVENTORY-DEPLOY` / `6c87d7aa16114f73997cedb228987df0` Passed。每请求 120 秒，独立测试实例。
- 最初未满栏探针 `342e0bc4fa284dcaad865369f33484d3` Failed：此前 UI 测试把永久培养目标和关闭自动计算写入测试实例配置，测试结束只恢复内存。修正 UI 测试结束时同步恢复持久化配置，并恢复该私有实例的平衡目标；未修改玩家设置。
- `SEARCH-OBJECTIVES-UI-LOCALE` / `b939207ba9a94af2904eff00ba4d9cca` Passed，测试后读取持久化配置确认平衡目标及自动计算已恢复。
- Release 构建 0 警告／0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=85`。以下上一轮满栏折扣测试结果仅作为历史记录，不代表当前策略。

## 2026-09-10：PR #69 / #71 / #73 战斗内部分集成

从三个审查分支提取战斗搜索目标、达标停止、无序牌堆缓存、战斗潜力与满栏 Smart 药水策略，集成到 0.34.8 之后；奖励、商店、删牌评分与画像 UI 未引入。以下为本次集成源码的直接结果，使用独立实例 `combat-pr-integration`，每请求 120 秒。

| 场景 | runId | 结果与范围 |
| --- | --- | --- |
| SEARCH-OBJECTIVES-GROWTH | `88504d6ad582413689ca1e42653bc348` | Passed，四模式正式短搜、收益与零损限制、增量回放、Fork、无序缓存与强制重算一致 |
| SEARCH-OBJECTIVES-RESOURCES | `efa7410eadc644c3a803f2f763aeef77` | Passed，金币收益与生存／平衡／限制模式对照、增量回放 |
| SEARCH-OBJECTIVES-TARGET-STOP | `0b4cb333c5044043a4e43d31955c0213` | Passed，实际培养达标且安全获胜，求解器日志与协调器停止谓词同时命中，增量回放 |
| SEARCH-OBJECTIVES-UI-LOCALE | `9ab2ae3174e642678d3eb7f65c3908d9` | Passed，战斗目标下拉框／限制保存、eng/zhs/zht 切换及面板边界；headless 控件验证 |
| SMART-POTION-INVENTORY-OPEN | `54568b386eaf427ca79698ef58fa292b` | Passed，未满栏保留药水、预测战损 3 |
| SMART-POTION-INVENTORY-FULL | `a4eb30b03d0143588c8dd75c245512f0` | Passed，满栏使用 1 瓶、预测战损 0 |
| SMART-POTION-INVENTORY-NO-BENEFIT | `fe8283a5b6334695a1c56a9ed518cb50` | Passed，满栏但无收益时保留药水 |
| PROFILE-STRENGTH-SHIV-DEPLOY | `0210cd748c894b85a98b020351726173` | Passed，力量与生成小刀，T1 无损部署、零计划外重算 |
| PROFILE-STRENGTH-SHIV-NO-SETUP | `7662f42e678e4a6db3a35e82014c02d2` | Passed，无需力量铺垫，T1 无损部署、零计划外重算 |
| PROFILE-EXHAUST-DRAW-DEPLOY | `07fb7f35c312410d8958a17243cb4e1f` | Passed，消耗抽牌组合，T1 无损部署、零计划外重算 |
| PROFILE-NO-DRAW-NO-SETUP | `6d1cb121f36f4965815454625a174a17` | Passed，禁抽时跳过无效铺垫，T1 无损部署、零计划外重算 |
| TEST-SUBJECT-ORIGINAL-REPORT | `3e6e7f796ffa41fcaa6d52c859a12404` | Passed，0.34.8 天际钻头原包首回合至 T2 完整状态回归 |
| REPORT-ROUND-DOOM-THRESHOLD-CARD | `534b875dd61c47a3a536b32fdde66f3a` | Passed，0.34.8 末日降临临界击杀与复活跨回合完整状态回归 |

- 目标停止探针最初 `3b410fb57dd342a79fc9392c9bfb8950` / `09a49fc244794146be3db2a9c26d82fc` Failed。建局将故障机器人原生 75 HP 降至 70，被累计战损记录为 5 HP，违反探针的零损收益限制，实际选中无收益路线；这是夹具输入问题。固定 75/75 HP 后达标停止通过，提交夹具为 `coverage/unattended/search-objectives-target-stop.json`。没有放宽生产血量限制。
- 本轮未修改被提取功能的生产语义；仅修正缩进、剥离局外入口并新增停止回归。最后两次构建只改变测试诊断，既有成功场景不重复运行。
- Release 构建 0 警告／0 错误；SearchObjectiveChecks 227 项、PotionInventoryChecks 21220 项通过；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=86`；CoverageCatalog `--verify-effective --verify-state-fields --verify-state-writes --verify-branch-state-reads` 3035 项通过。
- 复跑入口为 `tools/run-unattended-test.ps1` / `.sh`：成长用 `SEARCH-OBJECTIVES-GROWTH` / DEFECT，手牌 GENETIC_ALGORITHM、STRIKE_DEFECT；资源用 `SEARCH-OBJECTIVES-RESOURCES` / IRONCLAD，手牌 HAND_OF_GREED、STRIKE_IRONCLAD。两者均 ClearRunDeck、ClearPlayerPiles、初始能量 1、格挡 0、敌生命 6，手牌 TreatAsDeckCard=true，120 秒。UI 用成长建局并指定 `SEARCH-OBJECTIVES-UI-LOCALE`。目标停止输入见上述新 JSON，机制和药水输入见 `coverage/unattended/profile-*.json`、`smart-potion-inventory-*.json`。原包和末日降临沿用 0.34.8 的命令。本轮没有原包整场质量 A/B、可见帧率或发布结论。

## 0.34.8 发布集成

合入已发布 0.34.7 后，补充验证剑圣冻结根与复制牌的重放次数，以及原报告的击杀边界。既有分批失败基线与最终证据保留如下；发布集成结果另记于本节。

- Release source commit：`8caa17f`。最终 Release 构建 0 警告 / 0 错误；以下四项均使用该构建、私有实例 `report-release-0348`、单请求 120 秒。
- `REPORT-CARDS-SWORD-SAGE` 合并基线 `661dd28f012a42cc944d4b7c47a27e6f` Failed：零层时跳过登记，已有复制牌后获得剑圣少一次重放。删除跳过后 `3a3c72ddb6ff4d67a53cc52e32bd216d` Passed，逐牌完整状态与 Fork 一致。
- `BACKEND-SWORD-SAGE-ROOT` / `75152668cc8440b28e4d1fd9b70fb24e` Passed：冻结根、首次移除、从零获得、生成牌和父子分支隔离。
- `TEST-SUBJECT-ORIGINAL-REPORT` / `5e6958f43351426f9836f262d029b2b9` Passed：5cc95 原包首回合全部动作前缀及第二回合完整状态一致，原生开战恢复通过。
- `REPORT-ROUND-DOOM-THRESHOLD-CARD` / `9603604deaf8416d8dd9eec3ea8f4bb7` Passed：末日降临与血肉戏法临界击杀、复活至下一回合完整状态一致。
- 集成结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`，CoverageCatalog `--verify-effective --verify-runtime-evidence` 3035 项通过；随后仅修改剑圣的零层基线登记，使用上述两项剑圣场景验证，覆盖登记与结构未改变。其余已通过的分批行为证据复用，未做整场或可见界面完整验收。
- 更新日志中英文游戏名称分别与当前游戏 PCK 的 `localization/zhs`、`localization/eng` 核对。

实验体批次与此前静默猎手批次的证据分别记录，原包恢复与最小差分分开计。以下分别保留失败基线、最终结果及未整场回放的范围。

## 2026-09-10：两份原始 Boss 复活错误的根因修复

- `TEST-SUBJECT-ORIGINAL-REPORT` 从5cc95原包已验证的首个可操作状态出发，按原始部署顺序使用OROBIC_ACID、FLEX_POTION、LIQUID_BRONZE，打SPECTRUM_SHIFT、TYRANNY、HEAVENLY_DRILL、BULWARK，结束回合并按原记录选择ASCENDERS_BANE。基线 `ebbcbc7c3e8d4f688944b20b87024b66` 在T2复现玩家HP97/103；逐前缀定位 `9516db27445a487ea2cecad9d34e185c` 首错是HEAVENLY_DRILL后敌HP8/0。最终 `2139e56df0aa41f5940ad6bca1cee627` Passed，全部七个动作前缀及T2完整状态一致。只验证原包首回合至第二回合，不声称整场通过。
- HeavenlyDrill 相邻场景：升级牌X=3 `4837b0e8c6a84f15b3fa71bc7f3c79ad` Passed；基础投入2能量加CHEMICAL_X达到4 `8c38386ff1f5428ab60a336aecd9b7a5` Passed。两场均比较原生/预测完整状态和Fork，敌人200HP避免过早击杀掩盖次数误差。
- `REPORT-ROUND-DOOM-THRESHOLD-CARD` 使用e476报告的施放前临界条件：敌134HP（最大213）、Doom34、SleightOfFlesh13、Duplication1、升级EndOfDays37。基线 `d4ffe973627244df921d003d529bdf52` Failed，跨回合玩家HP预测46/原生66，处决晚一阶段。最终 `339f3bf702874325bf13a36ea0b9c37f` Passed，完整状态、Fork、父前缀继续回放一致。夹具显式断言134HP/34Doom，注入Doom在SleightOfFlesh之前，避免建局触发13伤害改变临界点。该夹具采用第一形态验证共用处决/复活链，没有整场恢复e476至T6。
- 最初简化探针使用低HP，或注入顺序使施放前HP已降至121，均无法证明此临界根因；旧Passed不能替代本次失败基线。一次新镜像错误调用live卡ResolveEnergyXValue产生空引用，已改为PredictedCard分支入口，最终证据以上述Passed为准。
- 最终Release构建0警告0错误，CoverageCatalog `--verify-effective --verify-runtime-evidence` 通过；HeavenlyDrill/EndOfDays覆盖条目指向本轮针对性证据，未发布。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId TEST-SUBJECT-ORIGINAL-REPORT -CheckpointArchivePath <5cc95原包.zip> -CheckpointSelector start -ReplayMode RestoreOnly -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-ROUND-DOOM-THRESHOLD-CARD -CharacterId NECROBINDER -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 134 -InitialEnemyMaxHpsJson '[213]' -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 3 -CardsJson '[{"CardId":"END_OF_DAYS","Pile":"Hand","UpgradeLevels":1},{"CardId":"DEFEND_NECROBINDER","Pile":"Draw","Count":7}]' -PowersJson '[{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1},{"PowerId":"DOOM_POWER","Target":"Enemy","Amount":34},{"PowerId":"SLEIGHT_OF_FLESH_POWER","Target":"Player","Amount":13},{"PowerId":"DUPLICATION_POWER","Target":"Player","Amount":1}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
```

## 2026-09-10：原生恢复边界与编码诊断（开发中）

- `REPLAY-BOUNDARY-CONTRACT` / `2ed7ea141baa48748df3b3fcaea69cb7` Passed：旧两项/三项历史匹配、已记录历史和其他字段不一致拒绝、当前四项任一不同拒绝；边界观察器的原异常对象被等待链抛出，不变成缺失边界。
- 5cc95 原包基线 `08cf2adff9b24b8cbf4f9892da607222` 报缺失边界，内部首差异实际是 `Y=0/0/0` 与 `0/0/0/0`。最终 `579796fc4ccc469ebd1de83f618f737f` Passed / restored，开战及首个可操作检查点完整状态、原生二进制均通过，restorationVerified/nativeStateVerified/readyCheckpointVerified=true。
- e476 原包原先停在 hash，移除硬门禁后 `39c37d90f60b411d88cfc9797663729e` 的 ContinuationStamp 通过，但本机表解码旧二进制报 SavedProperty58 越界（本机47项）。最终 `befca951aa014dfd977c22479e2e203e` Passed / restored_continuation，两处已记录战斗状态通过；原生二进制未核验，restorationVerified=false、nativeStateVerified=false，原因明确为旧包未记录模型编号映射。
- 两包均使用原 ZIP、start / RestoreOnly / 120秒，只验证开战至首次可操作检查点（replayedEvents=0），没有回放 Boss 复活错误回合，不表示原始战斗逻辑问题已修复。
- 最终 Release 构建0警告0错误，CheckpointTool self-test 29项通过，结构门禁 REFACTOR_BOUNDARIES_OK search_files=78。未发布。

## 2026-09-10：程序集清单差异取消硬拦截（开发中）

- 基线：两份原包在 `ValidateCheckpointModsAfterStartup` 以 `environment_mismatch:mods` 提前退出。该判断将整个程序集数组直接比较，混入外观 Mod、加载器和辅助库。
- 现在按名称生成缺失、新增和构建变化诊断，写入 `replayVerification.modEnvironmentComparison`；继续原生模型解码、事件恢复和完整状态检查。不会仅因库存不同失败，也没有把未知 Mod 宣称为无影响。
- 原包 `5cc95…` 再次 RestoreOnly：`08cf2adff9b24b8cbf4f9892da607222`，通过库存检查并进入 `native_replay_events`，最终 Failed / `native_replay_missing_combat_start_boundary`。
- 原包 `e476…` 再次 RestoreOnly：`6bbfd2c3cdd141269552ca6b92557ac7`，库存差异完整保存，随后 Failed / `environment_mismatch:modelIdHash`。模型 ID 表校验保留并补充预期/实际 hash 的明确字段诊断。
- 两次都使用原 ZIP、`CheckpointSelector=start`、`ReplayMode=RestoreOnly`、单请求 120 秒；这证明库存差异不再挡住恢复，不证明原包恢复成功。最终 Release 构建 0 警告/0 错误，未发版。

## 2026-09-09–10：实验体汇总包（开发中）

34 份报告的逐包结论见 [分诊记录](issues/test-subject-reports-20260909.md)。以下均为本任务实际运行结果；语义夹具比较完整 MoveStateSnapshot / ContinuationStamp，包括逐实例有序牌堆、Power、怪物 AI、资源与 RNG，另检查 Fork。第三方和克隆事件场景验证各自的明确边界，不冒充整场差分。

| 场景 | 失败基线 | 最终 Passed |
| --- | --- | --- |
| NIGHTMARE-SELECTION-SNAPSHOT | `9d2befcaf31a418cbd2529142373e0fe`，副本费用 3/0 | `0985fa1d841844db8f2ddd35494424ee` |
| CLONE-EVENT-ISOLATION | `df880d27c9ab4538bafa80039ca96e3d`，调用实时订阅 1 次 | `5fcb0c2afdea4e24b7271b33d9037095`，0 次 |
| REPORT-CARDS-PANACHE | `6e393d8b0b44472ba59de7550756fe71`，实例合并 | `2316903dd0814a9387fc7c71bc1a73cb` |
| REPORT-CARDS-CRUSH-UNDER | `d484ff388a6e4378964ec7f388e9b3f8`，临时 Power/力量顺序 | `9bd9c9d07e614cdda06d86837235b5a4`，首次与叠加 |
| HISTORY-COURSE-EMPTY-TURN | `9bc30ec4b2a94894aeb070184f2386bb`，多重放旧攻击，敌 HP 39/45 | `8c69d71eb49c4a588351ddaaddff68c4` |
| REPORT-CARDS-SWORD-SAGE | `e07652667e314c0782291fd1220b6bb3`，复制牌少一次重放 | `516bd8a4c9534091b640e988ac34d1b4` |
| FOREGONE-IMPLICIT-ORDER | `fbf96f2e4beb41eb974746cd2aa160bd`，两张牌顺序反转 | `bb04cacf2f344e5a8a6c4a5137b8d7bd` |
| REPORT-ROUND-UNCEASING-HELLRAISER | `d64d5f079c8f4487abb573535ccde4f8`，手牌 5/3 | `d05fcc32fb5e4501a3695cb0728f1cab`，最终回合顺序源码 |
| REPORT-ROUND-HAILSTORM-ORBS | `39c741c197c04e1892a665f4c437b9b1`，目标 RNG 2/1 | `8e753acd438e40e89b2bceb873495a7d`；移除实机额外回合列表读取后 `bdcf0989998a4b51b258dc5012359329` |
| REPORT-ROUND-NOSTALGIA-STRIKE | `e1d57aa82cc54fd5890d8628508890a8`，第二回合攻击进错牌堆 | `fa776392afa3413b93ee93ce4e931550`；新增检查点历史校验后 `2bff954cd0a246c4abd406220823b202` |
| REPORT-CARDS-PALE-BLUE-ROOT | `12498ff2098a424eb7f9037a1f2ff5c4`，下回合抽牌 Power 2/1 | `733ed99ebed048e1b9d2933512fd6899` |
| REPORT-ROUND-HOWL-MUSIC-BOX | `5dc660636a54439c9ae036755c11e582`，手牌 5/6 | `88166b32b56541479cb7fa666adeee09` |

相邻与失败边界：FocusedStrike 临时集中 `a7119fb6a1dc4159b0a827333f563b31`；UnceasingTop 正常手动出牌 `0d16b0590bdf4515aea99871715e6a05`；已知 Mod ID/程序集别名、包装异常文案及上传分类 `dae3810cc97f492280d62de99ae04173`；FlexPotion、SpeedPotion 使用与回合末恢复 `a1a7783a7d784e0390a4e51d8806afa1`，全部 Passed。

Nostalgia 最终夹具另校验包含四项 `Y` 的检查点历史能被读取，并明确拒绝被篡改的攻击/技能开始数；只验证历史解析，不替代原包全状态恢复。

关键复跑命令（其余使用相同请求入口，夹具专用语义在对应测试文件）：

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-ROUND-NOSTALGIA-STRIKE -CharacterId SILENT -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 200 -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 10 -CardsJson '[{"CardId":"STRIKE_SILENT","Pile":"Hand"},{"CardId":"DEFEND_SILENT","Pile":"Draw","Count":7}]' -PowersJson '[{"PowerId":"NOSTALGIA_POWER","Target":"Player","Amount":1},{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-CARDS-PALE-BLUE-ROOT -CharacterId REGENT -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 200 -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 20 -CardsJson '[{"CardId":"DEFEND_REGENT","Pile":"Hand","Count":6}]' -PowersJson '[{"PowerId":"PALE_BLUE_DOT_POWER","Target":"Player","Amount":1},{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-ROUND-HOWL-MUSIC-BOX -CharacterId IRONCLAD -EncounterId TEST_SUBJECT_BOSS -EnemyCurrentHp 200 -ClearAllPowers -ClearPlayerPiles -InitialPlayerEnergy 20 -CardsJson '[{"CardId":"DEFEND_IRONCLAD","Pile":"Hand","Count":8},{"CardId":"STRIKE_IRONCLAD","Pile":"Discard"},{"CardId":"HOWL_FROM_BEYOND","Pile":"Exhaust","UpgradeLevels":1}]' -PowersJson '[{"PowerId":"DARK_EMBRACE_POWER","Target":"Player","Amount":1},{"PowerId":"ADAPTABLE_POWER","Target":"Enemy","Amount":1}]' -RelicsJson '[{"RelicId":"MUSIC_BOX"}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-TEMPORARY-STATS -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 100 -ClearAllPowers -PotionChecksJson '[{"PotionId":"FLEX_POTION","TriggerPlayerSideTurnEndAfterUse":true},{"PotionId":"SPEED_POTION","TriggerPlayerSideTurnEndAfterUse":true}]' -HeadlessInstance testsubject-batch -TimeoutSeconds 120
```

复活问题的未复现探针：`REPORT-ROUND-END-OF-DAYS-CARD` / `dcd3e25a127345c79b9693f292876b6d`、`REPORT-ROUND-ROOT-DEAD` / `f52b03efa1e74883ae3b29dfcea0f466`、`REPORT-ROUND-SECOND-FORM-CARD` / `4e45a941db034c28ae0d766ace29b9b4`、`REPORT-ROUND-SLEIGHT-DOOM-CARD` / `98fe9ed3bc664a0bb10e28f4b18f3e7b` 均 Passed；昨日直接击杀、毒杀、多段攻击探针也未复现。它们证明这些最小输入的原生状态一致，**不证明两份原始复活报告已修复**。原包 Preflight 材料有效，RestoreOnly 均 `environment_mismatch:mods`，没有恢复成功证据。

最终行为源码 Release 构建 0 警告/0 错误；`verify-refactor-boundaries.ps1` 得到 `REFACTOR_BOUNDARIES_OK search_files=78`。CoverageCatalog `--verify-effective --verify-state-fields --verify-state-writes --verify-combat-choices` 通过，3035 项、0 未分类状态字段、0 未解决范围内选牌源。最终移除实机额外回合列表读取后，`--verify-effective --verify-branch-state-reads` 通过。没有整场求解、可见 UI 验收或发布。

## 2026-09-09：0.34.6 静默猎手修复合并验证

- 将 `fix/silent-unexpected-replans` 的 `7f5a984` 合入包含 PR #67 / #68 / #72 的源码。合并后的 Release 构建 0 警告 / 0 错误；`CopyModOnBuild=false`，使用本机现有 .NET 4.8 引用包。结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`；CoverageCatalog `--verify-effective --verify-roster-sources` 通过。
- 以下为本次合并后的直接结果，均完成 T1 → T2 严格差分，比较有序牌堆、逐实例卡牌、Power、怪物状态、RNG、ContinuationStamp 和相关 Fork 状态。它们验证新监听器过滤与既有修复的组合，不代表原报告整场回放。

| 场景 | runId | 结果 |
| --- | --- | --- |
| MURDER-ROOT-HISTORY | `c40bf06fe6d34e669ba6faf6f77fee68` | Passed，28.13 秒；根捕获后实机抽牌、父子分支倍率隔离、原生出牌及下一回合 |
| TENDER-DISCARD-ALL-SLY | `c8492cd07e864f47a7c5cfb1b06c23a2` | Passed，16.59 秒；精密计算与内层狡猾自动牌各结算一次、属性与回合末恢复 |
| STOCK-REPORT-RESPAWN-HP | `9452a1f11d5a4143a7fd383fd4699054` | Passed，12.45 秒；原报告 Niche RNG 边界、反伤死亡与下一回合替补完整状态 |

- 使用下方原场景命令，私有实例改为 `silent-release-0346`，每请求期限 120 秒；证据位于集成工作区 `.local/silent-merge-evidence/`。结束后已停止该实例并精确删除其拥有的 `game` 快照，Steam 游戏目录未写入。
- 本次未重复已通过的无关纯计算/并行调度检查，未运行整场性能 A/B 或可见 FPS 测试。更新日志已审核，0.34.6 定版仅改变版本与发布文档，复用上述行为验证。

本次静默猎手三项根因最终共 5 个最小行为场景通过（3 项根因 + 2 项相邻回归）；Release 构建 0 警告 / 0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=78`，CoverageCatalog `--verify-effective --verify-roster-sources` 通过。以下分别保留失败基线、最终结果及未整场回放的范围。

## 2026-09-09：谋杀根历史隔离（开发中）

- `MURDER-ROOT-HISTORY` 基线 `40301c95dd4b4bfba22aa1abb4e3fa96` Failed，21.22 秒：实机抽一张牌后，冻结父分支倍率从 8 变 9、已抽一张的 Fork 从 9 变 10。最终 `11ea828e2e69419fa522008201fb5e5e` Passed，28.14 秒。
- 最终夹具覆盖原生抽牌前后的父分支/Fork 隔离、实际打出谋杀的伤害与完整状态、T1 至 T2 全量状态和下一回合抽牌后的倍率稳定。完整比较包括有序牌堆、卡牌实例、Power、怪物状态、RNG、ContinuationStamp；未正式搜索、未整场回放。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId MURDER-ROOT-HISTORY -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 100 -ClearAllPowers -ClearPlayerPiles -CardsJson '[{"CardId":"MURDER","Pile":"Hand"},{"CardId":"DEFEND_SILENT","Pile":"Draw","Count":7}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
```

## 2026-09-09：温柔与出牌效果内自动牌（开发中）

- `TENDER-DISCARD-ALL-SLY`：基线 `289cb0270eeb48d7b4ac81e3247c85e3` Failed，23.96 秒，首差异力量预测 -3 / 原生 -2；最终 `94c01fe41a564329a15f54ae62d66dc4` Passed，16.63 秒，验证精密计算与内层 FLICK_FLACK 各触发一次、Fork 完整状态及 T2 属性恢复和计数归零。
- `TENDER-NESTED-SLY` 手动选牌相邻对照最终 `45bf88eef731446cbe7d11d279b2a792` Passed，16.25 秒。旧源码该对照 `fe4d41bbc2ba4e29b9bceb72890c5e80` 已通过，说明错误发生在出牌效果内自动牌历史被父牌扫描的路径。最初 `c77511470ae4469d80038ce74ef7cca3` 因夹具漏放准备而建局失败，只作输入错误记录，不作语义失败基线。
- 两项均比较完整 MoveStateSnapshot/ContinuationStamp、逐实例卡牌、有序牌堆与 Power、敌人状态和 RNG，无正式搜索或整包回放。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId TENDER-DISCARD-ALL-SLY -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 100 -ClearAllPowers -ClearPlayerPiles -CardsJson '[{"CardId":"CALCULATED_GAMBLE","Pile":"Hand"},{"CardId":"FLICK_FLACK","Pile":"Hand"},{"CardId":"DEFEND_SILENT","Pile":"Draw","Count":7}]' -PowersJson '[{"PowerId":"TENDER_POWER","Target":"Player","Amount":1}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
```

相邻对照使用 `TENDER-NESTED-SLY`，把手牌换成 PREPARED 和 UNTOUCHABLE，其他参数相同。

## 2026-09-09：补货原报告 RNG 边界修复（开发中）

- `STOCK-REPORT-RESPAWN-HP` 从报告 `835b630a...` T9 检查点注入 Niche counter=54 及四段内部状态，以旧个体最大生命 96、当前生命 3、ONE_TWO_MOVE 和 3 点荆棘构造两回合边界。基线 `416f94d9708746018de96cb3a37efb26` Failed，26.64 秒，首差异 `E0.hp expected=103 actual=104`；最终 `70aa5edb08e34137b61fc3d2c75d7978` Passed，27.71 秒。
- 相邻直接击杀 `STOCK-RESPAWN-HP` 最终 `403c3b881b8c45f2a4168ced84cdf54a` Passed，12.87 秒。使用下方已有命令；报告 RNG 场景使用荆棘命令并将 ScenarioId 改为 `STOCK-REPORT-RESPAWN-HP`。
- 严格比较死亡后及下一玩家回合的完整 MoveStateSnapshot/ContinuationStamp，包括有序牌堆、卡牌实例、Power、敌人阵容和 AI、九条 RNG；预测 Fork 保持相同快照。未正式搜索、未整包回放。旧通用种子探针的通过只属于旧输入，不能代替本次失败基线。

## 2026-09-09：静默猎手补货最小分诊（开发中）

- 两个夹具均运行在未改生产语义的 `2c7bee5` 基础上，完整 MoveStateSnapshot 比较包含有序牌堆、逐实例卡牌状态、Power、怪物状态、RNG 和 ContinuationStamp，另比较预测结果与 Fork。没有启动正式搜索，不使用增量搜索开关。
- `STOCK-RESPAWN-HP`：`a32c5e203ed54ca281ef9d961c78789e` Passed，28.12 秒，比较直接击杀后的替补状态及 T2 边界。
- `STOCK-THORNS-RESPAWN-HP`：`d2d88c43f8f84e35a4b89f427e5654b4` Passed，27.56 秒，比较敌方攻击被荆棘击杀后 T2 的替补状态。
- 这些是未复现的最小探针；没有失败基线，不能视为 `835b630a19a44c7c897d63dfc56d9a49` 已修复或原包回放通过。

```powershell
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId STOCK-RESPAWN-HP -CharacterId SILENT -EncounterId AXEBOTS_NORMAL -Ascension 10 -EnemyCurrentHp 1 -InitialEnemyMaxHpsJson '[96]' -ClearAllPowers -ClearPlayerPiles -CardId STRIKE_SILENT -PowersJson '[{"PowerId":"STOCK_POWER","Target":"Enemy","Amount":1}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId STOCK-THORNS-RESPAWN-HP -CharacterId SILENT -EncounterId AXEBOTS_NORMAL -Ascension 10 -EnemyCurrentHp 3 -InitialEnemyMaxHpsJson '[96]' -InitialEnemyMoveIdsJson '["ONE_TWO_MOVE"]' -InitialPlayerBlock 20 -ClearAllPowers -ClearPlayerPiles -CardId STRIKE_SILENT -PowersJson '[{"PowerId":"STOCK_POWER","Target":"Enemy","Amount":1},{"PowerId":"THORNS_POWER","Target":"Player","Amount":3}]' -HeadlessInstance silent-replans -TimeoutSeconds 120
```

## 2026-09-09：PR #67 / #68 / #72 的 Windows 合并验证（下一版本开发中）

- 集成基线 `2c7bee5`，PR heads 分别为 `0baaf74`、`3c9edc9`、`e9e6103`。两个背包检查工具均链接最终唯一生产实现 `ReachableHandValue`。
- `dotnet run --project tools/PreCombatRequestChecks -c Release`：10 项通过；这是替换进程边界的 API 合同，未运行真实战前 worker 关闭/空闲期限测试。
- `ReachableHandValueChecks`：32,551 组原二维递推精确对照通过；`ReachableHandPotentialChecks`：10,000 组子集 oracle、溢出/大数组、零分配通过。三类常见 DP 各 100,000 次调用新增托管分配均为 0 B，仅代表纯计算。
- `PowerAmountComparisonChecks`：8 项通过，包括 1,568 组原生输出及 getter 顺序、实际 Harmony 重写、未知 IL 保留、100,000 次调用装箱分配 9,600,000 → 0 B。`ExpansionBatchChecks` 8 项及 `CombatSolver.WavePolicyChecks` 容量/溢出/10,000 组边界检查通过。
- `StateFingerprintChecks`：200,005 个混合/边界输入的原 128 位输出一致；`RitsuTargetTypeLookupChecks` 23 项通过，含程序集加载、动态类型后创建、可回收程序集、并发与 live 查询保留。
- Release 构建 0 警告 / 0 错误，使用 `-p:CopyModOnBuild=false`；首次因未配置 .NET 4.8 引用路径失败，指定本机已有引用包的 `TargetFrameworkRootPath` 后通过。Power 检查工具显式传入当前游戏及 RitsuLib 路径。Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=84`。
- 私有实例 `pr-merge-20260909` 复用同一 DLL，每请求期限 120 秒。下表均为本次直接运行；证据保留在集成工作区 `.local/merge-evidence/`。

| 场景 | runId | 结果与范围 |
| --- | --- | --- |
| STAND-PAT-PROBE-BATCHES | `a8b90d9a00894ff2b941af088221c33f` | Passed，59.94 秒；原投影死灵药水 fixture，Deep 1,000 节点及 Short 250 节点 DOP1/2 动作、评分与非时序计数等价，实际并行、取消/异常排空、复用、257 槽位、Fork 边界 |
| MIRRORED-HOOK-FILTER | `1af13fe1b62f462abb717b86794de11d` | Passed，9.22 秒；1,670 模型 / 55 回调，顺序、重复、外部类型、Fork、失效、补丁刷新、共享布局和监听分段 |
| HAND-POTENTIAL-COSTS | `47ebddc7b39745648eaa754f7c31cc25` | Passed，6.64 秒；REGENT、能量/星能各 3、专用牌组，5 项原生与重复查询费用相等，完整根与分支状态保持 |
| MIXED-POWER-ACQUISITION-ORDER | `5c39acf4594146b8ba4e39874ffe0da3` | Passed，11.34 秒；0.34.5 的有序 Power、牌堆、资源、ContinuationStamp 和 Fork 严格差分 |
| HELLRAISER-TURN-START-HISTORY | `1e91f087d1aa48bb94e0545aacefaf7a` | Passed，9.74 秒；0.34.5 的第 1 → 2 回合抽牌自动出牌历史及完整状态差分 |
| BACKEND-SWORD-SAGE-ROOT | `9f39cb7d612c46628e719fd96c556d4b` | Passed，5.50 秒；REGENT / SOVEREIGN_BLADE / SWORD_SAGE_POWER=2，冻结根、分支增减、生成牌、重复归一和 Fork 隔离 |

- 并行场景沿用 `docs/performance/perf2-integration-20260909.md` 的最小合同命令并加 `-VerifyForkBoundaries`；监听和费用合同在首根断言后停止。两个 0.34.5 回归使用其原场景参数，Power 顺序场景指定 `-EnemyCurrentHp 100`。
- 未运行新一轮整场性能 A/B、可见 Steam / FPS、完整发布门禁；其他章节的作者历史测量不能计入本轮测试。
- 所有请求结束后已停止该私有实例，使用 `Remove-HeadlessOwnedGameTree` 精确清理其带所有权标记的 `game` 快照；Steam 游戏目录未写入，诊断证据保留。

## 2026-09-09：快照/重放复查与上游合并

- 投影洗牌原型预定 B–C–C–B：8 个 Short/normal 测量请求 Passed，90 项原始结果及各 59/64 条动作一致；normal 均值变化 −0.216% 小于 3.190% 基线漂移，原型已撤回。
- 合并 `7ac005e` 后 Release 0 警告/0 错误，两端结构门禁通过（84 个 Search 文件）。只改提示文案的双语部分做资源键、CPU/DOP 档位和占位符 L0 检查。
- 合并版 `STAND-PAT-PROBE-BATCHES`：`4286e80ab6b145ca8e778c03ddb3a7e5` Passed；257 槽位、取消/失败排空、复用及 235,536 字节记账，Deep 1,000 节点 DOP1/2 结果一致、587 次待命探针。
- 上游语义与分支所有权交叉验证：`NORMALITY-AUTOPLAY-REPLAY` / `4c8a33fd786144e5aa57f6d56185d646` 与 `SLOW-TURN-RESET-FORK` / `f876d91c61a04a54aeaa7cb7a2f89157` Passed；前者含完整原生状态与开始次数、父子隔离、回合清零。
- 最新上游/合并版目标 `bdf47f6589014329997f292485c07025` / `ebb2fce7ed124f79a13cd14ad798ec39` 均 Passed：主搜索 10,000/144,276/101,420、评分和战损/药水、57 条完整动作相同；6 项物理调度字段与 3 项共享时限累计工作字段不同。哨兵 `c065f7cf7b9847da967e022940036c0d` / `d8437a044f4242909b42cf20328d6b9f` 的全部 90 字段、9 条动作一致。
- 独立缓冲日志通过正常战斗退役后读取已持有的只读文件句柄取得。首次战后问题包采集在未修改上游 `45f433b9306e4545b22ed18af4a0951d` 的导出阶段失败，未写为通过；后续正常首结果请求不带该不适用的导出断言。详细 runId、原始差异、复跑及未验证范围见[报告](performance/snapshot-replay-followup-20260909.md)。

## 2026-09-09：perf-2 选择性合入与保路作业

- 最终固定 VeryHigh/DOP8/NoGC16GB，预定 B1–C1–C2–B2–B3–C3–C4–B4，八个独立进程各预热一次再测 Short/正常。正常均值 23.6271→23.2887 秒（−1.43%），Short −2.98%；全部16个正式结果 Passed，88项非时序 RESULT 字段（含7项 deferred-round）与59/64条动作逐项相等。原90字段比较也全部无差异。保留慢样本和GC暂停，原配置与实际申请分开记录。
- 最终政策合同 `2cfdd334274942abbe817d1c670faa07` Passed：两个真实固定lane、257槽位各一次、原始取消token/异常、在途排空、失败后复用及235,536字节成功/失败分配；Deep1000节点/641待命探针的DOP1/2完整结果/评分/动作/非时序指标相等，原根可复用。
- 短搜哨兵 `e7bae27e545942799fe3335c9673c6a6` Passed，与 `a85f3e7` 既有 `9664c01a18084c92afe111f9d47ce7e8` 的90字段/9动作一致。1GB NoGC `3f2ab070c3fa4f88bfe29f0cbe1ae173` Passed：52次重启、0丢失，与既有 `2345c8785fac4c8fb6d9b81e72aa43ae` 的90字段/64动作一致；请求39.96秒，未用跨批次时间计算提速。
- 最终Release零警告/错误；纯容量合同涵盖部分wave、零、奇数上限和精确饱和溢出算术；Bash/PowerShell门禁均 `REFACTOR_BOUNDARIES_OK search_files=83`。本轮没有最终Engine语义改动，Hook索引原型已撤回，未执行的索引专属合同未列为通过。
- 复跑使用现有 `STAND-PAT-PROBE-BATCHES` 政策合同、固定Short哨兵和正常首结果参数，详见[报告](performance/perf2-integration-20260909.md)及[结构化结果](performance/perf2-integration-20260909.json)。Profiler不进入倍率；未做本轮可见Steam、Windows游戏、完整部署或增量性能。

## 2026-09-09：回合结束探针与元数据热路径

- 第二组固定 VeryHigh/DOP8/NoGC16GB、B1–C1–C2–B2–B3–C3–C4–B4 全部八个正式结果 Passed：37.6927→25.4926 秒，平均耗时−32.37%，分配−17.14%，采样峰值 RSS−16.10%。全部90项逐项比较；唯一差异为 C2 的 `phase/deep_triggered`，源码证明由20秒耗时检查点派生，原比较和分类修正均保留。其余88项非时序字段与64条完整预测动作全部一致；正常 Deep 预算、主搜索/恢复工作量和选择数相同。第一组−24.74%的失败结果完整保留，未删慢样本；候选仍有约2.1秒GC长暂停。
- 当前候选 `5d194a22bcc8496c998ba396f9873e83` Passed：55 回调/1,670 Model、ForkBoundaries，包含完整类型/接收者顺序、碰撞与并发替换、默认关键字原生对照、无前段锚点回退、Fork 后卡牌变更、有效前段及 Power 投影保留和父分支隔离。新候选 Release 零警告/错误。
- 最终候选 Deep 固定1000节点 `8effcf7a2e804c52867335cf32e636aa` Passed（请求56.99秒）：实际641次探针，DOP1/2全结果/动作/评分/续用/非时序指标相同，双lane同步屏障、取消/异常身份和在途排空、部分工作唯一记账、原根复用。
- Knights Short 哨兵 `9664c01a18084c92afe111f9d47ce7e8` Passed，与基线 `a4683db7ba964e259533372424f94250` 的90项字段/9条动作一致。正常Deep1GB NoGC 基线/候选 `591767d49821417d90d7134ce000d764` / `2345c8785fac4c8fb6d9b81e72aa43ae` 均 Passed：90字段/64动作一致，63/52次回收后保持46,239展开、636,428转移；搜索56.9968/42.4920秒，请求81.80/67.33秒，均在120秒内。
- `dotnet run --project tools/RitsuTargetTypeLookupChecks -c Release` 23项通过：实际生产回调、AssemblyLoad/动态晚建失效、失败不发布、live旁路、弱所有权。`dotnet run --project tools/PowerAmountComparisonChecks -c Release` 8项通过：1,568组实际原生方法对照、getter调用顺序、未知IL/内部标签旁路，10万次分配9.6MB→0。
- 最终源码 Release 构建4.07秒，零警告/错误；Bash和PowerShell结构门禁均输出 `REFACTOR_BOUNDARIES_OK search_files=82`。
- 复跑命令、全部runId、撤回原型、线程/GC口径和未验证范围见[报告](performance/standpat-and-metadata-20260909.md)及JSON。性能只来自正常headless，未运行本轮整场原生部署或可见Steam；增量回放不用于性能测量。

## 2026-09-09：已准入父节点内的动作与选择作业

- 固定 VeryHigh/DOP8/NoGC设置16GB、首结果停止、每请求120秒；预定A–B–C–C–B–A独立进程，每进程短搜预热一次后测短搜/正常配置。最终正常搜索39.4485/42.2771→36.0760/36.7188秒，均值−10.93%；原版首尾漂移7.17%，仅headless样本。32组探索及正式结果的83项非时序/非物理调度字段和59/64条预测动作一致。
- 最终 `d31a5136765943cda47a5200337310e5`：`SearchPolicySnapshot`、`ForkBoundaries` Passed。覆盖固定节点DOP1/DOP2动作/选择/评分/续用/工作与剪枝等价、DOP2实际并发≥2、根/分支隔离、512回放上限和首层准入保证；新取消/异常注入确认排空、原始token/异常身份、部分工作只记一次，随后复用原根完成求解。
- Release零警告/错误；`ExpansionBatchChecks` 8项通过，包含药水移交顺序、失败所有权和旧租约隔离；最终Linux结构门禁81个Search文件通过，Windows规则同步但未执行。完整配置、所有runId、原型、GC/RSS口径和复跑方法见[报告](performance/admitted-expansion-jobs-20260908.md)及JSON。未运行本轮整场原生部署、增量性能或可见Steam。

- 最终机甲骑士短搜哨兵 `a4683db7ba964e259533372424f94250` 与本轮原版对照 Passed：83项字段及9条动作一致，3448展开/8796转移。1GB NoGC目标压力 `8fa51a5571924f4d9a888188ad2574fd` 与本轮原版对照 Passed：83项字段及59条动作一致，10000展开/144368转移/101808选择；两版均14次NoGC建立、13次回收后重启、0区域丢失，实际并发8。

## 2026-09-08：CPU 微架构与指纹计算

- 生产基线 DOP8/1/2/4/8 曲线同 10,000 展开/144,368 转移/101,808 选择；正常配置 PMU 请求 `e304caaff9954817ae0e07ca6969277d` Passed，40.3314 秒、平均约 3.02 搜索相关核，仍仅死亡路线。PMU 为含游戏/GC/JIT 的进程用户态窗口，非纯搜索。
- 指纹改写 ABBA 正式请求：`246844f6fc7b44a0b3f7487fe0ea68c5` / `b3c12fffa86246559cf99069e9500466` / `e9b9f16df72442a0ad0e67c045be0463` / `f84d9e08202049338620882443e4bcb8`，全部 Passed；90 项非耗时/非并发调度字段及 59 步预测动作一致，均值约 −1.91%，每版两个样本，不作显著提速结论。
- `dotnet run --project tools/StateFingerprintChecks -c Release`：200,005 个边界/混合输入逐步对照旧 128 位公式，通过。相同本地 FullOpts 标量体 107→83 字节；Release 零警告/错误。未测候选正常配置性能、整场原生部署、可见 Steam、Windows 或 IBS/DRAM/伪共享归因。完整配置与局限见[报告](performance/cpu-microarchitecture-20260908.md)及 JSON。

## 2026-09-08：较大范围后端性能实验（全部撤回）

- 基线及五项原型均独立进程预热后正式短搜一次，12 个请求 Passed；正式同 10,000 展开/144,368 转移/101,808 选择及投影战损 3。基线 5.6707 秒，候选 5.7442–6.6062 秒；单样本没有明确提速，全部撤回，不计完整状态等价。
- 单条目与三条目 COW 字典各通过原生 Dictionary 对照：9 万次随机操作、32 分支、顺序/异常/枚举/比较器/键身份；三条目最终包含 Keys/Values 枚举失效时机。RNG 与无序牌堆合同草稿未构建或运行。
- 所有原型 Release 编译通过；恢复原生产源码后重新 Release 构建。没有保留行为变更，未追加正常配置 A/B、DOP/原生差分/整场/可见/Windows 测试。全部 runId、指标、范围和复跑输入见[报告](performance/bold-backend-experiments-20260908.md)及 JSON。

## 2026-09-08：实测CPU热点驱动的路由聚合优化

- 正常配置A/B：`0c7c5b59ab2542ffa06ca40680856281` → `5a64a1d297cf4dcb8060aa69a0ded3cc`，均Passed；41.1816→40.6614秒，同46,239展开/636,428转移/431,140选择、90项非耗时/非并发调度字段及64步完整预测动作，仍仅死亡路线。短搜5.8670→5.6931秒；只有单组正式样本，GC差异明显，不保证倍率。
- 最终策略合同 `9eb710f1f0e44877aa4fd3bdae084ee7` Passed：250节点DOP1/DOP2动作、评分、续用、非时序工作/剪枝等价，并发至少2，取消与冻结策略通过。
- 最终整场 `1e8dec2b7eda4d83b2b851ddd7babd5e` Passed：Instant/0秒、战损8/57HP/T7/无药/零重算，27条实际动作与上一轮一致。最终Release零警告/错误、Bash/PowerShell结构门禁通过。
- 其余九项原型撤回；额外聚合缓存虽更快但4项内部指标不同，本轮未归因，不计等价通过。没有可见、Windows游戏、增量或发布验证。完整请求、输入、指标、撤回理由见[报告](performance/backend-hotspot-optimization-20260908.md)与JSON。

## 2026-09-08：真实CPU热点与SwordSage根合同

- 根失败基线`08fae83abbc3476ab75c4c6ff6afdd84`确认捕获后live层数影响worker；仅根修复`630b137688304d3196be5c10a874c6bc`通过，最终`97c88a05ed9342528dda268da91836d6`通过首次移除、从零获得、分叉隔离、生成与幂等性，Release零警告/错误。
- Linux perf与EventPipe分开解释CPU、分配、GC和锁等待。正常极高两个诊断请求同46,239/636,428/431,140工作量，CPU调用链23,915个搜索样本；诊断耗时不作A/B，纠正旧线程采样口径。两个Hook索引原型撤回，零加成跳过未证明明显加速。
- 完整runId、最小合同命令、各原型数字、符号缺口和未验证范围见[报告](performance/backend-cpu-hotspots-20260908.md)及JSON。没有新整场质量或翻倍结论。

## 2026-09-08：现有后端架构审查（诊断，不更换后端）

- 临时线程计数版单请求`ba7606885e0a4de4814747b5b22452c1` Passed：极高/DOP8，10,000展开/144,368转移/101,808选择、Short/NodeLimit、投影战损3。统计1.69亿过滤Hook位置检查、362万成员交出、三段归一化4380万牌访问。冷运行且有计数开销，耗时不与生产性能比较，不据此声称完整状态等价。
- 离线重分析已有25秒分配采样，区分Fork/回放/快照等互斥类别，非墙钟比例。临时Release通过后已恢复全部生产源码并停止独立实例；最终只提交审计文档。源码锚点、计数定义、后续最小合同及未验证事项见[架构审查](performance/backend-architecture-audit-20260908.md)及JSON。

## 2026-09-08：极高配置状态与缓存实验（全部撤回）

- 基线及四项原型各独立进程冷预热后正式短搜一次：均Passed，10,000展开/144,368转移/101,808选择，投影战损3；正式5.9322/6.1887/5.9039/5.8503/5.8670秒。只有单样本，未证明明显加速或完整语义等价，全部撤回。
- 另做基线采样及一次完整回放重复率诊断，均不计性能倍率。没有保留行为变更，因此没有追加整场/DOP/可见回归；本次只提交记录。全部runId、输入、局限和固定工作量命令见[报告](performance/veryhigh-state-experiments-20260908.md)及JSON。

## 2026-09-08：极高配置有界父节点队列

- 同一极高药水输入，两版新进程各一次短搜预热后测正常配置。基线`adc1976b425041a291e8e5989f132a0b`与最终`b160b6539cd44ed39ea8aa937b7e89c9`均Passed，45.660→39.915秒、RSS16.735→18.323GB；46,239/636,428/431,140工作量、64步动作/目标/选择及非时序搜索字段一致，仍仅死亡路线。单组正式数据，GC暂停不同，不宣称两倍。
- 最终`SearchPolicySnapshot`：`5eb810366694428eb44d15e7427c63ca` Passed；250节点DOP1/DOP2动作/评分/续用/非时序工作剪枝等价、实际并发≥2，取消记账和冻结设置合同通过。
- 最终完整哨兵`f272c7ba66994d93b811a9001237a709` Passed：战损8/57HP/T7/无药/零计划外重算，27条实际部署动作与上一轮一致，Instant/0秒。Release与两端结构门禁通过；没有可见、Windows游戏或增量性能。撤回实验、复跑命令和所有runId见[报告](performance/veryhigh-parent-queue-20260908.md)及JSON。

## 2026-09-08：极高配置路由去重优化

- 目标 `a053d59f19074cac8a83644734fe62a0`：与压力基线同46,239展开/636,428转移/431,140选择、Deep/战损投影9/仅死亡路线；58.531→51.219秒、35.810→35.623GB。单样本且GC/短暂后台活动不同，未宣称严格倍率。
- 整场哨兵 `f4007c4a2da645e3b3cbbb22c4687eca`：Passed，战损8/57HP/T7/无药/零计划外重算，27条实际出牌与上一轮一致。
- Fork/路由合同合集最终通过（`7ae66da70a944340b8b1abcc3ad1cc9c`）；初次fixture缺牌组身份，补齐后遇到旧Power重获身份断言，原始基线同样失败。仅修正测试按既有规则要求新实例唯一注册和隔离，未改生产能力语义。Release零警告/错误及两端结构门禁通过。具体runId、输入、峰值和限制见[本轮报告](performance/veryhigh-routing-order-20260908.md)及JSON。没有跑可见、Windows游戏或增量性能。

## 2026-09-08：极高配置其他战斗压力筛查

- 在`56165ed`上跑10组不同遭遇/牌组的极高短搜层，另对药水组合和灵魂枢纽各跑一次允许深搜的完整配置，共12个有效请求。固定DOP8/NoGC16GB，单请求120秒、首结果停止；按用户要求不测可见会话。本轮没有改生产代码。
- 死灵药水组合正常配置 `0f0f6eaee52b4967a39a771bc8026671`：Passed但仅死亡路线；58.531秒、35.810GB分配、独立峰值RSS16.845GB，636,428转移/431,140选择，3次NoGC重启，观测最长GC暂停1,745.901ms。
- 灵魂枢纽正常配置 `3635c085260f4d97b0254646ae93a284`：Passed，返回Short且未触发Deep；10.948秒、7.856GB分配、独立峰值9.473GB，预测战损6/T9/无药。
- 2305张牌堆 `955b322152c94c4383efad630bf34c6a`：Passed/TimeLimit，搜索50.210秒（请求94.679秒），277展开/3,390转移，12.381GB分配，约3.65MB/转移。女王生成/选牌 `06304b105d6c41978b61ed3c52765d52`：18.950秒/18.136GB，预测有风险，仅作压力探针。
- 同一30张牌组的感染棱柱/花园幽灵鳗/灵魂枢纽/外骨骼虫保留原生敌人HP与开局，短搜约3.586/0.152/3.200/1.470秒。初次错误使用runner默认1HP的四条`screen-*`数据全部排除；旧死灵白名单快照缺角色身份而建局失败，也不计性能。
- 新增明确注入的死灵牌/遗物/药水三个JSON，通过数量、字段与原投影一致性检查；原有启动器无需改动。没有做新一轮A/B、整场部署/原生差分、增量或Windows/可见性能验证。全部runId、指标、修正输入与复跑命令见[压力报告](performance/veryhigh-pressure-survey-20260908.md)及其JSON。


## 2026-09-08：极高配置第二轮空回调与并行度优化（开发中）

| 验证 | 本轮直接证据 |
|---|---|
| 原版默认 Hook 分发合同 | `7c156ff511cd4581ad7a2748e3ddf7a2` Passed；54回调/1,670 Model，顺序、重复、外部类型、Fork模型身份、能力移除/重获、生成牌、根只读与补丁刷新 |
| 原生费用查询与嵌套选择 | 普通 `b3b157be5c204d51a887fbae08ee31e4` / 虚空形态 `6334a021b90e4387b056e864567cf28c` / pending `4fc3f48112b846f3889d6e99097b7f7b`，均Passed |
| DOP1/DOP2等价 | `63106d5c5894456ca03801bb485288ae` 的 SearchPolicySnapshot 完成：250节点、完整动作/评分/续用/非时序剪枝一致、实际并发≥2；随后原UI尺寸持久化失败，整个请求Failed，不计控制器合同通过 |
| 自动并行度与显式设置优先 | `4dfa3c784aae4241b7e6b8c4889a980e` Passed，CPU默认1/2/4/8及自动/显式设置解析 |
| 最终候选整场原生部署 | `39fae73f72f2477ba0663d511fab8ccb` Passed；46.75秒，战损8/57HP/T7/无药/零重算，Instant/0秒；27条实际出牌与上一轮同基线可见部署一致（仅质量对照） |
| 固定构建性能 | VeryHigh、DOP4→8、NoGC16GB；机甲骑士各预热一次+3正式样本，中位数13.3008→6.1037秒，34动作与83项非时序/非调度日志字段一致；大牌组压力/小啃兽42/14动作及相同字段一致 |

- 机甲骑士同战损8/T7/无药；压力场景仍是死亡边界，小啃兽零损/T3。压力/小啃兽速度2.69/1.79倍。机甲骑士分配+4.10%，每次峰值RSS中位数+10.79%，整批随后的小啃兽复用进程峰值+12.47%，不将分配量当成内存占用。冷搜索只有1.71倍；同4线程的代码收益约1.67倍。
- 启动器在首批未固定artifact的后续请求中切回默认DLL，`formal-a-*`作废；本轮正式表使用每次显式指定artifact的`verified-*`。构建0警告/0错误，两端结构门禁通过。合同复跑参数与全部runId见[第二轮报告](performance/veryhigh-hook-dispatch-20260908.md)和结构化记录。
- 用户明确暂不测可见会话；本轮可见启动未得到性能结果，临时安装/原生设置已恢复。未跑Windows游戏、完整覆盖/发布门禁、逐转移增量搜索；上面的DOP等价使用固定节点生产搜索，不是增量模式性能。


## 2026-09-08：极高配置等质量性能优化（下一版本开发中）

| 验证 | 基线 → 候选 runId / 结果 |
|---|---|
| 双资源背包独立 oracle | `dotnet run --project tools/ReachableHandPotentialChecks -c Release` Passed：10,000随机子集最优解、原递推溢出/大容量边界、常见规模零分配 |
| HAND-POTENTIAL-COSTS 普通 | `be17b695fd114333baa185bc8e21ba1c` Passed：5张可打出牌费用与原生/重复查询相同，含X费用、条件/不可打出卡，根与分支完整状态不变 |
| HAND-POTENTIAL-COSTS 虚空形态 | `6bfe3e401e3e4a0b8b8dea7d259bdcc2` Passed：相同查询只读合同 |
| Steam 可见机甲骑士完整部署 | `2375d506a3ac455e94b6ccf9cefbed96` → `c2c1e8f6ee584fd1bbb5066b1bd98d83`，均Passed，战损8/T7/无药/零重算，27条实际出牌完全相同；初次搜索19.5643→18.7779秒 |
| Steam 可见小啃兽独立进程内存 | `eb56a536a72a4b1a904153f35eafbbc6` → `60f65ab1bddb410da8de39dcfe26fe12`，均Passed，预测零损/T3/14步路线一致；首结果停止，峰值RSS2,957,713,408→2,932,641,792B |

- 所有性能请求仅VeryHigh，固定DOP4/NoGC16GB，未调搜索预算，未启用增量或详细阶段诊断。Headless机甲骑士各预热一次后各取3次，中位数耗时−4.76%/分配−1.80%；大牌组压力与小啃兽各一组同工作量哨兵，非时序指标/动作完全相同。压力场景仍为死亡边界，不是胜利证明。全部runId及数值见[性能报告](performance/veryhigh-quality-preserving-20260908.md)与其结构化记录。
- Release编译0警告0错误；Bash/PowerShell结构门禁Passed（`search_files=78`）；两端可见脚本语法检查Passed。PowerShell本轮仅语法及结构检查，未在Windows启动Steam。
- 费用合同的100ms收尾搜索不计性能，复现牌组为 `coverage/unattended/hand-potential-cost-cards.json`；命令见性能报告。可见基准新增 `--request-fixture-path` / `-RequestFixturePath`，完整请求为 `coverage/unattended/performance-veryhigh-mecha-native.json`，同报告记录首结果与整场部署两种断言。
- 完整Mod组合的基线在搜索前因未支持的AveMujica subscriber失败（`cd8e58d396344dec8e0b2362f216879b`，120秒超时）。成功可见结果只覆盖原版+RitsuLib+CombatSolver，未延长超时。基线整场采样器在退出时失败而缺少峰值文件，测试结果有效；其整场结束RSS已高于候选记录的VmHWM。Headless复用进程小啃兽曾有+0.07%峰值反向样本，独立可见进程对照未复现；不作全场景逐样本内存保证。

## 2026-09-09：可达手牌估值分配（开发中）

- `dotnet run --project tools/ReachableHandValueChecks/ReachableHandValueChecks.csproj -c Release`：32,551 组与原二维 DP 精确相等，包含空手牌、免费牌、双资源、较大数组回退、负可用资源、重复牌和价值和溢出。测试直接链接生产纯计算源码，原递推作为对照。
- 固定 100,000 次调用的局部分配检查：单资源受限 5,600,000 → 0 B；双资源受限 13,600,000 → 0 B；全部可负担 29,600,000 → 0 B。仅统计 DP 计算，不包含游戏模型、候选费用捕获和整个 Snapshot；不代表整场分配降幅或可见帧率收益。
- 当前可见会话的一条 RESULT 记录：40,105 次总转移、约 2.00 GB 总 worker 分配、2,424 ms 总搜索时间、最大主线程帧间隔 24.6 ms，未记录超过 33 ms 的帧或 GC 暂停。这是在线观察，未作为隔离 A/B 基准。
- 未启动额外游戏进程，未修改运行中会话；固定战斗搜索 A/B、增量回放和可见性能验收未执行。纯估值等价检查不代替这些项目。

## 2026-09-09：战前预测请求选项（开发中）

- 独立回归入口：`dotnet run --project tools/PreCombatRequestChecks/PreCombatRequestChecks.csproj -c Release`，Windows / Linux 相同。直接链接实际 `PreCombatForecastApi` 与 contract 源码，替换游戏/进程边界，不启动游戏。
- 覆盖强制重算绕过运行中/已完成结果、同选项去重、不同关闭/空闲要求分开请求、缓存命中应用关闭/有限或无限空闲期限、脱离式取消、独占取消等待清理、已取消缓存命中不改变设置以及 live 状态过期。
- 上述 10 项独立检查通过，Linux 结构门禁返回 `REFACTOR_BOUNDARIES_OK search_files=78`。同一测试链接修复前 API 时，强制重算用例因没有第二个 worker 请求而超时，缓存生命周期用例因未调用生命周期入口而失败。
- 完整 Mod 构建尝试返回 32 个 `CS0246` 缺失游戏类型错误（包含 `CombatTurnState`、`CardLocation`）；Windows worker 进程回收未验证：本机游戏为 `0.107.1`，仓库要求 `0.111.0`。独立 API 检查不代表游戏模拟或进程生命周期验收。

## 2026-09-09：历史敏感 Power 顺序（0.34.5）

- `MIXED-POWER-ACQUISITION-ORDER` 失败基线 `6eba58023b214ead8829ed5effbad6ee`：旧逻辑在已有小刀/攻击/格挡历史后临时停用并恢复 Power，首个差异为 `P[1] expected Strength actual PhantomBlades`。
- 最终 `8f5c8d96d9e342e8a2163e54e359c0d2` Passed，26.26 秒。夹具先在没有目标 Power 时各打一张格挡牌和小刀，再按代表报告顺序获得幻影之刃、力量、第二个轨道、敏捷、致死性和不动；继续各打一张牌后施加虚弱，完整比较生命、格挡、能量、牌堆、有序 Power、ContinuationStamp、Fork 和父分支隔离。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId MIXED-POWER-ACQUISITION-ORDER -EnemyCurrentHp 100 -HeadlessInstance unexpected-replan-power-order-final`。最终 Release 构建 0 警告 / 0 错误。
- 代表报告 `4b188b2e088c4826ba1b14d0252bd3bf` 只直接核验元数据与独立日志；没有整包恢复或正式搜索。其余 19 份按相同首个 Power 顺序差异和相同执行路径静态归组，不能表述为 20 份整场重放通过。

## 2026-09-09：狂战士回合开始自动出牌历史（0.34.5）

- 代表报告 `c68216599d1a439b972d4161a2135988` 的第 2、3 回合均为 `Y expected=0/0/0 actual=0/0/1`；独立日志确认狂战士在抽牌时自动打出 Strike。只读取代表包日志，没有整包恢复。
- `HELLRAISER-TURN-START-HISTORY`：`5bbb6f1081aa4206ae077b8aff733194` Passed，24.82 秒。从第 1 回合推进到第 2 回合，预测与实机都在抽牌前开始新的历史窗口；狂战士自动出牌后严格比较生命、资源、四个牌堆、有序 Power、Power 内部状态、RNG 与 ContinuationStamp，并显式断言本回合出牌开始次数为 1。
- 关联回归 `NORMALITY-AUTOPLAY`：`f014ee4080bd4191805e67096eef7e8b` Passed，10.51 秒，覆盖同一出牌开始计数的根捕获、Fork 隔离、下一回合归零和被阻止自动牌的牌堆结果。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId HELLRAISER-TURN-START-HISTORY -HeadlessInstance unexpected-replan-power-order-final`；回归只替换 ScenarioId 为 `NORMALITY-AUTOPLAY` 并加 `-EnemyCurrentHp 100`。Release 构建 0 警告 / 0 错误。
- 同组共 9 份 / 7 场；代表报告仅日志直接核验，其余按同一 `Y` 首差异与抽牌自动出牌路径归组。没有把正常 `manual_divergence` 纳入修复数，也没有运行正式搜索或逐包整场部署。




## 2026-09-08：凡庸与自动打牌（0.34.4）

- 原报告 `12f213c23ccd4a00abaf7a80c796273e` 的第 6 回合在发现、彼岸咆哮后打出倾泻，Normality 仍在手；原版结束回合复核为 25 HP，计划为 0 HP。日志定位后直接构造最小夹具，没有运行原包恢复。
- `NORMALITY-AUTOPLAY` 失败基线 `b331a29d04dc4587a797ad2c5074eb78`，23.63 秒：两张防御后打倾泻，模拟格挡 27、原版 10。修复后 `5df3975b9e874fb1a34f5f966dc7f54d` Passed，25.97 秒，比较完整 MoveStateSnapshot/ContinuationStamp，包含有序牌堆、资源、能力和 RNG。
- `NORMALITY-AUTOPLAY-REPLAY`：`21766a0d3d3149d485d09baf36885cf3` Passed，26.44 秒。第一张牌被回响形态重复打出，两次开始加倾泻开始达到三次；验证计数不以“手动动作数”或“已完成次数”代替开始次数。
- 两组还比较：凡庸阻止的自动牌去向、诅咒离手后允许自动牌、预测/Fork/原版重捕获的开始次数、子分支回合清零及父分支保持。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId NORMALITY-AUTOPLAY -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance normality -TimeoutSeconds 120 -ExitOnComplete`；第二组替换 ScenarioId 为 `NORMALITY-AUTOPLAY-REPLAY`。Linux 使用同名 `.sh` 与对应长参数。
- Release 行为构建、Windows 结构门禁及 CoverageCatalog 的 effective/state-fields/autoplay-sources 检查通过。未运行正式搜索、整场部署、原包恢复或可见 FPS/交互验收。

## 2026-09-08：卡牌语言往返刷新（0.34.3）

- `UI-LOCALIZATION`：`1e7e7f53067f4c34a1732b6c5b63c033` Passed，24.96 秒。新增英文已保存 PlanAction / UI snapshot，构造一次真实胶囊，依次切 zhs / eng / zhs，断言标题、升级符号、选牌、tooltip 与游戏译名一致；JSON 往返及旧计划内容保持原样。
- 同时验证销毁控件后订阅数量恢复、语言切换不改变搜索状态或计划外重算计数，并继续通过 eng/zhs/zht 的 321 条文案和 20 类遗物摘要等既有合同。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId UI-LOCALIZATION -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance i18n-refresh -TimeoutSeconds 120 -ExitOnComplete`；Linux 使用对应 `.sh` 与同值长参数。
- Release 行为构建 0 警告 / 0 错误；Windows 结构门禁通过。没有启动真实搜索或整场部署，没有可见交互/排版/帧率验收；只新增显示元数据，不改变模拟结算。

## 2026-09-08：胶囊附加信息本地化（0.34.2）

- `UI-LOCALIZATION` 扩展合同 `43bdbcdfc4eb4bf68c1bd65746412e44` Passed，24.58 秒；eng/zhs/zht 分别覆盖 321 条文案、20 个遗物摘要样本、毒/荆棘/能力规范 ID 与类型名/充能球/敌方行动/未知来源、嵌套选择与空选择、药水标记、遗物胶囊和 tooltip 一致性。
- 捕获显示名后切换实时游戏语言，再在 Task.Run 中读取名称，验证 worker 输出仍使用已捕获语言。第三方自定义摘要样本与纯倍数保持原样。未重跑伤害模拟或整场搜索，改动只涉及显示。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId UI-LOCALIZATION -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance i18n-annotations -TimeoutSeconds 120 -ExitOnComplete`；Linux 使用对应 `.sh` 与同值长参数。
- Release 行为构建 0 警告 / 0 错误，Windows 结构门禁通过。无实机交互/视觉验收或帧率测试。Steamworks 对 schinese、english 的两次描述更新各返回 EResult.OK；仅更新元数据，未上传二进制。

## 2026-09-08：简化中英双语 UI（0.34.1）

- `UI-LOCALIZATION`：`5c12f39867df4f75beb28db5a593e840` Passed，23.72 秒。覆盖 319 条资源的占位符/数字格式、嵌入文本保持原样、eng/zhs/zht 语言选择、英文设置和上传弹窗控件/全部下拉选项无中文、设置切页、上传完成/取消状态、动态路线标题和失败引导。没有发送公网上传请求。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId UI-LOCALIZATION -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance i18n -TimeoutSeconds 120 -ExitOnComplete`。Linux 使用对应 `.sh` 与 `--scenario-id UI-LOCALIZATION --encounter-id BYGONE_EFFIGY_ELITE --headless-instance i18n --timeout-seconds 120 --exit-on-complete`。
- 行为构建 0 警告 / 0 错误，Windows 结构门禁通过，双平台门禁同步禁止 Search 引用 SolverText；之后仅同步版本、文档和结构门禁。没有执行可见布局/交互验收、帧率测量或整场搜索，headless 合同不代表这些项目通过。

## 2026-09-08：战斗独立日志（0.34.0）

- `dotnet run --project tools/DiagnosticLogTests/DiagnosticLogTests.csproj -c Release` 通过：冻结提交前缀、跨战斗摘要化、旧 worker 会话隔离、跑局摘要隔离、积压上限与显式不完整状态。1 万次入队调用约 6.91 ms、93.4 B/次，峰值待写计费 2,817,000 B；仅进程内微基准，不代表实机帧率。
- `COMBAT-DIAGNOSTIC-LOG` 验证正常增量/根回放完整状态相等、人为注入 5 HP 差异后的首个动作定位、Damage/Heal 原生严格差分、失败候选日志与异常传播、独立日志 ZIP。`a5abab57628642d584802ba2d275bfce` Passed，23.23 秒；初次运行因旧归档测试仍要求最多一份全局日志而失败，更新到独立日志合同后通过。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId COMBAT-DIAGNOSTIC-LOG -EncounterId BYGONE_EFFIGY_ELITE -HeadlessInstance diagnostic-log -TimeoutSeconds 120 -ExitOnComplete`；Linux 用对应 `.sh`、`--scenario-id`、`--encounter-id`、`--headless-instance`、`--timeout-seconds`、`--exit-on-complete`。
- Windows 结构门禁通过。未做可见游戏帧率验收、整场部署或公网上传测试；提交协议未变，此场只导出本地问题包。
- 最终合同 `6a1ff383efa84437a87b8eafc92843d1` Passed，23.37 秒，另覆盖正式搜索的最终路线物化；随后仅同步版本和文档。行为验证构建仍标记 0.33.9，发布构建统一为 0.34.0。

## 2026-09-08：在线监控登录持久化

- `node --test tools/OnlinePresence/test.mjs tools/OnlinePresence/pagination.test.mjs tools/OnlinePresence/history.test.mjs tools/OnlinePresence/session.test.mjs`：10 项通过。
- 会话接口验证同 IP 日志 Cookie 共存、重启后复用、14 天过期、退出撤销、修改密码失效；前端 VM 事件验证首次等待鉴权、成功直达后台、401 显示登录、网络失败重试。
- 未做交互级验收，未启动游戏；独立服务更新不需要 Mod 构建。

## 2026-09-08：旧日雕像缓慢跨回合分叉（0.33.9）

- `SLOW-TURN-RESET-FORK` 失败基线 `21070eeed4b84c628ae7ac7f6ebe1cdf`：敌方回合开始后 Fork 抛出 `SlowPower has no fork mapping`；最终 `2602bde5ac1c45f7af0344dc7cd6153a` Passed，24 秒。
- 严格比较完整 MoveStateSnapshot / ContinuationStamp：两次打击累积、敌方阶段清零、清零后分叉、再次打击伤害和动态变量；同时断言能力实例身份、状态指纹相等，以及清零和再次出牌均保持父分支隔离。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId SLOW-TURN-RESET-FORK -EncounterId BYGONE_EFFIGY_ELITE -ClearAllPowers -ClearPlayerPiles -CardsPath coverage/unattended/slow-turn-reset-fork-0339-cards.json -EnemyCurrentHp 500 -InitialPlayerEnergy 10 -TimeoutSeconds 120 -ExitOnComplete`。Linux 使用同名 `.sh` 和对应长参数。
- 51 份报告 / 40 场战斗的异常详情全部相同，只下载代表包 `c6e67f18952f4d1d9c9d3a4ac304b095`。Preflight 为 materials_valid；原生回放尝试 `0b29cb9ed6044ac9b702e53599111f18` 因 `environment_mismatch:mods` 被拒绝，restorationVerified=false。一次夹具启动使用了不存在的 BigDummy 遭遇名，修正为旧日雕像后取得上述基线。
- 行为源码 Release 构建 0 警告 / 0 错误。本轮没有运行整场自动部署、增量搜索、可见 Steam 或完整发布门禁；最小生命周期差分未启动搜索。

## 2026-09-08：问题包 v2 与 miaovps（0.33.8）

- `REPORT-V2-CONTRACT` 最终 `932296db26ac4e4ebb7b1432e823fddd` Passed，23 秒。覆盖真实 ZIP 导出、新目录与检查点材料、主线程战斗/角色/怪物元数据、未知和正/零/负战损下降值、multipart 字段、响应与取消边界、正式 HTTPS 证书校验上传及回执。命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId REPORT-V2-CONTRACT -HeadlessInstance report-v2 -TimeoutSeconds 120 -PreserveNativeCombatStateForTest -ForceShortSearchOnly`。
- 前一轮 `2a434cb1ea244452993488ee635f89b8` 上传合同通过；状态注入包预检明确返回 diagnostic_only:test_fixture_state_injection，不算恢复有效。改用保留原生状态后，新包 `bc43f15bc9234456a1d82ddf52eaa8d8` 的 preflight 为 materials_valid / restorationVerified=false。
- `CheckpointTool self-test` 29 项通过，覆盖旧目录、无索引、材料配对、路径拒绝和批量去重；新实际包路径由上述 preflight 验证。没有执行整场恢复或可见游戏交互。
- 独立后端 `python -m unittest -v test_reports_v2` 6 组通过：单次及并发去重、身份冲突、元数据与 ZIP 一致性、非法包清理、旧上传与管理鉴权、组合筛选及批量清单/删除一致性、未知/零/负差值和范围校验。
- 公网后台按真实 Mod 包元数据组合筛选及鉴权下载字节一致通过，测试报告删除。后端用户服务已更新；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=77`，行为源码 Release 编译 0 警告 / 0 错误。
- 列表按后续要求移除接收时间、联系、大小、已解决和备注列，改为横向元数据列、全宽页面与两行描述。新增列结构测试，并重跑受影响的组合筛选/批量和旧包鉴权测试，共 3 项通过；公网 HTML 已确认更新。未执行浏览器交互或像素验收。

## 2026-09-08：高频计划外重算（0.33.7）

| 场景 | 失败基线 runId / 差异 | 最终 runId / 结果 |
|---|---|---|
| MIXED-POWER-ACQUISITION-ORDER | `b8a4e65825d5470b887a535e7d2ad399`，P[1] Strength / PhantomBlades 顺序不一致 | `cb0fb008d094436fbb611ec62dbc1689`，Passed，24 秒 |
| REMOVED-POWER-REAPPLICATION | `c4cccbfbd04e4d3fbe0dc5dabd8a6b48`，DrawCardsNextTurn 的 AmountOnTurnStart 预测 5 / 原生 0 | `7cb00c33817b4521af540c868f187376`，Passed，8 秒 |
| SUMMONED-ALLY-POWER-ORDER | `a4659ef9aa474e0396b38667fbeeed7e`，敌方 Strength 在奥斯蒂 DieForYou 前面 | `7f93620fda4d4219a5bc49dc2ce37252`，Passed，8 秒 |
| ENERGY-RESET-POWER-ORDER-REAPPLY | 既有相邻回归 | `98d8b8e3ec1e497dbcbcfa74d77e98d5`，Passed，12 秒 |
| SUMMON-DEATH-POWER-ORDER | 既有敌方召唤/死亡两回合回归 | `78120e49e44449f494f275173edd57f5`，Passed，18 秒，T1→T3 |

- 前四场命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId <场景> -ClearAllPowers -ClearPlayerPiles -EnemyCurrentHp 500 -HeadlessInstance report-fixes -TimeoutSeconds 120`。最后一场使用 `-ScenarioId SUMMON-DEATH-POWER-ORDER -EncounterId FABRICATOR_NORMAL -EnemyCurrentHp 500 -HeadlessInstance report-fixes -TimeoutSeconds 120`。
- 比较完整 MoveStateSnapshot / ContinuationStamp；混合能力夹具覆盖多实例与普通能力交替获得、同类多个实例、Fork、子分支移除后重获及父分支隔离。既有重获回归还检查指纹与实际能量重置 Hook，召唤回归覆盖原生跨回合状态。正式搜索未启动，未开启增量搜索验证。
- 最终行为源码 Release 构建通过，0 警告、0 错误；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=77`。一轮启动因子进程未暴露 executable path 失败，未进入行为断言，重新启动后取得上表证据。基线期间一次并发启动被实例锁拒绝，后续请求全部串行复用同一实例。
- 188 份报告去重为 179 场，三类症状分别匹配 98/5/3 场；计数不是逐包通过率。外层 Preflight 超大小限制；拆分代表包 Preflight materials_valid / restorationVerified=false。未执行原包完整恢复、整场自动部署、可见 Steam 验收或完整发布门禁，详见 [分诊](issues/report-replans-20260908.md)。

## 2026-09-08：Issue #63 局外收益评分上限（0.33.6 开发中）

- `LONG-TERM-RESOURCE-BEAM-CAP` 失败基线 `08689d31bcec4f6ea3bb90ac40cd94d1`：25 点资源加分 625,000，额外自伤 1 HP 后仍高于父状态 565,000 分。
- 最终 `1dc49c905e36411781d7bf0f1ea66b74` Passed，22 秒。直接调用正式 Snapshot，对 1/25/1000 点资源分别比较无伤与自伤分支，验证总加分小于单项 HP 权重、真实资源值保留且未生成成长额度。
- `GROWTH-POLICY-FREE-FIRST`，`e373a06013e24124a17d1f1f3caa375e` Passed，21 秒；`GROWTH-POLICY-PAID`，`d9aca404d377479aa52d334e2f1ed10e` Passed，21 秒。覆盖免费成长优先、零额度拒绝额外战损、允许额度内付费成长、忽略收益开关、第三方额度合同与增量回放。时间来自测试模式，不用于性能结论。
- 新合同命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId LONG-TERM-RESOURCE-BEAM-CAP -HeadlessInstance issue63 -EnemyCurrentHp 100 -TimeoutSeconds 120 -ExitOnComplete`。两项成长用例沿用本文已有命令并加 `-ExitOnComplete`。
- Release 编译通过。Issue 附件仅 Preflight materials_valid，未完整恢复观者 Mod 跑局、未证明该包原先 21 HP 差距已消除；未跑全角色/全量性能基准，不声称有限搜索绝不漏解。0.33.6 保持未发布。

## 2026-09-08：PR #59–65 整合验证（0.33.6 开发中）

- 合并 #59、#60、#61、#62、#64、#65；#63 是 Issue。本节记录本轮直接证据，下方各 PR 原始记录保留其提交时的验证范围。
- Release 构建通过，0 警告、0 错误；结构检查通过，`REFACTOR_BOUNDARIES_OK search_files=77`。
- `GROWTH-POLICY-FREE-FIRST`，`4da884fe105d41a7865ff403988f392b` Passed，22 秒。覆盖第三方成长来源登记、额度/设置往返、Fork、侧栏总开关及免费收益搜索；整合后第三方额度与原版额度共同置灰。
- `PR60-65-CONTRACT`，`348d44e4a0b24db39cbd712f3570e5a0` Passed，22 秒。直接运行死亡补货胜利判定与第三方移除偏置合同，不扩跑整个 Fork 批次。
- 中间运行 `20f2a7b0333444609de93c417ffdf311` 的补货检查已通过，移除测试失败：替身牌通用估值 12 加 -10 为 2，原断言要求负值。测试偏置改为 -20 后通过，生产估值逻辑不变。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId PR60-65-CONTRACT -HeadlessInstance pr5965 -EnemyCurrentHp 100 -TimeoutSeconds 120 -ExitOnComplete`；成长夹具沿用下方 FREE-FIRST 命令并加 `-ExitOnComplete`。
- 本轮未重跑 API v6 的隔离 worker 样本，未跑节点预算的长循环性能基准、付费成长夹具或完整发布门禁；不能将作者历史证据视为本轮通过。未创建标签、发布包或上传工坊。

## 0.33.6 跑局统计

- 服务端 8 项测试通过，增加非战斗但 inRun=true 计入、inRun=false 排除、旧客户端缺失状态及非法字段拒绝。
- ONLINE-PRESENCE-CONTRACT：`5e4e6e2222d94e578103f46821fc3e58` Passed，22 秒；验证原生跑局标志与缓存时保留当前状态、TLS 与关闭持久化。未逐一自动进入地图/商店/事件界面。
## 第三方局外成长来源登记入口（开发中）

- Release 编译（`-p:CopyModOnBuild=false`）0 警告 0 错误，未复制到游戏目录。
- `GROWTH-POLICY-FREE-FIRST` / `GROWTH-POLICY-PAID` **本轮未执行**。新增断言 `AssertThirdPartyGrowthSources` 挂在这两个夹具原有的 `-VerifyGrowthPolicy` 路径上，复跑命令沿用本文《2026-09-07：局外成长策略》一节记录的原命令，不需要新参数。
- 已单独验证 `GrowthValues` 的 System.Text.Json 往返机制（record struct 定位构造函数加 `init` 属性、`JsonIgnore(WhenWritingNull)`、字典键不受 `PropertyNamingPolicy` 影响）：空表不写出 `thirdParty` 字段、有条目时往返相等并保留未登记 id、缺字段与显式 `null` 都还原成空表、`with` 表达式保留第三方部分、第三方条目参与相等判断。该验证在独立控制台工程完成，不进仓库。
## 不考虑局外收益开关（开发中）

- Release 编译（`-p:CopyModOnBuild=false`）0 警告 0 错误，结构门禁通过。
- `GROWTH-POLICY-FREE-FIRST` / `GROWTH-POLICY-PAID` **本轮未执行**。新增断言直接加在这两个夹具原有的 `-VerifyGrowthPolicy` 路径上，复跑命令沿用本文《2026-09-07：局外成长策略》一节记录的原命令，不需要新参数。新增覆盖：开关默认关闭、设置往返、原始额度保留而 `EffectiveGrowthBudgets` 归零、`EffectiveHasGrowthTargets` 归假并让可接受战损早停重新生效、开着开关求解仍然取胜、成长信用为零而快照其余两项保持如实（偏好开关不是状态开关）、战损不超过零额度基线、付费夹具下战损严格小于满额度那次、侧栏开关回读与额度置灰、点击开关翻转。
- 未做原生实机验证、未跑 248 条原版回归、未执行完整发布门禁。
## 节点预算按回合层分配（开发中）

- Release 编译（`-p:CopyModOnBuild=false`）0 警告 0 错误，结构门禁通过。
- 玩家实机复现材料：`CONSTRUCT_MENAGERIE_NORMAL-20260907-170814`（回合层停在 2、`play_depth` 173→248、`ended` 99845、`repeatable_no_progress_pruned=0`、无任何 `RESULT`）与 `CONSTRUCT_MENAGERIE_NORMAL-20260907-173454`（手动降到 `deepMaxExpandedNodes=12000` 后正常收敛，3 回合、战损 6、`boundary=NodeLimit`）。
- **无人测试未跑**：`tools/run-watcher-matrix.ps1` 与观者相关夹具开头会 `Stop-Process SlayTheSpire2`，用户正在实机测试，不能执行。本项由用户实机验证：看 `TURN_LAYER_BUDGET reason=nodes` 是否出现、搜索是否收敛。
## 第三方起手牌移除估值登记入口（开发中）

- Release 编译（`-p:CopyModOnBuild=false`）0 警告 0 错误，结构门禁通过。
- 新增 `AssertThirdPartyBasicCardRemoval`，挂在既有卡牌选择挂起用例路径上：登记表初始为空、未登记的牌排序键高于原版起手打击、登记为起手打击后排序键真的下降、重复登记与未定义类别抛错、原版写死的表不被登记表改写、撤销登记后排序键复原。
- **无人测试未跑**：相关脚本开头会 `Stop-Process SlayTheSpire2`，用户正在实机测试，不能执行。实机由用户验证（净化会不会开始优先烧观者打击）。
- 未做 248 条原版回归、未执行完整发布门禁。

## 0.33.5 受伤历史与攻击次数

- `TURN-START-DAMAGE-SPITE` / `THE_OBSCURA_NORMAL`：失败基线 `6fa9c33e5aca495cad4c1c0a8b662c95`，T2 怨恨后 E1.hp 预测 86、原生 81；最终 `18a3b15c05f94038a6cfb080fe41ef9c` Passed，28 秒。覆盖回合开始 Inferno 自伤后的双次攻击、召唤阵容、完整状态和 Fork，以及伤害记录不泄漏到敌方/额外玩家回合。
- `TEAR-ASUNDER-DAMAGE-HISTORY`：失败基线 `29bcf598a611426aad1d1a90c9810975`，两次根受伤和一次分支回合开始受伤后，预测 86、原生 71；最终 `0fefaa871ab344008481d54e8de10a23` Passed，29 秒。精确验证扯碎四次攻击、完整原生状态与 Fork。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId TURN-START-DAMAGE-SPITE -EncounterId THE_OBSCURA_NORMAL -EnemyCurrentHp 100 -HeadlessInstance obscurafix -TimeoutSeconds 120 -ExitOnComplete`；另一场替换 ScenarioId 为 `TEAR-ASUNDER-DAMAGE-HISTORY`。
- 初始两次夹具尝试 `50e220ad84fc4deebd62fb70189905f4`、`242abeca01374dafb2d42fc937ee1efb` 使用默认 1 HP 敌人，回合开始伤害已结束战斗，未到目标断言；修正回放参数并设置 EnemyCurrentHp=100 后取得有效基线。一次编译缺少测试 ValueProps 引用，补齐后成功。
- 两包 Preflight 为 materials_valid、restorationVerified=false。制造者包声明 0.33.2，顺序差异复用 0.33.3 同根证据，本轮未重复整场测试。未做完整发布门禁或整场零重算结论。

## 0.33.4 在线战斗缓存

- 服务端 8 项测试通过；新增旧协议空心跳保留、0 损保留、新战斗尚未算完时整组保留、完成后整组替换、新协议状态/采集时间、累计时长与超时清除。
- `ONLINE-PRESENCE-CONTRACT` / `FOGMOG_NORMAL`，`84c8fdb51f67414c9cc74355f558f83b` Passed，23 秒。验证原生标量采集、纯缓存转换的非战斗/待计算/新结果/首次空值、关闭持久化、真实 HTTPS 和错误指纹拒绝。仅向正式采集端发送空对象验证 400，没有注入玩家数据；未跑完整战斗生命周期。
- 本地 Playwright 验证缓存敌人及 0 HP、首次计算占位、真实战斗人数为 0、采集时间提示、桌面与 390px 手机布局，无页面异常。

## 0.33.3 召唤与死亡监听顺序

- `SUMMON-DEATH-POWER-ORDER` / `FOGMOG_NORMAL` 失败基线 `b61c2d6e82ce4b7cb9585e1e18e0315a`：T2 完整状态 P[0] 预测 Strength、原生 Illusion，精确复现问题包同根顺序差异。
- 修复后 `749e6fb069d6499da1e00861c2e0fcfa` Passed，34 秒；T1 至 T3 召唤、击杀与复活，每轮完整原生状态和 Fork 对账一致。
- `OVICOPTER_NORMAL`，`beff275eb28c4e52b9c0e88ebd93a615` Passed，38 秒；召唤后击杀一个蛋并推进到 T3，完整原生状态和 Fork 一致。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId SUMMON-DEATH-POWER-ORDER -EncounterId FOGMOG_NORMAL -HeadlessInstance summonorder -TimeoutSeconds 120 -ExitOnComplete`；另一场替换 EncounterId 为 `OVICOPTER_NORMAL`。
- 中间一次请求因构建尚未结束导致冻结 DLL 失败；另一次 `2ff74b0932904eebb65dc1abf745c0a1` 在原生资源预加载期间退出（0xc0000005），尚未进入目标战斗；相同构建重试通过。原问题包仅材料预检有效，未做整场路线恢复或完整发布门禁。

## 2026-09-07 图表聚合与悬停

- 后台 7 项测试通过；新增 1440 个高频交替采样合并为 144 个均值点、保留原始峰值，30 天/窄窗口降低采样密度，断档拆桶、零值和空数据。
- Playwright 对真实本地 SQLite 历史与 HTTP 服务验证聚合点数、原始峰值、断档、桌面/手机响应粒度；同一横轴顶部与底部命中相同数据，Canvas 像素检查确认垂直虚线，移出绘图区后清除悬停。未向正式库写入样例历史。

## 2026-09-07 排行与分页

- 服务端 4 项测试通过，新增 62 人分为 30/30/2 三页、全局降序名次、全局搜索、非法页参数、空结果/越界页、概览不携带名单、断线区间排除、跨次上线及服务重启后累计时长保留。
- Playwright 对真实本地服务验证首屏只请求第一页、每页最多 30 个 DOM 行、翻页、跨页搜索保留名次、恶意昵称纯文本、在线人数减少后的页码回收及桌面/手机布局。测试玩家仅写入本地临时数据库，未上传正式后台。

## 2026-09-07 后台公网 HTTPS

- 服务端原 3 项测试通过；实际公网管理入口验证证书名称/信任、页面 200、匿名 API 401、登录、Secure/HttpOnly cookie、错误协议 Origin 403、退出后会话拒绝。未上传测试玩家明细。
- 本轮只更新 Node 服务与运维配置，沿用已发布客户端；未重建或重发 Mod。

## 0.33.2 新召唤敌人行动

- `LIVING-FOG-SUMMON-INTENT`：失败基线 `9c5be94ee8ae48aeac82d4ef1b42a5d4` 精确复现 EXPLODE_MOVE 无后继异常；最终 `d54fff51d884479bb39176b0fa38d02f` Passed，34 秒。T1 BLOAT_MOVE 召唤至 T2，再推进自爆至 T3，两处完整原生状态、阵容、牌堆、AI、RNG 与 Fork 一致。中间运行的召唤数量断言修正见问题记录。
- 相邻 `RAT-SUMMON-NEXT-INTENT`，`58495b308c9448e3812284678f3cfdab` Passed，31 秒，确认需要首次 Roll 的新召唤双尾鼠仍正常生成意图。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId LIVING-FOG-SUMMON-INTENT -EncounterId LIVING_FOG_NORMAL -HeadlessInstance fogfix -TimeoutSeconds 120 -ExitOnComplete`；相邻用例替换 ScenarioId 为 `RAT-SUMMON-NEXT-INTENT`、EncounterId 为 `TWO_TAILED_RATS_NORMAL`。
- 原问题包只执行 Preflight，未作完整恢复结论。本次不扩展完整发布门禁；在线统计沿用下节同源行为证据。

## 0.33.1 在线统计

- `ONLINE-PRESENCE-CONTRACT` Passed，runId `76c3b303aee842b687562655577b551b`，23 秒。验证默认开启、关闭值序列化持久化、当前角色/楼层/战斗/未知战损标量快照、真实 .NET HTTPS 校验及错误证书指纹拒绝。网络检查发送无个人字段的空对象，预期 400；没有向正式统计写入测试玩家。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId ONLINE-PRESENCE-CONTRACT -HeadlessInstance presence -TimeoutSeconds 120 -ExitOnComplete`。要求私有端点配置；普通无人请求仍完全隔离上报。
- `tools/OnlinePresence` 的 `npm test`：3 项通过，包含字段/数值拒绝、鉴权与 Origin、安装标识去重、TTL、重启后聚合历史持久化及限流。
- Playwright 使用真实服务登录与空列表；注入页面级样例后验证桌面/手机布局、折线画布非空、搜索和昵称作为纯文本渲染。样例未发送至正式采集端。未以此声称已观察真实玩家的战斗路线或精确 Steam 人数。
- 正常 Steam 游戏启动后，正式后台收到 1 个带昵称的菜单心跳，角色为空、楼层和战损为 null；没有进入跑局。此行为检查使用版本元数据调整前的 0.33.0 测试构建，行为源码与 0.33.1 相同。

## 0.33.0 发布集成

- PR #57、#58 已合入本批。`PR57-58-STATE-CONTRACT` Passed，runId `e59d8568334c490c9ad9d488288c2ba7`，22 秒。验证污染叠加保持为 4、火花数量变化后同步为 3；隐藏状态槽排序、重复登记拒绝、根捕获委托参数分派、隐藏值变化区分指纹，以及撤销测试登记后恢复原指纹。
- 命令：`pwsh -NoProfile -File tools/run-unattended-test.ps1 -ScenarioId PR57-58-STATE-CONTRACT -HeadlessInstance logic0907 -CardId DEFEND_IRONCLAD -TimeoutSeconds 120 -ExitOnComplete`。登记测试仅临时使用原版 StrengthPower，finally 清理测试项，不新增生产注销入口。
- PR 集成 Release 编译通过，0 警告、0 错误。旧批次沿用下节既有证据；本次未执行完整发布门禁、117 份原包完整恢复或第三方角色整场适配。隐藏状态根捕获本次验证委托分派，未声称第三方内部状态的完整捕获、Fork 或续用通过。
- 暂停范围与后续调查见 [交接文档](issues/report-logic-bugs-20260907-handoff.md)。

## 2026-09-07：汇总日志硬逻辑批次

- `RAT-SUMMON-NEXT-INTENT`：基线 `6d34dbb147f143d58baaa535d3c4c997` 新个体下一行动预测 SCRATCH / 原生 DISEASE_BITE；修复后 `370dae413cb94195b2f5143a17132ad9` Passed。正式 EndTurn 回放对照原生下一玩家回合，完整状态、阵容数量、AI 日志与 RNG、Fork 一致。命令：`./tools/run-unattended-test.ps1 -ScenarioId RAT-SUMMON-NEXT-INTENT -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId TWO_TAILED_RATS_NORMAL -TimeoutSeconds 120 -ExitOnComplete`。
- 怪物下一行动遍历修复的相邻回归 `mercury-reattach-boundary-v0111`，`d74dcb906e9543d8b1e336456de303dc` Passed，沿用本节同名命令与完整复活/死亡差分断言；没有扩大测试超时。
- `TOASTY-MITTENS-NINE-CARD-SETUP` Passed，`8cdd213e82be4468aee65b200ffa9fe9`：SILENT 初始牌组、BAG_OF_PREPARATION / TOASTY_MITTENS 的原生开局选择与精确状态激活通过。命令：`./tools/run-unattended-test.ps1 -ScenarioId TOASTY-MITTENS-NINE-CARD-SETUP -HeadlessInstance logic0907 -CharacterId SILENT -EncounterId GLOBE_HEAD_NORMAL -RelicsJson '[{"relicId":"BAG_OF_PREPARATION"},{"relicId":"TOASTY_MITTENS"}]' -StopAfterInitialSetupAssertion -ExpectedInitialSetupChoiceSourceId TOASTY_MITTENS -ShortSearchBudgetOverrideMilliseconds 1500 -DeepSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`。未重现三份原包的全部牌组/遗物组合，不表示原报告解决。
- `FUNERARY-MASK-BEFORE-DRAW`：基线 `2cea9bef9f8d4b9198d1d3bf5859d694` 抽牌堆预测 4/原生 7；修复后 `f9fe9225531b4729930707d0688e344c` Passed，完整状态、随机插入顺序、RNG、Fork 及 turn 2 不再生成通过。命令：`./tools/run-unattended-test.ps1 -ScenarioId FUNERARY-MASK-BEFORE-DRAW -HeadlessInstance logic0907 -CharacterId NECROBINDER -EncounterId GLOBE_HEAD_NORMAL -TimeoutSeconds 120 -ExitOnComplete`。第二个回合条件通过原生 IncrementTurnNumber 注入，不代表完整两回合推进。
- 面具与筹码原生开局 `FUNERARY-MASK-TURN-SETUP`，`a7c54bdfd1e44721b30b184a0a8556b9` Passed，原生选择顺序与精确状态激活通过。命令：`./tools/run-unattended-test.ps1 -ScenarioId FUNERARY-MASK-TURN-SETUP -HeadlessInstance logic0907 -CharacterId NECROBINDER -EncounterId GLOBE_HEAD_NORMAL -RelicsJson '[{"relicId":"GAMBLING_CHIP"},{"relicId":"FUNERARY_MASK"}]' -StopAfterInitialSetupAssertion -ExpectedInitialSetupChoiceSourceId GAMBLING_CHIP -ShortSearchBudgetOverrideMilliseconds 1500 -DeepSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`。停在准备阶段，不作完整战斗结论。
- `TURN-SETUP-REFRESH-TAKEOVER`：基线 `34ed1aaed32a4deca433ce2847c43ff7` 重算期间开始执行旧计划；修复后 `94d006288b364d529ae5a579c5625855` Passed，接管排队、新计划发布、原生选择和精确状态激活通过。正常等待重算后接管的 `TURN-SETUP-REFRESH-NORMAL`，`4fc6aa624a5a46b1b061cb7ec35e362a` Passed。命令：`./tools/run-unattended-test.ps1 -ScenarioId TURN-SETUP-REFRESH-TAKEOVER -HeadlessInstance logic0907 -CharacterId SILENT -EncounterId GLOBE_HEAD_NORMAL -RelicsJson '[{"relicId":"GAMBLING_CHIP"}]' -VerifyTurnSetupManualRefresh -StopAfterInitialSetupAssertion -ExpectedInitialSetupChoiceSourceId GAMBLING_CHIP -ShortSearchBudgetOverrideMilliseconds 1500 -DeepSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`；正常流程仅替换 ScenarioId。固定短搜，停在准备阶段验收，不作整场求解质量结论。
- `CARD-ENERGY-GAIN-COMMAND`：基线 `e8b64cd4c1e44bc598e618f581c7273d` ALIGNMENT 能量预测 12/原生 10；修复后 `0647bc3be5824b7691859854e975b80a` Passed，11 张增能卡逐张完整原生差分通过，含动态增能、附加 Power 与生成牌。命令：`./tools/run-unattended-test.ps1 -ScenarioId CARD-ENERGY-GAIN-COMMAND -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId GLOBE_HEAD_NORMAL -PowerId NO_ENERGY_GAIN_POWER -PowerAmount 1 -PowerTarget Player -TimeoutSeconds 120 -ExitOnComplete`。初始建局 `c0dd9ab626ca404e985c6d9f1ee277e1` 遗漏 ALIGNMENT 的星能，原生无法出牌，未计作语义基线。此组只验证禁止回能命令路径，不宣称原报告全场回放通过。
- `SURROUNDED-STATE-IDENTITY` 扩展评分缓存检查 Passed，`99bb428e0c68471ba8a4c4b0d75d1234`：Crusher THRASH / Rocket CHARGE_UP，正式 Snapshot 左右朝向有不同指纹、预估 HP 与评分；同一 solver 先计算左再右，右值与独立 solver 一致，重新计算左值稳定。保留原朝向状态、续用、Fork 和 10/15 背击断言。命令沿用下方同名场景，增加 `-ExitOnComplete`。未执行 27 份蟹皇旧包的完整回放。
- `MELANCHOLY-OSTY-DEATH` Passed，`05b52a6c87034108996792f3e47ccbd6`：四个牌堆的升级忧郁带 SWIFT 2 / BOUND 3，奥斯提死亡后的完整原生差分、直接/分叉等价、父分支隔离及再次 Fork 通过。命令：`./tools/run-unattended-test.ps1 -ScenarioId MELANCHOLY-OSTY-DEATH -HeadlessInstance logic0907 -CharacterId NECROBINDER -EncounterId GLOBE_HEAD_NORMAL -TimeoutSeconds 120 -ExitOnComplete`。仅证明直接死亡通知，不覆盖报告 `c3f8cf86` 的最终路线回放。首次请求误用了不存在的遭遇 ID（`957f8afbf5914bc08621ee843cf5eaa7`），未进入战斗，不属于语义失败基线。
- `QUEEN-INFERNO-TERMINAL`：基线 `ace01a08d43b49ecbf358cb01fc89556` 清理前能量预测 5/原生 3；修复后 `5123f0ad07074cf9926b2c554bf8dc26` Passed，完整状态和 Fork 相等。使用与 `QUEEN-INFERNO-MINION-DEATH` 相同 CLI 参数，仅替换 ScenarioId；两个敌人均保留注入的 9 HP，通过既有原生 `EndCombatInternal` 观察者在战后回血和清理前取样并等待 CombatEnded。原先战后取样的 HP 77/80 不再作为模拟错误证据。
- `QUEEN-INFERNO-MINION-DEATH` Passed，`5e435cccefad4506a75537c2831c136f`：满血女王预置强化随从行动，炼狱击杀 9 HP 随从，原生/模拟完整状态及 Fork 一致，并检查阵容仅保留原女王实例。命令：`./tools/run-unattended-test.ps1 -ScenarioId QUEEN-INFERNO-MINION-DEATH -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId QUEEN_BOSS -CardId BLOODLETTING -ClearPlayerPiles -EnemyCurrentHp 9 -PowerId INFERNO_POWER -PowerAmount 9 -PowerTarget Player -TimeoutSeconds 120 -ExitOnComplete`。仅证明该最小路径，不证明报告 `387a2e1c` 已修复；放血能量命令修复后的相邻回归 `0a5c33b2f0f34339a14ae343338b87e1` 同样通过。
- `KNOWN-GAMEPLAY-MOD-BOUNDARY` Passed，`aa1ab43130824665a2524a107521b75b`：合成清单 ID 命中、清单改名但程序集名命中均抛出含实际 Mod ID 的 `IncompatibleGameplayModException`；明确拒绝策略优先于中性声明，空集合正常通过。命令：`tools/run-unattended-test.ps1 -ScenarioId KNOWN-GAMEPLAY-MOD-BOUNDARY -HeadlessInstance logic0907 -CharacterId DEFECT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。Release 零警告/错误并同步本地 mods；没有安装或执行第三方 Mod，也没有声称复现其改牌实现。
- `CYCLE-EXIT-REVOKED-PARENT`：基线 `4f2102226f00454396abd02b421ada79` Failed，子节点已有临时出口观测、父租约随后撤销时，生产准入条件跳过处理，观测残留。修复后 `b07bd11d4dac484390a2badc67d5c8fb` Passed，分别覆盖卡牌/药水/结束回合输入列表，清理失效观测且 tracker 不获得 envelope；沿用原混合顺序、反向顺序、64 候选单出口及其他无效观测合同。命令：`tools/run-unattended-test.ps1 -ScenarioId CYCLE-EXIT-REVOKED-PARENT -HeadlessInstance logic0907 -CharacterId REGENT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。这是合成调度元数据的生产准入条件与 materialization 合同，不是原包整场回放，也不构成 DOP 性能或整场质量结论。Release 零警告/错误并同步本地 mods，Windows 结构门禁通过。
- `NARROW-ORDERED-PILE-CAPACITY`：基线 `ec88461ccf314096955767f71754f2d1` Failed，真实 `RankBest` 抛“Beam 容量不足以保留策略必需分支”。夹具给四张偏折设置不同格挡数值，逐一回放 24 种出牌顺序并结束回合，确保至少 8 种有效状态/预计洗牌顺序，交给 6 宽 Deep 怀表通道。修复后 `808eda35b341442081083729dd9881d1` Passed，结果在既有 6–7 容量内、身份无重复；未饱和通道保留全部候选。可重跑命令：`tools/run-unattended-test.ps1 -ScenarioId NARROW-ORDERED-PILE-CAPACITY -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -CardsPath coverage/unattended/report-narrow-ordered-pile-cards.json -RelicsJson '[{"relicId":"POCKETWATCH"}]' -EnemyCurrentHp 80 -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120`。本次运行使用内容相同的内联 `CardsJson`，文件是在通过后固化的输入；这是回放生成候选后的局部政策合同，未宣称原包整场搜索通过。
- 本项短搜索回归 `FEED-THORNS-TERMINAL-TWO-CARDS` 的 `ffc089f076784745a8ccb6217b9b4221` Passed，沿用既有 1500 ms 固定预算与增量验证命令：先防御后狂宴、T1 零损胜利、6 展开/15 转移。Release 编译零警告/错误并同步本地 mods，Windows 结构门禁通过。
- `GAMBLERS-BREW-SLY-ORDER`：基线 `4dd54bb5380e4ebd84497e72ccdf2784` Failed，单张连续反弹被弃牌重抽后，预测仍在手牌，原生已经通过狡猾打出并回到弃牌堆。修复后 `e4411503e76b49fd9d76a8c2ab9a9627` Passed，原生用药的牌堆、伤害、历史与 RNG 完整差分一致。命令：`tools/run-unattended-test.ps1 -ScenarioId GAMBLERS-BREW-SLY-ORDER -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -EnemyCurrentHp 80 -PotionCheckPath coverage/unattended/report-gamblers-brew-sly.json -TimeoutSeconds 120`。首次夹具 `77d4c3ed342845809c54b3d4b9bf59d4` 因药水差分分支未采用命令行 CardId，候选缺失；改为在药水夹具中显式注入后才取得目标失败基线。
- `GAMBLING-CHIP-SLY-ORDER` Passed，`47ec379ab7f24176b8010eed8e1ac112`：开局弃牌重抽生产选择处理器与其 Fork 完整结果一致，并与原生 `CardCmd.DiscardAndDraw` 对照通过。命令：`tools/run-unattended-test.ps1 -ScenarioId GAMBLING-CHIP-SLY-ORDER -HeadlessInstance logic0907 -CharacterId SILENT -CardId RICOCHET -ClearPlayerPiles -EnemyCurrentHp 80 -TimeoutSeconds 120`。本测试冻结选择后直接对照原生组合操作，没有重放遗物 UI。
- `DISCARD-DRAW-SLY-PENDING` Passed，`3e182f6452944415b0e6f1850569e017`：抽牌已完成后才进入狡猾自动牌的挂起选择，处理器仍返回未完成。旧断言要求此时抽牌堆不动，实际固化了错误顺序，现按原版改为检查先抽牌。命令：`tools/run-unattended-test.ps1 -ScenarioId DISCARD-DRAW-SLY-PENDING -HeadlessInstance logic0907 -CharacterId SILENT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。Release 零警告/错误并同步本地 mods，Windows 结构门禁通过；原报告整场及全部 Mod 环境未回放。
- `GALVANIC-GENERATED-POWER`：基线 `4a50b407274a47e98c282bd5992c9522` Failed，生成 `AUTOMATION` 时预测没有苦难，原生为 `GALVANIZED:6`。修复后 `9ef200eadb2945e6949c1257c7987875` Passed，比较原生固定生成、Fork 后完整状态、子分支打出不污染父分支，以及原生打出的能量/Power/HP/牌堆/RNG；原有防御作为技能牌保持无此苦难。命令：`tools/run-unattended-test.ps1 -ScenarioId GALVANIC-GENERATED-POWER -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId GLOBE_HEAD_NORMAL -ClearPlayerPiles -CardId DEFEND_IRONCLAD -TimeoutSeconds 120 -ExitOnComplete`。Release 编译零警告/错误并同步本地 mods。测试直接生成原报告所选能力牌，没有重放工具箱随机选项或原包全部 Mod 整场。
- `NO-DRAW-DARK-EMBRACE`：基线 `be6fc75970c240d293327a0fae047013` Failed，先获得禁止抽牌时预测 5 / 原生 6 张手牌。修复后 `7dcb893a3c034a1d9f3d704561e88438` Passed；反向获得顺序 `DARK-EMBRACE-NO-DRAW` 的 `de4051e5c3e14b89b7bf7fc6c33ca55d` Passed，两者均比较回合末及下一回合准备的完整状态。命令：`tools/run-unattended-test.ps1 -ScenarioId NO-DRAW-DARK-EMBRACE -HeadlessInstance logic0907 -CharacterId IRONCLAD -MonsterMoveChecksPath coverage/unattended/report-no-draw-dark-embrace.json -TimeoutSeconds 120`；反向使用 `-ScenarioId DARK-EMBRACE-NO-DRAW -MonsterMoveChecksPath coverage/unattended/report-dark-embrace-no-draw.json`。
- `TURN-END-POWER-ORDER-FORK` Passed，`f4584f8b9805476aa9bfa83ad84f3d92`：相反获得顺序的指纹及续用文本不同；Fork 保留指纹、完整结算结果，子分支结束回合不改变父分支；抽牌结果分别为 1 / 0。命令：`tools/run-unattended-test.ps1 -ScenarioId TURN-END-POWER-ORDER-FORK -HeadlessInstance logic0907 -CharacterId IRONCLAD -ClearPlayerPiles -CardsJson '[{"cardId":"DEFEND_IRONCLAD","pile":"Draw"}]' -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120`。
- Power 指纹/续用改为完整有序列表后的相邻回归 `ENERGY-RESET-POWER-ORDER-REAPPLY` Passed，`5cb6fd6a83004397b413dbc71db5b59d`，覆盖移除后重新施加、Fork 与原生能量重置完整状态；命令沿用本节既有场景并加 `-ExitOnComplete`。Release 构建零警告/错误并同步本地 mods，Windows 结构门禁通过。该测试不代表其他 Power 的全部 Hook 时序已审计。
- `NO-DRAW-JOSS-END`：基线 `0b8e95d32a5d452daeb31954b38de364` Failed，下一回合手牌预测 5 / 原生 6；修复后 `53fd32235f674b708eeb851c659bdabb` Passed，回合末消耗、纸钱计数、抽牌与下一回合准备的完整状态一致。命令：`tools/run-unattended-test.ps1 -ScenarioId NO-DRAW-JOSS-END -HeadlessInstance logic0907 -CharacterId IRONCLAD -MonsterMoveChecksPath coverage/unattended/report-no-draw-joss-paper.json -TimeoutSeconds 120 -ExitOnComplete`。
- `PLAYER-END-PHASE-TWO-PENDING` Passed，`775c8f5b3a4541609857f36daf4dd968`：常规 Power 抽牌挂起时不访问后续 Power；纸钱抽牌挂起时不推进后续遗物及晚期瓦解伤害。命令：`tools/run-unattended-test.ps1 -ScenarioId PLAYER-END-PHASE-TWO-PENDING -HeadlessInstance logic0907 -CharacterId IRONCLAD -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。最后一次编译只增加测试入口与断言，生产代码沿用上条差分证据；Release 零警告/错误并同步本地 mods，Windows/Bash 结构门禁通过。原报告整场及全 Mod 环境未恢复；禁止抽牌与黑暗之拥的 Power 内部相对顺序另查。
- `BOUND-END-DRAW` 基线 `faa861a3c91747de952da67bcef4c82d` Failed：本回合束缚额度已用尽，回合末黑暗之拥抽到的首张防御预测带 BOUND，原生没有。修复后 `a8cfd38c8b74486d8dbda0763e306ff0` Passed，包含回合末抽牌及下一玩家回合准备的完整状态差分。命令：`tools/run-unattended-test.ps1 -ScenarioId BOUND-END-DRAW -HeadlessInstance logic0907 -CharacterId IRONCLAD -MonsterMoveChecksPath coverage/unattended/report-bound-end-draw.json -TimeoutSeconds 120`。
- `BOUND-ROOT-HISTORY` Passed，`315a0f6fc47e466fbba6a0a349a79f92`：捕获根前原生已经施加一次束缚，额度在根中保留，后续抽牌、回合末抽牌和下一回合准备的完整差分通过。命令沿用上条，改 `-ScenarioId BOUND-ROOT-HISTORY -MonsterMoveChecksPath coverage/unattended/report-bound-root-history.json -ExitOnComplete`。
- `BOUND-COUNTER-FORK` Passed，`bcccb419227141b587959bd39455ddcc`：父/子分支计数 1/2 互不污染，指纹区分已用额度，下一玩家回合重新计数且不修改父分支。命令：`tools/run-unattended-test.ps1 -ScenarioId BOUND-COUNTER-FORK -HeadlessInstance logic0907 -CharacterId IRONCLAD -PowerId CHAINS_OF_BINDING_POWER -PowerAmount 3 -PowerTarget Player -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120 -ExitOnComplete`。最后一次编译只新增此测试入口，前两项生产行为证据继续有效；构建已同步本地 mods，结构门禁通过。原报告的全部 Mod 和整场路线未回放。
- `PAPER-CUTS-THORNS-BOUNDARY` 基线 `78d357172eef4291927b3f9772af622e` Failed，最大生命预测 68 / 原生 70。修复后 `aaf903a158354b1c83ddbb3c097cb21d` Passed：卷轴在承伤前反伤中死亡，已开始的攻击继续命中，纸割退出普通回调；怪物行动完整状态差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId PAPER-CUTS-THORNS-BOUNDARY -HeadlessInstance logic0907 -CharacterId SILENT -EncounterId SCROLLS_OF_BITING_NORMAL -MonsterMoveChecksPath coverage/unattended/report-paper-cuts-thorns-boundary.json -TimeoutSeconds 120`。原报告整场及全部 Mod 未重放。
- 活动监听视图改动的死亡相邻回归：`DEATH-EFFECTS-ONCE` 的 `ac8a9bf19cae4502a7b3851124903b5c` Passed；`mercury-reattach-boundary-v0111` 的 `7139a36826034e0daae2f9e36c40e6ff` Passed，包含复活、再次死亡、直接/Fork/重新捕获根和清理前原生完整状态。沿用本节对应场景命令，最后一项加 `-ExitOnComplete`。Release 编译零警告/错误并同步本地 mods，结构门禁通过。
- `ENERGY-RESET-POWER-ORDER-REAPPLY` 基线 `5b0b96d193af4a7ba02fc8592f7c38d3` Failed：根中先有引雷能力，移除再获得后预测仍使用旧位置，球序继续颠倒。修复后 `017caab7d1164ae0907c7e0e1b912765` Passed，原生移除/施加、Fork 与能量重置后的完整状态一致。命令：`tools/run-unattended-test.ps1 -ScenarioId ENERGY-RESET-POWER-ORDER-REAPPLY -HeadlessInstance logic0907 -CharacterId DEFECT -CardId DEFEND_DEFECT -ClearPlayerPiles -EnemyCurrentHp 80 -TimeoutSeconds 120`。首次夹具 `412ac9f548ec45a49bb8e4a05226fa1d` 因直接施加后未结算 Power 变化事件，在 Fork 处失败；补上生产 `ResolvePowerAmountChanges` 后才取得目标失败基线。
- `ENERGY-RESET-POWER-ORDER-OVERFLOW` Passed，`a37f9fb36ac1481db00873d62a59360b`：已有闪电球，依次生成 3 个玻璃球和 1 个闪电球，覆盖满槽激发。逐球状态、敌方伤害、RNG、Power 与资源的完整原生差分及 Fork 通过。命令沿用上条并改 `-ScenarioId ENERGY-RESET-POWER-ORDER-OVERFLOW -ExitOnComplete`。
- `ENERGY-RESET-POWER-ORDER` 基线 `cb45a70d288f46a6afa301c08a721ccb` Failed：先施加 `SPINNER_POWER`、后施加 `LIGHTNING_ROD_POWER`，预测球序为闪电/闪电/玻璃，原生为闪电/玻璃/闪电。最终 `daddd03c11b54f02a99780ddabfcee0e` Passed；反向顺序 `7757429a90964e28b5b827e298e125c3` Passed。命令：`tools/run-unattended-test.ps1 -ScenarioId ENERGY-RESET-POWER-ORDER -HeadlessInstance logic0907 -CharacterId DEFECT -CardId DEFEND_DEFECT -ClearPlayerPiles -EnemyCurrentHp 80 -TimeoutSeconds 120`，反向使用 `-ScenarioId ENERGY-RESET-POWER-ORDER-REVERSE -ExitOnComplete`。
- 上述最终夹具在原生 `Hook.AfterEnergyReset` 上对照完整状态，同场覆盖 Genesis/StarNextTurn/Radiance 的星能、能量和层数变化；验证不同获得顺序具有不同分支指纹及续用文本，Fork 保持顺序。满球槽激发与重新获得能力由本节追加夹具覆盖；原包整场回放仍未运行。
- `REPLAY-START-HISTORY` 基线 `0b32fe311c324ba2a83ff026aff42b26` Failed：带 `GLAM` 的切割原生执行两次，预测 `Y=0/1`、原生 `Y=0/2`。最终 `930567f5a18640c39a78c00363d84474` Passed：完整状态差分、Fork、重新捕获根的零费攻击 2 次、出牌系列 1 次、手动操作 1 次一致。命令：`tools/run-unattended-test.ps1 -ScenarioId REPLAY-START-HISTORY -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -CardsJson '[{"cardId":"SLICE","pile":"Hand","enchantmentId":"GLAM"}]' -EnemyCurrentHp 80 -TimeoutSeconds 120 -ExitOnComplete`。
- `REPLAY-START-HISTORY-ECHO` Passed，`1c5954b23be5482497b42d66e6503899`：两层回响形态下依次打出带重放附魔和普通切割，分别执行 3 次和 2 次；逐动作完整原生差分、Fork 与重新捕获根的两种计数通过。命令：`tools/run-unattended-test.ps1 -ScenarioId REPLAY-START-HISTORY-ECHO -HeadlessInstance logic0907 -CharacterId SILENT -ClearPlayerPiles -CardsJson '[{"cardId":"SLICE","pile":"Hand","enchantmentId":"GLAM"},{"cardId":"SLICE","pile":"Hand"}]' -PowerId ECHO_FORM_POWER -PowerAmount 2 -PowerTarget Player -EnemyCurrentHp 80 -TimeoutSeconds 120`。早期只移除总计数门的方案曾通过普通重放 `3d517d24a8304d0d80dda0d493ecfe85`，静态追踪发现会改变回响形态的系列计数，最终拆清语义并重新验证；不以早期通过结果代表最终代码。
- `DEATH-EFFECTS-ONCE` 基线 `840bc517634f47a3815afc1e95bb4ecf` Failed：使用不同局部集合再次通知同一死亡，尸蛞蝓力量由 4 变为 8。修复后 `f153493cbdb5422fa167c7cf384b9cba` Passed：正式打击回放、重复通知、Fork 后通知及原生打击完整状态差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId DEATH-EFFECTS-ONCE -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId CORPSE_SLUGS_WEAK -CardId STRIKE_IRONCLAD -ClearPlayerPiles -EnemyCurrentHp 6 -TimeoutSeconds 120`。该夹具直接覆盖跨调用集合的重复通知，未重放原报告的完整球动作链。
- 死亡去重的相邻复活边界 `61831e7dc4ec47b985ecc6f8590bf2cd` Passed：`REATTACH_MOVE` 与后续 `DEAD_MOVE` 的直接、Fork、重新捕获根及原生清理前完整状态差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId mercury-reattach-boundary-v0111 -HeadlessInstance logic0907 -CharacterId IRONCLAD -EncounterId DECIMILLIPEDE_ELITE -TimeoutSeconds 120 -ExitOnComplete`。
- `FEED-THORNS-TERMINAL-TWO-CARDS` 基线 `e81a73db90aa499b9481e90087989c76` Failed：狂宴后继续展开防御，报“回放包含已锁定战斗终局之后的动作”。修复后 `1772dde4c0234433b3c0f0ef756916a8` Passed，先防御后狂宴、T1 零损获胜，6 节点/15 转移，增量回放通过。命令：`tools/run-unattended-test.ps1 -ScenarioId FEED-THORNS-TERMINAL-TWO-CARDS -HeadlessInstance logic0907 -CharacterId IRONCLAD -ClearPlayerPiles -CardsJson '[{"cardId":"FEED","pile":"Hand"},{"cardId":"DEFEND_IRONCLAD","pile":"Hand"}]' -EnemyCurrentHp 10 -InitialPlayerHp 1 -InitialPlayerEnergy 2 -PowerId THORNS_POWER -PowerAmount 2 -PowerTarget Enemy -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialUnmirroredCount 0 -ExpectedInitialFirstActionCardId DEFEND_IRONCLAD -ExpectedInitialFinalEnemyHpAtMost 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -ExitOnComplete`。
- `FEED-THORNS-TERMINAL-DIFFERENTIAL` Passed，`4881c50d0c164d8f957bf71df61cf096`：唯一狂宴动作致命反伤后恢复正 HP，模拟保持 Defeat；原生 PendingLoss 结束战斗，结束事件中的完整状态差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId FEED-THORNS-TERMINAL-DIFFERENTIAL -HeadlessInstance logic0907 -CharacterId IRONCLAD -CardId FEED -ClearPlayerPiles -EnemyCurrentHp 10 -InitialPlayerHp 1 -InitialPlayerEnergy 1 -PowerId THORNS_POWER -PowerAmount 2 -PowerTarget Enemy -TimeoutSeconds 120`。首次差分 `9f1fb8000d2a4b38941f661f9aad7ba7` 在敌方已移出 roster 后按下标取敌人失败，修正测试为保留原始 Creature 身份；首次搜索探针 `6d1a4a7f854e4bb5b53999cf07d64328` 因 CardsJson 覆盖 CardId，仅注入防御，不能作为目标验证。
- `DISTILLED-CHAOS-VOID-FORM-BOUNDARY`：基线 `d470307bcc5b445f8f9e7546fb2bd577` 在药水后的 EndTurn Fork 报未结算结束请求；修复后 `bdd86b08c8ea41df963c0031fe8ccdcf` Passed，32 节点/50 转移、1 瓶药水、未镜像项 0，增量回放通过。命令：`tools/run-unattended-test.ps1 -ScenarioId DISTILLED-CHAOS-VOID-FORM-BOUNDARY -HeadlessInstance logic0907 -CharacterId REGENT -CardId DEFEND_REGENT -ClearPlayerPiles -CardsJson '[{"cardId":"VOID_FORM","pile":"Draw"}]' -EnemyCurrentHp 80 -InitialPlayerEnergy 0 -PotionId DISTILLED_CHAOS -PotionPolicyForTest RequireAtLeastOne -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialUnmirroredCount 0 -ExpectedInitialPotionCount 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120`。
- `potion-forced-turn-terminal-v0111` Passed，`3bd9d757e90a4e8a9e90975904a1a651`：唯一药水动作自动打出 `VOID_FORM`，T+1 沙漏击杀的根回放、增量回放、Fork、释放后快照、正式标注及原生清理前完整状态差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId potion-forced-turn-terminal-v0111 -HeadlessInstance logic0907 -CharacterId REGENT -TimeoutSeconds 120 -ExitOnComplete`。本夹具沿用既有强制结束终局差分工具，不运行正式搜索，不加入无效增量搜索开关。
- `ROOT-CAPTURE-ACTION-BARRIER`：基线 `d01150ef49014dc8ba1931a8802992e6` Failed，真实 `BeforeActionExecuted` 期间调用搜索立即建立了搜索会话。修复后 `00f8c4d6237944dbb95061f695edc44c` Passed，队列执行期间不捕获，原生防御结算后延迟请求完成搜索。命令：`tools/run-unattended-test.ps1 -ScenarioId ROOT-CAPTURE-ACTION-BARRIER -HeadlessInstance logic0907 -CharacterId SILENT -CardId DEFEND_SILENT -ClearPlayerPiles -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -TimeoutSeconds 120 -ExitOnComplete`。该最小合同没有重建原报告的全部 Mod、后台回收和帕尔军团动画时序。
- 材料预检：`95e19fb8` 报告为 `materials_valid`。原生 `RestoreOnly` 请求 `4f52cb972f4447c5b87dcd4b1b3a9f2e` 因 `environment_mismatch:mods` 失败；本机与原报告 Mod 集合不同，保持严格拦截，未宣称原包恢复成功。
- `SURROUNDED-STATE-IDENTITY` 基线 `bfa9cc597f6647929f193c9aac98f163` Failed：左右朝向产生相同指纹。修复后 `e25cc4a6d62841ed97bcc3b179e1361a` Passed：状态指纹与 continuation 区分朝向、Fork 修改不回写父分支、背击预测为 10/15。命令：`tools/run-unattended-test.ps1 -ScenarioId SURROUNDED-STATE-IDENTITY -HeadlessInstance logic0907 -EncounterId KAISER_CRAB_BOSS -CharacterId SILENT -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120`。
- `SURROUNDED-POTION-DIFFERENTIAL` Passed，`99ee091bfceb42509e49cc62b78c3d7d`：虚弱药水转向左侧的 actual/simulated 严格差分与明确朝向断言通过。命令：`tools/run-unattended-test.ps1 -ScenarioId SURROUNDED-POTION-DIFFERENTIAL -HeadlessInstance logic0907 -EncounterId KAISER_CRAB_BOSS -CharacterId SILENT -PotionCheckPath coverage/unattended/power-lifecycle-batch-051-surrounded-potion.json -TimeoutSeconds 120 -ExitOnComplete`。
- Release 编译零警告/错误，Windows 结构门禁通过，默认构建同步本地 mods。该合同与单步差分不替代完整蟹皇战斗或整批报告验收。

## 0.32.0 定版

行为源码沿用下列成长策略与点击外部保存的通过证据，本次仅同步版本和发布文档，不重复行为场景。PR #22 按维护者决定关闭，其强制收益逻辑与精进成长未纳入版本；PR #56 既有适配入口随本版发布。最终产物从版本提交执行 Release 构建并同步本地 mods，发布使用最小 ZIP；未执行完整发布门禁或可见 Steam 人工验收。

## 2026-09-07：成长设置分离与点击外部保存

- “提前结束搜索的战损阈值”回到常规设置的求解器区域；成长侧栏只编辑成长额度。沿用原设置字段及成长优先策略。
- Release 默认构建零警告/错误，并通过项目 `CopyMod` 目标部署至本地游戏 `mods/CombatSolver`。
- `GROWTH-POLICY-FREE-FIRST` Passed，runId `c8c16e7ea30b4c3095709f79f8ab7338`，复跑使用下节同名命令并加 `-ExitOnComplete`。新增 UI 合同通过：成长输入文本设为 7，外部鼠标按下后失焦、SpinBox 应用且设置保存为 7；打开设置后将阈值输入设为 19，外部鼠标按下后失焦并保存为 19。侧栏边界、互斥、配置重载与零损优先成长检查同时通过。
- 测试调用真实面板输入处理器与 Godot 失焦信号，未进行可见 Steam 人工点击验收。

## 2026-09-07：局外成长策略（未发布）

Release 编译 `-p:CopyModOnBuild=false` 零警告/错误；Windows 结构门禁通过（`search_files=74`），两份修改过的 Bash 入口语法检查通过。所有请求使用隔离的 `growth` headless 实例，超时 120 秒，短搜预算 1500ms；搜索测试开启增量回放，时间数据不代表生产性能。

| 场景 | 结果 |
|---|---|
| `GROWTH-POLICY-FREE-FIRST` | Passed，`4545ca8b2d464137a35f567f77ccccd9`。零额度下遗传算法优先于更快的零损击杀；跨回合路线保留成长。八类配置默认值、序列化往返、不可变捕获、侧栏重载/开关/边界/互斥、分支计数隔离与累计额度检查通过。 |
| `GROWTH-POLICY-PAID` | Passed，`047d44398b1341019670ec6f18b0d756`。去掉初始格挡，零额度拒绝付血成长；额度 100 时取得成长且实际比零额度多损血，额外战损未超过额度。比较合同另检验额度边界及超额拒绝，胜利优先于成长。 |
| `GROWTH-REPLAY-COUNT` | Passed，`5075d66cd698428b8bf1fea9137102e0`。遗传算法附魔重放，两次成功成长计数为 2；先成长再击杀，T1 零损；5 节点/12 转移。 |
| `GROWTH-FATAL-PRIORITY` | Passed，`a23b3500f1a548af9564dec9f0e9162c`。贪婪之手与打击都可零损击杀时选择前者，收益次数 1；4 节点/12 转移。 |
| `GROWTH-NO-TARGET-POTION-SENTINEL` | Passed，`3d2008f608604f3785d41f8aead373cd`。无成长目标时仍按强制药水政策使用火焰药水，T1 零损、1 瓶、收益次数 0、未镜像项 0；1 节点/2 转移。 |

复跑入口：

```powershell
./tools/run-unattended-test.ps1 -ScenarioId GROWTH-POLICY-FREE-FIRST -HeadlessInstance growth -CharacterId DEFECT -ClearRunDeck -ClearPlayerPiles -CardsJson '[{"cardId":"GENETIC_ALGORITHM","pile":"Hand","treatAsDeckCard":true},{"cardId":"STRIKE_DEFECT","pile":"Hand","treatAsDeckCard":true}]' -EnemyCurrentHp 6 -InitialPlayerEnergy 1 -InitialPlayerBlock 99 -VerifyGrowthPolicy -StopAfterCombatRootSnapshotAssertion -TimeoutSeconds 120
# 付费场景使用同一参数，改 ScenarioId 为 GROWTH-POLICY-PAID，InitialPlayerBlock 为 0。
./tools/run-unattended-test.ps1 -ScenarioId GROWTH-REPLAY-COUNT -HeadlessInstance growth -CharacterId DEFECT -ClearRunDeck -ClearPlayerPiles -CardsJson '[{"cardId":"GENETIC_ALGORITHM","pile":"Hand","treatAsDeckCard":true,"enchantmentId":"GLAM"},{"cardId":"STRIKE_DEFECT","pile":"Hand"}]' -EnemyCurrentHp 6 -InitialPlayerEnergy 2 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialGrowthRewardCount 2 -ExpectedInitialFirstActionCardId GENETIC_ALGORITHM -ExpectedInitialProjectedBattleHpLost 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120
./tools/run-unattended-test.ps1 -ScenarioId GROWTH-FATAL-PRIORITY -HeadlessInstance growth -CharacterId IRONCLAD -ClearRunDeck -ClearPlayerPiles -CardsJson '[{"cardId":"HAND_OF_GREED","pile":"Hand"},{"cardId":"STRIKE_IRONCLAD","pile":"Hand"}]' -EnemyCurrentHp 6 -InitialPlayerEnergy 2 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialGrowthRewardCount 1 -ExpectedInitialFirstActionCardId HAND_OF_GREED -ExpectedInitialProjectedBattleHpLost 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120
./tools/run-unattended-test.ps1 -ScenarioId GROWTH-NO-TARGET-POTION-SENTINEL -HeadlessInstance growth -CardId DEFEND_IRONCLAD -ClearRunDeck -ClearPlayerPiles -InitialPlayerEnergy 0 -EnemyCurrentHp 20 -PotionId FIRE_POTION -PotionPolicyForTest RequireAtLeastOne -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialFirstActionPotionId FIRE_POTION -ExpectedInitialPotionCount 1 -ExpectedInitialGrowthRewardCount 0 -ExpectedInitialFinalEnemyHpAtMost 0 -ExpectedInitialUnmirroredCount 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -ExitOnComplete
```

迭代中曾命中原有零损早停；已在成长目标存在时关闭。无格挡且额度 2 的初始夹具未选成长，不能作为免费收益验证，后改为注入格挡和独立付费场景。`7f4963d71e11400aa1f9aaf8ab10beec` 因选择了等待正式搜索结果的停止参数而超时，改为根断言后停止；`e34830eb59864bc280802f82bbd7465c` 因 UI 测试未创建 overlay 失败，测试入口现先创建 overlay。编译期修复了类型名遮蔽、空值注解与测试 runner 实例调用；首次 Bash 路径错误后定位实际安装位置通过。

未做八类卡牌逐一原生部署或本轮 actual/simulated 全量差分；计数合同与增量回放不替代原生结算验收。未做可见 Steam UI 检查、重启游戏后的人工设置回读或性能基准，headless 只证明结构状态与设置序列化。本批未复制开发 DLL 到正常游戏目录，未发包或上传。

## 2026-09-07：文档目录整理

L0 文档检查：64 份资料归类移动，增加 8 份导航；整理后共 103 个文档与数据文件，258 个本地文件链接均可解析，全部文件可从文档总入口到达。源码、编译配置和运行行为未变，本轮未构建或启动游戏。

## 2026-09-07：PR #56 合并验证（未发布）

- Release 编译 `-p:CopyModOnBuild=false` 零警告/错误；Windows 结构门禁通过，`search_files=73`。
- `PR56-CARD-CHOICE-REGRESSION` Passed，runId `efc7007a3dc44012b65dafcb0a3e2ff3`：原版两种可选选牌的空选严格差分通过。命令：`tools/run-unattended-test.ps1 -ScenarioId PR56-CARD-CHOICE-REGRESSION -HeadlessInstance pr56 -MonsterMoveChecksPath coverage/unattended/card-on-play-batch-042-choice-zero-optional.json -EnemyCurrentHp 100 -TimeoutSeconds 120 -ExitOnComplete`。
- 未运行第三方许愿的登记委托、三选一实际结算或原生页面部署；原版回归不等于第三方效果验收。本次仅合并源码，保持已发布 `0.31.3` 的产物与标签。

## 0.31.3 定版

PR #49 直接合同在本机 RitsuLib `0.5.19` 上通过全部 10 项：当前真实回调匹配、静态正负查询、live 旁路、动态晚创建、并发、可卸载程序集与模拟后恢复。100000 次缺失类型查询的合同测量为 `18400000 -> 0` 字节，仅表示该查询，不代表整场性能。Windows 结构门禁通过。

本版本收录 PR #49–#55 和已定版 `0.31.2` 元数据。发布源提交 `d71ca2d` 的最终 Release 构建零警告/错误。PR #50–#55 与战前 API 的既有证据见下方；未执行完整发布门禁或可见 Steam 性能 A/B。

- `PR49-FIRE-POTION-0313` Passed，runId `c5c6183f6d424fbd84596ab86e8bef74`：强制火焰药水，Short1500ms，增量回放；1 节点/2 转移，首动作使用目标药水，1 瓶、T1 敌 HP0、未镜像项0。首请求 `3e1a71fc7c24497594eeb81b165475b7` 因空 `CardId` 在建局时报错，改为零能量的防御牌后通过，行为源码未改。
- `PR49-POTION-DIFF-0313` Passed，runId `41a2a580df544528b1585143e5829f3e`：复用同一 headless 进程，火焰药水对敌目标与结算严格差分，最后请求 `-ExitOnComplete` 退出。

```powershell
./tools/run-unattended-test.ps1 -ScenarioId PR49-FIRE-POTION-0313 -HeadlessInstance release0313 -CardId DEFEND_IRONCLAD -ClearPlayerPiles -InitialPlayerEnergy 0 -EnemyCurrentHp 20 -PotionId FIRE_POTION -PotionPolicyForTest RequireAtLeastOne -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialFirstActionPotionId FIRE_POTION -ExpectedInitialPotionCount 1 -ExpectedInitialFinalEnemyHpAtMost 0 -ExpectedInitialUnmirroredCount 0 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120
./tools/run-unattended-test.ps1 -ScenarioId PR49-POTION-DIFF-0313 -HeadlessInstance release0313 -PotionCheckPath coverage/unattended/potion-batch-044-fire.json -TimeoutSeconds 120 -ExitOnComplete
```

## 2026-09-07：PR #50–#55 合并验证

本轮验证六条 PR 合并后的行为源码。Release 编译零警告/错误，Windows 结构门禁、Git Bash `bash -n tools/run-unattended-test.sh`、CoverageCatalog `--verify-effective --verify-pre-play-choices --verify-combat-choices` 均通过。覆盖目录检查限原版目录，生成的时间戳变化未提交。

| 场景 | 结果与证据 | 复跑参数（共同使用 `tools/run-unattended-test.ps1 -HeadlessInstance pr50-55 -TimeoutSeconds 120`） |
|---|---|---|
| `PR50-55-GAMBLERS-REGRESSION` | Passed，runId `3fc6e575501d4b9596576c5167466cdb`；赌博药水弃牌与补抽严格差分 | `-ScenarioId PR50-55-GAMBLERS-REGRESSION -PotionCheckPath coverage/unattended/potion-batch-045-gamblers.json` |
| `PR50-55-OPTIONAL-CHOICE` | Passed，runId `28167ce3490c41d2bf04bc603732c637`；两种可选选牌空选的严格差分 | `-ScenarioId PR50-55-OPTIONAL-CHOICE -MonsterMoveChecksPath coverage/unattended/card-on-play-batch-042-choice-zero-optional.json -EnemyCurrentHp 100` |
| `PR50-55-CLASH-PLAYABILITY` | Passed，runId `376b830cdecb499aa4a9c0fe9a7a527e`；先出防御再出 Clash，2 动作、2 节点/4 转移、T1 零战损、未镜像项为 0，增量回放通过 | 见下方完整参数 |

```powershell
./tools/run-unattended-test.ps1 -ScenarioId PR50-55-CLASH-PLAYABILITY -HeadlessInstance pr50-55 -TimeoutSeconds 120 -CardId "" -ClearPlayerPiles -CardsJson '[{"cardId":"CLASH","pile":"Hand"},{"cardId":"DEFEND_IRONCLAD","pile":"Hand"}]' -EnemyCurrentHp 10 -InitialPlayerEnergy 1 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -VerifyIncrementalSearch -ExpectedInitialFirstActionCardId DEFEND_IRONCLAD -ExpectedInitialExecutableActionCountAtLeast 2 -StopAfterInitialSolverResultAssertion
```

首次 Clash 请求 `990db5f805574a8c8c050d44a0fa136a` 因同时要求卡牌 ID 与标题的测试参数被单独使用而失败；改为首动作与动作数断言后通过，行为源码未改。首次 Bash 语法检查使用了不存在的安装路径，定位本机 Git Bash 后通过。

上述用例证明原版相关通道回归通过。第三方战略估值委托、形态药剂、预视弃牌和未登记第三方可打出条件的专属夹具，本轮未执行；PR 作者提供的观者结果仍为作者历史证据。未运行可见 Steam 联动或完整战斗回归。测试实例已停止。

## 2026-09-07：Ritsu目标类型查询缓存

10项直接合同覆盖静态正负查询、live旁路、动态晚创建、并发和程序集卸载。固定0.31.0研究基线的Headless目标分配−34.4%、可见Steam−33.1%，完整动作/126非时序字段一致；Aeon哨兵行为一致但分配+4.5%，保留未解释限制。当前PR基于main `0552b33`，不将历史A/B标为新上游政策下的结果；详见 [验收与复现](performance/metadata-target-type-cache-20260907.md)。

## 0.31.2 定版

收录 PR #15、#18、#43。本次仅修改版本与发布资料，沿用下列已完成的定向验证，执行最终 Release 构建；未追加完整发布门禁或可见 Steam 双 Mod 联动验收。
## 2026-09-08：SeedOracle 规划快照 API v6

伴生 SeedOracle 的 `smoke/run-planning-smoke.ps1` 调用真实独立 worker。`PLANCOMBAT01` 普通战斗、`PLANCOMBAT02 -SimulationCase unknown` 问号点战斗、`PLANCOMBAT03 -SimulationCase event -SimulationEvent DenseVegetation` 多页事件回血后战斗，均 3/3 完成且严格恢复检查通过；样本结果可写回规划，主进程状态/RNG/地图指纹不变。单样本短搜 2000ms、整体 30000ms，批次上限 120 秒。

报告保存在伴生仓库 `docs/validation/planning-combat-2026-09-08.md`。没有逐个端到端验证所有事件或作可见 Steam 性能结论；失败或未完成的样本不作为规划参照。

补测 `PLANCOMBAT04` 木偶事件战斗后原生 Resume、奖励重放与 `PLANCOMBAT05` 普通战斗各 3/3 完成；累计 15 个样本。伴生面板 Debug 自检通过。完整 AutoSlay 在 120 秒内未完成，未计为完整跑局通过；无头正常退出仍报告 Godot 资源释放告警。

## 2026-09-06：PR #43 集成

`PR43-PRECOMBAT-API-INTEGRATION`，runId `d44c83b14da04695b79f218e5056d32d`，68.0秒 Passed：规范化恢复、独立 Mod 文件、设置令牌、取消、确定/假设预测、2次 worker 创建和3次复用、静音与主跑局不变。`PR43-EXIT-CLEANUP`，runId `b2d1e2d25beb47eeb976f8062657a4a3`，18.9秒 Passed，另查正常退出后 startup-mods 已清除。Seed Oracle main `29cee875` 对新 DLL 编译通过。详见 [审查记录](pr/pr43-review.md)；未执行可见 Steam 双 Mod 联动。作者原 0.29.x 测试数据保留为历史证据。

复跑：`tools/run-unattended-test.ps1 -ScenarioId PR43-PRECOMBAT-API-INTEGRATION -HeadlessInstance pr-api -VerifyPreCombatForecastApi -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 1500 -StopAfterInitialSolverResultAssertion -HeadlessFastModeForTest Instant -TimeoutSeconds 120 -ExitOnComplete`。Bash 对应 `--verify-pre-combat-forecast-api`，其余使用现有 kebab-case 参数。

## 2026-09-06：PR #18 第三方 OnPlay 边界

`PR18-FOREIGN-ONPLAY-BOUNDARY` Passed，runId `c374c02c64a34423b8f99a1da976a7b0`：真实 Harmony Prefix 安装/卸载，覆盖首次根捕获后新增补丁、已声明非玩法来源、未知来源、移除补丁后的正常捕获及 live 状态不变。既有 `PredictionFailureBoundaries` 和首结果增量短搜通过，3 节点/11 转移、无药零损 T1。Release 编译零警告/错误。夹具 `coverage/unattended/pr18-foreign-onplay-boundary.json`；未逐一覆盖 Prefix/Postfix/Transpiler/Finalizer，也未运行完整战斗或可见第三方 Mod 组合。

## 2026-09-06：PR #15 药水分档

`PR15-POTION-VALUE-TIERS` Passed，runId `11959921f03041e9a9f6fe7001023315`：校验 9/14/18 HP 准入门槛、Token/可再生免费、龙涎香独立计价、救命和强制用药，以及根快照中的高档成本。Short1500ms、增量验证、首结果停止；3 节点/11 转移，无药零损 T1。Release 编译零警告/错误。夹具 `coverage/unattended/pr15-potion-value-tiers.json`，不代表静态分档在所有情境都优于原规则。

## 2026-09-06：SL 路线记录

- `ROUTE-CACHE-RECORD-V0111`：`47626b91d2834703a403819e5ef2ae2e` Passed，验证独立磁盘副本、动作/选择/预测一致、策略与真实 HP 变化隔离、首次记录保留、Reset 后命中及手动重算。
- `ROUTE-CACHE-RESTORE-V0111`：新游戏进程 `5a23389a9b8e4ed485b28a912711974b` Passed，从上一进程文件恢复路线并于 T2 完成原生部署；部署过程断言没有额外搜索。结果协议的节点/耗时仍是被恢复路线的历史指标，实际恢复事件和搜索次数由 `ROUTE_CACHE_HIT` / `restored` 审计记录。
- 回合开始原生选牌：`70c2a626c1614211b05b867beac7588b` 记录、`9085ab68abd14ae49333cb51327ebd28` 新建战斗后恢复，均 Passed；恢复项断言 `InitialRouteCacheRestore`、`TurnSetupNativeChoiceOrder` 和界面恢复状态。夹具沿用 `initial-gambling-chip-397.json` 的遗物与断言，将 seed 固定为 `ROUTECACHESETUP031`，scenario 分别设为 `ROUTE-CACHE-SETUP-RECORD-V0111` / `ROUTE-CACHE-SETUP-RESTORE-V0111`，同一实例依序运行。
- 夹具：`coverage/unattended/route-cache-record-v0111.json`、`route-cache-restore-v0111.json`。在同一 headless 实例和同一 DLL 上依序运行，第一项退出进程、第二项重新启动。固定 seed `ROUTECACHE031`、敌 HP12、起始能量1、Short1500ms、Instant/0秒、每请求120秒上限。
- 早期测试两次失败来自夹具：首次删除尚不存在的缓存目录，以及未启用无人测试的后续回合自动搜索。均修正后取得上述证据。原生保存菜单和各快速 SL Mod 的按钮未逐项操作验证；这里验证同根跨进程重建和会话生命周期恢复。
- Release 编译零警告/错误、Windows 结构门禁通过。未运行 Linux 游戏或可见 UI 验收。
- 缓存命中场景使用普通搜索模式；增量语义验证及阶段性能测量显式跳过缓存读取，保证它们实际执行搜索。最后只补充了该测试模式准入条件，普通恢复的行为证据沿用上述结果。

## 0.31.0 定版

收录九个玩家 PR，逐项说明见 [0.31.0 更新日志](releases/0.31.0-RELEASE_NOTES.md)。本次仅变更版本和发布资料，沿用下列本会话已完成的集成与碎骨定向验证，执行一次最终 Release 构建；未追加完整发布门禁或可见性能验收。

## 2026-09-06：PR #48 碎骨

`BONE-SHARDS-OSTY-REPLAY-0300` 通过，runId `a6540dba59014aeb8ee79f00dee4f050`：奥斯提 10 HP，连续打出两张碎骨，逐动作 actual/simulated 严格差分一致；第二张不会额外加盾。夹具 `coverage/unattended/bone-shards-osty-replay-0300.json`。Release 与 CoverageCatalog `--verify-effective` 通过；未单独构造攻击触发待选牌的碎骨场景。

## 2026-09-06：八个 PR 集成验证

本轮构建、结构门禁、容器与 GC 合同、Windows headless 资源隔离、失败边界、Fork/根/控制器、DOP1/DOP2、终局增量回放、长循环与保命遗物完整自动部署通过。[直接证据与失败修正](pr/integration-39-47-review.md) 单独记录，不覆盖下方原 PR 历史证据。新增可重跑夹具：`coverage/unattended/pr-integration-lizard-tail-rescue.json`。

> 当前发布：CombatSolver `0.31.0`、塔 2 `0.111.0`、RitsuLib 实测 `0.5.18`（清单最低 `0.5.13`）、CombatSolver 内置战斗模拟引擎。下方历史版本记录保留各自验证范围。无人测试运行隔离的原版 `--headless` 游戏进程，不使用自建 STS CLI；性能最终门槛另由 Steam 可见会话验证。完整战斗基准使用 `Instant / 0 秒` 部署。

单项启动器使用本 worktree 的构建产物与私有游戏/Mod 快照，各实例独立保存 Windows APPDATA/LOCALAPPDATA 或 Linux XDG 数据及协议。内容变化只重启当前精确认领的实例，不能按进程名结束其他任务。实例目录/主机租约由平台 `headless-runtime` helper 管理，请求及静稳 Ready ACK 仍由原启动器管理。默认 exclusive；双方显式 parallel 时，主机资源允许最多两个游戏。预约是准入记账，不是硬配额，暖进程与 Held 仍占名额。批次最后一个请求须 ExitOnComplete；取消、Failed、超时清理本实例。Linux 默认 Instant；Windows 可显式 `-HeadlessFastModeForTest Instant`。参数、资料隔离、队列规则与检查入口见 [Headless 实例与并行测试](HEADLESS_TESTING.md)。并行样本不能用于单场速度、GC 暂停或峰值内存 A/B。

单项启动器未请求退出时会保留 marker 精确持有的 headless 游戏，供身份兼容的请求复用；完整矩阵遵守有界生命周期组。当前实例、产物冻结、并行预约与参数见 [Headless 测试隔离](HEADLESS_TESTING.md)。游戏/Mod 内容、私有数据目录、实际可执行文件和进程出生身份共同约束复用，不仅依赖 PID 或版本号；Linux 另核对进程环境。只有异步工作静稳且收到匹配 `schemaVersion/runId/held` 的 Ready ACK 才能复用；Failed、超时或取消只清理精确认领的进程。Windows 使用私有 APPDATA/LOCALAPPDATA，Linux 使用私有 XDG；两端关闭 Steam，从各自冻结快照加载 RitsuLib，不临时写入源游戏目录。Linux 默认测试速度 Instant，Windows 可显式 `-HeadlessFastModeForTest Instant`。并行数据不作为单场性能 A/B。

## PR #45 原分支验收摘要（2026-09-06，历史证据）

用户本次明确允许少量战损或回合数回退，停止为恢复全部旧回合目标继续试验；验收重点是通用正确性、避免明显战损及严重性能退化。最终灵魂枢纽、Phantasmal、受感染棱镜相对原历史样本分别多损 2、5、2 HP。按用户允许小幅回退的要求，本次以所测样本最多 +5 HP 且存活获胜进行验收，并披露实际差距；并非用户指定了精确 5 HP 阈值，也不是全部场景不退化。外骨骼虫仍零损 T10，旧零损 T5 不再作为必达目标。Smart 药水价值门槛与可接受战损停止规则保持上游政策；不为追求旧零损强制额外用药。下方历史 FAIL、实验撤回和暂停结论仍按当时标准保留；协议 Passed、模拟回放通过、首结果质量、实际部署与性能证据互不替代。

最终行为源码 `6ea7dc4` 已正常合并 `upstream/main` 的 `b04d3ec`；任务改动提交 `3864f2c`。全部上游 worldline 回滚已接受，移除失去 latent 消费者的 `AttackPlays` 闭包及专属 `StrategicContext` 测试并恢复上游惰性 `Build`。最终收敛阶段只做上游集成，不再追加策略实验；v77 结果只属于合并前源码。

| 最终集成验证 | 当前直接证据 | 结论与限制 |
| --- | --- | --- |
| Release / 结构 / 入口解析 | `6ea7dc4`：Release 7.79 秒、零警告/零错误；Bash / PowerShell 66 文件门禁、PowerShell launcher 解析通过 | 静态与构建通过，不能等同完整行为门禁 |
| 定向语义与搜索 | 原 22 项为 21 Passed / 1 Failed；Kaiser 修正预期的独立请求随后 Passed | 22 项适用用例通过；唯一旧失败是 T2 必须复用断言，原 Failed 保留，不能改称原批次 22 Passed |
| 正常可见 Steam | 未验证性能；`bad1543e22c54a2a966179e3d98e498a` 根捕获被现有 Ave Mujica subscriber 保护拒绝，Solve 未开始，launcher 120 秒超时退出 1 | 非 headless、真实正常聚焦窗口；120 秒不是搜索耗时。兼容阻塞不作性能通过，不绕过现有保护 |

### 最终定向结果（行为源码 `6ea7dc4`）

以下数据取各请求结果 JSON；除明确标注原版部署/差分的项目外，质量为正常搜索首结果。展开/转移/选择均为请求级总工作，而非选中 solver 指标；不把参数标为 DOP2 或聚合字段相同自动等同于逐转移、完整动作或性能 A/B 证明。

| 项目 | 结果与 runId | 证明边界 |
| --- | --- | --- |
| 策略合同 | Passed `68de2db369034ba7aebb9126140f7516` | 实际执行 SearchPolicySnapshot 合同 |
| Kaiser 嵌套边界 | Passed `9f490fedaf5a4b0691b4cdd949c96bd0` | 最小嵌套边界，不是整场部署 |
| 长隐藏相位 / 长增长伤害 | Passed `f3a76c24d4d14d988e610fd9f2b6fb7e` / `b6cc4ef97305421da996a5974a1ad9de` | 1200 动作/1200 洗牌与 892 动作/445 洗牌，均预计零损 T1；请求工作分别 1200/2400/0 与 1784/3570/2 |
| 同回合停滞 DOP1 / DOP2 | Passed `3d719ea0f46c4b44bd8fd057bd312d97` / `6c9c76e6ea7c4a73975519c30bc6ec14` | 均 10/20/0 后有界停止，不选空转；不是用零损终局证明停滞无限正确 |
| 有限成长 / 低损优先 | Passed `385a23bdc02048fdb15aa8542b09ac1b` / `8f711cc1874e424781f7e2e7f60cd917` | 前者 32 动作、64/130/2；后者 20/57/0、零损，不采用卖血动作 |
| 跨回合正例 / 停滞对照 | Passed `1ccb5c0066a14a729a2ce7fafc795c9a` / `dd6cf5c2f2734e6cacfc4c8e6f3ea857` | 正例 513/770/0、预计零损 T17；对照 78/117/0、敌 HP57 保留且不选无收益防御 |
| Persistent DOP1 / DOP2 | Passed `7e2d2545c85f4f46b8cf03593436567d` / `57815552b7674413a688596863c34d3a` | 均零损 / 6 HP / T1 / 零药、39 动作、4439/17886/4190；DOP2 实际最大并发 2 |
| 灵魂枢纽 DOP1 / DOP2 | Passed `5e399067e94849ec90b2a0c27a970905` / `85d3c75cfb2b42cd8904aa340e04f75b` | 均损 3 / 95 HP / T7 / 1 药、9919/67097/32140；相对原损 1 多 2 HP，接受；不是 v77 的损 1 / T4 / 零药 |
| 自定义战斗正常搜索及原版部署 | Passed `545078fcfef5433c93a2f742a320a02b` | 实际 T1、损 3 / 1 HP / 1 药 / 7 洗牌 / 19 动作，UnexpectedReplans=0、Instant 速度恢复通过；总 19859/66214/7910，不能用选中层 1820 展开隐藏请求总成本 |
| 外骨骼虫 | Passed `48e0f5095716411f8203b2a65792afe1` | 损 0 / 97 HP / T10 / 零药、14672/157206/97502；T10 作为已知限制接受，未找到旧 T5 |
| Phantasmal | Passed `bc980a2eb8044d71acf92b5f4f49f5cc` | 损 5 / 5 HP / T8 / 零药、10230/124024/83377；对旧零损 T3 / 1 药多 5 HP、少 1 药，按本次放宽口径接受，不回写旧严格门槛 |
| 受感染棱镜 | Passed `f82f35236d2f4cb6bbcb5db9e75f2306` | 损 6 / 54 HP / T6 / 1 药、18441/125939/64932；对旧损 4 / T5 多 2 HP，接受 |
| Kaiser 原整场请求 | Failed `ee51e6f651244ef6b89b57285d4c1a9f` | 实际 combatEnded=true / T2；仅旧“第 2 回合必须复用”断言失败。预测为 12 张 T1 牌及 EndTurn，T2 开头 Mayhem 自动出牌已胜，无 T2 PlayCard；searches=1、reused=0、各 Unexpected=0，不是状态漂移或计划外重算 |
| Kaiser 终局部署修正验收 | Passed `f220b8f2822942e28fa48d00093597d9`，实际 combatEnded=true / T2、UnexpectedReplans=0 | 仅去掉 expectedReusedTurn 与对应复用 HP 断言；原输入/预算、expectedFinishedPlayerHpAtLeast=80、expectedFinishedTurnAtMost=2、expectedUnexpectedReplansAtMost=0 全部保留且通过。独立请求不覆写上行 Failed |
| 永世沙漏 | Passed `2cb489b2b8ad4a9d9d5ccfc869f0d7dc` | 损 14 / 40 HP / T7 / 零药、32136/641116/488387；优于原历史损 33 / T9，首结果并非原版整场部署 |
| 长线 4 GB No-GC / 常规 GC | Passed `224b2d2f1c1247f7a20bd085c8a82a0d` / `996f5a6169b342bc9fe1442265adedf5` | 两者均损 0 / 65 HP / T9 / 零药、7097/41023/18723；质量和请求总工作相同，不因此声称完整状态等价 |

### Headless 性能严重退化筛查

时间为请求级搜索计账时间，GB 为十进制累计 worker 分配，不是端到端墙钟、存活堆或进程峰值。当前与历史记录的源码、路线、工作量及冷暖/GC 条件并非全部受控一致，以下只用于排除所测样本的明显失控，不宣传性能收益百分比或受控加速倍率；可见 Steam 另列证据。

| 样本 | 本轮 秒 / GB | 历史对照与限制 |
| --- | --- | --- |
| 灵魂枢纽 DOP1 / DOP2 | 17.386 / 3.740；9.610 / 3.743 | 原 v9 分别 20.357 / 3.009、17.236 / 4.528。合并前 v77 DOP1 仅 3.828 / 0.730，本轮明显慢且分配更多；上游 worldline 已回滚，路线和工作量已变，不隐藏代价，也不称同工作量退化归因 |
| 自定义战斗 | 2.877 / 2.429 | 包含失败/恢复/药水层的全部请求工作；本轮实际部署通过，不取选中 solver 的较小指标冒充总值 |
| 外骨骼虫 | 15.989 / 8.820 | 原 v9 17.698 / 7.032；v77 25.165 / 12.507。当前分配仍高于原 v9，不声称所有维度改善 |
| Phantasmal | 9.365 / 6.562 | 原 v9 14.921 / 6.399；本轮战损多 5、药量少 1，不能当同质量 A/B |
| 受感染棱镜 | 10.822 / 6.905 | 原 v9 19.592 / 10.538；本轮战损多 2，不能称无质量代价优化 |
| 永世沙漏 | 73.548 / 37.642 | 原历史 84.281 / 41.718、损 33 / T9；本轮损 14 / T7，仍是高分配长搜，不推广为低内存或无卡顿 |
| 长线 4 GB No-GC / 常规 GC | 16.511 / 5.578；29.178 / 5.570 | 原同根 v0272 72.151 / 27.102、97.927 / 27.161，均损 9；当前均零损，搜索工作不同，不宣传加速倍率 |

本轮长线 4 GB No-GC 的累计/最大 GC 暂停为 `125.682 / 61.900 ms`，常规 GC（No-GC 关闭）的对应值为 `12213.517 / 180.877 ms`。两侧请求工作一致，仅记录本次观察，不等于重复受控基准，也不替代可见帧卡顿与峰值内存测量。

### 正常可见 Steam：现有兼容保护阻塞

runId `bad1543e22c54a2a966179e3d98e498a` 由真实 Steam AppId `2868840` 启动，PID `409793`，X11 `WM_CLASS=Slay the Spire2`、`WM_STATE=Normal` 且聚焦，没有 `--headless`。约 78.67 秒进入初始结果断言前发生 `SEARCH_SETUP_FAILURE stage=combat_root_snapshot`：`AveMujica.AveMujicaCode.Ftue.DreamspinFtue` 是尚未支持的 Ave Mujica gameplay ModHelper subscriber。对应 `PredictionModHookSubscriberCapture` guard 相对上游无改动；搜索根未建立、Solve 未开始。

launcher 在 120 秒超时退出 1，不是 solver 搜索 120 秒，也不构成可见性能或卡顿通过。玩家原 DLL 与 manifest 已由清理恢复并记录 `PLAYER_MOD_RESTORED`，完整游戏日志保留。本次不改变兼容策略、不绕过保护、不扩大第三方适配范围。正常可见性能仍未验证；headless 数据仅用于严重退化筛查。

据用户放宽后的质量要求、定向行为结果和上述 headless 基准提交正式 PR，公开质量差距与可见性能限制。完整发布门禁、全量 CoverageCatalog 和 Windows 原生游戏验收未在本轮完成。旧版最小事务/Fork/终局差分、v52 GC 合同与固定工作量 A/B、v67 恢复合同、v66 自动部署均按各自源码阶段引用。PR 交付说明见 [通用搜索与正确性后续 PR](pr/generic-search-correctness-followup.md)。

### 合并前 v77 摘要（历史证据）

| 项目 | 合并前直接证据 | 结论与限制 |
| --- | --- | --- |
| v77 L0 / 策略合同 | Release 零警告/零错误；Bash / PowerShell 66 文件门禁；`373de14258414197b22e534152302b47` | 通过。验证普通同分排序的完整政策标签隔离与既有路由位置不动，不等同完整行为门禁 |
| v77 灵魂枢纽 DOP1 / DOP2 | `45cd4d4d81ee42498d9efd83e9e8cf9f` / `feefa42bb2d54ea99b1eb8ac08e4e54d`；均损 1 / T4 / 零药 | 原配置首结果通过；2112/13480/6996 工作量、80 项结果字段、26 日志动作、4 回合结果一致。不同 GC/冷暖条件，非性能 A/B、逐转移严格等价或本轮整场部署 |
| v77 外骨骼虫 | `00d1a845cfa74866a4f7e12910db2e18`；损 0 / T10 / 零药，13746/208100/147857 | 通过当前首结果哨兵；T10 为用户放宽后接受的质量限制，不声称旧 T5 已恢复 |
| v77 自定义战斗 | `9bc0a475157246e4a351a149e5ca012b`；损 3 / T1 / 1 药 / 7 洗牌 / 19 动作 | 原配置首结果通过；请求 28726/97786/10486，不把选中 solver 工作量当全部工作。旧 v66 部署证据并非本轮复跑 |
| v77 有序派生键清理 | `02354eecb8c145368efdf5ca083c3f08`；26 基础前缀与五个牌序变体 | 完整/增量与根/live 不变通过；完整 StateKey / Continuation 保留。不启动 Solve，不作独立性能收益结论 |

以上均为合并前证据，不构成 `6ea7dc4` 的最终验证；历史质量限制也不能覆盖合并后产生的新结果。

## 通用搜索、语义与 Headless 验证记录（开发中）

以下逐项记录保留对应 vN 阶段的证据与当时判定；当前交付口径以上方摘要为准。v66 同回合落选续搜已通过 Custom 首结果与实际部署，v67 已补边界合同。仅现有失败窄搜开启；前缀根/动作工作计入原节点预算，不能把单出边回放当成新的完整节点展开。

| 验证 | 当前证据 | 边界 |
| --- | --- | --- |
| 无保留路由普通同分排序 | v77策略合同 `373de14258414197b22e534152302b47`；Soul `45cd4d4d81ee42498d9efd83e9e8cf9f` 正常1损/97HP/T4/零药 | 原20秒/DOP1/GC、2112/13480/6996，旧质量审计通过；同Turn/full6D各自原位置排序，带旧保留路由的组内位置固定，不改必保/席数/预算。Release与两端66门禁通过，非本轮部署证明 |
| v77 Exoskeletons非退化哨兵 | `00d1a845cfa74866a4f7e12910db2e18` 零损T10、13746/208100/147857，当前v66基线审计通过 | 原VeryHigh/DOP8/NoGC16/Smart；旧T5质量审计仍失败，不把当前非退化当作全部目标达成。headless共享进程数据不作最终性能结论 |
| v77 Soul DOP1/2正常一致性 | DOP2 `feefa42bb2d54ea99b1eb8ac08e4e54d`，同1损/97HP/T4/零药和2112/13480/6996；实际最大并发2 | 80项非时序/非调度RESULT、26条日志动作、4条回合结果及政策路线一致；DOP2 NoGC4保持/rollover0。原断言、GC和冷暖配置不同，非性能A/B或完整PlanAction字节等价 |
| v77 Custom恢复非退化 | `9bc0a475157246e4a351a149e5ca012b` 原配置短质量通过，3损/1HP/T1/1药/7洗牌/19动作 | 获胜solver仍10742/39529/3802，请求28726/97786/10486略变；不宣称全部工作量等价，未重复v66原生整场部署 |
| 实验有序派生键清理 | v77 `02354eecb8c145368efdf5ca083c3f08`，26基础前缀及五变体第11/12步完整/增量、根/live通过 | 生产只去掉实验派生哈希及其冗余核验，完整StateKey/Continuation不变；Testing对每个已见变体逐一比较四牌堆token数组，不反向代替通用状态键。不启动Solve，未测量独立性能收益 |
| 新鲜生成粗族单席补位（否决实验） | v75 `7610a80fe425460c8917dfa14689a2ca` 合同通过；正常Exo `65db7899b0424deba05e9eca2f0058ad` 损1/T4 Failed | Release零警告错误、两端66门禁通过；4803/99681/75856，原VeryHigh/DOP8/NoGC16/Smart。提前结束不能代替零损目标；完成拒绝版诊断后已撤回生产因素及专属合同/门禁 |
| v75拒绝版第4步保留及第5步断点 | `5196a01b963a4e558a1808160c6bb00b` 6407事件NoDrops，完整四敌/根/live证明通过 | 4 raw2000→selected90→Final→Expanded，旧required/routing/容量不变；5生成/准入后Prune11丢失，该次不含5整池。诊断工作量同正常4803/99681/75856、仍损1/T4；不作质量或性能通过 |
| v76拒绝版第5步完整候选池 | `46ec3b33c3e34e889448b1b557f429b9` 3997事件NoDrops，四敌24前缀/根/live通过 | 目标raw257无必保/路由/选中；135全局选中均在153最终集合。33节点来源族有11个最终存活，完整生成签名未变；同父攻击敌3/2存活，敌1/4落选，战术前三键相同但防守投影不同。仍损1/T4，不支持延长生成保护；生产v75已撤回 |
| 普通同分截线同政策战术排序合同（已撤回实验） | v74 `f82e142ca2e64d52933ceeda728c1491` SearchPolicySnapshot通过，Release零警告错误、两端66门禁通过 | 真实9/7目标换入、完整6D+Turn逐维交错隔离、稳定词典序、必保原位及全部既有旁路；邻接质量回退后，仅撤回本因素及专属合同/门禁 |
| v74 Soul目标通过但外骨骼虫回退（已撤回） | Soul `72efaeef12f64c2b85b0a83b08120b64` 1损/T4通过；Exo `de76b95d766e4398b444010ed98421b0` 0损/T11，质量审计失败 | 原v9输入/预算/Smart。Soul2112/13477/6994；Exo14485/204622/142069，相对当前v66的0损T10回退且旧T5未达。无观察器，只验首结果，不是实际部署或Steam性能结论；未跑DOP2/全矩阵 |
| Soul实际换序代表与第18步整池 | v73 `8591487e906b426383ed306696ba6ecb` Passed，1406事件/1StrictAliasAnchor | 真实18前缀+原样末8步逐步完整/增量、根/live通过，1损97HP/T4；18 raw35无routing，普通同分块9选7遗漏目标。只证明战斗后缀及实际剪枝，不证明调度等价；原质量仍19损T7。Release零警告错误、两端66门禁通过 |
| 外骨骼虫第4步真实整池 | v72 `2bef44a967284a4c9bf39e9404b4295d` Passed，6400事件NoDrops | 真Exo solver第10边界：3109排序、91必保/135限额、96路由/54配额；目标raw2000无routing/required/selected/Final。完整保留池连续索引通过。复用进程日志须按Sample/solver分开；原质量0损T10、13746/208100/147857未变，非性能证据 |
| 五生成上下文完整后缀与联合路径观察 | v72 `adb95fc0a6f046afbe3a33ec1278b19f` Passed；五条26步各1损/97HP/T4/零药，626事件NoDrops | 仅第8步重绑定，后续全部冻结；完整/增量及根/live通过。按完整动作与实测政策分桶，防御变体12真实routing10/quota13、selected47并展开，准确展开到14；15为TT拒绝且有同状态别名，不作全路径丢失结论。正式搜索仍19损T7；Release零警告错误/两端66门禁通过，无生产保路变更或性能结论 |
| v71全部选牌输出细分普通席（已撤回） | 正常 `bf3b1d42f48344ec9e27c0c2d9873635` 损43/T11；诊断 `48fd2f875573474d8ca7739a8f900a1f` 第11步整池断言Failed | 正常主6926/45680/22819，请求8480/54547/25635原20秒；诊断110事件，两solver准确1–7 Expanded，8准入后Prune丢失，11未到达。仅通过Release/两端66门禁，未跑新静态合同/Exo哨兵；全部357行新普通席因素已撤回 |
| 外骨骼虫已知早胜路线原版严格对照 | v70 `9c8e6cf093bd40aa8149e9d225d66c44` 通过，实际97/103HP、0损、0药、T5 | `KNOWN-EXOSKELETONS-ROUTE-NATIVE-V0111`：24完整预测先冻结；24原版动作、6Primary/4Nested/4EndTurn逐敌StateDiff/Continuation、阵容/死亡/行动及累计伤害/药水/洗牌事件一致。真实清理前四敌取证并等待CombatEnded；不Solve、不代表生产UI部署。最终Release零警告错误、两端66文件门禁通过 |
| 外骨骼虫已知早胜路线首次丢路 | v70 `f538b44ac3d045f8a07a01f563abe7cc` 27事件NoDrops；准确第4步首次Prune丢失 | 原策略DOP1/NoGC16；1–3真正Expanded，4横祸完整Nested已Generated/TT接受/动作准入但无PruneFinal。实际仍0损T10、13746/208100/147857，与v66工作量相同；诊断非质量或性能通过，下一次整池锚点设4 |
| 生成上下文普通席合同（已撤回实验） | v69 `e4981f5eb9494a26bcd65cc438849e78` SearchPolicySnapshot通过，Release零警告错误、两端66文件门禁通过 | 原路由/必留/额度未变；替身合同验证无碰撞旁路、完整标签隔离、必留槽位、细上下文去重、公平与确定性。因真实质量未达标，筛选及专属合同已撤回 |
| v69普通席Soul质量与拒绝版诊断 | 正常 `872c23e436754eb9af4a1ff2c3272513` 损17/T11；诊断 `4c30e68dcffd4b4fbd5a857a952584a3` 首次丢失准确第12步 | 正常主7399/69026/43629，请求7964/72340/44590耗满原20秒；诊断333事件NoDrops，11从普通席selected38真正Expanded，12生成/TT/动作准入后Prune丢失。无12整池原因证明。诊断损24/T7受时限影响，不当作正常质量或性能证据；未跑Exo哨兵 |
| 新有序牌堆键真实上下文合同 | v69 `ab2936c3280941148ff2b9d19d4f8a92` 通过 | `KNOWN-SOUL-GENERATION-CONTEXT-V0111`在第11/12步各得到五个不同有序键、相同无序键；26已知前缀及五变体完整/增量、根/live不变。无Solve或原版动作 |
| 外骨骼虫早胜约束完整回放 | v69 `9739c9b9d20f4924986e8ba0357935cf` 全24步通过，模拟97HP/0损/T5/0药 | v9的24步约束加同导入根v31真实生成候选的第4步4Nested；6Primary/4EndTurn，逐敌完整/增量、阵容与死亡账本、root/live不变。不是v9 PlanAction字节恢复，不是原版或搜索发现证明 |
| Soul生成上下文具体牌序 | v68 `8fc546a16f184e5f95c08a3b1d42e6bc` 通过，26已知前缀及五变体11/12步完整/增量、根不变 | `KNOWN-SOUL-GENERATION-CONTEXT-V0111`；差异仅Hand语义token顺序，其他三堆相同；同一冻结过牌动作后仍如此。不Solve，不把差异本身视为胜负或调度证明 |
| 外骨骼虫旧零损早胜约束重建 | v68 `34f71157c08f4711a02e4d842e86ab0c` 前3/24步通过，第4步横祸因未知Nested明确失败 | `KNOWN-EXOSKELETONS-ROUTE-REPLAY-V0111`，逐敌完整/增量及根不变；Source=CATASTROPHE/Hand/AutoPlayRepeated/必选1/四候选，旧ACTION无记录，不能默认选牌。旧0损T5在当前引擎尚未证明合法；无Solve/原版动作 |
| 同回合落选恢复边界合同 | v67 `589c10309b54482397fdd66162811c91` 通过；Release零警告错误、两端66文件门禁通过 | `KNOWN-CUSTOM-DEFERRED-FRONTIER-V0111`不Solve；19个严格前缀及18步恢复+末步，三次选择/一药。九种预算、停止、取消、异常及成功路径均完成；默认关/仅窄搜开合同通过。合成政策元数据不证明实际调度资格，TT不检查私有标签内容 |
| 置顶有序谱系Soul首结果 | v67 `5e037748973c4057b9118fc738855857` 损2/96HP/T7/零药，严格损1目标失败；因素已撤回 | 原20秒/VeryHigh/DOP1/GC，无观察器；5109/33202/16639，比原损19改善但尚未达损1/T4；有序保留用满2048。不改Smart/可接受战损停止规则 |
| 置顶有序谱系Exoskeletons哨兵 | v67 `b365cdf18acf4287a49ccb4a44c4fe4d` 0损T11，晚于当前v66 T10，因素撤回 | 13727/155781/97093，协议Passed只代表零损断言；不以此覆盖结束回合回退。只撤回置顶builder因素，保留v66恢复与新边界合同 |
| v67拒绝版Soul路径诊断 | `69e7e9443f4e4bf190145257b634fd7d`，419事件无丢弃，26前缀及根不变通过 | 准确第11步raw59/parent47/routing13/quota13，leader仍未进入最终Prune；未实际展开后缀。诊断结果损2/T7、5109/33202/16639与无观察器相同，非质量通过 |
| Custom同回合落选续搜 | v66 `f5685edaae3c416ab1ccd09c1746002c` 原配置首结果通过，T1/1HP/损3/1药/19动作 | 未注入已知路线；用药恢复77叶、77根+570前缀动作计入10742/12000节点额度。全部76快照值恢复核对；请求28736展开工作/97719转移/10447选择，包含无药失败恢复成本，非性能收益声明 |
| Custom新路线实际部署 | v66 `6d25ca7a3e6a4252adcc0d5fe902bf3b` 原生T1结束战斗、火焰药水使用、UnexpectedReplans=0 | 同配置自行搜索后正常部署19动作与三次燃烧契约选择；Instant/0秒与速度恢复检查通过。不是显式已知路线，也不是每前缀全状态差分证明 |
| Exoskeletons当前基线哨兵 | v66 `d698c918a51f462eb93a6dec1adb7753` 0损T10、13746/208100/147857，与post0300/v35同聚合质量和工作量 | 未启用窄搜/落选恢复。旧T5目标未完成；较早T9不是当前合并后基线，v62 T10不能据此再算当前回归 |
| 有界剪枝恢复合同（v54 原型） | `5b7cd7d6882b40eda490bf166bed78e2` 三组入口全部通过；Release 0警告错误，两端门禁66 | `BEAM-CUT-RECOVERY-V0111`：值存储/分页/公平/祖先/容量；协调器替身累计预算/零工作/取消；真实根上的合成节点保护与最终别名计数。没有正式Solve或原版动作，不构成Custom质量通过；同进程后续目标搜索结果另记 |
| Custom 剪枝恢复 v54 | 质量失败 `718a49fbfc5647aba0a656e9b17ad3ce`，HP0/敌347/4洗牌，未部署 | 原配置/首结果停止；总29994展开/112117转移/15259选择，6.614秒、4,189,131,112 B、GC253.254毫秒；共享合同进程峰值3,855,784 KiB。恢复两层分别花满12000展开，用药层2256候选无普通席。原型未达标，不以新增展开证明改善 |
| v55同药量quota替换 | 合同 `96268fdb5d184077a50ccb74fb7cb222` 通过；Custom `350a83e241b545faba1c06670c9abc17` 质量仍失败，原型撤回 | HP0/敌347/4洗牌；29994展开/112192转移/15259选择、6.372秒、4,193,335,816 B、GC6.964毫秒；共享峰值5,026,748 KiB。用药层no_seats=0，却只服务cut17→14第一页，97准入/64实际展开；没有后续哨兵或部署。代码和夹具不再位于生产/Testing入口 |
| 当前引擎Soul已知路线约束重建 | v56 `3cd33a96dd544421b8585c6a6566e420` 全26前缀通过，预计97HP/损1/T4胜利 | `KNOWN-SOUL-ROUTE-REPLAY-V0111`，原导入根，五次真实主选择绑定，完整/增量StateDiff与root/live不变，风险门通过；无额外选择。非旧PlanAction字节复原，未Solve/原版动作/性能，metrics为空；原生对照见v58 |
| 灵魂枢纽已知路线原版严格对照 | v57末击失败；v58 `3a85ce2f16654b6daf976fff1307b688` 全26前缀通过，实际T4/97HP/损1/零药水 | `KNOWN-SOUL-ROUTE-NATIVE-V0111`；先冻结26个完整预测，再执行26个原版动作、5次严格实例选牌、3次原版EndTurn；末击清理前取证并等待CombatEnded。不是搜索发现、生产UI自动部署或性能通过 |
| 灵魂枢纽已知路线纯值追踪 | v59 `4ad27f7d9345427097003a0130d6bb67`，131事件无丢弃；搜索仍损19/T7 | `KNOWN-SOUL-PATH-TRACE-V0111`；原20秒/VeryHigh/DOP1/Smart/GC，前10步生成并展开，第11步类星体选择深谋远虑在外层Prune首次丢失。按完整动作/选择及政策标签区分同状态历史，不向Solve传入已知前缀；仅诊断完整性通过，非质量或性能通过 |
| 灵魂枢纽首次裁剪整池 | v60 `a444d99408ed4145ac060f8fd2c27d36`，98输入/98真实排名/54最终保留、381完整事件；仍损19/T7 | 同入口及预算，仅新增指定外层池观察。目标零基raw67、routing60/quota13，非leader；低于普通截线且不在路由配额，required15/54未满。五个不同前序放回选择具有相同无序生成上下文；不当作策略已修复 |
| 生成选项有序分组 v61 | 灵魂枢纽DOP1/2损1/T4通过，但Exoskeletons损1/T4退化，方案撤回 | Soul `7fbba3fcc7aa4cf09e83b689ef4f63ba` / `a505c55d01b64ce39f3fa58830f5bfc6`：27日志动作、80项非时序/非调度字段相同，实际并发2；日志未序列化全部PlanAction字段。Exoskeletons `3605b08cd8214adb87b58323b40bea74` 零损断言失败，不用Soul单项成功覆盖哨兵回归，不保留旧leader被替换的分组方案 |
| 同分生成上下文伙伴 v62 | Soul损13/T7，Exoskeletons零损T10，方案及有序键已撤回 | Soul `9206ac3b8b0149f1b544c5b82848628b` 的第11步存活、第12步裁剪；Exoskeletons `7f3de2d204ad4fb0b0b056a32638312c` 协议Passed但旧零损T5质量审计退出1，27.528秒/12.178GB分配。v66已纠正当前合并后基线也是T10，不再按更早T9声称其回合数回退。只保留测试共享helper，不保留伙伴保路策略 |
| 自定义战斗已知路线纯值追踪 v63 | `bbb40f801677463ba44047a4b04b3a38` 诊断通过、128完整事件；实际搜索仍死亡/敌347/4洗牌 | `KNOWN-CUSTOM-PATH-TRACE-V0111`，原政策/DOP1；19个冻结前缀回放、shadow/live根不变，同一用药solver准确生成并展开首步。135宽前9个目标状态存活，第10步外层Prune丢失；60宽第6步丢失。第3步准确路线及第5步其他排列的TT拒绝均有同状态同6维标签代表继续展开，不当作故障；第3步Traits已不同，尚未证明别名完整后缀或调度历史等价 |
| 自定义第10步别名后缀及整池 v64 | `49a3fb9418d644d3bb4f158b8e0911ec` 诊断通过；实际搜索质量仍失败 | 1个真实Generated别名原根回放及9步原样后缀逐步全状态/增量等价，T1/HP1/损3/1药胜利；非调度历史等价。1024无丢弃事件，355输入/排名、135全局/177最终保留，目标raw172未进route96/quota69，required90；同上下文raw17以更多即时伤害先入。原搜索9412/35298/4699未改变；构建及两端65项门禁通过 |
| 持久选择上下文SetupFirst v65 | Custom `127761331d114c96b870d61786e6991c` 质量失败，实验撤回 | 原配置/DOP8/NoGC16/无观察器/首结果停止；仍死亡/敌347/4洗牌，9353展开/35073转移/4755选择。只改同context候选顺序且保持集合/评分/预算，不足以恢复完整解；未跑哨兵或部署 |
| 遗物属性在末击后的命令边界 | v58 `b9edcf8d2c3a416baec2caa6505ea610` 两个最小边界、三遗物均通过 | `relic-stat-terminal-v0111`；苦无/手里剑/彩虹戒指在非致死动作正常加属性，致死动作仍递增计数但不施加属性；原版与全根/增量完整状态一致。每个根只打一张牌，不运行Solve |
| Windows helper 预约自测 | `tools/test-headless-runtime.ps1` 通过：双 parallel、exclusive、资源不足、未知游戏、归属、stale、warm | Linux 上的 PowerShell 替身测试；非 Windows 游戏进程或快照实测 |
| Windows资料复制边界 | `-ProfileOnly` 通过：私有拷贝、源资料不变、重解析点拒绝 | Linux上PowerShell文件系统验证，不是Windows游戏验证 |
| 两端结构门禁 | Bash / PowerShell 均 `REFACTOR_BOUNDARIES_OK search_files=64` | 只证明结构边界 |
| Linux helper 原生子进程生命周期 | 11项通过：并发、同实例拒绝、排队、独占、warm、取消/超时、pending/孤儿、stale、PID出生及未知进程 | 真实辅助层 + 私有原生sleep；枚举限定测试域，非游戏协议 |
| Linux 快照隔离 | `--snapshots` 4项通过：A/B不同DLL内容、A更新不改B/源树、旧快照保留、活进程拒绝替换 | 文本DLL替身，不证明实际程序集加载 |
| Linux 快照故障注入 | `--snapshot-failures` 9项通过：find、中间SHA、rm/mkdir/cp、retired mktemp/mv、publish mv、ID mv | 错误显式传播，不越界移动、不形成新game/旧ID错误缓存；旧树仍可恢复 |
| 真实双 headless | PID3841962 / PID3842057 的请求区间重叠约23.7秒；Fork通过 `3ca7afc55dc44476bf13f0ebf2ab6a7b`，Start旧DLL按预期失败 `bd5e0bbd45cd4698aa090070c67fa333`，各自退出 | Linux私有游戏/Mod/协议；不是单场性能对比，也不声称两个语义fixture都通过 |
| 真实静稳复用 | v43差分Passed→Ready→同PID3847699最小根检查Passed并ExitOnComplete；后者 `7ebee077e8e544cc8d0f1e713ea6816e` | 未真实测试Held或Windows；取消/故障互不误杀的细分证据来自原生替身 |
| 矩阵实例传递与清理 | 两端 `test-headless-matrix-runtime` 通过实例/参数传递、暖实例尾部stop、外来身份拒绝、同实例及取消隔离 | mock场景入口；PowerShell取消为适配器测试，不等于Windows原生Ctrl+C |
| stop-only真实入口 | Linux原生替身通过只停本实例、peer保留、stale/absent幂等、未知/PID复用/无marker/已有producer拒绝；PowerShell入口通过stale/absent/noPID/foreign-pwsh边界 | 缺DLL/依赖仍不创建request/profile/snapshot或启动游戏；不是Windows游戏生命周期实测 |
| 真实暖游戏stop-only | v49合同请求Passed→Ready后，精确停止PID4049888；故意指定不存在的构建/源游戏/依赖路径仍成功 | 旧结果 `be1553a145b64023b7d44ceab1ff1456` 保持，PID和marker消失，未创建指定目录；不再发布游戏请求，不代表Windows实机通过 |
| 回手 Start 根修正 | v43 `bf4fa60a07764a28ab452b93d203f9b3` 7项严格状态对照通过 | 包含三次PreDrawStartRoot与原版/连续/Fork/中途根，不是整场搜索 |
| 水银沙漏/千足虫死亡状态投影 | v44失败 `65d54627f8f3435da583ef159763e24a` 仅全灭后的MS0/1误分类；v45修后 `f654b4ad4a1c429f88e4739ff82a8b1a` 8项通过 | 一段1HP、两段原生复活资格；REATTACH→0/22/22不赢，DEAD→全灭；直接/Fork/重捕获根严格状态对照，非搜索排序或错误胜利复现 |
| 跨回合计划终局回合数 | v45基线 `0ae11f4b7f584d819d60d33cb928d276` 错报T1；v46 `8d53449e35b4407fa0a4d4c03164a955` 正确T2；闪电球末尾对照 `bf20264541ab457f975151c5846f4973` 仍T1 | 正式短搜+增量回放；标注、排序共用原版安全点锁定的玩家回合号，不统一给EndTurn加一 |
| 千足虫终局标记与严格状态 | v46 `15b7787fa6ba4dd6bc041fc79cbab81a` 两阶段8项通过 | 一段1HP、两段原生复活资格；额外断言终局Fork与首次锁定不覆盖；非整场性能/搜索质量结论 |
| 敌方开局中毒终局对照 | v45 `2ac6c15441594cb199551ead687fd534` 与 v46 `fd2a5d8612344f18a5a8304de814ae31` 都返回T1 | 修后正式短搜+增量回放，没有因EndTurn误加一；与沙漏/闪电球共享短请求进程，非独立性能A/B |
| 普通打牌强制结束后的终局 | v47 `2414e7137a554f54b8037df5b8adb89c` 6项通过 | 唯一出牌动作原版/根回放/增量回放严格对照，T动作触发T+1沙漏击杀；正式增量断言比较终局标记，Fork保持，释放快照后正式标注仍T+1；无搜索展开/性能结论 |
| Soul 语义修后搜索基线 | v47 `1f43a8bab8fd402baedcf600d4c70397` 质量失败：掉血19、79 HP、T7 | 原20秒/DOP1/NoGC关闭/VeryHigh；总8598展开/64393转移/31687选择；未再出现旧牌堆标注差分，但未达到历史掉血1目标，后续策略实验以此当前基线单因素对照 |
| Custom 语义修后搜索基线 | v47 `d0b4125e662f40ccabed12604aac9ebf` 仍只有死亡路线、敌剩347 HP、洗牌4次；未部署 | 原VeryHigh/DOP8/NoGC16，9412展开/35298转移/4699选择；搜索4.641秒、分配1,403,332,960 B、GC132.458毫秒、VmHWM2,285,236 KiB；未达到历史T1获胜目标 |
| 新鲜选牌父名次同分实验 | v48局部节点合同通过，但Soul质量退化至掉血47/T9（`70f18bd55eda4f7a8f4e6662ef3241c3`）；方案撤回 | 只作失败实验记录，不作为现行策略或通过证据 |
| 拥挤策略与普通候选仲裁实验 | v49合同通过 `be1553a145b64023b7d44ceab1ff1456`；Custom仍死亡/敌347/4洗牌（`c16bbcbbc2564e3eb3b21365d70607fe`），未解决目标，方案撤回 | 9142展开/34420转移/4689选择；4.069秒、分配1,355,445,536 B、GC146.625毫秒、VmHWM2,339,800 KiB；不将少量加速当修复，不执行后续Soul哨兵 |
| 当前引擎重建Custom已知解 | v50 `d519b3ca52f947c89d0ace26dbe9ede6` 通过19个前缀严格增量/全根回放，预计1 HP/损3/T1/敌灭/7洗牌/1火焰药 | 依据v29动作与实际三次单选记录，在当前原归一化根绑定完整卡牌/选择身份；不是旧PlanAction逐字反序列化。原模拟根及实战完整状态保持不变，未Solve、未原生部署、metrics为空；证明当前模拟器能表达该解，不证明搜索已找回它 |
| Custom已知解预测风险门 | v51 `fc247b55b2dc4a04990e1deda3cb773d` 全19前缀及终局通过，额外要求 `HasRisk=false` 且无未补偿PredictionGap | 补强模拟可行性证据；仍未Solve或原生部署，不计作搜索质量成功或性能数据 |
| Custom已知解原版严格对照 | v52 `83d25eef53f847df81d98f0cf18aea4d` 全19个原版动作/冻结预测前缀通过，实际T1胜利、1 HP、损3 | `KNOWN-CUSTOM-ROUTE-NATIVE-V0111`；同一归一化根，原版ManualPlay/EnqueueManualUse，三次选择核对完整实例状态与两个游标，末击原版清理前取证；头部无Solve指标，不当作搜索已找到路线或自动部署通过 |
| 后台Gen2检查点生命周期 | v52 `6c1ef5e88b67423b9e6a30406e089b50` 8组合同通过，复用PID4085610后退出 | `GC-CHECKPOINT-BACKGROUND-V0111`；正常确认/重建、同步上下文、取消与晚manual/引用释放、注入超时排空、旧早manual/开始前失败/epoch捕获前后；确认窗口可暂停但不控制CLR mark。普通完成实测background，超时兜底blocking；非长线性能A/B |
| Custom固定工作量回收A/B（1 GB） | v51同步 `841a2372b6bc4436b34b14d455fc0e02` / v52后台 `e9c77ba6a6394a6bbf604ebae20d018d`；两侧原胜利断言仍失败 | 同根/VeryHigh/DOP8/Smart；52项非时序字段、9412展开/35298转移/4699选择及13条日志动作一致（日志未序列化全部PlanAction字段）。22检查点均重建成功，新版22次background；计账5.481→5.301秒，GC累计/最长1601.244/104.588→46.818/4.977毫秒；峰值1,631,036→1,774,676 KiB（+140.3 MiB）。不当作已找到合法胜利或Windows长线性能通过 |

维护时默认使用分层快速回归：普通语义改动跑单效果严格差分；Fork、跨回合历史和续用改动补一个最小两回合或最早复用边界；搜索/部署改动的最终候选才运行必要的完整自动场。快速 unattended 请求总超时不超过 `120` 秒，超时后缩小 fixture 或记为未验证，不在同一轮延长等待。下方完整矩阵是发布门禁和专项审计入口，不是每次修复都要执行的默认清单。

## 未发布：GC 独立研究（2026-09-05）

固定上游 `5c4b69d`，版本不变；设置与隔离方式见 [研究起点](performance/gc-issue36-research.md)，最终实现、候选取舍和 A/B 口径见 [实施报告](performance/gc-issue36-implementation.md)。首轮 250 节点记录仍只是单次 pilot；下列最终 A/B 使用独立冷进程、固定节点预算和三次中位数。最终长搜及 Smart 样本的完整 ACTION/TURN、工作量和非时序剪枝比较均通过；NodeLimit 结果不代表完成整场。

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `GC36-ROUND2-SNAPSHOT-LIST` | 通过（实际 helper / 7项） | 嵌套、异常填充、单槽/reset、容量上限、旧/复制lease、owner隔离、弱引用释放。命令见 tools/SnapshotListBufferChecks/README.md。 | 2026-09-05 |
| `GC36-ROUND2-AB` | 通过（三次交替，固定工作量等价） | Silent 分配中位 −3.39%，Necrobinder −1.34%；完整 ACTION/TURN、评分、工作量和非时序剪枝一致，耗时无稳定收益。逐轮runId见 [第二轮证据](performance/gc-issue36-round2-results.json)。 | 2026-09-05 |
| `GC36-ROUND2-BOUNDARIES` | 通过（新 parallel 入口） | SearchPolicySnapshot / ForkBoundaries，含DOP1/DOP2等价、实际并发2及历史/根边界；runId `5171caca9cf84baaa3f48884644f3b07`。该并行样本不用于性能。 | 2026-09-05 |
| `GC36-ROUND2-TRACE` | 产物严格解析通过，runner停止等待超时 | 搜索18,572 allocation ticks，Fork权重43.93%；0缺栈/全未解析/报告丢失，19条含部分未解析帧。清理SIGTERM完成采集，完整经过及失败尝试见 [报告](performance/gc-issue36-round2.md)。 | 2026-09-05 |
| `GC36-ROUND2-SMART-SOFT` | 通过等价检查，未采用生产接线 | 关闭/512/192MiB各1个exclusive冷进程，回收0/1/2次、loss0；降低峰值但增加暂停与耗时，不作为默认阈值排名。 | 2026-09-05 |
| `HEADLESS-INSTANCE-HELPERS` | 通过（Linux替身） | 租约13组、快照隔离4组、失败注入9组；含暖进程剩余预约与token/出生身份变化。无真实游戏语义结论。 | 2026-09-05 |
| `GC36-PARALLEL-A/B` | 通过（两个真实独立进程） | 建局/退出均Passed，请求区间重叠23.47秒；runId `3f8b0b3f6bcc47ca82528f8528ccb486` / `a97addbd1bf34400afe13f33342013bf`。未请求额外root断言，不使用耗时做性能结论。 | 2026-09-05 |
| `GC36-RELEASE-BUILD` | 通过（Linux） | Release 构建显式设置 `CopyModOnBuild=false`，0 警告、0 错误；输出仅复制到本任务的隔离 mods。 | 2026-09-05 |
| `GC36-SILENT-250-GC` | 通过（headless pilot） | DOP1 / 普通 GC / 每 solver 250 节点；selected 展开/转移 250/1426，请求累计 500/2510，265,265,168 B worker 分配；runId `2f721baf127c41aaa0646dc50e46e754`。NodeLimit，未完成整场。 | 2026-09-05 |
| `GC36-NECRO-250-SMART-NOGC4` | 通过（headless pilot） | DOP1 / Smart / NoGC 4 GB / 每 solver 250 节点；请求累计 750/7540 展开/转移，432,608,568 B worker 分配；两次层间回收暂停约 102.4 ms。runId `129de2c8d3ea47649c61c5b6eb865566`。NodeLimit，未完成整场。 | 2026-09-05 |
| `GC36-FINAL-BOUNDARIES` | 通过（headless，最终候选5） | Fork/历史/根快照、取消工作量只记一次，以及 DOP1/DOP2 的路线、评分、展开/转移和非时序剪枝等价。runId `9c4b36665ce240f185e4c722c024ff23`。listener slot 已撤回。 | 2026-09-05 |
| `GC36-AEONGLASS-PREVIEW-OWNERSHIP` | 通过（两步 native 严格差分） | 先只读判型、仅 Wither 写入；第一次生成凋零总伤害6/力量3，第二次升级并生成后总伤害18/力量7。非 Wither preview 身份不变，未执行兄弟的 preview 身份与凋零伤害不变。runId `825d477edaa0456b91934583498388ba`。 | 2026-09-05 |
| `GC36-FINAL-LONG-SILENT-GC0-DOP4` | 通过（三次 A/B，路线/工作量/剪枝等价） | 每 solver 2,500 节点，请求5,000展开/19,065转移。基线→最终中位：分配2.805→1.829 GB（−34.8%）、时间13,672.4→10,484.7 ms（−23.3%）、暂停3,007.0→1,280.3 ms（−57.4%）、VmHWM2.117→1.684 GB（−20.4%）。最终 runId `d45e986c9fe0490f8a1f03afcb0fecdd` / `658c7729a5a54188bc76bca1a0c55f6c` / `04e508c3b433429ca84b31a69d788576`。 | 2026-09-05 |
| `GC36-FINAL-SMART-NECRO-NOGC4-DOP4` | 通过（三次 A/B，有峰值代价） | 每 solver 576 节点，请求1,728展开/22,541转移，路线/工作量/剪枝等价。基线→最终中位：分配1.302→1.296 GB、时间5,113.8→5,074.9 ms、暂停153.0→0 ms；VmHWM1.999→2.824 GB（约+0.825 GB）。最终 runId `356f302ce2fb400e9b67834e3989167a` / `c3810131b74546949076723dd3e6769b` / `4bc38f581c224830a51b1548d385744a`。 | 2026-09-05 |
| `GC36-LISTENER-SLOTS-EAGER-LAZY` | 已拒绝并撤回生产 | helper/游戏 Fork 顺序检查通过，但 lazy 版2,500节点长搜分配增加20.81%，主要反增位于敌方动作中的密集 preview 更新。仅撤回 listener 的候选4控制样本恢复2,801,108,024 B，路线/工作量/剪枝相同。代码归档于 [ExperimentalListenerSlots](../tools/ExperimentalListenerSlots/README.md)。 | 2026-09-05 |
| `GC36-FINAL-PRESSURE-1GB-DOP8` | 通过（合法低预算，单次） | 请求1,728展开/22,541转移，完整路线/工作量/剪枝等价；5,236.773 ms、1,279,327,296 B、暂停160.357 ms、VmHWM1,963,995,136 B。两个 Smart 层因 forecast_exceeds_remaining 回收，forced/start/end/restart/loss=`2/3/2/2/0`。runId `511d89a5ce1c4b378d20fc7bfc90c256`。 | 2026-09-05 |
| `GC36-FINAL-PRESSURE-0.6GB` | 设置校验拒绝，未执行搜索 | 低于1 GB最小设置，runId `a68a6be47d404e7195e45d3fa47af7d8`；不计为搜索失败或性能数据。 | 2026-09-05 |
| `GC36-AFTER-PRUNE-PRESSURE` | 代码审查，未有命中实测 | 保留有下一次 parent 准入时才在剪枝后回收的 guard。admitted_parents 同时受过滤和自然 frontier 影响，不将每个缩小的 wave 都归为内存压力事件。 | 2026-09-05 |
| `GC36-ADAPTIVE-WAVE-EXPERIMENT` | 已撤回生产接线 | 15项合成决策检查通过，真实单轮实验暂无收益依据；控制器和补丁归档于 [ExperimentalAdaptiveGc](../tools/ExperimentalAdaptiveGc)，检查仍可运行。 | 2026-09-05 |
| `GC36-FINAL-ADAPTIVE-SILENT` | 通过等价检查，候选未采用 | 每 solver 2,500节点，55个完整窗口、无probe；请求5,000展开/19,065转移，完整路线/工作量/剪枝等价。耗时11,114.4844 ms，相对最终长搜三次中位增加6.01%；单轮不作稳定退化或提升结论。runId `7ced6eed76f745968f0134743d308296`。 | 2026-09-05 |
| `GC36-FINAL-ADAPTIVE-NECRO` | 通过等价检查，候选未采用 | 每 solver 576节点，35个完整窗口；最后层probeLower=1/rejected=1，4→2核吞吐比0.638、GC duty 0.258→0.146，拒绝后恢复4核且无pending。请求1,728展开/22,541转移，完整路线/工作量/剪枝等价。耗时5,994.7884 ms，相对同代码单轮对照增加1.72%；单轮不作稳定退化或提升结论。runId `20a59a45c3154712981125be355787ae`。 | 2026-09-05 |

最终源码保留 StateStore/空 dirty 查询、历史引用解除、有界 batch storage 复用、原序前缀释放、GC scope 指标、按余量准入、Smart 预测回收及只读判型修复；listener、普通 GC 自适应并发、通用 StateStore COW/typed buckets 和 compact/undo/page COW 内核均不进入生产。VmHWM 为包含启动/建局的进程峰值，GB 使用十进制单位；本轮未完成 Windows、可见 Steam 或完整自动战斗验收。

## 0.30.0：Checkpoint 日志与回放入口重做

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `CHECKPOINT-BATCH-REUSE-0300` | 通过 | 正常/环境错误/正常三请求同 PID 22388，run 内耗时约17.1s/19.9ms/1.7s；后续 Resume 三项全部复用。证据 `.local/replay-validation/batch3/reuse-fixed/`。 | 2026-09-06 |
| `CHECKPOINT-BATCH-DIFFERENCE-0300` | 通过 | 故意修改 ChooseCard 候选状态，在 eventCursor=1 返回 recorded_action_mismatch，difference.json 保存三事件窗口及候选；下包重启到另一 PID 并通过。证据 `.local/replay-validation/batch3/difference-results/`。 | 2026-09-06 |
| `CHECKPOINT-LEGACY-PREFLIGHT-0300` | 通过（材料） | 200 ZIP 和2已解压旧包全部识别开战四材料；约5秒完成批量索引和汇总，没有逐包运行恢复。 | 2026-09-06 |
| `NATIVE-SPOOL-NESTED-0300` | 通过（录制前缀） | 工具箱与低语耳环、同名生存者与杂技建局；导出5623740223b9434abfa60ef462099260，回放d3a52fce86f34cc29aeb22a45bb6c10d。顺序文件及选择候选对账通过，记录3个外部/Hook事件，其余自动出牌由原版重建。 | 2026-09-06 |
| `NATIVE-OPENING-CHOICE-HANDOFF-0300` | 通过 | ed4e8d241b8c433e966f2e8a8e334a8a，开战原生状态一致后释放录制选择器，由求解器选择开局路线并完成 SearchOnly，固定1500ms短搜。此前等待用户启动造成的超时已修为测试器明确提交接管。 | 2026-09-06 |
| `VISIBLE-LOGGING-ROUNDTRIP-0300` | 通过 | 可见Steam六回合导出bf92cb1c0c284f23ab9d1fc58d9c4b4d；同包22事件回放1a633135013a4aa4bc2bdead00d79e75，开战求解器部署3f43a41babd54993a7aaa680c38e4ac5，均实际6HP/0药，严格原生状态与续用状态一致，部署零计划外重算。 | 2026-09-06 |
| `VISIBLE-LOGGING-COLLECTION-0300` | 已测量 | 最终事件累计0.8444ms/最大0.4235ms，峰值积压416B；14次检查点调用累计102.2237ms/最大26.7889ms，峰值积压2，保留6快照，ZIP326162B。未证明相较初次测量显著加速，不作“无卡顿”结论。 | 2026-09-06 |
| `CHECKPOINT-NEGATIVE-CONTRACT-0300` | 通过 | 工具29项断言，覆盖材料缺失、重复、目录穿越、配对错用、诊断包/录制缺项、顺序文件前缀及容量、JSONL断尾恢复、技术失败优先、实际相对人工+3、混合/未完战/不同药水政策拒绝比较。冷启动10秒超时实测正确写timeout并清理进程。 | 2026-09-06 |
| `NATIVE-REPLAY-COMBAT-0300` | 通过 | 原生四回合16事件重放，runId `55b8fd0e302f4151be6878c8c24273a5`，完整续用状态一致；进入/回放阶段约961ms。战后回血与清牌之前的相同结束边界对账。 | 2026-09-06 |
| `NATIVE-REPLAY-SETUP-CHOICE-0300` | 通过 | 工具箱开局生成及选择3个原生事件，runId `f97cf39891864fabb517e95b3def281f`；选择器限定在录制回放作用域，跳过求解器的页面接管，不启动搜索。 | 2026-09-06 |
| `NATIVE-REPLAY-MIXED-0300` | 通过 | 刀刃之舞生成牌及力量药水，runId `e866e6c7c24f4223a5c3b20e856c4740`，10事件，原生二进制及续用状态一致，实际0HP/1药，零重算。 | 2026-09-06 |
| `NATIVE-DEPLOY-FROM-OPENING-0300` | 通过 | runId `e716aea669114d199433daeb1bb8430f`，从原生开战恢复并通过二进制状态对账，求解器T1至T4实际5HP/0药、计划外重算0；记录实际政策及预测指标。 | 2026-09-06 |
| `LEGACY-OPENING-RANK3-0300` | 通过（开战检查点） | 旧实验体ZIP直接选择start，runId `651be9ae9c4a4e998cac5d9f9c28e774`，17.0秒，通过完整续用状态及原生二进制对账。原生加载跑局保留遗物池和存档属性；校验时点与原始开战导出一致。 | 2026-09-06 |
| `ARCHIVE-CONTRACT-0300` | 通过 | `dotnet run --project tools/CheckpointTool/CheckpointTool.csproj -c Release -- self-test`，12 项检查覆盖同名文件分离、稳定默认入口、战后选择、旧包无索引、会话错配、重复及不安全路径。 | 2026-09-06 |
| `ARCHIVE-V2-EXPORT-0300` / `ARCHIVE-V2-IMPORT-0300` | 通过（检查点） | 导出 runId `7ccbe05b91c441d3a7ff5ebea12ee660`；相同 ZIP 直接导入 runId `30f462a75df5456286fddae1144736aa`，`CheckpointContinuationMatched`，材料准备约29ms。没有运行搜索或整场部署。证据 `.local/replay-validation/batch1/`。 | 2026-09-06 |
| `CHECKPOINT-INDEX-0300` | 通过 | 导出 runId `b309d78548cd46708a8dcf008af8bd40` 生成唯一 `combat-solver/checkpoint.json`；索引指向的 metadata、replay-state、native-state、run-state 均存在。再以同一 ZIP 直接导入，runId `15fa0e74fee74af787116be3c2bf2dae` 通过开战根状态断言。 | 2026-09-05 |

> 上述日志批次未逐项实测全部怪物召唤/复活、能力内部引用和所有嵌套选择；缺乏完整记录的旧中途包可能返回具体恢复差异。Linux 脚本完成 Bash 语法及同源边界门禁，未在原生 Linux 游戏进程中运行。本轮不修改世界线策略清单，不以日志测试声明策略提升。

## 下一版本（开发中）：循环、顺序选择与回收边界

当前回手和终局时点定向修复已验证，优先继续搜索质量实验；完整进展以本文顶部最新证据为准。以下按历史阶段记录失败定位过程。v36/v37 临时诊断均已从源码撤除；触发来源 `PR-V37-SOUL-ROUTING-TRACE` 最终标注回放严格消耗堆差分失败、metrics为空，其较早剪枝日志只用于定位，不作为整场质量或性能通过。

| 回手边界 | 证据 | 验证范围 |
| --- | --- | --- |
| `PR-V38-RETURN-ORDER-BASELINE` → `PR-V39-RETURN-ORDER-FORK` | 失败 `0f65140fc6904b699556205a9d6d225d` → 通过 `1d704ce83b7f4208b01a9aed448759c6` | 第二次连续回手此前与当前弃牌堆次序相反；修后通过 `ForkBoundaries`、`CombatRootSnapshot`。独立影子根、资格回调、两次回手、跨牌堆顺序和移除后引用检查，不启动搜索。 |
| `RETURN-TO-HAND-ORDER-V0111` → `PR-V40-RETURN-ORDER-ACTUAL-RETRY` | 中途根失败 `e82c6aa15cbf4ab488411446bf1c0173` → 通过 `7726954cb8e34c69a3269ea938921e7c` | 原版手动出牌与抽牌前 Hook 严格对照连续、逐步 Fork、中途根、已消费历史根。正序、反序、不重新打出不得再次回手及总完成标记均通过，finishedTurn4、metrics为空。独立边界推进，无敌方行动/常规抽牌/搜索/部署；Start-phase 根尚未覆盖。 |
| `PR-V41-RETURN-MEMBERSHIP-BASELINE` | 失败 `6379c0eb127448ed9ee1ff04dc82bda6`，修后待验证 | 同根两张同名同升级牌仅第二张重放次数不同。保持两分支完整有序牌堆和历史不变，仅交换回手资格指向，实际得到不同后续牌堆但战斗指纹相同。包含无资格及同资格 Fork 控制，不冒充原版历史回放。 |

Start-phase 扩展测试在同一专用夹具中，于回合推进后、抽牌前分别捕获瞬时根，覆盖正序、反序和未重打负例，不重复推进新根回合。v42早先三次启动因其他任务游戏被拒绝；完成隔离后已取得v42失败基线，v43修后7项检查全部通过（含三项 `PreDrawStartRoot`，见顶部）。这不证明任意抽牌中途的Start根捕获。

2026-09-05 已同步上游 `0.30.0` / `e49aa18`。下述 v30–v33 为合并前本地实验编号；拉取完成后已建立以下新基线，仍有质量失败，不能沿用旧通过项宣称当前版本通过。三项请求总截止均为120秒，没有为失败延长超时。

| 合并后基线 | 质量结论 | 请求总工作量与成本 |
| --- | --- | --- |
| `PR-POST0300-EXOSKELETONS` | 协议通过、质量失败；零损/97 HP/T10/敌灭，目标零损T≤5。runId `38b24975ee1043c4ac15949156cb46e2` | 13,746展开/208,100转移/147,857选择；29.011秒、分配12,549,318,408 B；GC总/最长969.852毫秒，VmHWM 11,210,724 KiB。 |
| `PR-POST0300-CUSTOM` | 失败；死亡HP0/敌347，未完成部署。runId `45e637d4ed964734b94d482f425dd23a`。首条错误为洗牌4<7，但实质是未找到胜利，不放宽断言掩盖死亡。 | 9,135/34,553/4,610；3.517秒、分配1,365,577,712 B；GC总/最长100.685毫秒，VmHWM 2,276,360 KiB。 |
| `PR-POST0300-SOUL-DOP1-GC` | 失败；损13/85 HP/T9/敌灭，目标损≤1且同损T≤4。runId `b2b6e0fa2f8440e6ba2888967f81efa7`。启动前去掉精确T4，改以获胜和独立战损/回合审计允许真正更早获胜。 | 7,886/59,699/31,255；19.991秒、分配3,359,506,264 B；GC总2,753.657/最长54.098毫秒，VmHWM 1,592,312 KiB。 |
| `PR-V34-EXOSKELETONS` | 实验否决且已撤回；损1/96 HP/T4/敌灭，违反零损目标。runId `64ec100b55ca420d8e512eba93e09ca4`。完整组合身份/家族公平前缀不记为有效修复，新增单元合同尚未运行。 | 4,271/78,678/57,325；12.780秒、分配4,704,011,536 B；GC0，VmHWM 6,223,180 KiB。不能以更快更省内存掩盖战损退化。 |

以上均为独立 headless 进程；总分配不是峰值内存，不作为 Windows 可见游戏的提速结论。初始牌堆及玩家/敌人摘要与旧通过根一致；Exoskeletons 新 Power 导入写入0项，Custom/Soul 的新增赋值与旧严格续用根已有值相同。旧 Exoskeletons ACTION 缺嵌套选择，仍不能称为已在现引擎完整重放旧24步路线。

以下是本分支已执行的定向证据。只复用其后输入和所覆盖行为未改变的结果；最终宽预设和请求级搜索回归仍单列待验证。fixture 中的具体卡牌仅构造测试输入，生产调度依据通用状态与收益。

最新源码为 v30 无主动用药胜利 incumbent 候选，构建0警告/0错误、64项结构门禁及策略/8项最小搜索回归通过。下列 v29 真实样本结果仅作先前源码阶段证据。v30 Exoskeletons 原搜索配置复测协议Passed但零损/T9劣于旧零损/T5，质量FAIL，PR暂停；20秒短诊断与EventPipe仍不作为正常配置性能或同条件A/B成绩。

v32 fresh-option 战术同分实验也未改善零损T9，质量审计失败后仅撤该实验恢复v30。当前没有通过验收的组合启动保路修复；以下诊断或单元合同不充当完整旧路线复现与质量成功。

v33 标准窄预设诊断 runId `adb8c4f778104bb5a0264a4726be8217` 已结束并因损1/96 HP/T4违反零损断言而失败；不同预设不能与原配置比较性能，不作为已恢复质量的证据。

旧来源请求的协议断言不等于最终质量门槛；保持已启动输入不变，再人工核对非死亡、敌人全灭、战损优先及同损不增加回合。Phantasmal 须零损且T≤3，Exoskeletons 须零损且T≤5（旧请求仅限损≤1），Infested 须损≤4且损4时T≤5；长线对照须损≤9且损9时T≤9。Kaiser 按最早T2续用、复用预测零损及零计划外重算的部署边界验收，不由 Passed 推断整场完成。Aeonglass 与长线对照是不同输入根，不混用质量基线。

Smart 验收沿用当前上游政策：按无药基线和药水价值门槛逐层搜索，首个可接受获胜层或设置的战损阈值可以提前结束；记录实际层数和总工作量，不要求固定三层全搜索。最新抽牌修复移除分支历史累计 `100` 次的截断，`100` 仅限同步递归深度并在无法继续时明确失败；新增语义断言已由 v21 Fork 验证，长循环的 v30 定向搜索已通过，长线 GC 对照仍待完成。

v30 SearchPolicy 与下列8项最小搜索夹具在同一 headless 进程复用暖缓存，批次退出码0，监测整程最大 `VmHWM=1,928,528 KiB`。各项耗时/分配仅描述该次搜索，不是独立冷启动；不把共享进程峰值分配给单项，也不作为可见游戏性能结论。结果中的预计终局与完整自动部署分开表述。

| 场景 | 当前证据 | 验证内容 |
| --- | --- | --- |
| `PR-V21-FORK` | 通过，可复用语义证据 | `ForkBoundaries`、`CombatRootSnapshot`；runId `7314b9b4cc164984a706b7d39438f155`。覆盖 DrawLifetime 的超过100次合法抽牌、兄弟 Fork 独立历史及同步递归失败/释放深度，同时覆盖 AfterAttack 和既有待处理选择边界；其后仅有 Search 修改。 |
| `PR-V22-STRICT-KAISER-NESTED` | 通过，严格差分 | 嵌套自动出牌比较完整预测/实际状态，包括有序牌堆和 RNG；runId `1fff3b29c5854d8ca5a8779ab08a4a49`，完成检查 `FuzzyWurmCrawler:INHALE`。输入见 `mayhem-decisions-metamorphosis-nested-order-v0111.json`。 |
| `GENERIC-LOOP-STAGNANT-V30-DOP1/2` | v30 通过 | runId `3cfd5703aa0d49bcbf0cc81b27d053de` / `f0510219758f483fa0addbdcdb496e4b`：均 `10/20/0` 展开/转移/选择，有界探测后停止，最终0动作、不采用空转；搜索 `5.686/4.160 ms`、分配 `1,236,200/1,023,120 B`。两者 NoGC 配置不同，不作纯 DOP 性能比较。 |
| `GENERIC-LOOP-RAMPAGE-GROWTH-V30` | v30 通过 | runId `5d177f8527a4418a8d5577eb244fcf3d`：`64/130/2`、32动作/15洗牌，预计T1零损/80 HP/敌人全灭，`boundary=None`；`17.113 ms`、分配 `7,000,504 B`。正常抽牌没有历史累计次数上限。 |
| `GENERIC-LOOP-LONG-HIDDEN-PHASE` | v30 通过 | runId `30cc729ff61944ba907636d04b8a0820`：`1,200/2,400/0`，1,200动作及洗牌，预计T1零损/80 HP/敌人全灭，`boundary=None`；`0.612 s`、分配 `192,420,096 B`。 |
| `GENERIC-LOOP-LONG-GROWING-DAMAGE-V0111` | v30 通过 | 50万HP、超过256周期，runId `5d70d82fbc8a4198b0fdf1f18120921e`：`1,784/3,570/2`、892动作/445洗牌，预计T1零损/80 HP/敌人全灭，`boundary=None`；`0.766 s`、分配 `226,789,904 B`。动作/形状/伤害相位重复但伤害值可变，逐步刷新耐久低点，没有外推伤害。 |
| `GENERIC-CROSS-TURN-POSITIVE/CONTROL-V30` | v30 通过 | 正例 runId `87b23d759fdc46238e807b2722340b75`：`513/770/0`、预计T17零损/999 HP/敌人全灭，`74.548 ms`、分配 `36,748,288 B`。停滞对照 runId `9231490c20c14630a2885d2c0fa32160`：`78/117/0`、最终0动作且不采用纯防御空转，敌人仍57 HP，不是获胜；`15.009 ms`、分配 `6,142,760 B`。 |
| `GENERIC-LOOP-ZERO-LOSS-QUALITY-V30` | v30 通过 | runId `12fc55312bfd47738ae11cf4f0a02eaf`：`20/57/0`、5动作/4洗牌，预计T1零损/80 HP/敌人全灭；不选不必要的卖血动作。`8.076 ms`、分配 `3,020,680 B`。 |
| `SEARCH-POLICY-V30` | v30 通过 | runId `f79be2a0a3764d44ab869e2915c407a8`，完成 `SearchPolicySnapshot`：新无主动用药incumbent资格、旧exact药层门槛及严格下界断言已执行；保留普通截线、共享服务、付费/弱引用隔离、实际NoGC回退、生命周期与固定250节点DOP等价检查。不是实测弱引用GC节约。 |
| `PERSISTENT-CHOICE-ORDER-LETHAL-V29` | v29 DOP1/2 通过，完整路线等价 | runId `91f47065d7ef4016956c66193be37f53` / `3dbdca220d084e20ac73eca942d38fc1`：均 `4,439/17,886/4,190`、39动作/9洗牌、零损/6 HP/T1/敌人全灭、无边界；39条完整动作与80项非计时/非内存/非调度结果相同，含45项工作/剪枝计数。DOP1/2 分别 `4.966/4.233 s`、分配 `713,359,480/727,272,152 B`、GC累计 `522.803/0 ms`（DOP1最长 `13.574 ms`）、进程 `VmHWM=1,561,112/2,152,880 KiB`。NoGC 为关闭/4 GB，不能归因纯多核收益。 |
| `CUSTOM-231301-NORMALIZED-V29` | v29 搜索与自动部署通过 | runId `59472b049645446e8fdd272022b0b6b3`，VeryHigh/DOP8/NoGC16：19动作/7洗牌/1瓶 `FIRE_POTION`，战损3/1 HP/T1/敌人全灭；完整自动部署、零计划外重算、Instant 设置恢复。选中层 `1,820/7,200/901`、总计 `9,385/34,969/4,628`；`3.761 s`、分配 `1,362,432,464 B`、GC累计/最长 `100.747/100.747 ms`、`VmHWM=2,319,024 KiB`。归一化原版根不等于完整第三方 Mod 兼容。 |
| `SOUL-NEXUS-V29-DOP1-GC` | v29 质量回归通过 | runId `498a4092aebe4812b7ae9790e19377a6`：战损1/97 HP/T4/敌人全灭、1次洗牌，达到旧 v9 的战损/回合质量。`7,795/55,393/27,266` 展开/转移/选择、`20.071 s`、分配 `3,228,250,528 B`、GC累计/最长 `2,537.668/14.543 ms`、`VmHWM=1,673,108 KiB`。与上行 Custom 属于同一 v29 普通截线轮转方案，不拼接不同候选。 |
| `SOUL-NEXUS-V29-DOP2-NOGC4` | v29 质量回归通过 | runId `7320b172df194b90889bb986d285e5ae`：战损1/97 HP/T4；`8,035/56,372/27,464`、`13.455 s`、分配 `3,307,080,992 B`、GC累计/最长 `385.912/385.912 ms`、`VmHWM=3,856,408 KiB`。DOP与GC设置同时改变且工作量不同，不将与DOP1的差值解释为纯并行收益。 |
| `TIE-RANK-V28-AB` | 被否决的失败对照 | 直接撤第三排序键：Soul runId `0e4d810a675c42ad9ba2ce0ae516e487` 获胜，但 Custom runId `37cbc9160ee740b1bbedfd97d454cb78` 为 `OnlyDeath=true`、HP0/敌人372。该实验只用于根因定位，不属于当前方案或最终通过证据。 |
| `PHANTASMAL-V29` | 质量断言失败；Smart政策冲突与性能增长分开记录 | runId `3a8ba38edb9e4a59b0b203f32c0954e0`：旧v9无药死亡/损10后搜索1药层，节省10≥门槛9，得零损/10 HP/T3；v29无药胜利损5/5 HP/T8，Smart按 `5/9=0` 跳过药水层，旧胜利本轮未生成，不认定为已知剪枝丢解。无药工作量旧 `5,693/62,011/42,575`、`7.490 s`/约3.073 GB分配；本轮 `45,590/692,219/441,528`、`85.106 s`、分配 `55,959,575,112 B`、GC累计/最长 `6,781.069/1,547.848 ms`、`VmHWM=11,682,412 KiB`。原质量失败保留，模式选择待用户决定，不删更好无药解绕过门槛。 |
| `EXOSKELETONS-V29` | 120秒超时失败 | runId `c272a6a285de40b3a9dd67d5a9a4eae6`，进程 `VmHWM=12,654,004 KiB`；没有可用最终搜索指标，不填写推测的节点、战损或路线质量。补测批次已退出，广泛矩阵暂停。 |
| `EXOSKELETONS-V30` | 协议Passed，人工质量FAIL | 原搜索配置、不采样、请求截止120秒，runId `26c4826d1973456ca8a6d7087e8732a0`，退出码0；零损/97 HP/T9/敌人全灭，劣于旧v9零损/T5。整条计划42动作/3洗牌/0药，首回合0动作；`12,509/136,516/84,648`、`19.067 s`、分配 `7,859,213,448 B`、GC0、`VmHWM=9,242,068 KiB`、`boundary=None`。不能因协议通过、零损或无边界就宣称质量通过。 |
| `EXOSKELETONS-V31-RETENTION-DIAG` | 诊断证据，不是修复通过 | runId `22bc13a42f5147aaa1b79748d7349bf8`，工作量和零损T9结果同v30。pass9/展开555/去重池3,109，相关启动候选排名2,002、非必保、后续保留均false；同父40分支仅保留2个。日志pass13/展开1,154截断，pass1–4和6–12完整；旧日志缺NestedChoices，不能声称精确复现旧路线。 |
| `EXOSKELETONS-V32-FRESH-OPTION` | 实验否决，质量审计退出1 | runId `c0dbc1b82f1d4b909b234ee73e6a302d`：协议Passed，仍零损/97 HP/T9/敌人全灭，劣于旧零损/T5；`11,930/163,767/114,124`、`21.023 s`、分配 `9,134,969,760 B`、GC0、`VmHWM=10,515,728 KiB`。同父最高Beam同分战术代表未改善质量，不保留为有效修复。 |
| `EXOSKELETONS-V30-BOUNDED-PROFILE` | 短预算诊断，零损质量失败 | runId `3f39e454273f466f89b4fdd7e994822e`：损1/96 HP/T4、`TimeLimit`，`8,373/129,782/92,009`、`20.874 s`、分配 `7,337,646,088 B`。实际 `completed_turns=7`，选中T4不是探索深度；20秒deep预算改变配额并触发T2/T3强制结束候选，不与旧300秒配置作同条件A/B。EventPipe为67,011样本/1,015类型、加权 `7,299,859,352 B`，EventsLost0且未观测GC开始事件，非存活/峰值内存；线程Wait样本不是CPU利用率。该次峰值缺失，不用working set补位。 |
| `KAISER / INFESTED` | v29未运行 | 位于 Exoskeletons 超时之后，尚无本轮结果。后续须按上述质量门槛人工复核，Kaiser 单列T2续用与零计划外重算，不以较弱协议 Passed 或固定旧节点数代替质量。 |
| `AEONGLASS / LONGLINE-NOGC4/OFF` | v29未运行 | 位于 Exoskeletons 超时之后，须在后续验证实际获胜并比较 GC/NoGC 峰值、检查点和同根质量。旧 Aeonglass runId `030812eb278643389d002f23eef0f256` 虽协议为 Passed，实际只有死亡路线，不算获胜证据；headless 结果不作为可见 Steam 卡顿结论。 |

## 0.29.1（历史）：2026-09-04 19 点后问题包硬逻辑修复

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ISSUE-20260904-AFTER-1900-TRIAGE` | 已整理 | 5 批下载包共 173 条记录，单列计算失败、回合准备选牌、部署中止、计划外重算，并重点标出 26 条带玩家备注的原始证据。 | 2026-09-04 |
| `TURN-START-ORDER-0291` | 通过 | Power/遗物回合准备顺序、能量重置顺序和 Sly 嵌套选择保持原版时序；`FIX-141700-TURN-START-CHOICE-FIXED` runId `b366e89a3e1b4f6eb11744430aa66348`，Toasty 回归 runId `4364f489957f49bca864d69ffd708201`。 | 2026-09-04 |
| `KNOWLEDGE-DEMON-NATIVE-CHOICE-0291` | 通过 | 结束回合后原生知识恶魔选牌仍由部署会话驱动，观测 `MIND_ROT_POWER`；runId `8ed16a06900c48ddb048fa89d2fe6fe6`。该夹具最终只有死亡路线，不作为整战质量证据。 | 2026-09-04 |
| `CARD-DERIVED-STATE-IDENTITY-0291` | 已修复，待专用复跑 | 动作身份键与状态指纹排除派生 `CalculatedVar`，针对 `NO_ESCAPE`、`UNLEASH`、`COMET` 的问题包空手牌异常已完成根因修复。 | 2026-09-04 |
| `SPITE-REPEAT-DYNAMIC-VAR-0291` | 已修复，待专用复跑 | `Spite` 缺失 `Repeat` 时按原版固定公式和升级等级恢复重复攻击次数，针对 `Repeat` KeyNotFound 问题包完成根因修复。 | 2026-09-04 |
| `ROOT-HOOK-NULL-0291` | 已修复，待专用复跑 | 根监听器快照过滤空项，针对 Queen `BeforeAttack` 钩子中的 NullReferenceException 完成根因修复。 | 2026-09-04 |
| `POTION-SLOT-DRIFT-0291` | 已修复，待专用漂移夹具 | 部署前药水槽位为空或内容不符时转入 `DeploymentDrift` 重算；未使用宽泛异常吞掉真实执行错误。 | 2026-09-04 |

## 0.29.5（本地扩展）：主进程 Mod 版本钉住

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PRECOMBAT-MOD-PIN-015` | 通过 | 十 Mod 组合加载 Combat Solver `0.29.5`、Seed Oracle `0.1.18`、HowlFromBeyondBgm `1.1.5` 等主进程版本；工坊式原子替换后，启动期硬链接快照仍保留旧内容。两次确定预测和一次假设样本复用同一 worker，计数 `starts=1 / reuses=3`；战损 `5`、内存可见、隔离音频静音、2/10/30 分钟与一直维持、自动关闭及 live 状态不变均通过。runId `8382cbd40ce74a44ac2cac7e444f6403`。 | 2026-09-05 |

## 0.29.4（本地扩展）：作者 0.29.1 基线与可配置 worker 期限

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PRECOMBAT-API-V5-UPSTREAM-0291-013` | 通过 | 作者 `v0.29.1`、RitsuLib、Random Foreseer、Combat Solver `0.29.4` 与 Seed Oracle `0.1.16` 组合加载。停止状态接受 2/10 分钟和一直维持；预热后显示 PID/内存/静音并切换为 30 分钟；两次确定预测战损均为 `5`，一个假设样本复用同一 worker，计数 `starts=1 / reuses=3`，样本结束自动关闭，live 状态令牌不变。runId `e2236a4ec85041dc95c0b3417ed0b688`。 | 2026-09-04 |
| `SEEDORACLE-PRECOMBAT-V5-LOCALIZATION-014` | 通过 | Seed Oracle UI 自检验证 5 个 worker 策略、内存/关闭/预热控件、11 个样本次数、6 类对象、完整模拟风险说明，以及当前幕所有遭遇标题均由游戏本地化解析，不再显示 `LocString … .title`。runId `e15b744f36e84d85aa8771cf02328121`。 | 2026-09-04 |

## 0.29.3（本地扩展）：worker 资源控制与假设战斗样本

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PRECOMBAT-API-V4-WORKER-SIMULATION-010` | 通过 | RitsuLib、Combat Solver `0.29.3`、Random Foreseer 与 Seed Oracle `0.1.15` 四 Mod 同时加载。显式关闭后状态为停止；预热返回 PID、工作集、私有内存与静音标记；两次相同确定预测战损均为 `5`。同一 worker 计数 `starts=1 / reuses=3`；样本种子 `104372539623684` 在内层恢复校验后、开战前应用并回传匹配标记；样本结束自动关闭，主进程 live 状态令牌不变。runId `61f19e8db7ae4f9a8a217424e602f075`。 | 2026-09-04 |

## 0.29.2（本地扩展）：可复用静音战前 worker

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PRECOMBAT-API-MANUAL-CACHE-REUSE-009` | 通过 | RitsuLib、Combat Solver `0.29.2`、Random Foreseer 与 Seed Oracle `0.1.14` 四 Mod 同时加载；同一快照两次强制预测返回相同战损 `5`，使用同一 worker PID，计数为 `starts=1 / reuses=1`。隔离设置四类音量均为 `0`，live 状态令牌不变；首请求 game startup `12.57 s`，第二次 `0.6 ms`。runId `08fc0ae91d39417b9c234bbc5d2da6ea`。 | 2026-09-04 |

## 0.29.1（本地扩展）：基于 0.29.0 的战前预测 API 与硬逻辑修复

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ISSUE-20260903-MANGLE-COW-FIX` | 通过 | MANGLE + SLITHER 问题包在 `VerifyIncrementalSearch` 下完整回放通过；增量/完整回放的费用 RNG、牌面状态和搜索结果一致。runId `f25dee83d9ba4b039318a4cdef20fbe3`。 | 2026-09-04 |
| `ISSUE-20260903-MANGLE-CARD-DIFFERENTIAL-FIX` | 通过 | MANGLE 单卡严格差分，验证抽牌后带 SLITHER 的攻击牌仍可正确打出并产生预期效果。runId `b7e5d32dff794ef695c93dbc3123b689`。 | 2026-09-04 |
| `ISSUE-20260903-QUEEN-HP-MISMATCH-CURRENT` | 通过 | 复用 `0.28.2` Queen 遗物标注问题包的精确根和 RNG；当前源码短搜完成最终路线物化，没有再次出现 HP 标注回放差异。runId `22f3789304284eb49662d378361940e2`。 | 2026-09-04 |
| `ISSUE-20260903-QUEEN-HAZE-MISMATCH-CURRENT` | 通过 | 复用 `0.28.2` Queen/Haze 问题包的精确根和 RNG；当前源码完成短搜并返回候选，没有再次出现卡牌状态标注差异。runId `5bfc15213c444b449161c1b216ab256d`。 | 2026-09-04 |
| `ISSUE-20260903-SPITE-REPEAT-CURRENT` | 通过 | 当前原版卡牌严格差分覆盖失血后 Spite 的重复攻击语义，13 个动作检查全部通过。runId `cd97d0160dec44d3b35cae9b05ca328f`。 | 2026-09-04 |
| `ISSUE-20260903-DEPLOYMENT-DRIFT-RECOVERY` | 已修复，待实时漂移夹具 | 部署时普通计划手牌缺失现在归类为 `DeploymentDrift` 并重新捕获当前根；保留真实执行失败的显式错误。现有 Fork/部署身份边界通过，尚缺在可见游戏中先改动手牌再部署的专用夹具。 | 2026-09-04 |
| `ISSUE-20260903-OBSCURA-SPAWN-CURRENT` | 通过 | 原生 `THE_OBSCURA_NORMAL` 生成路径短搜覆盖 8 回合和 3 次洗牌；生成新怪物前固定敌人列表快照，没有再次出现 `Collection was modified`。runId `faeec097dee647af8453f9aa00f2c6ab`。 | 2026-09-04 |
| `ISSUE-20260903-KNOWLEDGE-CURSOR-FIX` | 代码修复，场景未通过 | 结束回合部署不再把 `ApplyKnowledgeCurse` 放入原生选牌游标；当前默认知识恶魔建局只有死亡路线，等待战斗结束超时，因此不记录为行为通过。已有 `PR29-KNOWLEDGE-CURSOR` 结构回归覆盖相同过滤边界。 | 2026-09-04 |
| `ISSUE-20260903-NATIVE-CHOICE-DRIFT-RECOVERY` | 已修复，待可见漂移夹具 | 原生选牌候选/页面生命周期不一致现在关闭当前页面并请求 `DeploymentDrift` 重捕获；确认按钮等待布局完成后再提交。尚未有专用可见页面先漂移再重捕获的 unattended 证据。 | 2026-09-04 |
| `ISSUE-20260903-NATIVE-CHOICE-PLAN-SEQUENCE` | 已修复，部分通过 | 原生选牌驱动器在收到计划外请求、计划提前结束或页面要求数量变化时报告选择计划漂移，并由部署层关闭页面后请求 `DeploymentDrift` 重捕获；重复计划仍显式失败。`SCULPTING-STRIKE-CHOICE-151` 严格增量回放和第 2 回合复用通过，runId `a35708eb3bae4aa49ef7769230d59bfa`。 | 2026-09-04 |
| `ISSUE-20260903-DEPLOYMENT-TURN-DRIFT` | 已修复，待专用时序夹具 | 部署动作检测到玩家回合已结束时现在清理旧路线并按 `DeploymentDrift` 重捕获，不再记为自动执行失败；其他部署异常仍显式失败。 | 2026-09-04 |
| `ISSUE-20260903-PENDING-CHOICE-HOOK-BOUNDARY` | 已修复，待双监听器夹具 | 洗牌 Hook 在已有待处理选择时停止继续调用监听器，分支消费后再继续；避免同一模拟事件创建冲突选择。 | 2026-09-04 |
| `ISSUE-20260903-NATIVE-CHOICE-SURFACE-TIMEOUT` | 已修复，待页面消失夹具 | 原生选牌页面或确认按钮等待超时现在按页面漂移关闭并请求 `DeploymentDrift` 重捕获；非原生等待超时仍走原有失败路径。 | 2026-09-04 |
| `PRECOMBAT-API-FINAL-004` | 通过 | 外层真实 headless 跑局调用 public API v1；内层独立进程精确恢复完整规范化存档后进入毛绒伏地虫战斗并返回战损 `5`、药水 `0`、边界 `None`，外层调用前后完整状态令牌一致。runId `7da852343994495e923dec58bf624b28`。 | 2026-09-04 |
| `PRECOMBAT-API-SEED-STACK-005` | 通过 | RitsuLib、Combat Solver、Random Foreseer 与 Seed Oracle 共四个 Mod 同时加载；Seed Oracle 探测到 API v1/隔离 worker，worker 对精确相同 Mod 集合完成直接恢复和战前搜索，无递归请求，状态令牌不变。runId `1961ef8c7d484da691e07cec99074215`。 | 2026-09-04 |
| `PRECOMBAT-POTION-METRICS-006` | 通过 | 强制至少使用一瓶药水的短搜索把非空动作写入公共结果协议：`FIRE_POTION` / “火焰药水”、第 `3` 回合、槽位 `0`；战斗第 `3` 回合结束。runId `5e281451a3534051b605577cd51c9038`。 | 2026-09-04 |
| `PRECOMBAT-REMOTE-CAMPFIRE-007` | 通过 | 从含历史事件选择的完整跑局精确恢复；按目标路线补记第 9 层篝火与第 10 层宝箱，用目标列坐标进入第 11 层 `SLIMES_NORMAL`，在开战 Hook 前覆盖为休息后的 `66 HP`。3 秒 DOP1 短搜返回战损 `12`、最终 `54 HP`；完成项包含 `DirectRunSnapshot:ExactStateRestored`、`PreCombatInterveningMapPoints:2`、`PreCombatPlayerHp:66`。runId `fe93b060ada64e78864fca8d825aeb85`。 | 2026-09-04 |
| `PRECOMBAT-API-MANUAL-V2-008` | 通过 | public API v2 双进程往返使用目标坐标、独占可取消 worker、强制重算和入战 HP 覆盖；空事件历史变量被规范化、非空变量保留，返回 `EntryHp=79`、战损 `1`、药水 `0`、边界 `None`，调用前后 live 状态令牌一致。runId `f8db7c03ea9444cc89483495c985f221`。 | 2026-09-04 |
| `PRECOMBAT-API-SEED-STACK-0.29.1-FINAL` | 通过 | API v2 改动重放到作者 `0.29.0` 后的四 Mod 组合回归；Combat Solver 以 `0.29.1.0` 加载，外层 Seed Oracle 记录 `precombat_public_api_v2=true`，公开 API 返回 `EntryHp=79`、战损 `5`、药水 `0`、边界 `None`，调用前后 live 状态令牌一致。runId `2e038fa1be6b45a19a7a5096c9be0f16`。 | 2026-09-04 |

性能指标口径：`selected_*` 只描述最终选中的单个 solver；请求级 `total_expanded_nodes / total_transitions / total_choice_branches`、`total_solver_ms`、分配与 GC 累计对正常、失败和取消的每个 solver 工作区间精确记录一次，包括取消前已发生的部分工作。Smart 有限药水层之间由 coordinator 主动执行的内存整理也计入时间、分配与 GC，但不增加 solver 数；建立开局、层间比较等其他编排工作仍不在这些总值中。因此端到端耗时以请求/阶段外层墙钟为准，峰值内存以进程 `VmHWM` 为准。Smart 多层的取消时点可能令请求总工作量小幅波动，语义验收优先比较胜负、战损、回合和动作路线。峰值工作集是瞬时进程峰值，不能跨阶段相加；`16 GB` NoGC 是运行时请求预算，不等于实际占用或硬上限；NoGC 活跃时 `GC.GetTotalMemory(false)` 不是严格 live-set 测量。

## 下一版本（开发中）：战后回血遗物计入战损

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POST-COMBAT-RELIC-HEAL-ASSERTIONS` | 通过（无人测试静态断言） | 无条件回血按剩余生命上限裁剪：`6` 点回血在 `70/80` 得 `6`、`78/80` 得 `2`、`80/80` 得 `0`。带阈值的一件按原版截断判定：`75` 点最大生命下停在 `37` 得 `18`、停在 `38` 只得 `6`，`70/75` 受上限裁剪得 `5`。排序口径 `MonotoneHealFor` 在同一输入下只给 `6`，阈值部分不进排序。未获胜或阵亡的路线一律得 `0`。 | 2026-09-04 |
| `POST-COMBAT-RELIC-HEAL-VANILLA-GROUND-TRUTH` | 通过（反编译核对） | 全量反编译 `sts2.dll` 后交叉筛选，胜利后回血的遗物恰好是燃烧之血、黑暗之血、带骨肉三件；其余用 `AfterCombatVictory` 的遗物不回血，其余会回血的遗物挂在进房间、回合开始等别的时点。带骨肉走 `AfterCombatVictoryEarly`，先于两件血遗物结算，阈值判定读的是未回血前的终局生命。 | 2026-09-04 |
| `POST-COMBAT-RELIC-HEAL-LIVE-RUN` | 未验证 | 实机整局观察尚未进行，headless fixture 仍因缺少非 Steam `default` 存档配置无法建立。 | 2026-09-04 |

> 带骨肉只显示不排序是本批的已知取舍，不是遗漏。要让它进排序，需要先把 `MultiObjectiveDominates` 和 `TranspositionLabel.Dominates` 改成比较战后终局生命而不是原始生命与原始掉血，并为阈值附近的支配关系补专门夹具。

## 下一版本（开发中）：路线回血计入战损

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `RECOVERED-HP-STRATEGIC-VALUE-ASSERTIONS` | 通过（无人测试静态断言） | `PersistentValueOfRecoveredHp` 在普通战斗、第一、二幕末 Boss 和最终 Boss 分别为 `10/2/0`；`StrategicHpDeficit(12,3,10,None)=5`、`(0,0,9,None)=-9`、`(0,0,9,RunEnding)=0`；可回血时 `0` 不再被当成已证明的最优下界。 | 2026-09-04 |
| `RECOVERED-HP-IRONCLAD-NOT-YET-LIVE-RUN` | 通过（实机单局观察，未建 headless fixture） | 战士带「时候未到」整局 23 次打出，回血量 `0/2/3/5/7/9`，均不超过牌面 `10`。多条选中路线在挨了伤害后回满：`战损 9 / 回血 9 / 结束 87`（T1 与 T3）、`战损 7 / 回血 7 / 结束 87`、`战损 3 / 回血 9 / 结束 87`。`战损 7 / 回血 2 / 结束 82` 证明溢出裁剪按回血发生时的空间结算，不按回合结束时的空间。整局决策未见退化。 | 2026-09-04 |

> headless 启动器需要一个非 Steam 的 `default` 存档配置来初始化隔离数据目录。当前验证机器只通过 Steam 启动过游戏，没有该配置，因此本批改动没有对应的 unattended fixture，行为证据来自实机单局观察。

## 0.28.3（已发布）：战损停止与路线信息

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ACCEPTABLE-BATTLE-HP-LOSS-THRESHOLD` | 通过（headless 控制器/UI/搜索生命周期，DOP1） | 设置页阈值 JSON 往返、默认 `0`、完整胜利且预计本局战损 `<=` 阈值才触发早停的边界断言通过；独立短搜在允许范围上限下返回满足阈值的完整胜利路线。runId `aca83616becd422e99558a0d07b970d0`。 | 2026-09-03 |
| `KILL-SOURCE-ANNOTATION` | 待实机确认 | 最终路线回放记录卡牌、药水、毒、荆棘、能力、遗物和球等击杀来源；直接移除仍标记为未知效果，召唤/重建敌人可保留目标名称。Release 构建和 headless 生命周期通过。 | 2026-09-03 |
| `GREMLIN-MERC-PRESERVE-RESOURCE-TARGET` | 待实机确认 | 保钱策略在胖地精携带被盗资源且暂无攻击威胁时保留追回资源的动作分支，避免资源携带者逃跑。Release 构建和 headless 生命周期通过。 | 2026-09-03 |

## 0.28.2（已发布）：搜索热路径与 NoGC 回退

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR37-HOT-PATH-ALLOCATION-A/B` | 贡献者 A/B 通过 | 固定机甲根在 Windows 与 macOS 上保持工作量、路线、评分和战损不变；Windows worker 分配 `7.40 GB → 6.20 GB`，macOS 分配 `10.40 GB → 9.44 GB`，两端搜索时间均下降。 | 2026-09-03 |
| `PR38-NOGC-FALLBACK-PARALLELISM-A/B` | 贡献者 A/B 通过 | macOS 不支持 NoGC 时保持固定工作量与结果，实际并发 `2 → 8`、耗时 `37.2 s → 27.2 s`；Windows 正常 NoGC 路径无可测差异。合并态策略断言覆盖系统余量回退保守并发与普通平台/尺寸回退完整并发。 | 2026-09-03 |

## 0.28.1（已发布）：Smart 药水门槛与手动深度释放系统内存

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `MANUAL-SYSTEM-MEMORY-RELEASE` | 待实机确认 | 主界面内存条右侧提供固定入口；等待搜索退出后压缩托管堆并修剪游戏进程工作集，UAC 辅助程序清空系统工作集与待机列表，不清空修改页列表。 | 2026-09-03 |
| `GENERIC-SMART-POTION-SAME-LOSS-CONSERVE-V0111` | 通过 | 无药零损获胜时，付费药即使更早结束也不绕过每瓶 `9 HP` 门槛；结果为 `24/52` 展开/转移、`0` 药、`0` 战损、T3，且未打出卖血牌。runId `c91f29fcaa9a40129e6f67adfe06a8b9`。 | 2026-09-03 |

## 0.28.0（已发布）：通用周期与跨回合收益

> 这里记录基于上游 `0.27.2` 的最终定向与性能证据。生产算法只使用控制形状、精确动作相位、分支相对 stand-pat 状态和通用收益向量；fixture 中的卡牌、药水或遗物名称只是输入，不是生产特判。周期识别窗口最多 `32` 个动作；跨回合基础观察期为 `max(16, 两个完整牌堆周期所需回合)`，语义变化探针最多 `64` 次回合转移，最近一回合确有通用改善的探针最多 `128` 次。命中节点上限的场景只证明预算内找到路线，不称穷尽或数学全局最优。

| 场景 | 当前结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `AFTER-STARS-GAINED-ENGINE-MIRROR-0111` | 通过 | 通用 `GainStars` 在状态变更后分发 `AfterStarsGained`；5 层黑洞配合发光严格对比原版与预测完整状态，并显式断言获得 `1` 星、敌人生命变化 `-5`。最终 runId `b4c281152e5b4468b59f10c665d68d`。 | 2026-09-03 |
| `GENERIC-LOOP-LETTER-OPENER-HIDDEN-PHASE-DOP1/2` | 通过 | 两次表面回到同一牌堆形状后，隐藏相位在后续重复中兑现；DOP1/DOP2 都为 `6/12` 展开/转移、`6` 次洗牌、T1。runId `1ae74fcaf14648a19a773e9be602fa34` / `28d3a383cb3c4fd4944ffcc923f76e7a`。 | 2026-09-03 |
| `GENERIC-CROSS-TURN-HIDDEN-BUFFER-DOP1/2` | 通过 | 表面进展长期停滞但精确状态仍跨回合推进；DOP1/DOP2 都为 `513/770` 展开/转移、`16` 次洗牌、T17；DOP2 最大并发为 `2`。runId `e1aac2f411fa4d58b91466022f5edde7` / `d38be6387ddd4aadb13a1adcdc3864cf`。 | 2026-09-03 |
| `GENERIC-CROSS-TURN-STAGNANT-CONTROL` | 通过（上游合并后定向证据） | 无伤害手段的停滞场在 `78/117` 展开/转移后有界停止，最终路线不采用纯防御空转；与隐藏缓冲场共同约束“不能早停、也不能无限续期”。runId `fbba8ab4c2724a3d8fa2585b448dd713`。 | 2026-09-02 |
| `GENERIC-CROSS-TURN-PURITY-PILLAGE` | 通过（定向证据） | 先净化牌库、下一回合兑现，`123/340` 展开/转移、T2。runId `c4e37ee3249d4a819c27a99e0d15fb8a`。 | 2026-09-02 |
| `GENERIC-LOOP-RAMPAGE-DYNAMIC-GROWTH-DOP1/2` | 通过 | 动态成长值不写入循环特判；DOP1/DOP2 都为 `2400/5354/344` 展开/转移/选牌、`32` 动作、T1，动作和全部非时序工作量一致；DOP2 搜索与动作重放最大并发均为 `2`。runId `2d33c3a4eaee44d5b06004758e8cefb4` / `5100ca05213f455ba6f1dd57f0916fef`。 | 2026-09-03 |
| `GENERIC-SMART-POTION-SAME-LOSS-FASTER` | 通过 | 同为零战损时，Smart 选择 T1 的一药路线；请求累计 `31/74`、选中层 `7/22` 展开/转移。runId `02c81917a3284c269a90e2fec5b65d4e`。 | 2026-09-03 |
| `GENERIC-SMART-POTION-THREE-LAYER-PROGRESS-REBASE` | 通过 | 完成无药、恰好一药、恰好两药三层，两次层间整理后仍选择零战损 T1 的一药路线；请求累计 `52/140`、选中层 `7/19`，Gen0/1/2 均 `4` 次。runId `8e01068bacc049b89acacd66e218f73f`。 | 2026-09-03 |
| `GENERIC-SEARCH-POLICY-BRANCHING-REBASE` | 通过 | 控制器生命周期、三层聚合、NoGC 生命周期、DOP 等价、快照释放及同父节点唯一循环租约/转置边界断言通过。runId `328dbe4322a54815a83f3946563d73b6`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-PILLAGE-SINGLE` | 通过 | 单张掠夺触发自动转移内连锁；`1/2` 展开/转移、T1、零损、一个显式动作。该机制不是循环规划器证明出的数学无限。runId `aed2647646d645abb18e1ef94bf53283`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-POMMEL-FINITE` | 通过 | 有限链控制场为 `6/7`、两次洗牌、T2、战损 `11`，首动剑柄打击；没有被误判为 T1 无限。runId `eb6084633d3744399a3e3422e13e2e8a`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-BLOODLETTING-QUALITY` | 通过 | `186/464`、四次洗牌、T2、战损 `3`；最终排序选择少卖血的 T2，而非战损 `6` 的 T1。runId `b57b545ed9c7415cb6644af2df7e89cc`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-SILENT-DISCARD` | 通过 | 准备/战术大师抽弃链为 `301/837/261` 展开/转移/选牌、16 个动作、T1、零损。runId `1991d55b153f44c8b722546214e57f0a`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-DEFECT-RETRIEVAL` | 节点上限内找到解 | 万物一心/全息影像取回链在 `2400/6584/1714` 后命中 NodeLimit，找到 29 动作、T1、零损路线；不称全量穷尽。runId `a7a3fda4781e4f329a00b39aecdf079b`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-REGENT-PARTICLE-WALL` | 节点上限内找到解 | 粒子墙/照我说的做链在 `2400/6037/2` 后命中 NodeLimit，找到 38 动作、T1、零损路线，实际/路线最大格挡 `243/1125`；不称全量穷尽。runId `5859c64bb16d4a0c8bf1134f45d23861`。 | 2026-09-03 |
| `GENERIC-CURRENT-RULE-REGENT-SEALED-BLACK-HOLE` | 通过 | 封印王座/黑洞资源链为 `10/20`、10 个动作、90 格挡、T1、零损。runId `3bb92f0684d7414d9d0660f83edb992b`。 | 2026-09-03 |
| `INFESTED-PRISMS-GENERIC-QUALITY-INTERMEDIATE-BASELINE` | 已被最终候选取代 | 历史中间候选为 `80,009/537,025/213,213`、约 `57.10 s`、约 `11.29 GiB`、52 HP/战损 8/T6；它暴露了质量保路导致的工作量膨胀，仅保留作优化过程证据。runId `f234bd8a4c0f4f2a87cb6a27458515e4`。 | 2026-09-02 |
| `INFESTED-PRISMS-GENERIC-QUALITY-FINAL` | 通过（配置搜索完整结束） | 同根 VeryHigh/Smart/DOP8/NoGC 16 GB：累计 `13,516/80,477/33,664`，选中层 `4,437/23,810/8,337`；搜索 `10,548.215 ms`、累计分配 `4,920,306,312 B`、`VmHWM=3,715,840 kB`（约 `3.54 GiB`）。结果 43 HP/战损 17/T5/两药，优于旧 42 HP/战损 18/T7；两次层间 NoGC 回收重建成功，Gen0/1/2 均 `4`，GC 暂停累计/最大 `640.203/404.340 ms`。runId `1e9c735a5c9e42889ab46c6389a66b16`。 | 2026-09-03 |
| `AEONGLASS-LONGLINE-NOGC4` | 通过 | 长线同根累计 `27,905/173,477/74,998`、选中层 `8,687/59,910/26,619`，战损 9/56 HP/T9/两药；`72,151.169 ms`、`VmHWM=4,396,804 kB`（约 `4.19 GiB`），Gen0/1/2 均 `30`，GC 暂停 `4,822.405 ms`。13 个压力检查点和 2 次层间整理全部重建 NoGC，无回退。runId `5f75b1cd2b604f34874be9e6243590db`。 | 2026-09-03 |
| `AEONGLASS-LONGLINE-NOGC-OFF` | 通过（A/B） | 与 NoGC4 节点、路线、战损和回合完全一致；`97,927.457 ms`、`VmHWM=2,745,000 kB`（约 `2.62 GiB`），Gen0/1/2=`3522/1695/88`，GC 暂停 `29,712.935 ms`。关闭 NoGC 省约 `1.57 GiB` 峰值内存，但慢约 `35.7%`（反向口径：NoGC 快约 `26.3%`）。runId `6c187605b7a8451bbd163a800d9f25c4`。 | 2026-09-03 |

### 新增 fixture 清单

| Fixture | 主要边界 |
| --- | --- |
| `coverage/unattended/after-stars-gained-black-hole-glow-0111.json` | 通用星能增加 Hook 分发、黑洞单次伤害与完整状态严格差分 |
| `coverage/unattended/generic-loop-letter-opener-hidden-phase-v0111.json` | 同牌堆形状的隐藏相位收益、DOP 等价 |
| `coverage/unattended/generic-cross-turn-hidden-buffer-positive-v0111.json` | 晚于基础观察期兑现的精确隐藏状态、DOP 等价 |
| `coverage/unattended/generic-cross-turn-stagnant-control-v0111.json` | 真正无收益跨回合路线有界停止 |
| `coverage/unattended/generic-loop-cross-turn-purity-pillage-positive-v0111.json` | 先净化牌库、下一回合兑现的跨回合收益 |
| `coverage/unattended/generic-loop-rampage-dynamic-growth-positive-v0111.json` | 动态成长循环、32 动作路线与 DOP 等价 |
| `coverage/unattended/generic-final-quality-zero-loss-over-faster-blood-sale-v0111.json` | 低战损优先；同战损才比较结束回合 |
| `coverage/unattended/generic-smart-potion-same-loss-faster-v0111.json` | Smart 付费药未省足战略 HP 时保留无药路线 |
| `coverage/unattended/generic-smart-potion-three-layer-progress-v0111.json` | 零损终局不启动无收益的付费药梯度 |
| `coverage/unattended/generic-loop-speedster-discard-draw-positive-v0111.json` | 多动作抽弃循环、洗牌与零损击杀 |
| `coverage/unattended/generic-loop-speedster-startup-positive-v0111.json` | 先建立能力再进入循环 |
| `coverage/unattended/generic-loop-hellraiser-pillage-bloodletting-positive-v0111.json` | 卖血/能量启动后兑现 |
| `coverage/unattended/generic-loop-hellraiser-startup-positive-v0111.json` | 先打能力牌再启动 |
| `coverage/unattended/generic-loop-hellraiser-pillage-defend-breaker-v0111.json` | 循环被非攻击抽牌打断并跨回合求解 |
| `coverage/unattended/generic-loop-pale-blue-dot-threshold-cross-turn-v0111.json` | 阈值状态、药水入口与跨回合/出口收益 |
| `coverage/unattended/generic-loop-regent-star-energy-positive-v0111.json` | 星能与能量循环 |
| `coverage/unattended/generic-loop-regent-black-hole-startup-positive-v0111.json` | 储君能力启动与循环 |
| `coverage/unattended/generic-loop-hellraiser-pillage-single-current-v0111.json` | 当前规则单张掠夺的自动转移内连锁；不是数学无限 |
| `coverage/unattended/generic-loop-hellraiser-pommel-finite-current-v0111.json` | 当前规则有限链负例；不得误判为 T1 无限 |
| `coverage/unattended/generic-loop-bloodletting-double-pommel-quality-v0111.json` | 卖血启动质量排序；低战损优先于少回合 |
| `coverage/unattended/generic-loop-silent-prepared-tactician-current-v0111.json` | 当前规则抽弃重复链 |
| `coverage/unattended/generic-loop-defect-all-for-one-hologram-current-v0111.json` | 当前规则取回重复链；NodeLimit 内有解 |
| `coverage/unattended/generic-loop-regent-particle-wall-make-it-so-current-v0111.json` | 当前规则技能/格挡重复链；NodeLimit 内有解 |
| `coverage/unattended/generic-loop-regent-sealed-throne-black-hole-current-v0111.json` | 当前规则双资源重复链 |

`coverage/unattended/generic-loop-hellraiser-pillage-bloodletting-cards.json` 只是可复用牌堆输入，不是独立场景。未在上表列出 runId 的 fixture 仍须复测，不能因文件存在就宣称当前工作树通过。

## 0.27.2（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `DYNAMIC-ROUTE-HP-LOSS-MONOTONIC` | 待玩家实测（Release 编译） | 未结束战斗的动态路线显示“预计战损 未知”；完整胜利路线才显示数值，并拒绝用更高战损候选覆盖当前展示。逐回合掉血不受影响。按要求不运行 UI 测试。 | 2026-09-02 |

## 0.27.1（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `UI-MEMORY-GC-WALL-IDLE` | 待玩家实测（系统压力修复已部署） | 实机基线在条显示 `20.1%` 时 NoGC 意外退出：本轮分配 `2.15 GB`，但预测系统压力已约 `96%`，随后累计 GC 暂停增至 `19.8 s`。配置预算现作为上限，实际区域按系统安全余量缩小；搜索检查点同时检查分配额度与系统压力。Smart 梯度之间主动清理，最终梯度正常结束后保留战斗级区域，战斗结束再延时清理。本轮按要求不运行 UI 测试，等待玩家实测。 | 2026-09-02 |
| `UI-MEMORY-SYSTEM-PROCESS-SEGMENTS` | 待玩家实测（Release 编译） | 内存条按实时物理内存分为灰色系统占用、彩色游戏进程占用和剩余空间；文字显示“当前内存占用 X GB / 搜索总可用 Y GB”，搜索总可用为 CLR 安全总量减去系统占用。本轮按要求不运行 UI 测试。 | 2026-09-02 |
| `UI-SEARCH-LIMIT-WARNING` | 待玩家实测（此前结构验证通过） | `TimeLimit` 与 `NodeLimit` 结果始终显示不可关闭的顶部警告，以“计算尚未彻底穷尽”解释时间/节点上限；正常结束不显示。此前结构 runId `0482e473ee9b4271ba314c25fa9285a7`；本轮按要求不运行 UI 测试。 | 2026-09-02 |

## 0.27.0（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR31-CONTROLLER-UI-LIFECYCLE` | 通过（headless 控制器/UI/药水生命周期，DOP4） | 自动计算持久化、独立停止/采用/执行控件、候选路线与逐回合对敌伤害、窄药水浮层、三向缩放、内容最小尺寸、折叠恢复及位置/尺寸 JSON 往返均通过。runId `75070ce99c1b48ef9c9608205ac57e19`。 | 2026-09-02 |
| `PR31-INITIAL-TOASTY-CONTROLS` | 通过（headless 烘焙手套开局搜索，DOP4） | 首次回合准备搜索可采用已展示候选，返回第 1 回合 `25` 个动作；采用、执行和后续重算仍由回合准备事务接管。runId `e6cb54b4de4f4110a996c2563c7f96ea`。 | 2026-09-02 |
| `OVERLAY-RESIZE-PERSISTENCE-NEXT` | 通过（headless 真实重排） | 宽/高成对持久化、右/下/右下三向缩放、三斜线抓手、`16 ms` 拖动节流、内容最小尺寸、紧凑收起和展开恢复通过；独立药水浮层不改变主面板持久宽度。可见观感未检查。 | 2026-09-02 |
| `BOSS-HP-STRATEGY-SETTINGS-NEXT` | 通过（headless 设置/UI/搜索策略，DOP4） | 第一、二幕与最终 Boss 两项策略独立 JSON 往返；通关优先分别保留 `45 HP/瓶`、`75 HP` 卖血阈值和最终 Boss 存活边界，最低战损独立恢复 `9 HP/瓶` 与普通 Boss 卖血阈值；两类提示文案和关闭状态互不串联。runId `e52f2ec763ac4361a9a09992ab8ae7d5`。 | 2026-09-02 |
| `PR32-DYNAMIC-PREVIEW-EARLY-FINISH` | 结构验证（Release 编译） | 动态演化预览约每 `100 ms` 更新，当前回合预览与可采用推演路线分离；等价获胜路线在敌方状态之后、总评分之前比较结束回合。零警告；按用户要求未运行行为回归。 | 2026-09-02 |
| `LIVING-FOG-GAS-BOMB-TERMINAL-MOVE` | 通过（headless 最小生命周期） | 毒气弹执行 `EXPLODE_MOVE` 后离开活动阵容；回合收尾保留其 AI 快照但不再解析不存在的后继行动。runId `0450d8534bce46e0b329a22f562d95a5`。 | 2026-09-02 |
| `PR33-DAMAGE-AND-POTION-PREVIEW` | 结构验证（Release 编译） | 逐回合对敌伤害累计实际失血；药水补查只在全局路线真正改善时同步更新预览和采用种子。零警告；按用户要求未追加行为回归。 | 2026-09-02 |

## 0.26.0（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR21-BOSS-HP-RELIEF-0254` | 通过（第一/二幕 Boss、第三幕第二 Boss，DOP4） | 第一、二幕分类为 `ActClearHeal`，普通药按 `45 HP/瓶` 开梯度，Boss 卖血阈值为 `75`；第三幕第二 Boss 分类为 `RunEnding`，血量只保留存活边界。runId `444c43b31b3745ef9d94758b2ed79d96`、`4cb4e5936f9b45a09b1ea4ba3091913d`、`da035809ed9243cc850b97085415b3af`。 | 2026-09-02 |
| `PR30-VOID-FORM-SCARCITY` | 通过（合并态 Fork 边界、DOP4） | 两张同成本牌下，虚空形态一个剩余免费格只计一张的机会价值，两个免费格精确计为两倍；PR #27 的奥斯蒂未来价值同时保留。具体玩家实战选牌未稳定复现。runId `aaaa1a2b47924f8b8774a1b6df0b017c`。 | 2026-09-02 |
| `PR29-KNOWLEDGE-CURSOR` | 通过（Fork 边界、DOP4） | 强制结束回合的出牌回放中，知识恶魔诅咒不进入卡牌选择游标；普通动作选择与普通回合选择仍保留并接受消费校验。runId `bb84ece61ac7453e8befa2bb37220f86`。 | 2026-09-02 |
| `PR27-MERGE-FORK-PARALLEL` | 通过（合并态 Fork 边界、DOP4） | PR #27 的低分配状态、roster、缓存与分支所有权断言通过，同时保留 `0.25.3` 的选牌、复活、自动出牌历史和球死亡召唤断言；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=59`。runId `a1747125352741efa72ec01a2ae64c4a`。 | 2026-09-02 |
| `INFESTED-PRISMS-V0251-FULL-SMART-DOP8-A/B` | 通过（最终低分配候选、最新上游同根完整搜索） | 上游/最终阶段墙钟 `112798.755 → 9846.963 ms`，加速 `11.46×`；结束采样工作集 `11,204,886,528 B`。请求累计 `24109/157893/69006` 展开/转移/选牌分支，选中 solver 为 `5861/31244/12125`；保持 `42 HP`、预计战损 `18`、第 `7` 回合和同一动作路线。runId `80921c75b7224f4b887e096f07505739`。 | 2026-09-02 |
| `LONG-LINE-V0251-FULL-SMART-DOP8-A/B` | 通过（四个公开合成长线根） | Silent 396、Necrobinder、Mecha、Queen 的上游→候选阶段墙钟为 `120464.516→55984.737`、`120399.236→29898.891`、`23853.522→8034.665`、`67789.142→29062.616 ms`，加速 `2.15×/4.03×/2.97×/2.33×`；胜负、战损、回合与动作语义不退化。 | 2026-09-02 |
| `SILENT-396-NOGC-BUDGET-BALANCE` | 通过（同根 16/4/2 GB） | `4 GB` 为 `53440.887 ms`、峰值工作集 `4.60 GB`，同 16 GB 路线和工作量且相对上游加速 `2.25×`；`2 GB` 为 `54006.365 ms`、峰值约 `3.4 GB`，同路线且加速 `2.23×`。证明预算是玩家可见的速度/内存权衡，不被预设改写或静默钳制。runId `86b10633dd78400fb9176877855074d3` / `fb93590ed2c04cc7b602fe16ea33f824`。 | 2026-09-02 |
| `PERF-NOGC-TOGGLE-DOP-LIFECYCLE-V0251` | 通过（headless GC/DOP 时序门） | 实际覆盖 NoGC→常规 GC→NoGC、切换中手动回收、关闭模式活动计数、搜索检查点吸收手动回收、引用释放后生命周期补账、`1→2 GB` 重建、取消工作量精确一次、节点快照释放及 DOP1/DOP2 全字段等价和真实并发。runId `df0ab7f8f52c41a2b856aea39c411f49`。 | 2026-09-02 |
| `SEARCH-GC-CLR-UPSTREAM-SHORT-FINAL` | 通过（关闭态端到端） | 配置保留 `false / 17,000,000,000 B`，实际为区域未激活、预算 `0 B`、latency `Interactive`；CLR 可自主回收，不把 GC 次数或 pause 错断言为零。runId `6473c714239b4f63a8735b9291d47629`。 | 2026-09-02 |
| `NOGC-SETTINGS-CONTROLLER-LIFECYCLE-FINAL` | 通过（设置页与 Reset 生命周期） | 新装默认开关与 16 GB、旧 JSON、关闭后预算保留、UI 控件归属均通过；全程关闭的 Reset 不建立自动 GC 根屏障，已启用模式的旧义务仍安全结清。runId `50b51af7a92f42948862686001b1b2cb`。 | 2026-09-02 |
| `PARALLEL-WAVE-ROUND-CHOICE-FINAL` | 通过（安全准入、玩家根与并行指标） | 每个并发 parent 按全局高水位 `1.5×` 预约，never-fit 纯串行，仅 multi-parent 成功 wave 扩宽；自然 singleton action replay、round-choice 唯一所有权及原序合并均实际命中。EXOSKELETONS DOP8 为 `5,686 / 199,522 / 175,150`、`44.608 s`、T4/掉 1；`max parent/action/round = 8/8/5`，runId `6ae1570a41054d369669d65895d285db`。最终 DOP1/DOP2 全政策字段等价、Fork/根快照边界通过，runId `66ca91f7b0934ea6aefd69d4ff563826`。 | 2026-09-02 |
| `PERF-PLAYER-ROOTS-LOW-ALLOCATION-FINAL` | 通过（最终低分配候选的 3 个性能根） | `16 GB` 下 INFESTED `24,109/157,893/69,006`、`9.847 s / 5.730 GB`、42 HP/T7，runId `80921c75b7224f4b887e096f07505739`；PHANTASMAL `24,526/477,315/353,923`、`74.328 s / 42.860 GB`、4 HP/T7，runId `6be970228d0b477d8da0fa2748523819`；EXOSKELETONS `5,686/199,522/175,150`、`42.150 s / 22.180 GB`、96 HP/T4，runId `a97129a4cc514665ba7d222169ac1aef`。三者胜负、战损、回合和动作路线不退化。AEONGLASS 已转独立质量分支。 | 2026-09-02 |
| `PERF-EXOSKELETONS-NOGC16-32-FINAL` | 通过（同 DLL、同工作量的 CPU/内存权衡） | NoGC `16 → 32 GB` 保持 `5,686/199,522/175,150`、评分和 96 HP/T4 路线，墙钟 `42.150 → 28.242 s`；结束工作集 `7.66 → 12.83 GB`、private `18.60 → 35.64 GB`。runId `a97129a4cc514665ba7d222169ac1aef` / `efd4eb77f20848cbbdd147c4a9a12c5f`。PHANTASMAL 同设置为 `74.328/75.273 s`，32 GB 没有收益且结束工作集升至约 `21.90 GB`，所以不作通用默认。 | 2026-09-02 |
| `PERF-ALLOCATION-ENUMERATOR-FORK-BOUNDARY` | 通过（低分配枚举与 Fork 所有权） | StateStore static factory、牌堆/AllCards/Forkable concrete enumerator、roster sink 与直接 COW Fork 构造已编译；Fork 边界完成 parent/child 隔离、阵容移除和状态存储验证，runId `5004871b37f94cbeaf6986556fd53533`。结构门禁 `REFACTOR_BOUNDARIES_OK search_files=59`。 | 2026-09-02 |
| `POWER-LISTENER-CACHE-FINAL` | 通过（Fork 隔离与三个性能根） | Fork 夹具通过 `1→2` 缓存身份、`2→0→1` 结构失效、父子缓存 Power 身份隔离及新增 Power 唯一/顺序，runId `962946a034004fd88cf7bec055c5a04f`。INFESTED 保持 `24,109/157,893/69,006`、42 HP/T7，`9.945 s / 5.474 GB`；PHANTASMAL 保持 `24,526/477,315/353,923`、4 HP/T7，`75.122 s / 40.015 GB`；EXOSKELETONS 保持 `5,686/199,522/175,150`、96 HP/T4，`42.257 s / 20.261 GB`。相对上一最终低分配根累计分配约降 `4.5%/6.6%/8.7%`，耗时中性。runId `5371edccb4c74f9dab68d85a51daa641`、`71606a471f5a44d9b1315af133c5987c`、`9b811499b12d4d0188f0d8db12e0ee4d`。 | 2026-09-02 |
| `PERF-EXOSKELETONS-DOP-SWEEP` | 通过（最终安全 admission、同结果并行扩展） | DOP4/8/12/16 均返回同一 `5,686 / 199,522 / 175,150`、96 HP/T4 路线，墙钟为 `44.896 / 44.608 / 43.157 / 43.082 s`；runId `53c14038d4984e9cac9c0113c6861991`、`6ae1570a41054d369669d65895d285db`、`d2029212067941978790f232ff126680`、`e53770540a464f4e94dec64d830e17d0`。DOP4→16 只快 `4.0%`，12→16 仅 `0.2%`，因此开放 16 但仍默认 DOP4。 | 2026-09-02 |
| `PERF-NOGC-LONG-ROOT-CHECKPOINTS` | 通过（安全点与观察内存） | 最终 `16 GB` INFESTED/PHANTASMAL/EXOSKELETONS 分别跨越 `0/4/6` 个 `SEARCH_MEMORY_CHECKPOINT/RESUMED` 成对边界；需要回收的两场均退出区域、回收并继续。结果与检查点日志观察到的最大工作集约 `11.25/12.22/7.28 GB`；这是离散观察值，不冒充连续采样的精确峰值。 | 2026-09-02 |
| `PERF-V0251-VISIBLE-STEAM` | 未验证（Steam 客户端阻断） | 可见门已尝试三次，最近一次仍未在 `60 s` 内启动游戏；没有留下游戏进程，协议文件已恢复。当前数据来自隔离 Linux headless，不替代完整 Mod 组合下的主线程 p95/p99/max 和可见搜索吞吐。 | 2026-09-02 |

## 0.25.3（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ENEMY-TURN-START-SPAWN-ACTION-BOUNDARY` | 通过（斧兵毒杀复生与千足虫、DOP4） | 敌方回合开始后才出生的怪物不参与本回合行动、不提前推进初始行动；斧兵与千足虫均在第 2 回合精确复用，计划外重算 `0`。runId `e33244c904394b19a5e85d78eba5dccf`、`eee444019fd440a887f22d4fe7b35de7`。 | 2026-09-02 |
| `BOMBARDMENT-EARLY-BEFORE-MAYHEM` | 通过（虔诚雕刻师实包第 2 回合根、DOP4） | 爆破在乱战抽牌前检查既有消耗区，不再把乱战本轮刚耗尽的爆破重复打出；第 3 回合精确复用，计划外重算 `0`。runId `e4f7b25918a74f819d0f3ef5705dcaa9`。 | 2026-09-02 |
| `HEXED-JOSS-PAPER-TURN-END` | 通过（三骑士实包第 4 回合根、DOP4） | 纸钱按 Power 动态赋予的虚无统计回合末消耗牌，阈值抽牌和后续手牌保持一致；第 5 回合精确复用，计划外重算 `0`。runId `6a26ff90d1de4c7e8238b82d6b335285`。 | 2026-09-02 |
| `PAELS-LEGION-CARDPLAY-COOLDOWN` | 通过（斧兵实包第 1 回合根、DOP4） | 补偿层产生的卡牌格挡保留 CardPlay 身份，佩尔士兵在出牌完成后启动冷却；第 2 回合精确复用，计划外重算 `0`。runId `0a6b24215996448f9204b84c3fc193da`。 | 2026-09-02 |
| `UNSETTLING-LAMP-CARD-POWER-SCOPE` | 通过（女王实包第 4 回合根、DOP4） | 卡牌 OnPlay 的通用 Power 效果与专项补偿共用同一卡牌作用域；躁动之灯由鞭打的 Doom 正确消耗，不再错误翻倍后续弱化之触。第 5 回合精确复用，计划外重算 `0`。runId `018bc519e6204a42be24ed6e92788eda`。 | 2026-09-02 |
| `ORB-SLOT-CAP-10` | 通过（Fork 边界、DOP4） | 增加轨道槽位统一遵守原版容量上限：`9 + 2 = 10`，满槽后继续增加仍为 `10`。永劫之镜问题包的完整回放曾卡在等待玩家回合，未声称整包复现。runId `2eeb4d75036a4f1a8245275efbbcf31f`。 | 2026-09-02 |
| `AUTOPLAY-UNMOVABLE-PRIOR-BLOCK` | 通过（Fork 边界、DOP4） | 自动打出的格挡牌读取本回合此前完整的卡牌格挡历史；坚不可摧生效前已有卡牌格挡时不再重复翻倍。runId `a2e032db9bf641b89d6d295fa1106f2d`。 | 2026-09-02 |
| `ORB-DEATH-SPAWN-BETWEEN-PASSIVES` | 通过（Fork 边界、DOP4） | 闪电球击杀感染目标后先完成四只扭动虫召唤，再结算后续玻璃球；四只新生怪均承受 `4` 点伤害。runId `a2e032db9bf641b89d6d295fa1106f2d`。 | 2026-09-02 |

## 0.25.2（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POTION-STALE-SLOT-AND-DISABLED-CAP-0252` | 通过（headless 设置、根捕获与 Smart 上限） | 旧药水腰带槽位不再导致初始化越界；两瓶中禁用一瓶后最大 Smart 梯度为一药。失败基线 `c0d4e5eda2c846cb84107b973f4a4374`，修复 runId `fb708ae2ba4b474aa9c8cf11d6c9a35e`。 | 2026-09-02 |
| `MAYHEM-EMPTY-REQUIRED-CHOICE-0252` | 通过（两组自动打牌顺序夹具） | 战乱自动打出空候选选牌牌时直接执行空选择语义，不再生成零候选请求。失败基线 `15c4938d272e43038a2968cd998f0d58`，修复 runId `d59b5f0128e34e058a2cdef78d6f1bf4`。 | 2026-09-02 |
| `KNOWLEDGE-INVALID-CHOICE-BRANCH-0252-FINAL` | 通过（问题包根状态、DOP4） | 无效的知识恶魔计划选牌候选只淘汰自身，其他分支在 30 秒短搜内返回可执行路线。runId `5bbd0920aabe4a4ca69e51d4d821867e`。 | 2026-09-02 |
| `POWER-AFFLICTION-FIRST-GENERATED-0252-FINAL` | 通过（Fork 边界与感染棱柱实包全自动） | 根卡牌在搜索物化时冻结，第一张新生成牌会正确获得生命火花/流电等状态；感染棱柱实际打出“发现”后结束战斗。runId `1a76d76419c14fa78fd60c8e46220587`、`a33c599436d74965afbada4582739aab`。 | 2026-09-02 |
| `KNIGHTS-DAMPEN-ROOT-0252-PASS` | 通过（三骑士第 5 回合实包根、DOP4） | 根捕获导入压制施法者和原始升级记录，搜索跨过魔法骑士死亡并返回 6 个可执行动作。runId `98d1af7fd9284e6698eb7deb2f37c51e`。 | 2026-09-02 |
| `BLESSED-ANTLER-GAMBLING-CHIP-0252` | 通过（假商人实包全自动、DOP4） | 受祝鹿角先随机插入晕眩，再计算花粉核心抽牌和筹码候选；原生手牌页只搜索/选择一次并在首回合结束战斗。runId `8a140d914a4848d096b00651ba4f438a`。 | 2026-09-02 |
| `SLIMED-NATIVE-CHOICE-0252-BASELINE` | 通过（黏液狂战士第 2 回合实包全自动、DOP4） | 当前编译版从问题根状态执行到第 9 回合结束战斗，燃烧契约与宇宙漠然的原生选牌未再漂移。runId `4ddbe88bfcc648f9a5ecf36da6a6a67a`。 | 2026-09-02 |
| `REVIVING-CREATURE-POWER-GATE-0252` | 通过（Fork 边界、DOP4） | 复活阶段统一拒绝新 Power，实验体重生时不会保留实机不存在的弱化。runId `daf83b4f2c614f008facdd5f9126ab23`。 | 2026-09-02 |
| `QUEEN-MINION-FATAL-0252-MINIMAL` | 通过（女王随从 Fatal 最小夹具、DOP4） | 狂宴首动作击杀 1 HP 火炬头随从后最大生命保持 `80`，不触发 Fatal。runId `f80ec3725924407a8603741a1e5d78ce`。 | 2026-09-02 |
| `MONSTER-INITIAL-ROLL-ISOLATION-0252` | 通过（Fork 边界、DOP4） | 搜索从分支快照解析怪物初始行动，不再进入实机 `RollMove` 及外部预测补丁；Search/Prediction 结构检查无残留调用。runId `78f1a80afe664d1cbc97a80b70e131ed`。 | 2026-09-02 |
| `FORCED-POTION-INTERIM-ADOPTION-0252` | 通过（控制器生命周期、DOP4） | 强制用药时，中间展示与采用路线必须已使用指定槽位的指定药水；零药完整胜利线不能提前收束搜索。runId `ce7406b333034a61b75c75ee4a5dac75`。 | 2026-09-02 |
| `AEONGLASS-TURN-START-DEPLOY-QUEUE-0252` | 结构验证（Release 编译） | 回合准备 `Start` 阶段的执行请求按战斗回合排队，进入 `Play` 后由同回合搜索消费，不再进入普通部署拒绝路径；问题包依赖真人点击时机，未声称自动复现。 | 2026-09-02 |

## 0.25.1（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `MANUAL-GC-PERFORMANCE-PAGE` | 通过（headless 设置页生命周期） | 主界面不再创建手动 GC 按钮；按钮归属性能页，常规/性能/反馈切换正常。runId `530e502cf9a744c4995c2a56af57a954`。 | 2026-09-01 |
| `ISSUE-213617-SMART-TIME-CLOSURE` | 通过（问题包同根 headless 短搜） | Smart、DOP4、`2.068 s`，生成 `9` 个动作；自动药不再触发主动用药门槛，超时结果保持回合完整。runId `7b2a2cf1e7e34dd7813ec0248733ee8b`。 | 2026-09-01 |
| `ISSUE-213617-MUMMIFIED-HAND-REPEAT-COST` | 通过（`1/1` 实机/模拟差分） | 连续打出 `SWORD_SAGE`、`PARRY` 后，木乃伊手临时费用、随机候选和 RNG 一致。runId `4e42216ff3d145d5a8a19b8dca0c857f`。 | 2026-09-01 |
| `TEST-SUBJECT-TURN-BUDGET-FIXED` | 通过（问题包同首根 headless 搜索） | Medium 搜索的升级早有准备、`Glam` 重放与本能反应/战术大师弃牌链按整张牌共用选择预算；深化 `30 s` 的回合层调度从修复前 `4` 回合推进到 `8` 回合，转移 `131,808 → 127,890`，分配 `12,410,879,432 → 12,246,683,840 B`。runId `116f9e03b1fd4181b7412792a9e8277e`。 | 2026-09-01 |
| `HEADBUTT-CHOICE-SCHEDULING-SENTINEL` | 通过（headless 选牌与跨回合复用） | 普通战斗的头槌牌堆选择正常部署，第 2 回合精确复用，计划外重算 `0`。runId `cb66e56414814c0eba21b176f5de3ce9`。 | 2026-09-01 |
| `STRATAGEM-SHUFFLE-CHOICE-FREEZE` | 通过（问题包同首根 headless 搜索） | 洗牌监听器中的战略选牌按请求时刻冻结候选；跨 `1` 次洗牌搜索到 `3` 回合路线，不再因后续生成的煤灰牌导致计算失败。runId `b8ca4dd0641840c59b7cee9a8c7a393e`。 | 2026-09-01 |
| `SEEKER-RANDOM-CHOICE-REUSE` | 通过（问题包同首根 headless 部署与复用） | 探寻打击在攻击及其触发结算后生成随机候选，候选绑定 RNG 计数与卡牌集合；同回合多次原生选牌全部部署成功，第 `2` 回合精确复用，计划外重算 `0`。runId `17cfabb9045a4935b2dcacb0b1ece959`。 | 2026-09-01 |

## 0.25.0（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POTION-PERSISTENCE-BOUNDED-AUDIT-0244` | 通过（headless 设置、搜索与控制器生命周期） | 强制/保护策略按槽位 + 药水 ID 完成 JSON 往返，同槽新药仍为 Smart，恢复 Smart 后不保留覆盖项；Smart 主搜索和药水后验共享 `1.2 s` 请求预算，累计耗时与进度不倒退。runId `0911df45b8b34a04b761d9239f530e9f`。 | 2026-09-01 |
| `PR25-RUNTIME-GC-INTEGRATION-0244` | 贡献者实测通过；本地集成门禁通过 | 默认关闭求解器 No-GC、补账回收与显式自动收集，保留玩家“手动 GC”入口。贡献者报告实机可行且内存占用下降；本轮不重复性能基准。合并态编译与结构门禁通过，药水/控制器夹具 runId `ffe6bad16592496ea1b02fbc6715930a`。 | 2026-09-01 |
| `POTION-SLIM-SIDEBAR-ANCHOR-0244` | 通过（headless UI 结构与生命周期） | 药水策略为约 `184 px` 单列窄侧栏；展开侧栏时标题栏预留同宽区域，药水策略、设置和收起按钮仍锚定在主面板右缘。runId `7c0d24e7f3b9448da0e596651d853e15`。 | 2026-09-01 |
| `POTION-SEARCH-MULTI-PHASE-LABELS-0244` | 通过（headless UI 文案与搜索阶段） | 战损提示包含性能预设建议，点击后持久关闭且不再跳转；搜索阶段覆盖无药、恰好 `N` 瓶的智能梯度，以及固定政策的单药、双药和三药药名。 | 2026-09-01 |
| `SMART-POTION-GRADIENT-EXACT-0244` | 通过（headless 搜索结构与阈值） | Smart 以无药为唯一基线，普通药按 `9/18/27 HP` 开放恰好 `1/2/3` 瓶额度，同层药水共同竞争并在第一条合格梯度停止。runId `406220b4b3b7482a97ebef4a16a330e9`。 | 2026-09-01 |
| `SMART-POTION-LETHAL-GRADIENT-0244` | 通过（headless 完整自动战斗） | 无药路线死亡时进入恰好一瓶梯度，实际使用格挡药并以零战损生还，计划外重算 `0`。runId `aa7e15b86b3a412c9c8abdea72d6b375`。 | 2026-09-01 |
| `COMPLETE-INTERIM-RESULT-0250` | 通过（headless 搜索与控制器生命周期） | 回合层检查点只发布敌人全灭、玩家存活的完整路线，未结束战斗的边界不能冒充整场预计战损；用药数与战损继续严格递增优，玩家采纳后采用同一结果。runId `646a6ebe134e4253a9693983ae398240`。 | 2026-09-01 |
| `TURN-SETUP-COMPLETE-INTERIM-0250` | 通过（headless 烘焙手套开局搜索） | 回合准备搜索只在完整获胜路线出现后允许采纳；点击后从安全检查点生成 `23` 动作计划。runId `3307d4fe09c544d3a4373b5339fa2991`。 | 2026-09-01 |
| `SEARCH-STATUS-TWO-LINE-0250` | 通过（headless UI 与控制器生命周期） | 搜索状态区使用 `64 px` 双行高度和正常字号；阶段保留在第一行，当前用药/战损及累计世界线显示在横跨面板的第二行。runId `f26539fb24a040c09705fb9f72947198`。 | 2026-09-01 |

## 0.24.3（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `UI-PERFORMANCE-POTION-GRID-0243` | 通过（headless UI 与设置生命周期） | `0.24.3` 一次性迁移到 Medium + `16 GB`，新默认一致；预设与内存独立保存。搜索中显示累计世界线，完成摘要包含耗时和总查阅数；战损提示可直达性能页；药水策略为右侧自适应网格卡片，主界面按钮字体与样式统一。runId `e93ec85ff4de49eea28dfeb5892de013`。 | 2026-09-01 |
| `PERFORMANCE-HIGH-INDEPENDENT-MEMORY-0243` | 通过（headless 实际 No-GC 区域） | High 预设与 `17 GB` 内存同时生效，实际建立 `17,000,000,000` 字节 No-GC 区域；证明切换预设不改写内存，旧 `16 GB` 上限已移除。runId `4d12cac9501248c6b108761450f098e8`。 | 2026-09-01 |
| `UI-VISIBLE-0243` | 未执行（按用户要求） | 不做可见界面观感检查；本轮只以 headless 控件结构、布局属性和生命周期断言作为界面验证。 | 2026-09-01 |

## 0.24.2（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `ISSUE-BYRDONIS-FIXED-PREFIX` | 通过（问题包同首根定向回放） | Smart、DOP4、`3 s` 下，修复前把第 `5` 回合赌徒特酿动作作为第 `1` 回合固定前缀并失败，runId `888ebe8510e7499aa9664ee096567dc0`；修复后正常返回预计用药 `1`、省血 `10/9` 的路线，runId `af0cd37576444983987eeb8e91be1dc3`。 | 2026-09-01 |
| `ISSUE-KNOWLEDGE-DEMON-SMART-OPTIONAL` | 通过（问题包同首根定向回放） | 玩家策略为 Smart；内部至少一瓶反事实没有合格路线时按可选候选缺失处理，不改变玩家政策。`1 s`、DOP4 返回无药路线且没有 `PotionPolicyUnsatisfiedException`，runId `df63187c89f748df8829271c8e333560`；原报告 `300 s` 整段未复跑。 | 2026-09-01 |

## 0.24.1（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `INITIAL-TOASTY-MITTENS-SEARCH-CONTROLS-NEXT` | 通过（headless 开局搜索控件） | 烘焙手套首次搜索未发布计划时依次请求“执行”和“重算”；执行进入回合准备接管队列，后续重算等待实际选牌完成并在 Play 阶段产生新的第 `1` 回合 `28` 动作路线，没有普通阶段拒绝。runId `f5b7c0a4749c4baba471d58f0c5fb676`。 | 2026-09-01 |
| `INITIAL-TOASTY-MITTENS-SCENE-EXIT-NEXT` | 通过（headless 场景退出边界） | 原生手牌选择等待期间返回主菜单，场景拆除前取消选择 `1` 次；原报告的 `NPlayerHand.SelectCards / AfterCardsSelected / move_child` 栈未再出现。测试结束后的 RitsuLib 设置页焦点链另有独立离树节点日志，不属于本项。runId `1868ed8d9b3a4de19715438060db287f`。 | 2026-09-01 |
| `ISSUE-GREMLIN-MERC-TOASTY-495-FIXED` | 通过（问题包同状态定向回放） | 修复前智能药水后验在烘焙手套分支报 `找不到手牌 PIERCING_WAIL`，runId `b89edc28d6924bf28ea28b4d7c9436a1`；修复后 `3 s`、DOP4 搜索生成预计战损 `1` 的手套计划并等待玩家确认，没有 `TURN_SETUP_FAILURE`，runId `8e44bc14c2f44574b36ac59a2dd402a2`。 | 2026-09-01 |

## 0.24.0（已发布）

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `TURN-SETUP-MANUAL-REFRESH-0240` | 通过（headless 原生选牌页刷新） | 烘焙手套手牌页、工具箱三选一和选择悖论页面都在首次计划就绪后再次请求手动重算，同一页面收到新的 `PlanReady` 并按刷新后的路线完成选择。runId `15f1fedfab9f4a15a8cf224afe6b36fd`、`55f9bea8cf014e94a194eaa8280ab1da`、`635775924e2041e5b8348def2a77fef7`。 | 2026-09-01 |
| `INITIAL-GAMBLING-CHIP-MANUAL-RECALCULATE-0240` | 通过（headless 选择后重算） | 先在赌博筹码页面空选跳过，再立即请求手动重算；求解器进入 Play 后从实际手牌搜索，第 `1` 回合新路线含 `13` 个动作，不再被阶段校验拒绝。runId `f87e11a8a9934e4cb0934ee47cadd4c0`。 | 2026-09-01 |
| `POTION-STRATEGY-FORCED-SEARCH-0240` | 通过（headless 搜索、结构与生命周期） | 主界面从当前药水栏建立图标、官方名称和逐瓶选项，折叠开关生效；新药默认 Smart，Force 的真实短搜使用精确槽位与药水 ID，Disabled 阻止主动用药；新安装/恢复默认解析为 VeryHigh。runId `2b1bef9d976242d09591331db0906466`。 | 2026-09-01 |
| `SMART-POTION-LETHAL-0240-SENTINEL` | 通过（headless 完整自动战斗） | 1 HP 致死场景继续选择格挡药并在第 2 回合获胜，首轮预计用药 `1`、整场战损 `0`、计划外重算 `0`。runId `33fad9cf08aa440eab0bc6dfc790f5b9`。 | 2026-09-01 |
| `STRATEGY-ACTION-ADMISSION-EXPERTISE` | 通过（headless） | 原单节点分支预算压到 `3`，手牌包含升级熟练、升级子弹时间和三张打击；搜索先保留资源/过牌代表，首动作选择熟练并完成三回合短搜。runId `d2edebb4a7094d2fbce5787e02cc849c`。 | 2026-08-31 |
| `STRATEGY-SMART-POTION-INTERVENTION` | 通过（headless） | 四只花园幽灵鳗固定 `15 HP`、纯攻击牌组和一瓶格挡药；Smart 主路线为无药死亡边界，主动强制一瓶反事实找到存活路线，最终采用格挡药并确认省血 `12/9`。runId `fb7b2287a75e442cad01e1ca32f14417`。 | 2026-08-31 |
| `STRATEGY-SEMANTIC-AFTERIMAGE` | 通过（headless） | 单节点分支预算为 `3`，逐次出牌获得格挡的能力与五张 0 费攻击牌同手；能力收益按可达出牌次数形成防伤向量，首动作使用能力牌，路线预计战损 `0`、实际格挡 `5`。runId `35b7698ab74d462bb40182008bf6cd82`。 | 2026-08-31 |
| `STRATEGY-HP-INVESTMENT-DYNAMIC` | 通过（headless） | 一张提供能量和抽牌的牌直接支付 `6 HP`，超过普通战斗原 `5 HP` 阈值；同起点保守路线存在时，至少一条确实换来战斗进度的投资分支获得保护，最终结果仍按整场战损选择零卖血路线。runId `02b131c187d1433c94ea558e485c591d`。 | 2026-08-31 |
| `STRATEGY-ACTION-ADMISSION-COVERAGE` | 通过（headless） | 固定单节点分支预算 `3`，六种不同即时攻击、过牌与费用控制候选竞争；至少一个原即时 Top-N 之外的战略家族代表进入 frontier。runId `06eb33b27c344825b0801d282b7b8df2`。 | 2026-08-31 |
| `STRATEGY-DOP-EQUIVALENCE` | 通过（headless） | 固定 `250` 节点下 DOP1/DOP2 的动作、评分、展开、转移、分族保路、生命投资和全部非时序剪枝统计一致；DOP2 实际形成至少两路并发。runId `64414648aaae4406a570a0ff59ef1f17`。 | 2026-08-31 |
| `STRATEGY-REPLAY-FDDD-MEDIUM` | 通过（headless） | 严格组合永世沙漏报告同检查点的 `run-state` 与 `replay-state`，完整 `ContinuationStamp` 一致；Medium `24/60` Beam、Smart、DOP4、8GB No-GC 下首动独门技术，第 `4` 回合无药击杀，预计战损 `3`，追平并优于玩家 `6` 战损上界。当前源码回归 runId `3e1f05195228471bbea6cafaabcab1d7`。 | 2026-08-31 |
| `STRATEGY-REPLAY-0F7F-VERYHIGH-RETAIN-ROUTING` | 通过（headless） | 严格恢复蜂群术士报告首回合根及完整 RNG；VeryHigh、禁用药水、DOP4、8GB No-GC 下完整路线第 `13` 回合无药击杀，预计整场战损 `0`，从报告原求解器 `57` 追平人工 `0`。runId `34d0742df3c74ee6a9a5006eabf0ece2`。 | 2026-08-31 |
| `STRATEGY-REPLAY-9E8B-HIGH-ORB-LINEAGE` | 通过（headless） | 严格恢复胧光怪报告第二回合、寄生惧魔召唤物、球槽和球队列；High、Smart、DOP4、12GB No-GC 下首步电击，随后飞跃、防御+、防御，完整路线第 `9` 回合无药击杀，预计整场战损 `0`，从报告原求解器 `29` 追平人工 `0`。runId `d7c41a3e64d24fb792d2b45504222e25`。 | 2026-08-31 |
| `STRATEGY-REPLAY-394B-HIGH-SHORT` | 通过（headless） | 严格恢复虔诚雕塑家报告第二回合根；High、禁用药水、DOP4 在 Short 阶段得到第 `4` 回合结束的 `0` 战损路线，从报告原求解器 `22` 追平人工 `0`。runId `75e243420b5b4f5ca48bc850a5e91ff2`。 | 2026-08-31 |
| `STRATEGY-REPLAY-EF3E-HIGH-POTION-COUNTERFACTUAL` | 通过（headless） | 严格恢复寄生蛙报告第三回合根；High、Smart、DOP4 在 Short 阶段找到 `1` 战损无药路线，评估并拒绝 `120` 条药水分支，从报告原求解器 `13` 改善并优于人工爆炸药路线 `2`。runId `a967a8dbce494fa2948f6d4d94211a4d`。 | 2026-08-31 |
| `STRATEGY-REPLAY-99DC-HIGH-CALCULATED-GAMBLE` | 通过（headless） | 严格恢复构装兽群报告首回合根；High、禁用药水、DOP4 在 Short 阶段得到第 `4` 回合结束的 `0` 战损路线，从报告原求解器 `10` 追平人工 `0`。runId `10f27aef7c9941a8820de637ce28c2ab`。 | 2026-08-31 |
| `STRATEGY-REPLAY-8695-HIGH-ENERGY-DEFENSE` | 通过（headless） | 严格恢复感染棱晶报告首回合根；High、Smart、DOP4 得到 `8` 战损，评估并拒绝 `120` 条药水分支，从报告原求解器 `21` 追平人工 `8`。runId `566104854a2448ef95976505746abac2`。 | 2026-08-31 |
| `STRATEGY-REPLAY-EE98-HIGH-DUAL-POTION` | 通过（headless） | 严格恢复寄生蛙报告首回合根；High、Smart、DOP4 在 Short 阶段使用束缚药水和格挡药水，反事实省血 `50/18`，战损从报告原求解器 `27` 降到 `17`，追平人工。runId `c3e91ca55bd3476487d8710e899b12f1`。 | 2026-08-31 |
| `STRATEGY-THE-HUNT-OPPORTUNITY-COST` | 通过（headless） | 感染棱晶严格首根 High、Smart、DOP4 从 `29` 降到 `21`，前三回合战损 `0/6/13` 与人工一致，runId `2ac40cf8aa7240629ccc5fa1a10f894d`；致命狩猎哨兵仍首动使用狩猎、长期资源至少 `30`、战损 `0`，runId `4c445ef747f643c59b0fc437bd161d4d`。 | 2026-09-01 |
| `STRATEGY-REPLAY-941B-GENERATED-RESOURCE-POTION` | 通过（headless） | 直飞产卵虫严格首根 High、Smart、DOP4 选择无色药水生成急躁，随后打击、急躁、群星之子+、战火铸就，战损从当前 `2` 降到 `0`，追平人工；runId `f8bc0f22a1be4c688f75c603528b917d`。感染棱晶哨兵保持 `21`，runId `6fb2503e4bb34db2972b569f7dd944d9`。 | 2026-09-01 |
| `STRATEGY-SMART-POTION-NONDEGRADING` | 通过（headless） | 蔓生伏地虫严格首根 High、Smart、DOP4 保留 `4` 战损无药主路线，拒绝 `14` 战损敏捷药补查路线并追平人工，runId `5db3d24895f045ee888841b2d9ee207a`；无色药生成急躁哨兵仍为 `0` 战损，runId `296884e4797343f8a1a0502da863b778`。 | 2026-09-01 |
| `STRATEGY-SMART-POTION-ENABLER-FOCUS` | 质量改善，待处理 | 残杀千足虫严格首根 High、Smart、DOP4 保留放血→迅捷药、持续设置和分目标首攻后验，战损从未完成路线的 `48` 降到获胜路线 `35`，人工为 `31`，runId `e05ccbcc56a0448e87d58fc075c9fd60`；蔓生伏地虫哨兵保持无药 `4`，runId `f2ab6a5bfb9a486ba244b1cbcf86a075`。 | 2026-09-01 |
| `STRATEGY-OPENING-RESOURCE-DEFENSE` | 通过（headless） | 灵魂枢纽严格首根 High、Smart、DOP4 选择燃烧契约→火焰屏障→好勇斗狠，战损从 `19` 降到 `12`，优于人工 `17`，runId `21c2a331c31641259a6810a7b93704d6`；蔓生伏地虫哨兵保持无药 `4`，runId `f387d4c98a3e478082ece11e79e38e0c`。 | 2026-09-01 |
| `STRATEGY-LONG-TERM-RESOURCE-CHANNEL` | 通过（headless） | 长期资源通道与即时战损主通道使用独立 Beam 排名。虱虫祖先严格中途根 High、Smart、DOP4 为 `16` 战损，追平人工，runId `ab7beaa750714ed28bfcb7a6d5d781fb`；感染棱晶哨兵为 `19` 战损，追平人工，runId `066e5ed6d1c246e5adbd2522261ebcad`。 | 2026-09-01 |
| `STRATEGY-SMART-POTION-COST-ACCOUNTING` | 通过（headless） | Smart 后验按每瓶 `9 HP` 重算强制用药候选成本。鬼祟珊瑚群两瓶药仅省 `9 < 18`，VeryHigh、Smart、DOP4 保留 `10` 战损无药路线，runId `121d37d20fa24430a2528879aebab339`；永世雕像一瓶迅捷药省 `10 >= 9`，保持 `6` 战损并追平人工，runId `5237dd207c044649b44534f3edb0ddf0`。 | 2026-09-01 |
| `LEGACY-REPLAY-BASELIB-EMPTY` | 通过（headless 导入边界） | schema 1 旧包缺少 BaseLib modifier 字段、当前卡牌均明确为 `baselib=-` 时，完整机甲骑士首回合根严格恢复；非空 modifier 仍不兼容。runId `17d735ddcc0f463d95cc4fca1e06253a`。 | 2026-09-01 |
| `AEONGLASS-LISTENER-CACHE-FORK-FINAL5` | 通过（headless 语义门禁） | 首次 COW、结构失效、普通字段缓存复用、父分支/OwnerPile 隔离和完整根快照均通过。runId `932ad9691a204b3c8ea37f795c4a92b7`。 | 2026-09-01 |
| `BASELIB-CARD-MODIFIER-LISTENER-CACHE-FINAL6` | 通过（完整 BaseLib headless 保守兼容门禁） | 动态增删、状态键、continuation、Owner/分支隔离、生成卡复制和空列表功能路径均通过；空路径不创建临时列表由源码审计。动态 `StoreSaveData` 回调在枚举中新增 live modifier，本次完整状态键仍与回调前相等。runId `06c5235a6d0941f69447180517bce7ab`。 | 2026-09-01 |
| `GC-LIFECYCLE-POLICY-MECHA-013` | 通过（headless 合成政策时序门） | 低/高分配与引用屏障通过；obligation 登记后，exhaustion 引用在 Gen2 标记前释放时总 Gen2=`1`，标记后为 `2`，两条弱引用图均死亡；额外正式 release epoch 不触发第三次回收。生产检测入口另经静态审计。runId `9b4f577a08c84800b219cbcb0bc83310`。 | 2026-09-01 |
| `GC-CONTROLLER-RELEASE-MECHA-013` | 通过（headless 控制器生命周期门） | A→B→Reset、搜索/部署 CTS 与旧 Setup epoch 通过；真实 3 秒 Godot timer helper 取消后屏障在 1 秒内完成，正式 Setup/Resume token 接线另经静态审计。runId `ef96c383720b47dbbdcb62075bcb665d`。 | 2026-09-01 |
| `PR19-NOGC-REGION-EXIT-DELAY` | 目标通过；组合门后续失败 | 实际建立 `1 GB` No-GC 区域后请求低分配战斗结束，区域退出延后 `3011.4 ms`，`gen2_delta=0`，确认延迟位于 `GC.EndNoGCRegion` 前。组合夹具随后在无关的DOP2搜索门以 `waves/work_items/max_concurrency=0/0/0` 失败，毛绒虫与机甲输入结果相同；不将整套组合门记为通过。目标 runId `f9615ee6bdf648fcbba17330aecfb9ea`。 | 2026-09-01 |
| `BUGREPORT-UTF8-CURRENT-RECENT` | 通过（headless 输出兼容门） | 当前/最近两类问题包的 metadata/replay 均为无 BOM 严格 UTF-8，并继续通过 JSON、native-state、run-state 与槽位隔离断言；`byte[]` 常驻表示和单次序列化另由源码审计及驻留估算支持。runId `43e3cc399a6b45e8968da5f7af05556d`。 | 2026-09-01 |
| `PRESENTATION-STRINGVAR-STATE-DIFF-FINAL` | 通过（headless 严格差分） | `NightmarePower.Card` 与 `ShrinkPower.ApplierName` 的本地化展示值不再制造假差异，字段/数值基线仍比较，未知字符串仍 fail-fast。runId `1fe98254c4d045f7a6d7872ae0f26c6f` / `fb2d85cd91724310a3772ebf6204e893`。 | 2026-09-01 |
| `HEADLESS-FULL-MATRIX-20260901` | 场景结果全通过；矩阵命令含 1 次已复验的启动器竞态 | 完整 `246` 命令为 `242 Passed / 3 SkippedMissingFixture / 1` 启动器退出身份竞态，因此该次矩阵命令整体非零；该项游戏内已 Passed（`50a3d2aaf3d648498666e9d46ad1b2b9`），同命令独立复验由启动器返回 `0`（`08fdef52d7974d9185b255edafd6395e`）。最终 `243` 条可执行命令（`242` 个唯一 ScenarioId）均有通过结果，无未解决行为失败。 | 2026-09-01 |
| `SEARCH-PERF-IRONCLAD-CLONE-HAVOC-ROOT-A/B` | 通过（清空默认牌组的固定根 A/B） | 同一 `2305` 张战斗牌、`2302` 张永久牌组牌、`4499` listener 的严格 A/B 为 `4570.556 → 2215.689 ms`；最终源码复验根阶段 `2208.239 ms`，runId `7e8a7512406c44e6bd0bb0fec7a788e0`。 | 2026-09-01 |
| `SEARCH-PERF-IRONCLAD-CLONE-HAVOC-1S-A/B` | 通过（清空默认牌组的固定工作量 A/B） | 严格 A/B 的耗时/分配降低 `33.07%/70.68%`；最终源码复验仍为 `1/3/3/2`、同一 `ENTROPY`、`-3752001`、TimeLimit、0 GC，`3717.9 ms / 267,559,992 B`，runId `47c008cb690d4073bfa0781d510d6378`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-KNIGHTS-CANONICAL-DOP1-2S` | 通过（合成首回合） | `5` 种随机攻击、`5` 种随机防御各 `6` 张，加 `INFERNAL_BLADE/STOKE/CATASTROPHE`；DOP1 短搜 `1324.6 ms / 96,126,192 B`，`choice/actions/sold-hp-pruned/turns=0/4/14/4`，0 GC。runId `108c2fd35daf46938d66be241d51283a`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-QUEEN-CANONICAL-DOP1-2S` | 通过（合成首回合） | 同样的 `5+5` 冻结随机填充，加 `BUNDLE_OF_JOY/SPECTRUM_SHIFT/ENTROPY/JACK_OF_ALL_TRADES` 及对应 Power；DOP1 短搜 `2143.5 ms / 250,810,968 B`，`choice/actions/sold-hp-pruned/turns=1146/6/270/2`，0 GC。runId `f590d200f7e0467aaf090c140d2eced8`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-TEST-SUBJECT-CANONICAL-DOP1-2S` | 通过（合成首回合） | 同样的 `5+5` 冻结随机填充，加 `CREATIVE_AI/AUTOMATION/MAYHEM/JACKPOT` 及对应 Power；DOP1 短搜 `2062.0 ms / 160,130,880 B`，`choice/actions/sold-hp-pruned/turns=0/6/0/5`，0 GC。runId `17875c14859c4aedb5f44e5f6539b788`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-AEONGLASS-DOP1-2S` | 通过（合成首回合） | 同样的 `5+5` 冻结随机填充，加 `TRANSFIGURE/SEEKER_STRIKE/CALL_OF_THE_VOID` 及对应 Power；DOP1 短搜 `1393.4 ms / 149,711,016 B`，`choice/actions/sold-hp-pruned/turns=516/4/24/5`，0 GC。runId `11d097fe49764027a69c551616ab5416`。 | 2026-09-01 |
| `SEARCH-PERF-COMPLEX-RANDOM-QUEEN-DOP4/8` | 通过（同工作量并行探索） | DOP4/8 均为 `choice/actions/sold-hp-pruned/turns=2237/6/234/6`；`1764.1 ms / 553,203,344 B` 对 `1489.3 ms / 560,337,384 B`。DOP8 快 `15.58%`，分配多 `1.29%`；runId `329ace6a379748fbb980b22d7d4f2ba3` / `c2b1de7dda994123be24c5fbdeb25b4c`。 | 2026-09-01 |
| `AEONGLASS-PERF-VISIBLE-STEAM` | 未验证 | 当前数字只来自隔离 Linux headless；完整用户 Mod 组合下的主线程帧、根捕获与 GC 仍需可见 Steam 会话验收。 | 2026-09-01 |

## 0.23.0

本版本只汇总 `0.22.1–0.22.11` 的既有改动并同步发布版本，没有修改行为源码；沿用下列各补丁版本已经记录的定向回归，不重复执行行为测试。

## 0.22.11

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-141700-TURN-START-CHOICE-BASELINE-R2` | 预期失败，已修正 | 第 2 回合熵选牌后通用阵营回合开始重复结算，路线产生一次计划外重算。runId `632b60e264064d149ec0e95f45e8a15a`。 | 2026-08-31 |
| `FIX-141700-TURN-START-CHOICE-FIXED` | 通过（headless） | 第 2 回合熵选牌后回合开始效果只结算一次；首轮路线直接复用，计划外重算 `0`。runId `cf5c0c5b057e4e0a80409e87124c7535`。 | 2026-08-31 |

## 0.22.10

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-133031-DEPLOY-CARD-IDENTITY-R2` | 通过（headless） | 两个同名卡牌实例具有不同临时状态；前一个实例离手后，实机部署按路线保存的完整状态身份选中剩余计划实例。Fork 边界与首回合实际自动战斗同时通过，runId `9e733b537f8f4eee98a4df4f79389bb6`。 | 2026-08-31 |
| `FIX-133031-TEST-SUBJECT-CURRENT-ROUTE` | 通过（headless） | 从实验体问题包战前跑局恢复种子、牌组、遗物与 RNG，以 12 秒短搜和 DOP8 自动执行到第 3 回合；直接复用首轮路线，计划外重算 `0`，没有部署或原生选牌失败。runId `7a8d7f88e17b4b05af7e99e2139e1044`。 | 2026-08-31 |
| `FIX-133031-TEST-SUBJECT-DEPLOY-IDENTITY` | 夹具断言失败，未计通过 | 同一现场已完成第 3 回合复用且计划外重算 `0`，但请求额外强制必须打出全息影像；当前短搜选择了另一条合法路线，因此只该特定出牌断言失败。runId `d376600caf704cdfb06cd2ef061658a7`。 | 2026-08-31 |

## 0.22.9

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-123849-BOUNDARIES` | 通过（headless） | 同一 L1 夹具覆盖新玩家能力位于战斗卡牌之前、横祸嵌套自动打出虚空形态后产生并消费一次结束回合请求，以及预知之滴在三个同名升级剑柄打击上跨同父 Fork 稳定回放。runId `aadd72eb88f0498a8a125e43dd2a3000`。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID` | 通过（headless） | 固定手牌仅横祸、抽牌堆仅虚空形态；短搜首动作是横祸，路线第 1 回合只有这一项动作并继续搜索到后续回合。runId `0569fa1dddf5479493aab7fe14a352c6`。 | 2026-08-31 |
| `FIX-123849-DROPLET` | 通过（headless） | 固定四张同名升级剑柄打击和预知之滴，DOP1 强制至少使用一瓶药；搜索完成 `12` 个选牌分支、使用一瓶药且没有动作回放失败。runId `5babab76d0de43a3bb73861bb7e40726`。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID-DEPLOY` | 通过（headless） | 全自动实际打出横祸并由内层虚空形态结束第 1 回合；没有尝试同回合后续动作，第 2 回合直接复用首轮预测，计划外重算 `0`。runId `9cc7926cb1814abcabad5ad83dee4bfc`。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID-DEPLOY`（首轮） | 预期失败，已修正 | 首轮 runId `27b504a98ee84ae8b3eb4b3288544c7b` 已证明部署停止同回合动作，但第 2 回合因续用缓存只登记显式结束回合节点而重算；统一动作回合边界后由最终夹具通过。 | 2026-08-31 |
| `FIX-123849-NESTED-VOID`（参数首轮） | 夹具失败，已修正 | runId `c1e23e0c1b444ee1ac05ae80800eb14a` 误用必须同时提供卡牌标题的断言参数；改用首动作卡牌 ID 断言后通过，不属于生产功能失败。 | 2026-08-31 |

## 0.22.8

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `KNIGHTS-GC-NRE-FIX-NEXT` | 通过（headless） | 实际进入一次搜索内内存检查点并完成全代回收后继续；随后 No-GC `1 GB → 2 GB` 生命周期正常，DOP1/DOP2 固定工作量结果一致。首轮短搜完成后停止，runId `f8764bdbe2594c66920ff428fe6425d6`。未完整重放问题包中的三骑士战斗。 | 2026-08-31 |

## 0.22.7

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `TOASTY-SINGLE-STEP-UI-FIXED-NEXT-R2` | 通过（headless） | 执行第 1 回合后停在第 2 回合烘焙手套原生手牌页；求解器没有代选，路线 UI 已同步到第 2 回合。runId `64243d425288491198f9a6bf5f415de0`。 | 2026-08-31 |
| `TOASTY-SINGLE-STEP-EXPLICIT-TAKEOVER-NEXT` | 通过（headless） | 从同一单步边界明确点击执行本回合后才接管烘焙手套；选择到部署间隔 `47 ms`，第 2 回合复用既有路线，计划外重算为 `0`。runId `ae2d5384fa144815b7a250f2442c556c`。 | 2026-08-31 |
| `TOASTY-SINGLE-STEP-UI-FIXED-NEXT` | 预期失败，已修正 | 新增 UI 回合断言首次抓到选牌状态早于原生页面锁回调，面板仍为第 1 回合；将显示同步到回合准备事务建立时后，由 R2 通过。runId `a3f331c2cf0b48c29f42e6b6c3a56b00`。 | 2026-08-31 |

## 0.22.6

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-095952-BOUNDARIES-R2` | 通过（headless） | 同一快速夹具覆盖冻结快照不受实机原牌移除标志污染、牌组原牌与局内生成复制的精确回放身份、流沙坑不存在时狂乱逃离为空操作、毒气炸弹自爆为终止行动且眩晕仍有后继，以及根级 Hook listener 捕获。`ForkBoundaries` 与 `CombatRootSnapshot` 均通过，runId `0c19ded7f13444fe914499f85924b840`。 | 2026-08-31 |
| `BATCH-095952-INCREMENTAL` | 通过（headless） | 固定 1 秒短预算在首个结果停止，增量分叉与完整前缀回放保持一致，runId `dd22d98ed4174af692a27764871314f9`。请求 DOP4，但严格增量模式按现有测试政策强制单线程，因此不计并行性能证据。 | 2026-08-31 |
| `BATCH-095952-BOUNDARIES-R1` | 夹具失败，已修正 | 新增流沙坑断言最初直接构造未挂接战斗状态的模拟对象，runId `52ab5e56bb1744e99aa9b993b3a22306` 在测试准备阶段失败；改为从已挂接模拟器取得战斗状态后由 R2 通过，不属于生产功能失败。 | 2026-08-31 |

## 0.22.5

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `TWO-TAILED-RAT-PENDING-AI-UNIT-0225` | 通过（headless） | 敌方回合生成随机初始分支的双尾鼠时不消费怪物 RNG；回合边界才掷出抓挠、疫病啃咬或尖啸。runId `02120badde45441f99b384437879e517`。 | 2026-08-31 |
| `TWO-TAILED-RAT-PENDING-AI-FINAL-0225` | 通过（headless） | 从问题包恢复同一首回合牌序、三只鼠生命与行动；修复前 runId `941ffb14c3244100b3285336a61db0eb` 在第 3 回合呼叫支援失败，修复后首轮短搜得到 10 回合路线、预计战损 `4`，runId `5649195a84e342c7b8bc8d70dd5281c0`。 | 2026-08-31 |

## 0.22.4

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `STRATEGY-PHANTASMAL-FINAL-0230` | 通过（headless） | 从问题包精确恢复花园幽灵鳗首回合根；4 秒短搜找到两瓶药路线，预计整场战损 `27`，低于改动前同根 `35`。runId `954385d2794049ada20ee0706016486c`。 | 2026-08-31 |
| `STRATEGY-AXEBOTS-MASTER-0230` | 通过（headless） | 从问题包精确恢复巨斧机器人根；必备工具 setup 校准后预计战损 `4`，改动前同根为 `5`。runId `4595ce39f3df4775b008ea75d46d1f20`。 | 2026-08-31 |
| `STRATEGY-KNOWLEDGE-REVERT-GUARD-0230` | 通过（headless） | 知识恶魔精确根维持预计战损 `3`；验证失败的早有准备统一加权已经撤回。runId `3f4973a3ebe64387af2fca6925797354`。 | 2026-08-31 |
| `STRATEGY-KAISER-CURRENT-0230` | 通过（headless） | 帝王蟹精确根维持预计战损 `0`，未因药水策略校准退化。runId `8b27b0f445b24e669189b855e342358a`。 | 2026-08-31 |
| `STRATEGY-NIBBITS-FINAL-GUARD-0230` | 通过（headless） | 固定双小啃兽长线根维持预计战损 `0`，不使用药水。runId `0d8363df9df44164a0ac8f4267d64e31`。 | 2026-08-31 |

## 0.22.3

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-0223-DYNAMIC-VAR` | 通过（headless） | 计算型 Damage 变量按实际 `DynamicVar` 读取基础值，不再强转原版 `DamageVar`，且选牌估值不调用第三方实机求值器。runId `adf440d49b424b659d54c2187f4c5953`。 | 2026-08-31 |
| `FIX-0223-GC-COLLECTION` | 通过（headless） | 高血量、多候选固定根完成首次后台 Gen2 回收、No-GC `1 GB → 2 GB` 切换和 DOP1/DOP2 全字段等价。runId `e8c3222c43d842daa3e2e640155c8d3f`。 | 2026-08-31 |

## 0.22.2

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `FIX-0222-BOUNDARIES` | 通过（headless） | 同一快速夹具覆盖原生选牌页期间卡牌状态变化后的实体定位、出牌结束 Power 提交、免费能力牌遗物消耗、超质量体生成牌创建者、死亡后禁止继续施加 Power、敌方回合召唤怪初始行动与幻象复活，以及清空实机怪物状态机后仍使用根快照推进行动。runId `805266b49faa4435abaae7566edaed64`。未逐场运行本批 `18` 份完整战斗。 | 2026-08-31 |

## 0.22.1

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-225649-ROOT-SEMANTICS` | 通过（headless） | 验证新怪物生命判重读取模拟最大生命；直飞产卵虫已有蛋的模拟/原生最大生命刻意不同时，新蛋仍避开模拟占用值。同步验证 `WHISPERING_EARRING` 在第二回合不再自动出牌及 Fork 边界。runId `a5104a89fd614a8196c17ea86bc2f042`。 | 2026-08-30 |
| `BATCH-225649-SPEED-POTION-END-TURN` | 通过（严格差分） | 从已存在 `DEXTERITY_POWER:8 + SPEED_POTION_POWER:5` 的根开始，回合末原版与预测都回到 `DEXTERITY_POWER:3` 并移除速度药水 Power。runId `bc952a2f52314755b7be8f215e348349`。 | 2026-08-30 |
| `BATCH-225649-COMPACT-BUG-REPORT` | 导出结构通过；后续搜索等待超时 | 实际问题包 `71,470` 字节、`21` 个条目；断言只含当前战斗，没有截图、`saves/` 或 `forensics/recent`，保留战前内存存档和两组完整检查点。结构断言完成后，夹具在等待初始求解结果时达到 `120` 秒；不计整场通过。runId `4b0759b2552a429db4a723058788333b`。 | 2026-08-30 |

## 0.22.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR10-LARGE-DECK-FIXED576-A/B` | 通过（PR 固定工作量 A/B） | 基线与优化 artifact 都保持 `576` 展开、`3463` 转移、`1124` 选牌分支和同一 3 回合、预计战损 `2` 路线；`5116.3 ms / 1,039,502,640 B` 对 `3985.0 ms / 528,876,328 B`，累计分配降低 `49.12%`，单次 headless 墙钟缩短 `22.11%`。不替代可见 Steam 结论。 | 2026-08-30 |
| `PR10-FULL-DEEP-16GB` | 通过（PR 当前 artifact） | 精确进入 `16,000,000,000 B` No-GC 区域，保持 `7018/52644/23196` 工作量、评分、10 回合胜利、预计战损 `56`、卖血 `11` 与两瓶药路线；`53605.8 ms / 11,655,259,632 B`。 | 2026-08-30 |
| `PR10-NOGC-RNG-BUDGET-CONTRACT` | 通过（PR 当前 artifact） | 实际进入 `1 GB` 区域，令并发 `2 GB` 请求等待，释放旧 scope 后精确重建 `2 GB`；同时验证完整 RNG 身份及 DOP1/DOP2 结果全字段一致。runId `5677b8ccffc842d68f2199da964ed610`。 | 2026-08-30 |
| `PR10-SANITIZED-STRESS-FIXTURES` | 通过（PR 当前 artifact） | Silent `396` 张合成牌堆和 Necrobinder 最小战前投影均使用公开合成 seed，断言极高档原 Beam、节点、分支和精确 `16 GB` No-GC；runId `a2aef73ea38345a7b48418f7ff498ffc`、`07427794ff05455886fc7faf2318966e`。 | 2026-08-30 |
| `PR10-POTION-CHOICE-ALLOCATION` | 通过（PR 严格语义门禁） | 生成牌药水只克隆实际选中牌；17 项药水完整原版/预测差分 `17/17`，赌徒特酿专项通过。runId `341bf965156644c4a8fa6e3cd2399682`、`c5f1b95001f542d3b0d536295f914bdc`。 | 2026-08-30 |
| `PR10-CACHE-FORK-BOUNDARIES` | 通过（PR 当前 artifact） | 覆盖 Ritsu capability 缓存失效、选牌键跨 Fork、池化身份哈希、listener observer、所属牌堆、投影洗牌、稀疏 Power affliction 和 `CardPlay` 选择风险隔离。runId `bd24e4eeda0247f99eaac9fa90281e3b`。 | 2026-08-30 |
| `PR10-MERGED-POLICY-FORK-0220` | 通过（本地主线合并态） | 高血量、多候选固定根实际完成 No-GC `1 GB → 2 GB` 切换、DOP1/DOP2 全字段等价、节点上限快照释放、Fork 边界及完整自动战斗；第 6 回合结束，runId `26654574147d4925be016d7295fbee2a`。首次 `1 HP` 烟雾输入因没有形成并行工作量而被门禁拒绝，不计功能失败。 | 2026-08-30 |
| `PR10-VISIBLE-STEAM` | 未验证 | PR 没有可比较的当前 artifact 可见 Steam 性能结果；本轮不把 headless 单次墙钟写成生产帧率结论。 | 2026-08-30 |
| `PR11-THEME-OPACITY-LIFECYCLE-0220` | 通过（headless） | 验证新设置默认深色主题与 100% 不透明度，浅色/55% 设置可回读；切换主题会重建覆盖层、恢复设置页与当前搜索状态，并即时应用 65% 透明度。既有三页设置、通知、上传终态、预设持久化和搜索停止/恢复同时通过，首回合结束。runId `921055897c4c43ceb773c90c79eac953`；不替代 Steam 可见像素、拖动和动画检查。 | 2026-08-30 |
| `PR11-OPACITY-SLIDER-VISIBLE-RAIL-0220` | 待实机确认 | 透明度控件改为固定可见轨道、强调色已选区段和独立悬停拖动圆点；数值回读、即时应用和主题重建由上一项覆盖，像素观感与鼠标拖动留给本地可见游戏确认。 | 2026-08-30 |

## 0.21.7

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `SETTINGS-TABS-LIFECYCLE-NEXT` | 通过（headless） | 实际创建设置控件并验证“常规 / 性能 / 反馈”三页独立切换；通知“关闭 / 仅后台 / 始终”无损回读旧字段，预设重载不误判自定义，上传成功/取消终态和控制器停止/恢复链路同时通过。第 1 回合结束，runId `852a0f03f4724d6598212e309d11a2b4`；不替代人工视觉检查。 | 2026-08-30 |
| `NOTIFICATION-SETTINGS-LIFECYCLE-NEXT` | 通过（headless） | 验证通知默认开启且默认为“仅游戏不在前台”，关闭、仅后台和始终通知的决策正确；设置 UI 按持久化值加载。用户停止搜索产生一次结束通知请求，headless 不进入 Windows 原生调用。runId `99e510b870fc4ad6ab0611ce36f8f3b1` | 2026-08-30 |
| `WINDOWS-NATIVE-NOTIFICATION-ENTRYPOINT-NEXT` | 通过（系统声音量未检测） | 可见游戏日志确认旧入口连续 5 次抛出 `EntryPointNotFoundException`；显式绑定 `Shell_NotifyIconW` / `LoadIconW` 后，独立 Win32 窗口按与 Mod 相同的结构提交通知，返回 `shown=True / win32_error=0`。通知未设置静音标志，实际音量与是否播放由 Windows 通知策略决定。 | 2026-08-30 |

## 0.21.6

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `PR9-BUG-REPORT-FIFO-VERSION-FINAL` | 通过 | 同一最小战斗覆盖在线描述版本号、控制器自动分类、后台检查点 FIFO 与导出屏障；活动战斗问题包成功生成，结构化状态、RNG、五个牌堆、原生状态、即时跑局存档和 `solver_only` 控制模式断言均通过。runId `7065e4ebabd64bc69700ebf70d323e27` | 2026-08-30 |

## 0.21.5

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-193556-RNG-IDENTITY-FINAL` | 通过 | 人工构造计数相同、内部状态不同的战斗 RNG，续用文本和搜索状态键都能区分；固定 250 节点的 DOP1/DOP2 完整搜索政策继续一致。runId `8d6ae9a5647240a5846a2549cb06c369` | 2026-08-30 |
| `BATCH-193556-SOUL-NEXUS-TURN-SETUP-FALLBACK-FINAL` | 通过 | 从 `SOUL_NEXUS` 问题包战前存档进入赌博筹码原生页面；释放候选后的重建保留开局选择，8 秒短搜返回 6 回合路线，不再要求已经换走的启动流程仍在手牌。runId `1ec4e296e601422b86674f7f7d3d35df` | 2026-08-30 |
| `BATCH-193556-SEEKER-CHOICE-ACTION-SCOPE-FINAL` | 通过 | 同一张牌在一个 `CardPlay` 内能看到自己的待解决选择；开启新的 `CardPlay` 后不会继承上一次选择风险，Fork 边界检查同时通过。runId `7fcf4d2c016f40a6aeeaf6f314b9cf3e` | 2026-08-30 |
| `BATCH-193556-SEEKER-CHOICE-DIFFERENTIAL-FINAL` | 通过 | 探寻打击的随机候选、原生选牌、移入手牌与预测完整状态严格一致。runId `cac18e0431be4bc2a06b474da84460dc` | 2026-08-30 |
| `BATCH-193556-EXOSKELETON-RNG-BASELINE` | 部分，不计通过 | 问题包战前根在 20 秒单线程短搜下运行至第 2 回合后达到 120 秒总上限，没有到达原第 5 回合选牌失败，不能作为问题包整场复现或修复证据。runId `8655f27259fa4d0c822ebef9084f2eac` | 2026-08-30 |

## 0.21.4

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-184344-MAYHEM-STRATAGEM-CHOICE-FINAL` | 通过 | 花样百出自动打出微光，微光抽牌触发洗牌和战略选牌，随后继续微光自己的手牌选择；两层选择按原版顺序完成，牌堆和完整状态严格一致。runId `0583a87ba9504c54b3b9df92f40398b3` | 2026-08-30 |
| `BATCH-184344-ILLUSION-FIRST-MOVE-REVIVE-FINAL3` | 通过 | `FOGMOG` 召唤利齿之眼后登记其首次正式行动，再于行动前击杀；原版与预测均进入 `REVIVE_MOVE`，完整状态严格一致。runId `2c4c0640cedd45bbac812bab31de22ee` | 2026-08-30 |
| `BATCH-184344-PARALLEL-CLONE-FORK` | 通过 | 缩小甲虫固定根以 250 节点比较 DOP1/DOP2，动作、选择、评分、快照和回合标注一致且形成真实并发；同时验证 `PAELS_LEGION` 完成/中止出牌后都能清理瞬时引用并稳定 Fork。runId `9678ee908aac427e9fbc5a6d7b17e4cf` | 2026-08-30 |

## 0.21.3

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `BATCH-164623-TRANSFORM-ENTERED-COMBAT-CONTINUATION` | 通过 | 先打出一张虚无牌，再把手牌固定变换为费用随本场虚无牌变化的牌；原版与预测的变换入场、费用、打出后牌堆和完整状态严格一致，并启用增量核对。runId `c84456dc9b214cf7a7542579a8cd521f` | 2026-08-30 |
| `BATCH-164623-TURN-SCOPED-CARD-HISTORY-CONTINUATION-FINAL` | 通过 | 同一夹具覆盖两个回合：迭代在新回合重新响应首张状态牌；上一回合零费攻击不污染本回合施加的野性。原版与预测完整状态严格一致，并启用增量核对。runId `98adb2c4b3df4c65aed9cd3fb850512b` | 2026-08-30 |
| `BATCH-164623-ORB-DEATH-POWER-ORDER-CONTINUATION` | 通过 | 双巨斧机器人中，累计 `30` 伤害的暗黑球连续激发：第一击击杀后先触发另一只敌人的 CrabRage Power，第二击再消耗所得格挡；原版与预测完整状态严格一致。runId `99dc41bee83243cf9bad3c6b32826c0b` | 2026-08-30 |
| `BATCH-164623-LAGAVULIN-REUSE-FINAL2` | 超时，不计通过 | 从母体问题包战前存档恢复并使用原高预算；第 1 回合搜索未在 `360` 秒总时限内完成，没有进入自动部署，不能证明第 7 回合复用。runId `c4d32e6bde1d4e01a122dce45caa69b6` | 2026-08-30 |
| `BATCH-164623-LAGAVULIN-REUSE-SHORT` | 超时，不计通过 | 同一战前状态改用固定 `8` 秒短搜并启用增量核对；仍在第 1 回合达到 `180` 秒总时限，没有观察到第 7 回合复用。runId `a5edd7975b4645baa6dd8cde97bfe0d3` | 2026-08-30 |

## 0.21.2

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POST0211-ADAPTIVE-DOP-PRESET-PERSISTENCE` | 通过 | 默认并行度对 `1/2/3/4/32` 个逻辑处理器分别解析为 `1/2/2/4/4`；高档预设在设置页重新加载并提交未修改数值后仍保持高档。控制器生命周期和首回合自动战斗同时通过。runId `25fd0bdbeaa9450ca13ca6eebec74d95` | 2026-08-30 |

## 0.21.1

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `V0211-PARALLEL-OFF-RECOVERY-UI` | 通过 | 设置面板成功创建并行度选择框；并行搜索失败提示同时包含上传问题包与切换“关闭（单线程）”，串行搜索失败只提示上传，不误报并行恢复建议。控制器停止/恢复生命周期和首回合自动战斗同时通过。runId `598166aaeb19404fa75603c35a5921fa` | 2026-08-30 |
| `POST021-SMARTFORMAT-DIRECT-PATCH-ATTEMPT` | 失败实验，已撤回 | `LocManager.SmartFormat` 含异常过滤器，Harmony 无法生成 Prefix/Finalizer 或 Prefix-only 改写，CombatSolver patch 整体回滚；runId `f15cebdaa235481d970942bbdf5c7232`、`b8f16a2de9ea4779a738604ab8a247c9`、`0e0f3425cc104417bdf083ccf6cdcca0`，不计回归通过 | 2026-08-30 |
| `POST021-POWER-DYNAMIC-WARMUP-SMOKE` | 通过 | 主线程物化与 Power 惰性变量 guard 正常加载；铁甲战士首回合自动结束 1 HP 小爬虫战斗，没有 patch 回滚。runId `8b4446b6e4a34fa9b68bcdbd971441af` | 2026-08-30 |
| `POST021-INFESTED-PRISM-SMARTFORMAT-DOP4` | 通过 | 从玩家包恢复受感染棱镜战前存档、手牌与 RNG；DOP4 短搜约 `2976.8 ms` 返回 5 个可执行动作和第 4 回合路线，没有集合并发异常。runId `cb71d23a0baf4046b65dc0348c355041` | 2026-08-30 |
| `POST021-POWER-DYNAMIC-DOP1-DOP2` | 通过 | 固定长线根比较 DOP1/DOP2；动作、选择、评分、展开、转移、全部非时序剪枝、快照、continuation 与回合标注一致。runId `614d4ac8af3749cd92b9676662956dae` | 2026-08-30 |
| `POST021-BASELIB-PARALLEL-ENCHANTED-TERROR-EEL` | 通过 | 完整加载 BaseLib `3.4.5`，从玩家包恢复骇鳗首回合手牌、牌序、迅捷生存者、螺旋防御与 RNG；DOP4 短搜正常返回第 8 回合可执行路线，没有重复键异常。runId `ad3c8c31ef054a459a3f27dc9c45b16f` | 2026-08-30 |
| `POST021-BASELIB-ENCHANTED-DOP1-DOP2-EQUIVALENCE` | 通过 | 同一附魔牌根在 BaseLib 完整加载时比较 DOP1/DOP2；动作、选择、评分、展开、转移、非时序剪枝、快照、continuation 与回合标注一致。runId `258ec69dbc1b45c8bb6afa809623f4b6` | 2026-08-30 |
| `POST021-BASELIB-PARALLEL-ENCHANTED-TERROR-EEL-FULL-AUTO` | 通过 | 玩家包根以 DOP4、Instant/0 秒完成整场自动部署，第 8 回合结束，计划外重算 `0`，没有重复键异常。runId `75cbc36d31d045778a72fa3acf60c080` | 2026-08-30 |

## 0.21.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `POST0201-SCRAPE-NEGATIVE-COST-FINAL` | 通过 | 刮削+依次抽到普通费用牌与负费用贪婪；预测与原生的手牌、弃牌堆及其余完整状态严格一致。runId `a5117c6888d0438693c0334775c720e1` | 2026-08-30 |
| `POST0201-WHISTLE-STUN-FOLLOW-UP-FINAL` | 通过 | 吹哨打断史莱姆狂战士的呕吐脓水；眩晕结束后实机与预测都恢复呕吐脓水，不再跳到狂怒痛击。runId `500c0e56d7fb4dd58977040a6dd92610` | 2026-08-30 |
| `POST0201-BRAND-POST-CHOICE-POWER-FORK-FINAL` | 通过 | 升级烙印完成原生手牌消耗选择并获得力量后，模拟状态立即满足稳定 Fork 边界。runId `7ea11c09cf614e28922104cf10875697` | 2026-08-30 |
| `POST0201-SLIMED-BERSERKER-WHISTLE-REUSE` | 通过 | 从史莱姆狂战士问题包恢复牌组、遗物与 RNG；严格增量搜索实际打出吹哨，连续复用到第 3 回合，计划外重算 `0`。runId `e6232efae6494f2cb91b14d6790c3c23` | 2026-08-30 |
| `EXHAUST-CLUTTER-SENTINEL` | 通过 | 贪婪在第 1 回合消耗一张手牌；候选含虚无与两张防御。用于确认移除排序与牌库杂物计分改动后路线不退化，贪婪仍在第 1 回合打出且随后仍能打出防御。本场景是不可退化哨兵，不是行为翻转证据——基线在同一夹具上已经选择消耗防御。 | 2026-09-05 |
| `POST0201-TEST-SUBJECT-SCRAPE-SCAVENGE-REUSE` | 通过 | 从实验体问题包恢复牌组、遗物与 RNG；第 2 回合打出刮削，内存清理随后在 4 张原生手牌候选中成功选中贪婪，严格增量复用到第 3 回合且计划外重算 `0`。runId `ec1c11fa2a444eda836520d1fe18e829` | 2026-08-30 |
| `POST0201-DECIMILLIPEDE-CURRENT-DEEP-BASELINE` | 部分，不计完整通过 | 当前源码从千足虫问题包根完成搜索，没有复现旧版待结算力量导致的 Fork 异常；结果只有死亡路线，测试因可执行动作下限断言失败，因此不作为完整战斗证据。runId `dbf082665e984821b835e665889cc168` | 2026-08-30 |
| `MULTICORE-NIBBITS-DOP1/DOP2-AB` | 通过（headless 迭代基准） | 固定双小啃兽快照在上游 DOP1、当前 DOP1、当前 DOP2 均为 `573` 展开、`2759` 转移、同一 5 回合零战损/零药路线。独立冷进程求解耗时 `1424.5 / 1449.3 / 955.2 ms`，当前 DOP2 缩短约 `34.1%`；累计分配 `177,425,048 / 177,767,800 / 179,165,392 B`，DOP2 并行遥测为 `286 waves / 572 items / max concurrency 2`。runId `e8bfb02cf9714c98ae9230842c756ff3`、`9314522060164435b551c18f16d7d093`、`ca238f1462874e7d8c67069b4b71077c`；不替代 Steam 可见性能门槛 | 2026-08-30 |
| `MULTICORE-POLICY-EQUIVALENCE` | 通过 | 带初始力量与 `SURVIVOR` 弃牌选择的固定根先执行冷缓存 DOP2，再执行 DOP1；递归核对完整动作/选择、评分、展开、转移、各类剪枝、快照、continuation 与回合标注。DOP1 并行遥测全零，DOP2 实际最大并发不小于 2。runId `3f8d240bda57441c8352bfb749424017` | 2026-08-30 |
| `MULTICORE-FULL-AUTO-DOP2` | 通过 | 默认并行路径完成双小啃兽整场自动部署，第 5 回合结束，第 3 回合精确复用；零药、零预计战损。runId `eda5d69feb774389a8990d788434ac6d` | 2026-08-30 |
| `MULTICORE-EARRING-NESTED-CHOICE-DOP2` | 通过 | 工具盒形成首回合多根，低语耳环连续自动打出高密度 `SURVIVOR` 并消费嵌套弃牌选择；精确原版状态检查通过，搜索记录 `4 waves / 8 items / max concurrency 2`。runId `f577be1a8e0a4ebb84aa115d3ab28734` | 2026-08-30 |
| `MULTICORE-NIBBITS-DOP4` | 通过 | 四条 lane 完成固定双小啃兽搜索，仍为 `573 / 2759`、同一路线与全部剪枝指标；并行遥测 `159 waves / 572 items / max concurrency 4`。runId `54243577aa984e8eb68f2d242a216eb9` | 2026-08-30 |
| `MULTICORE-INCREMENTAL-FORCED-SERIAL` | 通过 | 请求 DOP4 并开启严格增量回放；首轮完整结果的 `parallel_waves / work_items / max_concurrency` 均为 `0`，逐转移回放一致，第 5 回合结束且计划外重算 `0`。runId `96b7d9fdfbb245d68f4effefcd748b1e` | 2026-08-30 |
| `MULTICORE-V020-FINAL-POLICY-EQUIVALENCE` | 通过 | 合并 `upstream/main` 的 `v0.20.0` 后，以固定 250 节点先跑 DOP2 再跑 DOP1；动作、选择、评分、展开、转移、全部非时序剪枝、快照、continuation 与回合标注一致。门禁同时断言两档都实际释放节点上限丢弃的 Simulator；runId `c9052d90aa504ae0ba183a5089aa0e07` | 2026-08-30 |
| `MULTICORE-V020-FULL-AUTO-DOP2-FINAL` | 通过 | 合并态默认 DOP2 完整自动部署双小啃兽，第 5 回合结束、第 3 回合精确复用，零药、零预计战损、计划外重算 `0`；runId `f48b0cebd842466594bd8f30789d589a` | 2026-08-30 |
| `MULTICORE-MECHA-DOP4/6/8-WARM-NOCACHE` | 通过（headless 迭代基准） | 同一暖进程固定 `4319 / 33087 / 18399` 工作量与第 7 回合/28 战损路线；DOP4/6/8 为 `5451.7 / 4281.3 / 3813.0 ms`，累计分配为 `4,404,184,848 / 4,412,016,024 / 4,415,450,000 B`。runId `db33a20aa0764f068b34a3028ec06beb`、`eab8b0f052634b82be807b3af9ddaec9`、`38e6aed2c8834a4fa0cea8a35e18b6f5`；不替代 Steam 可见性能门槛 | 2026-08-30 |
| `MULTICORE-V020-MECHA-DOP8-FINAL` | 通过（headless 冷进程） | 当前合并态 DOP8 保持 `4319` 展开、`33087` 转移、`18399` 选牌分支与同一路线；`6366.8 ms / 4,441,356,192 B`，实际最大并发 `8`，结果时工作集 `6,011,162,624 B`，GC `0 ms`、最大帧 `18.0 ms`、无 `>50 ms` 帧。runId `2c38044b3fb44bef85b66032b488271c`；可见 Steam 启动未形成游戏进程，故不写成生产帧率结论 | 2026-08-30 |
| `PR8-MERGED-DOP1-DOP2-EQUIVALENCE` | 通过 | PR #8 合并到本地主线后，以固定 250 节点比较 DOP1/DOP2；动作、选择、评分、展开、转移、全部非时序剪枝、快照、continuation 与回合标注一致，两档都实际释放节点上限丢弃的 Simulator。runId `c4e1343ad44843229483f97e3d04265a` | 2026-08-30 |
| `PR8-MERGED-DOP2-FULL-AUTO` | 通过 | 合并态默认 DOP2 完整自动部署双小啃兽，第 5 回合结束、第 3 回合精确复用，计划外重算 `0`。runId `21f6375fb9d5499a99d992c2eb7a0878` | 2026-08-30 |

## 0.20.0：在线问题包、跨平台测试与选牌修复

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `LINUX-HEADLESS-INSTANT-AB` | 通过 | 同一 PID `66703` 先后运行 `Normal / Instant / Normal`；内部耗时分别为 `7098.0 / 2917.7 / 7064.5 ms`，Instant 稳定节省约 `59%`。三次均应用并恢复测试速度，runId `e8dede980dbb4daab57cc1c6a1d71730`、`8851e4147f9940e2921088474c8c7e5f`、`42148ebf930243b59d9230d2cb68f913` | 2026-08-29 |
| `LINUX-HEADLESS-REUSE-REGRESSION` | 通过 | 同一 PID 连续通过怪物严格差分、铁甲战士跨回合复用、跨角色切换到星辰、再切回铁甲战士完成精灵药自动战斗；对应 runId `74da3eefb1e841219c5721323dad83d6`、`dbd5cf92f61043b28d38978aa8307a6e`、`8605ff494b604c019323e222f5247314`、`261c9007073d40709a5f388d698063e9`，两项复用场景和完整战斗的计划外重算均为 `0` | 2026-08-29 |
| `LINUX-HEADLESS-LIFECYCLE` | 通过 | 原生日志为 `N/A (headless) / VRAM 0B`；marker 验证 PID starttime、隔离环境及 DLL/manifest 哈希。无变化复用同一 PID；Release 重建后输出 `UNATTENDED_RESTART reason=mod_changed` 并自动换 PID。失败退出约 `510 ms`，最终进程、marker、临时 RitsuLib 投影均清理 | 2026-08-29 |
| `HEADLESS-MATRIX-CANONICAL-0180` | 通过 | Linux 以 `--continue-on-failure` 按文档原有生命周期边界运行全量矩阵：`MATRIX_END total=228 attempted=225 passed=225 failed=0 skipped=3 cleanup_exit_code=0 elapsed_ms=1787606`，即 `29:47.606`；`52` 次冷启动、`173` 次安全复用，仅跳过缺少本机外部快照的 `3` 个场景 | 2026-08-30 |
| `LINUX-MECHA-MEMORY-CALIBRATION-0180` | 通过 | 固定机甲骑士快照在 Linux 原生 headless 的首轮搜索为 `14282.3 ms / 4,390,908,424 B`（累计分配，非峰值内存），第 `7` 回合结束、第 `3` 回合开始复用，runId `531a3a280ab24e89bad2a3536da8ecd6`。Linux 分配门槛按平台差异校准为 `4,500,000,000 B`，余量 `109,091,576 B`（`2.485%`）；Windows 命令的 `4,300,000,000 B` 门槛保持不变 | 2026-08-30 |
| `PR6-ARMAMENTS-IMPLICIT-INTEGRATED` | 通过 | 手牌仅有武装和未升级打击；原版隐式升级唯一候选后，部署器按请求时冻结的身份核销同一实例，再打出升级后的打击并于首回合结束战斗。原生选择 `visible=0 / selected=1 / search=0`，增量回放一致，计划外重算 `0`。runId `d1bc59759edf4c91a9c89f4bd6e6b2d4` | 2026-08-30 |
| `PR6-RNG-DETERMINISTIC-A/B` | 通过 | 同一 PID、相同 seed `PR6DETERMINISTIC` 连续两次建立史莱姆战，敌人均为 `LEAF_SLIME_S / TWIG_SLIME_M / TWIG_SLIME_S` 且生命上限为 `12 / 27 / 7`；两次均第 2 回合结束、计划外重算 `0`，第二次明确复用同一测试进程并正常退出。runId `cc320b89536149398707300e4c9258be`、`38c0fde339ae49e0b45524d3ce2c545d` | 2026-08-30 |
| `PR6-VIGOR/SELF-KILL-INTEGRATED` | 通过 | 骇鳗猛烈摆动携带活力时，完整攻击前/攻击后生命周期与原生严格一致，runId `0eac2a0e2d904b8bb332fb1c76c18d57`；七项怪物行动差分含毒气炸弹自爆并通过死亡结算，runId `edd957f4731445e9a300f9c0cad9646f` | 2026-08-30 |
| `PR6-EMOTION-CHIP-EXTRA-TURN-INTEGRATED` | 通过 | 琥珀灰触发额外回合；当前回合使用放血受伤后，情感芯片的损血窗口在跳过敌方阶段时正确滚动，额外回合开始的充能球被动与原生完整状态一致，实际伤害 `3`。runId `aaa8223a916b4b24b8c2983596010e31` | 2026-08-30 |
| `TOASTY-FIRST-TURN-USER-BOUNDARY-0190` | 通过 | 烘焙手套原生手牌页显示后开始搜索；计划就绪时仍为 `Selected=0 / CardsPlayed=0`，模拟玩家启动后才确认选择。严格增量/完整回放一致，开启结束回合变差复核后完整自动执行到第 2 回合，计划外重算 `0`。runId `a2a1fd688e71465d9458c5cbb1c743d4` | 2026-08-30 |
| `TOASTY-PHANTASMAL-BUNDLE-0190` | 通过 | 使用花园幽灵鳗问题包的战前牌组、遗物与 RNG；首回合计划展示时未选牌、未出牌，玩家启动后完整自动执行到第 5 回合。结束回合复核开启，计划外重算 `0`；严格完整回放从同一份首回合准备选择起步。runId `0451b77331a94c96ae507e2ccdb4603a` | 2026-08-30 |
| `UPLOAD-HARDENING-PR4-FINAL` | 通过 | 真实问题包导出保持完整夹具且移除联系QQ与本机绝对路径；本地假服务验证 multipart 三字段不变、字节进度到 `100%`、非 JSON/数字编号的成功响应回退客户端提交编号、超长描述联网前失败、超长错误响应截断并折叠换行。设置面板同时存在隐藏初始进度条与单实例上传按钮状态。导出/脱敏与上传协议 runId `8a4144cb3a264bb7abf33ea6461ddcb7`；最终 UI/进度状态 runId `c90d6b854c3446ffad4c09b4da1753bf`；最终成功响应兼容矩阵 runId `85861ebc16a04cb09fbd9894bfb3d088` | 2026-08-30 |
| `UPLOAD-PROGRESS-CANCEL-CONFIRMATION-NEXT-FINAL` | 通过 | 取代上一条中“任意成功响应回退客户端编号”的旧口径：文件发送完成只显示到 `95%` 并进入“等待服务器确认”，只有反馈编号与实收字节数匹配才确认成功。假服务分别在正文传输中和等待回执时取消，任务均在两秒内结束；无效回执与大小不一致均保留本地包。真实接收服务 test ZIP 返回 HTTP `201`、反馈编号 `9264c0f65854423e8254de5ff5e5449f` 并确认 `259 B`。runId `f328c2f5fe9f4ccca11826bcbb8b1f6c` | 2026-08-30 |
| `UPLOAD-DIRECT-STATE-OWNERSHIP-NEXT` | 通过 | 正式上传不继承游戏进程中指向失效 `127.0.0.1:7890` 的代理；同一环境下直连 test ZIP 于 `427 ms` 返回 HTTP `201`，反馈编号 `d186e8a6495d4f0291529595667e0a43`，实收 `259 B`。孤立状态转移夹具验证活动态与按钮文字可在同一主线程回调内切回空闲；后续实机证明该全局回调本身可能不被消费，最终实现由下一条面板完成邮箱回归取代。runId `ba1c33c4e68946129314dff1a61928cf` | 2026-08-30 |
| `UPLOAD-PANEL-MAILBOX-LIFECYCLE-NEXT` | 通过 | 实机已经记录 HTTP `201 / 1,340,897 B` 后仍卡等待，证明全局 dispatcher 未消费上传终态。上传会话改由设置面板完成邮箱独占；成功与取消两条路径都在面板进程中消费终态、收起进度条、释放令牌并恢复空闲按钮，“正在取消…”不再等待搜索 dispatcher。runId `fa5ba87bf06d4dac9c17b052192d0be8` | 2026-08-30 |
| `KNOWLEDGE-LIVE-END-RISK-BASELINE` | 失败（修复前基线） | 开启结束回合实时战损复核后，知识恶魔敌方回合诅咒计划被放入普通选牌游标；真正提交结束回合前稳定抛出“回合开始仍有 1 个计划选牌没有触发”，随后测试超时。runId `bc12f0e513ec4634b5c2f3dea0f84a66` | 2026-08-30 |
| `KNOWLEDGE-LIVE-RISK-CHOICE-PHASE-NEXT` | 通过 | `MONSTER-MOVES-BATCH-007` 在既有 10 项严格差分后，额外强制知识恶魔诅咒行动并向实时战损复核提供 `MIND_ROT` 计划；复核按来源和次数消费该计划，诅咒计数精确前进 `1`。runId `901066cdbaaa41329876325cd8a06ad5` | 2026-08-30 |
| `KNOWLEDGE-LIVE-END-RISK-FIXED` | 通过 | 开启结束回合实时战损复核，首轮路线计划 `MIND_ROT`；提交结束回合后原生页面完成选择、玩家获得对应 Power，计划外重算 `0`。runId `a43e0dc90cad444989efde50a99ba33b` | 2026-08-30 |
| `KNOWLEDGE-LIVE-END-RISK-INCREMENTAL` | 通过 | 与上一项相同的实时战损复核路径同时开启严格增量校验；初始搜索、完整回放、结束回合后的原生 `MIND_ROT` 选择一致，计划外重算 `0`。runId `b199bf29f0054c1789e1a2c2d2886435` | 2026-08-30 |
| `KNOWLEDGE-ANGER-BUNDLE-FIXED` | 通过 | 从铁甲战士问题包恢复战前存档、精确牌堆和 RNG；完整自动执行到第 6 回合结束，实机打出愤怒并通过两次知识恶魔原生选牌，计划外重算 `0`。runId `28ac269f1be64674976a8d5075965947` | 2026-08-30 |
| `KNOWLEDGE-TOASTY-BUNDLE-FIXED` | 通过 | 从静默猎手问题包恢复战前存档、精确牌堆和 RNG；开启实时战损复核后完成首个知识恶魔原生选牌并获得瓦解，计划外重算 `0`。runId `9d988afbd8f240e3af519755d35aff3b` | 2026-08-30 |

## 0.19.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `MONSTER-ATTACK-VIGOR-LIFECYCLE-POST018` | 通过 | 骇鳗攻击执行完整攻击前/攻击后生命周期，活力层数与原生严格一致。runId `9994be6016f345f892c6dbc3040384e2` | 2026-08-30 |
| `POST018-TERROR-EEL-CONTINUATION` | 通过 | 骇鳗完整自动执行到第 6 回合，跨回合计划外重算 `0`。runId `acd9c91efb7443edb5a19737d7aba92b` | 2026-08-30 |
| `POST018-TERROR-EEL-CONTINUATION-INCREMENTAL` | 超时，不计通过 | `360s` 上限内仍停留于首回合，没有完成断言；不作为增量等价证据。runId `7c53821b28fd4f84b337b673d9507adb` | 2026-08-30 |
| `POST018-FABRICATOR-CHOICE-AND-AI-REUSE` | 通过 | 条件行动包含自身的队友计数；暴政选择按玩家回合开始阶段接管，完整自动执行到第 9 回合且零重算。runId `766852f4a7574342959cdf9ca7e940b0` | 2026-08-30 |
| `POST018-DECIMILLIPEDE-UPGRADE-CHOICE-DEPLOY` | 通过 | 千足虫升级选牌完成原生部署，完整自动执行到第 4 回合且零重算。runId `dc190ef3c1784f27b0d9e2ef85f49b80` | 2026-08-30 |
| `POST018-OVICOPTER-CONTINUATION` | 通过 | 产卵飞虫完整自动执行到第 5 回合，计划外重算 `0`。runId `c8e98d81c2c24c7d9de1baeb2d67e6ce` | 2026-08-30 |
| `POST018-BYGONE-EFFIGY-TOOLS-CHOICE` | 通过 | 必备工具选择按下一玩家回合阶段消费，完整自动执行到第 7 回合且零重算。runId `1dbadbe59d9147879d62f44d38a55cf0` | 2026-08-30 |
| `POST018-KNOWLEDGE-ANGER-END-RISK` | 通过（历史邻接覆盖） | 完整自动执行到第 8 回合且零重算，但该场景没有让实时战损复核与知识恶魔敌方回合选择同时进入同一个模拟根，不能覆盖本次问题；由下一版本的实时复核专项取代。runId `2612caee23e14c42a274aa7567ae2251` | 2026-08-30 |
| `POST018-SCROLLS-AUTO-CHOICE-PHASE` | 通过（邻接覆盖） | 三卷轴怪当前路线首回合结束战斗，自动执行不中止且零重算；第 2 回合必备工具由独立阶段回归覆盖。runId `a9f7d23ea7d5439c97a78ddf327d520f` | 2026-08-30 |
| `POST018-SOUL-NEXUS-GRID-SCROLL` | 通过 | 原生 37 张卡牌网格滚动到底部并通过真实节点选择主宰，完整自动执行到第 7 回合且零重算。runId `b1a13ffbde4c46a6b64825cb2a3e8049` | 2026-08-30 |
| `POST018-OVERGROWTH-ENTROPIC-CONTINUATION` | 通过（未复现旧重算） | 从蔓生爬虫问题根完整执行到第 2 回合且零重算；旧包在搜索期间实机药水栏与 RNG 已变化，因此保留为证据不足。runId `17fb59dfc1284928912eba1644b1ead5` | 2026-08-30 |
| `POST018-TEST-SUBJECT-LOOT-CURRENT` | 通过 | 满手后的战利品生成与后续回放完整执行到第 12 回合，计划外重算 `0`。runId `7d95638f6d6f4a058221cb3507c05187` | 2026-08-30 |
| `POST018-AEONGLASS-CHOICE-CURRENT` | 通过 | 永世沙漏原生选牌与重新接管完整执行到第 6 回合，计划外重算 `0`；更优路线仍属暂缓项。runId `c51d8cf8f47e43ccb7e4a922361688d8` | 2026-08-30 |
| `POST018-ENTOMANCER-CLUMSY-CONTINUATION` | 通过 | 养蜂人塞入笨拙后的牌堆和洗牌续用一致，完整自动执行到第 4 回合且零重算。runId `066e8d5e64ff4eb3a2b0b2e8c4853685` | 2026-08-30 |
| `POST018-KNIGHTS-FAILURE-CURRENT` | 通过（入口覆盖） | 当前路线首回合结束，最终回放入口不再失败且零重算；不宣称复现旧 9 回合路线。runId `d19d34b506cd4b67a863e124293bfaf1` | 2026-08-30 |
| `POST018-DOMINATE-VICIOUS-ORDER` | 通过 | 主宰施加易伤后，凶恶先抽牌、地狱狂徒先自动打出攻击，随后才按当前易伤获得力量；原生与预测完整状态一致。runId `ad8f66a8594d436dac6a1c5fafb0e313` | 2026-08-30 |
| `POST018-HEX-DEATH-COVERAGE-FINAL` | 通过 | 两只幽灵骑士连续施加恶咒，后施加者死亡保留、初始施加者死亡移除；Power 与逐张卡牌状态三段差分一致。runId `d53826f1d06a487fbca60ffb740892f7` | 2026-08-30 |
| `POST018-CUSTOM-OVERRIDE-ASSERTION` | 通过 | 中档基础上覆盖短搜/深搜单节点出牌分支为 `23/37`、No-GC 为 `7 GB` 后，预设身份与三个实际值均按自定义配置断言。runId `011e9c0e7daa4564867e8195a6299a11` | 2026-08-30 |
| `POST018-BYRDONIS-EXACT-DEFERRED-BASELINE` | 通过（质量基线） | 从问题包回放状态恢复精确牌堆与 RNG，当前路线预计战损仍为 `55`，玩家手操上界为 `41`；只固化差距，不计作策略修复。runId `b4e2837cecc04cb59dad6a4e4be6cb37` | 2026-08-30 |
| `POST018-TEST-SUBJECT-EXACT-DEFERRED-BASELINE` | 通过（质量基线） | 从问题包回放状态恢复精确牌堆与 RNG，当前路线预计战损仍为 `20`，玩家手操上界为 `0`；只固化差距，不计作策略修复。runId `cad36ba7007d40e392ee882c49be2308` | 2026-08-30 |
| `POST018-DETAILED-PLAN-REPLAY-STATE` | 通过 | 详细诊断在最终路线回放与实机部署动作后写出能量、手牌、抽牌堆、弃牌堆、消耗堆及敌方生命/格挡；最小战斗完整结束。runId `1e83a3bc0d864e278fba5a8a20d66b96` | 2026-08-30 |

## 0.18.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `MAKE-IT-SO-FINISHED-HISTORY-018` | 通过 | 独立回合从 0 次技能历史开始，逐张断言如此甚好在前两张技能后留在弃牌堆、第 3 张后回手，实机与模拟一致。runId `5d226c100ea949f1bc498a01a7961106` | 2026-08-29 |
| `NEUROSURGE-MUTABLE-POWER-018` | 通过 | 精神过载及同批 47 项卡牌施加 Power 后，生命、能量、牌堆、Power 和怪物状态实机差分一致。runId `fa74bbbe3c4245188cde369d7bcf6144` | 2026-08-29 |
| `LIVE-END-TURN-RISK-CHOICE-REUSE-018` | 通过 | 惊逃在结束回合风险复核中自动打出头槌，复用路线选择将盛怒置顶，选择顺序与消费数严格一致。runId `216d8643891f442689074fa5f6f7954e` | 2026-08-29 |
| `FOREGONE-CONCLUSION-DELAYED-DRAW-018` | 通过 | 既定事项页面暂停回合准备时，先执行原版 `AfterSideTurnStart` 再确认选牌；下回合多抽一张在正式抽牌前移除，选中 3 张后总手牌为 8。实机与模拟严格一致。runId `a65c0460238c467c803394b5c07a59c5` | 2026-08-29 |
| `OWL-MAGISTRATE-TURN-SETUP-REUSE-018` | 通过 | 从猫头鹰法官问题包战前状态完整自动执行，既定事项、辉光和回合准备选牌跨回合保持一致，第 5 回合结束前计划外重算 `0`。runId `cd2f163cd0cd41f5af6183fe9b4fec5c` | 2026-08-29 |
| `SPECTRUM-FOREGONE-ORDER-018` | 通过 | 光谱偏移先生成随机无色牌，既定事项随后把 3 张牌移入手牌，再执行普通抽牌；有序牌堆、Power 和 RNG 与实机严格一致。runId `96f54d71babf4c3ba4871dd5400953a8` | 2026-08-29 |
| `KNOWLEDGE-POWER-REAPPLICATION-ORDER-018` | 通过 | 从知识恶魔问题包战前状态完整自动执行；敌方诅咒选择会话正常退出，第 11 回合重新施加既定事项后保持正确 Power 监听顺序，第 12 回合结束战斗，计划外重算 `0`。runId `bdf89c2bf77341ee8da1c7f68b2d2161` | 2026-08-29 |
| `CURRENT-BUNDLE-DIRECT-COVERAGE-018` | 通过 | 当前源码直接重放地道虫、外骨骼虫、感染棱柱、活体盾与高塔炮手，以及连枷骑士、幽灵骑士与魔法骑士问题包，分别越过原初始化、计划外选牌、实时风险选牌、精神过载动作回放及如此甚好部署找牌错误；runId `edce410703414934a3f3259429b10d26`、`6a32338eb2f24b44aa45cf731c845163`、`417c1b5707924606816351b1aa339c21`、`b2d4cb91395e46ccb944da5a8558192b`、`0d737a4acb9e4a85a9365f296562311c` | 2026-08-29 |
| `POWER-ROOT-INTERNAL-STATE-018` | 通过 | 鬼祟珊瑚群第 2 回合继承本回合已受到的 `9` 点伤害，搜索与实机的回合伤害上限一致；完整自动战斗在第 5 回合结束，计划外重算 `0`。runId `b126492a542c4c3d85bc47f0bffe0b1c` | 2026-08-29 |
| `EMOTION-CHIP-HISTORY-ROLL-018` | 通过 | 情感芯片触发充能球后保留上回合失去生命的记录，直到敌方回合结束再滚动；完整自动战斗在第 5 回合结束，计划外重算 `0`。runId `e265c0a6887f48ea892c2e5489236721` | 2026-08-29 |
| `FAN-OF-KNIVES-SHIV-TARGET-018` | 通过 | 刀扇生效后，小刀按全体攻击生成无目标动作并与实机一致；永世沙漏问题包完整自动执行到第 7 回合，计划外重算 `0`。runId `e825a7e0fe244d5b8be45086c3f3d7ef` | 2026-08-29 |
| `UPROAR-ECHO-FORM-AUTOPLAY-018` | 通过 | 回响形态重放骚动时，骚动自动打出的集中打击读取已经开始的外层出牌系列，因此只结算一次；敌人生命、集中、牌堆、能量与 RNG 的原生/预测完整状态一致。runId `06550cd755b246c7b044865469541422` | 2026-08-29 |
| `TEST-SUBJECT-GC-ECHO-FINAL-018` | 通过 | 实验体原问题包在首轮搜索与全自动请求重叠时只执行一次 No-GC 滚动回收，不再循环触发 `before_next_search`；随后连续复用并在第 8 回合结束，计划外重算 `0`。runId `913d3393e919438fbf2d7635ce318b2b` | 2026-08-29 |
| `DECISIONS-REPEATED-CHOICE-BUDGET-018` | 通过 | 抉择，抉择的三次自动出牌共享整张牌的手牌选择分支预算；储君实验体原包首轮短搜返回后连续精确复用 10 回合，第 11 回合结束，计划外重算 `0`。runId `c8c9f6f2edda40bd87bc2bf5e6b20520` | 2026-08-29 |
| `TURRET-RELIC-ANNOTATION-018` | 通过 | 活体盾与高塔炮手原问题包的首轮最终遗物标注正常完成；完整自动执行到第 4 回合，计划外重算 `0`。runId `6d1e48b181824f0fbebc43525c959403` | 2026-08-29 |
| `MECHA/SOUL-NEXUS-REPLAN-018` | 通过 | 机甲骑士与静默猎手对灵魂枢纽的两份原问题包分别完整自动执行到第 4、5 回合，计划外重算均为 `0`。runId `cb31ec9272284f579bbf6efa942281e0`、`2b11153f74af4766848d585e8372d62f` | 2026-08-29 |
| `WATERFALL-SMART-MARGINAL-POTION-018` | 通过 | 瀑布巨兽原问题包的智能用药路线只保留痊愈药水；独立无药反事实确认预计省血 `10/9`，再生药水不再借用另一瓶药水的收益通过门槛。runId `dbce824e7919490882bd6022f2ebc394` | 2026-08-29 |
| `MYTES-SMART-BLOCK-POTION-018` | 通过 | 异螨原问题包在第 2 回合实际使用格挡药水，完整自动执行到第 5 回合，预计省血 `9/9`，计划外重算 `0`。runId `1959c1dd79c743958a6f921f727d6cae` | 2026-08-29 |
| `LOST-FORGOTTEN-REQUIRED-POTION-BOUNDARY-018` | 通过 | 失落之物与遗忘之物原问题包在“至少使用一瓶”下正常返回一瓶药水的边界路线，不再把已展开的流动铜液与能力药水误报为没有可执行路线；该短预算结果仍为死亡边界。runId `d3fa5886b45e46948093c0d42518f79d` | 2026-08-29 |
| `QUEEN-POTION-POLICY-DISABLED-018` | 通过 | 女王原问题包全程记录“禁用药水”，路线按设置保留稳定血清与固化药水；玩家手动使用稳定血清后，本局已用药数正确增加。该项是设置行为，不是药水适配失败 | 2026-08-29 |
| `TEST-SUBJECT-REQUIRED-POTION-QUALITY-018` | 通过 | 实验体同一起点、高档预算成对复跑：智能模式 0 瓶、预计战损 `78`；至少使用一瓶时选择肌肉药水、预计战损 `74`，不再发生强制用药后战损上升。runId `12dc53ed6dc1469d9e0bae71a1b14b2e`、`6a4b981c9c7c4fb7999304980c33f00c` | 2026-08-29 |
| `INSATIABLE-REQUIRED-POTION-BOUNDARY-018` | 通过 | 无厌沙虫原问题包在“至少使用一瓶”下返回包含第 4 回合易伤药水的边界路线，不再因长战斗尚未搜索到完整胜利而误报没有可执行路线；该结果仍为死亡边界。runId `6563e660595d4b35932719281320b5c3` | 2026-08-29 |
| `HISTORY-COURSE-STAMPEDE-PAELS-EYE-018` | 通过 | 历史课在惊逃的回合末自动攻击之后记录上一回合最后一张非复制攻击牌；佩尔之眼只统计玩家主动出牌。永世沙漏原问题包完整全自动执行到第 5 回合，包含额外回合，计划外重算 `0`。runId `0b1d38c391d04a36a7a9cc14c5122d5f` | 2026-08-29 |
| `AEONGLASS-EXACT-PILE-ART-ROUTING-018` | 通过 | 从永世沙漏问题包战前存档恢复跑局与 RNG，并固定首手和 29 张有序抽牌；路线实际打出灵动步法+，预计战损 `27`，低于包内原路线的 `42`。runId `0c2c35a7510b4e0f9846fca740aa80c1` | 2026-08-30 |
| `QUEEN-ART-OF-WAR-LANE-REGRESSION-018` | 通过（邻接回归） | 女王战前重建在中档预算、智能用药下预计战损 `18`，低于回归上限 `26`；该场景只证明孙子兵法专用通道没有影响无关战斗，不作为第 18 项“更优解”的同根证明。runId `8d69269258924485aef31a0325d1d3c1` | 2026-08-30 |
| `QUEEN-EXACT-PILE-MANUAL-QUALITY-018` | 通过（未追平手操） | 固定女王问题包的 7 张首手和 32 张有序抽牌后，当前路线主动打出余像与计划妥当，预计战损 `9`；包内旧求解路线为 `26`，玩家手操路线实测为 `5`。runId `598bc5d8e86b4094bbf96c04942e192e` | 2026-08-30 |

## 0.17.2

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `QOL-CONTROLLER-STOP-172` | 通过 | 搜索时主操作按钮为“停止计算”；停止后当前搜索取消且自动回合入口不能重启，点击“重新计算”恢复。手操预计战损 `7 -> 3` 时记录差值并显示绿色反馈；消息区域启用整行自动换行。runId `bbbffd3cf1cd4d8686472175a44ed64e` | 2026-08-29 |
| `PERFORMANCE-PRESET-LOW/MEDIUM/HIGH/VERY-HIGH-172` | 通过 | 四档固定解析依次为 `5/60s + 6GB`、`8/120s + 8GB`、`12/180s + 12GB`、`20/300s + 16GB`，Beam、节点与出牌分支均匹配规格并完成首回合战斗。runId `c18b796053064ffb89eebde8da49fa69`、`516c303b65d547fb9e60fa34d79ca3b5`、`ce61ed362f5143fc9d69ee8b9763eb2c`、`9791b831f1ac4b6ca296fe28811f81c4` | 2026-08-29 |

## 0.17.1

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `SLY-AUTOPLAY-NESTED-CHOICE-0171` | 通过 | 手牌戏法给杂技附加奇巧，生存者弃掉杂技后触发自动出牌与杂技自身弃牌；有序牌堆、逐牌状态、Power、RNG 和续用状态严格一致。runId `97af4be8cb104d15baac75fe1e4c3701` | 2026-08-29 |
| `TRIGGERED-SHUFFLE-CHOICE-ORDER-0171` | 通过 | 既有早有准备与升级杂技洗牌选牌顺序保持严格一致，确认 PR 没有覆盖当前 0.17 的战略选择修复。runId `1cd79d4025cd4c5698ec2ac0edc39f4e` | 2026-08-29 |
| `OSTY-RATTLE-TURN-COUNTER-0171` | 通过 | 第一回合让奥斯提攻击，完整结束回合后在第二回合打出猛晃；上回合攻击与命中计数均已清零，伤害及完整状态与实机一致。runId `1d3f201d5c75487f9cf9b70d67635dcf` | 2026-08-29 |
| `AUTOPLAY-ADJACENT-REGRESSION-0171` | 通过 | 抽牌触发、回合末自动出牌和 32 项卡牌完成生命周期严格差分全部通过。runId `2552b97f8b024ebebf8fbe268377086b`、`03b7d9432aeb4024bf11c2d5cc3a8ed3`、`03944b3c91844c8799965f40166b261f` | 2026-08-29 |

## 0.17.0

本节只登记本批次实际运行的验证。需求原文和计划不作为测试通过证据。

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `OPENING-STRENGTH/DEXTERITY-POTION-0170` | 通过 | 力量药与敏捷药均为最终路线首个动作、位于首张牌之前；runId `a65e0ce7c1e1478c949052d86ed799a7`、`c6217b9d975e4ecbaf0e26a4a3dd7a5d` | 2026-08-29 |
| `LIZARD-TAIL-LIVE-REUSE-0170` | 通过 | 1 HP 触发蜥蜴尾巴后，首轮与第 2 回合复用均保留整场战损 1；路线有“蜥蜴尾巴：复活”，计划外重算 0。runId `735a14adb16241719f220badb89f00a9` | 2026-08-29 |
| `BRIGHTEST-FLAME-TERMINAL/NECESSARY-0170` | 通过 | 同样无伤可胜时不打至亮之焰；必须用它完成当回合击杀时，路线保留 78 最大生命与 2 点当前损失。runId `c6b2b17afea34e9d8befaf3f32401f36`、`207af957f0664c478201bcd4c49bffd2` | 2026-08-29 |
| `BATTLEWORN-DUMMY-V1/V2/V3-KILL-0170` | 通过 | 三档训练假人均以自伤攻击完成击杀，不用安全停滞替代目标；runId `319a22b784414d4d8f75559ecaa21779`、`9f7666436f4c4451907971ae732211b1`、`b61f285e96ce40e2bef847e1c708250c` | 2026-08-29 |
| `BATTLEWORN-DUMMY-EVENT-DEFEAT-0170` | 通过 | 倒计时耗尽返回 `EventDefeat`，不授予胜利。runId `963ddd67b7db402da7a46f17a73cd7a3` | 2026-08-29 |
| `TWO-CARD-INFINITE-DEPLOY-0170` | 通过 | 亮剑/亮技双卡无限执行 19 个动作、18 次洗牌，当回合零战损击杀；完整自动执行计划外重算 0。runId `54b78ec8e2ef4baf80a452ff0744a81f` | 2026-08-29 |
| `ANGER-COMPACT-ALTERNATIVE/REQUIRED-0170` | 通过 | 等价击杀选择切割且不打愤怒；只有愤怒可击杀时仍使用。runId `03bfc9e0b0d44389aaba29c74f6a99fa`、`64a3b6d6d4304bdd9c6db48386983122` | 2026-08-29 |
| `AEONGLASS-ANGER-MIDCOMBAT-0170` | 通过（近似重建） | 按问题包第 9 回合手牌、生命、格挡、Power 和行动历史近似重建，路线不再加入愤怒。该夹具仍只找到死亡路线，省略完整消耗堆与部分历史，不作为原包战损复放。runId `58125330c52a4552b196021df614298e` | 2026-08-29 |
| `BECKON-CROSS-TURN-DEPLOY-0170` | 通过 | 首动打出呼唤，预计整场战损 4，第 2 回合自动击杀，计划外重算 0。runId `41198704657b42d284ff24113dbc429b` | 2026-08-29 |
| `GENETIC-ALGORITHM-REPLAY / GOOPY / SCYTHE-0170` | 通过 | 遗传算法华彩重放累计成长 6，并在第 2 回合继续执行、计划外重算 0；黏糊防御成长 1；巨镰成长 5，三者均在同战损胜利路线中主动培养。runId `6097ecc0ab3142a0a6c0ee187c1eda54`、`4b36668f89c449ca8eeb5ea6e6e1d2e4`、`4420c21826404907b33a1e9543949cdc` | 2026-08-29 |
| `NIGHTMARE-CLONE-GROWTH-BOUNDARY / SOULS-POWER-GROWTH-0170` | 通过 | 梦魇 `Clone` 不带 `DeckVersion`，因此不虚构跑局成长；灵魂之力跨回合培养至少 6。边界验证 runId `6e616ddf5c9b45b9a7c20434a8f912c1`，灵魂之力 runId `4e265950217046f886890458d2728220`；错误保留跑局版本会在第 2 回合产生状态差异，失败证据 runId `6112275ee763406abe03f23dfdc5238c` | 2026-08-29 |
| `FEED / THE-HUNT / HAND-OF-GREED-FATAL-0170` | 通过 | 三类斩杀分别获取最大生命、卡牌奖励和金币，且优先于普通等价击杀。runId `1e25b558793e445cbfa2394b23e2ef7a`、`41205c552552424197c3dde4827fa0f7`、`716c7e94d0c44b659672a8954b47de20` | 2026-08-29 |
| `NOT-YET / ROYALTIES / FORBIDDEN-GRIMOIRE / ALCHEMIZE-0170` | 通过 | 同战损胜利中依次保留治疗、金币奖励、移除奖励和生成药水；runId `72e453a92041468e8b041df280512ecb`、`3d1538b146e249b6b9debbb1a84ee54c`、`62943077e9a241ff90097518571d6dfc`、`532ddda82beb48fe815a0f66d8528d06` | 2026-08-29 |

## 0.16.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `NIBBITS-DUPLICATE-TORIC-0160` | 通过 | 从啃咬兽问题包战前状态直接注入两份坚韧之环。正式搜索跨 8 回合返回，runId `254746adc3a54c52ad894279e310d1e6`；严格差分验证两份实例以 `Block=5/8` 分别触发，合计获得 13 格挡并剩余总层数 1，runId `1eedfd2234a847378a0e79c600fb1012`；问题包世界线完整全自动在第 8 回合结束、计划外重算 0，runId `bcd844191a81453d8d21702024aa0fb9` | 2026-08-29 |
| `KNIGHTS-RELIC-ANNOTATION-REGRESSION-0160` | 通过 | 从三骑士问题包战前存档恢复种子、A10、`108/97/89` HP 与首行动；最终遗物标注的完整路线回放正常完成，返回 3 回合候选，不再出现两张 `BOOST_AWAY` 升级/保留状态交换。当前路线与旧包不同，不记作旧路线逐动作回放。runId `dfdb18f2036e42ecb6beda299d808028` | 2026-08-29 |
| `CHOICES-PARADOX-SCROLLS-0160` | 通过 | 使用咬人卷轴问题包的战前存档、种子、A10、四敌生命和行动重建首回合；验证选择悖论原生页面先显示、搜索后启动、Mod 自动选牌，路线第一组胶囊以“选择悖论：选择 ”开头。短搜 `5891.5 ms`，比较 `5883` 个选牌分支，runId `b9fa371a5b29479bb97c19da7980526f`；最小五候选夹具 runId `220482eb7fbc4f459c6f970748b0e033` 同样通过 | 2026-08-28 |
| `RINGING-HAVOC-AUTOPLAY-0160-FINAL` | 通过 | 仪式兽施加昏眩后，破灭作为本回合第一张牌正常结算；其翻出的重振被原版 `CardPlaysStarted` 规则阻止，不获得格挡、不消耗手牌防御，重振按破灭规则进入消耗堆。原生与预测完整状态一致。runId `bbc71ec201d34e16a114e2a1769ceb52`；修复前基线 runId `c24277b02b414dc08fdcb59fc7cec21e` 为模拟 5 格挡、实机 0 格挡 | 2026-08-28 |
| `MONSTER-MOVES-BATCH-029-RINGING-0160-FINAL` | 通过 | 既有昏眩相邻回归升级为当前逐实例状态键后，两次 `BEAST_CRY_MOVE` 严格差分通过：第一张牌可打、后续带昏眩的牌不可打，玩家回合末 Power 与全部昏眩状态清除。runId `0b73e8ca09374b0dbb27e41f6f021ec9` | 2026-08-28 |
| `HEADBUTT-EMPTY-DISCARD-0160` | 通过 | 清空全部牌堆后实际打出头槌；弃牌堆为空产生的 `0` 选项原生牌堆请求按空选择完成，第 2 回合精确复用，计划外重算 0。runId `53d9a793c14040d790183727ab0a88cd` | 2026-08-28 |
| `COSMIC-INDIFFERENCE-EMPTY-DISCARD-0160` | 通过 | 清空全部牌堆后实际打出宇宙冷漠；空弃牌堆选择不再中止部署，第 2 回合精确复用，计划外重算 0。runId `b09e6ef038604469b73680803c6916e7` | 2026-08-28 |
| `TORIC-TOUGHNESS-FRAIL-BLOCK-0160` | 通过 | 虚弱 1 层下打出坚韧之环，角色实际获得 3 格挡，但 Power 内部精确保存 `Block=3.75`；原生与预测完整状态一致。runId `e0e74f8d044e4026b9484ae78d03a622` | 2026-08-28 |
| `JAXFRUIT-TORIC-TOUGHNESS-REUSE-0160` | 通过 | 从啪嗒果问题包战前存档恢复种子、A10、双敌生命、首行动与 RNG；第 4 回合精确复用，计划外重算 0。runId `ed4f9715bde9405bab9655fd83701aba` | 2026-08-28 |
| `PAINFUL-STABS-MONSTER-ATTACK-0160` | 通过 | 给酸液攻击怪物注入荆棘，单次穿透格挡的命中后弃牌堆精确加入 1 张伤口；原生与预测完整状态一致。runId `b89e025cf429450595e4d38f2e603c90` | 2026-08-28 |
| `POWER-DAMAGE-HOOKS-REGRESSION-0160` | 通过 | 14 组伤害与攻击钩子严格差分全部通过，覆盖荆棘、吸取、活力、缓冲等，确认怪物攻击接入共享 `AfterAttack` 后没有重复结算。runId `50849d4ecff04661a7254b529611c74e` | 2026-08-28 |
| `TEST-SUBJECT-PAINFUL-STABS-REUSE-0160` | 通过 | 从实验体问题包战前存档恢复种子、A10、牌组、遗物、首行动与 RNG；越过第二形态多爪与荆棘，至第 6 回合持续精确复用，计划外重算 0。runId `a267600d977549f1a492d36479394f60` | 2026-08-28 |
| `VANTOM-UPGRADED-CARD-SHUFFLE-0160` | 通过 | 从 Vantom 问题包战前存档恢复种子、A2、牌组、首行动与 RNG；普通/升级打击跨洗牌顺序一致，第 5 回合精确复用，计划外重算 0。runId `e877c8239def4647a36c7d5102c940f3` | 2026-08-28 |
| `INSATIABLE-INVOKE-CROSS-CHARACTER-0160` | 通过 | 静默猎手打出召唤后推进到第 2 回合；原生与预测均创建 `2/2` 奥斯提并施加 1 层“为你而死”，两项下回合 Power 被消费，额外能量与 5 张手牌严格一致。runId `ec2f0a77e09a424fad6b8f78f2460c7e`；既有亡灵契约师奥斯提卡牌与伤害转移回归 runId `037a7a3ec6bc48f797913d398f0dfde1`、`c09e18e9f28b47e1a5c528495c62c124` 同时通过 | 2026-08-28 |
| `INSATIABLE-INVOKE-SEARCH-0160` | 通过 | 无厌沙虫固定为液化地面，静默猎手只有召唤与 5 张防御；正式 Short 搜索越过原 `EndTurn → SUMMON_NEXT_TURN_POWER` 初始化错误，正常返回 5 回合候选、1 个可执行动作、未镜像项 0。runId `0765ed5133604dcb9fab017fa8e30f42` | 2026-08-28 |
| `PALE-BLUE-DOT-FIFTH-CARD-DRAW-0160` | 通过 | 注入 2 层暗淡蓝点后恰好打出 5 张牌并进入下一回合；原生与预测都在第五张触发，下回合均抽基础 5 张加额外 2 张，瞬时抽牌 Power 均已消费。runId `cc6470a2161c417bbf64e5a672a67367` | 2026-08-28 |
| `TRIGGERED-SHUFFLE-CHOICE-ORDER-0160` | 通过 | 两个严格差分场景分别用早有准备和升级杂技触发空抽牌堆洗牌；战略选择先从洗牌后的抽牌堆拿走打击，随后卡牌自身选择把同一张打击置顶或弃掉，原生/模拟完整状态一致。runId `8da0adbda1484b8f8131cadd30e60d2c` | 2026-08-28 |
| `DECIMILLIPEDE-TRIGGERED-CHOICE-REPLAY-0160` | 通过 | 从千足虫问题包战前存档、种子、三段生命和三个首行动重建初始搜索，正常返回候选且没有再次出现第 17 回合早有准备双 pending 异常。当前路线与包内旧失败分支不同，不记逐动作回放。runId `967571405a5c4a78851c5310fcdd303a` | 2026-08-28 |
| `AEONGLASS-TRIGGERED-CHOICE-REPLAY-0160` | 通过 | 从永世沙漏问题包战前存档、种子、A10 和 `EBB_MOVE` 重建强制短搜，越过原第 6 回合杂技双 pending 边界并返回候选。当前路线与包内旧失败分支不同，不记逐动作回放。runId `d85101a57dcc4d4d910a2e159e02e6c0` | 2026-08-28 |
| `KNOWLEDGE-DEMON-GLAM-POCKETWATCH-0160` | 通过 | 注入怀表和带华彩的升级后空翻，后空翻以一个路线动作完成两次 CardPlay；推进到第 2 回合后原生/模拟牌堆、抽牌及怀表私有计数严格一致，均为 `POCKETWATCH/0/2`。runId `c82c4fcbadda41dc96df6d65cf0e0d63`；问题包 Custom/Low 战前跑局均未在夹具上限内完成首搜，不记通过 | 2026-08-28 |
| `CARD-UPGRADE-STABLE-SHUFFLE-0160` | 通过 | 武装只升级两张同名防御中的一张，两张牌以升级/普通顺序进入弃牌堆后触发洗牌；修复前第 2 回合严格差分稳定得到普通/升级防御错位，runId `1718b01532d94f34b65acc79a246482a`；改为按分支当前预览排序后原生/模拟完整状态一致，runId `854065893bc742c5ac04e3d6f59e8cdf` | 2026-08-28 |
| `CHOMPERS-UPGRADED-CARD-SHUFFLE-0160` | 通过 | 从啃咬者问题包战前存档重建，完整自动战斗在第 5 回合结束；武装升级后的同名牌跨洗牌顺序与实机一致，计划外重算 0。runId `ddebe062128845f9a3f73fbb6992e3ff` | 2026-08-28 |
| `CHOMPERS-UPGRADED-CARD-SHUFFLE-INCREMENTAL-0160` | 通过 | 同一问题包状态强制短搜并启用增量/完整前缀核对，覆盖 12 回合、3 次洗牌，未镜像项 0，前缀回放一致。runId `0c32515cb2944570a6a748febf928737` | 2026-08-28 |
| `STRATAGEM-PREPARED-CHOICE-ORDER-FINAL-0160` | 通过 | 升级准备充足在空抽牌堆时触发洗牌，战略选择先从三张抽牌堆选一张，随后准备充足抽两张、弃两张并留下打击完成 1 HP 斩杀；增量/完整回放一致，真实原生页面按两次选择顺序完成，计划外重算 0。runId `d305379b208841b68e25f8987e2e1967` | 2026-08-28 |
| `TEST-SUBJECT-PREPARED-CHOICE-SHORT-0160` | 通过 | 从问题包搜索请求检查点固化 5 张手牌、27 张有序抽牌、玩家状态及 `BITE_MOVE` 状态日志；强制短搜越过原准备充足双 pending 失败点，返回 7 回合候选，未镜像项 0。runId `d4903310604044ae8fa0c689a82f8b8d`；整包增量与普通深搜均在 180 秒达到夹具上限，不记通过 | 2026-08-28 |
| `CROSS-TURN-NO-PROGRESS-0150` | 通过 | 仅有一张防御、100 敏捷且完全没有伤害手段；修复前耗满短搜约 22 秒并搜索 54 回合，修复后搜索本体 175.3 ms 结束、剪掉 18 条跨回合无进展分支。runId `5e3fa09b18094a77a07492098e204785`，修复前 runId `ddcb886becc84a28aa8b56dbb067bea9` | 2026-08-28 |
| `BOWLBUGS-CROSS-TURN-NO-PROGRESS-0150` | 通过 | 从问题包战前存档、种子、敌人生命与首轮意图近似重建，仍找到第 6 回合胜利、预计战损 3、零药水；当前没有原生战斗状态导入器，不记作问题包逐动作回放。runId `33c814b90b6b4bdda47fe5b9c98961f9` | 2026-08-28 |
| `SURVIVOR-REPLAY-EMPTY-CHOICE-0150` | 通过 | 爆发与复制使升级生存者执行三次；前两次实际弃完两张牌，第三次原版 `options=0 / select=0..0` 请求按无操作完成，不消费虚构计划。首回合结束、计划外重算 0，runId `207cdd4927f74188948ec903574a3c7c`；修复前 runId `026a459931b24b58be85d644d3778d25` 在同一请求报错 | 2026-08-28 |
| `NATIVE-EMPTY-PLAN-ADJACENT-0150` | 通过 | 复制拾荒在空手时发出两次原生空请求；搜索生成的两条显式空计划逐条核销，首回合结束、计划外重算 0。runId `c73b5302e55e4b06bf56dc35169f3e20` | 2026-08-28 |
| `POCKETWATCH-REPLAY-REUSE-0150` | 通过 | 手牌中的螺旋打击实际结算两次 `CardPlay`，路线仍保持一个出牌动作；怀表逐次计数后第 2 回合命中精确复用，增量分叉与完整前缀回放一致，计划外重算为 0。runId `93d934679e8746de95bebd9dd5ce58e2`；修复前基线 runId `89792141b109409aa2aa5adcc7d2a846` 稳定得到 `expected=1 / actual=2` | 2026-08-28 |
| `POCKETWATCH-REPLAY-FULL-COMBAT-0150` | 通过 | 手牌为螺旋打击、抽牌堆为普通打击，敌人 13 HP；完整自动部署在第 2 回合结束，增量分叉与完整前缀回放一致，计划外重算为 0。runId `106f0b2966dd4225ac9ce1213e123712` | 2026-08-28 |
| `INCOMPATIBLE-GAMEPLAY-MOD-MESSAGE-0150` | 通过 | 预测失败边界断言验证未知第三方玩法 Mod 的玩家提示包含 Mod 名称、标识和卸载建议，不暴露内部订阅器类型；详细异常仍保留 Mod 与订阅器上下文。runId `3b920fd04bb64fdeba536ee825219ea4` | 2026-08-28 |
| `BATTLEWORN-DUMMY-TIMEOUT-BOUNDARY-0150` | 通过 | 第二档假人 150 HP、时间限制 1 层；正式后台搜索在原生逃跑前返回 `EventDefeat`，不移除假人、不授予胜利。runId `1b1d321d7ac941adb5d515efa861d6ee` | 2026-08-28 |
| `BATTLEWORN-DUMMY-V2-EXACT-FINAL-0150` | 通过 | 从第二档训练假人问题包的战前存档、固定牌序和 150 HP 重建，开启增量分叉/完整前缀回放核对并完整自动执行。未击杀分支正确为 `won=False / EventDefeat`，击杀路线为 `won=True / None`；第 2、3 回合精确复用，计划外重算 0。runId `7fbd338febef40668a4980555cc51971` | 2026-08-28 |
| `FAIRY-AUTOMATIC-RESCUE-FINAL2-0150` | 通过 | 1 HP 铁甲战士持瓶中仙女，手牌/抽牌堆各一张重锤；求解器不再判定仅有死亡路线，第 1 回合精灵药自动复活，第 2 回合击杀。首轮路线记录 1 瓶药，实机消耗 `FAIRY_IN_A_BOTTLE`，增量/完整回放一致，计划外重算 0。runId `bbddfcc1e1e54be2a4405e58cd7f557e` | 2026-08-28 |
| `FAIRY-DEATH-LIFECYCLE-FINAL2-0150` | 通过 | 瓶中仙女的自动防死、消耗槽位和 30% 回复与原版完整状态严格一致，已消耗实例不会再次进入死亡监听。runId `e601bec430ea49318ef57a550d8284f8` | 2026-08-28 |
| `ONLY-DEATH-NO-FAIRY-REGRESSION-0150` | 通过 | 相同 1 HP 与酸液攻击下不注入精灵药，首轮仍正确报告仅死亡路线并在第 1 回合死亡，用药数 0。runId `53ad9b78496646b196aa4844794766ef` | 2026-08-28 |

## 0.15.0

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `VAMBRACE-STABLE-FORK-FINAL-0150` | 通过 | 原版臂铠已经获得本场首次格挡后仍保留触发卡引用；修复后状态可 Fork，触发卡身份和 `BlockGainedThisCombat=true` 均保持。runId `31793a0e83df4656aa0ea3b9182c4c29`；修复前基线 runId `0e1c56fe922c4b4a87109dbd1c06acd0` 稳定抛出问题包同款异常 | 2026-08-28 |
| `TUNNELER-IMBUED-GLACIER-VAMBRACE-0150` | 通过 | 缺陷机器人持臂铠，注能冰川开局自动打出并进入弃牌堆，原版得到 12 格挡；首轮搜索正常返回，未镜像项为 0。runId `a9ebb7b36f554948a8de0f658385aa34` | 2026-08-28 |
| `TUNNELER-IMBUED-GLACIER-VAMBRACE-INCREMENTAL-0150` | 通过 | 同一组合启用增量搜索核对，增量分叉与完整前缀回放一致；开局 12 格挡、未镜像项 0，搜索正常返回。runId `689793cd6343465393fdd567a4a7c41e` | 2026-08-28 |
| `RELIC-CARD-HOOKS-AUDIT-PART-2-VAMBRACE-FINAL-0150` | 通过 | 臂铠连续打出两张防御，第一张 5 格挡翻倍为 10，第二张按普通值获得 5，最终严格为 15；同批遗物 Hook 11/11 通过。runId `eddeca24b4544e918299e4b4bb2a401b` | 2026-08-28 |
| `AXEBOT-THORNS-MULTIHIT-FINAL-0150` | 通过 | 巨斧机器人以 `2 HP`、`2` 层库存执行两连击；玩家持有 `3` 点荆棘和 `12` 格挡。修复前模拟继续执行第二段并产生 `8` 点虚构战损；修复后第一段反伤致死即中止剩余攻击，玩家保持 `75 HP / 2` 格挡，库存重生后的完整实机/模拟状态一致。相邻上勾锤击同时核对攻击者死亡后仍结算虚弱/脆弱。runId `fac22ea0270a4996afa276df384ba370`，基线 runId `fc33aad095054c37a3de730d89472d2a` | 2026-08-28 |
| `AXEBOTS-BUNDLE-FULL-AUTO-FINAL2-0150` | 通过 | 从问题包战前存档重建，以 Low、Instant/0 秒完整自动结束于第 11 回合，`UnexpectedReplans:0`。当前路线与原包不同，不记作逐动作回放。runId `fca4e9e3a09d4ab490271ec6d38ad10a` | 2026-08-28 |
| `AXEBOTS-BUNDLE-INCREMENTAL-FINAL2-0150` | 通过 | 同一问题包状态以 Low/Short 完成增量分叉与完整前缀回放一致性，覆盖 11 回合、3 次洗牌，未镜像效果为 0。runId `c8c8f3675a934f028704ab82e2f7dd4d` | 2026-08-28 |
| `FTL-CROSS-TURN-STATE-0150` | 通过 | 修复前跨回合严格差分稳定复现第 3 张 FTL 少抽一张；最终分支状态实现下，第 3 张抽牌、第 4 张不抽均与实机一致。runId `add8e54810d54f41b5cb6b55dc410892`，基线 runId `6c069ce0891a484091481bd8b3387e35` | 2026-08-28 |
| `CURRENT-TURN-CARD-HISTORY-ADJACENT-FINAL-0150` | 通过 | Fetch 在下一回合经全息影像取回同一实例后重新允许抽牌；Make It So 在本回合第 3 张技能后返回手牌，实机/模拟严格一致。runId `f478c96ec3144c81847c3b225f95866e` | 2026-08-28 |
| `BYRDONIS-BUNDLE-REUSE-FINAL-0150` | 通过 | 从多尼斯异鸟问题包的战前跑局状态重建；第 3 回合精确复用，计划外重算 0，增量分叉与完整前缀回放一致。首抽路线与原包不同，因此不记作逐动作回放。runId `a0c1808cfae745188ae5a3f8d1f28270` | 2026-08-28 |
| `SLITHERING-STRANGLER-BUNDLE-REUSE-FINAL-0150` | 通过 | 从蛇行扼杀者问题包的战前跑局状态重建；越过原第 4 回合重算点并精确复用，计划外重算 0。首抽路线与原包不同，因此不记作逐动作回放。runId `a6b72e6f81f24aa096833902d6860046` | 2026-08-28 |
| `CUBEX-ROOT-CAPTURE-150` | 通过 | 修复前同场景稳定复现不存在的 `CubexConstruct.ChargeUpStrengthGain`；移除多余捕获后根快照成功物化。runId `0c39d6aa84904c5b994bf8f985bfd316`，基线 runId `f96241297c7a443a8a1fe50d0a7b5414` | 2026-08-28 |
| `CUBEX-SEARCH-INITIALIZATION-150` | 通过 | 方柱构装体正常首轮搜索覆盖 4 回合，返回 3 个可执行动作，未镜像效果为 0。runId `2f37d97d045743aea8d68ebf99db0e57` | 2026-08-28 |
| `MONSTER-MOVES-BATCH-020-CUBEX-150` | 通过 | 既有 13 项实机/模拟差分全部通过；方柱构装体排出、蓄能和两次重复轰击分别验证多段伤害及力量 `2 → 4 → 6` 累计。runId `1d382781dfc2402581af0383e093b5ea` | 2026-08-28 |
| `TOASTY-MITTENS-BUNDLE-FINAL-0150` | 通过 | 从异螨问题包的战前跑局状态重建首回合烘焙手套；原生手牌页按 `Visible → SearchStarted → Selected` 由 Mod 自动接管，搜索返回 1 个 `TOASTY_MITTENS` 选择并严格进入 Play 状态。runId `097e957b46b941e1b4eb0165862d5493` | 2026-08-28 |
| `KNOWLEDGE-DEMON-NATIVE-CHOICE-0150` | 通过 | 知识恶魔首轮路线计划 `MIND_ROT`；提交结束回合后原生 `ChooseCard` 页面 `visible=1 / selected=1 / search=0`，玩家获得 `MIND_ROT_POWER`，计划外重算 0，增量/完整回放一致。runId `5b5d61d595c249c0a4861151460cc490` | 2026-08-28 |
| `KNOWLEDGE-DEMON-NATIVE-CHOICE-REUSE-0150` | 通过 | 同一路线完成敌方回合二选一后，第 2 回合直接复用；知识恶魔选择没有被错误留给下一回合准备器，计划外重算 0。runId `27a42f3669fb479fafde8e10e3d499f3` | 2026-08-28 |
| `TOASTY-KNOWLEDGE-CROSS-PHASE-0150` | 通过 | 知识恶魔战同时持有烘焙手套；首回合手套保持 `Visible → SearchStarted → Selected`，结束回合后自动完成 `MIND_ROT` 二选一，第 2 回合只重放准备选择并精确复用，计划外重算 0。runId `39de2fb177ce43db95c1c2209c390330` | 2026-08-28 |
| `BURNING-PACT-AUTO-COMPLETE-0150` | 通过 | 固定手牌为燃烧契约+、升格者之灾、防御，抽牌堆为打击；Normal 部署先显示原生手牌页并选择升格者之灾，再打出抽到的打击结束战斗。请求记录 `manual_confirmation=False`，页面 `visible=1 / selected=1 / search=0`，增量/完整回放一致。runId `660b6ba4b2a044938d3960208639b5ef` | 2026-08-28 |
| `ARMAMENTS-AUTO-COMPLETE-ADJACENT-0150` | 通过 | 未升级武装从打击、防御中选择升级目标，原生手牌升级页完成后继续打出升级打击；页面 `visible=1 / selected=1 / search=0`。runId `793fd329c05340d98a775b173dd3b8c9` | 2026-08-28 |
| `CHOMPERS-BURNING-PACT-BUNDLE-FIXED-0150` | 本问题路径通过，整战断言失败 | 从问题包战前跑局状态重建同族小队，燃烧契约原生手牌页完成且未出现确认按钮异常，战斗第 5 回合结束；第 4 回合另有防御升级状态不一致并触发 1 次计划外重算，故不记为整场通过。runId `99127886a8c54dfe8941239186b5ddea` | 2026-08-28 |
| `TOADPOLES-WEAK-20260828-BUNDLE` | 根因确认，待 macOS 实机复测 | `0.14.11`、macOS ARM64 的两次搜索均在 `GC.TryStartNoGCRegion(6 GB, 1 GB)` 抛出 `ArgumentOutOfRangeException(totalSize)`；根快照已成功，尚未进入 Beam。当前代码只把该精确异常分类为 CLR 区域上限，其余异常保持失败 | 2026-08-28 |
| `GC-NOGC-REGION-LIMIT-0150` | 通过（正常 No-GC 路径） | Windows headless 设置 `16 GB` No-GC 预算；本机 CLR 成功进入 No-GC，首轮 Short 搜索在 `168.7 ms / 2.20 MB` 内产出 1 个可执行动作，GC 暂停 `0 ms`，场景 Passed。runId `04450f09159d48d9bfaca0ba9ba049e0`；该结果不覆盖 macOS 的区域拒绝分支 | 2026-08-28 |
| `KAISER-CRAB-SEARCH-REPLAY-FINAL-0150` | 通过 | 从帝王蟹问题包战前存档、原 seed 与两只怪物的 `209/199` HP 重建；修复前在第 3 回合 EndTurn 回放稳定复现缺失 `Rocket.ChargeUpStrengthGain`，修复后搜索覆盖 7 回合、未镜像效果为 0。runId `7236617d45b54d17b98bb2a8a68fcf21`，基线 runId `42db83c00fbb43548135efadaed5604d` | 2026-08-28 |
| `KAISER-CRAB-INCREMENTAL-SHORT-FINAL-0150` | 通过 | 同一问题包状态以 Low/Short 完成增量分叉与完整前缀回放对照，搜索覆盖 9 回合、未镜像效果为 0。runId `2067fe850f4d4cd0b011c7e1ce05e40a`；High 完整验证因仪器开销在 120 秒超时，runId `343c4c6b3ba54ef58050f6d1a898ac05` | 2026-08-28 |
| `MONSTER-MOVES-BATCH-021-KAISER-0150` | 通过 | 帝王蟹 10 个行动的实机/模拟严格差分全部通过；火箭蓄能获得 `2` 点力量，激光与重新充能保留累计状态。runId `7bcbece1c9e04a36a72b8ddddb2db361` | 2026-08-28 |
| `CALCULATED-VAR-ROOT-CAPTURE-FINAL-0150` | 通过 | 耗尽堆 `EXPECT_A_FIGHT` 固定 `CalculatedBlock=16 / CalculationBase=15`；修复前根投影稳定复现 `16 → 15` 失败，计算缓存改为派生字段后根快照通过。runId `e243b913a1a44aa8ba67e692da88d1b0`，基线 runId `0ea043478335482c84408617ab91e38a` | 2026-08-28 |
| `EXPECT-A-FIGHT-CALCULATED-BLOCK-FINAL-0150` | 通过 | 玩家持有 5 点力量时打出 `EXPECT_A_FIGHT`，实机与模拟完整状态严格一致，证明移除派生缓存没有丢失公式输入或实际格挡。runId `bccf3de8a91f4aad94af02b589a77d1a` | 2026-08-28 |
| `CARD-DOWNGRADE-STATE-AUDIT-382-0150` | 通过 | 魔法骑士抑制对手牌、抽牌堆和弃牌堆的 8 类升级牌执行降级，并在施法者死亡后恢复；实机与模拟逐实例状态一致。runId `95795debe881414e8d8179921061e20e` | 2026-08-28 |
| `KNIGHTS-ELITE-SEARCH-FINAL-0150` | 通过 | 从三骑士问题包战前存档、原 seed、进阶与 `108/97/89` HP 重建，首轮搜索正常返回可部署路线；复杂嵌套随机选牌的失效候选没有再中止搜索。runId `411ab4cdfe514a7cab2bac384354beb5` | 2026-08-28 |
| `KNIGHTS-ELITE-BUNDLE-FULL-AUTO-FINAL-0150` | 通过 | 同一战前存档以 Instant/0 秒完整自动部署，第 1 回合结束战斗，计划外重算 0。当前源码首抽路线与 `0.14.11` 原包不同，不记作原包逐动作回放。runId `bc508c1d2a75438599fc4cb26656acf4` | 2026-08-28 |
| `KNIGHTS-ELITE-INCREMENTAL-FINAL-0150` | 通过 | 同一问题包重建状态以 Low/Short 完成增量分叉与完整前缀回放一致性，首轮返回 11 个动作并结束战斗。runId `b0740c38be024c61a73a7c7aa281164a` | 2026-08-28 |
| `STATE-FIELDS-DERIVED-CALCULATED-0150` | 通过 | CoverageCatalog 将 43 个原版 `CalculatedVar` 字段登记为 `Derived`，未分类状态字段为 0；真实基础变量、私有状态和字符串显示字段分类保持不变 | 2026-08-28 |

## 0.14.13 Loadout 战斗费用兼容

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `LOADOUT-EVERY-CARD-FREE-ROOT-1413` | 目标路径通过，完整断言受限 | 投影实际 Loadout `0.4.10` 与 BaseLib `3.4.5` 后进入小啃兽战斗。第一轮成功创建并 Fork 根快照，未再出现 `LoadoutEveryCardFreeCombatHook` 的 `SEARCH_SETUP_FAILURE`；随后旧测试把 ModHelper 运行级 subscriber 误算进原版前缀，runId `7c5c868146194a05a7d038d93c31feb3` 在外围计数断言失败。修正断言后的第二轮在 Loadout 的 headless 战斗房间资源预载处超时，未进入战斗断言，不记为完整通过 | 2026-08-28 |

## 0.14.12 同族小队压缩连锁

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `kin-boss-route-clean` | 通过 | 固定 `oldE0VXH9PVN8`、进阶 10、第一幕同族小队、三敌 `63/62/199` HP、铁甲战士 `56/85` HP 与存档牌序；Smart、Instant 完整自动执行。第 4 回合燃烧契约、愤怒、余烬后使用灰水耗尽六张牌，第 5 回合以愤怒、放血、燃烧契约及连续攻击击杀三敌；最终 3 HP，战损 53，第 5 回合获胜，计划外重算 0。runId `41108853af1640fa8ee3379793469fc9` | 2026-08-28 |

## 0.14.10 敌方攻击压制保路

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `INSATIABLE-PARETO-CONTROL-150` | 通过 | 固定无厌沙虫“液化地面”、日志首手与有序牌堆；不再把某张牌的固定顺序当作质量代理，而是同时门禁首回合 `0` 掉血、整场预计掉血不超过 `7`、不卖血、不用药、至少搜索/存活至第 `7` 回合且终局敌方总生命不高于 `204`。隔离的上游 `v0.17.2` 与当前分支均复现旧 `MALAISE` 首动作断言失真，因此未修改生产搜索排序。runId `207ad2c8d02f489f9ba1aa41287d6966` | 2026-08-30 |
| `MALAISE-CONTROL-NIBBITS-REGRESSION-150` | 战损通过，回合断言失败 | 双小啃兽仍为 `0` 战损、`0` 计划外重算、两次洗牌；实际第 6 回合结束，请求沿用了第 5 回合精确断言，因此结果状态为 Failed，不计作整场通过。runId `a6ef1d75a48d46f2be7b98ce7ef4def5` | 2026-08-27 |

## 0.14.9 Tender 出牌完成结算

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `HUNTER-KILLER-TENDER-CARD-SEQUENCE-149` | 通过 | 猎人杀手场景注入 Tender，依次打出后空翻、中和+、打击；敌方实际只损失 `7` HP，力量/敏捷各降 `3`，逐字段 actual/simulated 一致。runId `79929fef88b3495cbe60e4d529594a31` | 2026-08-27 |
| `TENDER-INCREMENTAL-CARD-COMPLETION-149` | 通过 | 两张打击覆盖 Tender 的逐次出牌完成结算，增量分叉与完整前缀回放一致，首回合结束且计划外重算 `0`。runId `bb70d9239f78495e988682b40bda9bec` | 2026-08-27 |
| `TENDER-FULL-AUTO-REUSE-149` | 通过 | 猎人杀手完整自动部署后进入第 2 回合，continuation 精确复用，计划外重算 `0`。runId `c242e1f6287c484cbae5925b36a995f5` | 2026-08-27 |
| `MONSTER-MOVES-BATCH-033-TENDER-149` | 通过 | 旧 Tender 双打击与玩家回合末力量/敏捷恢复严格差分继续通过。runId `5da676be79aa45e7b4f6cff40b353fa4` | 2026-08-27 |
| 问题包战前存档重建 | 未进入战斗 | 现有无人入口在原版 `NOverlayStack` 初始化阶段空引用；不计作问题包回放通过 | 2026-08-27 |

## 0.14.8 回合首张牌出牌间隔

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| 回合抽牌完成到首张牌 | 未执行 | 按用户要求不运行测试；实现让全自动在原版抽牌及回合准备动作完成后，等待“牌间额外停顿”再恢复路线或部署首张牌 | 2026-08-26 |

`0.10.0` headless 接通阶段保留三条未计为通过的开发证据：首次隔离启动因未确认 Mod 警告而跳过全部 Mod；允许 Mod 后因关闭 Steam 而找不到创意工坊 RitsuLib；首次长线因无窗口“战斗基础”教学节点空引用而停住。启动器现分别通过隔离设置、临时 RitsuLib 投影和仅无人请求活动时跳过纯 UI 教学解决。熵的两个前置夹具也未冒充通过：低血双敌在第 `2` 回合先发生减员，导致第 `3` 回合按死亡敌人状态差异保守重搜；单敌夹具则被怪物自身 `2` 项未镜像效果的严格断言提前拒绝。最终通过项使用满血小啃兽，隔离了熵与 RNG 本身。

## 0.14.7 内存检查点续搜

| 场景 | 结果 | 验证内容 | 日期 |
| --- | --- | --- | --- |
| `GC-CHECKPOINT-RESUME-0147` | 通过 | 1 GB No-GC 压力下触发 5 次 Beam 检查点；每次从原回合层/出牌深度续搜，不从根重算。后台全代非压缩回收暂停 `3.1-4.0 ms`，托管存活量降至 `100-205 MB`；完整 6 回合获胜、零非预期重算、无 `>50 ms` 帧和 No-GC 耗尽 | 2026-08-26 |

## 0.14.6 动作选牌与部署高亮时序

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `AEONGLASS-WITHER-CHOICE-TIMING` | 通过 | 凋零气场 `CardsLeft=1` 时打出杂技+；模拟与原生选牌候选都断言不含尚未由 `AfterCardPlayed` 生成的凋零，选择真实防御后完整状态一致。runId `4e3576a6ffc546c6979c768ea6f46f60` | 2026-08-26 |
| `MANUAL-CHOICE-TRANSACTION-ADJACENT` | 通过 | 生存者、杂技+、早有准备+、燃烧契约依次覆盖弃牌、抽后弃、抽二弃二和耗尽后抽牌；四项 actual/simulated 有序牌堆、Power、逐牌状态与 RNG 严格一致。runId `bcdaab927b3d405c8b8d20e3d4de4c93` | 2026-08-26 |
| `AEONGLASS-WITHER-CHOICE-FULL-AUTO-FINAL` | 通过 | 搜索在凋零气场 `CardsLeft=1` 下规划杂技+弃防御，再打出抽到的打击击杀永世沙漏；原生页面只请求并完成一次弃牌，增量/完整回放一致，计划外重算 0。runId `3ad4ff73aefd4136b51d7596741a7795` | 2026-08-26 |
| `TOOLS-UI-ACTION-ALIGNMENT` | 通过 | 第 2 回合必备工具页面完成后精确复用；回合准备胶囊不占部署索引，真实第一张牌为 `active_action_index=0`，牌完成后 500 ms 间隔内活动索引为空，原生页面 `search=0`，计划外重算 0。runId `268f83840ffc40fdb182edf1c03ff2f3` | 2026-08-26 |
| `PAELS-EYE-TOOLS-UI-ALIGNMENT-FINAL2` | 通过 | 首回合 0 张出牌直接结束并触发佩尔之眼，直接结束胶囊经历 active/complete；额外回合出现必备工具页面后复用第 2 回合，第一张牌仍从动作索引 0 开始，原生页面不搜索且计划外重算 0。runId `5e0966d1ce114496b2c4292a60d3871b` | 2026-08-26 |

## 0.14.5 佩尔之眼与路线重放胶囊

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `PAELS-EYE-LIVE-END-TURN` | 通过 | 静默猎手只持有佩尔之眼，首回合 0 张出牌并直接结束；开启全自动“重算后战损增加暂停”以强制经过实机结束回合风险复核。路线与 Overlay 均标注 `PAELS_EYE:额外回合`，实际未触发 `live_end_turn_risk` 暂停，直接进入额外玩家回合并 `Reuse:Turn=2`，`UnexpectedReplans=0`。runId `ee7607c122bf4623a785daa13a3dc993` | 2026-08-26 |
| `OVERLAY-REPLAY-BADGE` | 通过 | 手牌只有螺旋附魔打击；搜索计划记录该实例附魔后重放次数为 1，Overlay 动作快照在牌名后显示 `重放×1`，随后实际打出该牌。runId `77cc4df7265640d9b78f93768a333f15` | 2026-08-26 |

## 0.14.4 单步选牌页接管与间隔

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `SINGLE-STEP-TOOLS-TAKEOVER-EXECUTE` | 通过 | 单步先停在第 2 回合必备工具原生手牌页，求解器尚未选择；随后请求“执行本回合”，按既有计划完成选择，直接复用第 2 回合且计划外重算 0。设置 500 ms 牌间停顿，选择完成到下一张牌实测 610 ms。runId `48ec35fe5d4e45e38ed6fbed3fc012e4` | 2026-08-26 |
| `SINGLE-STEP-TOOLS-TAKEOVER-FULL-AUTO` | 通过 | 同一停住边界在原生页面开启全自动；既有计划完成选择后复用第 2 回合，计划外重算 0，500 ms 设置下实测间隔 609 ms。runId `f82172f5aa264f5d9d73b6c59e79a2a9` | 2026-08-26 |
| `FULL-AUTO-TOOLS-CROSS-TURN-0144-FINAL` | 通过 | 开启增量/完整回放核对并以 `Instant / 0 秒` 完整自动部署；第 2、3 回合必备工具页面均为 `visible=2 / selected=2 / search=0`，两回合都复用首轮路线，计划外重算 0，第 3 回合结束。runId `cfb96d6f67b74b7797dec580983b9bbb` | 2026-08-26 |

## 0.14.3 部署动作完成边界

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `MONSTER-WATERFALL-SLY-AFTER-DEATH-ORDER` | 通过 | 双怪局中回响斩击先把 1 HP、带蒸汽爆发的瀑布巨兽转入蓄爆，再同回合由原生杂技页面弃升级战术大师。动作完成态为能量 2，战术大师与杂技均在弃牌堆；第 2 回合直接 `Reuse:Turn=2`，`UnexpectedReplans=0`。runId `cabb121a5a1544a196dd1bda013884b2` | 2026-08-26 |
| `MONSTER-WATERFALL-DEPLOYMENT-SETTLEMENT` | 通过 | 按玩家日志重建静默猎手 22 张有序牌堆与瀑布巨兽长线，`Instant / 0 秒` 全自动于第 10 回合结束，完整经过 `ABOUT_TO_BLOW_MOVE` 与 `EXPLODE_MOVE`；1 次搜索、9 次续用、计划外重算 0。runId `24ecb35d75b4417aa6cd7a44652dcc65` | 2026-08-26 |
| `DEPLOY-EXACT-POTION-ACTION` | 通过 | 强制至少使用一瓶药水，实际入队并使用弱化药后打出攻击，于首回合结束；验证药水部署也能捕获并等待本次 `UsePotionAction`。runId `dfed9e7bbf884d7f8fdf07960831bef4` | 2026-08-26 |

## 0.14.2 单步边界与同名重放卡牌

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `SINGLE-STEP-TOOLS-SPIRAL` | 通过 | PUNCH Construct，抽牌堆仅有普通防御和螺旋附魔防御，玩家已有必备工具。初始路线的 `EndTurn.TurnStartChoices` 精确指向普通防御；执行本回合后停在第 2 回合原生手牌页，全自动关闭且 `turn_setup:2` 没有 Selected 记录。runId `07d96613e313442a99275ee969e1c02b` | 2026-08-26 |
| `FULL-AUTO-TOOLS-SPIRAL` | 通过 | 同一逐实例牌组开启全自动。第 2 回合原生手牌页 `visible=1 / selected=1 / search=0`，选择普通防御后直接 `Reuse:Turn=2`，`UnexpectedReplans=0`；本次日志没有“原生选牌会话没有位于活动栈顶”。runId `80552c268daf450cbce05aeb9314b844` | 2026-08-26 |
| `PUNCH-CONSTRUCT-20260826-BUNDLE` | 受限 | 问题包确认旧版在后续回合重复报告 `turn_setup:N` 会话栈异常，并记录第 3 回合普通防御只提供 3 点格挡。包内检查点位于必备工具选择之后，无法精确恢复选择前手牌；不记为整战复现通过，逐实例选择由上述两个定向夹具覆盖 | 2026-08-26 |

## 0.14.1 原生选牌定版

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `NATIVE-CHOICE-REPLAY-NO-SEARCH-556/557` | 通过 | 首回合工具盒先显示原生页面，再搜索三个候选并按 `Visible → SearchStarted → Selected` 完成；第 2 回合必备工具读取上一轮 `EndTurn.TurnStartChoices`，原生手牌页 `visible=1 / selected=1 / search=0`，随后直接 `SEARCH_REUSED turn=2`。Steam 可见机甲整战共显示并完成 6 次手牌选择，页面期间 `search=0`，第 3 回合复用恢复通过，第 7 回合结束且计划外重算 0 | 2026-08-26 |
| `NATIVE-CHOICE-SURFACES-553/560` | 通过 | 当前工作树把求解器接管的选牌改为原版可见页面：工具盒使用 ChooseCard；选择悖论使用简易网格；烘焙手套、赌博筹码、助能生存者、出牌弃牌使用手牌页面；全息影像使用战斗牌堆页面；武装使用手牌升级页面。首回合页面后搜索，后续回合只重放既有路线；动作内选择在对应事务中播放，各场景保持精确 Play 状态或零计划外重算 | 2026-08-26 |
| `NATIVE-CHOICE-STRICT-DIFF-554` | 通过 | 无 UI 严格差分仍使用测试专用选择器，生存者、杂技、早有准备等推断选牌 12/12 完整状态一致；生产 `Runtime/` 除原生观察驱动外禁止调用 `CardSelectCmd.PushSelector`，覆盖扫描 85 个调用点、0 未解析 | 2026-08-26 |

## 0.14.0 重构验收

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `HIDDEN-GEM-REPLAY-552` | 通过 | 从玩家 `0.13.35` 问题包恢复猫头鹰法官首轮的 7 张手牌、30 张有序抽牌、跑局快照与 RNG。High 固定根主动打出未掘宝石，使灵体获得 2 次额外重放，并从原“仅死亡路线”改为第 8 回合胜利；第 2-8 回合精确复用、0 药、零计划外重算。独立一步差分通过；Low 增量/完整前缀核对同样获胜（第 10 回合）；双小啃兽增量长线保持第 5 回合、两次洗牌、0 药、0 战损 | 2026-08-26 |
| `REFACTOR-FINAL-NIBBITS-551` | 通过 | 从最终提交构建的 Release DLL 开启根快照与增量/完整回放核对；双小啃兽第 5 回合结束、两次洗牌、0 药、0 战损，第 2-5 回合精确复用且零非预期重算。首轮 `6.21 s / 2.47 GB / 0 ms GC / 17.2 ms 最大帧` | 2026-08-26 |
| `REFACTOR-FINAL-MECHA-HIGH-550` | 通过 | 从最终提交以原固定快照和 High 预设复跑：第 5 回合结束，第 2-5 回合精确复用；`expanded=4624`、`transitions=33432`、`choice_branches=17735`、`dominance/transposition/repeatable=214/700/0`，`11.45 s / 3.55 GB / 0 ms GC / 17.6 ms 最大帧`。此前把增量全回放诊断与性能门槛组合的请求因 `100.4 s / 34.4 GB` 正确失败；中档请求因第 7 回合结束正确失败，二者均未计为通过证据 | 2026-08-26 |
| `REFACTOR-FINAL-NIBBITS-549` | 通过 | 根怪物从活动 roster 移除后仍保留本分支 AI/静态参数，允许正在执行的怪物行动完成尾部结算；原第 4 回合稳定崩溃夹具现于第 5 回合结束、两次洗牌、0 药、0 战损、逐回合复用且零非预期重算 | 2026-08-26 |
| `MIRROR-REGISTRY-DESCRIPTOR-548` | 通过 | action/result registry 统一提供支持 descriptor，CoverageCatalog 删除对三个私有字段及 MethodSpec 布局的反射；切换前后 3035 项及全部门禁/生成文件一致，钢笔尖 Hook 增量路线与真实部署通过 | 2026-08-26 |
| `SOLVER-OVERLAY-SNAPSHOT-547` | 通过 | 控制器一次性捕获 Overlay/Turn/Action 只读快照，三个 Renderer 不再读取搜索/预测可变类型；钢笔尖两动作路线真实渲染并部署，遗物后缀、击杀路线、ready/deploying/complete 状态和速度恢复均通过。人工布局与字体仍按 UI 人工项执行 | 2026-08-26 |
| `UNATTENDED-EXECUTOR-546` | 通过 | 差分分派、设置覆盖、搜索/部署等待、提前停止与完整自动战斗进入 `Executor`；双球两项严格差分、强制一瓶药首回合击杀、速度恢复和 Held 结果均通过，普通请求复用同一进程 | 2026-08-26 |
| `UNATTENDED-ASSERTIONS-545` | 通过 | 执行前预测/Fork/根快照/会话/CardModifier 检查及执行后回合、生命、出牌、用药、Power 断言进入 `Assertions`；根快照检查、实际打出指定卡和首回合结束在同一场景通过 | 2026-08-26 |
| `UNATTENDED-SCENARIO-BUILDER-544` | 通过 | 建局、进入遭遇、怪物/生命/牌堆/球/药水/遗物/Power/RNG 注入进入 `ScenarioBuilder`；Defect 双球两项严格差分通过。故意注入错误敌人数时仍记录 `inject_state` 与真实第 1 回合，随后同进程恢复成功 | 2026-08-26 |
| `UNATTENDED-WRITER-543` | 通过 | Passed/Held/Failed 的公共协议字段、内存采集和临时文件原子替换进入 `Writer`；同一进程依次写出成功、故意断言失败和失败后恢复成功三份结果，状态、阶段、错误与进程复用均正确 | 2026-08-26 |
| `UNATTENDED-PROTOCOL-HOST-542` | 通过 | 请求文件接收、协议版本、每请求测试开关、状态漂移和清理进入 `ProtocolHost`；同一 headless PID 连续完成两场首回合击杀，第二场明确 `UNATTENDED_REUSED`，最后按请求退出 | 2026-08-26 |
| `FINAL-ORDERING-POLICIES-541` | 通过 | 同一击杀夹具依次验证 Disabled/Smart 均保留 0 药路线，RequireAtLeastOne 选择并实机使用 1 瓶弱化药；固定防御牌组保持主动卖血 `5/5` 上限并剪除超预算路线 | 2026-08-26 |
| `FINAL-PLAN-ORDERING-540` | 通过 | `Solve` 的终局胜负、药水、卖血和边界排序迁入 `FinalPlanOrdering`，候选通过 `SearchFeatures` 读取固定特征。机甲保持第 5 回合、同动作序列、`4624/33432/17735` 与全部剪枝计数，`11.51 s / 3.55 GB / 0 ms GC / 18.8 ms`，零重算 | 2026-08-26 |
| `FINAL-ORDERING-DUAL-539` | 通过 | 切换前由旧排序和 `FinalPlanOrdering` 对同一候选集合逐字段比较选中节点、得分、药水与卖血统计；钢笔尖增量路线一致并首回合无损击杀 | 2026-08-26 |
| `BEAM-RETENTION-POLICY-538` | 通过 | 状态去重、Beam 排名、多样性通道、药水配额和 Pareto 保留进入具体策略；只通过 stand-pat 委托访问模拟。机甲保持第 5 回合、同动作序列、`4624/33432/17735` 与全部剪枝计数，`11.64 s / 3.55 GB / 0 ms GC / 17.1 ms`，零重算 | 2026-08-26 |
| `SEARCH-RUN-CONTEXT-537` | 通过 | 15 个搜索计数器、性能/节流、转置及四类缓存收口到单次 `SearchRunContext`，不池化或改算法。固定机甲保持第 5 回合、同动作序列、`4624/33432/17735` 与全部剪枝计数，`11.52 s / 3.55 GB / 0 ms GC / 17.4 ms`，零重算 | 2026-08-26 |
| `BEAM-PARTIAL-SPLIT-536` | 通过 | `CombatBeamSolver` 纯移动为七个阶段 partial；结构门禁固定文件和代表方法归属。机甲完整 headless 保持第 5 回合、同动作序列、4624 展开、33432 转移、17735 选牌分支与全部剪枝计数，`11.35 s / 3.55 GB / 0 ms GC / 17.2 ms`，零重算；Defect 球/Synchronize 严格差分通过 | 2026-08-26 |
| `MOD-SUBSCRIBER-BOUNDARY-534` | 通过 | BaseLib/Loadout subscriber 分段捕获；实际 CardModifier 夹具验证克隆、Owner 重绑和写时复制，Ritsu capability 反向夹具验证非空集合仍走原属性贡献。空 capability 快通道及 Fork listener 缓存继承把机甲分配从约 `4.98 GB` 降至 `3.57 GB`；连续两次完整 Mod 可见整战均为第 5 回合胜利、`0 ms GC`，最大帧 `8.6/16.5 ms`。带实际 Modifier 的最终轮仍为 `11.87 s / 3.57 GB / 0 ms / 13.5 ms` | 2026-08-26 |

## 已通过的无人场景

| ID | 状态 | 场景与断言 | 最近验证 |
|---|---|---|---|
| `CARD-POWER-NESTED-534` | 通过 | 卡牌 Power 结算改为可嵌套源栈；Unsettling Lamp 按触发卡关联。Knife Trap/Eidolon 自动出牌和升级 Knife Trap 双 Shiv 的模拟/原生差分通过 | 2026-08-26 |
| `BUILTIN-LISTENER-IDENTITY-533` | 通过 | Badge listener 以根克隆进入预测；多人缩放 listener 清除 live RunState/CombatState，并由单人 mirror 返回精确倍率。根隔离和双小啃兽增量整战通过；Loadout/BaseLib 第三方订阅另列适配项 | 2026-08-26 |
| `MONSTER-MODIFIER-IDENTITY-532` | 通过 | Modifier 以根克隆进入 Hook；永世沙漏生成凋零读取分支升级计数，Murderous 对预测召唤敌人施加 3 力量，召唤遗物读取根清单。首次差分暴露直接构造模拟器未物化根，统一构造边界后定向差分、根隔离和双小啃兽增量整战通过 | 2026-08-26 |
| `RUN-SNAPSHOT-HOOK-PREFIX-531` | 通过 | Run 标量、RNG、起始回合和 Hook 前缀进入主线程根；牌组 Card/Enchantment listener 使用克隆，卡池筛选显式消费捕获约束。根隔离、攻击药生成差分和双小啃兽增量整战通过 | 2026-08-26 |
| `ROOT-MODEL-INVENTORY-530` | 通过 | 玩家回合/金币、Relic/Potion、卡牌注册、Osty、初始 Power、Run RNG 和怪物私有字段进入主线程根；listener 使用克隆。首次严格复跑抓到并根修复 Relic `AfterCloned` 重置私有计数；最终遗物 Hook 11/11、钢笔尖、Knowledge Demon、Smart 救命药和双小啃兽增量整战通过 | 2026-08-26 |
| `COMBAT-ROOT-SNAPSHOT-529` | 通过 | 搜索根只能在主线程捕获；live 与根投影 continuation 逐项一致。捕获后修改实机能量，后台 Fork 仍保持捕获值。Beam 根、当前历史和 Hook listeners 不再从 worker 惰性构造；钢笔尖增量与双小啃兽普通/增量整战保持通过 | 2026-08-26 |
| `FORK-BOUNDARIES-528` | 通过 | Fork 在克隆前统一拒绝未完成 trace、选择、出牌、Hook 私有事务、延迟历史和遗物记录；钢笔尖与蜷身的瞬时引用不进入稳定节点。臂铠触发卡由 `0.15.0` 依据原版生命周期纠正为可 Fork 的持续状态。配对中途死亡、钢笔尖增量、两组 Hook 差分及双小啃兽增量整战全部通过并零重算 | 2026-08-28 |
| `CONTROLLER-SESSIONS-527` | 通过 | 战斗、搜索和部署状态进入独立会话；取消搜索后旧 callback 不得写回。战斗结束异步 GC 回收与新搜索按完成信号串行。策略快照/取消/重搜/完整部署通过；双小啃兽普通与增量均第 5 回合、两次洗牌、0 药、0 战损并零重算 | 2026-08-26 |
| `REFACTOR-BOUNDARIES-526` | 通过 | 不支持的动态数值、推断 OnPlay 异常与搜索转移异常均 fail-fast 且保留搜索上下文；搜索只消费主线程捕获的策略快照。双小啃兽普通/增量均第 5 回合、两次洗牌、0 药、0 战损并成功复用；药水 17/17、推断选牌 12/12、推断卡 43/43、CalculatedVar 25/25 通过；Smart 与至少一瓶策略均零重算 | 2026-08-26 |
| `DECIMILLIPEDE-LATE-DEATH-REATTACH-524` | 通过 | 肢节先执行正常行动、再于同一敌方回合死亡时，`DEAD_MOVE` 按原状态机直接过渡到 `REATTACH_MOVE`，死亡保留 Power、行动历史、私有死亡阶段和九条 RNG 严格一致；结束回合产生的复活窗口进入通用 Beam 保留。亡灵契约师问题包第 6 回合两药胜利，第 2-6 回合精确复用、零计划外重算；上一份千足虫和双小啃兽普通/增量均保持通过 | 2026-08-25 |
| `DECIMILLIPEDE-DEAD-TO-REATTACH-521` | 通过 | 反馈包中复活肢节实际执行 `DEAD_MOVE` 后，模拟与原版一同推进重接后继，不再把 0 HP 的 `Reviving` 肢节永久冻结在死亡动作。修复前重建整战出现 1 次计划外重算；最终仓库夹具 Smart、Instant/0 秒第 4 回合结束，第 2-4 回合精确复用、零计划外重算，首轮三药计划完整执行且不再反复变化 | 2026-08-25 |
| `MYTES-SMART-INDEPENDENT-POTION-AUDIT-519` | 通过 | 同一异螨开局的统一 Smart Beam 错把无药战损估为 31，选择三瓶药掉 1；独立 Disabled 搜索实际为 0 药掉 11。Smart 选中药水后固定运行独立禁药反事实，纠正为三药只省 10、低于 27 门槛；最终 0 药、第 8 回合、预计/实测均掉 11，第 2-8 回合精确复用、零计划外重算。无药必死救药与既有低损无药回归保持通过 | 2026-08-25 |
| `TWO-TAILED-RAT-RAND-WEIGHT-507` | 通过 | 原版尖啸参数 `3` 按三回合冷却而非三倍权重处理；固定问题包种子的一步差分中，疾病啃咬后的原版与模拟均选择抓挠。用户存档 Medium、Smart、Instant/0 完整自动战斗第 6 回合结束，预计/实测均掉 9，第 2-6 回合精确复用、零计划外重算；500 ms 短搜增量等价与第 2 回合续用通过 | 2026-08-25 |
| `WATERFALL-HORIZON-LIFECYCLE-506` | 通过 | 两个 0.13.27 用户问题包复现节点上限未完成路线在第 12 回合空过，以及蒸汽喷发致死后阵容、无限生命、AI 与 Power 生命周期偏差；0.13.29 以原 seed/250 HP、中档、Smart、Instant/0 秒完整自动执行，分别第 13/16 回合结束且零计划外重算；定向两回合与增量等价回归通过 | 2026-08-25 |
| `RAVENOUS-IMMEDIATE-STUN-505` | 通过 | 玩家回合击杀尸蛞蝓同伴后，幸存者立即以带原行动后继的 `STUNNED` 替换当前意图；敌方 Doom 触发与盛碗虫眩晕循环保持一致。尸蛞蝓完整战斗第 7 回合结束、零计划外重算；增量分叉与完整前缀回放一致 | 2026-08-25 |
| `CHOMPERS-PAIR-TRANSACTION-504` | 通过 | 0.13.25 啃咬机问题包首回合搜索因 pending CardPlay 配对状态在 Fork 处失败；0.13.27 恢复原开战前存档、种子、A10、第二幕、64/67 HP 和首轮行动，以 Medium、Smart、Instant/0 完成普通与增量整战，均第 11 回合结束、零非预期重算、无 cannot fork | 2026-08-25 |
| `SLUMBERING-PAIR-OBLIVION-498-503` | 通过 | 0.13.25 睡眠甲虫问题包的 CardPlay 配对状态在监听 Power 被移除后仍于动作完成边界核销；连续两次湮灭严格使用出牌前 3 层快照而非叠加后的 6 层。0.13.27 最小差分预测/实机灾厄均为 6；原存档 Medium、Smart、Instant/0 完整战斗与增量等价均第 7 回合结束、零非预期重算、无 cannot fork | 2026-08-25 |
| `USER-BUNDLES-PAIRED-THEFT-DEBILITATE-492-497` | 通过 | 外骨骼结算中移除监听器后卡牌配对事务在动作完成边界清空，开启增量等价的原存档整战第 5 回合结束；偷窃草蜢保留已修改牌的 DeckVersion，三候选偷牌 RNG、振翅归零眩晕和后继行动与原版一致，原存档第 5 回合结束；仪式兽虚弱/易伤读取分支 Debilitate，原存档第 9 回合结束；三场完整自动战斗均为 Medium、Smart、Instant/0 且零非预期重算 | 2026-08-25 |
| `QUEEN-ROUTING-OPT-491` | 通过 | 幕末 Boss 深搜为选牌历史保留 50% 策略位，普通深搜 40%，并联合保留威胁集火、潜在能力、下回合资源和关键攻击；女王中档 1 瓶敏捷药、第 11 回合、预计/实测 0 战损、零重算；双小啃兽维持 0/0；发布 ZIP 干净安装后 Steam 可见机甲 12.23s/3.82GB、战损 30、GC 0、最大帧 9.4ms | 2026-08-25 |
| `RAVENOUS-QUEEN-LONGLINE-488-490` | 通过 | 蛞蝓玩家侧击杀与敌方回合末 Doom 都应立即建立 `STUNNED`；`0.13.28` 已纠正旧证据中的玩家侧延迟时序。Buffer/生成牌/球/虚无/Echo Form 状态修复；女王日志重建夹具由 0.13.24 的预计掉 25 作为本轮优化基线 | 2026-08-25 |
| `USER-REPORT-PAELS-ROUTES-484 / CONTROL-MODE-485-486` | 通过 | 熟睡甲虫、虫术师、炮台操作员精确还原佩尔之眼额外回合并全程零重算；凯撒蟹与永世沙漏找到生还路线；计划外重算告警固定在标题右侧，手操后再由求解器接管仍会告警；问题包区分 `solver_only` 与 `manual_plus_solver` 并记录最近完整自动执行回合 | 2026-08-25 |
| `THEFT-ILLUSION-CHOMPERS-480-482` | 通过 | 偷钱地精/偷窃草蜢仅在对应遭遇显示“保牌/保钱、放走”，分支内追踪被盗资源并按模式决定卖血/用药；幻象被灾厄回合末击杀后保留复活意图，佩尔之眼额外回合有遗物标注；啃咬机精确初手与 17 张抽牌堆第 6 回合获胜、预计/实际掉 21、零重算；甲虫汁先消耗人工制品且不施加缩小 | 2026-08-24 |
| `BUG-REPORT-FORENSICS-478` | 通过 | 活动战斗和战后错误时机导出均逐检查点包含 metadata、结构化中途状态、原生战斗包和即时跑局存档，并解析完整 RNG、五牌堆、阵容、历史和当时设置；真实喝药后第 2 回合结果分别记录已喝 1、未来 0 | 2026-08-24 |
| `RADIATE-AND-REQUIRE-ONE-472-477` | 通过 | 崇拜/胜券在王/辉光正确累计本回合星能，辐射连击完整；女王真实第 2 回合检查点当前回合 0 战损斩杀；“至少一瓶”对多瓶路线追加无药反事实并只保留一瓶，喝过药后的重算不重复强制；速度药不残留负敏捷，Smart 致死救药不退化 | 2026-08-24 |
| `INITIAL-OSTY-AND-PAIRED-FORK-465/466` | 通过 | 亡灵契约师持绑定护命匣和赌博筹码时，首回合不重复召唤奥斯蒂，选择后完整状态一致；独白配对状态下攻击 99 荆棘导致中途死亡时清理本次瞬时配对，搜索可继续分叉并按死亡回合暂停 | 2026-08-24 |
| `BUG-REPORT-FORENSICS-469/471` | 通过 | 同一战斗先活动导出、击杀并回主菜单后再次导出；current/recent 均含内存跑局快照、完整 Run RNG、玩家 RNG/odds、检查点、路线和重算审计；战后无当前战斗仍可还原最近一场，同秒连续导出不撞名；5 回合两次洗牌保持零重算 | 2026-08-24 |
| `SMART-POTION-COUNTERFACTUAL-461/463` | 通过 | 淤泥旋螺 Smart 首搜三药但无可信无药终局时追加纯无药审计，找到无药掉 2 后拒绝三药并第 5 回合结束；1 HP 致死反向场景审计确认无药不胜，保留格挡药并第 2 回合获胜 | 2026-08-24 |
| `NECROBINDER-OSTY-RAVENOUS-453/454` | 通过 | 奥斯蒂被连击击杀后由护卫复活，“为你而死”保持 1；蛞蝓同伴死亡后幸存者获得 5 力量并立即进入带原行动后继的 STUNNED；用户完整战斗第 5 回合结束，第 2-5 回合精确复用、零重算 | 2026-08-24 |
| `SECONDARY-END-AND-GENERATION-447-450` | 通过 | 用户储君 Fogmog 存档从错误的 486 回合/470 次洗牌改为第 3 回合结束，完整自动战斗零重算、无生成牌越界；定向主怪击杀+幻象次要敌人首回合正确结束；生成选择 4/4 与实验体三形态保持通过 | 2026-08-24 |
| `REGENT-PRINT-BRANCH-441/446` | 通过 | 固化实机缩小甲虫的储君牌序和生成牌链；两回合具体候选保护窗将节点/选牌/转移/分配约减半，并从基线第 5 回合改善为第 4 回合；完整自动战斗预计/实际掉 3，第 2-4 回合精确复用，零重算；Fisticuffs 日志洪泛为 0 | 2026-08-24 |
| `PRINT-PRUNE-REGRESSIONS-442-445` | 通过 | 生成三选一 4/4、推断卡 15/15、双小啃兽 0 药 0 战损及机甲第 7 回合全部通过；机甲和双小啃兽均零非预期重算 | 2026-08-24 |
| `COMPLETION-AUDIT-428-432` | 通过 | 当前最终 DLL：机甲第 7 回合、预计掉血 36，第 2-7 回合全部精确复用；双小啃兽第 5 回合、两次洗牌、0 药、0 战损；工具盒+烘焙手套+助能三段首回合选择、横祸嵌套选择和千足虫复活均零非预期重算；完整战斗统一使用 Instant/0 秒 | 2026-08-24 |
| `POWER-SHADOW-LIFECYCLE-425-427` | 通过 | Power 数量影子在每次 Hook 批次同步后删除；Burst 回合末不再复活旧层数。重复出牌 11/11 Hook、伤害 Power 十四场及强制 Burst 跨回合续用全部通过 | 2026-08-24 |
| `ROSTER-SOURCE-GATE-408` | 通过 | 原程序集阵容变化共 51 个调用点：47 个单人召唤/逃跑/Osty/宠物来源受支持、3 个 Mock、1 个多人来源、0 未解析；新入口会使普通覆盖门禁失败 | 2026-08-24 |
| `AUTOPLAY-SOURCE-GATE-407` | 通过 | 原程序集 `AutoPlay/AutoPlayFromDrawPile` 共 19 个调用点：18 个单人来源受支持、1 个多人来源、0 未解析；新入口会使普通覆盖门禁失败 | 2026-08-24 |
| `AUTOPLAY-NESTED-CHOICES-403-406` | 通过 | 横祸、破灭、骚动和蒸馏混沌自动打出带选择的牌；搜索规划并实机提交嵌套选择，三场整战零重算并精确复用，药水场完整状态/RNG 差分一致 | 2026-08-24 |
| `COMBAT-CHOICE-SOURCE-GATE-402` | 通过 | 扫描原程序集全部正式模型的 `CardSelectCmd` 调用：85 个调用点中 60 个单人战斗来源受支持、24 个获得遗物流程、1 个多人来源、0 未解析；新来源会使普通覆盖门禁失败 | 2026-08-24 |
| `INITIAL-NATIVE-START-EFFECTS-400/401` | 通过 | 工具盒与七件首回合遗物同场；精确覆盖宝石面具 RNG 移牌、礼炮伤害、谜盒生成、力量电池、扭曲漏斗、石化蟾蜍及低语耳环最多 13 张付费自动出牌；高密度生存者场景另强制覆盖 Vakuu 连续嵌套选牌 | 2026-08-24 |
| `INITIAL-PRE-PLAY-CHOICES-394-398` | 通过 | 从原版首回合 `Start` 阶段搜索并实际提交工具盒、选择悖论、烘焙手套、赌博筹码及助能生存者选择；五场均无玩家界面且进入 `Play` 后完整状态戳一致 | 2026-08-24 |
| `IMBUED-NESTED-CHOICE-393` | 通过 | 助能生存者首回合自动打出，计划并实际弃掉打击；模拟与原版完整牌堆、逐牌状态、资源及 RNG 一致，无玩家默认选择 | 2026-08-24 |
| `SLUMBERING-BEETLE-SILENT-392` | 通过 | 固化用户 6 HP 静默猎手、进阶 10、46/42/89 HP 三敌和原 RNG；盛碗虫完全格挡后进入可见 `STUNNED`，毒伤正确递减熟睡甲虫并切换 `ROLL_OUT_MOVE`；第 2-6 回合全部精确复用，零重算、零战损 | 2026-08-24 |
| `BOWLBUGS-AUTO-CHOICE-391` | 通过 | 直接恢复两个问题包的 BOWLBUGS 开战存档；Mayhem 先固定整批自动牌并给嵌套选择绑定牌身份，牌堆选择使用稳定快照；Custom 5/60s、Instant/0 第 2 回合结束，无选择异常、集合修改和非预期重算 | 2026-08-24 |
| `DECIMILLIPEDE-CONTINUATION-390` | 通过 | 千足虫复活段在下回合可被命中并获得毒雾，第 2 回合精确续用；另使两个 `CONSTRICT_MOVE` 连续两回合叠加 Weak，第 3 回合精确续用；两项均零非预期重算 | 2026-08-24 |
| `KIN-FULL-AUDIT-386D` | 通过 | 用户同族存档按原种子、进阶 10 和满血敌人恢复；中档预算、Instant/0 从首轮执行到第 16 回合结束，追踪之环全程读取分支虚弱，非预期重算为 0 | 2026-08-24 |
| `STATE-CATALOG-GATES-389` | 通过 | `3035` 个 Hook 门禁、分支实机读取、语义动态字段、搜索期状态写入、运行证据和原生重扫边界均为 0 缺口；首回合根状态补齐后剩余 22 个求解接管前快照写入，另有 115 个静态行动图构造器 | 2026-08-24 |
| `CARD-STATE-MUTATION-381-384` | 通过 | 98 张升级卡、8 张降级/恢复卡、5 种附魔及女妖哀嚎/精准打击/践踏/Flatten 入场状态逐字段一致；生成卡保留后续 Hook 监听 | 2026-08-24 |
| `LIFECYCLE-ORDER-378-388` | 通过 | 能量/星费、空手、药水前后、死亡阻止递归均按原版顺序；资源 4 项、药水 17 项、死亡/伤害遗物 11 项通过 | 2026-08-24 |
| `SOLVER-MONSTER-MOVE-AUDIT-387` | 通过 | 57 个补偿怪物行动按永世沙漏、旧日雕像、外骨骼及普通合法宿主分片复跑，完整状态与 RNG 全部一致 | 2026-08-24 |
| `SHRINKER-APPLIER-REUSE-373` | 通过 | 41 HP 缩小甲虫执行无限缩小后，Power 施加者名称、层数和减伤动态值与实机一致；第 2-5 回合全部精确复用，零重算、零未补偿且无错误终局边界 | 2026-08-24 |
| `OBSCURA-VIGOR-EXACT-372` | 通过 | 用户 Obscura 开战前存档恢复进阶 10、第二幕、牌组/遗物/RNG；万向斩组合攻击只消费一次 Vigor，幻象反复复活不产生非法后继；第 10 回合结束且全程零非预期重算。龙涎香 40% 门槛另完成 9 组边界计算 | 2026-08-24 |
| `VANTOM-STRATAGEM-SEARCH-364` | 通过 | 问题包的 Vantom 开战前存档恢复牌组、遗物和 RNG；战略跨洗牌不再抛错，首搜完成 7 回合、2 次洗牌、2623 个选择分支。当前结果仍为死亡线，不冒充生还 | 2026-08-24 |
| `STRATAGEM-SHUFFLE-CHOICE-365` | 通过 | 强制战略 Power 在下回合抽牌时跨洗牌；搜索计划选择打击，实机自动提交，第 2 回合状态精确复用，零重算且无玩家界面 | 2026-08-24 |
| `TEST-SUBJECT-LIVE-END-RISK-362` | 通过 | 开启“战损变差时暂停”并使用用户实验体存档；完整回合末复核计入山铜等遗物格挡，不再把预计 11 误算成 20；第 6 回合剩 66 HP，零误停、零重算 | 2026-08-23 |
| `LIVE-END-TURN-RISK-MINIMAL-363` | 通过 | 原路线以 5 格挡承受 6 点攻击、预计掉 1；提交结束回合前清零格挡后仍正确识别致死，关闭全自动并保留玩家回合 | 2026-08-23 |
| `TEST-SUBJECT-USER-RUN-360` | 通过 | 用户猎手开战前存档精确恢复牌组、遗物、四药水槽与 RNG；实验体三形态第 6 回合结束、实际剩 66 HP，第 2-6 回合全部精确复用，未镜像与非预期重算均为 0 | 2026-08-23 |
| `TEST-SUBJECT-REPTILE-TURN-END-361` | 通过 | 复制药触发爬虫饰品后推进玩家回合末；原版与模拟完整状态一致，复制、临时力量来源和附加力量均按原版移除 | 2026-08-23 |
| `TEMP-PLAN-IMPLEMENTATION-357` | 通过 | 腐臭药水、零权重 RAND、变牌生成 Hook、狠揍嵌套选牌和 Begone 部署事务定向通过；同族智能两药第 13 回合生还，双小啃兽 0 药 0 战损；4 GB No-GC 在第二次搜索前轮换 | 2026-08-23 |
| `DECIMILLIPEDE-REVIVE-350` | 通过 | 从一节 0 HP、下一行动 `REATTACH_MOVE` 开始，实机恢复 25 HP；第 2 回合全体真正死亡，首轮缓存精确复用且零重算 | 2026-08-23 |
| `TEMP-SCULPTOR-MID-358` | 通过 | 精确恢复虔诚雕刻师第 4 回合牌堆、Power、行动历史和 RNG；同回合 0 战损 0 药击杀，未镜像与重算均为 0 | 2026-08-23 |
| `TEMP-KNOWLEDGE-MID-359` | 通过 | 精确恢复知识恶魔第 11 回合状态；同回合 0 战损击杀、零重算，另由 `KNOWLEDGE-DEMON-SEARCH-CHOICE-162` 验证诅咒选择 | 2026-08-23 |
| `LIVE-END-TURN-RISK-PAUSE-270` | 通过 | 同构墨宝安全路线第 2 回合原计划产生 5 格挡；测试在执行后清零格挡，结束回合实机复核得到路线预计 0、当前预计 4 且致死，关闭全自动并保持第 2 回合，不提交结束回合 | 2026-08-23 |
| `INKLETS-RIPPLE-BASIN-269` | 通过 | 复原用户 4 HP、三只墨宝、完整手牌/抽牌、两瓶药和涟漪盆；修复前第 2 回合漏防御并死亡，修复后补打防御，第 2/3 回合精确复用、零战损且第 3 回合结束 | 2026-08-23 |
| `WORSE-RECALCULATION-PAUSE-267` | 通过 | 对一条已算到第 9 回合击杀的旧日雕像完整路线在第 2 回合注入 `4 HP` 状态漂移；记录首个差异、重算预计战损 `32→38`、界面劣化标记，并在执行该回合前关闭全自动 | 2026-08-23 |
| `BUG-REPORT-EXPORT-266` | 通过 | 设置页问题包按游戏口径在后台收集日志、档案、版本和截图，并追加当前战斗精确状态、当前路线、求解器设置和说明；Headless 回归实际创建 ZIP 并逐项验证四个附加条目 | 2026-08-23 |
| `DETAILED-DIAGNOSTIC-LOGS-268` | 通过 | 无设置文件时详细诊断默认关闭且普通日志不含 `[CombatSolver/Debug]`；测试覆盖开启后写出药水槽、分支、层与最终候选诊断 | 2026-08-23 |
| `BYGONE-EFFIGY-CONTINUATION-264` | 通过 | 复原用户旧日雕像的 16 张牌、初始手牌/抽牌顺序、进阶 10、38 HP 与初始弱化；当前版第 2-9 回合全部精确复用并按首轮预测结束。单步 `25` 攻击、`13` 格挡、1 层弱化差分同样通过 | 2026-08-23 |
| `NO-NATIVE-RESCAN-244` | 通过 | `3035` 个钩子中未分析、待实现、缺证据、非通过证据和 `NativeAutoRescan` 均为 `0`；随机生成/选牌、召唤/替换/逃跑、死亡/复活、自动出牌、额外回合、药水槽与私有 AI 均有原生差分或跨回合复用证据 | 2026-08-23 |
| `NIBBITS-NO-RESCAN-246` | 通过 | 固定双小啃兽普通搜索 `1.957s / 360.7MB`，第 `6` 回合、两次洗牌、`0` 药、`0` 战损，第 `2-6` 回合精确复用；增量分叉对完整前缀回放验证同样通过 | 2026-08-23 |
| `MECHA-NO-RESCAN-247` | 通过 | 固定机甲 `5s/60s`：headless `8.207s / 2.212GB`，Steam 正常可见完整 Mod 栈 `9.208s / 2.291GB`，均第 `8` 回合、预计战损 `31`；可见会话 GC `0ms`、最大帧间隔 `11.0ms` | 2026-08-23 |
| `PARTICLE-WALL-TOUCH-176` | 历史通过（旧算法已退役） | `0.12.5` 的同构牌堆从修复前 `1200` 节点、`2` 回合、`NodeLimit` 改为 `619` 节点、`17` 次无进展循环剪枝并第 `3` 回合零损击杀；反向场景保留 `9` 动作首回合击杀。该命名兑现例外不再属于现行设计，下一版本由顶部通用周期/出口 fixture 接替门禁。 | 2026-08-22 |
| `LAGAVULIN-DEPLOY-REPLAN-175` | 通过 | 乐加维林族母睡眠阶段第 `2` 回合精确复用；`BEAT_INTO_SHAPE` 正常路线首回合真实打出；部署中实机拒绝动作会从当前状态重搜而非中止 | 2026-08-22 |
| `PERFORMANCE-PRESETS-170` | 通过 | 无设置文件时默认中档 `5/60s + 6GB`、死亡暂停开、战斗结束暂停关；低档和高档分别完整断言 `2/20s + 4GB` 与 `8/120s + 8GB` 及对应 Beam、节点、出牌分支；自定义保持独立预设身份；双小啃兽维持第 `6` 回合 `0/0` | 2026-08-22 |
| `KNOWLEDGE-BOSS-POLICY-162` | 通过 | 知识恶魔评估 `396` 个选牌分支并计划/执行 `MIND_ROT`，选择结算后第 `2` 回合精确复用；二幕 Boss 与三幕第二 Boss 标记战后回血，三幕首 Boss 与普通战斗不标记；死亡回合暂停保持战斗进行并交还操作权 | 2026-08-22 |
| `NIBBITS-0.12.2-REGRESSION-163` | 通过 | 普通战斗策略不受幕末 Boss 权重影响：固定双小啃兽第 `6` 回合、两次洗牌、`0` 药、`0` 战损、`0` 卖血并第 `3` 回合复用；首轮 `2.119 s / 386 MB` | 2026-08-22 |
| `LONGLINE-0.12.1-161` | 通过 | 双小啃兽第 `6` 回合、两次洗牌、`0` 药、`0` 战损、`0` 卖血并第 `3` 回合复用，首轮 `2.288 s / 386 MB`；机甲 headless `9.820 s / 2.266 GB`，Steam 可见完整 Mod 栈 `9.818 s / 2.350 GB / 0 ms GC / 8.4 ms 最大帧间隔`，均第 `8` 回合、预计战损 `31`、`0` 药并第 `3` 回合复用 | 2026-08-22 |
| `VITAL-SPARK-LIFECYCLE-160` | 通过 | 感染棱柱连续执行 `RADIATE_MOVE → PULSATE_MOVE`；玩家既有技能牌污染保持不丢失，活力火花和逐牌污染从 `2` 同步到 `4`，两步完整牌堆、Power 与 RNG 差分一致 | 2026-08-22 |
| `UNATTENDED-CHOICE-FIXES-159` | 通过 | 雕琢打击唯一候选按实际虚无状态核销、宇宙冷漠按抽牌堆顶核销，二者均进入后续回合精确复用；知识恶魔不可跳过但 `minSelect=0` 的选择自动提交 `MIND_ROT`，玩家获得腐化心智且无界面干预 | 2026-08-22 |
| `RELIC-HIDDEN-STATE-158` | 通过 | 钢笔尖按“愤怒第 `9` 击 → 重锤第 `10` 击×2”首回合 `0` 战损击杀并生成 `钢笔尖×2` 胶囊；百年积木覆盖完全格挡、首次抽 `3` 和不重复触发；金纸覆盖 `5` 次耗尽抽 `1` 与余数 `0`；持久遗物状态门禁为 `0` 缺口 | 2026-08-22 |
| `SOLVER-RUNTIME-ROBUSTNESS-157` | 通过 | 女王战同时持有两瓶迅捷药水时 UI/搜索正常且首回合结束；仅死亡候选显示 `OnlyDeath=True` 并真实死亡；`Instant + 0.05s` 仅内存覆盖，自动执行后恢复 `Normal` 且不生成测试设置文件；Steam 截图确认固定状态列与钢笔尖、音乐盒遗物后缀 | 2026-08-22 |
| `MECHA-VISIBLE-STEAM-145` | 通过 | `0.12.0`、Steam 正常可见会话、用户完整 Mod、固定机甲 `5s/60s`：首轮 `14.736 s / 2.438 GB`、GC `0 ms`、最大帧间隔 `42.5 ms` 且无 `>50 ms` 帧；第 `8` 回合、1 次洗牌、`0` 药、`0` 卖血、Unmirrored=`0`，预计战损从旧基线 `43` 降至 `31`；结束后托管堆约 `259 MB`、工作集约 `2.30 GB` | 2026-08-22 |
| `NIBBITS-ADAPTATION-REGRESSION-139` | 通过 | `0.12.0` 适配层完成后复跑固定双小啃兽：首轮 `1880.2 ms`、第 `6` 回合、两次洗牌、`0` 药、`0` 战损、`0` 卖血、Unmirrored=`0`；第 `3` 回合命中精确复用并无人值守结束战斗 | 2026-08-22 |
| `CARD-EFFECT-SPEC-BATCH-137` | 通过 | 参数化 Power、资源、自伤、最大生命和一次性 Power 消耗共 `46` 条模拟/原生完整快照差分通过 | 2026-08-22 |
| `CARD-COMPLETION-BATCH-123` | 通过 | 补齐卡牌、既有牌选择、击杀奖励、Osty、永久牌面成长和 X 费共 `32` 条差分通过；修复选择后抽牌时来源牌过早进入弃牌堆并参与同次洗牌 | 2026-08-22 |
| `CALCULATED-CARD-BATCH-136` | 通过 | `25` 个代表场景验证牌堆、历史、Power、Osty、能量、弃牌、抽牌、星能和格挡的分支内 CalculatedVar；目录强制全部 `43` 张相关卡牌有公式 | 2026-08-22 |
| `POWER-EFFECT-COMPLETION-135` | 通过 | 毒、灾厄、临时力量、撕裂、吸取、召唤、抽牌/生成触发、墨染、眩晕动态边界、毁灭和必死共 `13` 条差分通过 | 2026-08-22 |
| `CARD-GENERATION-SPEC-BATCH-138` | 通过 | `11` 类固定生成、复制、升级和随机牌堆插入效果的牌堆、牌面及 RNG 差分通过 | 2026-08-22 |
| `CARD-GENERATED-CHOICE-BATCH-121` | 通过 | 富足、发现、类星体和飞溅的生成三选一由求解器分支并自动驱动原生选择界面，`4/4` 完整差分通过 | 2026-08-22 |
| `RELIC-COMPLETION-133` | 通过 | 自成型黏土、破甲钻、螺旋飞镖、苦无、彩虹戒指、手里剑和红头骨组合触发 `6/6` 差分通过 | 2026-08-22 |
| `MECHA-VISIBLE-STEAM-103` | 通过 | 两轮 Steam 正常可见会话、完整用户 Mod 组合、`5s/60s` 与默认 `6 GB` No-GC；首轮 `7.80 s / 1.806 GB`，最终复核轮 `8.49 s / 1.818 GB`，GC 均为 `0 ms`，p95/p99 `16.7 ms`、最大帧间隔不超过 `23.6 ms`，无 `>33/50/100 ms` 帧；第 `6` 回合 `0` 药、预计掉血 `43`，第 `2-6` 回合精确复用。Reset 后托管堆约 `259 MB`、工作集约 `2.51-2.56 GB` | 2026-08-22 |
| `MECHA-FINAL-OPT-097` | 通过 | 固定机甲 `5s/60s`、统一 `12/30` Beam 保持 `1453` 展开、`13338` 转移、第 `6` 回合、`0` 药、预计掉血 `43` 和第 `2-6` 回合精确复用；headless 首轮 `7.02 s / 1.73 GB`、GC `0 ms`、最大帧间隔 `20.3 ms`，相对 `0.11.2` 的 `2.90 GB` 分配下降约 `40.2%` | 2026-08-22 |
| `LONGLINE-DIFF-OPT-101` | 通过 | 双小啃兽固定快照在验证模式对 `2712` 个增量转移同步执行完整前缀回放；状态文本、双指纹、边界、风险、死亡集合与 RNG 全部一致，最终第 `6` 回合 `0` 药、`0` 战损并逐回合复用 | 2026-08-22 |
| `NIBBITS-SNAPSHOT-RELEASE-096` | 通过 | 紧凑 History、稀疏回合末卡牌清理和历史 Simulator 释放后，双小啃兽仍为第 `6` 回合 `0/0`、跨两次洗牌并精确复用；普通搜索约 `1.83 s / 380 MB` | 2026-08-22 |
| `PERF-END-TURN-CLEANUP-FINAL-099` | 通过 | 子弹时间把未打出的打击费用降为 `0` 后执行完整玩家回合结束；模拟与原版均把牌移入弃牌堆并将打击恢复到 `1` 费，验证稀疏清理不会漏掉 EndOfTurn 费用修正 | 2026-08-22 |
| `PERF-DAMAGE-PIPELINE-100` | 通过 | 缩小甲虫两组真实/模拟伤害差分通过，覆盖力量、虚弱、易伤、格挡、回合末伤害与单目标无批量容器路径 | 2026-08-22 |
| `QUEEN-CHAINS-OPT-102` | 通过 | StateStore eager fork、ForkContext 及时释放和历史 Simulator 释放后，女王束缚锁链仍在第 `2/3` 回合逐字段一致，第 `3` 回合命中精确续用 | 2026-08-22 |
| `CORPSE-SLUGS-OPT-103` | 通过 | 紧凑 History、StateStore eager fork 与单目标伤害路径下恢复用户噬尸蛞蝓快照，全自动第 `4` 回合结束，无 pending Power 变化或 Fork 异常 | 2026-08-22 |
| `MECHA-VISIBLE-STEAM-086` | 通过 | Steam 正常可见会话、用户完整 Mod 组合、统一 `12/30` Beam 与默认 `6 GB` No-GC；首轮 `9.57 s / 2.90 GB` 求解线程分配、GC `0 ms`，最大帧间隔 `88.7 ms`、`>50 ms` 为 `1`、无 `>100 ms` 帧；第 `6` 回合 `0` 药、预计掉血 `43`，第 `2-6` 回合精确复用。必备工具第 `2-6` 回合均消费 `1` 个计划选择，每回合末胶囊完成态与部署完成日志齐全；战斗 Reset 后托管堆约 `372 MB`、工作集约 `423 MB` | 2026-08-22 |
| `NIBBITS-UNIFIED30-SOLD-CAP-084` | 通过 | 双小啃兽固定快照验证取消药水独立 Beam 后的统一 `30` 宽度与恢复后的卖血硬剪枝；首轮约 `2.25 s / 439 MB`，剪掉 `102` 条超卖血预算路线，第 `6` 回合 `0` 药、`0` 战损，第 `2-6` 回合精确复用 | 2026-08-22 |
| `QUEEN-CHAINS-REUSE-FINAL-085` | 通过 | 女王与火炬头场景强制女王首轮使用 `PUPPET_STRINGS_MOVE`；束缚锁链施加后第 `2/3` 回合均与首轮预测状态逐字段一致，第 `3` 回合命中 `SEARCH_REUSED`，夹具在命中目标续用后退出战斗 | 2026-08-22 |
| `CORPSE-SLUGS-USER-RUN-073` | 通过 | 从用户 `Y883BRPFJZ05` 跑局快照恢复噬尸蛞蝓战；同伴死亡后的 `RAVENOUS_POWER` 力量变化完成 Power 生命周期结算后再分叉，不再出现 `Cannot fork with pending Power amount changes`，全自动第 `6` 回合结束 | 2026-08-22 |
| `MECHA-VISIBLE-STEAM-071` | 通过 | 由 Steam `-applaunch 2868840` 启动正常可见游戏，加载用户完整 Mod 组合并恢复固定机甲快照；默认 `6 GB` No-GC 下首轮 `9.62 s / 2.32 GB` 求解线程分配、GC `0 ms`，最大帧间隔 `39.4 ms`、`>50/100 ms` 均为 `0`；第 `7` 回合 `0` 药、预计掉血 `40`，第 `2-7` 回合精确复用。战斗 Reset 后托管堆约 `359 MB`、工作集约 `2.78 GB`、私有提交约 `6.04 GB` | 2026-08-22 |
| `NIBBITS-FINAL-071` | 通过 | 默认 `22+7` Beam 下恢复双小啃兽固定快照；首轮约 `1.76 s / 389 MB`，第 `6` 回合 `0` 战损、`0` 药损，第 `2-6` 回合精确复用，最大帧间隔 `22.0 ms`、无 `>50 ms` 帧 | 2026-08-22 |
| `SETTINGS-NOGC-071` | 通过 | 隔离设置写入 `5.5 GB` No-GC 预算后，启动日志解析为 `5,500,000,000 B`，实际区域使用 `5.5 GB / 1.1 GB LOH`；首回合烟雾战斗正常结束，测试设置文件随后删除 | 2026-08-22 |
| `MECHA-FINAL-071` | 通过 | 固定 `MECHA_KNIGHT_ELITE` 跑局快照与 `5s/60s` 配置验证单会话 anytime Beam；首轮约 `11.7 s / 3.14 GB`，第 `9` 回合结束、`0` 药、预计掉血 `40`，第 `2-9` 回合精确续用；GC 约 `3.26 s`、单次低于 `30 ms`，headless 无 `>50 ms` 帧，战斗 Reset 后统一压缩 | 2026-08-22 |
| `SOLVER-ROUTE-HISTORY-071` | 通过 | 固定双小啃兽 `19` 张牌与 RNG 快照；历史固定计数器和单会话搜索保持第 `6` 回合 `0` 战损、`0` 药损、第 `2-6` 回合精确续用；首轮约 `2.04 s / 427 MB` | 2026-08-22 |
| `LONGLINE-DIFF-071` | 通过 | 双小啃兽固定快照对 `5748` 个增量转移同步执行完整前缀回放；状态文本、双指纹、边界、风险、牌堆与 RNG 全部一致，最终第 `6` 回合 `0/0` 并逐回合续用 | 2026-08-22 |
| `SMOKE-FINAL-071` | 通过 | `0.11.0` 最终 Release 部署后，铁甲战士在原版 headless 进程搜索并真实打出打击，首回合结束战斗 | 2026-08-22 |
| `MECHA-RF-SUSTAINED-071` | 通过 | 隔离 headless 同时加载官方 RF `0.13.8`、RitsuMetrics `0.1.37` 和 RitsuLib；`SustainedLowLatency` 下机甲首轮 `11.64 s / 3.15 GB`、GC `3.26 s/22.8 ms max`，无 `>50 ms` 帧，并保持第 `9` 回合、`0` 药、预计掉血 `40`。同栈 `Interactive` 对照出现一次 `142.5 ms` GC/`>100 ms` 帧 | 2026-08-22 |
| `MECHA-MEMORY-FULL-AUTO-FINAL-070` | 通过 | 从用户最新 `current_run.save` 提取牌组、遗物与 RNG，复现 `MECHA_KNIGHT_ELITE` 和 `5s/60s`：首轮 `16.61 s / 4.20 GB` 分配，GC 累计 `5.12 s`、单次最大 `30.5 ms`，主线程最大帧间隔 `43.9 ms` 且 `>50 ms` 为 `0`；第 `2-9` 回合精确续用并真实全自动结束。战斗 Reset 后压缩 `145.5 ms`，托管堆 `110.8 MB`、碎片 `0.16 MB`、工作集约 `2.04 GB` | 2026-08-21 |
| `GC-FREEPLAY-BULLET-TIME-070` | 通过 | 求解作用域隔离 Ritsu 免费出牌全局状态后，子弹时间、整手费用与原生结算差分继续一致 | 2026-08-21 |
| `GC-LONGLINE-DIFF-FINAL-070` | 通过 | 内存修复后长线增量/完整前缀回放逐字段一致，第 `6` 回合 `0/0`、第 `2-6` 回合精确续用；验证模式 GC 累计 `877 ms`、单次最大 `11.6 ms`、主线程最大帧间隔 `28.4 ms` | 2026-08-21 |
| `EMBEDDED-ENGINE-LONGLINE-DIFF-069` | 通过 | RF 本地版共同加载时，从只读快照恢复种子 `BJCZX3J13PZJ`；内置引擎对 `2540` 个实际增量转移逐一执行旧完整前缀回放，状态文本、双指纹、边界、风险和 RNG 全部一致；第 `6` 回合 `0` 战损、`0` 药损，第 `2-6` 回合精确复用 | 2026-08-21 |
| `EMBEDDED-NO-RF-TOOLS-TURN-START-069` | 通过 | 游戏目录已移除 RF；必备工具第 `2-5` 回合的抽 `1` 弃 `1` 全部由首轮 Beam 规划并自动提交、逐回合精确复用，跨边界后的计划外选择由守卫自动处理并重搜，最终第 `11` 回合结束且无玩家干预 | 2026-08-21 |
| `EMBEDDED-NO-RF-ENTROPY-FINAL-069` | 通过 | 游戏目录已移除 RF；熵按路线逐回合选择并变换手牌，真实 `CombatCardSelection` RNG 与预测一致，第 `2-6` 回合精确复用；跨边界后继续自动选择与重搜，最终第 `14` 回合结束 | 2026-08-21 |
| `DECOUPLED-HEADLESS-SMOKE-069` | 通过 | 独立 `APPDATA/LOCALAPPDATA`、Steam 关闭、临时 RitsuLib 投影的原版 `--headless` 进程加载 CombatSolver `0.10.0`，搜索并真实自动打出打击首回合结束战斗；进程退出后临时依赖目录被删除 | 2026-08-21 |
| `TOOLS-TURN-START-FINAL-068` | 通过 | 注入 `1` 层必备工具，真实全自动战斗每回合按搜索结果弃牌；第 `2-4` 回合逐字段精确续用，首轮未补偿项为 `0`，全程无玩家选牌 | 2026-08-21 |
| `ENTROPY-TURN-START-LIVE-068D` | 通过 | 注入 `1` 层熵，真实全自动战斗按路线逐回合选择并随机变换手牌；第 `2-4` 回合牌序、变换结果和 `CombatCardSelection` RNG 精确续用，首轮未补偿项为 `0` | 2026-08-21 |
| `UNPLANNED-TURN-CHOICE-GUARD-068` | 通过 | 注入未进入长线镜像的既定事项回合开始选牌；部署守卫从抽牌堆自动选择最高价值牌、清除旧续用并于第 `2` 回合重搜，随后继续全自动至战斗结束，无玩家选牌 | 2026-08-21 |
| `CARD-ON-PLAY-GAPS-068` | 通过 | 斗篷与匕首、闪躲翻滚连续实机/模拟差分；验证 `10` 格挡、`4` 层下回合格挡、手牌生成小刀及弃牌堆顺序 | 2026-08-21 |
| `CARD-CHOICE-TRANSFORM-FINAL-068` | 通过 | 熵接入通用原位置变换后重跑固定变换选牌实机/模拟差分，牌堆位置、牌状态和变换结果一致 | 2026-08-21 |
| `HUNTER-KILLER-TENDER-067` | 通过 | 猎人杀手战斗中给玩家注入 `1` 层 Tender 和 `8` 张零费小刀；增量分叉/完整回放一致，求解器规划并真实执行 `3` 张后首回合击杀，无 pending Power 队列或搜索失败 | 2026-08-21 |
| `RF-FORK-DIFF-067` | 通过 | Tender 历史补偿循环结算修复后，主长线完整增量差分继续通过；第 `6` 回合 `0` 战损、`0` 药损且第 `2-6` 回合精确续用 | 2026-08-21 |
| `SETTINGS-PERSISTENCE-066` | 通过 | 备份/恢复范围内写入自定义设置，跨进程加载 `1.25/7.5 s`、Beam `7/16`、节点/分支预算及 UI 坐标 `111,77`；搜索 `WEIGHTS` 使用自定义值，运行前后文件 SHA256 一致，测试文件随后删除 | 2026-08-21 |
| `SETTINGS-FINAL-066` | 通过 | 无配置文件时按默认值创建完整设置 UI 并完成搜索/自动出牌；无人测试的非持久暂停开关同步不会创建或误写用户设置文件 | 2026-08-21 |
| `RF-FORK-PERF-065-FINAL` | 通过 | 用户要求两轮最终独立进程样本通过后停止继续统计并定版；两轮均为第 `6` 回合 `0/0`、逐回合精确续用、Gen2 `0`，中点 `208,448,400 B / 1.550 s / 218.7 ms GC`，约 `198.8 MiB` | 2026-08-21 |
| `RF-FORK-DIFF-065-FINAL` | 通过 | 无行动上限与最终 COW/Hook 缓存版本在长线快照比较 `2540` 个实际增量转移和完整前缀回放；完整状态文本、双指纹、边界、风险、死亡集合和 RNG 一致，最终第 `6` 回合 `0` 战损、`0` 药损并逐回合续用 | 2026-08-21 |
| `TWO-STAGE-AGGRESSIVE-064` | 通过 | 测试态将短搜压到 `1 s` 触发深化，生产同款 `24+8` Beam 深化展开 `1219` 节点、命中 `670` 次转移缓存，在搜索空间耗尽时提前返回并将有战损路线改善为第 `6` 回合 `0/0`；默认预算仍为短搜 `3 s`、深化 `20 s` | 2026-08-21 |
| `UNBOUNDED-ACTIONS-065` | 通过 | 清空牌堆后注入 `8` 张零费小刀，求解器在同一回合规划并真实执行全部 `8` 个动作后击杀；证明原 `7` 次回合内行动上限已删除 | 2026-08-21 |
| `TWO-STAGE-UNAVOIDABLE-064` | 通过 | 固定不可避免伤害牌组触发深化；深化无严格改善时保留短结果，主动卖血仍为 `0` | 2026-08-21 |
| `RF-FORK-DIFF-061` | 通过 | 创意工坊 RF 已取消订阅，游戏只加载本地 API `1` / 上游 `598dce0` fork；长线固定快照对 `2541` 个候选同时执行增量分叉和完整前缀回放，状态文本、指纹、边界、风险和 RNG 全部一致，最终第 `6` 回合结束、`0` 战损、`0` 药损 | 2026-08-21 |
| `RF-FORK-PERF-061` | 通过 | 相同长线固定快照在三次干净游戏进程中均通过；性能中位数 `605,129,120 B / 2.394 s / 505.6 ms GC 暂停 / gc2=0`，相对旧基线分别降低约 `89.9% / 89.3% / 95.1%`，通过 `0.9 GB / 5.6 s / 暂停降低 80%` 门槛 | 2026-08-21 |
| `RF-FORK-REGRESSION-061` | 通过 | 本地 fork 最终 DLL 复跑三条卖血策略与瀑布巨兽：防御选择、不可避免伤害、稳定不卖血均保持原断言；瀑布巨兽严格第 `2` 回合结束；日志无 RF 错误、Fork 映射遗漏或搜索失败 | 2026-08-21 |
| `SOLVER-ROUTE-POLICY-060` | 通过 | 从只读快照恢复种子 `BJCZX3J13PZJ` 的完整 `19` 张牌、四件遗物和全部 RNG；首手 `7`、抽牌堆 `12`、敌人 `42/46` 与 `SLICE/HISS` 均与原局日志一致。首轮找到第 `6` 回合结束的 `0` 战损、`0` 药损路线，第 `2-6` 回合全部精确复用；同时验证生存者选牌先于来源牌进入弃牌堆。性能记录为 `2542 replays / 5.99 GB / 22.4 s`，不登记为性能通过 | 2026-08-21 |
| `SOLD-HP-POLICY-BATCH-059` | 通过 | 三份固定牌组验证稳健卖血策略：能直接击杀威胁时选择 `0/5` 而不故意卖 `4`，有防御选择时保持 `0/5` 并剪除超预算路线，无防御时实际掉血但卖血仍为 `0`；跨回合精确复用继续保留累计值 | 2026-08-21 |
| `RELIC-POWER-BATCH-058` | 通过 | 最终 Release DLL 在同一可见游戏 PID 完成两场差分：损毁头盔首次力量翻倍后正确消费状态；不安油灯使首张有效减益牌的全部减益翻倍，并跳过已翻倍临时 Power 的内部力量。另 `4` 个永久 `Deck` 遗物钩子完成全调用点静态审计；覆盖目录达到 `3035/3035`、未分析 `0` | 2026-08-21 |
| `RELIC-REACTIVE-BATCH-057` | 通过 | 最终 Release DLL 在同一真实可见游戏进程连续完成 `11` 个最终请求，关闭 `38` 个未分析遗物条目：`21` 项覆盖药水响应、格挡清空、手牌清空、星能、回合结束、充能球、空手抽牌、伤害倍率及三个动态边界，`17` 项完成源码、初始快照、召唤边界、药水重搜和纯表现静态闭环；覆盖未分析降至 `8` | 2026-08-21 |
| `RELIC-TURN-LIFECYCLE-BATCH-056` | 通过 | 最终 Release DLL 在两个真实可见游戏进程中完成 `8` 个最终请求、`15` 条完整状态差分，关闭 `24` 个未分析遗物条目并纠正 `4` 个 RF 风险/ignored 假精确条目；覆盖私有计数重置、攻击/技能/能力触发、金币与星能、跨回合能量、奥斯蒂、充能球、生成牌、格挡冷却及受伤上限，另 `2` 项完成动态边界与纯表现静态闭环；覆盖未分析降至 `46` | 2026-08-21 |
| `RELIC-TURN-START-BATCH-055` | 通过 | 最终 Release DLL 在同一真实可见游戏 PID 中完成 `5` 个最终请求、`6` 条完整状态差分，关闭 `26` 个未分析遗物条目并纠正孙子兵法 `1` 个 RF ignored 假精确条目：`25` 项覆盖首回合资源/Power/升级/伤害、攻击历史、第 `2/3` 回合能量、私有计数、充能球和精英房条件，`2` 项随机生成牌完成静态边界闭环；覆盖未分析降至 `70` | 2026-08-21 |
| `RELIC-HOOKS-BATCH-054` | 通过 | 最终 Release DLL 在同一真实可见游戏 PID 中连续完成 `7` 个请求、`8` 条完整状态差分，关闭 `20` 个遗物条目：`18` 项覆盖未来回合能量/手牌/格挡、X 值、Power 层数、费用、充能球、伤害、失血与仆从牌倍增，`2` 项完成源码与动态边界静态闭环；覆盖未分析降至 `96` | 2026-08-21 |
| `RELIC-DRAW-STATE-BATCH-053` | 通过 | 最终 Release DLL 在真实可见游戏中完成摆动球/花粉核心六回合周期、怀表 `4/0/3` 张阈值和四件首回合生成遗物快照，共 `10` 条完整状态差分；遗物私有计数另与跨回合复用文本逐回合比较；关闭 `15` 个未分析项并纠正 `1` 个 RF ignored 假精确项 | 2026-08-21 |
| `RELIC-PURE-HOOKS-BATCH-052` | 通过 | 最终 Release DLL 完成组合抽牌、组合最大能量、原生精英房轰鸣海螺及连续第 2/3 回合共 `4` 个最终请求；关闭 `20` 个遗物纯 Hook，定位并修复未来搜索仍读取实时回合号的问题；另 `11` 个范围外/纯表现钩子完成源码与构建静态闭环 | 2026-08-21 |
| `POWER-LIFECYCLE-BATCH-051` | 通过 | 最终 Release DLL 完成 `22` 个最终真实游戏请求，关闭最后 `31` 个未分析 Power 钩子并纠正 `6` 个 RF 忽略/空处理造成的假精确项；`31` 项实机闭环覆盖 Power 数值触发、资源、入场附魔、私有计数、回合末、唤醒/逃跑/选牌动态边界及凯撒巨蟹药水朝向，另 `6` 项仅完成源码与构建静态闭环 | 2026-08-20 |
| `POWER-DEATH-BATCH-050` | 通过 | 最终 Release DLL 在真实可见游戏中完成 `16` 个最终请求，关闭 `37` 个 Power 死亡、移除与清格挡条目：`33` 项实机闭环覆盖蟹之怒、自成型黏土、坚韧之环、饥饿及死亡后复活/召唤/换位等动态边界，`4` 项完成源码与构建静态闭环；开发期向错误宿主注入幻象和饥饿的两次失败保留为夹具审计证据，改用原生宿主后通过 | 2026-08-20 |
| `POWER-TURN-START-BATCH-049` | 通过 | 最终 Release DLL 在同一可见游戏进程连续完成 `19` 个请求，关闭 `22` 个回合开始/生成/随机边界/致死语义并复跑夜魇、绯红披风和野性；固定生成、奥斯蒂、充能球、私有计数、随机目标 RNG 与沙坑致死逐字段一致，七种随机生成/选牌效果均由正式搜索返回 `DynamicResolution`；另 `2` 个虚空形态瞬时时序完成源码与构建静态闭环 | 2026-08-20 |
| `POWER-END-TURN-BATCH-048` | 通过 | 同一可见游戏 PID 连续完成四场、`10` 项差分，关闭 `14` 个 Power 配对/回合末/死亡钩子；覆盖独白力量回收、湮灭施加灾厄、魔法炸弹伤害与施加者死亡，以及神气制胜和胆小的跨回合私有计数 | 2026-08-20 |
| `POWER-NATIVE-HOOKS-BATCH-047` | 通过 | 真实可见游戏对真实态与模拟态调用原生抽牌、最大能量、清格挡和清手牌纯钩子；组合 Power、实际打出友谊后的下一回合、第 031 批资源及手牌生命周期回归全部通过 | 2026-08-20 |
| `POWER-TRIGGER-BATCH-047` | 通过 | 实际打出绯红披风后推进完整下一玩家回合，验证 `1` 点自伤与 `7` 点格挡；另验证野性中途施加会继承本回合已有零费攻击历史 | 2026-08-20 |
| `ENCHANTMENTS-ORB-BATCH-046` | 通过 | 真实可见游戏完成 `13` 种附魔与等离子球的生产模拟/原生生命周期差分；覆盖附魔数值、启用状态、私有成长、重复次数、自动预出牌、清空手牌前降费和回合开始能量 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-046-EXACT` | 通过 | 真实可见游戏逐项强制并差分 `22` 个怪物行动；覆盖攻击、力量、虚弱、烟雾、偷取状态、沙坑、随机向抽牌堆/弃牌堆插牌和相关 RNG | 2026-08-20 |
| `MONSTER-DYNAMIC-BOUNDARY-BATCH-046` | 通过 | 真实可见游戏以召唤、后续 AI 私有状态和牌库改写三类代表运行正式后台搜索；三个行动均在敌方结算后、下一玩家回合建立前返回 `DynamicResolution` | 2026-08-20 |
| `POTION-ON-USE-BATCH-045` | 通过 | 真实可见游戏关闭剩余 `19` 个药水入口及再生生命周期：覆盖手牌/抽牌堆/弃牌堆选择、自动复活、最大生命、锻造、整副打击重复次数和动态生成后同回合重搜；最终狡诈药水由全自动原生使用，作废旧路线后同回合重搜并打出三张升级小刀结束战斗 | 2026-08-20 |
| `POTION-ON-USE-BATCH-044` | 通过 | 真实可见游戏完成 `30` 种确定性药水即时生产模拟/原生使用差分，另完成 `3` 条临时属性生命周期及 `2` 条无法获得能量交互差分；最终火焰药水由搜索选中、全自动通过原生队列使用、消耗槽位并在第 `1` 回合结束战斗 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-043` | 通过 | 同一可见游戏 PID `8404` 连续执行 `10` 个场景、`12` 条生产模拟/原生状态差分，关闭 `17` 个即时与跨回合卡牌条目；另以最终 DLL 的 PID `23060` 验证虚空形态实际被搜索、自动打出并强制推进至第 `2` 回合。覆盖奥斯蒂当前/最大生命、复活、X 费生成与 RNG、选牌、消耗堆连锁、实例 Power、出牌限制、回合结束/抽牌前/自动预出牌阶段和升级分支；另静态关闭 `12` 项 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-042` | 通过 | 同一可见游戏 PID `46700` 连续执行 `21` 个最终场景，关闭 `24` 个确定性 `OnPlay`；验证随机目标/插牌、击杀递归、毒触发、多敌伤害、私有计数，以及选牌、可选空选择、跨牌堆移动、变形、复制、局部费用、重复次数和 `6` 组战斗 RNG | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-041` | 通过 | 同一可见游戏 PID `39540` 连续执行 `6` 个场景、`7` 条最终差分，关闭 `24` 个确定性 `OnPlay`；验证 Power、能量/星能、临时集中及回收、Orb 种类计数、跨牌堆君王之剑、锻造、虚无、小刀、局部费用和整手弃牌；另静态排除 `3` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-040` | 通过 | 同一可见游戏 PID `31288` 连续执行 `6` 组最终差分，关闭 `28` 个确定性 `OnPlay`；验证 Power、能量/星能、按当前格挡延迟获得格挡、目标中毒/湮灭、追踪之刃生成并锻造君王之剑；另静态排除 `3` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-039` | 通过 | 两个可见游戏进程共执行 `13` 组差分，关闭 `23` 个确定性 `OnPlay`；验证临时力量/集中力、自伤资源顺序、多层人工制品移除、双敌全体减益、小刀生成、墨染附魔、全牌堆升级、普通/升级费用持续时间和整手弃牌替换；另静态排除 `3` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-038` | 通过 | 同一可见游戏 PID `43080` 连续执行 `7` 组差分，关闭 `25` 个确定性 `OnPlay`；验证 Power、治疗、能量/星能、疯狂进食的临时力量双 Power，以及无处可逃按已有灾厄分段计算；另静态排除 `11` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-037` | 通过 | 同一可见游戏 PID `39500` 连续执行 `6` 组差分，关闭 `26` 个确定性 `OnPlay`；验证 Power、能量/星能、条件中毒、锻造、子弹时间零费化，以及野性/杂耍中途施加时继承已有攻击计数；另静态排除 `1` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-036` | 通过 | 同一可见游戏 PID 连续执行 `3` 组差分，关闭 `19` 个确定性 `OnPlay`；验证 Power、余像后续格挡、扩容槽位 `3→5`，以及预判临时敏捷的玩家回合末回收；另静态排除 `1` 个多人专属条目 | 2026-08-20 |
| `CARD-ON-PLAY-BATCH-035` | 通过 | 同一可见游戏 PID 连续执行 `5` 组差分，关闭 `20` 个 RF 未镜像的卡牌 `OnPlay`；验证 Power 顺序、X 费用、星能、充能球槽位、锻造与君王之剑伤害、下回合能量及尖啸回合末恢复 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-034` | 通过 | `13` 组可见游戏差分关闭 `22` 个单人 Power 钩子；验证伤害/格挡/费用/牌去向、首次攻击/小刀/格挡预测计数，以及为你而死的单段承伤、`8` 点溢出、多段中途死亡、死亡保留、不可选中和 Power 保留 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-033` | 通过 | 最终 DLL 共执行 `16` 条生产模拟/原生回调差分；覆盖 `20` 个单人 Power 钩子，包括触发上毒、伤害修正、自伤、生命周期、Orb 唤起和私有计数；巨像奇数伤害行动前移除的首次偏差已修复并复测 | 2026-08-20 |
| `SMOKE-002` | 通过 | 铁甲战士进入 `FUZZY_WURM_CRAWLER_WEAK`；敌人 `1 HP`；向手牌注入 `STRIKE_IRONCLAD`；全自动实际出牌并结束战斗；胜利进度写入被隔离 | 2026-08-20 |
| `MONSTER-WATERFALL-001` | 通过 | `0.7.0` 最终 Release：`WATERFALL_GIANT_BOSS` 为 `1 HP` 且拥有 `SteamEruptionPower:10`；提前击杀后依次进入蓄爆与爆炸，严格在第 `2` 回合结束；首轮 `116 replays / 74.65MB`，第二回合精确复用 `0 replays / 0 bytes / 0ms` | 2026-08-21 |
| `MONSTER-AXEBOT-HAMMER-001` | 通过 | 在 `AxebotsNormal` 强制执行 `HAMMER_UPPERCUT_MOVE`；生产预测与真实 `PerformMove` 逐字段比较；确认 `14` 点伤害、`2` 层虚弱、`2` 层脆弱完全一致 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-004` | 通过 | 单次进入 `LivingFogNormal`，按需召唤青蛙骑士、电球头和气态炸弹；连续完成 `7` 个生产预测与真实 `PerformMove` 差分；全部逐字段一致 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-005` | 通过 | 单次进入 `LivingFogNormal`，召唤幽灵船和猎人杀手；连续完成 `6` 个行动差分，并新增四个战斗牌堆的卡牌计数比较；纠缠的 `5` 张暈眩与真实弃牌堆一致 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-006` | 通过 | 单次进入 `LivingFogNormal`，召唤守护机器人、感染棱柱、墨宝和环境组装师；连续完成 `8` 项差分，并按模型比较全场敌人格挡；首次缺少组装师的夹具失败已保留 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-007` | 通过 | 单次进入 `LivingFogNormal`，召唤同族信徒、同族神官和知识恶魔；连续完成 `10` 项差分；思考额外验证攻击后治疗 `30` 及力量增加，随后验证实时战损复核按敌方回合语义消费知识恶魔诅咒计划。最新 runId `901066cdbaaa41329876325cd8a06ad5` | 2026-08-30 |
| `MONSTER-MOVES-BATCH-008` | 通过 | 单次进入 `LivingFogNormal`，召唤乐加维林族母和两种树叶史莱姆；连续完成 `9` 项差分；验证负数力量/敏捷、族母格挡和弃牌堆黏液 `0 → 2 → 3` | 2026-08-20 |
| `MONSTER-MOVES-BATCH-009` | 通过 | 单次进入 `LivingFogNormal`，召唤活体盾、蛮兽、异螨、小啃兽和啃咬机；连续完成 `13` 项差分；验证多段攻击、格挡、力量、易伤及手牌/弃牌堆状态牌 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-010` | 通过 | 单次进入 `LivingFogNormal`，在五个空槽位召唤五类怪物；连续完成 `16` 项差分；验证攻击、格挡、力量、脆弱、手牌灼傷和条件初始状态机 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-011` | 通过 | 单次进入 `LivingFogNormal`，在五个空槽位召唤五类怪物；连续完成 `13` 项差分；修复并验证扭动同时生成感染与力量 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-012` | 通过 | 单次进入 `LivingFogNormal`，在五个空槽位召唤五类怪物；同一场战斗连续完成 `11` 项差分；覆盖攻击、虚弱、脆弱、易伤和正负力量 | 2026-08-20 |
| `UNATTENDED-PROCESS-REUSE-001` | 通过 | 同一 PID `35048` 先执行第 012 批 `11` 项差分，再从主菜单接收巨斧机器人差分；日志依次为 `process_sequence=1/2`，第二批 `reused_process=True`，最后一批才退出 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-013` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `9` 项差分；验证攻击、格挡、仪式、易伤、力量累计和弃牌堆晕眩 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-014` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `10` 项差分；验证多段攻击、格挡、力量、脆弱及荆棘的增加和移除 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-015` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `16` 项差分；验证多段攻击、力量、十张黏液覆体及虚弱/易伤累计 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-016` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `13` 项差分；验证隐藏醒来行动、攻击段数、一张黏液覆体和装弹力量 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-017` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `14` 项差分；验证三代对手的弹幕力量、蟾蜍蝌蚪荆棘增减和藤蔓蹒跚者攻击 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-018` | 通过 | 单次进入 `LivingFogNormal`，召唤五类怪物并连续完成 `15` 项差分；验证入场力量后的攻击、力量累计、格挡以及弃牌堆感染和伤口 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-019` | 通过 | 单次进入 `LivingFogNormal`，先召唤女王依赖怪物，再连续完成 `14` 项差分；新增全场敌方 Power 比较，验证动态敏捷伤害、状态移除和女王群体增益 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-020` | 通过 | 单次进入 `LivingFogNormal`，四类怪物连续完成 `13` 项差分；验证累计力量、格挡、脆弱、埋地，并审计五个实例布尔字段只影响表现 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-021` | 通过 | 同一 PID 先在原生凯撒蟹 Boss 战完成双臂 `10` 项差分，再复用进程进入 `LivingFogNormal` 完成追踪手/噪音机器人 `3` 项；最后才退出游戏 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-022` | 通过 | 单次进入 `LivingFogNormal`，同一只灵魂异鱼按固定顺序完成 `5` 项差分；验证抽牌堆/弃牌堆“呼喚”累计、无实体和易伤 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-023` | 通过 | 单次进入原生 `BowlbugsWeak`，同一只盛碗虫（石）连续验证完全格挡触发 `STUNNED`、昏头转向后恢复头槌、部分格挡不触发 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-024` | 通过 | 单次进入 `LivingFogNormal` 完成 `6` 项连续差分；验证骇鳗活力获得/下一击消费，以及胧光怪全队力量进入三只怪物的后续伤害 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-026` | 通过 | 单次进入 `LivingFogNormal`，同一只永世沙漏连续两次执行“加大力度”；验证力量 `3 → 7`、弃牌堆凋萎 `1 → 2`、伤害总和 `6 → 18`，以及模拟计数器与生成牌等级一致 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-027` | 通过 | 单次进入 `LivingFogNormal`，同一只蛇行扼杀者连续两次执行“缠身”；验证 `3` 层紧缠在玩家回合结束造成 `3 HP`，再次施加后随施加者死亡完整移除 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-028` | 通过 | 同一可见游戏 PID 连续执行两场：第一场两只幽灵骑士验证现有牌/新牌受咒、后施加者死亡不清除、初始施加者死亡才清除；第二场复用进程验证完整回合结束时受咒手牌因虚无进入消耗堆 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-029` | 通过 | 同一 PID `8240` 连续执行五场、共 `9` 条差分；验证缩小/人工制品、缠结附魔与费用、昏眩每回合首张牌限制、无实体伤害上限及 `2 → 1 → 0` 生命周期 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-030` | 通过 | 同一 PID `13628` 连续执行五场、共 `11` 条差分；验证力量/虚弱/易伤伤害、敏捷/脆弱/不可格挡格挡、中毒与催化剂、残影/覆甲生命周期、双倍伤害及缓慢累计清零 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-031` | 通过 | 同一 PID `42532` 连续执行三场、共 `11` 条差分；关闭 `22` 个目录条目，验证下回合能量/抽牌/格挡、禁止抽牌/回能、费用/伤害/格挡修正、保留手牌时序和一次性 Power 生命周期 | 2026-08-20 |
| `MONSTER-MOVES-BATCH-032` | 通过 | 同一 PID `41596` 连续执行六场；关闭 `22` 个持续 Power 生命周期条目，验证抽牌、能量、辉星、Orb、全场目标、生成牌、仪式延迟，以及愤怒复制与活力消费回归 | 2026-08-20 |

运行命令按平台分列。两组命令覆盖同一批场景和断言：Windows 使用仓库保留的 PowerShell 启动器及 PascalCase 参数；Linux 使用原生 Bash 启动器及 GNU kebab-case 参数。修改场景时必须同步更新两组命令，并保持 `ScenarioId / --scenario-id` 集合一致。当前每端各有 `246` 条命令、`245` 个唯一 ScenarioId（`PERFORMANCE-PRESET-HIGH-172` 作为不同历史门禁重复一次）；未提供本机问题包时，矩阵只跳过 `CHOICES-PARADOX-SCROLLS-0160`、`QUEEN-CHAINS-REUSE-FINAL-085` 和 `CORPSE-SLUGS-USER-RUN-073` 这 `3` 个外部快照场景。2026-09-01 的最终 Linux 执行中，唯一非零项发生在游戏内结果已写为 Passed 后的进程身份退出确认；相同命令独立复验返回 `0`，详见顶部未发布门禁表。

全量运行优先使用两端等价的 `tools/run-headless-matrix.ps1` / `tools/run-headless-matrix.sh`：

```text
pwsh -NoProfile -File tools\run-headless-matrix.ps1 -ContinueOnFailure
./tools/run-headless-matrix.sh --continue-on-failure
```

矩阵启动首项前会先精确清理上一次命令留下的 managed marker 进程，再保留下方每条命令声明的 `KeepGameOpen / ExitOnComplete`：同一文档组内，每条都必须收到匹配的静稳 ready ACK 才能复用进程；跨组边界则启动新进程；带退出边界的命令即使因缺少外部夹具而跳过，也会显式清理已有进程。探索性地删除全部边界会越过游戏资源缓存已经验证的安全域，因此不作为可选发布模式。失败或中断时矩阵先清理已认领进程；`ContinueOnFailure / --continue-on-failure` 只决定清理后是否继续下一条，不会重试并掩盖失败。

### Windows（PowerShell 7）

本机问题包不进入仓库：运行 `CHOICES-PARADOX-SCROLLS-0160` 前必须设置 `$env:CHOICES_PARADOX_RUN_SNAPSHOT_PATH` 和 `$env:CHOICES_PARADOX_PROGRESS_SNAPSHOT_PATH`；运行 `QUEEN-CHAINS-REUSE-FINAL-085` 或 `CORPSE-SLUGS-USER-RUN-073` 前必须设置 `$env:RUN_SNAPSHOT_PATH`。

```powershell
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId FIX-141700-TURN-START-CHOICE-FIXED -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 999 -ClearPlayerPiles -CardsPath coverage\unattended\turn-start-choice-once-141700-cards.json -CombatRelicsPath coverage\unattended\turn-start-choice-once-141700-relics.json -PowersPath coverage\unattended\turn-start-choice-once-141700-powers.json -InitialPlayerEnergy 0 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 3000 -SearchMaxDegreeOfParallelismForTest 1 -ExpectedInitialTurnStartChoiceTurn 2 -ExpectedInitialTurnStartChoiceSourceId ENTROPY_POWER -ExpectedInitialTurnStartChoiceCardId STRIKE_IRONCLAD -ExpectedReusedTurn 2 -ExpectedUnexpectedReplansAtMost 0 -StopAfterExpectedReuse -HeadlessFastModeForTest Instant -DeploymentFastModeForTest Instant -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-SILENT-LARGE-DECK-5S -CharacterId SILENT -Seed SEARCH_PERF_SILENT_LARGE_DECK -EncounterId AEONGLASS_BOSS -Ascension 5 -ActIndexForTest 2 -EnemyCurrentHp 512 -InitialEnemyMoveIdsJson '["EBB_MOVE"]' -InitialPlayerHp 65 -InitialPlayerMaxHp 65 -InitialPlayerEnergy 3 -ClearPlayerPiles -CardsPath coverage\unattended\search-performance-silent-large-deck-cards.json -PerformancePresetForTest VeryHigh -PotionPolicyForTest Smart -SearchMaxDegreeOfParallelismForTest 8 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 5000 -MeasureSearchPhases -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-NECROBINDER-POTION-QUICK -CharacterId NECROBINDER -Seed SEARCH_PERF_NECROBINDER_POTION -RunSnapshotPath coverage\unattended\search-performance-necrobinder-potion-heavy-run-snapshot.json -EncounterId AEONGLASS_BOSS -Ascension 10 -ActIndexForTest 2 -EnemyCurrentHp 526 -InitialPlayerHp 41 -CardsJson '[]' -SearchMaxDegreeOfParallelismForTest 8 -PerformancePresetForTest VeryHigh -PotionPolicyForTest RequireAtLeastOne -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 5000 -MeasureSearchPhases -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-COMPLEX-RANDOM-KNIGHTS-CANONICAL-DOP1-2S -CharacterId IRONCLAD -Seed SEARCH_PERF_COMPLEX_RANDOM_KNIGHTS -EncounterId KNIGHTS_ELITE -Ascension 10 -ActIndexForTest 2 -InitialEnemyCurrentHpsJson '[108,97,89]' -InitialEnemyMoveIdsJson '["RAM_MOVE","HEX","POWER_SHIELD_MOVE"]' -InitialPlayerHp 80 -InitialPlayerMaxHp 80 -InitialPlayerEnergy 5 -ClearRunDeck -ClearPlayerPiles -CardsPath coverage\unattended\search-performance-complex-random-knights-cards.json -PerformancePresetForTest Low -PotionPolicyForTest Smart -SearchMaxDegreeOfParallelismForTest 1 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 2000 -MeasureSearchPhases -EnableDetailedDiagnosticLogsForTest 0 -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-COMPLEX-RANDOM-QUEEN-CANONICAL-DOP1-2S -CharacterId REGENT -Seed SEARCH_PERF_COMPLEX_RANDOM_QUEEN -EncounterId QUEEN_BOSS -Ascension 10 -ActIndexForTest 2 -InitialEnemyCurrentHpsJson '[211,419]' -InitialEnemyMoveIdsJson '["STRONG_TACKLE_MOVE","PUPPET_STRINGS_MOVE"]' -InitialPlayerHp 80 -InitialPlayerMaxHp 80 -InitialPlayerEnergy 5 -InitialPlayerStars 3 -ClearRunDeck -ClearPlayerPiles -CardsPath coverage\unattended\search-performance-complex-random-queen-cards.json -PowersPath coverage\unattended\search-performance-complex-random-queen-powers.json -PerformancePresetForTest Low -PotionPolicyForTest Smart -SearchMaxDegreeOfParallelismForTest 1 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 2000 -MeasureSearchPhases -EnableDetailedDiagnosticLogsForTest 0 -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-COMPLEX-RANDOM-TEST-SUBJECT-CANONICAL-DOP1-2S -CharacterId DEFECT -Seed SEARCH_PERF_COMPLEX_RANDOM_TEST_SUBJECT -EncounterId TEST_SUBJECT_BOSS -Ascension 10 -ActIndexForTest 2 -MarkEncounterAsSecondBossForTest -InitialEnemyCurrentHpsJson '[111]' -InitialEnemyMoveIdsJson '["BITE_MOVE"]' -InitialPlayerHp 80 -InitialPlayerMaxHp 80 -InitialPlayerEnergy 5 -ClearRunDeck -ClearPlayerPiles -CardsPath coverage\unattended\search-performance-complex-random-test-subject-cards.json -PowersPath coverage\unattended\search-performance-complex-random-test-subject-powers.json -PerformancePresetForTest Low -PotionPolicyForTest Smart -SearchMaxDegreeOfParallelismForTest 1 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 2000 -MeasureSearchPhases -EnableDetailedDiagnosticLogsForTest 0 -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SEARCH-PERF-COMPLEX-RANDOM-AEONGLASS-DOP1-2S -CharacterId NECROBINDER -Seed SEARCH_PERF_COMPLEX_RANDOM_AEONGLASS -EncounterId AEONGLASS_BOSS -Ascension 10 -ActIndexForTest 2 -EnemyCurrentHp 526 -InitialEnemyMoveIdsJson '["EBB_MOVE"]' -InitialPlayerHp 80 -InitialPlayerMaxHp 80 -InitialPlayerEnergy 5 -ClearRunDeck -ClearPlayerPiles -CardsPath coverage\unattended\search-performance-complex-random-aeonglass-cards.json -PowersPath coverage\unattended\search-performance-complex-random-aeonglass-powers.json -PerformancePresetForTest Low -PotionPolicyForTest Smart -SearchMaxDegreeOfParallelismForTest 1 -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 2000 -MeasureSearchPhases -EnableDetailedDiagnosticLogsForTest 0 -ExpectedInitialSearchPhase Short -ExpectedInitialDeepSearchTriggered 0 -ExpectedInitialExecutableActionCountAtLeast 1 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId BATCH-164623-TRANSFORM-ENTERED-COMBAT-CONTINUATION -CharacterId IRONCLAD -EncounterId LivingFogNormal -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\batch-164623-transform-entered-combat.json -VerifyIncrementalSearch -KeepGameOpen -TimeoutSeconds 180
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId BATCH-164623-TURN-SCOPED-CARD-HISTORY-CONTINUATION-FINAL -CharacterId IRONCLAD -EncounterId LivingFogNormal -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\batch-164623-turn-scoped-card-history.json -VerifyIncrementalSearch -KeepGameOpen -TimeoutSeconds 180
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId BATCH-164623-ORB-DEATH-POWER-ORDER-CONTINUATION -CharacterId DEFECT -EncounterId AXEBOTS_NORMAL -InitialEnemyCurrentHpsJson '[5,50]' -MonsterMoveChecksPath coverage\unattended\batch-164623-orb-death-power-order.json -VerifyIncrementalSearch -ExitOnComplete -TimeoutSeconds 180
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId EXHAUST-CLUTTER-SENTINEL -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 999 -ClearPlayerPiles -InitialPlayerEnergy 2 -CardsJson '[{"cardId":"SCAVENGE","pile":"Hand","count":1},{"cardId":"DAZED","pile":"Hand","count":1},{"cardId":"DEFEND_IRONCLAD","pile":"Hand","count":2}]' -StopAfterInitialSolverResultAssertion -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId POST0201-SCRAPE-NEGATIVE-COST-FINAL -CharacterId DEFECT -EncounterId LivingFogNormal -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\scrape-negative-cost-discard-0201.json -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId POST0201-WHISTLE-STUN-FOLLOW-UP-FINAL -CharacterId DEFECT -EncounterId THE_INSATIABLE_BOSS -EnemyCurrentHp 281 -MonsterMoveChecksPath coverage\unattended\whistle-stun-follow-up-0201.json -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId POST0201-BRAND-POST-CHOICE-POWER-FORK-FINAL -CharacterId IRONCLAD -EncounterId LivingFogNormal -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\brand-post-choice-power-fork-0201.json -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId QOL-CONTROLLER-STOP-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SETTINGS-TABS-LIFECYCLE-NEXT -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId UPLOAD-PROGRESS-CANCEL-CONFIRMATION-NEXT-FINAL -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId UPLOAD-DIRECT-STATE-OWNERSHIP-NEXT -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId UPLOAD-PANEL-MAILBOX-LIFECYCLE-NEXT -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-LOW-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest Low -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-MEDIUM-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest Medium -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-HIGH-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest High -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-VERY-HIGH-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest VeryHigh -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CHOICES-PARADOX-SCROLLS-0160 -CharacterId SILENT -Seed YS41WKT7ZUXS -RunSnapshotPath "$env:CHOICES_PARADOX_RUN_SNAPSHOT_PATH" -ProgressSnapshotPath "$env:CHOICES_PARADOX_PROGRESS_SNAPSHOT_PATH" -EncounterId SCROLLS_OF_BITING_NORMAL -Ascension 10 -ActIndexForTest 2 -InitialPlayerHp 60 -InitialEnemyCurrentHpsJson '[33,37,38,36]' -InitialEnemyMoveIdsJson '["CHEW","MORE_TEETH","CHOMP","MORE_TEETH"]' -ReloadRunRngAfterStateInjection -ForceShortSearchOnly -ShortSearchBudgetOverrideMilliseconds 8000 -ExpectedInitialSetupChoiceCountAtLeast 1 -ExpectedInitialSetupChoiceSourceId CHOICES_PARADOX -ExpectedInitialSetupChoiceTextStartsWith '选择悖论：选择 ' -ExpectedInitialChoiceBranchesEvaluatedAtLeast 5 -StopAfterInitialSetupAssertion -TimeoutSeconds 150 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RINGING-HAVOC-AUTOPLAY-0160-FINAL -CharacterId IRONCLAD -EncounterId LivingFogNormal -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\ringing-havoc-autoplay-0160.json -VerifyIncrementalSearch -ExitOnComplete -TimeoutSeconds 120
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId HEADBUTT-EMPTY-DISCARD-0160 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 50 -ClearPlayerPiles -CardsPath coverage\unattended\headbutt-empty-discard-0160-cards.json -ExpectedPlayedCardId HEADBUTT -ExpectedReusedTurn 2 -StopAfterExpectedReuse -ExpectedUnexpectedReplansAtMost 0 -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 120
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId COSMIC-INDIFFERENCE-EMPTY-DISCARD-0160 -CharacterId REGENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 50 -ClearPlayerPiles -CardsPath coverage\unattended\cosmic-indifference-empty-discard-0160-cards.json -ExpectedPlayedCardId COSMIC_INDIFFERENCE -ExpectedReusedTurn 2 -StopAfterExpectedReuse -ExpectedUnexpectedReplansAtMost 0 -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -ExitOnComplete -TimeoutSeconds 120
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId FAIRY-AUTOMATIC-RESCUE-FINAL2-0150 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 57 -InitialPlayerHp 1 -PotionId FairyInABottle -ClearPlayerPiles -CardsPath coverage\unattended\fairy-automatic-rescue-0150-cards.json -InitialEnemyMoveIdsJson '["FIRST_ACID_GOOP"]' -ExpectedInitialOnlyDeathRoutesFound 0 -ExpectedInitialCombatEndedTurn 2 -ExpectedInitialPotionCount 1 -ExpectedUsedPotionId FAIRY_IN_A_BOTTLE -ExpectedFinishedTurn 2 -ExpectedUnexpectedReplansAtMost 0 -VerifyIncrementalSearch -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 180 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId INSATIABLE-PARETO-CONTROL-150 -CharacterId SILENT -Seed 2DJ8M7EAKQUS -EncounterId THE_INSATIABLE_BOSS -EnemyCurrentHp 341 -InitialPlayerHp 24 -InitialPlayerMaxHp 57 -InitialPlayerEnergy 4 -InitialEnemyMoveIdsJson '["LIQUIFY_GROUND_MOVE"]' -ClearPlayerPiles -CardsPath coverage\unattended\insatiable-malaise-control-150-cards.json -PerformancePresetForTest High -ForceShortSearchOnly -ExpectedInitialHpLostAtMost 0 -ExpectedInitialProjectedBattleHpLostAtMost 7 -ExpectedInitialSoldHp 0 -ExpectedInitialPotionCount 0 -ExpectedInitialSearchedTurnsAtLeast 7 -ExpectedInitialDeathTurnAtLeast 7 -ExpectedInitialFinalEnemyHpAtMost 204 -StopAfterInitialSolverResultAssertion -TimeoutSeconds 180 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId BUILTIN-LISTENER-IDENTITY-533 -CharacterId SILENT -Seed BJCZX3J13PZJ -RunSnapshotPath coverage\unattended\solver-longline-run-snapshot.json -EncounterId NIBBITS_NORMAL -EnemyCurrentHp 999 -InitialPlayerHp 35 -PotionId WeakPotion -ExpectedInitialPotionCount 0 -ExpectedInitialHpLostAtMost 0 -ExpectedInitialProjectedBattleHpLostAtMost 0 -ExpectedInitialShufflesCrossedAtLeast 2 -ExpectedUnexpectedReplansAtMost 0 -ExpectedFinishedTurn 5 -VerifyCombatRootSnapshot -VerifyIncrementalSearch -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MODIFIER-IDENTITY-532 -EncounterId LivingFogNormal -ModifierId MURDEROUS -MonsterMoveChecksPath coverage\unattended\murderous-fabricator-spawn-532.json -ExitOnComplete
pwsh -NoProfile -File tools\run-visible-steam-benchmark.ps1 -TimeoutSeconds 360
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId LAGAVULIN-SLEEP-REUSE-176 -CharacterId REGENT -EncounterId LAGAVULIN_MATRIARCH_BOSS -EnemyCurrentHp 233 -ClearPlayerPiles -CardsJson '[{"cardId":"DEFEND_REGENT","pile":"Hand","count":1}]' -InitialEnemyMoveIdsJson '["SLEEP_MOVE"]' -ExpectedReusedTurn 2 -StopAfterExpectedReuse -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId BEAT-INTO-SHAPE-PLAYABLE-175 -CharacterId REGENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -ClearPlayerPiles -CardsJson '[{"cardId":"BEAT_INTO_SHAPE","pile":"Hand","count":1}]' -ExpectedPlayedCardId BEAT_INTO_SHAPE -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-LOW-171 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest Low -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-HIGH-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest High -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERFORMANCE-PRESET-CUSTOM-173 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -PerformancePresetForTest Custom -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId INFESTED-PRISM-VITAL-SPARK-152 -CharacterId REGENT -EncounterId INFESTED_PRISMS_ELITE -EnemyCurrentHp 171 -MonsterMoveChecksPath coverage\unattended\infested-prism-vital-spark-152.json -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId KNOWLEDGE-DEMON-SEARCH-CHOICE-162 -CharacterId REGENT -EncounterId KNOWLEDGE_DEMON_BOSS -EnemyCurrentHp 399 -ExpectedInitialChoiceBranchesEvaluatedAtLeast 2 -ExpectedInitialPlannedChoiceCardId MIND_ROT -ExpectedInitialActEndingBoss 1 -ExpectedObservedPlayerPowerId MIND_ROT_POWER -StopAfterExpectedPlayerPower -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId KNOWLEDGE-LIVE-END-RISK-INCREMENTAL -CharacterId REGENT -EncounterId KNOWLEDGE_DEMON_BOSS -EnemyCurrentHp 399 -ExpectedInitialChoiceBranchesEvaluatedAtLeast 2 -ExpectedInitialPlannedChoiceCardId MIND_ROT -ExpectedInitialActEndingBoss 1 -ExpectedObservedPlayerPowerId MIND_ROT_POWER -StopAfterExpectedPlayerPower -EnableStopOnWorseRecalculationForTest -ExpectedUnexpectedReplansAtMost 0 -VerifyIncrementalSearch -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId DEATH-TURN-PAUSE-165 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 57 -InitialPlayerHp 1 -ClearPlayerPiles -CardsJson '[]' -InitialEnemyMoveIdsJson '["FIRST_ACID_GOOP"]' -ExpectedInitialOnlyDeathRoutesFound 1 -ExpectedInitialDeathTurn 1 -ExpectedFullAutoPausedAtDeathTurn -TimeoutSeconds 120 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SCULPTING-STRIKE-CHOICE-151 -CharacterId NECROBINDER -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 50 -ClearPlayerPiles -CardsPath coverage\unattended\sculpting-strike-choice-151-cards.json -ExpectedPlayedCardId SCULPTING_STRIKE -ExpectedReusedTurn 2 -StopAfterExpectedReuse -VerifyIncrementalSearch -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId ARMAMENTS-IMPLICIT-UPGRADE-OBSERVATION-534 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 9 -ClearPlayerPiles -CardsJson '[{"cardId":"ARMAMENTS","pile":"Hand","count":1},{"cardId":"STRIKE_IRONCLAD","pile":"Hand","count":1}]' -ExpectedPlayedCardId ARMAMENTS -ExpectedFinishedTurn 1 -ExpectedUnexpectedReplansAtMost 0 -VerifyIncrementalSearch -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0 -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId COSMIC-INDIFFERENCE-IMPLICIT-151B -CharacterId REGENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 50 -ClearPlayerPiles -CardsPath coverage\unattended\cosmic-indifference-choice-151b-cards.json -ExpectedPlayedCardId COSMIC_INDIFFERENCE -ExpectedReusedTurn 2 -StopAfterExpectedReuse -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId DUPLICATE-POTION-SEARCH-147 -CharacterId REGENT -EncounterId QUEEN_BOSS -EnemyCurrentHp 1 -PotionsPath coverage\unattended\duplicate-potions-147.json -ExpectedInitialExecutableActionCountAtLeast 1 -ExpectedFinishedTurnAtMost 5 -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PEN-NIB-ROUTE-148 -CharacterId IRONCLAD -EncounterId MECHA_KNIGHT_ELITE -EnemyCurrentHp 70 -ClearPlayerPiles -CardsPath coverage\unattended\pen-nib-route-148-cards.json -RelicsPath coverage\unattended\pen-nib-route-148-relics.json -VerifyIncrementalSearch -ExpectedInitialExecutableActionCountAtLeast 2 -ExpectedInitialRelicEffectId PEN_NIB -ExpectedInitialRelicEffectSummary '×2' -ExpectedInitialHpLostAtMost 0 -ExpectedInitialProjectedBattleHpLostAtMost 0 -ExpectedInitialOnlyDeathRoutesFound 0 -ExpectedInitialCombatEndedTurn 1 -ExpectedFinishedTurn 1 -DeploymentFastModeForTest Instant -DeploymentInterActionDelaySecondsForTest 0.05 -AssertDeploymentSpeedRestored -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CENTENNIAL-PUZZLE-STATE-149 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 57 -CombatRelicsPath coverage\unattended\centennial-puzzle-state-149-relics.json -MonsterMoveChecksPath coverage\unattended\centennial-puzzle-state-149.json -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId JOSS-PAPER-STATE-150 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\joss-paper-state-150-relics.json -MonsterMoveChecksPath coverage\unattended\joss-paper-state-150.json -TimeoutSeconds 180 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId ONLY-DEATH-ROUTES-150 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 57 -InitialPlayerHp 1 -ClearPlayerPiles -CardsJson '[]' -InitialEnemyMoveIdsJson '["FIRST_ACID_GOOP"]' -ExpectedInitialOnlyDeathRoutesFound 1 -ExpectedPlayerDeath -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERF-END-TURN-CLEANUP-FINAL-099 -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\performance-end-turn-cleanup-098.json -TimeoutSeconds 180 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId PERF-DAMAGE-PIPELINE-100 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-damage.json -TimeoutSeconds 240 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId NIBBITS-UNIFIED30-SOLD-CAP-084 -CharacterId SILENT -Seed BJCZX3J13PZJ -RunSnapshotPath coverage\unattended\solver-longline-run-snapshot.json -EncounterId NIBBITS_NORMAL -EnemyCurrentHp 999 -InitialPlayerHp 35 -PotionId WeakPotion -ExpectedInitialPotionCount 0 -ExpectedInitialProjectedBattleHpLostAtMost 0 -ExpectedInitialSoldHp 0 -ExpectedInitialSoldHpBranchesPrunedAtLeast 1 -ExpectedReusedTurn 3 -ExpectedFinishedTurnAtMost 8 -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId QUEEN-CHAINS-REUSE-FINAL-085 -CharacterId IRONCLAD -Seed Y883BRPFJZ05 -RunSnapshotPath "$env:RUN_SNAPSHOT_PATH" -EncounterId QUEEN_BOSS -EnemyCurrentHp 70 -InitialPlayerHp 80 -InitialEnemyMoveIdsJson '["","PUPPET_STRINGS_MOVE"]' -ExpectedReusedTurn 3 -StopAfterExpectedReuse -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CORPSE-SLUGS-USER-RUN-073 -CharacterId IRONCLAD -Seed Y883BRPFJZ05 -RunSnapshotPath "$env:RUN_SNAPSHOT_PATH" -EncounterId CORPSE_SLUGS_WEAK -EnemyCurrentHp 999 -InitialPlayerHp 80 -ExpectedFinishedTurnAtMost 20 -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MECHA-MEMORY-FULL-AUTO-FINAL-070 -CharacterId SILENT -Seed BJCZX3J13PZJ -RunSnapshotPath coverage\unattended\mecha-knight-memory-run-snapshot.json -EncounterId MECHA_KNIGHT_ELITE -EnemyCurrentHp 300 -InitialPlayerHp 65 -ShortSearchBudgetOverrideMilliseconds 5000 -DeepSearchBudgetOverrideMilliseconds 60000 -ExpectedInitialSearchPhase Deep -ExpectedInitialDeepSearchTriggered 1 -ExpectedInitialTotalElapsedMillisecondsAtMost 25000 -ExpectedInitialTotalAllocatedBytesAtMost 4300000000 -ExpectedInitialGen2CollectionsAtMost 6 -ExpectedInitialTotalGcPauseMillisecondsAtMost 8000 -ExpectedInitialMaxGcPauseMillisecondsAtMost 50 -ExpectedInitialMaxMainThreadFrameGapMillisecondsAtMost 100 -ExpectedReusedTurn 3 -ExpectedFinishedTurnAtMost 9 -MeasureSearchPhases -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SOLVER-ROUTE-POLICY-060 -CharacterId SILENT -Seed BJCZX3J13PZJ -RunSnapshotPath coverage\unattended\solver-longline-run-snapshot.json -EncounterId NIBBITS_NORMAL -EnemyCurrentHp 999 -InitialPlayerHp 35 -PotionId WeakPotion -ExpectedInitialPotionCount 0 -ExpectedInitialProjectedBattleHpLostAtMost 0 -ExpectedReusedTurn 3 -TimeoutSeconds 300 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SOLD-HP-POLICY-BATCH-059-DEFENSE-CHOICE -CharacterId IRONCLAD -EncounterId NIBBITS_WEAK -EnemyCurrentHp 43 -CardsPath coverage\unattended\sold-hp-policy-batch-059-defense-choice.json -ClearPlayerPiles -ExpectedInitialSoldHpAtMost 5 -ExpectedInitialSoldHpBranchesPrunedAtLeast 1 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SOLD-HP-POLICY-BATCH-059-UNAVOIDABLE -CharacterId IRONCLAD -EncounterId NIBBITS_WEAK -EnemyCurrentHp 43 -CardsPath coverage\unattended\sold-hp-policy-batch-059-unavoidable.json -ClearPlayerPiles -ExpectedInitialSoldHp 0 -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SOLD-HP-POLICY-BATCH-059-STABLE-NO-SALE -CharacterId IRONCLAD -EncounterId SLIMES_WEAK -EnemyCurrentHp 999 -CardsPath coverage\unattended\sold-hp-policy-batch-059-active-sale.json -ClearPlayerPiles -ExpectedInitialSoldHp 0 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-POWER-BATCH-058 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-power-batch-058-relics.json -MonsterMoveChecksPath coverage\unattended\relic-power-batch-058.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-POWER-TEMPORARY-BATCH-058 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-power-batch-058-temporary-relics.json -MonsterMoveChecksPath coverage\unattended\relic-power-batch-058-temporary.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-POTION-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-potion-relics.json -PotionCheckPath coverage\unattended\relic-reactive-batch-057-potion.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-TURNS-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-turns-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-turns.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-TURN-END-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -RelicsPath coverage\unattended\relic-reactive-batch-057-turn-end-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-turn-end.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-KUSARIGAMA-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-kusarigama-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-kusarigama.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-STARS-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-stars-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-stars.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-EMOTION-FINAL -CharacterId DEFECT -EnemyCurrentHp 999 -OrbsJson '[{"orbId":"LIGHTNING_ORB","count":1}]' -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-emotion-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-emotion.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-TOP-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-top-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-top.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-UNDYING-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-undying-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-undying.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-PAELS-EYE-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-paels-eye-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-boundary.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-HISTORY-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-history-course-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-boundary.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-REACTIVE-BATCH-057-TOASTY-FINAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-reactive-batch-057-toasty-mittens-relics.json -MonsterMoveChecksPath coverage\unattended\relic-reactive-batch-057-boundary.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-DETERMINISTIC-056 -CharacterId DEFECT -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-deterministic-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-deterministic-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-OSTY-056 -CharacterId NECROBINDER -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-osty-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-osty-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-CYCLES-056 -CharacterId REGENT -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-cycles-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-cycles-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-ATTACKS-056 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-attacks-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-attacks-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-LETTER-056 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-letter-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-letter-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-LEGION-056 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-legion-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-legion-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-GENERATION-056 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-generation-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-generation-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-LIFECYCLE-DAMAGE-056 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-lifecycle-batch-056-damage-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-lifecycle-batch-056-damage-checks.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-START-FIRST-055 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-start-batch-055-first-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-start-batch-055-first-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-START-CYCLES-055 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-start-batch-055-cycles-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-start-batch-055-cycles-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-START-TEA-055 -CharacterId IRONCLAD -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-start-batch-055-tea-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-start-batch-055-tea-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-START-CORE-055 -CharacterId DEFECT -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-start-batch-055-core-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-start-batch-055-core-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-START-CONCH-055 -CharacterId IRONCLAD -EncounterId KnightsElite -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-turn-start-batch-055-conch-relics.json -MonsterMoveChecksPath coverage\unattended\relic-turn-start-batch-055-conch-checks.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-PERSISTENT-054 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-persistent-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-persistent-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-RUNIC-PYRAMID-054 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-runic-pyramid-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-runic-pyramid-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-INFUSED-CORE-054 -CharacterId DEFECT -EnemyCurrentHp 999 -RelicsPath coverage\unattended\relic-infused-core-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-infused-core-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-DAMAGE-054 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-damage-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-damage-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-BOOT-054 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-boot-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-boot-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TUNGSTEN-054 -EnemyCurrentHp 999 -AdditionalMonsterId BowlbugRock -CombatRelicsPath coverage\unattended\relic-tungsten-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-tungsten-batch-054-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-VITRUVIAN-054 -CharacterId NECROBINDER -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-vitruvian-batch-054-relics.json -MonsterMoveChecksPath coverage\unattended\relic-vitruvian-batch-054-checks.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-DRAW-CYCLES-053 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-draw-state-batch-053-cycles-relics.json -MonsterMoveChecksPath coverage\unattended\relic-draw-state-batch-053-cycles-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-POCKETWATCH-053 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-draw-state-batch-053-pocketwatch-relics.json -MonsterMoveChecksPath coverage\unattended\relic-draw-state-batch-053-pocketwatch-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-FIRST-TURN-SNAPSHOT-053 -EnemyCurrentHp 999 -RelicsPath coverage\unattended\relic-draw-state-batch-053-first-turn-relics.json -MonsterMoveChecksPath coverage\unattended\relic-draw-state-batch-053-first-turn-checks.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-PURE-DRAW-052 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-pure-batch-052-draw-relics.json -MonsterMoveChecksPath coverage\unattended\relic-pure-batch-052-draw-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-PURE-ENERGY-052 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-pure-batch-052-energy-relics.json -MonsterMoveChecksPath coverage\unattended\relic-pure-batch-052-energy-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-TURN-CONDITIONS-052 -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-pure-batch-052-turn-relics.json -MonsterMoveChecksPath coverage\unattended\relic-pure-batch-052-turn-checks.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId RELIC-BOOMING-CONCH-052 -EncounterId KnightsElite -EnemyCurrentHp 999 -CombatRelicsPath coverage\unattended\relic-pure-batch-052-booming-relics.json -MonsterMoveChecksPath coverage\unattended\relic-pure-batch-052-booming-checks.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-OSTY -CharacterId NECROBINDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-osty.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-DIRGE -CharacterId NECROBINDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-dirge.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-CHOICES -CharacterId NECROBINDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-choices.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-AUTOPLAY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-autoplay.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-POWERS -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-powers.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-NORMALITY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-normality.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-ENTHRALLED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-enthralled.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-RETURN-AUTOPLAY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-return-autoplay.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-UPGRADED -CharacterId NECROBINDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-NIGHTMARE-LIFECYCLE -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-043-nightmare-lifecycle.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-043-VOID-FORM -CharacterId IRONCLAD -EnemyCurrentHp 18 -CardId VOID_FORM -ClearPlayerHand -ExpectedPlayedCardId VOID_FORM -ExpectedFinishedTurn 2 -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-BOUNCING-FLASK -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-bouncing-flask.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-DIRECT-A -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-direct-a.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-OUTBREAK -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-outbreak.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-ECHOING-SLASH -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-echoing-slash.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-OMNISLICE -CharacterId IRONCLAD -EnemyCurrentHp 999 -AdditionalMonsterId CalcifiedCultist -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-omnislice.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-TRANSFORM -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-transform.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-HAND -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-hand.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-STATE -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-state.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-PILES -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-piles.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-TRANSFORM-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-transform-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-PURITY-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-purity-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-CHOICE-ZERO-OPTIONAL -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-choice-zero-optional.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-HIDDEN-DAGGERS-EMPTY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-hidden-daggers-empty.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-BRAND-EMPTY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-brand-empty.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-SCAVENGE-EMPTY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-scavenge-empty.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-FRANTIC-ESCAPE -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-frantic-escape.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-ECHOING-SLASH-KILL -CharacterId IRONCLAD -EnemyCurrentHp 999 -AdditionalMonsterId CalcifiedCultist -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-echoing-slash-kill.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-DIRECT-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-direct-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-END-OF-DAYS-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -AdditionalMonsterId CalcifiedCultist -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-end-of-days-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-042-END-OF-DAYS -CharacterId IRONCLAD -EnemyCurrentHp 999 -AdditionalMonsterId CalcifiedCultist -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-042-end-of-days.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-POWER-A -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-power-set-a.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-POWER-B -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-power-set-b.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-SYNCHRONIZE -CharacterId DEFECT -EnemyCurrentHp 999 -OrbsJson '[{"orbId":"LIGHTNING_ORB","count":1},{"orbId":"FROST_ORB","count":1}]' -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-synchronize.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-TURBO-SLEEVE -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-turbo-up-my-sleeve.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-SUMMON-FORTH -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-summon-forth.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-041-SHADOW-STEP -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-041-shadow-step.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-POWER-A -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-power-set-a.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-POWER-B -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-power-set-b.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-PALE-BLUE-DOT -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-pale-blue-dot.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-RESOURCES -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-resources-and-targets.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-SEEKING-EDGE -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-seeking-edge.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-040-SIGNAL-BOOST -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-040-signal-boost.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-POWER -CharacterId NECROBINDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-power-set.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-TARGET -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-target-effects.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-TARGET-LIFECYCLE -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-target-lifecycle.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-RESOURCES -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-resources.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-SHIVS -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-shivs.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-INK -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-blade-of-ink.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-APOTHEOSIS -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-apotheosis.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-ENLIGHTENMENT -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-enlightenment.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-ENLIGHTENMENT-UPGRADED -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-enlightenment-upgraded.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-STORM -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-storm-of-steel.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-HOTFIX-LIFECYCLE -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-hotfix-lifecycle.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-EXPOSE-ARTIFACT -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-expose-artifact.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-039-HAZE-MULTI -CharacterId SILENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-039-haze-multi.json -AdditionalMonsterId DampCultist -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-A -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-power-set-a.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-B -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-power-set-b.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-DANSE -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-danse-macabre.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-PLANNER -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-master-planner.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-SERPENT -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-serpent-form.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-STORM -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-storm.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-038-NO-ESCAPE -EnemyCurrentHp 80 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-038-no-escape.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-POWER-A -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-power-set-a.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-POWER-B -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-power-set-b.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-SPECIAL -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-special-effects.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-BULLET-TIME -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-bullet-time.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-FERAL-HISTORY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-feral-history.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-037-JUGGLING-HISTORY -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-037-juggling-history.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-036-A -CharacterId DEFECT -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-036-power-set-a.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-036-B -CharacterId DEFECT -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-036-power-set-b.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-036-ANTICIPATE -CharacterId DEFECT -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-036-anticipate-lifecycle.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-035-SELF -CharacterId IRONCLAD -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-035-self-powers.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-035-TARGET -CharacterId IRONCLAD -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-035-target-powers.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-035-BULK-UP -CharacterId DEFECT -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-035-bulk-up.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-035-REGENT -CharacterId REGENT -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-035-regent.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId CARD-ON-PLAY-BATCH-035-PIERCING-WAIL -CharacterId IRONCLAD -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\card-on-play-batch-035-piercing-wail-lifecycle.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-ACCURACY -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-accuracy.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-BLOCK -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-block.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-COST-LOCATION -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-cost-location.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-HANG -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-hang.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-HARD-TO-KILL -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-hard-to-kill.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-LEADERSHIP -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-leadership.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-LETHALITY -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-lethality.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-ONE-FOR-ALL -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-one-for-all.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-PHANTOM-BLADES -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-phantom-blades.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-SOAR -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-soar.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-TRACKING -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-tracking.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-CALCIFY -CharacterId NECROBINDER -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-calcify.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-034-DIE-FOR-YOU -CharacterId NECROBINDER -EnemyCurrentHp 50 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-034-die-for-you.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId SMOKE-002
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-WATERFALL-001 -EncounterId WATERFALL_GIANT_BOSS -PowerId STEAM_ERUPTION_POWER -PowerAmount 10 -ExpectedFinishedTurn 2
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-AXEBOT-HAMMER-001 -EncounterId AxebotsNormal -MonsterMoveId HAMMER_UPPERCUT_MOVE -ExpectedPlayerHpLoss 14 -ExpectedPlayerPowersJson '{"WEAK_POWER":2,"FRAIL_POWER":2}'
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-004 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-004.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-005 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-005.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-006 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-006.json -AdditionalMonsterId Fabricator
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-007 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-007.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-008 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-008.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-009 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-009.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-010 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-010.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-011 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-011.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-012 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-012.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-013 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-013.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-014 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-014.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-015 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-015.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-016 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-016.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-017 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-017.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-018 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-018.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-019 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-019.json -AdditionalMonsterId TorchHeadAmalgam
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-020 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-020.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-021-KAISER -EncounterId KaiserCrabBoss -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-021-kaiser.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-021-SUPPORT -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-021-support.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-022 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-022.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-023 -EncounterId BowlbugsWeak -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-023.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-024 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-024.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-026 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-026.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-027 -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-027.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-028-LIFECYCLE -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-028-lifecycle.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-028-TURN-END -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-028-turn-end.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-029-SHRINK -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-029-lifecycle.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-029-ARTIFACT -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-029-artifact.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-029-TANGLED -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-029-tangled.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-029-RINGING -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-029-ringing.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-029-INTANGIBLE -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-029-intangible.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-030-DAMAGE -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-damage.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-030-BLOCK -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-block.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-030-POISON -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-poison.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-030-BLOCK-LIFECYCLE -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-block-lifecycle.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-030-SLOW -EncounterId LivingFogNormal -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-030-slow.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-031-RESOURCES -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-031-resources.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-031-MODIFIERS -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-031-modifiers.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-031-LIFECYCLE -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-031-lifecycle.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-RESOURCES -CharacterId DEFECT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-resources.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-STARS -CharacterId REGENT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-stars.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-START-POWERS -CharacterId IRONCLAD -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-start-powers.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-COOLANT -CharacterId DEFECT -EnemyCurrentHp 999 -OrbsJson '[{"orbId":"LIGHTNING_ORB","count":1},{"orbId":"FROST_ORB","count":1}]' -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-coolant.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-GLOBAL -AdditionalMonsterId TurretOperator -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-global.json -KeepGameOpen
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-032-RITUAL -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-032-ritual.json -ExitOnComplete
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-COLOSSUS -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-colossus.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-TAINTED -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-tainted.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-CONCOCT -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-concoct.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-CORROSIVE-WAVE -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-corrosive-wave.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-DEMISE -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-demise.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-DISINTEGRATION -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-disintegration.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-LIFECYCLE -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-lifecycle.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-ORBS-NEMESIS -CharacterId DEFECT -EnemyCurrentHp 999 -OrbsJson '[{"orbId":"FROST_ORB","count":1}]' -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-orbs-nemesis.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-TENDER -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-tender.json
pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId MONSTER-MOVES-BATCH-033-JUGGLING -EnemyCurrentHp 999 -MonsterMoveChecksPath coverage\unattended\monster-moves-batch-033-juggling.json -ExitOnComplete
```

### Linux（Bash）

```bash
./tools/run-unattended-test.sh --scenario-id FIX-141700-TURN-START-CHOICE-FIXED --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 999 --clear-player-piles --cards-path coverage/unattended/turn-start-choice-once-141700-cards.json --combat-relics-path coverage/unattended/turn-start-choice-once-141700-relics.json --powers-path coverage/unattended/turn-start-choice-once-141700-powers.json --initial-player-energy 0 --force-short-search-only --short-search-budget-override-milliseconds 3000 --search-max-degree-of-parallelism-for-test 1 --expected-initial-turn-start-choice-turn 2 --expected-initial-turn-start-choice-source-id ENTROPY_POWER --expected-initial-turn-start-choice-card-id STRIKE_IRONCLAD --expected-reused-turn 2 --expected-unexpected-replans-at-most 0 --stop-after-expected-reuse --headless-fast-mode-for-test Instant --deployment-fast-mode-for-test Instant --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-SILENT-LARGE-DECK-5S --character-id SILENT --seed SEARCH_PERF_SILENT_LARGE_DECK --encounter-id AEONGLASS_BOSS --ascension 5 --act-index-for-test 2 --enemy-current-hp 512 --initial-enemy-move-ids-json '["EBB_MOVE"]' --initial-player-hp 65 --initial-player-max-hp 65 --initial-player-energy 3 --clear-player-piles --cards-path coverage/unattended/search-performance-silent-large-deck-cards.json --performance-preset-for-test VeryHigh --potion-policy-for-test Smart --search-max-degree-of-parallelism-for-test 8 --force-short-search-only --short-search-budget-override-milliseconds 5000 --measure-search-phases --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-NECROBINDER-POTION-QUICK --character-id NECROBINDER --seed SEARCH_PERF_NECROBINDER_POTION --run-snapshot-path coverage/unattended/search-performance-necrobinder-potion-heavy-run-snapshot.json --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2 --enemy-current-hp 526 --initial-player-hp 41 --cards-json '[]' --search-max-degree-of-parallelism-for-test 8 --performance-preset-for-test VeryHigh --potion-policy-for-test RequireAtLeastOne --force-short-search-only --short-search-budget-override-milliseconds 5000 --measure-search-phases --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-COMPLEX-RANDOM-KNIGHTS-CANONICAL-DOP1-2S --character-id IRONCLAD --seed SEARCH_PERF_COMPLEX_RANDOM_KNIGHTS --encounter-id KNIGHTS_ELITE --ascension 10 --act-index-for-test 2 --initial-enemy-current-hps-json '[108,97,89]' --initial-enemy-move-ids-json '["RAM_MOVE","HEX","POWER_SHIELD_MOVE"]' --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 5 --clear-run-deck --clear-player-piles --cards-path coverage/unattended/search-performance-complex-random-knights-cards.json --performance-preset-for-test Low --potion-policy-for-test Smart --search-max-degree-of-parallelism-for-test 1 --force-short-search-only --short-search-budget-override-milliseconds 2000 --measure-search-phases --enable-detailed-diagnostic-logs-for-test 0 --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-COMPLEX-RANDOM-QUEEN-CANONICAL-DOP1-2S --character-id REGENT --seed SEARCH_PERF_COMPLEX_RANDOM_QUEEN --encounter-id QUEEN_BOSS --ascension 10 --act-index-for-test 2 --initial-enemy-current-hps-json '[211,419]' --initial-enemy-move-ids-json '["STRONG_TACKLE_MOVE","PUPPET_STRINGS_MOVE"]' --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 5 --initial-player-stars 3 --clear-run-deck --clear-player-piles --cards-path coverage/unattended/search-performance-complex-random-queen-cards.json --powers-path coverage/unattended/search-performance-complex-random-queen-powers.json --performance-preset-for-test Low --potion-policy-for-test Smart --search-max-degree-of-parallelism-for-test 1 --force-short-search-only --short-search-budget-override-milliseconds 2000 --measure-search-phases --enable-detailed-diagnostic-logs-for-test 0 --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-COMPLEX-RANDOM-TEST-SUBJECT-CANONICAL-DOP1-2S --character-id DEFECT --seed SEARCH_PERF_COMPLEX_RANDOM_TEST_SUBJECT --encounter-id TEST_SUBJECT_BOSS --ascension 10 --act-index-for-test 2 --mark-encounter-as-second-boss-for-test --initial-enemy-current-hps-json '[111]' --initial-enemy-move-ids-json '["BITE_MOVE"]' --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 5 --clear-run-deck --clear-player-piles --cards-path coverage/unattended/search-performance-complex-random-test-subject-cards.json --powers-path coverage/unattended/search-performance-complex-random-test-subject-powers.json --performance-preset-for-test Low --potion-policy-for-test Smart --search-max-degree-of-parallelism-for-test 1 --force-short-search-only --short-search-budget-override-milliseconds 2000 --measure-search-phases --enable-detailed-diagnostic-logs-for-test 0 --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SEARCH-PERF-COMPLEX-RANDOM-AEONGLASS-DOP1-2S --character-id NECROBINDER --seed SEARCH_PERF_COMPLEX_RANDOM_AEONGLASS --encounter-id AEONGLASS_BOSS --ascension 10 --act-index-for-test 2 --enemy-current-hp 526 --initial-enemy-move-ids-json '["EBB_MOVE"]' --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 5 --clear-run-deck --clear-player-piles --cards-path coverage/unattended/search-performance-complex-random-aeonglass-cards.json --powers-path coverage/unattended/search-performance-complex-random-aeonglass-powers.json --performance-preset-for-test Low --potion-policy-for-test Smart --search-max-degree-of-parallelism-for-test 1 --force-short-search-only --short-search-budget-override-milliseconds 2000 --measure-search-phases --enable-detailed-diagnostic-logs-for-test 0 --expected-initial-search-phase Short --expected-initial-deep-search-triggered 0 --expected-initial-executable-action-count-at-least 1 --stop-after-initial-solver-result-assertion --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id BATCH-164623-TRANSFORM-ENTERED-COMBAT-CONTINUATION --character-id IRONCLAD --encounter-id LivingFogNormal --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/batch-164623-transform-entered-combat.json --verify-incremental-search --keep-game-open --timeout-seconds 180
./tools/run-unattended-test.sh --scenario-id BATCH-164623-TURN-SCOPED-CARD-HISTORY-CONTINUATION-FINAL --character-id IRONCLAD --encounter-id LivingFogNormal --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/batch-164623-turn-scoped-card-history.json --verify-incremental-search --keep-game-open --timeout-seconds 180
./tools/run-unattended-test.sh --scenario-id BATCH-164623-ORB-DEATH-POWER-ORDER-CONTINUATION --character-id DEFECT --encounter-id AXEBOTS_NORMAL --initial-enemy-current-hps-json '[5,50]' --monster-move-checks-path coverage/unattended/batch-164623-orb-death-power-order.json --verify-incremental-search --exit-on-complete --timeout-seconds 180
./tools/run-unattended-test.sh --scenario-id POST0201-SCRAPE-NEGATIVE-COST-FINAL --character-id DEFECT --encounter-id LivingFogNormal --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/scrape-negative-cost-discard-0201.json --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id POST0201-WHISTLE-STUN-FOLLOW-UP-FINAL --character-id DEFECT --encounter-id THE_INSATIABLE_BOSS --enemy-current-hp 281 --monster-move-checks-path coverage/unattended/whistle-stun-follow-up-0201.json --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id POST0201-BRAND-POST-CHOICE-POWER-FORK-FINAL --character-id IRONCLAD --encounter-id LivingFogNormal --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/brand-post-choice-power-fork-0201.json --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id QOL-CONTROLLER-STOP-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --verify-controller-session-lifecycle --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SETTINGS-TABS-LIFECYCLE-NEXT --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --verify-controller-session-lifecycle --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id UPLOAD-PROGRESS-CANCEL-CONFIRMATION-NEXT-FINAL --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --verify-controller-session-lifecycle --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id UPLOAD-DIRECT-STATE-OWNERSHIP-NEXT --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --verify-controller-session-lifecycle --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id UPLOAD-PANEL-MAILBOX-LIFECYCLE-NEXT --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --verify-controller-session-lifecycle --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-LOW-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test Low --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-MEDIUM-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test Medium --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-HIGH-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test High --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-VERY-HIGH-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test VeryHigh --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CHOICES-PARADOX-SCROLLS-0160 --character-id SILENT --seed YS41WKT7ZUXS --run-snapshot-path "${CHOICES_PARADOX_RUN_SNAPSHOT_PATH:?set CHOICES_PARADOX_RUN_SNAPSHOT_PATH}" --progress-snapshot-path "${CHOICES_PARADOX_PROGRESS_SNAPSHOT_PATH:?set CHOICES_PARADOX_PROGRESS_SNAPSHOT_PATH}" --encounter-id SCROLLS_OF_BITING_NORMAL --ascension 10 --act-index-for-test 2 --initial-player-hp 60 --initial-enemy-current-hps-json '[33,37,38,36]' --initial-enemy-move-ids-json '["CHEW","MORE_TEETH","CHOMP","MORE_TEETH"]' --reload-run-rng-after-state-injection --force-short-search-only --short-search-budget-override-milliseconds 8000 --expected-initial-setup-choice-count-at-least 1 --expected-initial-setup-choice-source-id CHOICES_PARADOX --expected-initial-setup-choice-text-starts-with '选择悖论：选择 ' --expected-initial-choice-branches-evaluated-at-least 5 --stop-after-initial-setup-assertion --timeout-seconds 150 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RINGING-HAVOC-AUTOPLAY-0160-FINAL --character-id IRONCLAD --encounter-id LivingFogNormal --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/ringing-havoc-autoplay-0160.json --verify-incremental-search --exit-on-complete --timeout-seconds 120
./tools/run-unattended-test.sh --scenario-id HEADBUTT-EMPTY-DISCARD-0160 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 50 --clear-player-piles --cards-path coverage/unattended/headbutt-empty-discard-0160-cards.json --expected-played-card-id HEADBUTT --expected-reused-turn 2 --stop-after-expected-reuse --expected-unexpected-replans-at-most 0 --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 120
./tools/run-unattended-test.sh --scenario-id COSMIC-INDIFFERENCE-EMPTY-DISCARD-0160 --character-id REGENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 50 --clear-player-piles --cards-path coverage/unattended/cosmic-indifference-empty-discard-0160-cards.json --expected-played-card-id COSMIC_INDIFFERENCE --expected-reused-turn 2 --stop-after-expected-reuse --expected-unexpected-replans-at-most 0 --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --exit-on-complete --timeout-seconds 120
./tools/run-unattended-test.sh --scenario-id FAIRY-AUTOMATIC-RESCUE-FINAL2-0150 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 57 --initial-player-hp 1 --potion-id FairyInABottle --clear-player-piles --cards-path coverage/unattended/fairy-automatic-rescue-0150-cards.json --initial-enemy-move-ids-json '["FIRST_ACID_GOOP"]' --expected-initial-only-death-routes-found 0 --expected-initial-combat-ended-turn 2 --expected-initial-potion-count 1 --expected-used-potion-id FAIRY_IN_A_BOTTLE --expected-finished-turn 2 --expected-unexpected-replans-at-most 0 --verify-incremental-search --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 180 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id INSATIABLE-PARETO-CONTROL-150 --character-id SILENT --seed 2DJ8M7EAKQUS --encounter-id THE_INSATIABLE_BOSS --enemy-current-hp 341 --initial-player-hp 24 --initial-player-max-hp 57 --initial-player-energy 4 --initial-enemy-move-ids-json '["LIQUIFY_GROUND_MOVE"]' --clear-player-piles --cards-path coverage/unattended/insatiable-malaise-control-150-cards.json --performance-preset-for-test High --force-short-search-only --expected-initial-hp-lost-at-most 0 --expected-initial-projected-battle-hp-lost-at-most 7 --expected-initial-sold-hp 0 --expected-initial-potion-count 0 --expected-initial-searched-turns-at-least 7 --expected-initial-death-turn-at-least 7 --expected-initial-final-enemy-hp-at-most 204 --stop-after-initial-solver-result-assertion --timeout-seconds 180 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id BUILTIN-LISTENER-IDENTITY-533 --character-id SILENT --seed BJCZX3J13PZJ --run-snapshot-path coverage/unattended/solver-longline-run-snapshot.json --encounter-id NIBBITS_NORMAL --enemy-current-hp 999 --initial-player-hp 35 --potion-id WeakPotion --expected-initial-potion-count 0 --expected-initial-hp-lost-at-most 0 --expected-initial-projected-battle-hp-lost-at-most 0 --expected-initial-shuffles-crossed-at-least 2 --expected-unexpected-replans-at-most 0 --expected-finished-turn 5 --verify-combat-root-snapshot --verify-incremental-search --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MODIFIER-IDENTITY-532 --encounter-id LivingFogNormal --modifier-id MURDEROUS --monster-move-checks-path coverage/unattended/murderous-fabricator-spawn-532.json --exit-on-complete
./tools/run-visible-steam-benchmark.sh --timeout-seconds 360
./tools/run-unattended-test.sh --scenario-id LAGAVULIN-SLEEP-REUSE-176 --character-id REGENT --encounter-id LAGAVULIN_MATRIARCH_BOSS --enemy-current-hp 233 --clear-player-piles --cards-json '[{"cardId":"DEFEND_REGENT","pile":"Hand","count":1}]' --initial-enemy-move-ids-json '["SLEEP_MOVE"]' --expected-reused-turn 2 --stop-after-expected-reuse --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id BEAT-INTO-SHAPE-PLAYABLE-175 --character-id REGENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --clear-player-piles --cards-json '[{"cardId":"BEAT_INTO_SHAPE","pile":"Hand","count":1}]' --expected-played-card-id BEAT_INTO_SHAPE --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-LOW-171 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test Low --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-HIGH-172 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test High --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERFORMANCE-PRESET-CUSTOM-173 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1 --performance-preset-for-test Custom --expected-finished-turn 1 --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id INFESTED-PRISM-VITAL-SPARK-152 --character-id REGENT --encounter-id INFESTED_PRISMS_ELITE --enemy-current-hp 171 --monster-move-checks-path coverage/unattended/infested-prism-vital-spark-152.json --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id KNOWLEDGE-DEMON-SEARCH-CHOICE-162 --character-id REGENT --encounter-id KNOWLEDGE_DEMON_BOSS --enemy-current-hp 399 --expected-initial-choice-branches-evaluated-at-least 2 --expected-initial-planned-choice-card-id MIND_ROT --expected-initial-act-ending-boss 1 --expected-observed-player-power-id MIND_ROT_POWER --stop-after-expected-player-power --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id KNOWLEDGE-LIVE-END-RISK-INCREMENTAL --character-id REGENT --encounter-id KNOWLEDGE_DEMON_BOSS --enemy-current-hp 399 --expected-initial-choice-branches-evaluated-at-least 2 --expected-initial-planned-choice-card-id MIND_ROT --expected-initial-act-ending-boss 1 --expected-observed-player-power-id MIND_ROT_POWER --stop-after-expected-player-power --enable-stop-on-worse-recalculation-for-test --expected-unexpected-replans-at-most 0 --verify-incremental-search --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id DEATH-TURN-PAUSE-165 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 57 --initial-player-hp 1 --clear-player-piles --cards-json '[]' --initial-enemy-move-ids-json '["FIRST_ACID_GOOP"]' --expected-initial-only-death-routes-found 1 --expected-initial-death-turn 1 --expected-full-auto-paused-at-death-turn --timeout-seconds 120 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SCULPTING-STRIKE-CHOICE-151 --character-id NECROBINDER --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 50 --clear-player-piles --cards-path coverage/unattended/sculpting-strike-choice-151-cards.json --expected-played-card-id SCULPTING_STRIKE --expected-reused-turn 2 --stop-after-expected-reuse --verify-incremental-search --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id ARMAMENTS-IMPLICIT-UPGRADE-OBSERVATION-534 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 9 --clear-player-piles --cards-json '[{"cardId":"ARMAMENTS","pile":"Hand","count":1},{"cardId":"STRIKE_IRONCLAD","pile":"Hand","count":1}]' --expected-played-card-id ARMAMENTS --expected-finished-turn 1 --expected-unexpected-replans-at-most 0 --verify-incremental-search --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0 --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id COSMIC-INDIFFERENCE-IMPLICIT-151B --character-id REGENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 50 --clear-player-piles --cards-path coverage/unattended/cosmic-indifference-choice-151b-cards.json --expected-played-card-id COSMIC_INDIFFERENCE --expected-reused-turn 2 --stop-after-expected-reuse --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id DUPLICATE-POTION-SEARCH-147 --character-id REGENT --encounter-id QUEEN_BOSS --enemy-current-hp 1 --potions-path coverage/unattended/duplicate-potions-147.json --expected-initial-executable-action-count-at-least 1 --expected-finished-turn-at-most 5 --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id PEN-NIB-ROUTE-148 --character-id IRONCLAD --encounter-id MECHA_KNIGHT_ELITE --enemy-current-hp 70 --clear-player-piles --cards-path coverage/unattended/pen-nib-route-148-cards.json --relics-path coverage/unattended/pen-nib-route-148-relics.json --verify-incremental-search --expected-initial-executable-action-count-at-least 2 --expected-initial-relic-effect-id PEN_NIB --expected-initial-relic-effect-summary '×2' --expected-initial-hp-lost-at-most 0 --expected-initial-projected-battle-hp-lost-at-most 0 --expected-initial-only-death-routes-found 0 --expected-initial-combat-ended-turn 1 --expected-finished-turn 1 --deployment-fast-mode-for-test Instant --deployment-inter-action-delay-seconds-for-test 0.05 --assert-deployment-speed-restored --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id CENTENNIAL-PUZZLE-STATE-149 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 57 --combat-relics-path coverage/unattended/centennial-puzzle-state-149-relics.json --monster-move-checks-path coverage/unattended/centennial-puzzle-state-149.json --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id JOSS-PAPER-STATE-150 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/joss-paper-state-150-relics.json --monster-move-checks-path coverage/unattended/joss-paper-state-150.json --timeout-seconds 180 --keep-game-open
./tools/run-unattended-test.sh --scenario-id ONLY-DEATH-ROUTES-150 --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 57 --initial-player-hp 1 --clear-player-piles --cards-json '[]' --initial-enemy-move-ids-json '["FIRST_ACID_GOOP"]' --expected-initial-only-death-routes-found 1 --expected-player-death --expected-finished-turn 1 --timeout-seconds 120 --keep-game-open
./tools/run-unattended-test.sh --scenario-id PERF-END-TURN-CLEANUP-FINAL-099 --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/performance-end-turn-cleanup-098.json --timeout-seconds 180 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id PERF-DAMAGE-PIPELINE-100 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-damage.json --timeout-seconds 240 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id NIBBITS-UNIFIED30-SOLD-CAP-084 --character-id SILENT --seed BJCZX3J13PZJ --run-snapshot-path coverage/unattended/solver-longline-run-snapshot.json --encounter-id NIBBITS_NORMAL --enemy-current-hp 999 --initial-player-hp 35 --potion-id WeakPotion --expected-initial-potion-count 0 --expected-initial-projected-battle-hp-lost-at-most 0 --expected-initial-sold-hp 0 --expected-initial-sold-hp-branches-pruned-at-least 1 --expected-reused-turn 3 --expected-finished-turn-at-most 8 --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id QUEEN-CHAINS-REUSE-FINAL-085 --character-id IRONCLAD --seed Y883BRPFJZ05 --run-snapshot-path "${RUN_SNAPSHOT_PATH:?set RUN_SNAPSHOT_PATH to current_run.save}" --encounter-id QUEEN_BOSS --enemy-current-hp 70 --initial-player-hp 80 --initial-enemy-move-ids-json '["","PUPPET_STRINGS_MOVE"]' --expected-reused-turn 3 --stop-after-expected-reuse --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CORPSE-SLUGS-USER-RUN-073 --character-id IRONCLAD --seed Y883BRPFJZ05 --run-snapshot-path "${RUN_SNAPSHOT_PATH:?set RUN_SNAPSHOT_PATH to current_run.save}" --encounter-id CORPSE_SLUGS_WEAK --enemy-current-hp 999 --initial-player-hp 80 --expected-finished-turn-at-most 20 --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MECHA-MEMORY-FULL-AUTO-FINAL-070 --character-id SILENT --seed BJCZX3J13PZJ --run-snapshot-path coverage/unattended/mecha-knight-memory-run-snapshot.json --encounter-id MECHA_KNIGHT_ELITE --enemy-current-hp 300 --initial-player-hp 65 --short-search-budget-override-milliseconds 5000 --deep-search-budget-override-milliseconds 60000 --expected-initial-search-phase Deep --expected-initial-deep-search-triggered 1 --expected-initial-total-elapsed-milliseconds-at-most 25000 --expected-initial-total-allocated-bytes-at-most 4500000000 --expected-initial-gen2-collections-at-most 6 --expected-initial-total-gc-pause-milliseconds-at-most 8000 --expected-initial-max-gc-pause-milliseconds-at-most 50 --expected-initial-max-main-thread-frame-gap-milliseconds-at-most 100 --expected-reused-turn 3 --expected-finished-turn-at-most 9 --measure-search-phases --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SOLVER-ROUTE-POLICY-060 --character-id SILENT --seed BJCZX3J13PZJ --run-snapshot-path coverage/unattended/solver-longline-run-snapshot.json --encounter-id NIBBITS_NORMAL --enemy-current-hp 999 --initial-player-hp 35 --potion-id WeakPotion --expected-initial-potion-count 0 --expected-initial-projected-battle-hp-lost-at-most 0 --expected-reused-turn 3 --timeout-seconds 300 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id SOLD-HP-POLICY-BATCH-059-DEFENSE-CHOICE --character-id IRONCLAD --encounter-id NIBBITS_WEAK --enemy-current-hp 43 --cards-path coverage/unattended/sold-hp-policy-batch-059-defense-choice.json --clear-player-piles --expected-initial-sold-hp-at-most 5 --expected-initial-sold-hp-branches-pruned-at-least 1 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SOLD-HP-POLICY-BATCH-059-UNAVOIDABLE --character-id IRONCLAD --encounter-id NIBBITS_WEAK --enemy-current-hp 43 --cards-path coverage/unattended/sold-hp-policy-batch-059-unavoidable.json --clear-player-piles --expected-initial-sold-hp 0 --keep-game-open
./tools/run-unattended-test.sh --scenario-id SOLD-HP-POLICY-BATCH-059-STABLE-NO-SALE --character-id IRONCLAD --encounter-id SLIMES_WEAK --enemy-current-hp 999 --cards-path coverage/unattended/sold-hp-policy-batch-059-active-sale.json --clear-player-piles --expected-initial-sold-hp 0 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-POWER-BATCH-058 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-power-batch-058-relics.json --monster-move-checks-path coverage/unattended/relic-power-batch-058.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-POWER-TEMPORARY-BATCH-058 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-power-batch-058-temporary-relics.json --monster-move-checks-path coverage/unattended/relic-power-batch-058-temporary.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-POTION-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-potion-relics.json --potion-check-path coverage/unattended/relic-reactive-batch-057-potion.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-TURNS-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-turns-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-turns.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-TURN-END-FINAL --character-id IRONCLAD --enemy-current-hp 999 --relics-path coverage/unattended/relic-reactive-batch-057-turn-end-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-turn-end.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-KUSARIGAMA-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-kusarigama-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-kusarigama.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-STARS-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-stars-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-stars.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-EMOTION-FINAL --character-id DEFECT --enemy-current-hp 999 --orbs-json '[{"orbId":"LIGHTNING_ORB","count":1}]' --combat-relics-path coverage/unattended/relic-reactive-batch-057-emotion-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-emotion.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-TOP-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-top-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-top.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-UNDYING-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-undying-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-undying.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-PAELS-EYE-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-paels-eye-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-boundary.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-HISTORY-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-history-course-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-boundary.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-REACTIVE-BATCH-057-TOASTY-FINAL --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-reactive-batch-057-toasty-mittens-relics.json --monster-move-checks-path coverage/unattended/relic-reactive-batch-057-boundary.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-DETERMINISTIC-056 --character-id DEFECT --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-deterministic-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-deterministic-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-OSTY-056 --character-id NECROBINDER --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-osty-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-osty-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-CYCLES-056 --character-id REGENT --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-cycles-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-cycles-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-ATTACKS-056 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-attacks-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-attacks-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-LETTER-056 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-letter-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-letter-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-LEGION-056 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-legion-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-legion-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-GENERATION-056 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-generation-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-generation-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-LIFECYCLE-DAMAGE-056 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-lifecycle-batch-056-damage-relics.json --monster-move-checks-path coverage/unattended/relic-turn-lifecycle-batch-056-damage-checks.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-START-FIRST-055 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-start-batch-055-first-relics.json --monster-move-checks-path coverage/unattended/relic-turn-start-batch-055-first-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-START-CYCLES-055 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-start-batch-055-cycles-relics.json --monster-move-checks-path coverage/unattended/relic-turn-start-batch-055-cycles-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-START-TEA-055 --character-id IRONCLAD --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-start-batch-055-tea-relics.json --monster-move-checks-path coverage/unattended/relic-turn-start-batch-055-tea-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-START-CORE-055 --character-id DEFECT --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-start-batch-055-core-relics.json --monster-move-checks-path coverage/unattended/relic-turn-start-batch-055-core-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-START-CONCH-055 --character-id IRONCLAD --encounter-id KnightsElite --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-turn-start-batch-055-conch-relics.json --monster-move-checks-path coverage/unattended/relic-turn-start-batch-055-conch-checks.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-PERSISTENT-054 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-persistent-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-persistent-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-RUNIC-PYRAMID-054 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-runic-pyramid-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-runic-pyramid-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-INFUSED-CORE-054 --character-id DEFECT --enemy-current-hp 999 --relics-path coverage/unattended/relic-infused-core-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-infused-core-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-DAMAGE-054 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-damage-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-damage-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-BOOT-054 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-boot-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-boot-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TUNGSTEN-054 --enemy-current-hp 999 --additional-monster-id BowlbugRock --combat-relics-path coverage/unattended/relic-tungsten-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-tungsten-batch-054-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-VITRUVIAN-054 --character-id NECROBINDER --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-vitruvian-batch-054-relics.json --monster-move-checks-path coverage/unattended/relic-vitruvian-batch-054-checks.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-DRAW-CYCLES-053 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-draw-state-batch-053-cycles-relics.json --monster-move-checks-path coverage/unattended/relic-draw-state-batch-053-cycles-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-POCKETWATCH-053 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-draw-state-batch-053-pocketwatch-relics.json --monster-move-checks-path coverage/unattended/relic-draw-state-batch-053-pocketwatch-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-FIRST-TURN-SNAPSHOT-053 --enemy-current-hp 999 --relics-path coverage/unattended/relic-draw-state-batch-053-first-turn-relics.json --monster-move-checks-path coverage/unattended/relic-draw-state-batch-053-first-turn-checks.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id RELIC-PURE-DRAW-052 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-pure-batch-052-draw-relics.json --monster-move-checks-path coverage/unattended/relic-pure-batch-052-draw-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-PURE-ENERGY-052 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-pure-batch-052-energy-relics.json --monster-move-checks-path coverage/unattended/relic-pure-batch-052-energy-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-TURN-CONDITIONS-052 --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-pure-batch-052-turn-relics.json --monster-move-checks-path coverage/unattended/relic-pure-batch-052-turn-checks.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id RELIC-BOOMING-CONCH-052 --encounter-id KnightsElite --enemy-current-hp 999 --combat-relics-path coverage/unattended/relic-pure-batch-052-booming-relics.json --monster-move-checks-path coverage/unattended/relic-pure-batch-052-booming-checks.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-OSTY --character-id NECROBINDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-osty.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-DIRGE --character-id NECROBINDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-dirge.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-CHOICES --character-id NECROBINDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-choices.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-AUTOPLAY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-autoplay.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-POWERS --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-powers.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-NORMALITY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-normality.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-ENTHRALLED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-enthralled.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-RETURN-AUTOPLAY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-return-autoplay.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-UPGRADED --character-id NECROBINDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-NIGHTMARE-LIFECYCLE --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-043-nightmare-lifecycle.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-043-VOID-FORM --character-id IRONCLAD --enemy-current-hp 18 --card-id VOID_FORM --clear-player-hand --expected-played-card-id VOID_FORM --expected-finished-turn 2 --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-BOUNCING-FLASK --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-bouncing-flask.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-DIRECT-A --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-direct-a.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-OUTBREAK --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-outbreak.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-ECHOING-SLASH --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-echoing-slash.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-OMNISLICE --character-id IRONCLAD --enemy-current-hp 999 --additional-monster-id CalcifiedCultist --monster-move-checks-path coverage/unattended/card-on-play-batch-042-omnislice.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-TRANSFORM --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-transform.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-HAND --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-hand.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-STATE --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-state.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-PILES --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-piles.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-TRANSFORM-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-transform-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-PURITY-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-purity-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-CHOICE-ZERO-OPTIONAL --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-choice-zero-optional.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-HIDDEN-DAGGERS-EMPTY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-hidden-daggers-empty.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-BRAND-EMPTY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-brand-empty.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-SCAVENGE-EMPTY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-scavenge-empty.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-FRANTIC-ESCAPE --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-frantic-escape.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-ECHOING-SLASH-KILL --character-id IRONCLAD --enemy-current-hp 999 --additional-monster-id CalcifiedCultist --monster-move-checks-path coverage/unattended/card-on-play-batch-042-echoing-slash-kill.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-DIRECT-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-042-direct-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-END-OF-DAYS-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --additional-monster-id CalcifiedCultist --monster-move-checks-path coverage/unattended/card-on-play-batch-042-end-of-days-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-042-END-OF-DAYS --character-id IRONCLAD --enemy-current-hp 999 --additional-monster-id CalcifiedCultist --monster-move-checks-path coverage/unattended/card-on-play-batch-042-end-of-days.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-POWER-A --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-041-power-set-a.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-POWER-B --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-041-power-set-b.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-SYNCHRONIZE --character-id DEFECT --enemy-current-hp 999 --orbs-json '[{"orbId":"LIGHTNING_ORB","count":1},{"orbId":"FROST_ORB","count":1}]' --monster-move-checks-path coverage/unattended/card-on-play-batch-041-synchronize.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-TURBO-SLEEVE --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-041-turbo-up-my-sleeve.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-SUMMON-FORTH --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-041-summon-forth.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-041-SHADOW-STEP --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-041-shadow-step.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-POWER-A --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-power-set-a.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-POWER-B --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-power-set-b.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-PALE-BLUE-DOT --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-pale-blue-dot.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-RESOURCES --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-resources-and-targets.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-SEEKING-EDGE --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-seeking-edge.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-040-SIGNAL-BOOST --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-040-signal-boost.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-POWER --character-id NECROBINDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-power-set.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-TARGET --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-target-effects.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-TARGET-LIFECYCLE --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-target-lifecycle.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-RESOURCES --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-resources.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-SHIVS --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-shivs.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-INK --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-blade-of-ink.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-APOTHEOSIS --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-apotheosis.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-ENLIGHTENMENT --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-enlightenment.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-ENLIGHTENMENT-UPGRADED --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-enlightenment-upgraded.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-STORM --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-storm-of-steel.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-HOTFIX-LIFECYCLE --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-hotfix-lifecycle.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-EXPOSE-ARTIFACT --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-expose-artifact.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-039-HAZE-MULTI --character-id SILENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-039-haze-multi.json --additional-monster-id DampCultist --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-A --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-power-set-a.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-B --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-power-set-b.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-DANSE --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-danse-macabre.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-PLANNER --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-master-planner.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-SERPENT --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-serpent-form.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-STORM --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-storm.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-038-NO-ESCAPE --enemy-current-hp 80 --monster-move-checks-path coverage/unattended/card-on-play-batch-038-no-escape.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-POWER-A --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-power-set-a.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-POWER-B --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-power-set-b.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-SPECIAL --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-special-effects.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-BULLET-TIME --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-bullet-time.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-FERAL-HISTORY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-feral-history.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-037-JUGGLING-HISTORY --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/card-on-play-batch-037-juggling-history.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-036-A --character-id DEFECT --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-036-power-set-a.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-036-B --character-id DEFECT --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-036-power-set-b.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-036-ANTICIPATE --character-id DEFECT --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-036-anticipate-lifecycle.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-035-SELF --character-id IRONCLAD --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-035-self-powers.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-035-TARGET --character-id IRONCLAD --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-035-target-powers.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-035-BULK-UP --character-id DEFECT --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-035-bulk-up.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-035-REGENT --character-id REGENT --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-035-regent.json
./tools/run-unattended-test.sh --scenario-id CARD-ON-PLAY-BATCH-035-PIERCING-WAIL --character-id IRONCLAD --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/card-on-play-batch-035-piercing-wail-lifecycle.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-ACCURACY --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-accuracy.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-BLOCK --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-block.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-COST-LOCATION --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-cost-location.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-HANG --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-hang.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-HARD-TO-KILL --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-hard-to-kill.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-LEADERSHIP --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-leadership.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-LETHALITY --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-lethality.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-ONE-FOR-ALL --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-one-for-all.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-PHANTOM-BLADES --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-phantom-blades.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-SOAR --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-soar.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-TRACKING --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-tracking.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-CALCIFY --character-id NECROBINDER --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-calcify.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-034-DIE-FOR-YOU --character-id NECROBINDER --enemy-current-hp 50 --monster-move-checks-path coverage/unattended/monster-moves-batch-034-die-for-you.json
./tools/run-unattended-test.sh --scenario-id SMOKE-002
./tools/run-unattended-test.sh --scenario-id MONSTER-WATERFALL-001 --encounter-id WATERFALL_GIANT_BOSS --power-id STEAM_ERUPTION_POWER --power-amount 10 --expected-finished-turn 2
./tools/run-unattended-test.sh --scenario-id MONSTER-AXEBOT-HAMMER-001 --encounter-id AxebotsNormal --monster-move-id HAMMER_UPPERCUT_MOVE --expected-player-hp-loss 14 --expected-player-powers-json '{"WEAK_POWER":2,"FRAIL_POWER":2}'
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-004 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-004.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-005 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-005.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-006 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-006.json --additional-monster-id Fabricator
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-007 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-007.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-008 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-008.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-009 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-009.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-010 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-010.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-011 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-011.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-012 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-012.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-013 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-013.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-014 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-014.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-015 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-015.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-016 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-016.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-017 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-017.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-018 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-018.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-019 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-019.json --additional-monster-id TorchHeadAmalgam
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-020 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-020.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-021-KAISER --encounter-id KaiserCrabBoss --monster-move-checks-path coverage/unattended/monster-moves-batch-021-kaiser.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-021-SUPPORT --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-021-support.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-022 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-022.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-023 --encounter-id BowlbugsWeak --monster-move-checks-path coverage/unattended/monster-moves-batch-023.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-024 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-024.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-026 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-026.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-027 --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-027.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-028-LIFECYCLE --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-028-lifecycle.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-028-TURN-END --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-028-turn-end.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-029-SHRINK --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-029-lifecycle.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-029-ARTIFACT --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-029-artifact.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-029-TANGLED --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-029-tangled.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-029-RINGING --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-029-ringing.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-029-INTANGIBLE --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-029-intangible.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-030-DAMAGE --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-damage.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-030-BLOCK --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-block.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-030-POISON --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-poison.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-030-BLOCK-LIFECYCLE --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-block-lifecycle.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-030-SLOW --encounter-id LivingFogNormal --monster-move-checks-path coverage/unattended/monster-moves-batch-030-slow.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-031-RESOURCES --monster-move-checks-path coverage/unattended/monster-moves-batch-031-resources.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-031-MODIFIERS --monster-move-checks-path coverage/unattended/monster-moves-batch-031-modifiers.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-031-LIFECYCLE --monster-move-checks-path coverage/unattended/monster-moves-batch-031-lifecycle.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-RESOURCES --character-id DEFECT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-032-resources.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-STARS --character-id REGENT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-032-stars.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-START-POWERS --character-id IRONCLAD --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-032-start-powers.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-COOLANT --character-id DEFECT --enemy-current-hp 999 --orbs-json '[{"orbId":"LIGHTNING_ORB","count":1},{"orbId":"FROST_ORB","count":1}]' --monster-move-checks-path coverage/unattended/monster-moves-batch-032-coolant.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-GLOBAL --additional-monster-id TurretOperator --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-032-global.json --keep-game-open
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-032-RITUAL --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-032-ritual.json --exit-on-complete
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-COLOSSUS --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-colossus.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-TAINTED --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-tainted.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-CONCOCT --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-concoct.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-CORROSIVE-WAVE --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-corrosive-wave.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-DEMISE --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-demise.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-DISINTEGRATION --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-disintegration.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-LIFECYCLE --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-lifecycle.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-ORBS-NEMESIS --character-id DEFECT --enemy-current-hp 999 --orbs-json '[{"orbId":"FROST_ORB","count":1}]' --monster-move-checks-path coverage/unattended/monster-moves-batch-033-orbs-nemesis.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-TENDER --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-tender.json
./tools/run-unattended-test.sh --scenario-id MONSTER-MOVES-BATCH-033-JUGGLING --enemy-current-hp 999 --monster-move-checks-path coverage/unattended/monster-moves-batch-033-juggling.json --exit-on-complete
```

成功请求尊重原命令的生命周期：Windows 的 `-ExitOnComplete` 和 Linux 的 `--exit-on-complete` 会在本条后退出；没有退出标志时，启动器必须等待当前 runId 的静稳 ready ACK 才返回并允许复用。`-KeepGameOpen / --keep-game-open` 为旧命令兼容保留；两端现在不传退出参数也会默认保持进程。任何 `Failed`、异步工作无法静稳、ready ACK 超时或启动器中断都会清理已精确认领的进程，不会把失败进程交给后续场景；Windows 与 Linux 都按可执行文件、启动身份、隔离目录和 DLL/manifest 哈希安全复用或重启。

## 自动覆盖范围

| 范围 | 状态 | 说明 |
|---|---|---|
| 单人战斗卡牌/选牌/生成牌 | 通过 | 既有牌选择、嵌套选择、随机生成、局内变换、升级/降级/附魔及生成卡后续监听均有严格差分 |
| Power、遗物、药水、充能球 | 通过 | 当前游戏 `0.111.0` 的单人战斗行为目录无未分类、无静态行为证据缺口、无原生重扫边界 |
| 怪物行动、死亡、复活、召唤 | 通过 | 57 个补偿行动全量分片复跑；结构性复活、召唤、替换、特殊移除另有整战与定向生命周期回归 |
| 跨回合算到底 | 通过 | 同族、实验体、花园鳗、旧日雕像、女王、双小啃兽等整战在预算覆盖范围内逐回合复用；生产搜索只允许时间和节点预算终止，回合上限只用于增量验证模式 |
| 多人模式及多人专属内容 | 不在范围 | 不把多人专属选择、队友死亡后的 Hook 活性或多人卡牌记为单人适配缺口 |

## 人工待测

| ID | 状态 | 检查项 |
|---|---|---|
| `SOLVER-DISABLE-525` | 待测 | 设置中禁用后立即取消后台搜索与自动部署、关闭全自动并清除旧路线；后续回合和首回合选牌阶段均不自动求解，手操不产生重算；新战斗仍可打开设置，重新启用后按当前真实状态搜索 |
| `UI-OVERLAY-001` | 待测 | 拖动、轻量收起按钮、单行 `14px` 粗体概览、无计数的状态详情按钮、无键位提示的重新计算/执行按钮、纯“推荐路线”标题、HP/费用固定双列、始终显示“余 0 费”、完整搜索回合滚动及底栏对齐；路线用药显示为 `预计用x瓶药`，数量等于已喝加路线剩余并在跨回合复用时保持；页面不使用中圆点拼接信息 |
| `UI-FULL-AUTO-001` | 待测 | 全自动关闭时为暗色次级按钮、运行中为绿色正向按钮，战斗结束暂停开关与退出战斗清理 |
| `UI-FONT-001` | 待测 | 中文字体使用游戏思源黑体、不回退到默认日文字形；普通/富文本/按钮 `2px` 描边清晰且箭头、展开符号不缺字 |
| `PERF-FRAME-001` | 待测 | 发牌动画和多回合后台搜索期间的实际帧时间体感；日志仅作为分配与 GC 辅助证据 |
| `RF-OFFICIAL-WORKSHOP-COEXIST-069` | 通过 | RF 本地 fork 已与 `0.10.0` 共同跑完完整长线；用户随后订阅创意工坊原版 RF 并完成一次实机启动，未出现初始化或共存问题 | 2026-08-21 |

## 判定规则

- “通过”必须有同一 `runId` 的 `Passed` 结果，并核对对应 `SEARCH_REQUEST`、`RESULT`、`ACTION`、`DEPLOY_*` 和真实怪物行动日志。
- 只编译通过、只看到最终胜利或只看模拟结果都不能标记为通过。
- `RID/resources still in use at exit` 当前记录为 Godot 退出噪音；任何 `CombatSolver/Unattended FAILED`、`SEARCH_FAILURE`、`DEPLOY_FAILURE` 或状态断言失败均判定场景失败。

`COMPACT-SEARCH-BACKEND`（L3，原始 SILENT／MECHA_KNIGHT_ELITE、VH_PERF_MECHA、30 张 RunCards、清空原跑局牌组且 Cards=[]、VeryHigh、8 worker）：同一捕获根运行完整旧／紧凑搜索，逐项比较路线、全部 SolverSnapshot 和主要逻辑计数，断言实机不变；另报完成、挂起和兼容物化数。请求上限 120 秒，NoGC 配置断言不代表 Runtime 已进入 NoGC。双端脚本使用现有 ScenarioId/RunCards/Cards/ClearRunDeck 参数，无新增协议字段。[通过结果与分阶段性能](performance/simulation-search-backend-20260911.md)。

`COMPACT-SEARCH-BACKEND` 现还校验原始战斗／跑局各 30 牌及完整 `Continuations`。政策读视图集成通过 `427def306b9748988a4088c10646bb4c`，既有完整路线和所有逻辑计数不变；物理成本单列，不能混同预算。

挂起状态接入后，`COMPACT-SEARCH-BACKEND` 逐个截断完整路线的所有选牌前缀，比对挂起快照、请求、完整选项和来源实例，逆序检查冻结选择及预览未被复用覆盖，并对错误选择来源断言相同业务拒绝。单线程／8 worker 使用相同原输入及现有 DOP 参数。`COMPACT-ROUND-NATIVE` 增加同一 helper，覆盖 Stratagem、Tools、Sly 嵌套及回合内未发布意图；本轮三模式的省略边界为 5／3／0 个。纯值 `RoundChecks` 另覆盖空根 AI 日志、根当前招式与下一招式不同的挂起发布边界。

`COMPACT-SEARCH-BACKEND` 现保存成功 A/B 的完整逻辑结果，并比对每个完成前缀的 ContinuationStamp，附进程级分配／CPU／GC 增量。单线程与 DOP8 的原完整路线和全部逻辑结果跨请求一致，物化均 0。`COMPACT-SERIAL-PREPARATION-SENTINEL` 使用 VerifySearchPolicySnapshot + StopAfterCombatRootSnapshotAssertion，固定 250 节点、SILENT／机械骑士 1000 HP、PREPARED／SURVIVOR／BACKFLIP 及 GAMBLERS_BREW／COLORLESS_POTION，验证旧路径 DOP1/DOP2、取消／失败排空。三模式 `COMPACT-ROUND-NATIVE` 的每步完成值还比对续用文本，覆盖非零根历史及 Power 初始字段。

`COMPACT-SEARCH-LIFECYCLE`：原始 30 牌根、250 节点，旧／紧凑 DOP1 和紧凑 DOP2 全政策结果一致；真实双线程、取消／注入异常排空、失败后复用根、未迁移药水明确拒绝及实机不变。`COMPACT-RUNTIME-FULL-AUTO`：原始机甲、VeryHigh/DOP8/NoGC16GB、Instant/0，实际紧凑执行与 T2–T8 精确续用，T8 胜利／6 HP／0 药／零计划外重算。`COMPACT-RUNTIME-MODEL-FALLBACK`：增加 FIRE_POTION 后明确使用模型后端，首轮结果正常。`COMPACT-RUNTIME-MODEL-BASELINE`：启用前 67aa82d 产物、同原输入与正常 NoGC，首轮逻辑计数／评分／预计战损与紧凑样本一致；仅停止在首轮结果。均为 120 秒以内 headless 请求。[结果与限制](performance/simulation-runtime-backend-20260911.md)。

### 精神过载与毁灭值执行（2026-09-11）

`COMPACT-NEUROSURGE-NATIVE`／`COMPACT-NEUROSURGE-CHOICES` 比较两次出牌、两回合和全部省略选择边界；原生选择观察器核对资源先到账、能力后施加。`COMPACT-DOOM-PLAYER`／`COMPACT-DOOM-ENEMY` 比较双方正确／错误相位、阈值、直接死亡与格挡／历史保留。均有完整能力元数据、原键／估值／续用、九 RNG、逆序与八工作区隔离。`COMPACT-NEUROSURGE-SEARCH` 同根固定 250 节点、旧／新 DOP1 和紧凑 DOP2 全政策相等，取消／异常排空后根可复用；明确拒绝未迁移药水。所有请求限制 120 秒。[构建、回归与结构化证据](performance/simulation-necro-resources-20260911.md)。

### 奥斯蒂根身份与死亡清理（2026-09-11）

`OSTY-STATE-LIFECYCLE`：无宠物根捕获后原生首次召唤；活宠物根五步承伤／复活／增长／直接受伤／复活，逐步完整原生快照、伤害结果顺序、连续 Fork、全部键／估值、八工作区。实机推进后两个冻结根不变，空根后续独立生成实例与原生首次召唤一致。120 秒以内 L2，无正式搜索、无性能断言。[结果和重跑参数](performance/simulation-osty-ownership-20260911.md)。

### 奥斯蒂值执行、生命封顶与搜索（2026-09-11）

`COMPACT-OSTY-NATIVE`：三个根状态、十五个原生步骤，包括存活／死亡根、双方不同力量、部分／完全格挡代伤、死亡后空攻击、复活／增长、群体效果不触及宠物及两个完整回合；逐步比对完整状态、能力元数据、历史、九 RNG、原键／估值／续用、缓存开关、逆序／撤销与八工作区，实机推进后冻结根不变。`COMPACT-OSTY-CAP`：接近／达到最大生命上限两次原生召唤与三十组事件编码边界；`COMPACT-OSTY-DEFEAT`：毁灭直接杀死玩家及存活宠物，保留格挡、不伪造伤害历史、完整能力退休。`COMPACT-ROUND-NATIVE` 三模式回归覆盖二十分支和七个原生动作。

`COMPACT-OSTY-SEARCH` 共用主场景原始建局并增加一层 Tools 触发选择，固定 250 节点，旧／新 DOP1 与紧凑 DOP2 比较完整政策及续用结果，验证实际并发、挂起候选、取消／注入异常排空、根可复用、未迁移药水拒绝及实机不变。全部使用 SILENT／MECHA_KNIGHT_ELITE、Cards=[]、HP300、Instant、120 秒上限，建局直接注入奥斯蒂；不依赖切换亡灵角色。[本轮结果与重跑命令](performance/simulation-osty-values-20260911.md)。

`COMPACT-OSTY-TURN-NATIVE`：复用奥斯蒂三个根建局，额外注入初始遗物、Tools 和 Stratagem，每根连续三个完整回合；九个原生回合、十五个省略选择边界与十五次原生选择前观察，覆盖受击后复活、存活增长、完整元数据／历史／键／估值／续用、逆序／撤销、八工作区和实机后冻结恢复。`COMPACT-OSTY-TURN-SEARCH` 共用相同遗物根，固定 250 节点旧／新和串／并行完整政策、取消／失败排空及根复用。SILENT／MECHA_KNIGHT_ELITE、Cards=[]、HP300、Instant、120 秒，与现有双端参数兼容。[本轮记录](performance/simulation-pet-turns-20260911.md)。

`COMPACT-DRAW-EXHAUST-NATIVE`：四张／单张／空抽牌堆，存活／死亡奥斯蒂；每根两种升级等级的洁净、来生和两个完整回合，18 原生动作、39 分支、15 个挂起边界及两次原生选择前观察。全快照、能力、九 RNG、历史、估值／原键／续用、生成身份、撤销／逆序／八工作区及实机后根隔离。`COMPACT-DRAW-EXHAUST-SEARCH` 固定 250 节点旧新串并行、取消失败排空和根复用。双端使用 SILENT／MECHA_KNIGHT_ELITE、Cards=[]、HP300、Instant、120 秒。[证据与夹具失败说明](performance/simulation-draw-exhaust-20260911.md)。


`COMPACT-DIRGE-NATIVE`：普通／升级挽歌、存活／死亡宠物、非空／空抽牌堆和满手边界；每根 X=3 挽歌、灵巧、灵魂、X=0 挽歌和两个完整回合。12 原生动作、24 替代分支、8 挂起边界，逐次召唤与生成数量、Shuffle 计数／九流状态、牌序／身份／升级、完整原键／续用、撤销／逆序／八工作区和实机后根隔离。`COMPACT-DIRGE-SEARCH` 固定 250 节点，旧新串并行完整结果、实际并发 2、取消失败排空及根复用。公共生成与夹具提取回归 `COMPACT-DRAW-EXHAUST-NATIVE`、`COMPACT-INKY-CARDS-NATIVE`。双端参数沿用既有场景、Instant、120 秒；[本轮直接证据](performance/simulation-dirge-20260911.md)。


`COMPACT-NECRO-CARDS-NATIVE`：两根包含五种新增类型与普通／升级实例；空／非空弃牌堆、满手、存活／死亡宠物；六次出牌及两个原生回合。对照全部快照、能力元数据、九 RNG、历史、原键／续用、根和生成实例指纹、撤销／逆序／八工作区及实机后根隔离。`COMPACT-CAPTURE-SPIRIT-TERMINAL` 验证击杀后 3 张未入堆灵魂的历史且不消费 RNG；`COMPACT-GRAVEBLAST-TERMINAL` 验证击杀后没有原生／预测选牌。终局在清理前捕获完整状态并等待真实结束事件。`COMPACT-NECRO-CARDS-SEARCH` 使用既有 250 节点串并行／取消异常排空合同；公共路径回归 `COMPACT-MONSTER-COMMANDS-NATIVE` 和 `COMPACT-DRAW-EXHAUST-NATIVE`。双端继续使用 SILENT／MECHA_KNIGHT_ELITE、Cards=[]、HP300、Instant、120 秒；[直接证据和失败基线](performance/simulation-necro-card-operations-20260911.md)。

## 2026-09-11：费用能力与挂起查询（开发中）

`COMPACT-COST-POWERS-NATIVE`：两根各九次出牌及两个原生回合，比较全牌堆费用、随机本地修饰、X／负基础费、初始能力／叠加／消耗／人工制品；22 原生动作、36 分支、8 挂起和 3 次原生免费费用观察。`COMPACT-COST-POWERS-ROOT-STACKS` 可单独运行已有能力根。`COMPACT-COST-POWERS-TERMINAL` 验证付款后最后击杀的能力门禁和全局费用停止。`COMPACT-COST-POWERS-SEARCH` 使用 250 节点完整旧新／串并行／取消异常排空合同。全快照、原键、续用、实例、RNG、冻结恢复和八工作区均比较。公共回归为 `TENDER-NESTED-SLY` 与 `HAND-POTENTIAL-COSTS`；后者只在 100ms 首结果停止，不作性能比较。[本轮证据与失败记录](performance/simulation-cost-powers-20260911.md)。
# 0.34.7 跑局战绩验证

- `dotnet run --project tools/RunStatisticsTests -c Release` 通过：胜负/放弃、空胜率、连续段中断、重复事件、离线收据与重启、原生结算恢复、历史快照隔离。
- 在线服务 14 项测试通过，包含旧心跳、管理鉴权、持久登录、统计加权、筛选和战绩数据库重启恢复。
- 日志服务 19 项测试通过，包含提交时战绩快照及小数百分比筛选；Windows 测试进程退出仍有原有 SQLite 临时文件清理占用提示。
- UI-LOCALIZATION `42dce92d333e46e482ba556b9959e4eb` Passed，24.85 秒，覆盖 322 条中英资源与 headless 统计节点隔离。不是可见游戏结算/交互或帧率验收。

## 上游 0.36.0 集成（2026-09-11）

本轮重跑 COMPACT-SHARED-FATE-NATIVE／SEARCH、COMPACT-PANACHE-PLAYER-DEATH、PANACHE-INSTANCES，完整状态／键／续用与 250 节点串并行对照均通过；独立 CardHookReceiverChecks 78 项、PlayerDeathChecks 48 项及纯值／Linux 结构／覆盖门禁通过。详见[合并记录](performance/simulation-upstream-merge-20260911.md)。未重跑完整正常 NoGC 性能基准。

## 书页风暴（2026-09-11）

`COMPACT-PAGESTORM-NATIVE`：三根、24 原生动作／63 分支／32 挂起，嵌套选择观察、逆序 Slither、父返回值、根／新能力、Swift／Sly、动态虚无和两个回合。全部状态／能力／九 RNG／原键／续用及八工作区严格比较。`COMPACT-PAGESTORM-SEARCH` 使用相同根和 250 节点，旧新／串并行完整结果、取消异常排空及根复用通过。`COMPACT-DRAW-EXHAUST-NATIVE` 回归无新能力根；纯值另验证九层抽牌与满手、终局、撤销。沿用 SILENT／MECHA_KNIGHT_ELITE、Instant、120 秒，无新增双端协议字段。[证据与失败归类](performance/simulation-pagestorm-20260911.md)。

## 完整生成池审计（2026-09-11）

`COMPACT-GENERATION-CLOSURE-AUDIT` 使用原始 NECROBINDER／AEONGLASS_BOSS 输入，38 牌、19 件遗物注入、保留初始遗物及两瓶药；导出 78／50 个完整有序候选，24 次抽样对照全部五个 RNG 字段，确认真实状态不变与未迁移根显式拒绝。只验证原生筛选／选择方法和旧模型选牌，不执行生成入堆及回调，也不启动搜索。双端使用既有协议参数，无新字段；[完整命令及边界](performance/simulation-generation-audit-20260911.md)。

## 完整角色生成池冻结（2026-09-11）

`COMPACT-GENERATION-CLOSURE-AUDIT` 增加完整角色池的 Fork 共享、错误池／人数约束拒绝、24 次原生／旧路径／缓存路径全 RNG 与卡牌指纹对照、生成实例突变隔离，并调用既有攻击池和无色池缓存合同。`CALL-OF-THE-VOID-GENERATION-ROOT` 使用同一完整亡灵输入，注入 4 层能力，连续三次原生 BeforeHandDraw；对照 12 张虚无牌、满手溢出、完整快照／续用、Fork 和实机后冻结根。它是该生成命令的模型对照，不代表紧凑随机生成闭包或整场搜索通过。[直接结果](performance/simulation-generation-root-20260911.md)。

## 上游 0.36.1 合并检查点（2026-09-11）

- `COMPACT-PAGESTORM-SEARCH`：`4062afbaaa7749afa4b20ddca77d86c5` Passed，30.44 秒含启动；250 节点旧新／串并行、实际并发 2、取消／故障排空及根复用。
- `RELIC-COUNTER-POLICY`：`6e67fdf96aaf4511a7e436e1de01b896` Passed，4.75 秒；十项根计数、Fork、实机推进隔离、设置/UI、真实搜索与原生末击；早停 2 节点，关闭后 17 节点。
- Release 零警告／错误，Linux 结构门禁 Passed（96 个 Search 文件）；PowerShell 对应规则未执行。未重跑完整部署、正常 NoGC 基准或覆盖分类 verify。

## 值 RNG 的完整池选取（2026-09-11，未完成）

`COMPACT-GENERATION-CLOSURE-AUDIT` 增加 78／50 个完整池的原生／旧链／缓存／值 RNG 四方对照；44 个边界组合覆盖空／单元素／双元素／完整池及负、零和超量请求，检查 scratch 尾部、容量不足拒绝、两池各 5,000 次复用选择的当前线程分配和完整 RNG 消耗。本轮 `3c479d36a88f4889b9d1f78f9d0c9251` Failed（23.945 秒），在首个池完成选择对照后，因分配／RNG 合并断言打印 160 字节而退出；没有完成第二个池和全部后续检查，分配归因待查。Release v2 和 Linux 结构门禁通过不代表该行为测试通过；保持失败断言及原生比较。后续归因已完成：160 字节为 Godot 进程内测试线程的一次性宿主开销，不是 `ValueRng` 原语；该场景现以独立的 RNG 与零分配块断言通过（见[新文档](performance/simulation-generation-metering-20260911.md)）。

## 生成池计量归因与紧凑生成执行（2026-09-11）

`COMPACT-GENERATION-CLOSURE-AUDIT` 的尾部计量更新为 5 块 × 5000 次（每池共 25,000 次）复用选择：RNG 消耗对全部块精确断言 `5×5000×(pool−1)`，分配断言改为“至少一块精确为 0 字节”（`min==0`），RNG 与分配各自独立抛出不同消息，不再使用合并断言或容差阈值。artifact-v3 连续两次与 artifact-v4 各一次 Passed，runId `1c9d47f348ea4473ae29037b2601913b`（24.03 秒）、`3ca8ef6137284d3dbffea37dce5100dc`（3.44 秒，复用进程）与 `a83926d065f247dc862bf1c1ea8c1304`（3.49 秒）。

`COMPACT-CALL-OF-THE-VOID-GENERATION` 复用 `COMPACT-GENERATION-CLOSURE-AUDIT` 的原始 NECROBINDER／AEONGLASS_BOSS 完整输入协议（38 牌、19 件遗物注入、保留初始遗物及两瓶药），注入 4 层 CallOfTheVoid 后执行三个批次；对照紧凑值层、旧 `TurnStartPowerSupport` 分支与原生 `BeforeHandDraw`，要求冻结池为 78 个候选。每批有序生成 ID 三方一致、五个 RNG 字段对齐、计数增量 308／616／924，12 张生成牌带虚无，第二批满手溢出转弃牌堆，逐实例 ID 递增且 `BORROWED_TIME` 跨批重复；撤销、确定性重放与实机后冻结根重放均验证。首跑通过：runId `b715d6a7100c4164b27c8c5bf7932152`（26.57 秒）。夹具沿用既有协议，120 秒上限、Instant，无新增双端字段。

`COMPACT-PAGESTORM-SEARCH` 在最终 artifact-v4 上 Passed，runId `68c51d68693b4e79a1ea03da531e4f2e`（9.20 秒），确认值层改动未破坏搜索等价。[完整证据](performance/simulation-generation-metering-20260911.md)。

严格形状（后续增补）：上述 `min==0` 形状已被三阶段计量取代——100 次 warmup；与正式测量完全同形的稳定化（同一个 `RunValueGenerationSelectionBlocks` helper、同样 5 块 × 5000 次，记录不读取、不计入分配断言，因此不作为通过证据）；正式 steady-state 5 块 × 5000 次，5 个块必须全部精确 0 字节，无容差阈值，也不再保留“至少一块为 0”的放宽。warmup／稳定化与正式段的 RNG 消耗分别精确断言（`(100+5×5000)×(pool−1)` 与 `5×5000×(pool−1)`），五字段比较与边界比较未删除。单块稳定化的中间形状在 Godot 首个正式块仍见 160 字节，runId `58268385f95c4d37a3383bfa46768b76` Failed，失败日志保留在 `.local/metering-strength-20260911/audit-run1-failed.log`；改为同形稳定化后 runId `e039ec137c8b4f768cf46d7e8fbdfdd9` Passed（24.04 秒），最终运行 runId `537d20c96a944b48b505b90f0bf7fd34` Passed（24.15 秒），证据目录 `.local/metering-strength-20260911/`。本批只收紧该审计计量形状，Release 零警告／错误、Linux 结构门禁 Passed（96 个 Search 文件）、PowerShell 未运行；整项极高配置完整搜索性能重构仍未完成。

## 虚空之唤生产编译与回合执行（2026-09-11，生产准入仍失败关闭）

`COMPACT-CALL-OF-THE-VOID-GENERATION` 在原三方差分上增加生产编译器与闭包普查：`CallOfTheVoid` 编译为单条 `ApplyBasicPower(CallOfTheVoid)`（数量取 `DynamicVars.Cards.BaseValue`），升级版只加原生 `Innate` 且指令不变，异常关键字与抽弃牌专用域显式拒绝；78 个冻结候选中 18 个可精确编译、60 个不可（首个 `BANSHEES_CRY`）。runId `46c0ff46f6b74e66ba551d5deb2e63ee` Passed（3.89 秒，复用进程；含启动的同批审计 24.03 秒）。

`COMPACT-CALL-OF-THE-VOID-ADMISSION`（新，SILENT／MECHA_KNIGHT_ELITE、`VH_PERF_MECHA`、30 张 RunCards、清空牌组且 Cards=[]）：先证明同形对照根通过完整准入，再实机施加 4 层 `CallOfTheVoidPower`，断言同一根在完整角色池第一个不可表示候选处被拒绝、拒绝不归因根内卡牌、单回合投影在回合闭包要求处拒绝且实机不变；潜行者池 78 个候选中 16 个可编译、62 个不可（首个 `ABRASIVE`）。runId `094fe477c6494317848cda39bfe0ca98` Passed（23.95 秒）。`COMPACT-SEARCH-LIFECYCLE` 用同一 30 牌根作回归哨兵：旧／紧凑 DOP1 与紧凑 DOP2 全部政策一致、并发 2、取消／失败排空、根复用与未迁移药水拒绝，runId `d1f51f1cd8614f41b2ed7c0ad9fd517f` Passed（5.11 秒）。

纯值 `tools/CompactCreatureChecks` 新增回合开始生成合同（施加／叠加计数与获得顺序、事件排在起手抽牌前、五字段 RNG 消耗、撤销与八工作区、零层不推进、满手溢出、三种构造拒绝），23 项全通过。夹具输入错误保留为失败基线：首次 `COMPACT-CALL-OF-THE-VOID-ADMISSION` 与同形 `COMPACT-SEARCH-LIFECYCLE` 探针都因缺少 30 张 RunCards 失败（空战斗牌集合与牌组数量不足），改用 `coverage/unattended/compact-void-admission-silent-run-cards.json` 后通过。生产准入未扩大：亡灵完整根继续显式拒绝，池模板构建的正向路径在池闭包完成前没有可执行样例。[完整证据](performance/simulation-void-generation-20260911.md)。

`COMPACT-DOOM-CARDS-NATIVE`（亡灵／MECHA_KNIGHT_ELITE、Cards=[]、敌人 300 HP、Instant、120 秒）由测试内部建局并注入 `DEATHBRINGER`、`NEGATIVE_PULSE`、`SCOURGE`、`PUTREFY`、`FEAR` 与 `STRIKE_NECROBINDER`：两种升级各六步出牌、两个完整回合、8 个原生动作、14 分支、4 个省略选择边界。生产编译器、紧凑 lane、物化投影与实机逐动作对比毁灭 21/26＋7/11＋13/16、虚弱／易伤施加顺序、消耗与虚无结果位置、Scourge 抽牌、Fear 先攻击后施加易伤、后续攻击消费 1.5 倍易伤与敌方阵营结束的持续时间递减；完整状态键、估值、能力元数据、九条 RNG、逆序、八工作区与实机后冻结根一致，runId `5193feebe1d947ad8de1015470d0ae60` Passed。

`COMPACT-DOOM-CARD-KILL-NATIVE`（同建局）实机先打出 `DEATHBRINGER`（`TryManualPlay(null)`，全体目标牌不传单体目标）并确认敌人仍存活，再由真实 `Hook.BeforeSideTurnEnd` 让生命低于等于毁灭层数的敌人直接死亡：保留格挡、无伤害历史、能力退休、终局锁定与撤销后无残留死亡状态，runId `d7b250f96c4c4f15956f4c05645dea31` Passed。

纯值 `tools/CompactCreatureChecks` 新增 `COMPACT_DOOM_VULNERABLE_CHECKS_OK`（24 项全通过）：敌方指令域拒绝、两条原生命令的整轮名单顺序与施加历史、人工制品整条拦截、易伤倍率、同牌内攻击顺序、持续时间递减、包含等号的毁灭阈值、格挡保留、撤销与八工作区。`COMPACT-CALL-OF-THE-VOID-GENERATION` 普查回归为 78 池 23 可编译／55 不可（首个 `BANSHEES_CRY`，原 18／60），`COMPACT-GENERATION-CLOSURE-AUDIT` 继续 `FullRootExplicitlyRejected`。[完整证据](performance/simulation-doom-vulnerable-cards-20260911.md)。

`COMPACT-DOOM-ROSTER-NATIVE`（亡灵／CORPSE_SLUGS_NORMAL、三敌、`--enemy-current-hp 256`、120 秒）在三个存活主敌人加已捕获奥斯蒂的名单上逐步执行这五张牌：紧凑 lane 的 `PowerChange` 序列逐条比对全体目标的毁灭／虚弱整轮名单、单目标的毁灭／虚弱／易伤与 Fear 先攻击后施加易伤，每一步的物化与读视图先自洽再与实机同一动作后的完整快照一致，撤销与实机后冻结候选重放一致，runId `3c3af9d6368e4441b6f893b1d9b33fe6` Passed（3.66 秒，复用进程）。它补上单敌夹具无法区分的 `AllEnemies`／`ChosenEnemy` 目标域。
