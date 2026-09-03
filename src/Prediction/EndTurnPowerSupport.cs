using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static partial class EndTurnPowerSupport
{
    public static void TriggerRegular(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CombatSide side,
        IEnumerable<Creature> participants,
        int etherealExhaustCount = 0)
    {
        HashSet<Creature> participantSet = participants.ToHashSet();
        foreach (PowerModel power in combat.EffectivePowers().ToArray())
        {
            if (power.Amount <= 0)
                continue;

            Creature owner = power.Owner;
            bool ownerParticipates = participantSet.Contains(owner);
            switch (power)
            {
                case ColossusPower when side == CombatSide.Enemy:
                    combat.SetAmount<ColossusPower>(owner, power.Amount - 1);
                    break;
                case ConcoctPower when owner.Side != side:
                    combat.SetAmount<ConcoctPower>(owner, 0);
                    break;
                case CorrosiveWavePower when ownerParticipates:
                    combat.SetAmount<CorrosiveWavePower>(owner, 0);
                    break;
                case DemisePower when ownerParticipates && simulator.State.GetCreature(owner).IsAlive:
                    using (simulator.PushDamageSource(
                        CombatDamageSource.For(CombatDamageSourceKind.Power, nameof(DemisePower))))
                    {
                        simulator.Damage(owner, power.Amount, ValueProp.Unblockable | ValueProp.Unpowered, null);
                    }
                    break;
                case EscapeArtistPower when ownerParticipates && power.Amount > 1:
                    combat.SetAmount<EscapeArtistPower>(owner, power.Amount - 1);
                    break;
                case GravityPower when ownerParticipates:
                    combat.SetAmount<GravityPower>(owner, 0);
                    break;
                case HatchPower when ownerParticipates:
                    combat.SetAmount<HatchPower>(owner, power.Amount - 1);
                    break;
                case HighVoltagePower when ownerParticipates:
                    combat.Apply<StrengthPower>(owner, power.Amount, owner);
                    break;
                case TaintedPower when side == CombatSide.Enemy:
                    combat.SetAmount<TaintedPower>(owner, 0);
                    break;
                case TerritorialPower when ownerParticipates:
                    combat.Apply<StrengthPower>(owner, power.Amount, owner);
                    break;
                case ConsumingShadowPower when ownerParticipates && owner.Player is { } player:
                    EvokeLastOrbs(simulator, player, power.Amount);
                    break;
                case NemesisPower when ownerParticipates:
                    TriggerNemesis(combat, owner);
                    break;
                case JugglingPower juggling when ownerParticipates:
                    simulator.StateStore
                        .Get(juggling, () => new JugglingPredictionState(juggling))
                        .AttacksPlayedThisTurn = 0;
                    break;
                case TenderPower when ownerParticipates:
                    RestoreTender(combat, owner, power.Applier);
                    break;
                case AsleepPower when ownerParticipates:
                {
                    int remaining = power.Amount - 1;
                    combat.SetPowerAmount(power, remaining);
                    if (remaining <= 0)
                    {
                        combat.SetAmount<PlatingPower>(owner, 0);
                        combat.SetMonsterBool(owner, "_isAwake", true);
                    }
                    break;
                }
                case SlumberPower when ownerParticipates:
                {
                    int remaining = power.Amount - 1;
                    combat.SetPowerAmount(power, remaining);
                    if (remaining <= 0)
                    {
                        combat.SetAmount<PlatingPower>(owner, 0);
                        combat.SetMonsterBool(owner, "_isAwake", true);
                    }
                    break;
                }
                case BattlewornDummyTimeLimitPower when ownerParticipates
                                                        && simulator.State.GetCreature(owner).IsAlive:
                    if (power.Amount > 1)
                        combat.SetPowerAmount(power, power.Amount - 1);
                    else
                        combat.MarkBattlewornDummyTimedOut();
                    break;
                case DarkEmbracePower when ownerParticipates
                                                 && etherealExhaustCount > 0
                                                 && owner.Player is { } player:
                    simulator.Draw(player, power.Amount * etherealExhaustCount);
                    break;
                case DoomPower when ownerParticipates
                                    && side != CombatSide.Enemy
                                    && simulator.State.GetCreature(owner).IsAlive
                                    && simulator.State.GetCreature(owner).CurrentHp <= power.Amount:
                    simulator.Kill(owner);
                    break;
                case PaleBlueDotPower paleBlueDot when ownerParticipates:
                    combat.SetPaleBlueDotActivated(paleBlueDot, activated: false);
                    break;
                case SmoggyPower when ownerParticipates:
                    combat.ClearSmogAfflictions(simulator, owner);
                    break;
                case ShrinkPower when ownerParticipates:
                    combat.SetPowerAmount(power, power.Amount - 1);
                    break;
            }
        }
        foreach (Creature owner in participantSet)
            combat.ResetPowerLifecycleTurn(owner);
        TriggerBatch048(simulator, combat, side, participantSet);
    }

    public static void TriggerVeryEarly(
        SimulatedCombatState combat,
        IEnumerable<Creature> participants)
    {
        HashSet<Creature> participantSet = participants.ToHashSet();
        foreach (AsleepPower asleep in combat.EffectivePowers().OfType<AsleepPower>().ToArray())
        {
            if (asleep.Amount <= 1 && participantSet.Contains(asleep.Owner))
                combat.SetAmount<PlatingPower>(asleep.Owner, 0);
        }
    }

    public static void TriggerEnemyDoom(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IEnumerable<Creature> participants)
    {
        Creature[] doomed = participants.Where(owner =>
                simulator.State.GetCreature(owner).IsAlive
                && combat.GetAmount<DoomPower>(owner) >= simulator.State.GetCreature(owner).CurrentHp)
            .ToArray();
        using (simulator.PushDamageSource(
            CombatDamageSource.For(CombatDamageSourceKind.Power, nameof(DoomPower))))
        {
            combat.DoomKill(simulator, doomed);
        }
    }

    public static void TriggerLate(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IEnumerable<Creature> participants)
    {
        foreach (Creature owner in participants)
        {
            int amount = combat.GetAmount<DisintegrationPower>(owner);
            if (amount > 0 && simulator.State.GetCreature(owner).IsAlive)
            {
                using (simulator.PushDamageSource(
                    CombatDamageSource.For(CombatDamageSourceKind.Power, nameof(DisintegrationPower))))
                {
                    simulator.Damage(owner, amount, ValueProp.Unpowered, owner);
                }
            }
        }
    }

    private static void EvokeLastOrbs(
        CombatPredictionSimulator simulator,
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        int amount)
    {
        SimOrbQueue queue = simulator.State.GetPlayerCombatState(player).OrbQueue;
        for (int index = 0; index < amount && queue.Orbs.Count > 0; index++)
        {
            OrbModel orb = queue.Orbs[^1];
            simulator.OrbEvoke(player, orb);
        }
    }

    private static void TriggerNemesis(SimulatedCombatState combat, Creature owner)
    {
        bool applyIntangible = !combat.GetNemesisShouldApplyIntangible(owner);
        combat.SetNemesisShouldApplyIntangible(owner, applyIntangible);
        if (applyIntangible)
            combat.ApplyFromMonster<IntangiblePower>(owner, 1, owner);
        else
            combat.SetAmount<IntangiblePower>(owner, 0);
    }

    private static void RestoreTender(
        SimulatedCombatState combat,
        Creature owner,
        Creature? applier)
    {
        int cardsPlayed = combat.GetTenderCardsPlayed(owner);
        if (cardsPlayed > 0)
        {
            combat.Apply<StrengthPower>(owner, cardsPlayed, applier);
            combat.Apply<DexterityPower>(owner, cardsPlayed, applier);
        }
        combat.ResetTenderCardsPlayed(owner);
    }
}
