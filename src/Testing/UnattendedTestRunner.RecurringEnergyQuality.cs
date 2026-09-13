using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task ProbeRecurringEnergyQualityAsync(CombatState combat, Player player)
    {
        string resourceCardId = _request.ScenarioId.StartsWith("AUTOMATION-", StringComparison.Ordinal) ? "AUTOMATION" : "ORBIT";
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        var enemy = combat.Enemies.Single();
        int enemyHp = _request.ScenarioId.EndsWith("-SHORT", StringComparison.Ordinal) ? 14 : _request.EnemyCurrentHp;
        await CreatureCmd.SetMaxHp(enemy, enemyHp);
        await CreatureCmd.SetCurrentHp(enemy, enemyHp);
        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
        await SetBlockAsync(player.Creature, 0);
        SetEnergy(player, 3);
        foreach (string id in new[] { resourceCardId, "INFLAME", "STONE_ARMOR", "IRON_WAVE", "POMMEL_STRIKE", "SHRUG_IT_OFF", "DEFEND_IRONCLAD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        foreach (string id in new[] { "IRON_WAVE", "STRIKE_IRONCLAD", "IRON_WAVE", "DEFEND_IRONCLAD", "SHRUG_IT_OFF", "IRON_WAVE", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Draw" });
        if (_request.ScenarioId.EndsWith("-DEPLOY", StringComparison.Ordinal))
        {
            SetStage("full_auto");
            SolverSettings.ApplyForTesting(SolverSettings.Current with
            {
                PerformancePreset = SolverPerformancePreset.Custom,
                SearchTimeLimitSeconds = 60,
                SearchBeamWidth = 24,
                SearchMaxExpandedNodes = 6000,
                SearchMaxDegreeOfParallelism = 1,
                DeploymentFastMode = SolverDeploymentFastMode.Instant,
                DeploymentInterActionDelaySeconds = 0,
            });
            SolverController.BeginCombat(combat);
            SolverController.SetStopFullAutoOnCombatEnd(false, persist: false);
            SolverController.SetStopFullAutoOnDeathTurn(false, persist: false);
            SolverController.SetStopFullAutoOnWorseRecalculation(false, persist: false);
            _protocolHost.EnableAutomaticTurnSearch();
            SolverController.SetFullAuto(_host, combat, true);
            while (CombatManager.Instance.IsInProgress)
            {
                EnsureWithinDeadline();
                if (SolverController.UnexpectedReplanCount != 0)
                    throw new InvalidOperationException("Recurring energy deployment had an unexpected replan: " + SolverController.ReplanAuditForBugReport);
                await NextFrameAsync();
            }
            if (player.Creature.CurrentHp <= 0 || !SolverController.WasCardDeployedForTesting(resourceCardId))
                throw new InvalidOperationException("Recurring energy deployment did not win with the resource card.");
            _completedChecks.Add($"RecurringEnergyQuality:{resourceCardId}:NativeFullDeployment:Hp={player.Creature.CurrentHp}:UnexpectedReplans=0:Instant");
            return;
        }
        var root = CombatRootSnapshot.Capture(combat);
        var settings = SolverSettings.Capture();
        var stages = new Dictionary<string, int>();
        var observations = new List<object>();
        var observer = new SearchPathObserver(_ => true, observation =>
        {
            if (!observation.Actions.Any(a => a.CardId == resourceCardId)) return;
            string key = observation.Stage + ":" + observation.Reason;
            stages[key] = stages.GetValueOrDefault(key) + 1;
            if (observations.Count < 100 && observation.Retention != null && observation.ActionCount <= 2)
                observations.Add(observation);
        }, _ => true);
        var policy = SolverController.CaptureSearchPolicy(settings, combat, false, null) with
        {
            Profile = settings.Profile with { MaxExpandedNodes = _request.VerifyIncrementalSearch ? 600 : 6000,
                BeamWidth = 24, SoftTimeBudgetMilliseconds = 60000 },
            FixedBudget = true,
            MaxDegreeOfParallelism = 1,
            VerifyIncrementalSearch = _request.VerifyIncrementalSearch,
            Diagnostics = new SearchDiagnosticsSink(Entry.Logger.Info, Entry.Logger.Debug, observer),
        };
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var result = await Task.Run(() => CombatSearchCoordinator.Solve(root, names,
            damage, policy, CancellationToken.None, progressCallback: null));
        string evidence = System.Text.Json.JsonSerializer.Serialize(new
        {
            ResourceCard = resourceCardId, result.ProjectedBattleHpLost, result.ExpandedNodes, result.TransitionCount, result.BoundaryReason,
            result.CombatEndedTurn, result.DeathTurn, result.Snapshot.AllEnemiesDead,
            Actions = result.BestNode.Actions.Select(a => new { a.Turn, a.CardId, a.Kind }),
            Stages = stages, Observations = observations,
        });
        Entry.Logger.Info("[CombatSolver/Test] RECURRING_ENERGY_QUALITY " + evidence);
        _completedChecks.Add("RecurringEnergyQuality:SearchEvidence:" + evidence);
        if (_request.ScenarioId.EndsWith("-SHORT", StringComparison.Ordinal)
            && (!result.Snapshot.AllEnemiesDead || result.ProjectedBattleHpLost != 0 || result.CombatEndedTurn != 1))
            throw new InvalidOperationException("Recurring energy valuation lost the immediate zero-damage victory.");
        if (!_request.VerifyIncrementalSearch && !_request.ScenarioId.EndsWith("-SHORT", StringComparison.Ordinal)
            && (!result.Snapshot.AllEnemiesDead || !result.BestNode.Actions.Any(a => a.CardId == resourceCardId)))
            throw new InvalidOperationException("Recurring energy valuation lost the long-combat resource route.");
    }
}
