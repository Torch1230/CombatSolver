using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    // Only bound synchronous recursion (draw hook -> automatic play -> draw), never the
    // accumulated battle history. Reaching this safety boundary invalidates the action;
    // returning a partially drawn hand would silently invent a different combat state.
    private const int MaximumNestedDrawDepth = 100;
    private int _activeDrawDepth;

    /// <summary>
    /// See <see cref="Draw(Player, int, bool)"/> for the main overload.
    /// This overload rounds the draw count to an integer.
    /// </summary>
    public IReadOnlyList<PredictedCard> Draw(Player player, decimal drawCount, bool fromHandDraw = false)
    {
        var roundedCount = drawCount > 0m ? (int)Math.Ceiling(drawCount) : 0;
        return Draw(player, roundedCount, fromHandDraw);
    }

    /// <summary>
    /// Mirrors <see cref="CardPileCmd.Draw(PlayerChoiceContext, decimal, Player, bool)"/>.
    /// </summary>
    public IReadOnlyList<PredictedCard> Draw(Player player, int drawCount, bool fromHandDraw = false)
    {
        if (_activeDrawDepth >= MaximumNestedDrawDepth)
        {
            throw new InvalidOperationException(
                $"模拟抽牌同步递归达到 {MaximumNestedDrawDepth} 层，无法安全完成当前动作。");
        }
        _activeDrawDepth++;
        try
        {
            return DrawCore(player, drawCount, fromHandDraw);
        }
        finally
        {
            _activeDrawDepth--;
        }
    }

    private IReadOnlyList<PredictedCard> DrawCore(Player player, int drawCount, bool fromHandDraw)
    {
        if (IsOverOrEnding || !HookMirrors.ShouldDraw(this, player, fromHandDraw, out _))
        {
            // Vanilla calls Hook.AfterPreventingDraw here, but all current listeners are cosmetic.
            return [];
        }

        var state = State.GetPlayerCombatState(player);
        int maxHandSize = GetMaxHandSize(player);
        List<PredictedCard> drawnCards = [];

        for (var i = 0; i < drawCount; i++)
        {
            if (IsOverOrEnding)
            {
                break;
            }

            if (state.Hand.Cards.Count >= maxHandSize)
            {
                break;
            }

            ShuffleIfNecessary(player);

            if (HasPendingChoice)
                break;

            if (state.DrawPile.IsEmpty || state.Hand.Cards.Count >= maxHandSize)
            {
                break;
            }

            var card = state.DrawPile.Cards[0];
            drawnCards.Add(card);
            AddToPile(card, state.Hand);
            var entry = History.CardDrawn(card, fromHandDraw);
            if (State.CombatState is ICombatPredictionCardEventSink eventSink)
                eventSink.RecordCardDrawn(card, fromHandDraw);

            HookMirrors.AfterCardDrawn(this, card, fromHandDraw);
            if (HasPendingChoice)
                return drawnCards;
            History.CardDrawResolved(entry, card);
        }

        return drawnCards;
    }

    public int GetMaxHandSize(Player player)
        => State.CombatState is ICombatPredictionPlayerLimits limits
            ? limits.GetMaxHandSize(player)
            : CardPile.MaxCardsInHand;

    /// <summary>
    /// Mirrors <see cref="CardPileCmd.Shuffle"/>.
    /// </summary>
    public void Shuffle(Player player)
    {
        if (IsOverOrEnding)
        {
            return;
        }

        // Mirrors CardPileCmd.Shuffle: merge discard cards with current draw-pile cards,
        // shuffle the combined list, then place all cards back into the draw pile.
        var state = State.GetPlayerCombatState(player);
        var shuffledCards = state.DiscardPile.Cards.ToList();

        // The original code adds draw-pile cards through ToHashSet(), relying on the current
        // implementation's iteration order; card piles do not contain duplicates, so the preview
        // uses the source order directly instead of modeling that implementation detail.
        shuffledCards.AddRange(state.DrawPile.Cards);
        shuffledCards.StableShuffle(Rng.Shuffle);

        HookMirrors.ModifyShuffleOrder(this, player, shuffledCards, isInitialShuffle: false);

        AddToPile(shuffledCards, state.DrawPile);
        ShuffleEventCount++;

        if (!IsOverOrEnding)
        {
            HookMirrors.AfterShuffle(this, player);
        }
    }

    /// <summary>
    /// Mirrors <see cref="CardPileCmd.ShuffleIfNecessary"/>.
    /// </summary>
    private void ShuffleIfNecessary(Player player)
    {
        var state = State.GetPlayerCombatState(player);
        if (state.DrawPile.IsEmpty && !state.DiscardPile.IsEmpty)
        {
            Shuffle(player);
        }
    }

    /// <summary>
    /// Mirrors <see cref="CardPileCmd.AddToCombatAndPreview{T}(Creature, PileType, int, Player?, CardPilePosition)"/>.
    /// </summary>
    public void AddToCombat<TCard>(
        Creature target,
        PileType pileType,
        int count,
        Player? creator,
        CardPilePosition position = CardPilePosition.Bottom)
        where TCard : CardModel
    {
        var player = target.Player ?? target.PetOwner;
        if (player is null || State.GetCreature(player.Creature).IsDead)
        {
            return;
        }

        CreateAndAddGeneratedCardsToCombat<TCard>(player, pileType, count, creator, position);
    }

    /// <summary>
    /// Mirrors the common vanilla sequence of <see cref="CombatState.CreateCard{T}"/> followed by
    /// <see cref="CardPileCmd.AddGeneratedCardToCombat"/>. A generic card type determines the generated
    /// identity, so these entries are Fixed even when pile insertion uses Shuffle RNG.
    /// </summary>
    public IReadOnlyList<SimCardPileAddResult> CreateAndAddGeneratedCardsToCombat<TCard>(
        Player player,
        PileType pileType,
        int count,
        Player? creator,
        CardPilePosition position = CardPilePosition.Bottom)
        where TCard : CardModel
    {
        List<PredictedCard> cards = [];
        for (var i = 0; i < count; i++)
        {
            cards.Add(PredictedCard.Create(CanonicalModels.Card<TCard>(), player));
        }

        return AddGeneratedCardsToCombat(cards, pileType, creator, position, CardGenerationResultKind.Fixed);
    }

    /// <summary>
    /// Mirrors <see cref="CardPileCmd.AddGeneratedCardToCombat"/>.
    /// Adds one generated card while preserving how its result should be projected.
    /// </summary>
    public SimCardPileAddResult AddGeneratedCardToCombat(
        PredictedCard card,
        PileType newPileType,
        Player? creator,
        CardPilePosition position = CardPilePosition.Bottom,
        CardGenerationResultKind resultKind = CardGenerationResultKind.Random)
    {
        if (!IsInProgress)
            return new SimCardPileAddResult(false, card);

        return AddGeneratedCardsToCombat([card], newPileType, creator, position, resultKind)[0];
    }

    /// <summary>
    /// Mirrors <see cref="CardPileCmd.AddGeneratedCardsToCombat"/>.
    /// Adds generated cards and records whether each result is random, contextual, or fixed.
    /// </summary>
    /// <remarks>
    /// The result kind affects only projection; every card is still added to shadow state and dispatched through
    /// generation hooks and history.
    /// </remarks>
    public IReadOnlyList<SimCardPileAddResult> AddGeneratedCardsToCombat(
        IReadOnlyList<PredictedCard> cards,
        PileType newPileType,
        Player? creator,
        CardPilePosition position = CardPilePosition.Bottom,
        CardGenerationResultKind resultKind = CardGenerationResultKind.Random)
    {
        if (!IsInProgress || cards.Count == 0)
        {
            return [];
        }

        if (!newPileType.IsCombatPile())
        {
            throw new InvalidOperationException("Generated combat cards can only be added to combat piles.");
        }

        if (cards.Any(card => card.GetPile(State) is not null))
        {
            throw new InvalidOperationException("Generated combat cards cannot already be in a pile.");
        }

        List<SimCardPileAddResult> results = [];

        foreach (var card in cards)
        {
            var entry = History.CardGenerated(card, creator, resultKind);
            results.Add(AddToPile(card, newPileType, position));

            HookMirrors.AfterCardGeneratedForCombat(this, card, creator);
            if (HasPendingChoice)
                return results;
            History.CardGenerationResolved(entry, card);
        }

        return results;
    }

    // Convenience overload for AddToPile with a single card.
    public SimCardPileAddResult AddToPile(
        PredictedCard card,
        PileType newPileType,
        CardPilePosition position = CardPilePosition.Bottom,
        bool isChangingOwners = false)
    {
        SimCardPile newPile = State.GetPlayerCombatState(card.Preview.Owner).GetCardPile(newPileType)
            ?? throw new InvalidOperationException(
                $"Cannot find combat pile {newPileType} for player {card.Preview.Owner}.");
        return AddToPile(card, newPile, position, isChangingOwners);
    }

    public SimCardPileAddResult AddToPile(
        PredictedCard card,
        SimCardPile newPile,
        CardPilePosition position = CardPilePosition.Bottom,
        bool isChangingOwners = false)
    {
        if (IsEnding)
            return new SimCardPileAddResult(false, card);

        Player owner = card.Preview.Owner
            ?? throw new InvalidOperationException("Cannot add a card with no owner to a pile.");
        SimPlayerCombatState playerCombatState = State.GetPlayerCombatState(owner);
        SimCardPile? oldPile = card.GetPile(playerCombatState);
        PileType oldPileType = oldPile?.Type ?? PileType.None;
        SimCardPileAddResult result = new(true, card, oldPileType, newPile.Type);
        if (card.Preview.HasBeenRemovedFromState
            || State.GetCreature(owner.Creature).IsDead
            || oldPileType != PileType.None && !oldPileType.IsCombatPile())
        {
            return result with { Success = false };
        }

        int maxHandSize = GetMaxHandSize(owner);
        SimCardPile targetPile = newPile.Type == PileType.Hand
            && newPile.Cards.Count >= maxHandSize
                ? playerCombatState.DiscardPile
                : newPile;
        oldPile?.Remove(card);
        int index = position switch
        {
            CardPilePosition.Bottom => targetPile.Cards.Count,
            CardPilePosition.Top => 0,
            CardPilePosition.Random => Rng.Shuffle.NextInt(targetPile.Cards.Count + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(position), position, null)
        };
        targetPile.Insert(index, card);

        if (oldPileType == PileType.None && !isChangingOwners
            && State.CombatState is ICombatPredictionCardEventSink eventSink)
        {
            eventSink.AfterCardEnteredCombat(this, card);
        }
        return result;
    }

    public IReadOnlyList<SimCardPileAddResult> AddToPile(
        IReadOnlyList<PredictedCard> cards,
        PileType newPileType,
        CardPilePosition position = CardPilePosition.Bottom,
        bool isChangingOwners = false)
    {
        if (cards.Count == 0)
        {
            return [];
        }

        var newPile = State.GetPlayerCombatState(cards[0].Preview.Owner).GetCardPile(newPileType)
            ?? throw new InvalidOperationException(
                $"Cannot find combat pile {newPileType} for player {cards[0].Preview.Owner}.");
        return AddToPile(cards, newPile, position, isChangingOwners);
    }

    // Mirrors the combat-pile branch of CardPileCmd.Add(IEnumerable<CardModel>, CardPile, ...).
    public IReadOnlyList<SimCardPileAddResult> AddToPile(
        IReadOnlyList<PredictedCard> cards,
        SimCardPile newPile,
        CardPilePosition position = CardPilePosition.Bottom,
        bool isChangingOwners = false)
    {
        if (cards.Count == 0)
        {
            return [];
        }

        if (IsEnding)
        {
            return [.. cards.Select(card => new SimCardPileAddResult(false, card))];
        }

        var owner = cards[0].Preview.Owner
            ?? throw new InvalidOperationException("Cannot add cards with no owner to a pile.");
        var playerCombatState = State.GetPlayerCombatState(owner);

        List<SimCardPileAddResult> results = [];

        foreach (var card in cards)
        {
            if (card.Preview.Owner != owner)
            {
                throw new InvalidOperationException("Cannot add cards with different owners to the same pile.");
            }

            var oldPile = card.GetPile(playerCombatState);
            var oldPileType = oldPile?.Type ?? PileType.None;

            if (card.Preview.HasBeenRemovedFromState ||
                State.GetCreature(owner.Creature).IsDead ||
                (oldPileType != PileType.None && !oldPileType.IsCombatPile()))
            {
                results.Add(new SimCardPileAddResult(false, card, oldPileType, newPile.Type));
                continue;
            }

            // Vanilla checks for card.UpgradePreviewType.IsPreview() here and throws if true.
            // The simulator does not currently support preview cards, so this is intentionally omitted.

            results.Add(new SimCardPileAddResult(true, card, oldPileType, newPile.Type));
        }

        foreach (var result in results)
        {
            if (!result.Success)
            {
                continue;
            }

            var card = result.CardAdded;

            var targetPile = newPile;
            int maxHandSize = GetMaxHandSize(owner);
            if (targetPile.Type == PileType.Hand && targetPile.Cards.Count >= maxHandSize)
            {
                targetPile = playerCombatState.DiscardPile;
            }

            card.GetPile(playerCombatState)?.Remove(card);

            var index = position switch
            {
                CardPilePosition.Bottom => targetPile.Cards.Count,
                CardPilePosition.Top => 0,
                CardPilePosition.Random => Rng.Shuffle.NextInt(targetPile.Cards.Count + 1),
                _ => throw new ArgumentOutOfRangeException(nameof(position), position, null)
            };
            targetPile.Insert(index, card);

            // Vanilla CardPile.AddInternal updates CombatManager.StateTracker and raises pile UI events.
            // Prediction piles are plain model mirrors, and those UI-facing side effects are ignored.

            if (result.OldPileType == PileType.None && !isChangingOwners)
            {
                if (State.CombatState is ICombatPredictionCardEventSink eventSink)
                    eventSink.AfterCardEnteredCombat(this, card);
            }
        }

        // Vanilla dispatches Hook.AfterCardChangedPiles after visuals finish. Current vanilla
        // listeners are deck-only or VFX/music-only for combat piles, so this is intentionally
        // skipped until a prediction-relevant combat-pile listener appears.

        return results;
    }

    // Mirrors CardPileCmd.RemoveFromCombat without mutating the real combat piles.
    public void RemoveFromCombat(PredictedCard card)
    {
        SimCardPile pile = card.GetPile(State)
            ?? throw new InvalidOperationException("Cannot remove card from combat because it is not in a pile.");
        pile.Remove(card);
        card.MutablePreview.HasBeenRemovedFromState = true;
        card.NotifyHookListenerStructureChanged();
        if (State.CombatState is ICombatPredictionCardEventSink eventSink)
            eventSink.AfterCardRemovedFromCombat(card);
    }

    // Mirrors CardPileCmd.RemoveFromCombat without mutating the real combat piles.
    public void RemoveFromCombat(IReadOnlyList<PredictedCard> cards)
    {
        if (cards.Count == 0)
        {
            return;
        }

        List<(PredictedCard card, PileType oldPileType)> removedCards = [];

        foreach (var card in cards)
        {
            var pile = card.GetPile(State)
                ?? throw new InvalidOperationException("Cannot remove card from combat because it is not in a pile.");
            pile.Remove(card);
            removedCards.Add((card, pile.Type));
        }

        foreach (var (card, _) in removedCards)
        {
            // Vanilla dispatches Hook.AfterCardChangedPiles here, which is not mirrored for the same reasons
            // as in AddToPile.
            card.MutablePreview.HasBeenRemovedFromState = true;
            card.NotifyHookListenerStructureChanged();
            if (State.CombatState is ICombatPredictionCardEventSink eventSink)
                eventSink.AfterCardRemovedFromCombat(card);
        }
    }

    // Mirrors CardPileCmd.GiveToAnotherPlayer for the post-play result-location path.
    public void GiveToAnotherPlayer(
        PredictedCard card,
        Player originalOwner,
        Player newOwner,
        PileType pileType,
        CardPilePosition position)
    {
        if (IsOverOrEnding || State.GetCreature(newOwner.Creature).IsDead)
        {
            return;
        }

        var oldPile = card.GetPile(State.GetPlayerCombatState(originalOwner))
            ?? throw new InvalidOperationException(
                $"Cannot transfer {card.Preview.Id} because it is not in {originalOwner}'s combat piles.");

        oldPile.Remove(card);
        card.MutablePreview.GiveToAnotherPlayer(newOwner);
        card.NotifyHookListenerStructureChanged();
        AddToPile(card, pileType, position, isChangingOwners: true);
    }
}

/// <summary>
/// Mirrors <see cref="CardPileAddResult"/>.
/// </summary>
internal readonly record struct SimCardPileAddResult(
    bool Success,
    PredictedCard CardAdded,
    PileType OldPileType,
    PileType TargetPileType)
{
    public SimCardPileAddResult(bool success, PredictedCard cardAdded)
        : this(success, cardAdded, PileType.None, PileType.None)
    {
    }
}
