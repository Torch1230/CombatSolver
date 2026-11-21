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
        out string? unsupportedReason)
    {
        if (settingsSnapshot.UseNoveltyPortfolio)
        {
            policy = null;
            unsupportedReason = "UseNoveltyPortfolio is active and not mapped to Sts2Sim bridge.";
            return false;
        }
        if (settingsSnapshot.UseEarlyTurnExploration)
        {
            policy = null;
            unsupportedReason = "UseEarlyTurnExploration is active and not mapped to Sts2Sim bridge.";
            return false;
        }

        GrowthValues growth = settingsSnapshot.GrowthBudgets;
        Dictionary<string, int>? thirdPartyGrowth = growth.Extras.ToDictionary();
        if (growth.ShowOff != 0 || (thirdPartyGrowth is not null && thirdPartyGrowth.Values.Any(static value => value != 0)))
        {
            policy = null;
            unsupportedReason = "ShowOff or third-party GrowthBudgets are nonzero and not mapped to Sts2Sim bridge.";
            return false;
        }

        var counterRules = new List<Sts2SimRelicCounterRuleDto>(settingsSnapshot.RelicCounterRules.Length);
        foreach (RelicCounterRule rule in settingsSnapshot.RelicCounterRules)
        {
            if (!BridgeRelicCounterNames.TryGetValue(rule.Id, out string? name))
            {
                policy = null;
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

        SolverSearchProfile profile = settingsSnapshot.Profile;
        int effectiveSoftTimeMs = Math.Clamp(searchBudgetMilliseconds, 1_000, 30_000);
        policy = new Sts2SimPolicyDto(
            settingsSnapshot.PotionPolicy.ToString(),
            directives,
            settingsSnapshot.PredictPotionReward,
            settingsSnapshot.RelicStrategyEnabled,
            counterRules,
            new Sts2SimGrowthBudgetsDto(
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
                thirdPartyGrowth),
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
            effectiveSoftTimeMs);
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
