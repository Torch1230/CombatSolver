using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static string AssertHpModifierCollections(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        CombatPredictionSimulator parent = root.ForkSimulator();
        string Stamp(CombatPredictionSimulator simulator) => ContinuationStamp.CapturePredicted(
            player, simulator, 1, root.Forecast, root.StartTurnNumber).StateText;
        string parentBefore = Stamp(parent);
        int comparisons = 0, empty = 0, nonempty = 0;
        foreach (int bufferCount in new[] { 0, 1, 2 })
        foreach (int intangibleCount in new[] { 0, 1 })
        foreach (decimal amount in new[] { 0m, 0.4m, 1.9m, 9.2m })
        foreach (bool excludeBuffer in new[] { false, true })
        foreach (HpLossHookPhase phase in new[]
        {
            (HpLossHookPhase)0, HpLossHookPhase.BeforeOsty, HpLossHookPhase.AfterOsty,
            HpLossHookPhase.BeforeOsty | HpLossHookPhase.AfterOsty,
        })
        {
            CombatPredictionSimulator baseline = parent.Fork(), candidate = parent.Fork();
            foreach (CombatPredictionSimulator simulator in new[] { baseline, candidate })
            {
                var state = (SimulatedCombatState)simulator.State.CombatState;
                state.SetAmount<BufferPower>(player.Creature, bufferCount);
                state.SetAmount<IntangiblePower>(player.Creature, intangibleCount);
            }
            Func<AbstractModel, bool>? filter = excludeBuffer ? static m => m is not BufferPower : null;
            decimal before = BaselineHpModifiers(baseline, player.Creature, amount, phase, filter,
                out List<AbstractModel> beforeModifiers);
            decimal after = HookMirrors.ModifyHpLost(candidate, player.Creature, amount, ValueProp.Move,
                combat.Enemies[0], null, phase, out var afterModifiers, filter);
            if (before != after || !beforeModifiers.Select(m => m.GetType())
                    .SequenceEqual(afterModifiers.Select(m => m.GetType())))
                throw new InvalidOperationException("HP modifier collection changed decimal result or listener order.");
            if (afterModifiers.Count == 0) empty++; else nonempty++;
            if (Stamp(baseline) != Stamp(candidate))
                throw new InvalidOperationException("HP modifier calculation changed complete state or RNG.");
            if ((phase & HpLossHookPhase.AfterOsty) != 0)
            {
                // Duplicate entries remain a membership test: notify a current listener once.
                BaselineAfterHpModifiers(baseline, beforeModifiers.Concat(beforeModifiers).ToArray());
                HookMirrors.AfterModifyingHpLostAfterOsty(candidate,
                    afterModifiers.Concat(afterModifiers).ToArray());
                if (Stamp(baseline) != Stamp(candidate))
                    throw new InvalidOperationException("HP modifier notification changed full state or consumed twice.");
            }
            comparisons++;
        }
        var retired = parent.Fork();
        var retiredState = (SimulatedCombatState)retired.State.CombatState;
        retiredState.SetAmount<BufferPower>(player.Creature, 2);
        BufferPower oldBuffer = retiredState.GetPower<BufferPower>(player.Creature)!;
        retiredState.SetAmount<BufferPower>(player.Creature, 0);
        retiredState.SetAmount<BufferPower>(player.Creature, 3);
        string beforeRetired = Stamp(retired);
        HookMirrors.AfterModifyingHpLostAfterOsty(retired, new AbstractModel[] { oldBuffer });
        HookMirrors.AfterModifyingHpLostAfterOsty(retired, Array.Empty<AbstractModel>());
        if (Stamp(retired) != beforeRetired || Stamp(parent) != parentBefore || empty == 0 || nonempty == 0)
            throw new InvalidOperationException("HP modifier contract failed retired/empty membership or parent isolation.");
        return $"HpModifierCollections:comparisons={comparisons}:empty={empty}:nonempty={nonempty}:decimal_truncation=true:filters=true:duplicate_membership=true:retired_identity=true:full_state_rng=true:parent_unchanged=true";
    }

    private static string AssertProjectedTailLookup(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        var snapshot = driver.ReplayDiagnosticPrefix([]);
        try
        {
            var simulator = (CombatPredictionSimulator)snapshot.Simulator;
            var state = (SimulatedCombatState)simulator.State.CombatState;
            LizardTail tail = state.RelicsOf(player).OfType<LizardTail>().Single();
            SimCreatureState creature = simulator.State.GetCreature(player.Creature);
            if (LizardTailMirrors.WasUsed(tail, simulator))
                throw new InvalidOperationException("Tail lookup fixture must start with an unused tail.");
            string before = driver.CaptureDiagnosticContinuation(snapshot).StateText;
            int expectedRevive = (int)LizardTailMirrors.HealAmount(tail, creature.MaxHp);
            if (driver.ProjectDiagnosticHits(snapshot, combat.Enemies[0], 1000) != expectedRevive
                || driver.CaptureDiagnosticContinuation(snapshot).StateText != before)
                throw new InvalidOperationException("Unused tail forecast differs or mutates its branch.");
            simulator.StateStore.Get(tail, () => new LizardTailPredictionState(tail)).WasUsed = true;
            before = driver.CaptureDiagnosticContinuation(snapshot).StateText;
            int expectedLoss = creature.CurrentHp - Math.Max(0, 1000 - creature.Block);
            if (driver.ProjectDiagnosticHits(snapshot, combat.Enemies[0], 1000) != expectedLoss
                || driver.CaptureDiagnosticContinuation(snapshot).StateText != before)
                throw new InvalidOperationException("Consumed tail was reused by the forecast or changed state.");
            return "ProjectedTailLookup:UnusedRevive:ConsumedBypass:CompleteStateUnchanged";
        }
        finally { snapshot.ReleaseSimulator(); }
    }

    // Frozen eager-list and four-phase pipeline from bcc15da. The fixture uses native
    // models: default callbacks are identity operations, so enumerating the full source
    // supplies an independent reference for the production mask-filtered iteration.
    private static decimal BaselineHpModifiers(CombatPredictionSimulator simulator, Creature target,
        decimal amount, HpLossHookPhase phases, Func<AbstractModel, bool>? filter,
        out List<AbstractModel> modifiers)
    {
        var context = new ModifyHpLostMirrorContext
        {
            Simulator = simulator, Target = target, Amount = amount, Props = ValueProp.Move,
            Dealer = simulator.State.Enemies[0], CardSource = null,
        };
        List<AbstractModel> changed = [];
        if ((phases & HpLossHookPhase.BeforeOsty) != 0)
        {
            Visit(ModifyHpLostMirrors.InvokeBeforeOsty);
            Visit(ModifyHpLostMirrors.InvokeBeforeOstyLate);
        }
        if ((phases & HpLossHookPhase.AfterOsty) != 0)
        {
            Visit(ModifyHpLostMirrors.InvokeAfterOsty);
            Visit(ModifyHpLostMirrors.InvokeAfterOstyLate);
        }
        modifiers = changed;
        return context.Amount;

        void Visit(Func<AbstractModel, ModifyHpLostMirrorContext, decimal> callback)
        {
            var source = (ICombatPredictionHookListenerSource)simulator.State.CombatState;
            foreach (AbstractModel listener in source.MirroredRunHookListeners)
            {
                if (simulator.HasPendingChoice) break;
                if (filter is not null && !filter(listener)) continue;
                decimal previous = context.Amount;
                context.Amount = callback(listener, context);
                if (decimal.Truncate(previous) != decimal.Truncate(context.Amount)) changed.Add(listener);
            }
        }
    }

    private static void BaselineAfterHpModifiers(CombatPredictionSimulator simulator,
        IReadOnlyList<AbstractModel> modifiers)
    {
        var context = new AfterModifyingHpLostMirrorContext { Simulator = simulator };
        var source = (ICombatPredictionHookListenerSource)simulator.State.CombatState;
        foreach (AbstractModel listener in source.MirroredRunHookListeners)
        {
            if (simulator.HasPendingChoice) break;
            if (modifiers.Contains(listener)) AfterModifyingHpLostAfterOstyMirrors.Invoke(listener, context);
        }
    }
}
