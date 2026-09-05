using System.Text.Json;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed record KnownRoutePrefix(
        PlanAction Action, MoveStateSnapshot State, StateFingerprint StateKey,
        int Turn, int HpLost, int PotionsUsed)
    {
        public int ShufflesCrossed { get; init; }
        public int PotionStrategicCost { get; init; }
        public CombatTerminalStamp? TerminalStamp { get; init; }
        public bool PlayerDead { get; init; }
        public bool AllEnemiesDead { get; init; }
    }

    private sealed record KnownRouteVariantNeedle(
        IReadOnlyList<KnownRoutePrefix> Prefixes, IReadOnlyList<string> ActionIdentities);

    private readonly record struct KnownRouteVariantStep(string Variant, int Step);

    // The known route is a test-only needle. It is never passed as a fixed search prefix,
    // candidate, score, policy override, or recovery instruction.
    private async Task<int> RunKnownRoutePathTraceAsync(
        CombatState combat,
        Player player,
        IReadOnlyList<KnownRoutePrefix> prefixes,
        string sample,
        string searchStage,
        int? requiredRetentionStep = null,
        bool requirePotionFirstStep = false,
        bool proveRetentionAliases = false,
        IReadOnlyDictionary<string, IReadOnlyList<KnownRoutePrefix>>? frozenVariants = null)
    {
        if (prefixes.Count == 0
            || (requiredRetentionStep is { } step && (step < 1 || step > prefixes.Count)))
            throw new InvalidOperationException("已知路径诊断缺少有效冻结前缀或保留边界。");
        if (requirePotionFirstStep
            && (prefixes[0].Action.Kind != PlanActionKind.UsePotion || prefixes[0].PotionsUsed != 1))
            throw new InvalidOperationException("用药首步诊断必须来自真实回放的一次主动用药。");
        if (proveRetentionAliases && (requiredRetentionStep == null || requiredRetentionStep >= prefixes.Count))
            throw new InvalidOperationException("别名后缀证明要求有效的中途保留边界和非空冻结后缀。");
        Dictionary<string, KnownRouteVariantNeedle>? variants = PrepareKnownRouteVariantNeedles(
            prefixes, frozenVariants, requiredRetentionStep, proveRetentionAliases);
        string checkName = sample + "PathTrace";
        HashSet<StateFingerprint> watched = prefixes.Select(prefix => prefix.StateKey).ToHashSet();
        if (variants != null)
            watched.UnionWith(variants.Values.SelectMany(variant => variant.Prefixes)
                .Select(prefix => prefix.StateKey));
        HashSet<StateFingerprint>? retentionStates = requiredRetentionStep is { } retentionStep
            ? variants == null ? [prefixes[retentionStep - 1].StateKey]
                : variants.Values.Select(variant => variant.Prefixes[retentionStep - 1].StateKey).ToHashSet()
            : null;
        string[] expectedActions = prefixes.Select(prefix => KnownRouteActionIdentity(prefix.Action)).ToArray();
        List<SearchPathObservation> observations = [];
        object observationGate = new();
        int dropped = 0;
        SearchPathObserver observer = new(watched.Contains, observation =>
        {
            lock (observationGate)
            {
                if (observations.Count < 16_384)
                    observations.Add(observation);
                else
                    dropped++;
            }
        }, wantsRetentionPool: retentionStates == null ? null : retentionStates.Contains);
        Creature[] enemies = combat.Enemies.ToArray();
        if (enemies.Length == 0 || (proveRetentionAliases && enemies.Length != 1))
            throw new InvalidOperationException("路径观察要求非空原根敌方阵容；后缀别名回放目前仅支持单敌样本。");
        MoveStateSnapshot[] liveBefore = enemies.Select(enemy => CaptureActual(combat, player, enemy)).ToArray();
        ContinuationStamp stampBefore = ContinuationStamp.CaptureLive(combat);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        MoveStateSnapshot[] rootBefore = CaptureKnownRouteRootStates(root, player, enemies);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverSettingsSnapshot settings = SolverSettings.Capture();
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(settings, combat,
            includeTurnSetup: false, theftPolicy: null);
        SearchDiagnosticsSink original = policy.Diagnostics;
        policy = policy with { Diagnostics = new SearchDiagnosticsSink(original.Info, original.Debug, observer) };
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(
            Math.Max(1, _request.TimeoutSeconds - _stopwatch.Elapsed.TotalSeconds)));
        SetStage(searchStage);
        SolverResult result = await Task.Run(() =>
        {
            Thread thread = Thread.CurrentThread;
            ThreadPriority previous = thread.Priority;
            thread.Priority = ThreadPriority.BelowNormal;
            try
            {
                using IDisposable gc = SearchGcPolicy.EnterLowLatencySearch(settings.EnableNoGcRegion,
                    settings.NoGcRegionBudgetBytes, policy.MemoryPressureSignal, cancellation.Token);
                return CombatSearchCoordinator.Solve(root, names, damage, policy,
                    cancellation.Token, progressCallback: null);
            }
            finally { thread.Priority = previous; }
        }, cancellation.Token);
        _writer.CaptureSolverResult(result);
        Entry.Logger.Info(SolverDiagnostics.DescribeResult(result));
        HashSet<KnownRouteAliasAnchor>? aliasAnchors = null;
        try
        {
            if (proveRetentionAliases)
            {
                if (dropped != 0)
                    throw new InvalidOperationException($"别名证明的路径观察丢弃了 {dropped} 条事件。");
                SetStage(searchStage + "_alias_replay");
                aliasAnchors = ProveKnownRouteAliases(root, names, damage,
                    policy with { Diagnostics = original }, player, enemies[0], prefixes,
                    observations, requiredRetentionStep!.Value, sample);
            }
        }
        finally
        {
            MoveStateSnapshot[] rootAfter = CaptureKnownRouteRootStates(root, player, enemies);
            for (int enemyIndex = 0; enemyIndex < enemies.Length; enemyIndex++)
            {
                string identity = $"CombatId={enemies[enemyIndex].CombatId}";
                AssertSnapshotEqual(rootAfter[enemyIndex], rootBefore[enemyIndex],
                    checkName, "RootUnchanged:" + identity);
                AssertSnapshotEqual(CaptureActual(combat, player, enemies[enemyIndex]), liveBefore[enemyIndex],
                    checkName, "LiveUnchanged:" + identity);
            }
            ContinuationStamp stampAfter = ContinuationStamp.CaptureLive(combat);
            if (stampAfter != stampBefore)
                throw new InvalidOperationException("路径观测或别名回放修改了实战根：" + stampBefore.DescribeFirstDifference(stampAfter));
        }

        // All workers have joined; no callback can still append to the value-only collector.
        Dictionary<SearchPathObservation, int[]> exactSteps = new(ReferenceEqualityComparer.Instance);
        Dictionary<SearchPathObservation, KnownRouteVariantStep[]>? variantSteps = variants == null
            ? null : new(ReferenceEqualityComparer.Instance);
        foreach (SearchPathObservation observation in observations)
        {
            int[] stateSteps = Enumerable.Range(0, prefixes.Count)
                .Where(index => prefixes[index].StateKey == observation.StateKey).ToArray();
            string[] observedActions = observation.Actions.Select(KnownRouteActionIdentity).ToArray();
            int[] exact = stateSteps.Where(index => observation.ActionCount == index + 1
                && observation.Turn == prefixes[index].Turn
                && observation.CumulativePlayerHpLost == prefixes[index].HpLost
                && observation.PotionCount == prefixes[index].PotionsUsed
                && observation.RootTurnSetupChoices.Count == 0
                && (index == 0 || observation.ParentStateKey == prefixes[index - 1].StateKey)
                && observedActions.SequenceEqual(expectedActions.Take(index + 1))).ToArray();
            exactSteps.Add(observation, exact);
            Entry.Logger.Info("[CombatSolver/Test] PATH_TRACE_EVENT " + JsonSerializer.Serialize(new
            {
                Sample = sample, StateSteps = stateSteps.Select(index => index + 1).ToArray(),
                ExactSteps = exact.Select(index => index + 1).ToArray(),
                Stage = observation.Stage.ToString(),
                Observation = observation,
            }));
            if (variants != null)
            {
                KnownRouteVariantStep[] stateMatches = variants.SelectMany(pair =>
                    Enumerable.Range(0, pair.Value.Prefixes.Count)
                        .Where(index => pair.Value.Prefixes[index].StateKey == observation.StateKey)
                        .Select(index => new KnownRouteVariantStep(pair.Key, index + 1))).ToArray();
                KnownRouteVariantStep[] exactMatches = stateMatches.Where(match =>
                    MatchesKnownRouteVariant(observation, observedActions, variants[match.Variant], match.Step)).ToArray();
                variantSteps!.Add(observation, exactMatches);
                Entry.Logger.Info("[CombatSolver/Test] PATH_TRACE_VARIANT_EVENT " + JsonSerializer.Serialize(new
                {
                    Sample = sample, StateMatches = stateMatches, ExactMatches = exactMatches,
                    Stage = observation.Stage.ToString(), Observation = observation,
                }));
            }
        }
        foreach (IGrouping<Guid, SearchPathObservation> solver in observations.GroupBy(observation => observation.SolverId))
        {
            foreach (int index in Enumerable.Range(0, prefixes.Count))
            {
                SearchPathObservation[] exact = solver.Where(item => exactSteps[item].Contains(index)).ToArray();
                Entry.Logger.Info("[CombatSolver/Test] PATH_TRACE_SUMMARY " + JsonSerializer.Serialize(new
                {
                    Sample = sample, SolverId = solver.Key, Step = index + 1,
                    Phase = solver.First().Phase.ToString(), solver.First().BeamWidth,
                    Stages = exact.GroupBy(item => new { Stage = item.Stage.ToString(), item.Reason })
                        .Select(group => new { group.Key.Stage, group.Key.Reason, Count = group.Count() }).ToArray(),
                    StateOnly = solver.Count(item => item.StateKey == prefixes[index].StateKey
                        && !exactSteps[item].Contains(index)),
                }));
            }
        }
        if (dropped != 0)
            throw new InvalidOperationException($"路径观察容量不足，丢弃了 {dropped} 条事件，不能据此判断首丢点。");
        if (variants != null)
            LogKnownRouteVariantSummaries(sample, variants, observations, variantSteps!);
        bool observedFirstStep = variants != null
            ? observations.SelectMany(item => variantSteps![item].Where(match => match.Step == 1)
                    .Select(match => new { match.Variant, Observation = item }))
                .GroupBy(item => new { item.Variant, item.Observation.SolverId,
                    item.Observation.PolicyLabel, item.Observation.ParentPolicyLabel })
                .Any(group => group.Any(item => item.Observation.Stage == SearchPathObservationStage.Generated)
                    && group.Any(item => item.Observation.Stage == SearchPathObservationStage.Expanded))
            : requirePotionFirstStep
            // Smart's Disabled baseline cannot emit this action. Both events must come from
            // one real solver that actually used the potion, not different solver aliases.
            ? observations.GroupBy(item => item.SolverId).Any(solver =>
                solver.Any(item => item.Stage == SearchPathObservationStage.Generated
                    && item.PotionCount == 1 && exactSteps[item].Contains(0))
                && solver.Any(item => item.Stage == SearchPathObservationStage.Expanded
                    && item.PotionCount == 1 && exactSteps[item].Contains(0)))
            : observations.Any(item => item.Stage == SearchPathObservationStage.Generated && exactSteps[item].Contains(0))
                && observations.Any(item => item.Stage == SearchPathObservationStage.Expanded && exactSteps[item].Contains(0));
        if (!observedFirstStep)
        {
            if (variants != null)
                throw new InvalidOperationException("没有任何同一 solver、变体及完整实测政策标签桶准确生成并展开首步；不能跨桶拼接路径证据。");
            if (requirePotionFirstStep)
                throw new InvalidOperationException("没有任何同一真实用药 solver 准确生成并展开已知用药首步；可能未进入用药层，不能据此声称路线已被剪枝。");
            throw new InvalidOperationException("当前搜索没有观察到已知首动作的准确生成和展开，不能冒充路径追踪成功。");
        }
        if (requiredRetentionStep is { } requiredStep)
        {
            Func<SearchPathObservation, bool>? requiredVariantObservation = null;
            if (variants != null)
            {
                // These are actual observed identities, not policy labels inferred from combat replay.
                var generated = observations.Where(item => item.Stage == SearchPathObservationStage.Generated)
                    .SelectMany(item => variantSteps![item].Where(match => match.Step == requiredStep)
                        .Select(match => (match.Variant, Anchor: KnownRouteAliasAnchorFor(item), item.ParentPolicyLabel)))
                    .ToHashSet();
                requiredVariantObservation = item => variantSteps![item].Any(match => match.Step == requiredStep
                    && generated.Contains((match.Variant, KnownRouteAliasAnchorFor(item), item.ParentPolicyLabel)));
            }
            AssertKnownRouteRetentionPools(observations, exactSteps, requiredStep, aliasAnchors,
                requiredVariantObservation);
        }
        _completedChecks.Add($"{checkName}:{observations.Count}DetachedEvents:NoDrops:FullActionIdentity:LiveUnchanged");
        if (requiredRetentionStep != null)
            _completedChecks.Add($"{checkName}:CompleteOuterRetentionPools:ActualRanksAndReservations");
        _completedChecks.Add($"{checkName}:RootUnchanged");
        if (requirePotionFirstStep)
            _completedChecks.Add($"{checkName}:SamePotionSolver:ExactFirstActionGeneratedAndExpanded");
        if (aliasAnchors != null)
            _completedChecks.Add($"{checkName}:{aliasAnchors.Count}StrictAliasAnchors:CombatSuffixOnly:NotSchedulingHistoryEquivalence");
        if (variants != null)
        {
            _completedChecks.Add($"{checkName}:{variants.Count}FrozenVariants:{prefixes.Count}StepsEach:PerVariantFullActionIdentity:ObservedPolicyBuckets:NotFrozenPolicyLabels");
            _completedChecks.Add($"{checkName}:SameSolverVariantAndPolicy:ExactFirstActionGeneratedAndExpanded");
            _completedChecks.Add($"{checkName}:AnyExactVariantStep{requiredRetentionStep}:GeneratedAndOuterRankedSameObservedPolicy:NotAllRoutesRetained");
        }
        _completedChecks.Add($"{checkName}:DiagnosticOnly:NotQualityOrPerformancePass:NotNativeDeployment");
        return player.PlayerCombatState!.TurnNumber;
    }

    private Dictionary<string, KnownRouteVariantNeedle>? PrepareKnownRouteVariantNeedles(
        IReadOnlyList<KnownRoutePrefix> primary,
        IReadOnlyDictionary<string, IReadOnlyList<KnownRoutePrefix>>? frozenVariants,
        int? requiredRetentionStep, bool proveRetentionAliases)
    {
        if (frozenVariants == null)
            return null;
        if (proveRetentionAliases || primary.Count == 0 || requiredRetentionStep is not { } retentionStep
            || retentionStep < 1 || retentionStep > primary.Count
            || frozenVariants.Count < 2 || frozenVariants.Keys.Any(string.IsNullOrWhiteSpace)
            || frozenVariants.Keys.Distinct(StringComparer.Ordinal).Count() != frozenVariants.Count)
            throw new InvalidOperationException("多变体观察要求至少两条具名完整路线及有效整池边界；不能混用单敌别名后缀证明。");
        Dictionary<string, KnownRouteVariantNeedle> variants = new(StringComparer.Ordinal);
        foreach ((string name, IReadOnlyList<KnownRoutePrefix> route) in frozenVariants.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (route.Count != primary.Count || route.Take(route.Count - 1).Any(prefix =>
                    prefix.PlayerDead || prefix.AllEnemiesDead || prefix.TerminalStamp != null)
                || route[^1].PlayerDead || !route[^1].AllEnemiesDead
                || route[^1].TerminalStamp is not { Outcome: CombatTerminalOutcome.Victory, PlayerTurn: > 0 }
                || route[^1].Turn != route[^1].TerminalStamp!.Value.PlayerTurn)
                throw new InvalidOperationException($"变体 {name} 缺少与主路线等长的完整动作及仅末步胜利的冻结终局。");
            for (int index = 0; index < route.Count
                && KnownRouteActionIdentity(route[index].Action) == KnownRouteActionIdentity(primary[index].Action); index++)
                AssertSamePrefix(route[index], primary[index], $"{name}:common:{index + 1}");
            variants.Add(name, new KnownRouteVariantNeedle(Array.AsReadOnly(route.ToArray()),
                Array.AsReadOnly(route.Select(prefix => KnownRouteActionIdentity(prefix.Action)).ToArray())));
        }
        if (variants.Values.Select(variant => JsonSerializer.Serialize(variant.ActionIdentities))
                .Distinct(StringComparer.Ordinal).Count() != variants.Count)
            throw new InvalidOperationException("变体名称不能把同一完整动作路线重复计为不同证明。");
        string[] primaryActions = primary.Select(prefix => KnownRouteActionIdentity(prefix.Action)).ToArray();
        KnownRouteVariantNeedle? primaryVariant = variants.Values.SingleOrDefault(variant =>
            variant.ActionIdentities.SequenceEqual(primaryActions));
        if (primaryVariant == null)
            throw new InvalidOperationException("完整变体中缺少与默认 primary 完全一致的主动作序列。");
        for (int index = 0; index < primary.Count; index++)
            AssertSamePrefix(primaryVariant.Prefixes[index], primary[index], $"primary:{index + 1}");
        return variants;

        void AssertSamePrefix(KnownRoutePrefix actual, KnownRoutePrefix expected, string label)
        {
            if (KnownRouteActionIdentity(actual.Action) != KnownRouteActionIdentity(expected.Action)
                || actual.StateKey != expected.StateKey || actual.Turn != expected.Turn
                || actual.HpLost != expected.HpLost || actual.PotionsUsed != expected.PotionsUsed
                || actual.PotionStrategicCost != expected.PotionStrategicCost
                || actual.ShufflesCrossed != expected.ShufflesCrossed
                || actual.TerminalStamp != expected.TerminalStamp
                || actual.PlayerDead != expected.PlayerDead || actual.AllEnemiesDead != expected.AllEnemiesDead)
                throw new InvalidOperationException($"变体 {label} 与默认冻结主路线的动作、状态或累计指标不同。");
            AssertSnapshotEqual(actual.State, expected.State, "KnownRouteVariants", label);
        }
    }

    private static bool MatchesKnownRouteVariant(
        SearchPathObservation observation, IReadOnlyList<string> observedActions,
        KnownRouteVariantNeedle variant, int step)
    {
        int index = step - 1;
        KnownRoutePrefix expected = variant.Prefixes[index];
        return observation.StateKey == expected.StateKey && observation.ActionCount == step
            && observation.PolicyLabel.ActionCount == step && observedActions.Count == step
            && observation.Turn == expected.Turn && observation.CumulativePlayerHpLost == expected.HpLost
            && observation.PotionCount == expected.PotionsUsed
            && observation.PotionStrategicCost == expected.PotionStrategicCost
            && observation.ShufflesCrossed == expected.ShufflesCrossed
            && !observation.HasPredictionRisk && observation.BoundaryReason == SearchBoundaryReason.None
            && observation.IsTerminal == (expected.PlayerDead || expected.AllEnemiesDead)
            && observation.RootTurnSetupChoices.Count == 0
            && (index == 0 || observation.ParentStateKey == variant.Prefixes[index - 1].StateKey)
            && observedActions.SequenceEqual(variant.ActionIdentities.Take(step));
    }

    private static void LogKnownRouteVariantSummaries(
        string sample, IReadOnlyDictionary<string, KnownRouteVariantNeedle> variants,
        IReadOnlyList<SearchPathObservation> observations,
        IReadOnlyDictionary<SearchPathObservation, KnownRouteVariantStep[]> variantSteps)
    {
        foreach (IGrouping<Guid, SearchPathObservation> solver in observations.GroupBy(item => item.SolverId))
        foreach ((string variant, KnownRouteVariantNeedle needle) in variants)
        for (int index = 0; index < needle.Prefixes.Count; index++)
        {
            KnownRouteVariantStep match = new(variant, index + 1);
            SearchPathObservation[] exact = solver.Where(item => variantSteps[item].Contains(match)).ToArray();
            Entry.Logger.Info("[CombatSolver/Test] PATH_TRACE_VARIANT_SUMMARY " + JsonSerializer.Serialize(new
            {
                Sample = sample, Variant = variant, Step = index + 1, SolverId = solver.Key,
                Phase = solver.First().Phase.ToString(), solver.First().BeamWidth,
                PolicyBuckets = exact.GroupBy(item => new { item.PolicyLabel, item.ParentPolicyLabel })
                    .Select(group => new
                    {
                        group.Key.PolicyLabel, group.Key.ParentPolicyLabel,
                        Stages = group.GroupBy(item => new { Stage = item.Stage.ToString(), item.Reason })
                            .Select(stage => new { stage.Key.Stage, stage.Key.Reason, Count = stage.Count() }).ToArray(),
                        BoundaryIds = group.Select(item => item.BoundaryId).Distinct().ToArray(),
                        GeneratedAndExpanded = group.Any(item => item.Stage == SearchPathObservationStage.Generated)
                            && group.Any(item => item.Stage == SearchPathObservationStage.Expanded),
                    }).ToArray(),
                StateOnly = solver.Count(item => item.StateKey == needle.Prefixes[index].StateKey
                    && !variantSteps[item].Contains(match)),
                Scope = "PerVariantObservedPolicy:NoCrossBucketSurvival:NotFrozenPolicyOrSchedulingEquivalence",
            }));
        }
    }

    private static MoveStateSnapshot[] CaptureKnownRouteRootStates(
        CombatRootSnapshot root, Player player, IReadOnlyList<Creature> enemies)
    {
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        var simulator = root.ForkSimulator();
        return enemies.Select(enemy => CaptureSimulated(simulator,
            (SimulatedCombatState)simulator.State.CombatState, player, enemy)).ToArray();
    }

    private static void AssertKnownRouteRetentionPools(
        IReadOnlyList<SearchPathObservation> observations,
        IReadOnlyDictionary<SearchPathObservation, int[]> exactSteps,
        int requiredStep,
        IReadOnlySet<KnownRouteAliasAnchor>? aliasAnchors = null,
        Func<SearchPathObservation, bool>? requiredObservation = null)
    {
        SearchPathObservation[] poolEvents = observations.Where(item => item.Stage is
            SearchPathObservationStage.RetentionPoolInput or SearchPathObservationStage.GlobalRetention
            or SearchPathObservationStage.RetentionPoolFinal).ToArray();
        if (!poolEvents.Any(item => item.Stage == SearchPathObservationStage.GlobalRetention
                && (requiredObservation != null ? requiredObservation(item) : aliasAnchors == null
                    ? exactSteps[item].Contains(requiredStep - 1)
                    : aliasAnchors.Contains(KnownRouteAliasAnchorFor(item)))))
            throw new InvalidOperationException($"未观察到已知第{requiredStep}步的外层真实排序，不能判断该保留边界。");
        foreach (var boundary in poolEvents.GroupBy(item => new { item.SolverId, item.BoundaryId }))
        {
            SearchPathObservation[] input = boundary.Where(item =>
                item.Stage == SearchPathObservationStage.RetentionPoolInput).ToArray();
            SearchPathObservation[] ranked = boundary.Where(item =>
                item.Stage == SearchPathObservationStage.GlobalRetention).ToArray();
            SearchPathObservation[] final = boundary.Where(item =>
                item.Stage == SearchPathObservationStage.RetentionPoolFinal).ToArray();
            if (boundary.Key.BoundaryId <= 0 || input.Length == 0 || ranked.Length == 0
                || !input.Select(item => item.Retention?.PoolIndex).SequenceEqual(
                    Enumerable.Range(0, input.Length).Select(index => (int?)index))
                || !ranked.Select(item => item.Retention?.RawRank).SequenceEqual(
                    Enumerable.Range(0, ranked.Length).Select(index => (int?)index))
                || ranked.Any(item => item.Retention?.RawCount != ranked.Length)
                || !final.Select(item => item.Retention?.PoolIndex).SequenceEqual(
                    Enumerable.Range(0, final.Length).Select(index => (int?)index)))
                throw new InvalidOperationException("外层保留观察缺少完整、同编号的输入/真实排序/最终集合。");
            SearchPathRetentionDetails details = ranked[0].Retention!;
            foreach ((Func<SearchPathRetentionDetails, int?> Select, int? Count) sequence in new[]
                     {
                         ((Func<SearchPathRetentionDetails, int?>)(item => item.RequiredIndex), details.RequiredCount),
                         ((Func<SearchPathRetentionDetails, int?>)(item => item.RoutingIndex), details.RoutingCount),
                         ((Func<SearchPathRetentionDetails, int?>)(item => item.SelectedIndex), details.SelectedCount),
                     })
            {
                if (sequence.Count == null || !ranked.Select(item => sequence.Select(item.Retention!))
                        .Where(index => index != null).Order().SequenceEqual(
                            Enumerable.Range(0, sequence.Count.Value).Select(index => (int?)index)))
                    throw new InvalidOperationException("外层真实保留的必保/路由/选中索引不完整。");
            }
        }
    }

    private static KnownRoutePrefix FreezeKnownRoutePrefix(
        PlanAction action, MoveStateSnapshot state, SimulationSnapshot snapshot)
    {
        PlanCardChoice Choice(PlanCardChoice choice) => choice with
        {
            Cards = Array.AsReadOnly(choice.Cards.Select(token => token with { }).ToArray()),
        };
        IReadOnlyList<PlanCardChoice>? Choices(IReadOnlyList<PlanCardChoice>? choices)
            => choices == null ? null : Array.AsReadOnly(choices.Select(Choice).ToArray());
        PlanAction frozen = action with
        {
            Choice = action.Choice is { } primary ? Choice(primary) : null,
            NestedChoices = Choices(action.NestedChoices),
            TurnStartChoices = Choices(action.TurnStartChoices),
            RelicEffects = action.RelicEffects is { } relics
                ? Array.AsReadOnly(relics.Select(effect => effect with { }).ToArray()) : null,
        };
        return new KnownRoutePrefix(frozen, state, snapshot.StateKey, snapshot.Turn,
            snapshot.CumulativePlayerHpLost, snapshot.PotionUseCount)
        {
            ShufflesCrossed = snapshot.ShufflesCrossed,
            PotionStrategicCost = snapshot.PotionStrategicCost,
            TerminalStamp = snapshot.TerminalStamp,
            PlayerDead = snapshot.PlayerDead,
            AllEnemiesDead = snapshot.AllEnemiesDead,
        };
    }

    private static object? KnownRouteChoiceIdentity(PlanCardChoice? choice)
        => choice == null ? null : new
        {
            choice.Effect, choice.SourcePile, choice.SourceId, choice.ContextId, choice.Timing,
            Cards = choice.Cards.Select(card => new
            {
                card.CardId, card.UpgradeLevel, card.StateKey, card.SourceOccurrence, card.OptionOccurrence,
            }).ToArray(),
        };

    private static string KnownRouteActionIdentity(PlanAction action)
    {
        return JsonSerializer.Serialize(new
        {
            action.Kind, action.Turn, action.CardId, action.CardOccurrence, action.CardStateKey,
            action.CardStateOccurrence, action.TargetIndex, action.TargetCombatId,
            action.PotionSlot, action.PotionId, action.ReplayCount, action.EndsPlayerTurn,
            action.NestedChoicesBeforePrimary, Primary = KnownRouteChoiceIdentity(action.Choice),
            Nested = (action.NestedChoices ?? []).Select(KnownRouteChoiceIdentity).ToArray(),
            TurnStart = (action.TurnStartChoices ?? []).Select(KnownRouteChoiceIdentity).ToArray(),
        });
    }
}
