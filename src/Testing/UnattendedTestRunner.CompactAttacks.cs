using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactAttacksAsync(CombatState combat, Player player)
    {
        if (combat.Enemies.Count != 3 || _mercuryTerminalObservation != null)
            throw new InvalidOperationException("Compact attack fixture requires three primary enemies and exclusive observation.");
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        for (int index = 0; index < 6; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection
                { CardId = "STRIKE_SILENT", Pile = "Hand", UpgradeLevels = index == 2 ? 1 : 0 });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_SILENT", Pile = "Hand" });
        CardModel[] nativeCards = player.PlayerCombatState!.Hand.Cards.ToArray();
        // Explicit native inputs cover a powered zero-damage hit and zero-energy attack start.
        nativeCards[0].DynamicVars.Damage.BaseValue = 0;
        nativeCards[0].EnergyCost._base = 0;
        Creature[] enemies = combat.Enemies.ToArray();
        for (int index = 0; index < enemies.Length; index++)
        {
            await CreatureCmd.SetMaxHp(enemies[index], 30);
            await CreatureCmd.SetCurrentHp(enemies[index], new[] { 8, 6, 4 }[index]);
            await SetBlockAsync(enemies[index], new[] { 6, 0, 2 }[index]);
        }
        SetEnergy(player, 10); SetStars(player, 0);
        await SetBlockAsync(player.Creature, 3);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var simulator = root.ForkSimulator();
        CompactDiscardProjection adapter;
        ResumableDiscardProgram.Candidate initial;
        List<(int Card, int Target, ResumableDiscardProgram.Candidate State, SimulationSnapshot Evaluation)> cases = [];
        List<MoveStateSnapshot> nativeExpected = [];
        List<(int Hp, int MaxHp, int Block, bool Present)[]> nativeCreatures = [];
        List<object> evidence = [];
        (int Card, int Target)[] route = [(0, 3), (1, 1), (2, 1), (3, 2), (4, 3)];
        using (SimulationNotificationIsolation.Enter())
        {
            adapter = new(simulator, player, includeAttacks: true);
            var lane = adapter.Program;
            initial = lane.Freeze();
            var reader = adapter.CreateReadView();
            var evaluator = new CompactEvaluationDriver(root, display, damage, policy);
            MoveStateSnapshot rootState = CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState, player, enemies[0]);
            for (int card = 0; card < 6; card++)
            for (int target = 1; target <= 3; target++)
            {
                initial.RestoreInto(lane);
                var mark = lane.State.Mark();
                int id = adapter.IndexOf(nativeCards[card]);
                lane.Begin(id, target); lane.Run();
                var oracle = simulator.Fork();
                if (!oracle.ManualPlay(oracle.State.FindCard(nativeCards[card])!, enemies[target - 1], out _))
                    throw new InvalidOperationException("Compact attack oracle suspended unexpectedly.");
                Compare(lane, oracle, $"single-{card}-{target}");
                reader.Read(lane);
                SimulationSnapshot evaluation = evaluator.Evaluate(reader);
                cases.Add((id, target, lane.Freeze(), evaluation));
                var result = oracle.History.Entries.OfType<CombatPredictionDamageReceivedEntry>().Last().Result;
                evidence.Add(new { card, target, result.BlockedDamage, result.UnblockedDamage, result.OverkillDamage,
                    result.WasBlockBroken, result.WasFullyBlocked, result.WasTargetKilled, evaluation.StateKey });
                lane.State.Rollback(mark);
                if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze()))
                    throw new InvalidOperationException("Attack rollback changed its root.");
            }
            initial.RestoreInto(lane);
            var continued = simulator.Fork();
            foreach (var step in route)
            {
                lane.Begin(adapter.IndexOf(nativeCards[step.Card]), step.Target); lane.Run();
                if (!continued.ManualPlay(continued.State.FindCard(nativeCards[step.Card])!, enemies[step.Target - 1], out _))
                    throw new InvalidOperationException("Continued attack oracle suspended.");
                Compare(lane, continued, $"route-{nativeExpected.Count}-before-safe-point");
                bool compactTerminal = lane.CheckWinCondition();
                if (compactTerminal != continued.CheckWinCondition(root.StartTurnNumber))
                    throw new InvalidOperationException("Attack terminal boundary differs.");
                Compare(lane, continued, $"route-{nativeExpected.Count}-after-safe-point");
                nativeExpected.Add(CaptureSimulated(continued, (SimulatedCombatState)continued.State.CombatState, player, enemies[0]));
                nativeCreatures.Add(enemies.Select((_, index) =>
                {
                    CreatureVitals value = lane.Creature(index + 1);
                    return (value.CurrentHp, value.MaxHp, value.Block, lane.CreaturePresent(index + 1));
                }).ToArray());
            }
            if (!lane.Terminal || !continued.TerminalStamp.HasValue)
                throw new InvalidOperationException("Attack route did not kill every enemy.");
            var terminal = lane.Freeze();
            ExpectCompactFailure(() => lane.Begin(adapter.IndexOf(nativeCards[5]), 1));
            initial.RestoreInto(lane);
            if (lane.Terminal || !lane.CreaturePresent(1)) throw new InvalidOperationException("Restore retained terminal/death state.");
            terminal.RestoreInto(lane);
            reader.Read(lane);
            AssertCompactEvaluation(Release(evaluator.Evaluate(continued)), evaluator.Evaluate(reader));
            AssertSnapshotEqual(rootState, CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState,
                player, enemies[0]), "CompactAttack", "RootUnchanged");

            void Compare(ResumableDiscardProgram values, CombatPredictionSimulator oracle, string stage)
            {
                CombatPredictionSimulator projection = adapter.Materialize(values);
                try { adapter.AssertValues(values, oracle); adapter.AssertValues(values, projection); }
                catch (InvalidOperationException error) { throw new InvalidOperationException($"Attack values stage={stage}", error); }
                AssertSnapshotEqual(CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemies[0]),
                    CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemies[0]), "CompactAttack", stage);
                string[] expectedHistory = CompactHistory(oracle, adapter).ToArray(), projectedHistory = CompactHistory(projection, adapter).ToArray();
                if (!expectedHistory.SequenceEqual(projectedHistory))
                    throw new InvalidOperationException($"Attack history differs at {stage}:\n" + string.Join("\n", expectedHistory.Zip(projectedHistory).Where(x => x.First != x.Second)));
                AssertCompactRngSet(oracle.Rng, projection.Rng);
                SimulationSnapshot expected = Release(new CompactEvaluationDriver(root, display, damage, policy).Evaluate(oracle));
                AssertCompactEvaluation(expected, Release(evaluator.Evaluate(projection)));
                reader.Read(values);
                try { AssertCompactEvaluation(expected, evaluator.Evaluate(reader)); }
                catch (InvalidOperationException error) { throw new InvalidOperationException($"Attack direct read stage={stage}", error); }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            var lane = initial.Open();
            var reader = adapter.CreateReadView(false);
            var evaluator = new CompactEvaluationDriver(root, display, damage, policy);
            foreach (var sample in cases)
            {
                initial.RestoreInto(lane); lane.Begin(sample.Card, sample.Target); lane.Run();
                if (!lane.State.Freeze().ContentEquals(sample.State.Open().State.Freeze()))
                    throw new InvalidOperationException("Independent attack worker differs.");
                reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
                sample.State.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
        })));
        _completedChecks.Add("CompactAttack:18Branches:FullState:DamageFlagsAndSources:AllSnapshotProperties:OriginalKeys:Undo:Frozen8Workers:PrivateReaderScratch");

        // Observe the final native command before game teardown, then require its real end event.
        MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "EndCombatInternal" && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
        PropertyInfo stateProperty = endCombat.GetParameters()[0].ParameterType.GetProperty("State")!;
        MethodInfo prefix = typeof(UnattendedTestRunner).GetMethod(nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
        Harmony patch = new("CombatSolver.Testing.CompactAttacks." + _request.RunId);
        MercuryTerminalObservation observation = new(this, combat, player, enemies[0], stateProperty, "CompactAttack");
        _mercuryTerminalObservation = observation;
        try
        {
            CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
            patch.Patch(endCombat, prefix: new HarmonyMethod(prefix));
            for (int index = 0; index < route.Length; index++)
            {
                var step = route[index];
                if (!nativeCards[step.Card].TryManualPlay(enemies[step.Target - 1]))
                    throw new InvalidOperationException("Native compact attack route was rejected.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                bool final = index == route.Length - 1;
                if (final)
                    while (observation.Snapshot == null || !observation.CombatEnded || CombatManager.Instance.IsInProgress)
                    { EnsureWithinDeadline(); observation.Failure?.Throw(); await NextFrameAsync(); }
                observation.Failure?.Throw();
                AssertSnapshotEqual(nativeExpected[index], final ? observation.Snapshot! : CaptureActual(combat, player, enemies[0]),
                    "CompactAttack", $"Native{index}");
                for (int creature = 0; creature < enemies.Length; creature++)
                {
                    var expected = nativeCreatures[index][creature];
                    Creature actual = enemies[creature];
                    if (actual.CurrentHp != expected.Hp || actual.MaxHp != expected.MaxHp || actual.Block != expected.Block
                        || combat.Enemies.Contains(actual) != expected.Present)
                        throw new InvalidOperationException("Native attack changed an individual creature or ordered roster unexpectedly.");
                }
            }
            if (observation.Turn != root.StartTurnNumber)
                throw new InvalidOperationException("Native attack terminal turn differs.");
        }
        finally
        {
            try { patch.Unpatch(endCombat, prefix); }
            finally { CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded; _mercuryTerminalObservation = null; }
        }
        _completedChecks.Add("CompactAttack:NativeFiveActions:ZeroDamageZeroCost:FullBlock:PartialAndFinalDeaths:PreTeardownFullState:CombatEnded:TerminalRestore");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-attacks.json"), JsonSerializer.Serialize(
                new { scope = "single-target Strike in primary-enemy domain without enemy Powers; compact native kill chain", branches = evidence, nativeSteps = route.Length },
                new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
