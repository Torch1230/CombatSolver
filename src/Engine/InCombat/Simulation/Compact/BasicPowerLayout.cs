namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal enum BasicPowerKind { Strength, Dexterity, Weak, Vulnerable, Frail, Poison, BlockNextTurn, ToolsOfTheTrade, PiercingWail, Artifact, Stratagem, Doom, Neurosurge, DieForYou, BorrowedTime, Veilpiercer, Hang, SpiritOfAsh, DanseMacabre, Lethality }
internal readonly record struct BasicPowerDefinition(BasicPowerKind Kind, int Owner, int Amount,
    int Applier, int Order, decimal Multiplier, bool RootSlot, int AmountOnTurnStart = 0, bool SkipNextDurationTick = false,
    int MinimumEnergyCost = 0);
internal readonly record struct BasicPowerValues(int Amount, int Applier, int Order, bool Retired,
    int AmountOnTurnStart = 0, bool SkipNextDurationTick = false);

// Locations and immutable parameters only. The first domain has one instance of each basic
// stat/debuff per creature; creation, stacking and removal write the same reversible workspace.
internal sealed class BasicPowerLayout
{
    private const int Width = 4;
    private readonly BasicPowerDefinition[] _definitions;
    private readonly int[] _beforeCardPowers;
    private readonly int _start, _orderSlot;
    internal int Count => _definitions.Length;
    internal bool HasArtifact { get; }
    internal bool HasGlobalEnergyCosts { get; }
    internal BasicPowerDefinition Definition(int index) => _definitions[index];

    internal BasicPowerLayout(ReversibleValueState state, BasicPowerDefinition[] definitions)
    {
        _definitions = (BasicPowerDefinition[])definitions.Clone();
        _beforeCardPowers = Enumerable.Range(0, definitions.Length).Where(index => definitions[index].Owner == 0
            && definitions[index].Kind is BasicPowerKind.Veilpiercer or BasicPowerKind.SpiritOfAsh or BasicPowerKind.DanseMacabre).ToArray();
        HasGlobalEnergyCosts = definitions.Any(definition => definition.Kind is BasicPowerKind.BorrowedTime or BasicPowerKind.Veilpiercer);
        HasArtifact = definitions.Any(definition => definition.Kind == BasicPowerKind.Artifact);
        _start = state.Allocate(checked(definitions.Length * Width));
        _orderSlot = state.Allocate(1);
        for (int index = 0; index < Count; index++)
        {
            var definition = definitions[index];
            Write(state, index, new(definition.Amount, definition.Applier, definition.Order, false,
                definition.AmountOnTurnStart, definition.SkipNextDurationTick));
            if (definition.Order > state[_orderSlot]) state.Write(_orderSlot, definition.Order);
        }
    }

    internal BasicPowerValues Read(ReversibleValueState state, int index)
    {
        int offset = _start + index * Width;
        long metadata = state[offset + 3];
        return new((int)state[offset], (int)state[offset + 1], (int)state[offset + 2], (metadata & 1) != 0,
            (int)(metadata >> 32), (metadata & 2) != 0);
    }

    private void Write(ReversibleValueState state, int index, BasicPowerValues values)
    {
        int offset = _start + index * Width;
        state.Write(offset, values.Amount); state.Write(offset + 1, values.Applier);
        state.Write(offset + 2, values.Order);
        state.Write(offset + 3, (long)values.AmountOnTurnStart << 32
            | (values.Retired ? 1L : 0L) | (values.SkipNextDurationTick ? 2L : 0L));
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

    internal int NextBeforeCardPower(ReversibleValueState state, int afterOrder)
    {
        int selected = -1, order = int.MaxValue;
        foreach (int index in _beforeCardPowers)
        {
            var value = Read(state, index);
            if (value.Amount > 0 && value.Order > afterOrder && (selected < 0 || value.Order < order))
            { selected = index; order = value.Order; }
        }
        return selected;
    }

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
            before.Retired || before.Amount != 0 && after == 0 && _definitions[index].RootSlot,
            before.Amount == 0 ? 0 : before.AmountOnTurnStart,
            before.Amount == 0 ? _definitions[index].Owner == 0 && IsDebuff(_definitions[index].Kind, after) : before.SkipNextDurationTick));
    }

    internal static bool IsDuration(BasicPowerKind kind) => kind is BasicPowerKind.Weak or BasicPowerKind.Vulnerable or BasicPowerKind.Frail;
    internal static bool IsDebuff(BasicPowerKind kind, int amount)
        => kind is BasicPowerKind.Strength or BasicPowerKind.Dexterity ? amount < 0
            : kind is BasicPowerKind.Weak or BasicPowerKind.Vulnerable or BasicPowerKind.Frail or BasicPowerKind.Poison
                or BasicPowerKind.PiercingWail or BasicPowerKind.Doom or BasicPowerKind.Neurosurge or BasicPowerKind.BorrowedTime or BasicPowerKind.Hang;

    internal void CaptureTurnStart(ReversibleValueState state, int owner)
    {
        for (int index = 0; index < Count; index++)
        {
            var before = Read(state, index);
            if (_definitions[index].Owner == owner && before.Amount != 0)
                Write(state, index, before with { AmountOnTurnStart = before.Amount });
        }
    }

    internal void ClearDurationSkip(ReversibleValueState state, int index)
        => Write(state, index, Read(state, index) with { SkipNextDurationTick = false });

    internal void RemoveOwner(ReversibleValueState state, int owner)
    {
        for (int index = 0; index < Count; index++)
        {
            if (_definitions[index].Owner != owner || _definitions[index].Kind == BasicPowerKind.DieForYou) continue;
            var before = Read(state, index);
            if (before.Amount != 0)
                Write(state, index, before with { Amount = 0, Order = 0, Retired = before.Retired || _definitions[index].RootSlot });
        }
    }

    internal decimal ModifyAttack(ReversibleValueState state, int dealer, int target, decimal amount,
        BasicPowerKind? cardMultiplier = null, bool firstCardAttack = false)
    {
        amount += Amount(state, dealer, BasicPowerKind.Strength);
        // Definitions retain captured listener order. Only enemy Weak can be newly created;
        // its owner position stays after the captured player's Vulnerable for monster attacks.
        // Player Weak and enemy Vulnerable remain captured for player attacks.
        for (int index = 0; index < Count; index++)
        {
            var definition = _definitions[index];
            int current = Read(state, index).Amount;
            if (current == 0) continue;
            if (definition.Kind == BasicPowerKind.Weak && definition.Owner == dealer
                || definition.Kind == BasicPowerKind.Vulnerable && definition.Owner == target)
                amount *= definition.Multiplier;
            if (definition.Kind == cardMultiplier && definition.Owner == target)
                amount *= current;
            if (firstCardAttack && definition.Kind == BasicPowerKind.Lethality && definition.Owner == 0)
                amount *= 1m + current / 100m;
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
