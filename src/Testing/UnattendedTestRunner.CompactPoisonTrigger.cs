using System.Reflection;
using HarmonyLib;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCompactPoisonTriggerAsync(CombatState combat, Player player, bool nonDefaultLifetime = false, bool powerExpressions = false)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        (string Id, int Upgrade)[] input = [("OUTBREAK", 0), ("OUTBREAK", 1), ("OUTBREAK", 0),
            ("DEADLY_POISON", 1), ("HAZE", 1), ("DEFEND_SILENT", 0)];
        if (powerExpressions) input = [.. input.Take(5), ("BUBBLE_BUBBLE", 0), ("BUBBLE_BUBBLE", 1),
            ("MIRAGE", 0), ("MIRAGE", 1), ("MIRAGE", 1)];
        foreach (var card in input)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = card.Id, UpgradeLevels = card.Upgrade, Pile = "Hand" });
        CardModel[] nativeCards = player.PlayerCombatState!.Hand.Cards.ToArray();
        Creature[] enemies = combat.Enemies.ToArray();
        if (enemies.Length != 3) throw new InvalidOperationException("Poison trigger fixture requires three enemies.");
        for (int index = 0; index < enemies.Length; index++)
        {
            if (powerExpressions) await CreatureCmd.SetMaxHp(enemies[index], 40);
            await CreatureCmd.SetCurrentHp(enemies[index], new[] { 10, 40, 18 }[index]);
            if (powerExpressions && enemies[index].CurrentHp != new[] { 10, 40, 18 }[index])
                throw new InvalidOperationException("Power expression fixture did not retain its requested enemy HP.");
            await SetBlockAsync(enemies[index], new[] { 20, 7, 6 }[index]);
        }
        nativeCards[0].DynamicVars.Poison.BaseValue = 0;
        await PowerCmd.Apply<PoisonPower>(new BlockingPlayerChoiceContext(), enemies[0], 1, enemies[1], null);
        await PowerCmd.Apply<PoisonPower>(new BlockingPlayerChoiceContext(), enemies[1], 2, player.Creature, null);
        if (nonDefaultLifetime)
        {
            PoisonPower captured = enemies[0].GetPower<PoisonPower>()!;
            captured.AmountOnTurnStart = 7;
            captured.SkipNextDurationTick = true;
            captured.Target = player.Creature;
        }
        await PowerCmd.Apply<StrengthPower>(new BlockingPlayerChoiceContext(), player.Creature, 50, player.Creature, null);
        await PowerCmd.Apply<WeakPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
        if (powerExpressions)
        {
            nativeCards[9].DynamicVars.CalculationBase.BaseValue = 2;
            nativeCards[9].DynamicVars.CalculationExtra.BaseValue = 3;
            nativeCards[9].DynamicVars.CalculatedBlock.RecalculateForUpgradeOrEnchant();
            await PowerCmd.Apply<DexterityPower>(new BlockingPlayerChoiceContext(), player.Creature, -1, player.Creature, null);
            await PowerCmd.Apply<FrailPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
        }
        SetEnergy(player, 20); SetStars(player, 0);
        await SetBlockAsync(player.Creature, 3);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var display = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        var simulator = root.ForkSimulator();
        CompactDiscardProjection adapter;
        ResumableDiscardProgram.Candidate initial;
        List<(int[] Path, ResumableDiscardProgram.Candidate State, SimulationSnapshot Evaluation)> cases = [];
        List<MoveStateSnapshot> nativeExpected = [];
        List<string[]> nativePowers = [];
        List<ResumableDiscardProgram.Candidate> nativeStates = [];
        List<object> evidence = [];
        int riskSourceCount;
        int[] route = powerExpressions ? [0, 5, 7, 3, 6, 9, 4, 1, 8, 2] : [0, 3, 4, 1, 2];
        int TargetIndex(int index) => index == 3 || powerExpressions && index == 5 ? 0 : powerExpressions && index == 6 ? 1 : -1;
        Creature? Target(int index) => TargetIndex(index) is var target && target >= 0 ? enemies[target] : null;
        using (SimulationNotificationIsolation.Enter())
        {
            adapter = new(simulator, player, includeAttacks: true);
            var lane = adapter.Program;
            initial = lane.Freeze();
            var reader = adapter.CreateReadView();
            riskSourceCount = reader.RiskSourceCount;
            var uncached = adapter.CreateReadView(false);
            var evaluator = new CompactEvaluationDriver(root, display, damage, policy);
            MoveStateSnapshot original = CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState, player, enemies[0]);
            int[][] paths = [[0], [1], [2], [3, 0], [0, 3], [4, 0], [0, 4], [3, 4, 1], [4, 3, 1], [0, 3, 4, 1], [0, 3, 4, 1, 2]];
            if (powerExpressions) paths = [[5], [6], [7], [8], [9], [0, 5], [0, 7], [0, 3, 5, 8],
                [5, 0, 8], [6, 9], [4, 5, 8], [0, 3, 4, 1, 8], [0, 3, 4, 1, 9]];
            foreach (int[] path in paths)
            {
                initial.RestoreInto(lane);
                var mark = lane.State.Mark();
                var oracle = simulator.Fork();
                foreach (int card in path)
                {
                    Play(lane, card);
                    if (!oracle.ManualPlay(oracle.State.FindCard(nativeCards[card])!, Target(card), out _))
                        throw new InvalidOperationException("PoisonTrigger oracle unexpectedly suspended.");
                    Compare(lane, oracle, "branch-" + string.Join('-', path) + "-" + card);
                }
                reader.Read(lane);
                var evaluation = evaluator.Evaluate(reader);
                cases.Add((path, lane.Freeze(), evaluation));
                evidence.Add(new { path, evaluation.PlayerBlock, evaluation.StateKey,
                    removed = lane.Count(ResumableDiscardProgram.Pile.Removed), exhausted = lane.Count(ResumableDiscardProgram.Pile.Exhaust), lane.Energy });
                lane.State.Rollback(mark);
                if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze()))
                    throw new InvalidOperationException("PoisonTrigger rollback retained removal, exhaust or captured X.");
            }
            if (powerExpressions && (cases[2].Evaluation.PlayerBlock != 4 || cases[3].Evaluation.PlayerBlock != 4
                || cases[4].Evaluation.PlayerBlock != 10 || cases[6].Evaluation.PlayerBlock != 3))
                throw new InvalidOperationException("Power expressions missed base/extra, zero or Dexterity/Frail rounding.");
            initial.RestoreInto(lane);
            var continued = simulator.Fork();
            foreach (int card in route)
            {
                Play(lane, card);
                if (!continued.ManualPlay(continued.State.FindCard(nativeCards[card])!, Target(card), out _))
                    throw new InvalidOperationException("Native poison-trigger oracle unexpectedly suspended.");
                Compare(lane, continued, "native-prefix-" + nativeExpected.Count);
                if (lane.CheckWinCondition() != continued.CheckWinCondition(root.StartTurnNumber))
                    throw new InvalidOperationException("Poison terminal safe point differs.");
                Compare(lane, continued, "native-after-safe-point-" + nativeExpected.Count);
                nativeExpected.Add(CaptureSimulated(continued, (SimulatedCombatState)continued.State.CombatState, player, enemies[0]));
                nativePowers.Add(CompactPowerValues(((SimulatedCombatState)continued.State.CombatState).EffectivePowers()));
                nativeStates.Add(lane.Freeze());
            }
            if (powerExpressions && (lane.Block != 69 || lane.Count(ResumableDiscardProgram.Pile.Exhaust) != 1))
                throw new InvalidOperationException("Power expression route retained dead-enemy poison or wrong upgrade exhaustion.");
            if (!lane.Terminal || lane.Count(ResumableDiscardProgram.Pile.Play) != 1)
                throw new InvalidOperationException("Poison route did not exercise the indirect terminal/result-pile gate.");
            if (continued.History.Entries.OfType<CombatPredictionDamageReceivedEntry>().Any(entry => entry.Dealer != null
                || entry.CardSource != null || entry.Result.Props != (ValueProp.Unpowered | ValueProp.Unblockable)
                || entry.Source.Kind != CombatDamageSourceKind.Poison))
                throw new InvalidOperationException("Poison history was attributed to an attack or card.");
            initial.RestoreInto(lane); reader.Read(lane);
            AssertCompactEvaluation(Release(evaluator.Evaluate(simulator)), evaluator.Evaluate(reader));
            foreach (var sample in cases.AsEnumerable().Reverse())
            {
                sample.State.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
            AssertSnapshotEqual(original, CaptureSimulated(simulator, (SimulatedCombatState)simulator.State.CombatState,
                player, enemies[0]), "CompactPoisonTrigger", "RootUnchanged");

            void Compare(ResumableDiscardProgram values, CombatPredictionSimulator oracle, string stage)
            {
                var projection = adapter.Materialize(values);
                adapter.AssertValues(values, oracle); adapter.AssertValues(values, projection);
                foreach (Creature enemy in enemies)
                    AssertSnapshotEqual(CaptureSimulated(oracle, (SimulatedCombatState)oracle.State.CombatState, player, enemy),
                        CaptureSimulated(projection, (SimulatedCombatState)projection.State.CombatState, player, enemy), "CompactPoisonTrigger", stage);
                if (!CompactPowerValues(((SimulatedCombatState)oracle.State.CombatState).EffectivePowers())
                    .SequenceEqual(CompactPowerValues(((SimulatedCombatState)projection.State.CombatState).EffectivePowers())))
                    throw new InvalidOperationException("Conditional Power ordering/ownership differs at " + stage);
                if (!CompactHistory(oracle, adapter).SequenceEqual(CompactHistory(projection, adapter)))
                    throw new InvalidOperationException("PoisonTrigger history/source differs at " + stage + "\nExpected:\n"
                        + string.Join('\n', CompactHistory(oracle, adapter)) + "\nActual:\n" + string.Join('\n', CompactHistory(projection, adapter)));
                AssertCompactRngSet(oracle.Rng, projection.Rng);
                var expected = Release(evaluator.Evaluate(oracle));
                AssertCompactEvaluation(expected, Release(evaluator.Evaluate(projection)));
                reader.Read(values); uncached.Read(values);
                try
                {
                    AssertCompactEvaluation(expected, evaluator.Evaluate(reader));
                    AssertCompactEvaluation(expected, evaluator.Evaluate(uncached));
                }
                catch (InvalidOperationException error) { throw new InvalidOperationException("PoisonTrigger completed read at " + stage, error); }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            var lane = initial.Open();
            var reader = adapter.CreateReadView();
            var evaluator = new CompactEvaluationDriver(root, display, damage, policy);
            foreach (var sample in cases)
            {
                initial.RestoreInto(lane);
                foreach (int card in sample.Path) Play(lane, card);
                if (!lane.State.Freeze().ContentEquals(sample.State.Open().State.Freeze()))
                    throw new InvalidOperationException("Independent poison-trigger worker differs.");
                reader.Read(lane); AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
            foreach (var sample in cases.AsEnumerable().Reverse())
            {
                sample.State.RestoreInto(lane); reader.Read(lane);
                AssertCompactEvaluation(sample.Evaluation, evaluator.Evaluate(reader));
            }
        })));
        MethodInfo endCombat = typeof(CombatManager).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "EndCombatInternal" && method.GetParameters() is [{ ParameterType.Name: "CombatTurnState" }]);
        PropertyInfo stateProperty = endCombat.GetParameters()[0].ParameterType.GetProperty("State")!;
        MethodInfo prefix = typeof(UnattendedTestRunner).GetMethod(nameof(ObserveMercuryCombatEndPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
        Harmony patch = new("CombatSolver.Testing.CompactPoisonTrigger." + _request.RunId);
        MercuryTerminalObservation observation = new(this, combat, player, enemies[0], stateProperty, "CompactPoisonTrigger");
        _mercuryTerminalObservation = observation;
        try
        {
            CombatManager.Instance.CombatEnded += observation.ObserveCombatEnded;
            patch.Patch(endCombat, prefix: new HarmonyMethod(prefix));
            for (int index = 0; index < route.Length; index++)
            {
                int card = route[index];
                if (!nativeCards[card].TryManualPlay(Target(card)))
                    throw new InvalidOperationException("Native poison route rejected.");
                await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                bool final = index == route.Length - 1;
                if (final)
                    while (observation.Snapshot == null || !observation.CombatEnded || CombatManager.Instance.IsInProgress)
                    { EnsureWithinDeadline(); observation.Failure?.Throw(); await NextFrameAsync(); }
                observation.Failure?.Throw();
                AssertSnapshotEqual(nativeExpected[index], final ? observation.Snapshot! : CaptureActual(combat, player, enemies[0]),
                    "CompactPoisonTrigger", "Native-" + index);
                if (!final && !nativePowers[index].SequenceEqual(CompactPowerValues(combat.Creatures.SelectMany(creature => creature.Powers))))
                    throw new InvalidOperationException("Native poison Power lifecycle differs.");
                var values = nativeStates[index].Open();
                for (int target = 0; target < enemies.Length; target++)
                {
                    var expected = values.Creature(target + 1);
                    var actual = enemies[target];
                    if (actual.CurrentHp != expected.CurrentHp || actual.Block != expected.Block || actual.MaxHp != expected.MaxHp
                        || combat.Enemies.Contains(actual) != values.CreaturePresent(target + 1))
                        throw new InvalidOperationException("Native poison changed a fixed enemy's HP, block or membership.");
                }
            }
            if (observation.Turn != root.StartTurnNumber) throw new InvalidOperationException("Poison terminal turn differs.");
        }
        finally
        {
            try { patch.Unpatch(endCombat, prefix); }
            finally { CombatManager.Instance.CombatEnded -= observation.ObserveCombatEnded; _mercuryTerminalObservation = null; }
        }
        _completedChecks.Add($"CompactPoisonTrigger:{cases.Count}Branches:NoDealerOrCardSource:UnpoweredUnblockable:NoAttackHistory:RootRetirementReacquisition:OriginalKeys:AllSnapshotProperties:Frozen8Workers:Native{route.Length}Actions:PreTeardownFullState:CombatEnded");
        if (powerExpressions) _completedChecks.Add("CompactPowerExpressions:TargetPresenceAfterRetirementReacquisition:LivingEnemySumAfterDeath:CalculationBaseExtra:ZeroAndFrailRounding:UpgradeExhaustion:Native10Actions");
        if (nonDefaultLifetime) _completedChecks.Add("CompactPowerReacquire:NonDefaultTurnStartAmountSkipTickTarget:NativeFreshInstance:ReverseRestore:Frozen8Workers");
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, powerExpressions ? "compact-power-expressions.json" : "compact-card-poison-trigger.json"), JsonSerializer.Serialize(
                new { branches = evidence, riskSourceCount,
                    nativeRoute = route.Select(index => new { index, input[index].Id, input[index].Upgrade }), nativeSteps = route.Length,
                    nativePowers, nonDefaultLifetime, powerExpressions, zeroBaseOutbreak = 0, productionBackendEnabled = false }, new JsonSerializerOptions { WriteIndented = true }));
        }

        void Play(ResumableDiscardProgram lane, int card)
        {
            lane.Begin(adapter.IndexOf(nativeCards[card]), TargetIndex(card) is var target && target >= 0 ? target + 1 : -1); lane.Run();
            if (!lane.Complete) throw new InvalidOperationException("PoisonTrigger compact command unexpectedly suspended.");
        }
    }
}
