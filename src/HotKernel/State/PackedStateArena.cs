using System.Buffers;

namespace CombatSolver.HotKernel;

internal readonly record struct HotStateHandle(int BlockIndex, int Offset, int Length);

/// <summary>
/// Slab arena renting 2 MiB byte[] blocks with reserve/commit/discard semantics.
/// Zero managed GC pressure during search.
/// </summary>
internal sealed class PackedStateArena : IDisposable
{
    private readonly int _blockPayloadBytes;
    private readonly List<byte[]> _blocks = [];
    private int _currentBlock;
    private int _tailOffset;
    private int _reservedLength;

    public PackedStateArena(int blockPayloadBytes = HotLimits.ArenaSlabBytes)
    {
        _blockPayloadBytes = blockPayloadBytes;
        AllocateBlock();
    }

    public long TotalCommittedBytes { get; private set; }

    public Span<byte> Reserve(int maxBytes)
    {
        int stride = Align8(maxBytes);
        byte[] block = _blocks[_currentBlock];
        if (_tailOffset + stride > block.Length)
        {
            AllocateBlock();
            block = _blocks[_currentBlock];
        }
        _reservedLength = stride;
        return block.AsSpan(_tailOffset, maxBytes);
    }

    public HotStateHandle CommitReserved(int actualBytes)
    {
        int aligned = Align8(actualBytes);
        if (aligned > _reservedLength)
            throw new InvalidOperationException($"Committed {aligned} bytes exceeding reserved {_reservedLength}.");
        HotStateHandle handle = new(_currentBlock, _tailOffset, actualBytes);
        _tailOffset += aligned;
        _reservedLength = 0;
        TotalCommittedBytes += aligned;
        return handle;
    }

    public void DiscardReserved() => _reservedLength = 0;

    public ReadOnlySpan<byte> GetSpan(HotStateHandle handle)
        => _blocks[handle.BlockIndex].AsSpan(handle.Offset, handle.Length);

    public void Reset()
    {
        while (_blocks.Count > 1)
        {
            byte[] tail = _blocks[^1];
            _blocks.RemoveAt(_blocks.Count - 1);
            ArrayPool<byte>.Shared.Return(tail);
        }
        _currentBlock = 0;
        _tailOffset = 0;
        _reservedLength = 0;
        TotalCommittedBytes = 0;
    }

    public void Dispose()
    {
        for (int i = 0; i < _blocks.Count; i++)
            ArrayPool<byte>.Shared.Return(_blocks[i]);
        _blocks.Clear();
    }

    private void AllocateBlock()
    {
        int totalSize = _blockPayloadBytes + HotLimits.MaxPackedStateBytes;
        byte[] rented = ArrayPool<byte>.Shared.Rent(totalSize);
        _blocks.Add(rented);
        _currentBlock = _blocks.Count - 1;
        _tailOffset = 0;
    }

    private static int Align8(int value) => (value + 7) & ~7;
}
