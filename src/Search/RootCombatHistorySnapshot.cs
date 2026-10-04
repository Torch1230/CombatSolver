using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;

namespace CombatSolver;

internal sealed record RootCombatHistorySnapshot(
    CardPlayStartedEntry[] CardPlaysStarted,
    CardPlayFinishedEntry[] CardPlaysFinished,
    DamageReceivedEntry[] DamageReceived,
    PowerReceivedEntry[] PowerReceived,
    CardDiscardedEntry[] CardsDiscarded,
    CreatureAttackedEntry[] CreatureAttacked,
    EnergySpentEntry[] EnergySpent,
    StarsModifiedEntry[] StarsModified,
    CardDrawnEntry[] CardsDrawn,
    CardExhaustedEntry[] CardsExhausted,
    BlockGainedEntry[] BlockGained,
    CardGeneratedEntry[] CardsGenerated,
    OrbChanneledEntry[] OrbsChanneled)
{
    public static RootCombatHistorySnapshot Capture()
    {
        var history = CombatManager.Instance.History;
        return new RootCombatHistorySnapshot(
            history.CardPlaysStarted.ToArray(),
            history.CardPlaysFinished.ToArray(),
            history.Entries.OfType<DamageReceivedEntry>().ToArray(),
            history.Entries.OfType<PowerReceivedEntry>().ToArray(),
            history.Entries.OfType<CardDiscardedEntry>().ToArray(),
            history.Entries.OfType<CreatureAttackedEntry>().ToArray(),
            history.Entries.OfType<EnergySpentEntry>().ToArray(),
            history.Entries.OfType<StarsModifiedEntry>().ToArray(),
            history.Entries.OfType<CardDrawnEntry>().ToArray(),
            history.Entries.OfType<CardExhaustedEntry>().ToArray(),
            history.Entries.OfType<BlockGainedEntry>().ToArray(),
            history.Entries.OfType<CardGeneratedEntry>().ToArray(),
            history.Entries.OfType<OrbChanneledEntry>().ToArray());
    }
}
