using System.Runtime.CompilerServices;

namespace CombatSolver.HotKernel;

internal enum HotChoiceKind : byte
{
    None = 0,
    HandCard = 1,
    DrawPile = 2,
    DiscardPile = 3,
    ExhaustPile = 4,
    GeneratedCard = 5,
}

[Flags]
internal enum HotChoiceGroupFlags : byte
{
    None = 0,
    Unordered = 1 << 0,
    Optional = 1 << 1,
    MultiPick = 1 << 2,
}

internal readonly record struct HotChoiceSelection(
    HotChoiceKind Kind,
    ushort CardUidOrIndex,
    ushort CardDefId);

internal struct HotListenerEntry
{
    public byte SourceKind; // 0=Relic, 1=Power, 2=Card, 3=Orb, 4=Dominion
    public ushort DefId;
    public ushort SlotOrIndex;
    public byte OwnerSlot;
}

internal struct HotPendingPowerChange
{
    public ushort PowerDefId;
    public byte OwnerSlot;
    public byte ApplierSlot;
    public int AmountDelta;
    public ushort AuxCardUid;
}

/// <summary>
/// Thread-local / lane-owned reusable scratch memory eliminating stack-overflow risk on large piles
/// and eliminating all per-step heap allocations for choices, shuffles, listeners, and power changes.
/// </summary>
internal sealed class HotExecScratch
{
    public const int MaxListeners = 512;
    public const int MaxPendingPowerChanges = 128;

    public readonly HotCard[] ShuffleCards = new HotCard[HotLimits.MaxCards];
    public readonly HotCard[] ShuffleScratch = new HotCard[HotLimits.MaxCards];
    public readonly int[] ShuffleOriginalIdx = new int[HotLimits.MaxCards];
    public readonly HotListenerEntry[] Listeners = new HotListenerEntry[MaxListeners];
    public readonly HotPendingPowerChange[] PendingPowerChanges = new HotPendingPowerChange[MaxPendingPowerChanges];
    public readonly HotChoiceSelection[] Choices = new HotChoiceSelection[HotLimits.MaxChoicesPerAction];

    public int ChoiceCount;
    public int ChoiceCursor;
    public bool NeedsChoice;
    public HotChoiceKind PendingChoiceKind;
    public ushort PendingChoiceSourceCardDefId;
    public byte PendingChoiceMin;
    public byte PendingChoiceMax;
    public ushort ActiveChoiceGroupSeq;
    public ushort NextChoiceGroupSeq;
    public HotChoiceGroupFlags ActiveChoiceGroupFlags;
    public ushort PendingChoiceGroupSeq;
    public HotChoiceGroupFlags PendingChoiceGroupFlags;

    // Transient attack / play scratch (cleared before every stable boundary)
    public int ActiveDrawDepth;
    public int PendingPowerChangeCount;
    public int LastStarsSpent;
    public bool InPlayerTurnSetup;
    public bool TurnSetupTakingExtraTurn;
    public bool TurnSetupSideStartDone;
    public int CurrentPlayIndex;
    public int CurrentPlayTargetSlot;
    // STS2 0.114.0 UnmovablePower: 0 or 1 indicating whether the active CardPlay has already
    // incremented HotCounterSlot.CardBlockGainsThisTurn (saved/restored across nested AutoPlays).
    public int CardBlockGainsInCurrentPlay;
    public bool UnblockedDamageThisPlay;
    public bool AttackActive;
    public uint AttackUnblockedEnemyMask;
    // STS2 0.114.0 Creature.IsAttacking / MonsterModel.IsPerformingMove deferred death cleanup:
    //   AttackingCreatureSlotMask: bit (1u << slot) set while slot (0=Player, 1=Osty, 2=Wuppy, 3..18=Enemies)
    //     is executing AttackCommand.Execute (save/restore previous bit for nested attacks).
    //   PerformingMoveEnemyMask: bit (1u << enemySlot) set while enemySlot (0..15) executes PerformMove.
    //   DeferredDeathCleanupSlotMask: bit (1u << slot) set when Kill defers RemoveAllPowersAfterDeath /
    //     RemoveCreature / DeactivateHooks until the active attack or monster move completes.
    public uint AttackingCreatureSlotMask;
    public uint PerformingMoveEnemyMask;
    public uint DeferredDeathCleanupSlotMask;
    // Monotonic per-action AttackCommand sequence IDs so powers (e.g. QuietusPower) can latch a
    // specific AttackCommand instance across nested attacks and remove themselves in AfterAttack.
    public int NextAttackCommandSeq;
    public int ActiveAttackCommandSeq;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BeginChoiceGroup(HotChoiceGroupFlags flags = HotChoiceGroupFlags.None)
    {
        ActiveChoiceGroupSeq = ++NextChoiceGroupSeq;
        ActiveChoiceGroupFlags = flags;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EndChoiceGroup()
    {
        ActiveChoiceGroupSeq = 0;
        ActiveChoiceGroupFlags = HotChoiceGroupFlags.None;
    }

    public void BeginAction(ReadOnlySpan<HotChoiceSelection> choices)
    {
        int copyCount = Math.Min(choices.Length, HotLimits.MaxChoicesPerAction);
        if (copyCount > 0)
            choices[..copyCount].CopyTo(Choices);
        ChoiceCount = copyCount;
        ChoiceCursor = 0;
        NeedsChoice = false;
        PendingChoiceKind = HotChoiceKind.None;
        PendingChoiceSourceCardDefId = 0;
        PendingChoiceMin = 0;
        PendingChoiceMax = 0;
        ActiveChoiceGroupSeq = 0;
        NextChoiceGroupSeq = 0;
        ActiveChoiceGroupFlags = HotChoiceGroupFlags.None;
        PendingChoiceGroupSeq = 0;
        PendingChoiceGroupFlags = HotChoiceGroupFlags.None;
        ActiveDrawDepth = 0;
        PendingPowerChangeCount = 0;
        LastStarsSpent = 0;
        InPlayerTurnSetup = false;
        TurnSetupTakingExtraTurn = false;
        TurnSetupSideStartDone = false;
        CurrentPlayIndex = 0;
        CurrentPlayTargetSlot = -1;
        CardBlockGainsInCurrentPlay = 0;
        UnblockedDamageThisPlay = false;
        AttackActive = false;
        AttackUnblockedEnemyMask = 0;
        AttackingCreatureSlotMask = 0;
        PerformingMoveEnemyMask = 0;
        DeferredDeathCleanupSlotMask = 0;
        NextAttackCommandSeq = 0;
        ActiveAttackCommandSeq = 0;
    }
}
