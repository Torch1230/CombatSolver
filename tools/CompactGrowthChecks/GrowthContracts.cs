using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class GrowthContracts
{
    internal static int Run()
    {
        Func<int, int> Allocator(ReversibleValueState state)
            => state.Allocate;
        int checks = 0;
        foreach (int initial in new[] { 0, 1, 63, 64, 65, 127 })
        {
            var state = new ReversibleValueState(initial);
            var allocate = Allocator(state);
            for (int i = 0; i < initial; i++) state.Write(i, i + 1);
            var root = state.Freeze();
            var outer = state.Mark();
            if (allocate(65) != initial) throw new InvalidOperationException("Append returned a wrong slot identity.");
            for (int i = initial; i < state.Count; i++) state.Write(i, -i - 1);
            var shorter = state.Freeze();
            var inner = state.Mark();
            int tail = allocate(193);
            for (int i = tail; i < state.Count; i++) state.Write(i, long.MaxValue - i);
            var longer = state.Freeze();
            Expect<InvalidOperationException>(() => state.Restore(root));
            Expect<InvalidOperationException>(() => state.Rollback(outer));
            if (!state.Freeze().ContentEquals(longer) || state.CheckpointDepth != 2)
                throw new InvalidOperationException("Rejected restore/rollback changed grown state.");
            state.Rollback(inner);
            if (!state.Freeze().ContentEquals(shorter)) throw new InvalidOperationException("Nested growth did not roll back.");
            Expect<ArgumentOutOfRangeException>(() => state.Write(tail, 3));
            Expect<ArgumentOutOfRangeException>(() => { _ = state[tail]; });
            if (allocate(193) != tail) throw new InvalidOperationException("Rollback did not reclaim the generated suffix.");
            for (int i = tail; i < state.Count; i++)
                if (state[i] != 0) throw new InvalidOperationException("A sibling inherited removed slot values.");
            state.Rollback(outer);
            if (!state.Freeze().ContentEquals(root)) throw new InvalidOperationException("Outer growth did not roll back.");
            Expect<ArgumentOutOfRangeException>(() => allocate(-1));
            if (initial != 0) Expect<OverflowException>(() => allocate(int.MaxValue));
            Expect<InvalidOperationException>(() => state.Restore(new ReversibleValueState(initial + 129).Freeze()));
            if (!state.Freeze().ContentEquals(root)) throw new InvalidOperationException("Rejected allocation changed state.");
            Parallel.For(0, 8, lane =>
            {
                var worker = root.CreateWorkspace();
                foreach (var frozen in new[] { longer, shorter, root, longer, root, shorter })
                {
                    worker.Restore(frozen);
                    if (!worker.Freeze().ContentEquals(frozen))
                        throw new InvalidOperationException("A worker restored stale pages across different slot counts.");
                    int generated = Allocator(worker)(129);
                    worker.Write(generated + lane, lane + 91);
                }
            });
            if (!state.Freeze().ContentEquals(root) || longer[tail] != long.MaxValue - tail)
                throw new InvalidOperationException("Worker growth changed a frozen candidate or root.");
            checks++;
        }

        // A list oracle checks mixtures of growing, writing, nested rollback, and
        // restoring differently sized candidates, independently of page representation.
        var mixed = new ReversibleValueState(0);
        var append = Allocator(mixed);
        List<long> expected = [];
        var marks = new Stack<(ReversibleValueState.Checkpoint Mark, long[] Values)>();
        var saved = new List<(ReversibleValueState.FrozenValues Frozen, long[] Values)>();
        Random random = new(9371);
        for (int step = 0; step < 600; step++)
        {
            switch (random.Next(7))
            {
                case 0:
                    int count = random.Next(130);
                    if (append(count) != expected.Count) throw new InvalidOperationException("Mixed append identity differs.");
                    expected.AddRange(new long[count]);
                    break;
                case 1 when expected.Count != 0:
                    int slot = random.Next(expected.Count);
                    long value = step % 3 == 0 ? 0 : step % 2 == 0 ? long.MinValue : random.NextInt64();
                    mixed.Write(slot, value); expected[slot] = value;
                    break;
                case 2 when marks.Count < 4:
                    marks.Push((mixed.Mark(), expected.ToArray()));
                    break;
                case 3 when marks.Count != 0:
                    var prior = marks.Pop();
                    mixed.Rollback(prior.Mark); expected = prior.Values.ToList();
                    break;
                case 4:
                    saved.Add((mixed.Freeze(), expected.ToArray()));
                    break;
                case 5 when marks.Count == 0 && saved.Count != 0:
                    var candidate = saved[random.Next(saved.Count)];
                    mixed.Restore(candidate.Frozen); expected = candidate.Values.ToList();
                    break;
                case 6:
                    long appended = step % 2 == 0 ? 0 : long.MaxValue;
                    if (mixed.Append(appended) != expected.Count) throw new InvalidOperationException("Append value identity differs.");
                    expected.Add(appended);
                    break;
            }
            if (mixed.Count != expected.Count || Enumerable.Range(0, expected.Count).Any(i => mixed[i] != expected[i]))
                throw new InvalidOperationException($"Mixed growth differs from the list oracle at step {step}.");
        }
        foreach (var candidate in saved)
            if (candidate.Frozen.Count != candidate.Values.Length
                || Enumerable.Range(0, candidate.Values.Length).Any(i => candidate.Frozen[i] != candidate.Values[i]))
                throw new InvalidOperationException("Later growth changed a previously frozen oracle result.");

        // Continue a genuine executor program beyond the former fixed event limit.
        // This is a storage/execution contract, not a native full-battle claim.
        CardEffectProgram draw = new([new(CardInstructionKind.Draw, 1)]);
        ResumableDiscardProgram.Card[] cards = [new(0, draw), new(0, draw)];
        var program = new ResumableDiscardProgram(cards, [[0], [1], [], [], []], 1, 0, 0,
            new(0, 1, 2, 3, 4), [0, -1, 1, 0]);
        var before = program.State.Freeze();
        var checkpoint = program.State.Mark();
        ResumableDiscardProgram.Candidate? middle = null;
        for (int action = 0; action < 512; action++)
        {
            program.Begin(action % 2);
            program.Run();
            if (!program.Complete || program.Count(ResumableDiscardProgram.Pile.Hand) != 1
                || program.CardAt(ResumableDiscardProgram.Pile.Hand, 0) != (action + 1) % 2)
                throw new InvalidOperationException("Long executor history changed its next action.");
            if (action == 255) middle = program.Freeze();
        }
        var end = program.Freeze();
        if (program.EventCount < 3000 || program.ShuffleCount != 511)
            throw new InvalidOperationException("Long execution did not exceed the former event capacity.");
        var endValues = program.State.Freeze();
        program.State.Rollback(checkpoint);
        if (!program.State.Freeze().ContentEquals(before)) throw new InvalidOperationException("Long execution did not restore its original root.");
        var restored = middle!.Open();
        for (int action = 256; action < 512; action++) { restored.Begin(action % 2); restored.Run(); }
        if (!restored.State.Freeze().ContentEquals(endValues)) throw new InvalidOperationException("Frozen history did not resume identically.");
        end.RestoreInto(program);
        if (!program.State.Freeze().ContentEquals(endValues)) throw new InvalidOperationException("Long candidate could not restore into the original lane.");
        return checks + 2;
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
