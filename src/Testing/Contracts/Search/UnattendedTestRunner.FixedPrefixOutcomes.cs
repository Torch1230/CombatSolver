using System.Reflection;
using System.Text.Json.Nodes;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertFixedPrefixPotionPolicyAsync(CombatState combat, Player player)
    {
        await ClearOrderedEffectFixtureAsync(combat, player);
        foreach (var potion in player.Potions.OfType<MegaCrit.Sts2.Core.Models.PotionModel>().ToArray())
            potion.Discard();
        for (int i = 0; i < 2; i++) InjectPotionForTest(player, "BLOCK_POTION");
        var root = CombatRootSnapshot.Capture(combat);
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            PotionPolicy = SolverPotionPolicy.Smart,
            PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Smart, [])
        };
        var first = new PlanAction(PlanActionKind.UsePotion, root.StartTurnNumber, PotionId: "BLOCK_POTION", PotionSlot: 0);
        var second = first with { PotionSlot = 1 };
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("Fixed potion prefix policy failed: " + label);
        }
        var disabled = new CombatBeamSolver(root, names, damage, policy, potionPolicyOverride: SolverPotionPolicy.Disabled);
        Check(!disabled.CanReplayOpeningPrefix([first]), "disabled potion use");
        var zero = new CombatBeamSolver(root, names, damage, policy, maximumPotionUses: 0);
        Check(!zero.CanReplayOpeningPrefix([first]), "zero explicit uses");
        var one = new CombatBeamSolver(root, names, damage, policy, maximumPotionUses: 1);
        Check(one.CanReplayOpeningPrefix([first]), "one permitted use");
        Check(!one.CanReplayOpeningPrefix([first, second]), "total explicit-use quota");
        var later = new CombatBeamSolver(root, names, damage, policy, earliestPotionTurn: root.StartTurnNumber + 1);
        Check(!later.CanReplayOpeningPrefix([first]), "earliest use turn");
        Check(!one.CanReplayOpeningPrefix([first with { TargetCombatId = combat.Enemies[0].CombatId }]), "target identity");
        var protectedPolicy = policy with
        {
            PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Smart,
                [new(0, "BLOCK_POTION", SolverPotionDirective.Disabled)])
        };
        var protectedDriver = new CombatBeamSolver(root, names, damage, protectedPolicy);
        Check(!protectedDriver.CanReplayOpeningPrefix([first]), "protected potion directive");
        var required = new CombatBeamSolver(root, names, damage, policy,
            potionPolicyOverride: SolverPotionPolicy.RequireAtLeastOne, maximumPotionUses: 2);
        Check(required.CanReplayOpeningPrefix([first, second]), "required opening potion pair");
        _completedChecks.Add("FixedPotionPrefix:Disabled:Quota:EarliestTurn:Target:Protect:RequiredPair");
    }

    private async Task AssertFixedPrefixTurnOutcomesAsync(CombatState combat, Player player)
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Fixed-prefix outcomes: " + message);
        }
        static void Reject(Action action, string expectedMessage)
        {
            try { action(); }
            catch (InvalidOperationException error) when (error.Message.Contains(expectedMessage, StringComparison.Ordinal)) { return; }
            throw new InvalidOperationException("Fixed-prefix outcomes: expected rejection " + expectedMessage);
        }

        await ClearPlayerPilesAsync(player);
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 50);
        await InjectCardAsync(combat, player, new UnattendedCardInjection
        {
            CardId = "STRIKE_IRONCLAD", Pile = "Hand", DynamicVars = new() { ["Damage"] = 100 },
        });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "WOUND", Pile = "Draw", Count = 3 });
        SetEnergy(player, 3);
        int firstTurn = player.PlayerCombatState!.TurnNumber;
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, false, null) with
        {
            FixedBudget = true, VerifyIncrementalSearch = true, DetailedDiagnostics = false,
            MaxDegreeOfParallelism = 1, BudgetOverrideMilliseconds = 5000,
            PotionPolicy = SolverPotionPolicy.Disabled,
            PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Disabled, []),
            Profile = SolverSearchProfile.Default with { BeamWidth = 8, MaxExpandedNodes = 100, SoftTimeBudgetMilliseconds = 5000 },
        };
        PlanAction[] turns = Enumerable.Range(firstTurn, 3)
            .Select(turn => new PlanAction(PlanActionKind.EndTurn, turn)).ToArray();
        PlanAction strike = new(PlanActionKind.PlayCard, firstTurn + 3,
            CardId: "STRIKE_IRONCLAD", TargetCombatId: combat.Enemies.Single().CombatId);
        PlanAction defend = new(PlanActionKind.PlayCard, firstTurn + 3, CardId: "DEFEND_IRONCLAD");
        CombatBeamSolver prefixProbe = new(root, names, damage, policy, searchProfile: policy.Profile);
        PlanAction immediateStrike = strike with { Turn = firstTurn };
        Check(prefixProbe.CanReplayOpeningPrefix([immediateStrike, new PlanAction(PlanActionKind.EndTurn, firstTurn)]),
            "opening discovery accepts the prefix truncated at terminal victory");
        SolverResult Solve(IReadOnlyList<PlanAction> prefix, bool reset = false)
            => new CombatBeamSolver(root, names, damage, policy, searchProfile: policy.Profile,
                fixedPrefixActions: prefix, resetFixedPrefixSchedulingBaseline: reset).Solve();
        SolverResult immediateVictory = await Task.Run(() => Solve(
            [immediateStrike, new PlanAction(PlanActionKind.EndTurn, firstTurn)]));
        Check(immediateVictory.Snapshot.AllEnemiesDead
              && immediateVictory.BestNode.Actions.Count == 1
              && immediateVictory.CombatEndedTurn == firstTurn,
            "opening terminal suffix preserves the victory action and combat end turn");

        CombatBeamSolver inspection = new(root, names, damage, policy, searchProfile: policy.Profile);
        SimulationSnapshot seedSnapshot = InvokeForcedTerminalReplay(inspection, [], null, 0, null);
        SearchNode seed = new(null, 0, 0, 0, firstTurn, SearchRouteTraits.None, 0,
            seedSnapshot.Score, seedSnapshot.StateKey, seedSnapshot.HasRisk, seedSnapshot.BoundaryReason,
            false, null, seedSnapshot, CombatProgressState.Capture(seedSnapshot));
        MethodInfo applyPrefix = typeof(CombatBeamSolver).GetMethod("ApplyFixedPrefix",
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [typeof(SearchNode), typeof(IReadOnlyList<PlanAction>), typeof(bool)], null)!;
        MethodInfo annotations = typeof(CombatBeamSolver).GetMethod("BuildRouteAnnotations",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        SearchNode? inspected = null;
        try
        {
            inspected = (SearchNode)applyPrefix.Invoke(inspection, [seed, turns, false])!;
            for (SearchNode? node = inspected; node?.Parent != null; node = node.Parent)
                Check(node.Outcome?.Turn == node.Action!.Turn, "prefix owns outcome before projection");
            SearchNode rawFallback = inspected with
            {
                Outcome = null, FutureSoldHp = inspected.Parent!.FutureSoldHp + 2,
            };
            object projected = annotations.Invoke(inspection, [rawFallback, null, null])!;
            var projectedSold = (IReadOnlyDictionary<int, int>)projected.GetType()
                .GetProperty("SoldHpByTurn")!.GetValue(projected)!;
            Check(projectedSold[firstTurn + 2] == 2 && rawFallback.Outcome == null
                && rawFallback.Score == inspected.Score, "raw fallback projection preserves score and sold delta");
            TurnOutcome existing = inspected.Outcome! with { MaxBlock = 123 };
            SearchNode ranked = inspected with { Outcome = existing };
            object retained = annotations.Invoke(inspection, [ranked, null, null])!;
            var retainedBlock = (IReadOnlyDictionary<int, int>)retained.GetType()
                .GetProperty("MaxBlockByTurn")!.GetValue(retained)!;
            Check(retainedBlock[firstTurn + 2] == 123, "existing comparative annotation remains authoritative");
        }
        finally
        {
            inspected?.Snapshot.ReleaseSimulator();
            seedSnapshot.ReleaseSimulator();
        }
        _completedChecks.Add("FixedPrefixOutcomes:NodeOwnedBeforeProjection:RawFallbackSoldDelta:ExistingOutcomePreserved");

        SolverResult result = await Task.Run(() => Solve(turns));
        Check(result.Snapshot.AllEnemiesDead && result.CombatEndedTurn == firstTurn + 3, "searched tail must win on fourth turn");
        Check(result.TryValidateTurnOutcomes(out _), "completed prefix tables");
        Check(result.HpLostByTurn.ContainsKey(firstTurn + 1) && result.HpLostByTurn[firstTurn + 1] == 0,
            "explicit zero-loss prefix turn");
        Check(result.HpLostByTurn[firstTurn + 2] > 0, "third prefix turn must lose HP");
        Check(result.HpLostByTurn.Values.Sum() == result.Snapshot.CumulativePlayerHpLost, "prefix and tail cumulative loss");
        Check(result.SoldHpByTurn.Values.Sum() == result.FutureSoldHp, "comparative sold HP unchanged");
        SolverResult terminal = await Task.Run(() => Solve([.. turns, strike], reset: true));
        Check(terminal.HpLostByTurn.OrderBy(pair => pair.Key).SequenceEqual(result.HpLostByTurn.OrderBy(pair => pair.Key)),
            "terminal prefix and searched tail losses");
        SolverResult partial = await Task.Run(() => Solve([.. turns, defend]));
        Check(partial.HpLostByTurn.Count == 4 && partial.ActualBlockByTurn[firstTurn + 3] == 5,
            "partial final prefix annotated once after suffix");
        Check(partial.EnergyLeftByTurn[firstTurn + 3] == 1, "partial prefix and suffix energy");
        SolverResult empty = await Task.Run(() => Solve([]));
        Check(empty.HpLostByTurn.ContainsKey(firstTurn), "empty-prefix ordinary search");
        foreach (var (candidate, label) in new[]
                 { (result, "searched-tail"), (terminal, "terminal-prefix"), (partial, "partial-tail"), (empty, "empty-prefix") })
        {
            await Task.Run(() => AssertIndependentPrefixContinuations(root, names, damage, policy, candidate, label));
        }
        _completedChecks.Add("FixedPrefixContinuations:IndependentPrefixOracle:FullStateText:Turn:ForecastOffset:Order");
        SolverResult truncated = await Task.Run(() => Solve(
            [.. turns, strike, new PlanAction(PlanActionKind.EndTurn, firstTurn + 3)]));
        Check(truncated.Snapshot.AllEnemiesDead
              && truncated.BestNode.Actions.Count == terminal.BestNode.Actions.Count
              && truncated.CombatEndedTurn == terminal.CombatEndedTurn,
            "prefix actions after a locked combat outcome are truncated");
        await Task.Run(() => Reject(() => Solve([new PlanAction(PlanActionKind.EndTurn, firstTurn + 1)]), "固定搜索前缀动作无效"));
        await Task.Run(() => Reject(() => Solve([new PlanAction(PlanActionKind.PlayCard, firstTurn,
            CardId: "DEFEND_IRONCLAD", EndsPlayerTurn: true)]), "固定搜索前缀动作无效"));
        Check(ContinuationStamp.CaptureLive(combat).StateText == liveBefore, "search changed live root");
        _completedChecks.Add("FixedPrefixOutcomes:ThreeTurns:ZeroAndPositiveLoss:PartialTail:Terminal:TruncatedSuffix:Empty:InvalidFlag:RootUnchanged");

        byte[] validBytes = SolvedRouteCache.SerializeRoute(result);
        SolverResult copy = SolvedRouteCache.DeserializeRoute(validBytes, root.Forecast);
        Check(copy.HpLostByTurn.OrderBy(pair => pair.Key).SequenceEqual(result.HpLostByTurn.OrderBy(pair => pair.Key)), "cache roundtrip");
        string[] fields = [nameof(SolverResult.HpLostByTurn), nameof(SolverResult.HpRecoveredByTurn),
            nameof(SolverResult.EnemyHpLostByTurn), nameof(SolverResult.SoldHpByTurn),
            nameof(SolverResult.MaxBlockByTurn), nameof(SolverResult.ActualBlockByTurn), nameof(SolverResult.EnergyLeftByTurn)];
        string directory = Path.Combine(Godot.ProjectSettings.GlobalizePath("user://"), "prefix-outcome-checks");
        Directory.CreateDirectory(directory);
        foreach (string field in fields)
        {
            JsonObject invalid = JsonNode.Parse(validBytes)!.AsObject();
            invalid[field]!.AsObject().Remove((firstTurn + 2).ToString());
            string path = Path.Combine(directory, field + ".json");
            string text = invalid.ToJsonString();
            File.WriteAllText(path, text);
            Check(new SolvedRouteCache(path).Read(root.Forecast) == null && File.ReadAllText(path) == text,
                "incomplete disk cache must be a preserved miss: " + field);
            bool rejected = false;
            try { SolvedRouteCache.DeserializeRoute(System.Text.Encoding.UTF8.GetBytes(text), root.Forecast); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "incomplete imported route: " + field);
        }
        Dictionary<int, int> losses = (Dictionary<int, int>)result.HpLostByTurn;
        int thirdLoss = losses[firstTurn + 2];
        losses.Remove(firstTurn + 2);
        try
        {
            Reject(() => result.RequireHpLostForTurn(firstTurn + 2), "不能按零伤害执行");
            Reject(() => SolvedRouteCache.SerializeRoute(result), "路线回合统计不完整");
            Reject(() => result.TryCreateContinuation(result.Continuations[0].ExpectedState, 80, damage, out _),
                "路线回合统计不完整");
        }
        finally { losses[firstTurn + 2] = thirdLoss; }
        _completedChecks.Add("FixedPrefixOutcomes:SevenTables:CacheRoundtrip:InvalidCachePreserved:ImportAndContinuationRejected:MissingLossNotZero");

        for (int turn = firstTurn; turn < firstTurn + 3; turn++)
        {
            LiveEndTurnRiskProjection risk = LiveEndTurnRiskEvaluator.Evaluate(combat, null);
            Check(risk.HpLost == result.RequireHpLostForTurn(turn), $"risk recheck turn {turn}: {risk.HpLost}");
            CombatManager.Instance.OnEndedTurnLocally();
            EndPlayerTurnAction end = new(player, turn);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(end);
            await end.CompletionTask;
            while (player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } current || current.TurnNumber <= turn)
            {
                EnsureWithinDeadline();
                await NextFrameAsync();
            }
            ContinuationStamp actual = ContinuationStamp.CaptureLive(combat);
            CachedContinuation expected = result.Continuations.Single(item => item.StartTurnNumber == turn + 1);
            Check(expected.ExpectedState == actual, $"native continuation turn {turn + 1}: {expected.ExpectedState.DescribeFirstDifference(actual)}");
            Check(result.TryCreateContinuation(actual, player.Creature.CurrentHp,
                BattleDamageTracker.Observe(combat), out SolverResult? continuation), "native route reuse");
            Check(continuation!.RequireHpLostForTurn(turn + 1) == result.RequireHpLostForTurn(turn + 1), "reuse retains prefix outcomes");
            var display = SolverOverlaySnapshot.BattleHpTotalsForDisplay(0, 0, result.HpLostByTurn,
                result.HpRecoveredByTurn, firstTurn);
            Check(display.ProjectedLoss == result.Snapshot.CumulativePlayerHpLost, "display includes prefix loss");
        }
        _completedChecks.Add("FixedPrefixOutcomes:NativeThreeTurnContinuation:LiveEndTurnRiskMatches:ReuseAndDisplayTotals");
        await AssertLongFixedPrefixContinuationsAsync(combat, player);
    }

    // Deliberately retain the old independent-prefix algorithm only in this test oracle.
    // No selected-path capture or intermediate snapshot from Solve is used as the expected value.
    private static void AssertIndependentPrefixContinuations(
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, SolverResult result, string label,
        CancellationToken cancellationToken = default)
    {
        CombatBeamSolver oracle = new(root, names, damage, policy, cancellationToken,
            searchProfile: policy.Profile);
        IReadOnlyList<PlanAction> actions = result.BestNode.Actions;
        List<CachedContinuation> expected = [];
        for (int index = 0; index < actions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanAction action = actions[index];
            if (action.Kind != PlanActionKind.EndTurn && !action.EndsPlayerTurn)
                continue;
            SimulationSnapshot? setup = null;
            SimulationSnapshot? replayed = null;
            try
            {
                if (policy.IncludeTurnSetup)
                    setup = (SimulationSnapshot)InvokeForcedTerminalMethod(oracle, "ReplayTurnSetup",
                        [result.TurnSetupChoices, true])!;
                replayed = InvokeForcedTerminalReplay(oracle, actions.Take(index + 1).ToArray(),
                    setup, root.StartTurnNumber, null);
                if (replayed.PlayerDead || replayed.AllEnemiesDead
                    || replayed.BoundaryReason != SearchBoundaryReason.None
                    || !actions.Skip(index + 1).Any(later => later.Turn == replayed.Turn))
                    continue;
                expected.Add(new CachedContinuation(ContinuationStamp.CapturePredicted(
                    root.PlayerIdentity, replayed.Simulator, replayed.Turn, root.Forecast, root.StartTurnNumber),
                    replayed.Turn, replayed.Turn - root.StartTurnNumber));
            }
            finally
            {
                replayed?.ReleaseSimulator();
                setup?.ReleaseSimulator();
            }
        }
        if (result.Continuations.Count != expected.Count)
            throw new InvalidOperationException($"Fixed-prefix continuations {label}: count "
                + $"expected={expected.Count} actual={result.Continuations.Count}.");
        for (int index = 0; index < expected.Count; index++)
        {
            CachedContinuation reference = expected[index];
            CachedContinuation actual = result.Continuations[index];
            if (reference.StartTurnNumber != actual.StartTurnNumber
                || reference.ForecastOffset != actual.ForecastOffset
                || !string.Equals(reference.ExpectedState.StateText, actual.ExpectedState.StateText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Fixed-prefix continuations {label}[{index}]: "
                    + $"turn={reference.StartTurnNumber}/{actual.StartTurnNumber} "
                    + $"offset={reference.ForecastOffset}/{actual.ForecastOffset} "
                    + reference.ExpectedState.DescribeFirstDifference(actual.ExpectedState));
            }
        }
    }

    private async Task AssertLongFixedPrefixContinuationsAsync(CombatState combat, Player player)
    {
        // Same deterministic hidden-Buffer construction as the existing v0.111 fixture.
        // The fixed prefix reaches the kill directly, so no long search or incremental timing is needed.
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        foreach (int turns in new[] { 4, 8, 17 })
        {
            EnsureWithinDeadline();
            deadline.Token.ThrowIfCancellationRequested();
            await ClearPlayerPilesAsync(player);
            ClearRunDeck((RunState)combat.RunState, player);
            foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
                await PowerCmd.Remove(power);
            await CreatureCmd.SetMaxHp(player.Creature, 999);
            await CreatureCmd.SetCurrentHp(player.Creature, 999);
            await SetBlockAsync(player.Creature, 0);
            await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 6);
            await SetBlockAsync(combat.Enemies.Single(), 0);
            await InjectCardAsync(combat, player, new UnattendedCardInjection
            {
                CardId = "STRIKE_IRONCLAD", Pile = "Hand", TreatAsDeckCard = true,
            });
            await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            {
                PowerId = "BUFFER_POWER", Target = "Player", Amount = 64,
            });
            await InjectPowerAsync(combat, player, new UnattendedPowerInjection
            {
                PowerId = "BUFFER_POWER", Target = "Enemy", TargetIndex = 0, Amount = turns - 1,
            });
            SetEnergy(player, 3);
            string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                SolverSettings.Capture(), combat, false, null) with
            {
                FixedBudget = true, VerifyIncrementalSearch = false, DetailedDiagnostics = false,
                MeasurePhasePerformance = false, MaxDegreeOfParallelism = 1, BudgetOverrideMilliseconds = 5000,
                PotionPolicy = SolverPotionPolicy.Disabled,
                PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Disabled, []),
                Profile = SolverSearchProfile.Default with
                {
                    BeamWidth = 8, MaxExpandedNodes = 100, SoftTimeBudgetMilliseconds = 5000,
                },
            };
            List<PlanAction> prefix = [];
            for (int offset = 0; offset < turns; offset++)
            {
                int turn = root.StartTurnNumber + offset;
                prefix.Add(new PlanAction(PlanActionKind.PlayCard, turn,
                    CardId: "STRIKE_IRONCLAD", TargetCombatId: combat.Enemies.Single().CombatId));
                if (offset + 1 < turns)
                    prefix.Add(new PlanAction(PlanActionKind.EndTurn, turn));
            }
            SolverResult result = await Task.Run(() => new CombatBeamSolver(
                root, names, damage, policy, deadline.Token, searchProfile: policy.Profile,
                fixedPrefixActions: prefix).Solve());
            if (!result.Snapshot.AllEnemiesDead || result.Snapshot.PlayerDead
                || result.Snapshot.CumulativePlayerHpLost != 0
                || result.CombatEndedTurn != root.StartTurnNumber + turns - 1
                || result.BestNode.Actions.Count != prefix.Count
                || result.Continuations.Count != turns - 1 || !result.TryValidateTurnOutcomes(out _))
                throw new InvalidOperationException($"Fixed-prefix continuations N{turns}: incomplete deterministic kill.");
            for (int index = 0; index < prefix.Count; index++)
            {
                PlanAction expected = prefix[index], actual = result.BestNode.Actions[index];
                if (actual.Kind != expected.Kind || actual.Turn != expected.Turn
                    || actual.CardId != expected.CardId || actual.TargetCombatId != expected.TargetCombatId)
                    throw new InvalidOperationException($"Fixed-prefix continuations N{turns}: changed action {index}.");
            }
            for (int index = 0; index < result.Continuations.Count; index++)
            {
                CachedContinuation continuation = result.Continuations[index];
                if (continuation.StartTurnNumber != root.StartTurnNumber + index + 1
                    || continuation.ForecastOffset != index + 1)
                    throw new InvalidOperationException($"Fixed-prefix continuations N{turns}: missing turn {index + 1}.");
            }
            await Task.Run(() => AssertIndependentPrefixContinuations(
                root, names, damage, policy, result, $"N{turns}", deadline.Token));
            if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
                throw new InvalidOperationException($"Fixed-prefix continuations N{turns}: search/oracle changed live root.");
            _completedChecks.Add($"FixedPrefixContinuations:N{turns}:IndependentPrefixOracle:FullStateText:Turn:ForecastOffset:RootUnchanged");
        }
    }
}
