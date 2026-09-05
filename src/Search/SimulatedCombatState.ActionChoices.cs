using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState :
    ICombatPredictionChoiceSink,
    ICombatPredictionNestedChoiceSink,
    ICombatPredictionManualCardChoiceSink
{
    private TurnStartChoiceCursor? _activeActionChoices;
    private PlanChoiceTiming _activeActionChoiceTiming = PlanChoiceTiming.Action;

    public PlanChoiceTiming ActiveActionChoiceTiming => _activeActionChoiceTiming;

    public TurnStartChoiceCursor BeginActionChoices(IReadOnlyList<PlanCardChoice>? choices)
        => BeginActionChoices(new TurnStartChoiceCursor(choices));

    public TurnStartChoiceCursor BeginActionChoices(TurnStartChoiceCursor choices)
    {
        if (_activeActionChoices != null)
            throw new InvalidOperationException("模拟状态已经处于动作选择结算中。");
        ClearPendingTurnStartChoice();
        _activeActionChoices = choices;
        _activeActionChoiceTiming = PlanChoiceTiming.Action;
        return choices;
    }

    public void SetActionChoiceTiming(PlanChoiceTiming timing)
    {
        if (_activeActionChoices == null)
            throw new InvalidOperationException("设置选牌阶段时没有活动的动作选择游标。");
        _activeActionChoiceTiming = timing;
    }

    public void EndActionChoices()
    {
        TurnStartChoiceCursor cursor = _activeActionChoices
            ?? throw new InvalidOperationException("模拟状态没有活动的动作选择游标。");
        if (PendingTurnStartChoice == null)
            cursor.AssertConsumed();
        _activeActionChoices = null;
        _activeActionChoiceTiming = PlanChoiceTiming.Action;
    }

    public TurnStartChoiceCursor OverrideActionChoices(TurnStartChoiceCursor choices)
    {
        TurnStartChoiceCursor previous = _activeActionChoices
            ?? throw new InvalidOperationException("覆盖动作选择游标时没有外层选择事务。");
        ClearPendingTurnStartChoice();
        _activeActionChoices = choices;
        return previous;
    }

    public void RestoreActionChoices(
        TurnStartChoiceCursor overridden,
        TurnStartChoiceCursor previous)
    {
        if (!ReferenceEquals(_activeActionChoices, overridden))
            throw new InvalidOperationException("恢复动作选择游标时当前游标已改变。");
        if (PendingTurnStartChoice != null)
            throw new InvalidOperationException("Vakuu 固定选择器仍有未解决的选择请求。");
        overridden.AssertConsumed();
        _activeActionChoices = previous;
    }

    bool ICombatPredictionChoiceSink.ResolvePileChoice(
        CombatPredictionSimulator simulator,
        string sourceId,
        Player player,
        PileType sourcePile,
        int count)
    {
        return TurnStartChoiceSupport.Resolve(
            simulator,
            this,
            player,
            _activeActionChoices,
            sourceId,
            PlanChoiceEffect.MoveToHand,
            count,
            sourcePile);
    }

    private bool ResolveActionCardChoice(
        CombatPredictionSimulator simulator,
        PredictedCard playedCard,
        string sourceId,
        CardChoiceSpec spec,
        ISet<uint> processedEnemyDeaths,
        string? contextIdOverride = null)
    {
        TurnStartChoiceCursor choices = _activeActionChoices
            ?? throw new InvalidOperationException($"{sourceId} 在动作选择作用域外请求卡牌选择。");
        TurnStartChoiceRequest request = new(
            sourceId,
            spec.Effect,
            spec.SourcePile,
            spec.MinCount,
            spec,
            contextIdOverride ?? spec.ContextId,
            Timing: _activeActionChoiceTiming);
        if (!choices.TryTake(request, out PlanCardChoice? choice))
        {
            if (!HasPendingChoice)
                SetPendingTurnStartChoice(request);
            return false;
        }
        CardChoiceSupport.Apply(simulator, this, playedCard, choice!, processedEnemyDeaths);
        // Applying an outer choice can auto-play another card which opens its own selector.
        // Propagate that suspension so the outer card remains inside its OnPlay transaction;
        // its result pile and AfterCardPlayed hooks must wait for the nested request.
        return !HasPendingChoice;
    }

    bool ICombatPredictionManualCardChoiceSink.ResolveManualCardChoice(
        CombatPredictionSimulator simulator,
        PredictedCard card)
    {
        ISet<uint> processedEnemyDeaths = _activeCardExecutionDeaths ?? new HashSet<uint>();
        CardChoiceSpec? spec = CardChoiceSupport.GetSpec(simulator, card);
        if (spec != null)
        {
            return ResolveActionCardChoice(
                simulator,
                card,
                string.Empty,
                spec,
                processedEnemyDeaths);
        }

        PlanCardChoice? requiredEmptyChoice = CardChoiceSupport.BuildRequiredEmptyChoice(card.Preview);
        if (requiredEmptyChoice == null)
        {
            CardChoiceSupport.ApplyNoChoiceEffects(simulator, this, card);
            return !HasPendingChoice;
        }

        TurnStartChoiceCursor choices = _activeActionChoices
            ?? throw new InvalidOperationException(
                $"{card.Preview.Id.Entry} 在动作选择作用域外请求空卡牌选择。");
        CardChoiceSpec emptySpec = new(
            requiredEmptyChoice.Effect,
            requiredEmptyChoice.SourcePile,
            0,
            0,
            [],
            [],
            ReplacementValue: 0d);
        TurnStartChoiceRequest request = new(
            string.Empty,
            requiredEmptyChoice.Effect,
            requiredEmptyChoice.SourcePile,
            0,
            emptySpec,
            Timing: _activeActionChoiceTiming);
        if (!choices.TryTake(request, out PlanCardChoice? plannedChoice))
        {
            if (!HasPendingChoice)
                SetPendingTurnStartChoice(request);
            return false;
        }

        CardChoiceSupport.Apply(
            simulator,
            this,
            card,
            plannedChoice!,
            processedEnemyDeaths);
        return !HasPendingChoice;
    }

    bool ICombatPredictionNestedChoiceSink.ResolveNestedCardChoice(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        string sourceId,
        string? contextId)
    {
        ISet<uint> processedEnemyDeaths = _activeCardExecutionDeaths ?? new HashSet<uint>();
        CardChoiceSpec? spec = CardChoiceSupport.GetSpec(simulator, card);
        if (spec != null)
        {
            return ResolveActionCardChoice(
                simulator,
                card,
                sourceId,
                spec,
                processedEnemyDeaths,
                contextId);
        }

        if (CardChoiceSupport.BuildRequiredEmptyChoice(card.Preview) is { } emptyChoice)
            CardChoiceSupport.Apply(simulator, this, card, emptyChoice, processedEnemyDeaths);
        else
            CardChoiceSupport.ApplyNoChoiceEffects(simulator, this, card);
        return !HasPendingChoice;
    }
}
