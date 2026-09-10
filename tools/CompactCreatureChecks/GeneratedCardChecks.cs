using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class GeneratedCardChecks
{
    internal static void Run()
    {
        const int roots = 10, generated = 300;
        ResumableDiscardProgram.Card[] cards = Enumerable.Range(0, roots).Select(index => new ResumableDiscardProgram.Card(0,
            index == 0 ? new([new(CardInstructionKind.GenerateCards, generated, CardTemplate: roots)])
            : index == 1 ? new([new(CardInstructionKind.Draw, 2)]) : CardEffectProgram.Empty)).ToArray();
        ResumableDiscardProgram.Card token = new(0, new([new(CardInstructionKind.AttackTarget, 4)]),
            ResultPile: ResumableDiscardProgram.Pile.Exhaust, Category: CardCategory.Attack);
        int[] comparisons = Enumerable.Range(0, roots + 1).SelectMany(left => Enumerable.Range(0, roots + 1)
            .Select(right => left.CompareTo(right))).ToArray();
        var lane = new ResumableDiscardProgram(cards, [Enumerable.Range(0, roots).ToArray(), [], [], [], []], 0, 0, 0,
            new(0, 1, 2, 3, 4), comparisons, creatures: [new(40, 40, 0), new(1000, 1000, 0)], generatedCards: [token]);
        var root = lane.Freeze(); var mark = lane.State.Mark();
        lane.Begin(0); lane.Run();
        if (lane.CardCount != 310 || lane.Count(ResumableDiscardProgram.Pile.Hand) != 10
            || !lane.Cards(ResumableDiscardProgram.Pile.Hand).SequenceEqual(Enumerable.Range(1, 10))
            || !lane.Cards(ResumableDiscardProgram.Pile.Discard).SequenceEqual(Enumerable.Range(11, 299).Append(0)))
            throw new InvalidOperationException("Generated identities or full-hand spill order differ.");
        var events = Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).Where(e => e.Kind == ResumableDiscardProgram.EventKind.Generated).ToArray();
        if (!events.Select(e => e.Card).SequenceEqual(Enumerable.Range(10, 300)) || events.Any(e => e.Value != roots)
            || Enumerable.Range(10, 300).Any(card => lane.DefinitionIndex(card) != roots || lane.Definition(card) != token))
            throw new InvalidOperationException("Generated definitions or full-width event identities differ.");
        var created = lane.Freeze();
        lane.Begin(10, 1); lane.Run(); lane.Begin(1); lane.Run();
        if (lane.Creature(1).CurrentHp != 996 || lane.Count(ResumableDiscardProgram.Pile.Exhaust) != 1
            || lane.CardAt(ResumableDiscardProgram.Pile.Exhaust, 0) != 10 || lane.ShuffleCount != 1
            || lane.Cards(ResumableDiscardProgram.Pile.Hand).Count(card => card >= 10) < 1)
            throw new InvalidOperationException("Generated attack/exhaustion or subsequent shuffle differs.");
        var shuffled = lane.Freeze();
        lane.State.Rollback(mark);
        if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("Generated rows and pile blocks did not roll back.");
        Parallel.For(0, 8, _ =>
        {
            var worker = created.Open();
            worker.Begin(10, 1); worker.Run(); worker.Begin(1); worker.Run();
            if (!worker.State.Freeze().ContentEquals(shuffled.Open().State.Freeze()))
                throw new InvalidOperationException("Generated worker continuation differs.");
            root.RestoreInto(worker); worker.Begin(0); worker.Run();
            if (!worker.State.Freeze().ContentEquals(created.Open().State.Freeze()))
                throw new InvalidOperationException("Generated worker identity allocation retained a sibling.");
        });
        Console.WriteLine("COMPACT_GENERATED_CARD_CHECKS_OK generated=300 full_hand_spill=true full_width_ids=true templates=true attack=true exhaust=true shuffle=true rollback=true frozen_workers=8");
    }
}
