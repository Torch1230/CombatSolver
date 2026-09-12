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

    // The production turn-start path: the applied CallOfTheVoid counter selects one full
    // pool shuffle per card after energy reset, before the synthetic hand draw.
    internal static void RunBeforeHandDraw()
    {
        ValueRng initialRng = new(11, 0x1f2e3d4c5b6a7988, 0x1122334455667788, 0x99aabbccddeeff00, 0x0f1e2d3c4b5a6978);
        int[] pool = [4, 5, 6, 7];
        var caster = new ResumableDiscardProgram.Card(1, new([new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.CallOfTheVoid)]),
            ResultPile: ResumableDiscardProgram.Pile.Removed);
        var defender = new ResumableDiscardProgram.Card(0, new([new(CardInstructionKind.GainBlock, 1)]));
        ResumableDiscardProgram.Card[] root = [caster, caster, defender, defender];
        ResumableDiscardProgram.Card[] templates = Enumerable.Range(0, pool.Length)
            .Select(_ => new ResumableDiscardProgram.Card(0, CardEffectProgram.Empty, Ethereal: true)).ToArray();
        var lane = RoundLane(root, [[0, 1, 2], [3], [], [], []], templates, [pool], initialRng, baseDraw: 1);
        var rootFreeze = lane.Freeze();
        var mark = lane.State.Mark();
        lane.Begin(0); lane.Run();
        int powerIndex = VoidIndex(lane);
        BasicPowerValues first = lane.Power(powerIndex);
        if (first.Amount != 1 || first.Applier != 0 || first.Retired || first.Order <= 0)
            throw new InvalidOperationException("Applied CallOfTheVoid lost its amount, applier or acquisition order.");
        lane.Begin(1); lane.Run();
        BasicPowerValues stacked = lane.Power(powerIndex);
        if (stacked.Amount != 2 || stacked.Applier != 0 || stacked.Retired || stacked.Order != first.Order)
            throw new InvalidOperationException("Stacked CallOfTheVoid changed its applier or acquisition order.");
        lane.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Sequential);
        lane.Run();
        int[] expected = SelectTemplates(pool, 2, initialRng, out ValueRng oracle);
        var events = Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).ToArray();
        var generated = events.Where(item => item.Kind == ResumableDiscardProgram.EventKind.Generated).ToArray();
        int firstDraw = Array.FindIndex(events, item => item.Kind == ResumableDiscardProgram.EventKind.Draw);
        if (generated.Length != 2 || firstDraw < 0
            || generated.Any(item => item.Target != ResumableDiscardProgram.TurnStartPowerCreator)
            || !generated.Select(item => item.Card).SequenceEqual(new[] { 4, 5 })
            || !generated.Select(item => item.Value).SequenceEqual(new[] { 0, 1 })
            || generated.Any(item => item.Flags != (int)ResumableDiscardProgram.Pile.Hand)
            || !Enumerable.Range(0, 2).All(index => lane.DefinitionIndex(4 + index) == expected[index] && lane.IsEthereal(4 + index)))
            throw new InvalidOperationException("BeforeHandDraw generation did not run before the hand draw with its frozen selection.");
        if (lane.CardGenerationRng != oracle || oracle.Counter - initialRng.Counter != 2 * (pool.Length - 1)
            || !lane.Cards(ResumableDiscardProgram.Pile.Hand).SequenceEqual(new[] { 4, 5, 3 }))
            throw new InvalidOperationException("BeforeHandDraw generation consumed a different RNG stream or pile order.");

        var completed = lane.Freeze();
        lane.State.Rollback(mark);
        if (!lane.State.Freeze().ContentEquals(rootFreeze.Open().State.Freeze()))
            throw new InvalidOperationException("BeforeHandDraw generation did not roll back its Power, RNG or piles.");
        lane.Begin(0); lane.Run(); lane.Begin(1); lane.Run();
        lane.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Sequential); lane.Run();
        if (!lane.State.Freeze().ContentEquals(completed.Open().State.Freeze()))
            throw new InvalidOperationException("BeforeHandDraw replay did not reproduce the completed turn start.");
        Parallel.For(0, 8, _ =>
        {
            var worker = completed.Open();
            if (worker.Power(VoidIndex(worker)).Amount != 2 || worker.CardCount != 6
                || !worker.Cards(ResumableDiscardProgram.Pile.Hand).SequenceEqual(new[] { 4, 5, 3 }))
                throw new InvalidOperationException("Frozen BeforeHandDraw continuation retained sibling state.");
        });

        // A zero counter keeps the tick and its five-field stream untouched.
        var idle = RoundLane(root, [[0, 1, 2], [3], [], [], []], templates, [pool], initialRng, baseDraw: 1);
        idle.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Sequential); idle.Run();
        if (idle.CardGenerationRng != initialRng || idle.CardCount != 4 || idle.Count(ResumableDiscardProgram.Pile.Hand) != 1
            || Enumerable.Range(0, idle.EventCount).Select(idle.EventAt)
                .Any(item => item.Kind == ResumableDiscardProgram.EventKind.Generated))
            throw new InvalidOperationException("Zero CallOfTheVoid still generated a card or consumed the stream.");

        // A full hand spills the rest of the batch into the discard pile in selection order.
        ResumableDiscardProgram.Card retained = new(0, new([new(CardInstructionKind.GainBlock, 1)]), Retain: true);
        ResumableDiscardProgram.Card[] capped = Enumerable.Repeat(retained, 8).ToArray();
        int[] cappedPool = [8, 9, 10, 11];
        var cappedTemplates = Enumerable.Range(0, cappedPool.Length)
            .Select(_ => new ResumableDiscardProgram.Card(0, CardEffectProgram.Empty, Ethereal: true)).ToArray();
        var overflow = RoundLane(capped, [Enumerable.Range(0, 8).ToArray(), [], [], [], []], cappedTemplates, [cappedPool], initialRng,
            baseDraw: 0, voidAmount: 4);
        overflow.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Sequential);
        var cappedExpected = SelectTemplates(cappedPool, 4, initialRng, out ValueRng cappedOracle);
        var spilled = Enumerable.Range(0, overflow.EventCount).Select(overflow.EventAt)
            .Where(item => item.Kind == ResumableDiscardProgram.EventKind.Generated).ToArray();
        if (overflow.CardGenerationRng != cappedOracle || spilled.Length != 4
            || !Enumerable.Range(0, 4).All(index => overflow.DefinitionIndex(8 + index) == cappedExpected[index])
            || overflow.Count(ResumableDiscardProgram.Pile.Hand) != 10 || overflow.Count(ResumableDiscardProgram.Pile.Discard) != 2
            || !spilled.Take(2).All(item => item.Flags == (int)ResumableDiscardProgram.Pile.Hand)
            || !spilled.Skip(2).All(item => item.Flags == (int)ResumableDiscardProgram.Pile.Discard)
            || !spilled.Select(item => item.Value).SequenceEqual(new[] { 8, 9, 0, 1 }))
            throw new InvalidOperationException("Full-hand BeforeHandDraw batch did not spill in selection order.");

        // The round path only exists with a captured pool, a player slot and a real index.
        Expect<NotSupportedException>(() => _ = new ResumableDiscardProgram(root, [[0, 1, 2], [3], [], [], []], 3, 0, 0,
            new ValueRng(0, 1, 2, 3, 4), Comparisons(root.Length), creatures: Creatures(), powers: Powers(0),
            handEndAdmitted: true, monsterMoves: [new([])], powerPhasesAdmitted: true, monsterAi: new(1, 0, [0], [0]),
            round: new(7, 9, 3, 1, BeforeHandDrawPool: 0)));
        Expect<NotSupportedException>(() => _ = new ResumableDiscardProgram(root, [[0, 1, 2], [3], [], [], []], 3, 0, 0,
            new ValueRng(0, 1, 2, 3, 4), Comparisons(root.Length), creatures: Creatures(), powers: Powers(0, voidSlot: false),
            generatedCards: templates, generationPools: [pool], cardGenerationRng: initialRng,
            handEndAdmitted: true, monsterMoves: [new([])], powerPhasesAdmitted: true, monsterAi: new(1, 0, [0], [0]),
            round: new(7, 9, 3, 1, BeforeHandDrawPool: 0)));
        Expect<NotSupportedException>(() => _ = new ResumableDiscardProgram(root, [[0, 1, 2], [3], [], [], []], 3, 0, 0,
            new ValueRng(0, 1, 2, 3, 4), Comparisons(root.Length), creatures: Creatures(), powers: Powers(0),
            generatedCards: templates, generationPools: [pool], cardGenerationRng: initialRng,
            handEndAdmitted: true, monsterMoves: [new([])], powerPhasesAdmitted: true, monsterAi: new(1, 0, [0], [0]),
            round: new(7, 9, 3, 1, BeforeHandDrawPool: 1)));
        Console.WriteLine("COMPACT_GENERATION_POOL_BEFORE_HAND_DRAW_OK stacked_counter=true order=true before_draw=true "
            + "five_field_rng=true rollback=true frozen_workers=8 zero_counter=true full_hand_spill=true boundaries=true");
    }

    private static ResumableDiscardProgram RoundLane(ResumableDiscardProgram.Card[] cards, IReadOnlyList<int>[] piles,
        ResumableDiscardProgram.Card[] templates, int[][]? pools, ValueRng rng, int baseDraw, int voidAmount = 0, int poolIndex = 0)
        => new(cards, piles, 3, 0, 0, new ValueRng(0, 1, 2, 3, 4), Comparisons(cards.Length + templates.Length),
            creatures: Creatures(), powers: Powers(voidAmount), generatedCards: templates, generationPools: pools,
            cardGenerationRng: pools == null ? null : rng, handEndAdmitted: true, monsterMoves: [new([])],
            powerPhasesAdmitted: true, monsterAi: new(1, 0, [0], [0]),
            round: new(7, 9, 3, baseDraw, BeforeHandDrawPool: pools == null ? -1 : poolIndex));

    private static CreatureVitals[] Creatures() => [new(80, 80, 0), new(200, 200, 0)];

    private static int[] Comparisons(int definitions) => Enumerable.Range(0, definitions).SelectMany(left =>
        Enumerable.Range(0, definitions).Select(right => left.CompareTo(right))).ToArray();

    private static BasicPowerDefinition[] Powers(int voidAmount, bool voidSlot = true) => Enumerable.Range(0, 2).SelectMany(owner =>
        Enum.GetValues<BasicPowerKind>().Where(kind => voidSlot || owner != 0 || kind != BasicPowerKind.CallOfTheVoid)
            .Select(kind => new BasicPowerDefinition(kind, owner,
                owner == 0 && kind == BasicPowerKind.CallOfTheVoid ? voidAmount : 0, -1, 0, 1m, false))).ToArray();

    private static int VoidIndex(ResumableDiscardProgram lane)
        => Enumerable.Range(0, lane.PowerCount).Single(index =>
            lane.PowerDefinition(index) is { Owner: 0, Kind: BasicPowerKind.CallOfTheVoid });

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
