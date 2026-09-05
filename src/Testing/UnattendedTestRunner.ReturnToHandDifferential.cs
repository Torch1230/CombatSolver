using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private const string ReturnToHandOrderScenarioId = "return-to-hand-order-v0111";

    private sealed class ReturnToHandComparisonLane(
        string name,
        CombatPredictionSimulator simulator,
        bool forkEachStep)
    {
        public string Name { get; } = name;
        public CombatPredictionSimulator Simulator { get; private set; } = simulator;

        public SimulatedCombatState BeginStep()
        {
            if (forkEachStep)
                Simulator = Simulator.Fork();
            return (SimulatedCombatState)Simulator.State.CombatState;
        }
    }

    // A dedicated, destructive-to-the-fixture boundary test, dispatched only by its ScenarioId.
    // Real TryManualPlay creates native history; real Hook.BeforeHandDraw determines eligibility
    // and listener order. We advance the player-turn boundary without an enemy move, hand draw,
    // or deployment/search, so this does not claim a complete natural game-turn replay.
    private async Task RunReturnToHandOrderDifferentialAsync(CombatState combat, Player player)
    {
        if (combat.Players.Count != 1 || player.Osty != null
            || player.PlayerCombatState?.OrbQueue.Orbs.Count > 0)
            throw new InvalidOperationException("回手边界夹具要求无奥斯提/充能球的单人状态。");
        Creature enemy = combat.Enemies.FirstOrDefault(candidate => candidate.IsAlive)
            ?? throw new InvalidOperationException("回手边界夹具没有存活目标。");
        foreach (RelicModel relic in player.Relics.ToArray())
            await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await SetBlockAsync(player.Creature, 0);
        await SetBlockAsync(enemy, 0);
        await CreatureCmd.SetMaxHp(enemy, Math.Max(256, enemy.MaxHp));
        await CreatureCmd.SetCurrentHp(enemy, 256);
        SetEnergy(player, 6);
        foreach (string cardId in new[] { "BOLAS", "THRUMMING_HATCHET" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = cardId, Pile = "Hand" });
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        CardModel[] fixtureCards = player.PlayerCombatState!.AllCards.ToArray();
        if (fixtureCards.Length != 2 || fixtureCards.Any(card => card is not (Bolas or ThrummingHatchet)))
            throw new InvalidOperationException("回手边界夹具没有建立唯一的两张回手牌。");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        List<ReturnToHandComparisonLane> lanes =
        [
            new("continuous", root.ForkSimulator(), forkEachStep: false),
            new("fork_each_step", root.ForkSimulator(), forkEachStep: true),
        ];
        AssertReturnToHandLanes(combat, player, enemy, lanes, "initial_root");
        await PlayReturningCardAsync(combat, player, enemy, lanes, "BOLAS");
        await PlayReturningCardAsync(combat, player, enemy, lanes, "THRUMMING_HATCHET");
        AssertReturningCardsPlayedThisTurn(combat, fixtureCards);

        // This root begins after both real plays. It must import current-turn eligibility,
        // instead of relying on RecordCardLifecycle having run in this simulator instance.
        lanes.Add(new("mid_turn_root", CombatRootSnapshot.Capture(combat).ForkSimulator(), forkEachStep: true));
        AssertReturnToHandLanes(combat, player, enemy, lanes, "mid_turn_root");
        await CrossReturningHandBoundaryAsync(combat, player, enemy, lanes, ["BOLAS", "THRUMMING_HATCHET"], "first_return");

        await PlayReturningCardAsync(combat, player, enemy, lanes, "THRUMMING_HATCHET");
        await PlayReturningCardAsync(combat, player, enemy, lanes, "BOLAS");
        AssertReturningCardsPlayedThisTurn(combat, fixtureCards);
        await CrossReturningHandBoundaryAsync(combat, player, enemy, lanes, ["THRUMMING_HATCHET", "BOLAS"], "reverse_return");

        // Do not play again. Moving the cards away makes erroneous re-import of the previous
        // turn's already-consumed eligibility observable; a hand-draw would hide this negative case.
        foreach (CardModel card in player.PlayerCombatState!.Hand.Cards.ToArray())
        {
            foreach (ReturnToHandComparisonLane lane in lanes)
            {
                _ = lane.BeginStep();
                lane.Simulator.AddToPile(FindSimulatedHandCard(lane.Simulator, player, card.Id.Entry, 0), PileType.Discard);
            }
            CardPileAddResult moved = await CardPileCmd.Add(card, PileType.Discard);
            if (!moved.success)
                throw new InvalidOperationException("原版拒绝回手负例的牌堆移动。");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            AssertReturnToHandLanes(combat, player, enemy, lanes, "unplayed_to_discard");
        }
        if (CombatManager.Instance.History.CardPlaysFinished.Any(entry =>
                entry.HappenedThisTurn(combat)
                && fixtureCards.Any(card => ReferenceEquals(card, entry.CardPlay.Card))))
            throw new InvalidOperationException("回手负例意外存在当前回合出牌历史。");
        lanes.Add(new("consumed_history_root", CombatRootSnapshot.Capture(combat).ForkSimulator(), forkEachStep: true));
        AssertReturnToHandLanes(combat, player, enemy, lanes, "consumed_history_root");
        await CrossReturningHandBoundaryAsync(combat, player, enemy, lanes, [], "no_repeat_without_play");
    }

    private async Task PlayReturningCardAsync(
        CombatState combat,
        Player player,
        Creature enemy,
        IReadOnlyList<ReturnToHandComparisonLane> lanes,
        string cardId)
    {
        EnsureWithinDeadline();
        foreach (ReturnToHandComparisonLane lane in lanes)
        {
            SimulatedCombatState predictedCombat = lane.BeginStep();
            PredictedCard card = FindSimulatedHandCard(lane.Simulator, player, cardId, 0);
            if (!predictedCombat.CanPlayCard(lane.Simulator, card))
                throw new InvalidOperationException($"模拟回手边界 {lane.Name} 不能打出 {cardId}。");
            PlaySimulatedCard(lane.Simulator, predictedCombat, card, enemy, combat.Enemies);
            predictedCombat.AssertForkable();
        }
        if (!FindActualHandCard(player, cardId, 0).TryManualPlay(enemy))
            throw new InvalidOperationException($"原版回手边界不能打出 {cardId}。");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        AssertReturnToHandLanes(combat, player, enemy, lanes, "play_" + cardId);
    }

    private async Task CrossReturningHandBoundaryAsync(
        CombatState combat,
        Player player,
        Creature enemy,
        IReadOnlyList<ReturnToHandComparisonLane> lanes,
        IReadOnlyList<string> expectedHand,
        string phase)
    {
        EnsureWithinDeadline();
        PlayerCombatState actualPlayer = player.PlayerCombatState
            ?? throw new InvalidOperationException("回手边界没有玩家战斗状态。");
        if (actualPlayer.Phase != PlayerTurnPhase.Play)
            throw new InvalidOperationException("孤立回手边界必须从玩家 Play 阶段进入。");
        foreach (ReturnToHandComparisonLane lane in lanes)
        {
            SimulatedCombatState predictedCombat = lane.BeginStep();
            predictedCombat.CurrentSide = CombatSide.Player;
            predictedCombat.RoundNumber++;
            predictedCombat.AdvancePlayerTurn(player);
        }
        combat.CurrentSide = CombatSide.Player;
        combat.RoundNumber++;
        actualPlayer.IncrementTurnNumber();

        // CombatManager uses this public setter after advancing the turn, before SetupPlayerTurn.
        // Capture a fresh root at that same pre-draw boundary, not after the native hook has run.
        // This transient lane lasts for one boundary only; it must not advance the turn twice.
        actualPlayer.Phase = PlayerTurnPhase.Start;
        try
        {
            CombatRootSnapshot startRoot = CombatRootSnapshot.Capture(combat);
            if (startRoot.PlayerPhase != PlayerTurnPhase.Start
                || startRoot.StartTurnNumber != actualPlayer.TurnNumber)
                throw new InvalidOperationException("抽牌前根没有捕获实际 Start 阶段与当前回合号。");
            ReturnToHandComparisonLane startLane = new(
                "pre_draw_start_root:" + phase,
                startRoot.ForkSimulator(),
                forkEachStep: true);
            _ = startLane.BeginStep();
            List<ReturnToHandComparisonLane> boundaryLanes = [.. lanes, startLane];
            foreach (ReturnToHandComparisonLane lane in boundaryLanes)
            {
                SimulatedCombatState predictedCombat = (SimulatedCombatState)lane.Simulator.State.CombatState;
                if (predictedCombat.GetPlayerTurnNumber(player) != actualPlayer.TurnNumber)
                    throw new InvalidOperationException($"抽牌前 {lane.Name} 的模拟回合号被重复推进或遗漏。");
                if (predictedCombat.PrepareBeforeHandDraw(lane.Simulator, player, new TurnStartChoiceCursor(null)))
                    throw new InvalidOperationException("回手边界产生了未计划的模拟选择。");
                if (!predictedCombat.TriggerSideTurnStart(lane.Simulator, CombatSide.Player, [player.Creature], decrementPlating: true))
                    throw new InvalidOperationException("回手边界的模拟回合历史重置没有完成。");
                predictedCombat.AssertForkable();
            }
            string[] eligibleInListenerOrder = actualPlayer.AllCards
                .Where(card => card is Bolas or ThrummingHatchet)
                .Where(card => CombatManager.Instance.History.CardPlaysFinished.Any(entry =>
                    ReferenceEquals(entry.CardPlay.Card, card) && entry.HappenedLastPlayerTurn(player)))
                .Select(card => card.Id.Entry)
                .ToArray();
            if (!eligibleInListenerOrder.SequenceEqual(expectedHand))
                throw new InvalidOperationException($"原版回手资格/入口顺序不符合夹具 {phase}：{string.Join(',', eligibleInListenerOrder)}。");
            await Hook.BeforeHandDraw(combat, player, new BlockingPlayerChoiceContext());
            await TriggerActualSideTurnStartAsync(combat, CombatSide.Player, player.Creature);
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
            string[] actualHand = actualPlayer.Hand.Cards.Select(card => card.Id.Entry).ToArray();
            if (!actualHand.SequenceEqual(expectedHand))
                throw new InvalidOperationException($"原版回手结果不符合夹具 {phase}：{string.Join(',', actualHand)}。");
            AssertReturnToHandLanes(combat, player, enemy, boundaryLanes, phase);
            _completedChecks.Add("ReturnToHandOrder:PreDrawStartRoot:" + phase);
        }
        finally
        {
            // Restore the fixture's playable surface, including when the new lane exposes a diff.
            // No natural hand draw, AutoPrePlay phase, or enemy turn is claimed by this fixture.
            actualPlayer.Phase = PlayerTurnPhase.Play;
        }
        _completedChecks.Add("ReturnToHandOrder:" + phase);
    }

    private static void AssertReturningCardsPlayedThisTurn(CombatState combat, IReadOnlyList<CardModel> cards)
    {
        foreach (CardModel card in cards)
        {
            if (!CombatManager.Instance.History.CardPlaysFinished.Any(entry =>
                    ReferenceEquals(entry.CardPlay.Card, card) && entry.HappenedThisTurn(combat)))
                throw new InvalidOperationException($"原版没有记录 {card.Id.Entry} 当前回合的完整出牌。");
        }
    }

    private void AssertReturnToHandLanes(
        CombatState combat,
        Player player,
        Creature enemy,
        IReadOnlyList<ReturnToHandComparisonLane> lanes,
        string phase)
    {
        MoveStateSnapshot actual = CaptureActual(combat, player, enemy);
        foreach (ReturnToHandComparisonLane lane in lanes)
        {
            AssertSnapshotEqual(
                CaptureSimulated(lane.Simulator, (SimulatedCombatState)lane.Simulator.State.CombatState, player, enemy),
                actual,
                "ReturnToHandOrder",
                phase + ":" + lane.Name);
        }
    }
}
