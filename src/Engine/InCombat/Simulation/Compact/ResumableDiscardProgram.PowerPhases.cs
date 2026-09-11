namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal sealed partial class ResumableDiscardProgram
{
    // These are admitted phase bodies. The round driver owns side/turn/history changes,
    // participant selection, first-turn block retention and the surrounding hooks.
    internal void CapturePowerTurnStart(int owner)
    {
        AssertPowerPhase(owner);
        _powers!.CaptureTurnStart(State, owner);
    }

    internal void ClearCreatureBlock(int owner)
    {
        AssertPowerPhase(owner);
        var values = Creature(owner);
        values.Block = 0;
        _combat!.Write(State, owner, values);
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
        // Only enemy temporary Strength is admitted. Its removal has no amount callback;
        // restoring Strength uses the owner as applier and the ordinary command gate.
        for (int owner = 1; enemySide && owner < CreatureCount; owner++)
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

    private void AssertPowerPhase(int owner = -1)
    {
        if (!_powerPhasesAdmitted || !Complete || owner < -1 || owner >= CreatureCount
            || owner >= 0 && (!CreaturePresent(owner) || Creature(owner).CurrentHp <= 0))
            throw new InvalidOperationException("Power phase requires an admitted idle program and a living participant.");
    }
}
