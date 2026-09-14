using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Simulation;

// Frames contain data and a program counter. They never retain a CLR stack, task, or callback.
internal interface ICombatPredictionExecutionFrame
{
    void PrepareFork(PredictionForkContext context) { }
    ICombatPredictionExecutionFrame Fork(PredictionForkContext context);
    bool Resume(CombatPredictionSimulator simulator);
    IEnumerable<CombatPredictionHistoryEntry> DeferredEntries => [];
    IEnumerable<CardPlay> ActiveCardPlays => [];
}

internal interface ICombatPredictionExecutionContinuationState
{
    bool CanCaptureExecutionContinuation { get; }
    bool CanSealExecutionPrefix { get; }
    IDisposable DetachPendingExecutionChoice();
    bool TryCaptureExecutionScopes(CombatPredictionSimulator simulator, out ICombatPredictionExecutionScopes? scopes);
    PredictedCard? FindExecutionCard(CardModel card);
}

internal interface ICombatPredictionExecutionScopes
{
    void PrepareFork(PredictionForkContext context);
    ICombatPredictionExecutionScopes Fork(PredictionForkContext context);
    IDisposable Enter(CombatPredictionSimulator simulator);
}

internal sealed record PredictionExecutionStep(PredictionTraceFrame? Trace, int DrawDepth,
    ICombatPredictionExecutionScopes? Scopes, ICombatPredictionExecutionFrame Frame);
internal sealed record PredictionExecutionContinuation(int HistoryStart, IReadOnlyList<PredictionExecutionStep> Steps);

internal sealed partial class CombatPredictionSimulator
{
    private ExecutionDispatchScope? _executionDispatchScope;
    private bool _captureExecutionContinuation;
    private bool _executionContinuationRejected;
    private int _executionContinuationHistoryStart;
    private List<PredictionExecutionStep>? _pendingExecutionSteps;
    private PredictionExecutionContinuation? _ownedExecutionContinuation;
    private PredictionExecutionContinuation? _preparedExecutionContinuation;

    internal bool IsCapturingExecutionContinuation => _captureExecutionContinuation;
    internal bool HasCapturedExecutionContinuation => _captureExecutionContinuation
        && !_executionContinuationRejected && _pendingExecutionSteps is { Count: > 0 };

    internal bool BeginExecutionContinuationCapture()
    {
        if (!StateStore.SupportsManualCardChoiceContinuation) return false;
        GuardOrdinaryExecutionContinuationFork();
        AssertForkable();
        _executionContinuationHistoryStart = History.PrepareManualCardChoice();
        _executionContinuationRejected = false;
        _pendingExecutionSteps = null;
        _captureExecutionContinuation = true;
        return true;
    }

    internal void EndExecutionContinuationCapture() => _captureExecutionContinuation = false;

    internal T? CapturedExecutionFrame<T>() where T : class, ICombatPredictionExecutionFrame
        => _pendingExecutionSteps?.Select(step => step.Frame).OfType<T>().LastOrDefault();

    // A completed prefix may be sealed while capture remains enabled. The caller first
    // ends its choice cursor; ordinary transaction assertions still apply unchanged.
    internal void SealStableExecutionPrefix()
    {
        if (!_captureExecutionContinuation) return;
        if (_pendingExecutionSteps != null || _ownedExecutionContinuation != null || _preparedExecutionContinuation != null)
            throw new InvalidOperationException("Cannot seal a suspended execution prefix.");
        AssertForkable();
        _executionContinuationHistoryStart = History.PrepareManualCardChoice();
    }

    internal void TrySealStableExecutionPrefix()
    {
        if (_captureExecutionContinuation && StateStore.SupportsManualCardChoiceContinuation
            && State.CombatState is ICombatPredictionExecutionContinuationState { CanSealExecutionPrefix: true })
            SealStableExecutionPrefix();
    }

    internal CombatPredictionSimulator ForkStableExecutionPrefix()
    {
        if (!_captureExecutionContinuation) return Fork();
        SealStableExecutionPrefix();
        _captureExecutionContinuation = false;
        try { return Fork(); }
        finally
        {
            _captureExecutionContinuation = true;
            _executionContinuationHistoryStart = History.Entries.Count;
        }
    }

    internal void CaptureExecutionChoice(ICombatPredictionExecutionFrame frame)
    {
        if (!_captureExecutionContinuation || _executionContinuationRejected) return;
        if (!HasPendingChoice || _pendingExecutionSteps is not null)
            throw new InvalidOperationException("Execution continuation must begin at one unresolved choice.");
        if (TryCaptureExecutionStep(frame) is { } step) _pendingExecutionSteps = [step];
    }

    internal void AppendExecutionContinuation(ICombatPredictionExecutionFrame frame)
    {
        if (!HasCapturedExecutionContinuation) return;
        if (!HasPendingChoice)
            throw new InvalidOperationException("Execution continuation tail added without a pending choice.");
        if (TryCaptureExecutionStep(frame) is { } step) _pendingExecutionSteps!.Add(step);
    }

    private PredictionExecutionStep? TryCaptureExecutionStep(ICombatPredictionExecutionFrame frame)
    {
        if (State.CombatState is ICombatPredictionExecutionContinuationState state
            && state.TryCaptureExecutionScopes(this, out var scopes))
            return new(CurrentFrame, _activeDrawDepth, scopes, frame);
        RejectExecutionContinuation();
        return null;
    }

    internal void RejectExecutionContinuation()
    {
        if (_captureExecutionContinuation) _executionContinuationRejected = true;
    }

    // Every opaque dispatch must explicitly acknowledge the captured inner operation and
    // describe its remaining work. An unadapted listener invalidates this optimization.
    internal ExecutionDispatchScope? BeginExecutionDispatch()
        => _captureExecutionContinuation ? new(this) : null;

    internal void AcknowledgeExecutionDispatch() => _executionDispatchScope?.Acknowledge();

    internal sealed class ExecutionDispatchScope : IDisposable
    {
        private readonly CombatPredictionSimulator _owner;
        private readonly ExecutionDispatchScope? _previous;
        private bool _acknowledged;
        internal ExecutionDispatchScope(CombatPredictionSimulator owner)
        {
            _owner = owner; _previous = owner._executionDispatchScope;
            owner._executionDispatchScope = this;
        }
        internal void Acknowledge() => _acknowledged = true;
        public void Dispose()
        {
            if (!ReferenceEquals(_owner._executionDispatchScope, this))
                throw new InvalidOperationException("Execution dispatch scopes are unbalanced.");
            _owner._executionDispatchScope = _previous;
            if (_owner.HasCapturedExecutionContinuation && !_acknowledged)
                _owner.RejectExecutionContinuation();
        }
    }

    internal PredictionExecutionContinuation? TakeExecutionContinuation()
    {
        if (_captureExecutionContinuation || CurrentFrame != null || _ownedExecutionContinuation is not null)
            throw new InvalidOperationException("Execution capture has not left its active scopes.");
        var steps = _pendingExecutionSteps;
        _pendingExecutionSteps = null;
        if (_executionContinuationRejected || steps is not { Count: > 0 }
            || State.CombatState is not ICombatPredictionExecutionContinuationState { CanCaptureExecutionContinuation: true }
            || !StateStore.SupportsManualCardChoiceContinuation
            || !History.SupportsExecutionContinuation(_executionContinuationHistoryStart,
                steps.SelectMany(step => step.Frame.DeferredEntries),
                steps.SelectMany(step => step.Frame.ActiveCardPlays)))
            return null;
        _ownedExecutionContinuation = new(_executionContinuationHistoryStart, steps);
        return _ownedExecutionContinuation;
    }

    private void GuardOrdinaryExecutionContinuationFork()
    {
        if (_captureExecutionContinuation || _ownedExecutionContinuation != null || _preparedExecutionContinuation != null || _pendingExecutionSteps != null)
            throw new InvalidOperationException("Suspended execution requires its owning continuation fork.");
    }

    internal CombatPredictionSimulator ForkExecutionContinuation(PredictionExecutionContinuation source,
        out PredictionExecutionContinuation continuation)
    {
        using PredictionForkContext context = new();
        return ForkExecutionContinuation(source, context, out continuation);
    }

    internal CombatPredictionSimulator ForkExecutionContinuation(PredictionExecutionContinuation source,
        PredictionForkContext context, out PredictionExecutionContinuation continuation)
    {
        if (!ReferenceEquals(source, _ownedExecutionContinuation))
            throw new InvalidOperationException("Execution continuation fork does not own this seed.");
        using var pending = ((ICombatPredictionExecutionContinuationState)State.CombatState).DetachPendingExecutionChoice();
        if (CurrentFrame != null || _damageSource != null || _activeDrawDepth != 0 || ActionRelicTriggers != null)
            throw new InvalidOperationException("Execution continuation still owns active CLR scopes.");
        PredictionTrace trace = new();
        CombatPredictionState state = State.Fork(context);
        foreach (PredictionExecutionStep step in source.Steps)
            step.Frame.PrepareFork(context);
        // Completed plays in this suffix can refer to a card that is played again later
        // in the same action. Keep their Card model alias on the same branch preview too.
        foreach (CombatPredictionHistoryEntry entry in History.EntriesFrom(source.HistoryStart))
        {
            CardPlay? play = entry switch
            {
                CombatPredictionCardPlayStartedEntry started => started.CardPlay,
                CombatPredictionCardPlayFinishedEntry finished => finished.CardPlay,
                _ => null,
            };
            if (play is null || context.TryRemap(play, out CardPlay? _)) continue;
            PredictedCard? card = State.FindCard(play.Card)
                ?? ((ICombatPredictionExecutionContinuationState)State.CombatState).FindExecutionCard(play.Card);
            if (card != null) PrepareExecutionCardPlay(card, play, context);
        }
        foreach (PredictionExecutionStep step in source.Steps)
            step.Scopes?.PrepareFork(context);
        PredictionStateStore store = StateStore.Fork(context);
        CombatPredictionHistory history = History.ForkExecutionContinuation(trace, context, source.HistoryStart);
        var steps = source.Steps.Select(step => new PredictionExecutionStep(
            ForkExecutionTrace(step.Trace, context), step.DrawDepth, step.Scopes?.Fork(context), step.Frame.Fork(context))).ToArray();
        continuation = new(source.HistoryStart, steps);
        var child = new CombatPredictionSimulator(trace, state, Rng.Fork(), store, history,
            IsInProgress, IsAboutToLose, TerminalStamp, ShuffleEventCount, null);
        child._preparedExecutionContinuation = continuation;
        foreach (var (play, block) in _blockGainedByCardPlay)
            child._blockGainedByCardPlay.Add(context.RequireRemap(play), block);
        return child;
    }

    internal bool ResumeExecutionContinuation(PredictionExecutionContinuation continuation)
    {
        if (_captureExecutionContinuation || _ownedExecutionContinuation != null
            || !ReferenceEquals(continuation, _preparedExecutionContinuation))
            throw new InvalidOperationException("Execution continuation resume does not own this prepared child.");
        _preparedExecutionContinuation = null;
        _captureExecutionContinuation = true;
        _executionContinuationRejected = false;
        _executionContinuationHistoryStart = continuation.HistoryStart;
        try
        {
            for (int index = 0; index < continuation.Steps.Count; index++)
            {
                PredictionExecutionStep step = continuation.Steps[index];
                bool completed;
                _activeDrawDepth = step.DrawDepth;
                try
                {
                    using var scopes = step.Scopes?.Enter(this);
                    using (_trace.ResumeExecution(step.Trace)) completed = step.Frame.Resume(this);
                }
                finally { _activeDrawDepth = 0; }
                if (completed && !HasPendingChoice) continue;
                if (HasCapturedExecutionContinuation)
                    for (int tail = index + 1; tail < continuation.Steps.Count; tail++)
                        _pendingExecutionSteps!.Add(continuation.Steps[tail]);
                return false;
            }
            return !HasPendingChoice;
        }
        finally { _captureExecutionContinuation = false; }
    }

    internal static PredictionTraceFrame? ForkExecutionTrace(PredictionTraceFrame? source, PredictionForkContext context)
    {
        if (source is null) return null;
        if (context.TryRemap(source, out PredictionTraceFrame? found)) return found;
        var result = new PredictionTraceFrame { Source = source.Source,
            Invocation = source.Invocation, Parent = ForkExecutionTrace(source.Parent, context) };
        context.Register(source, result);
        return result;
    }

    internal static CardPlay ForkExecutionCardPlay(CardPlay source, PredictionForkContext context)
    {
        if (context.TryRemap(source, out CardPlay? found)) return found!;
        var result = new CardPlay { Card = context.RequireRemap(source.Card), Player = source.Player,
            Target = source.Target, ResultPile = source.ResultPile, Resources = source.Resources,
            IsAutoPlay = source.IsAutoPlay, PlayIndex = source.PlayIndex, PlayCount = source.PlayCount };
        context.Register(source, result);
        return result;
    }
}
