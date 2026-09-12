using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class CandidateContracts
{
    internal static int Run()
    {
        // Optional binding keeps the same probe executable against the original source,
        // which did not provide Restore. Reflection is outside all performance windows.
        var method = typeof(ReversibleValueState).GetMethod("Restore");
        if (method is null) return 0;
        Action<ReversibleValueState.FrozenValues> Restorer(ReversibleValueState state)
            => method.CreateDelegate<Action<ReversibleValueState.FrozenValues>>(state);
        int checks = 0;
        foreach (int count in new[] { 0, 1, 63, 64, 65, 573, 1024 })
        {
            var state = new ReversibleValueState(count);
            var original = state.Freeze();
            long[] expected = new long[count];
            for (int i = 0; i < count; i++) state.Write(i, expected[i] = (i % 3) switch { 0 => long.MinValue, 1 => long.MaxValue, _ => i });
            var populated = state.Freeze();
            var worker = populated.CreateWorkspace();
            var restore = Restorer(worker);
            AssertValues(worker, expected);
            restore(original); AssertValues(worker, new long[count]);
            restore(populated); AssertValues(worker, expected);
            if (count != 0)
            {
                // Dirty page content must override reference equality with the old page.
                worker.Write(count - 1, 42);
                restore(populated); AssertValues(worker, expected);
                // Writing zeros must remove former values even when pages become empty.
                for (int i = 0; i < count; i++) worker.Write(i, 0);
                var cleared = worker.Freeze();
                restore(populated); restore(cleared); AssertValues(worker, new long[count]);
                restore(populated);
                var outer = worker.Mark();
                worker.Write(0, 13);
                var inner = worker.Mark();
                worker.Write(0, 17);
                var suspended = worker.Freeze();
                ExpectFailure(() => restore(original));
                if (worker[0] != 17 || worker.CheckpointDepth != 2) throw new InvalidOperationException("Rejected restore changed a transaction.");
                worker.Rollback(inner);
                if (worker[0] != 13 || worker.Freeze()[0] != 13) throw new InvalidOperationException("Rollback reused a stale frozen page.");
                worker.Rollback(outer); AssertValues(worker, expected);
                restore(suspended);
                if (worker[0] != 17) throw new InvalidOperationException("Suspended values were lost.");
                ExpectFailure(() => worker.Rollback(outer));
                restore(populated);
            }
            var before = Enumerable.Range(0, count).Select(i => worker[i]).ToArray();
            ExpectFailure(() => restore(new ReversibleValueState(count).Freeze()));
            AssertValues(worker, before);
            Parallel.For(0, 8, lane =>
            {
                var sibling = populated.CreateWorkspace();
                var siblingRestore = Restorer(sibling);
                for (int iteration = 0; iteration < 64; iteration++)
                {
                    siblingRestore(original);
                    if (count != 0) sibling.Write(lane % count, lane + 1);
                    siblingRestore(populated); AssertValues(sibling, expected);
                }
            });
            for (int i = 0; i < count; i++)
                if (populated[i] != expected[i] || original[i] != 0)
                    throw new InvalidOperationException("Frozen contents changed after worker reuse.");
            checks++;
        }
        return checks;
    }

    private static void AssertValues(ReversibleValueState state, long[] expected)
    {
        if (state.Count != expected.Length) throw new InvalidOperationException("Slot count differs.");
        for (int i = 0; i < expected.Length; i++)
            if (state[i] != expected[i]) throw new InvalidOperationException($"Slot {i} differs.");
    }

    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Invalid compact ownership operation was accepted.");
    }
}
