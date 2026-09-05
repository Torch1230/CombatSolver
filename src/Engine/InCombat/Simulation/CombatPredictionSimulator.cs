using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Simulation;

internal enum CombatTerminalOutcome
{
    Victory,
    Defeat,
}

internal readonly record struct CombatTerminalStamp(int PlayerTurn, CombatTerminalOutcome Outcome);

internal sealed partial class CombatPredictionSimulator
{
    private readonly PredictionTrace _trace;
    private CombatDamageSource? _damageSource;

    public CombatPredictionState State { get; }

    public CombatPredictionRngSet Rng { get; }

    public PredictionStateStore StateStore { get; }

    public CombatPredictionHistory History { get; }

    public bool HasRisk => History.HasRisk;

    public int ShuffleEventCount { get; private set; }

    public ActionRelicTriggerRecorder? ActionRelicTriggers { get; set; }

    public bool IsRecordingActionRelicTriggers => ActionRelicTriggers != null;

    public PredictionTraceFrame? CurrentFrame => _trace.Current;

    /// <summary>Temporarily overrides trace inference for effects such as poison and reflected thorns damage.</summary>
    public DamageSourceScope PushDamageSource(CombatDamageSource source)
    {
        CombatDamageSource? previous = _damageSource;
        _damageSource = source;
        return new DamageSourceScope(this, previous);
    }

    internal CombatDamageSource ResolveDamageSource(PredictedCard? cardSource)
    {
        if (_damageSource is { } explicitSource)
            return explicitSource;
        if (cardSource is { } card)
            return CombatDamageSource.For(CombatDamageSourceKind.Card, card.Preview.Id.Entry);

        for (PredictionTraceFrame? frame = CurrentFrame; frame is not null; frame = frame.Parent)
        {
            if (frame.Invocation.Action == PredictionActionKind.PotionUse)
                return CombatDamageSource.For(CombatDamageSourceKind.Potion, frame.Source.Id.Entry);
            if (frame.Invocation.Action == PredictionActionKind.CardPlay)
                return CombatDamageSource.For(CombatDamageSourceKind.Card, frame.Source.Id.Entry);

            CombatDamageSourceKind? kind = frame.Source switch
            {
                CardModel => CombatDamageSourceKind.Card,
                PotionModel => CombatDamageSourceKind.Potion,
                PowerModel => CombatDamageSourceKind.Power,
                RelicModel => CombatDamageSourceKind.Relic,
                OrbModel => CombatDamageSourceKind.Orb,
                MonsterModel => CombatDamageSourceKind.MonsterMove,
                _ => null,
            };
            if (kind is { } modelKind)
                return CombatDamageSource.For(modelKind, frame.Source.Id.Entry);
        }

        return CombatDamageSource.Unknown;
    }

    /// <summary>
    /// Mirrors <see cref="CombatTurnState.IsInProgress"/>.
    /// </summary>
    public bool IsInProgress { get; private set; } = true;

    /// <summary>
    /// Mirrors <see cref="CombatTurnState.PendingLoss"/>.
    /// </summary>
    public bool IsAboutToLose { get; private set; }

    // Locked only at a vanilla-safe victory/loss check, not by the IsEnding query.
    // A value copy survives Fork without retaining any combat or simulator graph.
    public CombatTerminalStamp? TerminalStamp { get; private set; }

    /// <summary>
    /// Mirrors <see cref="CombatManager.IsEnding"/>.
    /// </summary>
    public bool IsEnding => IsCombatEnding();

    /// <summary>
    /// Mirrors <see cref="CombatManager.IsOverOrEnding"/>.
    /// </summary>
    public bool IsOverOrEnding => !IsInProgress || IsEnding;

    public CombatPredictionSimulator(ICombatState combatState)
    {
        _trace = new PredictionTrace();
        State = new CombatPredictionState(combatState);
        Rng = combatState is ICombatPredictionRunSnapshot runSnapshot
            ? runSnapshot.CreatePredictionRngSet()
            : CombatPredictionRngSet.From(combatState.RunState.Rng);
        StateStore = new PredictionStateStore();
        History = new CombatPredictionHistory(_trace);
        if (combatState is ICombatPredictionRootMaterializable materializable)
            materializable.MaterializeRoot(this);
    }

    private CombatPredictionSimulator(
        PredictionTrace trace,
        CombatPredictionState state,
        CombatPredictionRngSet rng,
        PredictionStateStore stateStore,
        CombatPredictionHistory history,
        bool isInProgress,
        bool isAboutToLose,
        CombatTerminalStamp? terminalStamp,
        int shuffleEventCount,
        ActionRelicTriggerRecorder? actionRelicTriggers)
    {
        _trace = trace;
        State = state;
        Rng = rng;
        StateStore = stateStore;
        History = history;
        IsInProgress = isInProgress;
        IsAboutToLose = isAboutToLose;
        TerminalStamp = terminalStamp;
        ShuffleEventCount = shuffleEventCount;
        ActionRelicTriggers = actionRelicTriggers;
    }

    public CombatPredictionSimulator Fork()
    {
        AssertForkable();

        using PredictionForkContext context = new();
        PredictionTrace trace = new();
        CombatPredictionState state = State.Fork(context);
        PredictionStateStore stateStore = StateStore.Fork(context);
        CombatPredictionHistory history = History.Fork(trace);
        return new CombatPredictionSimulator(
            trace,
            state,
            Rng.Fork(),
            stateStore,
            history,
            IsInProgress,
            IsAboutToLose,
            TerminalStamp,
            ShuffleEventCount,
            ActionRelicTriggers);
    }

    internal void AssertForkable()
    {
        if (_trace.Current is not null)
            throw new InvalidOperationException("Combat prediction can only be forked between completed actions.");
        if (_damageSource is not null)
            throw new InvalidOperationException("Combat prediction cannot be forked while a damage source is active.");
        if (_activeDrawDepth != 0)
            throw new InvalidOperationException("Combat prediction cannot be forked during draw resolution.");
        if (ActionRelicTriggers is not null)
            throw new InvalidOperationException("Combat prediction cannot be forked while action relic triggers are being recorded.");
        if (State.CombatState is IPredictionForkBoundary combatBoundary)
            combatBoundary.AssertForkable();
        StateStore.AssertForkable();
        History.AssertForkable();
    }

    public void RecordRelicTrigger(RelicModel relic, string summary)
        => ActionRelicTriggers?.Record(relic, summary);

    public PredictionRisk Snapshot()
    {
        return History.GetCurrentRisk();
    }

    /// <summary>
    /// Mirrors the prediction-relevant boundary of <see cref="CombatManager.LoseCombat"/>.
    /// </summary>
    public void LoseCombat()
    {
        IsAboutToLose = true;
    }

    /// <summary>
    /// Mirrors the prediction-relevant boundary of <see cref="CombatManager.CheckWinCondition"/>.
    /// </summary>
    /// <remarks>
    /// This only evaluates the shadow pending-loss/victory state and commits the simulator's
    /// <see cref="IsInProgress"/> flag when the combat has reached a safe point.
    /// It does not simulate the vanilla combat teardown after <c>EndCombatInternal</c>, including
    /// after-combat hooks, rewards, room progression, save operations, music/UI cleanup, or run-loss handling.
    /// </remarks>
    public bool CheckWinCondition(int playerTurn)
    {
        if (TerminalStamp.HasValue)
            return true;
        if (playerTurn < 1)
            throw new ArgumentOutOfRangeException(nameof(playerTurn));
        if (!IsAboutToLose && !IsEnding)
        {
            return false;
        }

        TerminalStamp = new CombatTerminalStamp(playerTurn,
            IsAboutToLose ? CombatTerminalOutcome.Defeat : CombatTerminalOutcome.Victory);
        IsAboutToLose = false;
        IsInProgress = false;
        return true;
    }

    public PredictionTrace.TraceScope PushActionSource(AbstractModel model, PredictionActionKind action)
    {
        return _trace.Push(model, PredictionInvocation.ForAction(action));
    }

    public PredictionTrace.TraceScope PushMethodSource(AbstractModel model, MirrorMethodSpec method)
    {
        return _trace.Push(model, PredictionInvocation.ForMethod(method.BaseMethod));
    }

    public struct DamageSourceScope(CombatPredictionSimulator simulator, CombatDamageSource? previous)
        : IDisposable
    {
        private readonly CombatPredictionSimulator _simulator = simulator;
        private readonly CombatDamageSource? _previous = previous;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _simulator._damageSource = _previous;
            _disposed = true;
        }
    }

    private bool IsCombatEnding()
    {
        if (!IsInProgress)
        {
            return false;
        }

        if (IsAboutToLose)
        {
            return true;
        }

        IReadOnlyList<Creature> enemies = State.Enemies;
        ICombatPredictionCreatureSemantics? semantics =
            State.CombatState as ICombatPredictionCreatureSemantics;
        for (int index = 0; index < enemies.Count; index++)
        {
            Creature enemy = enemies[index];
            if (State.GetCreature(enemy).IsAlive
                && (semantics?.IsPrimaryEnemy(enemy) ?? enemy.IsPrimaryEnemy))
            {
                return false;
            }
        }
        return !Hook.ShouldStopCombatFromEnding(State.CombatState);
    }
}
