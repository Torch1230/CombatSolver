using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // Unlike an exact potion audit, this continuation only replaces an already
    // retained complete victory. Strictly worse HP cannot improve either final
    // comparator: death saves/theft precede HP, zero growth credit cannot offset
    // it, and potion cost/counters/turns only decide ties. The retained route need
    // not match the fixed prefix or the branch's eventual potion count.
    private readonly PrimarySearchIncumbent? _retainedPrimaryIncumbent =
        minimumPotionUses.GetValueOrDefault() == 0 && maximumPotionUses is null
            && potionPolicyOverride is null
            && !policy.DisableRefinementIncumbentForTesting
            && !policy.DisableSharedPrimaryIncumbentsForTesting
            && CanUseRetainedPrimaryHpBound(root, policy)
                ? retainedPrimaryIncumbent : null;

    private int _retainedPrimaryBoundQueries;
    private int _retainedPrimaryBoundUnknown;
    private int _retainedPrimaryBoundPruned;

    internal static bool CanUseRetainedPrimaryHpBound(CombatRootSnapshot root, SearchPolicySnapshot policy)
        => root.UsesPruningComponentHealingCertificate
            && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && policy.EffectiveGrowthBudgets == default
            && policy.RelicTargets.Count == 0
            && policy.TheftPolicy != SolverTheftPolicy.PreserveResources;

    private bool ShouldPruneByRetainedPrimaryIncumbent(SearchNode node)
    {
        if (_retainedPrimaryIncumbent is not { } incumbent
            || node.IsTerminal || !node.Snapshot.HasSimulator || node.Snapshot.HasRisk
            || node.Snapshot.BoundaryReason != SearchBoundaryReason.None
            || node.Snapshot.OutstandingStolenResource != 0
            || node.Snapshot.StrategicHpCredit != 0)
            return false;
        _retainedPrimaryBoundQueries++;
        int healing = StrategicHpRecoveryBound.ComponentHealingUpperBound(
            (CombatPredictionSimulator)node.Snapshot.Simulator, _player,
            root.PostCombatRelicHeal.UnconditionalHeal + root.PostCombatRelicHeal.WoundedHeal,
            includePotionHealing: true, maximumExplicitPotionUses: _maximumPotionUses,
            potionStrategy: _potionStrategy, effectivePotionPolicy: _potionPolicy,
            useReviewedSources: true);
        if (healing == int.MaxValue)
        {
            _retainedPrimaryBoundUnknown++;
            return false;
        }
        if (StrategicHpLowerBound(node.Snapshot, _strategicBossHpRelief, healing)
            <= incumbent.StrategicHpDeficit)
            return false;
        _retainedPrimaryBoundPruned++;
        return true;
    }

    private void EmitRetainedPrimaryBoundDiagnostics()
    {
        if (_retainedPrimaryIncumbent is not { } incumbent)
            return;
        policy.Diagnostics.Info($"[CombatSolver/Test] RETAINED_PRIMARY_BOUND "
            + $"witness_hp={incumbent.StrategicHpDeficit} queries={_retainedPrimaryBoundQueries} "
            + $"unknown={_retainedPrimaryBoundUnknown} pruned={_retainedPrimaryBoundPruned}");
    }
}
