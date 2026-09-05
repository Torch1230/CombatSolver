using System.Runtime;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Saves;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertSearchPolicySnapshotAsync(CombatState combat)
    {
        AssertBeamRankOffensiveProgressTieBreak();
        AssertOrdinaryBeamBoundaryDiversity();
        AssertStrategicEffectContextRequirements();
        AssertPrimaryIncumbentFiltering();
        AssertPrimaryIncumbentEligibility();

        if (Environment.ProcessorCount < 2)
        {
            throw new PlatformNotSupportedException(
                "DOP1/DOP2 搜索等价测试至少需要两个可用逻辑处理器。");
        }

        CombatBeamSolver.VerifyCycleFamilyLayerBudgetPolicyForTesting();
        CombatBeamSolver.VerifyCycleRegionRetentionPolicyForTesting();
        CombatBeamSolver.VerifyCycleExitTicketSettlementPolicyForTesting();
        CombatBeamSolver.VerifyOrderedMutationRetentionPolicyForTesting();
        CombatBeamSolver.VerifyRoutingChoicePortfolioBoundsForTesting();
        CombatBeamSolver.VerifyPotionQuotaReservationPolicyForTesting();
        CombatBeamSolver.VerifyPruneMemoryCheckpointPolicyForTesting();
        AssertAdaptiveFramePressureBaseline();
        await AssertNoGcBudgetTransitionAsync();

        SolverSettingsData originalSettings = SolverSettings.Current;
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot capturedPolicy = SolverController.CaptureSearchPolicy(
            settings,
            combat,
            includeTurnSetup: false,
            theftPolicy: SolverController.ResolveTheftPolicy(combat)) with
        {
            ShortProfile = settings.ShortProfile with
            {
                MaxExpandedNodes = Math.Min(settings.ShortProfile.MaxExpandedNodes, 250),
                SoftTimeBudgetMilliseconds = 120_000,
            },
            ForceShortOnly = true,
            VerifyIncrementalSearch = false,
            DetailedDiagnostics = false,
            MeasurePhasePerformance = false,
            ShortBudgetOverrideMilliseconds = null,
            DeepBudgetOverrideMilliseconds = null,
        };
        if (string.Equals(
                Godot.DisplayServer.GetName(),
                "headless",
                StringComparison.OrdinalIgnoreCase)
            && capturedPolicy.FramePressureSignal.RecoveryEnabled)
        {
            throw new InvalidOperationException("headless 搜索策略没有旁路渲染帧恢复。");
        }
        SolverDisplayNames displayNames = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot battleDamage = BattleDamageTracker.Observe(combat);
        AssertFullRngStateIdentity(combat);
        AssertRequiredPotionAuditSelectionAndTotals();
        CombatRootSnapshot rootSnapshot = CombatRootSnapshot.Capture(combat);
        AssertNarrowBeamRecoveryPolicy(rootSnapshot, capturedPolicy);
        await AssertCanceledSearchWorkRecordedOnceAsync(
            rootSnapshot,
            displayNames,
            battleDamage,
            capturedPolicy);
        await AssertInProgressCanceledExactLayerWorkRecordedOnceAsync(
            rootSnapshot,
            displayNames,
            battleDamage,
            capturedPolicy);

        SearchPolicySnapshot serialPolicy = capturedPolicy with { MaxDegreeOfParallelism = 1 };
        SearchPolicySnapshot parallelPolicy = capturedPolicy with { MaxDegreeOfParallelism = 2 };
        // Parallel first intentionally exercises cold static mirror caches under contention.
        SolverResult parallelResult = await Task.Run(() => CombatSearchCoordinator.Solve(
            rootSnapshot,
            displayNames,
            battleDamage,
            parallelPolicy,
            CancellationToken.None,
            progressCallback: null));
        SolverResult serialResult = await Task.Run(() => CombatSearchCoordinator.Solve(
            rootSnapshot,
            displayNames,
            battleDamage,
            serialPolicy,
            CancellationToken.None,
            progressCallback: null));
        AssertEquivalentSearchResults(serialResult, parallelResult, "DOP1/DOP2");
        if (serialResult.ParallelExpansionWaves != 0
            || serialResult.ParallelExpansionWorkItems != 0
            || serialResult.MaxParallelExpansionConcurrency != 0)
        {
            throw new InvalidOperationException(
                "DOP1 搜索意外记录了并行展开工作。");
        }
        if (parallelResult.ParallelExpansionWaves <= 0
            || parallelResult.ParallelExpansionWorkItems < 2
            || parallelResult.MaxParallelExpansionConcurrency < 2)
        {
            throw new InvalidOperationException(
                $"DOP2 搜索没有形成真实并行展开：" +
                $"waves={parallelResult.ParallelExpansionWaves} " +
                $"work_items={parallelResult.ParallelExpansionWorkItems} " +
                $"max_concurrency={parallelResult.MaxParallelExpansionConcurrency}。");
        }
        if (serialResult.NodeLimitSnapshotsReleased <= 0
            || parallelResult.NodeLimitSnapshotsReleased <= 0)
        {
            throw new InvalidOperationException(
                $"节点上限搜索没有释放被预算丢弃的模拟器快照：" +
                $"dop1={serialResult.NodeLimitSnapshotsReleased}/" +
                $"{serialResult.ExpandedNodes}/{serialResult.BoundaryReason} " +
                $"dop2={parallelResult.NodeLimitSnapshotsReleased}/" +
                $"{parallelResult.ExpandedNodes}/{parallelResult.BoundaryReason}。");
        }

        SolverPotionPolicy changedPotionPolicy = capturedPolicy.PotionPolicy == SolverPotionPolicy.Disabled
            ? SolverPotionPolicy.RequireAtLeastOne
            : SolverPotionPolicy.Disabled;
        try
        {
            SolverSettings.ApplyForTesting(originalSettings with
            {
                PotionPolicy = changedPotionPolicy,
                EnableDetailedDiagnosticLogs = !capturedPolicy.DetailedDiagnostics,
            });
            SolverResult afterMutation = await Task.Run(() => CombatSearchCoordinator.Solve(
                rootSnapshot,
                displayNames,
                battleDamage,
                serialPolicy,
                CancellationToken.None,
                progressCallback: null));
            AssertEquivalentSearchResults(serialResult, afterMutation, "captured policy/global settings mutation");
        }
        finally
        {
            SolverSettings.ApplyForTesting(originalSettings);
        }
    }

    private static void AssertBeamRankOffensiveProgressTieBreak()
    {
        const double tiedScore = 1234.5d;
        double nextScore = Math.BitIncrement(tiedScore);

        static void AssertEarlier(
            (double Score, int OffensiveProgress, int ActionCount) expected,
            (double Score, int OffensiveProgress, int ActionCount) other,
            string failure)
        {
            int forward = CombatBeamSolver.CompareBeamRankOrder(
                expected.Score,
                expected.OffensiveProgress,
                expected.ActionCount,
                other.Score,
                other.OffensiveProgress,
                other.ActionCount);
            int reverse = CombatBeamSolver.CompareBeamRankOrder(
                other.Score,
                other.OffensiveProgress,
                other.ActionCount,
                expected.Score,
                expected.OffensiveProgress,
                expected.ActionCount);
            if (forward >= 0 || reverse <= 0)
                throw new InvalidOperationException(failure);
        }

        AssertEarlier(
            (nextScore, OffensiveProgress: 0, ActionCount: 99),
            (tiedScore, OffensiveProgress: int.MaxValue, ActionCount: 0),
            "Beam 排序在评分不同时错误地让进攻进度覆盖了评分顺序。");
        AssertEarlier(
            (tiedScore, OffensiveProgress: 0, ActionCount: 3),
            (tiedScore, OffensiveProgress: int.MaxValue, ActionCount: 4),
            "Beam 同分时错误地让进攻进度覆盖了较短路线。");
        AssertEarlier(
            (tiedScore, OffensiveProgress: 55, ActionCount: 3),
            (tiedScore, OffensiveProgress: 50, ActionCount: 3),
            "Beam 同分同动作数时没有保留更高的可兑现进攻进度。");

        int exactTie = CombatBeamSolver.CompareBeamRankOrder(
            tiedScore,
            leftOffensiveProgressValue: 55,
            leftActionCount: 3,
            tiedScore,
            rightOffensiveProgressValue: 55,
            rightActionCount: 3);
        if (exactTie != 0)
            throw new InvalidOperationException("Beam 完全相同的排序键没有保持相等。");
    }

    private static void AssertPrimaryIncumbentEligibility()
    {
        static void AssertCandidate(
            SolverPotionPolicy? effectivePolicy,
            bool expected,
            int minimumUses = 0,
            int? maximumUses = null,
            bool completeVictory = true,
            bool satisfiesHardRules = true,
            int explicitUses = 0,
            int deficit = 5,
            int? endedTurn = 3,
            PotionFreePolicyBaseline? baseline = null,
            PrimarySearchIncumbent? initial = null)
        {
            PrimarySearchIncumbent? incumbent = initial;
            bool changed = CombatBeamSolver.TryTightenPrimarySearchIncumbent(
                baseline,
                minimumUses,
                maximumUses,
                completeVictory,
                satisfiesHardRules,
                explicitUses,
                deficit,
                endedTurn,
                ref incumbent,
                effectivePolicy);
            PrimarySearchIncumbent? expectedIncumbent = expected
                ? new PrimarySearchIncumbent(deficit, endedTurn!.Value)
                : initial;
            if (changed != expected || incumbent != expectedIncumbent)
            {
                throw new InvalidOperationException(
                    $"主结果下界资格或更新错误：policy={effectivePolicy} min={minimumUses} " +
                    $"max={maximumUses} complete={completeVictory} hard={satisfiesHardRules} " +
                    $"explicit={explicitUses} loss={deficit} turn={endedTurn} expected={expected}。");
            }
        }

        // The current solver's effective policy is required: a zero-use winner is
        // admissible in the main/Disabled searches, but not in a require-use search.
        AssertCandidate(SolverPotionPolicy.Disabled, expected: true);
        AssertCandidate(SolverPotionPolicy.Smart, expected: true);
        AssertCandidate(SolverPotionPolicy.Disabled, expected: true, maximumUses: 0);
        AssertCandidate(SolverPotionPolicy.RequireAtLeastOne, expected: false);
        AssertCandidate(null, expected: false);
        AssertCandidate(SolverPotionPolicy.Smart, expected: false, satisfiesHardRules: false);
        AssertCandidate(SolverPotionPolicy.Smart, expected: false, completeVictory: false);
        AssertCandidate(SolverPotionPolicy.Smart, expected: false, endedTurn: null);
        AssertCandidate(SolverPotionPolicy.Smart, expected: false, minimumUses: 1);
        AssertCandidate(SolverPotionPolicy.Smart, expected: false, explicitUses: 1);
        if (SolverInterimResultOrdering.IsCompleteVictory(0, true, false, 1)
            || SolverInterimResultOrdering.IsCompleteVictory(1, false, false, 1)
            || SolverInterimResultOrdering.IsCompleteVictory(1, true, true, 1)
            || SolverInterimResultOrdering.IsCompleteVictory(1, true, false, 0)
            || !SolverInterimResultOrdering.IsCompleteVictory(1, true, false, 1))
        {
            throw new InvalidOperationException("主结果下界不能把死亡或未完成候选作为完整获胜凭证。");
        }

        // A better loss wins even at a later turn; exact primary ties remain open
        // for the final ordering's theft, sold-HP and other resource tie-breaks.
        AssertCandidate(SolverPotionPolicy.Smart, expected: true,
            deficit: 4, endedTurn: 99, initial: new PrimarySearchIncumbent(5, 3));
        AssertCandidate(SolverPotionPolicy.Smart, expected: true,
            endedTurn: 2, initial: new PrimarySearchIncumbent(5, 3));
        AssertCandidate(SolverPotionPolicy.Smart, expected: false,
            initial: new PrimarySearchIncumbent(5, 3));
        AssertCandidate(SolverPotionPolicy.Smart, expected: false,
            deficit: 6, endedTurn: 1, initial: new PrimarySearchIncumbent(5, 3));
        AssertCandidate(SolverPotionPolicy.Smart, expected: false,
            endedTurn: 4, initial: new PrimarySearchIncumbent(5, 3));

        PrimarySearchIncumbent bound = new(5, 3);
        if (CombatBeamSolver.ShouldPruneByPrimaryIncumbent(4, 99, bound)
            || CombatBeamSolver.ShouldPruneByPrimaryIncumbent(5, 3, bound)
            || CombatBeamSolver.ShouldPruneByPrimaryIncumbent(5, 2, bound)
            || !CombatBeamSolver.ShouldPruneByPrimaryIncumbent(5, 4, bound)
            || !CombatBeamSolver.ShouldPruneByPrimaryIncumbent(6, 1, bound))
        {
            throw new InvalidOperationException("主结果下界破坏了低累计战损长线或同损同回合候选。");
        }

        // Exercise the existing exact-layer gate in this fixture as well: the
        // extension must not turn unaudited/soft-policy-ineligible potion wins into bounds.
        PotionFreePolicyBaseline audited = new(true, 5, 75, 3);
        AssertCandidate(null, expected: true, minimumUses: 1, maximumUses: 1,
            explicitUses: 1, deficit: 4, endedTurn: 99, baseline: audited);
        AssertCandidate(null, expected: true, minimumUses: 1, maximumUses: 1,
            explicitUses: 1, endedTurn: 2, baseline: audited);
        AssertCandidate(SolverPotionPolicy.Smart, expected: false,
            minimumUses: 1, maximumUses: 1, explicitUses: 1, deficit: 4);
        AssertCandidate(null, expected: false, minimumUses: 1, maximumUses: 2,
            explicitUses: 1, deficit: 4, baseline: audited);
        AssertCandidate(null, expected: false, minimumUses: 1, maximumUses: 1,
            explicitUses: 1, baseline: audited);
        AssertCandidate(null, expected: false, minimumUses: 1, maximumUses: 1,
            explicitUses: 1, endedTurn: 4, baseline: audited);
        AssertCandidate(null, expected: false, minimumUses: 1, maximumUses: 1,
            explicitUses: 1, deficit: 4, satisfiesHardRules: false, baseline: audited);
        AssertCandidate(null, expected: false, minimumUses: 1, maximumUses: 1,
            explicitUses: 1, deficit: 4, completeVictory: false, baseline: audited);
        AssertCandidate(null, expected: false, minimumUses: 1, maximumUses: 1,
            deficit: 4, baseline: audited);
    }

    private sealed record BeamBoundaryTestCandidate(
        int Identity,
        double Score,
        int Actions,
        int OffensiveProgress,
        int Potions = 0,
        bool Victory = false,
        CombatBeamSolver.OrdinaryBeamTacticalValues? Tactical = null);

    private static void AssertOrdinaryBeamBoundaryDiversity()
    {
        static CombatBeamSolver.OrdinaryBeamTacticalValues GetTactical(
            BeamBoundaryTestCandidate candidate)
            => candidate.Tactical ?? throw new InvalidOperationException(
                "普通 Beam 战术排序读取了必保节点或本应旁路的候选。");

        static void AssertSelection(
            IReadOnlyList<BeamBoundaryTestCandidate> pool,
            int[] original,
            int[] required,
            int[] expected,
            bool finalQualityFirst = false,
            bool useTacticalOrder = false)
        {
            List<BeamBoundaryTestCandidate> selected = original
                .Select(identity => pool.Single(candidate => candidate.Identity == identity))
                .ToList();
            List<BeamBoundaryTestCandidate> requiredNodes = required
                .Select(identity => pool.Single(candidate => candidate.Identity == identity))
                .ToList();
            BeamBoundaryTestCandidate[] before = selected.ToArray();
            for (int repetition = 0; repetition < 2; repetition++)
            {
                selected = before.ToList();
                CombatBeamSolver.DiversifyOrdinaryBeamBoundary(
                    pool,
                    selected,
                    requiredNodes,
                    candidate => (
                        candidate.Score,
                        candidate.Actions,
                        candidate.OffensiveProgress,
                        candidate.Potions,
                        candidate.Victory),
                    finalQualityFirst,
                    useTacticalOrder ? GetTactical : null);
                if (!selected.Select(candidate => candidate.Identity).SequenceEqual(expected)
                    || selected.Count != before.Length
                    || selected.Distinct(ReferenceEqualityComparer.Instance).Count() != selected.Count
                    || selected.Any(candidate => !pool.Any(item => ReferenceEquals(item, candidate)))
                    || requiredNodes.Any(candidate =>
                        !ReferenceEquals(selected[Array.IndexOf(before, candidate)], candidate))
                    || !selected.GroupBy(candidate => candidate.Potions)
                        .OrderBy(group => group.Key)
                        .Select(group => (group.Key, Count: group.Count()))
                        .SequenceEqual(before.GroupBy(candidate => candidate.Potions)
                            .OrderBy(group => group.Key)
                            .Select(group => (group.Key, Count: group.Count()))))
                {
                    throw new InvalidOperationException(
                        "普通 Beam 同分截线没有保持预期多样性、引用、药水席数或确定性。");
                }
            }
        }

        BeamBoundaryTestCandidate[] tied = Enumerable.Range(0, 12)
            .Select(index => new BeamBoundaryTestCandidate(
                index, Score: 10, Actions: 2, OffensiveProgress: index < 4 ? 18 : 15))
            .ToArray();
        AssertSelection(tied, [0, 1, 2, 3], [], [0, 4, 1, 5]);
        AssertSelection(tied, [0], [], [0]);
        AssertSelection(tied, [], [], []);
        AssertSelection(tied, [0, 1], [0, 1], [0, 1]);
        AssertSelection(tied, [0, 1, 2, 3, 4], [1], [0, 1, 4, 2, 5]);
        AssertSelection(tied, [0, 1, 2, 3], [], [0, 1, 2, 3], finalQualityFirst: true);
        int[] all = Enumerable.Range(0, tied.Length).ToArray();
        AssertSelection(tied, all, [], all);
        AssertSelection(
            tied.Select(candidate => candidate with { OffensiveProgress = 18 }).ToArray(),
            [0, 1, 2, 3], [], [0, 1, 2, 3]);
        AssertSelection(
            tied.Select(candidate => candidate with { Victory = candidate.Identity == 11 }).ToArray(),
            [0, 1, 2, 3], [], [0, 1, 2, 3]);

        // Required replacement can put a worse node in the middle of selected. Neither it,
        // a higher score, nor a shorter/longer action count belongs to the ordinary tie.
        BeamBoundaryTestCandidate[] primaryBoundaries =
        [
            new(100, Score: Math.BitIncrement(10d), Actions: 99, OffensiveProgress: 0),
            new(300, Score: 10, Actions: 1, OffensiveProgress: 0),
            .. tied,
            new(400, Score: 10, Actions: 3, OffensiveProgress: int.MaxValue),
            new(200, Score: 9, Actions: 1, OffensiveProgress: int.MaxValue),
        ];
        AssertSelection(
            primaryBoundaries,
            [0, 100, 1, 200, 300, 2, 3], [200],
            [0, 100, 4, 200, 300, 1, 5]);
        AssertSelection(
            tied.Select(candidate => candidate with { Score = candidate.Identity < 4 ? 11 : 10 })
                .ToArray(),
            [0, 1, 2, 3], [], [0, 1, 2, 3]);

        BeamBoundaryTestCandidate[] potions = Enumerable.Range(0, 8)
            .Select(index => new BeamBoundaryTestCandidate(
                index, Score: 10, Actions: 2,
                OffensiveProgress: index < 4 ? 18 : 15,
                Potions: index % 2))
            .ToArray();
        AssertSelection(potions, [0, 1, 2, 3], [], [0, 1, 4, 5]);
        AssertSelection(potions, [0, 1], [], [0, 1]);

        // Beam rank is deliberately different from the policy label's score. Only a
        // turn + complete production-label tie may change order within each progress group.
        int[] reachable = [25, 14, 18, 11, 25, 15, 13, 23, 9];
        int[] hand = [5, 5, 5, 5, 5, 5, 6, 6, 6];
        BeamBoundaryTestCandidate[] tacticalTie = Enumerable.Range(0, 9)
            .Select(index => new BeamBoundaryTestCandidate(
                index,
                Score: 1_581_982,
                Actions: 18,
                OffensiveProgress: index < 3 ? 18 : 15,
                Tactical: new CombatBeamSolver.OrdinaryBeamTacticalValues(
                    Turn: 3,
                    PotionCount: 0,
                    PotionStrategicCost: 0,
                    FutureSoldHp: 1,
                    CumulativePlayerHpLost: 1,
                    ActionCount: 18,
                    Score: 801_982,
                    ZeroCostPlayableCount: 1,
                    ReachableHandValue: reachable[index],
                    HandCount: hand[index])))
            .ToArray();
        int[] seven = [0, 1, 2, 3, 4, 5, 6];
        AssertSelection(tacticalTie, seven, [], [0, 3, 1, 4, 2, 5, 6]);
        AssertSelection(tacticalTie, seven, [], [0, 4, 2, 7, 1, 5, 6],
            useTacticalOrder: true);

        // Routing members keep their original progress-group positions, including an
        // unselected tactical extreme. Their presence does not block route-less peers.
        AssertSelection(
            tacticalTie.Select(candidate => candidate with
            {
                Tactical = GetTactical(candidate) with { HasRetainedRoutingChoice = true },
            }).ToArray(),
            seven, [], [0, 3, 1, 4, 2, 5, 6], useTacticalOrder: true);
        AssertSelection(
            tacticalTie.Select(candidate => candidate.Identity is 1 or 4 or 6 or 8
                ? candidate with
                {
                    Tactical = GetTactical(candidate) with
                    {
                        HasRetainedRoutingChoice = true,
                        ZeroCostPlayableCount = 99,
                        ReachableHandValue = 99,
                        HandCount = 99,
                    },
                }
                : candidate).ToArray(),
            seven, [], [0, 7, 1, 4, 2, 5, 6], useTacticalOrder: true);

        // Zero-cost availability outranks reachable value; hand count breaks only its tie.
        AssertSelection(
            tacticalTie.Select(candidate => candidate with
            {
                Tactical = candidate.Identity switch
                {
                    3 => GetTactical(candidate) with
                    {
                        ZeroCostPlayableCount = 2,
                        ReachableHandValue = 0,
                        HandCount = 0,
                    },
                    6 => GetTactical(candidate) with { ReachableHandValue = 25 },
                    _ => GetTactical(candidate),
                },
            }).ToArray(),
            seven, [], [0, 3, 2, 6, 1, 4, 7], useTacticalOrder: true);
        AssertSelection(
            tacticalTie.Select(candidate => candidate with
            {
                Tactical = GetTactical(candidate) with
                {
                    ZeroCostPlayableCount = 1,
                    ReachableHandValue = 1,
                    HandCount = 1,
                },
            }).ToArray(),
            seven, [], [0, 3, 1, 4, 2, 5, 6], useTacticalOrder: true);

        // Unequal cohorts are deliberately interleaved. Even an extreme tactical value
        // cannot move a cohort into another label's slots. Exercise every key component,
        // including an exact policy-score difference smaller than any scoring heuristic.
        Func<CombatBeamSolver.OrdinaryBeamTacticalValues,
            CombatBeamSolver.OrdinaryBeamTacticalValues>[] changePolicy =
        [
            values => values with { Turn = values.Turn + 1 },
            values => values with { PotionCount = values.PotionCount + 1 },
            values => values with { PotionStrategicCost = values.PotionStrategicCost + 1 },
            values => values with { FutureSoldHp = values.FutureSoldHp + 1 },
            values => values with { CumulativePlayerHpLost = values.CumulativePlayerHpLost + 1 },
            values => values with { ActionCount = values.ActionCount + 1 },
            values => values with { Score = Math.BitIncrement(values.Score) },
        ];
        foreach (var change in changePolicy)
        {
            BeamBoundaryTestCandidate[] interleaved = tacticalTie.Select(candidate =>
            {
                CombatBeamSolver.OrdinaryBeamTacticalValues values = GetTactical(candidate);
                if (candidate.Identity is 4 or 6 or 8)
                    values = change(values);
                if (candidate.Identity == 8)
                    values = values with { ReachableHandValue = 999 };
                return candidate with { Tactical = values };
            }).ToArray();
            AssertSelection(interleaved, seven, [], [0, 7, 2, 8, 1, 5, 4],
                useTacticalOrder: true);
        }

        AssertSelection(
            tacticalTie.Select(candidate => candidate.Identity == 3
                ? candidate with { Tactical = null }
                : candidate).ToArray(),
            seven, [3], [0, 4, 2, 3, 7, 1, 5], useTacticalOrder: true);
        AssertSelection(
            potions.Select(candidate => candidate with
            {
                Tactical = new CombatBeamSolver.OrdinaryBeamTacticalValues(
                    Turn: 1,
                    PotionCount: candidate.Potions,
                    PotionStrategicCost: 0,
                    FutureSoldHp: 0,
                    CumulativePlayerHpLost: 0,
                    ActionCount: candidate.Actions,
                    Score: candidate.Score,
                    ZeroCostPlayableCount: 1,
                    ReachableHandValue: candidate.Identity,
                    HandCount: 1),
            }).ToArray(),
            [0, 1, 2, 3], [], [2, 3, 6, 7], useTacticalOrder: true);

        // Missing tactical payloads make the callback fail if any existing bypass starts
        // consulting the new descriptor, including required-only and fully retained pools.
        BeamBoundaryTestCandidate[] bypass = tacticalTie
            .Select(candidate => candidate with { Tactical = null }).ToArray();
        AssertSelection(bypass, seven, [], seven,
            finalQualityFirst: true, useTacticalOrder: true);
        AssertSelection(bypass, [0], [], [0], useTacticalOrder: true);
        AssertSelection(bypass, [], [], [], useTacticalOrder: true);
        AssertSelection(bypass, seven, seven, seven, useTacticalOrder: true);
        int[] allTactical = Enumerable.Range(0, bypass.Length).ToArray();
        AssertSelection(bypass, allTactical, [], allTactical, useTacticalOrder: true);
        AssertSelection(
            bypass.Select(candidate => candidate with { OffensiveProgress = 15 }).ToArray(),
            seven, [], seven, useTacticalOrder: true);
        AssertSelection(
            bypass.Select(candidate => candidate with { Victory = candidate.Identity == 8 })
                .ToArray(),
            seven, [], seven, useTacticalOrder: true);
        AssertSelection(
            bypass.Select(candidate => candidate with { Score = candidate.Identity < 7 ? 11 : 10 })
                .ToArray(),
            seven, [], seven, useTacticalOrder: true);
    }

    private static async Task AssertCanceledSearchWorkRecordedOnceAsync(
        CombatRootSnapshot rootSnapshot,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot capturedPolicy)
    {
        SearchRequestWorkTotals requestWorkTotals = new();
        SearchPolicySnapshot canceledPolicy = capturedPolicy with
        {
            MaxDegreeOfParallelism = 1,
            RequestWorkTotals = requestWorkTotals,
        };
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        try
        {
            await Task.Run(() => new CombatBeamSolver(
                rootSnapshot,
                displayNames,
                battleDamage,
                canceledPolicy,
                cancellation.Token).Solve());
            throw new InvalidOperationException("预取消搜索没有抛出取消异常。");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token)
        {
        }

        SearchRequestWorkSnapshot totals = requestWorkTotals.Snapshot();
        if (requestWorkTotals.RecordedSolverCountForTesting != 1
            || totals.ExpandedNodes != 0
            || totals.TransitionCount != 0
            || totals.ChoiceBranchesEvaluated != 0)
        {
            throw new InvalidOperationException(
                $"取消搜索的请求工作量没有精确记录一次：" +
                $"records={requestWorkTotals.RecordedSolverCountForTesting} " +
                $"expanded={totals.ExpandedNodes} transitions={totals.TransitionCount} " +
                $"choices={totals.ChoiceBranchesEvaluated}。");
        }
    }

    private static void AssertAdaptiveFramePressureBaseline()
    {
        SearchFramePressureSignal signal = new();
        for (int index = 0; index < 31; index++)
            signal.ObserveFrame(50d, searchActive: false);
        signal.ResetPressure();
        if (signal.BaselineSampleCount != 31
            || Math.Abs(signal.BaselineFrameGapMilliseconds - 50d) > 0.001d
            || Math.Abs(signal.PressureFrameGapMilliseconds - 75d) > 0.001d)
        {
            throw new InvalidOperationException(
                $"低帧率基线没有形成相对帧压力阈值：" +
                $"samples={signal.BaselineSampleCount} " +
                $"baseline={signal.BaselineFrameGapMilliseconds:F3} " +
                $"threshold={signal.PressureFrameGapMilliseconds:F3}。");
        }
        signal.ObserveFrame(50d, searchActive: true);
        if (signal.PressureEpochForTesting != 0)
            throw new InvalidOperationException("稳定的低帧率基线被误判为搜索帧压力。");
        signal.ObserveFrame(75d, searchActive: true);
        if (signal.PressureEpochForTesting != 1)
            throw new InvalidOperationException("相对基线明显退化的帧没有触发搜索帧压力。");

        int backgroundObservedEpoch = 0;
        signal.ObserveFrame(
            500d,
            searchActive: true,
            frameRecoveryAllowed: false);
        if (signal.FrameRecoveryAllowed
            || signal.PressureEpochForTesting != 1
            || signal.WaitForRecovery(ref backgroundObservedEpoch)
            || backgroundObservedEpoch != 1)
        {
            throw new InvalidOperationException(
                "窗口失焦后的后台帧仍触发或继承了搜索帧恢复等待。");
        }
        signal.ObserveFrame(
            50d,
            searchActive: true,
            frameRecoveryAllowed: true);
        if (!signal.FrameRecoveryAllowed
            || signal.WaitForRecovery(ref backgroundObservedEpoch))
        {
            throw new InvalidOperationException(
                "窗口重新聚焦后仍继承失焦前的过期帧压力。");
        }

        SearchFramePressureSignal sixtyFpsSignal = new();
        for (int index = 0; index < 31; index++)
            sixtyFpsSignal.ObserveFrame(1000d / 60d, searchActive: false);
        sixtyFpsSignal.ResetPressure();
        if (Math.Abs(sixtyFpsSignal.PressureFrameGapMilliseconds - 33d) > 0.001d)
        {
            throw new InvalidOperationException(
                $"60 FPS 基线没有保留 33ms 的绝对响应下限：" +
                $"threshold={sixtyFpsSignal.PressureFrameGapMilliseconds:F3}。");
        }

        SearchFramePressureSignal sparseSignal = new();
        sparseSignal.ObserveFrame(200d, searchActive: false);
        sparseSignal.ResetPressure();
        if (Math.Abs(sparseSignal.BaselineFrameGapMilliseconds - (1000d / 60d)) > 0.001d
            || Math.Abs(sparseSignal.PressureFrameGapMilliseconds - 33d) > 0.001d)
        {
            throw new InvalidOperationException(
                $"稀疏加载帧错误抬高了帧压力阈值：" +
                $"baseline={sparseSignal.BaselineFrameGapMilliseconds:F3} " +
                $"threshold={sparseSignal.PressureFrameGapMilliseconds:F3}。");
        }

        SearchFramePressureSignal disabledSignal = new();
        for (int index = 0; index < 31; index++)
            disabledSignal.ObserveFrame(50d, searchActive: false);
        disabledSignal.ResetPressure(recoveryEnabled: false);
        disabledSignal.ObserveFrame(500d, searchActive: true);
        int disabledObservedEpoch = 0;
        if (disabledSignal.RecoveryEnabled
            || disabledSignal.PressureEpochForTesting != 0
            || disabledSignal.WaitForRecovery(ref disabledObservedEpoch))
        {
            throw new InvalidOperationException("无显示服务的搜索仍触发了帧恢复等待。");
        }

        signal.ResetPressure();
        if (signal.PressureEpochForTesting != 0)
            throw new InvalidOperationException("新搜索继承了上一轮帧压力 epoch。");

        SearchProgressDisplayState progressDisplay = new(startedAtTick: 10_000);
        SolverProgress sameProgress = new(
            StartTurnNumber: 1,
            CurrentTurnNumber: 1,
            CompletedTurnLayers: 0,
            PlayDepth: 0,
            ExpandedNodes: 7,
            ReviewedWorldlines: 37,
            MaxNodes: 100,
            FrontierNodes: 0,
            EndedNodes: 0,
            ElapsedMilliseconds: 100,
            Phase: "test");
        if (progressDisplay.TryCreate(sameProgress, 10_199, out _)
            || !progressDisplay.TryCreate(sameProgress, 10_200, out SolverProgress firstDisplay)
            || progressDisplay.TryCreate(sameProgress, 10_399, out _)
            || !progressDisplay.TryCreate(sameProgress, 10_400, out SolverProgress secondDisplay)
            || firstDisplay.ElapsedMilliseconds != 200
            || secondDisplay.ElapsedMilliseconds != 400
            || secondDisplay.ExpandedNodes != sameProgress.ExpandedNodes
            || secondDisplay.ReviewedWorldlines != sameProgress.ReviewedWorldlines
            || !string.Equals(secondDisplay.Phase, sameProgress.Phase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "搜索计时器没有在复用同一进度对象时按 200ms 间隔保持单调。");
        }

        progressDisplay.Restart(20_000);
        SolverProgress publishedAhead = sameProgress with { ElapsedMilliseconds = 4_000 };
        if (!progressDisplay.TryCreate(publishedAhead, 20_200, out SolverProgress aheadDisplay)
            || aheadDisplay.ElapsedMilliseconds != 4_000)
        {
            throw new InvalidOperationException("搜索计时器覆盖了 worker 发布的更大耗时。");
        }
    }

    private static void AssertFullRngStateIdentity(CombatState combat)
    {
        Rng rng = combat.RunState.Rng.CombatCardSelection;
        SerializableRng original = rng.ToSerializable();
        try
        {
            ContinuationStamp originalContinuation = ContinuationStamp.CaptureLive(combat);
            StateFingerprint originalFingerprint =
                CombatBeamSolver.CaptureRngStateFingerprintForTesting(rng);
            rng.LoadFromSerializable(new SerializableRng
            {
                counter = original.counter,
                state0 = original.state0 ^ 0x9E3779B97F4A7C15UL,
                state1 = original.state1,
                state2 = original.state2,
                state3 = original.state3,
            });
            ContinuationStamp changedContinuation = ContinuationStamp.CaptureLive(combat);
            StateFingerprint changedFingerprint =
                CombatBeamSolver.CaptureRngStateFingerprintForTesting(rng);
            if (originalContinuation == changedContinuation)
                throw new InvalidOperationException("续用状态没有区分计数相同但内部状态不同的 RNG。");
            if (originalFingerprint == changedFingerprint)
                throw new InvalidOperationException("搜索状态键没有区分计数相同但内部状态不同的 RNG。");
        }
        finally
        {
            rng.LoadFromSerializable(original);
        }
    }

    private static async Task AssertNoGcBudgetTransitionAsync()
    {
        const long initialBudgetBytes = 1_000_000_000L;
        const long changedBudgetBytes = 2_000_000_000L;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        using CancellationTokenSource firstSearchCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_no_gc_budget_transition_setup",
            forceCollection: true);
        await AssertNoGcDisableTransitionAsync(initialBudgetBytes, deadline.Token);
        await AssertManualGcAtInSearchCheckpointAsync(initialBudgetBytes, deadline.Token);
        await AssertDeferredReclaimDuringInSearchCheckpointAsync(
            initialBudgetBytes,
            deadline.Token);
        await AssertDeferredProcessReleaseDuringCanceledCheckpointAsync(
            initialBudgetBytes,
            deadline.Token);
        await AssertDeferredReclaimSurvivesFaultedCheckpointAsync(
            initialBudgetBytes,
            deadline.Token);
        await AssertRegionExitFailureSettlesDeferredRequestsAsync(
            initialBudgetBytes,
            deadline.Token);
        await AssertInSearchDefaultGcFallbackAsync(initialBudgetBytes, deadline.Token);
        await AssertInSearchReclaimAsync(deadline.Token);
        await AssertCombatEndReclaimPolicyAsync(deadline.Token);
        SearchGcPolicy.ResetCountersForTesting();

        TaskCompletionSource firstSearchEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? firstSearchTask = null;
        IDisposable? changedScope = null;
        Task<IDisposable>? changedScopeTask = null;
        try
        {
            firstSearchTask = Task.Run(async () =>
            {
                using IDisposable scope = SearchGcPolicy.EnterLowLatencySearch(
                    initialBudgetBytes,
                    new SearchMemoryPressureSignal(),
                    firstSearchCancellation.Token);
                firstSearchEntered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, firstSearchCancellation.Token);
            });
            await Task.WhenAny(firstSearchEntered.Task, firstSearchTask).WaitAsync(deadline.Token);
            if (firstSearchTask.IsCompleted)
                await firstSearchTask;
            await firstSearchEntered.Task;
            if (SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != initialBudgetBytes)
                throw new InvalidOperationException("No-GC 首次区域没有使用 1GB 测试预算。");
            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
                throw new InvalidOperationException("No-GC 首次 1GB 测试区域没有由 CLR 实际建立。");

            changedScopeTask = Task.Run(() => SearchGcPolicy.EnterLowLatencySearch(
                changedBudgetBytes,
                new SearchMemoryPressureSignal(),
                deadline.Token));
            while (SearchGcPolicy.BudgetChangeWaitCountForTesting == 0)
            {
                deadline.Token.ThrowIfCancellationRequested();
                await Task.Delay(10, deadline.Token);
            }
            if (changedScopeTask.IsCompleted)
                throw new InvalidOperationException("No-GC 改预算请求没有等待仍在使用旧区域的搜索退出。");

            firstSearchCancellation.Cancel();
            try
            {
                await firstSearchTask.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (firstSearchCancellation.IsCancellationRequested)
            {
            }

            changedScope = await changedScopeTask.WaitAsync(deadline.Token);
            if (SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != changedBudgetBytes)
            {
                throw new InvalidOperationException(
                    "No-GC 改预算后没有按 2GB 测试值建立新区域。");
            }
            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
                throw new InvalidOperationException("No-GC 改预算后的 2GB 区域没有由 CLR 实际建立。");
            if (SearchGcPolicy.BudgetChangeRebuildCountForTesting != 1)
            {
                throw new InvalidOperationException(
                    $"No-GC 改预算后重建次数为 " +
                    $"{SearchGcPolicy.BudgetChangeRebuildCountForTesting}，预期为 1。");
            }
        }
        finally
        {
            firstSearchCancellation.Cancel();
            if (firstSearchTask != null)
            {
                try
                {
                    await firstSearchTask;
                }
                catch (OperationCanceledException) when (firstSearchCancellation.IsCancellationRequested)
                {
                }
            }
            if (changedScope == null && changedScopeTask != null)
            {
                try
                {
                    changedScope = await changedScopeTask.WaitAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                }
            }
            changedScope?.Dispose();
            await SearchGcPolicy.ReclaimIfPendingAsync(
                "unattended_no_gc_budget_transition_cleanup",
                forceCollection: true);
        }
    }

    private static async Task AssertManualGcAtInSearchCheckpointAsync(
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_manual_gc_checkpoint_setup",
            forceCollection: true);
        SearchGcPolicy.ResetCountersForTesting();
        SearchMemoryPressureSignal signal = new();
        IDisposable? scope = SearchGcPolicy.EnterLowLatencySearch(
            enableNoGcRegion: true,
            budgetBytes,
            signal,
            cancellationToken);
        Task? manualGc = null;
        Task? checkpoint = null;
        int generation2Before = GC.CollectionCount(GC.MaxGeneration);
        try
        {
            manualGc = SearchGcPolicy.ForceManualGc();
            if (manualGc.IsCompleted)
                throw new InvalidOperationException("活动 NoGC 搜索中的手动 GC 没有排队。");
            checkpoint = Task.Run(
                () => signal.ReclaimAndContinue(cancellationToken),
                cancellationToken);
            await Task.WhenAll(manualGc, checkpoint).WaitAsync(cancellationToken);
            if (signal.ReclaimCount != 1
                || GC.CollectionCount(GC.MaxGeneration) <= generation2Before
                || SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 0
                || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 0)
            {
                throw new InvalidOperationException(
                    $"搜索内检查点没有恰好吸收手动 GC：" +
                    $"checkpoints={signal.ReclaimCount} " +
                    $"gen2_delta={GC.CollectionCount(GC.MaxGeneration) - generation2Before} " +
                    $"background_reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                    $"background_gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting}。");
            }
        }
        finally
        {
            scope?.Dispose();
            scope = null;
            if (checkpoint != null)
                await checkpoint.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (manualGc != null)
                await manualGc.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync(
                    "unattended_manual_gc_checkpoint_cleanup")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        if (SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 0
            || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 0)
        {
            throw new InvalidOperationException(
                "搜索内检查点满足手动 GC 后又重复安排了后台 Gen2。");
        }
    }

    private static async Task AssertDeferredReclaimDuringInSearchCheckpointAsync(
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_deferred_reclaim_checkpoint_setup",
            forceCollection: true);
        SearchGcPolicy.ResetCountersForTesting();
        SearchMemoryPressureSignal signal = new();
        IDisposable? scope = SearchGcPolicy.EnterLowLatencySearch(
            enableNoGcRegion: true,
            budgetBytes,
            signal,
            cancellationToken);
        Task? deferredReclaim = null;
        Task? checkpoint = null;
        try
        {
            deferredReclaim = SearchGcPolicy.ReclaimIfPendingAsync(
                "unattended_deferred_reclaim_during_search",
                forceCollection: true,
                includeCombatLifecyclePressure: false);
            if (deferredReclaim.IsCompleted)
            {
                throw new InvalidOperationException(
                    "活动搜索期间的后台 GC 请求没有等到搜索退出边界。");
            }

            checkpoint = Task.Run(
                () => signal.ReclaimAndContinue(cancellationToken),
                cancellationToken);
            await checkpoint.WaitAsync(cancellationToken);
            if (signal.ReclaimCount != 1)
                throw new InvalidOperationException("deferred GC 请求阻塞了搜索内检查点。");
            if (deferredReclaim.IsCompleted)
            {
                throw new InvalidOperationException(
                    "搜索内检查点提前完成了必须在搜索退出后执行的后台 GC 请求。");
            }

            scope.Dispose();
            scope = null;
            await deferredReclaim.WaitAsync(cancellationToken);
            if (SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 1
                || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 1)
            {
                throw new InvalidOperationException(
                    $"deferred GC 请求没有在退出后恰好完成一次后台回收：" +
                    $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                    $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting}。");
            }
        }
        finally
        {
            scope?.Dispose();
            if (checkpoint != null)
                await checkpoint.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (deferredReclaim != null)
                await deferredReclaim.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync(
                    "unattended_deferred_reclaim_checkpoint_cleanup")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task AssertDeferredProcessReleaseDuringCanceledCheckpointAsync(
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_canceled_checkpoint_deferred_setup",
            forceCollection: true);
        SearchGcPolicy.ResetCountersForTesting();
        using CancellationTokenSource checkpointCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SearchMemoryPressureSignal signal = new();
        IDisposable? scope = SearchGcPolicy.EnterLowLatencySearch(
            enableNoGcRegion: true,
            budgetBytes,
            signal,
            cancellationToken);
        Task? checkpoint = null;
        Task? processRelease = null;
        Task? duplicateProcessRelease = null;
        try
        {
            Task checkpointReached = SearchGcPolicy.PauseNextInSearchCheckpointForTesting();
            checkpoint = Task.Run(
                () => signal.ReclaimAndContinue(checkpointCancellation.Token),
                CancellationToken.None);
            Task firstCompleted = await Task.WhenAny(checkpointReached, checkpoint)
                .WaitAsync(cancellationToken);
            if (ReferenceEquals(firstCompleted, checkpoint))
            {
                await checkpoint;
                throw new InvalidOperationException(
                    "搜索内 checkpoint 没有停在 deferred 请求竞态边界。");
            }
            await checkpointReached;

            processRelease = SearchGcPolicy.ForceManualProcessMemoryRelease();
            duplicateProcessRelease = SearchGcPolicy.ForceManualProcessMemoryRelease();
            if (processRelease.IsCompleted
                || duplicateProcessRelease.IsCompleted
                || !ReferenceEquals(processRelease, duplicateProcessRelease))
            {
                throw new InvalidOperationException(
                    "checkpoint 运行中的重复 trim 请求没有共享搜索退出后的完成信号。");
            }

            checkpointCancellation.Cancel();
            SearchGcPolicy.ResumeInSearchCheckpointForTesting();
            try
            {
                await checkpoint.WaitAsync(cancellationToken);
                throw new InvalidOperationException("已取消的搜索内 checkpoint 没有传播取消。");
            }
            catch (OperationCanceledException) when (checkpointCancellation.IsCancellationRequested)
            {
            }
            if (processRelease.IsCompleted)
            {
                throw new InvalidOperationException(
                    "checkpoint 取消提前完成了必须等搜索退出的 trim 请求。");
            }

            scope.Dispose();
            scope = null;
            await Task.WhenAll(processRelease, duplicateProcessRelease)
                .WaitAsync(cancellationToken);
            if (SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 1
                || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 1)
            {
                throw new InvalidOperationException(
                    $"checkpoint 取消后的 trim 没有恰好完成一次后台回收：" +
                    $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                    $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting}。");
            }
        }
        finally
        {
            checkpointCancellation.Cancel();
            SearchGcPolicy.ResumeInSearchCheckpointForTesting();
            if (checkpoint != null)
                await checkpoint.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            scope?.Dispose();
            if (processRelease != null)
            {
                await processRelease.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            if (duplicateProcessRelease != null)
            {
                await duplicateProcessRelease
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync(
                    "unattended_canceled_checkpoint_deferred_cleanup")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task AssertRegionExitFailureSettlesDeferredRequestsAsync(
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_region_exit_failure_setup",
            forceCollection: true);
        SearchGcPolicy.ResetCountersForTesting();
        IDisposable? scope = SearchGcPolicy.EnterLowLatencySearch(
            enableNoGcRegion: true,
            budgetBytes,
            new SearchMemoryPressureSignal(),
            cancellationToken);
        Task? regionExit = null;
        Task? deferredRelease = null;
        try
        {
            regionExit = SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync(
                "no_gc_disabled");
            deferredRelease = SearchGcPolicy.ForceManualProcessMemoryRelease();
            if (regionExit.IsCompleted || deferredRelease.IsCompleted)
            {
                throw new InvalidOperationException(
                    "region-exit 失败测试的请求没有等到活动搜索退出。");
            }
            SearchGcPolicy.FailNextRegionExitAfterTransitionForTesting();
            scope.Dispose();
            scope = null;

            await AssertInjectedRegionExitFailureAsync(
                regionExit,
                "region-exit",
                cancellationToken);
            await AssertInjectedRegionExitFailureAsync(
                deferredRelease,
                "deferred trim",
                cancellationToken);

            Task retry = SearchGcPolicy.ForceManualProcessMemoryRelease();
            await retry.WaitAsync(cancellationToken);
            if (SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 1
                || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 1)
            {
                throw new InvalidOperationException(
                    $"region-exit 失败后的新请求没有恢复：" +
                    $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                    $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting}。");
            }
        }
        finally
        {
            scope?.Dispose();
            if (regionExit != null)
                await regionExit.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (deferredRelease != null)
            {
                await deferredRelease
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            SearchGcPolicy.ResetCountersForTesting();
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync(
                    "unattended_region_exit_failure_cleanup",
                    forceCollection: true)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task AssertDeferredReclaimSurvivesFaultedCheckpointAsync(
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_faulted_checkpoint_deferred_setup",
            forceCollection: true);
        SearchGcPolicy.ResetCountersForTesting();
        SearchMemoryPressureSignal signal = new();
        IDisposable? scope = SearchGcPolicy.EnterLowLatencySearch(
            enableNoGcRegion: true,
            budgetBytes,
            signal,
            cancellationToken);
        Task? checkpoint = null;
        Task? checkpointJoin = null;
        Task? deferredReclaim = null;
        try
        {
            Task checkpointReached = SearchGcPolicy.PauseNextInSearchCheckpointForTesting();
            checkpoint = Task.Run(
                () => signal.ReclaimAndContinue(cancellationToken),
                CancellationToken.None);
            Task firstCompleted = await Task.WhenAny(checkpointReached, checkpoint)
                .WaitAsync(cancellationToken);
            if (ReferenceEquals(firstCompleted, checkpoint))
            {
                await checkpoint;
                throw new InvalidOperationException(
                    "搜索内 checkpoint 没有停在 fault/deferred 竞态边界。");
            }
            await checkpointReached;

            checkpointJoin = SearchGcPolicy.ForceManualGc();
            deferredReclaim = SearchGcPolicy.ReclaimIfPendingAsync(
                "unattended_deferred_after_checkpoint_started",
                forceCollection: true,
                includeCombatLifecyclePressure: false);
            if (deferredReclaim.IsCompleted || ReferenceEquals(checkpointJoin, deferredReclaim))
            {
                throw new InvalidOperationException(
                    "checkpoint 加入者与 post-search deferred 请求错误共享了完成信号。");
            }

            SearchGcPolicy.FailNextInSearchCheckpointAfterTransitionForTesting();
            SearchGcPolicy.ResumeInSearchCheckpointForTesting();
            await AssertInjectedInSearchCheckpointFailureAsync(
                checkpoint,
                "checkpoint caller",
                cancellationToken);
            await AssertInjectedInSearchCheckpointFailureAsync(
                checkpointJoin,
                "checkpoint joiner",
                cancellationToken);
            if (deferredReclaim.IsCompleted)
            {
                throw new InvalidOperationException(
                    "faulted checkpoint 提前落定了独立的 post-search deferred 请求。");
            }

            scope.Dispose();
            scope = null;
            await deferredReclaim.WaitAsync(cancellationToken);
            if (SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 1
                || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 1)
            {
                throw new InvalidOperationException(
                    $"faulted checkpoint 后的 deferred 请求没有恢复：" +
                    $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                    $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting}。");
            }
        }
        finally
        {
            SearchGcPolicy.ResumeInSearchCheckpointForTesting();
            if (checkpoint != null)
                await checkpoint.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (checkpointJoin != null)
                await checkpointJoin.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            scope?.Dispose();
            if (deferredReclaim != null)
            {
                await deferredReclaim
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            SearchGcPolicy.ResetCountersForTesting();
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync(
                    "unattended_faulted_checkpoint_deferred_cleanup",
                    forceCollection: true)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task AssertInjectedRegionExitFailureAsync(
        Task operation,
        string waiter,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation.WaitAsync(cancellationToken);
        }
        catch (InvalidOperationException ex) when (
            ex.Message == "无人测试注入的 NoGC region-exit 完成失败。")
        {
            return;
        }
        throw new InvalidOperationException($"{waiter} 没有收到同一个 region-exit 失败。");
    }

    private static async Task AssertInjectedInSearchCheckpointFailureAsync(
        Task operation,
        string waiter,
        CancellationToken cancellationToken)
    {
        try
        {
            await operation.WaitAsync(cancellationToken);
        }
        catch (InvalidOperationException ex) when (
            ex.Message == "无人测试注入的搜索内 GC checkpoint 失败。")
        {
            return;
        }
        throw new InvalidOperationException($"{waiter} 没有收到同一个 checkpoint 失败。");
    }

    private static async Task AssertNoGcDisableTransitionAsync(
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        GCLatencyMode initialLatencyMode = GCSettings.LatencyMode;
        IDisposable? enabledScope = SearchGcPolicy.EnterLowLatencySearch(
            enableNoGcRegion: true,
            budgetBytes,
            new SearchMemoryPressureSignal(),
            cancellationToken);
        SearchMemoryPressureSignal disabledSignal = new();
        bool disabledCheckpointInvoked = false;
        disabledSignal.Configure(
            GC.GetTotalAllocatedBytes(precise: false),
            allocationLimitBytes: 1,
            memoryLoadBytesAtStart: 0,
            systemMemoryLimitBytes: long.MaxValue,
            _ => disabledCheckpointInvoked = true,
            _ => disabledCheckpointInvoked = true);
        Task<IDisposable> disabledScopeTask = Task.Run(() =>
            SearchGcPolicy.EnterLowLatencySearch(
                enableNoGcRegion: false,
                budgetBytes,
                disabledSignal,
                cancellationToken),
            cancellationToken);
        IDisposable? disabledScope = null;
        Task? transitionManualGc = null;
        Task? manualGc = null;
        Task<IDisposable>? reenabledScopeTask = null;
        IDisposable? reenabledScope = null;
        try
        {
            await Task.Delay(50, cancellationToken);
            if (disabledScopeTask.IsCompleted)
                throw new InvalidOperationException("关闭 NoGC 没有等待现有区域的活动搜索退出。");
            transitionManualGc = SearchGcPolicy.ForceManualGc();
            await Task.Delay(50, cancellationToken);
            if (transitionManualGc.IsCompleted)
            {
                throw new InvalidOperationException(
                    "NoGC→常规 GC 切换尚未退出活动搜索时手动 GC 提前完成。");
            }
            enabledScope.Dispose();
            enabledScope = null;
            disabledScope = await disabledScopeTask.WaitAsync(cancellationToken);
            await transitionManualGc.WaitAsync(cancellationToken);
            if (SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != 0
                || GCSettings.LatencyMode != initialLatencyMode
                || disabledSignal.AllocationLimitBytes != long.MaxValue
                || disabledSignal.ConservativeParallelismRequired
                || disabledCheckpointInvoked)
            {
                throw new InvalidOperationException(
                    $"关闭 NoGC 后没有恢复 CLR 常规 GC：" +
                    $"budget={SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting} " +
                    $"latency={GCSettings.LatencyMode} " +
                    $"limit={disabledSignal.AllocationLimitBytes}。");
            }

            SearchMemoryPressureSignal fallbackSignal = new();
            fallbackSignal.Configure(
                GC.GetTotalAllocatedBytes(precise: false),
                allocationLimitBytes: 1,
                memoryLoadBytesAtStart: 0,
                systemMemoryLimitBytes: long.MaxValue,
                _ => { },
                _ => { });
            fallbackSignal.UseDefaultGcFallback(systemHeadroomConstrained: true);
            if (fallbackSignal.IsEnabled
                || !fallbackSignal.ConservativeParallelismRequired)
            {
                throw new InvalidOperationException(
                    "NoGC 低余量回退没有关闭区域检查点并保留保守并行准入。");
            }
            fallbackSignal.Disable();
            if (fallbackSignal.ConservativeParallelismRequired)
            {
                throw new InvalidOperationException(
                    "普通 CLR GC 模式意外继承了 NoGC 低余量并行限制。");
            }
            try
            {
                disabledSignal.ReclaimAndContinue(cancellationToken);
                throw new InvalidOperationException("关闭 NoGC 后仍保留了搜索内存检查点回调。");
            }
            catch (InvalidOperationException ex) when (
                ex.Message == "搜索内存回收信号尚未配置。")
            {
            }

            manualGc = SearchGcPolicy.ForceManualGc();
            reenabledScopeTask = Task.Run(() =>
                SearchGcPolicy.EnterLowLatencySearch(
                    enableNoGcRegion: true,
                    budgetBytes,
                    new SearchMemoryPressureSignal(),
                    cancellationToken),
                cancellationToken);
            await Task.Delay(50, cancellationToken);
            if (manualGc.IsCompleted)
                throw new InvalidOperationException("CLR 常规 GC 搜索尚未退出时手动 GC 提前完成。");
            if (reenabledScopeTask.IsCompleted)
                throw new InvalidOperationException("CLR 常规 GC 搜索尚未退出时 NoGC 搜索提前进入。");

            disabledScope.Dispose();
            disabledScope = null;
            reenabledScope = await reenabledScopeTask.WaitAsync(cancellationToken);
            await manualGc.WaitAsync(cancellationToken);
            if (SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != budgetBytes)
            {
                throw new InvalidOperationException(
                    $"重新启用 NoGC 后没有恢复用户预算：" +
                    $"expected={budgetBytes} " +
                    $"actual={SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting}。");
            }
        }
        finally
        {
            enabledScope?.Dispose();
            disabledScope?.Dispose();
            await ((Task)disabledScopeTask)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (disabledScopeTask.IsCompletedSuccessfully)
                disabledScopeTask.Result.Dispose();
            reenabledScope?.Dispose();
            if (reenabledScopeTask != null)
            {
                await ((Task)reenabledScopeTask)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            if (reenabledScopeTask?.IsCompletedSuccessfully == true)
                reenabledScopeTask.Result.Dispose();
            if (manualGc != null)
            {
                await manualGc
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            if (transitionManualGc != null)
            {
                await transitionManualGc
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync(
                    "unattended_no_gc_disabled_cleanup")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync(
                    "unattended_no_gc_disabled_cleanup",
                    forceCollection: true)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        SearchGcPolicy.DetachCombatLifecyclePressure(
            "unattended_no_gc_disabled_lifecycle_setup");
        SearchGcPolicy.ResetCountersForTesting();
        GCLatencyMode steadyDisabledLatencyMode = GCSettings.LatencyMode;
        using (SearchGcPolicy.EnterLowLatencySearch(
                   enableNoGcRegion: false,
                   budgetBytes,
                   new SearchMemoryPressureSignal(),
                   cancellationToken))
        {
            if (GCSettings.LatencyMode != steadyDisabledLatencyMode
                || SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != 0)
            {
                throw new InvalidOperationException(
                    $"稳定关闭 NoGC 的搜索改变了 CLR latency：" +
                    $"expected={steadyDisabledLatencyMode} actual={GCSettings.LatencyMode} " +
                    $"budget={SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting}。");
            }
        }
        if (GCSettings.LatencyMode != steadyDisabledLatencyMode)
            throw new InvalidOperationException("关闭 NoGC 的搜索退出后没有保留 CLR latency。");
        SearchGcPolicy.ReportCombatLifecycleAllocation(
            270L * 1024 * 1024,
            "unattended_no_gc_disabled_root_snapshot",
            automaticGcEnabled: false);
        SearchGcPolicy.CombatLifecyclePressure disabledPressure =
            SearchGcPolicy.DetachCombatLifecyclePressure(
                "unattended_no_gc_disabled_lifecycle");
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_no_gc_disabled_lifecycle");
        if (disabledPressure.AllocatedBytes != 0
            || disabledPressure.RequiresCollection
            || SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 0
            || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 0)
        {
            throw new InvalidOperationException(
                $"关闭 NoGC 的稳定战斗仍登记了自动 Gen2 压力：" +
                $"allocated={disabledPressure.AllocatedBytes} " +
                $"requires_collection={disabledPressure.RequiresCollection} " +
                $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting}。");
        }
    }

    private static async Task AssertInSearchDefaultGcFallbackAsync(
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_in_search_default_gc_setup",
            forceCollection: true);
        GCLatencyMode initialLatencyMode = GCSettings.LatencyMode;
        SearchMemoryPressureSignal signal = new();
        IDisposable? scope = null;
        try
        {
            scope = SearchGcPolicy.EnterLowLatencySearch(
                enableNoGcRegion: true,
                budgetBytes,
                signal,
                cancellationToken);
            if (!signal.IsEnabled
                || GCSettings.LatencyMode != GCLatencyMode.NoGCRegion
                || SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting <= 0)
            {
                throw new InvalidOperationException(
                    "搜索内默认 GC 回退测试没有实际进入 NoGC 区域。");
            }

            int generation2Before = GC.CollectionCount(GC.MaxGeneration);
            signal.UseDefaultGcAndContinue(cancellationToken);
            if (GCSettings.LatencyMode != initialLatencyMode
                || SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != 0
                || signal.IsEnabled
                || signal.ReclaimCount != 1
                || GC.CollectionCount(GC.MaxGeneration) <= generation2Before)
            {
                throw new InvalidOperationException(
                    $"搜索内默认 GC 回退没有完成实际回收并清理区域所有权：" +
                    $"latency={GCSettings.LatencyMode} expected_latency={initialLatencyMode} " +
                    $"budget={SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting} " +
                    $"signal_enabled={signal.IsEnabled} checkpoints={signal.ReclaimCount}。");
            }

            try
            {
                signal.UseDefaultGcAndContinue(cancellationToken);
                throw new InvalidOperationException("默认 GC 回退后仍保留了旧区域检查点回调。");
            }
            catch (InvalidOperationException ex) when (
                ex.Message == "搜索默认 GC 回退信号尚未配置。")
            {
            }

            scope.Dispose();
            scope = null;
            if (GCSettings.LatencyMode != initialLatencyMode
                || SearchGcPolicy.CurrentNoGcRegionBudgetBytesForTesting != 0)
            {
                throw new InvalidOperationException(
                    "默认 GC 回退后的原搜索作用域退出时重新取得了 NoGC 所有权。");
            }
        }
        finally
        {
            scope?.Dispose();
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync("no_gc_disabled")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ReclaimIfPendingAsync(
                    "unattended_in_search_default_gc_cleanup",
                    forceCollection: true)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task AssertInSearchReclaimAsync(CancellationToken cancellationToken)
    {
        const long budgetBytes = 1_000_000_000L;
        SearchMemoryPressureSignal signal = new();
        using (SearchGcPolicy.EnterLowLatencySearch(
                   budgetBytes,
                   signal,
                   cancellationToken))
        {
            signal.ReclaimAndContinue(cancellationToken);
            if (signal.ReclaimCount != 1)
                throw new InvalidOperationException("搜索内存检查点没有完成一次全代回收后继续。");
        }
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_in_search_reclaim_cleanup",
            forceCollection: true);
    }

    private static async Task AssertCombatEndReclaimPolicyAsync(
        CancellationToken cancellationToken)
    {
        const long budgetBytes = 1_000_000_000L;
        await AssertManualGcPreservesLifecyclePressureAsync(cancellationToken);
        SearchGcPolicy.ResetCountersForTesting();
        SearchGcPolicy.ReportCombatLifecycleAllocation(
            1024 * 1024,
            "unattended_low_allocation_root_snapshot",
            automaticGcEnabled: true);
        IDisposable lowAllocationScope = SearchGcPolicy.EnterLowLatencySearch(
            budgetBytes,
            new SearchMemoryPressureSignal(),
            cancellationToken);
        Task earlyRegionExit = SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync(
            "unattended_low_allocation_combat_end");
        if (earlyRegionExit.IsCompleted)
        {
            lowAllocationScope.Dispose();
            throw new InvalidOperationException("活跃搜索尚未退出时提前结束了 No-GC 区域。");
        }
        lowAllocationScope.Dispose();
        await earlyRegionExit.WaitAsync(cancellationToken);
        await SearchGcPolicy.ReclaimIfPendingAsync("unattended_low_allocation_combat_end");
        if (SearchGcPolicy.NoGcRegionExitWithoutCollectionCountForTesting != 1
            || SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 0
            || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 0
            || GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
        {
            throw new InvalidOperationException(
                $"低分配战斗结束没有只退出 No-GC 区域：" +
                $"region_exits={SearchGcPolicy.NoGcRegionExitWithoutCollectionCountForTesting} " +
                $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting} " +
                $"latency={GCSettings.LatencyMode}。");
        }
        await AssertReferenceReleaseBarrierAsync(budgetBytes, cancellationToken);

        SearchGcPolicy.ResetCountersForTesting();
        long rootAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        WeakReference transientRoot = AllocateTransientSearchGraphForGcPolicyTest();
        long rootAllocated = GC.GetTotalAllocatedBytes(precise: true) - rootAllocatedBefore;
        SearchGcPolicy.ReportCombatLifecycleAllocation(
            rootAllocated,
            "unattended_high_allocation_root_snapshot",
            automaticGcEnabled: true);
        if (rootAllocated < 270L * 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"战斗根快照回收门禁只产生了 {rootAllocated} bytes，未越过 256 MiB 阈值。");
        }
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_high_allocation_root_snapshot").WaitAsync(cancellationToken);
        long rootManagedLiveReleased =
            SearchGcPolicy.LastBackgroundReclaimManagedLiveBeforeForTesting
            - SearchGcPolicy.LastBackgroundReclaimManagedLiveAfterForTesting;
        if (SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 1
            || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 1
            || transientRoot.IsAlive)
        {
            throw new InvalidOperationException(
                $"No-GC 区域外的战斗根快照压力没有完成一次有效 Gen2：" +
                $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting} " +
                $"managed_live_released={rootManagedLiveReleased}。");
        }

        SearchGcPolicy.ResetCountersForTesting();
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        WeakReference transientAllocation;
        using (SearchGcPolicy.EnterLowLatencySearch(
                   budgetBytes,
                   new SearchMemoryPressureSignal(),
                   cancellationToken))
        {
            transientAllocation = AllocateTransientSearchGraphForGcPolicyTest();
        }
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        if (allocated < 270L * 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"重分配回收门禁只产生了 {allocated} bytes，未越过 256 MiB 阈值。");
        }
        Task reclaim = SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_high_allocation_combat_end");
        Task joined = SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_high_allocation_combat_end_join");
        await Task.WhenAll(reclaim, joined).WaitAsync(cancellationToken);
        long managedLiveReleased =
            SearchGcPolicy.LastBackgroundReclaimManagedLiveBeforeForTesting
            - SearchGcPolicy.LastBackgroundReclaimManagedLiveAfterForTesting;
        if (SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 1
            || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 1
            || SearchGcPolicy.BackgroundReclaimJoinCountForTesting != 1
            || transientAllocation.IsAlive)
        {
            throw new InvalidOperationException(
                $"重分配战斗结束没有完成恰好一次有效 Gen2：" +
                $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting} " +
                $"joins={SearchGcPolicy.BackgroundReclaimJoinCountForTesting} " +
                $"managed_live_released={managedLiveReleased}。");
        }
        await AssertExhaustionReclaimReferenceCoverageAsync(cancellationToken);
    }

    private static async Task AssertManualGcPreservesLifecyclePressureAsync(
        CancellationToken cancellationToken)
    {
        SearchGcPolicy.DetachCombatLifecyclePressure(
            "unattended_manual_gc_lifecycle_setup");
        await SearchGcPolicy.ReclaimIfPendingAsync(
            "unattended_manual_gc_lifecycle_setup",
            forceCollection: true);
        SearchGcPolicy.ResetCountersForTesting();
        const long lifecycleAllocation = 270L * 1024 * 1024;
        SearchGcPolicy.ReportCombatLifecycleAllocation(
            lifecycleAllocation,
            "unattended_manual_gc_live_combat",
            automaticGcEnabled: true);
        await SearchGcPolicy.ForceManualGc().WaitAsync(cancellationToken);
        SearchGcPolicy.CombatLifecyclePressure pressure =
            SearchGcPolicy.DetachCombatLifecyclePressure(
                "unattended_manual_gc_reference_release");
        if (pressure.AllocatedBytes != lifecycleAllocation || !pressure.RequiresCollection)
        {
            throw new InvalidOperationException(
                $"手动 GC 错误清除了仍被战斗引用持有的生命周期压力：" +
                $"allocated={pressure.AllocatedBytes} " +
                $"requires_collection={pressure.RequiresCollection}。");
        }
        int releaseCallbackCount = 0;
        await SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
                "unattended_manual_gc_reference_release",
                pressure.RequiresCollection,
                includeCombatLifecyclePressure: false,
                Task.CompletedTask,
                () => Interlocked.Increment(ref releaseCallbackCount))
            .WaitAsync(cancellationToken);
        if (releaseCallbackCount != 1
            || SearchGcPolicy.BackgroundReclaimStartedCountForTesting != 2
            || SearchGcPolicy.BackgroundGen2CompletedCountForTesting != 2)
        {
            throw new InvalidOperationException(
                $"手动 GC 后没有在引用释放边界补做生命周期回收：" +
                $"callback={releaseCallbackCount} " +
                $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting}。");
        }
    }

    private static async Task AssertExhaustionReclaimReferenceCoverageAsync(
        CancellationToken cancellationToken)
    {
        await AssertExhaustionReclaimReferenceCoverageTimingAsync(
            pauseAfterCoverageCapture: false,
            expectedGeneration2Collections: 1,
            cancellationToken);
        await AssertExhaustionReclaimReferenceCoverageTimingAsync(
            pauseAfterCoverageCapture: true,
            expectedGeneration2Collections: 2,
            cancellationToken);
    }

    private static async Task AssertExhaustionReclaimReferenceCoverageTimingAsync(
        bool pauseAfterCoverageCapture,
        int expectedGeneration2Collections,
        CancellationToken cancellationToken)
    {
        SearchGcPolicy.ResetCountersForTesting();
        TaskCompletionSource referencesReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        (WeakReference graph, Task release) = CreateHeldGraphForGcPolicyTest(
            referencesReleased.Task);
        Task earlyReclaim = Task.CompletedTask;
        Task cleanup = Task.CompletedTask;
        try
        {
            long releaseEpochBefore = SearchGcPolicy.ReferenceReleaseEpochForTesting;
            (Task Reclaim, Task CoverageBoundaryReached) reclaimRequest =
                SearchGcPolicy.RequestNoGcExhaustionReclaimForTesting(
                    pauseAfterCoverageCapture);
            earlyReclaim = reclaimRequest.Reclaim;
            Task coverageBoundaryReached = reclaimRequest.CoverageBoundaryReached;
            Task firstCompleted = await Task.WhenAny(
                    coverageBoundaryReached,
                    earlyReclaim)
                .WaitAsync(cancellationToken);
            if (ReferenceEquals(firstCompleted, earlyReclaim))
            {
                await earlyReclaim;
                throw new InvalidOperationException(
                    "exhaustion 回收没有停在预期的 Gen2 覆盖边界。");
            }
            await coverageBoundaryReached;
            if (!graph.IsAlive)
            {
                throw new InvalidOperationException(
                    "exhaustion 测试图在引用释放前已不可达。");
            }

            int referenceCallbackCount = 0;
            string timing = pauseAfterCoverageCapture
                ? "after_coverage_capture"
                : "before_coverage_capture";
            cleanup = SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
                $"unattended_exhaustion_reference_coverage_{timing}",
                forceCollection: true,
                includeCombatLifecyclePressure: false,
                release,
                () => Interlocked.Increment(ref referenceCallbackCount));
            if (cleanup.IsCompleted)
                throw new InvalidOperationException("exhaustion 引用释放门没有等待测试图解绑。");
            referencesReleased.SetResult();
            while (SearchGcPolicy.ReferenceReleaseEpochForTesting == releaseEpochBefore)
                await Task.Delay(10, cancellationToken);
            if (SearchGcPolicy.ReferenceReleaseEpochForTesting
                != checked(releaseEpochBefore + 1))
            {
                throw new InvalidOperationException(
                    "exhaustion 覆盖边界测试观察到了意外的并发引用释放。");
            }
            SearchGcPolicy.ResumeGeneration2CoverageForTesting();
            await Task.WhenAll(earlyReclaim, cleanup).WaitAsync(cancellationToken);
            await SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
                    $"unattended_exhaustion_reference_coverage_{timing}_settled",
                    forceCollection: false,
                    includeCombatLifecyclePressure: false,
                    Task.CompletedTask,
                    static () => { })
                .WaitAsync(cancellationToken);
            int expectedJoinCount = pauseAfterCoverageCapture ? 0 : 1;
            if (referenceCallbackCount != 1
                || SearchGcPolicy.ReferenceReleaseEpochForTesting
                    != checked(releaseEpochBefore + 2)
                || SearchGcPolicy.BackgroundReclaimStartedCountForTesting
                    != expectedGeneration2Collections
                || SearchGcPolicy.BackgroundGen2CompletedCountForTesting
                    != expectedGeneration2Collections
                || SearchGcPolicy.BackgroundReclaimJoinCountForTesting != expectedJoinCount
                || graph.IsAlive)
            {
                throw new InvalidOperationException(
                    $"exhaustion 引用释放覆盖时序不正确：timing={timing} " +
                    $"callback={referenceCallbackCount} " +
                    $"reclaims={SearchGcPolicy.BackgroundReclaimStartedCountForTesting} " +
                    $"gen2={SearchGcPolicy.BackgroundGen2CompletedCountForTesting} " +
                    $"joins={SearchGcPolicy.BackgroundReclaimJoinCountForTesting} " +
                    $"graph_alive={graph.IsAlive}。");
            }
        }
        finally
        {
            referencesReleased.TrySetResult();
            SearchGcPolicy.ResumeGeneration2CoverageForTesting();
            await Task.WhenAll(earlyReclaim, cleanup)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Reference, Task Release) CreateHeldGraphForGcPolicyTest(
        Task releaseGate)
    {
        byte[][] graph = new byte[128][];
        for (int index = 0; index < graph.Length; index++)
        {
            graph[index] = new byte[32 * 1024];
            graph[index][0] = unchecked((byte)index);
        }
        StrongBox<object?> holder = new(graph);
        WeakReference reference = new(graph);
        Task release = ReleaseHeldGraphForGcPolicyTestAsync(holder, releaseGate);
        GC.KeepAlive(graph);
        return (reference, release);
    }

    private static async Task ReleaseHeldGraphForGcPolicyTestAsync(
        StrongBox<object?> holder,
        Task releaseGate)
    {
        await releaseGate;
        holder.Value = null;
    }

    private static async Task AssertReferenceReleaseBarrierAsync(
        long budgetBytes,
        CancellationToken cancellationToken)
    {
        IDisposable activeScope = SearchGcPolicy.EnterLowLatencySearch(
            budgetBytes,
            new SearchMemoryPressureSignal(),
            cancellationToken);
        TaskCompletionSource referencesReleased = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task regionExit = SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync(
            "unattended_reference_barrier_region_exit");
        int referenceCallbackCount = 0;
        Task cleanup = SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
            "unattended_reference_barrier",
            forceCollection: false,
            includeCombatLifecyclePressure: false,
            Task.WhenAll(regionExit, referencesReleased.Task),
            () => Interlocked.Increment(ref referenceCallbackCount));
        Task rootCaptureBarrier = SearchGcPolicy.CaptureRootSnapshotBarrier();
        if (rootCaptureBarrier.IsCompleted)
            throw new InvalidOperationException("根快照入口没有观察到旧战斗引用释放屏障。");

        TaskCompletionSource entrantStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource entrantEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task entrant = Task.Run(() =>
        {
            entrantStarted.TrySetResult();
            using IDisposable scope = SearchGcPolicy.EnterLowLatencySearch(
                budgetBytes,
                new SearchMemoryPressureSignal(),
                cancellationToken);
            entrantEntered.TrySetResult();
        }, cancellationToken);
        try
        {
            await entrantStarted.Task.WaitAsync(cancellationToken);
            await Task.Delay(50, cancellationToken);
            if (entrantEntered.Task.IsCompleted)
                throw new InvalidOperationException("新搜索在旧战斗引用释放屏障完成前进入了 GC 区域。");

            activeScope.Dispose();
            await regionExit.WaitAsync(cancellationToken);
            if (cleanup.IsCompleted)
                throw new InvalidOperationException("forensic/callback 引用尚未释放时提前完成了回收屏障。");
            referencesReleased.SetResult();
            await cleanup.WaitAsync(cancellationToken);
            await rootCaptureBarrier.WaitAsync(cancellationToken);
            await entrant.WaitAsync(cancellationToken);
            if (referenceCallbackCount != 1
                || !rootCaptureBarrier.IsCompletedSuccessfully
                || !entrantEntered.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException(
                    $"引用释放屏障没有按序放行新搜索：callback={referenceCallbackCount} " +
                    $"root_capture={rootCaptureBarrier.Status} entrant={entrantEntered.Task.Status}。");
            }
        }
        finally
        {
            activeScope.Dispose();
            referencesReleased.TrySetResult();
            await cleanup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await entrant.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await SearchGcPolicy.ExitNoGcRegionWhenSearchesIdleAsync(
                    "unattended_reference_barrier_cleanup")
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        int suppressedReferenceCallbacks = 0;
        await SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
            "unattended_reference_barrier_fault",
            forceCollection: false,
            includeCombatLifecyclePressure: false,
            Task.FromException(new InvalidOperationException("expected reference fault")),
            () => Interlocked.Increment(ref suppressedReferenceCallbacks));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
            "unattended_reference_barrier_cancel",
            forceCollection: false,
            includeCombatLifecyclePressure: false,
            Task.FromCanceled(canceled.Token),
            () => Interlocked.Increment(ref suppressedReferenceCallbacks));
        if (suppressedReferenceCallbacks != 2)
            throw new InvalidOperationException("fault/cancel 引用任务使后续 GC 屏障中毒。");

        List<int> serializedCallbacks = [];
        TaskCompletionSource firstReferences = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondReferences = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task firstCleanup = SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
            "unattended_reference_barrier_first",
            forceCollection: false,
            includeCombatLifecyclePressure: false,
            firstReferences.Task,
            () => serializedCallbacks.Add(1));
        Task secondCleanup = SearchGcPolicy.ReclaimAfterReferenceReleaseAsync(
            "unattended_reference_barrier_second",
            forceCollection: false,
            includeCombatLifecyclePressure: false,
            secondReferences.Task,
            () => serializedCallbacks.Add(2));
        try
        {
            secondReferences.SetResult();
            await Task.Delay(20, cancellationToken);
            if (secondCleanup.IsCompleted)
                throw new InvalidOperationException("后登记的跨战斗屏障越过了前一屏障。");
            firstReferences.SetResult();
            await Task.WhenAll(firstCleanup, secondCleanup).WaitAsync(cancellationToken);
            if (!serializedCallbacks.SequenceEqual([1, 2]))
                throw new InvalidOperationException("跨战斗引用释放屏障没有保持 FIFO 顺序。");
        }
        finally
        {
            firstReferences.TrySetResult();
            secondReferences.TrySetResult();
            await Task.WhenAll(firstCleanup, secondCleanup)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateTransientSearchGraphForGcPolicyTest()
    {
        byte[][] graph = new byte[9_000][];
        for (int index = 0; index < graph.Length; index++)
        {
            graph[index] = new byte[32 * 1024];
            graph[index][0] = unchecked((byte)index);
        }
        WeakReference reference = new(graph);
        GC.KeepAlive(graph);
        return reference;
    }

    private static void AssertEquivalentSearchResults(
        SolverResult expected,
        SolverResult actual,
        string comparison)
    {
        List<string> mismatches = [];
        if (!ActionsEquivalent(expected.BestNode.Actions, actual.BestNode.Actions))
            mismatches.Add("best.actions");
        if (!ChoicesEquivalent(expected.TurnSetupChoices, actual.TurnSetupChoices))
            mismatches.Add("turn_setup_choices");
        if (!ContinuationEquivalent(expected.TurnSetupPlayState, actual.TurnSetupPlayState))
            mismatches.Add("turn_setup_play_state");
        if (!PredictionGapsEquivalent(
                expected.Snapshot.PredictionGaps,
                actual.Snapshot.PredictionGaps))
        {
            mismatches.Add("snapshot.prediction_gaps");
        }
        if (!IntDictionaryEquivalent(expected.SoldHpByTurn, actual.SoldHpByTurn))
            mismatches.Add("annotations.sold_hp_by_turn");
        if (!IntDictionaryEquivalent(expected.HpLostByTurn, actual.HpLostByTurn))
            mismatches.Add("annotations.hp_lost_by_turn");
        if (!IntDictionaryEquivalent(expected.MaxBlockByTurn, actual.MaxBlockByTurn))
            mismatches.Add("annotations.max_block_by_turn");
        if (!IntDictionaryEquivalent(expected.ActualBlockByTurn, actual.ActualBlockByTurn))
            mismatches.Add("annotations.actual_block_by_turn");
        if (!IntDictionaryEquivalent(expected.EnergyLeftByTurn, actual.EnergyLeftByTurn))
            mismatches.Add("annotations.energy_left_by_turn");
        if (!IntDictionaryEquivalent(expected.PotionCountByTurn, actual.PotionCountByTurn))
            mismatches.Add("annotations.potion_count_by_turn");
        if (!IntDictionaryEquivalent(
                expected.PotionStrategicCostByTurn,
                actual.PotionStrategicCostByTurn))
        {
            mismatches.Add("annotations.potion_strategic_cost_by_turn");
        }
        if (!KillsEquivalent(expected.KillsAfterAction, actual.KillsAfterAction))
            mismatches.Add("annotations.kills_after_action");
        if (!ContinuationsEquivalent(expected.Continuations, actual.Continuations))
            mismatches.Add("continuations");

        AddMismatch(mismatches, "search_phase", expected.SearchPhase, actual.SearchPhase);
        AddMismatch(mismatches, "start_turn", expected.StartTurnNumber, actual.StartTurnNumber);
        AddMismatch(mismatches, "best.action_count", expected.BestNode.ActionCount, actual.BestNode.ActionCount);
        AddMismatch(mismatches, "best.score", expected.BestNode.Score, actual.BestNode.Score);
        AddMismatch(mismatches, "expanded", expected.ExpandedNodes, actual.ExpandedNodes);
        AddMismatch(mismatches, "dominated", expected.DominatedActionsPruned, actual.DominatedActionsPruned);
        AddMismatch(mismatches, "top_queue", expected.TopQueueActionsDropped, actual.TopQueueActionsDropped);
        AddMismatch(
            mismatches,
            "action_admission_protected",
            expected.ActionAdmissionRepresentativesProtected,
            actual.ActionAdmissionRepresentativesProtected);
        AddMismatch(mismatches, "duplicate_cards", expected.DuplicateCardBranchesPruned, actual.DuplicateCardBranchesPruned);
        AddMismatch(mismatches, "transitions", expected.TransitionCount, actual.TransitionCount);
        AddMismatch(mismatches, "choices", expected.ChoiceBranchesEvaluated, actual.ChoiceBranchesEvaluated);
        AddMismatch(mismatches, "shuffles", expected.ShuffleBranchesPruned, actual.ShuffleBranchesPruned);
        AddMismatch(mismatches, "sold_hp_pruned", expected.SoldHpBranchesPruned, actual.SoldHpBranchesPruned);
        AddMismatch(
            mismatches,
            "hp_investment_protected",
            expected.HpInvestmentBranchesProtected,
            actual.HpInvestmentBranchesProtected);
        AddMismatch(mismatches, "replays", expected.ReplayCount, actual.ReplayCount);
        AddMismatch(mismatches, "forks", expected.ForkCount, actual.ForkCount);
        AddMismatch(mismatches, "reused", expected.ReusedNodeSnapshots, actual.ReusedNodeSnapshots);
        AddMismatch(mismatches, "tt_pruned", expected.TranspositionBranchesPruned, actual.TranspositionBranchesPruned);
        AddMismatch(mismatches, "repeatable", expected.RepeatableNoProgressBranchesPruned, actual.RepeatableNoProgressBranchesPruned);
        AddMismatch(mismatches, "cycle_shapes", expected.CycleShapesDetected, actual.CycleShapesDetected);
        AddMismatch(
            mismatches,
            "cycle_probe_continuations",
            expected.CycleProbeContinuationsExpanded,
            actual.CycleProbeContinuationsExpanded);
        AddMismatch(
            mismatches,
            "cycle_candidates_protected",
            expected.CycleCandidatesProtected,
            actual.CycleCandidatesProtected);
        AddMismatch(
            mismatches,
            "cycle_continuations_stopped",
            expected.CycleContinuationsStopped,
            actual.CycleContinuationsStopped);
        AddMismatch(
            mismatches,
            "cycle_regions",
            expected.CycleRegionsDetected,
            actual.CycleRegionsDetected);
        AddMismatch(
            mismatches,
            "cycle_region_considered",
            expected.CycleRegionCandidatesConsidered,
            actual.CycleRegionCandidatesConsidered);
        AddMismatch(
            mismatches,
            "cycle_region_admitted",
            expected.CycleRegionCandidatesAdmitted,
            actual.CycleRegionCandidatesAdmitted);
        AddMismatch(
            mismatches,
            "cycle_region_dropped",
            expected.CycleRegionCandidatesDropped,
            actual.CycleRegionCandidatesDropped);
        AddMismatch(
            mismatches,
            "cycle_region_progress_epochs",
            expected.CycleRegionProgressEpochs,
            actual.CycleRegionProgressEpochs);
        AddMismatch(
            mismatches,
            "cycle_region_probe_admitted",
            expected.CycleRegionProbeCandidatesAdmitted,
            actual.CycleRegionProbeCandidatesAdmitted);
        AddMismatch(
            mismatches,
            "cycle_region_progress_admitted",
            expected.CycleRegionProgressCandidatesAdmitted,
            actual.CycleRegionProgressCandidatesAdmitted);
        AddMismatch(
            mismatches,
            "cycle_region_max_action_families",
            expected.CycleRegionMaxActionFamilies,
            actual.CycleRegionMaxActionFamilies);
        AddMismatch(
            mismatches,
            "ordered_admitted",
            expected.OrderedMutationCandidatesAdmitted,
            actual.OrderedMutationCandidatesAdmitted);
        AddMismatch(
            mismatches,
            "ordered_lease_expired_budget",
            expected.OrderedMutationLeaseExpiredBudget,
            actual.OrderedMutationLeaseExpiredBudget);
        AddMismatch(
            mismatches,
            "ordered_ordinary_fallback",
            expected.OrderedMutationOrdinaryFallbacks,
            actual.OrderedMutationOrdinaryFallbacks);
        AddMismatch(
            mismatches,
            "cold_atomic_committed",
            expected.OrderedMutationColdAtomicCommitted,
            actual.OrderedMutationColdAtomicCommitted);
        AddMismatch(
            mismatches,
            "cold_atomic_rejected",
            expected.OrderedMutationColdAtomicRejected,
            actual.OrderedMutationColdAtomicRejected);
        AddMismatch(
            mismatches,
            "cross_turn_candidates_protected",
            expected.CrossTurnCandidatesProtected,
            actual.CrossTurnCandidatesProtected);
        AddMismatch(
            mismatches,
            "cross_turn_continuations_stopped",
            expected.CrossTurnContinuationsStopped,
            actual.CrossTurnContinuationsStopped);
        AddMismatch(
            mismatches,
            "primary_incumbent_pruned",
            expected.PrimaryIncumbentBranchesPruned,
            actual.PrimaryIncumbentBranchesPruned);
        AddMismatch(
            mismatches,
            "primary_incumbent_updates",
            expected.PrimaryIncumbentUpdates,
            actual.PrimaryIncumbentUpdates);
        AddMismatch(mismatches, "stand_pat", expected.StandPatProbes, actual.StandPatProbes);
        AddMismatch(mismatches, "searched_turns", expected.SearchedTurns, actual.SearchedTurns);
        AddMismatch(mismatches, "boundary", expected.BoundaryReason, actual.BoundaryReason);
        AddMismatch(mismatches, "unavoidable_hp_lost", expected.UnavoidableHpLost, actual.UnavoidableHpLost);
        AddMismatch(mismatches, "sold_hp", expected.SoldHp, actual.SoldHp);
        AddMismatch(mismatches, "future_sold_hp", expected.FutureSoldHp, actual.FutureSoldHp);
        AddMismatch(mismatches, "battle_hp_lost", expected.BattleHpLostSoFar, actual.BattleHpLostSoFar);
        AddMismatch(mismatches, "projected_battle_hp_lost", expected.ProjectedBattleHpLost, actual.ProjectedBattleHpLost);
        AddMismatch(mismatches, "battle_potions_used", expected.BattlePotionsUsedSoFar, actual.BattlePotionsUsedSoFar);
        AddMismatch(mismatches, "potions", expected.PotionCount, actual.PotionCount);
        AddMismatch(mismatches, "potion_hp_saved", expected.PotionHpSaved, actual.PotionHpSaved);
        AddMismatch(mismatches, "potion_hp_required", expected.PotionHpRequired, actual.PotionHpRequired);
        AddMismatch(mismatches, "potion_branches_rejected", expected.PotionBranchesRejected, actual.PotionBranchesRejected);
        AddMismatch(mismatches, "theft_policy", expected.TheftPolicy, actual.TheftPolicy);
        AddMismatch(mismatches, "outstanding_stolen", expected.OutstandingStolenResource, actual.OutstandingStolenResource);
        AddMismatch(mismatches, "sold_hp_threshold", expected.SoldHpThreshold, actual.SoldHpThreshold);
        AddMismatch(mismatches, "combat_ended_turn", expected.CombatEndedTurn, actual.CombatEndedTurn);
        AddMismatch(mismatches, "death_turn", expected.DeathTurn, actual.DeathTurn);
        AddMismatch(mismatches, "only_death_routes", expected.OnlyDeathRoutesFound, actual.OnlyDeathRoutesFound);
        AddMismatch(mismatches, "act_ending_boss", expected.IsActEndingBoss, actual.IsActEndingBoss);
        AddMismatch(mismatches, "boss_hp_relief", expected.BossHpRelief, actual.BossHpRelief);

        AddMismatch(mismatches, "snapshot.risk", expected.Snapshot.HasRisk, actual.Snapshot.HasRisk);
        AddMismatch(mismatches, "snapshot.player_dead", expected.Snapshot.PlayerDead, actual.Snapshot.PlayerDead);
        AddMismatch(mismatches, "snapshot.enemies_dead", expected.Snapshot.AllEnemiesDead, actual.Snapshot.AllEnemiesDead);
        AddMismatch(mismatches, "snapshot.player_hp", expected.Snapshot.PlayerHp, actual.Snapshot.PlayerHp);
        AddMismatch(mismatches, "snapshot.player_max_hp", expected.Snapshot.PlayerMaxHp, actual.Snapshot.PlayerMaxHp);
        AddMismatch(mismatches, "snapshot.hp_lost", expected.Snapshot.CumulativePlayerHpLost, actual.Snapshot.CumulativePlayerHpLost);
        AddMismatch(mismatches, "snapshot.long_term", expected.Snapshot.LongTermResourceValue, actual.Snapshot.LongTermResourceValue);
        AddMismatch(mismatches, "snapshot.anger", expected.Snapshot.AngerCopiesGenerated, actual.Snapshot.AngerCopiesGenerated);
        AddMismatch(mismatches, "snapshot.projected_hp", expected.Snapshot.ProjectedPlayerHp, actual.Snapshot.ProjectedPlayerHp);
        AddMismatch(mismatches, "snapshot.block", expected.Snapshot.PlayerBlock, actual.Snapshot.PlayerBlock);
        AddMismatch(mismatches, "snapshot.enemy_hp", expected.Snapshot.EnemyHp, actual.Snapshot.EnemyHp);
        AddMismatch(mismatches, "snapshot.alive_enemies", expected.Snapshot.AliveEnemyCount, actual.Snapshot.AliveEnemyCount);
        AddMismatch(mismatches, "snapshot.energy", expected.Snapshot.Energy, actual.Snapshot.Energy);
        AddMismatch(mismatches, "snapshot.stars", expected.Snapshot.Stars, actual.Snapshot.Stars);
        AddMismatch(mismatches, "snapshot.hand", expected.Snapshot.HandCount, actual.Snapshot.HandCount);
        AddMismatch(mismatches, "snapshot.stolen", expected.Snapshot.OutstandingStolenResource, actual.Snapshot.OutstandingStolenResource);
        AddMismatch(mismatches, "snapshot.turn", expected.Snapshot.Turn, actual.Snapshot.Turn);
        AddMismatch(mismatches, "snapshot.shuffles", expected.Snapshot.ShufflesCrossed, actual.Snapshot.ShufflesCrossed);
        AddMismatch(mismatches, "snapshot.boundary", expected.Snapshot.BoundaryReason, actual.Snapshot.BoundaryReason);

        if (mismatches.Count == 0)
            return;
        throw new InvalidOperationException(
            $"搜索确定性比较 {comparison} 产生了不同结果：" +
            $"mismatches={string.Join(',', mismatches)} " +
            $"expected={DescribeResult(expected)} actual={DescribeResult(actual)}。");
    }

    private static void AddMismatch<T>(
        ICollection<string> mismatches,
        string name,
        T expected,
        T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            mismatches.Add(name);
    }

    private static bool ActionsEquivalent(
        IReadOnlyList<PlanAction> expected,
        IReadOnlyList<PlanAction> actual)
        => SequencesEquivalent(expected, actual, ActionEquivalent);

    private static bool ActionEquivalent(PlanAction expected, PlanAction actual)
        => expected.Kind == actual.Kind
            && expected.Turn == actual.Turn
            && string.Equals(expected.CardId, actual.CardId, StringComparison.Ordinal)
            && expected.CardOccurrence == actual.CardOccurrence
            && expected.TargetIndex == actual.TargetIndex
            && expected.TargetCombatId == actual.TargetCombatId
            && string.Equals(expected.CardTitle, actual.CardTitle, StringComparison.Ordinal)
            && string.Equals(expected.TargetName, actual.TargetName, StringComparison.Ordinal)
            && ChoiceEquivalent(expected.Choice, actual.Choice)
            && OptionalChoicesEquivalent(expected.NestedChoices, actual.NestedChoices)
            && expected.NestedChoicesBeforePrimary == actual.NestedChoicesBeforePrimary
            && expected.PotionSlot == actual.PotionSlot
            && string.Equals(expected.PotionId, actual.PotionId, StringComparison.Ordinal)
            && string.Equals(expected.PotionTitle, actual.PotionTitle, StringComparison.Ordinal)
            && OptionalChoicesEquivalent(expected.TurnStartChoices, actual.TurnStartChoices)
            && OptionalRelicEffectsEquivalent(expected.RelicEffects, actual.RelicEffects)
            && expected.ReplayCount == actual.ReplayCount;

    private static bool ChoicesEquivalent(
        IReadOnlyList<PlanCardChoice> expected,
        IReadOnlyList<PlanCardChoice> actual)
        => SequencesEquivalent(expected, actual, ChoiceEquivalent);

    private static bool OptionalChoicesEquivalent(
        IReadOnlyList<PlanCardChoice>? expected,
        IReadOnlyList<PlanCardChoice>? actual)
        => OptionalSequencesEquivalent(expected, actual, ChoiceEquivalent);

    private static bool ChoiceEquivalent(PlanCardChoice? expected, PlanCardChoice? actual)
    {
        if (expected == null || actual == null)
            return expected == null && actual == null;
        return expected.Effect == actual.Effect
            && expected.SourcePile == actual.SourcePile
            && SequencesEquivalent(expected.Cards, actual.Cards, CardTokenEquivalent)
            && string.Equals(expected.SourceId, actual.SourceId, StringComparison.Ordinal)
            && string.Equals(expected.ContextId, actual.ContextId, StringComparison.Ordinal)
            && expected.Timing == actual.Timing;
    }

    private static bool CardTokenEquivalent(PlanCardToken expected, PlanCardToken actual)
        => string.Equals(expected.CardId, actual.CardId, StringComparison.Ordinal)
            && expected.UpgradeLevel == actual.UpgradeLevel
            && string.Equals(expected.StateKey, actual.StateKey, StringComparison.Ordinal)
            && expected.SourceOccurrence == actual.SourceOccurrence
            && expected.OptionOccurrence == actual.OptionOccurrence
            && string.Equals(expected.Title, actual.Title, StringComparison.Ordinal);

    private static bool OptionalRelicEffectsEquivalent(
        IReadOnlyList<PlanRelicEffect>? expected,
        IReadOnlyList<PlanRelicEffect>? actual)
        => OptionalSequencesEquivalent(expected, actual, RelicEffectEquivalent);

    private static bool RelicEffectEquivalent(PlanRelicEffect expected, PlanRelicEffect actual)
        => string.Equals(expected.RelicId, actual.RelicId, StringComparison.Ordinal)
            && string.Equals(expected.RelicTitle, actual.RelicTitle, StringComparison.Ordinal)
            && string.Equals(expected.Summary, actual.Summary, StringComparison.Ordinal);

    private static bool PredictionGapsEquivalent(
        IReadOnlyList<PredictionGap> expected,
        IReadOnlyList<PredictionGap> actual)
        => SequencesEquivalent(expected, actual, static (left, right) =>
            string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal)
            && string.Equals(left.Method, right.Method, StringComparison.Ordinal)
            && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
            && left.Compensated == right.Compensated);

    private static bool ContinuationEquivalent(
        ContinuationStamp? expected,
        ContinuationStamp? actual)
        => expected == null || actual == null
            ? expected == null && actual == null
            : string.Equals(expected.StateText, actual.StateText, StringComparison.Ordinal);

    private static bool ContinuationsEquivalent(
        IReadOnlyList<CachedContinuation> expected,
        IReadOnlyList<CachedContinuation> actual)
        => SequencesEquivalent(expected, actual, static (left, right) =>
            string.Equals(
                left.ExpectedState.StateText,
                right.ExpectedState.StateText,
                StringComparison.Ordinal)
            && left.StartTurnNumber == right.StartTurnNumber
            && left.ForecastOffset == right.ForecastOffset);

    private static bool IntDictionaryEquivalent(
        IReadOnlyDictionary<int, int> expected,
        IReadOnlyDictionary<int, int> actual)
        => expected.Count == actual.Count
            && expected.All(item => actual.TryGetValue(item.Key, out int value) && value == item.Value);

    private static bool KillsEquivalent(
        IReadOnlyDictionary<int, IReadOnlyList<string>> expected,
        IReadOnlyDictionary<int, IReadOnlyList<string>> actual)
        => expected.Count == actual.Count
            && expected.All(item =>
                actual.TryGetValue(item.Key, out IReadOnlyList<string>? values)
                && item.Value.SequenceEqual(values, StringComparer.Ordinal));

    private static bool OptionalSequencesEquivalent<T>(
        IReadOnlyList<T>? expected,
        IReadOnlyList<T>? actual,
        Func<T, T, bool> equivalent)
    {
        if (expected == null || actual == null)
            return expected == null && actual == null;
        return SequencesEquivalent(expected, actual, equivalent);
    }

    private static bool SequencesEquivalent<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        Func<T, T, bool> equivalent)
    {
        if (expected.Count != actual.Count)
            return false;
        for (int index = 0; index < expected.Count; index++)
        {
            if (!equivalent(expected[index], actual[index]))
                return false;
        }
        return true;
    }

    private static string DescribeResult(SolverResult result)
        => $"{{actions={DescribeActions(result)} action_count={result.BestNode.ActionCount} " +
            $"score={result.BestNode.Score:R} expanded={result.ExpandedNodes} " +
            $"dominated={result.DominatedActionsPruned} top_queue={result.TopQueueActionsDropped} " +
            $"action_admission_protected={result.ActionAdmissionRepresentativesProtected} " +
            $"duplicate_cards={result.DuplicateCardBranchesPruned} transitions={result.TransitionCount} " +
            $"choices={result.ChoiceBranchesEvaluated} shuffles={result.ShuffleBranchesPruned} " +
            $"sold_hp={result.SoldHpBranchesPruned} " +
            $"hp_investment_protected={result.HpInvestmentBranchesProtected} " +
            $"replays={result.ReplayCount} forks={result.ForkCount} " +
            $"reused={result.ReusedNodeSnapshots} tt_pruned={result.TranspositionBranchesPruned} " +
            $"repeatable={result.RepeatableNoProgressBranchesPruned} stand_pat={result.StandPatProbes} " +
            $"turns={result.SearchedTurns} potions={result.PotionCount} future_sold={result.FutureSoldHp} " +
            $"projected_battle_hp_lost={result.ProjectedBattleHpLost} boundary={result.BoundaryReason} " +
            $"turn_setup=[{string.Join(';', result.TurnSetupChoices.Select(DescribeChoice))}] " +
            $"annotations=[sold={DescribeDictionary(result.SoldHpByTurn)} " +
            $"hp={DescribeDictionary(result.HpLostByTurn)} max_block={DescribeDictionary(result.MaxBlockByTurn)} " +
            $"block={DescribeDictionary(result.ActualBlockByTurn)} energy={DescribeDictionary(result.EnergyLeftByTurn)} " +
            $"potions={DescribeDictionary(result.PotionCountByTurn)} " +
            $"potion_cost={DescribeDictionary(result.PotionStrategicCostByTurn)} " +
            $"kills={DescribeKills(result.KillsAfterAction)}] " +
            $"snapshot=[risk={result.Snapshot.HasRisk} dead={result.Snapshot.PlayerDead} " +
            $"enemies_dead={result.Snapshot.AllEnemiesDead} hp={result.Snapshot.PlayerHp} " +
            $"max_hp={result.Snapshot.PlayerMaxHp} hp_lost={result.Snapshot.CumulativePlayerHpLost} " +
            $"long_term={result.Snapshot.LongTermResourceValue} anger={result.Snapshot.AngerCopiesGenerated} " +
            $"projected_hp={result.Snapshot.ProjectedPlayerHp} block={result.Snapshot.PlayerBlock} " +
            $"enemy_hp={result.Snapshot.EnemyHp} alive={result.Snapshot.AliveEnemyCount} " +
            $"energy={result.Snapshot.Energy} stars={result.Snapshot.Stars} hand={result.Snapshot.HandCount} " +
            $"stolen={result.Snapshot.OutstandingStolenResource} turn={result.Snapshot.Turn} " +
            $"shuffles={result.Snapshot.ShufflesCrossed} boundary={result.Snapshot.BoundaryReason} " +
            $"gaps={string.Join(',', result.Snapshot.PredictionGaps)}]}}";

    private static string DescribeActions(SolverResult result)
        => string.Join(',', result.BestNode.Actions.Select(DescribeAction));

    private static string DescribeAction(PlanAction action)
    {
        string identity = action.Kind switch
        {
            PlanActionKind.PlayCard => $"card:{action.CardId}:{action.CardOccurrence}:{action.TargetCombatId}",
            PlanActionKind.UsePotion => $"potion:{action.PotionId}:{action.PotionSlot}:{action.TargetCombatId}",
            PlanActionKind.EndTurn => "end",
            _ => throw new ArgumentOutOfRangeException(nameof(action.Kind), action.Kind, null),
        };
        return $"{action.Turn}:{identity}" +
            $"[choice={DescribeChoice(action.Choice)};" +
            $"nested={DescribeOptionalChoices(action.NestedChoices)};" +
            $"nested_before={action.NestedChoicesBeforePrimary};" +
            $"turn_start={DescribeOptionalChoices(action.TurnStartChoices)};" +
            $"relics={DescribeRelicEffects(action.RelicEffects)};replay={action.ReplayCount}]";
    }

    private static string DescribeChoice(PlanCardChoice? choice)
        => choice == null
            ? "-"
            : $"{choice.Timing}:{choice.SourceId}:{choice.ContextId}:{choice.Effect}:{choice.SourcePile}:" +
                string.Join('+', choice.Cards.Select(DescribeCardToken));

    private static string DescribeCardToken(PlanCardToken token)
        => $"{token.CardId}+{token.UpgradeLevel}:{token.StateKey}:" +
            $"{token.SourceOccurrence}:{token.OptionOccurrence}:{token.Title}";

    private static string DescribeOptionalChoices(IReadOnlyList<PlanCardChoice>? choices)
        => choices == null ? "null" : string.Join('|', choices.Select(DescribeChoice));

    private static string DescribeRelicEffects(IReadOnlyList<PlanRelicEffect>? effects)
        => effects == null
            ? "null"
            : string.Join('|', effects.Select(effect =>
                $"{effect.RelicId}:{effect.RelicTitle}:{effect.Summary}"));

    private static string DescribeDictionary(IReadOnlyDictionary<int, int> values)
        => string.Join(',', values.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}"));

    private static string DescribeKills(IReadOnlyDictionary<int, IReadOnlyList<string>> values)
        => string.Join(',', values
            .OrderBy(item => item.Key)
            .Select(item => $"{item.Key}:{string.Join('+', item.Value)}"));
}
