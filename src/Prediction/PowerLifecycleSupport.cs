using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class PowerLifecycleSupport
{
    public static int SemanticallyRelevantAmountOnTurnStart(PowerModel power)
        => power is DrawCardsNextTurnPower or SummonNextTurnPower or HelloWorldPower
            ? power.AmountOnTurnStart
            : 0;

    public static void UpdateSurroundedForTarget(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        Creature? target)
    {
        if (target == null)
            return;
        foreach (SurroundedPower power in combat.EffectivePowers().OfType<SurroundedPower>())
        {
            if (power.Amount <= 0 || !ReferenceEquals(power.Owner.Player, player))
                continue;
            SurroundedPredictionState state = simulator.StateStore
                .Get(power, () => new SurroundedPredictionState(power));
            if (target.HasPower<BackAttackLeftPower>())
                state.Facing = SurroundedPower.Direction.Left;
            else if (target.HasPower<BackAttackRightPower>())
                state.Facing = SurroundedPower.Direction.Right;
        }
    }

    public static void AfterCardPlayed(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard card,
        int historyEntryStart)
    {
        CombatPredictionCardPlayStartedEntry? started = null;
        foreach (CombatPredictionHistoryEntry entry in simulator.History.EntriesFrom(historyEntryStart))
        {
            if (entry is not CombatPredictionCardPlayStartedEntry candidate
                || !ReferenceEquals(candidate.CardPlay.Card, card.Preview))
            {
                continue;
            }
            started = candidate;
            break;
        }
        if (started == null)
            return;

        Creature owner = card.Preview.Owner.Creature;
        foreach (PaleBlueDotPower power in combat.EffectivePowers().OfType<PaleBlueDotPower>().ToArray())
        {
            if (power.Amount <= 0
                || !ReferenceEquals(power.Owner, owner)
                || combat.IsPaleBlueDotActivated(power)
                || combat.GetCardsPlayedThisTurn(owner) + 1 < PaleBlueDotPower.cardPlayThresholdValue)
            {
                continue;
            }
            combat.SetPaleBlueDotActivated(power, true);
            combat.Apply<DrawCardsNextTurnPower>(owner, power.Amount, owner);
        }

        // VitalSparkPower.AfterCardPlayed is dispatched by AfterCardPlayedMirrors. Keeping a
        // second compensation here would apply TaintedPower twice.
    }

    public static void AfterEnergySpent(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard card,
        int amount)
    {
        if (amount <= 0)
            return;
        Creature owner = card.Preview.Owner.Creature;
        foreach (OrbitPower power in combat.EffectivePowers().OfType<OrbitPower>().ToArray())
        {
            if (power.Amount <= 0 || !ReferenceEquals(power.Owner, owner))
                continue;
            int triggers = combat.AdvanceOrbitEnergy(power, amount);
            if (triggers > 0)
                simulator.State.GetPlayerCombatState(card.Preview.Owner).GainEnergy(power.Amount * triggers);
        }
    }

    public static void AfterStarsSpent(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        PredictedCard card,
        int amount)
    {
        if (amount <= 0)
            return;
        Creature owner = card.Preview.Owner.Creature;
        combat.TriggerRelicsAfterStarsSpent(simulator, card.Preview.Owner, amount);
        foreach (ChildOfTheStarsPower power in combat.EffectivePowers().OfType<ChildOfTheStarsPower>())
        {
            if (power.Amount > 0 && ReferenceEquals(power.Owner, owner))
                simulator.GainBlock(owner, power.Amount * amount, ValueProp.Unpowered);
        }
    }

    public static void ResolvePowerAmountChanges(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
    {
        while (true)
        {
            SimulatedPowerAmountChange[] changes = combat.DrainPowerAmountChanges();
            if (changes.Length == 0)
                return;
            foreach (SimulatedPowerAmountChange change in changes)
            {
                foreach (PowerModel listener in combat.EffectivePowers().ToArray())
                {
                    if (listener.Amount <= 0)
                        continue;
                    switch (listener)
                    {
                        case ShroudPower when ReferenceEquals(change.Applier, listener.Owner)
                                                   && change.Power is DoomPower:
                            simulator.GainBlock(listener.Owner, listener.Amount, ValueProp.Unpowered);
                            break;
                        case SleightOfFleshPower when change.Delta != 0
                            && change.Power.GetTypeForAmount(change.Delta) == PowerType.Debuff
                            && change.Power.Owner.IsEnemy
                            && ReferenceEquals(change.Applier, listener.Owner)
                            && change.Power is not ITemporaryPower:
                            using (simulator.PushDamageSource(
                                CombatDamageSource.For(CombatDamageSourceKind.Power, nameof(SleightOfFleshPower))))
                            {
                                simulator.Damage(
                                    change.Power.Owner,
                                    listener.Amount,
                                    ValueProp.Unpowered,
                                    listener.Owner);
                            }
                            break;
                        case ViciousPower when change.Delta > 0
                                                && change.Power is VulnerablePower
                                                && ReferenceEquals(change.Applier, listener.Owner)
                                                && listener.Owner.Player is { } player:
                            simulator.Draw(player, listener.Amount);
                            break;
                    }
                }
            }
        }
    }
}
