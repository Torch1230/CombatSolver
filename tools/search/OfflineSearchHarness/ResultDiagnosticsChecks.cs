using System.Text.RegularExpressions;
using CombatSolver;

namespace OfflineSearchHarness;

internal static class ResultDiagnosticsChecks
{
    internal static int Run()
    {
        SolverResult result = CreateResult();
        ProcessMemoryUsage memory = new(4_294_967_419L, 8_589_935_048L);
        string success = SolverDiagnostics.DescribeResult(result, memory);
        Check(Field(success, "process_working_set_bytes") == "4294967419", "Working-set value changed or was narrowed.");
        Check(Field(success, "process_private_bytes") == "8589935048", "Private-byte value changed or was narrowed.");

        ProcessMemoryUsage? unavailable = OperatingSystem.IsWindows()
            ? ProcessMemoryUsage.CaptureWindows(0)
            : null;
        Check(unavailable == null, "Invalid Windows handle must produce unavailable counters.");
        string failure = SolverDiagnostics.DescribeResult(result, unavailable);
        Check(Field(failure, "process_working_set_bytes") == "unavailable", "Failed working-set query was not reported.");
        Check(Field(failure, "process_private_bytes") == "unavailable", "Failed private-byte query was not reported.");
        Check(NormalizeSamples(success) == NormalizeSamples(failure), "Unavailable counters changed other result diagnostics.");

        string live = SolverDiagnostics.DescribeResult(result);
        Check(long.TryParse(Field(live, "process_working_set_bytes"), out long workingSet) && workingSet > 0,
            "Production entry point did not capture the current working set.");
        Check(long.TryParse(Field(live, "process_private_bytes"), out long privateBytes) && privateBytes >= 0,
            "Production entry point did not capture current private bytes.");
        foreach (string output in new[] { success, failure, live })
        {
            Check(output.Contains(" RESULT reused=False reused_from_turn=- expanded=17", StringComparison.Ordinal),
                "Result header was lost.");
            Check(output.Contains(" score=123 final_hp=63 final_block=7 final_enemy_hp=0", StringComparison.Ordinal),
                "Result fields following memory counters were lost.");
            Check(output.Contains(" TURN_OUTCOME turn=3 hp_lost=2 sold_hp=0 max_block=9 actual_block=7 energy_left=1", StringComparison.Ordinal),
                "Turn outcome was lost.");
            Check(output.EndsWith(" ACTION turn=3 kind=EndTurn combat_ended=true", StringComparison.Ordinal),
                "Action suffix was lost or diagnostic formatting stopped early.");
        }
        Console.WriteLine("PASS result diagnostics: exact counters, native failure/unavailable, unchanged result body and live capture");
        return 0;
    }

    private static string Field(string output, string name)
    {
        MatchCollection matches = Regex.Matches(output, $@"(?:^|\s){Regex.Escape(name)}=([^\s]+)");
        Check(matches.Count == 1, $"Expected exactly one {name} field.");
        return matches[0].Groups[1].Value;
    }

    // GC/live-process samples may move between calls; all other output must match.
    private static string NormalizeSamples(string output)
        => Regex.Replace(output,
            @"\b(managed_live_bytes|managed_heap_bytes|managed_fragmented_bytes|process_working_set_bytes|process_private_bytes)=[^\s]+",
            "$1=<sample>");

    private static SolverResult CreateResult() => new()
    {
        StartTurnNumber = 3,
        TurnSetupChoices = [],
        BestNode = new([new(PlanActionKind.EndTurn, Turn: 3)], ActionCount: 1, Score: 123),
        Snapshot = new(
            HasRisk: false, PlayerDead: false, AllEnemiesDead: true,
            PlayerHp: 63, PlayerMaxHp: 80, CumulativePlayerHpLost: 2, RecoveredPlayerHp: 0,
            DeathSaveRelicHpRestored: 0, LongTermResourceValue: 0, AngerCopiesGenerated: 0,
            ProjectedPlayerHp: 63, PlayerBlock: 7, EnemyHp: 0, AliveEnemyCount: 0,
            Energy: 1, Stars: 0, HandCount: 0, OutstandingStolenResource: 0, Turn: 3,
            ShufflesCrossed: 0, BoundaryReason: SearchBoundaryReason.None, PredictionGaps: []),
        Forecast = new()
        {
            Rounds = [], HasUnsupportedIntent = false, IsExactForModeledDamage = true,
            UnsupportedDetails = [], ApproximationDetails = [], MonsterAiCountersByRound = [],
        },
        ExpandedNodes = 17,
        DominatedActionsPruned = 0,
        TopQueueActionsDropped = 0,
        ActionAdmissionRepresentativesProtected = 0,
        DuplicateCardBranchesPruned = 0,
        ChoiceBranchesEvaluated = 0,
        ShuffleBranchesPruned = 0,
        SoldHpBranchesPruned = 0,
        HpInvestmentBranchesProtected = 0,
        ReplayCount = 0,
        ForkCount = 0,
        TransitionCount = 0,
        ReusedNodeSnapshots = 0,
        TranspositionBranchesPruned = 0,
        RepeatableNoProgressBranchesPruned = 0,
        StandPatProbes = 0,
        TransitionCacheHits = 0,
        WorkerAllocatedBytes = 0,
        Gen0Collections = 0,
        Gen1Collections = 0,
        Gen2Collections = 0,
        GcPauseDuration = TimeSpan.Zero,
        WorkerYieldCount = 0,
        SearchedTurns = 1,
        BoundaryReason = SearchBoundaryReason.None,
        UnavoidableHpLost = 2,
        SoldHp = 0,
        FutureSoldHp = 0,
        BattleHpLostSoFar = 0,
        ProjectedBattleHpLost = 2,
        BattlePotionsUsedSoFar = 0,
        BattlePotionIdsUsedSoFar = [],
        PlannedPotionIds = [],
        PotionRewardOutlook = PotionRewardOutlook.None,
        PotionCount = 0,
        ExplicitPotionCount = 0,
        PotionHpSaved = 0,
        PotionHpRequired = 0,
        PotionBranchesRejected = 0,
        TheftPolicy = null,
        OutstandingStolenResource = 0,
        SoldHpByTurn = new Dictionary<int, int>(),
        HpLostByTurn = new Dictionary<int, int> { [3] = 2 },
        HpRecoveredByTurn = new Dictionary<int, int>(),
        PostCombatRelicHeal = 0,
        EnemyHpLostByTurn = new Dictionary<int, int>(),
        MaxBlockByTurn = new Dictionary<int, int> { [3] = 9 },
        ActualBlockByTurn = new Dictionary<int, int> { [3] = 7 },
        EnergyLeftByTurn = new Dictionary<int, int> { [3] = 1 },
        PotionCountByTurn = new Dictionary<int, int>(),
        PotionStrategicCostByTurn = new Dictionary<int, int>(),
        KillsAfterAction = new Dictionary<int, IReadOnlyList<string>>(),
        CombatEndedTurn = 3,
        DeathTurn = null,
        OnlyDeathRoutesFound = false,
        IsActEndingBoss = false,
        BossHpRelief = BossHpRelief.None,
        Elapsed = TimeSpan.FromMilliseconds(42),
        Continuations = [],
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
