# 能力牌逐卡建模目录

[返回总计划](../power-card-valuation-plan-20260917.md)

本目录记录原版能力牌的官方资料和逐卡模型。卡名必须先从当前游戏 `zhs` / `eng` 本地化核对。通常由用户逐卡说明理解后建模；用户明确授权按实现者理解先做时，模型标为 `QuantifiedDraft`，与已经复核的模型区分。

## 状态

| 状态 | 含义 |
|---|---|
| `LegacyFallback` | 未登记，完整保持当前求解器行为 |
| `AwaitingUserInput` | 用户已点名，但逐卡理解还不足以形成规格 |
| `Specified` | 用户理解和语义已记录，公式或测试尚未完成 |
| `QuantifiedDraft` | 已按实现者理解完成公式、登记和公式测试，尚待用户复核且尚未接入搜索 |
| `Modeled` | 模型、登记和当前阶段所需验证均已完成 |
| `BlockedBySemantics` | 发现原版模拟缺失，必须先修结算语义 |
| `OutOfScopeMultiplayer` | 原版卡池中的多人专属能力牌；保留官方资料，不进入单人求解器建模 |

## 当前卡池清单

游戏版本 `0.111.0` 的六个目标 `CardPool` 共包含 112 张能力牌。105 张属于单人建模范围，7 张由原版标记为 `MultiplayerOnly`：铁甲战士、静默猎手、故障机器人、储君和无色各1张，亡灵契约师2张。六份卡池文档完整列出它们的原版 ID、实现类型、官方中英文名、普通/升级费用和官方效果描述。

目录数据直接核对同版本反编译 `CardPool`、卡牌 `CardType.Power` / `MultiplayerConstraint`，以及当前安装游戏 PCK 的 `localization/zhs/cards.json`、`localization/eng/cards.json`。能量和星能图片图标在 Markdown 中写成文字，未改写卡牌效果。

## 卡池索引

- [铁甲战士](ironclad.md)
- [静默猎手](silent.md)
- [静默猎手首版量化规格](silent-quantification-20260917.md)
- [静默猎手第二版量化与路线保护方案](silent-v2-valuation-and-retention-plan-20260917.md)
- [故障机器人](defect.md)
- [储君](regent.md)
- [亡灵契约师](necrobinder.md)
- [无色](colorless.md)

进入逐卡建模后，每张单人卡的条目继续补充：用户理解或明确授权的实现者假设、奖励公式、惩罚公式、时机、上下文需求、正例、反例、实现位置和验证证据。卡池清单是官方资料基线；只有状态和量化规格明确指向实现时，才代表已经建立草案模型。
