using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class TemporaryStrengthChecks
{
    internal static void Run()
    {
        var definitions = Enumerable.Range(0, 3).SelectMany(owner => Enum.GetValues<BasicPowerKind>()
            .Select(kind => new BasicPowerDefinition(kind, owner, (owner, kind) switch
            {
                (1, BasicPowerKind.Strength) => 6,
                (2, BasicPowerKind.PiercingWail) => 999_999_999,
                _ => 0
            }, owner, owner, 1m, true))).ToArray();
        CardEffectProgram Wail(int amount) => new([new(CardInstructionKind.ApplyTemporaryStrengthLoss,
            amount, BasicPowerKind.PiercingWail, CardInstructionTarget.AllEnemies)]);
        var program = new ResumableDiscardProgram([new(0, Wail(6), ResultPile: ResumableDiscardProgram.Pile.Exhaust),
                new(0, Wail(8), ResultPile: ResumableDiscardProgram.Pile.Exhaust), new(0, new([new(CardInstructionKind.AttackTarget, 1)]))],
            [[0, 1, 2], [], [], [], []], 0, 0, 0,
            creatures: [new(30, 30, 0), new(20, 20, 0), new(1, 1, 0)], powers: definitions);
        int Slot(int owner, BasicPowerKind kind) => Array.FindIndex(definitions, p => p.Owner == owner && p.Kind == kind);
        int strength1 = Slot(1, BasicPowerKind.Strength), counter1 = Slot(1, BasicPowerKind.PiercingWail);
        int strength2 = Slot(2, BasicPowerKind.Strength), counter2 = Slot(2, BasicPowerKind.PiercingWail);
        var root = program.Freeze(); var mark = program.State.Mark();
        program.Begin(0); program.Run();
        if (program.Power(strength1).Amount != 0 || !program.Power(strength1).Retired
            || program.Power(counter1).Amount != 6 || program.Power(counter1).Applier != 0
            || program.Power(strength2).Amount != -6 || program.Power(counter2).Amount != 999_999_999
            || program.Power(counter2).Applier != 2)
            throw new InvalidOperationException("Temporary Strength first application/capped stack/source differs.");
        var first = program.Freeze();
        program.Begin(1); program.Run();
        if (program.Power(strength1).Amount != -8 || program.Power(counter1).Amount != 14
            || program.Power(strength1).Order <= program.Power(counter1).Order || program.Power(strength1).Applier != 0
            || program.Power(strength2).Amount != -14 || program.Count(ResumableDiscardProgram.Pile.Exhaust) != 2)
            throw new InvalidOperationException("Temporary Strength reacquisition/stack/order differs.");
        var stacked = program.Freeze();
        program.Begin(2, target: 2); program.Run();
        if (program.CreaturePresent(2) || program.Power(counter2).Amount != 0 || !program.Power(counter2).Retired
            || program.Power(strength2).Amount != 0 || program.Power(counter1).Amount != 14)
            throw new InvalidOperationException("Temporary Strength death cleanup crossed owner boundaries.");
        var killed = program.Freeze();
        program.State.Rollback(mark);
        if (!program.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("Temporary Strength transaction did not roll back.");
        Parallel.For(0, 8, _ =>
        {
            var lane = first.Open();
            lane.Begin(1); lane.Run();
            if (!lane.State.Freeze().ContentEquals(stacked.Open().State.Freeze()))
                throw new InvalidOperationException("Temporary Strength worker stack retained sibling values.");
            lane.Begin(2, target: 2); lane.Run();
            if (!lane.State.Freeze().ContentEquals(killed.Open().State.Freeze()))
                throw new InvalidOperationException("Temporary Strength worker death differs.");
            root.RestoreInto(lane);
            if (lane.Power(counter1).Amount != 0 || lane.Power(strength1).Amount != 6)
                throw new InvalidOperationException("Temporary Strength restore retained a retired root value.");
        });
        Console.WriteLine("COMPACT_TEMPORARY_STRENGTH_CHECKS_OK first=true cap=true stack=true retirement_reacquisition=true order=true applier=true exhaustion=true death=true rollback=true frozen_workers=8");
    }
}
