using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertOrbitRootAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        SetEnergy(player, 3);
        for (int index = 0; index < 4; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_REGENT", Pile = "Hand" });
        var powerModel = await PowerCmd.Apply<OrbitPower>(new BlockingPlayerChoiceContext(), player.Creature, 1, player.Creature, null)
            ?? throw new InvalidOperationException("Native Orbit was not applied.");
        await powerModel.AfterEnergySpent(player.PlayerCombatState!.Hand.Cards[0], 2);
        var root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        var enemy = combat.Enemies.Single();
        ContinuationStamp Stamp(CombatPredictionSimulator current) => ContinuationStamp.CapturePredicted(
            player, current, root.StartTurnNumber, root.Forecast, root.StartTurnNumber);
        StateFingerprint Fingerprint(CombatPredictionSimulator current)
        {
            StateFingerprintBuilder builder = new();
            ((SimulatedCombatState)current.State.CombatState).AppendFingerprint(ref builder, current);
            return builder.Finish();
        }
        ContinuationStamp beforeStamp = Stamp(simulator);
        StateFingerprint beforeFingerprint = Fingerprint(simulator);
        if (beforeStamp != ContinuationStamp.CaptureLive(combat))
            throw new InvalidOperationException("Captured Orbit remainder differs from native continuation.");
        var fork = simulator.Fork();
        var forkState = (SimulatedCombatState)fork.State.CombatState;
        OrbitPower childPower = forkState.EffectivePowers().OfType<OrbitPower>().Single();
        forkState.AdvanceOrbitEnergy(childPower, 1);
        if (forkState.GetOrbitEnergyRemainder(childPower) != 3 || Stamp(fork) == beforeStamp
            || Fingerprint(fork) == beforeFingerprint || Stamp(simulator) != beforeStamp
            || Fingerprint(simulator) != beforeFingerprint || powerModel.DisplayAmount != 2)
            throw new InvalidOperationException("Orbit remainder equality or Fork isolation is inconsistent.");
        OrbitPower added = forkState.AddPowerInstance<OrbitPower>(player.Creature, 2, player.Creature);
        if (forkState.GetOrbitEnergyRemainder(added) != 0)
            throw new InvalidOperationException("New Orbit instance did not start at zero remainder.");
        foreach (var card in player.PlayerCombatState.Hand.Cards.ToArray())
        {
            PlaySimulatedCard(simulator, shadow, simulator.State.FindCard(card)!, null, [enemy]);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), card),
                () => { if (!card.TryManualPlay(null)) throw new InvalidOperationException("Orbit test card was refused."); }, deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
                _request.ScenarioId, "EnergyRefundAfterRootCapture");
        }
        var recaptured = CombatRootSnapshot.Capture(combat).ForkSimulator();
        if (Stamp(recaptured) != Stamp(simulator))
            throw new InvalidOperationException("Orbit remainder changed on root recapture.");
        _completedChecks.Add("Orbit:NativeRefunds:ForkIsolation:NewInstance:Recapture:FingerprintAndContinuation");
    }
}
