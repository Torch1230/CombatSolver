using CombatSolver.Api;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal static class InCombatSts2SimPolicyMapper
{
    public static bool TryBuildPolicy(
        SolverSettingsData settingsData,
        SolverSettingsSnapshot settingsSnapshot,
        SearchPolicySnapshot searchPolicy,
        Player player,
        out InCombatSts2SimPolicyDto? policy,
        out int searchBudgetMilliseconds,
        out string? unsupportedReason) =>
        TryBuildPolicy(
            settingsData,
            settingsSnapshot,
            searchPolicy,
            player,
            PreCombatSts2SimBridge.GetActiveCapabilities(),
            out policy,
            out searchBudgetMilliseconds,
            out unsupportedReason);

    public static bool TryBuildPolicy(
        SolverSettingsData settingsData,
        SolverSettingsSnapshot settingsSnapshot,
        SearchPolicySnapshot searchPolicy,
        Player player,
        Sts2SimBridgeCapabilitiesDto capabilities,
        out InCombatSts2SimPolicyDto? policy,
        out int searchBudgetMilliseconds,
        out string? unsupportedReason)
    {
        ArgumentNullException.ThrowIfNull(player);
        return TryBuildPolicy(
            settingsData,
            settingsSnapshot,
            searchPolicy,
            PreCombatSts2SimPolicyMapper.CaptureEffectivePotions(player),
            capabilities,
            out policy,
            out searchBudgetMilliseconds,
            out unsupportedReason);
    }

    public static bool TryBuildPolicy(
        SolverSettingsData settingsData,
        SolverSettingsSnapshot settingsSnapshot,
        SearchPolicySnapshot searchPolicy,
        IReadOnlyList<(int Slot, string PotionId)> effectivePotions,
        Sts2SimBridgeCapabilitiesDto capabilities,
        out InCombatSts2SimPolicyDto? policy,
        out int searchBudgetMilliseconds,
        out string? unsupportedReason)
    {
        ArgumentNullException.ThrowIfNull(settingsData);
        ArgumentNullException.ThrowIfNull(settingsSnapshot);
        ArgumentNullException.ThrowIfNull(searchPolicy);
        ArgumentNullException.ThrowIfNull(effectivePotions);
        ArgumentNullException.ThrowIfNull(capabilities);

        int rawBudgetMs = searchPolicy.BudgetOverrideMilliseconds
            ?? searchPolicy.Profile.SoftTimeBudgetMilliseconds;
        searchBudgetMilliseconds = Math.Clamp(rawBudgetMs, 500, InCombatSts2SimAbi.MaxSoftTimeBudgetMilliseconds);

        Sts2SimPortfolioPolicyInput portfolioInput = InCombatSts2SimEligibility.BuildPortfolioPolicyInput(searchPolicy);
        if (!Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(
                in portfolioInput,
                capabilities,
                out Sts2SimPortfolioPolicyFields resolved,
                out unsupportedReason))
        {
            policy = null;
            return false;
        }

        if (!PreCombatSts2SimPolicyMapper.TryMapGrowthBudgets(
                searchPolicy.GrowthBudgets,
                out Sts2SimGrowthBudgetsDto? growthDto,
                out unsupportedReason)
            || growthDto is null)
        {
            policy = null;
            return false;
        }

        if (!PreCombatSts2SimPolicyMapper.TryMapRelicCounterRules(
                settingsSnapshot.RelicCounterRules,
                out IReadOnlyList<Sts2SimRelicCounterRuleDto>? counterRules,
                out unsupportedReason)
            || counterRules is null)
        {
            policy = null;
            return false;
        }

        var directives = new List<Sts2SimPotionDirectiveDto>();
        foreach (PotionSlotDirective directive in searchPolicy.PotionStrategy.Directives)
        {
            if (effectivePotions.Any(p =>
                    p.Slot == directive.Slot
                    && string.Equals(p.PotionId, directive.PotionId, StringComparison.Ordinal)))
            {
                directives.Add(new Sts2SimPotionDirectiveDto(
                    directive.Slot,
                    directive.PotionId,
                    directive.Directive.ToString()));
            }
        }

        SolverSearchProfile profile = searchPolicy.Profile;
        int? potionFreeBeamWidth = settingsSnapshot.PotionFreeBeamWidth
            ?? (profile == settingsSnapshot.Profile ? settingsData.SearchPotionFreeBeamWidth : null);
        int? potionBeamWidth = settingsSnapshot.PotionBeamWidth
            ?? (profile == settingsSnapshot.Profile ? settingsData.SearchPotionBeamWidth : null);

        policy = new InCombatSts2SimPolicyDto(
            searchPolicy.PotionPolicy.ToString(),
            directives,
            searchPolicy.PredictPotionReward,
            settingsSnapshot.RelicStrategyEnabled,
            counterRules,
            growthDto,
            searchPolicy.BrightestFlameMaxHpLossLimit,
            searchPolicy.IgnoreLongTermRewards,
            searchPolicy.ActTransitionBossHpStrategy.ToString(),
            searchPolicy.FinalBossHpStrategy.ToString(),
            searchPolicy.AcceptableBattleHpLoss,
            searchPolicy.StopAtAcceptableBattleHpLoss,
            searchPolicy.UseBeamWidthPortfolio,
            searchPolicy.UseNoveltyPortfolio,
            searchPolicy.EarlyTurnExplorationDepth > 0,
            profile.BeamWidth,
            potionFreeBeamWidth,
            potionBeamWidth,
            profile.MaxExpandedNodes,
            profile.MaxCardBranchesPerNode,
            profile.MaxPileChoiceBranchesPerAction,
            profile.MaxHandChoiceBranchesPerAction,
            searchBudgetMilliseconds,
            searchPolicy.TheftPolicy?.ToString(),
            resolved.PortfolioOptionsVersion,
            resolved.BeamWidthPortfolioWidths,
            resolved.BeamWidthPortfolioPlainBaselineMember,
            resolved.ReallocatedRefinementPortfolio,
            resolved.OffensiveRefinementPortfolio,
            resolved.BoundedOffensiveRefinementPortfolio,
            resolved.StopPortfolioAtHpTarget,
            resolved.AdaptiveNoveltyRefinement,
            resolved.NoveltySearch,
            resolved.NoveltyBudget,
            resolved.EarlyTurnExplorationDepth,
            resolved.EarlyTurnExplorationBudgetMilliseconds,
            resolved.PortfolioSchedulingMode);
        InCombatSts2SimWireValidator.ValidatePolicy(policy, capabilities);
        unsupportedReason = null;
        return true;
    }
}
