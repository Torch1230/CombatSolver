using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Cards;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    /// <summary>
    /// Currently mirrors the prediction-relevant parts of <see cref="CombatManager.EndPlayerTurnPhaseOneInternal()"/>.
    /// </summary>
    internal bool SimulateEndPlayerTurnBeforeOrbPassives(int playerTurn)
    {
        var playersEndingTurn = State.CombatState.Players;

        foreach (var player in playersEndingTurn)
        {
            State.GetPlayerCombatState(player).Phase = PlayerTurnPhase.AutoPostPlay;
            HookMirrors.AfterAutoPostPlayPhaseEntered(this, player);
            if (HasPendingChoice)
                return false;
        }

        foreach (var player in playersEndingTurn)
            State.GetPlayerCombatState(player).Phase = PlayerTurnPhase.End;

        HookMirrors.BeforeSideTurnEnd(
            this,
            State.CombatState.CurrentSide,
            [.. playersEndingTurn.Select(static player => player.Creature)]);
        if (HasPendingChoice)
            return false;
        SynchronizePowerAmountPredictionStates();

        CheckWinCondition(playerTurn);
        return true;
    }

    internal bool SimulateEndPlayerTurnAfterOrbPassives(int playerTurn, bool stageHandEndCardsTogether = false)
    {
        if (IsOverOrEnding)
            return true;
        var playersEndingTurn = State.CombatState.Players;

        foreach (var player in playersEndingTurn)
        {
            if (!DoTurnEnd(player, stageHandEndCardsTogether))
                return false;
        }

        if (CheckWinCondition(playerTurn))
        {
            return true;
        }

        // Vanilla next calls Hook.BeforeFlush for each ending player. Its only vanilla listener is
        // SlumberingEssence, which is not used by the current version of the base game, so the hook is omitted.
        return true;
    }

    /// <summary>
    /// Mirrors the prediction-relevant parts of <see cref="CombatManager.DoTurnEnd"/>.
    /// </summary>
    private bool DoTurnEnd(Player player, bool stageHandEndCardsTogether)
    {
        var playerState = State.GetPlayerCombatState(player);
        if (IsOverOrEnding)
        {
            return true;
        }

        List<PredictedCard>? turnEndCards = null;
        List<PredictedCard>? etherealCards = null;

        foreach (var card in playerState.Hand)
        {
            if (card.Preview.HasTurnEndInHandEffect)
            {
                (turnEndCards ??= []).Add(card);
            }
            else if (card.HasKeyword(State, CardKeyword.Ethereal) &&
                     Hook.ShouldEtherealTrigger(State.CombatState, card.Preview))
            {
                (etherealCards ??= []).Add(card);
            }
        }

        if (etherealCards != null)
        {
            foreach (PredictedCard card in etherealCards)
            {
                Exhaust(card, causedByEthereal: true);
                if (HasPendingChoice)
                    return false;
            }
        }

        if (turnEndCards != null)
            return DoTurnEndCards(turnEndCards, stageHandEndCardsTogether);
        return true;
    }

    /// <summary>
    /// Mirrors the prediction-relevant parts of <see cref="CombatManager.DoTurnEndCards"/>.
    /// </summary>
    private bool DoTurnEndCards(IReadOnlyList<PredictedCard> cards, bool stageTogether)
    {
        // Native Instant visuals yield after starting each pile move, so all entries can
        // precede the first effect. The caller must capture/admit this scheduling contract;
        // the simulator never reads live animation settings on a worker.
        if (stageTogether)
            foreach (var card in cards) AddToPile(card, PileType.Play);
        foreach (var card in cards)
        {
            if (!stageTogether) AddToPile(card, PileType.Play);
            CardOnTurnEndInHandMirrors.Invoke(this, card);
            if (HasPendingChoice)
                return false;

            // Vanilla does not check Hook.ShouldEtherealTrigger here, so we keep the same behavior.
            if (card.HasKeyword(State, CardKeyword.Ethereal))
            {
                Exhaust(card, causedByEthereal: true);
                if (HasPendingChoice)
                    return false;
            }
            else
            {
                AddToPile(card, PileType.Discard);
            }
        }
        return true;
    }
}
