using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class BufferContracts
{
    internal static void Run()
    {
        var state = new ReversibleValueState(0);
        ReversibleValueBuffer[] buffers = [new(state), new(state), new(state)];
        var root = state.Freeze();
        List<long>[] expected = [[], [], []];
        var random = new Random(52881);
        var saved = new List<(ReversibleValueState.FrozenValues State, long[][] Values)>();
        var marks = new Stack<(ReversibleValueState.Checkpoint Mark, long[][] Values)>();
        for (int step = 0; step < 800; step++)
        {
            int buffer = random.Next(buffers.Length);
            switch (random.Next(6))
            {
                case 0:
                case 1:
                    int extra = state.Allocate(3); state.Write(extra, -step);
                    long[] values = Enumerable.Range(0, random.Next(100)).Select(i => (long)(step * 101 + i)).ToArray();
                    if (buffers[buffer].Append(state, values) != expected[buffer].Count)
                        throw new InvalidOperationException("Indexed append returned the wrong logical position.");
                    expected[buffer].AddRange(values);
                    break;
                case 2 when expected[buffer].Count != 0:
                    int index = random.Next(expected[buffer].Count);
                    long replacement = step % 2 == 0 ? long.MinValue : long.MaxValue;
                    buffers[buffer].Write(state, index, replacement); expected[buffer][index] = replacement;
                    break;
                case 3 when marks.Count < 3:
                    marks.Push((state.Mark(), Copy()));
                    break;
                case 4 when marks.Count != 0:
                    var before = marks.Pop(); state.Rollback(before.Mark); expected = before.Values.Select(v => v.ToList()).ToArray();
                    break;
                case 5:
                    if (marks.Count == 0 && saved.Count != 0 && step % 2 == 0)
                    {
                        var snapshot = saved[random.Next(saved.Count)];
                        state.Restore(snapshot.State); expected = snapshot.Values.Select(v => v.ToList()).ToArray();
                    }
                    else saved.Add((state.Freeze(), Copy()));
                    break;
            }
            Compare(state, Copy());
        }
        Parallel.For(0, 8, worker =>
        {
            var lane = root.CreateWorkspace();
            foreach (var snapshot in saved.AsEnumerable().Reverse())
            {
                lane.Restore(snapshot.State); Compare(lane, snapshot.Values);
                int extra = lane.Allocate(129); lane.Write(extra + worker, long.MaxValue);
                buffers[worker % 3].Append(lane, [worker, -worker]);
            }
        });
        // Cross each radix height boundary; multiple buffers and domain allocations remain
        // interleaved. These are list-oracle checks, independent of the tree representation.
        var deep = root.CreateWorkspace();
        long[][] large = Enumerable.Range(0, 3).Select(b => Enumerable.Range(0, 65_539 + b)
            .Select(i => i % 2 == 0 ? (long)i * (b + 1) : -(long)i * (b + 1)).ToArray()).ToArray();
        foreach (int boundary in new[] { 1, 63, 64, 65, 2047, 2048, 2049, 65535, 65536, 65539 })
        {
            for (int b = 0; b < 3; b++)
            {
                int begin = buffers[b].Count(deep);
                buffers[b].Append(deep, large[b].AsSpan(begin, boundary - begin));
                deep.Append(b);
            }
            Compare(deep, large.Select(v => v[..boundary]).ToArray());
        }
        var deepFrozen = deep.Freeze();
        var mark = deep.Mark();
        buffers[0].Write(deep, 65_536, long.MinValue); buffers[1].Append(deep, [long.MaxValue]);
        deep.Rollback(mark);
        if (!deep.Freeze().ContentEquals(deepFrozen)) throw new InvalidOperationException("Deep indexed writes/growth failed rollback.");
        deep.Restore(root); Compare(deep, [[], [], []]);
        deep.Restore(deepFrozen); Compare(deep, large.Select(v => v[..65_539]).ToArray());
        foreach (int id in new[] { -1, 0, 255, 256, 65_536, int.MaxValue })
        foreach (int value in new[] { int.MinValue, -999_999_999, 0, 999_999_999, int.MaxValue })
        foreach (bool automatic in new[] { false, true })
        {
            var item = new ResumableDiscardProgram.Event(ResumableDiscardProgram.EventKind.Damage,
                id, value, automatic, id, 255);
            if (ResumableDiscardProgram.Event.Decode(item.Data, item.Metadata) != item)
                throw new InvalidOperationException("Event encoding truncated a generated identity, amount or flag.");
        }
        Console.WriteLine("COMPACT_BUFFER_CHECKS_OK interleaved_buffers=3 random_steps=800 radix_boundaries=true frozen_workers=8 undo=true restore=true signed_32bit_event_fields=true");

        long[][] Copy() => expected.Select(v => v.ToArray()).ToArray();
        void Compare(ReversibleValueState lane, long[][] values)
        {
            for (int b = 0; b < buffers.Length; b++)
                if (buffers[b].Count(lane) != values[b].Length
                    || Enumerable.Range(0, values[b].Length).Any(i => buffers[b].Read(lane, i) != values[b][i]))
                    throw new InvalidOperationException($"Indexed buffer differs from the list oracle: buffer={b}.");
        }
    }
}
