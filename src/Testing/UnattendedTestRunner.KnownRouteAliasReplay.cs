using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // This is a verified combat replay anchor, not a reconstructed SearchNode or lease.
    private sealed record KnownRouteAliasAnchor(
        Guid SolverId, StateFingerprint StateKey, int Turn,
        SearchPathPolicyLabel PolicyLabel, string PathIdentity);

    private static string KnownRouteObservedPathIdentity(SearchPathObservation observation)
        => JsonSerializer.Serialize(new
        {
            Actions = observation.Actions.Select(KnownRouteActionIdentity).ToArray(),
            RootChoices = observation.RootTurnSetupChoices.Select(KnownRouteChoiceIdentity).ToArray(),
        });

    private static KnownRouteAliasAnchor KnownRouteAliasAnchorFor(SearchPathObservation observation)
        => new(observation.SolverId, observation.StateKey, observation.Turn,
            observation.PolicyLabel, KnownRouteObservedPathIdentity(observation));

    private HashSet<KnownRouteAliasAnchor> ProveKnownRouteAliases(
        CombatRootSnapshot root, SolverDisplayNames names, BattleDamageSnapshot damage,
        SearchPolicySnapshot policy, Player player, Creature enemy,
        IReadOnlyList<KnownRoutePrefix> prefixes, IReadOnlyList<SearchPathObservation> observations,
        int requiredStep, string sample)
    {
        KnownRoutePrefix expected = prefixes[requiredStep - 1];
        IGrouping<string, SearchPathObservation>[] aliases = observations
            .Where(item => item.Stage == SearchPathObservationStage.Generated && item.StateKey == expected.StateKey)
            .GroupBy(KnownRouteObservedPathIdentity, StringComparer.Ordinal).ToArray();
        if (aliases.Length == 0)
            throw new InvalidOperationException($"第{requiredStep}步没有可严格证明的真实 Generated 别名。");
        HashSet<KnownRouteAliasAnchor> anchors = [];
        int aliasIndex = 0;
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        foreach (IGrouping<string, SearchPathObservation> alias in aliases)
        {
            EnsureWithinDeadline();
            SearchPathObservation observed = alias.First();
            if (observed.RootTurnSetupChoices.Count != 0 || observed.Actions.Count == 0)
                throw new InvalidOperationException("Play 根别名不能忽略回合开始选择或空动作历史。");
            CombatBeamSolver driver = new(root, names, damage, policy);
            List<PlanAction> actions = [.. observed.Actions];
            List<SimulationSnapshot> owned = [];
            try
            {
                SimulationSnapshot parent = ReplayKnownCustom(driver, actions, null, 0, 0, owned);
                AssertKnownRouteAliasSnapshot(parent, expected, player, enemy, $"alias:{aliasIndex}:prefix");
                foreach (SearchPathObservation item in alias)
                {
                    double score = (double)InvokeKnownCustomMethod(driver, "ApplySoldHpPenalty",
                        [parent.Score, item.FutureSoldHp])!;
                    if (item.ActionCount != actions.Count || item.Turn != parent.Turn
                        || item.CumulativePlayerHpLost != parent.CumulativePlayerHpLost
                        || item.PotionCount != parent.PotionUseCount
                        || item.PotionStrategicCost != parent.PotionStrategicCost
                        || !item.Score.Equals(score) || item.HasPredictionRisk || item.IsTerminal
                        || item.BoundaryReason != SearchBoundaryReason.None)
                        throw new InvalidOperationException("实际别名观察的边界或政策标签与正式前缀回放不一致。");
                }
                for (int index = requiredStep; index < prefixes.Count; index++)
                {
                    EnsureWithinDeadline();
                    if (parent.PlayerDead || parent.AllEnemiesDead || !parent.Simulator.IsInProgress)
                        throw new InvalidOperationException($"别名在冻结第{index + 1}步之前已终局，不能跳过后缀。");
                    PlanAction action = prefixes[index].Action;
                    int priorActionCount = actions.Count;
                    actions.Add(action); // Use the frozen choice identities verbatim; never rebind.
                    SimulationSnapshot incremental = ReplayKnownCustom(driver, [action], parent,
                        parent.Turn, priorActionCount, owned);
                    SimulationSnapshot full = ReplayKnownCustom(driver, actions, null, 0, 0, owned);
                    string label = $"alias:{aliasIndex}:step:{index + 1}";
                    AssertKnownRouteAliasSnapshot(incremental, prefixes[index], player, enemy, label + ":incremental");
                    AssertKnownRouteAliasSnapshot(full, prefixes[index], player, enemy, label + ":full");
                    _ = InvokeKnownCustomMethod(driver, "AssertIncrementalEquivalent",
                        [action, actions.ToArray(), incremental, full]);
                    Entry.Logger.Info("[CombatSolver/Test] PATH_TRACE_ALIAS_SUFFIX " + JsonSerializer.Serialize(new
                    {
                        Sample = sample, Alias = aliasIndex, Step = index + 1,
                        ActionCount = actions.Count, incremental.StateKey, incremental.Turn,
                        incremental.CumulativePlayerHpLost, incremental.PotionUseCount,
                        incremental.ShufflesCrossed, FullFrozenStateAndIncrementalEquivalent = true,
                    }));
                    full.ReleaseSimulator();
                    parent.ReleaseSimulator();
                    parent = incremental;
                }
                if (parent.PlayerDead || !parent.AllEnemiesDead || parent.CombatEndedTurn == null
                    || parent.Simulator.IsInProgress)
                    throw new InvalidOperationException("别名完整冻结后缀没有抵达正式胜利终局。");
                foreach (SearchPathObservation item in alias)
                    anchors.Add(KnownRouteAliasAnchorFor(item));
                Entry.Logger.Info("[CombatSolver/Test] PATH_TRACE_ALIAS_PROOF " + JsonSerializer.Serialize(new
                {
                    Sample = sample, Alias = aliasIndex, RequiredStep = requiredStep,
                    PathIdentity = alias.Key,
                    SolverIds = alias.Select(item => item.SolverId).Distinct().ToArray(),
                    ObservedLabels = alias.Select(item => item.PolicyLabel).Distinct().ToArray(),
                    SuffixSteps = prefixes.Count - requiredStep, parent.CombatEndedTurn,
                    parent.PlayerHp, parent.CumulativePlayerHpLost, parent.PotionUseCount,
                    Scope = "CombatSuffixOnly:NotCycleOrOrderedSchedulingEquivalence",
                }));
                aliasIndex++;
            }
            finally
            {
                foreach (SimulationSnapshot snapshot in owned)
                    snapshot.ReleaseSimulator();
            }
        }
        return anchors;
    }

    private void AssertKnownRouteAliasSnapshot(
        SimulationSnapshot snapshot, KnownRoutePrefix expected, Player player, Creature enemy, string label)
    {
        SimulatedCombatState combat = (SimulatedCombatState)snapshot.Simulator.State.CombatState;
        if (snapshot.HasRisk || snapshot.PredictionGaps.Any(gap => !gap.Compensated)
            || snapshot.BoundaryReason != SearchBoundaryReason.None || combat.HasPendingChoice
            || combat.PlayerTurnEndRequested || snapshot.StateKey != expected.StateKey
            || snapshot.Turn != expected.Turn || snapshot.CumulativePlayerHpLost != expected.HpLost
            || snapshot.PotionUseCount != expected.PotionsUsed
            || snapshot.PotionStrategicCost != expected.PotionStrategicCost
            || snapshot.ShufflesCrossed != expected.ShufflesCrossed
            || snapshot.PlayerDead != expected.PlayerDead || snapshot.AllEnemiesDead != expected.AllEnemiesDead
            || snapshot.TerminalStamp != expected.TerminalStamp
            || snapshot.Simulator.IsInProgress == (snapshot.PlayerDead || snapshot.AllEnemiesDead))
            throw new InvalidOperationException($"别名 {label} 与冻结前缀的稳定状态/累计指标/终局不一致。");
        combat.AssertForkable();
        AssertSnapshotEqual(CaptureSimulated(snapshot.Simulator, combat, player, enemy), expected.State,
            "KnownRouteAlias", label);
    }
}
