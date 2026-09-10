using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class PowerExpressionChecks
{
    internal static void Run()
    {
        BasicPowerDefinition[] powers = Enumerable.Range(0, 3).SelectMany(owner => Enum.GetValues<BasicPowerKind>()
            .Select(kind => new BasicPowerDefinition(kind, owner, (owner, kind) switch
            {
                (0, BasicPowerKind.Dexterity) => -1, (0, BasicPowerKind.Frail) => 2,
                (1, BasicPowerKind.Poison) => 1, (2, BasicPowerKind.Poison) => 2, _ => 0
            }, 0, owner * 6 + (int)kind + 1, 0.75m, true))).ToArray();
        CardEffectProgram predicate = new([new(CardInstructionKind.SkipIfTargetLacksPower, 1, BasicPowerKind.Poison, CardInstructionTarget.ChosenEnemy),
            new(CardInstructionKind.ApplyBasicPower, 9, BasicPowerKind.Poison, CardInstructionTarget.ChosenEnemy)]);
        CardEffectProgram sum = new([new(CardInstructionKind.GainBlockFromPowerSum, 2, BasicPowerKind.Poison,
            CardInstructionTarget.AllEnemies, PowerMultiplier: 3)]);
        var program = new ResumableDiscardProgram([new(0, predicate), new(0, sum),
                new(0, new([new(CardInstructionKind.TriggerBasicPower, 0, BasicPowerKind.Poison, CardInstructionTarget.ChosenEnemy)])),
                new(0, new([new(CardInstructionKind.AttackTarget, 100)])),
                new(0, new([new(CardInstructionKind.ApplyBasicPower, 4, BasicPowerKind.Poison, CardInstructionTarget.ChosenEnemy)]))],
            [[0, 1, 2, 3, 4], [], [], [], []], 0, 3, 0,
            creatures: [new(50, 50, 3), new(20, 20, 0), new(20, 20, 0)], powers: powers);
        var root = program.Freeze();
        (int Card, int Target)[][] paths = [[(1, -1)], [(0, 1), (1, -1)], [(2, 1), (0, 1), (1, -1)],
            [(2, 1), (4, 1), (0, 1), (1, -1)], [(0, 1), (3, 1), (1, -1)]];
        int[] expectedBlock = [10, 30, 8, 37, 8];
        List<ResumableDiscardProgram.Candidate> retained = [];
        for (int index = 0; index < paths.Length; index++)
        {
            var mark = program.State.Mark();
            foreach (var action in paths[index]) { program.Begin(action.Card, action.Target); program.Run(); }
            if (program.Block != expectedBlock[index])
                throw new InvalidOperationException("Power expression used root, retired or removed enemy values.");
            retained.Add(program.Freeze());
            program.State.Rollback(mark);
            if (!program.State.Freeze().ContentEquals(root.Open().State.Freeze()))
                throw new InvalidOperationException("Power expression failed to roll back.");
        }
        Parallel.For(0, 8, _ =>
        {
            var lane = root.Open();
            for (int index = paths.Length - 1; index >= 0; index--)
            {
                root.RestoreInto(lane);
                foreach (var action in paths[index]) { lane.Begin(action.Card, action.Target); lane.Run(); }
                if (!lane.State.Freeze().ContentEquals(retained[index].Open().State.Freeze()))
                    throw new InvalidOperationException("Power expression worker retained sibling values.");
            }
        });
        CardInstruction[][] invalid = [[new(CardInstructionKind.SkipIfTargetLacksPower, 1, BasicPowerKind.Poison, CardInstructionTarget.ChosenEnemy)],
            [new(CardInstructionKind.GainBlockFromPowerSum, 0, BasicPowerKind.Poison, PowerMultiplier: 1)],
            [new(CardInstructionKind.GainBlock, 1, PowerMultiplier: 1)]];
        foreach (var instructions in invalid)
        {
            try { _ = new CardEffectProgram(instructions); }
            catch (ArgumentException) { continue; }
            catch (NotSupportedException) { continue; }
            throw new InvalidOperationException("Invalid Power expression was admitted.");
        }
        Console.WriteLine("COMPACT_POWER_EXPRESSION_CHECKS_OK presence=true retire_reacquire=true living_sum=true base_extra=true rounding=true rollback=true frozen_workers=8 invalid_rejected=true");
    }
}
