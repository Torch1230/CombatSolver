using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class AttackStartChecks
{
    internal static void Run()
    {
        // Synthetic nested attacks isolate start-vs-finish semantics: the inner attack
        // consumes the first-card window even with no hits of its own.
        ResumableDiscardProgram.Card[] cards = [new(0, new([
                new(CardInstructionKind.AttackTarget, 10), new(CardInstructionKind.Discard, 1),
                new(CardInstructionKind.AttackTarget, 10)]), Category: CardCategory.Attack),
            new(0, new([new(CardInstructionKind.GainEnergy, 1)]), Sly: true, Category: CardCategory.Attack)];
        BasicPowerDefinition[] powers = [new(BasicPowerKind.Strength, 0, 0, -1, 0, 1, false),
            new(BasicPowerKind.Lethality, 0, 75, 0, 1, 1, true)];
        foreach (int initial in new[] { 0, 2 })
        {
            var lane = new ResumableDiscardProgram(cards, [[0, 1], [], [], [], []], 0, 0, 0,
                creatures: [new(100, 100, 0), new(100, 100, 0)], powers: powers, attackCardStarts: initial);
            var root = lane.Freeze(); var mark = lane.State.Mark();
            lane.Begin(0, 1); lane.Run();
            if (!lane.NeedsChoice || lane.AttackCardStarts != initial + 1 || lane.Creature(1).CurrentHp != (initial == 0 ? 83 : 90))
                throw new InvalidOperationException("Attack start was not recorded before the pending selector.");
            var pending = lane.Freeze();
            lane.SupplyChoice([1]); lane.Run();
            if (!lane.Complete || lane.AttackCardStarts != initial + 2 || lane.Creature(1).CurrentHp != (initial == 0 ? 73 : 80))
                throw new InvalidOperationException("Nested card starts were confused with completed attacks.");
            var final = lane.Freeze(); lane.State.Rollback(mark);
            if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze())) throw new InvalidOperationException("Attack-start rollback leaked.");
            Parallel.For(0, 8, _ =>
            {
                var worker = pending.Open(); worker.SupplyChoice([1]); worker.Run();
                if (!worker.State.Freeze().ContentEquals(final.Open().State.Freeze())) throw new InvalidOperationException("Frozen attack-start counter differs.");
            });
        }
        var state = new ReversibleValueState(0);
        var layout = new BasicPowerLayout(state, powers);
        if (layout.ModifyAttack(state, 0, 1, 10) != 10 || layout.ModifyAttack(state, 0, 1, 10, firstCardAttack: true) != 17.5m)
            throw new InvalidOperationException("A powered hit without an eligible player card received Lethality.");
        Console.WriteLine("COMPACT_ATTACK_START_CHECKS_OK roots=2 nested_attack=true pending=true source_gate=true rollback=true frozen_workers=8");
    }
}
