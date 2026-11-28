using CombatSolver.HotSearch;

namespace CombatSolver.Api;

public sealed record LiveAutoPilotCombatOptions(
    double InterActionDelaySeconds = 0.0,
    int MaxCombatSolveSeconds = 15,
    int SearchMaxDegreeOfParallelism = 4,
    bool FastAnimations = false,
    string? DeploymentSpeedMode = null);

public sealed record LiveCombatTelemetrySnapshot(
    bool CombatActive,
    int TurnNumber,
    bool IsPlayerTurn,
    bool IsSearching,
    bool IsDeploying,
    bool FullAutoEnabled,
    string StatusCode,
    string? LastStopReason,
    int? ProjectedBattleHpLost,
    int? ProjectedFinalTurn,
    IReadOnlyList<string> PlannedActionSummaries,
    long LastActionUtcMs);

public static class LiveAutoPilotApi
{
    public static bool IsLiveAutoPilotMode => Entry.IsLiveAutoPilotMode;

    public static void ConfigureLiveSession(LiveAutoPilotCombatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Entry.IsLiveAutoPilotMode)
            return;

        double delaySeconds = Math.Clamp(options.InterActionDelaySeconds, 0d, 3d);
        int maxSolveSeconds = Math.Clamp(
            options.MaxCombatSolveSeconds,
            SolverSettings.MinimumMaxCombatSolveSeconds,
            SolverSettings.MaximumMaxCombatSolveSeconds);
        int dop = Math.Clamp(
            options.SearchMaxDegreeOfParallelism,
            1,
            HotSearchConstants.MaximumSearchMaxDegreeOfParallelism);
        SolverDeploymentFastMode fastMode = ResolveDeploymentSpeedMode(options.DeploymentSpeedMode, options.FastAnimations);

        SolverSettingsData liveSettings = SolverSettings.CreateLiveDefaults() with
        {
            SolverDisabled = false,
            AutomaticCalculationEnabled = true,
            AutoEnableFullAuto = true,
            OnlineStatisticsEnabled = false,
            StopFullAutoOnCombatEnd = false,
            StopFullAutoOnDeathTurn = false,
            StopFullAutoOnWorseRecalculation = false,
            ShowActTransitionBossHpStrategyHint = false,
            ShowFinalBossHpStrategyHint = false,
            ShowSpeedXWarning = false,
            SearchCompletionNotificationsEnabled = false,
            PotionPolicy = SolverPotionPolicy.Smart,
            RelicStrategyEnabled = true,
            MaxCombatSolveSeconds = maxSolveSeconds,
            SearchMaxDegreeOfParallelism = dop,
            DeploymentFastMode = fastMode,
            DeploymentInterActionDelaySeconds = delaySeconds,
        };

        SolverSettings.ApplyInMemoryOverride(liveSettings);
        SolverController.ApplyPersistentSettings(SolverSettings.Capture());
        Entry.Logger.Info(
            $"[CombatSolver/LiveAutoPilot] SESSION_CONFIGURED delay={delaySeconds:0.###} " +
            $"max_solve_sec={maxSolveSeconds} dop={dop} fast_mode={fastMode}");
    }

    private static SolverDeploymentFastMode ResolveDeploymentSpeedMode(string? mode, bool fastAnimations)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            return mode.Trim().ToLowerInvariant() switch
            {
                "instant" => SolverDeploymentFastMode.Instant,
                "fast" => SolverDeploymentFastMode.Fast,
                "normal" => SolverDeploymentFastMode.Normal,
                "followgame" or "follow_game" or "follow" => SolverDeploymentFastMode.FollowGame,
                _ => fastAnimations ? SolverDeploymentFastMode.Instant : SolverDeploymentFastMode.FollowGame,
            };
        }

        return fastAnimations ? SolverDeploymentFastMode.Fast : SolverDeploymentFastMode.FollowGame;
    }

    public static LiveCombatTelemetrySnapshot CaptureTelemetry()
        => SolverController.CaptureLiveTelemetry();

    public static bool TryResumeFullAuto(string reason)
        => SolverController.TryResumeFullAutoForLive(reason);
}
