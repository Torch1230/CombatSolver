namespace CombatSolver;

internal enum SearchObjective { Balanced, Survival, PermanentGrowth, NetResources }

internal readonly record struct SearchObjectivePolicy(
    SearchObjective Mode = SearchObjective.Balanced,
    int MaximumBattleHpLoss = 10,
    int MinimumEndingHp = 30,
    int GrowthTarget = 3,
    int NetResourceTarget = 25)
{
    public static SearchObjectivePolicy Default => new(SearchObjective.Balanced, 10, 30, 3, 25);

    public int EffectiveTarget => Mode == SearchObjective.PermanentGrowth
        ? GrowthTarget > 0 ? GrowthTarget : 3
        : Mode == SearchObjective.NetResources ? NetResourceTarget > 0 ? NetResourceTarget : 25 : 0;

    public bool IsRewardObjective => Mode is SearchObjective.PermanentGrowth or SearchObjective.NetResources;

    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || MaximumBattleHpLoss is < 0 or > 100_000
            || MinimumEndingHp is < 0 or > 100_000 || GrowthTarget is < 0 or > 100_000
            || NetResourceTarget is < 0 or > 100_000)
            throw new InvalidDataException("Invalid search objective or limits.");
    }
}

// Actual increments, separate from legacy per-trigger HP allowances.
internal readonly record struct PermanentGrowth(int MaxHp, int CardDamage, int CardBlock)
{
    public int Total => checked(MaxHp + CardDamage + CardBlock);
}

internal readonly record struct SearchObjectiveOutcome(
    SearchObjectivePolicy Policy,
    PermanentGrowth Growth,
    int GoldGain,
    int PendingGold,
    int ExtraCardRewards,
    int PotionCountChange,
    int PotionValueChange,
    int DeathSaveCost,
    int BattleHpLoss,
    int EndingHp) : IComparable<SearchObjectiveOutcome>
{
    public bool HasObjectiveGain => Policy.IsRewardObjective && TargetValue > 0;
    public bool MeetsLimits => !HasObjectiveGain
        || BattleHpLoss <= Policy.MaximumBattleHpLoss && EndingHp >= Policy.MinimumEndingHp;
    public int NetResourceScore => checked(GoldGain + PendingGold + ExtraCardRewards * 30 + PotionValueChange - DeathSaveCost);
    public int RawTargetValue => Policy.Mode switch
    {
        SearchObjective.PermanentGrowth => Growth.Total,
        SearchObjective.NetResources => NetResourceScore,
        _ => 0,
    };
    public int TargetValue => Math.Min(RawTargetValue, Policy.EffectiveTarget);
    public bool TargetReached => Policy.IsRewardObjective && RawTargetValue >= Policy.EffectiveTarget;
    public bool CanStopSearch(bool completeVictory)
        => completeVictory && TargetReached && MeetsLimits;

    // A negative comparison means preferred. Victory/survival are compared by the caller first.
    public int CompareTo(SearchObjectiveOutcome other)
    {
        if (!Policy.IsRewardObjective) return 0;
        int comparison = other.MeetsLimits.CompareTo(MeetsLimits);
        if (comparison != 0) return comparison;
        if (!MeetsLimits) return 0; // No safe farming route: fall back to HP quality.
        return Math.Max(0, other.TargetValue).CompareTo(Math.Max(0, TargetValue));
    }
}
