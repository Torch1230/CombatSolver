using CombatSolver.Engine.InCombat.Simulation.Compact;

internal static class PowerChecks
{
    internal static void Run()
    {
        var state = new ReversibleValueState(61);
        var definitions = new[]
        {
            new BasicPowerDefinition(BasicPowerKind.Vulnerable, 1, 2, 0, 1, 1.5m, true),
            new BasicPowerDefinition(BasicPowerKind.Strength, 0, 2, 0, 2, 1m, true),
            new BasicPowerDefinition(BasicPowerKind.Weak, 0, 2, 1, 3, 0.75m, true),
            new BasicPowerDefinition(BasicPowerKind.Dexterity, 0, 3, 0, 4, 1m, true),
            new BasicPowerDefinition(BasicPowerKind.Frail, 0, 2, 1, 5, 1m, true),
            new BasicPowerDefinition(BasicPowerKind.Weak, 1, 0, -1, 0, 0.75m, false)
        };
        var powers = new BasicPowerLayout(state, definitions);
        var root = state.Freeze();
        Equal(2.25m, powers.ModifyAttack(state, 0, 1, 0), "zero base damage");
        Equal(12.375m, powers.ModifyAttack(state, 0, 1, 9), "fractional damage modifiers");
        Equal(2.25m, powers.ModifyBlock(state, 0, 0), "zero base block");
        var mark = state.Mark();
        powers.Apply(state, 3, -6, 1);
        Equal(0m, powers.ModifyBlock(state, 0, 1), "negative Dexterity clamp");
        Equal(1.5m, powers.ModifyBlock(state, 0, 5), "negative Dexterity fractional block");
        powers.Apply(state, 1, -20, 1);
        Equal(0m, powers.ModifyAttack(state, 0, 1, 9), "negative Strength clamp");
        state.Rollback(mark);
        if (!root.ContentEquals(state.Freeze())) throw new InvalidOperationException("Power modifiers changed root.");
        powers.Apply(state, 5, 1, 0);
        var created = state.Freeze();
        if (powers.Read(state, 5) != new BasicPowerValues(1, 0, 6, false))
            throw new InvalidOperationException("New Power lost acquisition/applier.");
        powers.Apply(state, 5, int.MaxValue, 1);
        if (powers.Read(state, 5) != new BasicPowerValues(999_999_999, 0, 6, false))
            throw new InvalidOperationException("Stack cap overflowed or replaced original applier.");
        powers.Apply(state, 0, -2, 1);
        if (powers.Read(state, 0) != new BasicPowerValues(0, 0, 0, true))
            throw new InvalidOperationException("Root Power retirement lost.");
        powers.Apply(state, 0, 1, 1);
        if (powers.Read(state, 0) != new BasicPowerValues(1, 1, 7, true))
            throw new InvalidOperationException("Reacquired Power reused original order/applier.");
        var reacquired = state.Freeze();
        Parallel.For(0, 8, worker =>
        {
            var lane = root.CreateWorkspace();
            foreach (var retained in new[] { created, reacquired, root })
            {
                lane.Restore(retained);
                var before = lane.Mark();
                powers.RemoveOwner(lane, 1);
                if (powers.Read(lane, 0).Amount != 0 || powers.Read(lane, 5).Amount != 0)
                    throw new InvalidOperationException("Death retained owner Powers.");
                powers.Apply(lane, 1, worker + 1, 1);
                lane.Rollback(before);
                if (!lane.Freeze().ContentEquals(retained)) throw new InvalidOperationException("Power rollback leaked.");
            }
        });
        foreach (int index in new[] { 1, 3, 2 })
        {
            var before = state.Mark();
            powers.Apply(state, index, -powers.Read(state, index).Amount, 0);
            bool duration = definitions[index].Kind == BasicPowerKind.Weak;
            int amount = duration ? 2 : -2;
            powers.Apply(state, index, amount, 1);
            var value = powers.Read(state, index);
            if (value.Amount != amount || !value.Retired || value.Applier != 1 || value.SkipNextDurationTick != duration
                || !BasicPowerLayout.IsDebuff(definitions[index].Kind, amount))
                throw new InvalidOperationException("Native Power type metadata was confused with incoming amount type.");
            state.Rollback(before);
        }
        Console.WriteLine("COMPACT_POWER_CHECKS_OK modifiers=6 negative_stats=true native_type_vs_amount_type=true stack_cap=true applier=true retirement=true reacquisition=true undo=true frozen_workers=8");
    }

    private static void Equal(decimal expected, decimal actual, string label)
    {
        if (expected != actual) throw new InvalidOperationException($"Power {label}: {expected} != {actual}.");
    }
}
