namespace CombatSolver.HotKernel;

internal static class HotLimits
{
    public const int MaxCards = 256;
    public const int MaxPowers = 96;
    public const int MaxEnemies = 16;
    public const int MaxRelicStates = 64;
    public const int MaxCounters = 64;
    public const int MaxPotions = 12;
    public const int MaxOrbs = 10;
    public const int MaxArrows = 32;
    public const int MaxEnergyCostMods = 4;
    public const int MaxRngStreams = 10;
    public const int MaxMonsterRecentMoves = 8;
    public const int MaxMonsterAiRegisters = 4;
    public const int MaxGrowthSlots = 12; // 11 built-in + 1 spare
    public const int MaxHistoryCounters = 6;
    public const int MaxChoicesPerAction = 24;
    public const int MaxStackAllocElements = 128;

    // 524,288 bytes (or 2 MiB slabs) with slack matching sizeof(CombatHotState)
    public const int ArenaSlabBytes = 2 * 1024 * 1024;
    public const int MaxPackedStateBytes =
        1128
        + MaxEnemies * 64
        + MaxPowers * 24
        + MaxRelicStates * 8
        + MaxCards * 48;
}

internal enum HotRngStream : byte
{
    Shuffle = 0,
    CombatCardGeneration = 1,
    CombatPotionGeneration = 2,
    CombatCardSelection = 3,
    CombatEnergyCosts = 4,
    CombatTargets = 5,
    CombatOrbGeneration = 6,
    MonsterAi = 7,
    Niche = 8,
    NonCombatGeneration = 9,
}

internal enum HotPileKind : byte
{
    Hand = 0,
    Draw = 1,
    Discard = 2,
    Exhaust = 3,
    Play = 4,
}

internal enum HotCombatSide : byte
{
    Player = 0,
    Enemy = 1,
}

internal enum HotCombatOutcome : byte
{
    Ongoing = 0,
    Victory = 1,
    Defeat = 2,
    EventDefeat = 3,
}

internal enum HotDeathPhase : byte
{
    None = 0,
    Reviving = 1,
    PermanentlyDead = 2,
}

[Flags]
internal enum HotCardKeywordMask : byte
{
    None = 0,
    Exhaust = 1 << 0,
    Ethereal = 1 << 1,
    Innate = 1 << 2,
    Unplayable = 1 << 3,
    Retain = 1 << 4,
    Sly = 1 << 5,
    Eternal = 1 << 6,
}

[Flags]
internal enum HotCardFlags : ushort
{
    None = 0,
    CostsX = 1 << 0,
    HasStarCostX = 1 << 1,
    ExhaustOnNextPlay = 1 << 2,
    IsSlyThisTurn = 1 << 3,
    SingleTurnRetain = 1 << 4,
    HasDeckVersion = 1 << 5,
    RemovedFromState = 1 << 6,
    IsDupe = 1 << 7,
    IsClone = 1 << 8,
    CheckedPowerAfflictionEntry = 1 << 9,
    ReturnToHandNextTurn = 1 << 10,
    FetchPlayedThisTurn = 1 << 11,
    SovereignBladeCreatedThroughForge = 1 << 12,
    HasCostOverflow = 1 << 13,
    // Bits 14..15: HistoryCourseRole (0 = None, 1 = LastAttackThisTurn, 2 = LastAttackPreviousTurn)
}

[Flags]
internal enum HotCreatureFlags : byte
{
    None = 0,
    Alive = 1 << 0,
    ActiveInRoster = 1 << 1,
    IsPlayer = 1 << 2,
    IsOsty = 1 << 3,
    IsPet = 1 << 4,
    Escaped = 1 << 5,
}

[Flags]
internal enum HotEnemyFlags : byte
{
    None = 0,
    NeedsInitialRoll = 1 << 0,
    SkipNextMove = 1 << 1,
    Stunned = 1 << 2,
    Awake = 1 << 3,
    Hatched = 1 << 4,
    BeetleCharged = 1 << 5,
    AmalgamDied = 1 << 6,
    HasSummoned = 1 << 7,
}

[Flags]
internal enum HotPowerFlags : byte
{
    None = 0,
    SkipNextDurationTick = 1 << 0,
    WasJustAppliedByEnemy = 1 << 1,
    RetiredRootSlot = 1 << 2,
    Instanced = 1 << 3,
    BoolAux = 1 << 4,
}

[Flags]
internal enum HotCombatFlags : uint
{
    None = 0,
    IsInProgress = 1 << 0,
    IsAboutToLose = 1 << 1,
    PlayerHooksActive = 1 << 2,
    SwordSageCardsInitialized = 1 << 3,
    BattlewornDummyTimedOut = 1 << 4,
    EncounterGoldForfeited = 1 << 5,
    HasOsty = 1 << 6,
    HasWuppy = 1 << 7,
    HasHistoryCourseCard = 1 << 8,
    PendingTurnSetup = 1 << 9,
}

internal sealed class HotCapacityExceededException(string reason) : Exception(reason);
