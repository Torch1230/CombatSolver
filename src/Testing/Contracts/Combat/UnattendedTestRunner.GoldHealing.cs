using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Resources;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertGoldInactiveLifecycleAsync(CombatState live, Player player)
    {
        await ClearOrderedEffectFixtureAsync(live, player);
        await InjectRelicAsync(player, new() { RelicId = "DRAGON_FRUIT" });
        player.Gold = 137;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        CombatPredictionSimulator parent = root.ForkSimulator();
        CombatPredictionSimulator child = parent.Fork();
        string before = DescribeContinuationContractState(parent, root, player);
        SimulatedCombatState shadow = (SimulatedCombatState)child.State.CombatState;
        int maxHp = child.State.GetCreature(player.Creature).MaxHp;
        if (!child.Kill(player.Creature, force: true))
            throw new InvalidOperationException("金币生命周期夹具意外挂起死亡。");
        int gained = shadow.GainPlayerGold(child, player, 20);
        if (gained != 20 || shadow.GetPlayerGold(player) != 157
            || child.State.GetCreature(player.Creature).MaxHp != maxHp
            || child.State.GetCreature(player.Creature).CurrentHp != 0
            || DescribeContinuationContractState(parent, root, player) != before)
            throw new InvalidOperationException("失活玩家金币回调未保持金币、死亡与父分支隔离。");
        player.DeactivateHooks();
        try
        {
            string[] actualListeners = RunManager.Instance.DebugOnlyGetState()!.IterateHookListeners(null)
                .Select(model => model.GetType().FullName!).ToArray();
            string[] predictedListeners = shadow.GoldAfterGainHookListeners(child)
                .Select(model => model.GetType().FullName!).ToArray();
            if (!actualListeners.SequenceEqual(predictedListeners))
                throw new InvalidOperationException("失活玩家的金币跑局监听序列与原版不同。");
            await PlayerCmd.GainGold(20, player);
            if (player.Gold != 157 || player.Creature.MaxHp != maxHp)
                throw new InvalidOperationException("原版失活玩家金币命令未保留收益或触发了玩家遗物。");
        }
        finally { player.ActivateHooks(); }
        _completedChecks.Add("GoldHooks:PlayerDeath:NativeDeactivation:OrderedRunListeners:GainGold:RelicInactive:ForkIsolation");
    }

    private async Task AssertGoldHealingMechanismsAsync(CombatState live, Player player, bool includeBasicCases = true)
    {
        var evidence = new List<object>();
        async Task Relics(params string[] ids)
        {
            foreach (RelicModel relic in player.Relics.ToArray())
                await RelicCmd.Remove(relic);
            foreach (string id in ids)
                await InjectRelicAsync(player, new() { RelicId = id });
        }
        async Task CheckGold(string label, decimal amount, int expectedGold, int expectedHeal)
        {
            player.Gold = 137;
            int hp = player.Creature.CurrentHp, maxHp = player.Creature.MaxHp;
            CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
            CombatPredictionSimulator parent = root.ForkSimulator();
            CombatPredictionSimulator child = parent.Fork();
            var shadow = (SimulatedCombatState)child.State.CombatState;
            string parentBefore = DescribeContinuationContractState(parent, root, player);
            string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            int gained = shadow.GainPlayerGold(child, player, amount);
            if (gained != expectedGold || shadow.GetPlayerGold(player) != 137 + expectedGold
                || child.State.GetCreature(player.Creature).MaxHp != maxHp + expectedHeal
                || child.State.GetCreature(player.Creature).CurrentHp != hp + expectedHeal
                || DescribeContinuationContractState(parent, root, player) != parentBefore
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException($"Gold prediction or branch/live isolation failed: {label}.");
            var expected = CaptureSimulated(child, shadow, player, live.Enemies[0]);
            await PlayerCmd.GainGold(amount, player);
            AssertSnapshotEqual(expected, CaptureActual(live, player, live.Enemies[0]),
                _request.ScenarioId, label);
            bool hasDragon = player.Relics.Any(relic => relic is DragonFruit && !relic.IsMelted);
            int bound = StrategicHpRecoveryBound.KnownNativeHealingPotential(parent, player, 0);
            if (hasDragon && bound != int.MaxValue || !hasDragon && bound != 0)
                throw new InvalidOperationException($"Gold healing policy failed to preserve active sources: {label}.");
            if (DescribeContinuationContractState(parent, root, player) != parentBefore)
                throw new InvalidOperationException($"Native gold changed the captured parent: {label}.");
            evidence.Add(new { label, amount, gained, expectedHeal, bound,
                nativeHp = player.Creature.CurrentHp, nativeMaxHp = player.Creature.MaxHp });
            _completedChecks.Add($"GoldHealing:{label}:FullNativeStateRng:ForkParentLiveIsolation");
        }

        if (includeBasicCases)
        {
            await Relics();
            await CheckGold("NoHealing", 20m, 20, 0);
            await Relics("DRAGON_FRUIT");
            await CheckGold("DragonNegative", -5m, 0, 0);
            await CheckGold("DragonZero", 0m, 0, 0);
            await CheckGold("DragonFractional", .25m, 0, 1);
            await CheckGold("DragonPositive", 20m, 20, 1);
            await Relics("DRAGON_FRUIT", "BOWLER_HAT");
            await CheckGold("BowlerDragon", 20m, 25, 1);
            await CheckGold("BowlerDragonFractional", .75m, 0, 1);
            await Relics("DRAGON_FRUIT", "ECTOPLASM");
            await CheckGold("EctoplasmBlocksDragon", 20m, 0, 0);
            await Relics("DRAGON_FRUIT", "BOWLER_HAT", "ECTOPLASM");
            await CheckGold("BowlerThenEctoplasm", 20m, 0, 0);
            await Relics("DRAGON_FRUIT", "ECTOPLASM", "BOWLER_HAT");
            await CheckGold("EctoplasmThenBowler", 20m, 0, 0);
        }
        await Relics("DRAGON_FRUIT");
        player.Relics.OfType<DragonFruit>().Single().IsWax = true;
        await RelicCmd.Melt(player.Relics.OfType<DragonFruit>().Single());
        await CheckGold("MeltedDragon", 20m, 20, 0);

        CombatRootSnapshot unknownRoot = CombatRootSnapshot.Capture(live);
        var unknownParent = unknownRoot.ForkSimulator();
        string unknownBefore = DescribeContinuationContractState(unknownParent, unknownRoot, player);
        var receiver = ModelDb.All.OfType<UnknownGoldSubscriber>().Single();
        for (int stage = 0; stage < 3; stage++)
        {
            var branch = unknownParent.Fork();
            var context = new GoldGainMirrorContext { Simulator = branch, Player = player, Amount = 20m };
            bool rejected = false;
            try
            {
                if (stage == 0) _ = GoldGainedMirrors.Modify(receiver, context);
                else if (stage == 1) GoldGainedMirrors.AfterModify(receiver, context);
                else GoldGainedMirrors.AfterGain(receiver, context);
            }
            catch (PredictionUnsupportedException) { rejected = true; }
            if (!rejected || receiver.Calls != 0
                || DescribeContinuationContractState(unknownParent, unknownRoot, player) != unknownBefore)
                throw new InvalidOperationException("Unknown gold callback was executed or parent was changed.");
        }
        _completedChecks.Add("GoldHealing:UnknownThreeCallbacksRejectedWithoutExecution:ParentIsolation");

        for (int blocked = 0; blocked < 2; blocked++)
        {
            if (blocked == 0)
                await Relics("DRAGON_FRUIT", "BOWLER_HAT");
            else
                await Relics("DRAGON_FRUIT", "BOWLER_HAT", "ECTOPLASM");
            await ClearPlayerPilesAsync(player);
            await InjectCardAsync(live, player, new() { CardId = "HAND_OF_GREED", Pile = "Hand", TreatAsDeckCard = true });
            SetEnergy(player, 3);
            var target = live.Enemies[0];
            if (live.Enemies.Count == 1)
                await AddMonsterForTestAsync(live, target.Monster!.Id.Entry, null);
            await CreatureCmd.SetCurrentHp(target, 1);
            player.Gold = 137;
            CombatRootSnapshot routeRoot = CombatRootSnapshot.Capture(live);
            var routeParent = routeRoot.ForkSimulator();
            var prediction = routeParent.Fork();
            var routeCombat = (SimulatedCombatState)prediction.State.CombatState;
            string routeParentBefore = DescribeContinuationContractState(routeParent, routeRoot, player);
            string routeLiveBefore = ContinuationStamp.CaptureLive(live).StateText;
            CardModel nativeCard = player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == "HAND_OF_GREED");
            using (SimulationNotificationIsolation.Enter())
            {
                var card = prediction.State.GetPlayerCombatState(player).Hand.Cards
                    .Single(card => card.Preview.Id.Entry == "HAND_OF_GREED");
                using (routeCombat.BeginCardExecutionScope(new ForkableSet<uint>()))
                    if (!prediction.ManualPlay(card, target, out _))
                        throw new InvalidOperationException("Gold fatal route unexpectedly suspended.");
                CombatBeamSolver.SettleReplayActionBoundary(prediction, routeCombat);
            }
            int expectedGold = blocked == 0 ? 25 : 0;
            if (routeCombat.GetPlayerGold(player) != 137 + expectedGold || routeCombat.LongTermResourceValue != expectedGold
                || routeCombat.GrowthRewards.HandOfGreed != 1
                || DescribeContinuationContractState(routeParent, routeRoot, player) != routeParentBefore
                || ContinuationStamp.CaptureLive(live).StateText != routeLiveBefore)
                throw new InvalidOperationException("HandOfGreed route lost actual gold, growth or isolation.");
            var routeExpected = CaptureSimulated(prediction, routeCombat, player, target);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction play
                    && ReferenceEquals(play.NetCombatCard.ToCardModelOrNull(), nativeCard),
                () => { if (!nativeCard.TryManualPlay(target)) throw new InvalidOperationException("Native gold route refused."); },
                deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            AssertSnapshotEqual(routeExpected, CaptureActual(live, player, target), _request.ScenarioId, $"HandOfGreedFatal:blocked={blocked}");
            _completedChecks.Add($"GoldHealing:HandOfGreedFatal:NativePlay:Gold={expectedGold}:GrowthResource:FullStateRng:ParentLiveIsolation");
        }
        _writer.WriteGeneratedArtifact("gold-mechanisms.json", evidence);
    }

    private sealed class UnknownGoldSubscriber : AbstractModel
    {
        public override bool ShouldReceiveCombatHooks => false;
        public int Calls;
        public override decimal ModifyGoldGained(Player player, decimal amount) { Calls++; return amount; }
        public override Task AfterModifyingGoldGained(Player player, decimal amount) { Calls++; return Task.CompletedTask; }
        public override Task AfterGoldGained(Player player) { Calls++; return Task.CompletedTask; }
    }
}
