using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class OneShotEnchantmentChecks
{
    internal static void Run()
    {
        int encodings = 0;
        foreach (int definition in new[] { 0, 1, int.MaxValue })
        foreach (int captured in new[] { 0, 1, 999_999_999 })
        foreach (CardKeywordFlags keywords in new[] { CardKeywordFlags.None, CardKeywordFlags.Ethereal,
                     CardKeywordFlags.Retain, CardKeywordFlags.Ethereal | CardKeywordFlags.Retain })
        foreach (bool disabled in new[] { false, true })
        {
            var value = new CardInstanceValue(definition, captured, keywords, disabled);
            var paid = value with { CapturedX = 999_999_999 - captured };
            if (CardInstanceValue.Decode(value.Data) != value || CardInstanceValue.Decode(paid.Data) != paid)
                throw new InvalidOperationException("Definition, captured X, keywords and one-shot status overlap.");
            encodings++;
        }

        // A generated X card exercises the low-bit definition lookup after its one-shot
        // flag changes. The pause happens after one draw and must resume inside Swift.
        ResumableDiscardProgram.Card[] cards = [new(0, new([new(CardInstructionKind.GenerateCards, 1, CardTemplate: 4)])),
            new(0, CardEffectProgram.Empty), new(0, CardEffectProgram.Empty), new(0, CardEffectProgram.Empty)];
        ResumableDiscardProgram.Card token = new(0, new([new(CardInstructionKind.DrawOnce, 2)]), CostsX: true);
        int[] comparisons = Enumerable.Range(0, 5).SelectMany(left => Enumerable.Range(0, 5).Select(right => left.CompareTo(right))).ToArray();
        var lane = new ResumableDiscardProgram(cards, [[0], [1], [2, 3], [], []], 17, 0, 0,
            new(0, 1, 2, 3, 4), comparisons, stratagem: 1, generatedCards: [token]);
        lane.Begin(0); lane.Run();
        var root = lane.Freeze(); var mark = lane.State.Mark();
        lane.Begin(4); lane.Run();
        if (!lane.NeedsChoice || !lane.EnchantmentDisabled(4) || lane.CapturedX(4) != 17 || lane.Energy != 0
            || lane.DefinitionIndex(4) != 4 || lane.Count(ResumableDiscardProgram.Pile.Hand) != 1)
            throw new InvalidOperationException("One-shot disable/payment did not survive a generated-card shuffle pause.");
        var pending = lane.Freeze();
        int selected = lane.ChoiceOptions()[0]; lane.SupplyChoice([selected]); lane.Run();
        if (!lane.Complete || lane.Count(ResumableDiscardProgram.Pile.Hand) != 3
            || Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).Count(item => item.Kind == ResumableDiscardProgram.EventKind.EnchantmentStart) != 1
            || Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).Count(item => item.Kind == ResumableDiscardProgram.EventKind.Draw) != 2)
            throw new InvalidOperationException("Paused one-shot draw skipped or repeated its remaining instruction.");
        var completed = lane.Freeze();
        lane.State.Rollback(mark);
        if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("One-shot rollback retained its disabled bit, payment or draw progress.");
        Parallel.For(0, 8, _ =>
        {
            var worker = pending.Open(); worker.SupplyChoice([selected]); worker.Run();
            if (!worker.State.Freeze().ContentEquals(completed.Open().State.Freeze()))
                throw new InvalidOperationException("One-shot pending continuation differs between independent workers.");
        });
        Console.WriteLine($"COMPACT_ONE_SHOT_ENCHANTMENT_CHECKS_OK encodings={encodings} generated_x=true partial_draw=true pending=true rollback=true frozen_workers=8");
    }
}
