using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // v29 solver-only runs 498a4092aebe4812b7ae9790e19377a6 and
    // 7320b172df194b90889bb986d285e5ae recorded the same 26 actions / five primary choices.
    // Their ACTION records did not serialize today's complete PlanAction identities. This test
    // reconstructs those operational constraints on the imported current root; it is not legacy
    // PlanAction deserialization, a Solve, native deployment evidence, or a performance benchmark.
    // Unrecorded nested / turn-boundary choices are errors, never inferred empty selections.
    private int RunKnownSoulRouteReplay(CombatState combat, Player player,
        List<KnownRoutePrefix>? frozenPrefixes = null)
    {
        (string Id, int Upgrade)[] initialHand =
        [
            ("ROYAL_GAMBLE", 0), ("STRATAGEM", 0), ("MANIFEST_AUTHORITY", 1),
            ("NEOWS_FURY", 0), ("DEFEND_REGENT", 0),
        ];
        if (string.IsNullOrWhiteSpace(_request.RunSnapshotPath)
            || string.IsNullOrWhiteSpace(_request.ReplayStatePath)
            || combat.Players.Count != 1 || combat.Enemies.Count != 1
            || player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play, TurnNumber: 1 } pcs
            || player.Creature.CurrentHp != 98 || player.Creature.MaxHp != 103
            || combat.Enemies[0].CombatId != 1 || combat.Enemies[0].Monster?.Id.Entry != "SOUL_NEXUS"
            || combat.Enemies[0].CurrentHp != 245 || combat.Enemies[0].MaxHp != 254
            || !pcs.Hand.Cards.Select(card => (card.Id.Entry, card.CurrentUpgradeLevel)).SequenceEqual(initialHand))
            throw new InvalidOperationException("已知 Soul 路线要求原始导入的 T1 根；不补牌、不改敌人来迎合旧路线。");

        Creature enemy = combat.Enemies[0];
        MoveStateSnapshot actualBefore = CaptureActual(combat, player, enemy);
        ContinuationStamp liveBefore = ContinuationStamp.CaptureLive(combat);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat,
                includeTurnSetup: false, theftPolicy: null));
        // Empty card IDs denote the three recorded EndTurn actions. All card occurrences are zero.
        (int Turn, string CardId, bool EnemyTarget)[] steps =
        [
            (1, "MANIFEST_AUTHORITY", false), (1, "BOLAS", true), (1, "NEOWS_FURY", true),
            (1, "BOLAS", true), (1, "MANIFEST_AUTHORITY", false), (1, "PREP_TIME", false), (1, "", false),
            (2, "PHOTON_CUT", true), (2, "MANIFEST_AUTHORITY", false), (2, "MASTER_OF_STRATEGY", false),
            (2, "QUASAR", false), (2, "THINKING_AHEAD", false), (2, "KNOW_THY_PLACE", true),
            (2, "SPECTRUM_SHIFT", false), (2, "BOLAS", true), (2, "", false),
            (3, "PANIC_BUTTON", false), (3, "MANIFEST_AUTHORITY", false), (3, "METAMORPHOSIS", false),
            (3, "", false), (4, "GAMMA_BLAST", true), (4, "SUPERMASSIVE", true),
            (4, "PHOTON_CUT", true), (4, "KNOCKOUT_BLOW", true), (4, "HEGEMONY", true),
            (4, "HEIRLOOM_HAMMER", true),
        ];
        // One-based ACTION positions; order and the physical src0/opt0 cursors are constraints.
        Dictionary<int, (PlanChoiceEffect Effect, PileType Pile, (string Id, int Upgrade)[] Cards)> choices = new()
        {
            [3] = (PlanChoiceEffect.MoveToHand, PileType.Discard, [("MANIFEST_AUTHORITY", 1), ("BOLAS", 1)]),
            [8] = (PlanChoiceEffect.MoveToDrawTop, PileType.Hand, [("GENESIS", 0)]),
            [11] = (PlanChoiceEffect.GenerateToHand, PileType.None, [("THINKING_AHEAD", 1)]),
            [12] = (PlanChoiceEffect.MoveToDrawTop, PileType.Hand, [("METAMORPHOSIS", 1)]),
            [23] = (PlanChoiceEffect.MoveToDrawTop, PileType.Hand, [("SALVO", 0)]),
        };
        List<PlanAction> actions = [];
        List<SimulationSnapshot> owned = [];
        int completedPrefixes = 0;
        try
        {
            using (SimulationNotificationIsolation.Enter())
            {
                SimulationSnapshot initial = ReplayKnownCustom(driver, [], null, 0, 0, owned);
                AssertKnownSoulStable(initial, 1, "initial");
                AssertSnapshotEqual(CaptureSimulated(initial.Simulator,
                    (SimulatedCombatState)initial.Simulator.State.CombatState, player, enemy),
                    actualBefore, "KnownSoulRoute", "InitialRoot");

                void AssertRootsUnchanged(string label)
                {
                    AssertKnownSoulStable(initial, 1, $"{label}:original-shadow-root");
                    AssertSnapshotEqual(CaptureSimulated(initial.Simulator,
                        (SimulatedCombatState)initial.Simulator.State.CombatState, player, enemy),
                        actualBefore, "KnownSoulRoute", $"{label}:OriginalShadowRootUnchanged");
                    SimulationSnapshot rootAfter = ReplayKnownCustom(driver, [], null, 0, 0, owned);
                    try
                    {
                        AssertKnownSoulStable(rootAfter, 1, $"{label}:fresh-root");
                        if (rootAfter.StateKey != initial.StateKey)
                            throw new InvalidOperationException($"Soul {label} 回放修改了原始模拟根的状态键。");
                        AssertSnapshotEqual(CaptureSimulated(rootAfter.Simulator,
                            (SimulatedCombatState)rootAfter.Simulator.State.CombatState, player, enemy),
                            actualBefore, "KnownSoulRoute", $"{label}:RootUnchanged");
                    }
                    finally
                    {
                        rootAfter.ReleaseSimulator();
                    }
                    AssertSnapshotEqual(CaptureActual(combat, player, enemy), actualBefore,
                        "KnownSoulRoute", $"{label}:LiveUnchanged");
                    ContinuationStamp liveAfter = ContinuationStamp.CaptureLive(combat);
                    if (liveAfter != liveBefore)
                        throw new InvalidOperationException($"Soul {label} 模拟修改了实战根：" +
                            liveBefore.DescribeFirstDifference(liveAfter));
                }

                SimulationSnapshot parent = initial;
                for (int index = 0; index < steps.Length; index++)
                {
                    EnsureWithinDeadline();
                    int stepNumber = index + 1;
                    (int turn, string cardId, bool enemyTarget) = steps[index];
                    try
                    {
                        AssertKnownSoulStable(parent, turn, $"step:{stepNumber}:parent");
                        if (parent.PlayerDead || parent.AllEnemiesDead || !parent.Simulator.IsInProgress)
                            throw new InvalidOperationException("已经终局，不能把跳过的后缀计为执行成功。");
                        PlanAction action = new(PlanActionKind.EndTurn, turn);
                        if (cardId.Length != 0)
                        {
                            IReadOnlyList<PredictedCard> hand = parent.Simulator.State.GetPlayerCombatState(player).Hand.Cards;
                            PlanAction descriptor = new(PlanActionKind.PlayCard, turn, CardId: cardId, CardOccurrence: 0);
                            PredictedCard card = CombatBeamSolver.FindCardForReplay(hand, descriptor)
                                ?? throw new InvalidOperationException($"当前手牌找不到 {cardId}#0。");
                            string stateKey = CardChoiceSupport.ChoiceCardKey(card);
                            int stateOccurrence = hand.TakeWhile(candidate => !ReferenceEquals(candidate, card))
                                .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == stateKey);
                            action = descriptor with
                            {
                                TargetIndex = enemyTarget ? 0 : -1,
                                TargetCombatId = enemyTarget ? 1u : null,
                                CardStateKey = stateKey,
                                CardStateOccurrence = stateOccurrence,
                                ReplayCount = Math.Max(0, card.Preview.GetEnchantedReplayCount()),
                            };
                        }

                        if (choices.TryGetValue(stepNumber, out var recorded))
                        {
                            SimulationSnapshot probe = ReplayKnownCustom(driver, [action], parent, parent.Turn, index, owned);
                            try
                            {
                                SimulatedCombatState pendingCombat = (SimulatedCombatState)probe.Simulator.State.CombatState;
                                TurnStartChoiceRequest? pending = pendingCombat.PendingTurnStartChoice;
                                string pendingDescription = DescribeKnownSoulPending(pending);
                                if (pending is not { Spec: { } spec }
                                    || probe.BoundaryReason != SearchBoundaryReason.PendingChoice
                                    || !pendingCombat.HasPendingChoice || pending.Timing != PlanChoiceTiming.Action
                                    || pending.Effect != recorded.Effect || pending.SourcePile != recorded.Pile
                                    || spec.Effect != recorded.Effect || spec.SourcePile != recorded.Pile)
                                    throw new InvalidOperationException("已记录的主选择与当前真实请求不符：" + pendingDescription);
                                PlanCardChoice choice;
                                try
                                {
                                    choice = CardChoiceSupport.BuildRequestedChoice(spec,
                                        recorded.Cards.Select(card => card.Id).ToArray()) with
                                    {
                                        SourceId = pending.SourceId,
                                        ContextId = pending.ContextId,
                                        Timing = pending.Timing,
                                    };
                                }
                                catch (InvalidOperationException error)
                                {
                                    throw new InvalidOperationException("当前主选择不能满足旧记录约束：" + pendingDescription, error);
                                }
                                if (choice.Cards.Count != recorded.Cards.Length
                                    || choice.Cards.Where((token, cursor) => token.CardId != recorded.Cards[cursor].Id
                                        || token.UpgradeLevel != recorded.Cards[cursor].Upgrade
                                        || token.SourceOccurrence != 0 || token.OptionOccurrence != 0
                                        || string.IsNullOrEmpty(token.StateKey)).Any())
                                    throw new InvalidOperationException("主选择未绑定记录要求的顺序/升级/src0/opt0：" +
                                        pendingDescription + " bound=" + JsonSerializer.Serialize(choice));
                                // Binding current request identities is not evidence that old logs contained them.
                                action = action with { Choice = choice with { Cards = choice.Cards.ToArray() } };
                            }
                            finally
                            {
                                // A pending transaction is observed only, never forked or directly resumed.
                                probe.ReleaseSimulator();
                            }
                        }

                        actions.Add(action);
                        SimulationSnapshot incremental = ReplayKnownCustom(driver, [action], parent, parent.Turn, index, owned);
                        int expectedTurn = index + 1 < steps.Length ? steps[index + 1].Turn : turn;
                        AssertKnownSoulStable(incremental, expectedTurn, $"step:{stepNumber}:incremental");
                        SimulationSnapshot full = ReplayKnownCustom(driver, actions, null, 0, 0, owned);
                        AssertKnownSoulStable(full, expectedTurn, $"step:{stepNumber}:full");
                        _ = InvokeKnownCustomMethod(driver, "AssertIncrementalEquivalent",
                            [action, actions.ToArray(), incremental, full]);
                        AssertSnapshotEqual(CaptureSimulated(incremental.Simulator,
                                (SimulatedCombatState)incremental.Simulator.State.CombatState, player, enemy),
                            CaptureSimulated(full.Simulator,
                                (SimulatedCombatState)full.Simulator.State.CombatState, player, enemy),
                            "KnownSoulRoute", $"step:{stepNumber}:IncrementalFullStateDiff");
                        if (incremental.ShufflesCrossed != full.ShufflesCrossed
                            || incremental.CumulativePlayerHpLost != full.CumulativePlayerHpLost
                            || incremental.PotionUseCount != full.PotionUseCount)
                            throw new InvalidOperationException("完整/增量回放累计指标不同。");
                        AssertRootsUnchanged($"step:{stepNumber}");
                        frozenPrefixes?.Add(FreezeKnownRoutePrefix(action,
                            CaptureSimulated(incremental.Simulator,
                                (SimulatedCombatState)incremental.Simulator.State.CombatState, player, enemy),
                            incremental));
                        Entry.Logger.Info($"[CombatSolver/Test] KNOWN_SOUL_PREFIX step={stepNumber} " +
                            $"state={incremental.StateKey.First:x16}/{incremental.StateKey.Second:x16} " +
                            $"turn={incremental.Turn} hp={incremental.PlayerHp} enemy_hp={incremental.EnemyHp} " +
                            $"loss={incremental.CumulativePlayerHpLost} shuffles={incremental.ShufflesCrossed} " +
                            $"choice_count={action.GetActionChoicesInExecutionOrder().Count} " +
                            $"action={JsonSerializer.Serialize(action)}");
                        completedPrefixes++;
                        _completedChecks.Add($"KnownSoulRoute:StrictPrefix:{completedPrefixes}/26:RootAndLiveUnchanged");
                        full.ReleaseSimulator();
                        if (!ReferenceEquals(parent, initial))
                            parent.ReleaseSimulator();
                        parent = incremental;
                    }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException($"当前根 Soul 约束重建首失点 step={stepNumber}/26 " +
                            $"turn={turn} action={(cardId.Length == 0 ? "EndTurn" : cardId)}：{error.Message}", error);
                    }
                }

                if (completedPrefixes != 26 || actions.Count != 26
                    || actions.Count(action => action.Choice != null) != 5
                    || actions.Any(action => action.NestedChoices is { Count: > 0 }
                        || action.NestedChoicesBeforePrimary != 0 || action.TurnStartChoices is { Count: > 0 }
                        || action.EndsPlayerTurn)
                    || actions.Count(action => action.Kind == PlanActionKind.EndTurn) != 3
                    || actions.Any(action => action.Kind == PlanActionKind.UsePotion)
                    || parent.PlayerHp != 97 || parent.CumulativePlayerHpLost != 1 || parent.EnemyHp != 0
                    || parent.PlayerDead || !parent.AllEnemiesDead || parent.CombatEndedTurn is not (> 0 and <= 4)
                    || parent.Simulator.IsInProgress || parent.PotionUseCount != 0)
                    throw new InvalidOperationException($"当前根重建 Soul 路线未达到胜利约束：HP={parent.PlayerHp} " +
                        $"loss={parent.CumulativePlayerHpLost} enemy={parent.EnemyHp} turn={parent.CombatEndedTurn} " +
                        $"shuffles={parent.ShufflesCrossed} potions={parent.PotionUseCount}。");
                _completedChecks.Add("KnownSoulRoute:CurrentRootReconstruction:26Actions:5Primary:0ExtraChoices:0Potions:T4OrEarlier:Loss1:HP97");
            }
        }
        finally
        {
            foreach (SimulationSnapshot snapshot in owned)
                if (snapshot.HasSimulator)
                    snapshot.ReleaseSimulator();
            AssertSnapshotEqual(CaptureActual(combat, player, enemy), actualBefore,
                "KnownSoulRoute", "FinalLiveUnchanged");
            ContinuationStamp liveAfter = ContinuationStamp.CaptureLive(combat);
            if (liveAfter != liveBefore)
                throw new InvalidOperationException("Soul 路线模拟修改了实战根：" + liveBefore.DescribeFirstDifference(liveAfter));
        }
        _completedChecks.Add("KnownSoulRoute:EveryStablePrefixRootAndLiveUnchanged:SimulationOnly:NotNativeEvidence");
        return 1;
    }

    private static void AssertKnownSoulStable(SimulationSnapshot snapshot, int expectedTurn, string label)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (snapshot.BoundaryReason == SearchBoundaryReason.PendingChoice || combat.HasPendingChoice)
            throw new InvalidOperationException($"Soul {label} 出现旧记录未覆盖的额外选择；不会猜测为空或补选：" +
                DescribeKnownSoulPending(combat.PendingTurnStartChoice));
        if (snapshot.HasRisk || snapshot.PredictionGaps.Any(gap => !gap.Compensated))
            throw new InvalidOperationException($"Soul {label} 存在未补偿预测风险：" +
                JsonSerializer.Serialize(snapshot.PredictionGaps));
        if (snapshot.BoundaryReason != SearchBoundaryReason.None || snapshot.Turn != expectedTurn
            || combat.PlayerTurnEndRequested)
            throw new InvalidOperationException($"Soul {label} 不在 T{expectedTurn} 稳定边界：" +
                $"{snapshot.BoundaryReason}/T{snapshot.Turn}/end_requested={combat.PlayerTurnEndRequested}。");
        combat.AssertForkable();
    }

    private static string DescribeKnownSoulPending(TurnStartChoiceRequest? pending)
        => pending == null ? "pending=null" : JsonSerializer.Serialize(new
        {
            pending.SourceId,
            pending.ContextId,
            pending.Timing,
            pending.Effect,
            pending.SourcePile,
            pending.Count,
            Spec = pending.Spec is not { } spec ? null : new
            {
                spec.ContextId,
                spec.Effect,
                spec.SourcePile,
                spec.MinCount,
                spec.MaxCount,
                Options = spec.Options.Select(card => new
                {
                    Id = card.Preview.Id.Entry,
                    Upgrade = card.Preview.CurrentUpgradeLevel,
                    StateKey = CardChoiceSupport.ChoiceCardKey(card),
                }).ToArray(),
            },
        });
}
