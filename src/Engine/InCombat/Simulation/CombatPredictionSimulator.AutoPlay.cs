using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    /// <summary>
    /// Mirrors <see cref="CardCmd.AutoPlay"/>.
    /// </summary>
    /// <param name="nestedChoiceSourceId">
    /// Identifies the effect that requested this auto-play, for the card's own selection. Vanilla awaits
    /// that selection inside <c>OnPlay</c>, so it is resolved within <see cref="OnPlayWrapper"/> and the
    /// selected card reaches its pile before this card moves to its result pile. Leave null only when the
    /// card cannot request a selection.
    /// </param>
    public bool AutoPlay(
        PredictedCard card,
        Creature? target = null,
        AutoPlayType type = AutoPlayType.Default,
        bool skipXCapture = false,
        string? nestedChoiceSourceId = null)
    {
        using IDisposable? modifierScope = (State.CombatState as ICombatPredictionCardExecutionSink)
            ?.BeginHistorySensitiveCardModifierScope(card);
        if (IsOverOrEnding || State.GetCreature(card.Preview.Owner.Creature).IsDead)
        {
            return false;
        }

        if (card.HasKeyword(State, CardKeyword.Unplayable) ||
            !HookMirrors.ShouldPlay(this, card, out _, type) ||
            !TryResolveAutoPlayTarget(card, ref target))
        {
            MoveToResultPileWithoutPlaying(card);
            return false;
        }

        if (card.GetPile(State) is null)
        {
            AddToPile(card, PileType.Play);
        }

        int historyEntryStart = History.Entries.Count;
        // Game 0.111.0 has no vanilla BeforeCardAutoPlayed listeners; the hook catalog will expose any future addition.
        var resources = SpendResources(card, isAutoPlay: true, skipXCapture);
        OnPlayWrapper(card, target, isAutoPlay: true, resources, out var frame, nestedChoiceSourceId);
        if (History.HasCardPlayStartedSince(historyEntryStart, frame)
            && !HasPendingChoice
            && State.CombatState is ICombatPredictionCardExecutionSink sink)
        {
            sink.CompleteCardExecution(this);
        }
        return true;
    }

    public bool PaidAutoPlay(
        PredictedCard card,
        Creature? target = null,
        string? nestedChoiceSourceId = null)
    {
        SpendResources(card, isAutoPlay: false);
        return AutoPlay(card, target, AutoPlayType.Default, skipXCapture: true, nestedChoiceSourceId);
    }

    /// <summary>
    /// True while a card selection has been requested but not yet supplied by a plan. Auto-play loops stop
    /// on this instead of advancing to the next card, matching vanilla's paused player choice.
    /// </summary>
    public bool HasPendingChoice
        => State.CombatState is ICombatPredictionPendingChoiceState { HasPendingChoice: true };

    private bool ResolveNestedAutoPlayChoice(PredictedCard card, string sourceId)
        => State.CombatState is not ICombatPredictionNestedChoiceSink sink
            || sink.ResolveNestedCardChoice(this, card, sourceId);

    /// <summary>
    /// Mirrors <see cref="CardPileCmd.AutoPlayFromDrawPile"/>.
    /// </summary>
    public void AutoPlayFromDrawPile(
        Player player,
        int count,
        CardPilePosition position,
        bool forceExhaust = false)
    {
        if (IsOverOrEnding)
        {
            return;
        }

        using IDisposable? scope = (State.CombatState as ICombatPredictionCardExecutionSink)
            ?.BeginCardExecutionScope();
        foreach (var card in MoveCardsForAutoPlay(player, count, position))
        {
            if (State.GetCreature(card.Preview.Owner.Creature).IsDead)
            {
                break;
            }

            card.MutablePreview.ExhaustOnNextPlay = forceExhaust;
            AutoPlay(card, nestedChoiceSourceId: card.Preview.Id.Entry);
            if (HasPendingChoice)
            {
                break;
            }
        }
    }

    // Mirrors CardPileCmd.AutoPlayFromDrawPile until the card is moved to the play pile.
    internal IReadOnlyList<PredictedCard> MoveCardsForAutoPlay(
        Player player,
        int count,
        CardPilePosition position)
    {
        var cards = new List<PredictedCard>(count);
        var playerCombatState = State.GetPlayerCombatState(player);
        var drawPile = playerCombatState.DrawPile;

        for (var i = 0; i < count; i++)
        {
            ShuffleIfNecessary(player);
            if (HasPendingChoice)
                break;
            var card = position switch
            {
                CardPilePosition.Top => drawPile.TopCard,
                CardPilePosition.Bottom => drawPile.BottomCard,
                CardPilePosition.Random => Rng.CombatCardSelection.NextItem(drawPile.Cards),
                _ => null
            };

            if (card is null)
            {
                break;
            }

            cards.Add(card);
            AddToPile(card, playerCombatState.PlayPile);
            History.AutoPlayFromDrawPile(card);
        }

        return cards;
    }

    // Mirrors the logic in CardCmd.AutoPlay for resolving a target when none is provided.
    private bool TryResolveAutoPlayTarget(PredictedCard card, ref Creature? target)
    {
        switch (GetTargetType(card))
        {
            case TargetType.AnyEnemy:
                target ??= Rng.CombatTargets.NextItem(State.HittableEnemies);
                return target != null;

            case TargetType.AnyAlly:
                target ??= Rng.CombatTargets.NextItem(State.Allies.Where(ally =>
                    ally.IsPlayer && ally != card.Preview.Owner.Creature && State.GetCreature(ally).IsAlive));
                return target != null;

            default:
                return true;
        }
    }
}
