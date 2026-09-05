using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    private const int MaxSimulatedChanneledOrbs = 1000;

    public void AddOrbSlots(Player player, int amount)
    {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        SimOrbQueue queue = State.GetPlayerCombatState(player).OrbQueue;
        int added = Math.Min(OrbQueue.maxCapacity - queue.Capacity, amount);
        if (added > 0)
            queue.AddCapacity(added);
    }

    // Mirrors OrbModel.TriggerPassive without VFX/SFX, waits, or real model-stack updates.
    internal void TriggerOrbPassive(
        OrbModel orb,
        Creature? target,
        ISet<uint>? processedEnemyDeaths = null)
    {
        if (HasPendingChoice)
            return;

        var triggerCount = HookMirrors.ModifyOrbPassiveTriggerCount(this, orb, 1, out _);
        if (HasPendingChoice)
            return;
        // Vanilla calls Hook.AfterModifyingOrbPassiveTriggerCount here, but all listeners are cosmetic.
        processedEnemyDeaths ??= new HashSet<uint>();

        for (var i = 0; i < triggerCount; i++)
        {
            OrbPassive(orb, target, processedEnemyDeaths);
            if (HasPendingChoice)
                return;
        }
    }

    // Mirrors OrbCmd.Channel<T> without mutating the real orb queue.
    public void OrbChannel<T>(Player player, int count = 1) where T : OrbModel
    {
        for (var i = 0; i < count; i++)
        {
            if (!OrbChannel(player, CanonicalModels.Orb<T>().ToMutable()))
            {
                break;
            }
        }
    }

    // Mirrors OrbCmd.Channel without VFX/SFX, waits, real queue mutation, or async hook execution.
    public bool OrbChannel(Player player, OrbModel orb)
    {
        if (IsOverOrEnding || HasPendingChoice)
        {
            return false;
        }

        if (History.Count<CombatPredictionOrbChanneledEntry>() >= MaxSimulatedChanneledOrbs)
        {
            History.RecordRisk(PredictionRiskReason.OrbChannelLimitExceeded);
            return false;
        }

        var orbQueue = State.GetPlayerCombatState(player).OrbQueue;
        if (player.Character.BaseOrbSlotCount == 0 && orbQueue.Capacity == 0)
        {
            orbQueue.AddCapacity(1);
        }

        orb.AssertMutable();
        orb.Owner = player;

        if (orbQueue.Capacity > 0 && orbQueue.Orbs.Count >= orbQueue.Capacity)
        {
            OrbEvokeNext(player);
            if (HasPendingChoice)
                return false;

            // Vanilla OrbCmd.Channel immediately calls OrbQueue.TryEnqueue after EvokeNext. If
            // evoke side effects synchronously channel another orb and refill the freed slot,
            // vanilla throws "OrbQueue is full" here. Prediction fails closed instead of
            // reproducing that bug or inventing additional evokes to make room.
            if (orbQueue.Orbs.Count >= orbQueue.Capacity)
            {
                History.RecordRisk(PredictionRiskReason.MethodMirrorIncomplete);
                return false;
            }
        }

        if (!orbQueue.TryEnqueue(orb))
        {
            return false;
        }

        History.OrbChanneled(orb);
        HookMirrors.AfterOrbChanneled(this, player, orb);
        return !HasPendingChoice;
    }

    // Mirrors OrbCmd.EvokeNext without mutating the real orb queue.
    public void OrbEvokeNext(Player player, int repeat = 1, bool dequeue = true)
    {
        if (HasPendingChoice)
            return;

        var orbQueue = State.GetPlayerCombatState(player).OrbQueue;
        if (orbQueue.Orbs.Count > 0)
        {
            var orb = orbQueue.Orbs[0];
            ISet<uint> processedEnemyDeaths = new HashSet<uint>();
            for (int i = 0; i < repeat; i++)
            {
                OrbEvoke(player, orb, dequeue: dequeue && i == repeat - 1);
                if (HasPendingChoice)
                    return;
                if (State.CombatState is ICombatPredictionEnemyDeathSink deathSink)
                {
                    deathSink.ResolvePendingEnemyDeaths(this, processedEnemyDeaths);
                    if (HasPendingChoice)
                        return;
                }
            }
        }
    }

    // Mirrors OrbCmd.Evoke without VFX/SFX, choice-context model stack updates, or real queue mutation.
    public void OrbEvoke(Player player, OrbModel evokedOrb, bool dequeue = true)
    {
        if (IsOverOrEnding || HasPendingChoice)
        {
            return;
        }

        var orbQueue = State.GetPlayerCombatState(player).OrbQueue;
        if (orbQueue.Orbs.Count <= 0)
        {
            return;
        }

        if (dequeue)
        {
            _ = orbQueue.Remove(evokedOrb);
        }

        var targets = OrbMirrors.InvokeEvoke(this, evokedOrb);
        if (HasPendingChoice)
            return;

        // Vanilla calls evokedOrb.RemoveInternal after AfterOrbEvoked when dequeue succeeds.
        // We only remove from the shadow queue because mutating the real orb would affect
        // gameplay state and save data.
        HookMirrors.AfterOrbEvoked(this, evokedOrb, targets);
    }

    // Mirrors OrbCmd.Passive without VFX/SFX, choice-context model stack updates, or real orb mutation.
    public void OrbPassive(
        OrbModel orb,
        Creature? target = null,
        ISet<uint>? processedEnemyDeaths = null)
    {
        if (IsOverOrEnding || HasPendingChoice)
        {
            return;
        }

        OrbMirrors.InvokePassive(this, orb, target);
        if (HasPendingChoice)
            return;
        if (State.CombatState is ICombatPredictionEnemyDeathSink deathSink)
        {
            deathSink.ResolvePendingEnemyDeaths(
                this,
                processedEnemyDeaths ?? new HashSet<uint>());
        }
    }
}
