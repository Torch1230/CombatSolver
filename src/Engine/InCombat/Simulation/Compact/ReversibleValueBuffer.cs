namespace CombatSolver.Engine.InCombat.Simulation.Compact;

/// <summary>
/// An indexed, growing sequence inside the shared reversible workspace. The immutable
/// layout owns only header offsets; lengths, tree links and contents all roll back together.
/// Other domain allocations may occur between its leaves without moving existing values.
/// </summary>
internal sealed class ReversibleValueBuffer
{
    private const int LeafBits = 6, LeafSize = 1 << LeafBits;
    private const int BranchBits = 5, BranchSize = 1 << BranchBits;
    private const int CountOffset = 0, RootOffset = 1, HeightOffset = 2, TailOffset = 3;
    private readonly int _header;

    internal ReversibleValueBuffer(ReversibleValueState state) => _header = state.Allocate(4);
    internal int Count(ReversibleValueState state) => checked((int)state[_header + CountOffset]);

    internal long Read(ReversibleValueState state, int index)
    {
        RequireIndex(state, index);
        return state[FindLeaf(state, index >> LeafBits) + (index & (LeafSize - 1))];
    }

    internal void Write(ReversibleValueState state, int index, long value)
    {
        RequireIndex(state, index);
        state.Write(FindLeaf(state, index >> LeafBits) + (index & (LeafSize - 1)), value);
    }

    internal int Append(ReversibleValueState state, ReadOnlySpan<long> values)
    {
        int start = Count(state), end = checked(start + values.Length);
        int position = start, source = 0;
        while (position < end)
        {
            int within = position & (LeafSize - 1);
            int leaf;
            if (within == 0)
            {
                leaf = AddLeaf(state, position >> LeafBits);
                state.Write(_header + TailOffset, leaf);
            }
            else leaf = checked((int)state[_header + TailOffset]);
            int length = Math.Min(LeafSize - within, end - position);
            for (int offset = 0; offset < length; offset++) state.Write(leaf + within + offset, values[source + offset]);
            source += length; position += length;
        }
        state.Write(_header + CountOffset, end);
        return start;
    }

    private void RequireIndex(ReversibleValueState state, int index)
    {
        if ((uint)index >= (uint)Count(state)) throw new ArgumentOutOfRangeException(nameof(index));
    }

    private int FindLeaf(ReversibleValueState state, int page)
    {
        int node = checked((int)state[_header + RootOffset]);
        for (int height = checked((int)state[_header + HeightOffset]); height > 0; height--)
            node = checked((int)state[node + ((page >> ((height - 1) * BranchBits)) & (BranchSize - 1))]);
        return node;
    }

    private int AddLeaf(ReversibleValueState state, int page)
    {
        int root = checked((int)state[_header + RootOffset]);
        if (root == 0)
        {
            int first = state.Allocate(LeafSize);
            state.Write(_header + RootOffset, first);
            return first;
        }
        int height = checked((int)state[_header + HeightOffset]);
        while (page >= (1L << (height * BranchBits)))
        {
            int parent = state.Allocate(BranchSize);
            state.Write(parent, root);
            state.Write(_header + RootOffset, parent);
            state.Write(_header + HeightOffset, ++height);
            root = parent;
        }
        int node = root;
        for (int level = height; level > 0; level--)
        {
            int slot = node + ((page >> ((level - 1) * BranchBits)) & (BranchSize - 1));
            int child = checked((int)state[slot]);
            if (child == 0)
            {
                child = state.Allocate(level == 1 ? LeafSize : BranchSize);
                state.Write(slot, child);
            }
            node = child;
        }
        return node;
    }
}
