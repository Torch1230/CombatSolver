namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal enum BasicPowerKind { Strength, Dexterity, Weak, Vulnerable, Frail, Poison, BlockNextTurn, ToolsOfTheTrade, PiercingWail, Artifact }
internal readonly record struct BasicPowerDefinition(BasicPowerKind Kind, int Owner, int Amount,
    int Applier, int Order, decimal Multiplier, bool RootSlot);
internal readonly record struct BasicPowerValues(int Amount, int Applier, int Order, bool Retired);

// Locations and immutable parameters only. The first domain has one instance of each basic
// stat/debuff per creature; creation, stacking and removal write the same reversible workspace.
internal sealed class BasicPowerLayout
{
    private const int Width = 4;
    private readonly BasicPowerDefinition[] _definitions;
    private readonly int _start, _orderSlot;
    internal int Count => _definitions.Length;
    internal bool HasArtifact { get; }
    internal BasicPowerDefinition Definition(int index) => _definitions[index];

    internal BasicPowerLayout(ReversibleValueState state, BasicPowerDefinition[] definitions)
    {
        _definitions = (BasicPowerDefinition[])definitions.Clone();
        HasArtifact = definitions.Any(definition => definition.Kind == BasicPowerKind.Artifact);
        _start = state.Allocate(checked(definitions.Length * Width));
        _orderSlot = state.Allocate(1);
        for (int index = 0; index < Count; index++)
        {
            var definition = definitions[index];
            Write(state, index, new(definition.Amount, definition.Applier, definition.Order, false));
            if (definition.Order > state[_orderSlot]) state.Write(_orderSlot, definition.Order);
        }
    }

    internal BasicPowerValues Read(ReversibleValueState state, int index)
    {
        int offset = _start + index * Width;
        return new((int)state[offset], (int)state[offset + 1], (int)state[offset + 2], state[offset + 3] != 0);
    }

    private void Write(ReversibleValueState state, int index, BasicPowerValues values)
    {
        int offset = _start + index * Width;
        state.Write(offset, values.Amount); state.Write(offset + 1, values.Applier);
        state.Write(offset + 2, values.Order); state.Write(offset + 3, values.Retired ? 1 : 0);
    }

    internal int Find(int owner, BasicPowerKind kind) => FindOrDefault(owner, kind) is var index && index >= 0 ? index
        : throw new InvalidOperationException("Basic Power kind was not allocated for this creature.");

    internal int FindOrDefault(int owner, BasicPowerKind kind)
    {
        for (int index = 0; index < Count; index++)
            if (_definitions[index].Owner == owner && _definitions[index].Kind == kind) return index;
        return -1;
    }

    internal int Amount(ReversibleValueState state, int owner, BasicPowerKind kind) => Read(state, Find(owner, kind)).Amount;

    internal void Apply(ReversibleValueState state, int index, int amount, int applier)
    {
        if (amount == 0) return;
        var before = Read(state, index);
        int after = (int)Math.Clamp((long)before.Amount + amount, -999_999_999L, 999_999_999L);
        int order = before.Order;
        if (before.Amount == 0 && after != 0)
        {
            order = checked((int)state[_orderSlot] + 1);
            state.Write(_orderSlot, order);
        }
        Write(state, index, new(after, before.Amount == 0 ? applier : before.Applier, after == 0 ? 0 : order,
            before.Retired || before.Amount != 0 && after == 0 && _definitions[index].RootSlot));
    }

    internal void RemoveOwner(ReversibleValueState state, int owner)
    {
        for (int index = 0; index < Count; index++)
        {
            if (_definitions[index].Owner != owner) continue;
            var before = Read(state, index);
            if (before.Amount != 0)
                Write(state, index, before with { Amount = 0, Order = 0, Retired = before.Retired || _definitions[index].RootSlot });
        }
    }

    internal decimal ModifyAttack(ReversibleValueState state, int dealer, int target, decimal amount)
    {
        amount += Amount(state, dealer, BasicPowerKind.Strength);
        // Definitions retain captured listener order. New Weak only targets enemies in this
        // domain; it cannot reorder the player's Weak and the target's Vulnerable modifiers.
        for (int index = 0; index < Count; index++)
        {
            var definition = _definitions[index];
            if (Read(state, index).Amount == 0) continue;
            if (definition.Kind == BasicPowerKind.Weak && definition.Owner == dealer
                || definition.Kind == BasicPowerKind.Vulnerable && definition.Owner == target)
                amount *= definition.Multiplier;
        }
        return Math.Max(0m, amount);
    }

    internal decimal ModifyBlock(ReversibleValueState state, int owner, decimal amount)
    {
        amount += Amount(state, owner, BasicPowerKind.Dexterity);
        if (Amount(state, owner, BasicPowerKind.Frail) != 0) amount *= 0.75m;
        return Math.Max(0m, amount);
    }
}
