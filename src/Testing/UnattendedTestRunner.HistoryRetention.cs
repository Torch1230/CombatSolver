using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertHistoryRetentionBoundaries(CombatState combat, Player player, CardModel liveCard)
    {
        AssertHistoryActionIdentityAndFork(combat, player);
        AssertHistoryDeferredSnapshots(combat, player);

        // Keep only the persistent history, as a retained descendant does after dropping its
        // parent snapshot. This assertion runs before search; its forced GC is not a benchmark.
        (CombatPredictionHistory history, WeakReference<PredictedCard> wrapper,
            WeakReference<SimulatedCombatState> owner) = CreateRetainedHistoryProbe(combat, player, liveCard);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        if (wrapper.TryGetTarget(out _) || owner.TryGetTarget(out _))
            throw new InvalidOperationException("持久出牌/伤害历史仍通过卡牌 wrapper 保留祖先模拟分支。");
        if (history.OfType<CombatPredictionCardPlayFinishedEntry>().Count() != 1
            || history.OfType<CombatPredictionDamageReceivedEntry>().Single().CardSource?.Original != liveCard)
        {
            throw new InvalidOperationException("释放祖先分支后，出牌或伤害来源历史丢失。");
        }
        GC.KeepAlive(history);
    }

    private static void AssertHistoryActionIdentityAndFork(CombatState combat, Player player)
    {
        SimulatedCombatState state = new(combat);
        CombatPredictionSimulator simulator = new(state);
        PredictedCard card = PredictedCard.Create(CanonicalModels.Card<DefendIronclad>(), player);
        simulator.AddToPile(card, PileType.Hand);
        CardPlay firstPlay = CreateHistoryProbePlay(card, player, playIndex: 0, playCount: 2);
        CardPlay replay = CreateHistoryProbePlay(card, player, playIndex: 1, playCount: 2);
        PredictionTraceFrame frame;
        using (simulator.PushActionSource(card.Original, PredictionActionKind.CardPlay))
        {
            frame = simulator.CurrentFrame!;
            simulator.History.CardPlayStarted(card, firstPlay);
            simulator.History.CardPlayFinished(card, firstPlay, wasEthereal: true);
            simulator.History.CardPlayStarted(card, replay);
            simulator.History.CardPlayFinished(card, replay, wasEthereal: true);
        }
        CombatPredictionCardPlayStartedEntry started = simulator.History
            .OfType<CombatPredictionCardPlayStartedEntry>().First();
        CombatPredictionCardPlayFinishedEntry finished = simulator.History
            .OfType<CombatPredictionCardPlayFinishedEntry>().First();
        if (!ReferenceEquals(started.CardPlay, firstPlay)
            || !ReferenceEquals(finished.CardPlay, firstPlay)
            || !ReferenceEquals(firstPlay.Card, card.Preview)
            || !simulator.History.HasCardPlayStartedSince(0, frame)
            || simulator.History.OfType<CombatPredictionCardPlayStartedEntry>().Count() != 2)
        {
            throw new InvalidOperationException("历史快照改变了活动 CardPlay 身份或重放次数。");
        }

        CombatPredictionSimulator fork = simulator.Fork();
        PredictedCard forkCard = fork.State.GetPlayerCombatState(player).FindCard(card.Original)
            ?? throw new InvalidOperationException("历史 Fork 测试找不到子分支卡牌。");
        if (ReferenceEquals(card, forkCard) || !ReferenceEquals(card.Original, forkCard.Original))
            throw new InvalidOperationException("历史 Fork 测试没有建立同一 Original 的独立 wrapper。");
        int forkStart = fork.History.Entries.Count;
        PredictionTraceFrame siblingFrame;
        using (fork.PushActionSource(forkCard.Original, PredictionActionKind.CardPlay))
        {
            siblingFrame = fork.CurrentFrame!;
            if (fork.History.HasCardPlayStartedSince(0, siblingFrame))
                throw new InvalidOperationException("子分支动作错误匹配了祖先相同 Original 的出牌。");
            using (fork.PushActionSource(forkCard.Original, PredictionActionKind.CardPlay))
            {
                PredictionTraceFrame nestedFrame = fork.CurrentFrame!;
                CardPlay nested = CreateHistoryProbePlay(forkCard, player, isAutoPlay: true);
                fork.History.CardPlayStarted(forkCard, nested);
                if (fork.History.HasCardPlayStartedSince(forkStart, siblingFrame)
                    || !fork.History.HasCardPlayStartedSince(forkStart, nestedFrame))
                {
                    throw new InvalidOperationException("嵌套自动出牌错误匹配了尚未开始的外层动作。");
                }
                fork.History.CardPlayFinished(forkCard, nested, wasEthereal: false);
            }
            fork.History.CardPlayStarted(forkCard, CreateHistoryProbePlay(forkCard, player));
        }
        if (!fork.History.HasCardPlayStartedSince(forkStart, siblingFrame)
            || simulator.History.HasCardPlayStartedSince(0, siblingFrame)
            || fork.History.HasCardPlayStartedSince(forkStart, frame)
            || !ReferenceEquals(fork.History[0], simulator.History[0]))
        {
            throw new InvalidOperationException("出牌开始判定没有隔离动作范围和兄弟分支。");
        }

        SimulatedCombatState forkState = (SimulatedCombatState)fork.State.CombatState;
        forkState.RoundNumber++;
        forkState.AdvancePlayerTurn(player);
        CombatPredictionSimulator nextTurn = fork.Fork();
        if (nextTurn.History.OfType<CombatPredictionCardPlayFinishedEntry>()
                .Count(entry => entry.WasEthereal && entry.CardPlay.Player == player) != 2
            || !ReferenceEquals(nextTurn.History[0], started)
            || state.RoundNumber == forkState.RoundNumber)
        {
            throw new InvalidOperationException("跨回合 Fork 丢失了历史出牌计数或污染了父回合。");
        }
    }

    private static void AssertHistoryDeferredSnapshots(CombatState combat, Player player)
    {
        CombatPredictionSimulator simulator = new(new SimulatedCombatState(combat));
        PredictedCard card = PredictedCard.Create(CanonicalModels.Card<DefendIronclad>(), player);
        simulator.AddToPile(card, PileType.Hand);
        CombatPredictionCardDrawnEntry drawn = simulator.History.CardDrawn(card, fromHandDraw: true);
        CombatPredictionCardGeneratedEntry generated = simulator.History.CardGenerated(
            card, player, CardGenerationResultKind.Contextual);
        AssertUnresolvedHistoryRejectsFork(simulator);
        CardPlay cardPlay = CreateHistoryProbePlay(card, player);
        simulator.History.CardPlayStarted(card, cardPlay);
        CombatPredictionDamageReceivedEntry damage = simulator.History.DamageReceived(
            player.Creature, player.Creature, new DamageResult(player.Creature, ValueProp.Unpowered),
            card, CombatDamageSource.For(CombatDamageSourceKind.Card, card.Preview.Id.Entry));
        card.Upgrade();
        simulator.History.CardDrawResolved(drawn, card);
        AssertUnresolvedHistoryRejectsFork(simulator);
        simulator.History.CardGenerationResolved(generated, card);
        simulator.History.CardPlayFinished(card, cardPlay, wasEthereal: false);

        CombatPredictionSimulator fork = simulator.Fork();
        CombatPredictionCardDrawResolvedEntry resolvedDraw = fork.History
            .GetResolvedEntry<CombatPredictionCardDrawResolvedEntry>(drawn);
        CombatPredictionCardGenerationResolvedEntry resolvedGeneration = fork.History
            .GetResolvedEntry<CombatPredictionCardGenerationResolvedEntry>(generated);
        CombatPredictionCardPlayStartedEntry started = fork.History
            .OfType<CombatPredictionCardPlayStartedEntry>().Single();
        CombatPredictionCardPlayFinishedEntry finished = fork.History
            .OfType<CombatPredictionCardPlayFinishedEntry>().Single();
        if (drawn.Card.UpgradeLevel != 0 || generated.Card.UpgradeLevel != 0
            || started.Card.UpgradeLevel != 0 || finished.Card.UpgradeLevel != 1
            || damage.CardSource?.UpgradeLevel != 0
            || !ReferenceEquals(damage.CardSource?.Original, card.Original)
            || resolvedDraw.Card.UpgradeLevel != 1 || resolvedGeneration.Card.UpgradeLevel != 1
            || !ReferenceEquals(resolvedDraw.OriginalEntry, drawn)
            || !ReferenceEquals(resolvedGeneration.OriginalEntry, generated)
            || !ReferenceEquals(started.CardPlay, finished.CardPlay)
            || cardPlay.Card.CurrentUpgradeLevel != 1)
        {
            throw new InvalidOperationException("延迟事件配对、事件时点快照或原生 CardPlay preview 身份改变。");
        }

        CombatPredictionHistory foreign = new(new PredictionTrace());
        CombatPredictionCardDrawnEntry foreignDraw = foreign.CardDrawn(card, fromHandDraw: false);
        try
        {
            fork.History.GetResolvedEntry<CombatPredictionCardDrawResolvedEntry>(foreignDraw);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("has not been resolved", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("历史 Fork 接受了另一时间线的同序号延迟事件。");
    }

    private static void AssertUnresolvedHistoryRejectsFork(CombatPredictionSimulator simulator)
    {
        try
        {
            simulator.Fork();
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("unresolved deferred entries", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException("未完成的延迟历史事件错误通过了 Fork 边界。");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (CombatPredictionHistory, WeakReference<PredictedCard>, WeakReference<SimulatedCombatState>)
        CreateRetainedHistoryProbe(CombatState combat, Player player, CardModel liveCard)
    {
        SimulatedCombatState state = new(combat);
        CombatPredictionSimulator simulator = new(state);
        PredictedCard card = simulator.State.GetPlayerCombatState(player).FindCard(liveCard)
            ?? throw new InvalidOperationException("历史持有测试找不到根卡牌。");
        CardPlay cardPlay = CreateHistoryProbePlay(card, player);
        using (simulator.PushActionSource(card.Original, PredictionActionKind.CardPlay))
        {
            simulator.History.CardPlayStarted(card, cardPlay);
            simulator.History.DamageReceived(
                player.Creature, player.Creature, new DamageResult(player.Creature, ValueProp.Unpowered),
                card, CombatDamageSource.For(CombatDamageSourceKind.Card, card.Preview.Id.Entry));
            simulator.History.CardPlayFinished(card, cardPlay, wasEthereal: false);
        }
        return (simulator.History.Fork(new PredictionTrace()), new(card), new(state));
    }

    private static CardPlay CreateHistoryProbePlay(
        PredictedCard card,
        Player player,
        int playIndex = 0,
        int playCount = 1,
        bool isAutoPlay = false)
        => new()
        {
            Card = card.MutablePreview,
            Player = player,
            Target = null,
            ResultPile = PileType.Discard,
            Resources = default,
            IsAutoPlay = isAutoPlay,
            PlayIndex = playIndex,
            PlayCount = playCount,
        };
}
