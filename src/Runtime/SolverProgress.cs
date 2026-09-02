namespace CombatSolver;

internal enum SearchTakeoverKind
{
    ApplyCurrentTurn,
    AdoptRoute,
}

internal sealed record SearchTakeoverRequest(
    SearchTakeoverKind Kind,
    SolverRouteAdoptionSeed? RouteAdoptionSeed = null,
    bool StopAfterResult = false);

internal sealed class SolverRouteAdoptionSeed(
    int candidateVersion,
    IReadOnlyList<PlanAction> actions,
    Func<SolverResult> materialize)
{
    private readonly Lazy<SolverResult> _materialized = new(
        materialize,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public int CandidateVersion { get; } = candidateVersion;
    public IReadOnlyList<PlanAction> Actions { get; } = actions;

    public SolverResult Materialize()
        => _materialized.Value;
}

internal sealed class SearchInteractionState
{
    private readonly object _gate = new();
    private int _acceptingTakeover = 1;
    private SearchTakeoverRequest? _takeoverRequest;

    public SearchProgressDisplayState ProgressDisplay { get; } = new();
    public SolverProgress? Progress;
    public SolverProgress? RenderedProgress { get; private set; }
    public SolverRouteAdoptionSeed? RenderedRouteAdoptionSeed { get; set; }
    public SolverResult? StoppedResult { get; private set; }
    public LiveCombatStamp? StoppedStamp { get; private set; }

    public SearchTakeoverRequest? CurrentTakeoverRequest
        => Volatile.Read(ref _takeoverRequest);
    public bool CanAcceptTakeover
        => Volatile.Read(ref _acceptingTakeover) != 0;
    public bool IsApplyingCurrentTurn
        => CurrentTakeoverRequest?.Kind == SearchTakeoverKind.ApplyCurrentTurn;
    public bool IsAdoptingRoute
        => CurrentTakeoverRequest?.Kind == SearchTakeoverKind.AdoptRoute;
    public bool StopRequested
        => CurrentTakeoverRequest?.StopAfterResult == true;

    public void PublishProgress(SolverProgress progress)
        => Volatile.Write(ref Progress, progress);

    public bool TryCreateDisplayProgress(long now, out SolverProgress displayProgress)
    {
        SolverProgress? progress = Volatile.Read(ref Progress);
        if (progress == null || ReferenceEquals(progress, RenderedProgress)
            || !ProgressDisplay.TryCreate(progress, now, out displayProgress))
        {
            displayProgress = null!;
            return false;
        }
        RenderedProgress = displayProgress;
        return true;
    }

    public void ResetForSearch()
    {
        lock (_gate)
        {
            Volatile.Write(ref _takeoverRequest, null);
            Volatile.Write(ref _acceptingTakeover, 1);
        }
        Progress = null;
        RenderedProgress = null;
        RenderedRouteAdoptionSeed = null;
        StoppedResult = null;
        StoppedStamp = null;
        ProgressDisplay.Restart(Environment.TickCount64);
    }

    public bool RequestApplyCurrentTurn()
        => RequestTakeover(new SearchTakeoverRequest(SearchTakeoverKind.ApplyCurrentTurn));

    public bool RequestAdoptRoute(SolverRouteAdoptionSeed seed, bool stopAfterResult = false)
        => RequestTakeover(new SearchTakeoverRequest(
            SearchTakeoverKind.AdoptRoute,
            seed,
            stopAfterResult));

    private bool RequestTakeover(SearchTakeoverRequest request)
    {
        lock (_gate)
        {
            if (!CanAcceptTakeover || _takeoverRequest != null)
                return false;
            Volatile.Write(ref _takeoverRequest, request);
            return true;
        }
    }

    public SolverResult FinalizeWorkerResult(SolverResult result)
    {
        lock (_gate)
        {
            Volatile.Write(ref _acceptingTakeover, 0);
            SearchTakeoverRequest? request = CurrentTakeoverRequest;
            if (request?.Kind == SearchTakeoverKind.AdoptRoute
                && request.RouteAdoptionSeed != null
                && result.ResultScope != SolverResultScope.RouteAdoption)
            {
                return request.RouteAdoptionSeed.Materialize();
            }
            return result;
        }
    }

    public void PreserveStoppedResult(SolverResult result, LiveCombatStamp stamp)
    {
        StoppedResult = result;
        StoppedStamp = stamp;
    }

    public SolverResult? TakeStoppedResult(LiveCombatStamp currentStamp)
    {
        SolverResult? result = StoppedStamp == currentStamp ? StoppedResult : null;
        StoppedResult = null;
        StoppedStamp = null;
        return result;
    }
}

internal sealed record SolverInterimResult(
    bool Won,
    int OutstandingStolenResource,
    int ProjectedBattleHpLost,
    int StrategicHpDeficit,
    int PotionStrategicCost,
    int ProjectedBattlePotionCount,
    int EnemyHp,
    double Score,
    int? CombatEndedTurn = null);

internal sealed record SolverFrontierTurn(
    int Turn,
    IReadOnlyList<PlanAction> Actions,
    int HpLost,
    int EnemyHpLost,
    int EnergyLeft,
    bool CombatEnded)
{
    public static IReadOnlyList<SolverFrontierTurn> FromResult(SolverResult result)
        => result.BestNode.Actions
            .GroupBy(action => action.Turn)
            .OrderBy(group => group.Key)
            .Select(group => new SolverFrontierTurn(
                group.Key,
                group.ToArray(),
                result.HpLostByTurn.GetValueOrDefault(group.Key),
                result.EnemyHpLostByTurn.GetValueOrDefault(group.Key),
                result.EnergyLeftByTurn.GetValueOrDefault(group.Key),
                result.CombatEndedTurn == group.Key))
            .ToArray();
}

internal sealed record SolverCurrentTurnPreview(
    int CandidateVersion,
    int Turn,
    IReadOnlyList<PlanAction> Actions,
    int HpLost,
    int EnemyHpLost,
    int EnergyLeft,
    bool CombatEnded,
    IReadOnlyList<SolverFrontierTurn>? FrontierTurns = null)
{
    public static SolverCurrentTurnPreview FromResult(
        SolverResult result,
        int candidateVersion = 0)
        => new(
            candidateVersion,
            result.StartTurnNumber,
            result.BestNode.Actions
                .Where(action => action.Turn == result.StartTurnNumber)
                .ToArray(),
            result.HpLostByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.EnemyHpLostByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.EnergyLeftByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.CombatEndedTurn == result.StartTurnNumber,
            SolverFrontierTurn.FromResult(result));
}

internal sealed record SolverSpeculativeRoutePreview(
    int CandidateVersion,
    int StartTurnNumber,
    int ProjectedBattlePotionCount,
    int ProjectedBattleHpLost,
    bool CombatEnded,
    bool OnlyDeathRoutesFound,
    bool HasRisk,
    IReadOnlyList<SolverFrontierTurn> Turns)
{
    public static SolverSpeculativeRoutePreview FromResult(
        SolverResult result,
        int candidateVersion = 0)
        => new(
            candidateVersion,
            result.StartTurnNumber,
            result.ProjectedBattlePotionCount,
            result.ProjectedBattleHpLost,
            result.CombatEndedTurn.HasValue,
            result.OnlyDeathRoutesFound,
            result.Snapshot.HasRisk,
            SolverFrontierTurn.FromResult(result));
}

internal sealed record SolverProgress(
    int StartTurnNumber,
    int CurrentTurnNumber,
    int CompletedTurnLayers,
    int PlayDepth,
    int ExpandedNodes,
    long ReviewedWorldlines,
    int MaxNodes,
    int FrontierNodes,
    int EndedNodes,
    long ElapsedMilliseconds,
    string Phase,
    SolverInterimResult? CurrentBestResult = null,
    SolverCurrentTurnPreview? CurrentTurnPreview = null,
    SolverSpeculativeRoutePreview? SpeculativeRoutePreview = null,
    SolverRouteAdoptionSeed? RouteAdoptionSeed = null);
