using System.Diagnostics.CodeAnalysis;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay;

internal static class GeneralCardMirrors
{
    /// <summary>
    /// Simulates a general draw of one card for the card's owner.
    /// </summary>
    public static void GeneralOwnerDrawOneOnPlay(CardModel card, CardOnPlayMirrorContext context)
    {
        context.Simulator.Draw(card.Owner, 1);
    }

    /// <summary>
    /// Simulates an unconditional draw based on the card's <c>Cards</c> dynamic var for the card's owner.
    /// </summary>
    public static void GeneralOwnerDrawOnPlay(CardModel card, CardOnPlayMirrorContext context)
    {
        if (!card.DynamicVars.TryGetValue("Cards", out var cardsVar))
        {
            EngineDiagnostics.Warn($"Card {card.Id} has no cards var to simulate a draw.");
            context.History.RecordRisk(PredictionRiskReason.MethodMirrorIncomplete);
            return;
        }

        context.Simulator.Draw(card.Owner, context.Calculate(cardsVar));
    }

    /// <summary>
    /// Simulates a general attack when a card is played.
    /// </summary>
    /// <remarks>
    /// Targeting examples:
    /// <list type="bullet">
    /// <item><see cref="StrikeIronclad"/> targets any enemy.</item>
    /// <item><see cref="Breakthrough"/> targets all enemies.</item>
    /// <item><see cref="SwordBoomerang"/> targets random enemies.</item>
    /// </list>
    /// </remarks>
    public static void GeneralAttackOnPlay(CardModel card, CardOnPlayMirrorContext context)
    {
        if (!TryGetDynamicVar(card, ["CalculatedDamage", "Damage", "OstyDamage"], out var damage))
        {
            EngineDiagnostics.Warn($"Card {card.Id} has no damage var to simulate an attack command.");
            context.History.RecordRisk(PredictionRiskReason.MethodMirrorIncomplete);
            return;
        }

        var command = damage is CalculatedDamageVar calculatedDamageVar
            ? DamageCmd.Attack(calculatedDamageVar)
            : DamageCmd.Attack(context.Calculate(damage));

        if (card.Tags.Contains(CardTag.OstyAttack))
        {
            if (context.State.GetOsty(card.Owner) is not { } osty || !context.State.GetCreature(osty).IsAlive)
            {
                return;
            }

            command.FromOsty(osty, card, context.CardPlay);
        }
        else
        {
            command.FromCard(card, context.CardPlay);
        }

        if (TryGetDynamicVar(card, ["CalculatedHits", "HitCount", "Repeat"], out var repeat))
        {
            command.WithHitCount((int)context.Calculate(repeat));
        }
        else if (card.EnergyCost.CostsX)
        {
            command.WithHitCount(context.Card.ResolveEnergyXValue(context.State));
        }
        else if (card.HasStarCostX)
        {
            command.WithHitCount(context.Card.ResolveStarXValue(context.State));
        }

        TargetType targetType = context.Simulator.GetTargetType(context.Card);
        switch (targetType)
        {
            case TargetType.AnyEnemy:
                command.Targeting(context.Target);
                break;

            case TargetType.AllEnemies:
                command.TargetingAllOpponents(context.CombatState);
                break;

            case TargetType.RandomEnemy:
                command.TargetingRandomOpponents(context.CombatState);
                break;

            default:
                EngineDiagnostics.Warn($"Attack {card.Id} has an unsupported target type: {targetType}");
                context.History.RecordRisk(PredictionRiskReason.MethodMirrorIncomplete);
                return;
        }

        command.Simulate(context.Simulator);
    }

    /// <summary>
    /// Simulates a general block gain when a card is played.
    /// </summary>
    /// <remarks>
    /// Targeting examples:
    /// <list type="bullet">
    /// <item><see cref="DefendIronclad"/> targets self.</item>
    /// <item><see cref="Lift"/> targets any ally.</item>
    /// <item><see cref="Rally"/> targets all allies.</item>
    /// <item>
    /// <see cref="IronWave"/> is a combined attack-and-block card that targets an enemy while its block effect targets
    /// the owner. <see cref="Defy"/> is a debuff Skill that targets an enemy while its block effect targets the owner.
    /// </item>
    /// </list>
    /// </remarks>
    public static void GeneralBlockOnPlay(CardModel card, CardOnPlayMirrorContext context)
    {
        Action<Creature> blockAction;
        if (TryGetDynamicVar(card, ["CalculatedBlock", "Block"], out var block))
        {
            var amount = context.Calculate(block);
            var props = block switch
            {
                CalculatedBlockVar calculatedBlockVar => calculatedBlockVar.Props,
                BlockVar blockVar => blockVar.Props,
                _ => ValueProp.Move
            };
            blockAction = target => context.GainBlock(target, amount, props);
        }
        else
        {
            EngineDiagnostics.Warn($"Card {card.Id} has no block var to simulate a block gain.");
            context.History.RecordRisk(PredictionRiskReason.MethodMirrorIncomplete);
            return;
        }

        switch (card.TargetType)
        {
            case TargetType.Self:
            case TargetType.AnyEnemy or TargetType.AllEnemies or TargetType.RandomEnemy:
                blockAction(card.Owner.Creature);
                break;

            case TargetType.AnyAlly:
                blockAction(context.Target);
                break;

            case TargetType.AllAllies:
                var allies = context.CombatState.GetTeammatesOf(card.Owner.Creature)
                    .Where(creature => creature.IsPlayer && context.State.GetCreature(creature).IsAlive);
                foreach (var ally in allies)
                {
                    blockAction(ally);
                }
                break;

            default:
                EngineDiagnostics.Warn($"Block {card.Id} has an unsupported target type: {card.TargetType}");
                context.History.RecordRisk(PredictionRiskReason.MethodMirrorIncomplete);
                return;
        }
    }

    private static bool TryGetDynamicVar(
        CardModel card,
        IEnumerable<string> candidateKeys,
        [NotNullWhen(true)] out DynamicVar? dynamicVar)
    {
        foreach (var key in candidateKeys)
        {
            if (card.DynamicVars.TryGetValue(key, out dynamicVar))
            {
                return true;
            }
        }

        dynamicVar = null;
        return false;
    }
}
