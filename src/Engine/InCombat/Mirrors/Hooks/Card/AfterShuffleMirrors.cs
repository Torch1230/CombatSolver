using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;

using Registry = MethodMirrorRegistry<AbstractModel, AfterShuffleMirrorContext>;

internal static class AfterShuffleMirrors
{
    private static readonly MirrorMethodSpec AfterShuffle = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterShuffle),
        [typeof(PlayerChoiceContext), typeof(Player)]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(AbstractModel listener, AfterShuffleMirrorContext context)
    {
        using var dispatch = context.Simulator.BeginExecutionDispatch();
        Registry.Invoke(listener, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterShuffle);

        registry.Register<BiiigHug>(HandleBiiigHug);
        registry.Register<StratagemPower>(HandleStratagemPower);
        registry.Register<TheAbacus>(HandleTheAbacus);

        return registry;
    }

    private static void HandleBiiigHug(BiiigHug relic, AfterShuffleMirrorContext context)
    {
        if (relic.Owner == context.Player)
        {
            context.Simulator.CreateAndAddGeneratedCardsToCombat<Soot>(
                context.Player,
                PileType.Draw,
                1,
                context.Player,
                CardPilePosition.Random);
        }
    }

    private static void HandleStratagemPower(StratagemPower power, AfterShuffleMirrorContext context)
    {
        if (power.Owner.Player == context.Player)
        {
            if (context.CombatState is not ICombatPredictionChoiceSink choices)
                throw new InvalidOperationException("战略选择缺少分支选择接口。");
            _ = choices.ResolvePileChoice(
                context.Simulator,
                power.Id.Entry,
                context.Player,
                PileType.Draw,
                power.Amount);
        }
        context.Simulator.AcknowledgeExecutionDispatch();
    }

    private static void HandleTheAbacus(TheAbacus relic, AfterShuffleMirrorContext context)
    {
        if (relic.Owner == context.Player)
        {
            context.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
        }
    }
}

internal sealed class AfterShuffleMirrorContext : CombatMirrorContext
{
    public required Player Player { get; init; }
}
