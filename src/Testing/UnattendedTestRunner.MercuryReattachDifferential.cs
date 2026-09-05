using System.Reflection;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private const string MercuryReattachScenarioId = "mercury-reattach-boundary-v0111";
    private static MercuryTerminalObservation? _mercuryTerminalObservation;

    private sealed class MercuryTerminalObservation(
        UnattendedTestRunner runner,
        CombatState combat,
        Player player,
        Creature focus,
        PropertyInfo turnStateCombatProperty,
        string checkPrefix = "MercuryReattach")
    {
        public CombatState Combat { get; } = combat;
        public PropertyInfo TurnStateCombatProperty { get; } = turnStateCombatProperty;
        public MoveStateSnapshot? Snapshot { get; private set; }
        public int Turn { get; private set; }
        public bool CombatEnded { get; private set; }
        public ExceptionDispatchInfo? Failure { get; private set; }
        private readonly CombatRoom _room = combat.RunState.CurrentRoom as CombatRoom
            ?? throw new InvalidOperationException("沙漏观察必须在当前战斗房间内建立。");

        public void ObserveCombatEnded(CombatRoom room)
        {
            if (ReferenceEquals(room, _room))
                CombatEnded = true;
        }

        public void Capture()
        {
            try
            {
                if (Snapshot != null)
                    throw new InvalidOperationException("沙漏边界观察重复进入同一战斗的结束入口。");
                Turn = player.PlayerCombatState?.TurnNumber
                    ?? throw new InvalidOperationException("沙漏终局观察发生在玩家战斗状态清理之后。");
                Snapshot = CaptureActual(Combat, player, focus);
                runner._completedChecks.Add(checkPrefix + ":NativePreTeardownSnapshot");
            }
            catch (Exception error)
            {
                Failure = ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }
    }

    // Observation only: the original async method still runs unmodified. The exact
    // turn-state argument, not the manager's current state, establishes ownership.
    private static void ObserveMercuryCombatEndPrefix(CombatManager __instance, object __0)
    {
        MercuryTerminalObservation? observation = _mercuryTerminalObservation;
        if (observation == null || SimulationNotificationIsolation.IsActive
            || !ReferenceEquals(__instance, CombatManager.Instance)
            || !ReferenceEquals(observation.TurnStateCombatProperty.GetValue(__0), observation.Combat))
            return;
        observation.Capture();
    }

    private async Task<int> RunMercuryReattachDifferentialAsync(CombatState combat, Player player)
    {
        if (combat.Players.Count != 1 || player.Osty != null
            || player.PlayerCombatState?.OrbQueue.Orbs.Count > 0
            || combat.Enemies.Count != 3
            || combat.Enemies.Any(enemy => enemy.Monster is not DecimillipedeSegment))
            throw new InvalidOperationException("沙漏边界夹具要求无奥斯提/充能球的单人三段千足虫遭遇。");
        if (_mercuryTerminalObservation != null)
            throw new InvalidOperationException("沙漏终局观察已由另一个测试持有。");
        Creature[] segments = combat.Enemies.ToArray();
        foreach (var relic in player.Relics.ToArray())
            await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
        {
            if (power is not ReattachPower)
                await PowerCmd.Remove(power);
        }
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetMaxHp(player.Creature, 1000);
        await CreatureCmd.SetCurrentHp(player.Creature, 1000);
        await SetBlockAsync(player.Creature, 0);
        foreach (Creature segment in segments)
        {
            await SetBlockAsync(segment, 0);
            await CreatureCmd.SetCurrentHp(segment, segment.MaxHp);
            if (segment.GetPower<ReattachPower>()?.Amount != 25)
                throw new InvalidOperationException("千足虫夹具没有原版初始的 25 点重新接合能力。");
        }

        // Establish native private isReviving through real damage/AfterDeath,
        // then let a natural enemy turn perform DEAD_MOVE. No HP=0 injection,
        // move-log rewriting, or private revival-state mutation is used.
        await KillMercuryFixtureSegmentAsync(segments[1], player);
        await KillMercuryFixtureSegmentAsync(segments[2], player);
        AssertMercuryNativeReviving(segments[1], "DEAD_MOVE");
        AssertMercuryNativeReviving(segments[2], "DEAD_MOVE");
        await AdvanceMercuryActualTurnAsync(combat, player, expectVictory: false);
        AssertMercuryNativeReviving(segments[1], "REATTACH_MOVE");
        AssertMercuryNativeReviving(segments[2], "REATTACH_MOVE");
        await CreatureCmd.SetCurrentHp(segments[0], 1);
        await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = "MERCURY_HOURGLASS" });
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "EndCombatInternal"
                && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
        PropertyInfo stateProperty = endCombat.GetParameters()[0].ParameterType.GetProperty("State")
            ?? throw new MissingMemberException("CombatTurnState.State");
        MethodInfo prefix = typeof(UnattendedTestRunner).GetMethod(
            nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ObserveMercuryCombatEndPrefix));
        Harmony observerPatch = new("CombatSolver.Testing.MercuryReattach." + _request.RunId);
        MercuryTerminalObservation observation = new(this, combat, player, segments[0], stateProperty);
        _mercuryTerminalObservation = observation;
        try
        {
            CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
            observerPatch.Patch(endCombat, prefix: new HarmonyMethod(prefix));
            await RunMercuryRoundBoundaryAsync(combat, player, segments, "REATTACH_MOVE", expectVictory: false);
            if (segments[0].CurrentHp != 0 || segments[1].CurrentHp != 22 || segments[2].CurrentHp != 22)
                throw new InvalidOperationException("原版应先重新接合至 25 生命，再受沙漏 3 点伤害；不能报告全灭。");
            AssertMercuryNativeReviving(segments[0], "DEAD_MOVE");

            // In the same live encounter, make a second legal DEAD_MOVE boundary.
            // The previously killed front and newly killed middle must wait this
            // enemy turn; killing the remaining 1-HP back at next player start wins.
            await KillMercuryFixtureSegmentAsync(segments[1], player);
            await CreatureCmd.SetCurrentHp(segments[2], 1);
            AssertMercuryNativeReviving(segments[0], "DEAD_MOVE");
            AssertMercuryNativeReviving(segments[1], "DEAD_MOVE");
            int finishedTurn = await RunMercuryRoundBoundaryAsync(
                combat, player, segments, "DEAD_MOVE", expectVictory: true);
            _completedChecks.Add("MercuryReattach:ActualTurnBoundaryOnly:NoSolverTerminalAnnotationClaim");
            return finishedTurn;
        }
        finally
        {
            try
            {
                observerPatch.Unpatch(endCombat, prefix);
            }
            finally
            {
                CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded;
                _mercuryTerminalObservation = null;
            }
        }
    }

    private static async Task KillMercuryFixtureSegmentAsync(Creature segment, Player player)
    {
        if (!segment.IsAlive || !segment.IsHittable)
            throw new InvalidOperationException("合法死亡建局必须从可命中的存活千足虫开始。");
        await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), segment, segment.CurrentHp,
            ValueProp.Unblockable | ValueProp.Unpowered, player.Creature);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private static void AssertMercuryNativeReviving(Creature segment, string move)
    {
        if (!segment.IsDead || segment.IsHittable || segment.Monster?.NextMove.Id != move
            || segment.GetPower<ReattachPower>()?.Amount != 25)
            throw new InvalidOperationException($"原版千足虫没有合法的 {move}/不可命中/25 点复活状态。");
    }

    private async Task<int> RunMercuryRoundBoundaryAsync(
        CombatState combat, Player player, IReadOnlyList<Creature> segments, string phase, bool expectVictory)
    {
        EnsureWithinDeadline();
        int initialTurn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidOperationException("沙漏回合测试没有玩家回合号。");
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatRootSnapshot recapturedRoot = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator rootSimulator = root.ForkSimulator();
        (string Name, CombatRootSnapshot Root, CombatPredictionSimulator Simulator)[] lanes =
        [
            ("direct", root, rootSimulator),
            ("fork", root, rootSimulator.Fork()),
            ("recaptured_root", recapturedRoot, recapturedRoot.ForkSimulator()),
        ];
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null);
        MethodInfo advanceRound = typeof(CombatBeamSolver).GetMethod("AdvanceRound",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(CombatBeamSolver).FullName, "AdvanceRound");
        List<(string Name, MoveStateSnapshot Snapshot, int Turn, CombatTerminalStamp? Terminal)> predictions = [];
        MoveStateSnapshot initial = CaptureActual(combat, player, segments[0]);
        foreach (var lane in lanes)
        {
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            SimulatedCombatState state = (SimulatedCombatState)lane.Simulator.State.CombatState;
            AssertSnapshotEqual(CaptureSimulated(lane.Simulator, state, player, segments[0]),
                initial, "MercuryReattach", phase + ":initial:" + lane.Name);
            CombatBeamSolver driver = new(lane.Root, names, damage, policy);
            HashSet<uint> processedDeaths = segments
                .Where(enemy => lane.Simulator.State.GetCreature(enemy).IsDead)
                .Select(enemy => enemy.CombatId ?? throw new InvalidOperationException("夹具怪物缺少战斗身份。"))
                .ToHashSet();
            object?[] arguments = [lane.Simulator, state, 0, processedDeaths, 0, null];
            SearchBoundaryReason boundary;
            try
            {
                boundary = (SearchBoundaryReason)(advanceRound.Invoke(driver, arguments)
                    ?? throw new InvalidOperationException("回合驱动没有返回边界状态。"));
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
            if (boundary != SearchBoundaryReason.None || state.HasPendingChoice)
                throw new InvalidOperationException($"沙漏 {phase}/{lane.Name} 出现未计划边界 {boundary}。");
            state.AssertForkable();
            CombatPredictionSimulator terminalFork = lane.Simulator.Fork();
            if (terminalFork.TerminalStamp != lane.Simulator.TerminalStamp)
                throw new InvalidOperationException("终局回合标记没有按值保留到 Fork。");
            if (terminalFork.TerminalStamp.HasValue)
            {
                terminalFork.CheckWinCondition(initialTurn + 99);
                if (terminalFork.TerminalStamp != lane.Simulator.TerminalStamp)
                    throw new InvalidOperationException("终局回合标记被后续检查覆盖。");
            }
            predictions.Add((lane.Name, CaptureSimulated(lane.Simulator, state, player, segments[0]),
                state.GetPlayerTurnNumber(player), lane.Simulator.TerminalStamp));
        }

        int actualTurn = await AdvanceMercuryActualTurnAsync(combat, player, expectVictory);
        MoveStateSnapshot actual = expectVictory
            ? _mercuryTerminalObservation?.Snapshot
                ?? throw new InvalidOperationException("终局没有在原版清理前取得严格快照。")
            : CaptureActual(combat, player, segments[0]);
        foreach (var predicted in predictions)
        {
            bool won = predicted.Terminal is { Outcome: CombatTerminalOutcome.Victory };
            if (predicted.Turn != initialTurn + 1 || actualTurn != predicted.Turn || won != expectVictory
                || (expectVictory ? predicted.Terminal?.PlayerTurn != actualTurn : predicted.Terminal.HasValue))
                throw new InvalidOperationException(
                    $"沙漏 {phase}/{predicted.Name} 回合/终局错误：predicted_turn={predicted.Turn} " +
                    $"actual_turn={actualTurn} start_turn={initialTurn} terminal={predicted.Terminal} expected_won={expectVictory}。");
            AssertSnapshotEqual(predicted.Snapshot, actual, "MercuryReattach", phase + ":" + predicted.Name);
            _completedChecks.Add($"MercuryReattach:{phase}:{predicted.Name}:StrictEndTurn");
        }
        Entry.Logger.Info($"[CombatSolver/Unattended] MERCURY_REATTACH_BOUNDARY phase={phase} " +
            $"started_turn={initialTurn} actual_turn={actualTurn} victory={expectVictory} lanes={predictions.Count}");
        return actualTurn;
    }

    private async Task<int> AdvanceMercuryActualTurnAsync(CombatState combat, Player player, bool expectVictory)
    {
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("沙漏实机推进没有玩家战斗状态。");
        if (playerState.Phase != PlayerTurnPhase.Play || !ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), combat))
            throw new InvalidOperationException("沙漏实机推进必须拥有当前原版 Play 战斗。");
        int previousTurn = playerState.TurnNumber;
        CombatManager.Instance.OnEndedTurnLocally();
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, previousTurn));
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        while (true)
        {
            _mercuryTerminalObservation?.Failure?.Throw();
            EnsureWithinDeadline();
            if (expectVictory && _mercuryTerminalObservation?.Snapshot != null && !CombatManager.Instance.IsInProgress)
            {
                // The prefix freezes combat state, but EndCombatInternal still owns
                // asynchronous rewards/history cleanup. Do not race room teardown.
                if (_mercuryTerminalObservation.CombatEnded)
                    return _mercuryTerminalObservation.Turn;
                await NextFrameAsync();
                continue;
            }
            if (!CombatManager.Instance.IsInProgress)
                throw new InvalidOperationException("沙漏非终局边界意外结束了原版战斗。");
            if (player.PlayerCombatState is { Phase: PlayerTurnPhase.Play } current && current.TurnNumber > previousTurn)
            {
                if (expectVictory)
                    throw new InvalidOperationException("沙漏应全灭的边界仍进入下一玩家 Play 阶段。");
                return current.TurnNumber;
            }
            await NextFrameAsync();
        }
    }
}
