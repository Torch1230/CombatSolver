using System.Runtime.InteropServices;

namespace CombatSolver.HotKernel;

[StructLayout(LayoutKind.Sequential)]
internal struct CombatHotState
{
    public const int FixedCoreSize = 1128;

    // 0..16 (align 8)
    public ulong ActiveHookMask;
    public uint NextCreatureId;
    public int RoundNumber;

    // 16..80 (16 ints = 64 B)
    public int PlayerTurnNumber;
    public int ShuffleEventCount;
    public int Energy;
    public int Stars;
    public int Gold;
    public int DrawNextTurn;
    public int LongTermResourceValue;
    public int AngerCopiesGenerated;
    public int DeathSaveRelicHpRestored;
    public int DeathSavePotionHpRestored;
    public int DeathSaveUseCount;
    public int BrightestFlameMaxHpSpent;
    public int OutstandingStolenGold;
    public int OutstandingStolenCards;
    public int LastNormalizedVitalSparkAmount;
    public int ShowOffRewards;

    // 80..140 (3 x 20 B = 60 B)
    public HotCreature Player;
    public HotCreature Osty;
    public HotCreature Wuppy;

    // 140..144 (4 B -> total 144, % 8 == 0)
    public HotCombatFlags CombatFlags;

    // 144..168 (6 ints = 24 B -> total 168, % 8 == 0)
    public int HistoryFinishedPlays;
    public int HistoryEtherealPlays;
    public int HistoryLightningChannels;
    public int HistoryUnblockedHitsReceived;
    public int HistoryCardsDrawn;
    public int HistoryCardsGenerated;

    // 168..208 (8 ushorts + Growth[12] = 40 B -> total 208, % 8 == 0)
    public ushort HandCount;
    public ushort DrawCount;
    public ushort DiscardCount;
    public ushort ExhaustCount;
    public ushort PlayCount;
    public ushort NextCardUid;
    public ushort HistoryCourseUid;
    public ushort PadCore0;
    public HotUShort12 Growth;

    // 208..224 (16 bytes -> total 224, % 8 == 0)
    public HotCombatSide CurrentSide;
    public byte PlayerPhase;
    public byte TerminalTurn;
    public HotCombatOutcome TerminalOutcome;
    public byte MaxHandSize;
    public byte EnemyCount;
    public byte RosterCount;
    public byte PowerCount;
    public byte RelicStateCount;
    public byte OrbCount;
    public byte OrbCapacity;
    public byte PotionSlotCount;
    public byte ArrowCount;
    public byte PadCore1;
    public byte PadCore2;
    public byte PadCore3;

    // 224..480 (64 ints = 256 B)
    public HotInt64 Counters;

    // 480..880 (10 x 40 B = 400 B)
    public HotRng10 Rng;

    // 880..904 (12 ushorts = 24 B)
    public HotUShort12 Potions;

    // 904..984 (10 x 8 B = 80 B)
    public HotOrb10 Orbs;

    // 984..1064 (80 B)
    public HotFoolState Fool;

    // 1064..1080 (16 bytes)
    public HotByte16 ActiveEnemyRoster;

    // 1080..1128 (48 B)
    public HotCard HistoryCourseCard;

    // ---- Dynamic packed tail starts at offset 1128 ----
    // 1128..2152 (16 x 64 B = 1,024 B)
    public HotEnemy16 Enemies;

    // 2152..3688 (64 x 24 B = 1,536 B)
    public HotPower64 Powers;

    // 3688..3944 (32 x 8 B = 256 B)
    public HotRelicState32 RelicStates;

    // 3944..16232 (256 x 48 B = 12,288 B)
    public HotCard256 Cards;

    public readonly int TotalCardCount => HandCount + DrawCount + DiscardCount + ExhaustCount + PlayCount;

    public readonly int DrawStart => HandCount;
    public readonly int DiscardStart => HandCount + DrawCount;
    public readonly int ExhaustStart => HandCount + DrawCount + DiscardCount;
    public readonly int PlayStart => HandCount + DrawCount + DiscardCount + ExhaustCount;

    public static unsafe void ValidateLayout()
    {
        AssertEq(sizeof(HotRngState), 40, nameof(HotRngState));
        AssertEq(sizeof(HotCreature), 20, nameof(HotCreature));
        AssertEq(sizeof(HotEnemy), 64, nameof(HotEnemy));
        AssertEq(sizeof(HotPower), 24, nameof(HotPower));
        AssertEq(sizeof(HotEnergyCostMod), 3, nameof(HotEnergyCostMod));
        AssertEq(sizeof(HotEnergyCostMod4), 12, nameof(HotEnergyCostMod4));
        AssertEq(sizeof(HotCard), 48, nameof(HotCard));
        AssertEq(sizeof(HotRelicState), 8, nameof(HotRelicState));
        AssertEq(sizeof(HotOrb), 8, nameof(HotOrb));
        AssertEq(sizeof(HotFoolState), 80, nameof(HotFoolState));

        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(ActiveHookMask)), 0, "Offset.ActiveHookMask");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(Player)), 80, "Offset.Player");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(CombatFlags)), 140, "Offset.CombatFlags");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(HistoryFinishedPlays)), 144, "Offset.HistoryFinishedPlays");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(HandCount)), 168, "Offset.HandCount");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(Growth)), 184, "Offset.Growth");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(CurrentSide)), 208, "Offset.CurrentSide");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(Counters)), 224, "Offset.Counters");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(Rng)), 480, "Offset.Rng");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(Potions)), 880, "Offset.Potions");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(Orbs)), 904, "Offset.Orbs");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(Fool)), 984, "Offset.Fool");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(ActiveEnemyRoster)), 1064, "Offset.ActiveEnemyRoster");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(HistoryCourseCard)), 1080, "Offset.HistoryCourseCard");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(Enemies)), FixedCoreSize, "Offset.Enemies");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(Powers)), 2152, "Offset.Powers");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(RelicStates)), 3688, "Offset.RelicStates");
        AssertEq((int)Marshal.OffsetOf<CombatHotState>(nameof(Cards)), 3944, "Offset.Cards");

        int totalSize = sizeof(CombatHotState);
        AssertEq(totalSize, 16232, nameof(CombatHotState));
        if (FixedCoreSize % 8 != 0)
            throw new InvalidOperationException("FixedCoreSize must be 8-byte aligned.");
        if (totalSize >= 32768)
            throw new InvalidOperationException($"CombatHotState too large for safe CLR margins: {totalSize}");
    }

    private static void AssertEq(int actual, int expected, string name)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Hot layout mismatch for {name}: expected {expected}, actual {actual}.");
    }
}
