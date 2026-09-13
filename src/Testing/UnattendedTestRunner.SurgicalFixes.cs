using System.Reflection;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static SimulationSnapshot ReleaseSurgicalSnapshot(SimulationSnapshot snapshot)
    { snapshot.ReleaseSimulator(); return snapshot; }

    private sealed class SurgicalEvaluationDriver
    {
        private readonly Func<CombatPredictionSimulator, int, int, int, SearchBoundaryReason,
            IReadOnlySet<uint>, SimulationSnapshot> _snapshot;
        private readonly ForkableSet<uint> _deaths = new();
        private readonly int _turn;
        internal SurgicalEvaluationDriver(CombatRootSnapshot root, SolverDisplayNames display,
            BattleDamageSnapshot damage, SearchPolicySnapshot policy)
        {
            var solver = new CombatBeamSolver(root, display, damage, policy);
            _snapshot = typeof(CombatBeamSolver).GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<CombatPredictionSimulator, int, int, int, SearchBoundaryReason,
                    IReadOnlySet<uint>, SimulationSnapshot>>(solver);
            _turn = root.StartTurnNumber;
        }
        internal SimulationSnapshot Evaluate(CombatPredictionSimulator simulator)
            => _snapshot(simulator, _turn, 1, 0, SearchBoundaryReason.None, _deaths);
    }

    private static string[] SurgicalPowerValues(IEnumerable<PowerModel> powers)
        => powers.GroupBy(power => power.Owner).SelectMany(group => group.Select((power, index) =>
            $"{index}:{power.Owner.CombatId}:{power.Id.Entry}:{power.Amount}:{power.Applier?.CombatId}:{power.Target?.CombatId}:"
            + $"{power.AmountOnTurnStart}:{power.SkipNextDurationTick}:"
            + string.Join(',', power.DynamicVars.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value.BaseValue}"))))
            .Order(StringComparer.Ordinal).ToArray();
}
