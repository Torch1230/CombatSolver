using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertNightmareCapturedRootAsync(CombatState combat, Player player)
    {
        foreach (PowerModel existing in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(existing);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "PINPOINT", Pile = "Hand" });
        var selected = FindActualHandCard(player, "PINPOINT", 0);
        selected.EnergyCost.SetThisTurn(0);
        NightmarePower power = (NightmarePower)ModelDb.Power<NightmarePower>().ToMutable();
        await PowerCmd.Apply(new BlockingPlayerChoiceContext(), power, player.Creature, 3, player.Creature, null);
        power.SetSelectedCard(selected);
        await ClearPlayerPilesAsync(player);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var simulator = root.ForkSimulator();
        var shadow = (SimulatedCombatState)simulator.State.CombatState;
        ContinuationStamp Stamp(CombatPredictionSimulator current) => ContinuationStamp.CapturePredicted(
            player, current, root.StartTurnNumber, root.Forecast, root.StartTurnNumber);
        StateFingerprint Fingerprint(CombatPredictionSimulator current)
        {
            StateFingerprintBuilder builder = new();
            ((SimulatedCombatState)current.State.CombatState).AppendFingerprint(ref builder, current);
            return builder.Finish();
        }
        ContinuationStamp captured = Stamp(simulator);
        if (captured != ContinuationStamp.CaptureLive(combat))
            throw new InvalidOperationException("Nightmare root selection differs from native continuation.");
        StateFingerprint before = Fingerprint(simulator);
        var fork = simulator.Fork();
        var forkState = (SimulatedCombatState)fork.State.CombatState;
        forkState.GetNightmareSelection(forkState.EffectivePowers().OfType<NightmarePower>().Single())
            .MutablePreview.DynamicVars.Damage.BaseValue += 7;
        if (Fingerprint(fork) == before || Stamp(fork) == captured || Fingerprint(simulator) != before)
            throw new InvalidOperationException("Nightmare selected-card state was omitted from equality or leaked through Fork.");
        CardModel liveSelection = power.GetInternalData<NightmarePower.Data>().selectedCard!;
        decimal damageBefore = liveSelection.DynamicVars.Damage.BaseValue;
        liveSelection.DynamicVars.Damage.BaseValue += 11;
        if (Stamp(simulator) != captured || Fingerprint(simulator) != before)
            throw new InvalidOperationException("Native Nightmare selection mutation changed a captured root.");
        liveSelection.DynamicVars.Damage.BaseValue = damageBefore;
        if (shadow.PrepareBeforeHandDraw(simulator, player))
            throw new InvalidOperationException("Captured nightmare unexpectedly requested a choice.");
        await Hook.BeforeHandDraw(combat, player, new BlockingPlayerChoiceContext());
        var enemy = combat.Enemies.Single();
        AssertSnapshotEqual(CaptureSimulated(simulator, shadow, player, enemy), CaptureActual(combat, player, enemy),
            "Nightmare", "CapturedSelection");
        _completedChecks.Add("Nightmare:CapturedSelection:NativeCopies:ForkAndLiveIsolation:FingerprintAndContinuation");
    }

    private async Task AssertNightmareSnapshotAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies.Single();
        FindActualHandCard(player, "PINPOINT", 0).EnergyCost.SetThisTurn(0);
        int turn = player.PlayerCombatState!.TurnNumber;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var probe = root.ForkSimulator();
        var nightmare = probe.State.GetPlayerCombatState(player).Hand.Cards.Single(c => c.Preview.Id.Entry == "NIGHTMARE");
        var spec = CardChoiceSupport.GetSpec(probe, nightmare)
            ?? throw new InvalidOperationException("Nightmare choice missing.");
        var choice = CardChoiceSupport.BuildRequestedChoice(spec, ["PINPOINT"]);
        PlanAction action = new(PlanActionKind.PlayCard, turn, CardId: "NIGHTMARE", Choice: choice);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var next = InvokeForcedTerminalReplay(driver, [action, new PlanAction(PlanActionKind.EndTurn, turn)], null, 0, null);
        try
        {
            var shadow = (SimulatedCombatState)next.Simulator.State.CombatState;
            var expected = CaptureSimulated(next.Simulator, shadow, player, enemy);
            var fork = next.Simulator.Fork();
            AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy), "Nightmare", "Fork");
            using (CardSelectCmd.PushSelector(new UnattendedCardSelector(["PINPOINT"])))
            {
                if (!FindActualHandCard(player, "NIGHTMARE", 0).TryManualPlay(null))
                    throw new InvalidOperationException("Native Nightmare failed.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            CombatManager.Instance.OnEndedTurnLocally();
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
            {
                EnsureWithinDeadline();
                if (!CombatManager.Instance.IsInProgress)
                    throw new InvalidOperationException("Nightmare fixture ended combat.");
                await NextFrameAsync();
            }
            AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), "Nightmare", "NextTurnCopies");
        }
        finally
        {
            next.ReleaseSimulator();
        }
    }
}
