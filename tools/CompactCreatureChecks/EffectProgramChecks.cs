using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class EffectProgramChecks
{
    internal static void Run()
    {
        CardInstruction[] source = [new(CardInstructionKind.Draw, 1), new(CardInstructionKind.Discard, 1),
            new(CardInstructionKind.GainBlock, 7), new(CardInstructionKind.Draw, 1),
            new(CardInstructionKind.Discard, 1), new(CardInstructionKind.GainBlock, 11)];
        CardEffectProgram parent = new(source);
        source[0] = new(CardInstructionKind.GainBlock, 999);
        CardEffectProgram child = new([new(CardInstructionKind.Draw, 1), new(CardInstructionKind.Discard, 1),
            new(CardInstructionKind.GainBlock, 13)]);
        var cards = Enumerable.Repeat(new ResumableDiscardProgram.Card(0, CardEffectProgram.Empty), 7).ToArray();
        cards[0] = new(1, parent); cards[1] = new(0, child, true);
        var program = new ResumableDiscardProgram(cards, [[0, 1, 2], [3, 4, 5, 6], [], [], []], 3, 2, 3);
        var root = program.Freeze();
        var mark = program.State.Mark();
        program.Begin(0); program.Run();
        if (program.ChoiceCard != 0 || !program.Cards(ResumableDiscardProgram.Pile.Hand).SequenceEqual(new[] { 1, 2, 3 }))
            throw new InvalidOperationException("Instruction definition aliased its source or lost initial draw.");
        program.SupplyChoice([1]); program.Run();
        if (program.ChoiceCard != 1 || program.Block != 5)
            throw new InvalidOperationException("Parent continued before its nested Sly choice finished.");
        var nested = program.Freeze();
        program.State.Rollback(mark);
        if (!program.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("Instruction rollback did not restore the full root.");

        Parallel.For(0, 8, _ =>
        {
            var lane = nested.Open();
            lane.SupplyChoice([2]); lane.Run();
            if (lane.ChoiceCard != 0 || lane.Block != 28
                || !lane.Cards(ResumableDiscardProgram.Pile.Hand).SequenceEqual(new[] { 3, 4, 5 })
                || !lane.Cards(ResumableDiscardProgram.Pile.Draw).SequenceEqual(new[] { 6 }))
                throw new InvalidOperationException("Resuming child failed to continue later parent instructions exactly once.");
            var second = lane.Freeze();
            var cancel = lane.State.Mark();
            lane.SupplyChoice([3]);
            try { lane.Run(new CancellationToken(true)); throw new InvalidOperationException("Cancelled run executed."); }
            catch (OperationCanceledException) { lane.State.Rollback(cancel); }
            if (!lane.State.Freeze().ContentEquals(second.Open().State.Freeze()))
                throw new InvalidOperationException("Cancellation lost the current instruction or selection.");
            lane.SupplyChoice([3]); lane.Run();
            if (!lane.Complete || lane.Block != 42 || lane.Energy != 2
                || !lane.Cards(ResumableDiscardProgram.Pile.Hand).SequenceEqual(new[] { 4, 5 })
                || !lane.Cards(ResumableDiscardProgram.Pile.Discard).SequenceEqual(new[] { 2, 1, 3, 0 }))
                throw new InvalidOperationException("Repeated choices changed execution order or repeated earlier instructions.");
            if (Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).Count(e => e.Kind == ResumableDiscardProgram.EventKind.Draw) != 3)
                throw new InvalidOperationException("Instruction-local draw cursor leaked across commands.");
            var completed = lane.Freeze();
            nested.RestoreInto(lane);
            lane.SupplyChoice([2]); lane.Run(); lane.SupplyChoice([3]); lane.Run();
            if (!lane.State.Freeze().ContentEquals(completed.Open().State.Freeze()))
                throw new InvalidOperationException("Frozen instructions retained a worker continuation.");
        });
        var emptyChoice = new ResumableDiscardProgram([new(0, new([new(CardInstructionKind.Discard, 1),
            new(CardInstructionKind.GainBlock, 5)]))], [[0], [], [], [], []], 0, 0, 0);
        emptyChoice.Begin(0); emptyChoice.Run();
        if (!emptyChoice.Complete || emptyChoice.Block != 5)
            throw new InvalidOperationException("Empty selection skipped subsequent instructions.");
        try { _ = new CardEffectProgram([new((CardInstructionKind)int.MaxValue, 1)]); }
        catch (NotSupportedException)
        {
            Console.WriteLine("COMPACT_EFFECT_PROGRAM_CHECKS_OK ordered_effects=true repeated_choices=true nested_parent_resume=true immutable_definition=true cancellation=true frozen_workers=8 empty_choice=true unknown_rejected=true");
            return;
        }
        throw new InvalidOperationException("Unknown instruction was admitted.");
    }
}
