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
    public static void ExpectAFightOnPlay(ExpectAFight card, CardOnPlayMirrorContext context)
    {
        context.GainBlock(card.Owner.Creature);
        if (context.Simulator.HasPendingChoice)
            return;
        foreach (PredictedCard attack in context.OwnerState.Hand.Cards.ToArray())
            if (attack.Preview.Type == CardType.Attack && !attack.Preview.EnergyCost.CostsX)
                attack.MutablePreview.EnergyCost.AddUntilPlayed(-card.DynamicVars.Energy.IntValue);
    }

    public static void MementoMoriOnPlay(MementoMori card, CardOnPlayMirrorContext context)
    {
        Creature? target = context.State.HittableEnemies.MinBy(enemy => context.State.GetCreature(enemy).CurrentHp);
        if (target is not null)
            DamageCmd.Attack(card.DynamicVars.Damage.IntValue).FromCard(card, context.CardPlay)
                .Targeting(target).Simulate(context.Simulator);
    }

    public static void AstralPulseOnPlay(AstralPulse card, CardOnPlayMirrorContext context)
        => context.AttackAllOpponents(hitCount: card.DynamicVars.HitCount.IntValue);

    public static void DaggerSprayOnPlay(DaggerSpray card, CardOnPlayMirrorContext context)
        => context.AttackAllOpponents(hitCount: card.DynamicVars.HitCount.IntValue);

    public static void MiseryOnPlay(Misery _, CardOnPlayMirrorContext context)
    {
        SimulatedCombatState combat = context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("Misery requires simulated combat state.");
        Dictionary<PowerModel, int> debuffs = combat.EffectivePowers()
            .Where(power => power.Owner == context.Target
                && power.TypeForCurrentAmount == MegaCrit.Sts2.Core.Entities.Powers.PowerType.Debuff)
            .ToDictionary(power => PredictionUtils.CloneModelForSimulation(power), power => power.Amount);
        foreach (var (power, amount) in debuffs)
        {
            if (power is not ITemporaryPower temporary)
                continue;
            PowerModel? internalPower = debuffs.Keys.FirstOrDefault(candidate => candidate.Id == temporary.InternallyAppliedPower.Id);
            if (internalPower != null)
                debuffs[internalPower] += amount;
        }
        context.AttackSingle();
        if (context.Simulator.HasPendingChoice)
            return;
        foreach (Creature enemy in context.State.HittableEnemies.ToArray())
        {
            if (enemy == context.Target)
                continue;
            foreach (var (power, amount) in debuffs)
            {
                if (amount == 0)
                    continue;
                combat.ApplyClonedPower(power, enemy, amount, power.Applier);
                PowerLifecycleSupport.ResolvePowerAmountChanges(context.Simulator, combat);
                if (context.Simulator.HasPendingChoice)
                    return;
            }
        }
    }

    // Vanilla wraps the whole body in Osty.CheckMissingWithAnim, so the attack and the block are both
    // skipped once the Osty is gone. The sacrifice stays in CardEffectSpecRegistry, which runs after
    // this mirror and is gated on the same condition.
    public static void BoneShardsOnPlay(BoneShards card, CardOnPlayMirrorContext context)
    {
        if (context.State.GetOsty(card.Owner) is not { } osty || context.State.GetCreature(osty).IsDead)
        {
            return;
        }

        DamageCmd.Attack(card.DynamicVars.OstyDamage.BaseValue)
            .FromOsty(osty, card, context.CardPlay)
            .TargetingAllOpponents(context.CombatState)
            .Simulate(context.Simulator);

        if (context.Simulator.HasPendingChoice)
            return;

        context.GainBlock(card.Owner.Creature);
    }

    public static void PactsEndOnPlay(PactsEnd card, CardOnPlayMirrorContext context)
    {
        if (context.OwnerState.ExhaustPile.Cards.Count >= card.DynamicVars.Cards.IntValue)
            context.AttackAllOpponents();
    }

    public static void TwinStrikeOnPlay(TwinStrike card, CardOnPlayMirrorContext context)
        => context.AttackSingle(hitCount: card.DynamicVars.HitCount.IntValue);

    public static void HeavenlyDrillOnPlay(HeavenlyDrill card, CardOnPlayMirrorContext context)
    {
        int hits = context.Card.ResolveEnergyXValue(context.State);
        if (hits >= card.DynamicVars.Energy.IntValue)
            hits *= 2;
        context.AttackSingle(hitCount: hits);
    }

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
        int hitCount = GetPowerAmount<VulnerablePower>(context, context.Target) > 0 ? card.DynamicVars.HitCount.IntValue : 1;
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
            .WithHitCount(card.DynamicVars.HitCount.IntValue)
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
            hitCount = card.DynamicVars.HitCount.IntValue;
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
            combat.RecordGrowthReward(GrowthSource.TheScythe);
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
            .WithHitCount(card.DynamicVars.HitCount.IntValue)
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
