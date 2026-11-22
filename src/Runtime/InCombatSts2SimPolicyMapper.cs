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
        out string? unsupportedReason)
    {
        int rawBudgetMs = searchPolicy.BudgetOverrideMilliseconds
            ?? searchPolicy.Profile.SoftTimeBudgetMilliseconds;
        searchBudgetMilliseconds = Math.Clamp(rawBudgetMs, 500, InCombatSts2SimAbi.MaxSoftTimeBudgetMilliseconds);

        IReadOnlyList<(int Slot, string PotionId)> effectivePotions =
            PreCombatSts2SimPolicyMapper.CaptureEffectivePotions(player);

        if (!PreCombatSts2SimPolicyMapper.TryBuildPolicy(
                settingsData,
                settingsSnapshot,
                searchBudgetMilliseconds,
                effectivePotions,
                out Sts2SimPolicyDto? baseDto,
                out unsupportedReason)
            || baseDto is null)
        {
            policy = null;
            return false;
        }

        SolverSearchProfile profile = searchPolicy.Profile;
        policy = new InCombatSts2SimPolicyDto(
            baseDto.PotionPolicy,
            baseDto.PotionDirectives,
            baseDto.PredictPotionReward,
            baseDto.RelicStrategyEnabled,
            baseDto.RelicCounterRules,
            baseDto.GrowthBudgets,
            baseDto.BrightestFlameMaxHpLossLimit,
            baseDto.IgnoreLongTermRewards,
            baseDto.ActTransitionBossHpStrategy,
            baseDto.FinalBossHpStrategy,
            baseDto.AcceptableBattleHpLoss,
            baseDto.StopAtAcceptableBattleHpLoss,
            searchPolicy.UseBeamWidthPortfolio,
            baseDto.UseNoveltyPortfolio,
            baseDto.UseEarlyTurnExploration,
            profile.BeamWidth,
            null,
            null,
            profile.MaxExpandedNodes,
            profile.MaxCardBranchesPerNode,
            profile.MaxPileChoiceBranchesPerAction,
            profile.MaxHandChoiceBranchesPerAction,
            searchBudgetMilliseconds,
            searchPolicy.TheftPolicy?.ToString());
        unsupportedReason = null;
        return true;
    }
}
