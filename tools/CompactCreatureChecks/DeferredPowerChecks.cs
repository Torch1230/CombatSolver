using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class DeferredPowerChecks
{
    internal static void Run()
    {
        static BasicPowerDefinition[] Powers(int dexterity) => Enumerable.Range(0, 2)
            .SelectMany(owner => Enum.GetValues<BasicPowerKind>().Select(kind => new BasicPowerDefinition(kind, owner,
                owner == 0 ? kind switch { BasicPowerKind.Dexterity => dexterity, BasicPowerKind.Frail => 2, _ => 0 } : 0,
                0, 0, 0.75m, true))).ToArray();
        CardEffectProgram deferred = new([new(CardInstructionKind.GainBlockAndApplyPower, 6, BasicPowerKind.BlockNextTurn)]);
        ResumableDiscardProgram Make(int dexterity, bool growth = false) => new(
            [new(0, deferred), new(0, new([new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.ToolsOfTheTrade)]), ResultPile: ResumableDiscardProgram.Pile.Removed),
                new(0, growth ? new([new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.Dexterity)]) : deferred, ResultPile: ResumableDiscardProgram.Pile.Removed)],
            [[0, 1, 2], [], [], [], []], 0, 999_999_998, 0,
            creatures: [new(50, 50, 999_999_998), new(20, 20, 0)], powers: Powers(dexterity));
        var program = Make(-1);
        int deferredSlot = Enumerable.Range(0, program.PowerCount).Single(index => program.PowerDefinition(index).Owner == 0
            && program.PowerDefinition(index).Kind == BasicPowerKind.BlockNextTurn);
        int toolsSlot = Enumerable.Range(0, program.PowerCount).Single(index => program.PowerDefinition(index).Owner == 0
            && program.PowerDefinition(index).Kind == BasicPowerKind.ToolsOfTheTrade);
        var root = program.Freeze(); var mark = program.State.Mark();
        program.Begin(0); program.Run();
        if (program.Block != 999_999_999 || program.Power(deferredSlot).Amount != 3)
            throw new InvalidOperationException("Deferred block used the capped delta instead of its fractional return.");
        var first = program.Freeze();
        program.State.Rollback(mark);
        if (!program.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("Deferred Power did not roll back.");
        Parallel.For(0, 8, _ =>
        {
            var lane = first.Open();
            lane.Begin(2); lane.Run(); lane.Begin(1); lane.Run();
            if (lane.Power(deferredSlot).Amount != 6 || lane.Power(toolsSlot).Amount != 1
                || lane.Count(ResumableDiscardProgram.Pile.Removed) != 2 || lane.Power(toolsSlot).Applier != 0)
                throw new InvalidOperationException("Deferred Power stacking, source or card removal differs.");
            root.RestoreInto(lane);
            if (lane.Power(toolsSlot).Amount != 0 || lane.Power(deferredSlot).Amount != 0)
                throw new InvalidOperationException("Deferred Power restore retained a sibling counter.");
        });
        var zero = Make(-6); zero.Begin(0); zero.Run();
        if (zero.Power(deferredSlot).Amount != 0 || zero.Block != 999_999_998)
            throw new InvalidOperationException("Zero block return created a Power.");
        foreach (var input in new[] { (Dexterity: -5, Growth: false), (Dexterity: -6, Growth: true) })
        {
            try { _ = Make(input.Dexterity, input.Growth); }
            catch (NotSupportedException) { continue; }
            throw new InvalidOperationException("Reachable fractional zero-amount Power was admitted.");
        }
        Console.WriteLine("COMPACT_DEFERRED_POWER_CHECKS_OK fractional_return=true cap=true stack=true zero=true removal=true applier=true rollback=true frozen_workers=8 zero_instance_closure_rejected=true");
    }
}
