namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal sealed partial class ResumableDiscardProgram
{
    internal bool HasMonsterAi => _monsterAi != null;
    internal int CurrentMonsterMove => _monsterAi?.Current(State)
        ?? throw new InvalidOperationException("Monster AI was not admitted.");
    internal int MonsterMoveLogCount => _monsterAi?.LogCount(State)
        ?? throw new InvalidOperationException("Monster AI was not admitted.");
    internal int MonsterMoveLogAt(int index) => _monsterAi!.LogAt(State, index);
    // The old round protocol publishes new intent membership only after setup choices finish.
    internal int PublishedMonsterIntentMove => RoundInProgress ? _monsterAi!.Previous(State) : CurrentMonsterMove;

    // This is the selection boundary after an already completed move, matching the
    // existing round driver's AdvanceMonsterAi contract. It does not perform a move.
    internal void AdvanceMonsterMove(int owner)
    {
        if (_monsterAi == null || owner != _monsterAi.Owner || !Complete || Ending || Terminal
            || !CreaturePresent(owner) || Creature(owner).CurrentHp <= 0)
            throw new InvalidOperationException("Monster advance requires an admitted idle graph and living owner.");
        _monsterAi.Advance(State);
    }

    // Negative event sources encode a creature as -index-1; card instances keep their
    // existing nonnegative identity. Generated events still identify the new card.
    internal void ExecuteMonsterMove(int owner, int moveIndex)
    {
        if (_monsterMoves == null || !Complete || Terminal || Ending || owner <= 0 || owner >= CreatureCount
            || !CreaturePresent(owner) || Creature(owner).CurrentHp <= 0 || (uint)moveIndex >= (uint)_monsterMoves.Length)
            throw new InvalidOperationException($"Monster command requires an admitted idle root, living owner and captured move: owner={owner}, move={moveIndex}, admitted={_monsterMoves != null}, complete={Complete}, terminal={Terminal}, ending={Ending}.");
        int source = -owner - 1;
        var move = _monsterMoves[moveIndex];
        for (int index = 0; index < move.Count; index++)
        {
            var instruction = move[index];
            switch (instruction.Kind)
            {
                case MonsterInstructionKind.AttackPlayer:
                    AttackCreature(source, owner, 0, instruction.Amount);
                    break;
                case MonsterInstructionKind.GainBlock:
                    GainCreatureBlock(source, owner, _powers!.ModifyBlock(State, owner, instruction.Amount));
                    break;
                case MonsterInstructionKind.GainStrength:
                    if (PreparePower(source, owner, BasicPowerKind.Strength, instruction.Amount))
                        CommitPower(source, owner, BasicPowerKind.Strength, instruction.Amount, owner);
                    break;
                case MonsterInstructionKind.GenerateCards:
                    GenerateCards(instruction.CardTemplate, instruction.Amount, creator: -1);
                    break;
                default:
                    throw new InvalidOperationException("Unknown captured monster command.");
            }
        }
    }

    private void GenerateCards(int template, int count, int creator)
    {
        for (int index = 0; index < count && !Ending; index++)
        {
            int created = CardCount;
            Card definition = _definitions[template];
            _cardInstances.Append(State, [(long)(uint)template | (long)definition.CapturedX << 32]);
            Pile destination = Count(Pile.Hand) < 10 ? Pile.Hand : Pile.Discard;
            _piles[(int)destination].Append(State, [created]);
            Emit(EventKind.Generated, created, template, target: creator);
        }
    }
}
