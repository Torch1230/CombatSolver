# 智能药水机会成本（2026-10-06）

## 玩家反馈与材料

原始描述：“药水策略有问题，正常战损4血，使用智能药水计算用2瓶药战损3血还是选择了使用药水，这种情况非常之多我只能选择强制保护药水。在MOD更新之前并未出现这种情况”。

材料为 `CombatSolver-0.50.0-LAGAVULIN_MATRIARCH_BOSS-27c520401196446bb3eaedf1c6bd28f7.zip`。包内是 0.50.0、游戏 0.111.0，储君 A10 第一幕第17层、乐加维林族母首回合，65/75 HP；RitsuLib 0.6.5、BaseLib 3.4.7。本场记录为原版内容。

智能结果使用两瓶 `SKILL_POTION`，预计战损0，日志省血12、要求90；随后的全部保护结果为无药、预计战损5。两次根的完整续用文本和跑局 RNG 相同，政策差异仅为逐瓶指令。第一幕首领战后回复折算下，两瓶标准成本为 `18 × 5 = 90`，奖励预测关闭、抵扣0。玩家文字中的4→3为反馈中的另一种低收益表现。

智能结果由精确根路线缓存命中提供；包内缺少缓存路线最初生成的搜索日志，具体后置成员未确定。用户更新前的版本未知；质量旁路可追溯至 `fefb31a5`，相关准入与比较源码在0.50.0和本轮修复前一致。

## 根因与修复

终局准入曾把主要质量改善作为通用例外：战损略低或更早结束均可绕过智能药水与 `Ambergris` 的门槛。协调器的药水政策比较又优先战略战损，药水成本未进入这一步，因此后置成员也能替换合规结果。

终局准入只为获胜且减少复活消耗保留对应例外，机会成本与 `Ambergris` 省血限制继续执行；明确强制用药、必要获胜与资源追回保持各自政策。跨成员统一比较战略战损加可选药水机会成本，智能审计使用同一比较入口。成本沿用扣除强制药、一次奖励抵扣和首领折算后的值，成长信用仍进入战略战损。

## 最小验证

修复前源码抽取探针：普通战斗4→3、两药要求18，以及日志12/90判定输入，均为门槛未达标、终局获准、协调器提升。后者只是判定输入，不是整包搜索复现。

修复后的生产比较检查 `python -B tools/testing/checks/BeamWidthPortfolioChecks/run.py`：118项通过，包含上述两组低收益输入的双向取优、组合成员保持无药胜利、提前结束、等门槛、达标省血、奖励抵扣、强制/免费成本、必要胜利、复活资源与资源追回优先级；最终 Boss 的无限成本比较使用宽整数，避免溢出。

原生 `SMART-OPENING-POTION-ADMISSION`，runId `d9629992ac31423586c7465510c18f6d`，Passed（23.63秒，含建局）：无药3战损／2节点；固定单药与双药胜利因收益不足被终局拒绝；明确 Force 接受；确定掉药时0战损／1药、省血3／要求1、3节点；不掉药高收益哨兵0战损／1药、省血12／要求9、3节点。真实战斗及根保持一致，实例由启动器删除。

复跑：PowerShell `pwsh -NoProfile -File tools/testing/run-unattended-test.ps1 -ScenarioId SMART-OPENING-POTION-ADMISSION -CharacterId SILENT -EncounterId FUZZY_WURM_CRAWLER_WEAK -EnemyCurrentHp 1000 -EnableNoGcRegionForTest 0 -TimeoutSeconds 120 -CleanupInstanceOnExit`；Bash `./tools/testing/run-unattended-test.sh --scenario-id SMART-OPENING-POTION-ADMISSION --character-id SILENT --encounter-id FUZZY_WURM_CRAWLER_WEAK --enemy-current-hp 1000 --enable-no-gc-region-for-test 0 --timeout-seconds 120 --cleanup-instance-on-exit`。

原包整场搜索、可见 Steam 和 Linux 运行未验证；短场景耗时不代表玩家整场性能。调查阶段原包解压目录和一次性探针的删除被自动审批拒绝，返回 `blocked by policy`，没有具体原因；原始ZIP保留。
