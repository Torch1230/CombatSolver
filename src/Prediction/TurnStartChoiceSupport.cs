using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed record TurnStartChoiceRequest(
    string SourceId,
    PlanChoiceEffect Effect,
    PileType SourcePile,
    int Count,
    CardChoiceSpec? Spec = null,
    string ContextId = "",
    PlanChoiceTiming Timing = PlanChoiceTiming.Action);

internal sealed class InvalidPlannedChoiceBranchException(string message)
    : InvalidOperationException(message);

internal sealed class TurnStartChoiceCursor(IReadOnlyList<PlanCardChoice>? choices)
{
    private readonly IReadOnlyList<PlanCardChoice> _choices = choices ?? [];
    private readonly Func<TurnStartChoiceRequest, PlanCardChoice?>? _automaticPolicy;
    private int _index;
    private Func<bool>? _beforeNextTake;

    private TurnStartChoiceCursor(
        Func<TurnStartChoiceRequest, PlanCardChoice?> automaticPolicy,
        bool _)
        : this((IReadOnlyList<PlanCardChoice>?)null)
    {
        _automaticPolicy = automaticPolicy;
    }

    public static TurnStartChoiceCursor ForAutomaticPolicy(
        Func<TurnStartChoiceRequest, PlanCardChoice?> automaticPolicy)
        => new(automaticPolicy, true);

    public bool TryTake(TurnStartChoiceRequest request, out PlanCardChoice? choice)
    {
        // The callback can synchronously open and consume a deeper choice from this same cursor.
        // Run it before matching the original request, then re-read the cursor position.
        if (!InvokeBeforeNextTake())
        {
            choice = null;
            return false;
        }
        if (_index >= _choices.Count)
        {
            choice = _automaticPolicy?.Invoke(request);
            return choice != null;
        }
        choice = _choices[_index];
        if (!Matches(choice, request))
        {
            throw new InvalidPlannedChoiceBranchException(
                $"回合开始选牌顺序不一致：计划 {choice.SourceId}/{choice.Effect}/{choice.SourcePile}，" +
                $"当前需要 {request.SourceId}/{request.Effect}/{request.SourcePile}；" +
                $"计划上下文={choice.ContextId}，当前上下文={request.ContextId}。");
        }
        _index++;
        return true;
    }

    public bool TryTakeIfMatches(TurnStartChoiceRequest request, out PlanCardChoice? choice)
    {
        if (!InvokeBeforeNextTake())
        {
            choice = null;
            return false;
        }
        if (_index >= _choices.Count || !Matches(_choices[_index], request))
        {
            choice = _automaticPolicy?.Invoke(request);
            return choice != null;
        }
        choice = _choices[_index];
        _index++;
        return true;
    }

    public IDisposable BeforeNextTake(Func<bool> callback)
    {
        if (_beforeNextTake != null)
            throw new InvalidOperationException("选牌游标已经存在一个消费前回调。");
        _beforeNextTake = callback;
        return new BeforeNextTakeScope(this, callback);
    }

    public void AssertConsumed()
    {
        if (_index != _choices.Count)
        {
            PlanCardChoice next = _choices[_index];
            throw new InvalidPlannedChoiceBranchException(
                $"回合开始仍有 {_choices.Count - _index} 个计划选牌没有触发；" +
                $"下一个={next.SourceId}/{next.Effect}/{next.SourcePile}/{next.ContextId}。");
        }
    }

    private static bool Matches(PlanCardChoice choice, TurnStartChoiceRequest request)
        => string.Equals(choice.SourceId, request.SourceId, StringComparison.Ordinal)
            && choice.Effect == request.Effect
            && choice.SourcePile == request.SourcePile
            && string.Equals(choice.ContextId, request.ContextId, StringComparison.Ordinal)
            && choice.Timing == request.Timing;

    private bool InvokeBeforeNextTake()
    {
        Func<bool>? callback = _beforeNextTake;
        _beforeNextTake = null;
        return callback?.Invoke() ?? true;
    }

    private sealed class BeforeNextTakeScope(TurnStartChoiceCursor owner, Func<bool> callback) : IDisposable
    {
        public void Dispose()
        {
            if (ReferenceEquals(owner._beforeNextTake, callback))
                owner._beforeNextTake = null;
        }
    }
}

internal static class TurnStartChoiceSupport
{
    public static bool ResolveGeneratedToHand(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        TurnStartChoiceCursor? cursor,
        string sourceId,
        IReadOnlyList<PredictedCard> options,
        string contextId = "")
    {
        if (options.Count == 0)
            return true;

        CardChoiceSpec spec = new(
            PlanChoiceEffect.GenerateToHand,
            PileType.None,
            1,
            1,
            options,
            options,
            ReplacementValue: 0d);
        TurnStartChoiceRequest request = new(
            sourceId,
            PlanChoiceEffect.GenerateToHand,
            PileType.None,
            1,
            spec,
            contextId,
            combat.ActiveActionChoiceTiming);
        if (cursor == null || !cursor.TryTake(request, out PlanCardChoice? choice))
        {
            if (!combat.HasPendingChoice)
                combat.SetPendingTurnStartChoice(request);
            return false;
        }

        IReadOnlyList<PredictedCard> selected = ResolveTokens(
            choice!,
            options,
            minCount: 1,
            maxCount: 1);
        simulator.AddGeneratedCardsToCombat(
            selected,
            PileType.Hand,
            player,
            CardPilePosition.Bottom,
            CardGenerationResultKind.Random);
        if (combat.HasPendingChoice)
            return false;
        combat.ClearPendingTurnStartChoice();
        return true;
    }

    public static bool ResolveDiscardAndDraw(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        TurnStartChoiceCursor? cursor,
        string sourceId,
        string contextId = "")
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        IReadOnlyList<PredictedCard> options = state.Hand.Cards.ToArray();
        if (options.Count == 0)
            return true;

        CardChoiceSpec spec = new(
            PlanChoiceEffect.DiscardAndDraw,
            PileType.Hand,
            0,
            options.Count,
            options,
            state.Hand.Cards,
            ReplacementValue: 0d);
        TurnStartChoiceRequest request = new(
            sourceId,
            PlanChoiceEffect.DiscardAndDraw,
            PileType.Hand,
            options.Count,
            spec,
            contextId,
            combat.ActiveActionChoiceTiming);
        if (cursor == null || !cursor.TryTake(request, out PlanCardChoice? choice))
        {
            if (!combat.HasPendingChoice)
                combat.SetPendingTurnStartChoice(request);
            return false;
        }

        IReadOnlyList<PredictedCard> selected = ResolveTokens(
            choice!,
            options,
            minCount: 0,
            maxCount: options.Count);
        if (selected.Count > 0)
        {
            simulator.Discard(selected);
            if (combat.HasPendingChoice)
                return false;
            simulator.Draw(player, selected.Count);
            if (combat.HasPendingChoice)
                return false;
        }
        if (combat.HasPendingChoice)
            return false;
        combat.ClearPendingTurnStartChoice();
        return true;
    }

    public static bool Resolve(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        TurnStartChoiceCursor? cursor,
        string sourceId,
        PlanChoiceEffect effect,
        int requestedCount,
        PileType sourcePile = PileType.Hand)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        IReadOnlyList<PredictedCard> sourceCards = state.GetCardPile(sourcePile)?.Cards
            ?? throw new InvalidOperationException($"回合开始选择不支持牌堆 {sourcePile}。");
        IReadOnlyList<PredictedCard> options = effect == PlanChoiceEffect.Transform
            ? sourceCards.Where(card => card.Preview.IsTransformable).ToArray()
            : sourceCards.ToArray();
        int count = Math.Min(requestedCount, options.Count);
        if (count <= 0)
            return true;

        CardChoiceSpec spec = new(
            effect,
            sourcePile,
            count,
            count,
            options,
            sourceCards,
            ReplacementValue: 0d);
        TurnStartChoiceRequest request = new(
            sourceId,
            effect,
            sourcePile,
            count,
            spec,
            Timing: combat.ActiveActionChoiceTiming);
        IReadOnlyList<PredictedCard> selected;
        if (cursor == null || !cursor.TryTake(request, out PlanCardChoice? choice))
        {
            if (!combat.HasPendingChoice)
                combat.SetPendingTurnStartChoice(request);
            return false;
        }
        else
        {
            selected = CardChoiceSupport.ResolveStandaloneChoice(
                simulator,
                choice!,
                options,
                count,
                sourcePile);
        }
        switch (effect)
        {
            case PlanChoiceEffect.Discard:
                simulator.Discard(selected);
                if (combat.HasPendingChoice)
                    return false;
                break;
            case PlanChoiceEffect.Exhaust:
                foreach (PredictedCard card in selected)
                {
                    simulator.Exhaust(card);
                    if (combat.HasPendingChoice)
                        return false;
                }
                break;
            case PlanChoiceEffect.Transform:
                foreach (PredictedCard card in selected)
                {
                    CardModel replacement = CardFactory.CreateRandomCardForTransform(
                        card.Preview,
                        isInCombat: true,
                        simulator.Rng.CombatCardSelection);
                    CardChoiceSupport.TransformCardToGeneratedReplacement(simulator, card, replacement);
                    if (combat.HasPendingChoice)
                        return false;
                }
                break;
            case PlanChoiceEffect.MoveToHand:
                simulator.AddToPile(selected, PileType.Hand);
                break;
            default:
                throw new InvalidOperationException($"不支持的回合开始选牌效果：{effect}。");
        }
        if (combat.HasPendingChoice)
            return false;
        combat.ClearPendingTurnStartChoice();
        return true;
    }

    public static CardChoiceSpec BuildPendingSpec(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player)
    {
        TurnStartChoiceRequest request = combat.PendingTurnStartChoice
            ?? throw new InvalidOperationException("模拟状态没有待处理的回合开始选牌。");
        return BuildSpec(simulator, player, request);
    }

    public static CardChoiceSpec BuildSpec(
        CombatPredictionSimulator simulator,
        Player player,
        TurnStartChoiceRequest request)
    {
        if (request.Spec != null)
            return request.Spec;
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        IReadOnlyList<PredictedCard> sourceCards = state.GetCardPile(request.SourcePile)?.Cards
            ?? throw new InvalidOperationException($"回合开始选择不支持牌堆 {request.SourcePile}。");
        IReadOnlyList<PredictedCard> options = Options(simulator, player, request.Effect, request.SourcePile);
        int count = Math.Min(request.Count, options.Count);
        if (count <= 0)
            throw new InvalidOperationException($"{request.SourceId} 请求选牌，但当前没有合法候选。");
        return new CardChoiceSpec(
            request.Effect,
            request.SourcePile,
            count,
            count,
            options,
            sourceCards,
            ReplacementValue: 0d);
    }

    private static IReadOnlyList<PredictedCard> Options(
        CombatPredictionSimulator simulator,
        Player player,
        PlanChoiceEffect effect,
        PileType sourcePile)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
        IReadOnlyList<PredictedCard> cards = state.GetCardPile(sourcePile)?.Cards
            ?? throw new InvalidOperationException($"回合开始选择不支持牌堆 {sourcePile}。");
        return effect == PlanChoiceEffect.Transform
            ? cards.Where(card => card.Preview.IsTransformable).ToArray()
            : cards.ToArray();
    }

    private static IReadOnlyList<PredictedCard> ResolveTokens(
        PlanCardChoice choice,
        IReadOnlyList<PredictedCard> options,
        int minCount,
        int maxCount)
    {
        List<PredictedCard> selected = [];
        foreach (PlanCardToken token in choice.Cards)
        {
            PredictedCard card = options.Where(candidate => CardChoiceSupport.MatchesToken(candidate, token))
                .Skip(token.OptionOccurrence)
                .FirstOrDefault()
                ?? throw new InvalidPlannedChoiceBranchException(
                    $"回合开始选牌时找不到 {token.CardId}+{token.UpgradeLevel}#{token.OptionOccurrence}。");
            if (selected.Contains(card))
                throw new InvalidPlannedChoiceBranchException($"回合开始计划重复选择了 {token.CardId}。");
            selected.Add(card);
        }
        if (selected.Count < minCount || selected.Count > maxCount)
        {
            throw new InvalidPlannedChoiceBranchException(
                $"回合开始计划选择 {selected.Count} 张牌，但当前要求 {minCount}..{maxCount} 张。");
        }
        return selected;
    }
}
