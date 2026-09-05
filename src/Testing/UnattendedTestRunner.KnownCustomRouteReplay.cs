using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // v29 run 59472b049645446e8fdd272022b0b6b3: ACTION + actual deployment records.
    // This reconstructs the recorded operational constraints on today's imported root. The old
    // log did not serialize full PlanAction identities; this is NOT their deserialization, a Solve,
    // native deployment, or a performance benchmark. Each of the three primary choices had a
    // DEPLOY_CHOICE_PLAN count of one (primary + nested), so there are no unrecorded nested choices.
    private int RunKnownCustomRouteReplay(CombatState combat, Player player,
        List<(PlanAction Action, MoveStateSnapshot State)>? nativePrefixes = null,
        List<KnownRoutePrefix>? frozenPrefixes = null,
        bool verifyDeferredFrontier = false)
    {
        string[] initialHand = ["BURNING_PACT", "BODY_SLAM", "POMMEL_STRIKE", "RAGE", "FEEL_NO_PAIN", "BLOODLETTING"];
        if (string.IsNullOrWhiteSpace(_request.RunSnapshotPath)
            || string.IsNullOrWhiteSpace(_request.ReplayStatePath)
            || combat.Players.Count != 1 || combat.Enemies.Count != 1
            || player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play, TurnNumber: 1 } pcs
            || player.Creature.CurrentHp != 4 || player.Creature.MaxHp != 4
            || combat.Enemies[0].CombatId != 1 || combat.Enemies[0].Monster?.Id.Entry != "AEONGLASS"
            || combat.Enemies[0].CurrentHp != 512
            || !pcs.Hand.Cards.Select(card => card.Id.Entry).SequenceEqual(initialHand)
            || pcs.Hand.Cards.Any(card => card.CurrentUpgradeLevel != 1)
            || pcs.AllCards.Count() != initialHand.Length)
            throw new InvalidOperationException("已知路线要求原归一化 Custom 中途根；不补牌、不改敌人来迎合旧路线。");

        Creature enemy = combat.Enemies[0];
        MoveStateSnapshot actualBefore = CaptureActual(combat, player, enemy);
        ContinuationStamp liveBefore = ContinuationStamp.CaptureLive(combat);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SearchPolicySnapshot capturedPolicy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat), capturedPolicy);
        // All occurrences are zero. Targets are absent except for the potion and the two attacks.
        string[] cardIds = ["", "BLOODLETTING", "RAGE", "POMMEL_STRIKE", "RAGE", "BODY_SLAM",
            "BURNING_PACT", "RAGE", "BODY_SLAM", "POMMEL_STRIKE", "BODY_SLAM", "BURNING_PACT",
            "BODY_SLAM", "POMMEL_STRIKE", "BODY_SLAM", "BURNING_PACT", "BODY_SLAM", "POMMEL_STRIKE", "BODY_SLAM"];
        List<PlanAction> actions = [];
        List<SimulationSnapshot> owned = [];
        List<(PlanAction Action, SimulationSnapshot Snapshot)>? deferredPrefixes =
            verifyDeferredFrontier ? [] : null;
        ContinuationStamp? deferredLeafStamp = null;
        int completedPrefixes = 0;
        try
        {
            using (SimulationNotificationIsolation.Enter())
            {
                SimulationSnapshot initial = ReplayKnownCustom(driver, [], null, 0, 0, owned);
                AssertSnapshotEqual(CaptureSimulated(initial.Simulator,
                    (SimulatedCombatState)initial.Simulator.State.CombatState, player, enemy),
                    actualBefore, "KnownCustomRoute", "InitialRoot");
                AssertKnownCustomStable(initial, "initial");
                SimulationSnapshot parent = initial;
                for (int index = 0; index < cardIds.Length; index++)
                {
                    EnsureWithinDeadline();
                    AssertKnownCustomStable(parent, $"parent:{index}");
                    if (parent.PlayerDead || parent.AllEnemiesDead || !parent.Simulator.IsInProgress)
                        throw new InvalidOperationException($"旧路线在第 {index} 步之前已经终局，不能把跳过的后缀计为执行成功。");
                    PlanAction action;
                    if (index == 0)
                    {
                        // Replay itself validates the slot's potion ID, availability and target.
                        action = new PlanAction(PlanActionKind.UsePotion, 1,
                            TargetIndex: 0, TargetCombatId: 1, PotionSlot: 0, PotionId: "FIRE_POTION");
                    }
                    else
                    {
                        IReadOnlyList<PredictedCard> hand = parent.Simulator.State.GetPlayerCombatState(player).Hand.Cards;
                        PlanAction descriptor = new(PlanActionKind.PlayCard, 1,
                            CardId: cardIds[index], CardOccurrence: 0);
                        PredictedCard card = CombatBeamSolver.FindCardForReplay(hand, descriptor)
                            ?? throw new InvalidOperationException($"旧路线第 {index} 步找不到 {cardIds[index]}#0。");
                        if (card.Preview.CurrentUpgradeLevel != 1)
                            throw new InvalidOperationException($"旧路线第 {index} 步并非日志中的升级牌。");
                        string stateKey = CardChoiceSupport.ChoiceCardKey(card);
                        int stateOccurrence = hand.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                            .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == stateKey);
                        bool targeted = cardIds[index] is "POMMEL_STRIKE" or "BODY_SLAM";
                        action = descriptor with
                        {
                            TargetIndex = targeted ? 0 : -1,
                            TargetCombatId = targeted ? 1u : null,
                            CardStateKey = stateKey,
                            CardStateOccurrence = stateOccurrence,
                            ReplayCount = Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                        };
                    }

                    if (index is 6 or 11 or 15)
                    {
                        string chosenId = index == 6 ? "BLOODLETTING" : "WITHER";
                        int upgrade = index == 6 ? 1 : 0;
                        SimulationSnapshot probe = ReplayKnownCustom(driver, [action], parent, 1, index, owned);
                        try
                        {
                            SimulatedCombatState pendingCombat = (SimulatedCombatState)probe.Simulator.State.CombatState;
                            TurnStartChoiceRequest pending = pendingCombat.PendingTurnStartChoice
                                ?? throw new InvalidOperationException($"旧路线第 {index} 步未暴露真实选牌请求。");
                            CardChoiceSpec spec = pending.Spec
                                ?? throw new InvalidOperationException($"旧路线第 {index} 步请求缺少真实候选 spec。");
                            if (probe.BoundaryReason != SearchBoundaryReason.PendingChoice
                                || !pendingCombat.HasPendingChoice
                                || pending.Effect != PlanChoiceEffect.Exhaust || pending.SourcePile != PileType.Hand
                                || pending.Timing != PlanChoiceTiming.Action
                                || spec.Effect != PlanChoiceEffect.Exhaust || spec.SourcePile != PileType.Hand)
                                throw new InvalidOperationException($"旧路线第 {index} 步选择源/效果/阶段与部署记录不符。");
                            // ACTION recorded the outer card/effect and physical choice cursors,
                            // not the internal request SourceId/ContextId. Bind those from the
                            // current request; the manual-choice sink can legally use an empty source.
                            PlanCardChoice choice = CardChoiceSupport.BuildRequestedChoice(spec, [chosenId]) with
                            {
                                SourceId = pending.SourceId,
                                ContextId = pending.ContextId,
                                Timing = pending.Timing,
                            };
                            if (choice.Cards is not [var token] || token.CardId != chosenId
                                || token.UpgradeLevel != upgrade || token.SourceOccurrence != 0
                                || token.OptionOccurrence != 0 || string.IsNullOrEmpty(token.StateKey)
                                || choice.Effect != PlanChoiceEffect.Exhaust || choice.SourcePile != PileType.Hand
                                || choice.Timing != PlanChoiceTiming.Action)
                                throw new InvalidOperationException($"旧路线第 {index} 步未绑定日志要求的唯一 src0/opt0 选择。");
                            action = action with { Choice = choice };
                        }
                        finally
                        {
                            // A pending transaction is only observed, never forked or resumed directly.
                            probe.ReleaseSimulator();
                        }
                    }

                    actions.Add(action);
                    SimulationSnapshot incremental = ReplayKnownCustom(driver, [action], parent, 1, index, owned);
                    SimulationSnapshot full = ReplayKnownCustom(driver, actions, null, 0, 0, owned);
                    AssertKnownCustomStable(incremental, $"incremental:{index}");
                    AssertKnownCustomStable(full, $"full:{index}");
                    _ = InvokeKnownCustomMethod(driver, "AssertIncrementalEquivalent",
                        [action, actions.ToArray(), incremental, full]);
                    if (incremental.ShufflesCrossed != full.ShufflesCrossed
                        || incremental.CumulativePlayerHpLost != full.CumulativePlayerHpLost
                        || incremental.PotionUseCount != full.PotionUseCount)
                        throw new InvalidOperationException($"旧路线第 {index} 步完整/增量回放累计指标不同。");
                    Entry.Logger.Info($"[CombatSolver/Test] KNOWN_CUSTOM_PREFIX index={index} " +
                        $"state={incremental.StateKey.First:x16}/{incremental.StateKey.Second:x16} " +
                        $"hp={incremental.PlayerHp} enemy_hp={incremental.EnemyHp} " +
                        $"loss={incremental.CumulativePlayerHpLost} shuffles={incremental.ShufflesCrossed} " +
                        $"choice_count={action.GetActionChoicesInExecutionOrder().Count} " +
                        $"action={JsonSerializer.Serialize(action)}");
                    completedPrefixes++;
                    _completedChecks.Add($"KnownCustomRoute:StrictPrefix:{completedPrefixes}/19");
                    nativePrefixes?.Add((action, CaptureSimulated(incremental.Simulator,
                        (SimulatedCombatState)incremental.Simulator.State.CombatState, player, enemy)));
                    frozenPrefixes?.Add(FreezeKnownRoutePrefix(action,
                        CaptureSimulated(incremental.Simulator,
                            (SimulatedCombatState)incremental.Simulator.State.CombatState, player, enemy),
                        incremental));
                    deferredPrefixes?.Add((action, incremental));
                    if (verifyDeferredFrontier && index == cardIds.Length - 2)
                        deferredLeafStamp = ContinuationStamp.CapturePredicted(
                            player, incremental.Simulator, incremental.Turn,
                            root.Forecast, root.StartTurnNumber);
                    full.ReleaseSimulator();
                    if (!ReferenceEquals(parent, initial))
                        parent.ReleaseSimulator();
                    parent = incremental;
                }

                if (completedPrefixes != 19 || actions.Count != 19
                    || actions.Count(action => action.Choice != null) != 3
                    || actions.Any(action => action.NestedChoices is { Count: > 0 }
                        || action.NestedChoicesBeforePrimary != 0 || action.TurnStartChoices is { Count: > 0 }
                        || action.EndsPlayerTurn || action.Turn != 1)
                    || actions.Count(action => action.Kind == PlanActionKind.UsePotion) != 1
                    || parent.PlayerHp != 1 || parent.CumulativePlayerHpLost != 3 || parent.EnemyHp != 0
                    || parent.PlayerDead || !parent.AllEnemiesDead || parent.CombatEndedTurn != 1
                    || parent.Simulator.IsInProgress
                    || parent.ShufflesCrossed != 7 || parent.PotionUseCount != 1)
                    throw new InvalidOperationException($"当前根重建旧路线未达到胜利约束：HP={parent.PlayerHp} " +
                        $"loss={parent.CumulativePlayerHpLost} enemy={parent.EnemyHp} turn={parent.CombatEndedTurn} " +
                        $"shuffles={parent.ShufflesCrossed} potions={parent.PotionUseCount}。");

                SimulationSnapshot rootAfter = ReplayKnownCustom(driver, [], null, 0, 0, owned);
                if (rootAfter.StateKey != initial.StateKey)
                    throw new InvalidOperationException("旧路线回放修改了原始模拟根的状态键。");
                AssertSnapshotEqual(CaptureSimulated(rootAfter.Simulator,
                    (SimulatedCombatState)rootAfter.Simulator.State.CombatState, player, enemy),
                    actualBefore, "KnownCustomRoute", "RootUnchanged");
                if (verifyDeferredFrontier)
                {
                    foreach (SimulationSnapshot snapshot in owned)
                        snapshot.ReleaseSimulator();
                    AssertNarrowBeamRecoveryPolicy(root, capturedPolicy);
                    _completedChecks.Add("DeferredFrontier:NarrowRecoveryOnly:SharedBudgetAndCancellationContract");
                    AssertKnownCustomDeferredFrontierReplay(driver, initial,
                        deferredPrefixes!, deferredLeafStamp
                            ?? throw new InvalidOperationException("恢复合同缺少正式非终局前缀状态。"));
                    SimulationSnapshot afterDeferred = ReplayKnownCustom(driver, [], null, 0, 0, owned);
                    if (afterDeferred.StateKey != initial.StateKey)
                        throw new InvalidOperationException("落选恢复合同修改了原始模拟根的状态键。");
                    AssertSnapshotEqual(CaptureSimulated(afterDeferred.Simulator,
                        (SimulatedCombatState)afterDeferred.Simulator.State.CombatState, player, enemy),
                        actualBefore, "KnownCustomDeferredFrontier", "RootUnchanged");
                }
                _completedChecks.Add("KnownCustomRoute:ReconstructedRoute:19Actions:3Primary:0Nested:1Potion:7Shuffles:T1:Loss3:HP1");
            }
        }
        finally
        {
            foreach (SimulationSnapshot snapshot in owned)
                if (snapshot.HasSimulator)
                    snapshot.ReleaseSimulator();
            AssertSnapshotEqual(CaptureActual(combat, player, enemy), actualBefore,
                "KnownCustomRoute", "LiveUnchanged");
            ContinuationStamp liveAfter = ContinuationStamp.CaptureLive(combat);
            if (liveAfter != liveBefore)
                throw new InvalidOperationException("旧路线模拟修改了实战根：" + liveBefore.DescribeFirstDifference(liveAfter));
        }
        _completedChecks.Add("KnownCustomRoute:RootAndLiveUnchanged:SimulationOnly");
        return 1;
    }

    private static void AssertKnownCustomStable(SimulationSnapshot snapshot, string label)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (snapshot.HasRisk || snapshot.PredictionGaps.Any(gap => !gap.Compensated))
            throw new InvalidOperationException($"旧路线 {label} 存在未补偿预测风险：" +
                JsonSerializer.Serialize(snapshot.PredictionGaps));
        if (snapshot.BoundaryReason != SearchBoundaryReason.None || combat.HasPendingChoice
            || snapshot.Turn != 1 || combat.PlayerTurnEndRequested)
            throw new InvalidOperationException($"旧路线 {label} 不在 T1 稳定边界：{snapshot.BoundaryReason}/{snapshot.Turn}。");
        combat.AssertForkable();
    }

    private static SimulationSnapshot ReplayKnownCustom(CombatBeamSolver driver,
        IReadOnlyList<PlanAction> actions, SimulationSnapshot? parent, int startingTurn,
        int priorActionCount, List<SimulationSnapshot> owned)
    {
        SimulationSnapshot snapshot = (SimulationSnapshot)InvokeKnownCustomMethod(driver, "Replay",
            [actions, parent, startingTurn, priorActionCount, null, null])!;
        owned.Add(snapshot);
        return snapshot;
    }

    private static object? InvokeKnownCustomMethod(CombatBeamSolver driver, string name, object?[] arguments)
    {
        MethodInfo method = typeof(CombatBeamSolver).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(CombatBeamSolver).FullName, name);
        try
        {
            object? result = method.Invoke(driver, arguments);
            if (result == null && method.ReturnType != typeof(void))
                throw new InvalidOperationException($"已知路线 {name} 没有返回值。");
            return result;
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}
