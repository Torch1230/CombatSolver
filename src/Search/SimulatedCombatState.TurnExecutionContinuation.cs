using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private sealed record BeforeHandDrawRelicFrame(Player Player, IReadOnlyList<RelicModel> Relics,
        int Turn, int NextIndex) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Relics = Relics.Select(context.RemapOrSelf).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            return !combat.ContinueRelicsBeforeHandDraw(simulator, Player, combat.ActiveExecutionChoices, Relics, Turn, NextIndex);
        }
    }

    private sealed record AfterPlayerTurnStartRelicFrame(Player Player, IReadOnlyList<RelicModel> Relics,
        int Turn, int NextIndex, bool ApplyMittensStrength = false) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Relics = Relics.Select(context.RemapOrSelf).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            return !combat.ContinueRelicsAfterPlayerTurnStart(simulator, Player, combat.ActiveExecutionChoices,
                Relics, Turn, NextIndex, ApplyMittensStrength);
        }
    }

    private enum BeforeHandDrawStage { Relics, ReturningCards, RemoveReturnedCard }

    private sealed record BeforeHandDrawFrame(Player Player, IReadOnlyList<PredictedCard> ReturningCards,
        BeforeHandDrawStage Stage, int NextIndex = 0) : ICombatPredictionExecutionFrame
    {
        public void PrepareFork(PredictionForkContext context)
        {
            foreach (PredictedCard card in ReturningCards)
                if (!context.TryRemap(card, out PredictedCard? _)) card.Fork(context);
        }
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { ReturningCards = ReturningCards.Select(context.RequireRemap).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            return !combat.ContinueBeforeHandDraw(simulator, Player, combat.ActiveExecutionChoices,
                ReturningCards, Stage, NextIndex);
        }
    }

    private bool ContinueBeforeHandDraw(CombatPredictionSimulator simulator, Player player,
        TurnStartChoiceCursor choices, IReadOnlyList<PredictedCard> returningCards, BeforeHandDrawStage stage, int nextIndex = 0)
    {
        if (stage == BeforeHandDrawStage.Relics && PrepareRelicsBeforeHandDraw(simulator, player, choices))
        {
            simulator.AppendExecutionContinuation(new BeforeHandDrawFrame(player, returningCards, BeforeHandDrawStage.ReturningCards));
            return true;
        }
        if (_returnToHandNextTurn != null)
        {
            for (int index = nextIndex; index < returningCards.Count; index++)
            {
                PredictedCard card = returningCards[index];
                if (stage == BeforeHandDrawStage.RemoveReturnedCard)
                {
                    _returnToHandNextTurn.Remove(card);
                    stage = BeforeHandDrawStage.ReturningCards;
                    continue;
                }
                SimCardPile? pile = card.GetPile(simulator.State);
                if (card.Preview.HasBeenRemovedFromState)
                {
                    _returnToHandNextTurn.Remove(card);
                    continue;
                }
                if (pile?.Type != PileType.Hand)
                {
                    simulator.AddToPile(card, PileType.Hand);
                    if (HasPendingChoice)
                    {
                        simulator.AppendExecutionContinuation(new BeforeHandDrawFrame(player, returningCards, BeforeHandDrawStage.RemoveReturnedCard, index));
                        return true;
                    }
                }
                _returnToHandNextTurn.Remove(card);
            }
        }
        return false;
    }

    private sealed record AfterPlayerTurnStartFrame(Player Player) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context) => this;
        public bool Resume(CombatPredictionSimulator simulator)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            return !combat.TriggerRelicsAfterPlayerTurnStart(simulator, Player, combat.ActiveExecutionChoices);
        }
    }
}
