using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class CardLifecycleChecks
{
    internal static void Run()
    {
        var definitions = Enumerable.Range(0, 2).SelectMany(owner => Enum.GetValues<BasicPowerKind>().Select(kind =>
            new BasicPowerDefinition(kind, owner, owner == 1 && kind == BasicPowerKind.Strength ? 1 : 0,
                owner, owner == 1 && kind == BasicPowerKind.Strength ? 1 : 0, kind == BasicPowerKind.Weak ? 0.75m : 1.5m,
                owner == 1 && kind == BasicPowerKind.Strength))).ToArray();
        CardEffectProgram dexterity = new([new(CardInstructionKind.ApplyBasicPower, 3, BasicPowerKind.Dexterity)]);
        CardEffectProgram xEffects = new([new(CardInstructionKind.ApplyBasicPower, -1, BasicPowerKind.Strength,
                CardInstructionTarget.ChosenEnemy, -1),
            new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.Weak, CardInstructionTarget.ChosenEnemy, 1)]);
        CardEffectProgram zeroBlock = new([new(CardInstructionKind.GainBlock, 0)]);
        var program = new ResumableDiscardProgram([new(1, dexterity, ResultPile: ResumableDiscardProgram.Pile.Removed),
                new(0, xEffects, ResultPile: ResumableDiscardProgram.Pile.Exhaust, CostsX: true), new(0, zeroBlock)],
            [[0, 1, 2], [], [], [], []], 5, 2, 0, creatures: [new(50, 50, 2), new(50, 50, 0)], powers: definitions);
        var root = program.Freeze();
        var rootMark = program.State.Mark();
        program.Begin(0); program.Run();
        program.Begin(1, 1);
        if (program.Energy != 0 || program.CapturedX(1) != 4 || !program.CardRemoved(0))
            throw new InvalidOperationException("X payment or Power removal was not journaled before execution.");
        var paid = program.Freeze();
        program.State.Rollback(rootMark);
        if (!program.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("Lifecycle rollback lost prepayment state.");
        Parallel.For(0, 8, _ =>
        {
            var lane = paid.Open(); lane.Run();
            if (!lane.Complete || lane.ResultPile(1) != ResumableDiscardProgram.Pile.Exhaust
                || !lane.Cards(ResumableDiscardProgram.Pile.Exhaust).SequenceEqual(new[] { 1 })
                || lane.Power(Array.FindIndex(definitions, d => d.Owner == 1 && d.Kind == BasicPowerKind.Strength)).Amount != -4
                || lane.Power(Array.FindIndex(definitions, d => d.Owner == 1 && d.Kind == BasicPowerKind.Weak)).Amount != 5)
                throw new InvalidOperationException("X instructions reread spent energy or lost signed Power amounts.");
            lane.Begin(2); lane.Run();
            if (lane.Block != 5) throw new InvalidOperationException("A removed Power card did not affect a later zero-base block.");
            var completed = lane.Freeze();
            root.RestoreInto(lane);
            if (lane.CardRemoved(0) || lane.CapturedX(1) != 0 || lane.Count(ResumableDiscardProgram.Pile.Exhaust) != 0)
                throw new InvalidOperationException("Root restore retained card lifecycle metadata.");
            completed.RestoreInto(lane);
            if (!lane.State.Freeze().ContentEquals(completed.Open().State.Freeze()))
                throw new InvalidOperationException("Restoring a completed lifecycle candidate lost values.");
        });
        Console.WriteLine("COMPACT_CARD_LIFECYCLE_CHECKS_OK removed_power=true signed_x=true frozen_after_payment=true exhaust=true zero_base_block=true root_restore=true frozen_workers=8");
    }
}
