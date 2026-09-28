using System.Diagnostics;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private sealed record MultiplayerSupportInsertion(SearchNode Node, RouteAnnotations Annotations);

    private MultiplayerSupportInsertion? TryAppendPureSupport(
        SearchNode original,
        RouteAnnotations originalAnnotations,
        Stopwatch requestClock)
    {
        if (root.PlayerCount == 1 || _includeTurnSetup
            || requestClock.ElapsedMilliseconds >= _profile.SoftTimeBudgetMilliseconds)
            return null;

        PlanAction[] actions = original.Actions.ToArray();
        int insertAt = Array.FindIndex(actions, action => action.Turn == _startTurnNumber
            && (action.Kind == PlanActionKind.EndTurn || action.EndsPlayerTurn));
        if (insertAt < 0)
            return null;

        SimulationSnapshot prefix = Replay(actions.Take(insertAt).ToArray());
        try
        {
            CombatPredictionSimulator simulator = prefix.Simulator;
            IReadOnlyList<PredictedCard> hand = simulator.State.GetPlayerCombatState(_player).Hand.Cards;
            for (int handIndex = 0; handIndex < hand.Count; handIndex++)
            {
                if (requestClock.ElapsedMilliseconds >= _profile.SoftTimeBudgetMilliseconds)
                    return null;
                PredictedCard card = hand[handIndex];
                if (!simulator.CanPlay(card))
                    continue;
                foreach ((int targetIndex, Creature? target) in TargetsFor(card, simulator))
                {
                    if (!IsPureTeammateSupport(card, target)
                        || !card.Original.CanPlayTargeting(target))
                        continue;
                    string stateKey = CardChoiceSupport.ChoiceCardKey(card);
                    PlanAction support = new(
                        PlanActionKind.PlayCard,
                        _startTurnNumber,
                        card.Preview.Id.Entry,
                        hand.Take(handIndex).Count(prior => prior.Preview.Id == card.Preview.Id),
                        targetIndex,
                        target?.CombatId,
                        displayNames.Card(card.Preview),
                        displayNames.Creature(target,
                            ((SimulatedCombatState)simulator.State.CombatState).KnownEnemies),
                        CardStateKey: stateKey,
                        CardStateOccurrence: hand.Take(handIndex).Count(prior =>
                            CardChoiceSupport.ChoiceCardKey(prior) == stateKey),
                        CardEnchantmentId: card.Preview.Enchantment?.Id.Entry ?? "",
                        CardUpgradeLevel: card.Preview.CurrentUpgradeLevel);
                    PlanAction[] insertedActions = [.. actions.Take(insertAt), support, .. actions.Skip(insertAt)];
                    SearchNode? inserted = ReplayAdjustedRoute(insertedActions,
                        original.GetTurnSetupChoices(), original.GetTurnSetupPlayState(), originalAnnotations);
                    if (inserted == null)
                        continue;
                    bool accepted = !inserted.Snapshot.PlayerDead
                        && inserted.Snapshot.ProjectedPlayerHp >= original.Snapshot.ProjectedPlayerHp
                        && inserted.Snapshot.PlayerHp >= original.Snapshot.PlayerHp
                        && inserted.Snapshot.CumulativePlayerHpLost <= original.Snapshot.CumulativePlayerHpLost
                        && inserted.Snapshot.Energy >= original.Snapshot.Energy
                        && inserted.Snapshot.Stars >= original.Snapshot.Stars
                        && inserted.Snapshot.EnemyHp <= original.Snapshot.EnemyHp
                        && inserted.Snapshot.BoundaryReason == original.Snapshot.BoundaryReason;
                    if (!accepted)
                    {
                        inserted.Snapshot.ReleaseSimulator();
                        continue;
                    }
                    RouteAnnotations annotations = BuildRouteAnnotations(inserted);
                    policy.Diagnostics.Info($"[CombatSolver/Test] MULTIPLAYER_SUPPORT_INSERTED "
                        + $"card={support.CardId} target={support.TargetCombatId} turn={support.Turn}");
                    return new MultiplayerSupportInsertion(inserted, annotations);
                }
            }
            return null;
        }
        finally
        {
            prefix.ReleaseSimulator();
        }
    }
}
