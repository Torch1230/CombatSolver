namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal sealed partial class ResumableDiscardProgram
{
    // These are admitted phase bodies. The round driver owns side/turn/history changes,
    // participant selection, first-turn block retention and the surrounding hooks.
    internal void CapturePowerTurnStart(int owner)
    {
        AssertPowerPhase();
        if (owner < 0 || owner >= CreatureCount || !CreaturePresent(owner))
            throw new InvalidOperationException("Turn-start Power amounts require a captured participant.");
        _powers!.CaptureTurnStart(State, owner);
        if (owner == 0) CapturePanacheTurnStart();
    }

    internal void ClearCreatureBlock(int owner)
    {
        AssertPowerPhase(owner);
        var values = Creature(owner);
        values.Block = 0;
        _combat!.Write(State, owner, values);
        // Native clears block even during pending loss, but a new
        // AfterBlockCleared dispatch has no listeners once combat is ending.
        if (Ending) return;
        int index = _powers!.FindOrDefault(owner, BasicPowerKind.BlockNextTurn);
        if (index < 0 || Power(index).Amount == 0) return;
        int amount = Power(index).Amount;
        GainCreatureBlock(-owner - 1, owner, amount); // Unpowered: no Dexterity/Frail.
        CommitPower(-owner - 1, owner, BasicPowerKind.BlockNextTurn, -amount);
    }

    internal void EndSidePowerEffects(bool enemySide)
    {
        AssertPowerPhase();
        if (Ending) return;
        if (!enemySide)
        {
            TriggerDoom(enemySide: false);
            // The admitted player counter has no removal/amount observer. If Doom
            // killed its owner, death already cleared the same slot.
            int borrowed = _powers!.FindOrDefault(0, BasicPowerKind.BorrowedTime);
            if (borrowed >= 0 && Power(borrowed).Amount != 0)
                CommitPower(-1, 0, BasicPowerKind.BorrowedTime, -Power(borrowed).Amount);
            ResetPanacheTurn();
            return;
        }
        // Only enemy temporary Strength is admitted. Its removal has no amount callback;
        // restoring Strength uses the owner as applier and the ordinary command gate.
        for (int owner = 1; enemySide && owner < EnemyEnd; owner++)
        {
            if (!CreaturePresent(owner)) continue;
            int amount = _powers!.Amount(State, owner, BasicPowerKind.PiercingWail);
            if (amount == 0) continue;
            int source = -owner - 1;
            CommitPower(source, owner, BasicPowerKind.PiercingWail, -amount);
            if (PreparePower(source, owner, BasicPowerKind.Strength, amount))
                CommitPower(source, owner, BasicPowerKind.Strength, amount, owner);
        }
        if (!enemySide) return;
        // No admitted callback observes relative decrements. Duration changes bypass
        // Power application modifiers (including Artifact), as native Decrement does.
        for (int index = 0; index < PowerCount; index++)
        {
            var definition = PowerDefinition(index);
            var value = Power(index);
            if (!BasicPowerLayout.IsDuration(definition.Kind) || value.Amount == 0 || !CreaturePresent(definition.Owner)) continue;
            if (value.SkipNextDurationTick) _powers!.ClearDurationSkip(State, index);
            else CommitPower(-definition.Owner - 1, definition.Owner, definition.Kind, -1);
        }
    }

    internal void BeforeEndSidePowerEffects(bool enemySide)
    {
        AssertPowerPhase();
        if (enemySide) TriggerDoom(enemySide: true);
    }

    private void TriggerDoom(bool enemySide)
    {
        if (Ending) return;
        // Primary enemies have no resurrection/Fatal observers. The retained pet is
        // killed with its owner; native Doom has no damage or attack history.
        for (int owner = enemySide ? 1 : 0; owner < (enemySide ? EnemyEnd : 1); owner++)
        {
            int index = _powers!.FindOrDefault(owner, BasicPowerKind.Doom);
            if (index < 0 || !CreaturePresent(owner) || Creature(owner).CurrentHp <= 0
                || Creature(owner).CurrentHp > Power(index).Amount) continue;
            KillCreature(-owner - 1, owner);
        }
    }

    private void AssertPowerPhase(int owner = -1)
    {
        if (!_powerPhasesAdmitted || !Complete || owner < -1 || owner >= CreatureCount
            || owner >= 0 && (!CreaturePresent(owner) || Creature(owner).CurrentHp <= 0))
            throw new InvalidOperationException("Power phase requires an admitted idle program and a living participant.");
    }
}
