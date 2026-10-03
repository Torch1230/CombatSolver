using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertRegentRemainingHealingAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray())
            if (relic is not DivineRight) await RelicCmd.Remove(relic);
        foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray())
            if (power is not ReattachPower) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live, player, new() { CardId = "BUNDLE_OF_JOY", Pile = "Hand" });
        await InjectCardAsync(live, player, new() { CardId = "ENTROPY", Pile = "Hand" });
        await InjectCardAsync(live, player, new()
            { CardId = "DEFEND_REGENT", Pile = "Hand", TreatAsDeckCard = true });
        InjectPotionForTest(player, "REGEN_POTION");
        CardModel nativeDefend = player.PlayerCombatState!.Hand.Cards.Single(card => card is DefendRegent);
        CardModel transformedGrowth = CardFactory.CreateRandomCardForTransform(nativeDefend,
            new[] { ModelDb.Card<GeneticAlgorithm>() }, isInCombat: true,
            new MegaCrit.Sts2.Core.Random.Rng(7739));
        if (nativeDefend.DeckVersion is null || transformedGrowth.DeckVersion is not null)
            throw new InvalidOperationException("原生战斗变牌错误继承了永久牌组身份。");
        SetEnergy(player, 20);
        SetStars(player, 20);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
        if (!root.CanCertifyRemainingHealing || root.InitialRemainingHealingUpperBound != 15)
            throw new InvalidOperationException("储君生成牌治疗闭包未通过初始界限检查。");
        CombatPredictionSimulator parent = root.ForkSimulator();
        string parentBefore = DescribeContinuationContractState(parent, root, player);
        string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        int Bound(CombatPredictionSimulator simulator, int postCombat = 0, bool includePotions = true)
            => StrategicHpRecoveryBound.RemainingHealingUpperBound(simulator, player, postCombat,
                includePotions);
        using (SimulationNotificationIsolation.Enter())
        {
            CardPoolModel[] pools = [ModelDb.CardPool<RegentCardPool>(), ModelDb.CardPool<ColorlessCardPool>(),
                ModelDb.CardPool<IroncladCardPool>(), ModelDb.CardPool<SilentCardPool>(),
                ModelDb.CardPool<NecrobinderCardPool>(), ModelDb.CardPool<DefectCardPool>(),
                ModelDb.CardPool<StatusCardPool>(), ModelDb.CardPool<CurseCardPool>()];
            foreach (CardPoolModel pool in pools)
            {
                if (!StrategicHpRecoveryBound.HasCertifiedNativeNonHealingGenerationPool(pool)
                    || StrategicHpRecoveryBound.HasCertifiedNativeNonHealingGenerationPool(pool.ToMutable()))
                    throw new InvalidOperationException($"原生生成池或可变池门禁错误：{pool.Id}。");
                _completedChecks.Add($"RegentRemainingHealing:NativePool:{pool.Id.Entry}:" +
                    CardFactory.FilterForCombat(pool.AllCards).Count());
            }
            if (Bound(parent, includePotions: false) != 0 || Bound(parent) != 15)
                throw new InvalidOperationException("无药搜索没有排除药水治疗，或普通搜索丢失治疗额度。");
            foreach (CardModel model in new CardModel[] { ModelDb.Card<Debris>(), ModelDb.Card<Wound>(),
                         ModelDb.Card<Clumsy>() })
            {
                CardModel original = model.ToMutable();
                original.Owner = player;
                CardModel[] candidates = CardFactory.GetDefaultTransformationOptions(original,
                    isInCombat: true).ToArray();
                if (candidates.Length == 0 || candidates.Any(card => !ReferenceEquals(card.Pool, original.Pool)))
                    throw new InvalidOperationException("状态或诅咒的原生变牌没有保留原池。");
                foreach (CardModel candidate in candidates)
                {
                    CombatPredictionSimulator child = parent.Fork();
                    child.AddGeneratedCardToCombat(PredictedCard.Create(candidate, player), PileType.Hand,
                        player, resultKind: CardGenerationResultKind.Fixed);
                    if (Bound(child, includePotions: false) != 0)
                        throw new InvalidOperationException($"原生状态/诅咒候选未在已审查集合：{candidate.Id}。");
                }
            }
            CardModel[] excluded = [ModelDb.Card<Alchemize>(), ModelDb.Card<Feed>(), ModelDb.Card<NotYet>()];
            if (CardFactory.FilterForCombat(excluded).Any())
                throw new InvalidOperationException("原生生成过滤没有排除炼药、吞噬或治疗牌。");
            foreach (CardModel model in new CardModel[] { ModelDb.Card<StrikeIronclad>(),
                         ModelDb.Card<StrikeNecrobinder>(), ModelDb.Card<StrikeDefect>() })
            {
                CardModel original = model.ToMutable();
                original.Owner = player;
                if (CardFactory.GetDefaultTransformationOptions(original, isInCombat: true)
                    .Any(card => excluded.Any(healer => healer.Id == card.Id)))
                    throw new InvalidOperationException("外角色攻击牌的原生变牌池可达未审查治疗牌。");
            }
            foreach (var (model, pile) in new (CardModel, PileType)[]
            {
                (ModelDb.Card<Alchemize>(), PileType.Hand), (ModelDb.Card<Feed>(), PileType.Exhaust),
                (ModelDb.Card<NotYet>(), PileType.Draw),
                (ModelDb.Card<ForbiddenGrimoire>(), PileType.Hand),
            })
            {
                CombatPredictionSimulator child = parent.Fork();
                child.AddGeneratedCardToCombat(PredictedCard.Create(model, player), pile, player,
                    resultKind: CardGenerationResultKind.Fixed);
                if (Bound(child) != int.MaxValue)
                    throw new InvalidOperationException($"手动加入的治疗来源被错误认证：{model.Id}/{pile}。");
            }
            CombatPredictionSimulator regen = parent.Fork();
            ((SimulatedCombatState)regen.State.CombatState).SetAmount<RegenPower>(player.Creature, 5);
            if (Bound(regen, 6) != 61 || Bound(parent, 6) != 21
                || Bound(regen, 6, includePotions: false) != 21)
                throw new InvalidOperationException("再生与战后治疗的乐观上界错误。");
            CombatPredictionSimulator wrongOwner = parent.Fork();
            ((SimulatedCombatState)wrongOwner.State.CombatState).SetAmount<ReattachPower>(player.Creature, 25);
            if (Bound(wrongOwner) != int.MaxValue
                || StrategicHpRecoveryBound.CanCertifyRemainingHealingEnvironment(wrongOwner, player))
                throw new InvalidOperationException("玩家重新接合错误使用了敌方自愈例外。");
            CombatPredictionSimulator affliction = parent.Fork();
            PredictedCard unknown = affliction.State.GetPlayerCombatState(player).Hand.Cards
                .First(card => card.Preview.Id.Entry == "DEFEND_REGENT");
            if (affliction.Afflict<Hexed>(unknown, 1) is null || Bound(affliction) != int.MaxValue)
                throw new InvalidOperationException("未知附着效果没有关闭治疗界限。");
            if (DescribeContinuationContractState(parent, root, player) != parentBefore
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException("治疗闭包检查修改了父分支、原生状态或RNG。");
        }

        CombatPredictionSimulator prediction = parent.Fork();
        var combat = (SimulatedCombatState)prediction.State.CombatState;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        foreach (string id in new[] { "BUNDLE_OF_JOY", "ENTROPY" })
        {
            CardModel nativeCard = player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == id);
            using (SimulationNotificationIsolation.Enter())
            {
                PredictedCard card = prediction.State.GetPlayerCombatState(player).Hand.Cards
                    .Single(item => item.Preview.Id.Entry == id);
                using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                    if (!prediction.ManualPlay(card, null, out _))
                        throw new InvalidOperationException($"原生出牌意外挂起：{id}。");
                CombatBeamSolver.SettleReplayActionBoundary(prediction, combat);
                if (Bound(prediction) != 15 || Bound(prediction.Fork()) != 15
                    || Bound(prediction, includePotions: false) != 0)
                    throw new InvalidOperationException($"{id}或Fork破坏生成牌闭包。");
            }
            string expected = ContinuationStamp.CapturePredicted(player, prediction, root.StartTurnNumber,
                root.Forecast, root.StartTurnNumber).StateText;
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played
                    && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
                () =>
                {
                    if (!nativeCard.TryManualPlay(null))
                        throw new InvalidOperationException($"原生牌无法执行：{id}。");
                }, deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            if (ContinuationStamp.CaptureLive(live).StateText != expected)
                throw new InvalidOperationException($"{id}的完整原生状态与预测不一致。");
        }
        if (DescribeContinuationContractState(parent, root, player) != parentBefore
            || root.InitialRemainingHealingUpperBound != 15)
            throw new InvalidOperationException("原生出牌修改了父分支或冻结治疗元数据。");
        await InjectCardAsync(live, player, new()
            { CardId = "GENETIC_ALGORITHM", Pile = "Draw", TreatAsDeckCard = true });
        CombatRootSnapshot growthRoot = CombatRootSnapshot.Capture(live);
        if (growthRoot.CanCertifyRemainingHealing
            || Bound(growthRoot.ForkSimulator(), includePotions: false) != int.MaxValue
            || DescribeContinuationContractState(parent, root, player) != parentBefore)
            throw new InvalidOperationException("永久成长牌未禁用界限，或新实机牌组改变了旧根。");
        _completedChecks.Add("RegentRemainingHealing:EightNativePools:MutablePoolGuard:NativeGenerationFilter:ForeignAttackTransformFilter:StatusCurseNativeCandidates:NativeTransformNoDeckVersion:ManualHealingSources:UnknownExhaust:RegenerationPotionPolicy:ActiveRegenWithoutPotions:WrongOwnerInitialRootGuard:UnknownAffliction:BundleEntropyFullNativePlays:Fork:FullState:ParentLiveRngIsolation:PermanentGrowthGuard:ImmutableRoot");
    }
}
