using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private int SilentPowerProgressEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
    {
        if (!commitment.HasCard("MASTER_PLANNER"))
            return 0;
        return Math.Max(
            0,
            SlySkillValue(child.Snapshot.Simulator)
                - SlySkillValue(parent.Snapshot.Simulator));
    }

    private PowerEvidenceContribution SilentPowerRealizedEvidence(
        PowerCommitment commitment,
        SearchNode parent,
        SearchNode child)
    {
        long gain = 0;
        bool hasSpecializedEvidence = false;
        if (commitment.HasCard("AFTERIMAGE"))
        {
            hasSpecializedEvidence = true;
            if (child.Action is { Kind: PlanActionKind.PlayCard })
                gain += Math.Max(1, child.Snapshot.PlayerBlock - parent.Snapshot.PlayerBlock);
        }
        if (commitment.HasCard("FOOTWORK"))
        {
            hasSpecializedEvidence = true;
            if (child.Action is { Kind: PlanActionKind.PlayCard } action
                && IsBlockSkill(child.Snapshot.Simulator, action.CardId))
            {
                gain += Math.Max(1, child.Snapshot.PlayerBlock - parent.Snapshot.PlayerBlock);
                gain += Math.Max(
                    0,
                    child.Snapshot.ProjectedPlayerHp - parent.Snapshot.ProjectedPlayerHp);
            }
        }
        if (commitment.HasCard("WELL_LAID_PLANS"))
        {
            hasSpecializedEvidence = true;
            if (child.Turn > parent.Turn)
                gain += Math.Max(1, child.Snapshot.ReachableHandValue);
        }
        if (commitment.HasCard("MASTER_PLANNER"))
        {
            hasSpecializedEvidence = true;
            gain += SlyAutoPlayValue(
                child.Snapshot.Simulator,
                parent.Snapshot.HistoryEntryCount);
        }
        if (commitment.HasCard("SPEEDSTER"))
        {
            hasSpecializedEvidence = true;
            gain += SpeedsterDrawDamage(
                child.Snapshot.Simulator,
                parent.Snapshot.HistoryEntryCount,
                child.Snapshot.AliveEnemyCount);
        }
        if (commitment.HasCard("TOOLS_OF_THE_TRADE"))
        {
            hasSpecializedEvidence = true;
            if (child.Turn > parent.Turn)
                gain += Math.Max(1, child.Snapshot.ReachableHandValue);
        }
        return new PowerEvidenceContribution(
            hasSpecializedEvidence,
            (int)Math.Min(int.MaxValue, gain));
    }

    private int SlySkillValue(CombatPredictionSimulator simulator)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(_player);
        return state.Hand.Cards
            .Concat(state.DrawPile.Cards)
            .Concat(state.DiscardPile.Cards)
            .Where(card => card.Preview.Type == CardType.Skill
                && card.Preview.IsSlyThisTurn)
            .Sum(card => Math.Max(
                1,
                (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))));
    }

    private static int SlyAutoPlayValue(
        CombatPredictionSimulator simulator,
        int historyStart)
    {
        int value = 0;
        foreach (CombatPredictionHistoryEntry entry in simulator.History.EntriesFrom(historyStart))
        {
            if (entry is not CombatPredictionCardPlayStartedEntry started
                || !started.CardPlay.IsAutoPlay
                || started.CardPlay.Card.Type != CardType.Skill
                || !started.CardPlay.Card.IsSlyThisTurn)
            {
                continue;
            }
            value = SaturatingPowerCommitmentAdd(
                value,
                Math.Max(1, (int)Math.Round(
                    CardChoiceSupport.CardValue(started.CardPlay.Card))));
        }
        return value;
    }

    private static int SpeedsterDrawDamage(
        CombatPredictionSimulator simulator,
        int historyStart,
        int aliveEnemyCount)
    {
        int draws = 0;
        foreach (CombatPredictionHistoryEntry entry in simulator.History.EntriesFrom(historyStart))
        {
            if (entry is CombatPredictionCardDrawnEntry { FromHandDraw: false })
                draws++;
        }
        return (int)Math.Min(
            int.MaxValue,
            (long)draws * 2 * Math.Max(0, aliveEnemyCount));
    }

    private bool IsBlockSkill(CombatPredictionSimulator simulator, string cardId)
    {
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(_player);
        PredictedCard? card = state.Hand.Cards
            .Concat(state.DrawPile.Cards)
            .Concat(state.DiscardPile.Cards)
            .Concat(state.ExhaustPile.Cards)
            .FirstOrDefault(candidate => candidate.Preview.Id.Entry == cardId);
        return card?.Preview.Type == CardType.Skill
            && card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Block", StringComparison.OrdinalIgnoreCase));
    }
}
