using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed record KnownRoutePrefix(
        PlanAction Action, MoveStateSnapshot State, StateFingerprint StateKey,
        int Turn, int HpLost, int PotionsUsed)
    {
        public int ShufflesCrossed { get; init; }
        public int PotionStrategicCost { get; init; }
        public CombatTerminalStamp? TerminalStamp { get; init; }
        public bool PlayerDead { get; init; }
        public bool AllEnemiesDead { get; init; }
    }

    private static MoveStateSnapshot[] CaptureKnownRouteRootStates(
        CombatRootSnapshot root, Player player, IReadOnlyList<Creature> enemies)
    {
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        var simulator = root.ForkSimulator();
        return enemies.Select(enemy => CaptureSimulated(simulator,
            (SimulatedCombatState)simulator.State.CombatState, player, enemy)).ToArray();
    }

    private static KnownRoutePrefix FreezeKnownRoutePrefix(
        PlanAction action, MoveStateSnapshot state, SimulationSnapshot snapshot)
    {
        PlanCardChoice Choice(PlanCardChoice choice) => choice with
        {
            Cards = Array.AsReadOnly(choice.Cards.Select(token => token with { }).ToArray()),
        };
        IReadOnlyList<PlanCardChoice>? Choices(IReadOnlyList<PlanCardChoice>? choices)
            => choices == null ? null : Array.AsReadOnly(choices.Select(Choice).ToArray());
        PlanAction frozen = action with
        {
            Choice = action.Choice is { } primary ? Choice(primary) : null,
            NestedChoices = Choices(action.NestedChoices),
            TurnStartChoices = Choices(action.TurnStartChoices),
            RelicEffects = action.RelicEffects is { } relics
                ? Array.AsReadOnly(relics.Select(effect => effect with { }).ToArray()) : null,
        };
        return new KnownRoutePrefix(frozen, state, snapshot.StateKey, snapshot.Turn,
            snapshot.CumulativePlayerHpLost, snapshot.PotionUseCount)
        {
            ShufflesCrossed = snapshot.ShufflesCrossed,
            PotionStrategicCost = snapshot.PotionStrategicCost,
            TerminalStamp = snapshot.TerminalStamp,
            PlayerDead = snapshot.PlayerDead,
            AllEnemiesDead = snapshot.AllEnemiesDead,
        };
    }

    private void AssertKnownRouteAliasSnapshot(
        SimulationSnapshot snapshot, KnownRoutePrefix expected, Player player, Creature enemy, string label)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (snapshot.HasRisk || snapshot.PredictionGaps.Any(gap => !gap.Compensated)
            || snapshot.BoundaryReason != SearchBoundaryReason.None || combat.HasPendingChoice
            || combat.PlayerTurnEndRequested || snapshot.StateKey != expected.StateKey
            || snapshot.Turn != expected.Turn || snapshot.CumulativePlayerHpLost != expected.HpLost
            || snapshot.PotionUseCount != expected.PotionsUsed
            || snapshot.PotionStrategicCost != expected.PotionStrategicCost
            || snapshot.ShufflesCrossed != expected.ShufflesCrossed
            || snapshot.PlayerDead != expected.PlayerDead || snapshot.AllEnemiesDead != expected.AllEnemiesDead
            || snapshot.TerminalStamp != expected.TerminalStamp
            || snapshot.Simulator.IsInProgress == (snapshot.PlayerDead || snapshot.AllEnemiesDead))
            throw new InvalidOperationException($"别名 {label} 与冻结前缀的稳定状态/累计指标/终局不一致。");
        combat.AssertForkable();
        AssertSnapshotEqual(CaptureSimulated(snapshot.Simulator, combat, player, enemy), expected.State,
            "KnownRouteAlias", label);
    }
}
