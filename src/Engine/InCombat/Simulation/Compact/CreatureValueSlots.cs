namespace CombatSolver.Engine.InCombat.Simulation.Compact;

// A location in a program's immutable layout, never a workspace reference. The owning program
// must supply its own root's workspace. Presence has a separate lifecycle from positive HP.
internal readonly record struct CreatureValueSlots(int Offset)
{
    internal const int Width = 4;

    internal static CreatureValueSlots Allocate(ReversibleValueState state, CreatureVitals values, bool present)
    {
        CreatureValueSlots result = new(state.Allocate(Width));
        result.Write(state, values);
        result.SetPresent(state, present);
        return result;
    }

    internal CreatureVitals Read(ReversibleValueState state)
        => new(checked((int)state[Offset]), checked((int)state[Offset + 1]), checked((int)state[Offset + 2]));

    internal bool IsPresent(ReversibleValueState state) => state[Offset + 3] != 0;

    internal void SetPresent(ReversibleValueState state, bool present) => state.Write(Offset + 3, present ? 1 : 0);

    internal void Write(ReversibleValueState state, CreatureVitals values)
    {
        state.Write(Offset, values.CurrentHp);
        state.Write(Offset + 1, values.MaxHp);
        state.Write(Offset + 2, values.Block);
    }
}
