using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Api;

internal static class PreCombatSts2SimPolicyMapper
{
    private static readonly Dictionary<RelicCounterId, string> BridgeRelicCounterNames = new()
    {
        [RelicCounterId.HappyFlower] = nameof(RelicCounterId.HappyFlower),
        [RelicCounterId.FakeHappyFlower] = nameof(RelicCounterId.FakeHappyFlower),
        [RelicCounterId.Pendulum] = nameof(RelicCounterId.Pendulum),
        [RelicCounterId.PollinousCore] = nameof(RelicCounterId.PollinousCore),
        [RelicCounterId.PenNib] = nameof(RelicCounterId.PenNib),
        [RelicCounterId.Nunchaku] = nameof(RelicCounterId.Nunchaku),
        [RelicCounterId.TuningFork] = nameof(RelicCounterId.TuningFork),
        [RelicCounterId.JossPaper] = nameof(RelicCounterId.JossPaper),
        [RelicCounterId.IronClub] = nameof(RelicCounterId.IronClub),
        [RelicCounterId.GalacticDust] = nameof(RelicCounterId.GalacticDust),
        [RelicCounterId.MeatOnTheBone] = nameof(RelicCounterId.MeatOnTheBone),
    };

    public static bool TryBuildPolicy(
        SolverSettingsData settingsData,
        SolverSettingsSnapshot settingsSnapshot,
        int searchBudgetMilliseconds,
        IReadOnlyList<(int Slot, string PotionId)> effectivePotions,
        out Sts2SimPolicyDto? policy,
        out string? unsupportedReason) =>
        TryBuildPolicy(
            settingsData,
            settingsSnapshot,
            searchBudgetMilliseconds,
            settingsSnapshot.SearchMaxDegreeOfParallelism,
            effectivePotions,
            PreCombatSts2SimBridge.GetActiveCapabilities(),
            out policy,
            out unsupportedReason);

    public static bool TryBuildPolicy(
        SolverSettingsData settingsData,
        SolverSettingsSnapshot settingsSnapshot,
        int searchBudgetMilliseconds,
        int maxDegreeOfParallelism,
        IReadOnlyList<(int Slot, string PotionId)> effectivePotions,
        Sts2SimBridgeCapabilitiesDto capabilities,
        out Sts2SimPolicyDto? policy,
        out string? unsupportedReason)
    {
        ArgumentNullException.ThrowIfNull(settingsData);
        ArgumentNullException.ThrowIfNull(settingsSnapshot);
        ArgumentNullException.ThrowIfNull(effectivePotions);
        ArgumentNullException.ThrowIfNull(capabilities);

        int effectiveSoftTimeMs = Math.Clamp(searchBudgetMilliseconds, 1_000, 30_000);
        SolverSearchProfile profile = settingsSnapshot.Profile;
        var portfolioInput = new Sts2SimPortfolioPolicyInput(
            UseBeamWidthPortfolio: settingsSnapshot.UseBeamWidthPortfolio,
            UseNoveltyPortfolio: settingsSnapshot.UseNoveltyPortfolio,
            EarlyTurnExplorationDepth: settingsSnapshot.UseEarlyTurnExploration ? 2 : 0,
            EarlyTurnExplorationBudgetMilliseconds: settingsSnapshot.UseEarlyTurnExploration ? effectiveSoftTimeMs : 0,
            BeamWidthPortfolioWidths: null,
            BeamWidthPortfolioPlainBaselineMember: true,
            ReallocatedRefinementPortfolio: profile.ReallocatedRefinementPortfolio,
            OffensiveRefinementPortfolio: profile.OffensiveRefinementPortfolio,
            BoundedOffensiveRefinementPortfolio: profile.BoundedOffensiveRefinementPortfolio,
            StopPortfolioAtHpTarget: profile.StopPortfolioAtHpTarget,
            AdaptiveNoveltyRefinement: profile.AdaptiveNoveltyRefinement,
            NoveltySearch: null,
            NoveltyBudget: settingsSnapshot.UseNoveltyPortfolio || profile.AdaptiveNoveltyRefinement
                ? new Sts2SimNoveltyPortfolioBudgetDto(
                    NoveltyPortfolioBudget.Default.TimeDivisor,
                    NoveltyPortfolioBudget.Default.MaximumMilliseconds,
                    NoveltyPortfolioBudget.Default.MaximumExpandedNodes)
                : null,
            MaxDegreeOfParallelism: Math.Max(1, maxDegreeOfParallelism),
            HasContextualRanking: profile.ContextualRanking is not null,
            ContinuousThreatRanking: profile.ContinuousThreatRanking,
            BaseScoreTacticalTies: profile.BaseScoreTacticalTies,
            HasBeamWeightPerturbation: profile.BeamWeightPerturbation is not null,
            HasBuiltInOnlyProfileTuning: profile.SecondRankBand
                || profile.BaseScoreOnly
                || profile.BossTempoHpPricing
                || profile.AggressivePowerCommitment);

        if (!Sts2SimPortfolioPolicyBridge.TryResolvePortfolioPolicy(
                in portfolioInput,
                capabilities,
                out Sts2SimPortfolioPolicyFields resolved,
                out unsupportedReason))
        {
            policy = null;
            return false;
        }

        if (!TryMapGrowthBudgets(settingsSnapshot.GrowthBudgets, out Sts2SimGrowthBudgetsDto? growthDto, out unsupportedReason)
            || growthDto is null)
        {
            policy = null;
            return false;
        }

        if (!TryMapRelicCounterRules(settingsSnapshot.RelicCounterRules, out IReadOnlyList<Sts2SimRelicCounterRuleDto>? counterRules, out unsupportedReason)
            || counterRules is null)
        {
            policy = null;
            return false;
        }

        var directives = new List<Sts2SimPotionDirectiveDto>();
        foreach (PersistedPotionDirective directive in settingsData.PotionDirectives)
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

        policy = new Sts2SimPolicyDto(
            settingsSnapshot.PotionPolicy.ToString(),
            directives,
            settingsSnapshot.PredictPotionReward,
            settingsSnapshot.RelicStrategyEnabled,
            counterRules,
            growthDto,
            settingsSnapshot.BrightestFlameMaxHpLossLimit,
            settingsSnapshot.IgnoreLongTermRewards,
            settingsSnapshot.ActTransitionBossHpStrategy.ToString(),
            settingsSnapshot.FinalBossHpStrategy.ToString(),
            settingsSnapshot.AcceptableBattleHpLoss,
            settingsSnapshot.StopAtAcceptableBattleHpLoss,
            settingsSnapshot.UseBeamWidthPortfolio,
            settingsSnapshot.UseNoveltyPortfolio,
            settingsSnapshot.UseEarlyTurnExploration,
            profile.BeamWidth,
            profile.MaxExpandedNodes,
            profile.MaxCardBranchesPerNode,
            profile.MaxPileChoiceBranchesPerAction,
            profile.MaxHandChoiceBranchesPerAction,
            effectiveSoftTimeMs,
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
        PreCombatSts2SimWireValidator.ValidatePolicy(policy, capabilities);
        unsupportedReason = null;
        return true;
    }

    internal static bool TryMapGrowthBudgets(
        GrowthValues growth,
        out Sts2SimGrowthBudgetsDto? mapped,
        out string? unsupportedReason)
    {
        Dictionary<string, int>? thirdPartyGrowth = growth.Extras.ToDictionary();
        if (growth.ShowOff != 0 || (thirdPartyGrowth is not null && thirdPartyGrowth.Values.Any(static value => value != 0)))
        {
            mapped = null;
            unsupportedReason = "ShowOff or third-party GrowthBudgets are nonzero and not mapped to Sts2Sim bridge.";
            return false;
        }

        mapped = new Sts2SimGrowthBudgetsDto(
            growth.HandOfGreed,
            growth.TheHunt,
            growth.Feed,
            growth.Royalties,
            growth.Alchemize,
            growth.GeneticAlgorithm,
            growth.TheScythe,
            growth.Goopy,
            growth.ForbiddenGrimoire,
            growth.MadScience,
            growth.ShowOff,
            thirdPartyGrowth);
        unsupportedReason = null;
        return true;
    }

    internal static bool TryMapRelicCounterRules(
        IReadOnlyList<RelicCounterRule> rules,
        out IReadOnlyList<Sts2SimRelicCounterRuleDto>? mapped,
        out string? unsupportedReason)
    {
        var counterRules = new List<Sts2SimRelicCounterRuleDto>(rules.Count);
        foreach (RelicCounterRule rule in rules)
        {
            if (!BridgeRelicCounterNames.TryGetValue(rule.Id, out string? name))
            {
                mapped = null;
                unsupportedReason = $"Relic counter rule '{rule.Id}' is not supported by Sts2Sim bridge.";
                return false;
            }
            counterRules.Add(new Sts2SimRelicCounterRuleDto(
                name,
                rule.Enabled,
                rule.Minimum,
                rule.Maximum,
                rule.HpAllowance,
                rule.Priority));
        }

        mapped = counterRules;
        unsupportedReason = null;
        return true;
    }

    public static IReadOnlyList<(int Slot, string PotionId)> CaptureEffectivePotions(Player player)
    {
        var list = new List<(int, string)>();
        for (int slot = 0; slot < player.PotionSlots.Count; slot++)
        {
            if (player.PotionSlots[slot] is { } potion)
                list.Add((slot, potion.Id.Entry));
        }
        return list;
    }

    public static IReadOnlyList<(int Slot, string PotionId)> CaptureEffectivePotions(
        MegaCrit.Sts2.Core.Saves.Runs.SerializablePlayer player)
    {
        var list = new List<(int, string)>();
        foreach (MegaCrit.Sts2.Core.Saves.Runs.SerializablePotion potion in player.Potions)
        {
            if (potion.Id is { } id)
                list.Add((potion.SlotIndex, id.Entry));
        }
        return list;
    }

    public static PreCombatForecastResult LocalizeResultOnDelivery(PreCombatForecastResult result)
    {
        if (result.Status != PreCombatForecastStatus.Succeeded || result.PotionUses.Count == 0)
            return result;

        var localized = new PreCombatPotionUse[result.PotionUses.Count];
        bool changed = false;
        for (int i = 0; i < result.PotionUses.Count; i++)
        {
            PreCombatPotionUse use = result.PotionUses[i];
            string title = ResolveLocalizedPotionTitle(use.Id);
            if (!string.Equals(title, use.Title, StringComparison.Ordinal))
                changed = true;
            localized[i] = use with { Title = title };
        }
        return changed ? result with { PotionUses = localized } : result;
    }

    public static string ResolveLocalizedPotionTitle(string potionEntryId)
    {
        if (string.IsNullOrWhiteSpace(potionEntryId))
            return string.Empty;
        PotionModel? model = ModelDb.GetByIdOrNull<PotionModel>(new ModelId("POTION", potionEntryId));
        return model?.Title.GetFormattedText() ?? potionEntryId;
    }
}
