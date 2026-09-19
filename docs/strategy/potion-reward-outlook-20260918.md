# 战后掉药预测与满栏用药门槛（2026-09-18）

分支 `feat/potion-drop-odds`，基线上游 `0e6cc2d`（0.41.0）。全部数字来自离线宿主（不启动游戏）与隔离无头实例（macOS，`.local/headless-mac/`），未做可见 Steam 测试。

## 原版机制

- 每个玩家一条奖励 RNG（`PlayerRng.Rewards`，`Rng` = 种子 + 计数器，`SerializableRng` 随存档保存）。反编译全量检索它的消耗点：奖励生成/填充、事件、商店、遗物拾取、多人一次性同步；**战斗内没有消耗点**（战斗内生成牌/药水/球走各自的战斗 RNG）。
- `RewardsSet.WithRewardsFromRoom`：Monster/Elite/Boss 三类房间先把 `GoldReward`（Monster 房 `GoldProportion>0` 才有）、`PotionRewardOdds.Roll` 决定的 `PotionReward`、`CardReward`（精英另加 `RelicReward`）按序放进列表；`Roll` 在这一步就消耗一次 `NextFloat`，与 `CurrentValue + 精英 0.25×0.5` 比较，掉则概率 −0.1，不掉 +0.1；白兽像 `ShouldForcePotionReward` 直接返回真、不抽。
- `GenerateWithoutOffering` 按列表顺序 `Populate`：金币 `NextInt(min, max+1)` 一次；药水 `CreateRandomPotionsOutOfCombat`：`NextFloat` 定稀有度（≤0.1 稀有、≤0.35 罕见、其余普通），再 `NextItem` 从角色池 + 共享池的已解锁药水里取一瓶（`NextItem` = `NextInt(0, n)` 一次）。卡牌在药水之后，不影响药水身份。
- 首次跑局的铁甲战士：前 7 个 Monster 房与前两个 Elite 房走 `TryGenerateTutorialRewards`，药水是脚本指定的；最终幕 Boss 房不生成奖励。

## 实现

`PotionRewardOutlook.Capture`（主线程根捕获）：读 `PlayerOdds.PotionReward.CurrentValue` 得概率；`Rng.Clone()` 奖励流，按上面的顺序重放到药水身份；教程集只留概率（`Forecast=Unknown`），最终 Boss 记 `NoRewards`。
额度 `ReplacementHpCredit`：药水栏满且无 Sozu 时，`Drop` → 那瓶药的策略档位（9/14/18），`NoDrop/NoRewards` → 0，`Unknown` → round(概率 × 9)。`PotionUsePolicy.ApplyReplacementCredit` 从路线可选用药成本里扣一次，**下限 1 HP**。接线：`FinalPlanOrdering`、`BeamRetentionPolicy` 的资格事实、`CombatSearchCoordinator` 的 Smart 上限估计与显示所需 HP。

合并时将预知改为设置中的显式选择，默认关闭。关闭时不捕获奖励前景，也不在摘要显示；开启后获胜路线显示掉药结论和本地化药名。已用/后续用药从战斗历史与终局回放分别取药水 ID，避免中途续用时把累计瓶数误写成未来计划。原下限只约束有战略成本的药水，零成本药水继续按零成本处理。下述 PR 对照属于原始开启前景的实验结果，不代表默认关闭状态。

## 验证

### 镜像对原版生成（隔离无头实例）

新场景 `POTION-REWARD-FORECAST`：开战捕获根 → 让原版 `new RewardsSet(player).WithRewardsFromRoom(room)` + `GenerateWithoutOffering()` 在同一条奖励 RNG 上真实生成 → 比掉落结论与药水 ID。

| 用例 | 角色 / 遭遇 / 房间 | 种子 | 结果 |
|---|---|---|---|
| monster | SILENT / FUZZY_WURM_CRAWLER_WEAK / Monster | 默认 | Drop FRUIT_JUICE，一致 |
| monster-s2 | 同上 | POTION-S2 | NoDrop，一致 |
| monster-s3 | 同上 | POTION-S3 | Drop FLEX_POTION，一致 |
| monster-s4 | 同上 | POTION-S4 | NoDrop，一致 |
| elite-s1 | SILENT / BYGONE_EFFIGY_ELITE / Elite | 默认 | Drop FRUIT_JUICE，一致 |
| elite-s2 | 同上 | POTION-S2 | NoDrop，一致 |
| boss-s1 | SILENT / QUEEN_BOSS / Boss（第一幕） | 默认 | Drop FRUIT_JUICE，一致 |
| monster-open-belt | SILENT，1 瓶药 | POTION-S5 | NoDrop，一致（未满栏，额度 0） |

8/8 通过。铁甲战士在全新 profile 下前几场命中教程奖励集，镜像正确地退回 `Unknown`（这也是为什么用例改用静默猎手）。

### 未满栏逐字段不变（离线宿主）

`EQ`：大语料 5 角色 × 精英/Boss 各 1 根，A10，**1 瓶药**（A10 只有 2 格药水栏，未满，额度恒 0），High 90/50000、Coordinator、Smart、DOP 1。上游 0.41.0 DLL 对本分支 DLL：`compare_results.py` **10 根 983 字段全部一致**（路线逐动作、`projectedBattleHpLost`、展开量、根戳记；只排除耗时/内存字段）。

### 满栏对照（离线宿主）

`FULL`：同语料 5 角色 × 精英/Boss × 4 种子 = 40 根，**2 瓶药（满栏）**，其余同上。新版前景分布：Drop 15（全是普通档，额度 9）、NoDrop 17、NoRewards 4（最终幕 Boss 规格）、Unknown 4（铁甲精英命中教程集，按概率 0.525 计额度 5）。

| 根 | 基线 战损/用药/终 HP | 新版 战损/用药/终 HP | 新版前景 |
|---|---|---|---|
| FULL-NECROBINDER-ELITE-03 | 27 / 0 / 39 | **22** / 1（FIRE_POTION）/ 44 | Drop ENERGY_POTION，额度 9 |
| FULL-REGENT-ELITE-00 | 52 / 0 / 23 | **32** / 2（FLEX_POTION, CURE_ALL）/ 43 | Drop FORTIFIER，额度 9 |
| FULL-REGENT-ELITE-01 | 18 / 0 / 57 | **14** / 1（DEXTERITY_POTION）/ 61 | Drop BLOCK_POTION，额度 9 |
| 其余 37 根 | 与基线逐项相同 | | |

40 对：3 根变化，全部战损下降（合计 −29），用药 +4，完整胜利 25/25 不变。两版搜索工作量相同（同根 `total_expanded` / `total_transitions` 一致），差异只在终局采纳。

### 门槛下限的来历

第一轮满栏对照把额度扣到 0 时，6 根变化里有 2 根是坏的：`FULL-REGENT-BOSS-02` 采纳了多掉 15 血的用药路线，`FULL-REGENT-ELITE-00` 白用一瓶、战损不变。原因是 `IsEligible` 用 `HpSaved = max(0, 无药战损 − 用药战损) ≥ 门槛` 判定，门槛为 0 时任何用药路线都合格。改成下限 1 HP 后这两根回到基线，其余 3 根改善保留；另有 2 根 Boss 房的小幅改善（省 4 血）因幕末回血口径把 1 HP 放大为 5 HP 而不再采纳，属既有政策。

## 产物

- 无头：`.local/headless-mac/req-forecast-*.json`、`req-pr15.json`，各次 `combat_solver_test_result.json`。
- 离线：`.local/offline-harness/potion-odds/{eq-base,eq-new,full-base,full-new,full-new-floor0}/`、`eq-comparison.json`、`full-summary.md`；计划 `plans/*.json`，语料 `specs/`。
