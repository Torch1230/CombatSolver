using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Enchantments.OnPlay;

using Registry = MethodMirrorRegistry<EnchantmentModel, EnchantmentOnPlayMirrorContext>;

// Simulation-facing facade and central registration index for mirrored EnchantmentModel.OnPlay behavior.
internal static class EnchantmentOnPlayMirrors
{
    private static readonly MirrorMethodSpec OnPlay = new(
        typeof(EnchantmentModel),
        nameof(EnchantmentModel.OnPlay),
        BindingFlags.Instance | BindingFlags.Public,
        [typeof(PlayerChoiceContext), typeof(CardPlay)]);

    private static readonly Registry Registry = CreateRegistry();

    public static MirrorDispatchResult Invoke(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        CardPlay cardPlay,
        EnchantmentModel enchantment)
    {
        return Registry.Invoke(enchantment, new EnchantmentOnPlayMirrorContext
        {
            Simulator = simulator,
            Card = card,
            CardPlay = cardPlay
        });
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(OnPlay);

        registry.Register<Adroit>(HandleAdroit);
        registry.Register<Corrupted>(HandleCorrupted);
        registry.Register<Inky>(HandleInky);
        registry.Register<Momentum>(HandleMomentum);
        registry.Register<Sown>(HandleSown);
        registry.Register<Swift>(HandleSwift);

        return registry;
    }

    private static void HandleAdroit(Adroit enchantment, EnchantmentOnPlayMirrorContext context)
    {
        context.Simulator.GainBlock(
            context.PreviewCard.Owner.Creature,
            enchantment.DynamicVars.Block,
            context.Card,
            context.CardPlay);
    }

    private static void HandleCorrupted(Corrupted enchantment, EnchantmentOnPlayMirrorContext context)
    {
        var owner = context.PreviewCard.Owner.Creature;
        context.Simulator.Damage(
            [owner],
            2m,
            DamageProps.cardHpLoss,
            owner,
            context.Card,
            context.CardPlay);
    }

    private static void HandleInky(Inky enchantment, EnchantmentOnPlayMirrorContext context)
    {
        if (context.CombatState is not ICombatPredictionEffectSink effects)
            throw new InvalidOperationException("墨染附魔缺少可写的预测状态。");
        IEnumerable<Creature> targets = context.Simulator.GetTargetType(context.Card) == TargetType.AllEnemies
            ? context.State.HittableEnemies
            : context.CardPlay.Target is { } selectedTarget ? [selectedTarget] : [];
        foreach (Creature target in targets)
        {
            effects.ApplyPowerFromSource(
                typeof(WeakPower),
                target,
                enchantment.DynamicVars.Weak.IntValue,
                context.PreviewCard.Owner.Creature,
                context.PreviewCard);
        }
    }

    private static void HandleMomentum(Momentum enchantment, EnchantmentOnPlayMirrorContext context)
    {
        enchantment._extraDamage += enchantment.Amount;
    }

    private static void HandleSown(Sown enchantment, EnchantmentOnPlayMirrorContext context)
    {
        if (enchantment.Status != EnchantmentStatus.Normal)
        {
            return;
        }

        context.Simulator.GainEnergy(context.PreviewCard.Owner, enchantment.Amount);
    }

    private static void HandleSwift(Swift enchantment, EnchantmentOnPlayMirrorContext context)
    {
        if (enchantment.Status != EnchantmentStatus.Normal)
        {
            return;
        }

        context.Simulator.Draw(context.PreviewCard.Owner, enchantment.Amount);
    }
}
