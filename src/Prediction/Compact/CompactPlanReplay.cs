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
            int target = action.TargetCombatId == null ? -1 : Enumerable.Range(1, lane.EnemyEnd - 1)
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

        var cursor = new TurnStartChoiceCursor(choices);
        while (!lane.Complete)
        {
            lane.Run(cancellationToken);
            if (!lane.NeedsChoice) continue;
            var request = DescribeChoice(lane, action.Kind == PlanActionKind.EndTurn
                ? PlanChoiceTiming.PlayerTurnStart : PlanChoiceTiming.Action);
            if (!cursor.TryTake(request, out var choice)) return false;
            if (choice!.Cards.Count != lane.ChoiceCount)
                throw new InvalidPlannedChoiceBranchException("Compact choice cardinality differs from the plan.");
            _metadata.Read(lane);
            int[] options = lane.Cards(lane.ChoicePile);
            int[] selected = choice.Cards.Select(token => options.Where(id => CardChoiceSupport.MatchesToken(_metadata[id], token))
                .Skip(token.OptionOccurrence).Select(id => (int?)id).FirstOrDefault()
                ?? throw new InvalidPlannedChoiceBranchException($"Compact choice cannot find {token.CardId}+{token.UpgradeLevel}#{token.OptionOccurrence}.")).ToArray();
            lane.SupplyChoice(selected);
        }
        cursor.AssertConsumed();
        lane.CheckWinCondition();
        return true;
    }

    private TurnStartChoiceRequest DescribeChoice(ResumableDiscardProgram lane, PlanChoiceTiming timing)
    {
        if (!lane.NeedsChoice) throw new InvalidOperationException("Compact plan has no suspended selector.");
        bool retrieve = lane.ChoiceRetrieves;
        string source = retrieve || lane.ChoiceCard < 0 ? _adapter.ChoicePowerId(retrieve)
            : lane.ChoiceAutomatic ? _adapter.DefinitionModels[lane.DefinitionIndex(lane.ChoiceCard)].Id.Entry : "";
        int count = retrieve || lane.ChoiceCard < 0 ? lane.ChoiceCount : lane.ChoiceRequestedCount;
        return new(source, retrieve || lane.ChoiceReturnsFromDiscard ? PlanChoiceEffect.MoveToHand : lane.ChoiceExhausts ? PlanChoiceEffect.Exhaust : PlanChoiceEffect.Discard,
            lane.ChoicePile switch
            {
                ResumableDiscardProgram.Pile.Hand => PileType.Hand,
                ResumableDiscardProgram.Pile.Draw => PileType.Draw,
                ResumableDiscardProgram.Pile.Discard => PileType.Discard,
                _ => throw new InvalidOperationException("Unsupported choice pile.")
            }, count, Timing: timing);
    }

    internal TurnStartChoiceRequest CapturePendingChoice(ResumableDiscardProgram lane, PlanChoiceTiming timing)
    {
        var request = DescribeChoice(lane, timing);
        _metadata.Read(lane);
        // Choice policy can retain a spec across child replays. Only its current source
        // cards are cloned; these private previews never alias a mutable lane model pool.
        var cards = lane.Cards(lane.ChoicePile).Select(id => _metadata[id].Clone()).ToArray();
        return request with { Spec = new(request.Effect, request.SourcePile, request.Count, request.Count,
            cards, cards, ReplacementValue: 0d) };
    }
}
