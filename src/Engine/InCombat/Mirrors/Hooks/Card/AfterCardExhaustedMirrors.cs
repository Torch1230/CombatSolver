using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Achievements;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, AfterCardExhaustedMirrorContext>;

// Mirrors the prediction-relevant parts of Hook.AfterCardExhausted.
internal static class AfterCardExhaustedMirrors
{
    private static readonly MirrorMethodSpec AfterCardExhausted = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterCardExhausted),
        [
            typeof(PlayerChoiceContext),
            typeof(CardModel),
            typeof(bool)
        ]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, AfterCardExhaustedMirrorContext context)
    {
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterCardExhausted);

        registry.Register<BurningSticks>(HandleBurningSticks);
        registry.Register<CharonsAshes>(HandleCharonsAshes);
        registry.Register<DarkEmbracePower>(HandleDarkEmbracePower);
        registry.Register<DrumOfBattle>(HandleDrumOfBattle);
        registry.Register<FeelNoPainPower>(HandleFeelNoPainPower);
        registry.Register<ForgottenSoul>(HandleForgottenSoul);
        registry.Register<JossPaper>(HandleJossPaper);
        registry.Register<Midnight>(HandleMidnight);
        registry.RegisterIgnored<SkillIronclad1Achievement>();

        return registry;
    }

    private static void HandleBurningSticks(BurningSticks relic, AfterCardExhaustedMirrorContext context)
    {
        if (context.PreviewCard.Owner == relic.Owner && context.PreviewCard.Type == CardType.Skill)
        {
            var state = context.StateStore.Get(relic, () => new BurningSticksPredictionState(relic));
            if (!state.WasUsedThisCombat)
            {
                context.Simulator.AddGeneratedCardToCombat(
                    context.Card.CreateClone(),
                    PileType.Hand,
                    relic.Owner,
                    resultKind: CardGenerationResultKind.Contextual);
                if (context.Simulator.HasPendingChoice)
                    return;
                state.WasUsedThisCombat = true;
                context.Simulator.RecordRelicTrigger(relic, "：复制到手牌");
            }
        }
    }

    private static void HandleDarkEmbracePower(DarkEmbracePower power, AfterCardExhaustedMirrorContext context)
    {
        if (context.PreviewCard.Owner.Creature == power.Owner && power.Owner.Player is { } player)
        {
            if (context.CausedByEthereal)
            {
                // Ethereal exhaust only records the count here in vanilla; the actual draw happens
                // later in end-turn cleanup, which this simulation path does not include.
            }
            else
            {
                context.Simulator.Draw(player, power.Amount);
            }
        }
    }

    private static void HandleDrumOfBattle(DrumOfBattle card, AfterCardExhaustedMirrorContext context)
    {
        if (context.Card.References(card))
        {
            var playCount = context.Card.GeneratePlayCount(context.Simulator, target: null);
            for (var i = 0; i < playCount; i++)
            {
                context.Simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.BaseValue);
            }
        }
    }

    private static void HandleJossPaper(JossPaper relic, AfterCardExhaustedMirrorContext context)
    {
        if (context.PreviewCard.Owner == relic.Owner)
        {
            if (context.CausedByEthereal)
            {
                // Ethereal exhaust only records the count here in vanilla; the actual draw happens
                // later in end-turn cleanup, which this simulation path does not include.
            }
            else
            {
                var state = context.StateStore.Get(relic, () => new JossPaperPredictionState(relic));
                var threshold = relic.DynamicVars[JossPaper._exhaustAmountKey].IntValue;

                state.CardsExhausted++;
                if (state.CardsExhausted >= threshold)
                {
                    int draws = state.CardsExhausted / threshold;
                    context.Simulator.Draw(relic.Owner, draws);
                    if (context.Simulator.HasPendingChoice)
                        return;
                    state.CardsExhausted %= threshold;
                    if (context.Simulator.IsRecordingActionRelicTriggers)
                        context.Simulator.RecordRelicTrigger(relic, $"：抽{draws}");
                }
            }
        }
    }

    private static void HandleCharonsAshes(CharonsAshes relic, AfterCardExhaustedMirrorContext context)
    {
        if (relic.Owner == context.PreviewCard.Owner)
        {
            context.Simulator.Damage(context.State.HittableEnemies, relic.DynamicVars.Damage, relic.Owner.Creature);
            if (context.Simulator.HasPendingChoice)
                return;
            if (context.Simulator.IsRecordingActionRelicTriggers)
                context.Simulator.RecordRelicTrigger(relic, $"：全体伤害{relic.DynamicVars.Damage.IntValue}");
        }
    }

    private static void HandleForgottenSoul(ForgottenSoul relic, AfterCardExhaustedMirrorContext context)
    {
        if (relic.Owner == context.PreviewCard.Owner)
        {
            var target = context.Rng.CombatTargets.NextItem(context.State.HittableEnemies);
            if (target is not null)
            {
                context.Simulator.Damage(target, relic.DynamicVars.Damage, relic.Owner.Creature);
                if (context.Simulator.HasPendingChoice)
                    return;
                if (context.Simulator.IsRecordingActionRelicTriggers)
                    context.Simulator.RecordRelicTrigger(relic, $"：伤害{relic.DynamicVars.Damage.IntValue}");
            }
        }
    }

    private static void HandleFeelNoPainPower(FeelNoPainPower power, AfterCardExhaustedMirrorContext context)
    {
        if (power.Owner == context.PreviewCard.Owner.Creature)
        {
            context.Simulator.GainBlock(power.Owner, power.Amount, ValueProp.Unpowered);
        }
    }

    private static void HandleMidnight(Midnight card, AfterCardExhaustedMirrorContext context)
    {
        // StS2 v0.108.0 added Midnight's global exhaust listener; mutate only the predicted instance.
        context.State.FindCard(card)?.MutablePreview.EnergyCost.AddThisCombat(-1);
    }
}

internal sealed class BurningSticksPredictionState(BurningSticks relic) : IPredictionStateForkable
{
    public bool WasUsedThisCombat { get; set; } = relic.WasUsedThisCombat;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class JossPaperPredictionState(JossPaper relic) : IPredictionStateForkable
{
    public int CardsExhausted { get; set; } = relic.CardsExhausted;

    public object Fork(PredictionForkContext context) => MemberwiseClone();
}

internal sealed class AfterCardExhaustedMirrorContext : CombatCardMirrorContext
{
    public required bool CausedByEthereal { get; init; }
}
