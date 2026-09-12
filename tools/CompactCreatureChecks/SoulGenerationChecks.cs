using CombatSolver.Engine.InCombat.Simulation.Compact;

// GraveWarden and Reave insert generated Souls at a random draw-pile position. Upgraded
// Reave upgrades every Soul before that insertion, but the native CardCmd.Upgrade returns
// before it runs while the combat is ending. The instruction therefore carries a separate
// ending template instead of folding one variant into both paths.
internal static class SoulGenerationChecks
{
    internal static void Run()
    {
        ResumableDiscardProgram.Card upgraded = new(0, new([new(CardInstructionKind.Draw, 3)]),
            ResultPile: ResumableDiscardProgram.Pile.Exhaust, Category: CardCategory.Skill);
        ResumableDiscardProgram.Card plain = new(0, new([new(CardInstructionKind.Draw, 2)]),
            ResultPile: ResumableDiscardProgram.Pile.Exhaust, Category: CardCategory.Skill);
        // Root cards 0 and 1; the two Soul variants are the generated templates 2 and 3.
        ResumableDiscardProgram.Card reave = new(1, new([new(CardInstructionKind.AttackTarget, 9),
            new(CardInstructionKind.GenerateCards, 1, CardTemplate: 2, Placement: CardGenerationPlacement.RandomDraw,
                EndingCardTemplate: 3)]));
        ResumableDiscardProgram.Card graveWarden = new(1, new([new(CardInstructionKind.GainBlock, 8),
            new(CardInstructionKind.GenerateCards, 1, CardTemplate: 3, Placement: CardGenerationPlacement.RandomDraw)]));
        int[] comparisons = Enumerable.Range(0, 4).SelectMany(left => Enumerable.Range(0, 4)
            .Select(right => left.CompareTo(right))).ToArray();
        ValueRng rootRng = new(7, 1, 2, 3, 4);
        // An empty draw pile still consumes one random position: NextInt(count + 1) = NextInt(1).
        ValueRng placedRng = rootRng.NextInt(1, out _);

        foreach (bool ending in new[] { false, true })
        {
            var lane = new ResumableDiscardProgram([reave, graveWarden], [[0, 1], [], [], [], []], 9, 0, 0, rootRng,
                comparisons, creatures: [new(30, 30, 0), new(ending ? 9 : 30, 30, 0)], generatedCards: [upgraded, plain]);
            var root = lane.Freeze();
            var mark = lane.State.Mark();
            lane.Begin(0, 1); lane.Run();
            var events = Enumerable.Range(0, lane.EventCount).Select(lane.EventAt)
                .Where(item => item.Kind == ResumableDiscardProgram.EventKind.Generated).ToArray();
            if (events.Length != 1 || lane.CardCount != 3 || lane.Ending != ending
                || lane.DefinitionIndex(events[0].Card) != (ending ? 3 : 2)
                || lane.Definition(events[0].Card).Effects[0].Amount != (ending ? 2 : 3)
                || events[0].Target != 0)
                throw new InvalidOperationException($"Soul generation folded the native upgrade gate: ending={ending}.");
            if (ending)
            {
                if (events[0].Flags != (int)ResumableDiscardProgram.Pile.Unplaced || events[0].Value != -1
                    || lane.Count(ResumableDiscardProgram.Pile.Unplaced) != 1 || lane.Count(ResumableDiscardProgram.Pile.Draw) != 0
                    || lane.ShuffleRng != rootRng || !lane.CreatureDeathCompleted(1))
                    throw new InvalidOperationException("An ending Soul consumed a random position, entered a pile or lost its identity.");
                // Native play is refused once the combat is ending, so the terminal state is
                // the boundary this branch can compare.
                Expect<InvalidOperationException>(() => lane.Begin(1));
            }
            else
            {
                if (events[0].Flags != (int)ResumableDiscardProgram.Pile.Draw || events[0].Value != 0
                    || lane.Count(ResumableDiscardProgram.Pile.Unplaced) != 0 || lane.CardAt(ResumableDiscardProgram.Pile.Draw, 0) != 2
                    || lane.ShuffleRng != placedRng || lane.Creature(1).CurrentHp != 21)
                    throw new InvalidOperationException("A placed Soul lost its random insertion or shuffle consumption.");
            }
            var placed = lane.Freeze();
            if (!ending)
            {
                lane.Begin(1); lane.Run();
                int[] later = Enumerable.Range(0, lane.EventCount).Select(lane.EventAt)
                    .Where(item => item.Kind == ResumableDiscardProgram.EventKind.Generated).Select(item => item.Card).ToArray();
                if (later.Length != 2 || lane.DefinitionIndex(later[1]) != 3 || lane.ShuffleRng.Counter != 9
                    || lane.Creature(0).Block != 8)
                    throw new InvalidOperationException("GraveWarden did not keep its plain template in the same combat.");
            }
            var final = lane.Freeze();
            lane.State.Rollback(mark);
            if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze()))
                throw new InvalidOperationException("Soul generation did not roll back its identities, piles or RNG.");
            Parallel.For(0, 8, _ =>
            {
                var worker = final.Open();
                root.RestoreInto(worker);
                worker.Begin(0, 1); worker.Run();
                if (!worker.State.Freeze().ContentEquals(placed.Open().State.Freeze()))
                    throw new InvalidOperationException("Soul generation worker changed its definition or RNG.");
            });
        }

        // Only generation instructions may carry the ending template, and the two variants
        // must stay distinct.
        Expect<ArgumentException>(() => _ = new CardEffectProgram(
            [new(CardInstructionKind.GainBlock, 1, EndingCardTemplate: 2)]));
        Expect<NotSupportedException>(() => _ = new CardEffectProgram(
            [new(CardInstructionKind.GenerateCards, 1, CardTemplate: 2, Placement: CardGenerationPlacement.RandomDraw,
                EndingCardTemplate: 2)]));
        // The constructor applies the generated-slot range to both variants: root cards 0 and 1
        // can never become the ending variant, and index 4 is outside the four definitions.
        ResumableDiscardProgram.Card rootVariant = new(1, new([new(CardInstructionKind.GenerateCards, 1,
            CardTemplate: 2, Placement: CardGenerationPlacement.RandomDraw, EndingCardTemplate: 0)]));
        Expect<NotSupportedException>(() => _ = new ResumableDiscardProgram([rootVariant, graveWarden],
            [[0, 1], [], [], [], []], 9, 0, 0, rootRng, comparisons, generatedCards: [upgraded, plain]));
        ResumableDiscardProgram.Card outsideVariant = new(1, new([new(CardInstructionKind.GenerateCards, 1,
            CardTemplate: 2, Placement: CardGenerationPlacement.RandomDraw, EndingCardTemplate: 4)]));
        Expect<NotSupportedException>(() => _ = new ResumableDiscardProgram([outsideVariant, graveWarden],
            [[0, 1], [], [], [], []], 9, 0, 0, rootRng, comparisons, generatedCards: [upgraded, plain]));
        Console.WriteLine("COMPACT_SOUL_GENERATION_CHECKS_OK ending_template=plain placed_template=upgraded "
            + "shuffle_rng=true unplaced_history=true rollback=true frozen_workers=8 domain_rejections=true ending_template_range=true");
    }

    private static void Expect<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
