using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;

namespace CombatSolver.Engine.InCombat.Simulation;

internal static class CombatPredictedCardExtensions
{
    [ThreadStatic]
    private static HashSet<CardKeyword>? _keywordScratch;

    [ThreadStatic]
    private static bool _keywordScratchInUse;

    // Mirrors CardModel.Pile property, but returns the simulated pile instead of the actual pile.
    public static SimCardPile? GetPile(this PredictedCard card, CombatPredictionState state)
    {
        return card.GetPile(state.GetPlayerCombatState(card.Preview.Owner));
    }

    // Mirrors CardModel.Pile property, but returns the simulated pile instead of the actual pile.
    public static SimCardPile? GetPile(this PredictedCard card, SimPlayerCombatState playerCombatState)
    {
        SimCardPile? pile = card.OwnerPile;
        return pile is not null && playerCombatState.ContainsPile(pile)
            ? pile
            : null;
    }

    // Mirrors CardModel.CreateClone, but returns a PredictedCard instead of a CardModel.
    public static PredictedCard CreateClone(this PredictedCard card)
    {
        var clonedCard = PredictionUtils.CloneCardStateForSimulation(card.Preview);
        // CloneCardStateForSimulation restores these fields for prediction COW. Gameplay
        // CardModel.CreateClone instead keeps the reset values from CardModel.AfterCloned.
        clonedCard.DeckVersion = null;
        clonedCard.HasBeenRemovedFromState = false;
        clonedCard._cloneOf = card.Original;
        clonedCard.ExhaustOnNextPlay = false;
        return PredictedCard.FromGenerated(clonedCard);
    }

    /// <summary>
    /// Mirrors <see cref="CardModel.CreateCloneForPlayer"/>.
    /// </summary>
    public static PredictedCard CreateCloneForPlayer(this PredictedCard card, Player player)
    {
        var clone = card.CreateClone();
        clone.MutablePreview._owner = player;
        return clone;
    }

    /// <summary>Mirrors CardModel.CreateDupe for branch-local autoplay.</summary>
    public static PredictedCard CreateDupeForPlayer(this PredictedCard card, Player player)
    {
        PredictedCard source = card;
        if (card.Preview.IsDupe && card.Preview.DupeOf is { } original)
            source = PredictedCard.FromGenerated(PredictionUtils.CloneCardStateForSimulation(original));
        PredictedCard dupe = source.CreateCloneForPlayer(player);
        dupe.MutablePreview.IsDupe = true;
        dupe.MutablePreview.RemoveKeyword(CardKeyword.Exhaust);
        return dupe;
    }

    // Mirrors CardModel.AfflictInternal without firing live-model side effects. Preview cards still
    // keep the real owner, so AfflictInternal's Amount setter and AfflictionChanged event would
    // recalculate values through the real PlayerCombatState and notify real card listeners.
    public static void Afflict(this PredictedCard card, AfflictionModel affliction, decimal amount)
    {
        var previewCard = card.MutablePreview;
        previewCard.Affliction = affliction;
        previewCard.Affliction.Card = previewCard;
        previewCard.Affliction._amount = (int)amount;
        card.NotifyHookListenerStructureChanged();
    }

    // Mirrors CardModel.ClearAfflictionInternal without firing live-model side effects.
    public static void ClearAffliction(this PredictedCard card)
    {
        if (card.Preview.Affliction != null)
        {
            var previewCard = card.MutablePreview;
            previewCard.Affliction!.ClearInternal();
            previewCard.Affliction = null;
            card.NotifyHookListenerStructureChanged();
        }
    }

    // Mirrors CardModel.GeneratePlayCount.
    public static int GeneratePlayCount(this PredictedCard card, CombatPredictionSimulator simulator, Creature? target)
    {
        var playCount = HookMirrors.ModifyCardPlayCount(
            simulator,
            card,
            card.Preview.GetEnchantedReplayCount() + 1,
            target,
            out var modifiers);
        HookMirrors.AfterModifyingCardPlayCount(simulator, card, modifiers);
        return playCount;
    }

    // Mirrors CardEnergyCost.GetAmountToSpend.
    public static int GetEnergyCostWithModifiers(
        this PredictedCard card,
        CombatPredictionSimulator simulator,
        SimPlayerCombatState playerCombatState)
    {
        var energyCost = card.Preview.EnergyCost;
        if (energyCost.CostsX)
        {
            return playerCombatState.Energy;
        }

        return Math.Max(0, card.GetEnergyCostValueWithModifiers(simulator));
    }

    // Mirrors CardEnergyCost.GetWithModifiers(CostModifiers.All), preserving negative costs.
    public static int GetEnergyCostValueWithModifiers(
        this PredictedCard card,
        CombatPredictionSimulator simulator)
    {
        var energyCost = card.Preview.EnergyCost;

        var cost = energyCost._base;
        if (cost < 0 || energyCost.CostsX)
        {
            return cost;
        }

        foreach (var modifier in energyCost._localModifiers)
        {
            cost = modifier.Modify(cost);
        }

        cost = (int)HookMirrors.ModifyEnergyCostInCombat(simulator, card, cost);
        return Math.Max(0, cost);
    }

    // Mirrors CardModel.GetStarCostWithModifiers.
    public static int GetStarCostWithModifiers(
        this PredictedCard card,
        CombatPredictionSimulator simulator,
        SimPlayerCombatState playerCombatState)
    {
        if (card.Preview.HasStarCostX)
        {
            return playerCombatState.Stars;
        }

        var cost = card.Preview.CurrentStarCost;
        cost = (int)HookMirrors.ModifyStarCost(simulator, card, cost);
        return Math.Max(0, cost);
    }

    // Mirrors CardModel.ResolveEnergyXValue.
    public static int ResolveEnergyXValue(this PredictedCard card, CombatPredictionState state)
    {
        return Hook.ModifyXValue(state.CombatState, card.Preview, card.Preview.EnergyCost.CapturedXValue);
    }

    // Mirrors CardModel.ResolveStarXValue.
    public static int ResolveStarXValue(this PredictedCard card, CombatPredictionState state)
    {
        return Hook.ModifyXValue(state.CombatState, card.Preview, card.Preview.LastStarsSpent);
    }

    // Mirrors CardModel.Keywords => CardModel.GetKeywordsWithSources(KeywordSources.All).
    public static IReadOnlySet<CardKeyword> GetKeywords(this PredictedCard card, CombatPredictionState state)
    {
        var keywords = card.Preview.LocalKeywords.ToHashSet();
        Hook.ModifyKeywordsInCombat(state.CombatState, card.Preview, keywords);
        return keywords;
    }

    /// <summary>
    /// 热路径只查询一个关键字时复用线程本地集合，避免 CanPlay、回合末和伤害 Hook
    /// 为每次 Contains 都创建一个 HashSet。递归进入时退回独立集合，避免污染外层查询。
    /// </summary>
    public static bool HasKeyword(
        this PredictedCard card,
        CombatPredictionState state,
        CardKeyword keyword)
    {
        bool ownsScratch = !_keywordScratchInUse;
        HashSet<CardKeyword> keywords;
        if (ownsScratch)
        {
            _keywordScratchInUse = true;
            keywords = _keywordScratch ??= [];
        }
        else
        {
            keywords = [];
        }

        try
        {
            // LocalKeywords 以接口类型暴露一个数组，foreach 会为每次查询装箱一个枚举器。
            // 这里只往集合里塞元素再问 Contains，插入顺序不影响结果。
            if (card.Preview.LocalKeywords is IReadOnlyList<CardKeyword> indexableKeywords)
            {
                for (int index = 0; index < indexableKeywords.Count; index++)
                    keywords.Add(indexableKeywords[index]);
            }
            else
            {
                foreach (CardKeyword localKeyword in card.Preview.LocalKeywords)
                    keywords.Add(localKeyword);
            }
            Hook.ModifyKeywordsInCombat(state.CombatState, card.Preview, keywords);
            return keywords.Contains(keyword);
        }
        finally
        {
            keywords.Clear();
            if (ownsScratch)
                _keywordScratchInUse = false;
        }
    }

    // Forwards to CardModel.SetToFreeThisTurn, but returns the same PredictedCard for fluent chaining.
    public static PredictedCard SetToFreeThisTurn(this PredictedCard card)
    {
        card.MutablePreview.SetToFreeThisTurn();
        return card;
    }

    // Forwards to CardModel.SetToFreeThisCombat, but returns the same PredictedCard for fluent chaining.
    public static PredictedCard SetToFreeThisCombat(this PredictedCard card)
    {
        card.MutablePreview.SetToFreeThisCombat();
        return card;
    }
}
