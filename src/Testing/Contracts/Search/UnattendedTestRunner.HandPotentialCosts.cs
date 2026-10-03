using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertHandPotentialCosts(CombatState combat, Player player)
    {
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator simulator = root.ForkSimulator();
        SimulatedCombatState predictedCombat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState predictedPlayer = simulator.State.GetPlayerCombatState(player);
        int turn = predictedCombat.GetPlayerTurnNumber(player);
        string BeforeOrAfter() => ContinuationStamp.CapturePredicted(
            player, simulator, turn, root.Forecast, turn).StateText;
        string predictedBefore = BeforeOrAfter();
        int compared = 0;
        foreach (CardModel live in player.PlayerCombatState!.Hand.Cards)
        {
            PredictedCard card = predictedPlayer.FindCard(live)
                ?? throw new InvalidOperationException("Cost query test lost a hand card.");
            bool actualPlayable = live.CanPlay();
            bool simulatedPlayable = predictedCombat.CanPlayCard(
                simulator, card, out int energy, out int stars);
            if (actualPlayable != simulatedPlayable)
                throw new InvalidOperationException($"Cost query changed playability for {live.Id}.");
            if (!simulatedPlayable)
                continue;
            int actualEnergy = live.EnergyCost.GetAmountToSpend();
            int actualStars = Math.Max(0, live.GetStarCostWithModifiers());
            int repeatedEnergy = card.GetEnergyCostWithModifiers(simulator, predictedPlayer);
            int repeatedStars = card.GetStarCostWithModifiers(simulator, predictedPlayer);
            if (energy != actualEnergy || stars != actualStars
                || energy != repeatedEnergy || stars != repeatedStars)
            {
                throw new InvalidOperationException(
                    $"Cost query differs for {live.Id}: reused={energy}/{stars}, " +
                    $"native={actualEnergy}/{actualStars}, repeated={repeatedEnergy}/{repeatedStars}.");
            }
            compared++;
        }
        if (compared == 0)
            throw new InvalidOperationException("Cost query test did not exercise a playable card.");
        if (BeforeOrAfter() != predictedBefore
            || ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
        {
            throw new InvalidOperationException("Read-only cost/playability queries changed combat state.");
        }
        _completedChecks.Add($"HandPotentialCosts:NativeAndRepeated={compared}:RootAndBranchUnchanged");
    }
}
