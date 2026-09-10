using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class PoisonDiscardChecks
{
    internal static void Run()
    {
        BasicPowerDefinition[] powers = Enumerable.Range(0, 3).SelectMany(owner => Enum.GetValues<BasicPowerKind>()
            .Select(kind => new BasicPowerDefinition(kind, owner, kind == BasicPowerKind.Poison ? owner : 0,
                owner == 1 ? 2 : 0, owner, 0.75m, kind == BasicPowerKind.Poison && owner != 0))).ToArray();
        CardInstruction trigger = new(CardInstructionKind.TriggerBasicPower, 0, BasicPowerKind.Poison, CardInstructionTarget.AllEnemies);
        var poison = new ResumableDiscardProgram([new(0, new([trigger])),
                new(0, new([new(CardInstructionKind.ApplyBasicPower, 2, BasicPowerKind.Poison, CardInstructionTarget.AllEnemies), trigger])),
                new(0, new([trigger])), new(0, new([new(CardInstructionKind.ApplyBasicPower, 9, BasicPowerKind.Poison, CardInstructionTarget.AllEnemies), trigger]))],
            [[0, 1, 2, 3], [], [], [], []], 0, 0, 0,
            creatures: [new(50, 50, 0), new(8, 8, 20), new(3, 3, 20)], powers: powers);
        var root = poison.Freeze(); var checkpoint = poison.State.Mark();
        poison.Begin(0); poison.Run();
        int first = Enumerable.Range(0, poison.PowerCount).Single(index => poison.PowerDefinition(index) is { Owner: 1, Kind: BasicPowerKind.Poison });
        if (poison.Creature(1) != new CreatureVitals(7, 8, 20) || poison.Creature(2) != new CreatureVitals(1, 3, 20)
            || poison.Power(first) is not { Amount: 0, Retired: true })
            throw new InvalidOperationException("Poison failed to bypass block, decrement or retire its root instance.");
        poison.Begin(1); poison.Run();
        if (poison.Creature(1).CurrentHp != 5 || poison.CreaturePresent(2) || poison.Power(first) is not { Amount: 1, Applier: 0, Retired: true })
            throw new InvalidOperationException("Poison reacquisition or death cleanup differs.");
        var partial = poison.Freeze();
        poison.State.Rollback(checkpoint);
        if (!poison.State.Freeze().ContentEquals(root.Open().State.Freeze()))
            throw new InvalidOperationException("Poison death/reacquisition failed to undo.");
        Parallel.For(0, 8, _ =>
        {
            var lane = partial.Open(); lane.Begin(2); lane.Run(); lane.Begin(3); lane.Run();
            if (!lane.Ending || !lane.CheckWinCondition() || lane.Count(ResumableDiscardProgram.Pile.Play) != 1)
                throw new InvalidOperationException("Indirect final death bypassed the native terminal or result-pile gate.");
            foreach (var item in Enumerable.Range(0, lane.EventCount).Select(lane.EventAt))
            {
                if (item.Kind == ResumableDiscardProgram.EventKind.AttackFinish)
                    throw new InvalidOperationException("Poison emitted an attack completion.");
                if (item.Kind == ResumableDiscardProgram.EventKind.Damage
                    && ((item.Flags & 248) != 248 || (item.Flags & 6) != 0))
                    throw new InvalidOperationException("Poison lost indirect provenance or falsely broke/used block.");
            }
            root.RestoreInto(lane);
            if (lane.Terminal || !lane.CreaturePresent(2) || lane.Creature(1).Block != 20)
                throw new InvalidOperationException("Poison worker retained terminal state.");
        });

        var gamble = new ResumableDiscardProgram([new(0, new([new(CardInstructionKind.DiscardHandAndDraw, 0), new(CardInstructionKind.GainBlock, 5)]),
                ResultPile: ResumableDiscardProgram.Pile.Exhaust),
                new(0, new([new(CardInstructionKind.Draw, 1), new(CardInstructionKind.Discard, 1), new(CardInstructionKind.GainBlock, 7)]), Sly: true),
                new(0, CardEffectProgram.Empty), new(0, CardEffectProgram.Empty), new(0, CardEffectProgram.Empty)],
            [[0, 1, 2], [3], [4], [], []], 3, 0, 3, new(0, 11, 22, 33, 44), new int[25], stratagem: 1);
        var gambleRoot = gamble.Freeze(); var mark = gamble.State.Mark();
        gamble.Begin(0); gamble.Run();
        if (!gamble.NeedsChoice || gamble.ChoicePile != ResumableDiscardProgram.Pile.Draw || gamble.Block != 6
            || Enumerable.Range(0, gamble.EventCount).Select(gamble.EventAt).Count(e => e.Kind == ResumableDiscardProgram.EventKind.Start) != 1)
            throw new InvalidOperationException("Hand discard must finish hooks and partial draw before starting Sly.");
        var pendingShuffle = gamble.Freeze();
        gamble.SupplyChoice([1]); gamble.Run();
        if (gamble.ChoiceCard != 1 || gamble.ChoicePile != ResumableDiscardProgram.Pile.Hand
            || Enumerable.Range(0, gamble.EventCount).Select(gamble.EventAt).Count(e => e.Kind == ResumableDiscardProgram.EventKind.Draw) != 3)
            throw new InvalidOperationException("Redrawn Sly did not execute after all parent draws.");
        var pendingChild = gamble.Freeze();
        gamble.State.Rollback(mark);
        if (!gamble.State.Freeze().ContentEquals(gambleRoot.Open().State.Freeze()))
            throw new InvalidOperationException("Batch discard undo lost the captured hand.");
        Parallel.For(0, 8, _ =>
        {
            var lane = pendingShuffle.Open(); lane.SupplyChoice([1]); lane.Run();
            if (!lane.State.Freeze().ContentEquals(pendingChild.Open().State.Freeze()))
                throw new InvalidOperationException("Shuffle resume repeated discard or changed Sly order.");
            lane.SupplyChoice([2]); lane.Run();
            if (!lane.Complete || lane.Block != 21 || !lane.Cards(ResumableDiscardProgram.Pile.Exhaust).SequenceEqual(new[] { 0 }))
                throw new InvalidOperationException("Sly return skipped parent effects or exhaust.");
            var complete = lane.Freeze();
            pendingShuffle.RestoreInto(lane); lane.SupplyChoice([1]); lane.Run(); lane.SupplyChoice([2]); lane.Run();
            if (!lane.State.Freeze().ContentEquals(complete.Open().State.Freeze()))
                throw new InvalidOperationException("Nested batch resume retained another worker's state.");
        });
        var empty = new ResumableDiscardProgram([new(0, new([new(CardInstructionKind.DiscardHandAndDraw, 0)]), ResultPile: ResumableDiscardProgram.Pile.Exhaust)],
            [[0], [], [], [], []], 0, 0, 0, comparisons: [0]);
        empty.Begin(0); empty.Run();
        if (!empty.Complete || empty.Count(ResumableDiscardProgram.Pile.Exhaust) != 1
            || Enumerable.Range(0, empty.EventCount).Select(empty.EventAt).Any(e => e.Kind is ResumableDiscardProgram.EventKind.Draw or ResumableDiscardProgram.EventKind.Discard))
            throw new InvalidOperationException("Empty hand discard should not draw or suspend.");
        Console.WriteLine("COMPACT_POISON_DISCARD_CHECKS_OK unpowered_unblockable=true provenance=true decrement_retirement=true reacquisition=true indirect_death=true frozen_workers=8 discard_draw_sly_order=true partial_draw_shuffle_resume=true redrawn_sly=true empty_hand=true rollback=true");
    }
}
