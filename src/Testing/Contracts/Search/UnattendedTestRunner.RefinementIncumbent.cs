using System.Collections.Concurrent;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRefinementIncumbentAsync(CombatState live, Player player)
    {
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, 70);
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 18);
        await PowerCmd.Apply<StrengthPower>(new ThrowingPlayerChoiceContext(),
            live.Enemies.Single(), 12, null, null);
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_SILENT", Pile = "Hand", UpgradeLevels = 1 });
        await InjectCardAsync(live, player, new() { CardId = "DEFEND_SILENT", Pile = "Hand", UpgradeLevels = 1 });
        bool allNative = _request.ScenarioId == "NATIVE-HEALING-ALL-ENCOUNTERS";
        bool knownSourcePolicy = allNative || _request.ScenarioId == "KNOWN-HEALING-MEMBERS";
        if (allNative)
            player.AddRelicInternal(ModelDb.Relic<PenNib>().ToMutable());
        if (knownSourcePolicy)
            await InjectCardAsync(live, player, new() { CardId = "ALCHEMIZE", Pile = "Exhaust" });
        SetEnergy(player, 3);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        SolverDisplayNames displayNames = SolverDisplayNames.Capture(live);
        string before = ContinuationStamp.CaptureLive(live).StateText;
        var damage = BattleDamageTracker.Observe(live);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null) with
        {
            FixedBudget = true, VerifyIncrementalSearch = true, MaxDegreeOfParallelism = 2,
            DetailedDiagnostics = false, MeasurePhasePerformance = false,
            BudgetOverrideMilliseconds = null, StopAtAcceptableBattleHpLoss = false,
            UseBeamWidthPortfolio = true, BeamWidthPortfolioWidths = [12, 8, 18],
            UseNoveltyPortfolio = false, IgnoreLongTermRewards = false,
            PotionPolicy = SolverPotionPolicy.Disabled,
            PotionStrategy = new(SolverPotionPolicy.Disabled, []),
            RelicTargets = allNative ? [new(RelicCounterId.PenNib, 2, 2, 0, 10)] : [],
        };
        policy = policy with { Profile = policy.Profile with
        {
            BeamWidth = 12, MaxExpandedNodes = 800, SoftTimeBudgetMilliseconds = 20_000,
            StopPortfolioAtHpTarget = false,
        } };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SolverResult control = await Task.Run(() => CombatSearchCoordinator.Solve(root,
            displayNames, damage,
            policy with { DisableRefinementIncumbentForTesting = true }, deadline.Token, null));
        if (!(root.CanCertifyRemainingHealing || root.UsesKnownNativeHealingPolicy)
            || knownSourcePolicy && root.CanCertifyRemainingHealing
            || !control.Snapshot.AllEnemiesDead
            || control.Snapshot.PlayerDead || control.Snapshot.HasRisk
            || control.BoundaryReason != SearchBoundaryReason.None || control.ProjectedBattleHpLost <= 0)
            throw new InvalidOperationException($"Refinement incumbent requires a complete native nonzero-loss control: "
                + $"certified={root.CanCertifyRemainingHealing} knownPolicy={root.UsesKnownNativeHealingPolicy} "
                + $"won={control.Snapshot.AllEnemiesDead} dead={control.Snapshot.PlayerDead} risk={control.Snapshot.HasRisk} "
                + $"boundary={control.BoundaryReason} loss={control.ProjectedBattleHpLost}.");

        var messages = new ConcurrentQueue<string>();
        SearchDiagnosticsSink original = policy.Diagnostics;
        SearchPolicySnapshot observed = policy with { Diagnostics = new(message =>
        {
            messages.Enqueue(message);
            original.Info(message);
        }, original.Debug, original.PathObserver) };
        SolverResult candidate = await Task.Run(() => CombatSearchCoordinator.Solve(root,
            displayNames, damage, observed, deadline.Token, null));
        if (!messages.Any(message => message.Contains("BEAM_REFINEMENT_INCUMBENT ", StringComparison.Ordinal))
            || !candidate.Snapshot.AllEnemiesDead || candidate.Snapshot.PlayerDead || candidate.Snapshot.HasRisk
            || candidate.ProjectedBattleHpLost > control.ProjectedBattleHpLost
            || candidate.ExplicitPotionCount != 0
            || ContinuationStamp.CaptureLive(live).StateText != before)
            throw new InvalidOperationException("Refinement did not inherit a native incumbent or lost control quality/live isolation.");
        if (CombatSearchCoordinator.BuildRefinementPrimarySearchIncumbent(root, policy, null, control) == null
            || CombatSearchCoordinator.BuildRefinementPrimarySearchIncumbent(root,
                policy with { DisableRefinementIncumbentForTesting = true }, null, control) != null
            || CombatSearchCoordinator.BuildRefinementPrimarySearchIncumbent(root,
                policy with { GrowthOpportunityTargets = GrowthOpportunityTargets.UnboundedForTesting("refinement_guard") },
                null, control) != null)
            throw new InvalidOperationException("Refinement incumbent eligibility ignored disable/growth rules.");

        if (allNative && (!CombatBeamSolver.CanUseStrictHpRelicBound(root, policy)
            || CombatSearchCoordinator.BuildRefinementPrimarySearchIncumbent(root,
                policy with { RelicTargets = [new(RelicCounterId.PenNib, 2, 2, 1, 10)] }, null, control) != null
            || candidate.PrimaryIncumbentBranchesPruned <= 0))
            throw new InvalidOperationException($"Native counter-bound pruning did not execute or admitted a paid objective: "
                + $"pruned={candidate.PrimaryIncumbentBranchesPruned}.");

        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Exhaust" });
        CombatRootSnapshot unknown = CombatRootSnapshot.Capture(live);
        if (unknown.CanCertifyRemainingHealing || !unknown.UsesKnownNativeHealingPolicy
            || CombatSearchCoordinator.BuildRefinementPrimarySearchIncumbent(unknown, policy, null, control) == null)
            throw new InvalidOperationException("Known native cards outside the closed set must inherit the policy bound.");
        _completedChecks.Add("RefinementIncumbent:NativeRoot:Dop2:StrictIncremental:ControlQuality:Inherited:GrowthGuard:KnownExhaustCard:LiveIsolation");
        if (allNative)
            _completedChecks.Add($"AllNativeHealing:{player.Character.Id.Entry}:{live.Encounter!.Id.Entry}:ZeroAllowanceCounter:"
                + $"PaidAllowanceGuard:pruned={candidate.PrimaryIncumbentBranchesPruned}:"
                + $"loss={control.ProjectedBattleHpLost}/{candidate.ProjectedBattleHpLost}:"
                + $"transitions={control.TotalTransitionCount}/{candidate.TotalTransitionCount}");
        if (!knownSourcePolicy)
            await AssertOpeningPlanIncumbentAsync(live, player);
    }

    private async Task AssertOpeningPlanIncumbentAsync(CombatState live, Player player)
    {
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, 70);
        await CreatureCmd.SetCurrentHp(live.Enemies.Single(), 36);
        foreach (string id in new[] { "SPEEDSTER", "ADRENALINE", "STRIKE_SILENT", "DEFEND_SILENT" })
            await InjectCardAsync(live, player, new() { CardId = id, Pile = "Hand", UpgradeLevels = 1 });
        bool knownSourcePolicy = _request.ScenarioId == "KNOWN-HEALING-OPENING";
        if (knownSourcePolicy)
            await InjectCardAsync(live, player, new() { CardId = "ALCHEMIZE", Pile = "Exhaust" });
        SetEnergy(player, 3);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        SolverDisplayNames displayNames = SolverDisplayNames.Capture(live);
        string before = ContinuationStamp.CaptureLive(live).StateText;
        var damage = BattleDamageTracker.Observe(live);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), live, false, null) with
        {
            FixedBudget = true, VerifyIncrementalSearch = true, MaxDegreeOfParallelism = 2,
            DetailedDiagnostics = false, MeasurePhasePerformance = false,
            BudgetOverrideMilliseconds = null, StopAtAcceptableBattleHpLoss = false,
            UseBeamWidthPortfolio = true, BeamWidthPortfolioWidths = [12, 8, 18],
            UseNoveltyPortfolio = false, PotionPolicy = SolverPotionPolicy.Disabled,
            PotionStrategy = new(SolverPotionPolicy.Disabled, []),
        };
        policy = policy with { Profile = policy.Profile with
        {
            BeamWidth = 12, MaxExpandedNodes = 2_000, SoftTimeBudgetMilliseconds = 20_000,
            StopPortfolioAtHpTarget = false,
        } };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        SolverResult control = await Task.Run(() => CombatSearchCoordinator.Solve(root,
            displayNames, damage,
            policy with { DisableOpeningPlanIncumbentForTesting = true }, deadline.Token, null));
        var messages = new ConcurrentQueue<string>();
        SearchDiagnosticsSink original = policy.Diagnostics;
        SearchPolicySnapshot observed = policy with { Diagnostics = new(message =>
        {
            messages.Enqueue(message);
            original.Info(message);
        }, original.Debug, original.PathObserver) };
        SolverResult candidate = await Task.Run(() => CombatSearchCoordinator.Solve(root,
            displayNames, damage, observed, deadline.Token, null));
        if (!(root.CanCertifyRemainingHealing || root.UsesKnownNativeHealingPolicy)
            || knownSourcePolicy && root.CanCertifyRemainingHealing
            || !control.Snapshot.AllEnemiesDead
            || control.Snapshot.PlayerDead || control.Snapshot.HasRisk
            || !candidate.Snapshot.AllEnemiesDead || candidate.Snapshot.PlayerDead
            || candidate.Snapshot.HasRisk || candidate.ProjectedBattleHpLost > control.ProjectedBattleHpLost
            || candidate.ExplicitPotionCount != 0
            || messages.Count(message => message.Contains("EARLY_OPENING_PLAN_INCUMBENT start", StringComparison.Ordinal)) != 1
            || !messages.Any(message => message.Contains("BEAM_REFINEMENT_INCUMBENT member=0 ", StringComparison.Ordinal))
            || messages.Count(message => message.Contains("PLAN_SEARCH result ", StringComparison.Ordinal)) != 1
            || ContinuationStamp.CaptureLive(live).StateText != before)
            throw new InvalidOperationException($"Early opening plans lost native control quality, isolation, or single execution: "
                + $"control={control.ProjectedBattleHpLost}/{control.BoundaryReason}/{control.Snapshot.AllEnemiesDead} "
                + $"candidate={candidate.ProjectedBattleHpLost}/{candidate.BoundaryReason}/{candidate.Snapshot.AllEnemiesDead}.");
        _completedChecks.Add("OpeningPlanIncumbent:NativeRoot:Dop2:StrictIncremental:ControlQuality:FirstMemberSeed:SingleExecution:LiveIsolation");
    }
}
