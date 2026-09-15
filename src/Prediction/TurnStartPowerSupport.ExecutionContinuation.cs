using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal static partial class TurnStartPowerSupport
{
    // Use the same remapping as SimulatedCombatState's effective-power snapshot: root
    // models stay stable identities; branch-owned Power instances are already cloned by State.Fork.
    private enum ForegoneStage { Start, Select, Reset }

    private sealed record BeforeHandDrawPowerFrame(Player Player, IReadOnlyList<PowerModel> Powers,
        int NextIndex, ForegoneStage Stage) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Powers = Powers.Select(context.RemapOrSelf).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            return !ContinueBeforeHandDraw(simulator, combat, Player, combat.ActiveExecutionChoices, Powers, NextIndex, Stage);
        }
    }

    private sealed record AfterPlayerTurnStartPowerFrame(Player Player, IReadOnlyList<PowerModel> Powers,
        int NextIndex) : ICombatPredictionExecutionFrame
    {
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Powers = Powers.Select(context.RemapOrSelf).ToArray() };
        public bool Resume(CombatPredictionSimulator simulator)
        {
            var combat = (SimulatedCombatState)simulator.State.CombatState;
            return !ContinueAfterPlayerTurnStart(simulator, combat, Player, combat.ActiveExecutionChoices, Powers, NextIndex);
        }
    }
}
