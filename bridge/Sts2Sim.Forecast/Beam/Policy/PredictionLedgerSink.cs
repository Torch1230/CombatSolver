using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Models;

namespace CombatSolver.Beam.Policy;

internal readonly record struct ObservedCardPlay(
    string CardId,
    CardType CardType,
    bool IsAutoPlay,
    int EnergySpent,
    int StarSpent);

// Core clones deliberately omit PredictionSink. Attach one observer to each
// branch before executing its action; only values observed on that branch enter
// the policy ledger (ICombatPredictionSink.cs).
internal sealed class PredictionLedgerSink : ICombatPredictionSink
{
    public int BrightestFlameMaxHpLost { get; private set; }
    public int PotionsProcured { get; private set; }
    public int DeathSaveHpRestored { get; private set; }
    public int DeathSaveUseCount { get; private set; }
    public List<ObservedCardPlay>? PlayedCards { get; private set; }

    public void HealApplied(Creature creature, decimal requested, int hpBefore, int hpAfter) { }
    public void DeathPrevented(Creature target, AbstractModel preventer, int hpBefore, int hpAfter)
    {
        if (target.Player is null) return;
        DeathSaveUseCount++;
        DeathSaveHpRestored += Math.Max(0, hpAfter - hpBefore);
    }
    public void PotionProcured(Player player, PotionModel potion) => PotionsProcured++;

    public void MaxHpLost(Creature creature, decimal amount, int maxHpBefore,
        int maxHpAfter, bool isFromCard)
    {
        // Core v0.111.0 emits isFromCard=true only for BrightestFlame.
        // Observe nested automatic plays as well as direct player actions.
        if (isFromCard && creature.Player is not null)
            BrightestFlameMaxHpLost += Math.Max(0, maxHpBefore - maxHpAfter);
    }

    public void CardPlayStarted(CardPlay cardPlay)
    {
        PlayedCards ??= new List<ObservedCardPlay>(2);
        PlayedCards.Add(new ObservedCardPlay(
            cardPlay.Card.Id.Entry,
            cardPlay.Card.Type,
            cardPlay.IsAutoPlay,
            Math.Max(0, cardPlay.Resources.EnergySpent),
            Math.Max(0, cardPlay.Resources.StarsSpent)));
    }
}
