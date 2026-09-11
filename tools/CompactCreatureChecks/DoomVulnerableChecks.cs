using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class DoomVulnerableChecks
{
    internal static void Run()
    {
        // The application domain admits only enemy requests: owner targets, negative
        // amounts and X scaling stay rejected before any candidate runs.
        _ = new CardEffectProgram([new(CardInstructionKind.ApplyBasicPower, 21, BasicPowerKind.Doom, CardInstructionTarget.AllEnemies),
            new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.Vulnerable, CardInstructionTarget.ChosenEnemy)]);
        Reject([new(CardInstructionKind.ApplyBasicPower, 21, BasicPowerKind.Doom)]);
        Reject([new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.Vulnerable, CardInstructionTarget.Owner)]);
        Reject([new(CardInstructionKind.ApplyBasicPower, -21, BasicPowerKind.Doom, CardInstructionTarget.ChosenEnemy)]);
        Reject([new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.Vulnerable, CardInstructionTarget.AllEnemies, 1)]);

        // Deathbringer: one native bulk command finishes the whole roster before the next,
        // and a dead enemy is absent from both the Doom and the Weak pass.
        var bulk = Lane([new(0, new([new(CardInstructionKind.ApplyBasicPower, 21, BasicPowerKind.Doom, CardInstructionTarget.AllEnemies),
                new(CardInstructionKind.ApplyBasicPower, 1, BasicPowerKind.Weak, CardInstructionTarget.AllEnemies)])),
            new(0, new([new(CardInstructionKind.AttackTarget, 99)]))], [[0, 1], [], [], [], []],
            [new(50, 50, 0), new(10, 10, 0), new(10, 10, 0), new(10, 10, 0)]);
        bulk.Begin(1, 2); bulk.Run();
        var survivor = bulk.Freeze(); var bulkMark = bulk.State.Mark();
        bulk.Begin(0); bulk.Run();
        var changes = Enumerable.Range(0, bulk.EventCount).Select(bulk.EventAt)
            .Where(item => item.Kind == ResumableDiscardProgram.EventKind.PowerChange)
            .Select(item => (item.Target, item.Flags, item.Value)).ToArray();
        if (!changes.SequenceEqual(new[] { (1, (int)BasicPowerKind.Doom, 21), (3, (int)BasicPowerKind.Doom, 21),
                (1, (int)BasicPowerKind.Weak, 1), (3, (int)BasicPowerKind.Weak, 1) }))
            throw new InvalidOperationException("Doom and Weak did not keep their per-command roster passes.");
        var doomed = Enumerable.Range(0, bulk.EventCount).Select(bulk.EventAt)
            .Where(item => item.Kind == ResumableDiscardProgram.EventKind.DoomApplied)
            .Select(item => (Applier: item.Card, item.Value, item.Target)).ToArray();
        if (!doomed.SequenceEqual(new[] { (0, 21, 1), (0, 21, 3) }))
            throw new InvalidOperationException("Doom application history did not record the player applier per enemy.");
        if (bulk.Power(Slot(bulk, 1, BasicPowerKind.Doom)).Order >= bulk.Power(Slot(bulk, 1, BasicPowerKind.Weak)).Order)
            throw new InvalidOperationException("Doom and Weak lost their acquisition order.");
        bulk.State.Rollback(bulkMark);
        if (!bulk.State.Freeze().ContentEquals(survivor.Open().State.Freeze()))
            throw new InvalidOperationException("Doom and Weak failed to restore amounts and acquisition order.");

        // Putrefy applies both debuffs to the chosen enemy; Vulnerable multiplies incoming
        // powered damage while the attacker-side Weak factor stays separate.
        var putrefy = Lane([new(0, new([new(CardInstructionKind.ApplyBasicPower, 2, BasicPowerKind.Weak, CardInstructionTarget.ChosenEnemy),
                new(CardInstructionKind.ApplyBasicPower, 2, BasicPowerKind.Vulnerable, CardInstructionTarget.ChosenEnemy)])),
            new(0, new([new(CardInstructionKind.AttackTarget, 10)]))], [[0, 1], [], [], [], []],
            [new(50, 50, 0), new(100, 100, 0)]);
        putrefy.Begin(0, 1); putrefy.Run(); putrefy.Begin(1, 1); putrefy.Run();
        if (putrefy.Power(Slot(putrefy, 1, BasicPowerKind.Weak)).Amount != 2 || putrefy.Power(Slot(putrefy, 1, BasicPowerKind.Vulnerable)).Amount != 2
            || putrefy.Creature(1).CurrentHp != 85)
            throw new InvalidOperationException("Putrefy debuffs or the Vulnerable damage multiplier differ.");

        // Fear attacks before it applies Vulnerable, so the same hit cannot use the 1.5x.
        var fear = Lane([new(0, new([new(CardInstructionKind.AttackTarget, 10),
                new(CardInstructionKind.ApplyBasicPower, 2, BasicPowerKind.Vulnerable, CardInstructionTarget.ChosenEnemy)]))],
            [[0], [], [], [], []], [new(50, 50, 0), new(100, 100, 0)]);
        fear.Begin(0, 1); fear.Run();
        if (fear.Creature(1).CurrentHp != 90 || fear.Power(Slot(fear, 1, BasicPowerKind.Vulnerable)).Amount != 2)
            throw new InvalidOperationException("Fear used its own Vulnerable application on the same hit.");

        // NegativePulse pays its block command first, then both enemies receive Doom.
        var pulse = Lane([new(0, new([new(CardInstructionKind.GainBlock, 5),
                new(CardInstructionKind.ApplyBasicPower, 7, BasicPowerKind.Doom, CardInstructionTarget.AllEnemies)]))],
            [[0], [], [], [], []], [new(50, 50, 0), new(10, 10, 0), new(10, 10, 0)]);
        pulse.Begin(0); pulse.Run();
        if (pulse.Block != 5 || pulse.Creature(1).CurrentHp != 10 || pulse.Creature(2).CurrentHp != 10
            || pulse.Power(Slot(pulse, 1, BasicPowerKind.Doom)).Amount != 7 || pulse.Power(Slot(pulse, 2, BasicPowerKind.Doom)).Amount != 7)
            throw new InvalidOperationException("NegativePulse block or all-enemy Doom differs.");

        // Scourge applies Doom to the chosen enemy before it draws.
        var scourge = Lane([new(0, new([new(CardInstructionKind.ApplyBasicPower, 13, BasicPowerKind.Doom, CardInstructionTarget.ChosenEnemy),
                new(CardInstructionKind.Draw, 1)])), new(0, CardEffectProgram.Empty)], [[0], [1], [], [], []],
            [new(50, 50, 0), new(10, 10, 0)]);
        scourge.Begin(0, 1); scourge.Run();
        var scourgeEvents = Enumerable.Range(0, scourge.EventCount).Select(scourge.EventAt)
            .Where(item => item.Kind is ResumableDiscardProgram.EventKind.PowerChange or ResumableDiscardProgram.EventKind.Draw)
            .Select(item => item.Kind).ToArray();
        if (!scourgeEvents.SequenceEqual(new[] { ResumableDiscardProgram.EventKind.PowerChange, ResumableDiscardProgram.EventKind.Draw })
            || scourge.Count(ResumableDiscardProgram.Pile.Hand) != 1 || scourge.Power(Slot(scourge, 1, BasicPowerKind.Doom)).Amount != 13)
            throw new InvalidOperationException("Scourge applied Doom after its draw or lost the drawn card.");

        // Artifact consumes one whole debuff request, including Doom, and suppresses the
        // application history entry that feeds the turn-scoped applier set.
        var artifact = Lane([new(0, new([new(CardInstructionKind.ApplyBasicPower, 13, BasicPowerKind.Doom, CardInstructionTarget.ChosenEnemy)]))],
            [[0], [], [], [], []], [new(50, 50, 0), new(10, 10, 0)],
            extra: [new BasicPowerDefinition(BasicPowerKind.Artifact, 1, 2, -1, 1, 1m, true)]);
        artifact.Begin(0, 1); artifact.Run();
        if (artifact.Power(Slot(artifact, 1, BasicPowerKind.Doom)).Amount != 0 || artifact.Power(Slot(artifact, 1, BasicPowerKind.Artifact)).Amount != 1
            || Enumerable.Range(0, artifact.EventCount).Select(artifact.EventAt)
                .Any(item => item.Kind == ResumableDiscardProgram.EventKind.DoomApplied))
            throw new InvalidOperationException("Artifact did not consume an entire Doom request.");

        // Doom is a counter, not a duration. The player side end neither triggers an enemy
        // Doom nor decays an enemy debuff. The enemy trigger runs at the native
        // BeforeSideTurnEnd boundary and keeps the victim's block, and only the following
        // enemy side end ticks the Vulnerable duration while Doom keeps its amount.
        var phases = Lane([new(0, new([new(CardInstructionKind.ApplyBasicPower, 21, BasicPowerKind.Doom, CardInstructionTarget.AllEnemies),
                new(CardInstructionKind.ApplyBasicPower, 2, BasicPowerKind.Vulnerable, CardInstructionTarget.AllEnemies)]))],
            [[0], [], [], [], []], [new(50, 50, 0), new(20, 20, 3), new(60, 60, 0)], phases: true);
        phases.Begin(0); phases.Run();
        var applied = phases.Freeze();
        phases.EndSidePowerEffects(false);
        if (!phases.CreaturePresent(1) || phases.Power(Slot(phases, 1, BasicPowerKind.Vulnerable)).Amount != 2
            || phases.Power(Slot(phases, 1, BasicPowerKind.Doom)).Amount != 21)
            throw new InvalidOperationException("The player side end triggered or decayed an enemy debuff.");
        phases.BeforeEndSidePowerEffects(true);
        if (phases.CreaturePresent(1) || phases.Creature(1).CurrentHp != 0 || phases.Creature(1).Block != 3
            || phases.Power(Slot(phases, 1, BasicPowerKind.Doom)).Amount != 0
            || phases.Power(Slot(phases, 2, BasicPowerKind.Vulnerable)).Amount != 2 || phases.Power(Slot(phases, 2, BasicPowerKind.Doom)).Amount != 21)
            throw new InvalidOperationException("The enemy Doom phase lost its direct kill, block retention or early duration tick.");
        phases.EndSidePowerEffects(true);
        if (!phases.CreaturePresent(2) || phases.Power(Slot(phases, 2, BasicPowerKind.Vulnerable)).Amount != 1
            || phases.Power(Slot(phases, 2, BasicPowerKind.Doom)).Amount != 21)
            throw new InvalidOperationException("The enemy side end lost the Vulnerable duration tick or decayed Doom.");
        var killed = Enumerable.Range(0, phases.EventCount).Select(phases.EventAt)
            .Where(item => item.Kind is ResumableDiscardProgram.EventKind.Kill or ResumableDiscardProgram.EventKind.Death).ToArray();
        if (killed.Length != 2 || killed[0] is not { Kind: ResumableDiscardProgram.EventKind.Kill, Value: 20, Target: 1 }
            || killed[1] is not { Kind: ResumableDiscardProgram.EventKind.Death, Target: 1 }
            || Enumerable.Range(0, phases.EventCount).Select(phases.EventAt)
                .Any(item => item.Kind == ResumableDiscardProgram.EventKind.Damage))
            throw new InvalidOperationException("Doom did not use the native direct-death history.");
        var final = phases.Freeze();
        applied.RestoreInto(phases);
        if (!phases.State.Freeze().ContentEquals(applied.Open().State.Freeze()))
            throw new InvalidOperationException("Doom and Vulnerable phases failed to restore.");

        // The threshold is inclusive and direct death does not consume block.
        var threshold = Lane([new(0, new([new(CardInstructionKind.ApplyBasicPower, 10, BasicPowerKind.Doom, CardInstructionTarget.ChosenEnemy)]))],
            [[0], [], [], [], []], [new(50, 50, 0), new(10, 10, 4)], phases: true);
        threshold.Begin(0, 1); threshold.Run();
        threshold.BeforeEndSidePowerEffects(true);
        if (threshold.CreaturePresent(1) || threshold.Creature(1).Block != 4)
            throw new InvalidOperationException("The inclusive Doom threshold or its block retention differs.");
        var above = Lane([new(0, new([new(CardInstructionKind.ApplyBasicPower, 10, BasicPowerKind.Doom, CardInstructionTarget.ChosenEnemy)]))],
            [[0], [], [], [], []], [new(50, 50, 0), new(11, 11, 0)], phases: true);
        above.Begin(0, 1); above.Run();
        above.BeforeEndSidePowerEffects(true);
        if (!above.CreaturePresent(1) || above.Creature(1).CurrentHp != 11)
            throw new InvalidOperationException("Doom killed above its threshold.");

        Parallel.For(0, 8, _ =>
        {
            var worker = applied.Open();
            worker.EndSidePowerEffects(false); worker.BeforeEndSidePowerEffects(true); worker.EndSidePowerEffects(true);
            if (!worker.State.Freeze().ContentEquals(final.Open().State.Freeze()))
                throw new InvalidOperationException("Doom worker phase sequence differs.");
            applied.RestoreInto(worker);
            if (!worker.State.Freeze().ContentEquals(applied.Open().State.Freeze()))
                throw new InvalidOperationException("Doom worker retained a killed enemy or a decayed duration.");
        });
        Console.WriteLine("COMPACT_DOOM_VULNERABLE_CHECKS_OK ordered_bulk=true doom_history=true artifact_block=true "
            + "vulnerable_multiplier=true own_hit_order=true vulnerable_tick=true inclusive_threshold=true block_preserved=true "
            + "rollback=true frozen_workers=8");

        static void Reject(CardInstruction[] instructions)
        {
            try { _ = new CardEffectProgram(instructions); }
            catch (NotSupportedException) { return; }
            throw new InvalidOperationException("Doom or Vulnerable instruction was admitted outside its domain.");
        }

        static ResumableDiscardProgram Lane(ResumableDiscardProgram.Card[] cards, int[][] piles, CreatureVitals[] creatures,
            IEnumerable<BasicPowerDefinition>? extra = null, bool phases = false)
        {            // Artifact is captured separately: one admitted slot per creature.
            BasicPowerDefinition[] powers = Enumerable.Range(0, creatures.Length)
                .SelectMany(owner => Enum.GetValues<BasicPowerKind>().Where(kind => kind != BasicPowerKind.Artifact)
                    .Select(kind => new BasicPowerDefinition(kind, owner, 0, -1, 0,
                        kind is BasicPowerKind.Weak ? 0.75m : kind is BasicPowerKind.Vulnerable ? 1.5m : 1m, false)))
                .Concat(extra ?? []).ToArray();
            return new(cards, piles, 0, creatures[0].Block, 0, creatures: creatures, powers: powers, powerPhasesAdmitted: phases);
        }
        static int Slot(ResumableDiscardProgram lane, int owner, BasicPowerKind kind)
            => Enumerable.Range(0, lane.PowerCount).Single(index => lane.PowerDefinition(index).Owner == owner
                && lane.PowerDefinition(index).Kind == kind);
    }
}
