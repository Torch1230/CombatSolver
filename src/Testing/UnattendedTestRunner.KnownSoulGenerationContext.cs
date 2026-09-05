using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // These five step-eight choices were all observed as real generated step-eleven
    // candidates in v60/v67. Rebinding the recorded constraints below is formal replay,
    // not a claim that today's Search retained or assigned scheduling rights to them.
    private int RunKnownSoulGenerationContext(
        CombatState combat, Player player, bool fullKnownSuffix = false,
        Dictionary<string, IReadOnlyList<KnownRoutePrefix>>? frozenVariants = null)
    {
        if (frozenVariants != null && (!fullKnownSuffix || frozenVariants.Count != 0))
            throw new InvalidOperationException("生成上下文纯值输出只允许完整后缀模式，且输出字典必须为空。");
        Dictionary<string, IReadOnlyList<KnownRoutePrefix>>? completedVariants =
            frozenVariants == null ? null : new(StringComparer.Ordinal);
        List<KnownRoutePrefix> prefixes = [];
        RunKnownSoulRouteReplay(combat, player, prefixes);
        if (prefixes.Count != 26)
            throw new InvalidOperationException("生成上下文缺少全部 26 步已证明的基础路线。");
        string[] recordedTopChoices =
            ["GENESIS", "BOLAS", "DEFEND_REGENT", "DECISIONS_DECISIONS", "HEIRLOOM_HAMMER"];
        var enemy = combat.Enemies.Single();
        MoveStateSnapshot liveBefore = CaptureActual(combat, player, enemy);
        ContinuationStamp stampBefore = ContinuationStamp.CaptureLive(combat);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat,
                includeTurnSetup: false, theftPolicy: null));
        List<SimulationSnapshot> owned = [];
        Dictionary<int, Dictionary<string, string[]>> baselinePilesByStep = [];
        Dictionary<int, StateFingerprint> baselineUnorderedByStep = [];
        Dictionary<int, List<IReadOnlyDictionary<string, string[]>>> orderedPilesByStep = [];
        SimulationSnapshot? initial = null;
        int completed = 0;
        try
        {
            using IDisposable isolation = SimulationNotificationIsolation.Enter();
            initial = ReplayKnownCustom(driver, [], null, 0, 0, owned);
            AssertSnapshotEqual(CaptureSimulated(initial.Simulator,
                    (SimulatedCombatState)initial.Simulator.State.CombatState, player, enemy),
                liveBefore, "KnownSoulGenerationContext", "InitialRoot");
            foreach (string topChoice in recordedTopChoices)
            {
                EnsureWithinDeadline();
                // The same frozen next action tests actual access, not an inferred draw preview.
                // Only step eight is rebound below. All subsequent actions, physical tokens,
                // nested choices and turn-start choices remain the frozen base identities.
                PlanAction[] actions = prefixes.Take(fullKnownSuffix ? 26 : 12)
                    .Select(prefix => prefix.Action).ToArray();
                List<KnownRoutePrefix>? variantPrefixes = completedVariants == null
                    ? null : prefixes.Take(7).ToList();
                int currentStep = 8;
                try
                {
                    SimulationSnapshot parent = ReplayKnownCustom(driver, actions.Take(7).ToArray(),
                        null, 0, 0, owned);
                    AssertKnownRouteAliasSnapshot(parent, prefixes[6], player, enemy, "common-prefix");
                    SimulationSnapshot probe = ReplayKnownCustom(driver,
                        [actions[7] with { Choice = null }], parent, parent.Turn, 7, owned);
                    try
                    {
                        SimulatedCombatState pendingCombat = (SimulatedCombatState)probe.Simulator.State.CombatState;
                        TurnStartChoiceRequest? pending = pendingCombat.PendingTurnStartChoice;
                        if (pending is not { Effect: PlanChoiceEffect.MoveToDrawTop,
                                SourcePile: PileType.Hand, Timing: PlanChoiceTiming.Action, Spec: { } spec }
                            || probe.BoundaryReason != SearchBoundaryReason.PendingChoice
                            || !pendingCombat.HasPendingChoice)
                            throw new InvalidOperationException("生成上下文前置选择没有对应真实置顶请求：" +
                                DescribeKnownSoulPending(pending));
                        PlanCardChoice choice = CardChoiceSupport.BuildRequestedChoice(spec, [topChoice]) with
                        {
                            SourceId = pending.SourceId,
                            ContextId = pending.ContextId,
                            Timing = pending.Timing,
                        };
                        if (choice.Cards is not [var token] || token.CardId != topChoice
                            || token.UpgradeLevel != (topChoice == "BOLAS" ? 1 : 0)
                            || token.SourceOccurrence != 0 || token.OptionOccurrence != 0
                            || string.IsNullOrEmpty(token.StateKey))
                            throw new InvalidOperationException("生成上下文前置选择未匹配已观察的src0/opt0约束。");
                        actions[7] = actions[7] with { Choice = choice };
                    }
                    finally { probe.ReleaseSimulator(); }

                    for (int index = 7; index < actions.Length; index++)
                    {
                        currentStep = index + 1;
                        EnsureWithinDeadline();
                        AssertKnownSoulStable(parent, actions[index].Turn,
                            $"context:{topChoice}:step:{currentStep}:parent");
                        if (parent.PlayerDead || parent.AllEnemiesDead || !parent.Simulator.IsInProgress)
                            throw new InvalidOperationException("变体在冻结动作之前已经终局，不能跳过其余后缀。");
                        SimulationSnapshot next = ReplayKnownCustom(driver, [actions[index]],
                            parent, parent.Turn, index, owned);
                        AssertKnownSoulStable(next, prefixes[index].Turn, $"context:{topChoice}:step:{index + 1}");
                        SimulationSnapshot full = ReplayKnownCustom(driver,
                            actions.Take(index + 1).ToArray(), null, 0, 0, owned);
                        AssertKnownSoulStable(full, prefixes[index].Turn, $"context:{topChoice}:full:{index + 1}");
                        _ = InvokeKnownCustomMethod(driver, "AssertIncrementalEquivalent",
                            [actions[index], actions.Take(index + 1).ToArray(), next, full]);
                        MoveStateSnapshot nextState = CaptureSimulated(next.Simulator,
                            (SimulatedCombatState)next.Simulator.State.CombatState, player, enemy);
                        AssertSnapshotEqual(nextState,
                            CaptureSimulated(full.Simulator,
                                (SimulatedCombatState)full.Simulator.State.CombatState, player, enemy),
                            "KnownSoulGenerationContext", $"{topChoice}:{index + 1}:FullIncremental");
                        if (next.StateKey != full.StateKey || next.ShufflesCrossed != full.ShufflesCrossed
                            || next.Simulator.ShuffleEventCount != full.Simulator.ShuffleEventCount
                            || next.CumulativePlayerHpLost != full.CumulativePlayerHpLost
                            || next.PotionUseCount != full.PotionUseCount || next.PotionStrategicCost != full.PotionStrategicCost
                            || next.PlayerDead != full.PlayerDead || next.AllEnemiesDead != full.AllEnemiesDead
                            || next.TerminalStamp != full.TerminalStamp
                            || !next.ProcessedEnemyDeaths.SetEquals(full.ProcessedEnemyDeaths)
                            || next.Simulator.IsInProgress != full.Simulator.IsInProgress)
                            throw new InvalidOperationException("变体完整/增量回放的状态键、累计指标、死亡账本或终局不同。");
                        if (fullKnownSuffix)
                        {
                            if (next.PlayerDead || next.AllEnemiesDead != (index == actions.Length - 1)
                                || (next.TerminalStamp != null) != (index == actions.Length - 1)
                                || next.Simulator.IsInProgress == (next.PlayerDead || next.AllEnemiesDead))
                                throw new InvalidOperationException("变体在冻结后缀的错误边界结束战斗。");
                            AssertRootsUnchanged($"context:{topChoice}:step:{currentStep}");
                            variantPrefixes?.Add(FreezeKnownRoutePrefix(actions[index], nextState, next));
                            Entry.Logger.Info("[CombatSolver/Test] GENERATION_CONTEXT_SUFFIX_PREFIX " + JsonSerializer.Serialize(new
                            {
                                TopChoice = topChoice, Step = currentStep, Action = actions[index], next.StateKey,
                                next.Turn, next.CumulativePlayerHpLost, next.PlayerHp, next.EnemyHp,
                                next.PotionUseCount, next.PotionStrategicCost, next.ShufflesCrossed, next.TerminalStamp,
                                Scope = "FrozenSuffixReplay:NotSearchRetentionOrNativeEvidence",
                            }));
                        }
                        full.ReleaseSimulator();
                        parent.ReleaseSimulator();
                        parent = next;
                        if (topChoice == recordedTopChoices[0] && (fullKnownSuffix || index >= 10))
                            AssertKnownRouteAliasSnapshot(parent, prefixes[index], player, enemy,
                                $"known-context:{index + 1}");
                        if (index is 10 or 11)
                            ObservePiles(topChoice, index + 1, parent);
                    }
                    if (fullKnownSuffix)
                    {
                        if (actions.Length != 26 || actions.Count(action => action.Choice != null) != 5
                            || actions.Count(action => action.Kind == PlanActionKind.EndTurn) != 3
                            || actions.Any(action => action.Kind == PlanActionKind.UsePotion
                                || action.NestedChoices is { Count: > 0 } || action.NestedChoicesBeforePrimary != 0
                                || action.TurnStartChoices is { Count: > 0 } || action.EndsPlayerTurn)
                            || parent.PlayerHp != 97 || parent.PlayerMaxHp != 103 || parent.CumulativePlayerHpLost != 1
                            || parent.EnemyHp != 0 || parent.PlayerDead || !parent.AllEnemiesDead
                            || parent.Turn != 4 || parent.CombatEndedTurn != 4
                            || parent.TerminalStamp is not { Outcome: CombatTerminalOutcome.Victory, PlayerTurn: 4 }
                            || parent.Simulator.IsInProgress || parent.PotionUseCount != 0
                            || parent.PotionStrategicCost != 0 || variantPrefixes is { Count: not 26 })
                            throw new InvalidOperationException($"变体冻结后缀未达到 26 步/1损/97HP/T4/零药胜利：" +
                                $"turn={parent.CombatEndedTurn} hp={parent.PlayerHp}/{parent.PlayerMaxHp} " +
                                $"loss={parent.CumulativePlayerHpLost} enemy={parent.EnemyHp} potions={parent.PotionUseCount}。");
                        _completedChecks.Add($"KnownSoulGenerationContext:Variant:{topChoice}:26FrozenActions:5Primary:3EndTurns:Loss1:HP97:VictoryT4:0Potions");
                    }
                    parent.ReleaseSimulator();
                    AssertRootsUnchanged($"context:{topChoice}:final");
                    if (completedVariants != null)
                        completedVariants.Add(topChoice, Array.AsReadOnly(variantPrefixes!.ToArray()));
                    completed++;
                }
                catch (InvalidOperationException error)
                {
                    throw new InvalidOperationException($"生成上下文冻结后缀首失点 variant={topChoice} " +
                        $"step={currentStep}/{actions.Length} expected_turn={prefixes[currentStep - 1].Turn} " +
                        $"full_known_suffix={fullKnownSuffix} action={JsonSerializer.Serialize(actions[currentStep - 1])}：" +
                        error.Message, error);
                }
            }
        }
        finally
        {
            try
            {
                if (fullKnownSuffix && initial?.HasSimulator == true)
                {
                    using IDisposable isolation = SimulationNotificationIsolation.Enter();
                    AssertRootsUnchanged("Final");
                }
            }
            finally
            {
                foreach (SimulationSnapshot snapshot in owned)
                    snapshot.ReleaseSimulator();
                AssertLiveUnchanged("Final");
            }
        }
        if (completed != recordedTopChoices.Length)
            throw new InvalidOperationException("生成上下文回放未完成全部已观察选择。");
        _completedChecks.Add("KnownSoulGenerationContext:5RecordedVariants:DistinctOrderedPiles:SameFrozenAccessAction:FullIncremental:RootLiveUnchanged:NoSolve");
        if (fullKnownSuffix)
            _completedChecks.Add("KnownSoulGenerationContext:All5Variants:26FrozenActionsEach:Loss1:HP97:VictoryT4:0Potions:NoRebindingAfterStep8:SimulationOnly");
        // Publish no partial variant or simulator graph: all five routes and the final
        // original-shadow/fresh-root/live guards must succeed before touching the output.
        if (frozenVariants != null)
        {
            if (completedVariants == null || completedVariants.Count != recordedTopChoices.Length)
                throw new InvalidOperationException("生成上下文缺少全部五条完整冻结变体，不能发布部分结果。");
            foreach ((string topChoice, IReadOnlyList<KnownRoutePrefix> variant) in completedVariants)
                frozenVariants.Add(topChoice, variant);
        }
        return 1;

        void AssertLiveUnchanged(string label)
        {
            AssertSnapshotEqual(CaptureActual(combat, player, enemy), liveBefore,
                "KnownSoulGenerationContext", $"{label}:LiveUnchanged");
            ContinuationStamp stampAfter = ContinuationStamp.CaptureLive(combat);
            if (stampAfter != stampBefore)
                throw new InvalidOperationException($"生成上下文 {label} 回放修改了实战根：" +
                    stampBefore.DescribeFirstDifference(stampAfter));
        }

        void AssertRootsUnchanged(string label)
        {
            if (initial == null)
                throw new InvalidOperationException("生成上下文尚未建立原始影子根。");
            AssertKnownSoulStable(initial, 1, $"{label}:original-root");
            AssertSnapshotEqual(CaptureSimulated(initial.Simulator,
                    (SimulatedCombatState)initial.Simulator.State.CombatState, player, enemy),
                liveBefore, "KnownSoulGenerationContext", $"{label}:OriginalShadowRootUnchanged");
            SimulationSnapshot rootAfter = ReplayKnownCustom(driver, [], null, 0, 0, owned);
            try
            {
                AssertKnownSoulStable(rootAfter, 1, $"{label}:fresh-root");
                if (rootAfter.StateKey != initial.StateKey)
                    throw new InvalidOperationException("生成上下文回放修改了原根状态键。");
                AssertSnapshotEqual(CaptureSimulated(rootAfter.Simulator,
                        (SimulatedCombatState)rootAfter.Simulator.State.CombatState, player, enemy),
                    liveBefore, "KnownSoulGenerationContext", $"{label}:RootUnchanged");
            }
            finally { rootAfter.ReleaseSimulator(); }
            AssertLiveUnchanged(label);
        }

        void ObservePiles(string topChoice, int step, SimulationSnapshot snapshot)
        {
            var state = snapshot.Simulator.State.GetPlayerCombatState(player);
            Dictionary<string, string[]> piles = new()
            {
                ["Hand"] = state.Hand.Cards.Select(CardChoiceSupport.ChoiceCardKey).ToArray(),
                ["Draw"] = state.DrawPile.Cards.Select(CardChoiceSupport.ChoiceCardKey).ToArray(),
                ["Discard"] = state.DiscardPile.Cards.Select(CardChoiceSupport.ChoiceCardKey).ToArray(),
                ["Exhaust"] = state.ExhaustPile.Cards.Select(CardChoiceSupport.ChoiceCardKey).ToArray(),
            };
            baselinePilesByStep.TryAdd(step, piles);
            baselineUnorderedByStep.TryAdd(step, snapshot.UnorderedPileKey);
            if (!orderedPilesByStep.TryGetValue(step,
                    out List<IReadOnlyDictionary<string, string[]>>? orderedPiles))
            {
                orderedPiles = [];
                orderedPilesByStep.Add(step, orderedPiles);
            }
            if (snapshot.UnorderedPileKey != baselineUnorderedByStep[step]
                || orderedPiles.Any(previous => piles.All(pair =>
                    pair.Value.SequenceEqual(previous[pair.Key], StringComparer.Ordinal))))
            {
                throw new InvalidOperationException("已观察的生成上下文没有保持相同无序牌堆及各自不同的完整牌堆顺序。");
            }
            orderedPiles.Add(piles);
            Entry.Logger.Info("[CombatSolver/Test] GENERATION_CONTEXT_PILES " + JsonSerializer.Serialize(new
            {
                TopChoice = topChoice, Step = step, snapshot.StateKey, snapshot.UnorderedPileKey,
                SameUnorderedPilesAsKnown = snapshot.UnorderedPileKey == baselineUnorderedByStep[step],
                DifferentOrderedPiles = piles.Where(pair =>
                        !pair.Value.SequenceEqual(baselinePilesByStep[step][pair.Key], StringComparer.Ordinal))
                    .Select(pair => pair.Key).ToArray(),
                Piles = piles,
                Scope = "FormalConstraintReplay:NotSearchRetentionOrNativeEvidence",
            }));
        }
    }
}
