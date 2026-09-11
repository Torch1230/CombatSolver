using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRelicCountersAsync(CombatState combat, Player player)
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Relic counter policy: " + message);
        }
        var original = SolverSettings.Current;
        try
        {
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            foreach (var power in combat.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
            foreach (var entry in RelicCounterCatalog.All)
            {
                RelicModel relic = entry.Canonical().ToMutable();
                player.AddRelicInternal(relic);
                string member = entry.Id switch
                {
                    RelicCounterId.HappyFlower or RelicCounterId.FakeHappyFlower or RelicCounterId.Pendulum or RelicCounterId.PollinousCore => "TurnsSeen",
                    RelicCounterId.PenNib or RelicCounterId.Nunchaku => "AttacksPlayed",
                    RelicCounterId.TuningFork => "SkillsPlayed", RelicCounterId.JossPaper => "CardsExhausted",
                    RelicCounterId.IronClub => "CardsPlayed", RelicCounterId.GalacticDust => "StarsSpent",
                    _ => throw new ArgumentOutOfRangeException(),
                };
                SetRelicStateMember(relic, member, entry.Period - 1);
            }
            var rules = RelicCounterCatalog.All.Select(entry => new RelicCounterRule(entry.Id, true, entry.Period - 1, entry.Period - 1, 2)).ToArray();
            var settings = original with { AutomaticCalculationEnabled = false, RelicStrategyEnabled = true, RelicCounterRules = rules, GrowthBudgets = default, IgnoreLongTermRewards = false };
            var roundtrip = SolverSettings.RoundTripForTesting(settings);
            Check(roundtrip.RelicStrategyEnabled && roundtrip.RelicCounterRules.SequenceEqual(rules), "switches, ranges and allowances persist");
            SolverSettings.ApplyForTesting(settings);
            using (var panel = new SolverRelicStrategyPanel())
                Check(panel.ExerciseControlsForTesting(), "independent UI switches preserve other entries and saved values");
            Check(await SolverOverlay.ExerciseRelicPanelForTesting(), "header order, sidebar bounds and mutual exclusion");
            var targets = RelicCounterCatalog.Capture(combat, true, rules);
            Check(targets.Count == 10 && RelicCounterCatalog.Capture(combat, false, rules).Count == 0, "master switch");
            Check(RelicCounterCatalog.Capture(combat, true, rules.Select(rule => rule with { Enabled = false })).Count == 0, "per-relic switches");
            using (SimulationNotificationIsolation.Enter())
            {
                var simulator = new CombatPredictionSimulator(new SimulatedCombatState(combat));
                var shadow = (SimulatedCombatState)simulator.State.CombatState;
                var values = shadow.EvaluateRelicCounters(simulator, player, targets);
                Check(values.Satisfied && values.SatisfiedCount == 10 && values.HpCredit == 20, "all captured counters, inclusive ranges and one credit per relic");
                var fork = simulator.Fork();
                var child = (SimulatedCombatState)fork.State.CombatState;
                Check(child.EvaluateRelicCounters(fork, player, targets) == values, "fork preserves all counter goals");
                ((HappyFlower)player.Relics.Single(r => r is HappyFlower)).TurnsSeen = 0;
                Check(shadow.EvaluateRelicCounters(simulator, player, targets) == values, "frozen root ignores later live counter changes");
            }
            foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
            IronClub club = (IronClub)ModelDb.Relic<IronClub>().ToMutable();
            player.AddRelicInternal(club);
            await ClearPlayerPilesAsync(player);
            foreach (string id in new[] { "DEFEND_IRONCLAD", "STRIKE_IRONCLAD" })
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
            await CreatureCmd.SetCurrentHp(combat.Enemies[0], 1);
            SetEnergy(player, 3);
            var policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null) with
            {
                FixedBudget = true, BudgetOverrideMilliseconds = 1500, MaxDegreeOfParallelism = 1,
                PotionPolicy = SolverPotionPolicy.Disabled, VerifyIncrementalSearch = true,
                Profile = SolverSearchProfile.Default with { MaxExpandedNodes = 128 },
                RelicTargets = Array.AsReadOnly(new[] { new RelicCounterTarget(RelicCounterId.IronClub, 2, 2, 0, 4) }),
            };
            var root = CombatRootSnapshot.Capture(combat);
            var names = SolverDisplayNames.Capture(combat);
            async Task<SolverResult> Search(SearchPolicySnapshot selected)
                => await Task.Run(() => CombatSearchCoordinator.Solve(root, names, new BattleDamageSnapshot(0, 0, 0), selected, CancellationToken.None, null));
            var baseline = await Search(policy with { RelicTargets = Array.Empty<RelicCounterTarget>() });
            var aligned = await Search(policy);
            Check(baseline.ProjectedBattleHpLost == 0 && aligned.ProjectedBattleHpLost == 0 && aligned.Snapshot.AllEnemiesDead,
                "baseline and aligned routes survive with zero loss");
            Check(aligned.Snapshot.RelicCounters.Satisfied && aligned.BestNode.Actions.Count(action => action.Kind == PlanActionKind.PlayCard) == 2,
                "real search aligns before the killing card");
            Check(CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(policy, aligned)
                && !CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(policy, baseline), "only satisfied goals unblock zero-loss stopping");
            Check(!CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(policy with { HasGrowthTargets = true }, aligned), "existing growth gate remains");
            Check(!CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(policy with { StopAtAcceptableBattleHpLoss = false }, aligned), "stop switch remains authoritative");
            var continued = await Search(policy with { StopAtAcceptableBattleHpLoss = false });
            Check(aligned.TotalExpandedNodes <= continued.TotalExpandedNodes, "satisfied goals do not disable stopping globally");
            await ClearPlayerPilesAsync(player);
            foreach (string id in new[] { "BLOODLETTING", "STRIKE_IRONCLAD" })
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
            root = CombatRootSnapshot.Capture(combat);
            names = SolverDisplayNames.Capture(combat);
            var free = await Search(policy);
            var paidPolicy = policy with { AcceptableBattleHpLoss = 3,
                RelicTargets = Array.AsReadOnly(new[] { new RelicCounterTarget(RelicCounterId.IronClub, 2, 2, 3, 4) }) };
            var paid = await Search(paidPolicy);
            Check(free.ProjectedBattleHpLost == 0 && !free.Snapshot.RelicCounters.Satisfied, "zero allowance rejects paid alignment");
            Check(paid.ProjectedBattleHpLost == 3 && paid.Snapshot.RelicCounters.Satisfied && paid.Snapshot.RelicCounters.HpCredit == 3,
                "configured allowance admits exactly three HP alignment");
            Check(CombatSearchCoordinator.HasReachedAcceptableBattleHpLoss(paidPolicy, paid), "configured loss threshold still stops paid aligned route");
            var nativeCards = player.PlayerCombatState!.Hand.Cards.ToArray();
            await CardCmd.AutoPlay(new ThrowingPlayerChoiceContext(), nativeCards.Single(card => card.Id.Entry == "BLOODLETTING"), null);
            await CardCmd.AutoPlay(new ThrowingPlayerChoiceContext(), nativeCards.Single(card => card.Id.Entry == "STRIKE_IRONCLAD"), combat.Enemies[0]);
            Check(club.CardsPlayed % 4 == 2, "native killing-card hook leaves the same final counter");
            _completedChecks.Add($"RelicCounters:10RootReaders:FrozenLive:Fork:Persistence:MasterAndIndividual:AlignedZeroLoss:EarlyStop:Nodes={aligned.TotalExpandedNodes}/{continued.TotalExpandedNodes}");
        }
        finally { SolverSettings.ApplyForTesting(original); }
    }
}
