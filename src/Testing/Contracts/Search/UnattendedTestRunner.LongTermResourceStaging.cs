using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertLongTermResourceStaging(CombatState combat, Player player)
    {
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null)
            with { GrowthBudgets = default, IgnoreLongTermRewards = false };
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        CombatBeamSolver driver = new(root, names, damage, policy);
        SimulationSnapshot initial = InvokeForcedTerminalReplay(driver, [], null, 0, null);
        List<SimulationSnapshot> snapshots = [initial];
        try
        {
            foreach (int resource in new[] { 0, 5, 5, 9 })
            {
                CombatPredictionSimulator simulator = initial.Simulator.Fork();
                if (resource > 0)
                    ((SimulatedCombatState)simulator.State.CombatState).RecordLongTermResource(resource);
                simulator.Damage(player.Creature, snapshots.Count,
                    ValueProp.Unblockable | ValueProp.Unpowered, player.Creature);
                snapshots.Add((SimulationSnapshot)InvokeForcedTerminalMethod(driver, "Snapshot",
                    [simulator, initial.Turn, 0, initial.ShufflesCrossed,
                        SearchBoundaryReason.None, initial.ProcessedEnemyDeaths])!);
            }
            foreach (int[] indices in new int[][] { [], [0, 1, 0], [2, 3, 2], [0, 2, 3], [4, 2, 4, 0] })
            {
                SearchNode[] MakeGraph()
                {
                    SearchNode ancestor = ForcedTerminalAnnotationNode(initial, null, null);
                    SearchNode shared = ForcedTerminalAnnotationNode(initial, ancestor, null);
                    List<SearchNode> graph = [ancestor, shared];
                    for (int index = 0; index < indices.Length; index++)
                        graph.Add(ForcedTerminalAnnotationNode(snapshots[indices[index]],
                            index % 2 == 0 ? shared : ancestor, null));
                    for (int index = 0; index < graph.Count; index++)
                    {
                        graph[index].RetentionRank = 40 - index;
                        graph[index].LongTermResourceRetentionRank = index % 2 == 0 ? index + 1 : int.MaxValue;
                    }
                    return graph.ToArray();
                }
                SearchNode[] expected = MakeGraph();
                SearchNode[] actual = MakeGraph();
                CombatBeamSolver reference = new(root, names, damage, policy);
                CombatBeamSolver candidate = new(root, names, damage, policy);
                List<SearchNode> expectedSelected = reference.RankResourceStagingReferenceForTesting(
                    expected.Skip(2).ToList(), [expected[0], .. expected.Skip(2).Take(1)]);
                List<SearchNode> actualSelected = (List<SearchNode>)InvokeForcedTerminalMethod(candidate,
                    "RankLongTermResourceWithAncestorRanks",
                    [actual.Skip(2).ToList(), new List<SearchNode> { actual[0] }.Concat(actual.Skip(2).Take(1)).ToList()])!;
                int[] IdentityOrder(SearchNode[] graph, List<SearchNode> selected)
                    => selected.Select(node => Array.FindIndex(graph, item => ReferenceEquals(item, node))).ToArray();
                if (!IdentityOrder(expected, expectedSelected).SequenceEqual(IdentityOrder(actual, actualSelected))
                    || !expected.Select(node => (node.RetentionRank, node.LongTermResourceRetentionRank))
                        .SequenceEqual(actual.Select(node => (node.RetentionRank, node.LongTermResourceRetentionRank))))
                    throw new InvalidOperationException("Resource staging changed selected identity/order or restored rank vectors.");
                if (indices.Length == 0 || indices.Select(index => snapshots[index].LongTermResourceValue).Distinct().Count() == 1)
                {
                    if (actualSelected.Count != 0 || actual.Where((node, index) => node.RetentionRank != 40 - index).Any())
                        throw new InvalidOperationException("Uniform resource pool changed existing ranks or produced a resource route.");
                }
                else if (actualSelected.Count == 0)
                    throw new InvalidOperationException("Mixed resource fixture did not exercise ranking.");
            }
            if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
                throw new InvalidOperationException("Resource staging mutated live combat.");
        }
        finally
        {
            foreach (SimulationSnapshot snapshot in snapshots)
                snapshot.ReleaseSimulator();
        }
    }
}

internal sealed partial class CombatBeamSolver
{
    // Deliberately retains the old eager staging and independent Max/All/Where selector.
    internal List<SearchNode> RankResourceStagingReferenceForTesting(
        IReadOnlyList<SearchNode> pool, IReadOnlyList<SearchNode> global)
    {
        int[] globalRanks = global.Select(node => node.RetentionRank).ToArray();
        Dictionary<SearchNode, int> ancestorRanks = new(ReferenceEqualityComparer.Instance);
        foreach (SearchNode candidate in pool)
        {
            for (SearchNode? ancestor = candidate.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (!ancestorRanks.TryAdd(ancestor, ancestor.RetentionRank))
                    break;
                if (ancestor.LongTermResourceRetentionRank != int.MaxValue)
                    ancestor.RetentionRank = ancestor.LongTermResourceRetentionRank;
            }
        }
        List<SearchNode> selected = [];
        if (pool.Count != 0)
        {
            int maximum = pool.Max(node => node.Snapshot.LongTermResourceValue);
            if (!pool.All(node => node.Snapshot.LongTermResourceValue == maximum))
                selected = Retention.RankBest(pool.Where(node => node.Snapshot.LongTermResourceValue == maximum),
                    _profile.BeamWidth, preserveDefensiveRoute: true);
        }
        foreach (SearchNode node in selected)
            node.LongTermResourceRetentionRank = node.RetentionRank;
        foreach ((SearchNode node, int rank) in ancestorRanks)
            node.RetentionRank = rank;
        for (int index = 0; index < global.Count; index++)
            global[index].RetentionRank = globalRanks[index];
        return selected;
    }
}
