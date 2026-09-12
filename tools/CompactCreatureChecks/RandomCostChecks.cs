using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class RandomCostChecks
{
    internal static void Run()
    {
        ValueRng initialRng = new(11, 0x83f4ea876de83187, 0x467215e88d731934, 0x68f4672e51ed5634, 0x9241f375068e3fe7);
        var definition = new RandomDrawCost([3, 1]);
        ResumableDiscardProgram.Card[] cards = [new(0, new([new(CardInstructionKind.Draw, 1)])),
            new(2, new([new(CardInstructionKind.Draw, 1)]), DrawCost: definition)];
        var lane = new ResumableDiscardProgram(cards, [[0], [1], [], [], []], 1000, 0, 0,
            new(0, 1, 2, 3, 4), [0, -1, 1, 0], energyCostRng: initialRng);
        var root = lane.Freeze(); var mark = lane.State.Mark();
        ValueRng expectedRng = initialRng;
        List<int> expectedCosts = [3, 1];
        int energy = 1000;
        ResumableDiscardProgram.Candidate? middle = null;
        for (int cycle = 0; cycle < 130; cycle++)
        {
            expectedRng = expectedRng.NextInt(4, out int cost);
            expectedCosts.Add(cost);
            Cycle(lane);
            energy -= cost;
            if (lane.Energy != energy || lane.EnergyCost(1) != cost || lane.EnergyCostRng != expectedRng
                || !Enumerable.Range(0, lane.CostModifierCount(1)).Select(i => lane.CostModifierAt(1, i)).SequenceEqual(expectedCosts))
                throw new InvalidOperationException("Repeated random draws lost a modifier, RNG step or payment.");
            if (cycle == 69) middle = lane.Freeze();
        }
        if (expectedCosts.Distinct().Count() != 4) throw new InvalidOperationException("Random-cost fixture misses an admitted outcome.");
        var completed = lane.Freeze();
        lane.State.Rollback(mark);
        if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("Random-cost lists or RNG did not roll back.");
        Parallel.For(0, 8, _ =>
        {
            var worker = middle!.Open();
            for (int cycle = 70; cycle < 130; cycle++) Cycle(worker);
            if (!worker.State.Freeze().ContentEquals(completed.Open().State.Freeze()))
                throw new InvalidOperationException("Random-cost frozen continuation retained sibling state.");
            root.RestoreInto(worker);
            if (worker.CostModifierCount(1) != 2 || worker.EnergyCost(1) != 1 || worker.EnergyCostRng != initialRng)
                throw new InvalidOperationException("Random-cost prefix restore lost the captured modifier list.");
        });
        ResumableDiscardProgram.Card[] full = Enumerable.Range(0, 12).Select(index => new ResumableDiscardProgram.Card(0,
            index == 0 ? new([new(CardInstructionKind.Draw, 2)]) : CardEffectProgram.Empty,
            DrawCost: index >= 10 ? new([]) : null)).ToArray();
        var capped = new ResumableDiscardProgram(full, [Enumerable.Range(0, 10).ToArray(), [10, 11], [], [], []], 0, 0, 0,
            energyCostRng: initialRng);
        capped.Begin(0); capped.Run();
        if (capped.Count(ResumableDiscardProgram.Pile.Hand) != 10 || capped.CostModifierCount(10) != 1
            || capped.CostModifierCount(11) != 0 || capped.EnergyCostRng!.Value.Counter != initialRng.Counter + 1)
            throw new InvalidOperationException("Full-hand draw consumed RNG for an undrawn card.");
        Console.WriteLine("COMPACT_RANDOM_COST_CHECKS_OK repeated_draws=130 full_modifier_lists=true all_costs=true payment=true full_hand_gate=true rollback=true frozen_workers=8");
    }

    private static void Cycle(ResumableDiscardProgram lane)
    {
        lane.Begin(0); lane.Run(); lane.Begin(1); lane.Run();
    }
}
