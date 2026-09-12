using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class ArtifactChecks
{
    internal static void Run()
    {
        var powers = Enumerable.Range(0, 3).SelectMany(owner => Enum.GetValues<BasicPowerKind>()
            .Select(kind => new BasicPowerDefinition(kind, owner, (owner, kind) switch
            {
                (1, BasicPowerKind.Strength) => 6, (1, BasicPowerKind.Artifact) => 2,
                (2, BasicPowerKind.Strength) => -2, (2, BasicPowerKind.PiercingWail) => 2, (2, BasicPowerKind.Artifact) => 1,
                _ => 0
            }, owner, owner + 1, 1m, true))).ToArray();
        var wail = new CardEffectProgram([new(CardInstructionKind.ApplyTemporaryStrengthLoss, 6, BasicPowerKind.PiercingWail, CardInstructionTarget.AllEnemies)]);
        var zero = new CardEffectProgram([new(CardInstructionKind.ApplyBasicPower, 0, BasicPowerKind.Weak, CardInstructionTarget.ChosenEnemy)]);
        var negative = new CardEffectProgram([new(CardInstructionKind.ApplyBasicPower, -3, BasicPowerKind.Strength, CardInstructionTarget.ChosenEnemy)]);
        var lane = new ResumableDiscardProgram([new(0, wail), new(0, wail), new(0, zero), new(0, negative), new(0, new([new(CardInstructionKind.AttackTarget, 1)]))],
            [[0, 1, 2, 3, 4], [], [], [], []], 0, 0, 0, creatures: [new(20, 20, 0), new(20, 20, 0), new(1, 1, 0)], powers: powers);
        int Slot(int owner, BasicPowerKind kind) => Array.FindIndex(powers, p => p.Owner == owner && p.Kind == kind);
        int Amount(int owner, BasicPowerKind kind) => lane.Power(Slot(owner, kind)).Amount;
        var root = lane.Freeze(); var mark = lane.State.Mark();
        lane.Begin(2, 1); lane.Run();
        if (Amount(1, BasicPowerKind.Artifact) != 2) throw new InvalidOperationException("Zero application consumed Artifact.");
        lane.Begin(0); lane.Run();
        if (Amount(1, BasicPowerKind.Artifact) != 1 || Amount(1, BasicPowerKind.Strength) != 6 || Amount(1, BasicPowerKind.PiercingWail) != 0
            || Amount(2, BasicPowerKind.Artifact) != 0 || Amount(2, BasicPowerKind.Strength) != -2 || Amount(2, BasicPowerKind.PiercingWail) != 2
            || !lane.Power(Slot(2, BasicPowerKind.Artifact)).Retired)
            throw new InvalidOperationException("Artifact must reject a whole first or stacked temporary effect.");
        var blocked = lane.Freeze();
        Continue(lane);
        if (Amount(1, BasicPowerKind.Strength) != 3 || Amount(1, BasicPowerKind.PiercingWail) != 0
            || Amount(1, BasicPowerKind.Artifact) != 0 || !lane.Power(Slot(1, BasicPowerKind.Artifact)).Retired
            || lane.CreaturePresent(2) || Amount(2, BasicPowerKind.PiercingWail) != 0)
            throw new InvalidOperationException("Artifact depletion, subsequent negative stat or death differs.");
        var final = lane.Freeze(); lane.State.Rollback(mark);
        if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze())) throw new InvalidOperationException("Artifact did not roll back.");
        Parallel.For(0, 8, _ =>
        {
            var worker = blocked.Open(); Continue(worker);
            if (!worker.State.Freeze().ContentEquals(final.Open().State.Freeze())) throw new InvalidOperationException("Artifact worker continuation differs.");
            root.RestoreInto(worker);
            if (!worker.State.Freeze().ContentEquals(root.Open().State.Freeze())) throw new InvalidOperationException("Artifact restore retained retirement.");
        });
        Console.WriteLine("COMPACT_ARTIFACT_CHECKS_OK zero=true first_temporary=true stacked_temporary=true depletion=true negative_stat=true death=true rollback=true frozen_workers=8");
    }

    private static void Continue(ResumableDiscardProgram lane)
    {
        lane.Begin(1); lane.Run(); lane.Begin(3, 1); lane.Run(); lane.Begin(4, 2); lane.Run();
    }
}
