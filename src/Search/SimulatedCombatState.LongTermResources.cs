namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private int _longTermResourceValue;
    private int _angerCopiesGenerated;
    private int _deathSaveRelicHpRestored;
    private GrowthValues _growthRewards;
    private int _brightestFlameMaxHpSpent;
    public int BrightestFlameMaxHpSpent => _brightestFlameMaxHpSpent;

    public void RecordBrightestFlameMaxHpLoss(int amount)
        => _brightestFlameMaxHpSpent = checked(_brightestFlameMaxHpSpent + amount);

    public GrowthValues GrowthRewards => _growthRewards;

    public void RecordGrowthReward(GrowthSource source)
        => _growthRewards = _growthRewards.With(source, checked(_growthRewards.Get(source) + 1));

    /// <summary>
    /// 记一次第三方来源的局外收益到手。句柄从
    /// <see cref="GrowthSourceMirrors.Register(string, Func{MegaCrit.Sts2.Core.Models.CardModel}, Func{MegaCrit.Sts2.Core.Models.CardModel, bool}, Func{MegaCrit.Sts2.Core.Models.CardModel, string})"/>
    /// 取得。和原版八个来源一样，每次成功触发各记一次，额度逐次累计。
    /// </summary>
    public void RecordGrowthReward(GrowthSourceHandle source)
        => _growthRewards = _growthRewards.With(source, checked(_growthRewards.Get(source) + 1));

    public int LongTermResourceValue => _longTermResourceValue;
    public int AngerCopiesGenerated => _angerCopiesGenerated;

    /// <summary>
    /// HP a one-shot death-save relic put back on this route: currently only Lizard Tail.
    /// </summary>
    /// <remarks>
    /// The player really does get this HP, but it is not HP the route <em>earned</em>: the relic is a
    /// cross-combat resource that is gone afterwards, and the same revive would have been available in every
    /// later fight. Scoring takes it back out and charges a premium on top; see
    /// <see cref="ActEndingBossPolicy.DeathSaveRelicPremium"/>.
    /// </remarks>
    public int DeathSaveRelicHpRestored => _deathSaveRelicHpRestored;

    public void RecordLongTermResource(int value)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "长期资源增量必须为正数。");
        _longTermResourceValue = checked(_longTermResourceValue + value);
    }

    public void RecordAngerCopyGenerated()
        => _angerCopiesGenerated = checked(_angerCopiesGenerated + 1);

    public void RecordDeathSaveRelicHpRestored(int amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "保命遗物的回复量必须为正数。");
        _deathSaveRelicHpRestored = checked(_deathSaveRelicHpRestored + amount);
    }
}
