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
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // A native reduction of the existing Stratagem/Prepared choice-order regression, extended
    // with Sly, a 17-card duplicate-rich shuffle, and both represented relic hooks. This is not
    // the entire archived deck, an exhaustive choice search, or compact round advancement.
    private async Task AssertCompactShufflePowerAsync(CombatState combat, Player player, bool useSurvivor = false)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        foreach (string relic in new[] { "TOUGH_BANDAGES", "THE_ABACUS" })
            await InjectRelicAsync(player, new UnattendedRelicInjection { RelicId = relic });
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            { PowerId = "STRATAGEM_POWER", Target = "Player", Amount = 2 });
        foreach (string id in new[] { useSurvivor ? "SURVIVOR" : "ACROBATICS", "PREPARED", "DEFEND_SILENT", "STRIKE_SILENT" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = id, Pile = "Hand", UpgradeLevels = id == "PREPARED" ? 1 : 0 });
        foreach (string id in useSurvivor ? new[] { "DEFEND_SILENT" } : new[] { "DEFEND_SILENT", "STRIKE_SILENT", "DEFEND_SILENT" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Draw" });
        for (int i = 0; i < 17; i++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = i < 2 ? "BACKFLIP" : i % 2 == 0 ? "DEFEND_SILENT" : "STRIKE_SILENT", Pile = "Discard" });
        FindActualHandCard(player, "PREPARED", 0).GiveSingleTurnSly();
        SetEnergy(player, 6);
        SetStars(player, 0);
        await SetBlockAsync(player.Creature, 0);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        AssertCompactRngVectors();
        List<object> rounds = [];

        for (int round = 0; round < 2; round++)
        {
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            var enemy = combat.Enemies.Single();
            CombatPredictionSimulator rootSimulator = root.ForkSimulator();
            CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
                SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
            var snapshot = typeof(CombatBeamSolver).GetMethod("Snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate<Func<CombatPredictionSimulator, int, int, int, SearchBoundaryReason, IReadOnlySet<uint>, SimulationSnapshot>>(driver);
            var tokens = typeof(CardChoiceSupport).GetMethod("ToTokens", BindingFlags.NonPublic | BindingFlags.Static)!
                .CreateDelegate<Func<IReadOnlyList<PredictedCard>, IReadOnlyList<PredictedCard>, IReadOnlyList<PredictedCard>,
                    Func<CardModel, string>, IReadOnlyList<PlanCardToken>>>();
            var deaths = new ForkableSet<uint>();
            SimulationSnapshot Evaluate(CombatPredictionSimulator simulator)
                => Release(snapshot(simulator, root.StartTurnNumber, 1, simulator.ShuffleEventCount, SearchBoundaryReason.None, deaths));
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            long setupBytes = GC.GetAllocatedBytesForCurrentThread(), setupTime = Stopwatch.GetTimestamp();
            var adapter = new CompactDiscardProjection(rootSimulator, player);
            double rootSetupMicroseconds = Stopwatch.GetElapsedTime(setupTime).TotalMicroseconds;
            long rootSetupAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - setupBytes;
            var lane = adapter.Program;
            var reader = adapter.CreateReadView();
            var uncachedReader = adapter.CreateReadView(false);
            CardModel nativeCard = round == 0 ? FindActualHandCard(player, useSurvivor ? "SURVIVOR" : "ACROBATICS", 0)
                : player.PlayerCombatState!.Hand.Cards.First(c => c is DefendSilent);
            int played = adapter.IndexOf(nativeCard), prepared = adapter.Identity("PREPARED");
            var rootValues = lane.State.Freeze();
            var rootSnapshot = CaptureSimulated(rootSimulator, (SimulatedCombatState)rootSimulator.State.CombatState, player, enemy);
            if (round == 0)
            {
                // Type metadata may already be cached; each new root must still reject its
                // unsupported instance values before any compact command can run.
                var powered = rootSimulator.Fork();
                ((SimulatedCombatState)powered.State.CombatState).SetAmount<DexterityPower>(player.Creature, 1);
                ExpectCompactUnsupported(() => new CompactDiscardProjection(powered, player));
                var discounted = rootSimulator.Fork();
                discounted.State.FindCard(nativeCard)!.MutablePreview.EnergyCost.SetThisTurn(0);
                ExpectCompactUnsupported(() => new CompactDiscardProjection(discounted, player));
            }
            List<CompactCase> cases = [];

            (CombatPredictionSimulator Simulator, PlanCardChoice[] Plans) Oracle(int[][] path)
            {
                var simulator = rootSimulator.Fork();
                var shadow = (SimulatedCombatState)simulator.State.CombatState;
                int index = 0;
                List<PlanCardChoice> plans = [];
                shadow.BeginActionChoices(TurnStartChoiceCursor.ForAutomaticPolicy(request =>
                {
                    CardChoiceSpec spec = request.Spec!;
                    PredictedCard[] selected = path[index++].Select(id => spec.Options.Single(c => ReferenceEquals(c.Original, adapter.Original(id)))).ToArray();
                    PlanCardChoice plan = new(request.Effect, request.SourcePile,
                        tokens(selected, spec.Options, spec.SourceCards, static c => c.Id.Entry), request.SourceId, request.ContextId, request.Timing);
                    plans.Add(plan);
                    return plan;
                }));
                try
                {
                    if (!simulator.ManualPlay(simulator.State.FindCard(nativeCard)!, null, out _))
                        throw new InvalidOperationException("Expanded compact oracle left a pending choice.");
                }
                finally { shadow.EndActionChoices(); }
                if (index != path.Length) throw new InvalidOperationException("Expanded compact choice count differs.");
                return (simulator, plans.ToArray());
            }

            var rootMark = lane.State.Mark();
            lane.Begin(played); lane.Run();
            List<int[]> prefix = [];
            if (round == 0)
            {
                lane.SupplyChoice([prepared]); lane.Run(); prefix.Add([prepared]);
                if (!lane.NeedsChoice || lane.ChoicePile != ResumableDiscardProgram.Pile.Draw || lane.ShuffleCount != 1
                    || lane.ShuffleRng.Counter != rootSimulator.Rng.Shuffle.Counter() + 16)
                    throw new InvalidOperationException("Expanded fixture missed Sly's nested shuffle/Power choice.");
            }
            var pending = lane.Freeze();
            List<int> dirty = [];
            int samples = round == 0 ? 24 : 1;
            for (int sample = 0; sample < samples; sample++)
            {
                var mark = lane.State.Mark();
                List<int[]> path = [.. prefix];
                while (!lane.Complete)
                {
                    int[] options = lane.Cards(lane.ChoicePile);
                    int start = (sample * 7 + path.Count * 3) % options.Length;
                    int[] selected = Enumerable.Range(0, lane.ChoiceCount).Select(i => (start + i) % options.Length)
                        .Order().Select(i => options[i]).ToArray();
                    if (round == 0 && sample == 0)
                        selected = options.Where(id => lane.ChoicePile == ResumableDiscardProgram.Pile.Draw
                            ? adapter.Original(id) is Backflip : adapter.Original(id) is not Backflip).Take(lane.ChoiceCount).ToArray();
                    path.Add(selected); lane.SupplyChoice(selected); lane.Run();
                }
                var (oracle, plans) = Oracle(path.ToArray());
                CombatPredictionSimulator projection = adapter.Materialize(lane);
                adapter.AssertValues(lane, oracle);
                adapter.AssertValues(lane, projection);
                AssertSnapshotEqual(CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy),
                    CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy), "CompactShufflePower", "FullProjection");
                AssertCompactRngSet(oracle.Rng, projection.Rng);
                if (!CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projection, adapter)))
                    throw new InvalidOperationException("Expanded compact history/source ordering differs.");
                SimulationSnapshot expected = Evaluate(oracle);
                AssertCompactEvaluation(expected, Evaluate(projection));
                reader.Read(lane);
                AssertCompactEvaluation(expected, driver.SnapshotFromReadView(reader, root.StartTurnNumber, 1,
                    rootSimulator.ShuffleEventCount + lane.ShuffleCount, SearchBoundaryReason.None, deaths));
                uncachedReader.Read(lane);
                AssertCompactEvaluation(expected, driver.SnapshotFromReadView(uncachedReader, root.StartTurnNumber, 1,
                    rootSimulator.ShuffleEventCount + lane.ShuffleCount, SearchBoundaryReason.None, deaths));
                for (int id = 0; id < adapter.CardCount; id++)
                    if (CombatBeamSolver.CaptureCardStateFingerprintForTesting(oracle.State.FindCard(adapter.Original(id))!)
                        != CombatBeamSolver.CaptureCardStateFingerprintForTesting(rootSimulator.State.FindCard(adapter.Original(id))!))
                        throw new InvalidOperationException("Expanded read view reused changing card metadata.");
                cases.Add(new(path.ToArray(), lane.Freeze(), plans, expected));
                dirty.Add(lane.State.DistinctWrittenSlots(mark));
                lane.State.Rollback(mark);
            }
            lane.State.Rollback(rootMark);
            if (!lane.State.Freeze().ContentEquals(rootValues)) throw new InvalidOperationException("Expanded rollback changed the root or RNG.");
            AssertSnapshotEqual(rootSnapshot, CaptureSimulated(rootSimulator,
                (SimulatedCombatState)rootSimulator.State.CombatState, player, enemy), "CompactShufflePower", "RootUnchanged");

            var measurements = new List<object>();
            void Batch(int mode)
            {
                List<SimulationSnapshot> retained = new(cases.Count);
                if (mode == 0)
                {
                    foreach (CompactCase item in cases)
                    {
                        var simulator = rootSimulator.Fork();
                        var shadow = (SimulatedCombatState)simulator.State.CombatState;
                        shadow.BeginActionChoices(item.Plan);
                        try
                        {
                            if (!simulator.ManualPlay(simulator.State.FindCard(nativeCard)!, null, out _))
                                throw new InvalidOperationException("Expanded benchmark left a pending choice.");
                        }
                        finally { shadow.EndActionChoices(); }
                        retained.Add(Evaluate(simulator));
                    }
                }
                else
                {
                    var batchAdapter = mode == 3 ? new CompactDiscardProjection(rootSimulator, player) : adapter;
                    var batchLane = batchAdapter.Program;
                    var view = batchAdapter.CreateReadView(reuseInvariantFeatures: mode >= 2);
                    var mark = batchLane.State.Mark();
                    try
                    {
                        batchLane.Begin(played); batchLane.Run();
                        foreach (int[] choice in prefix) { batchLane.SupplyChoice(choice); batchLane.Run(); }
                        List<ResumableDiscardProgram.Candidate> handles = new(cases.Count);
                        foreach (CompactCase item in cases)
                        {
                            var branch = batchLane.State.Mark();
                            try
                            {
                                foreach (int[] choice in item.Choices.Skip(prefix.Count)) { batchLane.SupplyChoice(choice); batchLane.Run(); }
                                handles.Add(batchLane.Freeze()); view.Read(batchLane);
                                retained.Add(driver.SnapshotFromReadView(view, root.StartTurnNumber, 1,
                                    rootSimulator.ShuffleEventCount + batchLane.ShuffleCount, SearchBoundaryReason.None, deaths));
                            }
                            finally { batchLane.State.Rollback(branch); }
                        }
                        GC.KeepAlive(handles);
                    }
                    finally { batchLane.State.Rollback(mark); }
                }
                retained.Sort(CompareCompactEvaluation);
                GC.KeepAlive(retained);
            }
            if (round == 0)
            {
                Batch(0); Batch(1); Batch(2); Batch(3);
                for (int sample = 0; sample < 4; sample++)
                    foreach (int mode in sample % 2 == 0 ? new[] { 0, 1, 2, 3 } : new[] { 3, 2, 1, 0 })
                    {
                        EnsureWithinDeadline();
                        long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
                        double cpuStarted = CompactThreadCpu.Milliseconds();
                        const int iterations = 32;
                        for (int i = 0; i < iterations; i++) Batch(mode);
                        measurements.Add(new { sample, mode = mode switch { 0 => "legacy_replay", 1 => "compact_read_view",
                            2 => "compact_cached", _ => "compact_cached_with_admission" }, iterations,
                            leaves = cases.Count, microsecondsPerLeaf = Stopwatch.GetElapsedTime(started).TotalMicroseconds / (iterations * cases.Count),
                            cpuMicrosecondsPerLeaf = (CompactThreadCpu.Milliseconds() - cpuStarted) * 1000 / (iterations * cases.Count),
                            allocatedBytesPerLeaf = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)(iterations * cases.Count) });
                    }
            }
            rounds.Add(new { round = root.StartTurnNumber, card = nativeCard.Id.Entry, cards = adapter.CardCount,
                samples = cases.Count, rootSetupMicroseconds, rootSetupAllocatedBytes, valueSlots = lane.State.Count,
                distinctWrittenSlots = dirty, writes = lane.State.WriteAttempts, valueChanges = lane.State.ValueChanges,
                peakUndoEntries = lane.State.PeakUndoEntries,
                cache = new { reader.Invariants!.EnemyBuilds, reader.Invariants.FocusBuilds,
                    reader.Invariants.StrategyBuilds, cardValues = reader.Invariants.CardValues.Count }, measurements });

            CompactCase native = cases[0];
            CombatPredictionSimulator nativeExpected = adapter.Materialize(native.Candidate.Open());
            isolation.Dispose();
            var workers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                var workspace = pending.Open();
                foreach (int[] choice in native.Choices.Skip(prefix.Count)) { workspace.SupplyChoice(choice); workspace.Run(); }
                return workspace.State.Freeze();
            })));
            foreach (var values in workers)
                if (!values.ContentEquals(native.Candidate.Open().State.Freeze()))
                    throw new InvalidOperationException("Expanded pending shuffle leaked state between workers.");
            var selector = new PlannedCardSelector(native.Plan);
            using (CardSelectCmd.PushSelector(selector))
            {
                if (!nativeCard.TryManualPlay(null)) throw new InvalidOperationException("Expanded native card was not playable.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            }
            selector.AssertConsumed();
            AssertSnapshotEqual(CaptureSimulated(nativeExpected, (SimulatedCombatState)nativeExpected.State.CombatState, player, enemy),
                CaptureActual(combat, player, enemy), "CompactShufflePower", $"NativeTurn{root.StartTurnNumber}");

            if (round == 0)
            {
                CardModel backflip = player.PlayerCombatState!.Hand.Cards.First(c => c is Backflip);
                var continued = native.Candidate.Open();
                using (SimulationNotificationIsolation.Enter())
                {
                    continued.Begin(adapter.IndexOf(backflip)); continued.Run();
                    if (!continued.Complete) throw new InvalidOperationException("Native Backflip continuation unexpectedly needs a choice.");
                    var oldContinued = nativeExpected.Fork();
                    if (!oldContinued.ManualPlay(oldContinued.State.FindCard(backflip)!, null, out _))
                        throw new InvalidOperationException("Legacy Backflip continuation left a choice.");
                    nativeExpected = adapter.Materialize(continued);
                    adapter.AssertValues(continued, oldContinued);
                    AssertSnapshotEqual(CaptureSimulated(oldContinued, (SimulatedCombatState)oldContinued.State.CombatState, player, enemy),
                        CaptureSimulated(nativeExpected, (SimulatedCombatState)nativeExpected.State.CombatState, player, enemy), "CompactShufflePower", "BackflipProjection");
                    reader.Read(continued);
                    AssertCompactEvaluation(Release(snapshot(oldContinued, root.StartTurnNumber, 2, oldContinued.ShuffleEventCount,
                            SearchBoundaryReason.None, deaths)),
                        driver.SnapshotFromReadView(reader, root.StartTurnNumber, 2, nativeExpected.ShuffleEventCount, SearchBoundaryReason.None, deaths));
                    if (!CompactHistory(oldContinued, adapter).SequenceEqual(CompactHistory(nativeExpected, adapter)))
                        throw new InvalidOperationException("Backflip continuation history differs.");
                }
                if (!backflip.TryManualPlay(null)) throw new InvalidOperationException("Native Backflip continuation was not playable.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                AssertSnapshotEqual(CaptureSimulated(nativeExpected, (SimulatedCombatState)nativeExpected.State.CombatState, player, enemy),
                    CaptureActual(combat, player, enemy), "CompactShufflePower", "NativeBackflipContinuation");
                // Round lifecycle is still the production engine. This boundary checks that the
                // completed compact projection remains a valid predecessor; it is not counted
                // as compact execution or charged to the compact action speed measurement.
                SimulationSnapshot parent;
                SimulationSnapshot next;
                using (SimulationNotificationIsolation.Enter())
                {
                    parent = snapshot(nativeExpected, root.StartTurnNumber, 2,
                        nativeExpected.ShuffleEventCount, SearchBoundaryReason.None, deaths);
                    next = InvokeForcedTerminalReplay(driver, [new PlanAction(PlanActionKind.EndTurn, root.StartTurnNumber)], parent, root.StartTurnNumber, null);
                }
                parent.ReleaseSimulator();
                try
                {
                    await AdvanceMercuryActualTurnAsync(combat, player, expectVictory: false);
                    AssertSnapshotEqual(CaptureSimulated(next.Simulator, (SimulatedCombatState)next.Simulator.State.CombatState, player, enemy),
                        CaptureActual(combat, player, enemy), "CompactShufflePower", "NativeRoundBoundary");
                }
                finally { next.ReleaseSimulator(); }
            }
        }
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, useSurvivor ? "compact-effect-program.json" : "compact-shuffle-power.json"),
                JsonSerializer.Serialize(new { rounds, compactRoundAdvancement = false, productionBeamEnabled = false },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        _completedChecks.Add("CompactShufflePower:24Branches:AllSnapshotProperties:History:FiveFieldRng:Undo:Frozen8Workers:NativeTwoTurns:LegacyRoundBoundary");
        if (useSurvivor) _completedChecks.Add("CompactEffectProgram:SurvivorBlockThenDiscard:PreparedPartialDrawThenShuffle:BackflipContinuation:Native");
    }

    private static void AssertCompactRngVectors()
    {
        foreach (ulong seed in new[] { 0UL, 1UL, ulong.MaxValue, 0x8000000000000000UL })
        {
            Rng native = new(seed);
            PredictionRngState initial = native.CaptureState();
            ValueRng compact = new(initial.Counter, initial.State0, initial.State1, initial.State2, initial.State3);
            for (int i = 0; i < 128; i++)
            {
                int maximum = (i % 4) switch { 0 => 1, 1 => 2, 2 => 17, _ => int.MaxValue };
                compact = compact.NextInt(maximum, out int value);
                if (value != native.NextInt(maximum)) throw new InvalidOperationException("Compact random output differs.");
                PredictionRngState expected = native.CaptureState();
                if (compact != new ValueRng(expected.Counter, expected.State0, expected.State1, expected.State2, expected.State3))
                    throw new InvalidOperationException("Compact random internal state differs.");
            }
        }
    }

    private static void AssertCompactRngSet(CombatPredictionRngSet expected, CombatPredictionRngSet actual)
    {
        foreach (PropertyInfo property in typeof(CombatPredictionRngSet).GetProperties())
            if (((Rng)property.GetValue(expected)!).CaptureState() != ((Rng)property.GetValue(actual)!).CaptureState())
                throw new InvalidOperationException($"Compact RNG set differs at {property.Name}.");
    }
}
