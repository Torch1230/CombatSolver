using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Orbs;

namespace CombatSolver.Engine.InCombat.Simulation;

internal readonly record struct CombatHistoryCounters(
    int FinishedPlays, int EtherealPlays, int LightningChannels,
    int UnblockedHitsReceived, int CardsDrawn, int CardsGenerated)
{
    internal CombatHistoryCounters After(CombatPredictionHistoryEntry entry, Player owner)
        => entry switch
        {
            CombatPredictionCardPlayFinishedEntry play => this with
            {
                FinishedPlays = FinishedPlays + 1,
                EtherealPlays = EtherealPlays + (play.WasEthereal && play.CardPlay.Player == owner ? 1 : 0),
            },
            CombatPredictionOrbChanneledEntry channel when channel.Orb is LightningOrb && channel.Orb.Owner == owner
                => this with { LightningChannels = LightningChannels + 1 },
            CombatPredictionDamageReceivedEntry damage when damage.Receiver == owner.Creature && damage.Result.UnblockedDamage > 0
                => this with { UnblockedHitsReceived = UnblockedHitsReceived + 1 },
            CombatPredictionCardDrawnEntry drawn when drawn.Card.Owner == owner
                => this with { CardsDrawn = CardsDrawn + 1 },
            CombatPredictionCardGeneratedEntry generated when generated.Creator == owner
                => this with { CardsGenerated = CardsGenerated + 1 },
            _ => this,
        };

    // Independent reference implementation retained from the original state key.
    // It deliberately does not call After, so test builds detect accounting drift.
    internal static CombatHistoryCounters Scan(CombatPredictionHistory history, Player owner)
    {
        int finished = 0, ethereal = 0, lightning = 0, hits = 0, drawn = 0, generated = 0;
        foreach (CombatPredictionHistoryEntry entry in history)
        {
            switch (entry)
            {
                case CombatPredictionCardPlayFinishedEntry play:
                    finished++;
                    if (play.WasEthereal && play.CardPlay.Player == owner) ethereal++;
                    break;
                case CombatPredictionOrbChanneledEntry channel:
                    if (channel.Orb is LightningOrb && channel.Orb.Owner == owner) lightning++;
                    break;
                case CombatPredictionDamageReceivedEntry damage:
                    if (damage.Receiver == owner.Creature && damage.Result.UnblockedDamage > 0) hits++;
                    break;
                case CombatPredictionCardDrawnEntry draw:
                    if (draw.Card.Owner == owner) drawn++;
                    break;
                case CombatPredictionCardGeneratedEntry generation:
                    if (generation.Creator == owner) generated++;
                    break;
            }
        }
        return new(finished, ethereal, lightning, hits, drawn, generated);
    }
}
