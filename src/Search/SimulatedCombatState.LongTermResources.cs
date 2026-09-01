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

    /// <summary>A fatal kill landed with a card that pays a bonus for it: The Hunt, Feed, Hand of Greed.</summary>
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
    /// Goal categories whose Exhaust card was played without banking the goal, which cannot be told from
    /// <see cref="LongTermGoals"/> alone: a route that never draws The Hunt and a route that plays it as a plain
    /// attack both bank nothing, but only the second one destroyed the card.
    ///
    /// Only Exhaust cards are tracked here. Hand of Greed pays gold on a fatal kill but does not Exhaust, so
    /// playing it early is not a waste: it returns to the discard pile and stays in the deck either way.
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
