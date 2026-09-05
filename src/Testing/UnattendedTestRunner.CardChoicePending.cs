using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertCardChoiceResolutionPendingBoundaries(
        CombatState combat,
        Player player)
    {
        SimulatedCombatState parentCombat = new(combat);
        CombatPredictionSimulator parent = new(parentCombat);
        SimPlayerCombatState parentPlayer = parent.State.GetPlayerCombatState(player);
        parent.RemoveFromCombat(parentPlayer.AllCards.ToArray());
        foreach (PowerModel power in parentCombat.EffectivePowers().ToArray())
            parentCombat.SetPowerAmount(power, 0);
        StabilizeForkBoundaryEnemies(parent);

        _ = parentCombat.AddPowerInstance<HellraiserPower>(
            player.Creature,
            1,
            player.Creature);
        _ = parentCombat.AddPowerInstance<DarkEmbracePower>(
            player.Creature,
            1,
            player.Creature);
        PredictedCard outer = PredictedCard.Create(ModelDb.Card<Purity>(), player);
        PredictedCard first = PredictedCard.Create(ModelDb.Card<PommelStrike>(), player);
        PredictedCard second = PredictedCard.Create(ModelDb.Card<DefendDefect>(), player);
        parent.AddGeneratedCardToCombat(
            outer,
            PileType.Play,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        parent.AddGeneratedCardToCombat(
            first,
            PileType.Hand,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        parent.AddGeneratedCardToCombat(
            second,
            PileType.Hand,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        parent.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<SeekerStrike>(), player),
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        parent.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<DefendRegent>(), player),
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);
        parent.AddGeneratedCardToCombat(
            PredictedCard.Create(ModelDb.Card<DefendSilent>(), player),
            PileType.Draw,
            player,
            resultKind: CardGenerationResultKind.Fixed);

        CardChoiceSpec spec = CardChoiceSupport.GetSpec(parent, outer)
            ?? throw new InvalidOperationException("多选穷尽挂起测试没有建立 Purity 选择。");
        PlanCardChoice outerChoice = CardChoiceSupport.BuildRequestedChoice(
            spec,
            [first.Preview.Id.Entry, second.Preview.Id.Entry]);

        CombatPredictionSimulator pendingSimulator = parent.Fork();
        SimulatedCombatState pendingCombat =
            (SimulatedCombatState)pendingSimulator.State.CombatState;
        SimPlayerCombatState pendingPlayer = pendingSimulator.State.GetPlayerCombatState(player);
        PredictedCard pendingOuter = pendingPlayer.PlayPile.Cards.Single(card => card.Preview is Purity);
        PredictedCard pendingFirst = pendingPlayer.Hand.Cards.Single(card => card.Preview is PommelStrike);
        PredictedCard pendingSecond = pendingPlayer.Hand.Cards.Single(card => card.Preview is DefendDefect);
        pendingCombat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
        try
        {
            bool completed = CardChoiceSupport.Apply(
                pendingSimulator,
                pendingCombat,
                pendingOuter,
                outerChoice,
                new HashSet<uint>());
            if (completed)
                throw new InvalidOperationException("Purity 首项触发内层选择时错误报告为已完成。");
            AssertPendingChoice(
                pendingCombat,
                CanonicalModels.Power<HellraiserPower>().Id.Entry,
                PlanChoiceEffect.MoveToHand,
                "Purity 多选穷尽");
            if (pendingFirst.GetPile(pendingSimulator.State)?.Type != PileType.Exhaust
                || pendingSecond.GetPile(pendingSimulator.State)?.Type != PileType.Hand)
            {
                throw new InvalidOperationException("Purity 在首项挂起后仍穷尽了后续选择。");
            }
        }
        finally
        {
            pendingCombat.EndActionChoices();
        }

        CardChoiceReplaySnapshot firstReplay = ReplayResolvedMultiExhaustChoice(
            parent,
            player,
            outerChoice);
        CardChoiceReplaySnapshot secondReplay = ReplayResolvedMultiExhaustChoice(
            parent,
            player,
            outerChoice);
        if (firstReplay != secondReplay)
        {
            throw new InvalidOperationException(
                $"Purity 携带完整内层选择后的整动作重放不确定：" +
                $"first={firstReplay} second={secondReplay}。");
        }
    }

    private static CardChoiceReplaySnapshot ReplayResolvedMultiExhaustChoice(
        CombatPredictionSimulator parent,
        Player player,
        PlanCardChoice outerChoice)
    {
        CombatPredictionSimulator simulator = parent.Fork();
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(player);
        PredictedCard outer = playerState.PlayPile.Cards.Single(card => card.Preview is Purity);
        PredictedCard first = playerState.Hand.Cards.Single(card => card.Preview is PommelStrike);
        PredictedCard second = playerState.Hand.Cards.Single(card => card.Preview is DefendDefect);
        int historyStart = simulator.History.Entries.Count;
        combat.BeginActionChoices(CreateForkBoundaryAutomaticChoiceCursor());
        try
        {
            bool completed = CardChoiceSupport.Apply(
                simulator,
                combat,
                outer,
                outerChoice,
                new HashSet<uint>());
            if (!completed || combat.HasPendingChoice)
                throw new InvalidOperationException("Purity 携带完整内层选择重放后仍处于挂起状态。");
            if (first.GetPile(simulator.State)?.Type != PileType.Exhaust
                || second.GetPile(simulator.State)?.Type != PileType.Exhaust)
            {
                throw new InvalidOperationException("Purity 完整重放没有恰好穷尽两个外层选择。");
            }
        }
        finally
        {
            combat.EndActionChoices();
        }

        int drawStarted = 0;
        int drawResolved = 0;
        int playStarted = 0;
        int playFinished = 0;
        foreach (CombatPredictionHistoryEntry entry in simulator.History.EntriesFrom(historyStart))
        {
            drawStarted += entry is CombatPredictionCardDrawnEntry ? 1 : 0;
            drawResolved += entry is CombatPredictionCardDrawResolvedEntry ? 1 : 0;
            playStarted += entry is CombatPredictionCardPlayStartedEntry ? 1 : 0;
            playFinished += entry is CombatPredictionCardPlayFinishedEntry ? 1 : 0;
        }
        CardChoiceReplaySnapshot snapshot = new(
            PileKey(playerState.Hand),
            PileKey(playerState.DrawPile),
            PileKey(playerState.DiscardPile),
            PileKey(playerState.ExhaustPile),
            simulator.Rng.CombatCardSelection.CaptureState(),
            drawStarted,
            drawResolved,
            playStarted,
            playFinished);
        _ = simulator.Fork();
        return snapshot;
    }

    private static string PileKey(SimCardPile pile)
        => string.Join(',', pile.Cards.Select(card => CardChoiceSupport.ChoiceCardKey(card)));

    private readonly record struct CardChoiceReplaySnapshot(
        string Hand,
        string Draw,
        string Discard,
        string Exhaust,
        PredictionRngState CardSelectionRng,
        int DrawStarted,
        int DrawResolved,
        int PlayStarted,
        int PlayFinished);
}
