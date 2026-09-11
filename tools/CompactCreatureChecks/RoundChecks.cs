using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class RoundChecks
{
    internal static void Run()
    {
        var ordinary = new ResumableDiscardProgram.Card(0, new([new(CardInstructionKind.GainBlock, 1)]));
        ResumableDiscardProgram.Card[] cards = [ordinary, ordinary with { Retain = true },
            new(0, new([new(CardInstructionKind.Draw, 1), new(CardInstructionKind.Discard, 1)]), SingleTurnSly: true),
            ordinary, ordinary, ordinary, ordinary];
        var powers = Enumerable.Range(0, 2).SelectMany(owner => Enum.GetValues<BasicPowerKind>().Select(kind =>
            new BasicPowerDefinition(kind, owner, (owner, kind) switch
            {
                (0, BasicPowerKind.ToolsOfTheTrade) => 2,
                (0, BasicPowerKind.BlockNextTurn) => 4,
                (1, BasicPowerKind.Poison) => 2,
                (1, BasicPowerKind.Artifact) => 2,
                _ => 0
            }, -1, 0, 1m, false))).ToArray();
        var lane = new ResumableDiscardProgram(cards, [[0, 1, 2], [3, 4], [5, 6], [], []], 0, 8, 3,
            new(0, 1, 2, 3, 4), new int[49], stratagem: 1,
            creatures: [new(100, 100, 8), new(100, 100, 9)], powers: powers,
            handEndAdmitted: true, monsterMoves: [new([new(MonsterInstructionKind.AttackPlayer, 5)])],
            powerPhasesAdmitted: true, monsterAi: new(1, 0, [0], [0]), round: new(7, 9, 3, 2));
        var root = lane.Freeze(); var mark = lane.State.Mark();
        lane.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Sequential);
        lane.Run();
        if (!lane.NeedsChoice || lane.ChoicePile != ResumableDiscardProgram.Pile.Draw || lane.ChoiceCard != -1
            || lane.RoundNumber != 8 || lane.PlayerTurn != 10 || lane.Energy != 3 || lane.SingleTurnSly(2)
            || lane.Creature(1).CurrentHp != 98 || lane.Creature(1).Block != 0 || lane.Creature(0).CurrentHp != 100
            || lane.Block != 4 || !lane.Cards(ResumableDiscardProgram.Pile.Hand).Contains(1))
            throw new InvalidOperationException("Round ordering, retained hand, cleanup or suspended hand draw differs.");
        var middle = lane.Freeze();
        Complete(lane);
        if (lane.Cards(ResumableDiscardProgram.Pile.Hand).Length != 4 || lane.Block != 10
            || Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).Any(e => e.Kind == ResumableDiscardProgram.EventKind.Start)
            || Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).Count(e => e.Kind == ResumableDiscardProgram.EventKind.Draw && e.Value == 1) != 4)
            throw new InvalidOperationException("Tools discard, expired temporary Sly or hand-draw provenance differs.");
        lane.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Sequential);
        Complete(lane);
        if (lane.RoundNumber != 9 || lane.PlayerTurn != 11 || lane.Creature(1).CurrentHp != 97 || lane.MonsterMoveLogCount != 3
            || Enumerable.Range(0, lane.PowerCount).Any(index => lane.PowerDefinition(index) is { Owner: 1, Kind: BasicPowerKind.Artifact } && lane.Power(index).Amount != 2))
            throw new InvalidOperationException("Second round clock, poison or AI differs.");
        var final = lane.Freeze();
        lane.State.Rollback(mark);
        if (!lane.SingleTurnSly(2) || !lane.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("Round rollback retained clock, cleanup or history.");
        Parallel.For(0, 8, _ =>
        {
            var worker = middle.Open();
            Complete(worker);
            worker.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Sequential);
            Complete(worker);
            if (!worker.State.Freeze().ContentEquals(final.Open().State.Freeze()))
                throw new InvalidOperationException("Round continuation changed across frozen workers.");
        });
        // Fatal enemy start locks the existing player turn, before move or hand draw.
        var fatal = new ResumableDiscardProgram([ordinary], [[0], [], [], [], []], 1, 0, 0,
            comparisons: [0], creatures: [new(10, 10, 0), new(1, 1, 0)], powers: powers,
            handEndAdmitted: true, monsterMoves: [new([new(MonsterInstructionKind.AttackPlayer, 99)])],
            powerPhasesAdmitted: true, monsterAi: new(1, 0, [0], [0]), round: new(7, 9, 3, 2));
        fatal.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Sequential);
        if (!fatal.Terminal || fatal.DefeatTerminal || fatal.TerminalPlayerTurn != 9 || !fatal.EnemySide
            || fatal.PlayerTurn != 9 || fatal.MonsterMoveLogCount != 1 || fatal.Creature(0).CurrentHp != 10)
            throw new InvalidOperationException("Enemy-start terminal advanced the player clock or AI.");
        // An AI transition precedes setup selection, while published intent membership
        // still describes the previous turn. Empty root logs must retain that previous move.
        var intent = new ResumableDiscardProgram([ordinary], [[0], [], [], [], []], 0, 0, 0,
            comparisons: [0], creatures: [new(10, 10, 0), new(10, 10, 0)],
            powers: powers.Select(power => power with { Amount = power is { Owner: 0, Kind: BasicPowerKind.ToolsOfTheTrade } ? 1 : 0 }).ToArray(),
            handEndAdmitted: true, monsterMoves: [new([]), new([])], powerPhasesAdmitted: true,
            monsterAi: new(1, 1, [1, 0], []), round: new(7, 9, 3, 0));
        intent.BeginNextPlayerTurn(ResumableDiscardProgram.HandEndStaging.Together);
        intent.Run();
        var intentPending = intent.Freeze();
        if (!intent.NeedsChoice || intent.CurrentMonsterMove != 0 || intent.PublishedMonsterIntentMove != 1)
            throw new InvalidOperationException("Pending setup published its new AI intent too early.");
        Complete(intent);
        if (intent.PublishedMonsterIntentMove != 0 || intentPending.Open().PublishedMonsterIntentMove != 1)
            throw new InvalidOperationException("Published intent failed to follow the frozen setup boundary.");
        Console.WriteLine("COMPACT_ROUND_CHECKS_OK two_rounds=true noninitial_clock=true retained_hand=true cleanup=true hand_draw_shuffle=true tools=true poison=true terminal_turn=true frozen_workers=8 rollback=true pending_intent=true");
    }

    private static void Complete(ResumableDiscardProgram lane)
    {
        while (!lane.Complete)
        {
            lane.Run();
            if (!lane.NeedsChoice) continue;
            int[] eligible = lane.Cards(lane.ChoicePile);
            int[] selected = eligible.OrderBy(card => card == 2 ? 0 : 1).ThenBy(card => card).Take(lane.ChoiceCount).ToArray();
            lane.SupplyChoice(selected);
        }
    }
}
