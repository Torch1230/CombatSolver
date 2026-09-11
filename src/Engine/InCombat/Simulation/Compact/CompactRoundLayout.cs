namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal readonly record struct CompactRoundRoot(int Round, int PlayerTurn, int MaxEnergy, int BaseDraw, int TurnStartSummon = 0,
    int BeforeHandDrawPool = -1);

// The admitted domain starts in player choice, has one deterministic enemy and no
// extra turns or player Poison. Only player hand draw / Tools / Sly can suspend.
internal sealed class CompactRoundLayout
{
    private readonly int _start;
    internal CompactRoundRoot Root { get; }
    internal CompactRoundLayout(ReversibleValueState state, CompactRoundRoot root)
    {
        if (root.Round < 1 || root.PlayerTurn < 1 || root.MaxEnergy is < 0 or > 999_999_999 || root.BaseDraw is < 0 or > 10 || root.TurnStartSummon is < 0 or > 999_999_999
            || root.BeforeHandDrawPool < -1)
            throw new ArgumentException("Invalid captured round clock or resources.");
        Root = root;
        _start = state.Allocate(7);
        state.Write(_start, root.Round);
        state.Write(_start + 1, root.PlayerTurn);
        state.Write(_start + 6, 1); // The captured player-choice root has completed side-start hooks.
    }
    internal int Round(ReversibleValueState state) => checked((int)state[_start]);
    internal int PlayerTurn(ReversibleValueState state) => checked((int)state[_start + 1]);
    internal bool EnemySide(ReversibleValueState state) => state[_start + 2] != 0;
    internal bool CleanedCards(ReversibleValueState state) => state[_start + 3] != 0;
    internal int TerminalTurn(ReversibleValueState state) => checked((int)state[_start + 4]);
    internal bool BeganEnemy(ReversibleValueState state) => state[_start + 5] != 0;
    internal void CleanCards(ReversibleValueState state) => state.Write(_start + 3, 1);
    internal bool TryBeginPlayerSideStart(ReversibleValueState state)
    {
        if (state[_start + 6] != 0) return false;
        state.Write(_start + 6, 1);
        return true;
    }
    internal void BeginEnemy(ReversibleValueState state)
    {
        state.Write(_start + 2, 1);
        state.Write(_start + 5, 1);
    }
    internal void BeginPlayer(ReversibleValueState state)
    {
        state.Write(_start, checked(Round(state) + 1));
        state.Write(_start + 1, checked(PlayerTurn(state) + 1));
        state.Write(_start + 2, 0);
        state.Write(_start + 6, 0);
    }
    internal void LockTerminal(ReversibleValueState state)
    {
        if (TerminalTurn(state) == 0) state.Write(_start + 4, PlayerTurn(state));
    }
}
