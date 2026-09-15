using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertHpTargetStopAsync(CombatState combat, Player player, bool noveltyPortfolio = false)
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("HP target stop: " + message);
        }
        SolverSettingsData original = SolverSettings.Current;
        try
        {
            Check(new SolverSettingsData().StopAtAcceptableBattleHpLoss, "default enabled");
            var settings = original with { StopAtAcceptableBattleHpLoss = true,
                GrowthBudgets = new GrowthValues(ForbiddenGrimoire: 12), IgnoreLongTermRewards = false };
            Check(!SolverSettings.RoundTripForTesting(settings with { StopAtAcceptableBattleHpLoss = false }).StopAtAcceptableBattleHpLoss,
                "disabled preference round trip");
            SolverSettings.ApplyForTesting(settings);
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
            await ClearPlayerPilesAsync(player);
            foreach (string id in new[] { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "BASH", "INFLAME" })
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
            await CreatureCmd.SetCurrentHp(combat.Enemies[0], 1);
            SetEnergy(player, 3);
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
            {
                Act3BossStrategy = true,
                FixedBudget = true, BudgetOverrideMilliseconds = noveltyPortfolio ? 10000 : 1500,
                PotionPolicy = SolverPotionPolicy.Disabled, MaxDegreeOfParallelism = 1,
                DetailedDiagnostics = false, VerifyIncrementalSearch = false,
                Profile = SolverSearchProfile.Default with { MaxExpandedNodes = noveltyPortfolio ? 4000 : 128 },
                UseNoveltyPortfolio = noveltyPortfolio,
            };
            Check(policy.StopAtAcceptableBattleHpLoss && !policy.HasGrowthTargets && policy.CanStopAtHpTarget,
                "saved allowance without matching cards permits stopping");
            CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
            SolverDisplayNames names = SolverDisplayNames.Capture(combat);
            async Task<SolverResult> Search(SearchPolicySnapshot requested, int alreadyLost = 0)
                => await Task.Run(() => CombatSearchCoordinator.Solve(root, names,
                    new BattleDamageSnapshot(alreadyLost, 0, 0), requested, CancellationToken.None, null));
            SolverResult stopped = await Search(policy);
            Check(stopped.Snapshot.AllEnemiesDead && stopped.ProjectedBattleHpLost == 0, "zero-loss complete victory");
            if (noveltyPortfolio)
                Check(stopped.NoveltyPortfolio?.ExplorationDetails != null, "novelty exploration actually ran");
            SolverResult continued = await Search(policy with { StopAtAcceptableBattleHpLoss = false });
            Check(continued.Snapshot.AllEnemiesDead && continued.ProjectedBattleHpLost == 0
                && stopped.TotalExpandedNodes < continued.TotalExpandedNodes, "switch stops before remaining combinations");
            SolverResult threshold = await Search(policy with { AcceptableBattleHpLoss = 3 }, alreadyLost: 3);
            Check(threshold.ProjectedBattleHpLost == 3
                && CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(policy with { AcceptableBattleHpLoss = 3 }, threshold)
                && !CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(policy, threshold)
                && !CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(policy with { StopAtAcceptableBattleHpLoss = false, AcceptableBattleHpLoss = 3 }, threshold),
                "total battle threshold and toggle boundaries");
            await CreatureCmd.SetCurrentHp(combat.Enemies[0], 12);
            root = CombatRootSnapshot.Capture(combat);
            SolverResult parallel = await Search(policy with { MaxDegreeOfParallelism = 2 });
            Check(parallel.Snapshot.AllEnemiesDead && parallel.ProjectedBattleHpLost == 0
                && (noveltyPortfolio ? parallel.NoveltyPortfolio?.ExplorationDetails != null
                    : parallel.MaxParallelExpansionConcurrency == 2), "parallel policy returns winner");
            await CreatureCmd.SetCurrentHp(combat.Enemies[0], 1);
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "FORBIDDEN_GRIMOIRE", Pile = "Hand" });
            player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == "FORBIDDEN_GRIMOIRE").BaseReplayCount = 1;
            SearchPolicySnapshot growth = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
            Check(growth.HasGrowthTargets && growth.GrowthOpportunityTargets.IsBounded
                && growth.CanStopAtHpTarget && !growth.GrowthTargetSatisfied(default)
                && growth.GrowthOpportunityTargets.RequiredRewards.ForbiddenGrimoire == 2
                && growth.GrowthTargetSatisfied(new GrowthValues(ForbiddenGrimoire: 2))
                && (growth with { IgnoreLongTermRewards = true }).GrowthTargetSatisfied(default),
                "power card requires its unplayed physical instance plus fixed replay and the ignore switch removes that requirement");
            root = CombatRootSnapshot.Capture(combat);
            names = SolverDisplayNames.Capture(combat);
            SolverResult rewarded = await Search(policy with { GrowthOpportunityTargets = growth.GrowthOpportunityTargets });
            Check(rewarded.Snapshot.AllEnemiesDead && rewarded.Snapshot.GrowthRewards.ForbiddenGrimoire == 2,
                "each fixed replay records the same real growth reward before zero-loss stopping");
            _completedChecks.Add($"HpTargetStop:zero_nodes={stopped.TotalExpandedNodes}:off_nodes={continued.TotalExpandedNodes}:parallel_nodes={parallel.TotalExpandedNodes}:threshold3:growth_reward2");
            await ClearPlayerPilesAsync(player);
            foreach (string id in new[] { "THE_HUNT", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD" })
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
            SearchPolicySnapshot hunt = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
            Check(hunt.GrowthOpportunityTargets.IsBounded
                && hunt.GrowthOpportunityTargets.RequiredRewards.TheHunt == 1
                && !hunt.GrowthTargetSatisfied(default)
                && hunt.GrowthTargetSatisfied(new GrowthValues(TheHunt: 1)), "fatal growth completion target");
            root = CombatRootSnapshot.Capture(combat);
            names = SolverDisplayNames.Capture(combat);
            SolverResult hunted = await Search(policy with { GrowthOpportunityTargets = hunt.GrowthOpportunityTargets });
            Check(hunted.Snapshot.GrowthRewards.TheHunt == 1 && hunted.ProjectedBattleHpLost == 0
                && CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(hunt, hunted), "rewarded zero-loss hunt stops");

            await ClearPlayerPilesAsync(player);
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
            var forced = InjectPotionForTest(player, "ENERGY_POTION");
            var spare = InjectPotionForTest(player, "STRENGTH_POTION");
            PotionStrategySnapshot potionStrategy = new(SolverPotionPolicy.Smart,
                [new(player.GetPotionSlotIndex(forced), forced.Id.Entry, SolverPotionDirective.Force),
                 new(player.GetPotionSlotIndex(spare), spare.Id.Entry, SolverPotionDirective.Smart)]);
            root = CombatRootSnapshot.Capture(combat);
            names = SolverDisplayNames.Capture(combat);
            SolverResult onePotion = await Search(policy with { GrowthOpportunityTargets = GrowthOpportunityTargets.Empty,
                PotionPolicy = SolverPotionPolicy.Smart, PotionStrategy = potionStrategy });
            Check(onePotion.Snapshot.AllEnemiesDead && onePotion.ProjectedBattleHpLost == 0 && onePotion.PotionCount == 1
                && onePotion.BestNode.Actions.Any(a => a.PotionId == forced.Id.Entry), "forced potion zero loss preserves spare potion");
            _completedChecks.Add($"HpTargetStop:HuntRewardFulfilled:nodes={hunted.ExpandedNodes}:ForcedOnePotion:SparePreserved");
            SolverResult requiredOne = await Search(policy with { GrowthOpportunityTargets = GrowthOpportunityTargets.Empty,
                PotionPolicy = SolverPotionPolicy.RequireAtLeastOne,
                PotionStrategy = new PotionStrategySnapshot(SolverPotionPolicy.RequireAtLeastOne, []) });
            Check(requiredOne.Snapshot.AllEnemiesDead && requiredOne.PotionCount == 1, "at least one potion preserves spare at zero loss");
            var greedyCard = MegaCrit.Sts2.Core.Models.ModelDb.Card<MegaCrit.Sts2.Core.Models.Cards.HandOfGreed>().ToMutable();
            GrowthOpportunityTargets greedTarget = GrowthOpportunityPolicy.CaptureBuiltInForTesting([greedyCard], 3);
            Check(greedTarget.IsBounded && greedTarget.RequiredRewards.HandOfGreed == 3,
                "repeatable fatal source retains all enemy opportunities");
            _completedChecks.Add("HpTargetStop:RequireOnePotion:RepeatableFatalTarget3");

            async Task<GrowthOpportunityTargets> CaptureCards(params UnattendedCardInjection[] cards)
            {
                await ClearPlayerPilesAsync(player);
                foreach (UnattendedCardInjection card in cards)
                    await InjectCardAsync(combat, player, card);
                return SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null)
                    .GrowthOpportunityTargets;
            }

            GrowthOpportunityTargets powers = await CaptureCards(
                new UnattendedCardInjection { CardId = "ROYALTIES", Pile = "Hand", Count = 2 },
                new UnattendedCardInjection { CardId = "FORBIDDEN_GRIMOIRE", Pile = "Exhaust" });
            Check(powers.IsBounded && powers.RequiredRewards.Royalties == 2
                && powers.RequiredRewards.ForbiddenGrimoire == 0,
                "power targets count each unplayed physical instance and exclude instances already outside playable piles");

            GrowthOpportunityTargets alchemize = await CaptureCards(
                new UnattendedCardInjection { CardId = "ALCHEMIZE", Pile = "Hand", Count = 2 });
            Check(alchemize.IsBounded && alchemize.RequiredRewards.Alchemize == 2,
                "Alchemize uses executable card instances without clipping to current empty potion slots");

            GrowthOpportunityTargets permanentExhaust = await CaptureCards(
                new UnattendedCardInjection { CardId = "GENETIC_ALGORITHM", Pile = "Hand", TreatAsDeckCard = true },
                new UnattendedCardInjection { CardId = "GENETIC_ALGORITHM", Pile = "Hand" },
                new UnattendedCardInjection { CardId = "THE_SCYTHE", Pile = "Hand", TreatAsDeckCard = true },
                new UnattendedCardInjection { CardId = "THE_SCYTHE", Pile = "Exhaust", TreatAsDeckCard = true });
            Check(permanentExhaust.IsBounded
                && permanentExhaust.RequiredRewards.GeneticAlgorithm == 1
                && permanentExhaust.RequiredRewards.TheScythe == 1,
                "permanent exhaust growth counts only unconsumed deck-backed instances");

            GrowthOpportunityTargets goopy = await CaptureCards(
                new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand", TreatAsDeckCard = true,
                    EnchantmentId = "GOOPY" },
                new UnattendedCardInjection { CardId = "DEFEND_IRONCLAD", Pile = "Hand", EnchantmentId = "GOOPY" });
            Check(goopy.IsBounded && goopy.RequiredRewards.Goopy == 1,
                "Goopy counts the actual exhaust enchantment only on permanent deck instances");

            GrowthOpportunityTargets fatalCompetition = await CaptureCards(
                new UnattendedCardInjection { CardId = "THE_HUNT", Pile = "Hand" },
                new UnattendedCardInjection { CardId = "FEED", Pile = "Hand" });
            Check(!fatalCompetition.IsBounded
                && fatalCompetition.UnboundedSources.Count(source => source.Reason == "fatal_source_competition") == 2,
                "fatal growth sources competing for the same enemies remain unprovable");

            GrowthOpportunityTargets dynamicReplay = await CaptureCards(
                new UnattendedCardInjection { CardId = "FORBIDDEN_GRIMOIRE", Pile = "Hand" },
                new UnattendedCardInjection { CardId = "BURST", Pile = "Draw" });
            Check(!dynamicReplay.IsBounded
                && dynamicReplay.UnboundedSources.Any(source => source.SourceId == nameof(GrowthSource.ForbiddenGrimoire)
                    && source.Reason == "built_in:reachable_replay_or_copy_card"),
                "reachable dynamic replay disables growth early stopping");
            GrowthOpportunityTargets exhaustRecovery = await CaptureCards(
                new UnattendedCardInjection { CardId = "GENETIC_ALGORITHM", Pile = "Hand", TreatAsDeckCard = true },
                new UnattendedCardInjection { CardId = "HOWL_FROM_BEYOND", Pile = "Draw" });
            Check(!exhaustRecovery.IsBounded,
                "reachable exhaust recovery disables growth early stopping");
            _completedChecks.Add("HpTargetStop:GrowthOpportunityTargets:Power:Exhaust:DeckBacked:Replay:Goopy:FatalCompetition:Recovery");
        }
        finally { SolverSettings.ApplyForTesting(original); }
    }
}
