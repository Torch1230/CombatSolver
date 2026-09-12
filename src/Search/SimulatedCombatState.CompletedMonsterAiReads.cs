using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    internal BranchMonsterAiState RequireCapturedMonsterAi(Creature creature)
        => _monsterAiStates?.TryGetValue(creature, out var state) == true ? state
            : throw new InvalidOperationException("Completed AI reads require an already captured monster root.");

    // Owned by one synchronous evaluation lane. These records and their mutable log
    // are a read projection only, never an execution candidate or a retained fork.
    internal sealed class CompletedMonsterAiReadBinding
    {
        private readonly SimulatedCombatState _combat;
        private readonly Creature _owner;
        private readonly BranchMonsterAiState[] _states;
        private readonly List<string> _log = [];

        internal CompletedMonsterAiReadBinding(SimulatedCombatState combat, Creature owner, MoveState[] moves)
        {
            combat.AssertForkable();
            _combat = combat; _owner = owner;
            var source = combat.RequireCapturedMonsterAi(owner);
            _states = moves.Select(move => source with { Current = move, StateLog = _log, NeedsInitialRoll = false }).ToArray();
        }

        internal void Read(int current, ReadOnlySpan<int> log, bool intendsToAttack)
        {
            _log.Clear();
            foreach (int move in log) _log.Add(_states[move].Current.Id);
            (_combat._monsterAiStates ??= [])[_owner] = _states[current];
            _combat._enemiesIntendingAttack ??= [];
            if (intendsToAttack) _combat._enemiesIntendingAttack.Add(_owner);
            else _combat._enemiesIntendingAttack.Remove(_owner);
            _combat._hasPredictedEnemyIntents = true;
        }
    }
}
