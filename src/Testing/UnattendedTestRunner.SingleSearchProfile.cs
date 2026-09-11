using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertSingleSearchProfileAsync(CombatState combat)
    {
        SolverSettingsData legacy = SolverSettings.DeserializeForTesting(
            """{"performanceMigrationVersion":243,"performancePreset":"Custom","shortTimeLimitSeconds":1,"deepTimeLimitSeconds":123,"shortBeamWidth":5,"deepBeamWidth":77,"deepMaxExpandedNodes":12345}""");
        SolverSearchProfile migrated = SolverSettings.ResolvePerformanceValues(legacy).Profile;
        if (migrated.SoftTimeBudgetMilliseconds != 123000 || migrated.BeamWidth != 77 || migrated.MaxExpandedNodes != 12345)
            throw new InvalidOperationException("Legacy deep custom budget was not preserved.");
        SolverSettingsData restored = SolverSettings.RoundTripForTesting(legacy);
        if (SolverSettings.ResolvePerformanceValues(restored).Profile != migrated)
            throw new InvalidOperationException("Unified budget changed after save/reload.");
        string serialized = JsonSerializer.Serialize(restored);
        if (serialized.Contains("LegacyDeep", StringComparison.Ordinal) || serialized.Contains("ShortTimeLimitSeconds", StringComparison.Ordinal))
            throw new InvalidOperationException("Legacy budget fields leaked into new settings.");
        foreach (var (preset, seconds) in new[] { (SolverPerformancePreset.Low, 60), (SolverPerformancePreset.Medium, 120),
                     (SolverPerformancePreset.High, 180), (SolverPerformancePreset.VeryHigh, 300) })
            if (SolverSettings.ResolvePerformanceValues(SolverSettings.ApplyPerformancePreset(legacy, preset)).Profile.SoftTimeBudgetMilliseconds != seconds * 1000)
                throw new InvalidOperationException("Preset budget changed during migration.");
        AssertRequiredPotionAuditSelectionAndTotals();

        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        policy = policy with { Profile = policy.Profile with { MaxExpandedNodes = 128, SoftTimeBudgetMilliseconds = 1500 },
            FixedBudget = true, BudgetOverrideMilliseconds = 1500, VerifyIncrementalSearch = false };
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        List<string> phases = [];
        SolverResult result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy,
            CancellationToken.None, progressCallback: progress => phases.Add(progress.Phase)));
        if (phases.Count == 0 || phases.Any(phase => phase.Contains("快速搜索") || phase.Contains("深化搜索"))
            || result.TotalExpandedNodes < result.ExpandedNodes || result.TotalSearchElapsed < result.Elapsed)
            throw new InvalidOperationException("Unified search progress or cumulative totals are invalid.");
        _completedChecks.Add("SingleSearch:LegacyDeepCustom:RoundTrip:FourPresets:OneProgressPhase:TotalWork:FixedBudget");
    }
}
