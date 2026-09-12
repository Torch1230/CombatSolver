using CombatSolver.Engine.InCombat.Simulation.Compact;
using P = CombatSolver.Engine.InCombat.Simulation.Compact.ResumableDiscardProgram;

internal static class DrawHookChecks
{
    internal static void Run()
    {
        // The parent's first returned card is an Attack, while the innermost draw is a
        // Skill. A nested return must not satisfy the parent's Escape Plan condition.
        P.Card[] cards = [new(0, new([new(CardInstructionKind.Draw, 1),
                new(CardInstructionKind.SkipIfDrawnCardNotType, 1), new(CardInstructionKind.GainBlock, 42)])),
            new(0, CardEffectProgram.Empty, Category: CardCategory.Attack, Ethereal: true, DrawCost: new([])),
            new(0, CardEffectProgram.Empty, Category: CardCategory.Skill, Ethereal: true, DrawCost: new([])),
            new(0, CardEffectProgram.Empty),
            new(0, CardEffectProgram.Empty, Category: CardCategory.Skill, DrawCost: new([]))];
        var lane = Create(cards, [1, 2], [3, 4]);
        var root = lane.Freeze(); var mark = lane.State.Mark();
        lane.Begin(0); lane.Run();
        if (!lane.NeedsChoice || !lane.ChoiceRetrieves || lane.Count(P.Pile.Hand) != 2
            || lane.EnergyCostRng!.Value.Counter != 0 || Events(lane, P.EventKind.DrawResolved).Length != 0)
            throw new InvalidOperationException("Nested draw did not suspend before parent costs and completions.");
        var pending = lane.Freeze();
        lane.SupplyChoice([3]); lane.Run();
        if (!lane.Complete || lane.Block != 0 || !lane.Cards(P.Pile.Hand).SequenceEqual([1, 2, 3, 4])
            || !Events(lane, P.EventKind.Draw).Select(item => item.Card).SequenceEqual([1, 2, 4])
            || !Events(lane, P.EventKind.DrawResolved).Select(item => item.Card).SequenceEqual([4, 2, 1])
            || !Events(lane, P.EventKind.CostChanged).Select(item => item.Card).SequenceEqual([4, 2, 1])
            || lane.EnergyCostRng!.Value.Counter != 3)
            throw new InvalidOperationException("Nested returns, Slither RNG or parent draw identity changed.");
        var completed = lane.Freeze();
        lane.State.Rollback(mark);
        if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("Nested draw frames survived rollback.");
        Parallel.For(0, 8, _ =>
        {
            var worker = pending.Open(); worker.SupplyChoice([3]); worker.Run();
            if (!worker.State.Freeze().ContentEquals(completed.Open().State.Freeze()))
                throw new InvalidOperationException("Nested draw continuation shared a mutable stack.");
            root.RestoreInto(worker); worker.Begin(0); worker.Run();
            if (!worker.State.Freeze().ContentEquals(pending.Open().State.Freeze()))
                throw new InvalidOperationException("Nested draw reverse restoration lost pending parents.");
        });

        // Nine unfinished drawn cards exceed the independent card-frame limit. Retrieval
        // fills the tenth hand slot, so no draw event may be invented for that retrieved card.
        P.Card[] deepCards = [new(0, new([new(CardInstructionKind.Draw, 1)])),
            .. Enumerable.Range(1, 10).Select(_ => new P.Card(0, CardEffectProgram.Empty, Ethereal: true))];
        var deep = Create(deepCards, Enumerable.Range(1, 9).ToArray(), [10]);
        deep.Begin(0); deep.Run();
        if (!deep.NeedsChoice || Events(deep, P.EventKind.DrawPowerStart).Length != 9)
            throw new InvalidOperationException("Deep draw hooks were truncated at the card-frame capacity.");
        deep.SupplyChoice([10]); deep.Run();
        if (!deep.Complete || deep.Count(P.Pile.Hand) != 10 || Events(deep, P.EventKind.Draw).Length != 9
            || !Events(deep, P.EventKind.DrawResolved).Select(item => item.Card).SequenceEqual(Enumerable.Range(1, 9).Reverse())
            || Events(deep, P.EventKind.DrawPowerFinish).Length != 9)
            throw new InvalidOperationException("Full-hand retrieval did not unwind every suspended draw.");

        // A killed combat still disables Swift, while its draw command returns empty.
        P.Card[] terminalCards = [new(0, new([new(CardInstructionKind.LoseEnemyHp, 1), new(CardInstructionKind.DrawOnce, 3)])),
            new(0, CardEffectProgram.Empty, Ethereal: true)];
        var terminal = Create(terminalCards, [1], [], enemyHp: 1);
        terminal.Begin(0, 1); terminal.Run();
        if (!terminal.Ending || !terminal.Complete || !terminal.EnchantmentDisabled(0)
            || Events(terminal, P.EventKind.Draw).Length != 0 || Events(terminal, P.EventKind.DrawPowerStart).Length != 0)
            throw new InvalidOperationException("Ending draw started a new nested hook or reset its one-shot flag.");
        Console.WriteLine("COMPACT_DRAW_HOOK_CHECKS_OK nested_shuffle=true parent_return=true reverse_slither=true depth9=true full_hand=true terminal=true rollback=true frozen_workers=8");
    }

    private static P Create(P.Card[] cards, int[] draw, int[] discard, int enemyHp = 100)
    {
        int[] comparisons = Enumerable.Range(0, cards.Length).SelectMany(left => Enumerable.Range(0, cards.Length)
            .Select(right => left.CompareTo(right))).ToArray();
        return new(cards, [[0], draw, discard, [], []], 10, 0, 0, new(0, 1, 2, 3, 4), comparisons,
            stratagem: 1, creatures: [new(100, 100, 0), new(enemyHp, enemyHp, 0)],
            powers: [new(BasicPowerKind.Pagestorm, 0, 1, 0, 1, 1, true)], energyCostRng: new(0, 5, 6, 7, 8));
    }

    private static P.Event[] Events(P lane, P.EventKind kind)
        => Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).Where(item => item.Kind == kind).ToArray();
}
