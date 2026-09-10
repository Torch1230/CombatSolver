using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed record CompactCase(int[][] Choices, ResumableDiscardProgram.Candidate Candidate,
        PlanCardChoice[] Plan, SimulationSnapshot Evaluation);

    private async Task AssertCompactKernelAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = "TOUGH_BANDAGES" });
        foreach (string id in new[] { "ACROBATICS", "PREPARED", "STRIKE_SILENT", "DEFEND_SILENT", "DEFEND_SILENT" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = id, Pile = "Hand", UpgradeLevels = id == "PREPARED" ? 1 : 0 });
        for (int i = 0; i < 25; i++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = i % 2 == 0 ? "STRIKE_SILENT" : "DEFEND_SILENT", Pile = "Draw" });
        FindActualHandCard(player, "PREPARED", 0).GiveSingleTurnSly();
        SetEnergy(player, 6);
        SetStars(player, 0);
        await SetBlockAsync(player.Creature, 0);
        var enemy = combat.Enemies.Single();
        await CreatureCmd.SetCurrentHp(enemy, enemy.MaxHp);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatPredictionSimulator rootSimulator = root.ForkSimulator();
        var adapter = new CompactDiscardProjection(rootSimulator, player);
        ResumableDiscardProgram lane = adapter.Program;
        int played = adapter.Identity("ACROBATICS"), prepared = adapter.Identity("PREPARED");
        int turn = root.StartTurnNumber;
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        var snapshotMethod = typeof(CombatBeamSolver).GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var snapshot = snapshotMethod.CreateDelegate<Func<CombatPredictionSimulator, int, int, int, SearchBoundaryReason,
            IReadOnlySet<uint>, SimulationSnapshot>>(driver);
        var emptyDeaths = new ForkableSet<uint>();
        SimulationSnapshot Evaluate(CombatPredictionSimulator sim, int actions = 1)
            => snapshot(sim, turn, actions, 0, SearchBoundaryReason.None, emptyDeaths);
        var tokenMethod = typeof(CardChoiceSupport).GetMethod("ToTokens", BindingFlags.NonPublic | BindingFlags.Static)!;
        var tokens = tokenMethod.CreateDelegate<Func<IReadOnlyList<PredictedCard>, IReadOnlyList<PredictedCard>,
            IReadOnlyList<PredictedCard>, Func<CardModel, string>, IReadOnlyList<PlanCardToken>>>();

        CombatPredictionSimulator Replay(IReadOnlyList<PlanCardChoice> choices)
        {
            CombatPredictionSimulator sim = rootSimulator.Fork();
            var shadow = (SimulatedCombatState)sim.State.CombatState;
            shadow.BeginActionChoices(choices);
            try
            {
                if (!sim.ManualPlay(sim.State.FindCard(adapter.Original(played))!, null, out _))
                    throw new InvalidOperationException("Compact oracle left a pending choice.");
            }
            finally { shadow.EndActionChoices(); }
            return sim;
        }

        (CombatPredictionSimulator Sim, PlanCardChoice[] Plan) Oracle(int[][] path)
        {
            CombatPredictionSimulator sim = rootSimulator.Fork();
            var shadow = (SimulatedCombatState)sim.State.CombatState;
            int choiceIndex = 0;
            List<PlanCardChoice> plans = [];
            var cursor = TurnStartChoiceCursor.ForAutomaticPolicy(request =>
            {
                CardChoiceSpec spec = request.Spec ?? throw new InvalidOperationException("Compact oracle has no choice spec.");
                int[] selected = path[choiceIndex++];
                PredictedCard[] chosen = selected.Select(id => spec.Options.Single(c => ReferenceEquals(c.Original, adapter.Original(id)))).ToArray();
                PlanCardChoice choice = new(request.Effect, request.SourcePile,
                    tokens(chosen, spec.Options, spec.SourceCards, static c => c.Id.Entry),
                    request.SourceId, request.ContextId, request.Timing);
                plans.Add(choice);
                return choice;
            });
            shadow.BeginActionChoices(cursor);
            try
            {
                if (!sim.ManualPlay(sim.State.FindCard(adapter.Original(played))!, null, out _))
                    throw new InvalidOperationException("Compact oracle left a pending choice.");
            }
            finally { shadow.EndActionChoices(); }
            if (choiceIndex != path.Length) throw new InvalidOperationException("Compact and legacy choice counts differ.");
            return (sim, plans.ToArray());
        }

        List<CompactCase> cases = [];
        List<int> distinctWrittenSlots = [];
        var rootValues = lane.State.Freeze();
        var rootMark = lane.State.Mark();
        lane.Begin(played);
        lane.Run();
        if (!lane.NeedsChoice || lane.Energy != 5 || lane.EventCount != 5)
            throw new InvalidOperationException("Compact paid/draw prefix did not suspend at its first choice.");
        var outerValues = lane.State.Freeze();
        var outerMark = lane.State.Mark();
        lane.SupplyChoice([prepared]);
        lane.Run();
        if (!lane.NeedsChoice || lane.ChoiceCard != prepared || lane.ChoiceCount != 2)
            throw new InvalidOperationException("Compact Sly card did not suspend inside its own choice.");
        ResumableDiscardProgram.Candidate pending = lane.Freeze();
        var innerMark = lane.State.Mark();
        ExpectCompactFailure(() => lane.State.Rollback(outerMark));
        ExpectCompactFailure(() => pending.Open().State.Rollback(innerMark));
        var cancelled = new CancellationToken(canceled: true);
        int[] firstSelection = lane.Cards(ResumableDiscardProgram.Pile.Hand).Take(2).ToArray();
        lane.SupplyChoice(firstSelection);
        try { lane.Run(cancelled); throw new InvalidOperationException("Compact cancellation was ignored."); }
        catch (OperationCanceledException) { }
        lane.State.Rollback(innerMark);
        ExpectCompactFailure(() => lane.State.Rollback(innerMark));
        lane.State.Rollback(outerMark);
        if (!lane.State.Freeze().Values.SequenceEqual(outerValues.Values))
            throw new InvalidOperationException("Compact nested cancellation changed the suspended outer state.");
        int[] invalid = [prepared, prepared];
        ExpectCompactFailure(() => lane.SupplyChoice(invalid));
        if (!lane.State.Freeze().Values.SequenceEqual(outerValues.Values))
            throw new InvalidOperationException("Compact invalid choice mutated its owner.");

        Walk(lane, [], (program, path) =>
        {
            distinctWrittenSlots.Add(program.State.DistinctWrittenSlots(rootMark));
            var (oracle, plan) = Oracle(path);
            CombatPredictionSimulator projection = adapter.Materialize(program);
            adapter.AssertValues(program, oracle);
            adapter.AssertValues(program, projection);
            AssertSnapshotEqual(CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy),
                CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy), "CompactKernel", "FullState");
            SimulationSnapshot expected = Evaluate(oracle), actual = Evaluate(projection);
            AssertCompactEvaluation(expected, actual);
            if (!CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projection, adapter)))
                throw new InvalidOperationException("Compact semantic history differs from the legacy oracle.");
            expected.ReleaseSimulator();
            actual.ReleaseSimulator();
            cases.Add(new(path, program.Freeze(), plan, actual));
        });
        lane.State.Rollback(rootMark);
        if (!lane.State.Freeze().Values.SequenceEqual(rootValues.Values))
            throw new InvalidOperationException("Compact expansion changed its root.");
        if (cases.Count < 20 || cases.All(c => c.Choices.Length != 2))
            throw new InvalidOperationException("Compact fixture did not expand nested sibling choices.");

        // Frozen pending executions remain usable after their worker rolled all the way back.
        ResumableDiscardProgram[] siblings = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            ResumableDiscardProgram worker = pending.Open();
            worker.SupplyChoice(firstSelection);
            worker.Run();
            return worker;
        })));
        foreach (var worker in siblings)
        {
            if (!worker.Complete || !worker.State.Freeze().Values.SequenceEqual(siblings[0].State.Freeze().Values))
                throw new InvalidOperationException("Compact frozen candidates leaked between workers.");
        }
        foreach (CompactCase item in cases)
            AssertCompactEvaluation(item.Evaluation, Release(Evaluate(adapter.Materialize(item.Candidate.Open()))));

        // A retained completed candidate can start another action after all original lane state
        // has been rolled back. Its event tape and semantic counters survive across actions.
        CompactCase continuationCase = cases.First(c => c.Choices.Length == 1);
        ResumableDiscardProgram continuation = continuationCase.Candidate.Open();
        continuation.Begin(prepared); continuation.Run();
        int[] continuationSelection = continuation.Cards(ResumableDiscardProgram.Pile.Hand).Take(2).ToArray();
        continuation.SupplyChoice(continuationSelection); continuation.Run();
        CombatPredictionSimulator continuedOracle = Replay(continuationCase.Plan);
        var continuedCombat = (SimulatedCombatState)continuedOracle.State.CombatState;
        continuedCombat.BeginActionChoices(TurnStartChoiceCursor.ForAutomaticPolicy(request =>
        {
            CardChoiceSpec spec = request.Spec!;
            PredictedCard[] selected = continuationSelection.Select(id => spec.Options.Single(c => ReferenceEquals(c.Original, adapter.Original(id)))).ToArray();
            return new PlanCardChoice(request.Effect, request.SourcePile,
                tokens(selected, spec.Options, spec.SourceCards, static c => c.Id.Entry), request.SourceId, request.ContextId, request.Timing);
        }));
        try
        {
            if (!continuedOracle.ManualPlay(continuedOracle.State.FindCard(adapter.Original(prepared))!, null, out _))
                throw new InvalidOperationException("Compact continuation oracle left a pending choice.");
        }
        finally { continuedCombat.EndActionChoices(); }
        CombatPredictionSimulator continuedProjection = adapter.Materialize(continuation);
        adapter.AssertValues(continuation, continuedOracle);
        AssertSnapshotEqual(CaptureSimulated(continuedOracle, continuedCombat, player, enemy),
            CaptureSimulated(continuedProjection, (SimulatedCombatState)continuedProjection.State.CombatState, player, enemy), "CompactKernel", "NextAction");
        AssertCompactEvaluation(Release(Evaluate(continuedOracle, 2)), Release(Evaluate(continuedProjection, 2)));
        AssertCompactEvaluation(continuationCase.Evaluation, Release(Evaluate(adapter.Materialize(continuationCase.Candidate.Open()))));

        // Admission failures are explicit and precede any compact execution.
        CombatPredictionSimulator powered = rootSimulator.Fork();
        ((SimulatedCombatState)powered.State.CombatState).SetAmount<DexterityPower>(player.Creature, 1);
        ExpectCompactUnsupported(() => new CompactDiscardProjection(powered, player));
        CombatPredictionSimulator shuffle = rootSimulator.Fork();
        shuffle.AddToPile(shuffle.State.GetPlayerCombatState(player).DrawPile.Cards.ToArray(), MegaCrit.Sts2.Core.Entities.Cards.PileType.Discard);
        ExpectCompactUnsupported(() => new CompactDiscardProjection(shuffle, player));
        CombatPredictionSimulator modified = rootSimulator.Fork();
        modified.State.FindCard(adapter.Original(played))!.MutablePreview.EnergyCost.SetThisTurn(0);
        ExpectCompactUnsupported(() => new CompactDiscardProjection(modified, player));
        ExpectCompactFailure(() => adapter.Materialize(new CompactDiscardProjection(rootSimulator.Fork(), player).Program));

        // Both lanes use every physical leaf. Legacy gets precomputed choices, making it a
        // conservative baseline without charging it for discovering pending choice prefixes.
        var measurements = new List<object>();
        const int iterations = 16;
        long checksum = 0;
        void LegacyBatch()
        {
            List<SimulationSnapshot> retained = new(cases.Count);
            foreach (var item in cases) retained.Add(Evaluate(Replay(item.Plan)));
            retained.Sort(CompareCompactEvaluation);
            foreach (var item in retained) { checksum ^= (long)item.StateKey.First; item.ReleaseSimulator(); }
        }
        void CompactBatch(bool evaluate)
        {
            var mark = lane.State.Mark();
            List<(ResumableDiscardProgram.Candidate Handle, SimulationSnapshot? Evaluation)> retained = new(cases.Count);
            try
            {
                lane.Begin(played); lane.Run();
                Walk(lane, [], (program, _) =>
                {
                    var handle = program.Freeze();
                    SimulationSnapshot? value = evaluate ? Release(Evaluate(adapter.Materialize(program))) : null;
                    retained.Add((handle, value));
                });
                if (evaluate)
                {
                    retained.Sort((a, b) => CompareCompactEvaluation(a.Evaluation!, b.Evaluation!));
                    foreach (var item in retained) checksum ^= (long)item.Evaluation!.StateKey.First;
                }
                else foreach (var item in retained) checksum ^= item.Handle.PayloadBytes;
            }
            finally { lane.State.Rollback(mark); }
        }
        LegacyBatch(); CompactBatch(true); CompactBatch(false);
        for (int sample = 0; sample < 4; sample++)
        {
            foreach (string mode in sample % 2 == 0 ? new[] { "legacy", "compact_full", "compact_kernel" }
                         : new[] { "compact_kernel", "compact_full", "legacy" })
            {
                EnsureWithinDeadline();
                long before = GC.GetAllocatedBytesForCurrentThread();
                double cpuBefore = CompactThreadCpu.Milliseconds();
                var watch = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++)
                    if (mode == "legacy") LegacyBatch(); else CompactBatch(mode == "compact_full");
                watch.Stop();
                double cpuMilliseconds = CompactThreadCpu.Milliseconds() - cpuBefore;
                measurements.Add(new { mode, sample, iterations, leaves = cases.Count,
                    elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                    cpuMilliseconds,
                    allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before });
            }
        }
        var evidence = new { schemaVersion = 1, scope = "closed draw/discard prototype; full fixtures unchanged and not run",
            cards = 30, leaves = cases.Count, candidatesOwnValues = true, fullLegacyEvaluation = true,
            rngPolicy = "all random effects rejected at admission; nine root RNG states preserved by projection",
            payloadBytes = cases[0].Candidate.PayloadBytes,
            distinctWrittenSlots, workspaceSlots = lane.State.Count, lane.EventsExecuted,
            lane.State.WriteAttempts, lane.State.ValueChanges, lane.State.UndoEntriesWritten, lane.State.PeakUndoEntries,
            measurements, checksum };
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-kernel.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }

        CompactCase native = cases.First(c => c.Choices.Length == 2 && c.Choices[0].SequenceEqual(new[] { prepared }));
        CombatPredictionSimulator nativeExpected = adapter.Materialize(native.Candidate.Open());
        var nativeSelector = new PlannedCardSelector(native.Plan);
        using (CardSelectCmd.PushSelector(nativeSelector))
        {
            if (!FindActualHandCard(player, "ACROBATICS", 0).TryManualPlay(null))
                throw new InvalidOperationException("Native compact fixture could not play Acrobatics.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        nativeSelector.AssertConsumed();
        AssertSnapshotEqual(CaptureSimulated(nativeExpected, (SimulatedCombatState)nativeExpected.State.CombatState, player, enemy),
            CaptureActual(combat, player, enemy), "CompactKernel", "NativeNestedPlay");
        _completedChecks.Add($"CompactKernel:{cases.Count}Leaves:FullStateAndEvaluation:NestedUndo:Frozen8Workers:Admission:Native");
    }

    private static SimulationSnapshot Release(SimulationSnapshot snapshot) { snapshot.ReleaseSimulator(); return snapshot; }
    private static int CompareCompactEvaluation(SimulationSnapshot a, SimulationSnapshot b)
    {
        int score = b.Score.CompareTo(a.Score);
        if (score != 0) return score;
        int first = a.StateKey.First.CompareTo(b.StateKey.First);
        return first != 0 ? first : a.StateKey.Second.CompareTo(b.StateKey.Second);
    }

    private static void Walk(ResumableDiscardProgram program, int[][] path,
        Action<ResumableDiscardProgram, int[][]> completed)
    {
        if (program.Complete) { completed(program, path); return; }
        if (!program.NeedsChoice) throw new InvalidOperationException("Compact traversal has no suspended instruction.");
        int[] hand = program.Cards(ResumableDiscardProgram.Pile.Hand);
        int count = program.ChoiceCount;
        foreach (int[] selected in CompactCombinations(hand, count))
        {
            var mark = program.State.Mark();
            try
            {
                program.SupplyChoice(selected); program.Run();
                Walk(program, [.. path, selected], completed);
            }
            finally { program.State.Rollback(mark); }
        }
    }

    private static IEnumerable<int[]> CompactCombinations(int[] hand, int count)
    {
        if (count == 0) { yield return []; yield break; }
        if (count is < 0 or > 2) throw new NotSupportedException("Fixture enumerates choices of at most two instances.");
        for (int first = 0; first < hand.Length; first++)
            if (count == 1) yield return [hand[first]];
            else for (int second = first + 1; second < hand.Length; second++) yield return [hand[first], hand[second]];
    }

    private static void AssertCompactEvaluation(SimulationSnapshot expected, SimulationSnapshot actual)
    {
        foreach (PropertyInfo property in typeof(SimulationSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name is nameof(SimulationSnapshot.Simulator) or nameof(SimulationSnapshot.HasSimulator)) continue;
            object? left = property.GetValue(expected), right = property.GetValue(actual);
            bool equal = left is IReadOnlySet<uint> set && right is IReadOnlySet<uint> other ? set.SetEquals(other)
                : left is IReadOnlyList<PredictionGap> gaps && right is IReadOnlyList<PredictionGap> otherGaps ? gaps.SequenceEqual(otherGaps)
                : Equals(left, right);
            if (!equal) throw new InvalidOperationException($"Compact full evaluation differs at {property.Name}: {left} / {right}.");
        }
    }

    private static IEnumerable<string> CompactHistory(CombatPredictionSimulator simulator, CompactDiscardProjection adapter)
    {
        Dictionary<PredictionTraceFrame, int> frames = [];
        Dictionary<object, int> plays = [];
        int Identity(object play)
        {
            if (!plays.TryGetValue(play, out int identity)) plays.Add(play, identity = plays.Count);
            return identity;
        }
        string Trace(PredictionTraceFrame? trace)
        {
            if (trace == null) return "-";
            string parent = Trace(trace.Parent);
            if (!frames.TryGetValue(trace, out int identity)) frames.Add(trace, identity = frames.Count);
            string source = trace.Source is CardModel card ? adapter.IndexOf(card).ToString() : trace.Source.Id.Entry;
            return $"{parent}/{identity}:{source}:{trace.Invocation.Action}:{trace.Invocation.Method?.Name}";
        }
        string Card(CombatPredictionCardSnapshot card)
            => $"{adapter.IndexOf(card.Original)}:{card.Id}+{card.UpgradeLevel}:{card.Type}";
        foreach (var entry in simulator.History.Entries)
        {
            string value = entry switch
            {
                CombatPredictionCardPlayStartedEntry started => $"start:{Card(started.Card)}:{Identity(started.CardPlay)}:{started.CardPlay.IsAutoPlay}:{started.CardPlay.Resources.EnergySpent}:{started.CardPlay.Resources.EnergyValue}:{started.CardPlay.Resources.StarsSpent}:{started.CardPlay.Resources.StarValue}:{started.CardPlay.ResultPile}:{started.CardPlay.PlayIndex}:{started.CardPlay.PlayCount}",
                CombatPredictionCardPlayFinishedEntry finished => $"finish:{Card(finished.Card)}:{Identity(finished.CardPlay)}:{finished.CardPlay.IsAutoPlay}:{finished.WasEthereal}",
                CombatPredictionCardDrawnEntry drawn => $"draw:{Card(drawn.Card)}:{drawn.FromHandDraw}",
                CombatPredictionCardDrawResolvedEntry resolved => $"draw-resolved:{Card(resolved.Card)}:{resolved.OriginalEntry.Index}",
                CombatPredictionRiskEntry risk => $"risk:{risk.Reason}",
                _ => throw new InvalidOperationException($"Unexpected compact history entry {entry.GetType().Name}.")
            };
            yield return $"{entry.Index}:{Trace(entry.Trace)}:{value}";
        }
    }

    private static void ExpectCompactFailure(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Compact ownership/choice guard did not reject an invalid operation.");
    }
    private static void ExpectCompactUnsupported(Action action)
    {
        try { action(); } catch (NotSupportedException) { return; }
        throw new InvalidOperationException("Compact admission accepted unsupported semantics.");
    }
}
