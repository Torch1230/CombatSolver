using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CombatSolver.HotKernel;

namespace CombatSolver.HotSearch;

[Flags]
internal enum HotNodeFlags : ushort
{
    None = 0,
    IsRoot = 1 << 0,
    IsTerminal = 1 << 1,
    EndedTurn = 1 << 2,
    HasChoice = 1 << 3,
    HasDeathSave = 1 << 4,
    ForfeitsGold = 1 << 5,
    HasRisk = 1 << 6,
    StandPat = 1 << 7,
    HasRolloutScore = 1 << 8,
    RolloutPromoted = 1 << 9,
    HasChoiceLookahead = 1 << 10,
}

/// <summary>
/// Compact value-type search node stored inside chunked rented arrays of 4,096 elements.
/// Eliminates per-node heap objects, giant resize spikes, and Gen2 managed object chains.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct HotSearchNode
{
    public HotHash128 Hash;             // 16 B
    public long Score;                  // 8 B
    public long RolloutScore;           // 8 B (valid iff HasRolloutScore is set; reserved for P1 rollouts)
    public HotColdSidecar? Cold;        // 8 B (null >99% of vanilla states)
    public HotStateHandle StateHandle;  // 12 B
    public int ParentId;                // 4 B (-1 for root)
    public int ChoiceOffset;            // 4 B (-1 when ChoiceCount == 0)
    public int HpLost;                  // 4 B (cumulative unblocked HP lost from root)
    public int HpRecovered;             // 4 B (cumulative HP recovered from root)
    public int PlayerHp;                // 4 B
    public int EnemyHp;                 // 4 B
    public int SetupScore;              // 4 B (legacy heuristic setup score)
    public int BoardInvestment;         // 4 B (Niche 4: unclamped domain-agnostic persistent board investment)
    public int MaxDarkEvoke;            // 4 B (unclamped max Dark orb evoke charge for progress tracking)
    public int TotalPoisonAndDoom;      // 4 B (unclamped sum of enemy Poison + Doom for progress tracking)
    public short PlayerMaxHpDelta;      // 2 B (SIGNED: initialMaxHp - currentMaxHp; negative = MaxHp gain e.g. FruitJuice)
    public short PlayerBlock;           // 2 B (clamped to short.MaxValue)
    public short EffectiveDefense;      // 2 B (Niche 2: PlayerBlock + Plating/Metallicize + Frost + Wuppy + Osty)
    public short OptionalityScore;      // 2 B (Niche 5: Energy + Stars + PlayableHandCount + DrawNextTurn)
    public short OutstandingStolen;     // 2 B (stolen gold + stolen cards, clamped)
    public short GrowthHpCredit;        // 2 B
    public short PotionStrategicCost;   // 2 B (exact sum of paid optional root potion HP costs; 0 for Forced/Token/Generated)
    public short DeathSaveHpRestored;   // 2 B
    public ushort PotionMask;           // 2 B (root potion slots consumed; NEVER set for combat-generated potions)
    public ushort ActionCount;          // 2 B
    public ushort GrowthRewardCount;    // 2 B
    public HotNodeFlags Flags;          // 2 B
    public HotAction Action;            // 3 B (Kind, SlotOrHandIdx, TargetEnemySlot)
    public byte ChoiceCount;            // 1 B (0..HotLimits.MaxChoicesPerAction)
    public byte PotionCount;            // 1 B (total potions used incl. automatic)
    public byte ExplicitPotionCount;    // 1 B (manual UsePotion actions)
    public byte OptionalPaidPotionCount;// 1 B (paid optional root potions used)
    public byte AmbergrisCount;         // 1 B
    public byte AlchemizeCount;         // 1 B (s.Growth[4] clamped for slot-refill accounting)
    public byte EmptyPotionSlots;       // 1 B (open slots in belt after this node)
    public byte Energy;                 // 1 B (clamped to byte.MaxValue)
    public byte Turn;                   // 1 B (PlayerTurnNumber clamped 0..255)
    public byte ActionsThisTurn;        // 1 B (per-turn loop guard)
    public byte AliveEnemyCount;        // 1 B
    public byte RevivingEnemyCount;     // 1 B (enemies in HotDeathPhase.Reviving e.g. Decimillipede Reattach)
    public byte PlayableHandCount;      // 1 B (playable cards in hand for mid-turn combo diversity)
    public byte DeathSaveUseCount;      // 1 B
    public HotCombatOutcome Outcome;    // 1 B

    public readonly bool IsVictory => Outcome == HotCombatOutcome.Victory && PlayerHp > 0;
    public readonly bool IsDefeat => Outcome == HotCombatOutcome.Defeat || PlayerHp <= 0;
    public readonly bool IsEventDefeat => Outcome == HotCombatOutcome.EventDefeat && PlayerHp > 0;
    public readonly bool IsTerminal => (Flags & HotNodeFlags.IsTerminal) != 0 || Outcome != HotCombatOutcome.Ongoing;
    public readonly bool HasRolloutScore => (Flags & HotNodeFlags.HasRolloutScore) != 0;
    public readonly bool HasChoiceLookahead => (Flags & HotNodeFlags.HasChoiceLookahead) != 0;

    /// <summary>
    /// Overflow-safe exact integer evaluation of <c>0.35 * Score + 0.65 * RolloutScore</c>
    /// when <see cref="HasRolloutScore"/> is set; otherwise returns <see cref="Score"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly long EffectiveTurnBoundaryScore()
    {
        if (!HasRolloutScore)
            return Score;
        long q = (Score / 100L) * 35L + (RolloutScore / 100L) * 65L;
        long r = (Score % 100L) * 35L + (RolloutScore % 100L) * 65L;
        return q + (r / 100L);
    }
}

/// <summary>
/// Chunked rented storage for <see cref="HotSearchNode"/> and flat <see cref="HotChoiceSelection"/> slices.
/// Uses fixed 4,096-element chunks from <see cref="ArrayPool{T}.Shared"/> to prevent LOH fragmentation
/// and array copy spikes while returning all buffers cleanly on reset/dispose.
/// </summary>
internal sealed class HotNodeArena : IDisposable
{
    public const int ChunkShift = 12;
    public const int ChunkSize = 1 << ChunkShift; // 4,096 elements per chunk
    public const int ChunkMask = ChunkSize - 1;

    private readonly List<HotSearchNode[]> _nodeChunks = [];
    private readonly List<HotChoiceSelection[]> _choiceChunks = [];
    private int _nodeCount;
    private int _choiceCount;
    private bool _disposed;

    public HotNodeArena()
    {
        _nodeChunks.Add(ArrayPool<HotSearchNode>.Shared.Rent(ChunkSize));
        _choiceChunks.Add(ArrayPool<HotChoiceSelection>.Shared.Rent(ChunkSize));
    }

    public int Count => _nodeCount;
    public int ChoiceSelectionCount => _choiceCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref HotSearchNode GetRef(int nodeId)
        => ref _nodeChunks[nodeId >> ChunkShift][nodeId & ChunkMask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AddNode(in HotSearchNode node)
    {
        int id = _nodeCount;
        int chunkIdx = id >> ChunkShift;
        if (chunkIdx >= _nodeChunks.Count)
            _nodeChunks.Add(ArrayPool<HotSearchNode>.Shared.Rent(ChunkSize));
        _nodeChunks[chunkIdx][id & ChunkMask] = node;
        _nodeCount = id + 1;
        return id;
    }

    /// <summary>
    /// Gracefully appends a contiguous slice of <see cref="HotChoiceSelection"/>s into the chunked choice slab.
    /// Returns <c>false</c> (without throwing) if <paramref name="choices"/> exceeds <see cref="HotLimits.MaxChoicesPerAction"/>.
    /// </summary>
    public bool TryAppendChoices(ReadOnlySpan<HotChoiceSelection> choices, out int offset)
    {
        int len = choices.Length;
        if (len == 0)
        {
            offset = -1;
            return true;
        }
        if (len > HotLimits.MaxChoicesPerAction)
        {
            offset = -1;
            return false;
        }

        int nextOffset = _choiceCount;
        int inChunk = nextOffset & ChunkMask;
        if (inChunk + len > ChunkSize)
        {
            // Advance cleanly to next chunk boundary so the slice is contiguous.
            nextOffset = ((nextOffset >> ChunkShift) + 1) << ChunkShift;
            inChunk = 0;
        }

        int chunkIdx = nextOffset >> ChunkShift;
        while (chunkIdx >= _choiceChunks.Count)
            _choiceChunks.Add(ArrayPool<HotChoiceSelection>.Shared.Rent(ChunkSize));

        choices.CopyTo(_choiceChunks[chunkIdx].AsSpan(inChunk, len));
        _choiceCount = nextOffset + len;
        offset = nextOffset;
        return true;
    }

    /// <summary>
    /// Appends a contiguous slice of <see cref="HotChoiceSelection"/>s into the chunked choice slab.
    /// </summary>
    public int AppendChoices(ReadOnlySpan<HotChoiceSelection> choices)
    {
        if (!TryAppendChoices(choices, out int offset))
            throw new ArgumentOutOfRangeException(nameof(choices), choices.Length, "Choice count exceeds HotLimits.MaxChoicesPerAction.");
        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<HotChoiceSelection> GetChoices(int choiceOffset, int choiceCount)
    {
        if (choiceOffset < 0 || choiceCount <= 0)
            return ReadOnlySpan<HotChoiceSelection>.Empty;
        return _choiceChunks[choiceOffset >> ChunkShift].AsSpan(choiceOffset & ChunkMask, choiceCount);
    }

    public ReadOnlySpan<HotChoiceSelection> GetNodeChoices(in HotSearchNode node)
        => GetChoices(node.ChoiceOffset, node.ChoiceCount);

    /// <summary>
    /// Extracts the full root-to-leaf path of node IDs in forward execution order (excluding root).
    /// </summary>
    public int[] ExtractActionNodePath(int leafNodeId)
    {
        if (leafNodeId < 0)
            return [];

        int depth = 0;
        for (int cursor = leafNodeId; cursor >= 0;)
        {
            ref readonly HotSearchNode n = ref GetRef(cursor);
            if ((n.Flags & HotNodeFlags.IsRoot) != 0 || n.ParentId < 0)
                break;
            depth++;
            cursor = n.ParentId;
        }

        if (depth == 0)
            return [];

        int[] path = new int[depth];
        int write = depth;
        for (int cursor = leafNodeId; cursor >= 0 && write > 0;)
        {
            ref readonly HotSearchNode n = ref GetRef(cursor);
            if ((n.Flags & HotNodeFlags.IsRoot) != 0 || n.ParentId < 0)
                break;
            path[--write] = cursor;
            cursor = n.ParentId;
        }
        return path;
    }

    /// <summary>
    /// Resets the arena for a subsequent pass, keeping chunk 0 warm and returning extra chunks to the pool.
    /// Clears managed references (<see cref="HotSearchNode.Cold"/>) so dead sidecars are not pinned.
    /// </summary>
    public void Reset()
    {
        if (_disposed)
            return;

        int clearFirst = Math.Min(_nodeCount, ChunkSize);
        if (clearFirst > 0 && _nodeChunks.Count > 0)
            Array.Clear(_nodeChunks[0], 0, clearFirst);

        while (_nodeChunks.Count > 1)
        {
            HotSearchNode[] tail = _nodeChunks[^1];
            _nodeChunks.RemoveAt(_nodeChunks.Count - 1);
            ArrayPool<HotSearchNode>.Shared.Return(tail, clearArray: true);
        }

        while (_choiceChunks.Count > 1)
        {
            HotChoiceSelection[] tail = _choiceChunks[^1];
            _choiceChunks.RemoveAt(_choiceChunks.Count - 1);
            ArrayPool<HotChoiceSelection>.Shared.Return(tail, clearArray: false);
        }

        _nodeCount = 0;
        _choiceCount = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        for (int i = 0; i < _nodeChunks.Count; i++)
            ArrayPool<HotSearchNode>.Shared.Return(_nodeChunks[i], clearArray: true);
        _nodeChunks.Clear();

        for (int i = 0; i < _choiceChunks.Count; i++)
            ArrayPool<HotChoiceSelection>.Shared.Return(_choiceChunks[i], clearArray: false);
        _choiceChunks.Clear();

        _nodeCount = 0;
        _choiceCount = 0;
    }
}
