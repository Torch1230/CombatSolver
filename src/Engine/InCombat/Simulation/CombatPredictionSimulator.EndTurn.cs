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
    internal bool SimulateEndPlayerTurnAfterOrbPassives(int playerTurn)
    {
        var playersEndingTurn = CombatManager.Instance.PlayersTakingExtraTurn switch
        {
            { Count: > 0 } extraTurnPlayers => extraTurnPlayers,
            _ => State.CombatState.Players
        };

        foreach (var player in playersEndingTurn)
        {
            HookMirrors.AfterAutoPostPlayPhaseEntered(this, player);
            if (HasPendingChoice)
                return false;
        }

        HookMirrors.BeforeSideTurnEnd(
            this,
            State.CombatState.CurrentSide,
            [.. playersEndingTurn.Select(static player => player.Creature)]);
        if (HasPendingChoice)
            return false;
        SynchronizePowerAmountPredictionStates();

        if (CheckWinCondition(playerTurn))
        {
            return true;
        }

        foreach (var player in playersEndingTurn)
        {
            if (!DoTurnEnd(player))
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
    private bool DoTurnEnd(Player player)
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
            return DoTurnEndCards(turnEndCards);
        return true;
    }

    /// <summary>
    /// Mirrors the prediction-relevant parts of <see cref="CombatManager.DoTurnEndCards"/>.
    /// </summary>
    private bool DoTurnEndCards(IEnumerable<PredictedCard> cards)
    {
        foreach (var card in cards)
        {
            AddToPile(card, PileType.Play);
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
