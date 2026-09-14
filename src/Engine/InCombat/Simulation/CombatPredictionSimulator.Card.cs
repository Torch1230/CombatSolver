using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
        if (HasPendingChoice)
        {
            if (isSly) AppendExecutionContinuation(new DiscardSlyExecutionFrame(card));
            return;
        }
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

        ContinueDiscardAndDrawExecution(cardsToDiscard, cardsToDraw, [], DiscardExecutionStage.Discard, 0);
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
    public bool ManualPlay(
        PredictedCard card,
        Creature? target,
        [NotNullWhen(true)] out PredictionTraceFrame? frame)
    {
        int historyEntryStart = History.Entries.Count;
        var resources = SpendResources(card, isAutoPlay: false);
        if (HasPendingChoice)
        {
            frame = null;
            return false;
        }
        OnPlayWrapper(card, target, isAutoPlay: false, resources, out frame);
        if (HasPendingChoice && History.HasCardPlayStartedSince(historyEntryStart, frame))
            AppendExecutionContinuation(new FinishCardExecutionFrame());
        if (History.HasCardPlayStartedSince(historyEntryStart, frame)
            && !HasPendingChoice
            && State.CombatState is ICombatPredictionCardExecutionSink sink)
        {
            sink.CompleteCardExecution(this);
        }
        return !HasPendingChoice;
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
        => CanPlay(card, out _, out _);

    // Return the queried costs before excess-energy conversion. Search valuation uses the
    // unconverted prices too, and must not invoke the same read-only cost hooks a second time.
    public bool CanPlay(PredictedCard card, out int energyCost, out int starCost)
    {
        energyCost = 0;
        starCost = 0;
        if (card.HasKeyword(State, CardKeyword.Unplayable))
        {
            return false;
        }

        var ownerState = State.GetPlayerCombatState(card.Preview.Owner);
        energyCost = card.GetEnergyCostWithModifiers(this, ownerState);
        starCost = card.GetStarCostWithModifiers(this, ownerState);
        int payableEnergy = energyCost;
        int payableStars = starCost;

        if (payableEnergy > ownerState.Energy &&
            Hook.ShouldPayExcessEnergyCostWithStars(State.CombatState, card.Preview.Owner))
        {
            payableStars += 2 * (payableEnergy - ownerState.Energy);
            payableEnergy = ownerState.Energy;
        }

        if (payableEnergy > ownerState.Energy || payableStars > ownerState.Stars)
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
        string? nestedChoiceSourceId = null,
        string? nestedChoiceContextId = null)
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
        if (HasPendingChoice)
        {
            RejectExecutionContinuation();
            return;
        }
        HookMirrors.AfterModifyingCardPlayResultLocation(
            this,
            card,
            resultLocation,
            resultLocationModifiers);
        if (HasPendingChoice)
        {
            RejectExecutionContinuation();
            return;
        }

        var playCount = card.GeneratePlayCount(this, target);
        var ownerCreature = State.GetCreature(originalOwner.Creature);
        if (ownerCreature.IsDead)
        {
            return;
        }

        ContinueCardPlayExecution(card, target, isAutoPlay, resources, resultLocation, playCount,
            nestedChoiceSourceId, nestedChoiceContextId, 0);
    }

    private bool CompleteManualCardPlayTail(PredictedCard card, Creature? target, CardPlay cardPlay,
        int ownerBlockBeforePlay)
        => ContinueCardPlayTailExecution(card, target, cardPlay, ownerBlockBeforePlay, CardPlayTailStage.Enchantment, 0);

    private void CompleteManualCardResultTail(PredictedCard card,
        MegaCrit.Sts2.Core.Entities.Players.Player originalOwner, CardLocation resultLocation)
        => ContinueCardResultExecution(card, originalOwner, resultLocation, CardResultStage.Transfer);

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
