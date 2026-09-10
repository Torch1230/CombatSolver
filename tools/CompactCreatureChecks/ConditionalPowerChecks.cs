using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class ConditionalPowerChecks
{
    internal static void Run()
    {
        CardEffectProgram conditional = new([new(CardInstructionKind.Draw, 1),
            new(CardInstructionKind.SkipIfDrawnCardNotType, 1), new(CardInstructionKind.GainBlock, 7)]);
        foreach (CardCategory type in Enum.GetValues<CardCategory>())
        {
            var program = new ResumableDiscardProgram([new(0, conditional), new(0, CardEffectProgram.Empty, Category: type)],
                [[0], [1], [], [], []], 0, 2, 0);
            var root = program.Freeze(); var mark = program.State.Mark();
            program.Begin(0); program.Run();
            if (!program.Complete || program.Block != (type == CardCategory.Skill ? 9 : 2))
                throw new InvalidOperationException("Conditional draw used the wrong card category.");
            program.State.Rollback(mark);
            if (!program.State.Freeze().ContentEquals(root.Open().State.Freeze()))
                throw new InvalidOperationException("Conditional draw left frame or card state after rollback.");
        }
        var empty = new ResumableDiscardProgram([new(0, conditional)], [[0], [], [], [], []], 0, 2, 0, comparisons: [0]);
        empty.Begin(0); empty.Run();
        if (empty.Block != 2) throw new InvalidOperationException("Empty draw incorrectly satisfied its predicate.");

        // Shuffle selection retrieves a Skill, but only the subsequent actual draw supplies
        // the predicate. The returning shuffle must not count the retrieved card as a draw.
        var shuffled = new ResumableDiscardProgram([new(0, conditional),
                new(0, CardEffectProgram.Empty, Category: CardCategory.Skill), new(0, CardEffectProgram.Empty, Category: CardCategory.Attack)],
            [[0], [], [1, 2], [], []], 0, 2, 0, new(0, 11, 22, 33, 44), new int[9], stratagem: 1);
        var shuffleRoot = shuffled.Freeze();
        shuffled.Begin(0); shuffled.Run();
        if (!shuffled.NeedsChoice || shuffled.ChoicePile != ResumableDiscardProgram.Pile.Draw)
            throw new InvalidOperationException("Conditional draw did not suspend for shuffle selection.");
        var suspended = shuffled.Freeze();
        Parallel.For(0, 8, _ =>
        {
            var lane = suspended.Open();
            lane.SupplyChoice([1]); lane.Run();
            if (lane.Block != 2 || !lane.Complete) throw new InvalidOperationException("Retrieved Skill became the draw result.");
            suspended.RestoreInto(lane); lane.SupplyChoice([2]); lane.Run();
            if (lane.Block != 9 || !lane.Complete) throw new InvalidOperationException("Resumed Skill draw lost its return value.");
            shuffleRoot.RestoreInto(lane);
            if (!lane.State.Freeze().ContentEquals(shuffleRoot.Open().State.Freeze()))
                throw new InvalidOperationException("Conditional worker retained sibling frame state.");
        });

        BasicPowerDefinition[] powers = Enumerable.Range(0, 4).SelectMany(owner => Enum.GetValues<BasicPowerKind>()
            .Select(kind => new BasicPowerDefinition(kind, owner, 0, -1, 0, 0.75m, false))).ToArray();
        var all = new ResumableDiscardProgram([new(0, new([new(CardInstructionKind.ApplyBasicPower, 6, BasicPowerKind.Poison, CardInstructionTarget.AllEnemies),
                new(CardInstructionKind.ApplyBasicPower, 2, BasicPowerKind.Weak, CardInstructionTarget.AllEnemies)])),
                new(0, new([new(CardInstructionKind.AttackTarget, 99)]))],
            [[0, 1], [], [], [], []], 0, 0, 0,
            creatures: [new(50, 50, 0), new(10, 10, 0), new(10, 10, 0), new(10, 10, 0)], powers: powers);
        all.Begin(1, 2); all.Run();
        var survivor = all.Freeze(); var transaction = all.State.Mark();
        all.Begin(0); all.Run();
        var changes = Enumerable.Range(0, all.EventCount).Select(all.EventAt)
            .Where(e => e.Kind == ResumableDiscardProgram.EventKind.PowerChange).Select(e => (e.Target, e.Flags, e.Value)).ToArray();
        if (!changes.SequenceEqual(new[] { (1, (int)BasicPowerKind.Poison, 6), (3, (int)BasicPowerKind.Poison, 6),
                (1, (int)BasicPowerKind.Weak, 2), (3, (int)BasicPowerKind.Weak, 2) }))
            throw new InvalidOperationException("Bulk Power changed target/command order or hit a removed enemy.");
        all.State.Rollback(transaction);
        if (!all.State.Freeze().ContentEquals(survivor.Open().State.Freeze()))
            throw new InvalidOperationException("Bulk Power failed to undo amounts and acquisition order.");
        try { _ = new CardEffectProgram([new(CardInstructionKind.SkipIfDrawnCardNotType, 1)]); }
        catch (ArgumentException)
        {
            Console.WriteLine("COMPACT_CONDITIONAL_POWER_CHECKS_OK draw_categories=4 empty_draw=true shuffle_result=true frozen_workers=8 ordered_bulk=true removed_target=true rollback=true invalid_branch_rejected=true");
            return;
        }
        throw new InvalidOperationException("Out-of-range branch was admitted.");
    }
}
