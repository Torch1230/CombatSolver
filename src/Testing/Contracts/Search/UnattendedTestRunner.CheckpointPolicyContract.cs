using System.Text.Json;
using System.Text.Json.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertCheckpointPolicyContract()
    {
        foreach (bool enabled in new[] { true, false })
        {
            SolverSettingsData current = new()
            {
                PredictPotionReward = !enabled, UseNoveltyPortfolio = !enabled,
                UseBeamWidthPortfolio = !enabled, UseEarlyTurnExploration = !enabled,
            };
            JsonObject policy = new()
            {
                ["potionPolicy"] = "Smart", ["potionDirectives"] = new JsonArray(),
                ["actTransitionBossHpStrategy"] = "ProgressionFirst",
                ["finalBossHpStrategy"] = "MinimizeHpLoss",
                ["acceptableBattleHpLoss"] = 7, ["stopAtAcceptableBattleHpLoss"] = true,
                ["searchMaxDegreeOfParallelism"] = 2,
                ["profile"] = JsonSerializer.SerializeToNode(SolverSettings.Capture().Profile with
                {
                    BeamWidth = 37, MaxExpandedNodes = 1234, SoftTimeBudgetMilliseconds = 9000,
                }, UnattendedTestFiles.JsonOptions),
                ["predictPotionReward"] = enabled, ["useNoveltyPortfolio"] = enabled,
                ["useBeamWidthPortfolio"] = enabled, ["useEarlyTurnExploration"] = enabled,
            };
            string before = policy.ToJsonString();
            SolverSettingsData restored = RestoreCheckpointPolicy(current, policy);
            if (restored.PredictPotionReward != enabled || restored.UseNoveltyPortfolio != enabled
                || restored.UseBeamWidthPortfolio != enabled || restored.UseEarlyTurnExploration != enabled
                || restored.SearchBeamWidth != 37 || restored.SearchMaxExpandedNodes != 1234
                || restored.SearchTimeLimitSeconds != 9 || restored.SearchMaxDegreeOfParallelism != 2
                || restored.AcceptableBattleHpLoss != 7 || restored.FinalBossHpStrategy != BossHpStrategy.MinimizeHpLoss
                || policy.ToJsonString() != before || current.PredictPotionReward != !enabled)
                throw new InvalidOperationException("Checkpoint policy did not replace local switches and budgets without mutating its input.");

            policy.Remove("useNoveltyPortfolio");
            try
            {
                _ = RestoreCheckpointPolicy(current, policy);
                throw new InvalidOperationException("Missing checkpoint switch silently inherited a local setting.");
            }
            catch (InvalidDataException error) when (error.Message == "missing_policy:useNoveltyPortfolio") { }
        }
        _completedChecks.Add("CheckpointPolicy:OppositeLocalSwitches:TrueAndFalse:ExactBudgets:ImmutableInput:MissingRejected");
    }
}
