using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertRoundPrefixReplayBoundaries(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        foreach (string kind in new[] { "tools", "tyranny", "nested", "mayhem", "extra", "shuffle", "terminal" })
        {
            CombatBeamSolver driver = new(root, names, damage, policy);
            CombatPredictionSimulator simulator = root.ForkSimulator();
            SimulatedCombatState shadow = (SimulatedCombatState)simulator.State.CombatState;
            foreach (PowerModel power in shadow.EffectivePowers().ToArray())
                shadow.SetPowerAmount(power, 0);
            simulator.RemoveFromCombat(simulator.State.GetPlayerCombatState(player).AllCards.ToArray());
            foreach (var enemy in shadow.Enemies)
                simulator.State.GetCreature(enemy).CurrentHp = 1000;
            simulator.State.GetCreature(player.Creature).CurrentHp = 500;
            if (kind is "tools" or "nested" or "extra" or "shuffle" or "terminal")
                shadow.SetAmount<ToolsOfTheTradePower>(player.Creature, 1);
            if (kind is "tyranny" or "nested")
                shadow.SetAmount<TyrannyPower>(player.Creature, 1);
            if (kind == "mayhem")
                shadow.SetAmount<MayhemPower>(player.Creature, 1);
            if (kind == "extra")
                shadow.SetAmount<AmbergrisPower>(player.Creature, 1);
            if (kind == "terminal")
            {
                foreach (var enemy in shadow.Enemies)
                    shadow.SetAmount<PoisonPower>(enemy, 2000);
            }
            for (int index = 0; index < 3; index++)
                AddCard(ModelDb.Card<DefendIronclad>(), PileType.Hand, upgraded: index == 1);
            int drawCards = kind == "shuffle" ? 1 : 7;
            for (int index = 0; index < drawCards; index++)
                AddCard(kind == "mayhem" ? ModelDb.Card<Headbutt>() :
                    index % 2 == 0 ? ModelDb.Card<StrikeIronclad>() : ModelDb.Card<DefendIronclad>(),
                    PileType.Draw, upgraded: index == 1);
            if (kind == "shuffle")
                for (int index = 0; index < 5; index++)
                    AddCard(ModelDb.Card<StrikeIronclad>(), PileType.Discard, upgraded: index == 1);
            SimulationSnapshot parent = (SimulationSnapshot)InvokeForcedTerminalMethod(driver, "Snapshot",
                [simulator, root.StartTurnNumber, 0, 0, SearchBoundaryReason.None, new ForkableSet<uint>()])!;
            var original = CaptureSimulated(simulator, shadow, player, combat.Enemies[0]);
            SearchNode node = ForcedTerminalAnnotationNode(parent, null, null);
            List<(PlanAction Action, SimulationSnapshot Snapshot)> baseline = [];
            List<(PlanAction Action, SimulationSnapshot Snapshot)> resumed = [];
            try
            {
                var before = RoundPrefixCounters(driver);
                baseline.AddRange(Branches(driver, node, reuse: false));
                var middle = RoundPrefixCounters(driver);
                resumed.AddRange(Branches(driver, node, reuse: true));
                var after = RoundPrefixCounters(driver);
                if (baseline.Count == 0 || baseline.Count != resumed.Count)
                    throw new InvalidOperationException($"Round prefix {kind}: different branch counts.");
                if (kind != "terminal" && (after.Captures - middle.Captures != 1 || after.Resumes == middle.Resumes))
                    throw new InvalidOperationException($"Round prefix {kind}: checkpoint was not exercised.");
                if (kind == "terminal" && after.Captures != middle.Captures)
                    throw new InvalidOperationException("A terminal enemy phase captured a player-start checkpoint.");
                if (middle.Forks - before.Forks != after.Forks - middle.Forks
                    || middle.Transitions - before.Transitions != after.Transitions - middle.Transitions
                    || middle.Choices - before.Choices != after.Choices - middle.Choices)
                    throw new InvalidOperationException($"Round prefix {kind}: logical work changed.");
                for (int index = 0; index < baseline.Count; index++)
                {
                    var expected = baseline[index];
                    var actual = resumed[index];
                    if (!ActionEquivalent(expected.Action, actual.Action)
                        || expected.Snapshot.StateKey != actual.Snapshot.StateKey
                        || expected.Snapshot.Score != actual.Snapshot.Score
                        || expected.Snapshot.BoundaryReason != actual.Snapshot.BoundaryReason
                        || expected.Snapshot.Turn != actual.Snapshot.Turn
                        || expected.Snapshot.ShufflesCrossed != actual.Snapshot.ShufflesCrossed
                        || !expected.Snapshot.ProcessedEnemyDeaths.SetEquals(actual.Snapshot.ProcessedEnemyDeaths))
                        throw new InvalidOperationException($"Round prefix {kind}/{index}: replay metadata differs.");
                    var expectedState = CaptureSimulated(expected.Snapshot.Simulator,
                        (SimulatedCombatState)expected.Snapshot.Simulator.State.CombatState, player, combat.Enemies[0]);
                    AssertSnapshotEqual(expectedState, CaptureSimulated(actual.Snapshot.Simulator,
                        (SimulatedCombatState)actual.Snapshot.Simulator.State.CombatState, player, combat.Enemies[0]),
                        "RoundPrefixReplay", kind + index);
                    if (!actual.Snapshot.AllEnemiesDead)
                    {
                        var child = actual.Snapshot.Simulator.Fork();
                        AssertSnapshotEqual(expectedState, CaptureSimulated(child,
                            (SimulatedCombatState)child.State.CombatState, player, combat.Enemies[0]),
                            "RoundPrefixReplay", kind + index + "Fork");
                        child.State.GetCreature(player.Creature).CurrentHp--;
                        AssertSnapshotEqual(expectedState, CaptureSimulated(actual.Snapshot.Simulator,
                            (SimulatedCombatState)actual.Snapshot.Simulator.State.CombatState, player, combat.Enemies[0]),
                            "RoundPrefixReplay", kind + index + "Isolation");
                    }
                }
                AssertSnapshotEqual(original, CaptureSimulated(simulator, shadow, player, combat.Enemies[0]),
                    "RoundPrefixReplay", kind + "Parent");
                if (kind == "tools")
                {
                    using var partial = Branches(driver, node, reuse: true).GetEnumerator();
                    if (!partial.MoveNext()) throw new InvalidOperationException("Missing partial branch.");
                    partial.Current.Snapshot.ReleaseSimulator();
                    object context = PrefixContext(partial);
                    partial.Dispose();
                    AssertReleased(context);

                    using CancellationTokenSource cancellation = new();
                    CombatBeamSolver cancelDriver = new(root, names, damage, policy, cancellation.Token);
                    using var interrupted = Branches(cancelDriver, node, reuse: true).GetEnumerator();
                    if (!interrupted.MoveNext()) throw new InvalidOperationException("Missing cancellation branch.");
                    interrupted.Current.Snapshot.ReleaseSimulator();
                    object interruptedContext = PrefixContext(interrupted);
                    cancellation.Cancel();
                    bool canceled = false;
                    try { interrupted.MoveNext(); }
                    catch (OperationCanceledException) { canceled = true; }
                    if (!canceled) throw new InvalidOperationException("Choice continuation ignored cancellation.");
                    AssertReleased(interruptedContext);
                    using var subsequent = Branches(driver, node, reuse: true).GetEnumerator();
                    if (!subsequent.MoveNext()) throw new InvalidOperationException("Later iterator could not reuse the root.");
                    subsequent.Current.Snapshot.ReleaseSimulator();
                }
            }
            finally
            {
                foreach (var item in baseline) item.Snapshot.ReleaseSimulator();
                foreach (var item in resumed) item.Snapshot.ReleaseSimulator();
                parent.ReleaseSimulator();
            }

            void AddCard(CardModel model, PileType pile, bool upgraded)
            {
                PredictedCard card = PredictedCard.Create(model, player);
                if (upgraded) card.MutablePreview.UpgradeInternal();
                simulator.AddGeneratedCardToCombat(card, pile, player, resultKind: CardGenerationResultKind.Fixed);
            }
        }
        AssertRoundPrefixCursorBoundary(root.ForkSimulator());
        if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new InvalidOperationException("Round-prefix contract changed live state.");

        static IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)> Branches(
            CombatBeamSolver driver, SearchNode node, bool reuse)
            => (IEnumerable<(PlanAction, SimulationSnapshot)>)InvokeForcedTerminalMethod(
                driver, "BuildEndTurnBranches", [node, Array.Empty<PlanCardChoice>(), reuse])!;
        static object PrefixContext(object iterator)
            => iterator.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(field => field.FieldType.Name == "RoundPrefixReplayContext").GetValue(iterator)
                ?? throw new InvalidOperationException("EndTurn iterator did not own its prefix context.");
        static void AssertReleased(object context)
        {
            object? Read(string field) => context.GetType().GetField(field,
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context);
            if (Read("_disposed") is not true || Read("_checkpoint") != null || Read("_processedEnemyDeaths") != null)
                throw new InvalidOperationException("EndTurn iterator retained its checkpoint after exit.");
        }
    }

    private async Task AssertRoundPrefixNativeAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray())
            await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        var enemy = combat.Enemies.Single();
        await CreatureCmd.SetMaxHp(enemy, 1000);
        await CreatureCmd.SetCurrentHp(enemy, 1000);
        bool mayhem = _request.ScenarioId == "P5-ROUND-PREFIX-NATIVE-MAYHEM";
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = mayhem ? "MAYHEM_POWER" : "TOOLS_OF_THE_TRADE_POWER", Target = "Player", Amount = 1 });
        foreach (string card in new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = card, Pile = "Hand" });
        for (int index = 0; index < 7; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = mayhem ? "HEADBUTT" : index % 2 == 0 ? "STRIKE_IRONCLAD" : "DEFEND_IRONCLAD", Pile = "Draw" });
        if (_request.ScenarioId == "P5-ROUND-PREFIX-PARALLEL")
        {
            await AssertSearchPolicySnapshotAsync(combat);
            return;
        }
        int turn = player.PlayerCombatState!.TurnNumber;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        SimulationSnapshot initial = InvokeForcedTerminalReplay(driver, [], null, 0, null);
        SimulationSnapshot? selected = null;
        try
        {
            var branches = (IEnumerable<(PlanAction Action, SimulationSnapshot Snapshot)>)InvokeForcedTerminalMethod(
                driver, "BuildEndTurnBranches", [ForcedTerminalAnnotationNode(initial, null, null), Array.Empty<PlanCardChoice>(), true])!;
            PlanAction action;
            using (var iterator = branches.GetEnumerator())
            {
                if (!iterator.MoveNext()) throw new InvalidOperationException("Native round-prefix fixture has no branch.");
                (action, selected) = iterator.Current;
            }
            if (RoundPrefixCounters(driver) is not { Captures: 1, Resumes: > 0 }
                || action.TurnStartChoices is not { Count: > 0 })
                throw new InvalidOperationException("Native fixture did not restore a choice from its checkpoint.");
            var expected = CaptureSimulated(selected.Simulator,
                (SimulatedCombatState)selected.Simulator.State.CombatState, player, enemy);
            PlannedCardSelector selector = new(action.TurnStartChoices);
            selector.CaptureBefore(player);
            using (CardSelectCmd.PushSelector(selector))
            {
                CombatManager.Instance.OnEndedTurnLocally();
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, turn));
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
                {
                    EnsureWithinDeadline();
                    if (!CombatManager.Instance.IsInProgress)
                        throw new InvalidOperationException("Native round-prefix fixture ended combat unexpectedly.");
                    await NextFrameAsync();
                }
            }
            selector.ReconcileImplicitChoices(player);
            selector.AssertConsumed();
            AssertSnapshotEqual(expected, CaptureActual(combat, player, enemy), "RoundPrefixNative", mayhem ? "Mayhem" : "Tools");
            _completedChecks.Add("RoundPrefix:NativeFullState:T1ToT2:RestoredCheckpoint");
        }
        finally { selected?.ReleaseSimulator(); initial.ReleaseSimulator(); }
    }

    private static (int Captures, int Resumes, int Forks, int Transitions, int Choices)
        RoundPrefixCounters(CombatBeamSolver driver)
    {
        object run = typeof(CombatBeamSolver).GetField("_run", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(driver)!;
        int Read(string field) => (int)run.GetType().GetField(field)!.GetValue(run)!;
        return (Read("RoundPrefixCaptures"), Read("RoundPrefixResumes"), Read("ForkCount"), Read("TransitionCount"), Read("ChoiceReplayAttempts"));
    }

    private static void AssertRoundPrefixCursorBoundary(CombatPredictionSimulator simulator)
    {
        var combat = (SimulatedCombatState)simulator.State.CombatState;
        TurnStartChoiceCursor cursor = combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        combat.SetActionChoiceTiming(PlanChoiceTiming.EnemyTurn);
        try
        {
            using (cursor.BeforeNextTake(() => true))
            {
                bool rejected = false;
                try { combat.ForkCompletedRoundPrefix(simulator, cursor); }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Checkpoint detached a cursor with an active callback.");
            }
            simulator.ActionRelicTriggers = new ActionRelicTriggerRecorder();
            bool rejectedRecorder = false;
            try { combat.ForkCompletedRoundPrefix(simulator, cursor); }
            catch (InvalidOperationException) { rejectedRecorder = true; }
            finally { simulator.ActionRelicTriggers = null; }
            if (!rejectedRecorder) throw new InvalidOperationException("Checkpoint bypassed the ordinary recorder Fork barrier.");
            // This succeeds only if the failed Fork restored the same active cursor.
            var fork = combat.ForkCompletedRoundPrefix(simulator, cursor);
            fork.AssertForkable();
        }
        finally { combat.EndActionChoices(); }
        simulator.AssertForkable();
    }
}
