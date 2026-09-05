using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class BespokeCardMirrors
{
    public static void AstralPulseOnPlay(AstralPulse _, CardOnPlayMirrorContext context)
        => context.AttackAllOpponents(hitCount: 2);

    public static void DaggerSprayOnPlay(DaggerSpray _, CardOnPlayMirrorContext context)
        => context.AttackAllOpponents(hitCount: 2);

    public static void PactsEndOnPlay(PactsEnd card, CardOnPlayMirrorContext context)
    {
        if (context.OwnerState.ExhaustPile.Cards.Count >= card.DynamicVars.Cards.IntValue)
            context.AttackAllOpponents();
    }

    public static void TwinStrikeOnPlay(TwinStrike _, CardOnPlayMirrorContext context)
        => context.AttackSingle(hitCount: 2);

    public static void FiendFireOnPlay(FiendFire card, CardOnPlayMirrorContext context)
    {
        PredictedCard[] hand = context.OwnerState.Hand.Cards.ToArray();
        foreach (PredictedCard candidate in hand)
        {
            context.Simulator.Exhaust(candidate);
            if (context.Simulator.HasPendingChoice)
                return;
        }
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(hand.Length)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
    }

    public static void DismantleOnPlay(Dismantle card, CardOnPlayMirrorContext context)
    {
        int hitCount = GetPowerAmount<VulnerablePower>(context, context.Target) > 0 ? 2 : 1;
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(hitCount)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
    }

    public static void EntrenchOnPlay(Entrench card, CardOnPlayMirrorContext context)
    {
        int currentBlock = context.State.GetCreature(card.Owner.Creature).Block;
        context.GainBlock(
            card.Owner.Creature,
            currentBlock,
            MegaCrit.Sts2.Core.ValueProps.ValueProp.Unpowered
            | MegaCrit.Sts2.Core.ValueProps.ValueProp.Move);
    }

    public static void LeadingStrikeOnPlay(LeadingStrike card, CardOnPlayMirrorContext context)
    {
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Simulator.CreateAndAddGeneratedCardsToCombat<Shiv>(
            card.Owner,
            PileType.Hand,
            card.DynamicVars["Shivs"].IntValue,
            card.Owner);
    }

    public static void MaulOnPlay(Maul card, CardOnPlayMirrorContext context)
    {
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(2)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
        if (context.Simulator.HasPendingChoice)
            return;
        decimal increase = card.DynamicVars["Increase"].BaseValue;
        foreach (PredictedCard candidate in context.OwnerState.AllCards
                     .Where(candidate => candidate.Preview is Maul)
                     .ToArray())
        {
            Maul mutable = (Maul)candidate.MutablePreview;
            mutable.DynamicVars.Damage.BaseValue += increase;
            mutable._extraDamageFromMaulPlays += increase;
        }
    }

    public static void SpiteOnPlay(Spite card, CardOnPlayMirrorContext context)
    {
        SimulatedCombatState combat = context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("恶意缺少分支回合受伤状态。");
        int hitCount = 1;
        if (combat.HasLostHpThisTurn(card.Owner.Creature))
        {
            // Spite's RepeatVar is canonical (2, plus one per upgrade). A few live cards can
            // carry a stale DynamicVarSet after an in-run card-state rewrite; recover this
            // card-specific canonical value instead of failing the whole search.
            hitCount = card.DynamicVars.TryGetValue("Repeat", out DynamicVar? repeat)
                ? repeat.IntValue
                : 2 + card.CurrentUpgradeLevel;
        }
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(hitCount)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
    }

    public static void TheScytheOnPlay(TheScythe card, CardOnPlayMirrorContext context)
    {
        DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .FromCard(card, context.CardPlay)
            .Targeting(context.Target)
            .Simulate(context.Simulator);
        if (context.Simulator.HasPendingChoice)
            return;
        TheScythe mutable = (TheScythe)context.Card.MutablePreview;
        int increase = card.DynamicVars["Increase"].IntValue;
        mutable.IncreasedDamage += increase;
        mutable.CurrentDamage = 13 + mutable.IncreasedDamage;
        if (mutable.DeckVersion != null
            && context.CombatState is SimulatedCombatState combat)
        {
            combat.RecordLongTermResource(increase);
        }
    }

    public static void SacrificeOnPlay(Sacrifice card, CardOnPlayMirrorContext context)
    {
        if (context.State.GetOsty(card.Owner) is not { } osty || !context.State.GetCreature(osty).IsAlive)
            return;
        int block = context.State.GetCreature(osty).MaxHp * 3;
        context.Simulator.Kill(osty, force: true);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Simulator.GainBlock(
            card.Owner.Creature,
            block,
            card.DynamicVars.CalculatedBlock.Props,
            context.Card,
            context.CardPlay);
    }

    public static void SecondWindOnPlay(SecondWind card, CardOnPlayMirrorContext context)
    {
        PredictedCard[] cards = context.OwnerState.Hand.Cards
            .Where(candidate => candidate.Preview.Type != CardType.Attack)
            .ToArray();
        foreach (PredictedCard candidate in cards)
        {
            context.Simulator.Exhaust(candidate);
            if (context.Simulator.HasPendingChoice)
                return;
            context.Simulator.GainBlock(
                card.Owner.Creature,
                card.DynamicVars.Block,
                context.Card,
                context.CardPlay);
            if (context.Simulator.HasPendingChoice)
                return;
        }
    }

    public static void SovereignBladeOnPlay(SovereignBlade card, CardOnPlayMirrorContext context)
    {
        bool allEnemies = GetPowerAmount<SeekingEdgePower>(context, card.Owner.Creature) > 0;
        var attack = DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .WithHitCount(card.DynamicVars.Repeat.IntValue)
            .FromCard(card, context.CardPlay);
        if (allEnemies)
            attack.TargetingAllOpponents(context.CombatState);
        else
            attack.Targeting(context.Target);
        attack.Simulate(context.Simulator);
        if (context.Simulator.HasPendingChoice)
            return;

        int parry = GetPowerAmount<ParryPower>(context, card.Owner.Creature);
        if (parry > 0)
        {
            context.Simulator.GainBlock(
                card.Owner.Creature,
                parry,
                card.DynamicVars.CalculatedBlock.Props,
                context.Card,
                context.CardPlay);
        }
    }

    private static int GetPowerAmount<TPower>(CardOnPlayMirrorContext context, Creature owner)
        where TPower : PowerModel
    {
        if (context.CombatState is ICombatPredictionHookListenerSource source)
        {
            return source.HookListeners.OfType<TPower>()
                .Where(power => power.Owner == owner)
                .Sum(power => power.Amount);
        }
        return owner.GetPowerAmount<TPower>();
    }
}
