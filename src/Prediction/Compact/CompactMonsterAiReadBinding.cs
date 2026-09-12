using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace CombatSolver;

internal sealed class CompactMonsterAiReadBinding
{
    private readonly SimulatedCombatState.CompletedMonsterAiReadBinding _binding;
    private readonly bool[] _attacks;
    private int[] _log = [];

    internal CompactMonsterAiReadBinding(SimulatedCombatState combat, Creature owner, MoveState[] moves, bool[] attacks)
    {
        _binding = new(combat, owner, moves);
        _attacks = (bool[])attacks.Clone();
    }

    internal void Read(ResumableDiscardProgram program)
    {
        int count = program.MonsterMoveLogCount;
        if (_log.Length < count) Array.Resize(ref _log, Math.Max(count, Math.Max(8, _log.Length * 2)));
        for (int index = 0; index < count; index++) _log[index] = program.MonsterMoveLogAt(index);
        _binding.Read(program.CurrentMonsterMove, _log.AsSpan(0, count), _attacks[program.PublishedMonsterIntentMove]);
    }
}
