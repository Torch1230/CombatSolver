using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class HandEndChecks
{
    internal static void Run()
    {
        foreach (int hp in new[] { 10, 1 })
        foreach (int block in new[] { 0, 1, 6 })
        foreach (var staging in Enum.GetValues<ResumableDiscardProgram.HandEndStaging>())
        {
            var burn = new ResumableDiscardProgram.Card(-1, CardEffectProgram.Empty, Category: CardCategory.Status,
                HandEndDamage: 2, Unplayable: true);
            var lane = new ResumableDiscardProgram([burn, new(1, new([new(CardInstructionKind.GainBlock, 5)]), Ethereal: true), burn, burn],
                [[0, 1, 2, 3], [], [], [], []], 3, block, 0, creatures: [new(hp, 20, block), new(30, 30, 0)],
                powers: [new(BasicPowerKind.Strength, 0, 9, 0, 1, 1m, true)], handEndAdmitted: true);
            var root = lane.Freeze(); var mark = lane.State.Mark();
            bool rejected = false;
            try { lane.Begin(0); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected || !lane.State.Freeze().ContentEquals(root.Open().State.Freeze()))
                throw new InvalidOperationException("Unplayable status changed the root.");
            lane.EndHandEffects(staging);
            int lost = Math.Min(hp, Math.Max(0, 6 - block));
            bool dead = lost == hp;
            var events = Enumerable.Range(0, lane.EventCount).Select(lane.EventAt).ToArray();
            var damage = events.Where(item => item.Kind == ResumableDiscardProgram.EventKind.Damage).ToArray();
            int hits = dead ? Math.Min(3, (hp + block + 1) / 2) : 3;
            if (lane.Creature(0) != new CreatureVitals(hp - lost, 20, Math.Max(0, block - 6))
                || !lane.CreaturePresent(0) || lane.CreatureDeathCompleted(0) != dead || lane.Terminal
                || lane.Energy != 3 || lane.Power(0).Amount != (dead ? 0 : 9) || !lane.Cards(ResumableDiscardProgram.Pile.Exhaust).SequenceEqual([1])
                || damage.Length != hits || damage.Sum(item => item.Value) != lost
                || damage.Any(item => item.Target != 0 || (item.Flags & (int)ResumableDiscardProgram.DamageTraits.Unpowered) == 0)
                || events.Any(item => item.Kind is ResumableDiscardProgram.EventKind.Start or ResumableDiscardProgram.EventKind.Finish
                    or ResumableDiscardProgram.EventKind.Discard or ResumableDiscardProgram.EventKind.Pay or ResumableDiscardProgram.EventKind.AttackFinish))
                throw new InvalidOperationException("Hand-end damage, source, power cleanup or lifecycle differs.");
            int expectedPlay = !dead ? 0 : staging == ResumableDiscardProgram.HandEndStaging.Together ? 4 - hits : 1;
            if (lane.Count(ResumableDiscardProgram.Pile.Play) != expectedPlay
                || lane.Count(ResumableDiscardProgram.Pile.Hand) != (dead && staging == ResumableDiscardProgram.HandEndStaging.Sequential ? 3 - hits : 0)
                || lane.Count(ResumableDiscardProgram.Pile.Discard) != (dead ? hits - 1 : 3))
                throw new InvalidOperationException("Hand-end staging or fatal result-pile gate differs.");
            var pending = lane.Freeze();
            if (lane.CheckWinCondition() != dead || lane.DefeatTerminal != dead)
                throw new InvalidOperationException("Hand-end loss was not locked at its safe point.");
            var final = lane.Freeze(); lane.State.Rollback(mark);
            if (!lane.State.Freeze().ContentEquals(root.Open().State.Freeze()))
                throw new InvalidOperationException("Hand-end rollback retained damage or staging.");
            Parallel.For(0, 8, _ =>
            {
                var worker = root.Open(); worker.EndHandEffects(staging); worker.CheckWinCondition();
                if (!worker.State.Freeze().ContentEquals(final.Open().State.Freeze())) throw new InvalidOperationException("Hand-end worker differs.");
                pending.RestoreInto(worker);
                if (worker.Terminal || worker.CheckWinCondition() != dead) throw new InvalidOperationException("Pending loss restore differs.");
                root.RestoreInto(worker);
                if (!worker.State.Freeze().ContentEquals(root.Open().State.Freeze())) throw new InvalidOperationException("Root retained fatal hand-end state.");
            });
        }
        var actionOnly = new ResumableDiscardProgram([new(0, new([new(CardInstructionKind.GainBlock, 1)]))], [[0], [], [], [], []], 0, 0, 0);
        var actionRoot = actionOnly.Freeze();
        foreach (var worker in new[] { actionOnly, actionRoot.Open() })
        {
            bool rejected = false;
            try { worker.EndHandEffects(ResumableDiscardProgram.HandEndStaging.Together); } catch (InvalidOperationException) { rejected = true; }
            if (!rejected || !worker.State.Freeze().ContentEquals(actionRoot.Open().State.Freeze()))
                throw new InvalidOperationException("An action-only root entered an unadmitted hand-end phase.");
        }
        Console.WriteLine("COMPACT_HAND_END_CHECKS_OK cases=12 staging_orders=2 phase_admission=true unplayable=true unpowered=true ethereal_first=true player_roster_retained=true pending_loss=true frozen_workers=8");
    }
}
