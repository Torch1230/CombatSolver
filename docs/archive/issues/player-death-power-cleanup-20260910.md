# 玩家死亡能力清理遗漏

## 关联证据

沿报告 `794cc6676ffb49c0badc4c5bbabda86a`（0.35.1）的玩家死亡 → 奥斯提清理 → NecroMastery 伤害链，发现两个独立问题：PR #81 纠正伤害来源的分支死亡判定；当前修复处理更早的死亡能力清理遗漏。不是新增一份同根报告，不重复计算已归档数量。

原版 0.111.0 CreatureCmd.KillWithoutCheckingWinCondition 在 AfterDeath、敌方阵容移除后调用 Creature.RemoveAllPowersAfterDeath，再进入玩家球队列和宠物清理。旧模拟入口没有移除玩家能力，而后续 DeathPowerSupport 清扫只处理敌人；因此玩家的可移除能力仍出现在宠物死亡回调中。

## 改动

在 HandlePlayerDeath 开始处，对求解器拥有的 SimulatedCombatState 调用既有 RemovePowersAfterDeath，再执行原球/宠物处理。该入口仍在死亡被确认后调用，死亡被阻止分支不经过它。移除条件、允许死后存续能力、其他 owner 和敌人领域清扫均复用既有实现。

此项不提高抽牌递归上限，也不替代 PR #81 的伤害来源状态隔离。通用 AfterRemoved 回调语义未在本项扩展，不将使用现有能力移除策略写成完整原生死亡流程覆盖。

## 验证

42 项合同直接编译生产玩家清理入口与既有能力移除方法。旧源码在宠物死亡观察点仍有可移除能力，修复后先移除能力、再清球和宠物；覆盖无宠物、pending、正负层数、保留能力、其他 owner、重复清理和现有 Illusion 策略。

状态、能力及宠物 Kill 是确定性替身；原报告完整死亡/抽牌链、真正的死亡阻止、原生 AfterRemoved、实际 Fork 和游戏内差分尚未验证。关联报告备注应补充本 PR，同时保留此前验证限制。
