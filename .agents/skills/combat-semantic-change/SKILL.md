---
name: combat-semantic-change
description: Modify card, power, relic, potion, orb, monster, death/summon, choice, RNG, or turn-continuation semantics in CombatSolver's HotKernel v2 engine.
---

# Combat Semantic Change

修改战斗数值、结算顺序、选牌、随机源或跨回合机制时使用本指南。**先查 `.local/decompiled/` 核对原版行为，再改 `HotKernel`，并用 `OfflineSearchHarness` 做定向语义验证。**

## 1. 模块定位（HotKernel v2 + Runtime 交接）

| 层级 | 核心文件 | 职责边界 |
| --- | --- | --- |
| **原版导入** | `src/HotKernel/Import/{HotRootBuilder,HotContinuationStamp,HotImportSupport,VanillaRootGate}.cs` | **唯一允许读取 `MegaCrit.Sts2.Core.Models` 的内核层**；主线程采集牌/遗物/药水/Power/怪物/RNG/队列并构造成 `HotRootBundle` |
| **规则目录** | `src/HotKernel/Rules/{HotRules,HotDefinitions,HotEnums,HotHookMask,HotCombatRulesSnapshot}.cs`、`HotCatalog.{Cards,Monsters,Potions,Powers,Relics}.cs` | 静态 ID、枚举掩码、Hook 订阅与基础规则表（无游戏模型引用） |
| **纯值状态** | `src/HotKernel/State/{CombatHotState,HotColdSidecar,HotStatePacker,HotStructs,HotLimits,HotHash,HotKeywords,PackedStateArena,HotExecScratch}.cs` | 非托管热状态、`HotColdSidecar` 辅助状态、`Pack`/`Unpack` 紧凑快照与状态哈希 |
| **核心模拟** | `src/HotKernel/Sim/HotCombatEngine*.cs`、`HotCostModifiers.cs`、`HotPiles.cs`、`HotRng.cs` | 出牌、费用修改、伤害/格挡、抽弃消消耗、球、Power、回合切换、选牌、字节码与怪物 AI |
| **特色机制** | `src/HotKernel/Sim/Content/{HotMonsters,HotRelics,HotPotions,HotRegentNecro,HotFoolTarot}.cs` | 怪物特定逻辑、遗物/药水、摄政王星辉与塔罗/死灵等专属机制 |
| **计划物化与选牌** | `src/HotSearch/HotPlanMaterializer.cs`、`src/Runtime/{NativeChoiceRuntime,PlayerTurnSetupPatches,CombatRootSnapshot}.cs` | 最优动作序列物化与原版实机选牌/部署对账 |

## 2. 状态扩展与失败边界

- **热字段 vs 边车**：高频参与分支模拟与哈希去重的字段放 `CombatHotState`（纯值）；超限稀有/复杂长尾数据放 `HotColdSidecar`。
- **同步清单**：改动 `CombatHotState` 或 `HotColdSidecar` 时，同步检查：
  1. `HotStatePacker` 的 `Pack`/`Unpack` 往返一致；
  2. `HotHash` 是否覆盖影响等价去重的关键状态；
  3. `HotContinuationStamp`（以及 `Runtime/ContinuationStamp.cs`）跨回合对账字段是否一致。
- **显式失败**：遇到尚未支持的原版牌/Power/Mod 钩子或异常状态时，明确抛出 `PredictionUnsupportedException` 或 `IncompatibleGameplayModException`，不用默认值或静默降级掩盖语义缺失；禁止在 `HotKernel` 中使用宽泛 `catch (Exception)`。
- **未知选牌/动作**：确保 `NativeChoiceRuntime` 与 `HotPlanMaterializer` 的实例标识、选项顺序匹配与模拟一致，避免实机部署“找不到动作”触发整场重算。

## 3. 按需验证路径

构建主工程与 `OfflineSearchHarness` 后，按改动影响从轻量到规模按需选用验证（详见 [OFFLINE_SEARCH_HARNESS.md](../../../docs/OFFLINE_SEARCH_HARNESS.md)）：

```powershell
# 1) 改过 State 结构或目录后：秒级检查紧凑布局与实体覆盖
OfflineSearchHarness.exe --check-hot-layout
OfflineSearchHarness.exe --audit-hot-coverage

# 2) 内核自洽、多回合连续性与 DOP1/DOP8 确定性
OfflineSearchHarness.exe --verify-hot-parity --dop 8

# 3) 若手里有最小复现包或玩家问题包，优先定向复现（秒级）
OfflineSearchHarness.exe --replay-fuzz <repro.json>
OfflineSearchHarness.exe --replay-bundle <bundle.zip> --lockstep-actions --deploy

# 4) 大范围机制改动时跑定向差分模糊与整场部署压测
OfflineSearchHarness.exe --fuzz-combat --fights 500 --seed 4026 --phase high-risk
OfflineSearchHarness.exe --soak-deploy --fights 200 --seed 4026 --pool high-risk --profile Medium --dop 8
```

- 涉及原版 UI 交互或生命周期接线（如 `NativeChoiceRuntime`、`PlayerTurnSetupPatches`）时，可补跑一次定向无头实机场景验证（`tools/testing/run-unattended-test.ps1` 带 `-CleanupInstanceOnExit`）。
- 遵循用户最新工程偏好：不必为每一处常规修复都强行补写重型回归夹具或固定日志台账；用最直接、可复现的验证确认修复生效即可。
