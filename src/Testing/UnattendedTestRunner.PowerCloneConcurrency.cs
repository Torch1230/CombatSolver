using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertPowerCloneConcurrency(CombatState combat)
    {
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        PowerModel source = (PowerModel)ModelDb.Power<WeakPower>().MutableClone();
        DynamicVarSet variables = source.DynamicVars;
        PowerModel emptyVariables = ModelDb.Power<StrengthPower>();
        PowerModel customInitialization = ModelDb.Power<VigorPower>();
        _ = emptyVariables.DynamicVars;
        _ = customInitialization.DynamicVars;
        source._owner = combat.Creatures.First();
        source._applier = source._owner;
        source._target = source._owner;
        source._amount = 3;
        source.AmountOnTurnStart = 2;
        int sourceEvents = 0;
        source.Removed += () => sourceEvents++;
        FieldInfo removedEvent = typeof(PowerModel).GetField("Removed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object internalState = new();
        source._internalData = internalState;
        Type extensions = AccessTools.TypeByName("BaseLib.Extensions.DynamicVarExtensions")
            ?? throw new InvalidOperationException("The Power clone contract requires real BaseLib.");
        object field = extensions.GetField("DynamicVarUpgrades")!.GetValue(null)!;
        var upgrades = (ConditionalWeakTable<DynamicVar, object?>)field.GetType()
            .GetField("_table", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(field)!;
        DynamicVar originalVariable = variables["DamageDecrease"];
        upgrades.AddOrUpdate(originalVariable, 3.5m);
        PowerModel native = (PowerModel)source.MutableClone();
        if (NativeModelCloneConcurrency.CanCloneIndependently(source))
            throw new InvalidOperationException("Non-isolated Power clone bypassed the framework gate.");
        using (SimulationNotificationIsolation.Enter())
        {
            if (!NativeModelCloneConcurrency.CanCloneIndependently(source)
                || !NativeModelCloneConcurrency.CanCloneIndependently(emptyVariables))
                throw new InvalidOperationException("Native Power with default initialization was not eligible.");
            if (NativeModelCloneConcurrency.CanCloneIndependently(customInitialization))
                throw new InvalidOperationException("Power with custom internal initialization bypassed the gate.");
            source._dynamicVars = null;
            try
            {
                if (NativeModelCloneConcurrency.CanCloneIndependently(source) || source._dynamicVars != null)
                    throw new InvalidOperationException("Power eligibility materialized missing source variables.");
            }
            finally { source._dynamicVars = variables; }
            variables._vars.Add("ConcurrencyFixture", new CloneConcurrencyVariable());
            try
            {
                if (NativeModelCloneConcurrency.CanCloneIndependently(source))
                    throw new InvalidOperationException("Third-party Power variable bypassed the gate.");
            }
            finally { variables._vars.Remove("ConcurrencyFixture"); }
        }

        List<PowerModel>[] clones = [[], []];
        Exception?[] errors = new Exception?[2];
        using CountdownEvent done = new(2);
        Thread[] workers = Enumerable.Range(0, 2).Select(index => new Thread(() =>
        {
            try
            {
                using IDisposable isolation = SimulationNotificationIsolation.Enter();
                for (int count = 0; count < 32; count++)
                    clones[index].Add(PredictionUtils.CloneModelForSimulation(source));
            }
            catch (Exception error) { errors[index] = error; }
            finally { done.Signal(); }
        }) { IsBackground = true, Name = $"Power clone contract {index}" }).ToArray();
        bool entered = BaseLibCloneConcurrency.Enter();
        if (!entered) throw new InvalidOperationException("Real BaseLib clone lock was not present.");
        bool completedWhileHeld;
        try
        {
            foreach (Thread worker in workers) worker.Start();
            completedWhileHeld = done.Wait(TimeSpan.FromSeconds(3));
        }
        finally
        {
            BaseLibCloneConcurrency.Exit(entered);
            foreach (Thread worker in workers)
                if (!worker.Join(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("Power clone worker did not drain.");
        }
        if (errors.FirstOrDefault(error => error != null) is { } failure)
            throw new InvalidOperationException("Concurrent Power clone failed.", failure);
        if (!completedWhileHeld)
            throw new InvalidOperationException("Eligible Power clones waited for the framework lock.");
        HashSet<object> identities = new(ReferenceEqualityComparer.Instance);
        foreach (PowerModel clone in clones.SelectMany(group => group))
        {
            DynamicVar copy = clone.DynamicVars["DamageDecrease"];
            DynamicVar nativeVariable = native.DynamicVars["DamageDecrease"];
            if (!identities.Add(clone) || !identities.Add(copy)
                || ReferenceEquals(copy, originalVariable) || !ReferenceEquals(copy._owner, clone)
                || clone.Id != native.Id || !clone.IsMutable || clone._owner != native._owner
                || clone._applier != native._applier || clone._target != native._target
                || clone.Amount != native.Amount || clone.AmountOnTurnStart != native.AmountOnTurnStart
                || clone._internalData != null || native._internalData != null
                || removedEvent.GetValue(clone) != null || removedEvent.GetValue(native) != null
                || copy.BaseValue != nativeVariable.BaseValue
                || copy.EnchantedValue != nativeVariable.EnchantedValue
                || copy.PreviewValue != nativeVariable.PreviewValue
                || !upgrades.TryGetValue(copy, out object? upgrade) || !Equals(upgrade, 3.5m))
                throw new InvalidOperationException("Concurrent Power clone differs from native clone or shares state.");
            copy.BaseValue++;
            upgrades.AddOrUpdate(copy, 9m);
        }
        if (sourceEvents != 0 || removedEvent.GetValue(source) == null
            || !ReferenceEquals(source._internalData, internalState) || source._owner == null
            || !upgrades.TryGetValue(originalVariable, out object? sourceUpgrade) || !Equals(sourceUpgrade, 3.5m))
            throw new InvalidOperationException("Power clone changed source ownership or metadata.");

        MethodInfo prefix = AccessTools.Method(typeof(UnattendedTestRunner), nameof(CloneConcurrencyStagePrefix));
        Harmony harmony = new("CombatSolver.Tests.PowerCloneConcurrency");
        foreach (MethodInfo stage in new[] {
            AccessTools.Method(typeof(PowerModel), "DeepCloneFields"),
            AccessTools.Method(typeof(PowerModel), "AfterCloned"),
            AccessTools.Method(typeof(PowerModel), "InitInternalData"),
            AccessTools.Method(typeof(AbstractModel), "DeepCloneFields"),
            AccessTools.PropertyGetter(typeof(PowerModel), nameof(PowerModel.DynamicVars)),
            AccessTools.Method(typeof(DynamicVar), "Clone") })
        {
            try
            {
                harmony.Patch(stage, prefix: new HarmonyMethod(prefix));
                using IDisposable isolation = SimulationNotificationIsolation.Enter();
                if (NativeModelCloneConcurrency.CanCloneIndependently(source))
                    throw new InvalidOperationException("Power clone retained evidence after a stage was patched.");
            }
            finally { harmony.Unpatch(stage, prefix); }
            using (SimulationNotificationIsolation.Enter())
                if (!NativeModelCloneConcurrency.CanCloneIndependently(source))
                    throw new InvalidOperationException("Power clone evidence was not refreshed after unpatching.");
        }
        if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new InvalidOperationException("Power clone contract changed live combat.");
    }
}
