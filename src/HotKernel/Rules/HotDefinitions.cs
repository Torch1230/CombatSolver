using System.Runtime.InteropServices;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Powers;

namespace CombatSolver.HotKernel;

/// <summary>
/// Named indices into <see cref="CombatHotState.Counters"/> (64 ints).
/// Shared across root import, simulation engine, and continuation stamp capture.
/// </summary>
internal enum HotCounterSlot : byte
{
    StatusCardsDrawnThisTurn     = 0,
    ZeroCostAttackStartsThisTurn = 1,
    CardPlayStartsThisTurn       = 2,
    AttackSkillStartsThisTurn    = 3,
    AttackPlayStartsThisTurn     = 4,
    SkillPlayStartsThisTurn      = 5,
    PowerPlayStartsThisTurn      = 6,
    CardsExhaustedThisTurn       = 7,
    CardsDiscardedThisTurn       = 8,
    EnergySpentThisTurn          = 9,
    StarsGainedThisTurn          = 10,
    NonHandDrawsThisTurn         = 11,
    HasLostHpThisTurn            = 12,
    WasDoomAppliedThisTurn       = 13,
    AttacksPlayedThisTurn        = 14,
    SkillsPlayedThisTurn         = 15,
    CardsPlayedThisTurn          = 16,
    PlayerCumulativeHpLost       = 17,
    PlayerRecoveredHp            = 18,
    OstyAttacksThisTurn          = 19,
    SadnessGeneratedBefore       = 20,
    BaseMaxEnergy                = 21,
    BaseHandDraw                 = 22,
    CardBlockGainsThisTurn       = 23,
}

internal enum HotCardOpKind : byte
{
    Nop                   = 0,
    Attack                = 1,
    AttackAll             = 2,
    AttackRandom          = 3,
    AttackOsty            = 4,
    GainBlock             = 5,
    Draw                  = 6,
    ApplyPowerSelf        = 7,
    ApplyPowerTarget      = 8,
    ApplyPowerAllEnemies  = 9,
    ApplyTempStat         = 10,
    GainEnergy            = 11,
    GainStars             = 12,
    LoseHp                = 13,
    Heal                  = 14,
    Forge                 = 15,
    ChannelOrb            = 16,
    EvokeOrb              = 17,
    SummonOsty            = 18,
    AddFixedCard          = 19,
    ShootArrow            = 20,
    DiscardHand           = 21,
    Custom                = 22,
}

[Flags]
internal enum HotCardOpFlags : byte
{
    None              = 0,
    UseCardDamage     = 1 << 0,
    UseCardBlock      = 1 << 1,
    UseCardMagic      = 1 << 2,
    UseCardHits       = 1 << 3,
    AddCloneToDiscard = 1 << 4,
    Unpowered         = 1 << 5,
    LiteralAmount     = 1 << 6,
    FixedOne          = 1 << 7,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct HotCardEffectOp
{
    public readonly HotCardOpKind Op;
    public readonly byte Flags;
    public readonly short Arg0;

    public HotCardEffectOp(HotCardOpKind op, byte flags = 0, short arg0 = 0)
    {
        Op = op;
        Flags = flags;
        Arg0 = arg0;
    }

    public HotCardEffectOp(HotCardOpKind op, HotCardOpFlags flags, short arg0 = 0)
    {
        Op = op;
        Flags = (byte)flags;
        Arg0 = arg0;
    }
}

internal enum HotCardPrivateKind : byte
{
    None             = 0,
    Claw             = 1,
    GeneticAlgorithm = 2,
    Maul             = 3,
    MadScience       = 4,
    Rampage          = 5,
    TheScythe        = 6,
    TheShot          = 7,
    ShowOff          = 8,
    SovereignBlade   = 9,
}

internal enum HotPowerSpecialKind : byte
{
    None       = 0,
    Surrounded = 1,
    Nightmare  = 2,
    Orbit      = 3,
    ShowOff    = 4,
    Impaled    = 5,
    Aim        = 6,
    Encore     = 7,
    Yah        = 8,
    Lullaby    = 9,
}

internal enum HotRelicSpecialKind : byte
{
    None             = 0,
    // Stateful relics (emitted in ;relicCounters=)
    ArtOfWar         = 1,
    ConductorsBaton  = 2,
    Wuppy            = 3,
    Byrdpip          = 4,
    BeltBuckle       = 5,
    BoneTea          = 6,
    EmotionChip      = 7,
    FakeHappyFlower  = 8,
    FakeVenerableTeaSet = 9,
    HappyFlower      = 10,
    MiniRegent       = 11,
    GalacticDust     = 12,
    PaelsEye         = 13,
    PaelsTears       = 14,
    RainbowRing      = 15, // Both Stateful AND Tracked!
    RedSkull         = 16,
    Pendulum         = 17,
    Pocketwatch      = 18,
    PollinousCore    = 19,
    RuinedHelmet     = 20,
    UnsettlingLamp   = 21,
    VenerableTeaSet  = 22,
    // Tracked mirror relics (emitted in ;mirrorRelics=)
    BeatingRemnant   = 23,
    BrilliantScarf   = 24,
    BurningSticks    = 25,
    CentennialPuzzle = 26,
    DemonTongue      = 27,
    IronClub         = 28,
    JossPaper        = 29,
    Kunai            = 30,
    Kusarigama       = 31,
    LetterOpener     = 32,
    LizardTail       = 33,
    Metronome        = 34,
    MusicBox         = 35,
    Nunchaku         = 36,
    OrnamentalFan    = 37,
    PaelsLegion      = 38,
    PenNib           = 39,
    Permafrost       = 40,
    Regalite         = 41,
    Shuriken         = 42,
    ThrowingAxe      = 43,
    TuningFork       = 44,
    Vambrace         = 45,
}

internal enum HotMonsterStateKind : byte
{
    None        = 0,
    FrogKnight  = 1,
    TwoTailedRat = 2,
    Fabricator  = 3,
    ToughEgg    = 4,
    TestSubject = 5,
    Tunneler    = 6,
    AwakeCheck  = 7, // LagavulinMatriarch, SlumberingBeetle
    Queen       = 8,
}

internal enum HotMonsterNodeKind : byte
{
    Move              = 0,
    RandomBranch      = 1,
    ConditionalBranch = 2,
}

internal enum HotMonsterConditionalKind : byte
{
    None                    = 0,
    StaticFallback          = 1,
    FrogKnightHalfHealth    = 2,
    FabricatorBranch        = 3,
    TestSubjectRevive       = 4,
    OvicopterSummon         = 5,
    LagavulinSleep          = 6,
    SlumberingBeetleSnore   = 7,
    QueenAmalgam            = 8,
    LivingShieldSlam        = 9,
    BowlbugRockPostHeadbutt = 10,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct HotMonsterMoveEffect
{
    public readonly HotCardOpKind Op;
    public readonly byte Flags;
    public readonly ushort TargetDefId;
    public readonly short Amount;

    public HotMonsterMoveEffect(HotCardOpKind op, ushort targetDefId, short amount, byte flags = 0)
    {
        Op = op;
        Flags = flags;
        TargetDefId = targetDefId;
        Amount = amount;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct HotMonsterAttack
{
    public readonly short BaseDamage;
    public readonly byte Repeats;
    public readonly byte Flags;

    public HotMonsterAttack(short baseDamage, byte repeats, byte flags = 0)
    {
        BaseDamage = baseDamage;
        Repeats = repeats;
        Flags = flags;
    }
}

internal sealed class HotCardDef
{
    public required ushort DefId { get; init; }
    public required ushort CanonicalDefId { get; init; }
    public HotCustomCardId CardId { get; init; }
    public HotCustomCardId CustomCardId { get; init; }
    public HotCardDomain Domain { get; init; }
    public HotCalcVarKind CalcVar { get; init; }
    public required string Entry { get; init; }
    public required CardType Type { get; init; }
    public CardRarity Rarity { get; init; }
    public required TargetType Target { get; init; }
    public required sbyte BaseCost { get; init; }
    public required sbyte UpgradedCost { get; init; }
    public required sbyte BaseStarCost { get; init; }
    public required byte BaseKeywords { get; init; }
    public required short BaseDamage { get; init; }
    public required short UpgradedDamage { get; init; }
    public required short BaseBlock { get; init; }
    public required short UpgradedBlock { get; init; }
    public required short BaseMagic { get; init; }
    public required short UpgradedMagic { get; init; }
    public required byte BaseHits { get; init; }
    public required byte UpgradedHits { get; init; }
    public required HotHookMask HookMask { get; init; }
    public required bool CanHealPlayer { get; init; }
    public required bool CanGenerateUnknown { get; init; }
    public required short SetupWeight { get; init; }
    public required int OpStart { get; init; }
    public required byte OpCount { get; init; }
    public required byte CalcVarKind { get; init; }
    public required ushort CustomHandlerId { get; init; }
    public required string[] OrderedVarNames { get; init; }
    public required decimal[] BaseVarValues { get; init; }
    public required decimal[] UpgradedVarValues { get; init; }
    public string?[]? OrderedVarStrings { get; init; }
    public byte Var0Index { get; init; } = 0xFF;
    public byte Var1Index { get; init; } = 0xFF;
    public required bool IsAllowedInBlitz { get; init; }
    public bool GainsBlock { get; init; }
    public bool CanBeGeneratedInCombat { get; init; } = true;
    public int MaxUpgradeLevel { get; init; } = 1;
    public HotCardPrivateKind PrivateKind { get; init; }
    public bool IsStrikeTag { get; init; }
    public bool IsDefendTag { get; init; }
    public bool IsShootTag { get; init; }
    public bool IsOstyAttackTag { get; init; }
    public bool IsShivTag { get; init; }
    public bool IsMinionTag { get; init; }
    public byte MelodyTagMask { get; init; }
    public bool IsVisualColorless { get; init; }
    public string? RootDupeSourceKey { get; init; }

    public int GetDamage(byte upgradeLevel, short var0Delta)
        => (upgradeLevel > 0 ? UpgradedDamage : BaseDamage) + var0Delta;

    public int GetBlock(byte upgradeLevel, short var1Delta)
        => (upgradeLevel > 0 ? UpgradedBlock : BaseBlock) + var1Delta;

    public int GetMagic(byte upgradeLevel)
        => upgradeLevel > 0 ? UpgradedMagic : BaseMagic;

    public int GetHits(byte upgradeLevel)
        => Math.Max(1, (int)(upgradeLevel > 0 ? UpgradedHits : BaseHits));
}

internal sealed class HotPowerDef
{
    public required ushort DefId { get; init; }
    public HotPowerId PowerId { get; init; }
    public HotPowerDomain Domain { get; init; }
    public required string Entry { get; init; }
    public required PowerType Type { get; init; }
    public required HotHookMask HookMask { get; init; }
    public required bool CanHealPlayer { get; init; }
    public required bool HasRelevantTurnStartAmount { get; init; }
    public required bool HasRelevantSkipDurationTick { get; init; }
    public required short SetupWeight { get; init; }
    public required string[] OrderedVarNames { get; init; }
    public required decimal[] DefaultVarValues { get; init; }
    public string?[]? OrderedVarStrings { get; init; }
    public HotPowerSpecialKind SpecialKind { get; init; }
    public PowerInstanceType InstanceType { get; init; }
    public bool IsInstanced => InstanceType != PowerInstanceType.None;
    public PowerStackType StackType { get; init; }
}

internal sealed class HotRelicDef
{
    public required ushort DefId { get; init; }
    public HotRelicId RelicId { get; init; }
    public required string Entry { get; init; }
    public required HotHookMask HookMask { get; init; }
    public required bool IsStateful { get; init; }
    public required bool IsTracked { get; init; }
    public required bool CanHealPlayer { get; init; }
    public required bool CanGenerateUnknown { get; init; }
    public HotRelicSpecialKind SpecialKind { get; init; }
    public short PrimaryValue { get; init; }
    public short SecondaryValue { get; init; }
}

internal sealed class HotPotionDef
{
    public required ushort DefId { get; init; }
    public HotCustomPotionId PotionId { get; init; }
    public HotCustomPotionId CustomPotionId { get; init; }
    public required string Entry { get; init; }
    public PotionRarity Rarity { get; init; }
    public required TargetType Target { get; init; }
    public required bool CanHealPlayer { get; init; }
    public required bool CanGenerateUnknown { get; init; }
    public short PrimaryValue { get; init; }
    public short SecondaryValue { get; init; }
    public int OpStart { get; init; }
    public byte OpCount { get; init; }
    public short StrategicHpCost { get; init; }
    public bool RequiresOpeningUse { get; init; }
    public bool GeneratesPotionChain { get; init; }
    public bool IsCombatUsable { get; init; } = true;
    public bool CanBeGeneratedInCombat { get; init; } = true;
}

internal sealed class HotOrbDef
{
    public required byte DefId { get; init; }
    public HotOrbId OrbId { get; init; }
    public required string Entry { get; init; }
    public required short BasePassiveVal { get; init; }
    public required int BaseEvokeVal { get; init; }
}

internal sealed class HotEnchantmentDef
{
    public required byte DefId { get; init; }
    public HotEnchantmentId EnchantmentId { get; init; }
    public required string Entry { get; init; }
    public required bool HasMomentumExtraDamage { get; init; }
}

internal sealed class HotAfflictionDef
{
    public required byte DefId { get; init; }
    public HotAfflictionId AfflictionId { get; init; }
    public required string Entry { get; init; }
}

internal sealed class HotMonsterDef
{
    public required ushort DefId { get; init; }
    public HotMonsterId MonsterId { get; init; }
    public required string Entry { get; init; }
    public required string TypeName { get; init; }
    public required HotMonsterStateKind StateKind { get; init; }
    public required int MachineIndex { get; init; }
    public IReadOnlyDictionary<string, int>? StaticInts { get; init; }
    public int TestSubjectBaseMultiClawCount { get; init; }
    public int MinInitialHp { get; init; }
    public int MaxInitialHp { get; init; }
    public bool IsBoss { get; init; }
    public bool IsElite { get; init; }
}

internal sealed class HotMonsterNode
{
    public required short NodeId { get; init; }
    public required string MoveId { get; init; }
    public required HotMonsterNodeKind NodeKind { get; init; }
    public required int BaseDamage { get; init; }
    public required int Repeats { get; init; }
    public HotMonsterAttack[] Attacks { get; init; } = [];
    public bool RemovesOwnerOnMove { get; init; }
    public required short NextNode { get; init; }
    public required short[] BranchTargets { get; init; }
    public required float[] BranchWeights { get; init; }
    public required byte[] BranchRepeatTypes { get; init; }
    public required byte[] BranchMaxTimes { get; init; }
    public required byte[] BranchCooldowns { get; init; }
    public required int Cooldown { get; init; }
    public required bool MustPerformOnceBeforeRepeat { get; init; }
    public required bool CannotRepeat { get; init; }
    public required HotMonsterConditionalKind ConditionalKind { get; init; }
    public required HotMonsterMoveEffect[] MoveEffects { get; init; }

    public bool IsMove => NodeKind == HotMonsterNodeKind.Move;
}

internal sealed class HotMonsterMachine
{
    public required ushort MonsterDefId { get; init; }
    public required string MonsterEntry { get; init; }
    public required short InitialNode { get; init; }
    public required HotMonsterNode[] Nodes { get; init; }
    public required IReadOnlyDictionary<string, short> NodeById { get; init; }

    public short FindNode(string moveId)
        => NodeById.TryGetValue(moveId, out short id) ? id : (short)-1;
}

internal readonly record struct HotActiveRelic(
    ushort RelicDefId,
    byte StateSlot);

internal readonly record struct HotRootPetSnapshot(
    uint CombatId,
    string MonsterEntry,
    int CurrentHp,
    int MaxHp,
    int Block,
    bool IsWuppySlot);

internal sealed class HotCoverageManifest
{
    public required bool[] SupportedCardDefs { get; init; }
    public required bool[] SupportedPowerDefs { get; init; }
    public required bool[] SupportedRelicDefs { get; init; }
    public required bool[] SupportedMonsterDefs { get; init; }
    public required bool IsRootFullySupported { get; init; }
    public required string[] UnsupportedReasons { get; init; }

    public bool IsCardSupported(ushort defId)
        => defId < SupportedCardDefs.Length && SupportedCardDefs[defId];

    public bool IsPowerSupported(ushort defId)
        => defId < SupportedPowerDefs.Length && SupportedPowerDefs[defId];

    public bool IsRelicSupported(ushort defId)
        => defId < SupportedRelicDefs.Length && SupportedRelicDefs[defId];

    public bool IsMonsterSupported(ushort defId)
        => defId < SupportedMonsterDefs.Length && SupportedMonsterDefs[defId];
}
