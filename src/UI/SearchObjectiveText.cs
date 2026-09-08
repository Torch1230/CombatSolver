namespace CombatSolver;

internal static class SearchObjectiveText
{
    internal static string Name(SearchObjective mode) => mode switch
    {
        SearchObjective.Balanced => "平衡",
        SearchObjective.Survival => "保命优先",
        SearchObjective.PermanentGrowth => "永久培养优先",
        SearchObjective.NetResources => "净收益优先",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static string Summary(SearchObjectiveOutcome outcome)
    {
        string name = SolverText.Get(Name(outcome.Policy.Mode));
        return SolverText.Format($"目标：{name} · 永久成长 {outcome.Growth.Total} 点 · 净收益参考分 {outcome.NetResourceScore}");
    }

    internal static string FullAutoStop(SearchObjectiveOutcome outcome, bool completeVictory)
    {
        if (!completeVictory)
            return SolverText.Get("全自动已暂停：尚未找到完整胜利路线，无法确认收益限制。请重新计算，或在成长策略中切换为平衡／保命优先。");
        List<string> reasons = [];
        if (outcome.BattleHpLoss > outcome.Policy.MaximumBattleHpLoss)
            reasons.Add(SolverText.Format($"预计累计战损 {outcome.BattleHpLoss} HP，超过上限 {outcome.Policy.MaximumBattleHpLoss} HP"));
        if (outcome.EndingHp < outcome.Policy.MinimumEndingHp)
            reasons.Add(SolverText.Format($"预计结束血量 {outcome.EndingHp} HP，低于下限 {outcome.Policy.MinimumEndingHp} HP"));
        if (reasons.Count == 0) return string.Empty;
        return SolverText.Get("全自动已暂停：") + string.Join("；", reasons) + "\n"
            + SolverText.Get("可在成长策略中调整限制或切换目标，然后重新计算；也可人工检查路线后执行本回合。");
    }

    internal static string Details(SearchObjectiveOutcome outcome)
    {
        string detail = SolverText.Format($"本次路线收益：最大生命 {outcome.Growth.MaxHp:+0;-0;0} · 永久伤害 {outcome.Growth.CardDamage:+0;-0;0} · 永久格挡 {outcome.Growth.CardBlock:+0;-0;0}\n金币净变化 {outcome.GoldGain:+0;-0;0} · 待结算金币 {outcome.PendingGold} · 药水数量净变化 {outcome.PotionCountChange:+0;-0;0} · 额外选牌奖励 {outcome.ExtraCardRewards}");
        if (outcome.Policy.IsRewardObjective)
        {
            detail += "\n" + SolverText.Format($"目标限制：本场累计战损 ≤ {outcome.Policy.MaximumBattleHpLoss} · 结束血量 ≥ {outcome.Policy.MinimumEndingHp}");
            if (!outcome.MeetsLimits)
                detail += "\n" + SolverText.Get("此路线未满足收益目标限制，已退回保命比较；请人工决定是否执行。");
        }
        if (outcome.Policy.Mode == SearchObjective.PermanentGrowth)
            detail += "\n" + SolverText.Format($"本次路线培养目标：{outcome.Policy.GrowthTarget} 点（0 不限）；达标后按战损和回合数比较，重新搜索时重新计量。");
        if (outcome.Policy.Mode == SearchObjective.NetResources)
            detail += "\n" + SolverText.Get("净收益为估值：金币 1 分，额外选牌奖励 30 分，药水按现有资源价值折算；消耗一次保命遗物扣 500 分。不预测普通战后随机奖励或整局收益。");
        return detail;
    }
}
