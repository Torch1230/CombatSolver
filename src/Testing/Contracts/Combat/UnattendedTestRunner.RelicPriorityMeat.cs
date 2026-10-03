using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRelicPriorityMeatAsync(CombatState combat, Player player)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Relic priorities: " + message); }
        var legacy = JsonSerializer.Deserialize<RelicCounterRule>("{\"Id\":0,\"Enabled\":true,\"Minimum\":2,\"Maximum\":2,\"HpAllowance\":0}")!;
        Check(legacy.Priority == 1, "old rules retain normal priority");
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        player.AddRelicInternal(ModelDb.Relic<IronClub>().ToMutable());
        player.AddRelicInternal(ModelDb.Relic<TuningFork>().ToMutable());
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        await CreatureCmd.SetCurrentHp(combat.Enemies[0], 1);
        SetEnergy(player, 3);
        var root = CombatRootSnapshot.Capture(combat);
        var names = SolverDisplayNames.Capture(combat);
        var damage = BattleDamageTracker.Observe(combat);
        var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
        {
            FixedBudget = true, BudgetOverrideMilliseconds = 1500, MaxDegreeOfParallelism = 1,
            PotionPolicy = SolverPotionPolicy.Disabled, VerifyIncrementalSearch = true,
            Profile = SolverSearchProfile.Default with { MaxExpandedNodes = 64 },
            RelicTargets = new[] { new RelicCounterTarget(RelicCounterId.IronClub, 1, 1, 0, 4, 3), new RelicCounterTarget(RelicCounterId.TuningFork, 1, 1, 0, 10, 1) },
        };
        var first = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null));
        Check(first.Snapshot.RelicCounters.Value(RelicCounterId.IronClub) == 1 && first.ProjectedBattleHpLost == 0, "higher priority selects club at equal loss");
        policy = policy with { RelicTargets = policy.RelicTargets.Select(t => t with { Priority = t.Id == RelicCounterId.TuningFork ? 3 : 1 }).ToArray() };
        var reversed = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null));
        Check(reversed.Snapshot.RelicCounters.Value(RelicCounterId.TuningFork) == 1 && reversed.ProjectedBattleHpLost == 0, "reversing priorities selects tuning fork at equal loss");
        var high = RelicCounterPolicy.Add(default, new(RelicCounterId.HappyFlower, 2, 2, 0, 3, 3), 2);
        var low = RelicCounterPolicy.Add(default, new(RelicCounterId.PenNib, 1, 1, 0, 10, 1), 1);
        Check(high.SatisfiedPriority > low.SatisfiedPriority && high.HpCredit == low.HpCredit, "priority has no HP allowance");
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        player.AddRelicInternal(ModelDb.Relic<MeatOnTheBone>().ToMutable());
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "BLOODLETTING", "STRIKE_IRONCLAD" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        int initialHp = player.Creature.MaxHp / 2 + 1;
        await CreatureCmd.SetCurrentHp(player.Creature, initialHp);
        root = CombatRootSnapshot.Capture(combat); names = SolverDisplayNames.Capture(combat); damage = BattleDamageTracker.Observe(combat);
        policy = policy with { RelicTargets = new[] { new RelicCounterTarget(RelicCounterId.MeatOnTheBone, 1, 1, 0, 2) } };
        var meat = await Task.Run(() => CombatSearchCoordinator.Solve(root, names, damage, policy, CancellationToken.None, null));
        Check(meat.Snapshot.AllEnemiesDead && meat.Snapshot.RelicCounters.Satisfied
            && meat.PostCombatRelicHeal > 0 && meat.Snapshot.PlayerHp + meat.PostCombatRelicHeal > initialHp
            , $"half-HP route earns net post-combat healing: won={meat.Snapshot.AllEnemiesDead} satisfied={meat.Snapshot.RelicCounters.Satisfied} hp={meat.Snapshot.PlayerHp} heal={meat.PostCombatRelicHeal} initial={initialHp} actions={string.Join(',', meat.BestNode.Actions.Select(a => a.CardId))}");
        await CreatureCmd.SetMaxHp(player.Creature, 70);
        // EndTurn also wins before the enemy can offer an alternative, cheaper HP payment.
        await InjectPowerAsync(combat, player, new UnattendedPowerInjection { PowerId = "POISON_POWER", Target = "Enemy", Amount = 1 });
        foreach (int extraLoss in new[] { 1, 12, 13 })
        {
            await ClearPlayerPilesAsync(player);
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BLOODLETTING", Pile = "Hand", UpgradeLevels = 1,
                DynamicVars = new() { ["HpLoss"] = 14 } });
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "BLOODLETTING", Pile = "Hand",
                DynamicVars = new() { ["HpLoss"] = extraLoss } });
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
            await CreatureCmd.SetCurrentHp(player.Creature, 50);
            root = CombatRootSnapshot.Capture(combat); names = SolverDisplayNames.Capture(combat); damage = BattleDamageTracker.Observe(combat);
            policy = policy with { RelicTargets = new[] { new RelicCounterTarget(RelicCounterId.MeatOnTheBone, 1, 1, 1000, 2, 3) } };
            // Preserve the same 14-HP prefix for both alternatives, representing an already chosen policy cost.
            var prefix = new[] { new PlanAction(PlanActionKind.PlayCard, root.StartTurnNumber,
                CardId: "BLOODLETTING", CardUpgradeLevel: 1) };
            var result = await Task.Run(() => new CombatBeamSolver(root, names, damage, policy,
                searchProfile: policy.Profile, fixedPrefixActions: prefix).Solve());
            int expectedHp = extraLoss == 1 ? 35 : 36;
            Check(result.Snapshot.AllEnemiesDead && result.Snapshot.PlayerHp == expectedHp
                && result.Snapshot.RelicCounters.HpCredit == 0,
                $"incremental cost {extraLoss}: expected HP {expectedHp}, actual {result.Snapshot.PlayerHp}");
            if (extraLoss == 1)
                Check(result.PostCombatRelicHeal == 12 && result.Snapshot.PlayerHp + result.PostCombatRelicHeal == 47,
                    "50 -> 36 -> 35 -> 47 earns 11 HP over stopping at 36");
        }
        _completedChecks.Add("RelicPriority:LegacyDefault:NoExtraHpAllowance:MeatIncrementalGain:50to36to35to47:BreakEvenAndLossRejected:IncrementalReplay");
    }
}
