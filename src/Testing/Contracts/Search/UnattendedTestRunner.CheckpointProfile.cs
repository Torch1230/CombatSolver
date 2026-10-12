using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Combat;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertCheckpointProfileContract(CombatState combat)
    {
        AssertCheckpointPortfolioSettingsContract(combat);
        if (_protocolHost.BeamWeightPerturbationOverride != null)
            throw new InvalidOperationException("Profile contract requires no separate CLI weight override.");
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SolverSearchProfile? previous = _protocolHost.CheckpointProfileOverride;
        SolverSearchProfile expected = settings.Profile with
        {
            BeamWidth = 7,
            MaxExpandedNodes = 1234,
            SoftTimeBudgetMilliseconds = 987,
            SecondRankBand = true,
            ContinuousThreatRanking = true,
            BaseScoreTacticalTies = true,
            AdaptiveNoveltyRefinement = true,
            BeamWeightPerturbation = new(BeamWeightTerm.CurrentEnergy, 0),
            OffensiveRefinementPortfolio = true,
            BoundedOffensiveRefinementPortfolio = true,
            ReallocatedRefinementPortfolio = false,
            StopPortfolioAtHpTarget = false,
            BaseScoreOnly = true,
            AggressivePowerCommitment = true,
        };
        try
        {
            _protocolHost.ApplyRecordedSearchProfile(expected);
            SearchPolicySnapshot captured = SolverController.CaptureSearchPolicy(settings, combat, false, null);
            if (captured.Profile != expected)
                throw new InvalidOperationException("Recorded profile lost fields during policy capture.");
            _protocolHost.ApplyRecordedSearchProfile(null);
            SearchPolicySnapshot ordinary = SolverController.CaptureSearchPolicy(settings, combat, false, null);
            if (ordinary.Profile != settings.Profile)
                throw new InvalidOperationException("Recorded profile leaked into the ordinary policy.");
            _completedChecks.Add("CheckpointProfile:CompleteRecord:FrozenPolicy:ClearRestoresOrdinaryProfile");
        }
        finally
        {
            _protocolHost.ApplyRecordedSearchProfile(previous);
        }
    }

    private void AssertCheckpointPortfolioSettingsContract(CombatState combat)
    {
        SolverSettingsData original = SolverSettings.Current;
        JsonObject? previousImport = _checkpointImport;
        SolverSettingsSnapshot before = SolverSettings.Capture();
        SolverSearchProfile profile = before.Profile;
        try
        {
            foreach (var flags in new[]
            {
                (Novelty: true, Beam: false, Early: false, Reward: false),
                (Novelty: false, Beam: true, Early: true, Reward: true),
            })
            {
                SolverSettingsData current = original with
                {
                    UseNoveltyPortfolio = !flags.Novelty,
                    UseBeamWidthPortfolio = !flags.Beam,
                    UseEarlyTurnExploration = !flags.Early,
                    PredictPotionReward = !flags.Reward,
                };
                JsonObject recorded = JsonSerializer.SerializeToNode(current, UnattendedTestFiles.JsonOptions)!.AsObject();
                recorded["potionPolicy"] = JsonSerializer.SerializeToNode(current.PotionPolicy, UnattendedTestFiles.JsonOptions);
                recorded["profile"] = JsonSerializer.SerializeToNode(profile, UnattendedTestFiles.JsonOptions);
                recorded["searchMaxDegreeOfParallelism"] = before.SearchMaxDegreeOfParallelism;
                recorded["useNoveltyPortfolio"] = flags.Novelty;
                recorded["useBeamWidthPortfolio"] = flags.Beam;
                recorded["useEarlyTurnExploration"] = flags.Early;
                recorded["predictPotionReward"] = flags.Reward;
                _checkpointImport = new JsonObject { ["resolvedPolicy"] = recorded };
                SolverSettingsData restored = ApplyRecordedCheckpointPolicy(current);
                if (restored.UseNoveltyPortfolio != flags.Novelty
                    || restored.UseBeamWidthPortfolio != flags.Beam
                    || restored.UseEarlyTurnExploration != flags.Early
                    || restored.PredictPotionReward != flags.Reward)
                    throw new InvalidOperationException("Recorded portfolio/reward switches were replaced by current settings.");
                SolverSettings.ApplyForTesting(restored);
                SearchPolicySnapshot frozen = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
                if (frozen.UseNoveltyPortfolio != flags.Novelty
                    || frozen.UseBeamWidthPortfolio != flags.Beam
                    || frozen.EarlyTurnExplorationDepth != (flags.Early ? 2 : 0)
                    || frozen.PredictPotionReward != flags.Reward)
                    throw new InvalidOperationException("Recorded portfolio/reward switches changed during policy capture.");
                foreach (string key in new[] { "useNoveltyPortfolio", "useBeamWidthPortfolio", "useEarlyTurnExploration", "predictPotionReward" })
                {
                    JsonObject incomplete = (JsonObject)recorded.DeepClone();
                    incomplete.Remove(key);
                    _checkpointImport = new JsonObject { ["resolvedPolicy"] = incomplete };
                    try
                    {
                        ApplyRecordedCheckpointPolicy(current);
                        throw new InvalidOperationException($"Missing recorded switch {key} accepted a local default.");
                    }
                    catch (InvalidDataException error) when (error.Message == $"missing_policy:{key}")
                    {
                    }
                }
            }
            _completedChecks.Add("CheckpointPolicy:RecordedTrueAndFalseSwitches:FrozenPolicy:MissingSwitchRequiresOverride");
        }
        finally
        {
            _checkpointImport = previousImport;
            SolverSettings.ApplyForTesting(original);
        }
    }
}
