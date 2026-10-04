using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed class SimCreatureState
{
    /// <summary>
    /// Settlement ceiling for block. Predicted block is an integer in the native game; the
    /// simulation keeps every block value inside this magnitude so arithmetic and casts stay
    /// defined when a modifier chain would otherwise leave the representable range.
    /// </summary>
    internal const decimal BlockSettlementCeiling = 999_999_999m;

    public Creature Creature { get; }

    public int CurrentHp { get; internal set; }

    public int MaxHp { get; private set; }

    public int Block { get; private set; }

    public HpDisplay HpDisplay { get; set; }

    public SimCreatureState(Creature creature)
        : this(creature, creature.CurrentHp, creature.MaxHp, creature.Block, creature.HpDisplay)
    {
    }

    private SimCreatureState(
        Creature creature,
        int currentHp,
        int maxHp,
        int block,
        HpDisplay hpDisplay)
    {
        Creature = creature;
        CurrentHp = currentHp;
        MaxHp = maxHp;
        Block = block;
        HpDisplay = hpDisplay;
    }

    public bool IsAlive => CurrentHp > 0;

    public bool IsDead => !IsAlive;

    public decimal DamageBlock(decimal amount, ValueProp props)
    {
        var blockedDamage = props.HasFlag(ValueProp.Unblockable)
            ? 0m
            : Math.Min(Block, amount);

        Block -= (int)blockedDamage;
        return blockedDamage;
    }

    public DamageResult LoseHp(decimal amount, ValueProp props)
    {
        var wasTargetKilled = CurrentHp > 0 && amount >= CurrentHp;
        var previousHp = CurrentHp;
        var damage = (int)Math.Min(amount, 999999999m);
        CurrentHp = Math.Max(CurrentHp - damage, 0);

        return new DamageResult(Creature, props)
        {
            UnblockedDamage = previousHp - CurrentHp,
            WasTargetKilled = wasTargetKilled,
            OverkillDamage = wasTargetKilled ? Math.Max(damage - previousHp, 0) : 0
        };
    }

    public void GainBlock(decimal amount)
    {
        if (amount < 0m)
        {
            throw new ArgumentException("amount must be positive. Use LoseBlock for block loss.", nameof(amount));
        }

        Block = (int)Math.Min(Block + amount, BlockSettlementCeiling);
    }

    public void Heal(decimal amount)
    {
        if (amount < 0m)
        {
            throw new ArgumentException("amount must be positive.", nameof(amount));
        }

        CurrentHp = (int)Math.Min(CurrentHp + amount, MaxHp);
    }

    public void SetMaxHp(int amount)
    {
        MaxHp = Math.Clamp(amount, 1, 999_999_999);
        CurrentHp = Math.Min(CurrentHp, MaxHp);
    }

    internal SimCreatureState Fork(PredictionForkContext context)
    {
        SimCreatureState fork = new(Creature, CurrentHp, MaxHp, Block, HpDisplay);
        context.Register(this, fork);
        return fork;
    }
}
