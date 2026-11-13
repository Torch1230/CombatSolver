using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class FoolArrowCardMirrors
{
    internal static void Blossom(BlossomShot card, CardOnPlayMirrorContext c)
    {
        var sim = c.Simulator;
        if (c.Target is null || sim.State.GetCreature(c.Target).IsDead) return;
        var arrow = sim.ShootArrow(card.Owner, c.Card);
        if (sim.HasPendingChoice || arrow is null || sim.State.GetCreature(c.Target).IsDead) return;
        var attack = DamageCmd.Attack(card.DynamicVars.Damage.BaseValue).FromCard(card, c.CardPlay)
            .FromArrow().Targeting(c.Target);
        sim.ExecuteAttack(attack);
        if (sim.HasPendingChoice) return;
        int damage = attack.Results.SelectMany(hit => hit).Sum(result => result.TotalDamage + result.OverkillDamage);
        ((SimulatedCombatState)c.CombatState).ApplyPowerFromSource(typeof(StretchPower), card.Owner.Creature,
            damage, card.Owner.Creature, card);
        if (!sim.HasPendingChoice) sim.ImpaleArrow(c.Target, arrow, c.Card, c.Card);
    }

    internal static void Rain(RainShot card, CardOnPlayMirrorContext c)
    {
        var sim = c.Simulator;
        var combat = (SimulatedCombatState)c.CombatState;
        if (c.Target is null || sim.State.GetCreature(c.Target).IsDead) return;
        var arrow = sim.ShootArrow(card.Owner, c.Card);
        if (sim.HasPendingChoice || arrow is null) return;
        var attack = sim.BeginAttackContext(c.Card, c.CardPlay);
        bool completed = false;
        try
        {
            if (sim.HasPendingChoice) return;
            if (sim.State.GetCreature(c.Target).IsAlive)
            {
                var results = sim.Damage([c.Target], card.DynamicVars.Damage.BaseValue,
                    ValueProp.Move | ValueProp.FromArrow, card.Owner.Creature, c.Card, c.CardPlay);
                if (sim.HasPendingChoice) return;
                sim.AddAttackContextHit(attack, results);
                if (combat.GetPower<AimPower>(card.Owner.Creature) is { } aim) combat.SetPowerAmount(aim, 0);
                if (combat.GetPower<QuietusPower>(card.Owner.Creature) is { } quietus) combat.SetPowerAmount(quietus, 0);
                if (results.FirstOrDefault() is { } result)
                {
                    var targets = combat.GetTeammatesOf(result.Receiver)
                        .Where(target => target != c.Target && sim.State.IsHittable(target)).ToArray();
                    if (targets.Length > 0)
                    {
                        var spread = sim.Damage(targets, result.TotalDamage + result.OverkillDamage,
                            ValueProp.Move, card.Owner.Creature, c.Card, c.CardPlay, ModifyDamageHookType.Cap);
                        if (sim.HasPendingChoice) return;
                        sim.AddAttackContextHit(attack, spread);
                    }
                }
                sim.ImpaleArrow(c.Target, arrow, c.Card, c.Card);
                if (sim.HasPendingChoice) return;
            }
            completed = true;
        }
        finally { sim.EndAttackContext(attack, completed); }
    }

    internal static void Shoot(CardModel card, CardOnPlayMirrorContext context)
    {
        var combat = (SimulatedCombatState)context.CombatState;
        int hits = card switch
        {
            RapidShot => card.DynamicVars.HitCount.IntValue,
            RepeatShot => context.Card.ResolveEnergyXValue(context.State),
            MelodicShot => card.DynamicVars.CalculationBase.IntValue
                + card.DynamicVars.CalculationExtra.IntValue * combat.MelodyCount(card.Owner),
            _ => 1
        };
        if (hits <= 0) return;
        decimal damage = card is TheShot theShot ? theShot.DynamicVars.CalculationBase.BaseValue
            + theShot.DynamicVars.ExtraDamage.BaseValue * theShot._cardsExhaustedByFlorescence : card.DynamicVars.Damage.BaseValue;
        var attack = DamageCmd.Attack(damage).FromArrow().FromCard(card, context.CardPlay)
            .WithHitCount(hits).Targeting(context.Target);
        bool shot = context.Simulator.ShootArrowAt(context.Target!, card.Owner, context.Card, attack);
        if (context.Simulator.HasPendingChoice) return;
        if (shot && card is HobblingShot)
        {
            combat.ApplyPowerFromSource(typeof(WeakPower), context.Target!, card.DynamicVars["Power"].IntValue, card.Owner.Creature, card);
            combat.ApplyPowerFromSource(typeof(VulnerablePower), context.Target!, card.DynamicVars["Power"].IntValue, card.Owner.Creature, card);
        }
    }

    internal static void Nock(Nock card, CardOnPlayMirrorContext context)
        => ((SimulatedCombatState)context.CombatState).ApplyPowerFromSource(typeof(AimPower), card.Owner.Creature,
            card.DynamicVars["AimPower"].IntValue, card.Owner.Creature, card);

    internal static void Quiver(Quiver card, CardOnPlayMirrorContext context)
    {
        for (int index = 0; index < card.DynamicVars.Repeat.BaseValue; index++)
            context.OwnerState.GainArrowSlot();
    }
}
