# CombatSolver 测试入口历史卷 02

## 策略迭代脚手架（开发中，2026-09-25）

- 前 150 第 17 包 `b8bafe147e09452b97111fb036a619ca`：`combat_start` 与第 7 回合玩家检查点严格恢复，原生状态核对通过；报告引用的第 4 回合检查点缺少状态材料。VeryHigh / 180 秒 / DOP 8，同根基线只有死亡路线，结果字段 75 HP / 0 瓶；最终双药成员完整胜利 69 HP / 2 瓶、剩余 6 HP。玩家第 7 回合检查点当前源码续搜为整场 38 HP / 累计 2 瓶。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T2306436226653-run`，双药成员 `20260926T2318178824475-run`，玩家检查点 `20260926T2310192232938-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 15 包 `10d01cc2d1f7445c8ff72e76e783aeb0`：`combat_start` 与玩家首、次回合检查点严格恢复，原生状态核对通过。VeryHigh / 180 秒 / DOP 8，同根基线完整胜利 12 HP / 0 瓶、剩余 69 HP；提前弃牌后 7 HP / 0 瓶、剩余 74 HP；玩家第二回合检查点当前源码续搜 5 HP / 0 瓶，未追平。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T2159092478223-run`，首回合检查点 `20260926T2202141519802-run`，第二回合检查点 `20260926T2201035376947-run`，最终 `20260926T2230339782718-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 11 包 `bb426281cba74e048f2fd79a8e41bb87`：`combat_start` 与玩家首回合检查点严格恢复，原生状态核对通过。VeryHigh / 180 秒 / DOP 8，同根基线完整胜利 51 HP / 0 瓶、剩余 24 HP；持续减费药水的有界开局前缀后为 18 HP / 1 瓶、剩余 57 HP，与旧人工投影一致。最终路线首回合攻击女王，第 3 至 6 回合清火炬，再收女王。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T2100329160712-run`，玩家检查点 `20260926T2102388988037-run`，最终 `20260926T2128515571842-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 8 包 `878d73bbdbed442bb5fcd13a5a4556c5`：`combat_start` 与玩家第二回合检查点严格恢复，continuation 和原生状态核对通过。VeryHigh / 180 秒 / DOP 8，同根修改前完整胜利 39 HP / 0 瓶、剩余 46 HP；换手前缀后完整胜利 9 HP / 0 瓶、剩余 76 HP。玩家检查点当前源码续搜 21 HP / 0 瓶。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1948481061725-run`，检查点 `20260926T1950352880644-run`，最终 `20260926T2004345144992-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 7 包 `4eb25e79483c462089f9c6088d650c77`：`combat_start` 和玩家第二回合检查点严格恢复，continuation 及原生状态核对通过。VeryHigh / 180 秒 / DOP 8，同根修改前完整胜利 34 HP / 0 瓶、剩余 52 HP；新增延后能力成员后 8 HP / 0 瓶、剩余 78 HP，与玩家检查点当前源码续搜一致。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1935528900427-run`，检查点 `20260926T1937517416782-run`，最终 `20260926T1944101350112-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 6 包 `ebeacefdcc49421294a8d66a7535caa7`：`combat_start` 严格恢复。VeryHigh / 180 秒 / DOP 8，同根基线完整胜利 47 HP / 1 瓶格挡药水、剩余 23 HP；取消格挡药水插入后的审计截断后，完整胜利 26 HP / 1 瓶迅捷药水、剩余 44 HP。报告所指玩家检查点 `:3` 没有状态材料，旧人工 4 HP / 2 瓶只作投影参考。有效结果 `.local/strategy-sessions/worldline-20260925/requests/20260926T1902005233467-run`；后续无收益组合实验已撤回，未跑哨兵或 Linux 门禁。
- 前 150 第 5 包 `a9d1a29a2f8b49879a0f2a3f9ad1761a`：开战根和玩家第二回合检查点严格恢复，后者原生状态核对通过。VeryHigh / 180 秒 / DOP 8，默认策略修改前 0 HP / 4 瓶、剩余 74 HP；仅允许无色药水的诊断对照为 16 HP / 1 瓶；最终默认策略为 16 HP / 1 瓶、剩余 58 HP，完整胜利，追平旧人工 16 HP / 1 瓶。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1817332465305-run`，单药对照 `20260926T1826260714679-run`，最终请求 `20260926T1840485624137-run`。未跑哨兵或 Linux 门禁。
- 前 150 第 3 包 `0edb8da283cf4ca1a543de9ffbc8dcbb`：开战根与玩家两处检查点严格恢复；玩家首回合录制事件重放通过。VeryHigh / 180 秒 / DOP 8，同根基线 50 HP / 0 瓶，目标、能力与防御后验后 22 HP / 0 瓶，完整胜利；玩家第二回合后检查点当前源码续搜为整场 18 HP / 0 瓶。最终请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1804375711652-run`。未跑哨兵或 Linux 门禁。
- 第 68 包 `493782770ca44fcaa63560ec98d130f0`：`combat_start` 严格恢复；修改前固定前缀尝试继续 `EndsPlayerTurn=True` 的虚空形态，初始搜索失败。过滤进攻及手牌整理的不可继续动作后，VeryHigh / 180 秒 / DOP 8 从开战根完整获胜，0 HP / 0 瓶、剩余 69 HP，证据 `.local/strategy-sessions/worldline-20260925/requests/20260926T1530557597261-run`。玩家第二回合检查点 `:3` 原生事件重放失败，原因是本地选牌 ID 13 与录制 ID 1 不符，不能作为当前源码续搜对照。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 66 包 `c683e82c46d84c719c8a19ddeed614db`：`combat_start` 严格恢复并搜索完整胜利，VeryHigh / 180 秒 / DOP 8 修改前后均为 4 HP / 0 瓶、剩余 22 HP。玩家第三回合检查点 `:5` 严格恢复；修改前在 `BuildOpeningHandSetupActions` 为先抽后弃的 `NEUTRALIZE+1` 估值时失败，修改后完整搜索为整场 4 HP / 0 瓶、剩余 22 HP。失败证据 `.local/strategy-sessions/worldline-20260925/requests/20260926T1456554816971-run`，修复后检查点 `20260926T1508000873100-run`、开战根 `20260926T1510447645464-run`。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 63 包 `73ab8686a86d41d3bf31230c739ffc7c`：`combat_start` 和玩家第二回合检查点严格恢复。VeryHigh / 180 秒 / DOP 8，同根基线 36 HP / 3 瓶、剩余 22 HP；最终路线 29 HP / 2 瓶、剩余 29 HP。玩家首回合用格挡药水并损失 2 HP，检查点后当前源码续搜再损失 27 HP / 1 瓶，整场同为 29 HP / 2 瓶。诊断确认首回合重放与玩家检查点只有弃牌堆中进攻、防御顺序不同；最终路线按玩家顺序出牌。基线请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1332254526998-run`，检查点请求 `20260926T1333507208018-run`，最终请求 `20260926T1430189918175-run`。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 61 包 `481ecc46c597437d828ba2bf8ebabf79`：`combat_start` 严格恢复，补槽后的完整预测 continuation 与玩家检查点相同。VeryHigh / 180 秒 / DOP 8，原基线 40 HP / 0 瓶且死亡；来源成本规则下开战根 38 HP / 3 瓶、剩余 2 HP，药水要求 27 HP（迅捷 18、混沌 9、生成的能量药 0）；玩家检查点同进程续搜 27 HP / 后续 2 瓶、剩余 13 HP，尚未追平，按用户要求暂跳过。最终开战根请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1316580403773-run`，检查点请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1259226152900-run`。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 59 包 `9daddd281e734097a8f8df32ef1026e5` 的 `combat_start` 与玩家第四回合检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前 47 HP / 1 瓶、剩余 1 HP；扩展双药梯度后为 37 HP / 2 瓶，持续伤害复制目标后验入选后为 5 HP / 2 瓶、剩余 43 HP。玩家检查点之前已用两瓶药水，当前源码续搜再用 0 瓶、22 HP、剩余 26 HP。最终请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1036269299230-run` 记录 `EARLIER_COPY_DELAYED_DAMAGE` 入选，战损 5 HP。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 58 包 `c8f249011b5544ecb3848069037bdf89` 的 `combat_start` 与玩家第四回合检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前完整胜利 27 HP / 0 瓶、剩余 14 HP；修改后 13 HP / 1 瓶力量药水、剩余 28 HP。玩家检查点当前源码续搜为 18 HP / 0 瓶、剩余 23 HP。最终请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T1019088538109-run` 记录第二回合在非药水动作后合法使用力量药水。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 57 包 `7ac4366d9f374d9cb2e58fcf47688ba5` 的 `combat_start`、玩家首回合及第三回合后检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前 58 HP / 1 瓶且死亡；最终源码为 55 HP / 2 瓶、剩余 3 HP 且完整胜利。玩家第三回合后检查点当前源码续搜为 56 HP / 1 瓶、剩余 2 HP；战损更低但资源成本未追平。最终请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T0954431463851-run` 记录下一回合换序后验胜利并入选。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 56 包 `5dbdbf0185ee4e3b8ee393d10a35d68d` 的 `combat_start` 与玩家首回合后检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前完整胜利 42 HP / 0 瓶、剩余 33 HP；最终源码 25 HP / 0 瓶、剩余 50 HP，追平玩家检查点当前源码续搜。最终请求 `.local/strategy-sessions/worldline-20260925/requests/20260926T0803330151506-run` 记录另一合法诅咒选项后验 28 HP 入选，缩短首回合前缀后 25 HP 入选。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 54 包 `1706dd3050a44182ba74ed1c842e1fe5` 的 `combat_start` 与玩家第四回合检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根修改前完整胜利为 13 HP / 2 瓶、剩余 2 HP；最终源码为 8 HP / 2 瓶、剩余 7 HP，追平检查点当前源码续搜。中途基线修正与前缀续搜先达到 9 HP，合法同类零费攻击补打后达到 8 HP；末次源码调整后目标结果再次通过。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 46 包 `eb78b8a887b841d884fdec9406ba7313` 的 `combat_start` 严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根完整胜利为 13 HP / 0 瓶、最终剩余 29 HP；玩家旧投影为 14 HP、录制未用药。玩家第 7 回合检查点恢复报 `native_state_mismatch:byte=569`，不能称旧人工路线严格核对。未改策略、未跑哨兵或 Linux 门禁。
- 第 45 包 `326fb1d060fc4e97a7f67e9c2466a995` 的 `combat_start` 与玩家后续检查点严格恢复，continuation 和原生状态对账通过。VeryHigh / 180 秒 / DOP 8 开战根完整胜利为 20 HP / 1 瓶、最终剩余 60 HP；玩家路线录制了两次力量药水使用，从检查点当前源码续搜为 15 HP、最终剩余 65 HP。原始战损仍多 5 HP，但少用 1 瓶，按 9 HP / 瓶折算净省 4 HP。未改策略、未跑哨兵或 Linux 门禁。
- 第 44 包 `ff9b6165cddb4b57bc02b99a3d22b099` 的 `combat_start` 录制状态 continuation 对账通过，旧包原生二进制因模型编号映射缺失不可比较。VeryHigh / 180 秒 / DOP 8 最终源码同根完整胜利为 5 HP / 0 瓶、剩余 58 HP；首回合组合前缀合法性修复后不再因重复物理牌导致请求失败。玩家第二回合检查点的首个原生动作不匹配（录制应打出 `TORIC_TOUGHNESS`，当前回放进入敌方回合准备），人工旧投影 3 HP 未严格验证。Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；未跑哨兵或 Linux 门禁。
- 第 43 包 `c6907e067c294192b614078f821331be` 的 `combat_start` 与玩家第二回合检查点严格恢复，continuation、原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根修改前 22 HP / 0 瓶、剩余 57 HP；有界零净费用前缀后完整获胜，17 HP / 0 瓶、剩余 62 HP。玩家第二回合检查点当前源码续搜亦为 17 HP / 0 瓶后续用药，仅作定位对照。最终源码同包复跑仍为 17 HP / 0 瓶，Windows Release 构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未跑哨兵或 Linux 门禁。
- 第 42 包 `3742f202cf4146b1a1543ba60c98f3c8` 预检有效，`combat_start` 严格恢复、continuation 与原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根完整获胜，3 HP / 0 瓶、最终剩余 61 HP；玩家旧投影为 3 HP 战损，用药未知。当前源码已追平战损，未修改策略；未跑哨兵或 Linux 门禁。
- 第 41 包 `f73f92c95c3145168ab7bfe96fc848a4` 预检有效，`combat_start` 严格恢复、continuation 与原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根完整获胜，0 HP / 0 瓶、最终剩余 66 HP；玩家旧投影为 0 HP 战损，用药未知。当前源码已追平战损，未修改策略；未跑哨兵或 Linux 门禁。
- 第 40 包 `384119c6bdea454a87bcd74d8574853a` 预检有效，`combat_start` 恢复通过；VeryHigh / 180 秒 / DOP 8 搜索达到上限，状态 `timeout`，没有当前战损结果。
- 第 39 包 `4b28d1e3575c425b96959fd6e1ca7018` 预检有效，但 `combat_start` 严格恢复在 `native_replay_events` 失败：第 9 个遗物为当前 `DEPRECATED_RELIC`，录制状态为 `ANCIENTAFFECTION-DEVOTED_SERE_TALON`。状态为 `restore_mismatch`，没有搜索或当前战损结果。
- 第 38 包 `358700198bb74b90b1942c2916bafb25` 预检有效，`combat_start` 恢复通过；VeryHigh / 180 秒 / DOP 8 搜索于 `assert_initial_solver_result` 阶段超时，记录 `exceeded_180_seconds_package_discarded`，没有当前战损结果。关联包 `5dacf918eb3e4bcd9be7b086dbda3a93` 同战斗会话，未重复运行。
- 第 37 包 `bd580e3209034bb294d77eda34eb8705` 的 `combat_start` 与玩家第 2、3 回合检查点严格恢复，continuation、原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根修改前 1 HP / 0 瓶、剩余 79 HP；修改后完整获胜，0 HP / 0 瓶、剩余 80 HP；玩家第 3 回合检查点当前源码续搜亦为 0 HP / 0 瓶、剩余 80 HP，仅作定位对照。仅修改比较器时仍为 1 HP；加入有界的跨回合防御候选后为 0 HP。Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未跑哨兵或 Linux 门禁。
- 第 36 包 `61a935eb01414ede9833448f1c659e1d` 的 `combat_start` 与玩家首回合后检查点 continuation 对账通过；旧包原生二进制状态不可比。VeryHigh / 180 秒 / DOP 8 同根修改前仅死亡路线，38 HP / 0 瓶；修改后完整获胜，30 HP / 1 瓶、剩余 8 HP，后验入选 `MAZALETHS_GIFT+MASTER_OF_STRATEGY+DISMANTLE`。玩家检查点当前源码续搜 36 HP / 0 瓶后续用药、剩余 2 HP，仅作定位对照。Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未跑哨兵或 Linux 门禁。
- 第 35 包 `9a7c143b932942d0a22ba19e1b074458` 的 `combat_start` 与玩家第 4 回合检查点严格恢复，continuation、原生状态对账通过。VeryHigh / 180 秒 / DOP 8 同根修改前 1 HP / 0 瓶、剩余 63 HP；修改后首回合边界复搜找到 0 HP / 0 瓶、剩余 64 HP，完整获胜。玩家第 4 回合检查点当前源码续搜亦为 0 HP / 0 瓶、剩余 64 HP，只作定位对照。Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未跑哨兵或 Linux 门禁。
- 第 34 包 `b0e5689561f6429c8a73766419436b8d` 的 `combat_start` 和玩家首回合后检查点严格恢复，continuation、原生状态对账通过。同根 VeryHigh / 180 秒 / DOP 8 修改前 26 HP / 0 瓶、剩余 38 HP，修改后 0 HP / 1 瓶、剩余 64 HP，完整获胜；后验日志中 `DEXTERITY_POTION+FOOTWORK+DEFEND_SILENT+CLOAK_AND_DAGGER` 前缀入选。玩家喝药后的旧求解投影为 7 HP / 1 瓶、剩余 57 HP；当前源码从玩家后续检查点续搜为 5 HP、剩余 59 HP，仅作定位对照。Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未跑哨兵或 Linux 门禁。
- 第 32 包 `080a80618c124d3c8b4a5dce883a1743` 的 `combat_start` 与玩家首回合后检查点严格恢复，continuation、原生状态均对账通过。VeryHigh / 180 秒 / DOP 8 同根改前仅死亡路线，60 HP / 0 瓶；改后完整获胜，52 HP / 2 瓶、剩余 8 HP。救援成员按首回合不同合法顺序复搜；入选 `SHRUG_IT_OFF+POMMEL_STRIKE+STRIKE_IRONCLAD`，第 3、10 回合分别用攻击药水、虚弱药水。玩家首回合后当前源码续搜为 56 HP / 2 瓶，只作定位对照。未跑哨兵或 Linux 门禁。
- 新批次第 26 包 `f74859853a4b49588770b6ee0f8a4a56` 的 `combat_start` 和玩家首回合后检查点严格恢复，continuation、原生状态均对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前 22 HP / 0 瓶，最终 8 HP / 1 瓶、8 回合、总展开 383,398；后验日志为 `VICIOUS+POTION_OF_BINDING+BRAND`，获胜且入选。当前源码从玩家首回合后检查点单独搜索为 8 HP，属于定位证据，不计入同根优化量。未跑哨兵或 Linux 门禁。
- 常驻会话在可用内存低于默认 4096+2048 MiB 准入条件时 `start` 排队；MemoryCleaner 退出码 0，清理前后可用内存 6307→6308 MiB，随后又降到 5790 MiB。`start --host-memory-mib 2560` 在同一个固定实例成功，PID 33156，随后第 26 包中途检查点请求完成搜索而非宿主准入失败；继续请求仍记录 VeryHigh / 180 秒 / DOP 8。该预留值不是游戏实际内存上限。
- 新批次第 24 包 `f88c625680e64a2c99a2ab8844abcbdd` 的 `combat_start` 及玩家用药后检查点严格恢复，continuation 与原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根修改前 36 HP / 2 瓶，修改后 0 HP / 1 瓶。智能生成选项日志：妙计路线与秘密技法路线未获胜，炸弹路线获胜、0 HP、1 瓶并入选；该路线第 1 回合用无色药水选炸弹并打出。未跑哨兵或 Linux 门禁。
- 第 17 包 `52728767fb5a42db81311a634f74f802` 严格恢复 `combat_start`，continuation 与原生状态对账通过。VeryHigh / 180 秒 / DOP 8，同根改前 35 HP / 1 瓶，改后 15 HP / 1 瓶，8 回合，总展开 223,026；能力路线日志记录 `WISH+FEEL_NO_PAIN` 前缀、完整获胜 15 HP，最终入选。玩家记录为操作后旧求解器投影 15 HP / 1 瓶；只确认数值追平，未独立重放完整人工路线。本包逐个运行，未跑哨兵或 Linux 门禁。
- 第 9 包 `ab0b65295edd48bab2d3b5dd53cd14ba` 在载入 Loadout v0.5.8 与 BaseLib 的隔离游戏源中从 `combat_start` 完成搜索；continuation 对账通过，原生二进制因原包未记录旧模型编号映射而不可比较。VeryHigh / 180 秒 / DOP 8、两槽药水均禁用时，改前 38 HP / 0 瓶，最终源码 23 HP / 0 瓶、6 回合、总展开 212,448；首回合“武装 → 燃烧+”，后续完整路线与原包 23 HP / 0 瓶的旧求解投影同序。原包智能药水政策在最终源码下仍为 0 HP / 1 瓶。只验证本包的质量，不将其外推至其他升级牌或其他 Mod 组合；未跑哨兵或 Linux 门禁。
- 夜魇通用入口替换逐包卡名链后，`faa009d05a2f411b9fadb30d46709192` 从同一 `combat_start` 严格恢复并完成 VeryHigh / 180 秒 / DOP 8 搜索，最终 0 HP / 1 瓶、21 回合；路线第 1 回合夜魇复制灵动步法，第 2 回合打出三张。候选提名按手牌整理、可用药水及夜魇复制目标的类型／价值／可支付费用进行，终局仍按完整路线排序。能力、攻击和技能三类均有候选入口；其他复制目标的质量未实测。本次未跑哨兵或 Linux 门禁。
- 新一轮三份可运行世界线包逐个从 `combat_start` 严格恢复并运行 VeryHigh / 180 秒 / DOP 8：`faa009d05a2f411b9fadb30d46709192` 修改前 2 HP / 2 瓶，修改后 0 HP / 1 瓶，路线第 1 回合夜魇复制灵动步法、第 2 回合打出三张灵动步法；`cd3dd71f2bf640108eae6c9ac9521882` 当前 10 HP / 1 瓶，追平站点人工战损 10 HP；`96734908a96f47e6a2fde88dc92cce40` 当前 6 HP / 0 瓶，追平站点人工战损 6 HP。第四份 `ab0b65295edd48bab2d3b5dd53cd14ba` 当时的无头实例未载入 Loadout，在首个原生事件的 `loadout_summon_powers=empty` 字段对账失败；补齐环境后的结果见上一条。本轮未跑哨兵或 Linux 门禁；两份已追平包是在组合改动前测得，最终源码未对它们复测。最终源码 Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。
- 固定实例实测：旧启动墙钟约 120.5 秒、旧暖进程导包约 100.9 秒；旧快照计划每次遍历 4,918 文件、约 3.7 GB。改为固定副本后，首次建副本的启动约 51.5 秒；复用副本但仍跑测试战斗的启动 28.3 秒。去掉启动测试战斗后，`fixed-c` 启动 13.8 秒，监控窗口约 1.1 秒就绪。扩容包随后直接运行，PID 均为 5304，墙钟 26.3 秒，`search_completed`；请求证据目录未产生 `preflight.json`。这些是不同阶段的实测墙钟，不作为受控提速倍率。
- Windows 无游戏快照夹具通过：稳定资产复用哈希缓存、同大小同时间戳的 Mod 重建仍被识别、游戏资产改变能更新固定副本；实际启动日志记录 `UNATTENDED_SNAPSHOT_PATCH changed=2 removed=0`，暖请求记录 `UNATTENDED_REUSE_ONLY ... snapshot_scan=skipped`。轻量启动第一次因游戏管理器尚未初始化而失败，第二次因沿用战斗清理等待而失败；修正后上述固定实例与导包实测通过。Linux 入口已接入复用模式，本轮未运行 Linux 门禁。
- 扩容策略最终 DLL：报告 `b642c1ccc4074802a40e4abcc97396a9` 从严格恢复的 `combat_start` 以 VeryHigh / 180 秒 / DOP 8 搜索完成，预计战损 35、用药 3；旧源码同根为 46、用药 3。最终路线首回合两瓶敏捷药水、白噪声、生成的扩容，四个对应专搜成员均完整获胜，最优 35 战损。独立哨兵 `62707d0e24684e1c827bc7812d8b0878` 同配置完成，原成员 20 战损、0 药入选；新增白噪声生成能力成员 31–45 战损，均未入选。另一个普通战斗 `1cd90a005b0c4c6ca9c0042251ae0424` 完成 38 战损、0 药，但起手没有白噪声，只作旁证。各包单独运行，最终会话 `stop` 成功。Windows Release 构建 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`。未运行 Linux 门禁。
- 独立监控窗口验收：`monitor-accept` 启动后游戏 PID 34500，监控进程 PID 39212 有独立 WPF 标题和非零窗口句柄。`b642c1ccc4074802a40e4abcc97396a9` 搜索完成；切换 `7a0eb30ddd6247cf8bcc7efa83721af7` 时状态文件立即显示新报告 ID，并在搜索中更新阶段、15,250 ms、2,492 展开节点、72,676 条已查世界线、4,765.6 条/秒、138 前沿节点、当前最好预计战损 29 与用药 0。搜索中关闭监控窗口后 `status` 为游戏运行、监控关闭；该包仍在 PID 34500 完成，最终预计战损 14、用药 1。`stop` 成功并删除私有实例目录。
- 同包有窗／无窗各一次，均为 `VeryHigh`、180 秒、DOP 8，从 `b642...` 的 `combat_start` 搜索，战损 46、用药 3、展开 8,152、转移 39,691 完全一致。有窗：墙钟 89.135 秒、请求 11.832 秒、搜索总耗时 10.010 秒；无窗：墙钟 88.107 秒、请求 12.071 秒、搜索总耗时 10.232 秒。只有单对样本，时间差小于一次运行的自然波动证据范围，不宣称监控零开销或固定提速。
- 重新 `start` 默认开启监控后，`stop` 同时结束游戏 PID 37312 与监控进程，私有实例目录消失。最终源码 Windows Release 主项目、CheckpointTool 均 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`、`git diff --check` 通过。本轮未运行 Linux 门禁。

- Windows `scaffold-accept` 会话启动后，已下载的 `b642c1ccc4074802a40e4abcc97396a9` 在同一开战根搜索两次。首次脚本哈希 `059a8b23`、参数哈希 `14d3d4fc`、PID 36064、墙钟 136.2 秒；修改脚本和参数后哈希为 `c6c13b35` / `dcb10d68`，PID 仍为 36064，`reusedProcess=true`、墙钟 85.2 秒。两次均 `search_completed`、预计战损 46。墙钟包含准备和清理，不以两份样本宣称固定提速率。
- 同进程无脚本哨兵同包 `search_completed`、PID 36064、预计战损 46、墙钟 83.3 秒。错误 C# 脚本 1.1 秒内明确记为 `strategy_or_input_failed`，留下编译日志，未向游戏提交旧脚本结果。`stop` 结束 PID 并清理私有实例；第一次停止曾因清理顺序与启动器自身所有权标记冲突失败，修正为启动器停止、独立所有权校验清理后成功。仓库忽略目录中保留会话请求证据，451 份原 ZIP 未删除。
- Windows Release 主项目和 CheckpointTool 构建均 0 警告、0 错误；Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=210`；`git diff --check` 通过。Linux 脚本入口已同步，本轮未运行 Linux 门禁。180 秒超时分类未用真包等到上限，仅静态核对工具分支，不能称为实测通过。
- 本轮结束前精确覆盖本地游戏 `mods/CombatSolver` 的 manifest、Release DLL、Windows MemoryCleaner、许可证与第三方声明；未启动可见 Steam。
- 最终接线 `scaffold-final`：启动后 PID 25648，`b642c1ccc4074802a40e4abcc97396a9` 使用示例 C# 脚本和空参数搜索 `search_completed`，`reusedProcess=true`，墙钟 89.1 秒；开战检查点恢复通过。结果中的主 DLL／脚本／参数哈希与请求逐项一致，有效政策记录 `VeryHigh`、DOP 8、`softTimeBudgetMilliseconds=180000`。`stop` 成功，私有实例目录不存在。最终接线没有再重复做两版脚本 A/B；那项证据见上一条。
## 未发布：搜索热路径 CPU 复查（2026-09-26）

- 基于 PR #138 的最终生产 DLL，Linux `perf record` 在静默猎手精英固定预算根得到 84,345 个无丢样 CPU 样本；另对 Regent 首领生产预算根得到 3,107,070 个无丢样样本。采样只用于热点归因，不用于耗时 A/B。
- 单因素试验将 `ReplayAction` 的捕获委托改为直接异常守卫，12 个五角色固定根各 ABBA（48 次独立进程，High、DOP8、Coordinator/组合、Smart、Server GC、5000 节点、120 秒）全部 Passed、无时间边界；路线哈希、展开、转移、战损、分数逐根一致。分配中位数之和少 0.41%，墙钟中位数之和多 2.37%；试验已撤回，未修改当前生产行为。逐根口径见[性能报告](../performance/search-hotpath-allocation-20260925.md#后续-perf-cpu-复查2026-09-26)，[48 份逐次结果](../performance/search-hotpath-cpu-20260926-rejected-trial.json)可复算。
- 本批没有启动可见 Steam 会话，也没有把无头样本当作帧时间或玩家可感知提速证据。

## 0.46.4：战损路线筛选与 Loadout 兼容（2026-09-25）

- 本机最新独立战斗日志：`SEARCH_SETUP_FAILURE stage=combat_root_snapshot`，异常是 `PowerGiver summon powers are configured or this Loadout version is not verified`；`godot.log` 证实求解器 `0.46.4` 与 Loadout `v0.5.8` 均已加载。实际 `v0.5.8` 的召唤钩子和公开怪物能力计数读取，与保留的 `v0.5.6` 程序集反编译结果一致。
- 修改前用实际 Loadout `v0.5.8`、BaseLib 和隔离游戏源运行 `LOADOUT-EMPTY-ROOT` / `05c8f3d560324d2aa91baca0a8697ffd`，在根快照断言失败，错误为 `Loadout PowerGiver summon powers are not inactive`。中间版对同一场景的 `d571e61d9bbe496fa91379743c140df8` 报 `Passed`：真实订阅者加载、空配置捕获和 Fork 通过，5 秒固定预算内取得首回合一动作零战损胜利路线。测试脚本退出后清理首次遇到文件占用；原生进程退出后通过仓库的所有权校验清理函数删除该实例。
- 最终实现不再以 Loadout 清单版本判定：仅在忽略目录的隔离游戏源把真实 `v0.5.8` 程序集对应清单临时改为模拟的 `v0.5.9`，`LOADOUT-EMPTY-ROOT` / `dd278b11f3f74d63a194e207e4d512fa` Passed。真实订阅者加载、公开怪物能力计数为空、根捕获与 Fork 均通过，5 秒固定预算内取得首回合一动作零战损胜利路线。测试实例由启动器删除，清单已恢复 `v0.5.8`。这证明版本号变化不会单独拒绝；没有取得真实未来版程序集，也未运行非空怪物能力配置差分或可见 Steam 实机。
- 根证书与数值合同：`HEAL-BOUND-SAFE-ROOT` 铁甲战士 `502f0df041fd460d8355dd7fd8102c38`、含精神过载的亡灵契约师 `1aedd4e7daba44fcb81b92e530e6a6db` 均 Passed；带鲜血药水的 `HEAL-BOUND-UNKNOWN-ROOT` `fccb56555b6d41fb9541c85f31a1bc8a` Passed，确认退回完整缺血余量。三次无头实例均由启动器清理。
- 战斗路径：放血短搜 `HEAL-BOUND-SEARCH` `1df1846a042948229de58f3088e67e5b` Passed，3 回合零战损获胜；该根提前达到可接受战损，剪枝数为 0，不作为提速证据。高灾厄、5 HP、敌 1 HP 的 `HEAL-BOUND-DOOM-TIMING` `39db271e55834aa8bfc23ab2773750ce` Passed，仍能在玩家回合结束前获胜；首次尝试因测试参数要求同时给卡牌 ID 与标题而未进入行为断言，修正输入后通过。实例均已清理。
- 带鲜血药水的强制用药搜索夹具 `HEAL-BOUND-POTION-ROUTE` `428aad7ece014d2cb40cdc73e3f9410a` 未找到可执行的必用药路线，故没有取得该路线的行为证据；根证书退回宽松界已由上一项独立验证。尚未做同根 A/B、可见 Steam 帧时间或 GC 暂停测量。
- 最终行为源码的合并哨兵 `HEAL-BOUND-SAFE-ROOT` `50b1cdcc907f424d894010ad48cd7e2f` Passed：亡灵契约师手中有精神过载、玩家 5 HP／10 层灾厄、敌 1 HP，根证书与数值合同通过，搜索仍在玩家回合结束前完成零战损胜利；实例已清理。该源码的 Windows Release 开发构建 0 警告、0 错误，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=209`，`git diff --check` 通过。

## 0.46.3：搜索速度指标与状态行去噪（2026-09-24）

- Windows Release 构建 0 警告、0 错误；PowerShell 结构门禁 `tools\verify-refactor-boundaries.ps1` 校验通过（`REFACTOR_BOUNDARIES_OK search_files=208`）。
- `English.json` 447 项词条格式与参数占位符校验全部通过。
- 控制器会话与 UI 状态生命周期无头测试通过：`pwsh -NoProfile -File tools\run-unattended-test.ps1 -ScenarioId QOL-CONTROLLER-STOP-172 -CharacterId IRONCLAD -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1 -VerifyControllerSessionLifecycle -ExpectedFinishedTurn 1 -TimeoutSeconds 120 -CleanupInstanceOnExit` 执行 Passed，验证了世界线数字、速度读数（xx 条/s）与平滑缓动结算断言，临时测试实例已由启动器清理。未做可见 Steam 实机人工验收。

## 0.46.3：内存回收设置说明（2026-09-24）

- 设置页回收相关的 32 个中英文词条均已精确映射对齐，面向玩家的文案清晰直观、消除术语堆砌。Windows Release 构建 0 警告、0 错误，PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=208`，`RuntimeGcProfileChecks` 54 项检查全数通过，`git diff --check` 通过。
- 仅修改 UI 文本与状态显示结构，未修改底层 GC 策略；未启动可见 Steam，真实设置页实机排版由用户验收。

## 0.46.3：新鲜资源保路通道探测上限（2026-09-24）

- Windows Release 构建 0 警告、0 错误（`-p:CopyModOnBuild=false`，不写实机 Mod 目录）；PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=208`；`git diff --check` 通过。
- 旗舰根 `EQ-IRONCLAD-ELITE-00` 串行 8 次 ABBA（执行前固定 A B B A A B B A，两臂各 4 次，全部样本保留）：墙钟均值 12.448 → 10.488 s（−15.7%），两臂区间不重叠（9.711–11.191 对 11.769–13.005）；`standPatProbes` −41.2%、展开 −22.7%、转移 −27.7%、`forkCount` −27.2%、累计分配 −27.8%；8 次预计战损、分数与终止边界逐项相同（52 / 9999279964 / `None`）。
- 60 根 `coverage/equivalence` 语料的上限扫描（VeryHigh / beam 135 / nodes 60000 / DOP1 / 60 s，workers 4）：8 名额三种预排与 32 档前缀均被否决（0～2 根存活/阵亡翻转、净战损 −45～+20），采用的 64 档在 58 可比根上 0 翻转、净战损 −7、更差 1 根（`EQ-DEFECT-ELITE-00` 0→2）、更好 2 根，探测 −13.7%、展开 −2.7%。`FULL-SILENT-ELITE-03` 两臂与 `FULL-DEFECT-ELITE-00` 候选臂为 `TimeLimit`，不计入判决。
- `tools/OfflineSearchHarness/compare_results.py` 逐字段对照基线臂与采用臂：60 根对齐、无缺根、6447 个非时间/非内存字段；`rootState` 与 `catalog` 差异 0，`route` 231 处/15 根，`continuations` 15 根，`solverMetrics` 非时间字段 399 处/34 根。结构化样本：[fresh-resource-standpat-probe-cap-20260924.json](../performance/fresh-resource-standpat-probe-cap-20260924.json)。
- 未执行：游戏内 `UnattendedTestRunner.StandPatProbes` 契约（双车道探测、注入异常传播、并行与串行等价）、玩家检查点批量回放、DOP>1 与组合（Coordinator/portfolio）路径、可见 Steam 帧时间与 GC 暂停、No-GC 区域行为。`tools/BeamRankSortChecks` 在未改动的 `main` 上即因 `Snapshot.PlayerDead` 报错，本轮未修改。
- 合并审查追加：PR #134 的 Windows Release 构建和结构门禁通过。`STAND-PAT-PROBE-BATCHES` 在默认小牌组未到达剪枝检查点；改用既有死灵药水输入后，PR head `aaf3ab0ce9124430a554535f232c2aa2` 与未改动 `main` `339d90af220949d8aa49fd8ed861c247` 均因同一 DOP1／DOP2 非时序计数差异失败，路线、评分、预计战损及边界相同。因此该合同未通过，失败不能归因于 PR #134；两次私有实例已清理。未由此取得 DOP>1 质量结论。

## 0.46.3：ServerGC 普通启动自动接入（2026-09-24）

- PR #133 两平台配置／真实 CLR 合同各 54 项通过；Windows 私有实例首次准备、下次激活及另一次恢复启动均 Passed，详见[结构化证据](../performance/server-gc-auto-startup-20260924.json)。本轮合并修正了空路径检查顺序，相关配置合同与 Release 构建另以最终合并源码为准。
- 自动配置默认无头及显式启动器跳过；正式 Steam 设置开关、工坊更新链路和云存档未验证。此前可选 profile 的性能取舍沿用 PR #132 的五根原生宿主证据，不把启动配置合同当作可见性能验收。

## 0.46.2：可选 ServerGC 启动配置（2026-09-24）

- PR #132 的 `RuntimeGcProfileChecks` 25 项纯值合同、跨平台启动器合同及五根十个原生宿主请求证据见[专项报告](../performance/server-gc-launch-profile-20260924.md)；本轮集成验证另列于下。
- 合入当前 `main` 后，Windows Release 构建 0 警告、0 错误，PowerShell 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=208`，25 项 GC profile 纯值检查与 PowerShell 启动器合同通过。
- 默认模式 `PR131-SIDEBAR-PR132-UI` / `cdf2a4755c3a4ba4a183e5548445d826` Passed；ServerGC 模式 `PR132-SERVER-GC-UI` / `890a55679cc546b293d4c17c37900fe7` Passed。两次均穿过侧栏四项坐标断言、设置页和控制器生命周期，首回合结束；默认模式有效 NoGC 为 true，ServerGC 模式为 false，私有无头实例均被启动器清理。此项验证不含可见排版或长线性能对照。

## 0.46.2：策略侧栏展开方向（2026-09-24）

- 控制器会话合同新增四个 1920 宽视口的侧栏横坐标断言：右侧可放、主面板贴右缘改放左侧且不相交、两侧都放不下时分别贴右缘和左缘。

## 0.46.1：在线连接配置恢复（2026-09-24）

- 0.46.0 发布 worktree 缺少 `presence.props`、`showcase.props`，其 MSBuild 监控端点属性为空；主仓库私有配置存在且属性非空。0.46.0 发布 DLL 的监控和战斗展示连接元数据均不存在，线上监控容器正常运行。
- 新门禁拒绝缺少私有配置的发布构建，也拒绝 0.46.0 旧 DLL 缺失 `PresenceEndpoint`。从明确指定的私有配置目录执行 Windows Release 发布构建通过，0 警告、0 错误，产物通过两项连接元数据检查。
- `ONLINE-PRESENCE-CONTRACT` / `db7129b3f04b41f38a9d1307670c114a` Passed：默认与关闭设置、当前战斗标量快照、真实 HTTPS 连接及错误证书指纹拒绝通过；只向端点发送应返回 400 的空请求，不生成在线玩家记录。无头实例已清理。
- `git diff --check`、Bash 构建脚本语法与 PowerShell 脚本解析通过。未进行可见 Steam 在线状态验收；正式 0.46.1 构建将在最终提交后执行，不重复相同源码的行为场景。

## 0.46.0 定版验证范围（2026-09-23）

- 本次合并后只同步版本与发布文档，UI 行为源码沿用下列 0.46.0 无头路线场景与结构门禁证据；发布构建从最终提交执行，不重复相同输入的行为场景。

## 0.46.0：UI 视觉层级重构与排版布局优化（2026-09-23）

- Windows Release 构建通过，0 警告、0 错误。
- PowerShell 结构门禁 `tools\verify-refactor-boundaries.ps1` 校验通过，`REFACTOR_BOUNDARIES_OK search_files=208`。
- `git diff --check` 格式门禁通过，无空白行或悬挂空格。
- `ROUTE-ROW-REUSE` / `3f480bd1bd3c468a8c0d73799ea1486d`、`e242a10e56dd4ddb8526912435a0c3b8`、`78160d7af1d040f9918539fc22a74f6b`、`8b1cf2751de34bf3a772d3f224501704`、`e1b78931a6644d0683789afdca4fe5f9` 与 `5f37adf0fff947188e17323a4823c926` Passed：动作块构造、路线行复用、执行状态、语言往返等既有布局与状态合同全部通过；已验证回合开始选牌胶囊专属色标与动画、循环组 `LoopBadge` 徽章随卡牌流式排版、消除下沉对齐与大框自适应贴合、循环结束胶囊淡化熄灭生命周期；已指定 `EvidenceDirectory`，无头实例由 `CleanupInstanceOnExit` 自动清理删除。
- `UI-LOCALIZATION`：在基线提交（0c5f677b）夹具生成怪物时即因 `ConditionalBranchState.GetNextState` 抛出 `No valid next state found`，无法在当前夹具环境完整通过，如实记录未标记为通过。
- 本轮只验证代码编译、结构门禁与无头交互合同；浅色、深色主题的可见画面排版与交互未进行 Steam 实机观感验收。

## 0.45.0 定版验证范围（2026-09-23）

- 本次仅同步版本与玩家更新日志，行为源码沿用下列 PR #130 Windows 集成和位置持久化成功证据；发布构建从最终提交执行，不重复相同行为场景。

## 求解器窗口位置持久化（2026-09-23）

- `UI-POSITION-PERSISTENCE-20260923` / `e9be7c39948343ecb6d1c5d886b1350e` Passed：无头原生战斗中检查位置写盘后重新加载、鼠标释放经输入桥保存，以及大面板临时挤压后恢复原位置；同场既有窗口缩放、折叠、设置和控制器生命周期合同通过，首回合战斗正常结束。引入位置回归断言后的初次运行 `6e8df725fd6d441a90fa272eb3f46baa` 在窗口持久化合同失败；修复后通过。两次实例均由启动器删除。
- Windows Release 构建 0 警告、0 错误。未进行可见 Steam 鼠标拖动和跨战斗视觉验收；无头结果只证明事件与设置文件、布局状态的合同。

## 搜索读数过渡动画（2026-09-23）

- Windows 集成：Release 构建 0 警告、0 错误，PowerShell 结构门禁 `search_files=208`。控制器会话 `b173172965a4434cb468b87502369aab` Passed；`UI-LOCALIZATION` 在原生 `PHROG_PARASITE_ELITE` 场景首次进入新增节奏合同后指出呼吸峰值断言时刻错误，修正断言后的 `a45dd7eae3274b208d36ebd555f0b19b` Passed，中英简繁的循环高亮、节奏和行复用合同均通过。实例由启动器删除；未做 Windows 可见观感验收。
- macOS Release 构建 0 警告、0 错误（RitsuLib 0.6.2 工坊引用）；Bash 结构门禁 `search_files=208`。
- 控制器会话合同 `AssertControllerSessionLifecycleAsync` 在读取世界线摘要和进度比例前先让读数收敛，继续核对“已查阅 42 条世界线”与 `0.05` 进度；PR 作者在 macOS 未运行该合同，Windows 集成结果见上。
- 追加已用时间走表与上传进度缓动后重新构建 0 警告、0 错误，结构门禁 `search_files=208`；测试收敛入口只做缓动、不推进走表，既有 `0.05` 进度断言不受走表影响。
- 部署高亮过渡：`UI-LOCALIZATION` 的循环高亮合同在比较颜色前先收敛过渡，并新增节奏合同（未知节奏与 `1.5 秒` 间隔取 `0.08 秒`、`0.2 秒` 间隔取 `0.04 秒`、`0.02 秒` 间隔直接切换、呼吸延迟内为 0、满幅峰值 >0.99）；行复用合同仍要求复用后立即全白。PR 作者在 macOS 未运行该合同；Windows 集成结果见上。
- 本地部署后 macOS headless 加载：本地 0.44.1 副本初始化成功，68 个补丁全部应用，工坊副本按设置跳过。缓动、呼吸与计数滚动的可见观感未进行 Steam 实机验收。
- 进度条取整修复后在 macOS 实机观察：搜索进度条、已用时间、世界线计数、内存条与执行高亮的过渡观感已确认。

## 0.44.1 定版验证范围（2026-09-23）

- 版本号与中英更新日志已同步；本次仅变更版本和文档。最终 Release 构建通过，0 警告、0 错误；行为验证沿用下列回合开始镜像、Power 施加差分和 Loadout 空配置实测结果，未重复运行。

## Loadout 空怪物能力配置（2026-09-23）

- 日志基线：本机最新战斗日志在根捕获拒绝 `Loadout.Services.PowerGiver.PowerGiverSummonHook`；Loadout `v0.5.6` 的公开实现显示其怪物能力计数非空时会在召唤及部分怪物阶段切换时施加 Power。当前跑局侧文件的 `monsterCounters` 和 `combatStartSnapshot.monsterCounters` 均为空。
- 使用隔离游戏源载入实际 `Loadout.dll/.pck`、BaseLib 与求解器，`LOADOUT-EMPTY-ROOT` / `c3f95d2453954479aa5ad790691a1c3a` Passed：断言真实订阅者已加载、公开计数快照为空、根状态戳和 Fork 均保留空配置。`LOADOUT-EMPTY-SEARCH` / `cc1a04534280423497ae2db5cc96939d` Passed：固定 5 秒预算的首个搜索得到 1 动作、零损、首回合胜利路线；两个无头实例均由启动器删除。
- Release 构建 0 警告、0 错误；结构门禁通过。未运行怪物能力计数非空的语义差分；该配置仍明确拒绝。

## 0.44.1：玩家回合开始三阶段镜像

- `TurnPhaseMirrorChecks --after-player-start` 原有 40 项，审计补充普通阶段生成 Late 监听者后为 41 项；`--after-player-start --vanilla` 1 项、`--after-player-start --seal` 3 项。覆盖三表登记拒绝、精确类型、冻结、三阶段监听顺序、轮间成员变动、卡牌 COW、选择暂停与未知覆写拒绝；已登记但入口尚无外部监听者时仍进入三轮派发。`--mask <生产 DLL>` 确认 61 个独立 bit。
- 主 DLL 与离线宿主 Release 0 警告 / 0 错误；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=208`。
- CoverageCatalog 原表两条上游旧证据状态无法解析；仅在隔离副本排除未引用记录后，3035 条通过，无未分类、缺失或非通过引用。生产源码与原证据表未改，详见 [检查摘要](../../../coverage/equivalence/after-player-turn-start/validation.json)。
- 对照 `523aea57` 的 EQ 10 / FULL 40 / GA 10：60 对有效、无时间截断，6341 个确定性字段 `IDENTICAL`（1069/4212/1060），一次批次无补跑。口径 High 90 / nodes 250000 / 分支 48/28/36 / Coordinator / Smart / DOP 1，逐根数据与命令见 [等价证据](../../../coverage/equivalence/after-player-turn-start/README.md)。

## 0.44.1：回合开始前镜像

- `TurnPhaseMirrorChecks --start` 22 项、`--start --seal` 1 项通过：精确类型、空/重复/抽象/未覆写拒绝、首次派发与首根冻结、Power/遗物/Modifier/卡牌混合顺序、参与者、选择暂停、监听者快照。原晚期回合末 25 项通过。
- 变基至 `6922828d` 后，主 DLL、宿主及检查工具 Release 均 0 警告 / 0 错误；Bash 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=208`。`--mask <生产 DLL>` 确认 58 个单 bit 互不重叠，BeforeSideTurnStart 不复用 AfterEnergyReset 的 bit 56。
- CoverageCatalog 原始表解析失败：上游两个未被 classifications 引用的 LOOP 证据使用未知枚举状态。仅在 archive 隔离副本排除两项后，3035 条核验通过、无未分类/缺失/非通过引用；没有改动原表。工具的 RitsuLib 分拆与 SmartFormat 引用只在本地构建补齐。
- `6922828d` 对照本入口：EQ 10 / FULL 40 / GA 10 一次完成，High 90 / nodes 250000 / 分支 48/28/36 / Coordinator / Smart / DOP 1；60 对均 Passed，6341 个确定性字段 `IDENTICAL`，无时间截断。两侧各一个离线宿主，600 秒软预算，未中断或补跑；输入、DLL 哈希、逐根摘要及复算命令见 [0.43.3 等价证据](../../../coverage/equivalence/before-side-turn-start-0433/README.md)。 变基到 0.44.0（`42e09028`）后重跑 EQ 10 根：`compare_results.py` 1,069 字段 `IDENTICAL`；`TurnPhaseMirrorChecks --start` 22、`--start --seal` 1、`--mask` 58 个单 bit 互不重叠。
- 历史 `8be1410` 对照：5 角色 × 精英/首领共 10 根，High（90/50000）、Coordinator、Smart、DOP 1，992 个确定性字段 `IDENTICAL`。变基前的历史数字，原始产物未随分支保留；当前基线证据见下一条。

## 0.44.0 发布验证范围（2026-09-22）

- 本次定版仅变更版本与文档，复用下列集成、UI 本地化、左起编号及完整部署的成功证据；由最终提交执行一次 Release 构建并生成最小包，统一脚本记录三个渠道的发布结果。
- 未追加可见 Steam 验收或性能测试，不将已有无头结果扩展为可见效果或通用性能保证。

## 生成敌人编号与循环分组（2026-09-22）

- 左起编号修正：`UI-LOCALIZATION` / `3f56d808fea54349a12b4d92cc560427` 在原生 `PHROG_PARASITE_ELITE` 场景 Passed（26.40 秒）。eng/zhs/zht 下倒序创建四只扭动虫，编号逐项对齐原版 Marker2D 横坐标顺序；Fork 与移除已死敌人后标签保持。此项替代此前内部战斗 ID 后缀的显示约定，既有根敌人标签保持。
- `SPAWNED-LEFT-ORDER-DEPLOY` / `ac863c2c1bc1463b99986ce8c535bdc7` Passed（23.45 秒）：原生寄生虫场景，五张打击/五能量/100力量，先杀寄生虫再杀四只扭动虫；严格增量、Instant/0 完整执行，5动作零损T1获胜、0计划外重算。该最小场景经过目标生成与最终击杀注释的生产路径，不代表截图原局面的完整复现。
- 最终 Windows Release 0 警告、0 错误，PowerShell 结构门禁 `search_files=208`；两个实例均由启动器删除，未进行可见 Steam 验收。
- `UI-LOCALIZATION` / `d63691bed27a4f8faf9a84ba41a3ac9f` Passed（26.22 秒）：每种 eng/zhs/zht 语言下，从冻结根的模拟器创建四只扭动虫，核对标签各异、含战斗 ID、Creature 与击杀记录名称入口一致。新增显示身份合同核对物理手牌序号/阵容索引归一化、不同 CombatId 即使同名也分开、Search 原键仍区分物理手牌序号；既有高亮、宽窄布局与复用合同通过。
- 循环展示纯合同 125473 项通过，含两次重复直接展开、三次折叠、完整序列还原与动作索引唯一映射。Windows Release 0 警告、0 错误，PowerShell 结构门禁 `search_files=208`。
- 首次新增测试 `0aebe0e839d74f599900a260452212d7` 在测试准备中把主线程根捕获放进隔离域，被 Power 惰性物化保护拒绝；仅修正测试的捕获顺序后通过。两次实例均已清理。未获得截图原局面的完整路线日志，未进行该原局面逐动作复现或可见 Steam 验收。

## 紧凑循环动作组（2026-09-22）

- 虚线在上次位置基础上再下移 2 像素：仅调整绘制坐标，Windows Release 构建 0 警告、0 错误；未重跑行为测试，可见观感未验收。
- 虚线按实机截图下移 1 像素：仅修改绘制坐标，Windows Release 构建 0 警告、0 错误；未重跑行为测试，调整后的可见观感未验收。
- 蓝色虚线追加：Windows Release 0 警告、0 错误；`UI-LOCALIZATION` / `1299d057871f4c2aac47486b1297c7f1` Passed（26.36 秒），既有中英简繁、紧凑高度、宽窄往返、高亮与复用合同通过。源码仅在现有空间绘制下划线，未改变布局尺寸；未进行可见观感验收。实例 `loop-dashed-underline` 已由启动器删除。
- `UI-LOCALIZATION` / `1ccc7277cdba46279dc5c1e295f496b0` Passed（26.39 秒）：eng/zhs/zht 中 41 个动作折叠为一个双动作循环组与独立末击；次数位于动作右侧，宽布局贴合内容、组高仅增加外框留白、末击同排，窄布局内部换行且动作/次数均被外框包围，宽→窄→宽恢复通过。循环中间/末次/后缀高亮及行复用通过，既有完整本地化合同通过。
- Windows Release 构建 0 警告、0 错误，PowerShell 结构门禁 `search_files=208`。无头实例 `compact-loop-ui` 已由启动器删除；仅 UI 布局与显示变化，未重跑搜索语义或性能基准，未进行可见 Steam 观感验收。

## 能力驱动的 Power 施加不再继承外层卡牌来源（问题包 c4e28f3b）（2026-09-22）

- 本体 Release 构建通过（0 error）。
- 新增严格差分夹具 `LAMP-POWER-SOURCED-DEBUFF`：向战斗注入 `CorrosiveWavePower`，手牌给后空翻、
  并向**抽牌堆注入 2 张**保证抽牌真的发生（第一版夹具只清空牌堆，抽牌不发生、断言空过，已修正）。
  断言能力驱动的这层毒不按卡牌来源记账——不安油灯不触发、毒不被增幅。
- 改动前对照：本问题包的实机证据即修改前状态（预测毒 11／实机 5、油灯 1／0）。本次未在改动前的
  构建上重跑该夹具的反向对照：无头宿主当时被用户的可见游戏进程占用，未排队等待。
- 结构门禁 `tools/verify-refactor-boundaries.ps1` 通过；受影响的原版组合（腐蚀波 + 后空翻、吸取、
  手里剑/激怒/湮灭/撕裂/温柔等遗物与 Power 触发）走既有夹具与同一差分路径，未新增逐项夹具。

## 击杀后不再向已离场个体施加 Power（问题包 24b8f299）（2026-09-22）

- 本体 Release 构建通过（0 error）。
- 新增严格差分夹具 `LAMP-DEBUFF-ON-KILL`（不安油灯 + 中和打在会被这一击打死的目标上；
  `-EncounterId CULTISTS_NORMAL -CharacterId IRONCLAD`，需要两个敌人，否则一击杀就结束战斗）：
  - **改动前**（把 `CanReceivePredictedPowers` 还原成只看死亡阶段）**Failed**，错误逐字复现问题包：
    `Lamp kill mismatch: field=relicCounters expected={UNSETTLING_LAMP/1/0} actual={UNSETTLING_LAMP/0/0}`；
  - **改动后 Passed**，完成检查
    `LampDebuffOnKilledTarget:SkipsDebuffOnRemovedTarget:KeepsCharge:FullContinuationState`
    （预测与实机逐字比较完整 `ContinuationStamp`）。
- 哨兵（同一构建）：`LAMP-INDIRECT-POISON`、`LAMP-INDIRECT-TEMPORARY-STRENGTH`、`CRAB-RAGE-DEATH-TIMING` 通过。
- 结构门禁 `tools/verify-refactor-boundaries.ps1` 通过。
- **未建模**：实机里正在执行自己行动的怪物（`IsPerformingMove`）在死亡当时不离场，这个例外求解器不模拟
  （怪物行动不在预测范围内），代码注释已记明。

## PR #123 / #124 / #125 合并验证（2026-09-22）

- 行为基线为计算失败修复 `94254728` 加三个原 PR，合并提交 `0f7d6935`；两处计算失败生产修复文件与 `94254728` 完全一致。Windows Release 构建 0 警告、0 错误（`CopyModOnBuild=false`）；PowerShell 结构门禁 `search_files=208`、108 项组合合同、122505 项循环显示索引断言、12 项循环预算分类与 2 项比较上下文测试通过。
- 同一个无头进程启用 `COMBATSOLVER_VERIFY_FAST_LANES=1`，以下定向合同通过；这是合并组合的运行证据，作者的离线性能与大语料结果仍按各自原基线引用。

| 场景 | runId | 核对边界 |
| --- | --- | --- |
| ARSENAL-HAND-DRAW-SHUFFLE-CHOICE-REPLAY | `b70cc6575b474bbb8280fd3e0800466f` | 回合开始 Power 通知、选牌检查点、DOP1/DOP2、取消和异常 |
| ADJUSTED-ROUTE-INVALID-SUFFIX | `7a29239c6c5341e2a83b6eb1919f5cfc` | 失效手牌后缀、终局后缀舍弃与合法路线保留 |
| SEARCH-HP-TARGET-STOP | `39a66450ae2b4e3a94ec2d52ec788cc6` | 组合早停、治疗保护、成长、强制与智能用药、DOP2 |
| LOOP-HISTORY-DEPENDENCIES | `c8977ec522cd4660b143f4c085bd262e` | 三类牌堆历史键、未来生成读者、Fork 与并发共享额度 |
| LOOP-REPLAY-REQUEST-BUDGET | `515228c6cb8e4e94915bcc3676f101ec` | 两个真实 solver 分别消费 96/0 次回放、请求总额4096、前缀续搜、严格增量与 live 不变 |

- 反伤完整部署首次运行 `72d0e86119044d9bb515872f356f41b7` 在 NoGC 配置断言失败：搜索得到了零损 T1 胜利，生命周期统计有一次区域建立与结束，但断言时区域已结束，尚未完成部署验收。保留失败记录，未将其计为通过；本批无头实例 `pr123-125-integration` 已由启动器删除。
- 同一反伤夹具显式关闭 NoGC 后，`LOOP-DEFENSE-REPLAYED-THORNS-RESERVE` / `f35a94cf82c2456e8b161a8e2cf41bff` Passed：快速通道对账模式、严格增量、Instant/0 完整部署，3 动作、零损 T1 胜利、零计划外重算。独立实例 `pr123-125-deploy` 已由启动器删除。该结果证明本场战斗语义与部署，未解决或验收前述 NoGC 保留断言。
- 本轮未验证可见 Steam、性能收益或任意第三方补丁回退；未发包或更新本地游戏 Mod。

## 下一版本（开发中）：计算失败

- 未结清 Power：原玩家包 `716a294f…` 的 `search_request_AutoTurnStart` 检查点，修复前 `SearchOnly` 在第 5 回合的预抽牌前缀以 `STRENGTH_POWER:1` Failed（runId `3219ddc70d9e420e99b61a8762825b11`）；来源追踪确认为“军火库”在摸牌前生成牌时加力量。修复后同一包同一检查点 Passed（runId `ec5a5bc9f00749eeb1afdb7117fd759f`），搜索包括 2050 次回合前缀捕获与 1001 次复用。独立 `ARSENAL-HAND-DRAW-SHUFFLE-CHOICE-REPLAY` Passed（runId `38d1275a230f4b2e800a9838a0eb03fb`），覆盖生成牌、力量变更、预抽牌检查点、选牌兄弟分支及 DOP1/DOP2 等价；无头，不代表玩家战斗的可见部署。
- 插入路线：`ADJUSTED-ROUTE-INVALID-SUFFIX` Passed（runId `038c4e9ef71943f28559f3d91c6ea1e1`），同一真实模拟根分别验证计划手牌状态失效、致胜后仍有 EndTurn 的后缀被舍弃，合法致胜动作保留。终局原玩家包 `5b184b7d…` 在本机原生还原阶段因第三方模型的 `SavedProperty net ID 51` 与当前可用的 47 项不匹配而 Failed（非搜索断言）；未宣称原包搜索通过。20/9 两组的其他原包、可见 Steam 和长期路线质量未逐份复测。
- 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=206`；当前行为源码 Release 构建 0 警告、0 错误。各无人实例均已停止；本地游戏 Mods 目录的部署不等于可见 Steam 验收。

## 下一版本：热路径快速通道

- 等价性（High 90/50000、Coordinator、Smart、DOP 1）：EQ 10 + FULL 40 + GA 10 对未改动 0.43.2，`compare_results.py` 5,590 字段 `IDENTICAL`。变基到 `8826a333` 后加 5 个能力哨兵根共 65 根重跑：64 根 6,016 字段一致；`POWER-REGENT-ELITE` 在 600 秒批次预算下两臂各自撞上用药梯度的到期停止（机器降频，展开数 110000 对 106716），把预算放到 1,800,000 ms 单进程各跑两次后四次都停在节点上限，展开、转移、选牌分支、路线、分数逐字段一致。
- 校验模式 `COMBATSOLVER_VERIFY_FAST_LANES=1` 10 根零失败；负对照：故意改错 `AfterCardPlayed` 早期一趟的位图，校验 208 ms 内抛出并点名 `Kusarigama`，同一错位图不开校验时把 `EQ-IRONCLAD-ELITE-00` 从 `expanded=12190 / hpLost=52` 改成 `12785 / 74`。
- 计时（A B B A，每臂 4 次，机器空闲，单进程）：5 根跨根中位 −3.27%（−0.63% ~ −4.11%），REGENT、NECROBINDER、DEFECT 三根两臂完全分离；60 根分配总量 −0.41%，65 根批次 −0.56%（55/64 根下降）。批次并行时的墙钟受降频影响（同根同 DLL 满载 40 分钟后漂 20% 以上），不作计时依据。
- 只在离线宿主验证，未在游戏内验证；未测 DOP > 1；带第三方 mod 时位图关闭的回退只做了代码审查。

## 下一版本：默认组合再分配

- 默认组合再分配最终验收：新种子REALLOCATED-HOLDOUT35根/70进程，34对Comparable、1基线超时；2早结束/31同/1多损2 HP，无胜负翻转。Low/High两代表4对全部Comparable，Low防御多1回合，High消耗高压少7 HP。最终4根16次独立进程ABBA全部Comparable，同配置重复路线/质量/剪枝/工作完全一致；新接线与冻结双开关、关闭接线与冻结基线的代表根等价。108项组合合同、2组比较上下文、两端208结构边界、Release/宿主0警告错误通过。
- 原生 `CONTEXTUAL-REALLOCATED-DEPLOY` / `f51aa294fe2749d3b1f6b30e00e0fc8a` Passed，42.78秒：默认候选10 HP/0药/T9路线完整执行至胜利，0意外重算，Instant/0，实例清理；17742展开/75006转移。独立test没有战损改善，Regent+2 HP及ABBA托管采样均值+13.31%等代价如实保留；不外推Windows或可见性能。详见[完整再分配证据](../strategy/contextual-portfolio-reallocation-20260922-evidence.json)。

普通基线消融/有界进攻组合：开发32根均Comparable，战损/用药不变、结束回合1好/1差；组合保留训练代表消耗高压17→10 HP的离线收益，开发转移−4.73%、分配−4.11%，明确保留内存与发布延迟尾项。这是独立test前的开发阶段记录；最终独立测试、默认接受、原生和ABBA结果见本页顶部。宿主新元数据编译0/0、2组比较上下文检查通过；见[证据与协议](../strategy/contextual-portfolio-reallocation-20260922-evidence.json)。

已撤回条件窄成员替换原型：训练5根1好/4同；开发验证32根1好/31同，均未见时间截断，无胜负翻转，总转移−0.92%、分配+0.63%。原型115项组合合同、Release/宿主及两端208门禁通过；源码/合同已归档，不能把活动检查程序说成包含这些新合同。未运行新独立test、原生或ABBA；见[证据](../strategy/contextual-structural-refinement-20260922-evidence.json)。

后置结构探索实验（默认关闭）：5个训练代表1好/4同；validation32根实际预算观察1好/31同，无胜负翻转，其中8根新颖性时间截断，未截断24根1好/23同。原Beam成员工作/质量32根全部保持，唯一改善少1 HP；新增11项预算合同、12项停止原因分类检查、2项CLI拒绝通过，两端结构门禁208。没有新独立test、原生部署或ABBA验收；成本与尾项见[结构化证据](../strategy/contextual-adaptive-novelty-20260922-evidence.json)。

## 上下文排序与组合成本（2026-09-22，排序模型仍关闭）

- 单进展值同分截线实验：V1训练Evaluate20根5好/11同/4差、各1次胜负翻转；完整8代表2好/6同，但validation出现候选独有超时。仅基础分V3的validation32根2好/30同；冻结后test34可比根0好/32同/2差、无胜负翻转，另1基线超时并省略候选。实际战损+9、战略战损+15 HP，包含少回血与少用药的反例；默认关闭，不声明原生/ABBA通过。纯值生产方法合同覆盖单组排序、必保位置及旧旁路；最终默认/显式入口控制与构建/门禁见[结构化证据](../strategy/contextual-tactical-ties-20260922-evidence.json)。

- 有界追加进攻成员：五个完整训练请求1好/4同，32个validation可比请求0好/32同/0差（3对双方120秒超时排除），原成员的预算/准入/结果/展开/转移逐条保持。追加成员21次运行、11次达标跳过，validation无赢家；总转移+3.36%，默认关闭。合同106项、非法CLI组合5项、构建0/0、两端门禁208通过；不声明该排序原生或ABBA通过，test划分未使用。

- 窄进攻精炼替换宽成员：五个完整请求训练代表1好/4同，消耗高压17→10 HP；新种子validation35根32可比/3双方120秒超时，0实质改善/28同/4差，三根各+1 HP、一根0损多3回合，总转移−6.69%，拒绝默认启用。32根原普通主搜/窄成员工作与结果保持；两根关闭实验的完整路线/质量/工作保持。生产组合合同100项、四个非法组合、构建0/0、两端门禁208通过。独立2426节点探针仍找到10 HP见证，但有界追加尚未接入，不声明该候选原生或ABBA通过。

- 三项Beam敏感度140次训练测量全Comparable：敌方血量1.5倍在20根为8项实质改善/12同，但完整Coordinator五根仅少一回合且出现消耗高压胜转败，拒绝默认启用；没有为该候选声明原生/ABBA通过。20根默认结果与工作量保持；2根1倍扰动完整路线及工作量保持；9个非法/冲突参数拒绝，Release/宿主0/0，两端门禁208。见上下文实验记录及结构化敏感度证据。

- 组合目标早停初版35根：33可比/2双方120秒进程超时；31项实质相同、2个防御根多1/3回合，无胜负翻转/战损/回血差异，11根工作减少。7个定向治疗边界暴露少回血及延后卖血回血机会；加入主线程冻结的Heal变量/已有再生与实际回血保护后，11触发根质量/工作保持，7个边界与原基线完整政策和展开/转移相同。其余22个未触发重根未重新运行，不混称最终整批复测。
- 最终 `SEARCH-HP-TARGET-STOP` / `042d6ed60cdf47de9681a9c16e14ffeb` Passed，25.72秒：达标跳过、关闭开关保留审计、待抽治疗牌未实际回血时保留审计、DOP2、阈值3、成长/固定重放与混合用药合同通过，实例清理。此前未保护版runId `36c798602c1847709a2b3aebea302c1d` 只作为初版记录。最终Release/宿主0/0，两端结构门禁208通过；正式ABBA及默认配置完整部署见实验记录。

- 最终4根16次独立进程ABBA全部Comparable；两种收益根耗时−76.72%/−75.00%，防御根0损但T11→T13；未触发根耗时约0%/+0.39%，采样托管峰值+12.04%/+19.56%、工作集+1.43%/+3.27%，明确保留这些内存尾项。重复质量和展开/转移一致。
- 默认配置原生完整执行 `CONTEXTUAL-TARGET-STOP-DEPLOY` / `6dfaaebbb5784a0c896fe88c8136c288` Passed，29.85秒，预测0损路线执行至T6、0意外重算、结束HP下限65，Instant/0，实例清理。没有Windows或可见Steam性能验收。

- 自生成 105 根；训练 35 根的 105 次排序观察中 101 Comparable、4 TimeLimited，全部进程成功；时间截断不进固定工作量质量/性能结论。20 个定向根采集 60 次，得到 94 个有实质政策差异的候选配对，13 个根贡献标签；未知被剪分支不标负样本。
- `RankingChecks`：514 组真实/边界特征的 Python/C# 对齐，连同模型版本、范围、截断与终局旁路共 14921 个断言通过。
- 未注入模型的候选 DLL，在 `exhaust_resources-train-high`、`target_order-train-low` 两根与冻结基线的根、完整路线和所有非时序 pruneCounters 相同。
- 第一个模型的 20 根复搜：原政策含尾分为 7 好/4 差/9 同；去旧 Score 尾键为 6 好/4 差/10 同。消耗牌高压根由胜转败，拒绝；未进入 validation/test 或原生部署验收。不得把这些数字表述为已完成优化或普遍不退化。
- 两端结构门禁通过（208 Search 文件），Python 工具编译检查通过；追加三根六次外层保路诊断无时间边界。
- 连续威胁排序：全部中途节点版本20根4好/1差，拒绝；仅新回合起点版本20根3好/0差/17同，验证集35根3好/3差/29同；最终test35根2好/3差/30同，含两次胜转败。105根中104可比，1根墙钟截断排除；完整Coordinator另测，不声称训练外零退化。两版源码均编译0/0，终局/转置政策保持，默认关闭。
- 新增无人预算参数：Release与宿主构建0/0，Bash/PowerShell参数语法与非法范围拒绝通过；原生 `CONTEXTUAL-BUDGET-OVERRIDES` / `0ded12cc74694c3d90ea8bdb8a0f8992` Passed，Beam24/20000实际注入，0 HP/T1完整执行，0意外重算，实例删除。宿主M1元数据核对真实请求种子通过。此项未启用实验排序。
- 完整Coordinator test35根：33Comparable、2TimeLimited排除，主要质量2好/1差/30同，无胜负翻转；默认Medium/组合开启3个代表主要质量全同。候选仍关闭，未作最终ABBA；全部工作量与反例见下述记录。
- 当前排序证据是离线测量与纯值合同，没有可见 Steam 或原生新排序行为验收；复现命令和后续结果见[实验记录](../strategy/contextual-ordering-20260922.md)。

## 循环外层胶囊（2026-09-21）

- `UI-LOCALIZATION` / `ffaa5652dcfe42e89eea9e7cbd10e789` Passed：eng/zhs/zht 的 41 动作显示为「外框含 2 动作 ×20」加独立末击；在真实 Godot 容器中宽→窄→宽，断言框内换行、标题及动作完整包围、宽度不超父流；周期首/中/末次执行高亮、后缀高亮、重复填充复用通过。
- `ROUTE-ROW-REUSE` / `254c96d24a6d45eb9031971398696611` Passed：完整显示身份、失败填充重试、语言往返及订阅清理。既有非循环 16 动作行的两组 64 次不变填充分别 0.2099 / 0.1784 ms、各 48 B；这只证明既有复用路径，不作为循环新建布局或可见帧率的数据。
- Release 0 警告/0 错误；两项均用 120 秒上限和 `--cleanup-instance-on-exit`，实例已删除。没有改动搜索/模拟/执行数组；每个循环仅多 2 个容器节点。尚未验收可见 Steam 排版。

```bash
./tools/run-unattended-test.sh --scenario-id UI-LOCALIZATION --timeout-seconds 120 --cleanup-instance-on-exit
./tools/run-unattended-test.sh --scenario-id ROUTE-ROW-REUSE --evidence-directory "$PWD/.local/loop-group-row" --timeout-seconds 120 --cleanup-instance-on-exit
```

- PR #123 / 上游 8826a333 整合：Release 0/0、两端门禁 207；UI-LOCALIZATION `70dae8a331234fcbb6e3a51a40a089f1` 与必要格挡原生部署 `19f8f8ad583b4b029a5c559f759243fa` Passed，实例清理；cap / 多 solver 两组与 656a9608 完整路线、质量 Equivalent。

- [循环请求额度与历史依赖收尾](../performance/loop-final-20260921.md)：28 组（26 根）/23 完整同路线/5 改善；4 个最终原生场景 Passed（历史依赖、两 solver 共享额度、两种投影范围外伤害必要格挡）；严格增量与完整部署分别记录，实例全部清理。Python 10 项与两端结构门禁通过；ABBA 将时间切层组标为 Inconclusive，另列无时间切层的固定节点实验。

## 循环预算审计跟进（2026-09-21）

- [F1/F2/F3 修正及证据](../performance/loop-boundaries-20260921.md)：8 项 Python 分类合同；正常预算 cap＋finisher 的 A/B Equivalent；主动 2000 ms 时间切层两侧各 time=3/node=0，原始未击杀观察保留，工具正确返回 Inconclusive／2。无时间边界的差异仍退出 1，不放宽为自动通过。
- 新 `generic-loop-replay-estimate-margin.json` 离线单 solver：6835 HP，6 展开／4107 转移、4095 额外回放、零损 T1；估算 4101 并不证明超过 4096 无法完成。非原生验证，不加入原 19 根 A/B 等价集。
- Release 0 警告／0 错误；遥测拆分不改变搜索判断或战斗语义，未重跑之前通过的四项原生部署。请求级累计额度仍未验证，不将 Evaluate 的 4096 检查外推到 Coordinator。

```bash
python3 -m unittest discover -s tools/OfflineSearchHarness -p test_loop_boundaries.py -v
```

## 追加循环边界与审计（2026-09-21）

- [19 根边界集及审计复核](../performance/loop-boundaries-20260921.md)：同根串行 A/B；质量指标均相同，18 根完整路线相同，1 根同质量异路线。包含隐藏相位、Buffer、格挡、4096 耗尽、替代出牌、付费抽牌、低血卖血、三目标、选牌、星能和 BansheesCry。离线显式检查见 `coverage/unattended/loop-boundaries-20260921/suite.json`。
- 耗尽反例 ABBA：展开/转移 4675/9375 → 4675/13471，路线及 4 HP/T2 相同；求解 +11.3%、累计分配 +14.9%、采样活对象峰值 -18.6%。不是无退化验收。
- 下列前三项严格增量及完整原生部署 Passed、0 计划外重算；最后一项在首次结果断言后停止。runId 与实例清理记录见报告。不是历史 EQ10/FULL40；P4 投影低估反例尚未验证。

```bash
./tools/run-unattended-test.sh --scenario-id LOOP-BOUNDARY-LETTER-BRANCH-FINESSE --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --seed LOOPLETTEROPENERPHASE0111 --enemy-current-hp 61 --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 0 --clear-player-piles --clear-all-powers --cards-json '[{"cardId": "IMPATIENCE", "pile": "Hand"}, {"cardId": "FINESSE", "pile": "Hand"}, {"cardId": "IMPATIENCE", "pile": "Discard"}]' --relics-json '[{"relicId": "LETTER_OPENER"}]' --force-short-search-only --short-search-budget-override-milliseconds 10000 --search-max-degree-of-parallelism-for-test 1 --measure-search-phases --timeout-seconds 120 --performance-preset-for-test Low --initial-enemy-max-hps-json '[61]' --initial-enemy-current-hps-json '[61]' --expected-initial-projected-battle-hp-lost 0 --expected-initial-combat-ended-turn 1 --expected-initial-final-enemy-hp-at-most 0 --verify-incremental-search --expected-unexpected-replans-at-most 0 --cleanup-instance-on-exit
./tools/run-unattended-test.sh --scenario-id LOOP-BOUNDARY-MULTI-TARGET --character-id IRONCLAD --encounter-id CORPSE_SLUGS_NORMAL --seed LOOPLETTEROPENERPHASE0111 --enemy-current-hp 9 --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 0 --clear-player-piles --clear-all-powers --cards-json '[{"cardId": "FLASH_OF_STEEL", "pile": "Hand"}, {"cardId": "FINESSE", "pile": "Discard"}]' --relics-json '[]' --force-short-search-only --short-search-budget-override-milliseconds 10000 --search-max-degree-of-parallelism-for-test 1 --measure-search-phases --timeout-seconds 120 --performance-preset-for-test Low --expected-initial-projected-battle-hp-lost 0 --expected-initial-combat-ended-turn 1 --expected-initial-final-enemy-hp-at-most 0 --verify-incremental-search --expected-unexpected-replans-at-most 0 --cleanup-instance-on-exit
./tools/run-unattended-test.sh --scenario-id LOOP-BOUNDARY-BLOCK-BODY-SLAM --character-id IRONCLAD --encounter-id FUZZY_WURM_CRAWLER_WEAK --seed LOOPLETTEROPENERPHASE0111 --enemy-current-hp 37 --initial-player-hp 80 --initial-player-max-hp 80 --initial-player-energy 0 --clear-player-piles --clear-all-powers --cards-json '[{"cardId": "FINESSE", "pile": "Hand"}, {"cardId": "FINESSE", "pile": "Discard"}, {"cardId": "BODY_SLAM", "pile": "Discard", "upgradeLevels": 1}]' --relics-json '[]' --force-short-search-only --short-search-budget-override-milliseconds 10000 --search-max-degree-of-parallelism-for-test 1 --measure-search-phases --timeout-seconds 120 --performance-preset-for-test Low --expected-initial-projected-battle-hp-lost 0 --expected-initial-combat-ended-turn 2 --expected-initial-final-enemy-hp-at-most 0 --verify-incremental-search --expected-unexpected-replans-at-most 0 --cleanup-instance-on-exit
./tools/run-unattended-test.sh --scenario-id LOOP-BOUNDARY-BLOOD-LOW-HP --character-id SILENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --seed LOOPBLOODPOMMELQUALITY0111 --enemy-current-hp 40 --initial-enemy-move-ids-json '["INHALE"]' --initial-player-hp 4 --initial-player-max-hp 57 --initial-player-energy 0 --clear-run-deck --clear-player-piles --clear-all-powers --cards-json '[{"cardId": "BLOODLETTING", "pile": "Hand", "treatAsDeckCard": true}, {"cardId": "POMMEL_STRIKE", "pile": "Hand", "count": 2, "upgradeLevels": 1, "treatAsDeckCard": true}]' --force-short-search-only --short-search-budget-override-milliseconds 10000 --search-max-degree-of-parallelism-for-test 1 --measure-search-phases --stop-after-initial-solver-result-assertion --timeout-seconds 120 --performance-preset-for-test Low --expected-initial-projected-battle-hp-lost 3 --expected-initial-combat-ended-turn 2 --expected-initial-final-enemy-hp-at-most 0 --cleanup-instance-on-exit
```

## 下一版本（开发中）：循环验证（2026-09-21）

- [循环报告及逐次数据](../performance/loop-optimization-20260921.md)：2000 HP、1200 动作 A/B 路线逐字段相同，零损 T1；最终 DOP1/DOP2 6 展开/1206 转移一致，最大父节点并发 2。分支根关闭早停时 101/256 一致，动作并发 2；仅调度计数、lane 局部缓存条目数不同。
- `LOOP-DEFENSIVE-VALUE` / `50cfafd5acc642508e18840ab459f69a` Passed：100→200 格挡饱和、Barricade 原生保留、固定保留上限、BansheesCry 历史读者门控。Release 0 警告/0 错误，Bash/PowerShell 两端结构门禁均通过（search_files=206）。
- `LoopDisplayChecks`：122505 断言通过。`UI-LOCALIZATION` / `275d2dcafd214c62b675f5832a11993d` Passed；`ROUTE-ROW-REUSE` / `5ec0d3f0b90c450b8c43a87b0cdaee25` Passed。41 动作折叠、额外重放与末击后缀、循环高亮、失败重试、完整显示身份复用、中英简繁及订阅释放通过；未做可见排版验收。
- `LOOP-REPLAY-DEPLOY` / `d4867a3cb9154340a85fe848d1139a56` Passed：40 HP、24 动作、零战损 T1、严格增量/全前缀核对、原生部署、0 非预期重算。严格回放的时间不用于性能表。
- 新 `generic-loop-bloodletting-no-postcombat-heal-quality.json` 排除燃烧之血战后回血干扰，A/B 675/1820、3 HP/T2，完整路线相同；原生 `41f101db99fa4f8fb9a55bd6494cc456` 同断言 Passed。旧铁甲战士夹具仍会给 6 HP/T1（战后补满），不能将旧 3 HP 断言误报为通过。
- 50 万 HP 成长循环按 Low 60000 节点、RequireAtLeastOne 对照：两侧 10555/22248、零损 T1、完整路线相同。6000 节点的早期内环两侧都失败，只说明该预算不足。


## 0.43.3：余像路线与战后掉药预测

- 药水奖励机会成本：更新纯合同，概率 100%/40% 但结果未知的满栏情形额度均为 0；确定掉药按预测药水档位抵扣一次，确定不掉、药栏未满和禁用获得药水时额度为 0。UI 本地化合同新增搜索刚开始即显示预测掉药/不掉药，关闭预测时清空；英/简/繁分别核对。本地 Release 构建 0 警告、0 错误，自动部署的 DLL 与构建 DLL SHA256 一致。没有运行无人游戏合同或可见实机。结构门禁本次失败：`CombatBeamSolver.BlockPotionInsertion.cs` 缺少脚本硬编码的 `ReplayInsertedRoute(`，但当前 HEAD 的该文件原本就命名为 `ReplayAdjustedRoute(`，本次未改动该文件；不把门禁当作通过。
- 依据玩家本机 `combat-a6529611f1c3484bafe6d8a7d4f07f1d.jsonl` 的第 2 回合计划，原路线先撕咬／暴政、后打两张余像，原计划的逐动作格挡与原生 Hook 口径对得上；前置路径尚无同根实机执行证据。用户明确要求停止测试，已终止隔离场景并清理实例；本次只执行 Release 编译（0 警告、0 错误），没有将编译当作路线改善验证。
- `CombatSolver.GcPolicyChecks`：Linux / Windows 各 54 项通过（26 + 8 + 9 + 2 + 1 + 8）。新增 `diagnostic-failure` 用工具侧日志替身验证八条故障路径、异常身份、排队手动任务和后续搜索准入；上游检查点失败与中间版本漏结清手动任务均有 12 秒超时基线。
- Release 构建及 Bash / PowerShell 结构门禁通过。当前上游 `3f4002bd` 的组合候选 ABBA：三场 12 份有效搜索，6 对非时序指标和完整记录路线一致；因耗时/峰值代价撤掉根缓存。另保留一份系统压力导致 No-GC 未建立的失败基线，不计入性能均值。
- Skittish 单独筛选两场 8 份搜索、4 对对账一致，2 项原生差分通过；收益/代价仍不足，一并撤掉。最终纯修复版花园冒烟 Passed（`bed2628af2e94abd843a0c45103bbc05`），与上游非时序指标/路线对账无差异，最大 GC 暂停仍为 1645.559 ms。验证与清理证据见 [本轮报告](../performance/gc-completion-allocation-20260921.md) 和 [结构化数据](../performance/gc-completion-allocation-20260921.json)。均为无头或 CLR 证据，未验证可见 Steam 帧时间或整场自动部署。
- #122 合入 0.43.3 时处理两项审查意见：多次失败合并时展平既有 `AggregateException`，未取得回收后堆快照时三个指标用 `-1` 明确表示未知，不再伪装为真实 0。合并组合在 Windows 上运行 `diagnostic-failure` 8 项通过，结构门禁 `REFACTOR_BOUNDARIES_OK search_files=206`，Release 构建 0 警告、0 错误并自动部署本地 Mod；没有重跑作者已完成的 20 份性能对照、Linux 合同或可见 Steam。

## 0.43.2：混合用药、生成牌、路线缓存与增量历史计数

- 强制／智能混合用药：`SEARCH-HP-TARGET-STOP` / `312cb8cd77fb470eaac9bbc48cd19506` Passed，强制能量药与智能力量药的真实搜索在零战损胜利时仅用一瓶，DOP1/DOP2 完整结果和非时序指标逐字段一致；改为只持防御牌与 15 HP 敌人时，仅强制药无法获胜，智能火焰药作为第二瓶救命且不被误拦。纯合同核对强制基线只允许指定槽位、额外一瓶仅比强制基线多省 1 HP 时不满足 9 HP 门槛、强制药本身不计入额外药机会成本及梯度瓶数。隔离实例已清理。中间正向场景曾 Failed：初始接线把只允许强制药的临时策略传给后续 Smart 审计，使 `maximum=0`；改由审计读取原始逐瓶策略后通过。结构门禁 `REFACTOR_BOUNDARIES_OK search_files=205`，Windows Release 0 警告／错误。短根验证了混合策略、早停和救命路径；未取得玩家原战斗同根对照，也未实测非零但不足门槛的实际两药胜利比较。启动器曾报告一次 `Import-Clixml` 解析警告，随后游戏请求 Passed、目标断言完成；未把警告当成产品行为结论。
- #105 原提交合入后的集成修正：`AdaptedOnPlayChecks` 40 项和空登记 2 项通过，涵盖已登记生成牌根前预审、未登记生成牌由根冻结的补丁集合拒绝、根捕获后安装／卸载补丁不改变旧根及新根恢复普通镜像。`ADAPTED-ONPLAY-INTEGRATION-CARD` / `5ae3ade71d1e4e9c9eb0d9aaff6ce209` Passed，真实游戏的替换只执行一次、完整快照／增量回放／Fork／第 1 至 2 回合对账及晚装补丁拒绝通过；最终构建的 `ADAPTED-ONPLAY-INTEGRATION-REUSE` / `1b16e4e994f74934ba79ecf044a324f9` Passed，精确续用到第 2 回合、计划外重算 0。两场隔离实例均已清理。Windows Release 0 警告／错误，`REFACTOR_BOUNDARIES_OK search_files=205`。首次把请求 JSON 误传给 `-GeneratedScenarioPath`，启动阶段报未知 `scenarioId`，属于命令输入错误，不计为产品断言；改为显式测试选项后上述场景通过。未跑可见 Steam、任意第三方 Mod 或执行中并发热换补丁；生成牌的根冻结边界由独立合同覆盖，而非真实游戏生成牌场景。
- #117/#118/#119 均以原 PR 提交 merge 到已发布的 0.43.1 基线上，仅手工并列解决版本文档与双平台结构门禁冲突。合并组合 Windows Release 构建 0 警告/错误，`REFACTOR_BOUNDARIES_OK search_files=205`。#117 的辅助失败边界收窄后，Windows 离线单根 `OFFLINE_HARNESS_ANCILLARY_CHECKS=1` / `ancillary-integration` Passed：原 17 项磁盘/取消合同与新增程序错误传播断言合计 18 项；固定 150 节点、DOP 1、实际展开 119、转移 405。作者的 60 根逐字段对照、#118 的逐事件验证构建、#119 的百万项前沿检查及三根 VeryHigh 观察均是各 PR 的原有证据，本次未重跑，不等同于 0.43.1 三 PR 合并组合的整场等价性或可见实机验收。
- 离线宿主 `OFFLINE_HARNESS_ANCILLARY_CHECKS=1`（DEFECT、`FUZZY_WURM_CRAWLER_WEAK`、High、DOP 1）在一次真实求解结果上注入磁盘故障，17 项通过：正常写读往返、临时文件清理、坏 JSON 与空路线按未命中处理并改名 `.bad`、隔离后同一键可重新写入、独占文件锁按未命中处理且不隔离、解锁后可读、缓存目录被文件占位、Unix 只读目录、目标路径被目录占用、结果序列化字节不变、取消异常传播、普通失败只记一次日志。作者在 macOS 本机运行；PR 阶段未验证 Windows 文件锁，本轮短根已覆盖 Windows 独占锁分支。
- 录像采集与打包的隔离、打包调用顺序调整只经过编译和结构门禁，未在游戏内验证；符合录像条件的进阶 10 第三幕 Boss 无伤路线本机没有复现条件。
- `OFFLINE_HARNESS_HISTORY_CHECKS=1`，DEFECT、`--milestone M1`：21 项通过。检查原始/完成事件、嵌套自动出牌、两种暂停续接、普通 Fork、父/根隔离和键位一致性。
- `-p:VerifyHistoryCounters=true` 逐事件及 Fork/构键读取核对独立全扫描。语料、构键计时与验证范围见 [专题](../strategy/incremental-history-counters.md)。
- EQ 10 / FULL 40 / GA 10 对照 `8be1410`：60 根有效，5,670 个确定性字段及补充预算/剪枝字段一致。普通构建另测两个根的选中通道构键阶段，数据见专题。变基到 0.43.0（`cccc270`）后重跑 EQ 10 根，`compare_results.py` 992 字段 `IDENTICAL`。
- 前沿与观测检查 1,024,010 项通过，新增标签数、首次触顶、峰值跨重建保留和分布检查；原支配决策逐项对照独立 List 基准。
- EQ 10、FULL 40、GA 10 对照 `8be1410`：60 根有效，5,670 个确定性字段、额外预算／剪枝字段及完整结果快照一致。变基到 0.43.0（`cccc270`）后重跑 EQ 10 根，`compare_results.py` 992 字段 `IDENTICAL`。
- VeryHigh 生产预算观察：三个重型根均未触顶，最大占用 832,793 / 1,000,000（83.28%）。没有触顶根，未运行放大上限臂；本轮不提供默认触顶后的质量结论。数据见 [专题](../performance/transposition-cap-evidence-20260920.md)。未实机验证。

## 0.43.1：英文界面启动与疯狂科学成长策略

- `GROWTH-POLICY-FREE-FIRST` / `fb11ce8e1d95410b80d58555a26f6b07` Passed（22.80 秒）。在 Defect 独立原生战斗中注入疯狂科学能力／改进变体，冻结可升级正式牌组三张的目标，预测出牌产生一层改进及一次独立成长额度，随后真实出牌并逐字段对照；另核对非改进／非能力变体不计成长、两张牌与一张可升级目标时目标封顶、零容量不产生目标、额度设置往返、侧栏独立行、Fork 隔离及重复记录封顶。修复前 `630075e609644c418a9c5eece3b23330` 在变体识别断言按预期 Failed；中间 `45bee93749284fff8eba0dc8839755c7` 为夹具错误地重复转可变卡，`4c962a31b75f4a53a278e4b2c4373474` 与 `d3a9d2720aba442e8e3ae03eff03538d` 是反射回放参数及未重编 DLL 的夹具失败，均非产品断言失败。所有隔离实例已删除。未覆盖事件实际生成选项页及正式战后随机升级的可见动画。
- `UI-LOCALIZATION` / `db0cc94e1f604a21843514d4c5bd1610` Passed（25.76 秒），eng/zhs/zht 子面板构造和动态标题未因新增成长行失败；结构门禁 `REFACTOR_BOUNDARIES_OK search_files=204`。均仅为无头／静态证据，可见排版尚未验收。
- `SEARCH-HP-TARGET-STOP` / `e60944f238684bc1a41135317e7d10d6` Passed（23.55 秒）：零损/阈值、成长达标、可重复致命来源及相关回收与动态重放回归，隔离实例已删除。
- `GROWTH-ANCIENT-POLICY` / `16e87117632749b783df9b46909f8581` Passed（26.93 秒）：原有成长牌手动历史、跨回合/Fork、至亮之焰硬上限及禁忌魔典额度合同继续成立，隔离实例已删除。
- `UI-LOCALIZATION` 增加 eng/zhs/zht 药水、成长、遗物子面板的实际构造与“收起”按钮文案合同。0.43.0 源码增加断言后，在英文药水面板构造处按玩家异常栈 Failed（runId `391a52cdd52942d9a45e8b514f9034b9`，`KeyNotFoundException: 收起`）；补齐英文词典后 Passed（runId `333583c32c81407bbcd7b3171e872198`），三种语言的三个子面板均完成检查。两次都使用 120 秒上限、独立无头实例及 `-CleanupInstanceOnExit`，实例已删除。该合同覆盖建窗对象与本地化，不等于可见 Steam 排版验收。
## 0.43.0：路线连续性与操作体验（2026-09-19）

- 两回合原生场景 `TOASTY-QOL-MANUAL-SAME` Passed（runId `c82b3f8125cc4b8998047ccabaf3ca7a`）：第 2 回合烘焙手套手牌页按计划手动删牌，精确续用，新增搜索 0、计划外重算 0。`TOASTY-QOL-MANUAL-DIFFERENT` Passed（runId `f42cc57ca55e4c8e8a91a64a42f951a8`）：选另一张牌严格失配并重新计算。固定夹具位于 `coverage/unattended/toasty-qol-*.json`，可用 `pwsh -NoProfile -File tools/run-qol-contracts.ps1 -Case manual-same`（或 `manual-different`）重跑。最初误将生成场景输出路径用作输入的启动失败不计入上述通过结果，隔离实例已清理。
- `TOASTY-QOL-FROZEN-SAME` Passed（runId `4726fb1c0b1444c58e15a912f604be06`）：第 2 回合原生手牌页冻结后，玩家按计划手动选牌，精确续用且无新搜索。`TOASTY-QOL-FROZEN-DIFFERENT` Passed（runId `9a9e04b913f64057806f8e849255d223`）：异选后旧路线仅供参考，直接请求执行也不搜索、不执行；手动重新计算解除冻结并从真实状态得到可执行路线。
- `TOASTY-QOL-AUTO-OFF-FULLAUTO` Passed（runId `f19b059e55d5488e9971fa20b3e5a042`）：第 2 回合原生选牌页关闭自动计算后明确开启全自动，计划选择后无搜索续用并开始出牌，计划外重算 0；全自动保持开、自动计算保持关。上述五场均为隔离无头实例，退出后实例删除；未测试可见 Steam。
- 上一版 `UI-COMPACT-QOL` 曾 Passed（runId `44dd21643d604c6a95c1a0527aa816fe`），但把战损拆成累计受伤、回血和净变化后，界面实际过于冗长。恢复旧摘要前先增加“无回血时仍紧凑”的断言；旧实现按预期失败（runId `6bd1cb09ba2d4785a4c869656de0da17`）。上一轮修正后 `UI-COMPACT-QOL` Passed（runId `a9fafeccff0e45c4956d21c61b93f6ee`），但后来玩家可见实机显示首回合“路线回血 14”，续用到下一回合显示“路线回血 9”；旧实现把实际回血从后续预测中删去、并从“已扣／预计扣”扣除。新增“回血不改累计受伤”断言后原实现按预期 Failed（runId `537783e8716a43f784fa2d8257af71c1`），隔离实例已删除。修正后 `UI-COMPACT-QOL` Passed（runId `ebd827a759f84a0fab2d74aeef662b0c`）：核对本场已受伤加未来逐回合受伤、本场已恢复加未来逐回合恢复；精确续用的纯显示合同中，已发生的 5 HP 回血与余下 9 HP 合计仍是 14，累计扣血不因单纯回血跳变；折叠及无回血时的标签规则也通过。此合同没有真实打完两回合再生药战斗，可见会话新画面仍待玩家验收。模拟不支持 NoGC 的入口仍核对未调用区域启动且未取得/恢复延迟模式所有权；该模拟不等于 Android/iOS 真机验证。`UI-PRIORITY-FEEDBACK` Passed（runId `508238b1cbd74c21bbb41f7459118d52`）：展开和小窗“采用／执行／冻结”各自的可见与禁用状态合同通过。
- `UI-LOCALIZATION` 修正后 Passed（runId `af8f282e828c4bc486105ff617975849`）：中英简繁 426 项目录占位符与原有路线标签一致；两项隔离无头实例都已删除。可见界面的遮挡和点击体验未验收。
- 花园幽灵鳗问题定位：本机 CombatSolver 独立日志 `combat-4b7cdbd07e434de0b9f448456daca5be.jsonl` 中首次 `projected_battle_hp_lost=12`，随后第 2–5 回合 `SEARCH_REUSED validation=exact_state_text`，逐回合受伤 7/0/2/0/3；第 3 回合先 `TURN_SETUP_RESULT_PREVIEW` 后 `SEARCH_REUSED`，旧选牌预览源已受伤 0 + 后续 5 与实况 7 + 后续 5 不同。`UI-COMPACT-QOL` 补充当前选牌预览需保留已观察 7 HP 的断言后，旧代码按预期 Failed（runId `d9ccbebf72b343479b6b62cc7ff8448e`，实例已删除）。修正后 `UI-COMPACT-QOL` Passed（runId `0f41076362304db9886cccbc0ab36432`），`TOASTY-QOL-MANUAL-SAME` 的真实第 2 回合原生选牌与精确续用 Passed（runId `28d2a9acedfb48a6ac4387c6745d80da`），其中定向注入“已受伤 7、已回血 2”的预览快照断言通过，新增搜索 0；均用 `-CleanupInstanceOnExit` 删除隔离实例。改动只涉及 UI 主线程快照，原日志不含界面文字或遗物逐效果结算；花园幽灵鳗可见会话新画面仍待玩家验收。
- 单步执行后原生选牌页按钮回归：本机日志 `combat-5fe601b09c3d4aaab663622187640132.jsonl` 中第 3 回合 `TURN_SETUP_RESULT_PREVIEW` 先于 `NATIVE_CHOICE_VISIBLE` / `TURN_SETUP_PLAN_READY driving=false`，期间没有 `UI_ACTION action=deploy`。新增固定 `coverage/unattended/toasty-qol-single-step-execute.json`，命令 `pwsh -NoProfile -File tools/run-qol-contracts.ps1 -Case single-step-execute`：等待选牌表面准备完毕后，旧版按钮仍禁用，Failed（runId `d69e63aedbde424191ccc2c3d74102f9`）；在准备完成时刷新控件后，触发按钮本身，原生选牌先完成且下一回合开始部署，精确续用、搜索次数不增加、计划外重算 0、全自动保持关闭，最终 Passed（runId `0ae649cacf8d4dc6876f19113764db9e`），实例已删除。首次红灯 `8a42cd9b0bdd47ab961f3040f5a9725c` 发生于页面可见而计划尚未准备好的更早时点，不计为最终失败基线；未追加零受伤场景以外的整场质量结论。可见 Steam 点击与该花园幽灵鳗原场景未复跑。
- 实时路线的回合开始选牌：`UI-LOCALIZATION` 扩展合同用候选根遗物选牌、下一回合能力选牌、再下一回合遗物选牌核对中英简繁显示；同时核对前沿预览和候选切换后移除旧选择。修复前 Failed（runId `1e1930dbf02b4625b2e50d8e59390ada`，英文的能力/遗物选择均为空），修复后 Passed（runId `4410cb175cef4f569f0efec3b561b4d0`），隔离实例已删除。该合成投影合同不等于可见实机中整场搜索帧时序验收。
- 路线有效性定时刷新及小窗独立禁用状态接线后，`UI-PRIORITY-FEEDBACK` 再次 Passed（runId `508238b1cbd74c21bbb41f7459118d52`）；`TOASTY-QOL-FROZEN-DIFFERENT` 再次 Passed（runId `b1a30ec68a76481ab16058b2fec312d2`），过期路线没有出牌或计划外搜索，手动重算仍恢复可执行路线。
- 本机目前没有连接的 Android 设备，也没有本次玩家异常栈；手机端实机未验证。可见界面的实际遮挡、长译文排版与点击体验留作人工验收。未验证的场景不计为通过。

## 0.42.0：发布构建

- 合入 #109、#111、#112、#113、#114 与 #115 后，相关语义、界面、搜索和内存定向验证见下方各节；版本提升只改 manifest、项目版本与发布文案，不重复行为场景。已知未验证范围包括默认转置表触顶后的广泛整场质量，以及可见 Steam 会话性能与排版。

## 未发布：PR #114 原始分支与 #115 集成

- 原分支的 `PortfolioSelectorChecks` 12 组 410 断言、`BeamOrderingKeyChecks` 8 组 549747 断言、`BeamWidthPortfolioChecks` 93 项，以及 No-GC 和内存截断、转置表上下限的原始对照均属 PR #114 基线证据，见各研究报告；不能当作现行主线组合已通过。当前整合后的验证和未通过项在本节续记。
- 默认值核对：转置支配表合计上限 1,000,000 为唯一新增的默认搜索决策；无进展截断=0、基线组合成员=true、状态键盐/牌堆顺序商/转置消融=0；无有效环境模型时学习型门控不启用。低于上限的原分支对照不能证明触顶后的路线质量，完整关闭剪枝的消融也不是质量代价上界。
- 本次原分支并入现行 main 后，Windows 结构门禁 `REFACTOR_BOUNDARIES_OK search_files=204`；主 DLL Release 编译 0 警告，完整工程只因本机缺 .NET Framework 4.8 引用程序集停在 MemoryCleaner；离线宿主 Release 0 警告/错误。`GcPolicyChecks` 基础 26 项和 `recovery` 9 项、`PortfolioSelectorChecks` 12 组 410 断言、`BeamOrderingKeyChecks` 8 组 549747 断言、`BeamWidthPortfolioChecks` 93 项通过。
- 当前合并组合的固定故障机器人/FUZZY_WURM 根（Custom、beam 30、nodes 2000、DOP1、Coordinator+组合）对已合入 #115 的同根产物比较 72 字段 `IDENTICAL`，包含路线和续用文本；默认 100 万条对同一代码的无限制版也是 72 字段 `IDENTICAL`、展开 236、转移 832。测试上限设为 50 时两表恰好 34+16 条、`transpositionLimitBypasses=957`；这一个根的 72 个决策字段仍相同，不代表其他根的触顶质量。原 #114 新宿主不能直接加载旧 #115 DLL（实验策略接口不同），跨版本对照沿用此前 #115 宿主产物，不把失败的直接加载记为通过。
- 内存截断受控样本（铁甲战士/FUZZY_WURM、1 GB No-GC、600 MiB 活压力、阈值 1）得到 `MemoryNoProgress`、展开 1、可执行的防御牌 + EndTurn 两步路线、组合成员 `MemoryTruncated`；这是注入压力，不代表真实长搜的质量。DOP2 组合测量完整结束，实际最大并发 2，`phasePerformance` 写入宿主结果；首次整合时该字段为空，已修复并复测。尚无默认 100 万条触顶后的广泛整场质量对照，也未跑可见 Steam/正常会话性能。
