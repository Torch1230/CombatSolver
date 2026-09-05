## 汇总

- 有战损对照记录：460 条
- 有玩家备注：129 条
- 无玩家备注：331 条
- 减损低于 5 HP、暂不修改：148 条
- 进入修复队列：312 条

排序规则：先有玩家备注，再无玩家备注；每组按原始减损降序。减损低于 `5 HP` 的记录单独列出。

## 一、有玩家备注，进入修复队列

| # | 原始减损 | 原始求解器 → 玩家 | 优化后相对人工 | 是否更优 | 处理状态 | 遭遇 | 日志 | 汇总包 | 玩家备注 |
|---:|---:|---:|---:|---|---|---|---|---|---|
| 1 | 104 HP | 110 → 6 | +3 | 是 | 整场部署通过：开战还原、VeryHigh、原包四药强制政策，3 HP / 4 药，零非预期重算；Smart 另测预测 8 HP / 3 药 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-153416-774.zip` | `combatsolver-reports-20260905-104714.zip` | 110->6 |
| 2 | 68 HP | 123 → 55 | +5 | 是 | 整场部署通过：开战还原、VeryHigh、原包通关优先及仅稳定血清强制政策，50 HP / 1 药，零非预期重算；减战损优先另测仅死亡路线，评分试验退化已撤回 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-152708-532.zip` | `combatsolver-reports-20260905-104714.zip` | 沙小第知道他自己打得过120血带药战士吗？ |
| 3 | 58 HP | 61 → 3 | 待验证 | 待验证 | 未追平，待回访：开战与首次抽牌对账通过；VeryHigh、减战损优先、原包禁药政策预测从 22 HP 降至 20 HP，但仍未追平人工 3 HP，未做整场部署验收；包 3 本轮快速迭代停止，试验留存待后续有新证据时再回访 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260902-133704-483.zip` | `combatsolver-reports-20260905-104714.zip` | 打实验体不开壁垒吃重击的来 |
| 4 | 52 HP | 52 → 0 | 未验收 | 未验收 | 按用户要求跳过；旧 T4 续局 High/Force 2 HP、VeryHigh/Force 0 HP，整场未验收 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-151202-078.zip` | `combatsolver-reports-20260905-104714.zip` | 手操从死亡到0战损 |
| 5 | 49 HP | 73 → 24 | 待验证 | 待验证 | 证据不足，暂缓：原包 `settings.json` 缺少 `potionDirectives`，开战预检明确阻塞；未伪造药水指令或整场结果 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260902-013258-533.zip` | `combatsolver-reports-20260905-104714.zip` | 更好的世界线 |
| 6 | 45 HP | 45 → 0 | 待验证 | 待验证 | 证据不足，暂缓：`current` 缺少 `replay-state`；仅有 `recent/000-combat_start`，且会话标记为 `manual_plus_solver`，旧续局结果不作为整场验收 | MECHA_KNIGHT_ELITE | `CombatSolver-MECHA_KNIGHT_ELITE-20260902-131216-716.zip` | `combatsolver-reports-20260905-104714.zip` | 战损45变0 |
| 7 | 45 HP | 50 → 5 | 待验证 | 待验证 | 开战预检通过；VeryHigh、MinimizeHpLoss、Smart 从 `000-combat_start` 得到完整胜利预测 23 HP（117.11 秒，runId `2b5b7786e5814054ac3f121bb98d3725`），仍高于人工 5 HP；未做整场部署验收，保留为策略缺口 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-173813-639.zip` | `combatsolver-reports-20260905-104652.zip` | 不尊重灵动？ |
| 8 | 44 HP | 64 → 20 | 不可比较 | 待验证 | 基线未完战，暂跳过：20 HP 时敌剩 420 HP；当前手操后 High 完整胜利战损 52 HP | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-053711-747.zip` | `combatsolver-reports-20260905-104549.zip` | 无法参考寄托里的世界线 |
| 9 | 44 HP | 74 → 30 | 待验证 | 待验证 | 证据不足，暂缓：原包 `settings.json` 缺少 `potionDirectives`，开战预检明确阻塞；人工 30 含此前 21 HP，未伪造药水指令或整场结果 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260902-113237-991.zip` | `combatsolver-reports-20260905-104714.zip` | 似乎不太会用疯狂之触药水 |
| 10 | 42 HP | 51 → 9 | 待验证 | 待验证 | 证据不足，暂缓：开战 `VeryHigh / Reported` 请求在 120 秒上限内超时（runId `3040e72109584c14bc8ea5b64df2610e`），未得到战损；人工 9 含此前 T1 已发生的 8 HP，不能直接做整场比较 | KNIGHTS_ELITE | `CombatSolver-KNIGHTS_ELITE-20260904-034250-040.zip` | `combatsolver-reports-20260905-104549.zip` | 更好的世界线 |
| 11 | 40 HP | 62 → 22 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260902-012309-024.zip` | `combatsolver-reports-20260905-104714.zip` | 更好世界线 |
| 12 | 35 HP | 51 → 16 | 待验证 | 待验证 | 待分诊 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260904-231535-471.zip` | `combatsolver-reports-20260905-104412.zip` | 提示打不过boss，手动打了三个回合后计算就能打过了 |
| 13 | 33 HP | 51 → 18 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260902-131206-788.zip` | `combatsolver-reports-20260905-104714.zip` | 上来给我算死了，操作一下中间重算好几次，然后只用掉18 |
| 14 | 33 HP | 61 → 28 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260902-134814-807.zip` | `combatsolver-reports-20260905-104714.zip` | 不会打女王 |
| 15 | 32 HP | 47 → 15 | 待验证 | 待验证 | 待分诊 | SOUL_NEXUS_ELITE | `CombatSolver-SOUL_NEXUS_ELITE-20260902-091440-844.zip` | `combatsolver-reports-20260905-104714.zip` | 不会开能力药 |
| 16 | 31 HP | 49 → 18 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-131532-464.zip` | `combatsolver-reports-20260905-104538.zip` | 更好地世界线 |
| 17 | 31 HP | 75 → 44 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260903-103232-259.zip` | `combatsolver-reports-20260905-104714.zip` | 贪两点血不打御血术导致更大战损 |
| 18 | 30 HP | 38 → 8 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-174830-615.zip` | `combatsolver-reports-20260905-104652.zip` | 又是不尊重灵动 |
| 19 | 25 HP | 44 → 19 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-111908-114.zip` | `combatsolver-reports-20260905-104714.zip` | 最后个bo's's不会打 |
| 20 | 25 HP | 46 → 21 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260904-170254-913.zip` | `combatsolver-reports-20260905-104511.zip` | 壁垒肚皮战士打不明白实验体 |
| 21 | 25 HP | 28 → 3 | 待验证 | 待验证 | 待分诊 | MECHA_KNIGHT_ELITE | `CombatSolver-MECHA_KNIGHT_ELITE-20260903-200801-987.zip` | `combatsolver-reports-20260905-104621.zip` | 神秘ai不打神话被入烂，我神之一手打出神话拯救自己于水火 |
| 22 | 24 HP | 24 → 0 | 待验证 | 待验证 | 待分诊 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260904-191223-178.zip` | `combatsolver-reports-20260905-104454.zip` | 最好的世界线 |
| 23 | 23 HP | 36 → 13 | 待验证 | 待验证 | 待分诊 | EXOSKELETONS_NORMAL | `CombatSolver-EXOSKELETONS_NORMAL-20260903-012013-664.zip` | `combatsolver-reports-20260905-104714.zip` | 更好的世界线 |
| 24 | 23 HP | 69 → 46 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260903-061712-200.zip` | `combatsolver-reports-20260905-104714.zip` | 更好的世界线 |
| 25 | 23 HP | 26 → 3 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-224033-312.zip` | `combatsolver-reports-20260905-104606.zip` | 有坚定不开说是 |
| 26 | 22 HP | 27 → 5 | 待验证 | 待验证 | 待分诊 | WATERFALL_GIANT_BOSS | `CombatSolver-WATERFALL_GIANT_BOSS-20260904-180202-212.zip` | `combatsolver-reports-20260905-104454.zip` | 无药过关掉5 |
| 27 | 22 HP | 53 → 31 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260902-001428-060.zip` | `combatsolver-reports-20260905-104714.zip` | 打出了更好的世界线 |
| 28 | 21 HP | 35 → 14 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260904-130858-026.zip` | `combatsolver-reports-20260905-104538.zip` | 有时候卖血开能力启动更好 |
| 29 | 21 HP | 34 → 13 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260902-144557-425.zip` | `combatsolver-reports-20260905-104714.zip` | 依旧不会蹭武装 |
| 30 | 18 HP | 36 → 18 | 待验证 | 待验证 | 待分诊 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260903-074314-472.zip` | `combatsolver-reports-20260905-104714.zip` | 打翻车鱼能力药开无惧不早开，第三回合才开，多吃20战损 |
| 31 | 17 HP | 22 → 5 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260905-013436-898.zip` | `combatsolver-reports-20260905-104355.zip` | 更好世界线 |
| 32 | 16 HP | 29 → 13 | 待验证 | 待验证 | 待分诊 | MECHA_KNIGHT_ELITE | `CombatSolver-MECHA_KNIGHT_ELITE-20260904-130436-004.zip` | `combatsolver-reports-20260905-104538.zip` | 新算法 |
| 33 | 16 HP | 42 → 26 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260903-191405-779.zip` | `combatsolver-reports-20260905-104632.zip` | 不尊重虚弱 |
| 34 | 15 HP | 39 → 24 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-192157-595.zip` | `combatsolver-reports-20260905-104632.zip` | 更优时间线 |
| 35 | 14 HP | 14 → 0 | 待验证 | 待验证 | 待分诊 | THIEVING_HOPPER_WEAK | `CombatSolver-THIEVING_HOPPER_WEAK-20260902-221156-638.zip` | `combatsolver-reports-20260905-104714.zip` | 开局打两防就能防19血，手动打了战损就清零了 |
| 36 | 14 HP | 52 → 38 | 待验证 | 待验证 | 待分诊 | VANTOM_BOSS | `CombatSolver-VANTOM_BOSS-20260903-124045-501.zip` | `combatsolver-reports-20260905-104714.zip` | 更优解 |
| 37 | 14 HP | 18 → 4 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260904-075135-914.zip` | `combatsolver-reports-20260905-104549.zip` | 优化了一下，省了一瓶药 |
| 38 | 13 HP | 19 → 6 | 待验证 | 待验证 | 待分诊 | THE_OBSCURA_NORMAL | `CombatSolver-THE_OBSCURA_NORMAL-20260902-130441-222.zip` | `combatsolver-reports-20260905-104714.zip` | 放血加费不舍得出 |
| 39 | 12 HP | 12 → 0 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-075434-482.zip` | `combatsolver-reports-20260905-104549.zip` | 依旧先起放后上灵动然后吃战损 |
| 40 | 12 HP | 13 → 1 | 待验证 | 待验证 | 待分诊 | AXEBOTS_NORMAL | `CombatSolver-AXEBOTS_NORMAL-20260903-224649-286.zip` | `combatsolver-reports-20260905-104606.zip` | 更优路线 |
| 41 | 12 HP | 14 → 2 | 待验证 | 待验证 | 待分诊 | INFESTED_PRISMS_ELITE | `CombatSolver-INFESTED_PRISMS_ELITE-20260904-023516-038.zip` | `combatsolver-reports-20260905-104549.zip` | 无药2回合击杀，强制使用药水4回合击杀，无药第一回合-14，按理使用格挡药可以变成-2 |
| 42 | 12 HP | 42 → 30 | 待验证 | 待验证 | 待分诊 | MECHA_KNIGHT_ELITE | `CombatSolver-MECHA_KNIGHT_ELITE-20260904-145500-799.zip` | `combatsolver-reports-20260905-104522.zip` | 先防御再开坚定 |
| 43 | 12 HP | 31 → 19 | 待验证 | 待验证 | 待分诊 | THE_OBSCURA_NORMAL | `CombatSolver-THE_OBSCURA_NORMAL-20260903-180719-973.zip` | `combatsolver-reports-20260905-104652.zip` | 喜欢乱留小刀 |
| 44 | 11 HP | 17 → 6 | 待验证 | 待验证 | 待分诊 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260903-163652-769.zip` | `combatsolver-reports-20260905-104705.zip` | 吃个技能药减10战损 |
| 45 | 10 HP | 10 → 0 | 待验证 | 待验证 | 待分诊 | ENTOMANCER_ELITE | `CombatSolver-ENTOMANCER_ELITE-20260905-103511-987.zip` | `combatsolver-reports-20260905-104355.zip` | 更好的世界线 |
| 46 | 10 HP | 34 → 24 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-004149-767.zip` | `combatsolver-reports-20260905-104606.zip` | 能力药里的群蛇没算进去 |
| 47 | 10 HP | 26 → 16 | 待验证 | 待验证 | 待分诊 | LAGAVULIN_MATRIARCH_BOSS | `CombatSolver-LAGAVULIN_MATRIARCH_BOSS-20260904-211517-176.zip` | `combatsolver-reports-20260905-104427.zip` | 更好世界线 |
| 48 | 10 HP | 34 → 24 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260904-145317-179.zip` | `combatsolver-reports-20260905-104522.zip` | ai不敢用内核加速配合大奖进行输出，导致并非最优解。 |
| 49 | 10 HP | 20 → 10 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260903-212109-022.zip` | `combatsolver-reports-20260905-104621.zip` | 更好的世界线 |
| 50 | 9 HP | 27 → 18 | 待验证 | 待验证 | 待分诊 | TERROR_EEL_ELITE | `CombatSolver-TERROR_EEL_ELITE-20260902-211725-141.zip` | `combatsolver-reports-20260905-104714.zip` | AI似乎不会用预判 |
| 51 | 9 HP | 9 → 0 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-234033-528.zip` | `combatsolver-reports-20260905-104412.zip` | 不开谋划专家 开了 0战损 |
| 52 | 9 HP | 17 → 8 | 待验证 | 待验证 | 待分诊 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260903-001308-143.zip` | `combatsolver-reports-20260905-104714.zip` | 你打出了比求解器更好的路线，请在设置中点击“上传问题包”提交日志。这可以更好地推动算法进步！ |
| 53 | 8 HP | 38 → 30 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260903-103545-698.zip` | `combatsolver-reports-20260905-104714.zip` | 有子程序的情况下不开等于白给的燃烧导致战损增加 |
| 54 | 8 HP | 46 → 38 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260903-103434-785.zip` | `combatsolver-reports-20260905-104714.zip` | 烧进阶之灾不烧打击 |
| 55 | 8 HP | 28 → 20 | 待验证 | 待验证 | 待分诊 | WATERFALL_GIANT_BOSS | `CombatSolver-WATERFALL_GIANT_BOSS-20260904-163612-860.zip` | `combatsolver-reports-20260905-104511.zip` | 不先打爆发 |
| 56 | 8 HP | 16 → 8 | 待验证 | 待验证 | 待分诊 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260902-002251-180.zip` | `combatsolver-reports-20260905-104714.zip` | mod不不懂先铸剑，再开武装 |
| 57 | 8 HP | 49 → 41 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-083658-245.zip` | `combatsolver-reports-20260905-104714.zip` | 更好路线 |
| 58 | 8 HP | 41 → 33 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260904-191349-474.zip` | `combatsolver-reports-20260905-104454.zip` | ai不喜欢打能力啊 |
| 59 | 7 HP | 18 → 11 | 待验证 | 待验证 | 待分诊 | TUNNELER_WEAK | `CombatSolver-TUNNELER_WEAK-20260904-180953-098.zip` | `combatsolver-reports-20260905-104454.zip` | 更好的世界线 |
| 60 | 7 HP | 15 → 8 | 待验证 | 待验证 | 待分诊 | ENTOMANCER_ELITE | `CombatSolver-ENTOMANCER_ELITE-20260902-045026-001.zip` | `combatsolver-reports-20260905-104714.zip` | 更好 |
| 61 | 7 HP | 34 → 27 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260904-150843-493.zip` | `combatsolver-reports-20260905-104522.zip` | 有尾巴直接送死 手打保护尾巴了 |
| 62 | 7 HP | 23 → 16 | 待验证 | 待验证 | 待分诊 | SEAPUNK_WEAK | `CombatSolver-SEAPUNK_WEAK-20260904-134928-828.zip` | `combatsolver-reports-20260905-104538.zip` | 更好的世界线 |
| 63 | 7 HP | 14 → 7 | 待验证 | 待验证 | 待分诊 | BOWLBUGS_WEAK | `CombatSolver-BOWLBUGS_WEAK-20260902-073925-101.zip` | `combatsolver-reports-20260905-104714.zip` | mod不会卡盛碗虫的防御线 |
| 64 | 7 HP | 17 → 10 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260903-214907-324.zip` | `combatsolver-reports-20260905-104621.zip` | 更好的世界线 |
| 65 | 7 HP | 25 → 18 | 待验证 | 待验证 | 待分诊 | TUNNELER_WEAK | `CombatSolver-TUNNELER_WEAK-20260903-184813-007.zip` | `combatsolver-reports-20260905-104632.zip` | 更好的世界线 |
| 66 | 7 HP | 17 → 10 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-152111-422.zip` | `combatsolver-reports-20260905-104714.zip` | 烧延申妙计导致防不住实验体三阶段 |
| 67 | 7 HP | 14 → 7 | 待验证 | 待验证 | 待分诊 | HUNTER_KILLER_NORMAL | `CombatSolver-HUNTER_KILLER_NORMAL-20260902-110839-339.zip` | `combatsolver-reports-20260905-104714.zip` | 明明筑墙+虚弱防得差不多了，非要打应急按钮 |
| 68 | 7 HP | 18 → 11 | 待验证 | 待验证 | 待分诊 | SLUMBERING_BEETLE_NORMAL | `CombatSolver-SLUMBERING_BEETLE_NORMAL-20260904-195059-524.zip` | `combatsolver-reports-20260905-104427.zip` | 神之一手先开侧步减低战损 |
| 69 | 7 HP | 10 → 3 | 待验证 | 待验证 | 待分诊 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260904-160716-850.zip` | `combatsolver-reports-20260905-104511.zip` | 不开添柴 |
| 70 | 6 HP | 26 → 20 | 待验证 | 待验证 | 待分诊 | BYRDONIS_ELITE | `CombatSolver-BYRDONIS_ELITE-20260902-111257-582.zip` | `combatsolver-reports-20260905-104714.zip` | 更好的世界线 |
| 71 | 6 HP | 14 → 8 | 待验证 | 待验证 | 待分诊 | MECHA_KNIGHT_ELITE | `CombatSolver-MECHA_KNIGHT_ELITE-20260904-114052-141.zip` | `combatsolver-reports-20260905-104538.zip` | 鱼香后打糖丸了 |
| 72 | 6 HP | 61 → 55 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-172124-980.zip` | `combatsolver-reports-20260905-104705.zip` | 不会先融入暗影再起防 |
| 73 | 6 HP | 12 → 6 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260904-001250-951.zip` | `combatsolver-reports-20260905-104606.zip` | 有攻击牌优先打而不是先抽牌让key牌上手 |
| 74 | 6 HP | 20 → 14 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260903-133943-480.zip` | `combatsolver-reports-20260905-104714.zip` | 第一回合有费用不输出导致并非最优解 |
| 75 | 6 HP | 7 → 1 | 待验证 | 待验证 | 待分诊 | ENTOMANCER_ELITE | `CombatSolver-ENTOMANCER_ELITE-20260902-004749-094.zip` | `combatsolver-reports-20260905-104714.zip` | mod不会先开环轨 |
| 76 | 6 HP | 32 → 26 | 待验证 | 待验证 | 待分诊 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260902-132043-022.zip` | `combatsolver-reports-20260905-104714.zip` | 依旧小循环 |
| 77 | 5 HP | 17 → 12 | 待验证 | 待验证 | 待分诊 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260902-071630-276.zip` | `combatsolver-reports-20260905-104714.zip` | mod好像不会优先给1点多次攻击上虚弱 |
| 78 | 5 HP | 34 → 29 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-165245-576.zip` | `combatsolver-reports-20260905-104705.zip` | 不理解有余像不叠，起三点防御是什么意思 |
| 79 | 5 HP | 10 → 5 | 待验证 | 待验证 | 待分诊 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260904-131053-040.zip` | `combatsolver-reports-20260905-104538.zip` | 找到了更好的世界线 |
| 80 | 5 HP | 45 → 40 | 待验证 | 待验证 | 待分诊 | CONSTRUCT_MENAGERIE_NORMAL | `CombatSolver-CONSTRUCT_MENAGERIE_NORMAL-20260902-235218-331.zip` | `combatsolver-reports-20260905-104714.zip` | 比推荐更加 |
| 81 | 5 HP | 27 → 22 | 待验证 | 待验证 | 待分诊 | SKULKING_COLONY_ELITE | `CombatSolver-SKULKING_COLONY_ELITE-20260903-210131-186.zip` | `combatsolver-reports-20260905-104621.zip` | 我打出了更优解 |
| 82 | 5 HP | 37 → 32 | 待验证 | 待验证 | 待分诊 | KNIGHTS_ELITE | `CombatSolver-KNIGHTS_ELITE-20260903-123001-578.zip` | `combatsolver-reports-20260905-104714.zip` | 有坚定的情况下先巨像后防御导致少防 |
| 83 | 5 HP | 21 → 16 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260903-175643-099.zip` | `combatsolver-reports-20260905-104652.zip` | 不会留暴露 |
| 84 | 5 HP | 9 → 4 | 待验证 | 待验证 | 待分诊 | WATERFALL_GIANT_BOSS | `CombatSolver-WATERFALL_GIANT_BOSS-20260903-180516-218.zip` | `combatsolver-reports-20260905-104652.zip` | ai隔着逗我呢 |
| 85 | 5 HP | 37 → 32 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260902-101659-107.zip` | `combatsolver-reports-20260905-104714.zip` | AI不怎么尊重被宝石面具的0费涂毒啊，梦魇不复制0费涂毒 |
| 86 | 5 HP | 39 → 34 | 待验证 | 待验证 | 待分诊 | WATERFALL_GIANT_BOSS | `CombatSolver-WATERFALL_GIANT_BOSS-20260901-233737-505.zip` | `combatsolver-reports-20260905-104714.zip` | 先出打击后上易伤，药水壁垒不会早开攒防御 |

## 二、无玩家备注，进入修复队列

| # | 原始减损 | 原始求解器 → 玩家 | 优化后相对人工 | 是否更优 | 处理状态 | 遭遇 | 日志 | 汇总包 | 玩家备注 |
|---:|---:|---:|---:|---|---|---|---|---|---|
| 1 | 88 HP | 117 → 29 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-150530-326.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 2 | 76 HP | 76 → 0 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260905-031716-858.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 3 | 69 HP | 71 → 2 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-171304-305.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 4 | 69 HP | 69 → 0 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260904-204129-023.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 5 | 65 HP | 76 → 11 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260904-141404-295.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 6 | 55 HP | 75 → 20 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260905-101704-251.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 7 | 52 HP | 52 → 0 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-215443-162.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 8 | 50 HP | 57 → 7 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260904-215125-616.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 9 | 49 HP | 58 → 9 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260903-225935-540.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 10 | 48 HP | 54 → 6 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-163537-881.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 11 | 44 HP | 66 → 22 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260903-213404-610.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 12 | 44 HP | 52 → 8 | 待验证 | 待验证 | 待分诊 | KNIGHTS_ELITE | `CombatSolver-KNIGHTS_ELITE-20260903-171238-041.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 13 | 43 HP | 56 → 13 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260903-124452-648.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 14 | 43 HP | 45 → 2 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260903-235354-297.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 15 | 42 HP | 150 → 108 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-124010-856.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 16 | 40 HP | 50 → 10 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-214817-494.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 17 | 39 HP | 68 → 29 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-175324-023.zip` | `combatsolver-reports-20260905-104652.zip` | 【CombatSolver 自动分类】 |
| 18 | 38 HP | 41 → 3 | 待验证 | 待验证 | 待分诊 | MECHA_KNIGHT_ELITE | `CombatSolver-MECHA_KNIGHT_ELITE-20260903-124910-481.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 19 | 38 HP | 66 → 28 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260902-001925-408.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 20 | 37 HP | 88 → 51 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-125630-680.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 21 | 35 HP | 36 → 1 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-184453-569.zip` | `combatsolver-reports-20260905-104652.zip` | 【CombatSolver 自动分类】 |
| 22 | 35 HP | 66 → 31 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-103344-385.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 23 | 32 HP | 67 → 35 | 待验证 | 待验证 | 待分诊 | LAGAVULIN_MATRIARCH_BOSS | `CombatSolver-LAGAVULIN_MATRIARCH_BOSS-20260904-164743-701.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 24 | 32 HP | 40 → 8 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260904-150000-124.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 25 | 31 HP | 66 → 35 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-192846-549.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 26 | 31 HP | 35 → 4 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260902-012415-646.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 27 | 29 HP | 29 → 0 | 待验证 | 待验证 | 待分诊 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260904-193850-893.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 28 | 29 HP | 30 → 1 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260904-151730-975.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 29 | 29 HP | 29 → 0 | 待验证 | 待验证 | 待分诊 | SOUL_NEXUS_ELITE | `CombatSolver-SOUL_NEXUS_ELITE-20260904-112340-339.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 30 | 28 HP | 72 → 44 | 待验证 | 待验证 | 待分诊 | KNIGHTS_ELITE | `CombatSolver-KNIGHTS_ELITE-20260903-111107-971.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 31 | 28 HP | 35 → 7 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-161343-196.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 32 | 26 HP | 64 → 38 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260904-001513-382.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 33 | 26 HP | 34 → 8 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260903-212145-206.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 34 | 26 HP | 50 → 24 | 待验证 | 待验证 | 待分诊 | INFESTED_PRISMS_ELITE | `CombatSolver-INFESTED_PRISMS_ELITE-20260902-094502-760.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 35 | 26 HP | 30 → 4 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260904-074722-364.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 36 | 25 HP | 45 → 20 | 待验证 | 待验证 | 待分诊 | SOUL_NEXUS_ELITE | `CombatSolver-SOUL_NEXUS_ELITE-20260903-190357-282.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 37 | 25 HP | 42 → 17 | 待验证 | 待验证 | 待分诊 | SLUMBERING_BEETLE_NORMAL | `CombatSolver-SLUMBERING_BEETLE_NORMAL-20260902-013502-659.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 38 | 25 HP | 61 → 36 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260902-095311-968.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 39 | 25 HP | 31 → 6 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260902-102209-151.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 40 | 24 HP | 26 → 2 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260904-184915-677.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 41 | 24 HP | 44 → 20 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-033156-918.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 42 | 24 HP | 32 → 8 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260903-193322-892.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 43 | 24 HP | 49 → 25 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260905-003042-179.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 44 | 24 HP | 49 → 25 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-165926-602.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 45 | 23 HP | 43 → 20 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260904-231359-598.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 46 | 22 HP | 35 → 13 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260902-012515-528.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 47 | 22 HP | 32 → 10 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260903-005553-575.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 48 | 22 HP | 43 → 21 | 待验证 | 待验证 | 待分诊 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260903-124712-021.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 49 | 21 HP | 23 → 2 | 待验证 | 待验证 | 待分诊 | CHOMPERS_NORMAL | `CombatSolver-CHOMPERS_NORMAL-20260904-223541-290.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 50 | 21 HP | 32 → 11 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-210543-180.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 51 | 21 HP | 38 → 17 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-222917-800.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 52 | 21 HP | 21 → 0 | 待验证 | 待验证 | 待分诊 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260903-010551-615.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 53 | 20 HP | 25 → 5 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260904-195832-045.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 54 | 20 HP | 20 → 0 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260903-042834-027.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 55 | 20 HP | 49 → 29 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260902-025937-979.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 56 | 20 HP | 49 → 29 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260903-102954-097.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 57 | 20 HP | 56 → 36 | 待验证 | 待验证 | 待分诊 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260904-125451-501.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 58 | 20 HP | 31 → 11 | 待验证 | 待验证 | 待分诊 | INFESTED_PRISMS_ELITE | `CombatSolver-INFESTED_PRISMS_ELITE-20260903-003111-881.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 59 | 19 HP | 25 → 6 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-051708-155.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 60 | 19 HP | 45 → 26 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-195036-510.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 61 | 19 HP | 33 → 14 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260903-095908-598.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 62 | 18 HP | 19 → 1 | 待验证 | 待验证 | 待分诊 | WATERFALL_GIANT_BOSS | `CombatSolver-WATERFALL_GIANT_BOSS-20260902-143005-414.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 63 | 18 HP | 28 → 10 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260903-180854-498.zip` | `combatsolver-reports-20260905-104652.zip` | 【CombatSolver 自动分类】 |
| 64 | 18 HP | 37 → 19 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260904-161243-231.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 65 | 17 HP | 51 → 34 | 待验证 | 待验证 | 待分诊 | INFESTED_PRISMS_ELITE | `CombatSolver-INFESTED_PRISMS_ELITE-20260904-113227-377.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 66 | 17 HP | 23 → 6 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260904-124726-971.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 67 | 17 HP | 49 → 32 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260904-063710-438.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 68 | 17 HP | 56 → 39 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-181817-798.zip` | `combatsolver-reports-20260905-104652.zip` | 【CombatSolver 自动分类】 |
| 69 | 17 HP | 30 → 13 | 待验证 | 待验证 | 待分诊 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260903-081345-783.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 70 | 17 HP | 23 → 6 | 待验证 | 待验证 | 待分诊 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260902-135743-874.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 71 | 16 HP | 27 → 11 | 待验证 | 待验证 | 待分诊 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260903-101302-482.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 72 | 16 HP | 16 → 0 | 待验证 | 待验证 | 待分诊 | INFESTED_PRISMS_ELITE | `CombatSolver-INFESTED_PRISMS_ELITE-20260904-231011-767.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 73 | 16 HP | 64 → 48 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-000318-262.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 74 | 16 HP | 19 → 3 | 待验证 | 待验证 | 待分诊 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260904-112403-447.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 75 | 16 HP | 31 → 15 | 待验证 | 待验证 | 待分诊 | TUNNELER_WEAK | `CombatSolver-TUNNELER_WEAK-20260901-235117-063.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 76 | 15 HP | 26 → 11 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260903-202303-662.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 77 | 15 HP | 44 → 29 | 待验证 | 待验证 | 待分诊 | CEREMONIAL_BEAST_BOSS | `CombatSolver-CEREMONIAL_BEAST_BOSS-20260903-145238-789.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 78 | 15 HP | 42 → 27 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260903-023113-664.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 79 | 15 HP | 26 → 11 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-005821-879.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 80 | 15 HP | 50 → 35 | 待验证 | 待验证 | 待分诊 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260902-235700-925.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 81 | 15 HP | 53 → 38 | 待验证 | 待验证 | 待分诊 | BYRDONIS_ELITE | `CombatSolver-BYRDONIS_ELITE-20260903-190701-290.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 82 | 15 HP | 19 → 4 | 待验证 | 待验证 | 待分诊 | MECHA_KNIGHT_ELITE | `CombatSolver-MECHA_KNIGHT_ELITE-20260904-114328-332.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 83 | 15 HP | 15 → 0 | 待验证 | 待验证 | 待分诊 | SLIMED_BERSERKER_NORMAL | `CombatSolver-SLIMED_BERSERKER_NORMAL-20260905-095245-286.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 84 | 15 HP | 22 → 7 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260904-115225-234.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 85 | 15 HP | 48 → 33 | 待验证 | 待验证 | 待分诊 | SLUMBERING_BEETLE_NORMAL | `CombatSolver-SLUMBERING_BEETLE_NORMAL-20260905-090817-937.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 86 | 15 HP | 21 → 6 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260905-102510-872.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 87 | 15 HP | 29 → 14 | 待验证 | 待验证 | 待分诊 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260905-071209-092.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 88 | 14 HP | 28 → 14 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260902-231100-494.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 89 | 14 HP | 18 → 4 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-160511-343.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 90 | 14 HP | 30 → 16 | 待验证 | 待验证 | 待分诊 | SKULKING_COLONY_ELITE | `CombatSolver-SKULKING_COLONY_ELITE-20260904-162828-212.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 91 | 14 HP | 14 → 0 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260902-023040-904.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 92 | 14 HP | 38 → 24 | 待验证 | 待验证 | 待分诊 | CEREMONIAL_BEAST_BOSS | `CombatSolver-CEREMONIAL_BEAST_BOSS-20260903-224707-602.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 93 | 14 HP | 38 → 24 | 待验证 | 待验证 | 待分诊 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260904-160236-505.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 94 | 14 HP | 29 → 15 | 待验证 | 待验证 | 待分诊 | BYRDONIS_ELITE | `CombatSolver-BYRDONIS_ELITE-20260904-000155-525.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 95 | 14 HP | 37 → 23 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260904-195241-718.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 96 | 14 HP | 23 → 9 | 待验证 | 待验证 | 待分诊 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260903-230747-213.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 97 | 14 HP | 82 → 68 | 待验证 | 待验证 | 待分诊 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260903-122234-657.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 98 | 13 HP | 53 → 40 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260904-213058-879.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 99 | 13 HP | 58 → 45 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260902-005249-276.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 100 | 13 HP | 45 → 32 | 待验证 | 待验证 | 待分诊 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260903-185816-315.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 101 | 13 HP | 25 → 12 | 待验证 | 待验证 | 待分诊 | SKULKING_COLONY_ELITE | `CombatSolver-SKULKING_COLONY_ELITE-20260904-152239-209.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 102 | 13 HP | 33 → 20 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260902-235907-075.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 103 | 13 HP | 29 → 16 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-064829-834.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 104 | 12 HP | 16 → 4 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260902-142009-055.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 105 | 12 HP | 12 → 0 | 待验证 | 待验证 | 待分诊 | ENTOMANCER_ELITE | `CombatSolver-ENTOMANCER_ELITE-20260903-172109-257.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 106 | 12 HP | 12 → 0 | 待验证 | 待验证 | 待分诊 | HUNTER_KILLER_NORMAL | `CombatSolver-HUNTER_KILLER_NORMAL-20260904-121916-156.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 107 | 12 HP | 12 → 0 | 待验证 | 待验证 | 待分诊 | MECHA_KNIGHT_ELITE | `CombatSolver-MECHA_KNIGHT_ELITE-20260903-162331-651.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 108 | 12 HP | 34 → 22 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260904-044659-483.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 109 | 12 HP | 25 → 13 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260905-015424-663.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 110 | 12 HP | 20 → 8 | 待验证 | 待验证 | 待分诊 | TURRET_OPERATOR_WEAK | `CombatSolver-TURRET_OPERATOR_WEAK-20260903-081628-145.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 111 | 12 HP | 26 → 14 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-224636-193.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 112 | 12 HP | 17 → 5 | 待验证 | 待验证 | 待分诊 | TERROR_EEL_ELITE | `CombatSolver-TERROR_EEL_ELITE-20260902-025714-148.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 113 | 12 HP | 14 → 2 | 待验证 | 待验证 | 待分诊 | SCROLLS_OF_BITING_WEAK | `CombatSolver-SCROLLS_OF_BITING_WEAK-20260905-084959-608.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 114 | 12 HP | 18 → 6 | 待验证 | 待验证 | 待分诊 | TWO_TAILED_RATS_NORMAL | `CombatSolver-TWO_TAILED_RATS_NORMAL-20260903-001340-861.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 115 | 12 HP | 13 → 1 | 待验证 | 待验证 | 待分诊 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260902-041852-927.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 116 | 12 HP | 12 → 0 | 待验证 | 待验证 | 待分诊 | SCROLLS_OF_BITING_WEAK | `CombatSolver-SCROLLS_OF_BITING_WEAK-20260903-103426-963.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 117 | 12 HP | 15 → 3 | 待验证 | 待验证 | 待分诊 | ENTOMANCER_ELITE | `CombatSolver-ENTOMANCER_ELITE-20260902-222325-722.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 118 | 12 HP | 43 → 31 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260902-215913-895.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 119 | 12 HP | 16 → 4 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-211959-004.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 120 | 12 HP | 46 → 34 | 待验证 | 待验证 | 待分诊 | WATERFALL_GIANT_BOSS | `CombatSolver-WATERFALL_GIANT_BOSS-20260904-123055-152.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 121 | 12 HP | 29 → 17 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260905-003801-526.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 122 | 11 HP | 19 → 8 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-130017-267.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 123 | 11 HP | 38 → 27 | 待验证 | 待验证 | 待分诊 | VANTOM_BOSS | `CombatSolver-VANTOM_BOSS-20260903-232502-336.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 124 | 11 HP | 28 → 17 | 待验证 | 待验证 | 待分诊 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260904-004430-742.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 125 | 11 HP | 67 → 56 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-183902-893.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 126 | 11 HP | 42 → 31 | 待验证 | 待验证 | 待分诊 | VANTOM_BOSS | `CombatSolver-VANTOM_BOSS-20260903-122826-859.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 127 | 11 HP | 49 → 38 | 待验证 | 待验证 | 待分诊 | SKULKING_COLONY_ELITE | `CombatSolver-SKULKING_COLONY_ELITE-20260902-004332-828.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 128 | 11 HP | 42 → 31 | 待验证 | 待验证 | 待分诊 | WATERFALL_GIANT_BOSS | `CombatSolver-WATERFALL_GIANT_BOSS-20260903-020033-044.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 129 | 11 HP | 21 → 10 | 待验证 | 待验证 | 待分诊 | ENTOMANCER_ELITE | `CombatSolver-ENTOMANCER_ELITE-20260903-173617-159.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 130 | 11 HP | 23 → 12 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260902-133256-703.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 131 | 11 HP | 30 → 19 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260902-143446-711.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 132 | 11 HP | 17 → 6 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260904-152951-039.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 133 | 11 HP | 11 → 0 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260904-143626-186.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 134 | 11 HP | 11 → 0 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-235652-377.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 135 | 11 HP | 22 → 11 | 待验证 | 待验证 | 待分诊 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260902-234951-025.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 136 | 11 HP | 25 → 14 | 待验证 | 待验证 | 待分诊 | EXOSKELETONS_WEAK | `CombatSolver-EXOSKELETONS_WEAK-20260904-165643-978.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 137 | 11 HP | 23 → 12 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-013528-351.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 138 | 10 HP | 10 → 0 | 待验证 | 待验证 | 待分诊 | EXOSKELETONS_NORMAL | `CombatSolver-EXOSKELETONS_NORMAL-20260903-022141-053.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 139 | 10 HP | 10 → 0 | 待验证 | 待验证 | 待分诊 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260903-102841-511.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 140 | 10 HP | 10 → 0 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260903-234034-381.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 141 | 10 HP | 20 → 10 | 待验证 | 待验证 | 待分诊 | SLUMBERING_BEETLE_NORMAL | `CombatSolver-SLUMBERING_BEETLE_NORMAL-20260903-121641-406.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 142 | 10 HP | 35 → 25 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260904-132932-602.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 143 | 10 HP | 17 → 7 | 待验证 | 待验证 | 待分诊 | ENTOMANCER_ELITE | `CombatSolver-ENTOMANCER_ELITE-20260904-213736-018.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 144 | 10 HP | 50 → 40 | 待验证 | 待验证 | 待分诊 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260903-173758-487.zip` | `combatsolver-reports-20260905-104652.zip` | 【CombatSolver 自动分类】 |
| 145 | 9 HP | 11 → 2 | 待验证 | 待验证 | 待分诊 | MECHA_KNIGHT_ELITE | `CombatSolver-MECHA_KNIGHT_ELITE-20260905-003301-306.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 146 | 9 HP | 9 → 0 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-234008-878.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 147 | 9 HP | 33 → 24 | 待验证 | 待验证 | 待分诊 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260903-080848-024.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 148 | 9 HP | 10 → 1 | 待验证 | 待验证 | 待分诊 | INFESTED_PRISMS_ELITE | `CombatSolver-INFESTED_PRISMS_ELITE-20260902-232141-396.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 149 | 9 HP | 11 → 2 | 待验证 | 待验证 | 待分诊 | CHOMPERS_NORMAL | `CombatSolver-CHOMPERS_NORMAL-20260902-122247-391.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 150 | 9 HP | 25 → 16 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260904-175232-562.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 151 | 9 HP | 15 → 6 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260902-092732-609.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 152 | 9 HP | 22 → 13 | 待验证 | 待验证 | 待分诊 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260904-163723-347.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 153 | 9 HP | 11 → 2 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260902-093449-224.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 154 | 9 HP | 16 → 7 | 待验证 | 待验证 | 待分诊 | TERROR_EEL_ELITE | `CombatSolver-TERROR_EEL_ELITE-20260904-164030-766.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 155 | 9 HP | 12 → 3 | 待验证 | 待验证 | 待分诊 | LOUSE_PROGENITOR_NORMAL | `CombatSolver-LOUSE_PROGENITOR_NORMAL-20260903-201059-922.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 156 | 9 HP | 11 → 2 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-192425-137.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 157 | 9 HP | 82 → 73 | 待验证 | 待验证 | 待分诊 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260903-185114-314.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 158 | 9 HP | 20 → 11 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260903-192819-071.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 159 | 9 HP | 32 → 23 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260903-161351-589.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 160 | 8 HP | 8 → 0 | 待验证 | 待验证 | 待分诊 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260904-183651-041.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 161 | 8 HP | 42 → 34 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260903-232856-385.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 162 | 8 HP | 50 → 42 | 待验证 | 待验证 | 待分诊 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260902-110104-659.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 163 | 8 HP | 8 → 0 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260902-121403-727.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 164 | 8 HP | 8 → 0 | 待验证 | 待验证 | 待分诊 | CONSTRUCT_MENAGERIE_NORMAL | `CombatSolver-CONSTRUCT_MENAGERIE_NORMAL-20260904-113359-233.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 165 | 8 HP | 37 → 29 | 待验证 | 待验证 | 待分诊 | INFESTED_PRISMS_ELITE | `CombatSolver-INFESTED_PRISMS_ELITE-20260902-144818-087.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 166 | 8 HP | 10 → 2 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-123755-027.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 167 | 8 HP | 56 → 48 | 待验证 | 待验证 | 待分诊 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260904-144840-980.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 168 | 8 HP | 8 → 0 | 待验证 | 待验证 | 待分诊 | SCROLLS_OF_BITING_WEAK | `CombatSolver-SCROLLS_OF_BITING_WEAK-20260904-174621-394.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 169 | 8 HP | 21 → 13 | 待验证 | 待验证 | 待分诊 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260903-182719-563.zip` | `combatsolver-reports-20260905-104652.zip` | 【CombatSolver 自动分类】 |
| 170 | 8 HP | 51 → 43 | 待验证 | 待验证 | 待分诊 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260904-233025-442.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 171 | 8 HP | 51 → 43 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-203540-039.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 172 | 8 HP | 22 → 14 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-213125-275.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 173 | 7 HP | 30 → 23 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260902-222154-447.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 174 | 7 HP | 52 → 45 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260903-141238-799.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 175 | 7 HP | 43 → 36 | 待验证 | 待验证 | 待分诊 | ENTOMANCER_ELITE | `CombatSolver-ENTOMANCER_ELITE-20260903-170230-954.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 176 | 7 HP | 29 → 22 | 待验证 | 待验证 | 待分诊 | INFESTED_PRISMS_ELITE | `CombatSolver-INFESTED_PRISMS_ELITE-20260903-194135-526.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 177 | 7 HP | 45 → 38 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260903-173130-810.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 178 | 7 HP | 24 → 17 | 待验证 | 待验证 | 待分诊 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260903-185631-512.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 179 | 7 HP | 12 → 5 | 待验证 | 待验证 | 待分诊 | ENTOMANCER_ELITE | `CombatSolver-ENTOMANCER_ELITE-20260902-232914-493.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 180 | 7 HP | 27 → 20 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260902-021222-549.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 181 | 7 HP | 30 → 23 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260902-234157-522.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 182 | 7 HP | 55 → 48 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260904-150105-348.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 183 | 7 HP | 22 → 15 | 待验证 | 待验证 | 待分诊 | HAUNTED_SHIP_NORMAL | `CombatSolver-HAUNTED_SHIP_NORMAL-20260904-230140-298.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 184 | 7 HP | 14 → 7 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-231323-590.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 185 | 7 HP | 16 → 9 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260902-012428-502.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 186 | 7 HP | 64 → 57 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-174204-145.zip` | `combatsolver-reports-20260905-104652.zip` | 【CombatSolver 自动分类】 |
| 187 | 7 HP | 26 → 19 | 待验证 | 待验证 | 待分诊 | BYRDONIS_ELITE | `CombatSolver-BYRDONIS_ELITE-20260902-094127-139.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 188 | 7 HP | 7 → 0 | 待验证 | 待验证 | 待分诊 | TURRET_OPERATOR_WEAK | `CombatSolver-TURRET_OPERATOR_WEAK-20260903-021440-295.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 189 | 7 HP | 60 → 53 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-171244-450.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 190 | 6 HP | 11 → 5 | 待验证 | 待验证 | 待分诊 | LAGAVULIN_MATRIARCH_BOSS | `CombatSolver-LAGAVULIN_MATRIARCH_BOSS-20260902-123538-526.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 191 | 6 HP | 6 → 0 | 待验证 | 待验证 | 待分诊 | SCROLLS_OF_BITING_WEAK | `CombatSolver-SCROLLS_OF_BITING_WEAK-20260902-110101-196.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 192 | 6 HP | 6 → 0 | 待验证 | 待验证 | 待分诊 | DEVOTED_SCULPTOR_WEAK | `CombatSolver-DEVOTED_SCULPTOR_WEAK-20260903-222335-513.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 193 | 6 HP | 7 → 1 | 待验证 | 待验证 | 待分诊 | SLIMES_NORMAL | `CombatSolver-SLIMES_NORMAL-20260902-094904-990.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 194 | 6 HP | 28 → 22 | 待验证 | 待验证 | 待分诊 | ENTOMANCER_ELITE | `CombatSolver-ENTOMANCER_ELITE-20260904-163616-449.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 195 | 6 HP | 7 → 1 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260902-004901-517.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 196 | 6 HP | 31 → 25 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260902-015838-132.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 197 | 6 HP | 21 → 15 | 待验证 | 待验证 | 待分诊 | MYTES_NORMAL | `CombatSolver-MYTES_NORMAL-20260903-014829-199.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 198 | 6 HP | 51 → 45 | 待验证 | 待验证 | 待分诊 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260905-005836-216.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 199 | 6 HP | 9 → 3 | 待验证 | 待验证 | 待分诊 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260903-190353-611.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 200 | 6 HP | 6 → 0 | 待验证 | 待验证 | 待分诊 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260904-155815-937.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 201 | 6 HP | 6 → 0 | 待验证 | 待验证 | 待分诊 | SCROLLS_OF_BITING_WEAK | `CombatSolver-SCROLLS_OF_BITING_WEAK-20260902-233045-944.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 202 | 6 HP | 17 → 11 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260902-234406-163.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 203 | 6 HP | 24 → 18 | 待验证 | 待验证 | 待分诊 | OVERGROWTH_CRAWLERS | `CombatSolver-OVERGROWTH_CRAWLERS-20260904-161037-452.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 204 | 6 HP | 26 → 20 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260905-023746-498.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 205 | 6 HP | 48 → 42 | 待验证 | 待验证 | 待分诊 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260903-163031-433.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 206 | 6 HP | 9 → 3 | 待验证 | 待验证 | 待分诊 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260903-012407-704.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 207 | 6 HP | 18 → 12 | 待验证 | 待验证 | 待分诊 | VANTOM_BOSS | `CombatSolver-VANTOM_BOSS-20260904-130116-776.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 208 | 6 HP | 10 → 4 | 待验证 | 待验证 | 待分诊 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260905-094805-516.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 209 | 5 HP | 12 → 7 | 待验证 | 待验证 | 待分诊 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260902-022553-160.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 210 | 5 HP | 24 → 19 | 待验证 | 待验证 | 待分诊 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260903-110825-178.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 211 | 5 HP | 11 → 6 | 待验证 | 待验证 | 待分诊 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260902-235120-544.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 212 | 5 HP | 44 → 39 | 待验证 | 待验证 | 待分诊 | INFESTED_PRISMS_ELITE | `CombatSolver-INFESTED_PRISMS_ELITE-20260904-231303-715.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 213 | 5 HP | 39 → 34 | 待验证 | 待验证 | 待分诊 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-003218-752.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 214 | 5 HP | 17 → 12 | 待验证 | 待验证 | 待分诊 | SKULKING_COLONY_ELITE | `CombatSolver-SKULKING_COLONY_ELITE-20260904-195053-636.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 215 | 5 HP | 23 → 18 | 待验证 | 待验证 | 待分诊 | HUNTER_KILLER_NORMAL | `CombatSolver-HUNTER_KILLER_NORMAL-20260903-104459-074.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 216 | 5 HP | 51 → 46 | 待验证 | 待验证 | 待分诊 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260904-201814-788.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 217 | 5 HP | 29 → 24 | 待验证 | 待验证 | 待分诊 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260903-133140-672.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 218 | 5 HP | 6 → 1 | 待验证 | 待验证 | 待分诊 | HUNTER_KILLER_NORMAL | `CombatSolver-HUNTER_KILLER_NORMAL-20260903-135344-189.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 219 | 5 HP | 26 → 21 | 待验证 | 待验证 | 待分诊 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260904-162602-595.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 220 | 5 HP | 27 → 22 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260903-175144-720.zip` | `combatsolver-reports-20260905-104652.zip` | 【CombatSolver 自动分类】 |
| 221 | 5 HP | 17 → 12 | 待验证 | 待验证 | 待分诊 | CEREMONIAL_BEAST_BOSS | `CombatSolver-CEREMONIAL_BEAST_BOSS-20260904-152744-235.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 222 | 5 HP | 17 → 12 | 待验证 | 待验证 | 待分诊 | INKLETS_NORMAL | `CombatSolver-INKLETS_NORMAL-20260903-190349-649.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 223 | 5 HP | 26 → 21 | 待验证 | 待验证 | 待分诊 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260903-210952-314.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 224 | 5 HP | 63 → 58 | 待验证 | 待验证 | 待分诊 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-094648-318.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 225 | 5 HP | 10 → 5 | 待验证 | 待验证 | 待分诊 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260902-145411-895.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 226 | 5 HP | 40 → 35 | 待验证 | 待验证 | 待分诊 | WATERFALL_GIANT_BOSS | `CombatSolver-WATERFALL_GIANT_BOSS-20260903-223426-569.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |

## 三、减损低于 5 HP，暂不修改

| # | 原始减损 | 原始求解器 → 玩家 | 优化后相对人工 | 是否更优 | 处理状态 | 遭遇 | 日志 | 汇总包 | 玩家备注 |
|---:|---:|---:|---:|---|---|---|---|---|---|
| 1 | 4 HP | 14 → 10 | 待验证 | 待验证 | 暂不修改 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-131938-945.zip` | `combatsolver-reports-20260905-104714.zip` | 有添柴不开无惧 |
| 2 | 4 HP | 7 → 3 | 待验证 | 待验证 | 暂不修改 | SLIMED_BERSERKER_NORMAL | `CombatSolver-SLIMED_BERSERKER_NORMAL-20260904-194442-893.zip` | `combatsolver-reports-20260905-104427.zip` | 没有计算佩尔之泪的收益 |
| 3 | 4 HP | 6 → 2 | 待验证 | 待验证 | 暂不修改 | KNIGHTS_ELITE | `CombatSolver-KNIGHTS_ELITE-20260903-131305-984.zip` | `combatsolver-reports-20260905-104714.zip` | 不要滥用愤怒 |
| 4 | 4 HP | 17 → 13 | 待验证 | 待验证 | 暂不修改 | NIBBITS_NORMAL | `CombatSolver-NIBBITS_NORMAL-20260903-115926-199.zip` | `combatsolver-reports-20260905-104714.zip` | 第一回合卖血可以提前击杀第三回合伤害更高的小怪 |
| 5 | 4 HP | 34 → 30 | 待验证 | 待验证 | 暂不修改 | TURRET_OPERATOR_WEAK | `CombatSolver-TURRET_OPERATOR_WEAK-20260903-085815-806.zip` | `combatsolver-reports-20260905-104714.zip` | 666 |
| 6 | 4 HP | 29 → 25 | 待验证 | 待验证 | 暂不修改 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260904-232217-223.zip` | `combatsolver-reports-20260905-104412.zip` | 更好的操作 |
| 7 | 4 HP | 7 → 3 | 待验证 | 待验证 | 暂不修改 | SLIMES_NORMAL | `CombatSolver-SLIMES_NORMAL-20260903-115122-648.zip` | `combatsolver-reports-20260905-104714.zip` | 对精确切击的使用时机存在问题，泄牌意识不足 |
| 8 | 3 HP | 6 → 3 | 待验证 | 待验证 | 暂不修改 | SHRINKER_BEETLE_WEAK | `CombatSolver-SHRINKER_BEETLE_WEAK-20260904-133039-070.zip` | `combatsolver-reports-20260905-104538.zip` | 延申不最后出装唐 |
| 9 | 3 HP | 8 → 5 | 待验证 | 待验证 | 暂不修改 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-201459-661.zip` | `combatsolver-reports-20260905-104621.zip` | 神之一手 |
| 10 | 3 HP | 27 → 24 | 待验证 | 待验证 | 暂不修改 | CEREMONIAL_BEAST_BOSS | `CombatSolver-CEREMONIAL_BEAST_BOSS-20260904-191307-055.zip` | `combatsolver-reports-20260905-104454.zip` | ai经过多次计算，未找到活的机会，我不服，第一回合无色药水开出滚石，就打赢了 |
| 11 | 3 HP | 11 → 8 | 待验证 | 待验证 | 暂不修改 | MECHA_KNIGHT_ELITE | `CombatSolver-MECHA_KNIGHT_ELITE-20260903-174256-994.zip` | `combatsolver-reports-20260905-104652.zip` | 不会先用防御高的触发臂甲,吃了更大战损 |
| 12 | 3 HP | 6 → 3 | 待验证 | 待验证 | 暂不修改 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260903-154107-520.zip` | `combatsolver-reports-20260905-104714.zip` | ai不能计算环规，不会叠雕像的buff |
| 13 | 3 HP | 39 → 36 | 待验证 | 待验证 | 暂不修改 | KNIGHTS_ELITE | `CombatSolver-KNIGHTS_ELITE-20260904-152729-725.zip` | `combatsolver-reports-20260905-104522.zip` | 还是不会蹭武装 |
| 14 | 3 HP | 21 → 18 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-111222-133.zip` | `combatsolver-reports-20260905-104714.zip` | 打出了比ai更好的时间线 |
| 15 | 3 HP | 48 → 45 | 待验证 | 待验证 | 暂不修改 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260904-150140-244.zip` | `combatsolver-reports-20260905-104522.zip` | 不愿意蹭风女 |
| 16 | 3 HP | 30 → 27 | 待验证 | 待验证 | 暂不修改 | VANTOM_BOSS | `CombatSolver-VANTOM_BOSS-20260903-020201-868.zip` | `combatsolver-reports-20260905-104714.zip` | 666 |
| 17 | 3 HP | 56 → 53 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260905-004247-163.zip` | `combatsolver-reports-20260905-104355.zip` | 一个解法 |
| 18 | 2 HP | 2 → 0 | 待验证 | 待验证 | 暂不修改 | SOUL_NEXUS_ELITE | `CombatSolver-SOUL_NEXUS_ELITE-20260903-182549-808.zip` | `combatsolver-reports-20260905-104652.zip` | 更优路线 |
| 19 | 2 HP | 14 → 12 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260902-014002-741.zip` | `combatsolver-reports-20260905-104714.zip` | 说是打出了更好的世界线让我上传 |
| 20 | 2 HP | 34 → 32 | 待验证 | 待验证 | 暂不修改 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260904-011828-088.zip` | `combatsolver-reports-20260905-104549.zip` | 更好的世界线 |
| 21 | 2 HP | 2 → 0 | 待验证 | 待验证 | 暂不修改 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260904-113150-504.zip` | `combatsolver-reports-20260905-104511.zip` | Did not consider to not play Neurosurge on first cycle. |
| 22 | 2 HP | 3 → 1 | 待验证 | 待验证 | 暂不修改 | HUNTER_KILLER_NORMAL | `CombatSolver-HUNTER_KILLER_NORMAL-20260904-064629-773.zip` | `combatsolver-reports-20260905-104549.zip` | 牌序 |
| 23 | 2 HP | 23 → 21 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-213950-113.zip` | `combatsolver-reports-20260905-104621.zip` | 卖血开能力后更好的世界线 |
| 24 | 2 HP | 16 → 14 | 待验证 | 待验证 | 暂不修改 | TUNNELER_WEAK | `CombatSolver-TUNNELER_WEAK-20260903-190452-418.zip` | `combatsolver-reports-20260905-104632.zip` | 不尊重祭品 |
| 25 | 2 HP | 6 → 4 | 待验证 | 待验证 | 暂不修改 | BOWLBUGS_WEAK | `CombatSolver-BOWLBUGS_WEAK-20260902-103039-829.zip` | `combatsolver-reports-20260905-104714.zip` | 打盛碗虫第一回合 |
| 26 | 2 HP | 6 → 4 | 待验证 | 待验证 | 暂不修改 | AXEBOTS_NORMAL | `CombatSolver-AXEBOTS_NORMAL-20260903-183023-470.zip` | `combatsolver-reports-20260905-104652.zip` | 不尊重计划妥当 |
| 27 | 2 HP | 47 → 45 | 待验证 | 待验证 | 暂不修改 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260905-023338-834.zip` | `combatsolver-reports-20260905-104355.zip` | 更优方案 |
| 28 | 2 HP | 2 → 0 | 待验证 | 待验证 | 暂不修改 | DEVOTED_SCULPTOR_WEAK | `CombatSolver-DEVOTED_SCULPTOR_WEAK-20260903-181738-026.zip` | `combatsolver-reports-20260905-104652.zip` | 更优路线 |
| 29 | 2 HP | 6 → 4 | 待验证 | 待验证 | 暂不修改 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260902-122732-724.zip` | `combatsolver-reports-20260905-104714.zip` | 算不明白跨回合，识别不了霉运，不会优先烧诅咒 |
| 30 | 2 HP | 7 → 5 | 待验证 | 待验证 | 暂不修改 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260902-123121-679.zip` | `combatsolver-reports-20260905-104714.zip` | 能力牌顺序 |
| 31 | 2 HP | 44 → 42 | 待验证 | 待验证 | 暂不修改 | WATERFALL_GIANT_BOSS | `CombatSolver-WATERFALL_GIANT_BOSS-20260903-015803-116.zip` | `combatsolver-reports-20260905-104714.zip` | 第一回合一武装三打一防怎么就直接跳了 |
| 32 | 2 HP | 69 → 67 | 待验证 | 待验证 | 暂不修改 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260904-224101-378.zip` | `combatsolver-reports-20260905-104412.zip` | 更好的世界线，可通关 |
| 33 | 1 HP | 10 → 9 | 待验证 | 待验证 | 暂不修改 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260903-165239-293.zip` | `combatsolver-reports-20260905-104705.zip` | 無厭沙蟲解算並不會打出狂亂逃離，致使容易誤算死亡 |
| 34 | 1 HP | 65 → 64 | 待验证 | 待验证 | 暂不修改 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260903-143513-125.zip` | `combatsolver-reports-20260905-104714.zip` | 打出了绝世操作杀死了恶魔 |
| 35 | 1 HP | 5 → 4 | 待验证 | 待验证 | 暂不修改 | TWO_TAILED_RATS_NORMAL | `CombatSolver-TWO_TAILED_RATS_NORMAL-20260903-140145-834.zip` | `combatsolver-reports-20260905-104714.zip` | 毒雾可以拖 |
| 36 | 1 HP | 25 → 24 | 待验证 | 待验证 | 暂不修改 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260902-135348-694.zip` | `combatsolver-reports-20260905-104714.zip` | 原来是不会攻哈大怪( |
| 37 | 1 HP | 28 → 27 | 待验证 | 待验证 | 暂不修改 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260903-102643-501.zip` | `combatsolver-reports-20260905-104714.zip` | 666 |
| 38 | 1 HP | 1 → 0 | 待验证 | 待验证 | 暂不修改 | CHOMPERS_NORMAL | `CombatSolver-CHOMPERS_NORMAL-20260903-095508-304.zip` | `combatsolver-reports-20260905-104714.zip` | 先出余像能多1点防但是算法喜欢先打一张别的再出余像 |
| 39 | 1 HP | 33 → 32 | 待验证 | 待验证 | 暂不修改 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260902-000205-261.zip` | `combatsolver-reports-20260905-104714.zip` | 找到了5000+条世界外的神奇世界线。AI似乎不会选择那些明显低收益（比如第一回合弃掉进阶之灾）但是能改变洗牌排序的操作 |
| 40 | 1 HP | 1 → 0 | 待验证 | 待验证 | 暂不修改 | TOADPOLES_WEAK | `CombatSolver-TOADPOLES_WEAK-20260904-205217-479.zip` | `combatsolver-reports-20260905-104427.zip` | 更好的世界线说是 |
| 41 | 1 HP | 33 → 32 | 待验证 | 待验证 | 暂不修改 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260905-041230-027.zip` | `combatsolver-reports-20260905-104355.zip` | 烘焙手套局，关闭自动求解时，每回合点击求解，显示不在回合内 |
| 42 | 1 HP | 1 → 0 | 待验证 | 待验证 | 暂不修改 | DEVOTED_SCULPTOR_WEAK | `CombatSolver-DEVOTED_SCULPTOR_WEAK-20260904-211404-871.zip` | `combatsolver-reports-20260905-104427.zip` | 先打环绕轨道过后0站损 |
| 43 | 1 HP | 25 → 24 | 待验证 | 待验证 | 暂不修改 | TERROR_EEL_ELITE | `CombatSolver-TERROR_EEL_ELITE-20260904-183857-714.zip` | `combatsolver-reports-20260905-104454.zip` | 更好的世界线 |
| 44 | 4 HP | 28 → 24 | 待验证 | 待验证 | 暂不修改 | CHOMPERS_NORMAL | `CombatSolver-CHOMPERS_NORMAL-20260904-174740-124.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 45 | 4 HP | 13 → 9 | 待验证 | 待验证 | 暂不修改 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260902-144807-778.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 46 | 4 HP | 43 → 39 | 待验证 | 待验证 | 暂不修改 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-173201-457.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 47 | 4 HP | 9 → 5 | 待验证 | 待验证 | 暂不修改 | ENTOMANCER_ELITE | `CombatSolver-ENTOMANCER_ELITE-20260905-064200-145.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 48 | 4 HP | 19 → 15 | 待验证 | 待验证 | 暂不修改 | DECIMILLIPEDE_ELITE | `CombatSolver-DECIMILLIPEDE_ELITE-20260905-012306-931.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 49 | 4 HP | 8 → 4 | 待验证 | 待验证 | 暂不修改 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260903-102159-252.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 50 | 4 HP | 10 → 6 | 待验证 | 待验证 | 暂不修改 | SLUMBERING_BEETLE_NORMAL | `CombatSolver-SLUMBERING_BEETLE_NORMAL-20260904-190352-677.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 51 | 4 HP | 8 → 4 | 待验证 | 待验证 | 暂不修改 | EXOSKELETONS_WEAK | `CombatSolver-EXOSKELETONS_WEAK-20260903-203256-358.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 52 | 4 HP | 4 → 0 | 待验证 | 待验证 | 暂不修改 | FROG_KNIGHT_NORMAL | `CombatSolver-FROG_KNIGHT_NORMAL-20260902-225802-675.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 53 | 4 HP | 7 → 3 | 待验证 | 待验证 | 暂不修改 | THE_OBSCURA_NORMAL | `CombatSolver-THE_OBSCURA_NORMAL-20260904-190026-176.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 54 | 4 HP | 7 → 3 | 待验证 | 待验证 | 暂不修改 | MYTES_NORMAL | `CombatSolver-MYTES_NORMAL-20260902-103306-136.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 55 | 4 HP | 28 → 24 | 待验证 | 待验证 | 暂不修改 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260902-135028-308.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 56 | 4 HP | 46 → 42 | 待验证 | 待验证 | 暂不修改 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260904-180530-827.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 57 | 4 HP | 9 → 5 | 待验证 | 待验证 | 暂不修改 | TOADPOLES_WEAK | `CombatSolver-TOADPOLES_WEAK-20260904-001851-252.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 58 | 4 HP | 6 → 2 | 待验证 | 待验证 | 暂不修改 | MYTES_NORMAL | `CombatSolver-MYTES_NORMAL-20260902-225207-870.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 59 | 4 HP | 21 → 17 | 待验证 | 待验证 | 暂不修改 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260904-194126-089.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 60 | 4 HP | 5 → 1 | 待验证 | 待验证 | 暂不修改 | SEWER_CLAM_NORMAL | `CombatSolver-SEWER_CLAM_NORMAL-20260904-002145-481.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 61 | 4 HP | 4 → 0 | 待验证 | 待验证 | 暂不修改 | TUNNELER_WEAK | `CombatSolver-TUNNELER_WEAK-20260904-161401-011.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 62 | 4 HP | 4 → 0 | 待验证 | 待验证 | 暂不修改 | SLIMES_NORMAL | `CombatSolver-SLIMES_NORMAL-20260902-100415-712.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 63 | 4 HP | 4 → 0 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260902-025622-362.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 64 | 4 HP | 21 → 17 | 待验证 | 待验证 | 暂不修改 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260904-194103-331.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 65 | 4 HP | 24 → 20 | 待验证 | 待验证 | 暂不修改 | LAGAVULIN_MATRIARCH_BOSS | `CombatSolver-LAGAVULIN_MATRIARCH_BOSS-20260902-004841-085.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 66 | 4 HP | 18 → 14 | 待验证 | 待验证 | 暂不修改 | BYGONE_EFFIGY_ELITE | `CombatSolver-BYGONE_EFFIGY_ELITE-20260903-142926-184.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 67 | 3 HP | 35 → 32 | 待验证 | 待验证 | 暂不修改 | TERROR_EEL_ELITE | `CombatSolver-TERROR_EEL_ELITE-20260904-204648-923.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 68 | 3 HP | 31 → 28 | 待验证 | 待验证 | 暂不修改 | SPINY_TOAD_NORMAL | `CombatSolver-SPINY_TOAD_NORMAL-20260904-193549-275.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 69 | 3 HP | 3 → 0 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-002140-087.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 70 | 3 HP | 14 → 11 | 待验证 | 待验证 | 暂不修改 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260902-232554-340.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 71 | 3 HP | 18 → 15 | 待验证 | 待验证 | 暂不修改 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260904-201357-850.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 72 | 3 HP | 27 → 24 | 待验证 | 待验证 | 暂不修改 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260904-143351-545.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 73 | 3 HP | 3 → 0 | 待验证 | 待验证 | 暂不修改 | SKULKING_COLONY_ELITE | `CombatSolver-SKULKING_COLONY_ELITE-20260902-143443-955.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 74 | 3 HP | 9 → 6 | 待验证 | 待验证 | 暂不修改 | OVERGROWTH_CRAWLERS | `CombatSolver-OVERGROWTH_CRAWLERS-20260903-191116-514.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 75 | 3 HP | 36 → 33 | 待验证 | 待验证 | 暂不修改 | VANTOM_BOSS | `CombatSolver-VANTOM_BOSS-20260903-064744-280.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 76 | 3 HP | 30 → 27 | 待验证 | 待验证 | 暂不修改 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-014242-228.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 77 | 3 HP | 10 → 7 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260904-150755-365.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 78 | 3 HP | 16 → 13 | 待验证 | 待验证 | 暂不修改 | LOUSE_PROGENITOR_NORMAL | `CombatSolver-LOUSE_PROGENITOR_NORMAL-20260903-005212-425.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 79 | 3 HP | 37 → 34 | 待验证 | 待验证 | 暂不修改 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260904-174329-576.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 80 | 3 HP | 11 → 8 | 待验证 | 待验证 | 暂不修改 | TURRET_OPERATOR_WEAK | `CombatSolver-TURRET_OPERATOR_WEAK-20260904-174529-131.zip` | `combatsolver-reports-20260905-104511.zip` | 【CombatSolver 自动分类】 |
| 81 | 3 HP | 16 → 13 | 待验证 | 待验证 | 暂不修改 | LOUSE_PROGENITOR_NORMAL | `CombatSolver-LOUSE_PROGENITOR_NORMAL-20260903-004937-987.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 82 | 3 HP | 3 → 0 | 待验证 | 待验证 | 暂不修改 | OWL_MAGISTRATE_NORMAL | `CombatSolver-OWL_MAGISTRATE_NORMAL-20260902-092237-302.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 83 | 3 HP | 21 → 18 | 待验证 | 待验证 | 暂不修改 | WATERFALL_GIANT_BOSS | `CombatSolver-WATERFALL_GIANT_BOSS-20260904-232316-604.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 84 | 3 HP | 18 → 15 | 待验证 | 待验证 | 暂不修改 | LAGAVULIN_MATRIARCH_BOSS | `CombatSolver-LAGAVULIN_MATRIARCH_BOSS-20260903-230536-508.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 85 | 3 HP | 22 → 19 | 待验证 | 待验证 | 暂不修改 | BYRDONIS_ELITE | `CombatSolver-BYRDONIS_ELITE-20260902-101810-439.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 86 | 3 HP | 29 → 26 | 待验证 | 待验证 | 暂不修改 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260904-193051-968.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 87 | 3 HP | 6 → 3 | 待验证 | 待验证 | 暂不修改 | AXEBOTS_NORMAL | `CombatSolver-AXEBOTS_NORMAL-20260904-235831-339.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 88 | 3 HP | 3 → 0 | 待验证 | 待验证 | 暂不修改 | LOUSE_PROGENITOR_NORMAL | `CombatSolver-LOUSE_PROGENITOR_NORMAL-20260902-104024-554.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 89 | 3 HP | 3 → 0 | 待验证 | 待验证 | 暂不修改 | HUNTER_KILLER_NORMAL | `CombatSolver-HUNTER_KILLER_NORMAL-20260903-110425-551.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 90 | 3 HP | 10 → 7 | 待验证 | 待验证 | 暂不修改 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260905-050131-521.zip` | `combatsolver-reports-20260905-104355.zip` | 【CombatSolver 自动分类】 |
| 91 | 3 HP | 11 → 8 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-181731-834.zip` | `combatsolver-reports-20260905-104652.zip` | 【CombatSolver 自动分类】 |
| 92 | 3 HP | 18 → 15 | 待验证 | 待验证 | 暂不修改 | LAGAVULIN_MATRIARCH_BOSS | `CombatSolver-LAGAVULIN_MATRIARCH_BOSS-20260903-230652-694.zip` | `combatsolver-reports-20260905-104606.zip` | 【CombatSolver 自动分类】 |
| 93 | 3 HP | 29 → 26 | 待验证 | 待验证 | 暂不修改 | AXEBOTS_NORMAL | `CombatSolver-AXEBOTS_NORMAL-20260903-001653-475.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 94 | 2 HP | 47 → 45 | 待验证 | 待验证 | 暂不修改 | LAGAVULIN_MATRIARCH_BOSS | `CombatSolver-LAGAVULIN_MATRIARCH_BOSS-20260904-153423-221.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 95 | 2 HP | 26 → 24 | 待验证 | 待验证 | 暂不修改 | SKULKING_COLONY_ELITE | `CombatSolver-SKULKING_COLONY_ELITE-20260902-231216-501.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 96 | 2 HP | 4 → 2 | 待验证 | 待验证 | 暂不修改 | GREMLIN_MERC_NORMAL | `CombatSolver-GREMLIN_MERC_NORMAL-20260902-213059-810.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 97 | 2 HP | 27 → 25 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260902-122349-067.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 98 | 2 HP | 17 → 15 | 待验证 | 待验证 | 暂不修改 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260902-103606-700.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 99 | 2 HP | 2 → 0 | 待验证 | 待验证 | 暂不修改 | KNIGHTS_ELITE | `CombatSolver-KNIGHTS_ELITE-20260902-092105-411.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 100 | 2 HP | 13 → 11 | 待验证 | 待验证 | 暂不修改 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260902-013552-017.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 101 | 2 HP | 7 → 5 | 待验证 | 待验证 | 暂不修改 | TURRET_OPERATOR_WEAK | `CombatSolver-TURRET_OPERATOR_WEAK-20260902-005803-601.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 102 | 2 HP | 2 → 0 | 待验证 | 待验证 | 暂不修改 | SCROLLS_OF_BITING_WEAK | `CombatSolver-SCROLLS_OF_BITING_WEAK-20260902-233721-681.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 103 | 2 HP | 2 → 0 | 待验证 | 待验证 | 暂不修改 | AXEBOTS_NORMAL | `CombatSolver-AXEBOTS_NORMAL-20260903-021151-638.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 104 | 2 HP | 31 → 29 | 待验证 | 待验证 | 暂不修改 | OWL_MAGISTRATE_NORMAL | `CombatSolver-OWL_MAGISTRATE_NORMAL-20260903-104249-941.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 105 | 2 HP | 16 → 14 | 待验证 | 待验证 | 暂不修改 | SKULKING_COLONY_ELITE | `CombatSolver-SKULKING_COLONY_ELITE-20260903-181929-657.zip` | `combatsolver-reports-20260905-104652.zip` | 【CombatSolver 自动分类】 |
| 106 | 2 HP | 49 → 47 | 待验证 | 待验证 | 暂不修改 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260903-024831-757.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 107 | 2 HP | 4 → 2 | 待验证 | 待验证 | 暂不修改 | OVICOPTER_NORMAL | `CombatSolver-OVICOPTER_NORMAL-20260903-192953-748.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 108 | 2 HP | 18 → 16 | 待验证 | 待验证 | 暂不修改 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260903-200450-829.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 109 | 2 HP | 31 → 29 | 待验证 | 待验证 | 暂不修改 | HUNTER_KILLER_NORMAL | `CombatSolver-HUNTER_KILLER_NORMAL-20260904-183817-184.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 110 | 2 HP | 2 → 0 | 待验证 | 待验证 | 暂不修改 | GLOBE_HEAD_NORMAL | `CombatSolver-GLOBE_HEAD_NORMAL-20260903-215517-872.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 111 | 2 HP | 58 → 56 | 待验证 | 待验证 | 暂不修改 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260903-194057-791.zip` | `combatsolver-reports-20260905-104632.zip` | 【CombatSolver 自动分类】 |
| 112 | 2 HP | 24 → 22 | 待验证 | 待验证 | 暂不修改 | THE_KIN_BOSS | `CombatSolver-THE_KIN_BOSS-20260903-110949-385.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 113 | 2 HP | 50 → 48 | 待验证 | 待验证 | 暂不修改 | QUEEN_BOSS | `CombatSolver-QUEEN_BOSS-20260903-134839-132.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 114 | 2 HP | 3 → 1 | 待验证 | 待验证 | 暂不修改 | AXEBOTS_NORMAL | `CombatSolver-AXEBOTS_NORMAL-20260905-000057-470.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 115 | 2 HP | 4 → 2 | 待验证 | 待验证 | 暂不修改 | OWL_MAGISTRATE_NORMAL | `CombatSolver-OWL_MAGISTRATE_NORMAL-20260904-010113-351.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 116 | 2 HP | 10 → 8 | 待验证 | 待验证 | 暂不修改 | CUBEX_CONSTRUCT_NORMAL | `CombatSolver-CUBEX_CONSTRUCT_NORMAL-20260904-152423-997.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 117 | 2 HP | 28 → 26 | 待验证 | 待验证 | 暂不修改 | KNIGHTS_ELITE | `CombatSolver-KNIGHTS_ELITE-20260903-105116-821.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 118 | 1 HP | 22 → 21 | 待验证 | 待验证 | 暂不修改 | HUNTER_KILLER_NORMAL | `CombatSolver-HUNTER_KILLER_NORMAL-20260904-010809-785.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 119 | 1 HP | 43 → 42 | 待验证 | 待验证 | 暂不修改 | LAGAVULIN_MATRIARCH_BOSS | `CombatSolver-LAGAVULIN_MATRIARCH_BOSS-20260904-012323-026.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 120 | 1 HP | 5 → 4 | 待验证 | 待验证 | 暂不修改 | SLIMES_NORMAL | `CombatSolver-SLIMES_NORMAL-20260904-100000-101.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 121 | 1 HP | 66 → 65 | 待验证 | 待验证 | 暂不修改 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260904-005317-497.zip` | `combatsolver-reports-20260905-104549.zip` | 【CombatSolver 自动分类】 |
| 122 | 1 HP | 1 → 0 | 待验证 | 待验证 | 暂不修改 | THE_LOST_AND_FORGOTTEN_NORMAL | `CombatSolver-THE_LOST_AND_FORGOTTEN_NORMAL-20260903-221519-150.zip` | `combatsolver-reports-20260905-104621.zip` | 【CombatSolver 自动分类】 |
| 123 | 1 HP | 30 → 29 | 待验证 | 待验证 | 暂不修改 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260904-124001-727.zip` | `combatsolver-reports-20260905-104538.zip` | 【CombatSolver 自动分类】 |
| 124 | 1 HP | 2 → 1 | 待验证 | 待验证 | 暂不修改 | MYTES_NORMAL | `CombatSolver-MYTES_NORMAL-20260904-141320-721.zip` | `combatsolver-reports-20260905-104522.zip` | 【CombatSolver 自动分类】 |
| 125 | 1 HP | 10 → 9 | 待验证 | 待验证 | 暂不修改 | INFESTED_PRISMS_ELITE | `CombatSolver-INFESTED_PRISMS_ELITE-20260902-105541-530.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 126 | 1 HP | 1 → 0 | 待验证 | 待验证 | 暂不修改 | OWL_MAGISTRATE_NORMAL | `CombatSolver-OWL_MAGISTRATE_NORMAL-20260902-110307-908.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 127 | 1 HP | 1 → 0 | 待验证 | 待验证 | 暂不修改 | TERROR_EEL_ELITE | `CombatSolver-TERROR_EEL_ELITE-20260902-112734-530.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 128 | 1 HP | 11 → 10 | 待验证 | 待验证 | 暂不修改 | LIVING_FOG_NORMAL | `CombatSolver-LIVING_FOG_NORMAL-20260902-114249-593.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 129 | 1 HP | 1 → 0 | 待验证 | 待验证 | 暂不修改 | OWL_MAGISTRATE_NORMAL | `CombatSolver-OWL_MAGISTRATE_NORMAL-20260903-043527-186.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 130 | 1 HP | 40 → 39 | 待验证 | 待验证 | 暂不修改 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260902-122006-800.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 131 | 1 HP | 1 → 0 | 待验证 | 待验证 | 暂不修改 | THE_OBSCURA_NORMAL | `CombatSolver-THE_OBSCURA_NORMAL-20260903-020345-648.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 132 | 1 HP | 71 → 70 | 待验证 | 待验证 | 暂不修改 | KNOWLEDGE_DEMON_BOSS | `CombatSolver-KNOWLEDGE_DEMON_BOSS-20260904-223712-496.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 133 | 1 HP | 53 → 52 | 待验证 | 待验证 | 暂不修改 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260904-233856-685.zip` | `combatsolver-reports-20260905-104412.zip` | 【CombatSolver 自动分类】 |
| 134 | 1 HP | 14 → 13 | 待验证 | 待验证 | 暂不修改 | PHANTASMAL_GARDENERS_ELITE | `CombatSolver-PHANTASMAL_GARDENERS_ELITE-20260903-073914-028.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 135 | 1 HP | 12 → 11 | 待验证 | 待验证 | 暂不修改 | KAISER_CRAB_BOSS | `CombatSolver-KAISER_CRAB_BOSS-20260903-084716-850.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 136 | 1 HP | 32 → 31 | 待验证 | 待验证 | 暂不修改 | OWL_MAGISTRATE_NORMAL | `CombatSolver-OWL_MAGISTRATE_NORMAL-20260903-104131-186.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 137 | 1 HP | 57 → 56 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-123613-012.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 138 | 1 HP | 40 → 39 | 待验证 | 待验证 | 暂不修改 | TEST_SUBJECT_BOSS | `CombatSolver-TEST_SUBJECT_BOSS-20260902-121734-601.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 139 | 1 HP | 4 → 3 | 待验证 | 待验证 | 暂不修改 | SOUL_FYSH_BOSS | `CombatSolver-SOUL_FYSH_BOSS-20260904-201558-611.zip` | `combatsolver-reports-20260905-104427.zip` | 【CombatSolver 自动分类】 |
| 140 | 1 HP | 5 → 4 | 待验证 | 待验证 | 暂不修改 | TERROR_EEL_ELITE | `CombatSolver-TERROR_EEL_ELITE-20260903-145239-014.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 141 | 1 HP | 62 → 61 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-154224-800.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 142 | 1 HP | 5 → 4 | 待验证 | 待验证 | 暂不修改 | KNIGHTS_ELITE | `CombatSolver-KNIGHTS_ELITE-20260903-171142-914.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
| 143 | 1 HP | 7 → 6 | 待验证 | 待验证 | 暂不修改 | THE_INSATIABLE_BOSS | `CombatSolver-THE_INSATIABLE_BOSS-20260902-004409-916.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 144 | 1 HP | 1 → 0 | 待验证 | 待验证 | 暂不修改 | WATERFALL_GIANT_BOSS | `CombatSolver-WATERFALL_GIANT_BOSS-20260904-182145-477.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 145 | 1 HP | 11 → 10 | 待验证 | 待验证 | 暂不修改 | AEONGLASS_BOSS | `CombatSolver-AEONGLASS_BOSS-20260903-182058-526.zip` | `combatsolver-reports-20260905-104652.zip` | 【CombatSolver 自动分类】 |
| 146 | 1 HP | 1 → 0 | 待验证 | 待验证 | 暂不修改 | AXEBOTS_NORMAL | `CombatSolver-AXEBOTS_NORMAL-20260904-180858-088.zip` | `combatsolver-reports-20260905-104454.zip` | 【CombatSolver 自动分类】 |
| 147 | 1 HP | 44 → 43 | 待验证 | 待验证 | 暂不修改 | PHROG_PARASITE_ELITE | `CombatSolver-PHROG_PARASITE_ELITE-20260902-224016-110.zip` | `combatsolver-reports-20260905-104714.zip` | 【CombatSolver 自动分类】 |
| 148 | 1 HP | 2 → 1 | 待验证 | 待验证 | 暂不修改 | NIBBITS_NORMAL | `CombatSolver-NIBBITS_NORMAL-20260903-162107-733.zip` | `combatsolver-reports-20260905-104705.zip` | 【CombatSolver 自动分类】 |
