using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal static class InCombatRustKernelPolicyMapper
{
    public static IReadOnlyList<(int Slot, string PotionId)> CaptureEffectivePotions(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var potions = new List<(int Slot, string PotionId)>();
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            PotionModel? potion = player.GetPotionAtSlotIndex(slot);
            if (potion is not null)
                potions.Add((slot, potion.Id.Entry));
        }
        return potions;
    }

    public static bool TryBuildPolicy(
        SolverSettingsData settingsData,
        SolverSettingsSnapshot settingsSnapshot,
        SearchPolicySnapshot searchPolicy,
        IReadOnlyList<(int Slot, string PotionId)> effectivePotions,
        out RustKernelPolicyDto? policy,
        out int searchBudgetMilliseconds,
        out string? unsupportedReason)
    {
        ArgumentNullException.ThrowIfNull(settingsData);
        ArgumentNullException.ThrowIfNull(settingsSnapshot);
        ArgumentNullException.ThrowIfNull(searchPolicy);
        ArgumentNullException.ThrowIfNull(effectivePotions);

        RustKernelEligibilityResult policyEligibility = InCombatRustKernelEligibility.AssessSearchPolicy(searchPolicy);
        if (!policyEligibility.IsEligible)
        {
            policy = null;
            searchBudgetMilliseconds = 0;
            unsupportedReason = policyEligibility.Detail ?? "Search policy is unsupported by Rust combat kernel.";
            return false;
        }

        int rawBudgetMs = searchPolicy.BudgetOverrideMilliseconds
            ?? searchPolicy.Profile.SoftTimeBudgetMilliseconds;
        searchBudgetMilliseconds = Math.Clamp(rawBudgetMs, 500, RustKernelAbi.MaxSoftTimeBudgetMilliseconds);

        var relicCounterRules = new List<RustKernelRelicCounterRuleDto>(settingsSnapshot.RelicCounterRules.Length);
        foreach (RelicCounterRule rule in settingsSnapshot.RelicCounterRules)
        {
            RelicCounterCatalog.Entry? catalogEntry = RelicCounterCatalog.All.FirstOrDefault(e => e.Id == rule.Id);
            if (catalogEntry is null
                || rule.Minimum < 0
                || rule.Maximum < rule.Minimum
                || rule.Maximum >= catalogEntry.Period
                || rule.HpAllowance is < 0 or > 1000
                || rule.Priority is < 1 or > 3)
            {
                policy = null;
                unsupportedReason = $"Relic counter rule '{rule.Id}' is out of range for Rust combat kernel.";
                return false;
            }
            relicCounterRules.Add(new RustKernelRelicCounterRuleDto(
                rule.Id.ToString(),
                rule.Enabled,
                rule.Minimum,
                rule.Maximum,
                rule.HpAllowance,
                rule.Priority));
        }

        GrowthValues gb = searchPolicy.GrowthBudgets;
        var growthDto = new RustKernelGrowthBudgetsDto(
            gb.HandOfGreed,
            gb.TheHunt,
            gb.Feed,
            gb.Royalties,
            gb.Alchemize,
            gb.GeneticAlgorithm,
            gb.TheScythe,
            gb.Goopy,
            gb.ForbiddenGrimoire,
            gb.MadScience,
            gb.ShowOff,
            gb.Extras.ToDictionary());

        var directives = new List<RustKernelPotionDirectiveDto>();
        foreach (PotionSlotDirective directive in searchPolicy.PotionStrategy.Directives)
        {
            if (effectivePotions.Any(p =>
                    p.Slot == directive.Slot
                    && string.Equals(p.PotionId, directive.PotionId, StringComparison.Ordinal)))
            {
                directives.Add(new RustKernelPotionDirectiveDto(
                    directive.Slot,
                    directive.PotionId,
                    directive.Directive.ToString()));
            }
        }

        Dictionary<string, int> thresholdOverrides = settingsSnapshot.PotionThresholds.ToDictionary()
            ?? new Dictionary<string, int>(StringComparer.Ordinal);

        SolverSearchProfile profile = searchPolicy.Profile;
        int? potionFreeBeamWidth = profile == settingsSnapshot.Profile ? settingsData.SearchPotionFreeBeamWidth : null;
        int? potionBeamWidth = profile == settingsSnapshot.Profile ? settingsData.SearchPotionBeamWidth : null;

        bool useBeamWidthPortfolio = searchPolicy.AdaptiveEscalationEnabled && !searchPolicy.FixedBudget;

        policy = new RustKernelPolicyDto(
            PotionPolicy: searchPolicy.PotionPolicy.ToString(),
            PotionDirectives: directives,
            PotionHpThresholdOverrides: thresholdOverrides,
            PredictPotionReward: searchPolicy.PredictPotionReward,
            RelicStrategyEnabled: settingsSnapshot.RelicStrategyEnabled,
            RelicCounterRules: relicCounterRules,
            GrowthBudgets: growthDto,
            BrightestFlameMaxHpLossLimit: searchPolicy.BrightestFlameMaxHpLossLimit,
            IgnoreLongTermRewards: searchPolicy.IgnoreLongTermRewards,
            ActTransitionBossHpStrategy: searchPolicy.ActTransitionBossHpStrategy.ToString(),
            FinalBossHpStrategy: searchPolicy.FinalBossHpStrategy.ToString(),
            AcceptableBattleHpLoss: searchPolicy.AcceptableBattleHpLoss,
            StopAtAcceptableBattleHpLoss: searchPolicy.StopAtAcceptableBattleHpLoss,
            UseBeamWidthPortfolio: useBeamWidthPortfolio,
            UseNoveltyPortfolio: false,
            UseEarlyTurnExploration: false,
            BeamWidth: profile.BeamWidth,
            PotionFreeBeamWidth: potionFreeBeamWidth,
            PotionBeamWidth: potionBeamWidth,
            MaxExpandedNodes: profile.MaxExpandedNodes,
            MaxCardBranchesPerNode: profile.MaxCardBranchesPerNode,
            MaxPileChoiceBranchesPerAction: profile.MaxPileChoiceBranchesPerAction,
            MaxHandChoiceBranchesPerAction: profile.MaxHandChoiceBranchesPerAction,
            SoftTimeBudgetMilliseconds: searchBudgetMilliseconds,
            TheftPolicy: searchPolicy.TheftPolicy?.ToString());

        RustKernelWireValidator.ValidatePolicy(policy);
        unsupportedReason = null;
        return true;
    }
}
