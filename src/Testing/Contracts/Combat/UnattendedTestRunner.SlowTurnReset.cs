using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertSlowTurnResetForkAsync(CombatState combat, Player player)
    {
        var enemy = combat.Enemies[0];
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
        { PowerId = "SLOW_POWER", Target = "Enemy", Amount = 1 });
        CombatPredictionSimulator simulator = CombatRootSnapshot.Capture(combat).ForkSimulator();
        SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
        for (int index = 0; index < 2; index++)
        {
            PredictedCard card = simulator.State.GetPlayerCombatState(player).Hand.Cards.First();
            PlaySimulatedCard(simulator, shadow, card, enemy, combat.Enemies);
        }
        MoveStateSnapshot parent = CaptureSimulated(simulator, shadow, player, enemy);
        CombatPredictionSimulator resetBranch = simulator.Fork();
        SimulatedCombatState resetState = (SimulatedCombatState)resetBranch.State.CombatState;
        SlowPower power = resetState.GetPower<SlowPower>(enemy)!;
        resetState.BeginSideTurn(enemy);
        resetState.SnapshotPowerAmountsAtTurnStart([enemy]);
        if (!resetState.TriggerSideTurnStart(resetBranch, CombatSide.Enemy, [enemy], false))
            throw new InvalidOperationException("Slow reset encountered a pending choice.");

        // Fork at the first failing boundary, before another card can obscure the reset.
        CombatPredictionSimulator fork = resetBranch.Fork();
        SimulatedCombatState forkState = (SimulatedCombatState)fork.State.CombatState;
        if (!ReferenceEquals(power, resetState.GetPower<SlowPower>(enemy)))
            throw new InvalidOperationException("Slow turn reset changed its acquired power identity.");
        MoveStateSnapshot reset = CaptureSimulated(resetBranch, resetState, player, enemy);
        AssertSnapshotEqual(reset, CaptureSimulated(fork, forkState, player, enemy),
            _request.ScenarioId, "ResetFork");
        StateFingerprintBuilder resetKey = new();
        StateFingerprintBuilder forkKey = new();
        resetState.AppendFingerprint(ref resetKey, resetBranch);
        forkState.AppendFingerprint(ref forkKey, fork);
        if (resetKey.Finish() != forkKey.Finish())
            throw new InvalidOperationException("Slow reset fork changed the state fingerprint.");
        AssertSnapshotEqual(parent, CaptureSimulated(simulator, shadow, player, enemy),
            _request.ScenarioId, "ParentIsolation");

        for (int index = 0; index < 2; index++)
        {
            if (!FindActualHandCard(player, "STRIKE_IRONCLAD", 0).TryManualPlay(enemy))
                throw new InvalidOperationException("Native Slow fixture Strike was not playable.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        AssertSnapshotEqual(parent, CaptureActual(combat, player, enemy),
            _request.ScenarioId, "NativeAccumulation");
        await TriggerActualSideTurnStartAsync(combat, CombatSide.Enemy, enemy);
        AssertSnapshotEqual(reset, CaptureActual(combat, player, enemy),
            _request.ScenarioId, "NativeReset");

        PlaySimulatedCard(fork, forkState,
            fork.State.GetPlayerCombatState(player).Hand.Cards.First(), enemy, combat.Enemies);
        MoveStateSnapshot next = CaptureSimulated(fork, forkState, player, enemy);
        if (!FindActualHandCard(player, "STRIKE_IRONCLAD", 0).TryManualPlay(enemy))
            throw new InvalidOperationException("Native post-reset Strike was not playable.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        AssertSnapshotEqual(next, CaptureActual(combat, player, enemy),
            _request.ScenarioId, "NativePostResetDamage");
        AssertSnapshotEqual(reset, CaptureSimulated(resetBranch, resetState, player, enemy),
            _request.ScenarioId, "ResetBranchIsolation");
    }
}
