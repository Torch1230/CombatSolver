using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class PanacheChecks
{
    internal static void Run()
    {
        BasicPowerDefinition[] powers = Enumerable.Range(0, 3).SelectMany(owner => Enum.GetValues<BasicPowerKind>()
            .Select(kind => new BasicPowerDefinition(kind, owner, 0, -1, 0, 1, false))).ToArray();
        ResumableDiscardProgram.Card[] cards = [new(0, new([new(CardInstructionKind.Discard, 1)]), Category: CardCategory.Skill),
            new(0, new([new(CardInstructionKind.AddPanachePower, 10)]), Sly: true, Category: CardCategory.Power)];
        var lane = new ResumableDiscardProgram(cards, [[0, 1], [], [], [], []], 0, 0, 0,
            creatures: [new(100, 100, 0), new(100, 100, 0), new(3, 3, 0)], powers: powers, powerPhasesAdmitted: true,
            panache: [new(6, 0, 7, CardsLeft: 1, AlreadyApplied: true), new(9, 0, 8, CardsLeft: 2, AlreadyApplied: true)]);
        var root = lane.Freeze(); var mark = lane.State.Mark();
        lane.Begin(0); lane.Run(); var pending = lane.Freeze();
        lane.SupplyChoice([1]); lane.Run();
        if (lane.PanacheCount != 3 || lane.Panache(2).Order != 9 || lane.Panache(2).CardsLeft != 4
            || lane.Creature(1).CurrentHp != 85 || lane.CreaturePresent(2)
            || lane.Panache(0).CardsLeft != 4 || lane.Panache(1).CardsLeft != 5)
            throw new InvalidOperationException("Independent counters, nested completion or ordered group damage differs.");
        var completed = lane.Freeze();
        lane.EndSidePowerEffects(false); lane.CapturePowerTurnStart(0);
        if (Enumerable.Range(0, lane.PanacheCount).Any(index => lane.Panache(index).CardsLeft != 5
            || !lane.Panache(index).AlreadyApplied || lane.Panache(index).AmountOnTurnStart != lane.Panache(index).Amount))
            throw new InvalidOperationException("Independent turn reset changed its activation lifetime.");
        lane.State.Rollback(mark);
        if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze())) throw new InvalidOperationException("Independent Power rollback leaked.");
        Parallel.For(0, 8, _ =>
        {
            var worker = pending.Open(); worker.SupplyChoice([1]); worker.Run();
            if (!worker.State.Freeze().ContentEquals(completed.Open().State.Freeze())) throw new InvalidOperationException("Independent frozen completion differs.");
        });

        // The number of instances is not limited by cards, frames or a fixed slot table.
        var growth = new ResumableDiscardProgram([new(0, new(Enumerable.Range(0, 300)
                .Select(index => new CardInstruction(CardInstructionKind.AddPanachePower, index + 1)).ToArray()))],
            [[0], [], [], [], []], 0, 0, 0, creatures: [new(100, 100, 0), new(100, 100, 0)], powers: powers.Where(power => power.Owner < 2).ToArray());
        var growthRoot = growth.Freeze(); var growthMark = growth.State.Mark();
        growth.Begin(0); growth.Run(); var grown = growth.Freeze();
        if (growth.PanacheCount != 300 || growth.Panache(299) is not { Amount: 300, Order: 300, CardsLeft: 5, AlreadyApplied: true })
            throw new InvalidOperationException("Independent Power growth lost identity or counters.");
        growth.State.Rollback(growthMark);
        if (!growth.State.Freeze().ContentEquals(growthRoot.Open().State.Freeze())) throw new InvalidOperationException("Independent buffer growth did not roll back.");
        Parallel.For(0, 8, _ =>
        {
            var worker = growthRoot.Open(); worker.Begin(0); worker.Run();
            if (!worker.State.Freeze().ContentEquals(grown.Open().State.Freeze())) throw new InvalidOperationException("Independent buffer growth changed across workers.");
        });
        Console.WriteLine("COMPACT_PANACHE_CHECKS_OK independent=true nested_completion=true multi_target_death=true reset=true growth=300 rollback=true frozen_workers=8");
    }
}
