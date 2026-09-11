# 回合末 COW 卡牌接收者失效

## 分诊范围

2026-09-10 读取后台 146 份未解决的 SearchFailure 报告并按包内异常分组。同一报告可能包含多种异常，重复上传也未等同于独立玩家或独立 Bug。

- 43 份包含最终路线标注回放状态不一致；35 份的 ROUTE_REPLAY 首次数值差异发生在 EndTurn。
- 上述 43 份中，33 份的战斗状态包含 REGRET。这是关联样本范围，不表示 33 份均已回放或都由本根因造成。
- 另有 32 份包含循环出口 observation 越过 action admission frontier；当前上游已有相关准入修复，本 PR 不重复修改或宣称全部关闭。
- 女王遭遇占全部 146 份计算失败中的 56 份，遭遇集中不等于单一根因。

代表报告 `195aa3183f734d3c91512b155a740239`（0.35.1）与 `f787327bff0f41588dbe9004dfe252e0`（0.35.0）保留在本地，不上传原始包、玩家信息、设置或认证材料。

## 直接证据与根因

第一份报告的 ROUTE_ACTION index=10 为 EndTurn：搜索期预期 HP=79，回放 HP=76，Block/Energy/Stars/HandCount 相同。ROUTE_HEALTH 明确记录该动作由 REGRET 请求并造成 3 点伤害；另一次搜索 index=44 也相差 3 HP。

核对游戏 v0.111.0 原生实现：Bound 没有覆写默认的 `CanAfflictUnplayableCards=true`，所以Regret可以被束缚。ChainsOfBindingPower 在 BeforeSideTurnEnd 清除所有 Bound；Regret 的同阶段 Hook 先记录手牌张数，后续 OnTurnEndInHand 按该值失血。

预测使用 COW：清除 Bound 会经 MutablePreview 替换共享的 CardModel。原 Hook 枚举固定的是旧 CardModel；随后 Regret 的 Hand.Find(receiver) 匹配不到旧预览，直接返回，未记录本回合手牌数。整条路线重新回放与逐节点 Fork 的共享状态不同，因此可能出现回放有伤害、搜索遗漏伤害。

## 修复范围

仅在 BeforeSideTurnEnd 的常规阶段，先固定既有监听顺序及对应 PredictedCard，再逐项读取 wrapper 的当前 Preview。非卡牌接收者保持原身份；卡牌被移出手牌后仍由原 Hook 检查位置。阶段中新增监听者不加入本轮，不重建或重新执行监听序列。

不放宽最终回放校验、不改变卡牌伤害公式、不读 live 状态、不扩大搜索预算，不变更第三方登记接口。该接收者是一次 Hook 派发内的局部值，不进入指纹、续用文本或跨分支缓存。

## 本轮验证

- 生产 PredictedCard/SimCardPile 与模型替身组成最小 COW 检查；旧 frozen-preview 派发模式在 fork=True、hand=1 时复现 hand-only Hook 漏记。
- 当前接收者跟随 wrapper 后通过 78 项断言，覆盖共享/非共享、1/3/8 张牌、同类型逐实例身份、原始模型与父分支隔离、移出手牌、新加入卡牌和非卡牌接收者。
- 这不是原生伤害差分，也不是原报告恢复或整场回放。游戏内验证仍待完成，不能据此将全部关联报告标记修复。
