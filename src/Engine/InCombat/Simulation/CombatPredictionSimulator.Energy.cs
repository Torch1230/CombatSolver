using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    // Mirrors PlayerCmd.GainEnergy.
    public void GainEnergy(Player player, decimal amount)
    {
        if (IsEnding || amount <= 0m)
        {
            return;
        }

        var modifiedAmount = Hook.ModifyEnergyGain(State.CombatState, player, amount, out var modifiers);
        // Mirrors PlayerCmd.GainEnergy's value hook. AfterModifyingEnergyGain is
        // intentionally not mirrored: reviewed vanilla listeners only flash UI and
        // do not mutate prediction-relevant state.
        _ = modifiers;

        if (modifiedAmount > 0m)
            State.GetPlayerCombatState(player).GainEnergy(modifiedAmount);
    }

    // Mirrors PlayerCmd.LoseEnergy.
    public void LoseEnergy(Player player, decimal amount)
    {
        if (IsEnding || amount <= 0m)
        {
            return;
        }

        State.GetPlayerCombatState(player).LoseEnergy(amount);
    }

    // Mirrors PlayerCmd.GainStars, including AfterStarsGained after the state mutation.
    public void GainStars(Player player, decimal amount)
    {
        if (IsEnding || !Hook.ShouldGainStars(State.CombatState, amount, player))
        {
            return;
        }

        State.GetPlayerCombatState(player).GainStars(amount);
        if (State.CombatState is ICombatPredictionCardEventSink eventSink)
            eventSink.RecordStarsGained(player, (int)amount);
        HookMirrors.AfterStarsGained(this, (int)amount, player);
    }
}
