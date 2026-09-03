using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    private readonly PredictionTrace _trace;

    public CombatPredictionState State { get; }

    public CombatPredictionRngSet Rng { get; }

    public PredictionStateStore StateStore { get; }

    public CombatPredictionHistory History { get; }

    public bool HasRisk => History.HasRisk;

    public int ShuffleEventCount { get; private set; }

    public ActionRelicTriggerRecorder? ActionRelicTriggers { get; set; }

    public bool IsRecordingActionRelicTriggers => ActionRelicTriggers != null;

    public PredictionTraceFrame? CurrentFrame => _trace.Current;

    /// <summary>
    /// Reusable mirror context for <see cref="Mirrors.HookMirrors.ModifyEnergyCostInCombat"/>.
    /// </summary>
    /// <remarks>
    /// 该 context 在调用返回后没有任何持有者，可以复用；它的 <c>Simulator</c> 是 required init，
    /// 所以复用范围绑定在单个模拟器上。取用方负责先摘空这个槽位以防重入。
    /// </remarks>
    internal Mirrors.Hooks.Card.ModifyEnergyCostInCombatMirrorContext? EnergyCostMirrorScratch;

    /// <summary>
    /// Mirrors <see cref="CombatTurnState.IsInProgress"/>.
    /// </summary>
    public bool IsInProgress { get; private set; } = true;

    /// <summary>
    /// Mirrors <see cref="CombatTurnState.PendingLoss"/>.
    /// </summary>
    public bool IsAboutToLose { get; private set; }

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
            ShuffleEventCount,
            ActionRelicTriggers);
    }

    internal void AssertForkable()
    {
        // 只是判断有没有活动作用域，不需要把帧物化出来。
        if (_trace.HasCurrentFrame)
            throw new InvalidOperationException("Combat prediction can only be forked between completed actions.");
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
    public bool CheckWinCondition()
    {
        if (!IsAboutToLose && !IsEnding)
        {
            return false;
        }

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
