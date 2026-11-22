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

internal readonly record struct SealedSearchTakeover(
    int Generation,
    SearchTakeoverKind? Kind,
    SolverRouteAdoptionSeed? RouteAdoptionSeed,
    int? CandidateVersion,
    bool StopAfterResult)
{
    public SolverResultScope TargetScope => Kind switch
    {
        SearchTakeoverKind.ApplyCurrentTurn => SolverResultScope.CurrentTurnAdoption,
        SearchTakeoverKind.AdoptRoute => SolverResultScope.RouteAdoption,
        _ => SolverResultScope.SearchCompletion,
    };
}

internal sealed class SolverRouteAdoptionSeed(
    int candidateVersion,
    IReadOnlyList<PlanAction> actions,
    Func<SolverResult> materialize,
    int backendAttempt = 0)
{
    private readonly Lazy<SolverResult> _materialized = new(
        materialize,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public int CandidateVersion { get; } = candidateVersion;
    public IReadOnlyList<PlanAction> Actions { get; } = actions;
    public int BackendAttempt { get; } = backendAttempt;

    public SolverResult Materialize()
        => _materialized.Value;
}

internal sealed class SearchInteractionState(int generation = 0)
{
    private readonly object _gate = new();
    private int _generation = generation;
    private int _backendAttempt;
    private int _acceptingTakeover = 1;
    private SearchTakeoverRequest? _takeoverRequest;
    private Action? _onTakeoverRequested;
    private SolverRouteAdoptionSeed? _renderedRouteAdoptionSeed;

    public SearchProgressDisplayState ProgressDisplay { get; } = new();
    public SolverProgress? Progress;
    public SolverProgress? RenderedProgress { get; private set; }
    public SolverRouteAdoptionSeed? RenderedRouteAdoptionSeed
    {
        get
        {
            lock (_gate)
            {
                return _renderedRouteAdoptionSeed is { } seed && seed.BackendAttempt == _backendAttempt
                    ? seed
                    : null;
            }
        }
        set
        {
            lock (_gate)
            {
                _renderedRouteAdoptionSeed = value is { } seed && seed.BackendAttempt == _backendAttempt
                    ? seed
                    : null;
            }
        }
    }
    public SolverResult? StoppedResult { get; private set; }
    public LiveCombatStamp? StoppedStamp { get; private set; }

    public int CurrentBackendAttempt => Volatile.Read(ref _backendAttempt);
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
    public bool HasCurrentAttemptTurnPreview
    {
        get
        {
            lock (_gate)
            {
                return Volatile.Read(ref Progress) is { CurrentTurnPreview: not null } progress
                    && progress.BackendAttempt == _backendAttempt;
            }
        }
    }

    public void InvalidateRenderedRouteAdoptionSeed()
    {
        lock (_gate)
        {
            _renderedRouteAdoptionSeed = null;
        }
    }

    public void PublishProgress(SolverProgress progress)
    {
        lock (_gate)
        {
            if (progress.BackendAttempt != _backendAttempt
                || (progress.RouteAdoptionSeed is { } seed && seed.BackendAttempt != _backendAttempt))
            {
                return;
            }
            Volatile.Write(ref Progress, progress);
        }
    }

    public bool TryCreateDisplayProgress(long now, out SolverProgress displayProgress)
    {
        lock (_gate)
        {
            SolverProgress? progress = Volatile.Read(ref Progress);
            if (progress == null || progress.BackendAttempt != _backendAttempt
                || ReferenceEquals(progress, RenderedProgress)
                || !ProgressDisplay.TryCreate(progress, now, out displayProgress))
            {
                displayProgress = null!;
                return false;
            }
            RenderedProgress = displayProgress;
            _renderedRouteAdoptionSeed = displayProgress.SpeculativeRoutePreview == null
                ? null
                : (displayProgress.RouteAdoptionSeed is { } seed && seed.BackendAttempt == _backendAttempt
                    ? seed
                    : null);
            return true;
        }
    }

    public void ResetForSearch(int? generation = null)
    {
        lock (_gate)
        {
            _generation = generation ?? (_generation + 1);
            _backendAttempt = 0;
            Volatile.Write(ref _takeoverRequest, null);
            Volatile.Write(ref _acceptingTakeover, 1);
            _onTakeoverRequested = null;
            Volatile.Write(ref Progress, null);
            RenderedProgress = null;
            _renderedRouteAdoptionSeed = null;
        }
        StoppedResult = null;
        StoppedStamp = null;
        ProgressDisplay.Restart(Environment.TickCount64);
    }

    public void SetTakeoverCallback(Action? callback)
    {
        bool invokeNow = false;
        lock (_gate)
        {
            _onTakeoverRequested = callback;
            invokeNow = callback != null && _takeoverRequest != null;
        }
        if (invokeNow)
            callback!();
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
        Action? callback;
        lock (_gate)
        {
            if (!CanAcceptTakeover || _takeoverRequest != null)
                return false;
            if (request.Kind == SearchTakeoverKind.AdoptRoute
                && (request.RouteAdoptionSeed is null || request.RouteAdoptionSeed.BackendAttempt != _backendAttempt))
            {
                return false;
            }
            Volatile.Write(ref _takeoverRequest, request);
            callback = _onTakeoverRequested;
        }
        callback?.Invoke();
        return true;
    }

    public SealedSearchTakeover SealTakeover()
    {
        lock (_gate)
        {
            Volatile.Write(ref _acceptingTakeover, 0);
            SearchTakeoverRequest? request = _takeoverRequest;
            return new SealedSearchTakeover(
                _generation,
                request?.Kind,
                request?.RouteAdoptionSeed,
                request?.RouteAdoptionSeed?.CandidateVersion,
                request?.StopAfterResult == true);
        }
    }

    public bool PrepareFallbackToBuiltIn(int generation, int failedBackendAttempt)
    {
        lock (_gate)
        {
            if (_generation != generation || _takeoverRequest?.StopAfterResult == true)
                return false;
            if (_backendAttempt == failedBackendAttempt)
                _backendAttempt++;
            Volatile.Write(ref Progress, null);
            RenderedProgress = null;
            _renderedRouteAdoptionSeed = null;
            ProgressDisplay.Restart(Environment.TickCount64);
            if (_takeoverRequest?.Kind == SearchTakeoverKind.AdoptRoute)
                Volatile.Write(ref _takeoverRequest, null);
            if (_takeoverRequest == null)
                Volatile.Write(ref _acceptingTakeover, 1);
            return true;
        }
    }

    public SolverResult FinalizeWorkerResult(SolverResult result)
    {
        SealedSearchTakeover sealedTakeover = SealTakeover();
        if (sealedTakeover.Kind == SearchTakeoverKind.AdoptRoute
            && sealedTakeover.RouteAdoptionSeed != null
            && result.ResultScope != SolverResultScope.RouteAdoption)
        {
            return sealedTakeover.RouteAdoptionSeed.Materialize();
        }
        if (sealedTakeover.Kind == SearchTakeoverKind.ApplyCurrentTurn
            && (result.ResultScope != SolverResultScope.CurrentTurnAdoption
                || result.Continuations.Count != 0))
        {
            return result.WithCurrentTurnAdoptionScope();
        }
        return result;
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

    // Worker completion and UI takeover completion are separate boundaries. Setup keeps its
    // native choice session alive after a result, so it must retire the request explicitly.
    public SearchTakeoverRequest? CompleteTakeover()
    {
        lock (_gate)
        {
            SearchTakeoverRequest? completed = _takeoverRequest;
            Volatile.Write(ref _takeoverRequest, null);
            Volatile.Write(ref _acceptingTakeover, 0);
            _renderedRouteAdoptionSeed = null;
            return completed;
        }
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
    int? CombatEndedTurn = null)
{
    public SolverTheftPolicy? TheftPolicy { get; init; }
    public bool ForfeitsEncounterGold { get; init; }
    public bool Survives { get; init; }
    public int DeathSaveUseCount { get; init; }
    public int GrowthHpCredit { get; init; }
    public int GrowthRewardCount { get; init; }
}

internal sealed record SolverFrontierTurn(
    int Turn,
    IReadOnlyList<PlanAction> Actions,
    int HpLost,
    int HpRecovered,
    int EnemyHpLost,
    int EnergyLeft,
    bool CombatEnded)
{
    public IReadOnlyList<PlanCardChoice> TurnStartChoices { get; init; } = [];

    public static IReadOnlyList<PlanCardChoice> ChoicesForTurn(
        int turn,
        int startTurn,
        IReadOnlyList<PlanCardChoice> setupChoices,
        IReadOnlyList<PlanAction> actions)
    {
        IReadOnlyList<PlanCardChoice> preceding = actions
            .FirstOrDefault(action => action.Turn == turn - 1 && action.TurnStartChoices is { Count: > 0 })
            ?.TurnStartChoices ?? [];
        return turn == startTurn
            ? setupChoices.Concat(preceding).ToArray()
            : preceding;
    }

    public static IReadOnlyList<SolverFrontierTurn> FromResult(SolverResult result)
        => result.BestNode.Actions
            .GroupBy(action => action.Turn)
            .OrderBy(group => group.Key)
            .Select(group => new SolverFrontierTurn(
                group.Key,
                group.ToArray(),
                result.HpLostByTurn.GetValueOrDefault(group.Key),
                result.HpRecoveredByTurn.GetValueOrDefault(group.Key),
                result.EnemyHpLostByTurn.GetValueOrDefault(group.Key),
                result.EnergyLeftByTurn.GetValueOrDefault(group.Key),
                result.CombatEndedTurn == group.Key)
            {
                TurnStartChoices = ChoicesForTurn(
                    group.Key,
                    result.StartTurnNumber,
                    result.WasReused ? [] : result.TurnSetupChoices,
                    result.BestNode.Actions),
            })
            .ToArray();
}

internal sealed record SolverCurrentTurnPreview(
    int CandidateVersion,
    int Turn,
    IReadOnlyList<PlanAction> Actions,
    int HpLost,
    int HpRecovered,
    int EnemyHpLost,
    int EnergyLeft,
    bool CombatEnded,
    IReadOnlyList<SolverFrontierTurn>? FrontierTurns = null)
{
    public IReadOnlyList<PlanCardChoice> TurnStartChoices { get; init; } = [];

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
            result.HpRecoveredByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.EnemyHpLostByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.EnergyLeftByTurn.GetValueOrDefault(result.StartTurnNumber),
            result.CombatEndedTurn == result.StartTurnNumber,
            SolverFrontierTurn.FromResult(result))
        {
            TurnStartChoices = result.WasReused ? [] : result.TurnSetupChoices,
        };
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
    SolverRouteAdoptionSeed? RouteAdoptionSeed = null,
    int RequestBudgetMilliseconds = 0,
    int BackendAttempt = 0);
