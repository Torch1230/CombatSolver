using System.Reflection;
using System.Threading.Channels;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using STS2RitsuLib.Patching.Models;
using CombatSolver.Engine.Common;

namespace CombatSolver;

internal enum NativeChoiceSurfaceKind
{
    ChooseCard,
    SimpleGrid,
    CombatPile,
    Hand,
    HandUpgrade,
}

internal readonly record struct NativeChoiceObservedOption(
    string CardId,
    int UpgradeLevel,
    string StateKey);

internal sealed record NativeChoiceRequest(
    long Sequence,
    NativeChoiceSurfaceKind Surface,
    Player Player,
    IReadOnlyList<CardModel> Options,
    IReadOnlyList<NativeChoiceObservedOption>? ObservedOptions,
    int MinSelect,
    int MaxSelect,
    bool RequiresSurface,
    bool CanSkip,
    bool RequireManualConfirmation,
    string SourceId);

internal sealed record NativeChoiceTrace(
    long Order,
    long Sequence,
    string Owner,
    NativeChoiceSurfaceKind Surface,
    string Stage,
    int Turn,
    long OccurredAtMilliseconds);

internal class NativeChoicePlanMismatchException(string message)
    : InvalidOperationException(message);

internal class NativeChoiceSurfaceMismatchException(string message)
    : InvalidOperationException(message);

internal sealed class NativeChoiceSurfaceTimeoutException(string message)
    : NativeChoiceSurfaceMismatchException(message);

internal sealed class NativeChoicePlanNotRequestedException(string message)
    : NativeChoicePlanMismatchException(message);

internal static class NativeChoiceRuntime
{
    private static readonly MethodInfo CancelHandSelectionMethod = AccessTools.Method(
        typeof(NPlayerHand),
        "CancelHandSelectionIfNecessary")
        ?? throw new MissingMethodException(typeof(NPlayerHand).FullName, "CancelHandSelectionIfNecessary");
    private static readonly List<NativeChoiceSession> Sessions = [];
    private static readonly List<NativeChoiceTrace> Traces = [];
    private static readonly object TraceSync = new();
    private static long _nextSequence;
    private static long _nextTraceOrder;
    private static int _sceneExitCancellationCountForTesting;

    internal static int SceneExitCancellationCountForTesting
        => _sceneExitCancellationCountForTesting;

    internal static IReadOnlyList<NativeChoiceTrace> TraceSnapshotForTesting
    {
        get
        {
            lock (TraceSync)
                return Traces.ToArray();
        }
    }

    internal static void ResetTraceForTesting()
    {
        lock (TraceSync)
        {
            Traces.Clear();
            _nextTraceOrder = 0;
            _sceneExitCancellationCountForTesting = 0;
        }
    }

    public static NativeChoiceSession Begin(
        CombatState combat,
        Player player,
        string owner)
    {
        NativeChoiceSession session = new(combat, player, owner);
        Sessions.Add(session);
        return session;
    }

    public static void Observe(
        NativeChoiceSurfaceKind surface,
        Player player,
        IReadOnlyList<CardModel> options,
        int minSelect,
        int maxSelect,
        bool requiresSurface,
        bool canSkip = false,
        bool requireManualConfirmation = false,
        string sourceId = "")
    {
        if (CardSelectCmd.Selector != null || Sessions.Count == 0)
            return;
        NativeChoiceSession session = Sessions[^1];
        if (!ReferenceEquals(session.Player, player)
            || !ReferenceEquals(session.Combat, CombatManager.Instance.DebugOnlyGetState()))
        {
            return;
        }

        CardModel[] observedOptions = options.ToArray();
        NativeChoiceObservedOption[]? observedIdentities = requiresSurface
            ? null
            : observedOptions
                .Select(option => new NativeChoiceObservedOption(
                    option.Id.Entry,
                    option.CurrentUpgradeLevel,
                    CardChoiceSupport.ChoiceCardKey(option)))
                .ToArray();
        int optionCount = observedOptions.Length;
        session.Enqueue(new NativeChoiceRequest(
            Interlocked.Increment(ref _nextSequence),
            surface,
            player,
            observedOptions,
            observedIdentities,
            Math.Min(minSelect, optionCount),
            Math.Min(maxSelect, optionCount),
            requiresSurface,
            canSkip,
            requireManualConfirmation,
            sourceId));
    }

    internal static void End(NativeChoiceSession session)
    {
        int index = Sessions.FindLastIndex(candidate => ReferenceEquals(candidate, session));
        if (index < 0)
            throw new InvalidOperationException($"原生选牌会话 {session.Owner} 没有注册为活动会话。");
        Sessions.RemoveAt(index);
    }

    internal static bool CancelActiveHandSelectionForSceneExit()
    {
        if (Sessions.Count == 0
            || NPlayerHand.Instance is not { IsInCardSelection: true } hand
            || !hand.IsInsideTree())
        {
            return false;
        }

        Sessions[^1].ReleaseVisibleSurface();
        CancelHandSelectionMethod.Invoke(hand, null);
        _sceneExitCancellationCountForTesting++;
        Entry.Logger.Info(
            "[CombatSolver/Test] NATIVE_CHOICE_CANCELED reason=scene_exit surface=Hand");
        return true;
    }

    internal static void CancelActiveHandSelection()
    {
        if (NPlayerHand.Instance is { IsInCardSelection: true } hand)
            CancelHandSelectionMethod.Invoke(hand, null);
    }

    internal static void RecordTrace(
        NativeChoiceSession session,
        NativeChoiceRequest request,
        string stage)
    {
        int turn = session.Player.PlayerCombatState?.TurnNumber ?? -1;
        lock (TraceSync)
        {
            Traces.Add(new NativeChoiceTrace(
                ++_nextTraceOrder,
                request.Sequence,
                session.Owner,
                request.Surface,
                stage,
                turn,
                System.Environment.TickCount64));
        }
    }
}

internal sealed class NativeChoiceSession : IDisposable
{
    private readonly Channel<NativeChoiceRequest> _requests = Channel.CreateUnbounded<NativeChoiceRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<NativeChoiceRequest> _firstVisibleRequest = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allPlansConsumed = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _firstSurfaceSync = new();
    private readonly HashSet<long> _visibleTraceSequences = [];
    private Task<NativeChoiceSurfaceLock>? _firstSurfaceTask;
    private NativeChoiceSurfaceLock? _firstSurfaceLock;
    private CancellationTokenSource? _driverCancellation;
    private IReadOnlyList<PlanCardChoice>? _plans;
    private Task? _driver;
    private bool _producerCompleted;
    private bool _detached;
    private bool _disposed;

    public NativeChoiceSession(CombatState combat, Player player, string owner)
    {
        Combat = combat;
        Player = player;
        Owner = owner;
    }

    public CombatState Combat { get; }
    public Player Player { get; }
    public string Owner { get; }
    public bool HasVisibleRequest => _firstVisibleRequest.Task.IsCompletedSuccessfully;
    public bool IsVisibleSurfaceOpen
        => _firstVisibleRequest.Task.IsCompletedSuccessfully
           && NativeChoiceSurface.IsVisible(_firstVisibleRequest.Task.Result.Surface);
    public bool IsVisibleChoicePending
    {
        get
        {
            if (!_firstVisibleRequest.Task.IsCompletedSuccessfully)
                return false;
            NativeChoiceSurfaceKind surface = _firstVisibleRequest.Task.Result.Surface;
            return surface is NativeChoiceSurfaceKind.Hand or NativeChoiceSurfaceKind.HandUpgrade
                ? NPlayerHand.Instance?.IsInCardSelection == true
                : NativeChoiceSurface.IsVisible(surface);
        }
    }

    public void Enqueue(NativeChoiceRequest request)
    {
        if (_producerCompleted)
            throw new InvalidOperationException($"原生选牌会话 {Owner} 完成后又收到选择请求。");
        if (!_requests.Writer.TryWrite(request))
            throw new InvalidOperationException($"原生选牌会话 {Owner} 无法记录选择请求。");
        if (request.RequiresSurface)
            _firstVisibleRequest.TrySetResult(request);
        NativeChoiceRuntime.RecordTrace(this, request, "Requested");
        Entry.Logger.Info(
            $"[CombatSolver/Test] NATIVE_CHOICE_REQUEST owner={Owner} sequence={request.Sequence} " +
            $"surface={request.Surface} visible={request.RequiresSurface} source={request.SourceId} " +
            $"options={request.Options.Count} select={request.MinSelect}..{request.MaxSelect} " +
            $"manual_confirmation={request.RequireManualConfirmation} " +
            $"card_selection_rng={request.Player.RunState.Rng.CombatCardSelection.Counter()}");
    }

    public async Task<bool> WaitForFirstVisibleSurfaceAsync(
        NGame host,
        Task phaseTask,
        CancellationToken token)
    {
        Task winner = await Task.WhenAny(_firstVisibleRequest.Task, phaseTask);
        if (ReferenceEquals(winner, phaseTask))
        {
            await phaseTask;
            return false;
        }

        NativeChoiceRequest request = await _firstVisibleRequest.Task.WaitAsync(token);
        Task<NativeChoiceSurfaceLock> surfaceTask;
        lock (_firstSurfaceSync)
        {
            _firstSurfaceTask ??= NativeChoiceSurface.WaitAndLockAsync(host, request, token);
            surfaceTask = _firstSurfaceTask;
        }
        _firstSurfaceLock = await surfaceTask;
        RecordVisibleOnce(request);
        Entry.Logger.Info(
            $"[CombatSolver/Test] NATIVE_CHOICE_VISIBLE owner={Owner} sequence={request.Sequence} " +
            $"surface={request.Surface}");
        return true;
    }

    public void RecordSearchStarted()
    {
        NativeChoiceRequest request = _firstVisibleRequest.Task.IsCompletedSuccessfully
            ? _firstVisibleRequest.Task.Result
            : throw new InvalidOperationException($"原生选牌会话 {Owner} 尚未显示页面。");
        NativeChoiceRuntime.RecordTrace(this, request, "SearchStarted");
    }

    public void RecordPlanReady()
    {
        NativeChoiceRequest request = _firstVisibleRequest.Task.IsCompletedSuccessfully
            ? _firstVisibleRequest.Task.Result
            : throw new InvalidOperationException($"原生选牌会话 {Owner} 尚未显示页面。");
        NativeChoiceRuntime.RecordTrace(this, request, "PlanReady");
    }

    public void SetPlanAndStartDriving(
        NGame host,
        IReadOnlyList<PlanCardChoice> plans,
        CancellationToken token)
    {
        if (_plans != null)
            throw new InvalidOperationException($"原生选牌会话 {Owner} 已经安装计划。");
        _plans = plans;
        if (plans.Count == 0)
            _allPlansConsumed.TrySetResult();
        _driverCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _driver = DriveAsync(host, _driverCancellation.Token);
        if (UnattendedAsyncActivityTracker.IsRequestActive)
            _driver = UnattendedAsyncActivityTracker.Track(_driver);
    }

    public async Task WaitForAllPlansConsumedAsync(CancellationToken token)
    {
        Task driver = _driver
            ?? throw new InvalidOperationException($"原生选牌会话 {Owner} 尚未启动驱动器。");
        Task winner = await Task.WhenAny(_allPlansConsumed.Task, driver).WaitAsync(token);
        if (ReferenceEquals(winner, driver))
        {
            await driver;
            throw new InvalidOperationException($"原生选牌会话 {Owner} 在消费全部计划前结束。");
        }
        await _allPlansConsumed.Task.WaitAsync(token);
    }

    public async Task AwaitProducerAndCompleteAsync(Task producerTask)
    {
        Task driver = _driver
            ?? throw new InvalidOperationException($"原生选牌会话 {Owner} 尚未启动驱动器。");
        Task winner = await Task.WhenAny(producerTask, driver);
        if (ReferenceEquals(winner, driver) && !producerTask.IsCompleted)
            await driver;
        await producerTask;
        await CompleteAsync();
    }

    public async Task AwaitPhaseAsync(Task phaseTask)
    {
        if (_driver == null)
        {
            await phaseTask;
            return;
        }
        Task winner = await Task.WhenAny(phaseTask, _driver);
        if (ReferenceEquals(winner, _driver) && !phaseTask.IsCompleted)
            await _driver;
        await phaseTask;
    }

    public async Task CompleteAsync()
    {
        if (_producerCompleted)
            throw new InvalidOperationException($"原生选牌会话 {Owner} 被重复完成。");
        _producerCompleted = true;
        _requests.Writer.Complete();
        if (_driver != null)
            await _driver;
    }

    public async Task CompleteAndDetachAsync()
    {
        if (_producerCompleted)
            throw new InvalidOperationException($"原生选牌会话 {Owner} 被重复完成。");
        _producerCompleted = true;
        _requests.Writer.Complete();
        Detach();
        if (_driver != null)
            await _driver;
    }

    public void ReleaseVisibleSurface()
    {
        _firstSurfaceLock?.Dispose();
        _firstSurfaceLock = null;
    }

    public void CancelVisibleSurfaceForReplan()
    {
        NativeChoiceSurface.Cancel(_firstVisibleRequest.Task.IsCompletedSuccessfully
            ? _firstVisibleRequest.Task.Result.Surface
            : null);
        ReleaseVisibleSurface();
    }

    public async Task LockVisibleSurfaceForSearchAsync(NGame host, CancellationToken token)
    {
        NativeChoiceRequest request = await _firstVisibleRequest.Task.WaitAsync(token);
        ReleaseVisibleSurface();
        _firstSurfaceLock = await NativeChoiceSurface.WaitAndLockAsync(host, request, token);
        RecordVisibleOnce(request);
    }

    internal async Task SelectVisibleCardsForTesting(
        NGame host,
        IReadOnlyList<CardModel> selected,
        CancellationToken token)
    {
        if (!UnattendedTestRunner.IsActive)
            throw new InvalidOperationException("只有无人值守测试可以模拟玩家完成原生选牌。");

        NativeChoiceRequest request = await _firstVisibleRequest.Task.WaitAsync(token);
        NativeChoiceSurfaceLock surfaceLock = await NativeChoiceSurface.WaitAndLockAsync(
            host,
            request,
            token);
        using (surfaceLock)
            await NativeChoiceSurface.SelectAsync(host, surfaceLock, request, selected, token);
        NativeChoiceRuntime.RecordTrace(this, request, "ManualSelected");
    }

    private async Task DriveAsync(NGame host, CancellationToken token)
    {
        IReadOnlyList<PlanCardChoice> plans = _plans
            ?? throw new InvalidOperationException($"原生选牌会话 {Owner} 缺少计划。");
        int planIndex = 0;
        await foreach (NativeChoiceRequest request in _requests.Reader.ReadAllAsync(token))
        {
            if (request.Options.Count == 0
                && request.MinSelect == 0
                && request.MaxSelect == 0)
            {
                bool consumedEmptyPlan = planIndex < plans.Count
                    && plans[planIndex].Cards.Count == 0;
                if (consumedEmptyPlan)
                {
                    planIndex++;
                    if (planIndex == plans.Count)
                        _allPlansConsumed.TrySetResult();
                }
                Entry.Logger.Info(
                    $"[CombatSolver/Test] NATIVE_CHOICE_NO_OP owner={Owner} sequence={request.Sequence} " +
                    $"surface={request.Surface} source={request.SourceId} " +
                    $"consumed_empty_plan={consumedEmptyPlan}");
                NativeChoiceRuntime.RecordTrace(this, request, "NoOp");
                continue;
            }

            if (planIndex >= plans.Count)
            {
                throw new NativeChoicePlanNotRequestedException(
                    $"原生选牌会话 {Owner} 收到计划外选择：{request.SourceId}/{request.Surface}。");
            }

            PlanCardChoice plan = plans[planIndex];
            IReadOnlyList<CardModel> selected;
            if (request.RequiresSurface)
            {
                NativeChoiceSurfaceLock surfaceLock;
                if (_firstSurfaceLock is { } first && first.Request.Sequence == request.Sequence)
                {
                    surfaceLock = first;
                    _firstSurfaceLock = null;
                }
                else
                {
                    surfaceLock = await NativeChoiceSurface.WaitAndLockAsync(host, request, token);
                }
                RecordVisibleOnce(request);
                // A visible page can remain open while the search runs. Match only after the page
                // is locked, against its current semantic identity, so stale plans fail safely.
                selected = ResolvePlannedCards(plan, request, useObservedIdentity: false);
                using (surfaceLock)
                    await NativeChoiceSurface.SelectAsync(host, surfaceLock, request, selected, token);
            }
            else
            {
                // An implicit all-card selection can mutate each selected card before the driver
                // consumes the request. Preserve the identity seen at the actual choice boundary.
                selected = ResolvePlannedCards(plan, request, useObservedIdentity: true);
                ValidateImplicitSelection(request, selected);
                Entry.Logger.Info(
                    $"[CombatSolver/Test] NATIVE_CHOICE_IMPLICIT owner={Owner} sequence={request.Sequence} " +
                    $"source={plan.SourceId} cards={string.Join(',', plan.Cards.Select(card => card.CardId))}");
            }
            Entry.Logger.Info(
                $"[CombatSolver/Test] NATIVE_CHOICE_SELECTED owner={Owner} sequence={request.Sequence} " +
                $"surface={request.Surface} source={plan.SourceId} context={plan.ContextId} " +
                $"planned={string.Join(',', plan.Cards.Select(card =>
                    $"{card.CardId}+{card.UpgradeLevel}#src{card.SourceOccurrence}/opt{card.OptionOccurrence}"))} " +
                $"selected={string.Join(',', selected.Select(CardChoiceSupport.ChoiceCardKey))}");
            NativeChoiceRuntime.RecordTrace(this, request, "Selected");
            planIndex++;
            if (planIndex == plans.Count)
                _allPlansConsumed.TrySetResult();
        }

        while (planIndex < plans.Count && plans[planIndex].Cards.Count == 0)
            planIndex++;
        if (planIndex != plans.Count)
        {
            PlanCardChoice next = plans[planIndex];
            throw new NativeChoicePlanNotRequestedException(
                $"原生选牌会话 {Owner} 仍有 {plans.Count - planIndex} 个计划没有被游戏请求；" +
                $"下一个={next.SourceId}/{next.Effect}/{next.ContextId}。");
        }
    }

    private static IReadOnlyList<CardModel> ResolvePlannedCards(
        PlanCardChoice plan,
        NativeChoiceRequest request,
        bool useObservedIdentity)
    {
        if (useObservedIdentity && request.ObservedOptions == null)
            throw new InvalidOperationException("隐式原生选牌请求缺少冻结候选身份。");

        List<CardModel> selected = [];
        foreach (PlanCardToken token in plan.Cards)
        {
            CardModel? card = useObservedIdentity
                ? request.Options
                    .Select((option, index) => (Option: option, Observed: request.ObservedOptions![index]))
                    .Where(option => option.Observed.CardId == token.CardId
                        && option.Observed.UpgradeLevel == token.UpgradeLevel)
                    .Skip(token.OptionOccurrence)
                    .Select(static option => option.Option)
                    .FirstOrDefault()
                : request.Options
                    .Where(option => CardChoiceSupport.MatchesToken(option, token))
                    .Skip(token.OptionOccurrence)
                    .FirstOrDefault();
            if (card == null)
            {
                throw new NativeChoicePlanMismatchException(
                    $"原生选牌页面找不到 {token.CardId}+{token.UpgradeLevel}#{token.OptionOccurrence}；" +
                    $"观测候选={(request.ObservedOptions == null ? "-" : string.Join(',', request.ObservedOptions.Select(option => option.StateKey)))}；" +
                    $"当前候选={string.Join(',', request.Options.Select(CardChoiceSupport.ChoiceCardKey))}。");
            }
            if (selected.Contains(card))
                throw new InvalidOperationException($"原生选牌计划重复选择了 {token.CardId}。");
            selected.Add(card);
        }

        if (selected.Count < request.MinSelect || selected.Count > request.MaxSelect)
        {
            throw new NativeChoicePlanMismatchException(
                $"原生选牌页面要求选择 {request.MinSelect}..{request.MaxSelect} 张，" +
                $"当前计划选择 {selected.Count} 张。");
        }
        return selected;
    }

    private void RecordVisibleOnce(NativeChoiceRequest request)
    {
        if (_visibleTraceSequences.Add(request.Sequence))
            NativeChoiceRuntime.RecordTrace(this, request, "Visible");
    }

    private static void ValidateImplicitSelection(
        NativeChoiceRequest request,
        IReadOnlyList<CardModel> selected)
    {
        if (request.Options.Count == 0)
        {
            if (selected.Count != 0)
                throw new InvalidOperationException("无候选的原生选择包含计划卡牌。");
            return;
        }
        if (selected.Count != request.Options.Count
            || selected.Any(card => !request.Options.Contains(card)))
        {
            throw new InvalidOperationException(
                $"原版会隐式选择全部 {request.Options.Count} 张牌，但计划选择了 {selected.Count} 张。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _driverCancellation?.Cancel();
        _driverCancellation?.Dispose();
        _driverCancellation = null;
        if (!_producerCompleted)
            _requests.Writer.TryComplete();
        ReleaseVisibleSurface();
        Detach();
    }

    private void Detach()
    {
        if (_detached)
            return;
        NativeChoiceRuntime.End(this);
        _detached = true;
    }
}

internal sealed class NativeChoiceSurfaceLock(
    NativeChoiceRequest request,
    Node surface,
    Control blocker,
    NPlayerHand? hand,
    bool handUnhandledInput) : IDisposable
{
    private bool _disposed;

    public NativeChoiceRequest Request { get; } = request;
    public Node Surface { get; } = surface;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (GodotObject.IsInstanceValid(blocker))
        {
            blocker.GetParent()?.RemoveChild(blocker);
            blocker.QueueFree();
        }
        if (hand != null && GodotObject.IsInstanceValid(hand))
            hand.SetProcessUnhandledInput(handUnhandledInput);
    }
}

internal static class NativeChoiceSurface
{
    private const long SurfaceTimeoutMilliseconds = 30_000;
    private const ulong ChooseCardMinimumOpenMilliseconds = 400;
    private const ulong OtherSurfaceMinimumOpenMilliseconds = 250;
    private const ulong MultiSelectStepMilliseconds = 150;

    public static bool IsVisible(NativeChoiceSurfaceKind kind)
        => FindSurface(kind) is CanvasItem canvas
           && canvas.IsInsideTree()
           && canvas.IsVisibleInTree();

    public static async Task<NativeChoiceSurfaceLock> WaitAndLockAsync(
        NGame host,
        NativeChoiceRequest request,
        CancellationToken token)
    {
        long deadline = System.Environment.TickCount64 + SurfaceTimeoutMilliseconds;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Node? surface = FindSurface(request.Surface);
            if (surface is CanvasItem canvas && surface.IsInsideTree() && canvas.IsVisibleInTree())
            {
                Control blocker = new()
                {
                    Name = $"CombatSolverChoiceBlocker{request.Sequence}",
                    MouseFilter = Control.MouseFilterEnum.Stop,
                    FocusMode = Control.FocusModeEnum.All,
                };
                surface.AddChild(blocker);
                blocker.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
                blocker.MoveToFront();
                NPlayerHand? hand = surface as NPlayerHand;
                bool handUnhandledInput = hand?.IsProcessingUnhandledInput() ?? false;
                hand?.SetProcessUnhandledInput(false);
                blocker.CallDeferred(Control.MethodName.GrabFocus);
                return new NativeChoiceSurfaceLock(request, surface, blocker, hand, handUnhandledInput);
            }
            if (System.Environment.TickCount64 >= deadline)
            {
                throw new NativeChoiceSurfaceTimeoutException(
                    $"30 秒内没有出现原生选牌页面 {request.Surface}（来源 {request.SourceId}）。");
            }
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    public static async Task SelectAsync(
        NGame host,
        NativeChoiceSurfaceLock surfaceLock,
        NativeChoiceRequest request,
        IReadOnlyList<CardModel> selected,
        CancellationToken token)
    {
        ulong minimumOpenMilliseconds = request.Surface == NativeChoiceSurfaceKind.ChooseCard
            ? ChooseCardMinimumOpenMilliseconds
            : OtherSurfaceMinimumOpenMilliseconds;
        ulong openedAt = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - openedAt < minimumOpenMilliseconds)
        {
            token.ThrowIfCancellationRequested();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        surfaceLock.Dispose();

        switch (request.Surface)
        {
            case NativeChoiceSurfaceKind.ChooseCard:
                SelectChooseCard(surfaceLock.Surface, request, selected);
                break;
            case NativeChoiceSurfaceKind.SimpleGrid:
            case NativeChoiceSurfaceKind.CombatPile:
                await SelectGridAsync(host, surfaceLock.Surface, request, selected, token);
                break;
            case NativeChoiceSurfaceKind.Hand:
            case NativeChoiceSurfaceKind.HandUpgrade:
                await SelectHandAsync(host, surfaceLock.Surface, request, selected, token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Surface), request.Surface, null);
        }
        await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static void SelectChooseCard(
        Node surface,
        NativeChoiceRequest request,
        IReadOnlyList<CardModel> selected)
    {
        if (selected.Count == 0)
        {
            if (!request.CanSkip)
                throw new InvalidOperationException("原生三选一页面不可跳过，但计划没有选择卡牌。");
            NChoiceSelectionSkipButton skip = surface.GetNode<NChoiceSelectionSkipButton>("SkipButton");
            skip.EmitSignal(NClickableControl.SignalName.Released, skip);
            return;
        }
        if (selected.Count != 1)
            throw new InvalidOperationException($"原生三选一页面计划选择了 {selected.Count} 张牌。");
        NCardHolder holder = Descendants<NCardHolder>(surface)
            .FirstOrDefault(candidate => ReferenceEquals(candidate.CardModel, selected[0]))
            ?? throw new NativeChoicePlanMismatchException(
                $"原生三选一页面没有 {selected[0].Id.Entry} 的卡牌节点。");
        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
    }

    private static async Task SelectGridAsync(
        NGame host,
        Node surface,
        NativeChoiceRequest request,
        IReadOnlyList<CardModel> selected,
        CancellationToken token)
    {
        NCardGrid grid = Descendants<NCardGrid>(surface).Single();
        foreach (CardModel card in selected)
        {
            NGridCardHolder holder = await FindGridCardHolderAsync(
                host,
                grid,
                request,
                card,
                token);
            grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
            token.ThrowIfCancellationRequested();
            await WaitMillisecondsAsync(host, MultiSelectStepMilliseconds, token);
        }
        if (request.RequireManualConfirmation)
        {
            NConfirmButton confirm = surface.GetNode<NConfirmButton>("%Confirm");
            await WaitForConfirmationAsync(
                host,
                confirm,
                () => surface.IsInsideTree()
                    && surface is CanvasItem canvas
                    && canvas.IsVisibleInTree(),
                "原生网格页面在选择完成后关闭，确认计划无法提交。",
                "原生网格页面选择完成后确认按钮在 30 秒内仍不可用。",
                token);
            confirm.EmitSignal(NClickableControl.SignalName.Released, confirm);
        }
    }

    private static async Task<NGridCardHolder> FindGridCardHolderAsync(
        NGame host,
        NCardGrid grid,
        NativeChoiceRequest request,
        CardModel card,
        CancellationToken token)
    {
        NGridCardHolder? holder = grid.CurrentlyDisplayedCardHolders
            .FirstOrDefault(candidate => ReferenceEquals(candidate.CardModel, card));
        if (holder != null)
            return holder;

        float top = grid.Get(NCardGrid.PropertyName.ScrollLimitTop).AsSingle();
        float bottom = grid.Get(NCardGrid.PropertyName.ScrollLimitBottom).AsSingle();
        int sweepSteps = Math.Max(1, request.Options.Count);
        for (int step = 1; step <= sweepSteps; step++)
        {
            token.ThrowIfCancellationRequested();
            grid.SetScrollPosition(Mathf.Lerp(top, bottom, step / (float)sweepSteps));
            grid.Call(NCardGrid.MethodName.AllocateCardHolders);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            holder = grid.CurrentlyDisplayedCardHolders
                .FirstOrDefault(candidate => ReferenceEquals(candidate.CardModel, card));
            if (holder != null)
                return holder;
        }

        throw new NativeChoicePlanMismatchException(
            $"原生网格页面滚动完整个卡组后仍没有 {card.Id.Entry} 的卡牌节点。");
    }

    private static async Task SelectHandAsync(
        NGame host,
        Node surface,
        NativeChoiceRequest request,
        IReadOnlyList<CardModel> selected,
        CancellationToken token)
    {
        NPlayerHand hand = (NPlayerHand)surface;
        foreach (CardModel card in selected)
        {
            NCardHolder holder = hand.GetCardHolder(card)
                ?? throw new NativeChoicePlanMismatchException(
                    $"原生手牌选择没有 {card.Id.Entry} 的卡牌节点。");
            holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
            token.ThrowIfCancellationRequested();
            await WaitMillisecondsAsync(host, MultiSelectStepMilliseconds, token);
        }
        if (!hand.IsInCardSelection)
        {
            if (request.RequireManualConfirmation)
                throw new InvalidOperationException("原生手牌选择在手动确认前已经结束。");
            return;
        }
        NConfirmButton confirm = hand.GetNode<NConfirmButton>("%SelectModeConfirmButton");
        await WaitForConfirmationAsync(
            host,
            confirm,
            () => hand.IsInsideTree() && hand.IsInCardSelection,
            "原生手牌选择页面在确认前关闭，确认计划无法提交。",
            "原生手牌选择完成后确认按钮在 30 秒内仍不可用。",
            token);
        confirm.EmitSignal(NClickableControl.SignalName.Released, confirm);
    }

    private static async Task WaitForConfirmationAsync(
        NGame host,
        NConfirmButton confirm,
        Func<bool> surfaceIsActive,
        string closedMessage,
        string timeoutMessage,
        CancellationToken token)
    {
        long deadline = System.Environment.TickCount64 + SurfaceTimeoutMilliseconds;
        while (!confirm.IsEnabled)
        {
            token.ThrowIfCancellationRequested();
            if (!surfaceIsActive())
                throw new NativeChoiceSurfaceMismatchException(closedMessage);
            if (System.Environment.TickCount64 >= deadline)
                throw new NativeChoiceSurfaceTimeoutException(timeoutMessage);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static async Task WaitMillisecondsAsync(
        NGame host,
        ulong milliseconds,
        CancellationToken token)
    {
        ulong startedAt = Time.GetTicksMsec();
        while (Time.GetTicksMsec() - startedAt < milliseconds)
        {
            token.ThrowIfCancellationRequested();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    public static void Cancel(NativeChoiceSurfaceKind? kind)
    {
        if (kind is NativeChoiceSurfaceKind.Hand or NativeChoiceSurfaceKind.HandUpgrade)
        {
            NativeChoiceRuntime.CancelActiveHandSelection();
            return;
        }

        if (kind is not { } surfaceKind
            || FindSurface(surfaceKind) is not IOverlayScreen screen
            || NOverlayStack.Instance is not { } stack)
        {
            return;
        }
        stack.Remove(screen);
    }

    private static Node? FindSurface(NativeChoiceSurfaceKind kind)
        => kind switch
        {
            NativeChoiceSurfaceKind.ChooseCard => NOverlayStack.Instance?.Peek() as NChooseACardSelectionScreen,
            NativeChoiceSurfaceKind.SimpleGrid => NOverlayStack.Instance?.Peek() as NSimpleCardSelectScreen,
            NativeChoiceSurfaceKind.CombatPile => NOverlayStack.Instance?.Peek() as NCombatPileCardSelectScreen,
            NativeChoiceSurfaceKind.Hand or NativeChoiceSurfaceKind.HandUpgrade
                when NPlayerHand.Instance?.IsInCardSelection == true => NPlayerHand.Instance,
            _ => null,
        };

    private static IEnumerable<T> Descendants<T>(Node node) where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T match)
                yield return match;
            foreach (T descendant in Descendants<T>(child))
                yield return descendant;
        }
    }
}

internal sealed class ChooseCardObservationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_native_choice_choose_card";
    public static string Description => "观测原版战斗三选一卡牌页面";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext), typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(bool)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Prefix(IReadOnlyList<CardModel> cards, Player player, bool canSkip)
        => NativeChoiceRuntime.Observe(
            NativeChoiceSurfaceKind.ChooseCard,
            player,
            cards,
            0,
            1,
            cards.Count > 0,
            canSkip);
}

internal sealed class SimpleGridObservationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_native_choice_simple_grid";
    public static string Description => "观测原版战斗卡牌网格页面";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext), typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(CardSelectorPrefs)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Prefix(IReadOnlyList<CardModel> cardsIn, Player player, CardSelectorPrefs prefs)
        => NativeChoiceRuntime.Observe(
            NativeChoiceSurfaceKind.SimpleGrid,
            player,
            cardsIn,
            prefs.MinSelect,
            prefs.MaxSelect,
            cardsIn.Count > 0 && (prefs.RequireManualConfirmation || cardsIn.Count > prefs.MinSelect),
            requireManualConfirmation: prefs.RequireManualConfirmation);
}

internal sealed class RewardGridObservationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_native_choice_reward_grid";
    public static string Description => "观测原版战斗生成牌网格页面";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGridForRewards),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext), typeof(List<CardCreationResult>), typeof(Player), typeof(CardSelectorPrefs)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Prefix(List<CardCreationResult> cards, Player player, CardSelectorPrefs prefs)
    {
        IReadOnlyList<CardModel> options = cards.Select(result => result.Card).ToArray();
        NativeChoiceRuntime.Observe(
            NativeChoiceSurfaceKind.SimpleGrid,
            player,
            options,
            prefs.MinSelect,
            prefs.MaxSelect,
            options.Count > 0 && (prefs.RequireManualConfirmation || options.Count > prefs.MinSelect),
            requireManualConfirmation: prefs.RequireManualConfirmation);
    }
}

internal sealed class CombatPileObservationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_native_choice_combat_pile";
    public static string Description => "观测原版战斗牌堆选择页面";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromCombatPile),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext), typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Prefix(
        CardPile pile,
        Player player,
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter)
    {
        IReadOnlyList<CardModel> options = (filter == null ? pile.Cards : pile.Cards.Where(filter)).ToArray();
        NativeChoiceRuntime.Observe(
            NativeChoiceSurfaceKind.CombatPile,
            player,
            options,
            prefs.MinSelect,
            prefs.MaxSelect,
            options.Count > 0 && (prefs.RequireManualConfirmation || options.Count > prefs.MinSelect),
            requireManualConfirmation: prefs.RequireManualConfirmation);
    }
}

internal sealed class HandObservationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_native_choice_hand";
    public static string Description => "观测原版战斗手牌选择动画";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>), typeof(AbstractModel)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Prefix(
        Player player,
        CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter,
        AbstractModel source)
    {
        IReadOnlyList<CardModel> options = player.PlayerCombatState!.Hand.Cards
            .Where(filter ?? (_ => true))
            .ToArray();
        NativeChoiceRuntime.Observe(
            NativeChoiceSurfaceKind.Hand,
            player,
            options,
            prefs.MinSelect,
            prefs.MaxSelect,
            options.Count > 0 && (prefs.RequireManualConfirmation || options.Count > prefs.MinSelect),
            requireManualConfirmation: prefs.RequireManualConfirmation,
            sourceId: source.Id.Entry);
    }
}

internal sealed class HandUpgradeObservationPatch : IPatchMethod
{
    public static string PatchId => "combat_solver_native_choice_hand_upgrade";
    public static string Description => "观测原版战斗手牌升级页面";
    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForUpgrade),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext), typeof(Player), typeof(AbstractModel)]),
    ];

    [HarmonyPriority(Priority.First)]
    public static void Prefix(Player player, AbstractModel source)
    {
        IReadOnlyList<CardModel> options = player.PlayerCombatState!.Hand.Cards
            .Where(card => card.IsUpgradable)
            .ToArray();
        NativeChoiceRuntime.Observe(
            NativeChoiceSurfaceKind.HandUpgrade,
            player,
            options,
            1,
            1,
            options.Count > 1,
            requireManualConfirmation: false,
            sourceId: source.Id.Entry);
    }
}
