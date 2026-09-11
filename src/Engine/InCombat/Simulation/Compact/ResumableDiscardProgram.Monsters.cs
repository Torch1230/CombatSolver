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
        if (_monsterMoves == null || !Complete || Terminal || Ending || owner <= 0 || owner >= EnemyEnd
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
                    // Native AddToCombatAndPreview checks its recipient before creating
                    // cards; the card-level AddGeneratedCards command has a later gate.
                    if (Creature(0).CurrentHp > 0) GenerateCards(instruction.CardTemplate, instruction.Amount, creator: MonsterCreator);
                    break;
                default:
                    throw new InvalidOperationException("Unknown captured monster command.");
            }
        }
    }

    private void GenerateCards(int template, int count, int creator, CardGenerationPlacement placement = CardGenerationPlacement.Hand,
        int endingTemplate = -1)
    {
        for (int index = 0; index < count; index++)
        {
            int created = CardCount;
            // The ending variant models a native upgrade command that returns before it runs
            // while the combat is ending, so only the uninserted identity changes.
            int definitionIndex = Ending && endingTemplate >= 0 ? endingTemplate : template;
            Card definition = _definitions[definitionIndex];
            _cardInstances.Append(State, [new CardInstanceValue(definitionIndex, definition.CapturedX,
                EnchantmentDisabled: definition.EnchantmentInitiallyDisabled).Data]);
            if (Ending)
            {
                // Generation history survives the native ending gate on pile insertion.
                // These identities never entered combat and are not removed card models.
                _piles[(int)Pile.Unplaced].Append(State, [created]);
                Emit(EventKind.Generated, created, -1, target: creator, flags: (int)Pile.Unplaced);
                continue;
            }
            Pile destination = placement == CardGenerationPlacement.RandomDraw ? Pile.Draw
                : Count(Pile.Hand) < 10 ? Pile.Hand : Pile.Discard;
            int position = Count(destination);
            if (placement == CardGenerationPlacement.RandomDraw)
            {
                // Native insertion consumes the shuffle stream even for an empty pile.
                WriteRng(ShuffleRng.NextInt(checked(position + 1), out position));
            }
            _piles[(int)destination].Append(State, [created]);
            for (int offset = Count(destination) - 1; offset > position; offset--)
                _piles[(int)destination].Write(State, offset, CardAt(destination, offset - 1));
            _piles[(int)destination].Write(State, position, created);
            // Template follows the immutable instance definition. Record the resolved
            // location so a compatibility reader never repeats the random command.
            Emit(EventKind.Generated, created, position, target: creator, flags: (int)destination);
        }
    }

    // Native pool generation selects every card with a full-pool shuffle of the frozen
    // candidates on this workspace's exclusive scratch before the whole batch enters a
    // pile; ethereal is part of the template.
    private void GenerateFromPool(int pool, int count, int creator)
    {
        int first = CardCount;
        for (int index = 0; index < count; index++)
        {
            int template = _generation!.SelectOne(State, pool, _generationScratch!.AsSpan(0, _generation.PoolLength(pool)));
            if (template < 0) return; // Native skips generation entirely for an empty pool.
            Card definition = _definitions[template];
            _cardInstances.Append(State, [new CardInstanceValue(template, definition.CapturedX,
                EnchantmentDisabled: definition.EnchantmentInitiallyDisabled).Data]);
        }
        for (int created = first; created < CardCount; created++)
        {
            if (Ending)
            {
                _piles[(int)Pile.Unplaced].Append(State, [created]);
                Emit(EventKind.Generated, created, -1, target: creator, flags: (int)Pile.Unplaced);
                continue;
            }
            // This placement never targets the random draw pile, so an overflowing hand
            // appends at the end of its destination exactly like the template path.
            Pile destination = Count(Pile.Hand) < 10 ? Pile.Hand : Pile.Discard;
            int position = Count(destination);
            _piles[(int)destination].Append(State, [created]);
            Emit(EventKind.Generated, created, position, target: creator, flags: (int)destination);
        }
    }
}
