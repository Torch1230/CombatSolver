using System.Reflection;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private const string ForcedTurnTerminalScenarioId = "forced-turn-terminal-v0111";

    // Explicit one-card replay and native manual play: no Solve, added EndTurn action,
    // or direct AdvanceRound call can substitute for the card's forced-end request.
    private async Task<int> RunForcedTurnTerminalDifferentialAsync(CombatState combat, Player player)
    {
        if (combat.Players.Count != 1 || player.Osty != null
            || player.PlayerCombatState?.OrbQueue.Orbs.Count > 0
            || player.PlayerCombatState?.Phase != PlayerTurnPhase.Play
            || combat.Enemies.Count != 1
            || combat.Enemies[0].Monster is not FuzzyWurmCrawler)
            throw new InvalidOperationException("强制结束终局夹具要求 Play 阶段、无伙伴/球的单人毛毛虫建局。");
        if (_mercuryTerminalObservation != null)
            throw new InvalidOperationException("强制结束终局夹具不能覆盖其他原版终局观察者。");
        Creature enemy = combat.Enemies[0];
        foreach (RelicModel relic in player.Relics.ToArray())
            await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetMaxHp(player.Creature, 80);
        await CreatureCmd.SetCurrentHp(player.Creature, 80);
        await SetBlockAsync(player.Creature, 1000);
        await SetBlockAsync(enemy, 0);
        await CreatureCmd.SetCurrentHp(enemy, 1);
        SetEnergy(player, 3);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "VOID_FORM", Pile = "Hand" });
        await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = "MERCURY_HOURGLASS" });
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CardModel actualCard = FindActualHandCard(player, "VOID_FORM", 0);
        if (actualCard is not VoidForm || player.PlayerCombatState!.AllCards.Count() != 1
            || !enemy.IsAlive || enemy.CurrentHp != 1)
            throw new InvalidOperationException("强制结束夹具没有建立唯一虚空形态与存活的 1 HP 目标。");

        int actionTurn = player.PlayerCombatState.TurnNumber;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat,
                includeTurnSetup: false, theftPolicy: null));
        PlanAction action = new(PlanActionKind.PlayCard, actionTurn, CardId: "VOID_FORM");
        ActionRelicTriggerRecorder triggers = new();
        MoveStateSnapshot initial = CaptureActual(combat, player, enemy);
        List<(string Name, MoveStateSnapshot State)> predictions = [];
        SimulationSnapshot? before = null;
        SimulationSnapshot? direct = null;
        SimulationSnapshot? incremental = null;
        using (SimulationNotificationIsolation.Enter())
        {
            try
            {
                before = InvokeForcedTerminalReplay(driver, [], null, 0, null);
                AssertSnapshotEqual(CaptureSimulated(before.Simulator,
                    (SimulatedCombatState)before.Simulator.State.CombatState, player, enemy),
                    initial, "ForcedTurnTerminal", "InitialRoot");
                if (before.TerminalStamp.HasValue || before.AllEnemiesDead)
                    throw new InvalidOperationException("强制结束夹具在出牌前已经终局。");
                direct = InvokeForcedTerminalReplay(driver, [action], null, 0, triggers);
                incremental = InvokeForcedTerminalReplay(driver, [action], before, actionTurn, null);
                _ = InvokeForcedTerminalMethod(driver, "AssertIncrementalEquivalent",
                    [action, new PlanAction[] { action }, incremental, direct]);
                _completedChecks.Add("ForcedTurnTerminal:FormalIncrementalEquivalence");
                // Recording is a replay-only observer and deliberately prevents Fork until detached.
                direct.Simulator.ActionRelicTriggers = null;
                foreach ((string name, SimulationSnapshot snapshot) in new[]
                         { ("root_replay", direct), ("incremental_replay", incremental) })
                {
                    CombatPredictionSimulator simulator = snapshot.Simulator;
                    SimulatedCombatState state = (SimulatedCombatState)simulator.State.CombatState;
                    if (action.Kind != PlanActionKind.PlayCard || action.Turn != actionTurn
                        || action.EndsPlayerTurn || snapshot.Turn != actionTurn + 1
                        || state.GetPlayerTurnNumber(player) != actionTurn + 1
                        || state.PlayerTurnEndRequested || simulator.IsInProgress
                        || snapshot.TerminalStamp != new CombatTerminalStamp(actionTurn + 1, CombatTerminalOutcome.Victory)
                        || snapshot.CombatEndedTurn != actionTurn + 1 || snapshot.DeathTurn != null
                        || snapshot.PlayerDead || !snapshot.AllEnemiesDead
                        || snapshot.BoundaryReason != SearchBoundaryReason.None || state.HasPendingChoice)
                        throw new InvalidOperationException($"强制结束回放 {name} 未在 T+1 锁定完整胜利：action={action.Turn} snapshot={snapshot.Turn} terminal={snapshot.TerminalStamp}。");
                    state.AssertForkable();
                    predictions.Add((name, CaptureSimulated(simulator, state, player, enemy)));
                    CombatPredictionSimulator fork = simulator.Fork();
                    fork.CheckWinCondition(actionTurn + 2);
                    if (fork.TerminalStamp != snapshot.TerminalStamp
                        || simulator.TerminalStamp != snapshot.TerminalStamp)
                        throw new InvalidOperationException("强制结束终局 stamp 未按值 Fork 或被后续检查改写。");
                }
                if (direct.StateKey != incremental.StateKey)
                    throw new InvalidOperationException("强制结束根回放与增量回放的终局状态键不同。");
                if (!triggers.KillsForAction(0).Any(kill => kill.CombatId == enemy.CombatId
                        && kill.Source.Kind == CombatDamageSourceKind.Relic
                        && kill.Source.Id == "MERCURY_HOURGLASS"))
                    throw new InvalidOperationException("唯一普通出牌动作的正式回放没有记录下一回合沙漏触发。");

                SearchNode parent = ForcedTerminalAnnotationNode(before, null, null);
                SearchNode child = ForcedTerminalAnnotationNode(direct, parent, action);
                before.ReleaseSimulator();
                direct.ReleaseSimulator();
                incremental.ReleaseSimulator();
                if (direct.CombatEndedTurn != actionTurn + 1
                    || incremental.TerminalStamp != direct.TerminalStamp)
                    throw new InvalidOperationException("释放模拟器后强制结束快照丢失终局时点。");
                object annotations = InvokeForcedTerminalMethod(driver, "BuildRouteAnnotations", [child, triggers])
                    ?? throw new InvalidOperationException("正式路线标注没有返回值。");
                PropertyInfo endedTurn = annotations.GetType().GetProperty("CombatEndedTurn")
                    ?? throw new MissingMemberException("RouteAnnotations.CombatEndedTurn");
                if (endedTurn.GetValue(annotations) is not int annotatedTurn || annotatedTurn != actionTurn + 1)
                    throw new InvalidOperationException("正式路线标注仍以普通出牌的 T 代替终局 stamp 的 T+1。");
                _completedChecks.Add("ForcedTurnTerminal:ReplayForkReleasedSnapshotAndAnnotations");
            }
            finally
            {
                before?.ReleaseSimulator();
                direct?.ReleaseSimulator();
                incremental?.ReleaseSimulator();
            }
        }

        MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "EndCombatInternal"
                && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
        PropertyInfo stateProperty = endCombat.GetParameters()[0].ParameterType.GetProperty("State")
            ?? throw new MissingMemberException("CombatTurnState.State");
        MethodInfo prefix = typeof(UnattendedTestRunner).GetMethod(
            nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ObserveMercuryCombatEndPrefix));
        Harmony patch = new("CombatSolver.Testing.ForcedTurnTerminal." + _request.RunId);
        MercuryTerminalObservation observation = new(this, combat, player, enemy, stateProperty);
        _mercuryTerminalObservation = observation;
        try
        {
            CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
            patch.Patch(endCombat, prefix: new HarmonyMethod(prefix));
            if (!actualCard.TryManualPlay(null))
                throw new InvalidOperationException("原版拒绝手动打出唯一的虚空形态。");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            while (observation.Snapshot == null || !observation.CombatEnded || CombatManager.Instance.IsInProgress)
            {
                EnsureWithinDeadline();
                observation.Failure?.Throw();
                await NextFrameAsync();
            }
            observation.Failure?.Throw();
            if (observation.Turn != actionTurn + 1)
                throw new InvalidOperationException($"原版强制结束后的沙漏终局回合为 {observation.Turn}，预期 {actionTurn + 1}。");
            MoveStateSnapshot actual = observation.Snapshot
                ?? throw new InvalidOperationException("原版强制结束终局没有捕获清理前状态。");
            foreach ((string name, MoveStateSnapshot predicted) in predictions)
            {
                AssertSnapshotEqual(predicted, actual, "ForcedTurnTerminal", name);
                _completedChecks.Add("ForcedTurnTerminal:NativePreTeardown:" + name);
            }
            _completedChecks.Add("ForcedTurnTerminal:NativeVoidFormOnly:ActionTurnT:VictoryTurnTPlusOne");
            return observation.Turn;
        }
        finally
        {
            try
            {
                patch.Unpatch(endCombat, prefix);
            }
            finally
            {
                CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded;
                _mercuryTerminalObservation = null;
            }
        }
    }

    private static SimulationSnapshot InvokeForcedTerminalReplay(CombatBeamSolver driver,
        IReadOnlyList<PlanAction> actions, SimulationSnapshot? parent, int turn,
        ActionRelicTriggerRecorder? triggers)
        => (SimulationSnapshot)InvokeForcedTerminalMethod(driver, "Replay", [actions, parent, turn, 0, triggers, null])!;

    private static object? InvokeForcedTerminalMethod(CombatBeamSolver driver, string name, object?[] arguments)
    {
        MethodInfo method = typeof(CombatBeamSolver).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(CombatBeamSolver).FullName, name);
        try
        {
            object? result = method.Invoke(driver, arguments);
            if (result == null && method.ReturnType != typeof(void))
                throw new InvalidOperationException($"强制结束夹具调用 {name} 未返回值。");
            return result;
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static SearchNode ForcedTerminalAnnotationNode(SimulationSnapshot snapshot, SearchNode? parent, PlanAction? action)
        => new(Action: action, ActionCount: parent == null ? 0 : 1,
            PotionCount: snapshot.PotionUseCount, PotionStrategicCost: snapshot.PotionStrategicCost,
            Turn: snapshot.Turn, Traits: SearchRouteTraits.None, FutureSoldHp: 0,
            Score: snapshot.Score, StateKey: snapshot.StateKey, HasPredictionRisk: snapshot.HasRisk,
            BoundaryReason: snapshot.BoundaryReason, IsTerminal: snapshot.AllEnemiesDead || snapshot.PlayerDead,
            Parent: parent, Snapshot: snapshot, CombatProgress: CombatProgressState.Capture(snapshot));
}
