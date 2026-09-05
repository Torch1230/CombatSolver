namespace CompactStatePrototype;

internal static class PrototypeChecks
{
    internal static readonly Func<RootSnapshot, IBranchState>[] Factories =
    [
        root => new DeepCopyState(root),
        root => new UndoJournalState(root),
        root => new PageCowState(root),
    ];

    public static string[] Run()
    {
        RootSnapshot root = new(37, 0x0123456789abcdefUL);
        long[] immutableRoot = root.CreateInitialState();
        foreach (Func<RootSnapshot, IBranchState> factory in Factories)
        {
            AssertBranches(factory(root));
            AssertFaultRollback(factory(root), cancel: false);
            AssertFaultRollback(factory(root), cancel: true);
            AssertNestedTransactions(factory(root));
            AssertEqual(immutableRoot, root.CreateInitialState(), "immutable root");
        }
        AssertReplay(root);
        AssertTreeEquivalence(root);
        bool invalidIdRejected = false;
        try { root.Resolve(new EntityId(1001)); }
        catch (ArgumentOutOfRangeException) { invalidIdRejected = true; }
        Require(invalidIdRejected, "Unknown entity IDs must fail explicitly.");
        return ["immutable root and stable IDs", "persistent sibling fork isolation including RNG",
            "exception rollback after third write", "cancellation rollback after third write",
            "nested commit/rollback and repeated writes", "full-state replay equality after every transition",
            "full-state DFS/retained-frontier equality"];
    }

    private static void AssertBranches(IBranchState parent)
    {
        long[] original = Workload.Snapshot(parent);
        IBranchState left = parent.ForkRetained();
        IBranchState right = parent.ForkRetained();
        Require(ReferenceEquals(parent.Root, left.Root) && ReferenceEquals(parent.Root, right.Root),
            "Fork must share only the immutable root and explicitly shared storage.");
        Workload.Apply(left, 13, WritePattern.Sparse);
        long[] leftExpected = Workload.Snapshot(left);
        AssertEqual(original, Workload.Snapshot(parent), parent.Name + " parent after left fork write");
        AssertEqual(original, Workload.Snapshot(right), parent.Name + " sibling after left fork write");
        Workload.Apply(right, 27, WritePattern.Dense);
        Workload.Apply(parent, 31, WritePattern.Dense);
        AssertEqual(leftExpected, Workload.Snapshot(left), parent.Name + " child after parent/sibling writes");
        Require(left.Read(Layout.RandomDraws) == 2 && right.Read(Layout.RandomDraws) == 2,
            parent.Name + " RNG counter was shared between forks.");
    }

    private sealed class ProbeException : Exception;

    private sealed class FaultingState(ICompactState inner, bool cancel, CancellationTokenSource cancellation)
        : ICompactState
    {
        private int _writes;
        public RootSnapshot Root => inner.Root;
        public long Read(int word) => inner.Read(word);
        public void Write(int word, long value)
        {
            inner.Write(word, value);
            if (++_writes != 3)
                return;
            if (!cancel)
                throw new ProbeException();
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        }
    }

    private static void AssertFaultRollback(IBranchState state, bool cancel)
    {
        long[] before = Workload.Snapshot(state);
        using CancellationTokenSource cancellation = new();
        bool observed = false;
        state.BeginBranch();
        try
        {
            Workload.Apply(new FaultingState(state, cancel, cancellation), 7, WritePattern.Dense, cancellation.Token);
        }
        catch (ProbeException) when (!cancel) { observed = true; }
        catch (OperationCanceledException ex) when (cancel && ex.CancellationToken == cancellation.Token)
        { observed = true; }
        finally { state.RollbackBranch(); }
        Require(observed, state.Name + " fault was not injected.");
        AssertEqual(before, Workload.Snapshot(state), state.Name + (cancel ? " cancellation rollback" : " exception rollback"));
        Require(state.TransactionDepth == 0, "Fault cleanup left an active transaction.");
    }

    private static void AssertNestedTransactions(IBranchState state)
    {
        long[] before = Workload.Snapshot(state);
        int word = Layout.Entity(0, Layout.Block);
        state.BeginBranch();
        state.Write(word, 1);
        state.Write(word, 2);
        state.BeginBranch();
        state.Write(word, 3);
        state.CommitBranch();
        Require(state.Read(word) == 3, "Inner commit did not publish its writes.");
        state.RollbackBranch();
        AssertEqual(before, Workload.Snapshot(state), state.Name + " outer rollback of committed child");

        state.BeginBranch();
        state.Write(word, 4);
        state.BeginBranch();
        state.Write(word, 5);
        state.RollbackBranch();
        Require(state.Read(word) == 4, "Inner rollback lost the outer write.");
        state.CommitBranch();
        Require(state.Read(word) == 4 && state.TransactionDepth == 0, "Outer commit did not persist.");
        state.BeginBranch();
        state.Write(word, 6);
        state.RollbackBranch();
        Require(state.Read(word) == 4, "Reused transaction did not preserve the committed state.");
    }

    private static void AssertReplay(RootSnapshot root)
    {
        IBranchState[] states = Factories.Select(factory => factory(root)).ToArray();
        for (ulong transition = 1; transition <= 64; transition++)
        {
            WritePattern pattern = transition % 3 == 0 ? WritePattern.Dense : WritePattern.Sparse;
            foreach (IBranchState state in states)
            {
                state.BeginBranch();
                try
                {
                    Workload.Apply(state, transition * 17, pattern);
                    state.CommitBranch();
                }
                catch
                {
                    state.RollbackBranch();
                    throw;
                }
            }
            long[] expected = Workload.Snapshot(states[0]);
            foreach (IBranchState state in states.Skip(1))
                AssertEqual(expected, Workload.Snapshot(state), state.Name + " replay transition " + transition);
        }
    }

    private static void AssertTreeEquivalence(RootSnapshot root)
    {
        foreach (WritePattern pattern in Enum.GetValues<WritePattern>())
        {
            List<long[]>? expected = null;
            foreach (Func<RootSnapshot, IBranchState> factory in Factories)
            {
                IBranchState state = factory(root);
                List<long[]> visited = [];
                Visit(state, 3, 3, 1, pattern, visited);
                AssertEqual(root.CreateInitialState(), Workload.Snapshot(state), state.Name + " tree rollback");
                if (expected is null)
                    expected = visited;
                else
                    for (int index = 0; index < expected.Count; index++)
                        AssertEqual(expected[index], visited[index], state.Name + " tree node " + index);

                List<(IBranchState State, ulong Path)> frontier = [(factory(root), 1)];
                for (int depth = 0; depth < 3; depth++)
                {
                    List<(IBranchState State, ulong Path)> next = [];
                    foreach ((IBranchState parent, ulong path) in frontier)
                    {
                        for (int child = 0; child < 3; child++)
                        {
                            ulong childPath = path * 3 + (ulong)child;
                            IBranchState branch = parent.ForkRetained();
                            Workload.Apply(branch, childPath, pattern);
                            next.Add((branch, childPath));
                        }
                    }
                    frontier = next;
                }
                foreach ((IBranchState leaf, ulong path) in frontier)
                {
                    IBranchState replay = new DeepCopyState(root);
                    Workload.Apply(replay, path / 9, pattern);
                    Workload.Apply(replay, path / 3, pattern);
                    Workload.Apply(replay, path, pattern);
                    AssertEqual(Workload.Snapshot(replay), Workload.Snapshot(leaf), state.Name + " retained leaf replay");
                }
            }
        }
    }

    private static void Visit(IBranchState state, int depth, int fanout, ulong path,
        WritePattern pattern, List<long[]> visited)
    {
        for (int child = 0; child < fanout; child++)
        {
            state.BeginBranch();
            try
            {
                ulong childPath = path * (ulong)fanout + (ulong)child;
                Workload.Apply(state, childPath, pattern);
                visited.Add(Workload.Snapshot(state));
                if (depth > 1)
                    Visit(state, depth - 1, fanout, childPath, pattern, visited);
            }
            finally { state.RollbackBranch(); }
        }
    }

    private static void AssertEqual(long[] expected, long[] actual, string context)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidOperationException("State equality failed: " + context);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
