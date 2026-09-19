using System.Collections.Frozen;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Orbs;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

/// <summary>
/// Whole-combat history counters that some cards read through <c>CalculatedVarSpecRegistry</c> and that the
/// state key otherwise does not see.
/// </summary>
/// <remarks>
/// The state key covers piles, powers, RNG streams and the per-turn counters kept on
/// <see cref="SimulatedCombatState"/>, but not how many card plays, draws, channels or hits the whole fight has
/// accumulated. Gold Axe's damage is the number of finished card plays in the combat; Voltaic, Tear Asunder,
/// Pull From Below, Murder and Supermassive read similar totals. Two branches that reach the same piles and
/// resources through a different number of plays therefore hash to the same key while those cards would deal
/// different damage, and exact dedup keeps only one of them. A shadow trace over 50 offline roots found six such
/// same-key/different-output pairs, all on Gold Axe (19 versus 18 finished plays).
///
/// The counters are appended only when the root deck holds one of those cards, so every other fight keeps its
/// key, its transposition hits and its dedup exactly as before. The live history before the root is constant
/// for the whole request and is left out; only the simulated part can differ between branches.
/// </remarks>
internal static class CombatHistoryCounterKey
{
    private static readonly FrozenSet<string> CardIds = new[]
    {
        "GOLD_AXE",
        "VOLTAIC",
        "TEAR_ASUNDER",
        "PULL_FROM_BELOW",
        "MURDER",
        "SUPERMASSIVE",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool AppliesTo(IReadOnlySet<string> playerCardIds)
    {
        foreach (string cardId in CardIds)
        {
            if (playerCardIds.Contains(cardId))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Appends the simulated part of every counter the registry reads for the player's cards, in one pass over the
    /// prediction history.
    /// </summary>
    public static void Append(ref StateFingerprintBuilder key, CombatPredictionSimulator simulator, Player owner)
    {
        Creature ownerCreature = owner.Creature;
        int finishedPlays = 0;
        int etherealPlays = 0;
        int lightningChannels = 0;
        int unblockedHitsReceived = 0;
        int cardsDrawn = 0;
        int cardsGenerated = 0;
        foreach (CombatPredictionHistoryEntry entry in simulator.History)
        {
            switch (entry)
            {
                case CombatPredictionCardPlayFinishedEntry play:
                    finishedPlays++;
                    if (play.WasEthereal && play.CardPlay.Player == owner)
                        etherealPlays++;
                    break;
                case CombatPredictionOrbChanneledEntry channel:
                    if (channel.Orb is LightningOrb && channel.Orb.Owner == owner)
                        lightningChannels++;
                    break;
                case CombatPredictionDamageReceivedEntry damage:
                    if (damage.Receiver == ownerCreature && damage.Result.UnblockedDamage > 0)
                        unblockedHitsReceived++;
                    break;
                case CombatPredictionCardDrawnEntry drawn:
                    if (drawn.Card.Owner == owner)
                        cardsDrawn++;
                    break;
                case CombatPredictionCardGeneratedEntry generated:
                    if (generated.Creator == owner)
                        cardsGenerated++;
                    break;
            }
        }
        key.Add('h');
        key.Add(finishedPlays);
        key.Add(etherealPlays);
        key.Add(lightningChannels);
        key.Add(unblockedHitsReceived);
        key.Add(cardsDrawn);
        key.Add(cardsGenerated);
    }
}
