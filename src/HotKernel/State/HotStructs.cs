using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace CombatSolver.HotKernel;

[StructLayout(LayoutKind.Sequential)]
internal struct HotRngState
{
    public ulong S0;
    public ulong S1;
    public ulong S2;
    public ulong S3;
    public int Counter;
    public uint Pad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HotCreature
{
    public uint CombatId;
    public int CurrentHp;
    public int MaxHp;
    public int Block;
    public HotCreatureFlags Flags;
    public byte SlotIdx;
    public ushort Pad;
}

[InlineArray(8)]
internal struct HotByte8
{
    private byte _element0;
}

[InlineArray(4)]
internal struct HotInt4
{
    private int _element0;
}

[InlineArray(3)]
internal struct HotByte3
{
    private byte _element0;
}

[InlineArray(3)]
internal struct HotUShort3
{
    private ushort _element0;
}

[InlineArray(32)]
internal struct HotUShort32
{
    private ushort _element0;
}

[InlineArray(12)]
internal struct HotUShort12
{
    private ushort _element0;
}

[InlineArray(16)]
internal struct HotByte16
{
    private byte _element0;
}

[InlineArray(64)]
internal struct HotInt64
{
    private int _element0;
}

[InlineArray(10)]
internal struct HotRng10
{
    private HotRngState _element0;
}

[InlineArray(10)]
internal struct HotOrb10
{
    private HotOrb _element0;
}

[InlineArray(HotLimits.MaxEnemies)]
internal struct HotEnemy16
{
    private HotEnemy _element0;
}

[InlineArray(HotLimits.MaxPowers)]
internal struct HotPower96
{
    private HotPower _element0;
}

[InlineArray(HotLimits.MaxRelicStates)]
internal struct HotRelicState64
{
    private HotRelicState _element0;
}

[InlineArray(HotLimits.MaxCards)]
internal struct HotCard256
{
    private HotCard _element0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HotEnemy
{
    public HotCreature Vital;               // 20 B (0..20)
    public ushort MonsterDefId;             // 2 B  (20..22)
    public short CurrentMoveNode;           // 2 B  (22..24)
    public HotDeathPhase DeathPhase;        // 1 B  (24)
    public HotEnemyFlags Flags;             // 1 B  (25)
    public byte SteamPhase;                 // 1 B  (26)
    public byte RecentMoveCount;            // 1 B  (27)
    public short PoweredHitsFromPlayer;     // 2 B  (28..30)
    public short CreatureAttacksThisTurn;   // 2 B  (30..32)
    public ulong EverUsedMovesMask;         // 8 B  (32..40)
    public HotByte8 RecentMoves;            // 8 B  (40..48)
    public HotInt4 AiRegs;                  // 16 B (48..64)
}

[StructLayout(LayoutKind.Sequential)]
internal struct HotPower
{
    public ushort PowerDefId;               // 2 B (0..2)
    public byte OwnerSlot;                  // 1 B (2)   0=Player, 1=Osty, 2=Wuppy, 3..18=Enemies[0..15]
    public byte ApplierSlot;                // 1 B (3)   0xFF=None
    public byte TargetSlot;                 // 1 B (4)   0xFF=None
    public HotPowerFlags Flags;             // 1 B (5)
    public ushort AuxCardUid;               // 2 B (6..8)
    public int Amount;                      // 4 B (8..12)
    public int AmountOnTurnStart;           // 4 B (12..16)
    public int Aux0;                        // 4 B (16..20)
    public int Aux1;                        // 4 B (20..24)
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct HotEnergyCostMod
{
    public short Amount;                    // 2 B
    // Bit 0 (0x01): IsAbsolute
    // Bits 1..2 (0x06): LocalCostModifierExpiration flags directly (0=EndOfCombat, 2=EndOfTurn, 4=WhenPlayed, 6=EndOfTurn|WhenPlayed)
    // Bit 3 (0x08): IsReduceOnly
    public byte PackedFlags;                // 1 B

    public readonly bool IsAbsolute => (PackedFlags & 0x01) != 0;
    public readonly int Expiration => PackedFlags & 0x06;
    public readonly LocalCostModifierExpiration ExpirationEnum => (LocalCostModifierExpiration)(PackedFlags & 0x06);
    public readonly bool ClearsOnTurnEnd => (PackedFlags & (int)LocalCostModifierExpiration.EndOfTurn) != 0;
    public readonly bool ClearsWhenPlayed => (PackedFlags & (int)LocalCostModifierExpiration.WhenPlayed) != 0;
    public readonly bool IsReduceOnly => (PackedFlags & 0x08) != 0;

    public static byte PackFlags(bool isAbsolute, int expiration, bool isReduceOnly)
    {
        if ((expiration & ~0x06) != 0)
            throw new ArgumentOutOfRangeException(
                nameof(expiration),
                expiration,
                "Expiration must be a LocalCostModifierExpiration value (0=EndOfCombat, 2=EndOfTurn, 4=WhenPlayed, 6=EndOfTurn|WhenPlayed).");
        return (byte)((isAbsolute ? 0x01 : 0) | (expiration & 0x06) | (isReduceOnly ? 0x08 : 0));
    }

    public static byte PackFlags(bool isAbsolute, LocalCostModifierExpiration expiration, bool isReduceOnly)
        => PackFlags(isAbsolute, (int)expiration, isReduceOnly);
}

[InlineArray(4)]
internal struct HotEnergyCostMod4
{
    private HotEnergyCostMod _element0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HotCard
{
    public ushort CardDefId;                // 2 B (0..2)
    public ushort CardUid;                  // 2 B (2..4)
    public byte UpgradeLevel;               // 1 B (4)
    public sbyte BaseEnergyCost;            // 1 B (5)
    public sbyte BaseStarCost;              // 1 B (6)
    public byte BaseReplayCount;            // 1 B (7)
    public byte ExtraReplayThisTurn;        // 1 B (8)
    public HotCardKeywordMask LocalKeywordsMask;          // 1 B (9)
    public HotCardKeywordMask KeywordsAddedOutsideMask;   // 1 B (10)
    public HotCardKeywordMask KeywordsRemovedOutsideMask; // 1 B (11)
    public HotCardFlags Flags;              // 2 B (12..14)
    public byte CostModCount;               // 1 B (14)  0..4 inline (when >4, HasCostOverflow flag set)
    // Bit 7: HasTempStarCost, Bit 6: ClearsWhenTurnEnds, Bit 5: ClearsWhenCardIsPlayed, Bits 0..4: Cost (0..31)
    public byte TempStarCostPacked;         // 1 B (15)
    public HotEnergyCostMod4 EnergyCostMods;// 12 B (16..28)
    public byte EnchId;                     // 1 B (28)  0=None
    public byte EnchStatus;                 // 1 B (29)
    public short EnchAmt;                   // 2 B (30..32)
    public byte AfflId;                     // 1 B (32)  0=None
    public byte DampenOrigUpg;              // 1 B (33)
    public short AfflAmt;                   // 2 B (34..36)
    public short Var0Delta;                 // 2 B (36..38)
    public short Var1Delta;                 // 2 B (38..40)
    public short EnchPrivate;               // 2 B (40..42)
    public short CapturedEnergy;            // 2 B (42..44)
    public int CardPrivate;                 // 4 B (44..48)
}

[StructLayout(LayoutKind.Sequential)]
internal struct HotRelicState
{
    public int Current;
    public int PreviousOrAux;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HotOrb
{
    public byte OrbTypeId;                  // 1 B (0)  0=None
    public byte Flags;                      // 1 B (1)
    public short PassiveVal;                // 2 B (2..4)
    public int EvokeVal;                    // 4 B (4..8)
}

[StructLayout(LayoutKind.Sequential)]
internal struct HotFoolState
{
    // STS2 0.114.0 ArrowSlot / ArrowStatus bit layout inside Arrows[i]:
    //   bits 0..3   (0x000F): ArrowStatus (0 = Retrieved, 1 = Away; Destroyed removed in 0.114.0)
    //   bit  4      (0x0010): retired (DestroyAfterImpaled removed in 0.114.0; always 0)
    //   bits 5..9   (0x03E0): ImpaledEnemySlot + 1 (0 = not impaled, 1..16 = enemy slots 0..15)
    //   bits 10..15 (0xFC00): unused (always 0)
    public const ushort ArrowStatusMask = 0x000F;
    public const int ArrowEnemyShift = 5;
    public const ushort ArrowEnemyMask = 0x001F;
    public const ushort ArrowValidMask = ArrowStatusMask | (ArrowEnemyMask << ArrowEnemyShift);

    public HotUShort32 Arrows;              // 64 B (0..64)  low 4b=Status, b4=0 (retired), b5..9=ImpaledEnemySlot+1
    public HotByte3 MelodyStarted;          // 3 B  (64..67) Wah/Cho/Ki plays started this turn (0.114.0 MelodyCmd.PlayedThisTurn)
    public HotByte3 MelodyFinished;         // 3 B  (67..70) Wah/Cho/Ki plays finished this turn (history/stamp parity)
    public byte MelodyCardsFinished;        // 1 B  (70)     Melody cards finished this turn (history/heuristic parity)
    public byte Pad0;                       // 1 B  (71)
    public HotUShort3 FirstPlayCardUid;     // 6 B  (72..78) First Wah/Cho/Ki CardUid this turn (MelodyCmd.IsFirstPlayThisTurn)
    public ushort Pad1;                     // 2 B  (78..80)
}
