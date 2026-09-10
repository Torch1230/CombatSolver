using System.Numerics;

namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal sealed partial class ReversibleValueState
{
    private const int PageWidth = 64;
    private static int PageCount(int slots) => slots / PageWidth + (slots % PageWidth == 0 ? 0 : 1);

    /// <summary>Privately owned packed values, published once and never mutated.</summary>
    private sealed class ValuePage(ulong occupied, long[] values)
    {
        internal static readonly ValuePage Empty = new(0, []);
        private readonly ulong _occupied = occupied;
        private readonly long[] _values = values;
        internal int PayloadBytes => sizeof(ulong) + _values.Length * sizeof(long);

        internal long Read(int offset)
        {
            ulong bit = 1UL << offset;
            return (_occupied & bit) == 0 ? 0 : _values[BitOperations.PopCount(_occupied & (bit - 1))];
        }

        internal static ValuePage Capture(ReadOnlySpan<long> source, ValuePage previous)
        {
            ulong mask = 0;
            for (int i = 0; i < source.Length; i++)
                if (source[i] != 0) mask |= 1UL << i;
            if (mask == 0) return Empty;
            int count = BitOperations.PopCount(mask);
            bool dense = count == source.Length;
            if (mask == previous._occupied)
            {
                // Rollback invalidates a page conservatively. A sibling can finish with
                // identical values; reuse that immutable page after exact comparison.
                if (dense && source.SequenceEqual(previous._values)) return previous;
                if (!dense)
                {
                    int index = 0;
                    bool equal = true;
                    for (ulong remaining = mask; remaining != 0; remaining &= remaining - 1)
                        if (source[BitOperations.TrailingZeroCount(remaining)] != previous._values[index++])
                        { equal = false; break; }
                    if (equal) return previous;
                }
            }
            if (dense) return new(mask, source.ToArray());
            long[] packed = new long[count];
            int next = 0;
            for (ulong remaining = mask; remaining != 0; remaining &= remaining - 1)
                packed[next++] = source[BitOperations.TrailingZeroCount(remaining)];
            return new(mask, packed);
        }

        internal void CopyTo(Span<long> destination)
        {
            // Full pages can use the runtime bulk copy; sparse pages explicitly erase
            // values left by a different candidate before restoring occupied slots.
            if (_values.Length == destination.Length) { _values.CopyTo(destination); return; }
            destination.Clear();
            int next = 0;
            for (ulong remaining = _occupied; remaining != 0; remaining &= remaining - 1)
                destination[BitOperations.TrailingZeroCount(remaining)] = _values[next++];
        }
    }

    internal sealed class FrozenValues
    {
        private readonly object _rootIdentity;
        private readonly ValuePage[] _pages;
        public int Count { get; }
        internal int PayloadBytes => _pages.Length * IntPtr.Size + _pages.Sum(p => p.PayloadBytes);

        private FrozenValues(ReversibleValueState source)
        {
            _rootIdentity = source._rootIdentity;
            Count = source.Count;
            // The directory belongs to this candidate. Page objects contain only immutable
            // values; neither a worker nor an ancestor candidate is retained by this handle.
            _pages = (ValuePage[])source._pages.Clone();
        }

        public long this[int slot]
        {
            get
            {
                if ((uint)slot >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(slot));
                return _pages[slot / PageWidth].Read(slot % PageWidth);
            }
        }

        public ReversibleValueState CreateWorkspace()
        {
            var workspace = new ReversibleValueState(_rootIdentity, new long[Count]);
            RestoreInto(workspace);
            return workspace;
        }

        public bool ContentEquals(FrozenValues other)
        {
            if (Count != other.Count) return false;
            for (int page = 0; page < _pages.Length; page++)
            {
                if (ReferenceEquals(_pages[page], other._pages[page])) continue;
                int end = Math.Min(Count, (page + 1) * PageWidth);
                for (int slot = page * PageWidth; slot < end; slot++)
                    if (this[slot] != other[slot]) return false;
            }
            return true;
        }

        internal bool HasRoot(object rootIdentity) => ReferenceEquals(_rootIdentity, rootIdentity);
        internal bool HasSameRoot(FrozenValues other) => HasRoot(other._rootIdentity);
        internal static FrozenValues Capture(ReversibleValueState source) => new(source);

        internal void RestoreInto(ReversibleValueState workspace)
        {
            for (int page = 0; page < _pages.Length; page++)
            {
                ValuePage frozen = _pages[page];
                if (workspace._dirtyPages[page] || !ReferenceEquals(workspace._pages[page], frozen))
                    frozen.CopyTo(workspace.PageValues(page));
                workspace._pages[page] = frozen;
                workspace._dirtyPages[page] = false;
            }
        }

        // Structural diagnostics count each shared page once. They describe payload and
        // directory bytes, not CLR object headers, allocator slack, RSS or a GC trace.
        internal static (int Pages, long PayloadBytes, long DirectoryBytes) RetainedStorage(
            IReadOnlyList<FrozenValues> candidates)
        {
            HashSet<ValuePage> unique = [];
            long directories = 0;
            foreach (FrozenValues candidate in candidates)
            {
                directories += candidate._pages.Length * IntPtr.Size;
                foreach (ValuePage page in candidate._pages)
                    if (!ReferenceEquals(page, ValuePage.Empty)) unique.Add(page);
            }
            return (unique.Count, unique.Sum(p => (long)p.PayloadBytes), directories);
        }
    }
}
