using System.Globalization;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal enum PlanActionKind
{
    PlayCard,
    UsePotion,
    EndTurn,
}

internal enum PlanChoiceEffect
{
    MoveToHand,
    MoveToDrawTop,
    Discard,
    Exhaust,
    Upgrade,
    Transform,
    Duplicate,
    Modify,
    Nightmare,
    DiscardAndDraw,
    MoveToHandFreeThisTurn,
    SetFreeThisCombat,
    ApplySly,
    ApplyEthereal,
    ApplyRetain,
    AutoPlayRepeated,
    GenerateToHand,
    ApplyKnowledgeCurse,
    ModDefined,
}

internal enum PlanChoiceTiming
{
    Action,
    PlayerTurnEnd,
    EnemyTurn,
    PlayerTurnStart,
}

internal enum SearchBoundaryReason
{
    None,
    Shuffle,
    NoCards,
    UnsupportedEffect,
    DynamicResolution,
    PendingChoice,
    EventDefeat,
    TurnLimit,
    NodeLimit,
    TimeLimit,
}

internal enum SolverResultScope
{
    SearchCompletion,
    CurrentTurnAdoption,
    RouteAdoption,
}

internal sealed record PlanCardToken(
    string CardId,
    int UpgradeLevel,
    string StateKey,
    int SourceOccurrence,
    int OptionOccurrence,
    string Title);

internal sealed record PlanCardChoice(
    PlanChoiceEffect Effect,
    PileType SourcePile,
    IReadOnlyList<PlanCardToken> Cards,
    string SourceId = "",
    string ContextId = "",
    PlanChoiceTiming Timing = PlanChoiceTiming.Action);

internal sealed record PredictionGap(
    string SourceId,
    string Method,
    string Reason,
    bool Compensated)
{
    public string Key => $"{SourceId}.{Method}";
}

internal sealed record PlanRelicEffect(
    string RelicId,
    string RelicTitle,
    string Summary);

internal readonly record struct PredictedPotionUse(
    int Slot,
    string PotionId,
    int StrategicHpCost,
    bool Automatic);

internal static class CardChoiceSupport
{
    internal static string ChoiceCardKey(CardModel card)
        => HotImportSupport.FormatLiveChoiceCardKey(card);

    internal static bool MatchesToken(CardModel card, PlanCardToken token)
        => card.Id.Entry == token.CardId
            && card.CurrentUpgradeLevel == token.UpgradeLevel;
}

internal sealed record SearchRequestBudgetSnapshot(
    long ExpandedNodes,
    long NodeLimit,
    long ElapsedMilliseconds,
    long TimeLimitMilliseconds)
{
    internal SearchBoundaryReason LimitReason => ExpandedNodes >= NodeLimit
        ? SearchBoundaryReason.NodeLimit
        : ElapsedMilliseconds >= TimeLimitMilliseconds
            ? SearchBoundaryReason.TimeLimit
            : SearchBoundaryReason.None;
}

internal sealed record PlanAction(
    PlanActionKind Kind,
    int Turn,
    string CardId = "",
    int CardOccurrence = 0,
    int TargetIndex = -1,
    uint? TargetCombatId = null,
    string CardTitle = "",
    string TargetName = "",
    PlanCardChoice? Choice = null,
    IReadOnlyList<PlanCardChoice>? NestedChoices = null,
    int NestedChoicesBeforePrimary = 0,
    int PotionSlot = -1,
    string PotionId = "",
    string PotionTitle = "",
    IReadOnlyList<PlanCardChoice>? TurnStartChoices = null,
    IReadOnlyList<PlanRelicEffect>? RelicEffects = null,
    int ReplayCount = 0,
    string CardStateKey = "",
    int CardStateOccurrence = 0,
    bool EndsPlayerTurn = false,
    int CardUpgradeLevel = 0,
    string CardEnchantmentId = "")
{
    public bool IsExecutable => Kind is PlanActionKind.PlayCard or PlanActionKind.UsePotion;
    public string ActionTitle => Kind == PlanActionKind.UsePotion ? PotionTitle : CardTitle;

    public IReadOnlyList<PlanCardChoice> GetActionChoicesInExecutionOrder()
    {
        IReadOnlyList<PlanCardChoice> nested = NestedChoices ?? [];
        if (NestedChoicesBeforePrimary < 0 || NestedChoicesBeforePrimary > nested.Count)
        {
            throw new InvalidOperationException(
                $"动作内前置选择数量越界：{NestedChoicesBeforePrimary}/{nested.Count}。");
        }

        List<PlanCardChoice> ordered = new(nested.Count + (Choice == null ? 0 : 1));
        ordered.AddRange(nested.Take(NestedChoicesBeforePrimary));
        if (Choice != null)
            ordered.Add(Choice);
        ordered.AddRange(nested.Skip(NestedChoicesBeforePrimary));
        return ordered;
    }
}

internal static class HpChangeText
{
    public static string Signed(int netHpChange)
        => netHpChange > 0
            ? $"+{netHpChange}"
            : netHpChange.ToString(CultureInfo.InvariantCulture);

    public static string SignedColored(int netHpChange)
        => netHpChange switch
        {
            > 0 => $"[color=#73c991]{Signed(netHpChange)}[/color]",
            < 0 => $"[color=#ef6b6b]{Signed(netHpChange)}[/color]",
            _ => Signed(netHpChange),
        };
}

/// <summary>搜索完成后交给运行时的轻量计划。</summary>
internal sealed record SelectedSearchPlan(
    IReadOnlyList<PlanAction> Actions,
    int ActionCount,
    double Score);

/// <summary>最终路线的只读标量摘要。</summary>
internal sealed record SolverSnapshot(
    bool HasRisk,
    bool PlayerDead,
    bool AllEnemiesDead,
    int PlayerHp,
    int PlayerMaxHp,
    int CumulativePlayerHpLost,
    int RecoveredPlayerHp,
    int DeathSaveRelicHpRestored,
    int LongTermResourceValue,
    int AngerCopiesGenerated,
    int ProjectedPlayerHp,
    int PlayerBlock,
    int EnemyHp,
    int AliveEnemyCount,
    int Energy,
    int Stars,
    int HandCount,
    int OutstandingStolenResource,
    int Turn,
    int ShufflesCrossed,
    SearchBoundaryReason BoundaryReason,
    IReadOnlyList<PredictionGap> PredictionGaps)
{
    public int DeathSavePotionHpRestored { get; init; }
    public int DeathSaveHpRestored => DeathSaveRelicHpRestored + DeathSavePotionHpRestored;
    public int DeathSaveUseCount { get; init; }
    public int ProjectedDeathSaveUseCount { get; init; }
    public int? UnrecoveredGold { get; init; }
    public int? UnrecoveredCards { get; init; }
    public RelicCounterEvaluation RelicCounters { get; init; }
    public bool ForfeitsEncounterGold { get; init; }
    public int StrategyGoalHpCredit => GrowthHpCredit + RelicCounters.HpCredit;
    public int StrategicHpCredit => StrategyGoalHpCredit + RelicCounters.HealingHpCredit;
    public int StrategyGoalCount => GrowthRewards.Total + RelicCounters.SatisfiedPriority;
    public int GrowthHpCredit { get; init; }
    public GrowthValues GrowthRewards { get; init; }
}

internal sealed record CachedContinuation(
    ContinuationStamp ExpectedState,
    int StartTurnNumber,
    int ForecastOffset);

internal sealed class SolverResult
{
    public SolverInterimResult? ComparisonQuality { get; internal set; }
    public string? ComparisonRootState { get; internal set; }
    public bool WasRestoredFromCache { get; internal set; }
    public SolverResultScope ResultScope { get; internal set; } = SolverResultScope.SearchCompletion;
    public bool SingleSessionSearch { get; internal set; }
    /// <summary>
    /// True when emitted early via <c>onPreliminaryResult</c> after Stage 1 while Stage 2/3 deepening
    /// continues in the background. Preliminary results must never overwrite persistent route caches.
    /// </summary>
    public bool IsPreliminary { get; internal set; }
    /// <summary>
    /// Highest adaptive escalation stage completed for this result (<c>1</c>, <c>2</c>, or <c>3</c>).
    /// </summary>
    public int EscalationStage { get; internal set; } = 1;

    internal SolverResult CloneAsPreliminary(int escalationStage)
    {
        SolverResult copy = (SolverResult)MemberwiseClone();
        copy.IsPreliminary = true;
        copy.EscalationStage = escalationStage;
        return copy;
    }

    public TimeSpan TotalSearchElapsed { get; internal set; }
    public long TotalWorkerAllocatedBytes { get; internal set; }
    public int TotalGen0Collections { get; internal set; }
    public int TotalGen1Collections { get; internal set; }
    public int TotalGen2Collections { get; internal set; }
    public TimeSpan TotalGcPauseDuration { get; internal set; }
    public TimeSpan TotalMaxObservedGcPause { get; internal set; }

    public int MainThreadFrameCount { get; internal set; }
    public int MainThreadFramesOver33Milliseconds { get; internal set; }
    public double MaxMainThreadFrameGapMilliseconds { get; internal set; }
    public double P95MainThreadFrameGapMilliseconds { get; internal set; }
    public double P99MainThreadFrameGapMilliseconds { get; internal set; }
    public int MainThreadFramesOver50Milliseconds { get; internal set; }
    public int MainThreadFramesOver100Milliseconds { get; internal set; }

    public required int StartTurnNumber { get; init; }
    public required IReadOnlyList<PlanCardChoice> TurnSetupChoices { get; init; }
    public ContinuationStamp? TurnSetupPlayState { get; init; }
    public required SelectedSearchPlan BestNode { get; init; }
    public IReadOnlyList<PredictedPotionUse> PotionUses { get; init; } = [];
    public required SolverSnapshot Snapshot { get; init; }
    public required IntentForecast Forecast { get; init; }
    public required int ExpandedNodes { get; init; }
    public long TotalExpandedNodes { get; internal set; }
    public SearchRequestBudgetSnapshot? RequestBudget { get; internal set; }

    public required int DominatedActionsPruned { get; init; }
    public required int TopQueueActionsDropped { get; init; }
    public required int ActionAdmissionRepresentativesProtected { get; init; }
    public required int DuplicateCardBranchesPruned { get; init; }
    public required int ChoiceBranchesEvaluated { get; init; }
    public long TotalChoiceBranchesEvaluated { get; internal set; }
    public required int ShuffleBranchesPruned { get; init; }
    public required int SoldHpBranchesPruned { get; init; }
    public required int HpInvestmentBranchesProtected { get; init; }
    public required int ReplayCount { get; init; }
    public required int ForkCount { get; init; }
    public int RoundReplayPrefixCaptures { get; init; }
    public int RoundReplayPrefixReuses { get; init; }
    public required int TransitionCount { get; init; }
    public long TotalTransitionCount { get; internal set; }
    public required int ReusedNodeSnapshots { get; init; }
    public required int TranspositionBranchesPruned { get; init; }
    public required int RepeatableNoProgressBranchesPruned { get; init; }
    public int PrimaryIncumbentBranchesPruned { get; init; }
    public int PrimaryIncumbentUpdates { get; init; }
    public required int StandPatProbes { get; init; }
    public int ParallelExpansionWaves { get; init; }
    public int ParallelExpansionWorkItems { get; init; }
    public int MaxParallelExpansionConcurrency { get; init; }
    public required int TransitionCacheHits { get; init; }
    public required long WorkerAllocatedBytes { get; init; }
    public required int Gen0Collections { get; init; }
    public required int Gen1Collections { get; init; }
    public required int Gen2Collections { get; init; }
    public required TimeSpan GcPauseDuration { get; init; }
    public TimeSpan MaxObservedGcPause { get; init; }
    public required int WorkerYieldCount { get; init; }

    public required int SearchedTurns { get; init; }
    public required SearchBoundaryReason BoundaryReason { get; init; }
    public required int UnavoidableHpLost { get; init; }
    public required int SoldHp { get; init; }
    public required int FutureSoldHp { get; init; }
    public required int BattleHpLostSoFar { get; init; }
    public int BattleHpRecoveredOrGainedSoFar { get; init; }
    public required int ProjectedBattleHpLost { get; init; }
    public required int BattlePotionsUsedSoFar { get; init; }
    public required string[] BattlePotionIdsUsedSoFar { get; init; }
    public required string[] PlannedPotionIds { get; init; }
    public required PotionRewardOutlook PotionRewardOutlook { get; init; }
    public required int PotionCount { get; init; }
    public required int ExplicitPotionCount { get; init; }
    public int ProjectedBattlePotionCount => BattlePotionsUsedSoFar + PotionCount;
    public required int PotionHpSaved { get; internal set; }
    public required int PotionHpRequired { get; internal set; }
    public required int PotionBranchesRejected { get; init; }
    public required SolverTheftPolicy? TheftPolicy { get; init; }
    public required int OutstandingStolenResource { get; init; }
    public bool ForfeitsEncounterGold => Snapshot.ForfeitsEncounterGold;

    public required IReadOnlyDictionary<int, int> SoldHpByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> HpLostByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> HpRecoveredByTurn { get; init; }
    public required int PostCombatRelicHeal { get; init; }
    public required IReadOnlyDictionary<int, int> EnemyHpLostByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> MaxBlockByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> ActualBlockByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> EnergyLeftByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> PotionCountByTurn { get; init; }
    public required IReadOnlyDictionary<int, int> PotionStrategicCostByTurn { get; init; }
    public required IReadOnlyDictionary<int, IReadOnlyList<string>> KillsAfterAction { get; init; }
    public required int? CombatEndedTurn { get; init; }
    public required int? DeathTurn { get; init; }
    public required bool OnlyDeathRoutesFound { get; init; }
    public required bool IsActEndingBoss { get; init; }
    public required BossHpRelief BossHpRelief { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required IReadOnlyList<CachedContinuation> Continuations { get; init; }

    public int TranspositionCount { get; init; }
    public int TranspositionLimitBypasses { get; init; }
    public int StandPatCacheCount { get; init; }
    public bool WasReused { get; init; }
    public int? ReusedFromTurn { get; init; }
    public bool RecalculatedAfterCompleteProjection { get; internal set; }
    public int? PreviousProjectedBattleHpLost { get; internal set; }
    public int ProjectedBattleHpLossIncrease => PreviousProjectedBattleHpLost is { } previous
        ? Math.Max(0, ProjectedBattleHpLost - previous)
        : 0;
    public string? RecalculationStateDifference { get; internal set; }

    internal bool TryValidateTurnOutcomes(out string? failure)
    {
        (string Name, IReadOnlyDictionary<int, int>? Values)[] tables =
        [
            (nameof(HpLostByTurn), HpLostByTurn),
            (nameof(HpRecoveredByTurn), HpRecoveredByTurn),
            (nameof(EnemyHpLostByTurn), EnemyHpLostByTurn),
            (nameof(SoldHpByTurn), SoldHpByTurn),
            (nameof(MaxBlockByTurn), MaxBlockByTurn),
            (nameof(ActualBlockByTurn), ActualBlockByTurn),
            (nameof(EnergyLeftByTurn), EnergyLeftByTurn),
        ];
        foreach (var table in tables)
        {
            if (table.Values == null)
            {
                failure = $"{table.Name}=null";
                return false;
            }
        }
        int lastActionTurn = BestNode.Actions.Select(action => action.Turn).DefaultIfEmpty(StartTurnNumber).Max();
        bool terminal = Snapshot.PlayerDead || Snapshot.AllEnemiesDead
            || Snapshot.BoundaryReason != SearchBoundaryReason.None;
        HashSet<int> checkedTurns = [];
        foreach (PlanAction action in BestNode.Actions)
        {
            if (action.Turn == lastActionTurn && !terminal
                && action.Kind != PlanActionKind.EndTurn && !action.EndsPlayerTurn)
                continue;
            if (!checkedTurns.Add(action.Turn))
                continue;
            foreach (var table in tables)
            {
                if (!table.Values!.ContainsKey(action.Turn))
                {
                    failure = $"{table.Name}[{action.Turn}] missing";
                    return false;
                }
            }
        }
        failure = null;
        return true;
    }

    internal void AssertCompleteTurnOutcomes()
    {
        if (!TryValidateTurnOutcomes(out string? failure))
            throw new InvalidOperationException($"路线回合统计不完整：{failure}。");
    }

    internal int RequireHpLostForTurn(int turn)
        => HpLostByTurn.TryGetValue(turn, out int hpLost)
            ? hpLost
            : throw new InvalidOperationException($"路线缺少第 {turn} 回合的预计掉血，不能按零伤害执行。");

    public bool TryCreateContinuation(
        ContinuationStamp actual,
        int currentHp,
        BattleDamageSnapshot battleDamage,
        out SolverResult? continuation)
    {
        AssertCompleteTurnOutcomes();
        CachedContinuation? cached = Continuations.FirstOrDefault(item => item.ExpectedState == actual);
        if (cached == null)
        {
            continuation = null;
            return false;
        }
        if (!BestNode.Actions.Any(action => action.Turn == cached.StartTurnNumber))
        {
            continuation = null;
            return false;
        }

        int searchedTurns = Math.Max(1, BestNode.Actions
            .Select(action => action.Turn)
            .Where(turn => turn >= cached.StartTurnNumber)
            .DefaultIfEmpty(cached.StartTurnNumber)
            .Max() - cached.StartTurnNumber + 1);
        int totalRemainingLoss = Math.Max(0, currentHp - Snapshot.PlayerHp);
        int remainingPotionCount = PotionCountByTurn
            .Where(item => item.Key >= cached.StartTurnNumber)
            .Sum(item => item.Value);
        int remainingExplicitPotionCount = BestNode.Actions.Count(action =>
            action.Kind == PlanActionKind.UsePotion
            && action.Turn >= cached.StartTurnNumber);
        int remainingPotionCost = PotionStrategicCostByTurn
            .Where(item => item.Key >= cached.StartTurnNumber)
            .Sum(item => item.Value);
        Dictionary<int, int> soldByTurn = SoldHpByTurn.ToDictionary(item => item.Key, item => item.Value);
        int remainingSold = Math.Min(totalRemainingLoss, SoldHpByTurn
            .Where(item => item.Key >= cached.StartTurnNumber)
            .Sum(item => item.Value));
        int unavoidable = Math.Max(0, battleDamage.HpLostSoFar - battleDamage.SoldHpCommitted)
            + Math.Max(0, totalRemainingLoss - remainingSold);
        IntentForecast slicedForecast = new()
        {
            Rounds = Forecast.Rounds.Skip(cached.ForecastOffset).ToList(),
            HasUnsupportedIntent = Forecast.HasUnsupportedIntent,
            IsExactForModeledDamage = Forecast.IsExactForModeledDamage,
            UnsupportedDetails = Forecast.UnsupportedDetails,
            ApproximationDetails = Forecast.ApproximationDetails,
            MonsterAiCountersByRound = Forecast.MonsterAiCountersByRound.Skip(cached.ForecastOffset).ToList(),
        };
        continuation = new SolverResult
        {
            StartTurnNumber = cached.StartTurnNumber,
            TurnSetupChoices = TurnSetupChoices,
            TurnSetupPlayState = TurnSetupPlayState,
            TranspositionCount = TranspositionCount,
            TranspositionLimitBypasses = TranspositionLimitBypasses,
            StandPatCacheCount = StandPatCacheCount,
            BestNode = BestNode,
            Snapshot = Snapshot,
            Forecast = slicedForecast,
            ExpandedNodes = 0,
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
            ReusedNodeSnapshots = 1,
            TranspositionBranchesPruned = 0,
            RepeatableNoProgressBranchesPruned = 0,
            PrimaryIncumbentBranchesPruned = 0,
            PrimaryIncumbentUpdates = 0,
            StandPatProbes = 0,
            TransitionCacheHits = 0,
            WorkerAllocatedBytes = 0,
            Gen0Collections = 0,
            Gen1Collections = 0,
            Gen2Collections = 0,
            GcPauseDuration = TimeSpan.Zero,
            WorkerYieldCount = 0,
            SearchedTurns = searchedTurns,
            BoundaryReason = BoundaryReason,
            UnavoidableHpLost = unavoidable,
            SoldHp = battleDamage.SoldHpCommitted + remainingSold,
            FutureSoldHp = remainingSold,
            BattleHpLostSoFar = battleDamage.HpLostSoFar,
            BattleHpRecoveredOrGainedSoFar = battleDamage.HpRecoveredOrGainedSoFar,
            ProjectedBattleHpLost = battleDamage.HpLostSoFar + totalRemainingLoss,
            BattlePotionsUsedSoFar = battleDamage.PotionsUsedSoFar,
            BattlePotionIdsUsedSoFar = battleDamage.PotionIdsUsedSoFar,
            PlannedPotionIds = PlannedPotionIds.Skip(PotionCount - remainingPotionCount).ToArray(),
            PotionRewardOutlook = PotionRewardOutlook,
            PotionCount = remainingPotionCount,
            ExplicitPotionCount = remainingExplicitPotionCount,
            PotionHpSaved = remainingPotionCount == 0 ? 0 : PotionHpSaved,
            PotionHpRequired = remainingPotionCost,
            PotionBranchesRejected = 0,
            TheftPolicy = TheftPolicy,
            OutstandingStolenResource = Snapshot.OutstandingStolenResource,
            SoldHpByTurn = soldByTurn,
            HpLostByTurn = HpLostByTurn,
            HpRecoveredByTurn = HpRecoveredByTurn,
            PostCombatRelicHeal = PostCombatRelicHeal,
            EnemyHpLostByTurn = EnemyHpLostByTurn,
            MaxBlockByTurn = MaxBlockByTurn,
            ActualBlockByTurn = ActualBlockByTurn,
            EnergyLeftByTurn = EnergyLeftByTurn,
            PotionCountByTurn = PotionCountByTurn,
            PotionStrategicCostByTurn = PotionStrategicCostByTurn,
            PotionUses = PotionUses,
            KillsAfterAction = KillsAfterAction,
            CombatEndedTurn = CombatEndedTurn,
            DeathTurn = DeathTurn,
            OnlyDeathRoutesFound = OnlyDeathRoutesFound,
            IsActEndingBoss = IsActEndingBoss,
            BossHpRelief = BossHpRelief,
            Elapsed = TimeSpan.Zero,
            Continuations = Continuations.Where(item => item.StartTurnNumber > cached.StartTurnNumber).ToList(),
            WasReused = true,
            ReusedFromTurn = StartTurnNumber,
            RecalculatedAfterCompleteProjection = RecalculatedAfterCompleteProjection,
            PreviousProjectedBattleHpLost = PreviousProjectedBattleHpLost,
            RecalculationStateDifference = RecalculationStateDifference,
        };
        return true;
    }

    public string Format()
    {
        List<string> lines =
        [
            "[b]战斗路线求解器[/b]",
            $"洗牌边界前预计：玩家 {Snapshot.PlayerHp} HP / {Snapshot.PlayerBlock} 格挡；敌方合计 {Snapshot.EnemyHp} HP",
            $"置信度：{ConfidenceText()}　展开 {ExpandedNodes} 节点　{Elapsed.TotalMilliseconds:F0} ms",
            $"动态范围：{SearchedTurns} 回合，边界 {BoundaryReason}；洗牌分支停止 {ShuffleBranchesPruned}",
            $"本局战损：已发生 {BattleHpLostSoFar}，路线预计累计 {ProjectedBattleHpLost}；主动卖血 {SoldHp}",
            BattlePotionsUsedSoFar > 0
                ? $"本局已喝药：{BattlePotionsUsedSoFar} 瓶；路线还需使用 {PotionCount} 瓶"
                : $"路线预计用药：{PotionCount} 瓶",
            "",
        ];
        if (TheftPolicy is { } theftPolicy)
        {
            lines.Insert(
                lines.Count - 1,
                $"偷窃策略：{(theftPolicy == SolverTheftPolicy.PreserveResources ? "保牌/保钱" : "放走")}；未追回资源 {OutstandingStolenResource}");
        }

        for (int turn = StartTurnNumber; turn < StartTurnNumber + SearchedTurns; turn++)
        {
            List<(PlanAction Action, int Index)> indexedActions = BestNode.Actions
                .Select((action, index) => (Action: action, Index: index))
                .Where(item => item.Action.Turn == turn && item.Action.IsExecutable)
                .ToList();
            string playText = indexedActions.Count == 0
                ? "直接结束"
                : string.Join(" | ", indexedActions.Select(item => DescribeWithKills(item.Action, item.Index)));
            int turnHpLost = HpLostByTurn.GetValueOrDefault(turn);
            int turnHpRecovered = HpRecoveredByTurn.GetValueOrDefault(turn);
            string hpLoss = (turnHpLost, turnHpRecovered) switch
            {
                (> 0, > 0) => $"　[color=#ef6b6b]预计掉血 {turnHpLost}[/color]" +
                    $"　[color=#73c991]回血 {turnHpRecovered}[/color]" +
                    $"　净 {HpChangeText.SignedColored(turnHpRecovered - turnHpLost)} HP",
                (> 0, _) => $"　[color=#ef6b6b]预计掉血 {turnHpLost}[/color]",
                (_, > 0) => $"　[color=#73c991]预计回血 {turnHpRecovered}[/color]",
                _ => string.Empty,
            };
            string combatEnd = CombatEndedTurn == turn
                ? "　[color=#73c991][b]战斗结束[/b][/color]"
                : string.Empty;
            lines.Add($"[b]第 {turn} 回合[/b]　{playText}{hpLoss}{combatEnd}");
        }

        if (PostCombatRelicHeal > 0)
            lines.Add($"[color=#73c991]战斗结束后遗物回血 {PostCombatRelicHeal} HP[/color]");

        lines.Add("");
        lines.Add("[color=#d5b46a]评分：不死优先；区分不可避免战损与主动卖血，并综合击杀、输出、易伤、能力牌和费用利用。[/color]");
        IReadOnlyList<string> unmirrored = UnmirroredDetails();
        if (unmirrored.Count > 0)
            lines.Add($"[color=#e86b6b]未镜像：{string.Join("、", unmirrored)}[/color]");
        IReadOnlyList<string> compensated = CompensatedDetails();
        if (compensated.Count > 0)
            lines.Add($"[color=#73c991]求解器已补偿：{string.Join("、", compensated)}[/color]");
        if (Forecast.ApproximationDetails.Count > 0)
            lines.Add($"[color=#e8a05b]近似预测：{string.Join("、", Forecast.ApproximationDetails)}[/color]");
        return string.Join('\n', lines);
    }

    public string ConfidenceText()
    {
        if (Forecast.HasUnsupportedIntent || Snapshot.PredictionGaps.Any(gap => !gap.Compensated))
            return "低（存在未镜像效果）";
        if (!Forecast.IsExactForModeledDamage)
            return "中（后续状态按当前数值估算）";
        return "中高（攻击、牌序与 RNG 已建模）";
    }

    public IReadOnlyList<string> UnmirroredDetails()
    {
        return Snapshot.PredictionGaps
            .Where(gap => !gap.Compensated)
            .Select(gap => $"{gap.Key} [{gap.Reason}]")
            .Concat(Forecast.UnsupportedDetails)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<string> CompensatedDetails()
    {
        return Snapshot.PredictionGaps
            .Where(gap => gap.Compensated)
            .Select(gap => gap.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static string Describe(PlanAction action)
    {
        string title = action.Kind switch
        {
            PlanActionKind.UsePotion => $"{action.PotionTitle}（药水）",
            PlanActionKind.EndTurn => "结束回合",
            _ => action.CardTitle,
        };
        string card = string.IsNullOrEmpty(action.TargetName)
            ? title
            : $"{title}→{action.TargetName}";
        if (action.RelicEffects is { Count: > 0 })
            card += $" [{string.Join("、", action.RelicEffects.Select(DescribeRelicEffect))}]";
        if (action.Choice == null)
            return card;
        if (action.Choice.Cards.Count == 0)
            return $"{card}（不选）";
        string chosen = string.Join("、", action.Choice.Cards.Select(item => item.Title));
        return $"{card}（选 {chosen}）";
    }

    private static string DescribeRelicEffect(PlanRelicEffect effect)
        => string.IsNullOrEmpty(effect.Summary)
            ? effect.RelicTitle
            : $"{effect.RelicTitle}{effect.Summary}";

    public string DescribeWithKills(PlanAction action, int actionIndex)
    {
        string text = Describe(action);
        if (!KillsAfterAction.TryGetValue(actionIndex, out IReadOnlyList<string>? kills) || kills.Count == 0)
            return text;
        return $"{text} [color=#73c991][b]击杀 {string.Join("、", kills)}[/b][/color]";
    }
}

internal static class PlanActionKeyHelper
{
    public static StateFingerprint BuildCycleActionKey(PlanAction action)
    {
        StateFingerprintBuilder key = new();
        key.Add((int)action.Kind);
        key.Add(action.Turn);
        key.Add(action.CardId);
        key.Add(action.CardOccurrence);
        key.Add(action.TargetIndex);
        key.Add(action.TargetCombatId ?? uint.MaxValue);
        key.Add(action.PotionId);
        key.Add(action.PotionSlot);
        key.Add(action.NestedChoicesBeforePrimary);
        AppendCycleChoiceKey(ref key, action.Choice);
        AppendCycleChoiceListKey(ref key, action.NestedChoices);
        AppendCycleChoiceListKey(ref key, action.TurnStartChoices);
        return key.Finish();
    }

    private static void AppendCycleChoiceListKey(
        ref StateFingerprintBuilder key,
        IReadOnlyList<PlanCardChoice>? choices)
    {
        key.Add(choices?.Count ?? -1);
        if (choices == null)
            return;
        foreach (PlanCardChoice choice in choices)
            AppendCycleChoiceKey(ref key, choice);
    }

    private static void AppendCycleChoiceKey(
        ref StateFingerprintBuilder key,
        PlanCardChoice? choice)
    {
        if (choice == null)
        {
            key.Add(-1);
            return;
        }
        key.Add((int)choice.Effect);
        key.Add((int)choice.SourcePile);
        key.Add(choice.SourceId);
        key.Add(choice.ContextId);
        key.Add((int)choice.Timing);
        key.Add(choice.Cards.Count);
        foreach (PlanCardToken card in choice.Cards)
        {
            key.Add(card.CardId);
            key.Add(card.UpgradeLevel);
            key.Add(card.SourceOccurrence);
            key.Add(card.OptionOccurrence);
        }
    }
}
