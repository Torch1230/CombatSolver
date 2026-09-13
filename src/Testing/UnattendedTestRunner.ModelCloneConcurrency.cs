using System.Reflection;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static void AssertModelCloneConcurrency(CombatState combat)
    {
        string liveBefore = ContinuationStamp.CaptureLive(combat).StateText;
        CardModel source = ModelDb.Card<StrikeRegent>().ToMutable();
        _ = source.DynamicVars;
        if (NativeModelCloneConcurrency.CanCloneIndependently(source))
            throw new InvalidOperationException("Non-isolated clone bypassed the framework gate.");
        using (SimulationNotificationIsolation.Enter())
        {
            if (!NativeModelCloneConcurrency.CanCloneIndependently(source))
                throw new InvalidOperationException("Fixture did not reach eligible native card cloning.");
            source._dynamicVars!._vars.Add("ConcurrencyFixture", new CloneConcurrencyVariable());
            try
            {
                if (NativeModelCloneConcurrency.CanCloneIndependently(source))
                    throw new InvalidOperationException("Third-party dynamic variable bypassed the gate.");
            }
            finally { source._dynamicVars._vars.Remove("ConcurrencyFixture"); }
        }

        // Holding the real framework gate must not prevent two independent native
        // simulation clones. Each worker has its own scope and output collection.
        bool entered = BaseLibCloneConcurrency.Enter();
        if (!entered)
            throw new InvalidOperationException("BaseLib is required for the clone lock contract.");
        using CountdownEvent done = new(2);
        List<CardModel>[] clones = [[], []];
        Exception?[] errors = new Exception?[2];
        Thread[] workers = Enumerable.Range(0, 2).Select(index => new Thread(() =>
        {
            try
            {
                using IDisposable isolation = SimulationNotificationIsolation.Enter();
                for (int i = 0; i < 32; i++)
                    clones[index].Add(PredictionUtils.CloneModelForSimulation(source));
            }
            catch (Exception error) { errors[index] = error; }
            finally { done.Signal(); }
        }) { IsBackground = true, Name = $"Clone contract {index}" }).ToArray();
        bool completedWhileHeld = false;
        try
        {
            foreach (Thread worker in workers) worker.Start();
            completedWhileHeld = done.Wait(TimeSpan.FromSeconds(3));
        }
        finally
        {
            BaseLibCloneConcurrency.Exit(entered);
            foreach (Thread worker in workers)
            {
                if (!worker.Join(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("Clone contract worker did not drain.");
            }
        }
        if (errors.FirstOrDefault(error => error != null) is { } failure)
            throw new InvalidOperationException("Concurrent native clone failed.", failure);
        if (!completedWhileHeld)
            throw new InvalidOperationException("Independent native clones waited for the global framework gate.");
        HashSet<object> identities = new(ReferenceEqualityComparer.Instance);
        foreach (CardModel clone in clones.SelectMany(group => group))
        {
            if (ReferenceEquals(clone, source) || !identities.Add(clone)
                || clone.Id != source.Id || !clone.IsMutable)
                throw new InvalidOperationException("Concurrent clone identity changed.");
            foreach (var (key, value) in source.DynamicVars._vars)
            {
                DynamicVar copy = clone.DynamicVars[key];
                if (ReferenceEquals(copy, value) || !identities.Add(copy)
                    || copy.BaseValue != value.BaseValue
                    || copy.EnchantedValue != value.EnchantedValue
                    || copy.PreviewValue != value.PreviewValue)
                    throw new InvalidOperationException("Concurrent clone shared or changed variable state.");
                copy.BaseValue++;
            }
        }

        // Evidence must be refreshed after a scope ends, including on this same thread.
        MethodInfo prefix = AccessTools.Method(typeof(UnattendedTestRunner), nameof(CloneConcurrencyStagePrefix));
        Harmony harmony = new("CombatSolver.Tests.ModelCloneConcurrency");
        foreach (MethodInfo stage in new[] {
            AccessTools.Method(typeof(CardModel), "DeepCloneFields"),
            AccessTools.Method(typeof(DynamicVar), "Clone") })
        {
            try
            {
                harmony.Patch(stage, prefix: new HarmonyMethod(prefix));
                using IDisposable isolation = SimulationNotificationIsolation.Enter();
                if (NativeModelCloneConcurrency.CanCloneIndependently(source))
                    throw new InvalidOperationException("A newly patched clone stage retained stale parallel evidence.");
            }
            finally { harmony.Unpatch(stage, prefix); }
            using (SimulationNotificationIsolation.Enter())
            {
                if (!NativeModelCloneConcurrency.CanCloneIndependently(source))
                    throw new InvalidOperationException("Unpatched native clone evidence was not refreshed.");
            }
        }
        if (ContinuationStamp.CaptureLive(combat).StateText != liveBefore)
            throw new InvalidOperationException("Clone concurrency contract changed live combat.");
    }

    private static void CloneConcurrencyStagePrefix() { }
    private sealed class CloneConcurrencyVariable() : DynamicVar("ConcurrencyFixture", 1m);
}
