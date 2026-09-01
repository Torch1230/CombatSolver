namespace CombatSolver;

/// <summary>
/// Categories of value a route can bank that outlives the current combat.
/// </summary>
/// <remarks>
/// <see cref="SimulatedCombatState.LongTermResourceValue"/> stays the magnitude axis, but its units are mixed
/// (gold, permanent block, a card reward). A caller that wants to <em>pursue</em> one kind of payoff needs to know
/// which kind was banked, not how much, so the two are tracked side by side.
/// </remarks>
[Flags]
internal enum LongTermGoals
{
    None = 0,

    /// <summary>A fatal kill landed with a card that pays a bonus for it: Hand of Greed, The Hunt, Feed.</summary>
    FatalKillBonus = 1,

    /// <summary>A deck card that grows permanently was played: Genetic Algorithm, The Scythe, Royalties.</summary>
    PersistentGrowth = 2,
}

internal sealed partial class SimulatedCombatState
{
    private int _longTermResourceValue;
    private int _angerCopiesGenerated;
    private LongTermGoals _longTermGoals;
    private LongTermGoals _longTermGoalCardsPlayed;

    public int LongTermResourceValue => _longTermResourceValue;
    public int AngerCopiesGenerated => _angerCopiesGenerated;
    public LongTermGoals LongTermGoals => _longTermGoals;

    /// <summary>
    /// Goal categories whose card was played at all, banked or wasted. Requiring a goal means the card must not be
    /// spent without banking it, which cannot be told from <see cref="LongTermGoals"/> alone: a route that never
    /// draws Hand of Greed and a route that throws it away as a plain attack both bank nothing.
    /// </summary>
    public LongTermGoals LongTermGoalCardsPlayed => _longTermGoalCardsPlayed;

    public void RecordLongTermGoal(LongTermGoals goal) => _longTermGoals |= goal;

    public void RecordLongTermGoalCardPlayed(LongTermGoals goal) => _longTermGoalCardsPlayed |= goal;

    public void RecordLongTermResource(int value)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "长期资源增量必须为正数。");
        _longTermResourceValue = checked(_longTermResourceValue + value);
    }

    public void RecordAngerCopyGenerated()
        => _angerCopiesGenerated = checked(_angerCopiesGenerated + 1);
}
