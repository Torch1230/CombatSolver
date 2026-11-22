using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
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

            int unblockedHit = 0;
            for (int hit = 1; hit <= 1000; hit++)
            {
                int projectedHp = driver.ProjectDiagnosticHits(snapshot, combat.Enemies[0], hit);
                if (projectedHp > 0 && projectedHp < creature.CurrentHp)
                {
                    unblockedHit = hit;
                    break;
                }
            }
            if (unblockedHit == 0)
                throw new InvalidOperationException("Gambit forecast fixture has no nonlethal unblocked hit.");
            state.SetAmount<TheGambitPower>(player.Creature, 1);
            before = driver.CaptureDiagnosticContinuation(snapshot).StateText;
            var gambitThreat = driver.ProjectDiagnosticThreat(snapshot, combat.Enemies[0], unblockedHit);
            if (gambitThreat.Hp != expectedRevive
                || gambitThreat.DeathSaveUseCount != 1
                || gambitThreat.DeathSaveHpRestored != expectedRevive
                || driver.CaptureDiagnosticContinuation(snapshot).StateText != before)
            {
                throw new InvalidOperationException(
                    "Gambit unblocked-damage forecast did not consume exactly one death save without mutating state.");
            }
            simulator.GainBlock(player.Creature, 1000, ValueProp.Unpowered);
            before = driver.CaptureDiagnosticContinuation(snapshot).StateText;
            var blockedGambitThreat = driver.ProjectDiagnosticThreat(snapshot, combat.Enemies[0], unblockedHit);
            if (blockedGambitThreat.Hp != creature.CurrentHp
                || blockedGambitThreat.DeathSaveUseCount != 0
                || driver.CaptureDiagnosticContinuation(snapshot).StateText != before)
            {
                throw new InvalidOperationException(
                    "Gambit forecast triggered through full block or mutated the branch.");
            }
            simulator.StateStore.Get(tail, () => new LizardTailPredictionState(tail)).WasUsed = true;
            before = driver.CaptureDiagnosticContinuation(snapshot).StateText;
            int expectedLoss = creature.CurrentHp - Math.Max(0, 1000 - creature.Block);
            if (driver.ProjectDiagnosticHits(snapshot, combat.Enemies[0], 1000) != expectedLoss
                || driver.CaptureDiagnosticContinuation(snapshot).StateText != before)
                throw new InvalidOperationException("Consumed tail was reused by the forecast or changed state.");
            return "ProjectedTailLookup:UnusedRevive:GambitUnblockedDeath:GambitBlockedSafe:ConsumedBypass:CompleteStateUnchanged";
        }
        finally { snapshot.ReleaseSimulator(); }
    }

    private static string AssertProjectedMultiHitModifiers(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null));
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        Creature enemy = combat.Enemies[0];

        SimulationSnapshot slipperySingle = driver.ReplayDiagnosticPrefix([]);
        try
        {
            var simulator = (CombatPredictionSimulator)slipperySingle.Simulator;
            var state = (SimulatedCombatState)simulator.State.CombatState;
            LizardTail tail = state.RelicsOf(player).OfType<LizardTail>().Single();
            simulator.StateStore.Get(tail, () => new LizardTailPredictionState(tail)).WasUsed = true;
            SimCreatureState creature = simulator.State.GetCreature(player.Creature);
            creature.SetMaxHp(25);
            creature.CurrentHp = 5;
            state.SetAmount<SlipperyPower>(player.Creature, 1);
            SlipperyPower slippery = state.GetPower<SlipperyPower>(player.Creature)!;
            if (simulator.StateStore.TryGetReadOnly<PowerAmountPredictionState>(slippery, out _)
                || simulator.StateStore.HasEntries<PowerAmountPredictionState>())
            {
                throw new InvalidOperationException("Slippery 预测基线不应预先插入 PowerAmountPredictionState。");
            }
            string before = driver.CaptureDiagnosticContinuation(slipperySingle).StateText;
            var result = driver.ProjectDiagnosticThreat(slipperySingle, enemy, 8, 8);
            if (result.Hp != -4
                || driver.CaptureDiagnosticContinuation(slipperySingle).StateText != before
                || simulator.StateStore.TryGetReadOnly<PowerAmountPredictionState>(slippery, out _)
                || simulator.StateStore.HasEntries<PowerAmountPredictionState>())
            {
                throw new InvalidOperationException(
                    $"Slippery 1层面对[8,8]应受9点伤害(剩余-4)且不改状态与状态库，实际 Hp={result.Hp}。");
            }
        }
        finally { slipperySingle.ReleaseSimulator(); }

        SimulationSnapshot slipperyOneDamage = driver.ReplayDiagnosticPrefix([]);
        try
        {
            var simulator = (CombatPredictionSimulator)slipperyOneDamage.Simulator;
            var state = (SimulatedCombatState)simulator.State.CombatState;
            LizardTail tail = state.RelicsOf(player).OfType<LizardTail>().Single();
            simulator.StateStore.Get(tail, () => new LizardTailPredictionState(tail)).WasUsed = true;
            SimCreatureState creature = simulator.State.GetCreature(player.Creature);
            creature.SetMaxHp(25);
            creature.CurrentHp = 5;
            state.SetAmount<SlipperyPower>(player.Creature, 2);
            SlipperyPower slippery = state.GetPower<SlipperyPower>(player.Creature)!;
            string before = driver.CaptureDiagnosticContinuation(slipperyOneDamage).StateText;
            var result = driver.ProjectDiagnosticThreat(slipperyOneDamage, enemy, 1, 8, 8);
            if (result.Hp != -5
                || driver.CaptureDiagnosticContinuation(slipperyOneDamage).StateText != before
                || simulator.StateStore.TryGetReadOnly<PowerAmountPredictionState>(slippery, out _)
                || simulator.StateStore.HasEntries<PowerAmountPredictionState>())
            {
                throw new InvalidOperationException(
                    $"Slippery 2层面对[1,8,8]应在第1段1伤也消耗层数(剩余-5)且不写入状态库，实际 Hp={result.Hp}。");
            }
        }
        finally { slipperyOneDamage.ReleaseSimulator(); }

        SimulationSnapshot bufferThenSlippery = driver.ReplayDiagnosticPrefix([]);
        try
        {
            var simulator = (CombatPredictionSimulator)bufferThenSlippery.Simulator;
            var state = (SimulatedCombatState)simulator.State.CombatState;
            LizardTail tail = state.RelicsOf(player).OfType<LizardTail>().Single();
            simulator.StateStore.Get(tail, () => new LizardTailPredictionState(tail)).WasUsed = true;
            SimCreatureState creature = simulator.State.GetCreature(player.Creature);
            creature.SetMaxHp(25);
            creature.CurrentHp = 5;
            state.SetAmount<BufferPower>(player.Creature, 1);
            state.SetAmount<SlipperyPower>(player.Creature, 1);
            SlipperyPower slippery = state.GetPower<SlipperyPower>(player.Creature)!;
            string before = driver.CaptureDiagnosticContinuation(bufferThenSlippery).StateText;
            var result = driver.ProjectDiagnosticThreat(bufferThenSlippery, enemy, 8, 8);
            if (result.Hp != 4
                || driver.CaptureDiagnosticContinuation(bufferThenSlippery).StateText != before
                || simulator.StateStore.TryGetReadOnly<PowerAmountPredictionState>(slippery, out _)
                || simulator.StateStore.HasEntries<PowerAmountPredictionState>())
            {
                throw new InvalidOperationException(
                    $"Buffer+Slippery 面对[8,8]首段应仅耗Buffer、次段耗Slippery留4HP且不写入状态库，实际 Hp={result.Hp}。");
            }
        }
        finally { bufferThenSlippery.ReleaseSimulator(); }

        SimulationSnapshot remnantCap = driver.ReplayDiagnosticPrefix([]);
        try
        {
            var simulator = (CombatPredictionSimulator)remnantCap.Simulator;
            var state = (SimulatedCombatState)simulator.State.CombatState;
            BeatingRemnant remnant = (BeatingRemnant)PredictionUtils.CreateRelic(
                CanonicalModels.Relic<BeatingRemnant>(),
                player);
            ReplaceRootRelicsForTurnBoundaryTest(state, player, remnant);
            SimCreatureState creature = simulator.State.GetCreature(player.Creature);
            creature.SetMaxHp(30);
            creature.CurrentHp = 25;
            string before = driver.CaptureDiagnosticContinuation(remnantCap).StateText;
            var result = driver.ProjectDiagnosticThreat(remnantCap, enemy, 15, 15);
            if (result.Hp != 5
                || driver.CaptureDiagnosticContinuation(remnantCap).StateText != before
                || simulator.StateStore.TryGetReadOnly<BeatingRemnantPredictionState>(remnant, out _)
                || simulator.StateStore.HasEntries<BeatingRemnantPredictionState>())
            {
                throw new InvalidOperationException(
                    $"BeatingRemnant 20伤上限面对[15,15]应总损20剩5HP且不写入状态库，实际 Hp={result.Hp}。");
            }
        }
        finally { remnantCap.ReleaseSimulator(); }

        SimulationSnapshot remnantRevive = driver.ReplayDiagnosticPrefix([]);
        try
        {
            var simulator = (CombatPredictionSimulator)remnantRevive.Simulator;
            var state = (SimulatedCombatState)simulator.State.CombatState;
            LizardTail tail = state.RelicsOf(player).OfType<LizardTail>().Single();
            BeatingRemnant remnant = (BeatingRemnant)PredictionUtils.CreateRelic(
                CanonicalModels.Relic<BeatingRemnant>(),
                player);
            ReplaceRootRelicsForTurnBoundaryTest(state, player, tail, remnant);
            SimCreatureState creature = simulator.State.GetCreature(player.Creature);
            creature.SetMaxHp(40);
            creature.CurrentHp = 10;
            int expectedRevive = (int)LizardTailMirrors.HealAmount(tail, creature.MaxHp);
            int expectedCap = remnant.DynamicVars[BeatingRemnant._maxHpLossKey].IntValue;
            int expectedSecondHit = Math.Min(15, expectedCap - creature.CurrentHp);
            string before = driver.CaptureDiagnosticContinuation(remnantRevive).StateText;
            var result = driver.ProjectDiagnosticThreat(remnantRevive, enemy, 18, 15);
            if (result.Hp != expectedRevive - expectedSecondHit
                || result.DeathSaveUseCount != 1
                || result.DeathSaveHpRestored != expectedRevive
                || driver.CaptureDiagnosticContinuation(remnantRevive).StateText != before
                || simulator.StateStore.TryGetReadOnly<BeatingRemnantPredictionState>(remnant, out _)
                || simulator.StateStore.HasEntries<BeatingRemnantPredictionState>()
                || simulator.StateStore.TryGetReadOnly<LizardTailPredictionState>(tail, out _))
            {
                throw new InvalidOperationException(
                    $"BeatingRemnant 复活溢出面对[18,15]应保留10点剩余上限留{expectedRevive - expectedSecondHit}HP且不写入状态库，实际 Hp={result.Hp} saves={result.DeathSaveUseCount} restored={result.DeathSaveHpRestored}。");
            }
        }
        finally { remnantRevive.ReleaseSimulator(); }

        return "ProjectedMultiHitModifiers:SlipperyTwoHits:SlipperyOneDamageHit:BufferBeforeSlippery:BeatingRemnantCap:BeatingRemnantReviveOverkillCap:StoreUnmodified:CompleteStateUnchanged";
    }

    private static string AssertProjectedSandpitBoundary(CombatState combat, Player player)
    {
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        var driver = new CombatBeamSolver(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat),
            SolverController.CaptureSearchPolicy(SolverSettings.Capture(),combat,false,null));
        using IDisposable isolation = SimulationNotificationIsolation.Enter();
        foreach ((int amount,bool playerTarget,bool ownerAlive) in new[]
                 {(1,true,true),(2,true,true),(0,true,true),(1,false,true),(1,true,false)})
        {
            SimulationSnapshot snapshot = driver.ReplayDiagnosticPrefix([]);
            try
            {
                var simulator = (CombatPredictionSimulator)snapshot.Simulator;
                var state = (SimulatedCombatState)simulator.State.CombatState;
                Creature owner = combat.Enemies[0];
                simulator.GainBlock(player.Creature,1000,ValueProp.Unpowered);
                state.ApplyTargeted<SandpitPower>(owner,playerTarget?player.Creature:owner,amount,owner);
                if(!ownerAlive) simulator.State.GetCreature(owner).CurrentHp=0;
                string before = driver.CaptureDiagnosticContinuation(snapshot).StateText;
                var threat = driver.ProjectDiagnosticEnemyTurn(snapshot);
                bool expires = amount==1 && playerTarget && ownerAlive;
                int expected = expires?0:simulator.State.GetCreature(player.Creature).CurrentHp;
                if(threat.Hp!=expected || threat.DeathSaveUseCount!=0 || threat.DeathSaveHpRestored!=0
                    || driver.CaptureDiagnosticContinuation(snapshot).StateText!=before)
                    throw new InvalidOperationException("Sandpit threat changed state or ignored its exact target/deadline/owner.");
                var fork = simulator.Fork();
                var forkState = (SimulatedCombatState)fork.State.CombatState;
                if(!TurnStartPowerSupport.TriggerAfterSideTurnStart(fork,forkState,CombatSide.Enemy,[owner]))
                    throw new InvalidOperationException("Sandpit boundary unexpectedly requested a choice.");
                if((fork.State.GetCreature(player.Creature).CurrentHp==0)!=expires
                    || LizardTailMirrors.WasUsed(forkState.RelicsOf(player).OfType<LizardTail>().Single(),fork)
                    || driver.CaptureDiagnosticContinuation(snapshot).StateText!=before)
                    throw new InvalidOperationException("Sandpit forced death did not match projection or leaked through Fork.");
            }
            finally {snapshot.ReleaseSimulator();}
        }
        return "SandpitThreat:FiveBoundaries:ForcedDeathBypassesBlockAndTail:Targets:DeadOwner:Deadline:FullStateUnchanged:ForkIsolated";
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
