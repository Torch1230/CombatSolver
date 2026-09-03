using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Damage;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal static class TurnStartPowerSupport
{
    public static void PrepareVoidFormApplication(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature target)
    {
        VoidFormPower? power = combat.GetMutablePower<VoidFormPower>(target);
        if (power is not { Amount: > 0 })
            return;
        simulator.StateStore
            .Get(power, () => new VoidFormPredictionState(power))
            .CardsPlayedThisTurn = 999_999_999;
    }

    public static bool TriggerBeforeSideTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        IReadOnlyList<Creature> participants)
    {
        if (combat.CurrentSide == CombatSide.Player && combat.RoundNumber <= 1)
        {
            foreach (PlatingPower plating in combat.EffectivePowers().OfType<PlatingPower>())
            {
                if (plating.Amount > 0 && plating.Owner.IsEnemy)
                    simulator.GainBlock(plating.Owner, plating.Amount, ValueProp.Unpowered);
            }
        }

        foreach (AggressionPower aggression in combat.EffectivePowers().OfType<AggressionPower>().ToArray())
        {
            if (aggression.Amount <= 0
                || !participants.Contains(aggression.Owner)
                || aggression.Owner.Player is not { } aggressionPlayer)
            {
                continue;
            }
            SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(aggressionPlayer);
            PredictedCard[] selected = playerState.DiscardPile.Cards
                .Where(static card => card.Preview.Type == CardType.Attack)
                .ToList()
                .UnstableShuffle(simulator.Rng.CombatCardSelection)
                .Take(aggression.Amount)
                .ToArray();
            foreach (PredictedCard card in selected)
            {
                simulator.AddToPile([card], PileType.Hand);
                if (card.Preview.IsUpgradable)
                    card.Upgrade();
            }
        }

        foreach (PowerModel power in combat.EffectivePowers().ToArray())
        {
            if (power.Amount <= 0)
                continue;

            switch (power)
            {
                case HardenedShellPower shell:
                    simulator.StateStore
                        .Get(shell, () => new HardenedShellPredictionState(shell))
                        .DamageReceivedThisTurn = 0;
                    break;
                case SlothPower sloth when participants.Contains(sloth.Owner):
                    simulator.StateStore
                        .Get(sloth, () => new CounterPredictionState(
                            combat.GetCardsPlayedThisTurn(sloth.Owner)))
                        .Value = 0;
                    break;
                case VoidFormPower voidForm when participants.Contains(voidForm.Owner):
                    simulator.StateStore
                        .Get(voidForm, () => new VoidFormPredictionState(voidForm))
                        .CardsPlayedThisTurn = 0;
                    break;
            }
        }
        return false;
    }

    public static bool TriggerBeforeHandDraw(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        TurnStartChoiceCursor choices)
    {
        foreach (PowerModel power in combat.EffectivePowers().ToArray())
        {
            if (power.Amount <= 0 || !ReferenceEquals(power.Owner.Player, player))
                continue;

            if (power is ForegoneConclusionPower)
            {
                SimPlayerCombatState state = simulator.State.GetPlayerCombatState(player);
                if (state.DrawPile.IsEmpty && !state.DiscardPile.IsEmpty)
                {
                    simulator.Shuffle(player);
                    if (combat.HasPendingChoice)
                        return true;
                }
                if (!TurnStartChoiceSupport.Resolve(
                        simulator,
                        combat,
                        player,
                        choices,
                        power.Id.Entry,
                        PlanChoiceEffect.MoveToHand,
                        power.Amount,
                        PileType.Draw))
                {
                    return true;
                }
                combat.SetPowerAmount(power, 0);
                continue;
            }

            IEnumerable<CardModel>? options = null;
            int count = power.Amount;
            bool ethereal = false;
            bool generateOneAtATime = false;
            switch (power)
            {
                case CallOfTheVoidPower:
                    options = player.Character.CardPool
                        .GetUnlockedCards(player.UnlockState, combat.CardMultiplayerConstraint)
                        .Where(card => card.Rarity is not (CardRarity.Basic or CardRarity.Ancient));
                    ethereal = true;
                    generateOneAtATime = true;
                    break;
                case CreativeAiPower:
                    options = player.Character.CardPool
                        .GetUnlockedCards(player.UnlockState, combat.CardMultiplayerConstraint)
                        .Where(card => card.Type == CardType.Power);
                    generateOneAtATime = true;
                    break;
                case HelloWorldPower when power.AmountOnTurnStart >= 1:
                    options = player.Character.CardPool
                        .GetUnlockedCards(player.UnlockState, combat.CardMultiplayerConstraint)
                        .Where(card => card.Rarity == CardRarity.Common);
                    count = power.AmountOnTurnStart;
                    break;
                case SpectrumShiftPower:
                    options = ModelDb.CardPool<ColorlessCardPool>()
                        .GetUnlockedCards(player.UnlockState, combat.CardMultiplayerConstraint);
                    break;
            }
            if (options == null || count <= 0)
                continue;

            List<PredictedCard> generated;
            if (generateOneAtATime)
            {
                generated = [];
                for (int index = 0; index < count; index++)
                {
                    PredictedCard? card = options
                        .GetDistinctForCombat(
                            player,
                            1,
                            simulator.Rng.CombatCardGeneration,
                            combat.CardMultiplayerConstraint)
                        .FirstOrDefault();
                    if (card != null)
                        generated.Add(card);
                }
            }
            else
            {
                generated = options
                    .GetDistinctForCombat(
                        player,
                        count,
                        simulator.Rng.CombatCardGeneration,
                        combat.CardMultiplayerConstraint)
                    .ToList();
            }
            if (ethereal)
            {
                foreach (PredictedCard card in generated)
                    card.MutablePreview.AddKeyword(CardKeyword.Ethereal);
            }
            simulator.AddGeneratedCardsToCombat(
                generated,
                PileType.Hand,
                player,
                CardPilePosition.Bottom,
                CardGenerationResultKind.Random);
        }
        return false;
    }

    public static bool TriggerAfterPlayerTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Player player,
        TurnStartChoiceCursor choices)
    {
        Creature owner = player.Creature;
        foreach (var power in combat.EffectivePowers().ToArray())
        {
            if (power.Amount <= 0 || !ReferenceEquals(power.Owner, owner))
                continue;

            switch (power)
            {
                case EntropyPower:
                    if (!TurnStartChoiceSupport.Resolve(
                            simulator,
                            combat,
                            player,
                            choices,
                            power.Id.Entry,
                            PlanChoiceEffect.Transform,
                            power.Amount))
                    {
                        return true;
                    }
                    break;
                case CrimsonMantlePower mantle:
                    int selfDamage = mantle.DynamicVars["SelfDamage"].IntValue;
                    if (selfDamage > 0)
                    {
                        simulator.Damage(
                            owner,
                            selfDamage,
                            ValueProp.Unblockable | ValueProp.Unpowered,
                            owner);
                    }
                    simulator.GainBlock(owner, mantle.Amount, ValueProp.Unpowered);
                    break;
                case HibernatePower:
                    combat.SetPowerAmount(power, power.Amount - 1);
                    break;
                case InfernoPower inferno:
                    int infernoDamage = inferno.DynamicVars["SelfDamage"].IntValue;
                    if (infernoDamage > 0)
                    {
                        simulator.Damage(
                            owner,
                            infernoDamage,
                            ValueProp.Unblockable | ValueProp.Unpowered,
                            owner);
                    }
                    break;
                case LoopPower:
                    SimOrbQueue queue = simulator.State.GetPlayerCombatState(player).OrbQueue;
                    if (queue.Orbs.Count == 0)
                        break;
                    for (int index = 0; index < power.Amount; index++)
                        simulator.OrbPassive(queue.Orbs[0]);
                    break;
                case RollingBoulderPower rolling:
                    using (simulator.PushDamageSource(
                        CombatDamageSource.For(CombatDamageSourceKind.Power, nameof(RollingBoulderPower))))
                    {
                        simulator.Damage(combat.HittableEnemies, rolling.Amount, ValueProp.Unpowered, owner);
                    }
                    combat.SetPowerAmount(rolling, rolling.Amount + rolling.DynamicVars.Damage.IntValue);
                    break;
                case SummonNextTurnPower:
                    combat.SummonOsty(simulator, player, power.Amount);
                    combat.SetPowerAmount(power, 0);
                    break;
                case ToolsOfTheTradePower:
                    if (!TurnStartChoiceSupport.Resolve(
                            simulator,
                            combat,
                            player,
                            choices,
                            power.Id.Entry,
                            PlanChoiceEffect.Discard,
                            power.Amount))
                    {
                        return true;
                    }
                    break;
                case TyrannyPower:
                    if (!TurnStartChoiceSupport.Resolve(
                            simulator,
                            combat,
                            player,
                            choices,
                            power.Id.Entry,
                            PlanChoiceEffect.Exhaust,
                            power.Amount))
                    {
                        return true;
                    }
                    break;
            }
        }
        return false;
    }

    public static void TriggerAfterSideTurnStart(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        CombatSide side,
        IReadOnlyList<Creature> participants)
    {
        foreach (CountdownPower countdown in combat.EffectivePowers().OfType<CountdownPower>().ToArray())
        {
            if (countdown.Amount <= 0 || !participants.Contains(countdown.Owner))
                continue;
            List<Creature> candidates = combat.GetOpponentsOf(countdown.Owner)
                .Where(simulator.State.IsHittable)
                .ToList();
            if (candidates.Count == 0)
                continue;
            Creature target = simulator.Rng.CombatTargets.NextItem(candidates)
                ?? throw new InvalidOperationException("倒计时的随机目标列表非空但没有返回目标。");
            combat.Apply<DoomPower>(target, countdown.Amount, countdown.Owner);
        }

        if (side != CombatSide.Enemy)
            return;
        foreach (SandpitPower sandpit in combat.EffectivePowers().OfType<SandpitPower>().ToArray())
        {
            if (sandpit.Amount <= 0)
                continue;
            int remaining = sandpit.Amount - 1;
            combat.SetPowerAmount(sandpit, remaining);
            if (remaining > 0)
                continue;

            Creature target = sandpit.Target
                ?? throw new InvalidOperationException("流沙坑没有被拖入坑中的目标。");
            if (!simulator.State.GetCreature(sandpit.Owner).IsAlive
                || !simulator.State.GetCreature(target).IsAlive)
            {
                continue;
            }
            using (simulator.PushDamageSource(
                CombatDamageSource.For(CombatDamageSourceKind.Power, nameof(SandpitPower))))
            {
                simulator.Kill(target, force: true);
            }
            if (target.Player is { } player
                && simulator.State.GetOsty(player) is { } osty
                && simulator.State.GetCreature(osty).IsAlive)
            {
                using (simulator.PushDamageSource(
                    CombatDamageSource.For(CombatDamageSourceKind.Power, nameof(SandpitPower))))
                {
                    simulator.Kill(osty, force: true);
                }
            }
        }
    }
}
