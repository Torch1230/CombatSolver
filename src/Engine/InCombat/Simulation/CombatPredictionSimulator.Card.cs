using System.Diagnostics;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Afflictions.OnPlay;
using CombatSolver.Engine.InCombat.Mirrors.Cards;
using CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;
using CombatSolver.Engine.InCombat.Mirrors.Enchantments.OnPlay;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    // Mirrors CardPileCmd.AddDuringManualCardPlay, which is called when a card is manually played
    // from hand and is added to the play pile.
    public void AddDuringManualCardPlay(PredictedCard card)
    {
        if (IsOverOrEnding)
        {
            return;
        }

        card.GetPile(State)?.Remove(card);
        State.GetPlayerCombatState(card.Preview.Owner).PlayPile.Add(card);

        // Vanilla dispatches Hook.AfterCardChangedPiles after visuals finish. This is intentionally
        // skipped currently, for the same reasons as in AddToPile.
    }

    // Mirrors CardCmd.MoveToResultPileWithoutPlaying, not CardModel.MoveToResultPileWithoutPlaying.
    // CardCmd first moves the card to the play pile, then calls the CardModel method; this
    // inlines both steps.
    public void MoveToResultPileWithoutPlaying(PredictedCard card)
    {
        AddToPile(card, PileType.Play);

        if (card.Preview.IsDupe)
        {
            RemoveFromCombat(card);
        }
        else if (card.Preview.ExhaustOnNextPlay || card.HasKeyword(State, CardKeyword.Exhaust))
        {
            Exhaust(card);
        }
        else
        {
            AddToPile(card, PileType.Discard);
        }
    }

    // Mirrors CardCmd.Discard(PlayerChoiceContext, CardModel).
    // Useful when discarding a single card and drawing no cards.
    public void Discard(PredictedCard card)
    {
        if (IsOverOrEnding)
            return;

        bool isSly = card.Preview.IsSlyThisTurn;
        AddToPile(card, PileType.Discard);
        if (State.CombatState is ICombatPredictionCardEventSink eventSink)
            eventSink.RecordCardDiscarded(card.Preview.Owner.Creature);
        HookMirrors.AfterCardDiscarded(this, card);
        if (isSly)
            AutoPlay(card, type: AutoPlayType.SlyDiscard, nestedChoiceSourceId: card.Preview.Id.Entry);
    }

    // Mirrors CardCmd.Discard(PlayerChoiceContext, IEnumerable<CardModel>).
    // Useful when discarding multiple cards and drawing no cards.
    public void Discard(IReadOnlyList<PredictedCard> cards)
    {
        DiscardAndDraw(cards, 0);
    }

    // Mirrors CardCmd.DiscardAndDraw.
    public void DiscardAndDraw(IReadOnlyList<PredictedCard> cardsToDiscard, int cardsToDraw)
    {
        if (IsOverOrEnding || cardsToDiscard.Count == 0 && cardsToDraw == 0)
        {
            return;
        }

        List<PredictedCard> slyCards = [];

        foreach (var card in cardsToDiscard)
        {
            if (card.Preview.IsSlyThisTurn)
            {
                slyCards.Add(card);
            }

            AddToPile(card, PileType.Discard);
            if (State.CombatState is ICombatPredictionCardEventSink eventSink)
                eventSink.RecordCardDiscarded(card.Preview.Owner.Creature);
            HookMirrors.AfterCardDiscarded(this, card);
        }

        if (cardsToDraw > 0)
        {
            Draw(cardsToDiscard[0].Preview.Owner, cardsToDraw);
        }

        foreach (var slyCard in slyCards)
        {
            AutoPlay(slyCard, type: AutoPlayType.SlyDiscard, nestedChoiceSourceId: slyCard.Preview.Id.Entry);
            if (HasPendingChoice)
            {
                break;
            }
        }
    }

    // Mirrors CardCmd.Exhaust.
    public void Exhaust(PredictedCard card, bool causedByEthereal = false)
    {
        if (IsOverOrEnding)
        {
            return;
        }

        AddToPile(card, PileType.Exhaust);
        if (State.CombatState is ICombatPredictionCardEventSink eventSink)
            eventSink.RecordCardExhausted(card.Preview.Owner.Creature);
        HookMirrors.AfterCardExhausted(this, card, causedByEthereal);
    }

    /// <summary>
    /// Mirrors the prediction-relevant portion of <see cref="PlayCardAction.ExecuteAction"/> for a manual card play.
    /// </summary>
    /// <param name="card">The prediction-owned card wrapper to play.</param>
    /// <param name="target">The already-resolved target, if required.</param>
    /// <param name="frame">The exact root card-play frame.</param>
    /// <remarks>
    /// The returned frame has <see cref="PredictedCard.Original"/> as its source and
    /// <see cref="PredictionActionKind.CardPlay"/> as its action. It remains a stable identity after its trace scope is
    /// disposed and must be paired only with this simulator's history. Card playability and target validation checks
    /// are outside this entry point; callers must perform any required UI/target gating before invocation.
    /// </remarks>
    public void ManualPlay(PredictedCard card, Creature? target, out PredictionTraceFrame frame)
    {
        int historyEntryStart = History.Entries.Count;
        var resources = SpendResources(card, isAutoPlay: false);
        OnPlayWrapper(card, target, isAutoPlay: false, resources, out frame);
        if (HasCardPlayStartedSince(historyEntryStart, card)
            && !HasPendingChoice
            && State.CombatState is ICombatPredictionCardExecutionSink sink)
        {
            sink.CompleteCardExecution(this);
        }
    }

    private bool HasCardPlayStartedSince(int historyEntryStart, PredictedCard card)
    {
        foreach (CombatPredictionHistoryEntry entry in History.EntriesFrom(historyEntryStart))
        {
            if (entry is CombatPredictionCardPlayStartedEntry started
                && ReferenceEquals(started.Card, card))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Mirrors <see cref="CardModel.CanPlay()"/>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CardModel.CanPlay()"/>, this mirror does not check whether there are living allies
    /// for <see cref="TargetType.AnyAlly"/> cards, since <see cref="CardModel.IsValidTarget(Creature?)"/>
    /// already rejects null or dead ally targets.
    /// </remarks>
    public bool CanPlay(PredictedCard card)
    {
        if (card.HasKeyword(State, CardKeyword.Unplayable))
        {
            return false;
        }

        var ownerState = State.GetPlayerCombatState(card.Preview.Owner);
        var energyCost = card.GetEnergyCostWithModifiers(this, ownerState);
        var starCost = card.GetStarCostWithModifiers(this, ownerState);

        if (energyCost > ownerState.Energy &&
            Hook.ShouldPayExcessEnergyCostWithStars(State.CombatState, card.Preview.Owner))
        {
            starCost += 2 * (energyCost - ownerState.Energy);
            energyCost = ownerState.Energy;
        }

        if (energyCost > ownerState.Energy || starCost > ownerState.Stars)
        {
            return false;
        }

        if (!HookMirrors.ShouldPlay(this, card, out _, AutoPlayType.None))
        {
            return false;
        }

        if (!CardIsPlayableMirrors.Invoke(this, card))
        {
            return false;
        }

        return true;
    }

    // Mirrors CardModel.SpendResources, but returns ResourceInfo instead of (int, int) for convenience.
    // Also implements the auto-play logic for capturing X values and star costs, which is handled in CardCmd.AutoPlay
    // in vanilla.
    private ResourceInfo SpendResources(PredictedCard card, bool isAutoPlay, bool skipXCapture = false)
    {
        var playerCombatState = State.GetPlayerCombatState(card.Preview.Owner);
        var energyValue = card.GetEnergyCostWithModifiers(this, playerCombatState);
        var starValue = card.GetStarCostWithModifiers(this, playerCombatState);

        if (!isAutoPlay && energyValue > playerCombatState.Energy &&
            Hook.ShouldPayExcessEnergyCostWithStars(State.CombatState, card.Preview.Owner))
        {
            starValue += 2 * (energyValue - playerCombatState.Energy);
            energyValue = playerCombatState.Energy;
        }

        if (!skipXCapture)
        {
            if (card.Preview.EnergyCost.CostsX)
            {
                card.MutablePreview.EnergyCost.CapturedXValue = energyValue;
            }
            card.MutablePreview.LastStarsSpent = starValue;
        }

        if (isAutoPlay)
        {
            return new ResourceInfo
            {
                EnergySpent = 0,
                EnergyValue = energyValue,
                StarsSpent = 0,
                StarValue = starValue
            };
        }

        // Mirrors CardModel.SpendEnergy and CardModel.SpendStars.
        if (energyValue > 0)
        {
            if (State.CombatState is ICombatPredictionCardEventSink eventSink)
                eventSink.RecordEnergySpent(card.Preview.Owner, energyValue);
            playerCombatState.LoseEnergy(energyValue);
        }
        if (State.CombatState is ICombatPredictionCardEventSink energySink)
            energySink.AfterEnergySpent(this, card, energyValue);

        card.MutablePreview.LastStarsSpent = starValue;
        if (starValue > 0)
        {
            playerCombatState.LoseStars(starValue);
            if (State.CombatState is ICombatPredictionCardEventSink starSink)
                starSink.AfterStarsSpent(this, card, starValue);
        }

        return new ResourceInfo
        {
            EnergySpent = energyValue,
            EnergyValue = energyValue,
            StarsSpent = starValue,
            StarValue = starValue
        };
    }

    /// <summary>
    /// Mirrors <see cref="CardModel.OnPlayWrapper"/>.
    /// </summary>
    private void OnPlayWrapper(
        PredictedCard card,
        Creature? target,
        bool isAutoPlay,
        ResourceInfo resources,
        out PredictionTraceFrame frame,
        string? nestedChoiceSourceId = null)
    {
        using var _ = PushActionSource(card.Original, PredictionActionKind.CardPlay);
        frame = CurrentFrame ?? throw new UnreachableException("No current frame after pushing action source.");

        var previewCard = card.MutablePreview;
        var originalOwner = previewCard.Owner;
        previewCard.CurrentTarget = target;
        previewCard.CurrentPlayIndex = 0;

        if (isAutoPlay)
        {
            AddToPile(card, PileType.Play);
        }
        else
        {
            AddDuringManualCardPlay(card);
        }

        var resultLocation = CardResultLocationMirrors.GetResultLocation(this, card);
        resultLocation = HookMirrors.ModifyCardPlayResultLocation(
            this,
            card,
            isAutoPlay,
            resources,
            resultLocation,
            out var resultLocationModifiers);
        HookMirrors.AfterModifyingCardPlayResultLocation(
            this,
            card,
            resultLocation,
            resultLocationModifiers);

        var playCount = card.GeneratePlayCount(this, target);
        var ownerCreature = State.GetCreature(originalOwner.Creature);
        if (ownerCreature.IsDead)
        {
            return;
        }

        for (var i = 0; i < playCount; i++)
        {
            if (IsOverOrEnding)
            {
                break;
            }

            previewCard.CurrentPlayIndex = i;
            int ownerBlockBeforePlay = ownerCreature.Block;
            int playHistoryEntryStart = History.Entries.Count;

            var cardPlay = new CardPlay
            {
                Card = previewCard,
                Player = originalOwner,
                Target = target,
                ResultPile = resultLocation.pileType,
                Resources = resources,
                IsAutoPlay = isAutoPlay,
                PlayIndex = i,
                PlayCount = playCount
            };

            HookMirrors.BeforeCardPlayed(this, card, cardPlay);
            SynchronizePowerAmountPredictionStates();
            History.CardPlayStarted(card, cardPlay);
            if (State.CombatState is ICombatPredictionCardExecutionSink startedSink)
                startedSink.RecordCardPlayStarted(card, cardPlay);

            ICombatPredictionCardExecutionSink? effectSink =
                State.CombatState as ICombatPredictionCardExecutionSink;
            decimal cardBlockGained;
            using (effectSink?.BeginCardPowerApplication(card))
            {
                CardOnPlayMirrors.Invoke(this, card, cardPlay);
                cardBlockGained = TakeBlockGained(cardPlay);
                effectSink?.ApplyCardPlayEffects(
                    this,
                    card,
                    cardPlay,
                    target,
                    ownerBlockBeforePlay,
                    cardBlockGained,
                    playHistoryEntryStart);
            }

            // OnPlay can suspend on a triggered choice before the card's own selector opens.
            if (State.CombatState is ICombatPredictionPendingChoiceState { HasPendingChoice: true })
            {
                HookMirrors.AbortCardPlayed(this, cardPlay);
                return;
            }

            if (!isAutoPlay
                && State.CombatState is ICombatPredictionManualCardChoiceSink choiceSink
                && !choiceSink.ResolveManualCardChoice(this, card))
            {
                HookMirrors.AbortCardPlayed(this, cardPlay);
                return;
            }

            // Vanilla awaits an auto-played card's own selection inside OnPlay, so the selected card
            // reaches its pile before this card moves to its result pile.
            if (isAutoPlay
                && nestedChoiceSourceId != null
                && !ResolveNestedAutoPlayChoice(card, nestedChoiceSourceId))
            {
                HookMirrors.AbortCardPlayed(this, cardPlay);
                return;
            }

            if (ownerCreature.IsDead)
            {
                HookMirrors.AbortCardPlayed(this, cardPlay);
                return;
            }

            if (previewCard.Enchantment is { } enchantment)
            {
                EnchantmentOnPlayMirrors.Invoke(this, card, cardPlay, enchantment);

                if (ownerCreature.IsDead)
                {
                    HookMirrors.AbortCardPlayed(this, cardPlay);
                    return;
                }
            }

            if (previewCard.Affliction is { } affliction)
            {
                AfflictionOnPlayMirrors.Invoke(this, card, target, affliction);

                if (ownerCreature.IsDead)
                {
                    HookMirrors.AbortCardPlayed(this, cardPlay);
                    return;
                }
            }

            int completionHistoryEntryStart = History.Entries.Count;
            History.CardPlayFinished(
                card,
                cardPlay,
                card.HasKeyword(State, CardKeyword.Ethereal));
            HookMirrors.AfterCardPlayed(this, card, cardPlay);
            if (State.CombatState is ICombatPredictionCardExecutionSink completionSink)
            {
                completionSink.CompleteCardPlayEffects(
                    this,
                    card,
                    ownerBlockBeforePlay,
                    completionHistoryEntryStart);
            }

            if (ownerCreature.IsDead)
            {
                return;
            }
        }

        if (originalOwner != resultLocation.player && resultLocation.pileType != PileType.None)
        {
            GiveToAnotherPlayer(
                card,
                originalOwner,
                resultLocation.player,
                resultLocation.pileType,
                resultLocation.position);
        }

        if (card.GetPile(State)?.Type is PileType.Play)
        {
            switch (resultLocation.pileType)
            {
                case PileType.None:
                    RemoveFromCombat(card);
                    break;
                case PileType.Exhaust:
                    Exhaust(card);
                    break;
                default:
                    AddToPile(card, resultLocation.pileType, resultLocation.position);
                    break;
            }
        }

        if (State.CombatState is ICombatPredictionCardEventSink handSink)
            handSink.AfterHandEmptied(this, originalOwner);

        previewCard.EnergyCost.AfterCardPlayedCleanup();
        previewCard._temporaryStarCosts.RemoveAll(cost => cost.ClearsWhenCardIsPlayed);
        card.InvalidateCaches();

        previewCard.CurrentTarget = null;
        previewCard.CurrentPlayIndex = 0;
        SynchronizePowerAmountPredictionStates();
    }

    // Mirrors CardModel.Afflict<T>.
    public T? Afflict<T>(PredictedCard card, decimal amount) where T : AfflictionModel
    {
        return Afflict(CanonicalModels.Affliction<T>().ToMutable(), card, amount) as T;
    }

    /// <summary>
    /// Mirrors <see cref="CardCmd.Afflict(AfflictionModel, CardModel, decimal)"/>.
    /// </summary>
    public AfflictionModel? Afflict(AfflictionModel affliction, PredictedCard card, decimal amount)
    {
        if (IsOverOrEnding)
        {
            return null;
        }

        affliction.AssertMutable();

        if (!Hook.ShouldAfflict(State.CombatState, card.Preview, affliction) ||
            !affliction.CanAfflict(card.Preview))
        {
            return null;
        }

        if (card.Preview.Affliction == null)
        {
            card.Afflict(affliction, amount);
            // Currently, no vanilla affliction overrides AfterApplied, but it is called here for completeness.
            affliction.AfterApplied();
        }
        else
        {
            if (card.Preview.Affliction.GetType() != affliction.GetType())
            {
                return null;
            }

            // We don't use AfflictionModel.Amount here because its setter recalculates values through
            // the real owner PlayerCombatState even though this is only a preview card.
            card.MutablePreview.Affliction!._amount += (int)amount;
        }

        History.CardAfflicted(card, affliction);
        return card.Preview.Affliction;
    }

    /// <summary>
    /// Mirrors <see cref="CardCmd.Upgrade(CardModel, MegaCrit.Sts2.Core.Nodes.CommonUi.CardPreviewStyle)"/>.
    /// </summary>
    public bool Upgrade(PredictedCard card)
    {
        if (IsEnding)
        {
            return false;
        }

        card.Upgrade();
        return true;
    }
}
