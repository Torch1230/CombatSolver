using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation.Compact;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed class SimCreatureState
{
    public Creature Creature { get; }

    private CreatureVitals _values;

    public int CurrentHp { get => _values.CurrentHp; internal set => _values.CurrentHp = value; }

    public int MaxHp => _values.MaxHp;

    public int Block => _values.Block;

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
        _values = new(currentHp, maxHp, block);
        HpDisplay = hpDisplay;
    }

    public bool IsAlive => CurrentHp > 0;

    public bool IsDead => !IsAlive;

    public decimal DamageBlock(decimal amount, ValueProp props)
        => _values.DamageBlock(amount, props.HasFlag(ValueProp.Unblockable));

    public DamageResult LoseHp(decimal amount, ValueProp props)
    {
        HpLossValues result = _values.LoseHp(amount);
        return new DamageResult(Creature, props)
        {
            UnblockedDamage = result.UnblockedDamage,
            WasTargetKilled = result.WasTargetKilled,
            OverkillDamage = result.OverkillDamage
        };
    }

    public void GainBlock(decimal amount)
        => _values.GainBlock(amount);

    public void Heal(decimal amount)
        => _values.Heal(amount);

    public void SetMaxHp(int amount)
        => _values.SetMaxHp(amount);

    internal SimCreatureState Fork(PredictionForkContext context)
    {
        SimCreatureState fork = new(Creature, CurrentHp, MaxHp, Block, HpDisplay);
        context.Register(this, fork);
        return fork;
    }
}
