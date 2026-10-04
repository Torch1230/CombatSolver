using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // A continuation prefix may contain a card action that ended the turn in the frozen
    // route (VoidForm ends the player turn). Applying it must settle the turn end instead
    // of rejecting the prefix.
    private async Task AssertB013FixedPrefixTurnEndCardAsync(CombatState combat, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 40);
        await InjectCardAsync(combat, player, new() { CardId = "VOID_FORM", Pile = "Hand" });
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
        PlanAction voidForm = new(PlanActionKind.PlayCard, firstTurn,
            CardId: "VOID_FORM", EndsPlayerTurn: true);
        SolverResult result = new CombatBeamSolver(root, names, damage, policy,
            searchProfile: policy.Profile,
            fixedPrefixActions: [voidForm]).Solve();
        if (result.ResultScope != SolverResultScope.SearchCompletion
            || result.BestNode.Actions.Count == 0
            || result.BestNode.Actions[0].CardId != "VOID_FORM"
            || !result.BestNode.Actions[0].EndsPlayerTurn)
        {
            throw new InvalidOperationException(
                $"结束回合卡固定前缀未被接受：scope={result.ResultScope} " +
                $"actions={string.Join('+', result.BestNode.Actions.Select(action => action.CardId))}。");
        }
        _completedChecks.Add("FixedPrefixTurnEndCard:VoidFormAccepted:TurnAdvanced");
    }
}
