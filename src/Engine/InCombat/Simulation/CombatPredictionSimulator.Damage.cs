using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    // Convenience overload for non-card Damage with a single target and a DamageVar.
    public IReadOnlyList<DamageResult> Damage(
        Creature target,
        DamageVar damageVar,
        Creature? dealer)
    {
        return DamageSingleTarget(target, damageVar.BaseValue, damageVar.Props, dealer, null, null);
    }

    // Convenience overload for non-card Damage with a single target.
    public IReadOnlyList<DamageResult> Damage(
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer)
    {
        return DamageSingleTarget(target, amount, props, dealer, null, null);
    }

    // Convenience overload for non-card Damage with a DamageVar.
    public IReadOnlyList<DamageResult> Damage(
        IReadOnlyList<Creature> targets,
        DamageVar damageVar,
        Creature? dealer)
    {
        return Damage(targets, damageVar.BaseValue, damageVar.Props, dealer, cardSource: null, cardPlay: null);
    }

    // Convenience overload for non-card Damage.
    public IReadOnlyList<DamageResult> Damage(
        IReadOnlyList<Creature> targets,
        decimal amount,
        ValueProp props,
        Creature? dealer)
    {
        return Damage(targets, amount, props, dealer, cardSource: null, cardPlay: null);
    }

    // Mirrors CreatureCmd.Damage without mutating real Creature state.
    public IReadOnlyList<DamageResult> Damage(
        IReadOnlyList<Creature> targets,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        PredictedCard? cardSource,
        CardPlay? cardPlay)
    {
        if (dealer?.IsDead == true || targets.Count == 0)
        {
            // Vanilla returns empty DamageResult shells when the dealer is dead. The simulator
            // only uses damage results to update prediction state, so no-op results are omitted.
            return [];
        }

        CombatDamageSource source = ResolveDamageSource(cardSource);
        var results = new List<DamageResult>();

        foreach (var originalTarget in targets)
        {
            results.AddRange(DamageTarget(originalTarget, amount, props, dealer, cardSource, cardPlay, source));
        }

        ProcessDamageResults(results, dealer, cardSource, source);
        return results;
    }

    private IReadOnlyList<DamageResult> DamageSingleTarget(
        Creature target,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        PredictedCard? cardSource,
        CardPlay? cardPlay)
    {
        if (dealer?.IsDead == true)
            return [];
        CombatDamageSource source = ResolveDamageSource(cardSource);
        IReadOnlyList<DamageResult> results = DamageTarget(
            target,
            amount,
            props,
            dealer,
            cardSource,
            cardPlay,
            source);
        ProcessDamageResults(results, dealer, cardSource, source);
        return results;
    }

    // Mirrors the per-target body of CreatureCmd.Damage.
    private IReadOnlyList<DamageResult> DamageTarget(
        Creature originalTarget,
        decimal amount,
        ValueProp props,
        Creature? dealer,
        PredictedCard? cardSource,
        CardPlay? cardPlay,
        CombatDamageSource source)
    {
        var originalTargetState = State.GetCreature(originalTarget);
        if (originalTargetState.IsDead)
        {
            return [];
        }

        var modifiedAmount = HookMirrors.ModifyDamage(
            this,
            originalTarget,
            dealer, amount,
            props,
            cardSource,
            cardPlay);

        HookMirrors.BeforeDamageReceived(
            this,
            originalTarget,
            modifiedAmount,
            props,
            dealer,
            cardSource);

        var blockTarget = originalTarget.PetOwner?.Creature ?? originalTarget;
        var blockTargetState = State.GetCreature(blockTarget);
        var blockedDamage = blockTargetState.DamageBlock(modifiedAmount, props);

        var unblockedDamage = HookMirrors.ModifyHpLost(
            this,
            originalTarget,
            Math.Max(modifiedAmount - blockedDamage, 0m),
            props,
            dealer,
            cardSource,
            HpLossHookPhase.BeforeOsty,
            out _);

        var unblockedDamageTarget = Hook.ModifyUnblockedDamageTarget(
            State.CombatState,
            originalTarget,
            unblockedDamage,
            props,
            dealer);

        unblockedDamage = HookMirrors.ModifyHpLost(
            this,
            unblockedDamageTarget,
            unblockedDamage,
            props,
            dealer,
            cardSource,
            HpLossHookPhase.AfterOsty,
            out var afterOstyModifiers);
        HookMirrors.AfterModifyingHpLostAfterOsty(this, afterOstyModifiers);

        var unblockedDamageTargetState = State.GetCreature(unblockedDamageTarget);
        var unblockedDamageResult = unblockedDamageTargetState.LoseHp(unblockedDamage, props);
        var wasBlockBroken = originalTargetState.Block <= 0 && blockedDamage > 0m;
        var wasFullyBlocked = !props.HasFlag(ValueProp.Unblockable) &&
            (blockedDamage > 0m || originalTargetState.Block > 0) &&
            (int)unblockedDamage == 0;

        if (originalTarget == unblockedDamageTarget)
        {
            unblockedDamageResult.BlockedDamage = (int)blockedDamage;
            unblockedDamageResult.WasBlockBroken = wasBlockBroken;
            unblockedDamageResult.WasFullyBlocked = wasFullyBlocked;
            History.DamageReceived(
                unblockedDamageResult.Receiver,
                dealer,
                unblockedDamageResult,
                cardSource,
                source);
            if (State.CombatState is ICombatPredictionCardEventSink directEventSink)
                directEventSink.RecordDamageReceived(unblockedDamageResult.Receiver, dealer, unblockedDamageResult);
            return [unblockedDamageResult];
        }

        var originalTargetDamage = HookMirrors.ModifyHpLost(
            this,
            originalTarget,
            unblockedDamageResult.OverkillDamage,
            props,
            dealer,
            cardSource,
            HpLossHookPhase.AfterOsty,
            out var redirectedAfterOstyModifiers);
        HookMirrors.AfterModifyingHpLostAfterOsty(this, redirectedAfterOstyModifiers);

        var damageResult = originalTargetDamage > 0m
            ? originalTargetState.LoseHp(originalTargetDamage, props)
            : new DamageResult(originalTarget, props);
        damageResult.BlockedDamage = (int)blockedDamage;
        damageResult.WasBlockBroken = wasBlockBroken;
        damageResult.WasFullyBlocked = wasFullyBlocked;
        History.DamageReceived(
            unblockedDamageResult.Receiver,
            dealer,
            unblockedDamageResult,
            cardSource,
            source);
        if (State.CombatState is ICombatPredictionCardEventSink eventSink)
            eventSink.RecordDamageReceived(unblockedDamageResult.Receiver, dealer, unblockedDamageResult);
        History.DamageReceived(
            damageResult.Receiver,
            dealer,
            damageResult,
            cardSource,
            source);
        if (State.CombatState is ICombatPredictionCardEventSink redirectedEventSink)
            redirectedEventSink.RecordDamageReceived(damageResult.Receiver, dealer, damageResult);
        return [unblockedDamageResult, damageResult];
    }

    // Mirrors the post-target DamageResult processing in CreatureCmd.Damage.
    private void ProcessDamageResults(
        IEnumerable<DamageResult> results,
        Creature? dealer,
        PredictedCard? cardSource,
        CombatDamageSource source)
    {
        List<Creature>? killedCreatures = null;
        foreach (var damageResult in results)
        {
            var originalTarget = damageResult.Receiver;

            if (damageResult.WasBlockBroken)
            {
                HookMirrors.AfterBlockBroken(this, originalTarget, dealer);
            }

            if (damageResult.UnblockedDamage > 0)
            {
                HookMirrors.AfterCurrentHpChanged(this, originalTarget, -damageResult.UnblockedDamage);
            }

            HookMirrors.AfterDamageGiven(
                this,
                originalTarget,
                damageResult,
                damageResult.Props,
                dealer,
                cardSource);

            if (!damageResult.WasTargetKilled || !State.GetCreature(originalTarget).IsDead)
            {
                HookMirrors.AfterDamageReceived(
                    this,
                    originalTarget,
                    damageResult,
                    damageResult.Props,
                    dealer,
                    cardSource);
            }
            else
            {
                (killedCreatures ??= []).Add(originalTarget);
                if (originalTarget.Side == CombatSide.Enemy
                    && originalTarget.CombatId is uint combatId)
                {
                    string targetId = originalTarget.Monster?.Id.Entry
                        ?? throw new InvalidOperationException("敌方伤害击杀缺少怪物模型 ID。");
                    ActionRelicTriggers?.RecordKill(combatId, targetId, source);
                }
            }
        }

        if (killedCreatures != null)
            Kill(killedCreatures);
        else if (State.Players.All(player => State.GetCreature(player.Creature).IsDead))
            LoseCombat();
    }

    // Convenience overload for Kill with a single target.
    public void Kill(Creature creature, bool force = false)
    {
        KillWithoutCheckingWinCondition(creature, force);
        if (State.Players.All(player => State.GetCreature(player.Creature).IsDead))
            LoseCombat();
    }

    // Mirrors CreatureCmd.Kill.
    public void Kill(IReadOnlyList<Creature> creatures, bool force = false)
    {
        foreach (var creature in creatures)
        {
            KillWithoutCheckingWinCondition(creature, force);
        }

        if (State.Players.All(player => State.GetCreature(player.Creature).IsDead))
        {
            LoseCombat();
        }

        // Vanilla ends a player's turn when the player is killed, which is not simulated here.
    }

    // Mirrors CreatureCmd.KillWithoutCheckingWinCondition, without recursion checks.
    private void KillWithoutCheckingWinCondition(Creature creature, bool force, int recursion = 0)
    {
        var creatureState = State.GetCreature(creature);
        var currentHp = creatureState.CurrentHp;
        bool wasAlive = creatureState.IsAlive;
        if (currentHp > 0)
        {
            creatureState.LoseHp(currentHp, ValueProp.Unblockable | ValueProp.Unpowered);
            HookMirrors.AfterCurrentHpChanged(this, creature, -currentHp);
        }

        HookMirrors.BeforeDeath(this, creature);

        if (force || creatureState.MaxHp <= 0 || HookMirrors.ShouldDie(this, creature, out var preventer))
        {
            if (wasAlive
                && creature.Side == CombatSide.Enemy
                && creature.CombatId is uint combatId)
            {
                string targetId = creature.Monster?.Id.Entry
                    ?? throw new InvalidOperationException("直接击杀缺少怪物模型 ID。");
                ActionRelicTriggers?.RecordKill(combatId, targetId, ResolveDamageSource(null));
            }
            bool shouldRemoveFromCombat = State.CombatState is ICombatPredictionCreatureSemantics semantics
                ? semantics.ShouldRemoveAfterDeath(creature)
                : Hook.ShouldCreatureBeRemovedFromCombatAfterDeath(State.CombatState, creature);

            HookMirrors.AfterDeath(this, creature, wasRemovalPrevented: false);

            var aliveTeammates = State.GetTeammatesOf(creature)
                .Where(c => State.GetCreature(c).IsAlive)
                .ToArray();

            if (shouldRemoveFromCombat && creature.Side == CombatSide.Enemy && State.Enemies.Contains(creature))
            {
                // Vanilla also checks creature.Monster.IsPerformingMove here, which is omitted in the simulator
                // because we do not simulate monster moves.
                State.RemoveCreature(creature);
            }

            bool isPrimaryEnemy = State.CombatState is ICombatPredictionCreatureSemantics primarySemantics
                ? primarySemantics.IsPrimaryEnemy(creature)
                : creature.IsPrimaryEnemy;

            // Solver-owned combat states remove powers after running the complete predicted death-hook chain.

            if (creature.Side == CombatSide.Enemy)
            {
                if (isPrimaryEnemy
                    && aliveTeammates.Length > 0
                    && aliveTeammates.All(c => State.CombatState is ICombatPredictionCreatureSemantics predicted
                        ? !predicted.IsPrimaryEnemy(c)
                        : c.IsSecondaryEnemy))
                {
                    Kill(aliveTeammates);
                }
            }
            else if (creature.Player is { } player)
            {
                HandlePlayerDeath(player);
            }
        }
        else
        {
            HookMirrors.AfterDeath(this, creature, wasRemovalPrevented: true);
            HookMirrors.AfterPreventingDeath(this, preventer, creature);

            if (State.GetCreature(creature).IsDead)
            {
                if (recursion >= 10)
                    throw new InvalidOperationException("死亡被连续阻止十次后，生物仍未复活。");
                KillWithoutCheckingWinCondition(creature, force, recursion + 1);
            }
        }
    }

    // Mirrors the player-death flow in CreatureCmd.KillWithoutCheckingWinCondition.
    private void HandlePlayerDeath(Player player)
    {
        var playerState = State.GetPlayerCombatState(player);
        playerState.OrbQueue.Clear();

        if (State.GetOsty(player) is { } osty && State.GetCreature(osty).IsAlive)
        {
            Kill(osty, force: true);
        }

        // Player hook deactivation only affects a surviving multiplayer teammate's later hooks; multiplayer is out of scope.

        // Mirrors CombatManager.HandlePlayerDeath, which is only called when not all players are dead.
        if (!State.Players.All(p => State.GetCreature(p.Creature).IsDead))
        {
            RemoveFromCombat([.. playerState.AllCards]);

            // Vanilla calls PlayerCmd.Set{Energy,Stars} here, which in turn calls PlayerCmd.Lose{Energy,Stars}.
            // Technically, this can trigger some hooks, but since the player is dead, those hooks are not likely
            // to have any meaningful effect. Therefore, they are not mirrored here.
            playerState.LoseEnergy(playerState.Energy);
            playerState.LoseStars(playerState.Stars);
        }
    }
}
