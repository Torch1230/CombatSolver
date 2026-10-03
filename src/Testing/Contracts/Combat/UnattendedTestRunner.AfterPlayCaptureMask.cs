using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertAfterPlayCaptureMatchesFacade(CombatState live, Player player)
    {
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        foreach (bool ending in new[] { false, true })
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            CombatPredictionSimulator parent = root.ForkSimulator();
            var combat = (SimulatedCombatState)parent.State.CombatState;
            parent.RemoveFromCombat(parent.State.GetPlayerCombatState(player).AllCards.ToArray());
            var played = PredictedCard.Create(ModelDb.Card<StrikeIronclad>(), player);
            var late = PredictedCard.Create(ModelDb.Card<RightHandHand>(), player);
            parent.AddGeneratedCardToCombat(played, PileType.Hand, player,
                resultKind: CardGenerationResultKind.Fixed);
            parent.AddGeneratedCardToCombat(late, PileType.Discard, player,
                resultKind: CardGenerationResultKind.Fixed);
            combat.SetAmount<RagePower>(player.Creature, 3);
            combat.SetAmount<PanachePower>(player.Creature, 3);
            if (ending)
                foreach (var enemy in parent.State.Enemies)
                    parent.State.GetCreature(enemy).CurrentHp = 0;
            string before = DescribeContinuationContractState(parent, root, player);
            CombatPredictionSimulator Run(bool capture)
            {
                var sim = parent.Fork();
                var state = sim.State.GetPlayerCombatState(player);
                PredictedCard card = state.Hand.Cards.Single();
                var play = new CardPlay
                {
                    Card = card.Preview, Player = player, Target = null,
                    ResultPile = PileType.Discard, PlayIndex = 0, PlayCount = 1,
                    Resources = new ResourceInfo
                    {
                        EnergySpent = late.Preview.DynamicVars.Energy.IntValue,
                        EnergyValue = late.Preview.DynamicVars.Energy.IntValue,
                        StarsSpent = 0, StarValue = 0,
                    },
                    IsAutoPlay = false,
                };
                if (capture && !sim.BeginExecutionContinuationCapture())
                    throw new InvalidOperationException("After-play fixture could not begin capture.");
                try { HookMirrors.AfterCardPlayed(sim, card, play); }
                finally { if (capture) sim.EndExecutionContinuationCapture(); }
                if (sim.HasPendingChoice || sim.TakeExecutionContinuation() is not null)
                    throw new InvalidOperationException("After-play no-choice fixture unexpectedly suspended.");
                // The late hook still runs after lethal, but its move command has its
                // own native IsEnding gate. Counter callbacks (Panache) still commit.
                if (state.DiscardPile.Cards.Count != (ending ? 1 : 0)
                    || state.Hand.Cards.Count != (ending ? 1 : 2))
                    throw new InvalidOperationException($"Late after-play callback changed its move gate: ending={ending} capture={capture}.");
                int expectedBlock = parent.State.GetCreature(player.Creature).Block + (ending ? 0 : 3);
                if (sim.State.GetCreature(player.Creature).Block != expectedBlock)
                    throw new InvalidOperationException("Ordinary after-play callback changed the native ending command gate.");
                return sim;
            }
            if (DescribeContinuationContractState(Run(false), root, player)
                != DescribeContinuationContractState(Run(true), root, player)
                || DescribeContinuationContractState(parent, root, player) != before)
                throw new InvalidOperationException("After-play capture changed full state, history, RNG, or parent ownership.");
        }
        if (ContinuationStamp.CaptureLive(live).StateText != liveBefore)
            throw new InvalidOperationException("After-play capture fixture changed live combat.");
    }
}
