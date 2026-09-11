using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class GenerationPoolChecks
{
    internal static void Run()
    {
        ValueRng initialRng = new(7, 0x83f4ea876de83187, 0x467215e88d731934, 0x68f4672e51ed5634, 0x9241f375068e3fe7);
        const int draws = 4, poolLength = 3;
        int[][] pools = [[2, 3, 4]];
        // Root 0 selects from the captured pool; root 1 returns the spent card so the
        // same lane runs a second batch with repeated candidates.
        ResumableDiscardProgram.Card generator = new(0, new([new(CardInstructionKind.GenerateFromPool, draws, GenerationPool: 0)]));
        ResumableDiscardProgram.Card retriever = new(0, new([new(CardInstructionKind.RetrieveFromDiscard, 1)]));
        ResumableDiscardProgram.Card[] templates = Enumerable.Range(0, poolLength)
            .Select(_ => new ResumableDiscardProgram.Card(0, CardEffectProgram.Empty, Ethereal: true)).ToArray();
        int[] comparisons = Enumerable.Range(0, 2 + poolLength).SelectMany(left => Enumerable.Range(0, 2 + poolLength)
            .Select(right => left.CompareTo(right))).ToArray();
        var lane = new ResumableDiscardProgram([generator, retriever], [[0, 1], [], [], [], []], 0, 0, 0,
            new(0, 1, 2, 3, 4), comparisons, creatures: [new(40, 40, 0), new(1000, 1000, 0)],
            generationPools: pools, cardGenerationRng: initialRng, generatedCards: templates);
        var root = lane.Freeze();
        var mark = lane.State.Mark();
        int[] expected = SelectTemplates(pools[0], 2 * draws, initialRng, out ValueRng oracle);
        int[] cappedExpected = SelectTemplates(pools[0], draws, initialRng, out ValueRng cappedOracle);
        if (expected.Distinct().Count() == expected.Length)
            throw new InvalidOperationException("Generation-pool fixture missed a repeated candidate.");
        if (new CardEffectProgram([new(CardInstructionKind.GenerateFromPool, draws, GenerationPool: 0)])
            is not { GeneratesCards: true, RequiresGenerationRng: true, TotalDraw: 0 })
            throw new InvalidOperationException("Pool generation did not announce its template-free RNG use.");

        lane.Begin(0); lane.Run();
        var created = lane.Freeze();
        lane.Begin(1); lane.Run(); lane.SupplyChoice([0]); lane.Run();
        lane.Begin(0); lane.Run();
        var completed = lane.Freeze();
        if (lane.CardCount != 2 + expected.Length
            || !lane.Cards(ResumableDiscardProgram.Pile.Hand).SequenceEqual(Enumerable.Range(2, expected.Length))
            || !lane.Cards(ResumableDiscardProgram.Pile.Discard).SequenceEqual(new[] { 1, 0 })
            || Enumerable.Range(2, expected.Length).Any(card => lane.DefinitionIndex(card) != expected[card - 2])
            || Enumerable.Range(2, expected.Length).Select(lane.DefinitionIndex).Distinct().Count() == expected.Length
            || Enumerable.Range(2, expected.Length).Any(card => !lane.IsEthereal(card))
            || lane.CardGenerationRng != oracle || oracle.Counter - initialRng.Counter != expected.Length * (poolLength - 1))
            throw new InvalidOperationException("Pool selection order, template binding, ethereal or RNG consumption differs.");
        var events = Enumerable.Range(0, lane.EventCount).Select(lane.EventAt)
            .Where(e => e.Kind == ResumableDiscardProgram.EventKind.Generated).ToArray();
        if (events.Length != expected.Length || !events.Select(e => e.Card).SequenceEqual(Enumerable.Range(2, expected.Length))
            || !events.Take(draws).Select(e => e.Value).SequenceEqual(Enumerable.Range(1, draws))
            || !events.Skip(draws).Select(e => e.Value).SequenceEqual(Enumerable.Range(draws, draws))
            || events.Any(e => e.Target != 0 || e.Flags != (int)ResumableDiscardProgram.Pile.Hand))
            throw new InvalidOperationException("Generated batch identities, positions or destinations differ.");

        lane.State.Rollback(mark);
        if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("Generated instances, piles or the pool RNG did not roll back.");
        Parallel.For(0, 8, _ =>
        {
            var worker = created.Open();
            worker.Begin(1); worker.Run(); worker.SupplyChoice([0]); worker.Run();
            worker.Begin(0); worker.Run();
            if (!worker.State.Freeze().ContentEquals(completed.Open().State.Freeze()))
                throw new InvalidOperationException("Frozen pool continuation retained sibling state.");
            root.RestoreInto(worker); worker.Begin(0); worker.Run();
            if (!worker.State.Freeze().ContentEquals(created.Open().State.Freeze()))
                throw new InvalidOperationException("Frozen pool regeneration retained a sibling batch.");
        });

        // A full hand spills the rest of the batch into the discard pile in selection order.
        ResumableDiscardProgram.Card[] full = Enumerable.Range(0, 10)
            .Select(index => index == 0 ? generator : new ResumableDiscardProgram.Card(0, CardEffectProgram.Empty)).ToArray();
        var capped = new ResumableDiscardProgram(full, [Enumerable.Range(0, 10).ToArray(), [], [], [], []], 0, 0, 0,
            generationPools: pools, cardGenerationRng: initialRng);
        capped.Begin(0); capped.Run();
        var spilled = Enumerable.Range(0, capped.EventCount).Select(capped.EventAt)
            .Where(e => e.Kind == ResumableDiscardProgram.EventKind.Generated).ToArray();
        if (capped.CardCount != 14 || capped.Count(ResumableDiscardProgram.Pile.Hand) != 10
            || capped.Count(ResumableDiscardProgram.Pile.Discard) != 4
            || Enumerable.Range(10, draws).Any(card => capped.DefinitionIndex(card) != cappedExpected[card - 10])
            || capped.CardGenerationRng != cappedOracle || cappedOracle.Counter - initialRng.Counter != draws * (poolLength - 1)
            || spilled.Length != draws || spilled[0].Flags != (int)ResumableDiscardProgram.Pile.Hand
            || spilled.Skip(1).Any(e => e.Flags != (int)ResumableDiscardProgram.Pile.Discard)
            || spilled.Any(e => e.Target != 0))
            throw new InvalidOperationException("Full-hand pool batch did not spill in selection order.");

        // An empty eligible list skips the whole native effect, including its RNG stream.
        var empty = new ResumableDiscardProgram([generator], [[0], [], [], [], []], 0, 0, 0,
            generationPools: [[]], cardGenerationRng: initialRng);
        empty.Begin(0); empty.Run();
        if (empty.CardCount != 1 || empty.CardGenerationRng != initialRng
            || Enumerable.Range(0, empty.EventCount).Select(empty.EventAt)
                .Any(e => e.Kind == ResumableDiscardProgram.EventKind.Generated))
            throw new InvalidOperationException("Empty generation pool still created a card or consumed RNG.");
        // A single candidate consumes zero native shuffle steps.
        var single = new ResumableDiscardProgram([new(0, new([new(CardInstructionKind.GenerateFromPool, 2, GenerationPool: 0)]))],
            [[0], [], [], [], []], 0, 0, 0, generationPools: [[1]], cardGenerationRng: initialRng,
            generatedCards: [templates[0]]);
        single.Begin(0); single.Run();
        if (single.CardCount != 3 || single.DefinitionIndex(1) != 1 || single.DefinitionIndex(2) != 1
            || !single.IsEthereal(1) || single.CardGenerationRng != initialRng)
            throw new InvalidOperationException("Single-candidate pool changed its identity or RNG stream.");

        Expect<NotSupportedException>(() => _ = new ResumableDiscardProgram(
            [new(0, new([new(CardInstructionKind.GenerateFromPool, 1, GenerationPool: 3)]))], [[0], [], [], [], []], 0, 0, 0,
            generationPools: pools, cardGenerationRng: initialRng, generatedCards: templates));
        Expect<NotSupportedException>(() => _ = new ResumableDiscardProgram(
            [new(0, new([new(CardInstructionKind.GenerateFromPool, 1, GenerationPool: 0)]))], [[0], [], [], [], []], 0, 0, 0,
            generationPools: [[9]], cardGenerationRng: initialRng));
        Expect<NotSupportedException>(() => _ = new ResumableDiscardProgram(
            [new(0, new([new(CardInstructionKind.GenerateFromPool, 1, GenerationPool: 0)]))], [[0], [], [], [], []], 0, 0, 0,
            generationPools: [[2, 3, 4]], generatedCards: templates));
        Expect<ArgumentException>(() => _ = new CardEffectProgram([new(CardInstructionKind.Draw, 1, GenerationPool: 0)]));
        Expect<ArgumentException>(() => _ = new CardEffectProgram(
            [new(CardInstructionKind.GenerateFromPool, 1, CardTemplate: 2, GenerationPool: 0)]));

        Console.WriteLine("COMPACT_GENERATION_POOL_CHECKS_OK batches=2 draws=8 pool=3 ethereal=true full_hand_spill=true "
            + "rollback=true frozen_workers=8 empty_pool=true single_candidate=true boundaries=true");
    }

    // Replays the native full-pool shuffle per selection and maps the frozen indices.
    private static int[] SelectTemplates(int[] pool, int count, ValueRng initial, out ValueRng final)
    {
        int[] scratch = new int[pool.Length];
        int[] selected = new int[count];
        ValueRng rng = initial;
        for (int index = 0; index < count; index++)
        {
            rng = rng.TakeDistinctIndices(pool.Length, 1, scratch, out _);
            selected[index] = pool[scratch[0]];
        }
        final = rng;
        return selected;
    }

    private static void Expect<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
