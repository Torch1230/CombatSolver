using System.Collections;
using System.Reflection;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertPowerHiddenStateRegistration(CombatState combat, Player player)
    {
        SimulatedCombatState state = new(combat);
        CombatPredictionSimulator simulator = new(state);
        state.Apply<StrengthPower>(player.Creature, 1);
        StrengthPower power = state.GetPower<StrengthPower>(player.Creature)!;
        var registry = (IDictionary)typeof(PowerHiddenStateMirrors)
            .GetField("Registry", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var captures = (IDictionary)typeof(PowerHiddenStateMirrors)
            .GetField("RootCaptures", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        if (registry.Contains(typeof(StrengthPower)) || captures.Contains(typeof(StrengthPower)))
            throw new InvalidOperationException("Fixture requires unregistered StrengthPower.");
        StateFingerprintBuilder baseline = new();
        state.AppendFingerprint(ref baseline, simulator);
        long value = 4;
        bool captured = false;
        try
        {
            PowerHiddenStateMirrors.Register<StrengthPower>("z", (_, _) => value);
            PowerHiddenStateMirrors.Register<StrengthPower>("a", (_, _) => 7);
            if (PowerHiddenStateMirrors.Slots(power)[0].Name != "a")
                throw new InvalidOperationException("Hidden-state slots are not ordered.");
            bool duplicateRejected = false;
            try { PowerHiddenStateMirrors.Register<StrengthPower>("a", (_, _) => 0); }
            catch (ArgumentException) { duplicateRejected = true; }
            if (!duplicateRejected)
                throw new InvalidOperationException("Duplicate hidden-state slot was accepted.");
            PowerHiddenStateMirrors.RegisterRootCapture<StrengthPower>((sim, clone, original) =>
            {
                captured = ReferenceEquals(sim, simulator) && ReferenceEquals(clone, power)
                    && ReferenceEquals(original, power);
            });
            PowerHiddenStateMirrors.CaptureRootState(simulator, power, power);
            if (!captured) throw new InvalidOperationException("Root capture delegate was not dispatched.");
            StateFingerprintBuilder first = new();
            state.AppendFingerprint(ref first, simulator);
            value = 5;
            StateFingerprintBuilder second = new();
            state.AppendFingerprint(ref second, simulator);
            if (first.Finish() == second.Finish())
                throw new InvalidOperationException("Different hidden states share a fingerprint.");
        }
        finally
        {
            registry.Remove(typeof(StrengthPower));
            captures.Remove(typeof(StrengthPower));
        }
        StateFingerprintBuilder restored = new();
        state.AppendFingerprint(ref restored, simulator);
        if (baseline.Finish() != restored.Finish())
            throw new InvalidOperationException("Empty registration changed the native fingerprint.");
    }
}
