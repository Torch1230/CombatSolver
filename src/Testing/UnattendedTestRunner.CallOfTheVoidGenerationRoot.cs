using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCallOfTheVoidGenerationRootAsync(CombatState combat, Player player)
    {
        if (player.Character.Id.Entry != "NECROBINDER")
            throw new InvalidOperationException("CallOfTheVoid generation requires its native character pool.");
        var enemy = combat.Enemies.Single();
        var power = await PowerCmd.Apply<CallOfTheVoidPower>(
            new BlockingPlayerChoiceContext(), player.Creature, 4, player.Creature, null)
            ?? throw new InvalidOperationException("CallOfTheVoid fixture did not apply its power.");
        var originalCards = player.PlayerCombatState!.AllCards.ToHashSet();
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        MoveStateSnapshot? firstExpected = null;
        for (int batch = 0; batch < 3; batch++)
        {
            MoveStateSnapshot expected;
            using (SimulationNotificationIsolation.Enter())
            {
                Advance(simulator);
                var shadow = (SimulatedCombatState)simulator.State.CombatState;
                expected = CaptureSimulated(simulator, shadow, player, enemy);
                var fork = simulator.Fork();
                AssertSnapshotEqual(expected,
                    CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy),
                    "CallOfTheVoidGenerationRoot", $"Fork{batch}");
            }
            firstExpected ??= expected;
            await power.BeforeHandDraw(player, new BlockingPlayerChoiceContext(), combat);
            AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy),
                "CallOfTheVoidGenerationRoot", $"NativeBatch{batch}");
        }
        var generated = player.PlayerCombatState.AllCards.Where(card => !originalCards.Contains(card)).ToArray();
        if (generated.Length != 12 || generated.Any(card => !card.Keywords.Contains(CardKeyword.Ethereal))
            || !generated.Any(card => card.Pile?.Type == PileType.Hand)
            || !generated.Any(card => card.Pile?.Type == PileType.Discard))
            throw new InvalidOperationException("CallOfTheVoid fixture did not cover its complete ethereal batch and hand overflow.");
        using (SimulationNotificationIsolation.Enter())
        {
            var frozenReplay = root.ForkSimulator();
            Advance(frozenReplay);
            AssertSnapshotEqual(firstExpected!,
                CaptureSimulated(frozenReplay, (SimulatedCombatState)frozenReplay.State.CombatState, player, enemy),
                "CallOfTheVoidGenerationRoot", "FrozenRootAfterNativeGeneration");
        }
        _completedChecks.Add("CallOfTheVoidGenerationRoot:ThreeNativeBatches:TwelveEtherealCards:HandOverflow:FullSnapshotAndContinuation:Fork:FrozenRootAfterNativeGeneration");

        void Advance(CombatPredictionSimulator target)
        {
            if (TurnStartPowerSupport.TriggerBeforeHandDraw(
                target, (SimulatedCombatState)target.State.CombatState, player, new TurnStartChoiceCursor([])))
                throw new InvalidOperationException("CallOfTheVoid unexpectedly opened a choice.");
        }
    }
}
