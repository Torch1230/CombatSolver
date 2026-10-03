using System.Reflection;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // These probes establish the proposed report boundaries. Passing them does not
    // establish that the historical report or its full mod environment was replayed.
    private async Task<int> AssertB015BoundariesAsync(CombatState combat, Player player)
    {
        if (combat.Players.Count != 1 || combat.Enemies.Count != 1
            || combat.Enemies[0].Monster is not Axebot
            || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play)
            throw new InvalidOperationException("B015 boundaries require one Axebot in the single-player Play phase.");
        foreach (var relic in player.Relics.ToArray())
            await RelicCmd.Remove(relic);
        await ClearPlayerPilesAsync(player);
        Creature original = combat.Enemies[0];
        if (original.GetPower<StockPower>() is not { Amount: > 0 })
            throw new InvalidOperationException("B015 stock boundary requires the native Stock power.");
        await CreatureCmd.SetCurrentHp(original, 1);
        await SetBlockAsync(original, 0);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "SHIV", Pile = "Hand", Count = 2 });
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        await AssertB015ReplacementTargetAsync(combat, player, original);
        return await AssertB015CrimsonMantleDeathAsync(combat, player);
    }

    private async Task AssertB015ReplacementTargetAsync(CombatState combat, Player player, Creature original)
    {
        int turn = player.PlayerCombatState!.TurnNumber;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        PlanAction kill = new(PlanActionKind.PlayCard, turn, CardId: "SHIV", TargetCombatId: original.CombatId);
        SimulationSnapshot? first = null;
        SimulationSnapshot? incremental = null;
        SimulationSnapshot? direct = null;
        try
        {
            MoveStateSnapshot afterKill;
            MoveStateSnapshot afterHit;
            uint replacementId;
            int replacementHp;
            using (SimulationNotificationIsolation.Enter())
            {
                first = InvokeForcedTerminalReplay(driver, [kill], null, turn, null);
                SimulatedCombatState state = (SimulatedCombatState)first.Simulator.State.CombatState;
                Creature replacement = state.Enemies.Single();
                replacementId = replacement.CombatId ?? throw new InvalidOperationException("Predicted replacement lost CombatId.");
                if (replacementId == original.CombatId || !ReferenceEquals(state.GetCreature(replacementId), replacement)
                    || state.ContainsCreature(original) || !state.KnownEnemies.Contains(original)
                    || first.AllEnemiesDead || first.TerminalStamp != null)
                    throw new InvalidOperationException("Predicted Stock replacement did not preserve the active and retired identities.");
                replacementHp = first.Simulator.State.GetCreature(replacement).CurrentHp;
                afterKill = CaptureSimulated(first.Simulator, state, player, original);
                CombatPredictionSimulator fork = first.Simulator.Fork();
                SimulatedCombatState forkState = (SimulatedCombatState)fork.State.CombatState;
                if (!ReferenceEquals(forkState.GetCreature(replacementId), replacement))
                    throw new InvalidOperationException("Fork lost the Stock replacement target identity.");
                AssertSnapshotEqual(afterKill, CaptureSimulated(fork, forkState, player, original), "B015T016", "ReplacementFork");
                PlanAction hit = new(PlanActionKind.PlayCard, turn, CardId: "SHIV", TargetCombatId: replacementId);
                incremental = (SimulationSnapshot)InvokeForcedTerminalMethod(driver, "Replay",
                    [new PlanAction[] { hit }, first, turn, 1, null, null, null, null, null, null, null, null, true, false, null])!;
                direct = InvokeForcedTerminalReplay(driver, [kill, hit], null, turn, null);
                _ = InvokeForcedTerminalMethod(driver, "AssertIncrementalEquivalent", [hit, new PlanAction[] { kill, hit }, incremental, direct]);
                SimulatedCombatState hitState = (SimulatedCombatState)incremental.Simulator.State.CombatState;
                Creature hitTarget = hitState.GetCreature(replacementId)
                    ?? throw new InvalidOperationException("Replay lost the prepared Stock replacement target.");
                if (incremental.Simulator.State.GetCreature(hitTarget).CurrentHp != replacementHp - 4
                    || hitState.Enemies.Count != 1 || incremental.HasRisk || direct.HasRisk)
                    throw new InvalidOperationException("The second Shiv did not deal exactly four damage to the surviving replacement.");
                afterHit = CaptureSimulated(incremental.Simulator, hitState, player, original);
                AssertSnapshotEqual(afterKill, CaptureSimulated(first.Simulator, state, player, original), "B015T016", "ParentUnchanged");
            }
            if (!FindActualHandCard(player, "SHIV", 0).TryManualPlay(original))
                throw new InvalidOperationException("Native first Shiv was rejected.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            Creature actualReplacement = combat.Enemies.Single();
            if (actualReplacement.CombatId != replacementId || actualReplacement.CurrentHp != replacementHp)
                throw new InvalidOperationException("Native replacement identity or HP differs before its first targeted play.");
            AssertSnapshotEqual(afterKill, CaptureActual(combat, player, original), "B015T016", "NativeReplacement");
            if (!FindActualHandCard(player, "SHIV", 0).TryManualPlay(actualReplacement))
                throw new InvalidOperationException("Native second Shiv was rejected.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertSnapshotEqual(afterHit, CaptureActual(combat, player, original), "B015T016", "NativeReplacementTarget");
            _completedChecks.Add("B015T016:StockReplacement:CombatId:Fork:DirectIncrementalReplay:TwoNativeShivs:FullState");
        }
        finally
        {
            first?.ReleaseSimulator();
            incremental?.ReleaseSimulator();
            direct?.ReleaseSimulator();
        }
    }

    private async Task<int> AssertB015CrimsonMantleDeathAsync(CombatState combat, Player player)
    {
        Creature enemy = combat.Enemies.Single();
        // The freshly spawned Axebot's first action is BOOT_UP_MOVE, so it cannot
        // kill the 1 HP player before the player-start hook under investigation.
        if (enemy.Monster is not Axebot || enemy.Monster.NextMove?.Id != "BOOT_UP_MOVE")
            throw new InvalidOperationException("B015 mantle boundary requires the replacement's harmless Boot Up move.");
        foreach (var power in player.Creature.Powers.ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, 1);
        await SetBlockAsync(player.Creature, 0);
        SetEnergy(player, 3);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "CRIMSON_MANTLE", Pile = "Hand" });
        if (!FindActualHandCard(player, "CRIMSON_MANTLE", 0).TryManualPlay(null))
            throw new InvalidOperationException("Native Crimson Mantle was rejected.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CrimsonMantlePower mantle = player.Creature.GetPower<CrimsonMantlePower>()
            ?? throw new InvalidOperationException("Native Crimson Mantle did not apply its power.");
        if (mantle.Amount != 7 || mantle.DynamicVars["SelfDamage"].IntValue != 1 || player.Creature.CurrentHp != 1)
            throw new InvalidOperationException("B015 mantle root must be 1 HP, seven block and one unblockable self damage.");
        int turn = player.PlayerCombatState!.TurnNumber;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        SimulationSnapshot? predicted = null;
        MoveStateSnapshot expected;
        try
        {
            using (SimulationNotificationIsolation.Enter())
            {
                predicted = InvokeForcedTerminalReplay(driver, [new PlanAction(PlanActionKind.EndTurn, turn)], null, turn, null);
                if (!predicted.PlayerDead || predicted.PlayerHp != 0 || predicted.AllEnemiesDead
                    || predicted.DeathTurn != turn + 1 || predicted.HasRisk
                    || predicted.TerminalStamp != new CombatTerminalStamp(turn + 1, CombatTerminalOutcome.Defeat))
                    throw new InvalidOperationException("B015 mantle EndTurn did not reach player-start defeat at T+1.");
                expected = CaptureSimulated(predicted.Simulator, (SimulatedCombatState)predicted.Simulator.State.CombatState, player, enemy);
                CombatPredictionSimulator fork = predicted.Simulator.Fork();
                AssertSnapshotEqual(expected, CaptureSimulated(fork, (SimulatedCombatState)fork.State.CombatState, player, enemy), "B015T019", "DefeatFork");
            }
            if (_mercuryTerminalObservation != null)
                throw new InvalidOperationException("B015 cannot replace an existing native terminal observer.");
            // Defeat is finalized by ProcessPendingLoss; EndCombatInternal is the victory path.
            MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(method => method.Name == "ProcessPendingLoss" && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
            PropertyInfo stateProperty = endCombat.GetParameters()[0].ParameterType.GetProperty("State")
                ?? throw new MissingMemberException("CombatTurnState.State");
            MethodInfo prefix = typeof(UnattendedTestRunner).GetMethod(nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(ObserveMercuryCombatEndPrefix));
            Harmony patch = new("CombatSolver.Testing.B015Mantle." + _request.RunId);
            MercuryTerminalObservation observation = new(this, combat, player, enemy, stateProperty, "B015T019");
            _mercuryTerminalObservation = observation;
            try
            {
                CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
                patch.Patch(endCombat, prefix: new HarmonyMethod(prefix));
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions().WaitAsync(TimeSpan.FromSeconds(15));
                DateTimeOffset observationDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (observation.Snapshot == null || !observation.CombatEnded || CombatManager.Instance.IsInProgress)
                {
                    EnsureWithinDeadline();
                    if (DateTimeOffset.UtcNow >= observationDeadline)
                        throw new TimeoutException("Native pending loss did not reach its observed safe point.");
                    observation.Failure?.Throw();
                    await NextFrameAsync();
                }
                observation.Failure?.Throw();
                if (observation.Turn != turn + 1 || observation.Snapshot.PlayerHp != 0)
                    throw new InvalidOperationException("Native mantle death did not occur at player-start T+1.");
                AssertSnapshotEqual(expected, observation.Snapshot, "B015T019", "NativePreTeardown");
                _completedChecks.Add("B015T019:OneHp:NativeCrimsonMantle:EndTurn:DefeatTPlusOne:Fork:FullState");
                return observation.Turn;
            }
            finally
            {
                try { patch.Unpatch(endCombat, prefix); }
                finally
                {
                    CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded;
                    _mercuryTerminalObservation = null;
                }
            }
        }
        finally { predicted?.ReleaseSimulator(); }
    }
}
