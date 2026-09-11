namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal readonly record struct PanachePowerValues(int Amount, int Applier, int Order,
    int AmountOnTurnStart = 0, int CardsLeft = 5, bool AlreadyApplied = false, bool SkipNextDurationTick = false);

// Independent player-owned instances, in acquisition order. All fields and the growing
// instance count belong to the reversible buffer; the shared layout retains no cursor.
internal sealed class PanachePowerLayout
{
    private const int Width = 6;
    private readonly ReversibleValueBuffer _values;
    internal int Count(ReversibleValueState state) => _values.Count(state) / Width;

    internal PanachePowerLayout(ReversibleValueState state, ReadOnlySpan<PanachePowerValues> roots)
    {
        _values = new(state);
        foreach (var value in roots) Add(state, value);
    }

    internal PanachePowerValues Read(ReversibleValueState state, int index)
    {
        int offset = checked(index * Width);
        int flags = (int)_values.Read(state, offset + 5);
        return new((int)_values.Read(state, offset), (int)_values.Read(state, offset + 1),
            (int)_values.Read(state, offset + 2), (int)_values.Read(state, offset + 3),
            (int)_values.Read(state, offset + 4), (flags & 1) != 0, (flags & 2) != 0);
    }

    internal void Add(ReversibleValueState state, PanachePowerValues value)
        => _values.Append(state, [value.Amount, value.Applier, value.Order, value.AmountOnTurnStart,
            value.CardsLeft, Flags(value)]);

    internal void Write(ReversibleValueState state, int index, PanachePowerValues value)
    {
        int offset = checked(index * Width);
        _values.Write(state, offset, value.Amount); _values.Write(state, offset + 1, value.Applier);
        _values.Write(state, offset + 2, value.Order); _values.Write(state, offset + 3, value.AmountOnTurnStart);
        _values.Write(state, offset + 4, value.CardsLeft); _values.Write(state, offset + 5, Flags(value));
    }

    private static int Flags(PanachePowerValues value) => (value.AlreadyApplied ? 1 : 0) | (value.SkipNextDurationTick ? 2 : 0);
}
