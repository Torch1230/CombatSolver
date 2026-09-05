using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Hooks;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnEnd;

using Registry = MethodMirrorRegistry<AbstractModel, BeforeSideTurnEndMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.BeforeSideTurnEnd.
internal static class BeforeSideTurnEndMirrors
{
    private static readonly MirrorMethodSpec BeforeSideTurnEndVeryEarly = MirrorMethodSpec.Hook(
        nameof(AbstractModel.BeforeSideTurnEndVeryEarly),
        [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IEnumerable<Creature>)]);

    private static readonly MirrorMethodSpec BeforeSideTurnEndEarly = MirrorMethodSpec.Hook(
        nameof(AbstractModel.BeforeSideTurnEndEarly),
        [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IEnumerable<Creature>)]);

    private static readonly MirrorMethodSpec BeforeSideTurnEnd = MirrorMethodSpec.Hook(
        nameof(AbstractModel.BeforeSideTurnEnd),
        [typeof(PlayerChoiceContext), typeof(CombatSide), typeof(IEnumerable<Creature>)]);

    private static readonly Registry VeryEarlyRegistry = CreateVeryEarlyRegistry();
    private static readonly Registry EarlyRegistry = CreateEarlyRegistry();
    private static readonly Registry Registry = CreateRegistry();

    public static void InvokeVeryEarly(AbstractModel listener, BeforeSideTurnEndMirrorContext context)
    {
        VeryEarlyRegistry.Invoke(listener, context);
    }

    public static void InvokeEarly(AbstractModel listener, BeforeSideTurnEndMirrorContext context)
    {
        EarlyRegistry.Invoke(listener, context);
    }

    public static void Invoke(AbstractModel listener, BeforeSideTurnEndMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    private static Registry CreateVeryEarlyRegistry()
    {
        var registry = new Registry(BeforeSideTurnEndVeryEarly);

        registry.Register<Orichalcum>(OrichalcumMirrors.BeforeSideTurnEndVeryEarly);
        registry.Register<FakeOrichalcum>(OrichalcumMirrors.BeforeSideTurnEndVeryEarly);
        registry.RegisterIgnored<AsleepPower>();

        return registry;
    }

    private static Registry CreateEarlyRegistry()
    {
        var registry = new Registry(BeforeSideTurnEndEarly);

        registry.Register<PlatingPower>(HandlePlatingPower);
        registry.Register<RegenPower>(HandleRegenPower);
        // PrepareExtraPlayerTurn resolves Pael's Eye before the turn history is rolled.
        // Re-evaluating it here would see the freshly reset history and exhaust hands
        // even on turns where the player actually played cards.
        registry.RegisterIgnored<PaelsEye>();

        return registry;
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(BeforeSideTurnEnd);

        registry.Register<Orichalcum>(OrichalcumMirrors.BeforeSideTurnEnd);
        registry.Register<FakeOrichalcum>(OrichalcumMirrors.BeforeSideTurnEnd);
        registry.Register<CloakClasp>(HandleCloakClasp);
        registry.Register<RippleBasin>(HandleRippleBasin);
        registry.Register<HailstormPower>(HandleHailstormPower);
        registry.Register<ScreamingFlagon>(HandleScreamingFlagon);
        registry.Register<StoneCalendar>(HandleStoneCalendar);
        registry.Register<TheBombPower>(HandleTheBombPower);
        registry.Register<DoomPower>(HandleDoomPower);
        registry.Register<Regret>(HandleRegret);
        registry.Register<ChainsOfBindingPower>(HandleChainsOfBindingPower);

        registry.RegisterIgnored<PaelsTears>();
        registry.RegisterIgnored<SandpitPower>();

        return registry;
    }

    private static void HandlePlatingPower(PlatingPower power, BeforeSideTurnEndMirrorContext context)
    {
        if (context.Participants.Contains(power.Owner))
        {
            context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
        }
    }

    private static void HandleRegenPower(RegenPower power, BeforeSideTurnEndMirrorContext context)
    {
        if (context.Participants.Contains(power.Owner) && context.State.GetCreature(power.Owner).IsAlive)
        {
            // Mirrors RegenPower.BeforeSideTurnEndEarly's CreatureCmd.Heal call before Doom's
            // normal end-turn kill check. PowerCmd.Decrement is not persisted in prediction state
            // because no later hook in this simulation consumes Regen's decremented amount.
            context.Simulator.Heal(power.Owner, power.Amount);
        }
    }

    private static void HandleCloakClasp(CloakClasp relic, BeforeSideTurnEndMirrorContext context)
    {
        if (!context.Participants.Contains(relic.Owner.Creature))
        {
            return;
        }

        var cardsInHand = context.State.GetPlayerCombatState(relic.Owner).Hand.Cards.Count;
        if (cardsInHand <= 0)
        {
            return;
        }

        context.Simulator.GainBlock(
            relic.Owner.Creature,
            cardsInHand * relic.DynamicVars.Block.BaseValue,
            ValueProp.Unpowered);
    }

    private static void HandleRippleBasin(RippleBasin relic, BeforeSideTurnEndMirrorContext context)
    {
        if (context.CombatState is not SimulatedCombatState combat)
            throw new InvalidOperationException("涟漪盆缺少分支出牌历史。");
        if (!context.Participants.Contains(relic.Owner.Creature)
            || combat.GetAttacksPlayedThisTurn(relic.Owner.Creature) > 0)
        {
            return;
        }

        context.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
    }

    private static void HandleHailstormPower(HailstormPower power, BeforeSideTurnEndMirrorContext context)
    {
        if (!context.Participants.Contains(power.Owner) ||
            power.Owner.Player is not { } player)
        {
            return;
        }

        var frostCount = context.State.GetPlayerCombatState(player).OrbQueue.Orbs
            .Count(static orb => orb is FrostOrb);
        if (frostCount >= power.DynamicVars[HailstormPower.frostOrbKey].IntValue)
        {
            context.Simulator.Damage(context.State.HittableEnemies, power.Amount, ValueProp.Unpowered, power.Owner);
        }
    }

    private static void HandleScreamingFlagon(ScreamingFlagon relic, BeforeSideTurnEndMirrorContext context)
    {
        if (context.Participants.Contains(relic.Owner.Creature) &&
            context.State.GetPlayerCombatState(relic.Owner).Hand.IsEmpty)
        {
            context.Simulator.Damage(context.State.HittableEnemies, relic.DynamicVars.Damage, relic.Owner.Creature);
        }
    }

    private static void HandleStoneCalendar(StoneCalendar relic, BeforeSideTurnEndMirrorContext context)
    {
        if (context.Participants.Contains(relic.Owner.Creature) &&
            context.CombatState is SimulatedCombatState combat &&
            combat.GetPlayerTurnNumber(relic.Owner) == relic.DynamicVars[StoneCalendar._damageTurnKey].IntValue)
        {
            context.Simulator.Damage(context.State.HittableEnemies, relic.DynamicVars.Damage, relic.Owner.Creature);
        }
    }

    private static void HandleTheBombPower(TheBombPower power, BeforeSideTurnEndMirrorContext context)
    {
        if (!context.Participants.Contains(power.Owner))
            return;

        PowerAmountPredictionState amount = context.StateStore.GetPowerAmount(power);
        if (amount.Amount > 1)
        {
            amount.Decrement();
            return;
        }

        context.Simulator.Damage(context.State.HittableEnemies, power.DynamicVars.Damage, power.Owner);
        if (context.Simulator.HasPendingChoice)
            return;
        amount.Consume();
    }

    private static void HandleDoomPower(DoomPower power, BeforeSideTurnEndMirrorContext context)
    {
        if (context.Simulator.IsOverOrEnding ||
            context.Side == CombatSide.Player ||
            !context.Participants.Contains(power.Owner) ||
            !context.State.GetCreature(power.Owner).IsAlive)
        {
            return;
        }

        if (context.CombatState is not ICombatPredictionHookListenerSource listeners
            || context.CombatState is not ICombatPredictionEffectSink effects)
        {
            throw new InvalidOperationException("毁灭结算缺少可写的预测状态。");
        }
        Dictionary<Creature, int> doomByOwner = listeners.HookListeners
            .OfType<DoomPower>()
            .Where(doom => doom.Amount > 0)
            .GroupBy(doom => doom.Owner)
            .ToDictionary(group => group.Key, group => group.Sum(doom => doom.Amount));
        var doomedCreatures = context.State.GetCreaturesOnSide(context.Side)
            .Where(creature => doomByOwner.TryGetValue(creature, out int amount)
                && context.State.GetCreature(creature).CurrentHp <= amount)
            .ToList();
        if (doomedCreatures.Count > 0 && doomedCreatures[0] == power.Owner)
        {
            effects.DoomKill(context.Simulator, doomedCreatures);
        }
    }

    private static void HandleRegret(Regret card, BeforeSideTurnEndMirrorContext context)
    {
        if (!context.Participants.Contains(card.Owner.Creature))
        {
            return;
        }

        var ownerState = context.State.GetPlayerCombatState(card.Owner);
        if (ownerState.Hand.Find(card) is not { } predictedCard)
        {
            return;
        }

        var previewCard = (Regret)predictedCard.MutablePreview;
        previewCard.CardsInHand = ownerState.Hand.Cards.Count;
    }

    private static void HandleChainsOfBindingPower(
        ChainsOfBindingPower power,
        BeforeSideTurnEndMirrorContext context)
    {
        if (!context.Participants.Contains(power.Owner) ||
            power.Owner.Player is not { } player)
        {
            return;
        }

        foreach (var card in context.State.GetPlayerCombatState(player).AllCards)
        {
            if (card.Preview.Affliction is Bound)
            {
                card.ClearAffliction();
            }
        }
        ChainsOfBindingPredictionState state = context.StateStore.Get(
            power,
            () => new ChainsOfBindingPredictionState(power));
        state.BoundCardPlayed = false;
        state.BoundCardsAfflictedThisTurn = 0;
    }

}

internal sealed class BeforeSideTurnEndMirrorContext : CombatMirrorContext
{
    public required CombatSide Side { get; init; }

    public required IReadOnlyList<Creature> Participants { get; init; }
}
