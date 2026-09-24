using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Singleton;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static async Task AssertCombatRootSnapshotAsync(
        CombatState combat, Player player, bool requireInactiveLoadoutSummon = false,
        bool? expectOnlyPostCombatHealing = null)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("根快照测试必须从主线程开始。");
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("根快照测试找不到玩家战斗状态。");
        int capturedEnergy = playerState.Energy;
        int capturedActIndex = combat.RunState.CurrentActIndex;
        RoomType? capturedRoomType = combat.RunState.CurrentRoom?.RoomType;
        var capturedMapCoord = combat.RunState.CurrentMapCoord;
        var capturedCardConstraint = combat.RunState.CardMultiplayerConstraint;
        int capturedShuffleCounter = combat.RunState.Rng.Shuffle.Counter();
        AbstractModel[] liveCombatListeners = combat.IterateHookListeners().ToArray();
        RunState concreteRunState = combat.RunState as RunState
            ?? throw new InvalidOperationException("根快照测试要求具体 RunState。");
        if (requireInactiveLoadoutSummon)
        {
            AbstractModel loadoutHook = ModHelper.IterateAllCombatStateSubscribers(combat)
                .SingleOrDefault(subscriber => subscriber.GetType().FullName ==
                    "Loadout.Services.PowerGiver.PowerGiverSummonHook")
                ?? throw new InvalidOperationException("Loadout PowerGiver summon hook was not loaded.");
            if (loadoutHook.GetType().Assembly.GetName().Name != "Loadout"
                || PredictionModHookSubscriberCapture.CaptureLiveLoadoutSummonPowerState(combat) != "empty")
                throw new InvalidOperationException("Loadout PowerGiver summon powers are not inactive.");
        }
        AbstractModel[] liveStandardRunListeners = concreteRunState.Players
            .Where(candidate => candidate.IsActiveForHooks)
            .SelectMany(candidate => candidate.Deck.Cards)
            .SelectMany(card => card.Enchantment == null
                ? [card]
                : new AbstractModel[] { card, card.Enchantment })
            .Where(RunState.Contains)
            .ToArray();
        AbstractModel? loadoutEveryCardFreeHook = liveCombatListeners.SingleOrDefault(listener =>
            listener.GetType().FullName == "Loadout.Services.TildeKey.LoadoutEveryCardFreeCombatHook");
        bool liveEveryCardFree = false;
        if (loadoutEveryCardFreeHook is not null)
        {
            CardModel probe = playerState.AllCards.First(card => !card.IsCanonical);
            liveEveryCardFree = loadoutEveryCardFreeHook.TryModifyEnergyCostInCombatLate(
                probe,
                1m,
                out decimal modifiedCost);
            if (modifiedCost != (liveEveryCardFree ? 0m : 1m))
                throw new InvalidOperationException("Loadout 全卡免费 hook 返回了未知费用语义。");
        }
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        if (expectOnlyPostCombatHealing is { } expectedHealingBound)
        {
            AssertPrimaryIncumbentFiltering();
            if (root.HasOnlyPostCombatHealing != expectedHealingBound)
                throw new InvalidOperationException(
                    $"回血上界根证书不符：expected={expectedHealingBound} actual={root.HasOnlyPostCombatHealing}。");
        }
        if (requireInactiveLoadoutSummon
            && !root.ContinuationStamp.StateText.Contains(";loadout_summon_powers=empty;", StringComparison.Ordinal))
            throw new InvalidOperationException("根快照没有冻结 Loadout 召唤能力的空配置。");

        bool workerCaptureRejected = await Task.Run(() =>
        {
            try
            {
                _ = CombatRootSnapshot.Capture(combat);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("main thread", StringComparison.Ordinal);
            }
        });
        if (!workerCaptureRejected)
            throw new InvalidOperationException("后台线程创建 combat root snapshot 未被拒绝。");
        bool workerLiveConstructorRejected = await Task.Run(() =>
        {
            try
            {
                _ = new SimulatedCombatState(combat);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("main thread", StringComparison.Ordinal);
            }
        });
        if (!workerLiveConstructorRejected)
            throw new InvalidOperationException("后台线程直接构造 live SimulatedCombatState 未被拒绝。");

        try
        {
            playerState.GainEnergy(1);
            CombatPredictionSimulator fork = await Task.Run(root.ForkSimulator);
            SimPlayerCombatState predictedPlayer = fork.State.GetPlayerCombatState(player);
            if (predictedPlayer.Energy != capturedEnergy)
            {
                throw new InvalidOperationException(
                    $"根快照在实机状态变化后回读了能量：captured={capturedEnergy} predicted={predictedPlayer.Energy}。");
            }
            SimulatedCombatState predictedCombat = (SimulatedCombatState)fork.State.CombatState;
            if (requireInactiveLoadoutSummon && !predictedCombat.HasInactiveLoadoutSummonPowers)
                throw new InvalidOperationException("Fork 丢失了 Loadout 召唤能力的空配置。");
            bool capturedEveryCardFree =
                ((ICombatPredictionPlayerCardRules)predictedCombat).AreCardsFree(player);
            if (capturedEveryCardFree != liveEveryCardFree)
                throw new InvalidOperationException("根快照没有保留 Loadout 全卡免费状态。");
            int capturedMaxHandSize = ((ICombatPredictionPlayerLimits)predictedCombat).GetMaxHandSize(player);
            if (capturedMaxHandSize != CardPile.MaxCardsInHand)
            {
                throw new InvalidOperationException(
                    $"基础场景最大手牌应为 {CardPile.MaxCardsInHand}，根捕获为 {capturedMaxHandSize}。");
            }
            IReadOnlyList<AbstractModel> predictedCombatListeners =
                ((ICombatPredictionHookListenerSource)predictedCombat).HookListeners;
            OrbModel[] liveOrbs = liveCombatListeners.OfType<OrbModel>().ToArray();
            OrbModel[] predictedOrbs = predictedCombatListeners.OfType<OrbModel>().ToArray();
            if (liveOrbs.Any(liveOrb => predictedOrbs.Contains(liveOrb))
                || !liveOrbs.Select(orb => orb.GetType()).OrderBy(type => type.FullName)
                    .SequenceEqual(predictedOrbs.Select(orb => orb.GetType()).OrderBy(type => type.FullName)))
            {
                throw new InvalidOperationException("Orb listener 没有从当前分支 OrbQueue 重建。");
            }
            foreach (BadgeModel liveBadge in liveCombatListeners.OfType<BadgeModel>())
            {
                AbstractModel? predictedBadge = predictedCombatListeners
                    .SingleOrDefault(listener => listener.GetType() == liveBadge.GetType());
                if (predictedBadge is null || ReferenceEquals(liveBadge, predictedBadge))
                    throw new InvalidOperationException($"Badge listener {liveBadge.Id.Entry} 没有以根克隆进入预测。");
            }
            if (combat.MultiplayerScalingModel is { } liveMultiplayerScaling
                && (ReferenceEquals(liveMultiplayerScaling, predictedCombat.MultiplayerScalingModel)
                    || !predictedCombat.RootMultiplayerScalingIsDetached))
            {
                throw new InvalidOperationException("多人缩放 listener 仍持有实机 RunState 或 CombatState。");
            }
            if (predictedCombat.Modifiers.Count != combat.Modifiers.Count)
                throw new InvalidOperationException("根快照没有保留自定义规则清单。");
            for (int index = 0; index < combat.Modifiers.Count; index++)
            {
                ModifierModel liveModifier = combat.Modifiers[index];
                ModifierModel predictedModifier = predictedCombat.Modifiers[index];
                if (ReferenceEquals(liveModifier, predictedModifier)
                    || liveModifier.Id != predictedModifier.Id)
                {
                    throw new InvalidOperationException($"自定义规则 {liveModifier.Id.Entry} 没有以根克隆进入预测。");
                }
            }
            if (predictedCombat.CurrentActIndex != capturedActIndex
                || predictedCombat.CurrentRoomType != capturedRoomType
                || predictedCombat.CurrentMapCoord != capturedMapCoord
                || predictedCombat.CardMultiplayerConstraint != capturedCardConstraint
                || fork.Rng.Shuffle.Counter() != capturedShuffleCounter)
            {
                throw new InvalidOperationException("根快照没有保留捕获时的运行级标量或 RNG 状态。");
            }
            if (predictedCombat.RootRunHookListenerCount != liveStandardRunListeners.Length)
            {
                throw new InvalidOperationException(
                    $"运行级监听器前缀数量不一致：captured={predictedCombat.RootRunHookListenerCount} " +
                    $"live={liveStandardRunListeners.Length}。");
            }
            IReadOnlyList<AbstractModel> predictedRunListeners =
                ((ICombatPredictionHookListenerSource)predictedCombat).RunHookListeners;
            for (int index = 0; index < liveStandardRunListeners.Length; index++)
            {
                AbstractModel liveListener = liveStandardRunListeners[index];
                if (liveListener is not (CardModel or EnchantmentModel))
                    continue;
                if (ReferenceEquals(predictedRunListeners[index], liveListener))
                    throw new InvalidOperationException($"运行级监听器 {liveListener.Id.Entry} 仍引用实机牌组模型。");
            }
            PredictedCard firstCard = predictedPlayer.AllCards.First();
            if (!predictedCombat.ContainsCard(firstCard.Original)
                || !predictedCombat.ContainsCard(firstCard.Preview))
            {
                throw new InvalidOperationException("根快照没有保留卡牌 Original/Preview 的战斗注册身份。");
            }
            ContinuationStamp predicted = ContinuationStamp.CapturePredicted(
                player,
                fork,
                root.StartTurnNumber,
                root.Forecast,
                root.StartTurnNumber);
            if (!string.Equals(
                    root.ContinuationStamp.StateText,
                    predicted.StateText,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "根快照 Fork 与捕获状态不一致：" +
                    root.ContinuationStamp.DescribeFirstDifference(predicted));
            }
        }
        finally
        {
            SetEnergy(player, capturedEnergy);
        }

        await AssertSwordSageRootBaselineAsync(combat, player);

        Entry.Logger.Info(
            $"[CombatSolver/Unattended] COMBAT_ROOT_SNAPSHOT_OK " +
            $"turn={root.StartTurnNumber} enemies={root.Enemies.Count} energy={capturedEnergy}");
    }
}
