using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class EnchantmentLifecycleSupport
{
    public static void BeforeFlush(CombatPredictionSimulator simulator, Player player)
    {
        foreach (PredictedCard card in simulator.State.GetPlayerCombatState(player).Hand)
        {
            if (card.Preview.Enchantment is SlumberingEssence)
                card.MutablePreview.EnergyCost.AddUntilPlayed(-1);
        }
    }

    public static bool TriggerAutoPrePlay(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        int turnNumber,
        TurnStartChoiceCursor choices,
        ISet<uint> processedEnemyDeaths)
    {
        if (turnNumber > 1)
            return false;

        PredictedCard[] imbuedCards = simulator.State.GetPlayerCombatState(player).AllCards
            .Where(card => card.Preview.Enchantment is Imbued)
            .ToArray();
        return ContinueImbuedAutoPlay(simulator, combat, processedEnemyDeaths, imbuedCards, 0);
    }

    private static bool ContinueImbuedAutoPlay(CombatPredictionSimulator simulator, SimulatedCombatState combat,
        ISet<uint> deaths, IReadOnlyList<PredictedCard> cards, int nextIndex)
    {
        for (int index = nextIndex; index < cards.Count; index++)
        {
            PredictedCard card = cards[index];
            if (combat.AutoPlayWithChoice(simulator, card, card.Preview.Enchantment!.Id.Entry,
                $"{card.Preview.Id.Entry}+{card.Preview.CurrentUpgradeLevel}#{index}", combat.ActiveExecutionChoices, deaths)) continue;
            simulator.AppendExecutionContinuation(new ImbuedAutoPlayFrame(deaths, cards, index + 1));
            return true;
        }
        return false;
    }

    private sealed record ImbuedAutoPlayFrame(ISet<uint> Deaths, IReadOnlyList<PredictedCard> Cards, int NextIndex)
        : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            SimulatedCombatState.ForkExecutionDeaths(Deaths, context);
            foreach (PredictedCard card in Cards)
                if (!context.TryRemap(card, out PredictedCard? _)) card.Fork(context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Deaths = context.RequireRemap(Deaths), Cards = Cards.Select(context.RequireRemap).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
            => !ContinueImbuedAutoPlay(simulator, (SimulatedCombatState)simulator.State.CombatState, Deaths, Cards, NextIndex);
    }

    public static void TriggerAfterTurnStartOrbs(CombatPredictionSimulator simulator, Player player)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        foreach (PlasmaOrb orb in state.OrbQueue.Orbs.OfType<PlasmaOrb>())
            simulator.GainEnergy(player, orb.PassiveVal);
    }
}
