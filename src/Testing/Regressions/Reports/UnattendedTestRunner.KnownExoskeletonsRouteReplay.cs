using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed record KnownExoskeletonsEnemyState(
        uint CombatId, bool InRoster, bool IsDead, string MoveId, MoveStateSnapshot State);

    private sealed record KnownExoskeletonsPrefix(
        KnownRoutePrefix Prefix, IReadOnlyList<KnownExoskeletonsEnemyState> Enemies, int ShuffleEvents);

    // v9 run 0662449960184e98959c4e3521697333 recorded these 24 ACTION constraints and
    // six primary choices, but not complete PlanAction / NestedChoices identities. The same
    // imported run/replay root's v31 run 22bc13a42f5147aaa1b79748d7349bf8 recorded a real
    // generated Catastrophe candidate (godot-headless.log:21786, final rejection:24895),
    // with the four complete nested choices below after the same three-action prefix.
    // That v31 candidate was not retained and is not proof of v9's selected PlanAction bytes.
    // Reconstruct the v9 constraints plus this explicitly sourced v31 candidate on today's
    // real requests. Other unrecorded choices still fail; no Solve, native or performance proof.
    private int RunKnownExoskeletonsRouteReplay(
        CombatState combat, Player player, List<KnownExoskeletonsPrefix>? freeze = null)
    {
        if (freeze is { Count: > 0 })
            throw new InvalidOperationException("外骨骼虫冻结输出必须为空，不能拼接不同根的路线。");
        List<KnownExoskeletonsPrefix>? frozen = freeze == null ? null : [];
        (string Id, int Upgrade)[] initialHand =
        [
            ("KNOW_THY_PLACE", 0), ("MANIFEST_AUTHORITY", 0), ("SPECTRUM_SHIFT", 0),
            ("METAMORPHOSIS", 0), ("DEFEND_REGENT", 0),
        ];
        Creature[] rootEnemies = combat.Enemies.ToArray();
        if (string.IsNullOrWhiteSpace(_request.RunSnapshotPath)
            || string.IsNullOrWhiteSpace(_request.ReplayStatePath)
            || combat.Players.Count != 1 || rootEnemies.Length != 4
            || player.PlayerCombatState is not { Phase: PlayerTurnPhase.Play, TurnNumber: 1 } pcs
            || player.Creature.CurrentHp != 97 || player.Creature.MaxHp != 103
            || pcs.Energy != 4 || pcs.Stars != 3
            || !rootEnemies.Select(enemy => enemy.CombatId).SequenceEqual(new uint?[] { 1, 2, 3, 4 })
            || rootEnemies.Any(enemy => enemy.Monster?.Id.Entry != "EXOSKELETON")
            || !rootEnemies.Select(enemy => enemy.CurrentHp).SequenceEqual(new[] { 28, 26, 30, 29 })
            || !rootEnemies.Select(enemy => enemy.MaxHp).SequenceEqual(new[] { 28, 26, 30, 29 })
            || !pcs.Hand.Cards.Select(card => (card.Id.Entry, card.CurrentUpgradeLevel)).SequenceEqual(initialHand))
            throw new InvalidOperationException("已知外骨骼虫路线要求原始导入的四敌 T1 根；不补牌、不改敌人迎合旧路线。");

        // StateDiff's single-focus fields and per-model sums are not the whole proof: preserve
        // every original creature identity, compare all four focus snapshots, and retain the
        // complete continuation, state-key, terminal and processed-death checks below.
        MoveStateSnapshot[] actualBefore = rootEnemies.Select(enemy => CaptureActual(combat, player, enemy)).ToArray();
        ContinuationStamp liveBefore = ContinuationStamp.CaptureLive(combat);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat,
                includeTurnSetup: false, theftPolicy: null));
        // An empty ID is EndTurn. Targets use the logged stable CombatId; the current roster
        // index is bound below only as descriptive PlanAction metadata, never copied from v9.
        (int Turn, string CardId, uint? TargetId)[] steps =
        [
            (1, "", null),
            (2, "QUASAR", null), (2, "MANIFEST_AUTHORITY", null), (2, "CATASTROPHE", null),
            (2, "FIGHT_ME", 1), (2, "REAP", 1), (2, "SUNDER", 1), (2, "NEOWS_FURY", 2),
            (2, "SPLASH", null), (2, "PHOTON_CUT", 3), (2, "BANSHEES_CRY", null), (2, "", null),
            (3, "SHOCKWAVE", null), (3, "DEFEND_REGENT", null), (3, "DEFEND_REGENT", null), (3, "", null),
            (4, "SUNDER", 2), (4, "PHOTON_CUT", 4), (4, "FIGHT_ME", 3), (4, "", null),
            (5, "MANIFEST_AUTHORITY", null), (5, "SPLASH", null), (5, "DEBILITATE", 4), (5, "GOLD_AXE", 4),
        ];
        // One-based ACTION positions. The old primary-card order, upgrades and src0/opt0
        // cursors are constraints; current state keys, source and context are bound, not guessed.
        Dictionary<int, (PlanChoiceEffect Effect, PileType Pile, (string Id, int Upgrade)[] Cards)> choices = new()
        {
            [2] = (PlanChoiceEffect.GenerateToHand, PileType.None, [("SPLASH", 0)]),
            [8] = (PlanChoiceEffect.MoveToHand, PileType.Discard, [("REAP", 0), ("SPLASH", 0)]),
            [9] = (PlanChoiceEffect.GenerateToHand, PileType.None, [("BANSHEES_CRY", 0)]),
            [10] = (PlanChoiceEffect.MoveToDrawTop, PileType.Hand, [("SHOCKWAVE", 0)]),
            [18] = (PlanChoiceEffect.MoveToDrawTop, PileType.Hand, [("SPLASH", 0)]),
            [22] = (PlanChoiceEffect.GenerateToHand, PileType.None, [("DEBILITATE", 0)]),
        };
        const string recordedCatastropheStateKey =
            "CATASTROPHE+0|energy=False:2|stars=False:-1|replay=0|exhaust=False|sly=False|retain=False|deck=False|keywords=|vars=Cards=2|-|:0|baselib=-";
        // Complete v31 nested-token identities, not selections inferred from the next cards.
        // All four records have Timing=Action, ContextId="", upgrade/src/opt=0. The first
        // belongs to Catastrophe's auto-played DecisionsDecisions; the next three belong
        // to that card's three auto-plays of Splash. The current pending request must agree.
        (string SourceId, PlanChoiceEffect Effect, PileType Pile, string CardId, string StateKey)[] recordedNested =
        [
            ("CATASTROPHE", PlanChoiceEffect.AutoPlayRepeated, PileType.Hand, "SPLASH",
                "SPLASH+0|energy=False:1|stars=False:-1|replay=0|exhaust=False|sly=False|retain=False|deck=False|keywords=|vars=|-|:0|baselib=-"),
            ("DECISIONS_DECISIONS", PlanChoiceEffect.GenerateToHand, PileType.None, "FIGHT_ME",
                "FIGHT_ME+0|energy=False:2|stars=False:-1|replay=0|exhaust=False|sly=False|retain=False|deck=False|keywords=|vars=Damage=5;EnemyStrength=1;Repeat=2;StrengthPower=3|-|:0|baselib=-"),
            ("DECISIONS_DECISIONS", PlanChoiceEffect.GenerateToHand, PileType.None, "REAP",
                "REAP+0|energy=False:3|stars=False:-1|replay=0|exhaust=False|sly=False|retain=True|deck=False|keywords=Retain|vars=Damage=27|-|:0|baselib=-"),
            ("DECISIONS_DECISIONS", PlanChoiceEffect.GenerateToHand, PileType.None, "SUNDER",
                "SUNDER+0|energy=False:3|stars=False:-1|replay=0|exhaust=False|sly=False|retain=False|deck=False|keywords=|vars=Damage=26;Energy=3|-|:0|baselib=-"),
        ];
        List<PlanAction> actions = [];
        List<SimulationSnapshot> owned = [];
        SimulationSnapshot? initial = null;
        int completedPrefixes = 0;

        void AssertLiveUnchanged(string label)
        {
            for (int enemyIndex = 0; enemyIndex < rootEnemies.Length; enemyIndex++)
                AssertSnapshotEqual(CaptureActual(combat, player, rootEnemies[enemyIndex]), actualBefore[enemyIndex],
                    "KnownExoskeletonsRoute", $"{label}:LiveUnchanged:CombatId={rootEnemies[enemyIndex].CombatId}");
            ContinuationStamp liveAfter = ContinuationStamp.CaptureLive(combat);
            if (liveAfter != liveBefore)
                throw new InvalidOperationException("外骨骼虫路线模拟修改了实战根：" + liveBefore.DescribeFirstDifference(liveAfter));
        }

        void AssertShadowRootUnchanged(SimulationSnapshot snapshot, string label)
        {
            AssertKnownExoskeletonsStable(snapshot, 1, label);
            for (int enemyIndex = 0; enemyIndex < rootEnemies.Length; enemyIndex++)
                AssertSnapshotEqual(CaptureSimulated(snapshot.Simulator,
                        (SimulatedCombatState)snapshot.Simulator.State.CombatState, player, rootEnemies[enemyIndex]),
                    actualBefore[enemyIndex], "KnownExoskeletonsRoute",
                    $"{label}:CombatId={rootEnemies[enemyIndex].CombatId}");
        }

        void AssertRootsUnchanged(string label)
        {
            if (initial == null)
                throw new InvalidOperationException("外骨骼虫回放尚未建立原始影子根。");
            AssertShadowRootUnchanged(initial, $"{label}:OriginalShadowRootUnchanged");
            SimulationSnapshot rootAfter = ReplayKnownCustom(driver, [], null, 0, 0, owned);
            try
            {
                AssertShadowRootUnchanged(rootAfter, $"{label}:FreshRootUnchanged");
                if (rootAfter.StateKey != initial.StateKey)
                    throw new InvalidOperationException($"外骨骼虫 {label} 回放修改了原根状态键。");
            }
            finally { rootAfter.ReleaseSimulator(); }
            AssertLiveUnchanged(label);
        }

        try
        {
            using (SimulationNotificationIsolation.Enter())
            {
                initial = ReplayKnownCustom(driver, [], null, 0, 0, owned);
                AssertShadowRootUnchanged(initial, "InitialRoot");
                SimulationSnapshot parent = initial;
                for (int index = 0; index < steps.Length; index++)
                {
                    EnsureWithinDeadline();
                    int stepNumber = index + 1;
                    (int turn, string cardId, uint? targetId) = steps[index];
                    try
                    {
                        AssertKnownExoskeletonsStable(parent, turn, $"step:{stepNumber}:parent");
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
                            Creature[] currentEnemies = parent.Simulator.State.Enemies.ToArray();
                            int targetIndex = targetId == null ? -1
                                : Array.FindIndex(currentEnemies, enemy => enemy.CombatId == targetId);
                            if (targetId != null && (targetIndex < 0
                                || parent.Simulator.State.GetCreature(currentEnemies[targetIndex]).IsDead))
                                throw new InvalidOperationException($"旧目标 CombatId={targetId} 已不在可用敌方阵容；" +
                                    $"当前=[{string.Join(',', currentEnemies.Select(enemy => enemy.CombatId))}]。");
                            action = descriptor with
                            {
                                TargetIndex = targetIndex,
                                TargetCombatId = targetId,
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
                                string pendingDescription = DescribeKnownExoskeletonsPending(pending);
                                // Manual primary requests have an empty source; a nested source must not
                                // accidentally consume a primary constraint with a matching effect/card ID.
                                if (pending is not { Spec: { } spec }
                                    || probe.BoundaryReason != SearchBoundaryReason.PendingChoice
                                    || !pendingCombat.HasPendingChoice || pending.Timing != PlanChoiceTiming.Action
                                    || pending.SourceId.Length != 0
                                    || pending.Effect != recorded.Effect || pending.SourcePile != recorded.Pile
                                    || spec.Effect != recorded.Effect || spec.SourcePile != recorded.Pile)
                                    throw new InvalidOperationException("已记录的主选择与当前真实请求不符，不能补未记录选择：" +
                                        pendingDescription);
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
                                    throw new InvalidOperationException("主选择未绑定旧记录要求的顺序/升级/src0/opt0：" +
                                        pendingDescription + " bound=" + JsonSerializer.Serialize(choice));
                                action = action with { Choice = choice with { Cards = choice.Cards.ToArray() } };
                            }
                            finally
                            {
                                // Pending transactions are observed and discarded, never forked or resumed.
                                probe.ReleaseSimulator();
                            }
                        }

                        if (stepNumber == 4)
                        {
                            if (action.Kind != PlanActionKind.PlayCard || action.Turn != 2
                                || action.CardId != "CATASTROPHE" || action.CardOccurrence != 0
                                || action.CardStateOccurrence != 0 || action.ReplayCount != 0
                                || action.TargetCombatId != null || action.TargetIndex != -1
                                || action.CardStateKey != recordedCatastropheStateKey || action.Choice != null)
                                throw new InvalidOperationException("当前第4步动作身份不符合已记录的 v31 横祸候选：" +
                                    JsonSerializer.Serialize(action));
                            List<PlanCardChoice> nested = [];
                            for (int nestedIndex = 0; nestedIndex < recordedNested.Length; nestedIndex++)
                            {
                                EnsureWithinDeadline();
                                var constraint = recordedNested[nestedIndex];
                                // Every attempt starts from the unchanged stable step-3 parent, carrying
                                // only the choices bound so far. Never fork or resume the pending probe.
                                SimulationSnapshot probe = ReplayKnownCustom(driver, [action], parent, parent.Turn, index, owned);
                                try
                                {
                                    SimulatedCombatState pendingCombat = (SimulatedCombatState)probe.Simulator.State.CombatState;
                                    TurnStartChoiceRequest? pending = pendingCombat.PendingTurnStartChoice;
                                    string pendingDescription = DescribeKnownExoskeletonsPending(pending);
                                    if (pending is not { Spec: { } spec }
                                        || probe.BoundaryReason != SearchBoundaryReason.PendingChoice
                                        || !pendingCombat.HasPendingChoice
                                        || pending.SourceId != constraint.SourceId || pending.ContextId != ""
                                        || pending.Timing != PlanChoiceTiming.Action
                                        || pending.Effect != constraint.Effect || pending.SourcePile != constraint.Pile
                                        || spec.Effect != constraint.Effect || spec.SourcePile != constraint.Pile)
                                        throw new InvalidOperationException($"第4步嵌套选择 {nestedIndex + 1}/4 与 v31 来源记录不符；" +
                                            "不会改约束或补选：" + pendingDescription);
                                    PlanCardChoice choice;
                                    try
                                    {
                                        choice = CardChoiceSupport.BuildRequestedChoice(spec, [constraint.CardId]) with
                                        {
                                            SourceId = pending.SourceId,
                                            ContextId = pending.ContextId,
                                            Timing = pending.Timing,
                                        };
                                    }
                                    catch (InvalidOperationException error)
                                    {
                                        throw new InvalidOperationException($"第4步嵌套选择 {nestedIndex + 1}/4 找不到 v31 记录的实例：" +
                                            pendingDescription, error);
                                    }
                                    if (choice.Cards.Count != 1 || choice.Cards[0].CardId != constraint.CardId
                                        || choice.Cards[0].UpgradeLevel != 0 || choice.Cards[0].SourceOccurrence != 0
                                        || choice.Cards[0].OptionOccurrence != 0 || choice.Cards[0].StateKey != constraint.StateKey)
                                        throw new InvalidOperationException($"第4步嵌套选择 {nestedIndex + 1}/4 的完整卡牌身份不符合 v31 记录：" +
                                            pendingDescription + " bound=" + JsonSerializer.Serialize(choice));
                                    nested.Add(choice with { Cards = choice.Cards.ToArray() });
                                    action = action with
                                    {
                                        NestedChoices = nested.ToArray(),
                                        NestedChoicesBeforePrimary = nested.Count,
                                    };
                                    Entry.Logger.Info($"[CombatSolver/Test] KNOWN_EXOSKELETONS_V31_NESTED_BOUND step=4 " +
                                        $"nested={nestedIndex + 1}/4 source_run=22bc13a42f5147aaa1b79748d7349bf8 " +
                                        $"choice={JsonSerializer.Serialize(choice)}");
                                }
                                finally { probe.ReleaseSimulator(); }
                            }
                        }

                        actions.Add(action);
                        SimulationSnapshot incremental = ReplayKnownCustom(driver, [action], parent, parent.Turn, index, owned);
                        int expectedTurn = stepNumber < steps.Length ? steps[stepNumber].Turn : turn;
                        AssertKnownExoskeletonsStable(incremental, expectedTurn, $"step:{stepNumber}:incremental");
                        SimulationSnapshot full = ReplayKnownCustom(driver, actions, null, 0, 0, owned);
                        AssertKnownExoskeletonsStable(full, expectedTurn, $"step:{stepNumber}:full");
                        _ = InvokeKnownCustomMethod(driver, "AssertIncrementalEquivalent",
                            [action, actions.ToArray(), incremental, full]);
                        for (int enemyIndex = 0; enemyIndex < rootEnemies.Length; enemyIndex++)
                            AssertSnapshotEqual(CaptureSimulated(incremental.Simulator,
                                    (SimulatedCombatState)incremental.Simulator.State.CombatState, player, rootEnemies[enemyIndex]),
                                CaptureSimulated(full.Simulator,
                                    (SimulatedCombatState)full.Simulator.State.CombatState, player, rootEnemies[enemyIndex]),
                                "KnownExoskeletonsRoute", $"step:{stepNumber}:IncrementalFullState:CombatId={rootEnemies[enemyIndex].CombatId}");
                        AssertKnownExoskeletonsEnemyLedgerEqual(incremental, full, $"step:{stepNumber}");
                        if (incremental.ShufflesCrossed != full.ShufflesCrossed
                            || incremental.Simulator.ShuffleEventCount != full.Simulator.ShuffleEventCount
                            || incremental.CumulativePlayerHpLost != full.CumulativePlayerHpLost
                            || incremental.PotionUseCount != full.PotionUseCount)
                            throw new InvalidOperationException("完整/增量回放累计指标不同。");
                        AssertRootsUnchanged($"step:{stepNumber}");
                        if (frozen != null)
                        {
                            SimulatedCombatState state = (SimulatedCombatState)incremental.Simulator.State.CombatState;
                            KnownExoskeletonsEnemyState[] enemies = rootEnemies.Select(enemy => new KnownExoskeletonsEnemyState(
                                enemy.CombatId ?? throw new InvalidOperationException("冻结敌人缺少原始 CombatId。"),
                                state.ContainsCreature(enemy), incremental.Simulator.State.GetCreature(enemy).IsDead,
                                state.GetPredictedMoveId(enemy), CaptureSimulated(incremental.Simulator, state, player, enemy))).ToArray();
                            frozen.Add(new KnownExoskeletonsPrefix(
                                FreezeKnownRoutePrefix(action, enemies[0].State, incremental),
                                Array.AsReadOnly(enemies), incremental.Simulator.ShuffleEventCount));
                        }
                        Entry.Logger.Info($"[CombatSolver/Test] KNOWN_EXOSKELETONS_PREFIX step={stepNumber} " +
                            $"state={incremental.StateKey.First:x16}/{incremental.StateKey.Second:x16} " +
                            $"turn={incremental.Turn} hp={incremental.PlayerHp} enemy_hp={incremental.EnemyHp} " +
                            $"loss={incremental.CumulativePlayerHpLost} shuffles={incremental.ShufflesCrossed} " +
                            $"choice_count={action.GetActionChoicesInExecutionOrder().Count} " +
                            $"action={JsonSerializer.Serialize(action)}");
                        completedPrefixes++;
                        _completedChecks.Add($"KnownExoskeletonsRoute:StrictPrefix:{completedPrefixes}/24:AllFourEnemies:RootAndLiveUnchanged");
                        full.ReleaseSimulator();
                        if (!ReferenceEquals(parent, initial))
                            parent.ReleaseSimulator();
                        parent = incremental;
                    }
                    catch (InvalidOperationException error)
                    {
                        throw new InvalidOperationException($"当前根外骨骼虫约束重建首失点 step={stepNumber}/24 " +
                            $"turn={turn} action={(cardId.Length == 0 ? "EndTurn" : cardId)} target_combat_id={targetId}：" +
                            error.Message, error);
                    }
                }

                if (completedPrefixes != 24 || actions.Count != 24
                    || actions.Count(action => action.Choice != null) != 6
                    || actions.Where((_, index) => index != 3).Any(action => action.NestedChoices is { Count: > 0 }
                        || action.NestedChoicesBeforePrimary != 0)
                    || actions[3].NestedChoices is not { Count: 4 } || actions[3].NestedChoicesBeforePrimary != 4
                    || actions.Any(action => action.TurnStartChoices is { Count: > 0 }
                        || action.EndsPlayerTurn)
                    || actions.Count(action => action.Kind == PlanActionKind.EndTurn) != 4
                    || actions.Any(action => action.Kind == PlanActionKind.UsePotion)
                    || parent.PlayerHp != 97 || parent.PlayerMaxHp != 103 || parent.CumulativePlayerHpLost != 0
                    || parent.EnemyHp != 0 || parent.PlayerDead || !parent.AllEnemiesDead
                    || parent.CombatEndedTurn is not (> 0 and <= 5)
                    || parent.Simulator.IsInProgress || parent.PotionUseCount != 0
                    || rootEnemies.Any(enemy => !parent.Simulator.State.GetCreature(enemy).IsDead))
                    throw new InvalidOperationException($"当前根重建外骨骼虫路线未达到胜利约束：HP={parent.PlayerHp}/{parent.PlayerMaxHp} " +
                        $"loss={parent.CumulativePlayerHpLost} enemy={parent.EnemyHp} turn={parent.CombatEndedTurn} " +
                        $"shuffles={parent.ShufflesCrossed} potions={parent.PotionUseCount}。");
                _completedChecks.Add("KnownExoskeletonsRoute:CurrentRootV9ConstraintsPlusObservedV31Candidate:24ExecutedActions:6Primary:4RecordedStep4Nested:0UnrecordedChoices:0Potions:T5OrEarlier:Loss0:HP97");
            }
        }
        finally
        {
            try
            {
                if (initial?.HasSimulator == true)
                {
                    using (SimulationNotificationIsolation.Enter())
                        AssertRootsUnchanged("Final");
                }
            }
            finally
            {
                foreach (SimulationSnapshot snapshot in owned)
                    if (snapshot.HasSimulator)
                        snapshot.ReleaseSimulator();
                AssertLiveUnchanged("Final");
            }
        }
        // Publish value-only records only after all 24 steps, terminal and final root guards
        // succeeded. No simulator, SearchNode or partially validated prefix escapes here.
        if (freeze != null)
            freeze.AddRange(frozen!);
        _completedChecks.Add("KnownExoskeletonsRoute:EveryStablePrefixAllEnemyStateAndContinuation:KnownEnemyRosterAndDeathLedger:RootAndLiveUnchanged:SimulationOnly:NoSolve:NotV9SelectedPlanActionBytes:NotNativeOrPerformanceEvidence");
        return 1; // The native combat has not advanced from its imported T1 root.
    }

    private static void AssertKnownExoskeletonsStable(SimulationSnapshot snapshot, int expectedTurn, string label)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (snapshot.BoundaryReason == SearchBoundaryReason.PendingChoice || combat.HasPendingChoice)
            throw new InvalidOperationException($"外骨骼虫 {label} 出现旧记录未覆盖的嵌套/回合选择；不会猜测为空或补选：" +
                DescribeKnownExoskeletonsPending(combat.PendingTurnStartChoice));
        if (snapshot.HasRisk || snapshot.PredictionGaps.Any(gap => !gap.Compensated))
            throw new InvalidOperationException($"外骨骼虫 {label} 存在未补偿预测风险：" + JsonSerializer.Serialize(snapshot.PredictionGaps));
        if (snapshot.BoundaryReason != SearchBoundaryReason.None || snapshot.Turn != expectedTurn
            || combat.PlayerTurnEndRequested)
            throw new InvalidOperationException($"外骨骼虫 {label} 不在 T{expectedTurn} 稳定边界：" +
                $"{snapshot.BoundaryReason}/T{snapshot.Turn}/end_requested={combat.PlayerTurnEndRequested}。");
        combat.AssertForkable();
    }

    private static void AssertKnownExoskeletonsEnemyLedgerEqual(
        SimulationSnapshot incremental, SimulationSnapshot full, string label)
    {
        // The continuation covers active AI state; this also checks the captured current move
        // of removed known enemies. It is not an arbitrary private-AI-field serialization.
        static (uint? Id, bool InRoster, int Hp, int MaxHp, int Block, string MoveId)[] Capture(SimulationSnapshot snapshot)
        {
            SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
            return combat.KnownEnemies.Select(enemy =>
            {
                SimCreatureState state = snapshot.Simulator.State.GetCreature(enemy);
                return (enemy.CombatId, combat.ContainsCreature(enemy), state.CurrentHp, state.MaxHp, state.Block,
                    combat.GetPredictedMoveId(enemy));
            }).ToArray();
        }
        if (!Capture(incremental).SequenceEqual(Capture(full))
            || !incremental.ProcessedEnemyDeaths.SetEquals(full.ProcessedEnemyDeaths))
            throw new InvalidOperationException($"外骨骼虫 {label} 的逐身份已知敌人/活动阵容/死亡账本不一致。");
    }

    private static string DescribeKnownExoskeletonsPending(TurnStartChoiceRequest? pending)
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
                SourceCards = spec.SourceCards.Select(card => new
                {
                    Id = card.Preview.Id.Entry,
                    Upgrade = card.Preview.CurrentUpgradeLevel,
                    StateKey = CardChoiceSupport.ChoiceCardKey(card),
                }).ToArray(),
            },
        });
}
