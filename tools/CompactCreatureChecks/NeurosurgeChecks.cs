using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class NeurosurgeChecks
{
    internal static void Run()
    {
        var ordinary = new ResumableDiscardProgram.Card(0, new([new(CardInstructionKind.GainBlock, 0)]));
        var cards = new[]
        {
            new ResumableDiscardProgram.Card(0, new([new(CardInstructionKind.GainEnergy, 4), new(CardInstructionKind.Draw, 1),
                new(CardInstructionKind.ApplyBasicPower, 3, BasicPowerKind.Neurosurge)]), ResultPile: ResumableDiscardProgram.Pile.Removed),
            ordinary, ordinary
        };
        var powers = Enumerable.Range(0, 2).SelectMany(owner => Enum.GetValues<BasicPowerKind>().Select(kind =>
            new BasicPowerDefinition(kind, owner, (owner, kind) switch
            {
                (0, BasicPowerKind.Neurosurge) => 2, (0, BasicPowerKind.Artifact) => 1, _ => 0
            }, owner, 1, 1m, true))).ToArray();
        int Slot(BasicPowerKind kind) => Array.FindIndex(powers, power => power.Owner == 0 && power.Kind == kind);
        var lane = new ResumableDiscardProgram(cards, [[0], [], [1, 2], [], []], 999_999_998, 0, 0,
            new(0, 1, 2, 3, 4), new int[9], stratagem: 1, creatures: [new(4, 4, 0), new(10, 10, 0)], powers: powers,
            handEndAdmitted: true, monsterMoves: [new([])], powerPhasesAdmitted: true,
            monsterAi: new(1, 0, [0], [0]), round: new(7, 9, 3, 2));
        var initial = lane.Freeze(); var mark = lane.State.Mark();
        lane.Begin(0); lane.Run();
        var pendingCard = lane.Freeze();
        if (!lane.NeedsChoice || lane.Energy != 999_999_999 || lane.Power(Slot(BasicPowerKind.Doom)).Amount != 0
            || lane.Power(Slot(BasicPowerKind.Artifact)).Amount != 1)
            throw new InvalidOperationException("Energy cap, OnPlay suspension or captured side-start state differs.");
        Complete(lane);
        if (lane.Power(Slot(BasicPowerKind.Neurosurge)).Amount != 2 || lane.Power(Slot(BasicPowerKind.Artifact)).Amount != 0)
            throw new InvalidOperationException("Post-draw Neurosurge application skipped Artifact.");
        lane.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Together); lane.Run();
        var pendingRound = lane.Freeze();
        if (!lane.NeedsChoice || lane.Power(Slot(BasicPowerKind.Doom)).Amount != 2)
            throw new InvalidOperationException("Side-start Doom must commit before the first setup choice.");
        Continue(lane);
        var final = lane.Freeze();
        if (!lane.Terminal || !lane.DefeatTerminal || lane.EnemySide || lane.PlayerTurn != 11 || lane.TerminalPlayerTurn != 11
            || lane.MonsterMoveLogCount != 3 || lane.Creature(0).CurrentHp != 0 || !lane.CreaturePresent(0)
            || Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).Any(e => e.Kind == ResumableDiscardProgram.EventKind.Damage))
            throw new InvalidOperationException("Player Doom death advanced the enemy phase or became damage.");
        lane.State.Rollback(mark);
        if (!lane.State.Freeze().ContentEquals(initial.Open().State.Freeze())) throw new InvalidOperationException("Doom round rollback leaked.");
        Parallel.For(0, 8, _ =>
        {
            var worker = pendingCard.Open(); Complete(worker);
            worker.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Together); worker.Run();
            if (!worker.State.Freeze().ContentEquals(pendingRound.Open().State.Freeze())) throw new InvalidOperationException("Worker repeated the card prefix.");
            Continue(worker);
            if (!worker.State.Freeze().ContentEquals(final.Open().State.Freeze())) throw new InvalidOperationException("Worker repeated a side-start callback.");
            initial.RestoreInto(worker);
            worker.Begin(0); worker.Run();
            if (!worker.State.Freeze().ContentEquals(pendingCard.Open().State.Freeze())) throw new InvalidOperationException("Reverse restore retained the side-start flag.");
        });
        var enemyPowers = powers.Select(power => power with { Amount = power is { Owner: 1, Kind: BasicPowerKind.Doom } ? 3 : 0 }).ToArray();
        var enemy = new ResumableDiscardProgram([ordinary], [[0], [], [], [], []], 1, 0, 0, comparisons: [0],
            creatures: [new(10, 10, 0), new(3, 3, 0)], powers: enemyPowers,
            handEndAdmitted: true, monsterMoves: [new([new(MonsterInstructionKind.GainBlock, 9)])], powerPhasesAdmitted: true,
            monsterAi: new(1, 0, [0], [0]), round: new(7, 9, 3, 2));
        enemy.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Together);
        if (!enemy.Terminal || enemy.DefeatTerminal || !enemy.EnemySide || enemy.PlayerTurn != 9 || enemy.MonsterMoveLogCount != 1
            || enemy.CreaturePresent(1) || enemy.Creature(1).Block != 9)
            throw new InvalidOperationException("Enemy Doom must follow its move and precede the next player turn.");
        Console.WriteLine("COMPACT_NEUROSURGE_CHECKS_OK energy_cap=true onplay_pause=true artifact=true side_start_once=true doom_both_terminal_phases=true reverse_restore=true frozen_workers=8");

        static void Continue(ResumableDiscardProgram program)
        {
            Complete(program);
            program.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Together); Complete(program);
            program.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Together);
        }
    }

    private static void Complete(ResumableDiscardProgram program)
    {
        while (!program.Complete)
        {
            program.Run();
            if (program.NeedsChoice) program.SupplyChoice(program.Cards(program.ChoicePile).Take(program.ChoiceCount).ToArray());
        }
    }
}
