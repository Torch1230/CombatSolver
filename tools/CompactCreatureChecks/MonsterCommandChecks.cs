using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class MonsterCommandChecks
{
    internal static void Run()
    {
        var wind = new MonsterEffectProgram([new(MonsterInstructionKind.GainBlock, 15), new(MonsterInstructionKind.GainStrength, 5)]);
        var flame = new MonsterEffectProgram([new(MonsterInstructionKind.AttackPlayer, 8), new(MonsterInstructionKind.GenerateCards, 4, 10)]);
        var powers = Enumerable.Range(0, 3).SelectMany(owner => Enum.GetValues<BasicPowerKind>()
            .Select(kind => new BasicPowerDefinition(kind, owner, (owner, kind) switch
            {
                (0, BasicPowerKind.Vulnerable) => 1, (1, BasicPowerKind.Strength) => 2,
                (1, BasicPowerKind.Weak) or (1, BasicPowerKind.Frail) => 1, (1, BasicPowerKind.Dexterity) => 3,
                _ => 0
            }, 0, owner + 1, kind == BasicPowerKind.Weak ? 0.75m : kind == BasicPowerKind.Vulnerable ? 1.5m : 1m, true))).ToArray();
        var block = new ResumableDiscardProgram.Card(0, new([new(CardInstructionKind.GainBlock, 2)]));
        var burn = new ResumableDiscardProgram.Card(-1, CardEffectProgram.Empty, Category: CardCategory.Status, HandEndDamage: 2, Unplayable: true);
        var lane = new ResumableDiscardProgram(Enumerable.Repeat(block, 10).ToArray(), [Enumerable.Range(0, 10).ToArray(), [], [], [], []], 3, 3, 0,
            creatures: [new(50, 60, 3), new(60, 60, 0), new(60, 60, 0)], powers: powers, generatedCards: [burn], monsterMoves: [wind, flame]);
        var root = lane.Freeze(); var mark = lane.State.Mark();
        lane.ExecuteMonsterMove(1, 0); lane.ExecuteMonsterMove(2, 0); lane.ExecuteMonsterMove(1, 1);
        var first = lane.Freeze();
        if (lane.Creature(1).Block != 13 || lane.Creature(2).Block != 15 || lane.Creature(0).CurrentHp != 37
            || lane.Creature(0).Block != 0 || lane.CardCount != 14 || lane.Count(ResumableDiscardProgram.Pile.Discard) != 4
            || lane.Power(Array.FindIndex(powers, p => p.Owner == 1 && p.Kind == BasicPowerKind.Strength)).Applier != 0
            || lane.Power(Array.FindIndex(powers, p => p.Owner == 2 && p.Kind == BasicPowerKind.Strength)).Applier != 2
            || lane.CardValuesInvariant)
            throw new InvalidOperationException("Monster block/stat ownership or first full-hand generation differs.");
        Continue(lane);
        var final = lane.Freeze();
        var events = Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).ToArray();
        if (lane.Creature(0).CurrentHp != 23 || lane.Creature(0).Block != 0 || lane.CardCount != 18
            || !lane.Cards(ResumableDiscardProgram.Pile.Discard).SequenceEqual([10, 11, 12, 13, 0, 15, 16, 17])
            || lane.CardAt(ResumableDiscardProgram.Pile.Hand, 9) != 14
            || events.Count(e => e.Kind == ResumableDiscardProgram.EventKind.AttackFinish && e.Card == -2) != 2
            || events.Where(e => e.Kind == ResumableDiscardProgram.EventKind.Damage).Any(e => e.Card != -2 || e.Target != 0
                || (e.Flags & (int)ResumableDiscardProgram.DamageTraits.NoCard) == 0)
            || events.Where(e => e.Kind == ResumableDiscardProgram.EventKind.Generated).Any(e => e.Target != -1))
            throw new InvalidOperationException("Monster continuation, command sources or creator-less generation differs.");
        lane.State.Rollback(mark);
        if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze())) throw new InvalidOperationException("Monster commands failed to roll back.");
        Parallel.For(0, 8, _ =>
        {
            var worker = first.Open(); Continue(worker);
            if (!worker.State.Freeze().ContentEquals(final.Open().State.Freeze())) throw new InvalidOperationException("Monster worker continuation differs.");
            root.RestoreInto(worker);
            if (!worker.State.Freeze().ContentEquals(root.Open().State.Freeze())) throw new InvalidOperationException("Monster root retained generated cards or values.");
        });
        var fatal = new ResumableDiscardProgram([block], [[0], [], [], [], []], 3, 0, 0,
            creatures: [new(1, 10, 0), new(60, 60, 0), new(60, 60, 0)], powers: powers, generatedCards: [burn],
            monsterMoves: [new([new(MonsterInstructionKind.AttackPlayer, 8), new(MonsterInstructionKind.GenerateCards, 4, 1)])]);
        fatal.ExecuteMonsterMove(1, 0);
        if (fatal.CardCount != 1 || !fatal.Ending || fatal.Terminal || !fatal.CreaturePresent(0)
            || !fatal.CheckWinCondition() || !fatal.DefeatTerminal)
            throw new InvalidOperationException("Fatal monster damage generated cards or lost its defeat boundary.");
        Console.WriteLine("COMPACT_MONSTER_COMMAND_CHECKS_OK block_modifiers=true strength_appliers=true powered_sources=true no_creator=true full_hand=true interleaved_card=true fatal_generation_gate=true rollback=true frozen_workers=8");
    }

    private static void Continue(ResumableDiscardProgram lane)
    {
        lane.Begin(0); lane.Run(); lane.ExecuteMonsterMove(1, 1);
    }
}
