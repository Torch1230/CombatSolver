using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertIronRemainingHealingAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray())
            if (relic is not BurningBlood) await RelicCmd.Remove(relic);
        foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray())
            if (power is not ReattachPower) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live, player, new() { CardId = "DEFEND_IRONCLAD", Pile = "Hand" });
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Draw" });
        await InjectCardAsync(live, player, new() { CardId = "INFERNAL_BLADE", Pile = "Hand" });
        await InjectCardAsync(live, player, new() { CardId = "JACK_OF_ALL_TRADES", Pile = "Hand" });
        await InjectCardAsync(live, player, new() { CardId = "JACKPOT", Pile = "Hand" });
        SetEnergy(player, 20);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        if (!root.CanCertifyRemainingHealing || root.InitialRemainingHealingUpperBound != 0)
            throw new InvalidOperationException("铁甲原生闭包未通过初始界限检查。");
        CombatPredictionSimulator parent = root.ForkSimulator();
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        using (SimulationNotificationIsolation.Enter())
        {
            if (StrategicHpRecoveryBound.RemainingHealingUpperBound(parent, player, 6) != 6)
                throw new InvalidOperationException("活动造牌入口没有保留战后6HP界限。");
            foreach (var (model, pile) in new (CardModel, PileType)[]
            {
                (ModelDb.Card<Feed>(), PileType.Hand),
                (ModelDb.Card<Feed>(), PileType.Exhaust),
            })
            {
                CombatPredictionSimulator child = parent.Fork();
                child.AddGeneratedCardToCombat(PredictedCard.Create(model, player), pile, player,
                    resultKind: CardGenerationResultKind.Fixed);
                if (StrategicHpRecoveryBound.RemainingHealingUpperBound(child, player, 6) != int.MaxValue
                    || StrategicHpRecoveryBound.CanCertifyRemainingHealingEnvironment(child, player))
                    throw new InvalidOperationException($"未保留 {model.Id}/{pile} 的保守治疗界限。");
            }
            CombatPredictionSimulator regen = parent.Fork();
            ((SimulatedCombatState)regen.State.CombatState).SetAmount<RegenPower>(player.Creature, 5);
            if (StrategicHpRecoveryBound.RemainingHealingUpperBound(regen, player, 6) != 21)
                throw new InvalidOperationException("再生5与战后6HP未得到21HP上界。");
            CombatPredictionSimulator wrongOwner = parent.Fork();
            ((SimulatedCombatState)wrongOwner.State.CombatState).SetAmount<ReattachPower>(player.Creature, 25);
            if (StrategicHpRecoveryBound.RemainingHealingUpperBound(wrongOwner, player, 6) != int.MaxValue)
                throw new InvalidOperationException("玩家拥有的重新接合错误使用了敌方自愈例外。");
            CombatPredictionSimulator affliction = parent.Fork();
            PredictedCard unknown = PredictedCard.Create(ModelDb.Card<DefendIronclad>(), player);
            affliction.AddGeneratedCardToCombat(unknown, PileType.Hand, player,
                resultKind: CardGenerationResultKind.Fixed);
            if (affliction.Afflict<Hexed>(unknown, 1) is null
                || StrategicHpRecoveryBound.RemainingHealingUpperBound(affliction, player, 6) != int.MaxValue)
                throw new InvalidOperationException("未知附着效果没有禁用铁甲界限。");

            var combat = (SimulatedCombatState)parent.State.CombatState;
            if (((ICombatPredictionCardGenerationPoolSnapshot)combat).TryGetRootEligibleAllCharacterCards(
                    player, player.Character.CardPool, combat.CardMultiplayerConstraint, out var cards))
            {
                _completedChecks.Add("IronRemainingHealing:JackpotPool:" + string.Join(',', cards
                    .Where(card => card.EnergyCost is { Canonical: 0, CostsX: false })
                    .Select(card => card.Id.Entry)));
                foreach (CardModel model in cards.Where(card => card.EnergyCost is { Canonical: 0, CostsX: false })
                    .Append(ModelDb.Card<Jackpot>()).Append(ModelDb.Card<GiantRock>()))
                {
                    CombatPredictionSimulator child = parent.Fork();
                    child.AddGeneratedCardToCombat(PredictedCard.Create(model, player), PileType.Hand, player,
                        resultKind: CardGenerationResultKind.Fixed);
                    if (StrategicHpRecoveryBound.RemainingHealingUpperBound(child, player, 6) != 6)
                        throw new InvalidOperationException($"已审查零费生成闭包拒绝了 {model.Id}。");
                }
                _completedChecks.Add("IronRemainingHealing:AllNativeZeroCostPool:RepeatableJackpot:GiantRockClosure");
            }
            else
                throw new InvalidOperationException("原生铁甲候选池快照未建立。");
            if (DescribeContinuationContractState(parent, root, player) != parentBefore
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException("铁甲治疗界限检查改变了父分支或原生状态。");
        }

        var originalCards = player.PlayerCombatState!.Hand.Cards.ToDictionary(card => card.Id.Entry);
        CombatPredictionSimulator prediction = parent.Fork();
        var predictedCombat = (SimulatedCombatState)prediction.State.CombatState;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        foreach (string id in new[] { "DEFEND_IRONCLAD", "INFERNAL_BLADE", "JACK_OF_ALL_TRADES", "JACKPOT" })
        {
            CardModel nativeCard = originalCards[id];
            var target = nativeCard is Jackpot ? live.Enemies.First(enemy => !enemy.IsDead) : null;
            using (SimulationNotificationIsolation.Enter())
            {
                using (predictedCombat.BeginCardExecutionScope(new ForkableSet<uint>()))
                    prediction.ManualPlay(prediction.State.FindCard(nativeCard)!, target, out _);
                if (!CombatBeamSolver.SettleReplayActionBoundary(prediction, predictedCombat)
                    || StrategicHpRecoveryBound.RemainingHealingUpperBound(prediction, player, 6) != 6
                    || StrategicHpRecoveryBound.RemainingHealingUpperBound(prediction.Fork(), player, 6) != 6)
                    throw new InvalidOperationException($"{id}破坏治疗闭包或Fork。");
            }
            string expected = ContinuationStamp.CapturePredicted(player, prediction, root.StartTurnNumber,
                root.Forecast, root.StartTurnNumber).StateText;
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played
                    && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
                () =>
                {
                    if (!nativeCard.TryManualPlay(target))
                        throw new InvalidOperationException($"原生牌无法执行：{id}。");
                }, deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            if (ContinuationStamp.CaptureLive(live).StateText != expected
                || DescribeContinuationContractState(parent, root, player) != parentBefore)
                throw new InvalidOperationException($"{id}完整原生状态或父分支隔离不一致。");
        }
        await InjectCardAsync(live, player, new() { CardId = "FEED", Pile = "Exhaust" });
        CombatRootSnapshot unknownRoot = CombatRootSnapshot.Capture(live);
        if (unknownRoot.CanCertifyRemainingHealing
            || StrategicHpRecoveryBound.RemainingHealingUpperBound(unknownRoot.ForkSimulator(), player, 6) != int.MaxValue
            || root.InitialRemainingHealingUpperBound != 0
            || DescribeContinuationContractState(parent, root, player) != parentBefore)
            throw new InvalidOperationException("未知消耗区治疗牌未永久禁用证明，或旧根发生变化。");
        _completedChecks.Add("IronGenerationHealing:ActiveInfernalBlade:ActiveJackOfAllTrades:ActiveJackpot:HealingCard:UnknownExhaustRoot:Regen:WrongOwner:UnknownAffliction:NativePlays:FullState:Fork:ParentLiveRngIsolation");
    }
}
