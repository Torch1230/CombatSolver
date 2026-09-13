using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static string AssertSnapshotCoverageContract(MegaCrit.Sts2.Core.Combat.CombatState combat)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        CombatPredictionSimulator parent = root.ForkSimulator();
        SimulatedCombatState state = (SimulatedCombatState)parent.State.CombatState;
        AbstractModel[] sources = parent.State.GetPlayerCombatState(combat.Players[0]).AllCards
            .Select(card => (AbstractModel)card.Preview).Concat(state.EffectivePowers())
            .Append(CanonicalModels.Card<Armaments>()).ToArray();
        MirrorMethodSpec onPlay = new(typeof(CardModel), "OnPlay",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(PlayerChoiceContext), typeof(CardPlay)]);
        PredictionRiskReason[] reasons = Enum.GetValues<PredictionRiskReason>();
        int comparisons = 0;
        void Compare(CombatPredictionSimulator simulator)
        {
            IReadOnlyList<PredictionGap> expected = PredictionCoverage.CollectBaselineForTesting(simulator);
            IReadOnlyList<PredictionGap> actual = PredictionCoverage.Collect(simulator);
            if (!expected.SequenceEqual(actual))
                throw new InvalidOperationException("Coverage dedup changed a field or stable tie order.");
            comparisons++;
        }
        Compare(parent);
        parent.History.RecordRisk(reasons[0]); // null trace / UNKNOWN
        for (int index = 0; index < 4096; index++)
        {
            AbstractModel source = sources[index % sources.Length];
            using (parent.PushMethodSource(source, onPlay))
            {
                parent.History.RecordRisk(reasons[(index / sources.Length) % reasons.Length]);
                if (index % 5 == 0)
                    parent.History.RecordRisk(reasons[(index / sources.Length) % reasons.Length]);
                if (index % 7 == 0)
                    parent.History.CardsSelected([]); // non-risk entries interleaved
            }
            if (index % 64 != 0) continue;
            Compare(parent);
            IReadOnlyList<PredictionGap> beforeFork = PredictionCoverage.Collect(parent);
            CombatPredictionSimulator child = parent.Fork();
            using (child.PushActionSource(source, PredictionActionKind.CardPlay))
                child.History.RecordRisk(reasons[(index + 1) % reasons.Length]);
            Compare(child);
            Compare(parent);
            if (!beforeFork.SequenceEqual(PredictionCoverage.Collect(parent)))
                throw new InvalidOperationException("Child risk history changed parent coverage.");
        }
        Compare(parent);
        IReadOnlyList<PredictionGap> final = PredictionCoverage.Collect(parent);
        if (!final.Any(gap => gap.Compensated) || !final.Any(gap => !gap.Compensated))
            throw new InvalidOperationException("Coverage fixture did not exercise both compensation values.");
        for (int index = 0; index < 8; index++)
        {
            _ = PredictionCoverage.CollectBaselineForTesting(parent);
            _ = PredictionCoverage.Collect(parent);
        }
        const int iterations = 100;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
            _ = PredictionCoverage.CollectBaselineForTesting(parent);
        long baselineBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
            _ = PredictionCoverage.Collect(parent);
        long candidateBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        if (candidateBytes >= baselineBytes)
            throw new InvalidOperationException("Duplicate-rich history did not allocate fewer coverage objects.");
        return $"SnapshotCoverage:comparisons={comparisons}:gaps={final.Count}:iterations={iterations}:baseline_bytes={baselineBytes}:candidate_bytes={candidateBytes}:parent_child_isolated=true";
    }
}
