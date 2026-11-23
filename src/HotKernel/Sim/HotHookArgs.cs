namespace CombatSolver.HotKernel;

/// <summary>
/// Zero-allocation mutable hook payload passed across core engine, power, relic, dominion,
/// enchantment, and affliction hook dispatchers.
/// </summary>
internal ref struct HotHookArgs
{
    public int IntValue;
    public decimal DecimalValue;
    public decimal DecimalMultiplier;
    public bool BoolFlag;
    public byte ActorSlot;
    public byte TargetSlot;
    public int CardIdx;
    public ushort CardDefId;
    public ushort PowerDefId;
    public int Amount;
    public bool IsPoweredAttack;
    public bool IsCardBlock;
    public bool IsUnblockable;
    public bool FromHandDraw;
    public bool CausedByEthereal;
    public bool WasBlockBroken;
    public bool WasFullyBlocked;
    public bool WasTargetKilled;
    public int BlockedDamage;
    public int UnblockedDamage;
    public int BlockGained;
    public byte ResultPile;
    public int PlayCount;
    public int EnergySpent;
    public int StarsSpent;
    public int HandDrawCount;
    public int MaxEnergy;
    public int PlayIndex;
    public bool IsAutoPlay;
    public byte OrbTypeId;
    public int OrbValue;
    public int HealAmount;
    public int EtherealExhaustCount;
    public HotCombatSide Side;
}
