using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    // An exact optional-potion layer can affect the Smart selection only if it
    // meets the existing HP-saving threshold after a complete potion-free victory.
    // Component certification supplies the recovery proof. It does not certify
    // inventory event callbacks; unknown recovery retains the counterfactual search.
    private readonly int? _smartPotionEligibilityHpCeiling =
        (directSearchPurpose == DirectSearchPurpose.SmartPotionGradient
            || CanUseComponentSmartPotionEligibility(root, policy)
                && attributionPurpose == ContinuationPurpose.SmartOpeningPotionPosterior)
        && policy.PotionPolicy == SolverPotionPolicy.Smart
        && !policy.PotionStrategy.HasForcedDirectives
        && policy.TheftPolicy != SolverTheftPolicy.PreserveResources
        && root.SearchablePotions.Count > 0
        && (CanUseComponentSmartPotionEligibility(root, policy)
            || (root.PlayerIdentity.Character.GetType() == typeof(MegaCrit.Sts2.Core.Models.Characters.Defect)
                && root.Enemies.Count > 0
                && root.Enemies.All(enemy => enemy.Monster?.GetType() == typeof(MegaCrit.Sts2.Core.Models.Monsters.InfestedPrism))
                && CanUseStrictHpRelicBound(root.CanCertifyRemainingHealing
                    && root.InitialRemainingHealingUpperBound == 0,
                    policy.EffectiveHasGrowthTargets, policy.RelicTargets)
                && root.SearchablePotions.Count > 0
                && root.SearchablePotions.All(potion => potion.PotionId
                    == MegaCrit.Sts2.Core.Models.ModelDb.Potion<MegaCrit.Sts2.Core.Models.Potions.FoulPotion>().Id.Entry)))
        && minimumPotionUses is > 0 && maximumPotionUses == minimumPotionUses
            // Initial bottle prices are not a lower bound on future acquisitions.
            // Until a use is recorded, its unavoidable strategic cost is zero.
            ? SmartPotionEligibilityHpCeilingForCost(potionFreePolicyBaseline,
                minimumPotionUses.Value, minimumCost: 0,
                root.PotionRewardOutlook.ReplacementHpCredit,
                ActEndingBossPolicy.ResolveStrategicHpRelief(root.BossHpRelief,
                    policy.ActTransitionBossHpStrategy, policy.FinalBossHpStrategy))
            : null;

    internal static bool CanUseComponentSmartPotionEligibility(CombatRootSnapshot root, SearchPolicySnapshot policy)
        => root.UsesComponentHealingCertificate
            && root.SearchablePotions.Count > 0 && policy.PotionPolicy == SolverPotionPolicy.Smart
            && !policy.PotionStrategy.HasForcedDirectives
            && !policy.EffectiveHasGrowthTargets
            && (policy.RelicTargets.Count == 0 || CanUseStrictHpRelicBound(root, policy))
            && policy.TheftPolicy != SolverTheftPolicy.PreserveResources
            && (root.InitialRemainingHealingUpperBound == 0
                || StrategicHpRecoveryBound.ComponentHealingUpperBound(root.ForkSimulator(),
                    root.PlayerIdentity, postCombatHeal: 0,
                    potionStrategy: policy.PotionStrategy, effectivePotionPolicy: policy.PotionPolicy) == 0);

    private int _smartPotionEligibilityBranchesPruned;
    private int _paidCostEligibilityBranchesPruned;
    internal bool ComponentSmartBoundEnabledForTesting => _smartPotionEligibilityHpCeiling is not null;
    internal int ComponentSmartBoundPrunedForTesting => _smartPotionEligibilityBranchesPruned;

    internal static int? SmartPotionEligibilityHpCeiling(
        PotionFreePolicyBaseline? baseline, int exactPotionUses,
        int minimumPotionStrategicCost, int replacementHpCredit, BossHpRelief bossHpRelief)
    {
        if (baseline is not { Won: true, DeathSaveUseCount: 0 } audited
            || exactPotionUses <= 0 || minimumPotionStrategicCost < 0)
            return null;
        int minimumCost = checked(exactPotionUses * minimumPotionStrategicCost);
        return SmartPotionEligibilityHpCeilingForCost(baseline, exactPotionUses, minimumCost,
            replacementHpCredit, bossHpRelief);
    }

    internal static int? SmartPotionEligibilityHpCeilingForCost(
        PotionFreePolicyBaseline? baseline, int exactPotionUses, int minimumCost,
        int replacementHpCredit, BossHpRelief bossHpRelief)
    {
        if (baseline is not { Won: true, DeathSaveUseCount: 0 } audited
            || exactPotionUses <= 0 || minimumCost < 0)
            return null;
        int requiredHpSaved = PotionUsePolicy.SmartRequiredHpSaved(
            PotionUsePolicy.ApplyReplacementCredit(minimumCost, exactPotionUses, replacementHpCredit),
            bossHpRelief);
        return audited.HpDeficit - requiredHpSaved;
    }

    private List<SearchNode> ApplySmartPotionEligibilityBound(List<SearchNode> retained)
    {
        if (_smartPotionEligibilityHpCeiling is not { } ceiling)
            return retained;
        List<SearchNode>? bounded = null;
        for (int index = 0; index < retained.Count; index++)
        {
            SearchNode node = retained[index];
            int healing = RemainingHealingPotential(node.Snapshot);
            // Unknown branch sources retain the original exhaustive audit.
            if (root.UsesComponentHealingCertificate && healing == int.MaxValue)
            {
                bounded?.Add(node);
                continue;
            }
            int lowerBound = StrategicHpLowerBound(node.Snapshot, _strategicBossHpRelief, healing);
            bool hpBoundPrunes = lowerBound > ceiling;
            // Only costs already recorded by this branch are unavoidable. Future
            // potions may be free or replenished; frozen slots do not prove otherwise.
            bool paidCostBoundPrunes = !hpBoundPrunes
                && root.UsesComponentHealingCertificate
                && node.Snapshot.ExplicitPotionStrategicCost > 0
                && SmartPotionEligibilityHpCeilingForCost(_potionFreePolicyBaseline, _minimumPotionUses,
                    node.Snapshot.ExplicitPotionStrategicCost,
                    root.PotionRewardOutlook.ReplacementHpCredit, _strategicBossHpRelief) is { } tighter
                && lowerBound > tighter;
            if (hpBoundPrunes || paidCostBoundPrunes)
            {
                if (bounded is null)
                {
                    bounded = new List<SearchNode>(retained.Count);
                    if (index > 0)
                        bounded.AddRange(retained.GetRange(0, index));
                }
                _smartPotionEligibilityBranchesPruned++;
                if (paidCostBoundPrunes)
                    _paidCostEligibilityBranchesPruned++;
                continue;
            }
            bounded?.Add(node);
        }
        return bounded ?? retained;
    }

    private void EmitSmartPotionEligibilityBoundDiagnostics()
    {
        EmitRetainedPrimaryBoundDiagnostics();
        if (_smartPotionEligibilityHpCeiling is { } ceiling)
            policy.Diagnostics.Info($"[CombatSolver/Test] SMART_POTION_ELIGIBILITY_BOUND "
                + $"ceiling={ceiling} pruned={_smartPotionEligibilityBranchesPruned} "
                + $"paid_cost_extra={_paidCostEligibilityBranchesPruned}");
    }
}
