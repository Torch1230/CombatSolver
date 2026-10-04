using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // The turn-boundary rescue can dispatch a continuation whose fixed prefix wins the
    // combat before the prefix ends. Applying the remaining actions to a stopped
    // simulator was the reported replay failure; the prefix must stop at the locked
    // outcome instead of crossing it.
    private async Task AssertB013FixedPrefixTerminalBoundaryAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 1);
        await InjectCardAsync(combat, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(combat, player, new() { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        SetEnergy(player, 3);

        int firstTurn = player.PlayerCombatState!.TurnNumber;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, false, null) with
        {
            FixedBudget = true, VerifyIncrementalSearch = false, DetailedDiagnostics = false,
            MaxDegreeOfParallelism = 1, BudgetOverrideMilliseconds = 5000,
            PotionPolicy = SolverPotionPolicy.Disabled,
            PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.Disabled, []),
            Profile = SolverSearchProfile.Default with
            {
                BeamWidth = 8, MaxExpandedNodes = 100, SoftTimeBudgetMilliseconds = 5000,
            },
        };
        PlanAction kill = new(PlanActionKind.PlayCard, firstTurn,
            CardId: "STRIKE_IRONCLAD", TargetCombatId: combat.Enemies.Single().CombatId);
        PlanAction afterOutcome = new(PlanActionKind.PlayCard, firstTurn, CardId: "DEFEND_IRONCLAD");

        SolverResult result = new CombatBeamSolver(root, names, damage, policy,
            searchProfile: policy.Profile,
            fixedPrefixActions: [kill, afterOutcome]).Solve();
        if (result.ResultScope != SolverResultScope.SearchCompletion
            || !result.Snapshot.AllEnemiesDead
            || result.BestNode.Actions.Count != 1
            || result.BestNode.Actions[0].CardId != "STRIKE_IRONCLAD")
        {
            throw new InvalidOperationException(
                $"固定前缀未在战斗终局处截断：scope={result.ResultScope} " +
                $"enemies_dead={result.Snapshot.AllEnemiesDead} " +
                $"actions={string.Join('+', result.BestNode.Actions.Select(action => action.CardId))}。");
        }
        _completedChecks.Add("FixedPrefixTerminalBoundary:TruncatedAtVictory:Actions=1");
    }
}
