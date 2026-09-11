using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver;

/// <summary>Bridge for the production plan protocol and an admitted value workspace.</summary>
internal sealed class CompactPlanReplay
{
    private readonly CompactDiscardProjection _adapter;
    private readonly CompactCardMetadataReadBinding _metadata;

    internal CompactPlanReplay(CompactDiscardProjection adapter)
    {
        _adapter = adapter;
        _metadata = adapter.CreatePlanMetadata();
    }

    internal void Execute(ResumableDiscardProgram lane, PlanAction action)
    {
        if (!TryExecute(lane, action, default))
            throw new InvalidOperationException("Compact execution requires an omitted plan choice.");
    }

    // A missing choice is a defined search boundary. All supplied choices and execution
    // failures remain strict; the caller restores its frozen parent before another attempt.
    internal bool TryExecute(ResumableDiscardProgram lane, PlanAction action, CancellationToken cancellationToken)
    {
        if (action.Turn != lane.PlayerTurn || action.EndsPlayerTurn || action.ReplayCount != 0)
            throw new NotSupportedException("Compact plan has an unrepresented turn or replay request.");
        IReadOnlyList<PlanCardChoice> choices;
        if (action.Kind == PlanActionKind.PlayCard)
        {
            _metadata.Read(lane);
            int[] hand = lane.Cards(ResumableDiscardProgram.Pile.Hand);
            var card = CombatBeamSolver.FindCardForReplay(hand.Select(id => _metadata[id]).ToArray(), action)
                ?? throw new InvalidOperationException("Compact plan card instance is absent from hand.");
            int identity = hand.Single(id => ReferenceEquals(_metadata[id], card));
            int target = action.TargetCombatId == null ? -1 : Enumerable.Range(1, lane.CreatureCount - 1)
                .Single(id => _adapter.Creature(id).CombatId == action.TargetCombatId);
            lane.Begin(identity, target);
            choices = action.GetActionChoicesInExecutionOrder();
            if (action.TurnStartChoices is { Count: > 0 })
                throw new NotSupportedException("Compact card plan unexpectedly includes a turn-start suffix.");
        }
        else if (action.Kind == PlanActionKind.EndTurn)
        {
            lane.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Together);
            choices = action.TurnStartChoices ?? [];
        }
        else throw new NotSupportedException("Compact plan has no admitted potion protocol.");

        int consumed = 0;
        while (!lane.Complete)
        {
            lane.Run(cancellationToken);
            if (!lane.NeedsChoice) continue;
            if (consumed == choices.Count) return false;
            var choice = choices[consumed++];
            PileType pile = lane.ChoicePile == ResumableDiscardProgram.Pile.Draw ? PileType.Draw : PileType.Hand;
            PlanChoiceEffect effect = pile == PileType.Draw ? PlanChoiceEffect.MoveToHand : PlanChoiceEffect.Discard;
            if (choice.SourcePile != pile || choice.Effect != effect || choice.Cards.Count != lane.ChoiceCount)
                throw new InvalidOperationException("Compact choice effect, pile or cardinality differs from the plan.");
            _metadata.Read(lane);
            int[] options = lane.Cards(lane.ChoicePile);
            int[] selected = choice.Cards.Select(token => options.Where(id => CardChoiceSupport.MatchesToken(_metadata[id], token))
                .Skip(token.OptionOccurrence).First()).ToArray();
            lane.SupplyChoice(selected);
        }
        if (consumed != choices.Count) throw new InvalidOperationException("Compact execution left unconsumed plan choices.");
        lane.CheckWinCondition();
        return true;
    }
}
