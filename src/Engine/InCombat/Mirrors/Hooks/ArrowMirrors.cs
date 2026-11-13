using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks;

internal static class ArrowMirrors
{
    private static readonly MethodMirrorRegistry<AbstractModel, ArrowMirrorContext> Shot = new(
        MirrorMethodSpec.Hook(nameof(AbstractModel.AfterArrowShot),
            [typeof(Player), typeof(ArrowSlot), typeof(PlayerChoiceContext), typeof(CardModel)]));
    private static readonly MethodMirrorRegistry<AbstractModel, ArrowMirrorContext> Impaled = new(
        MirrorMethodSpec.Hook(nameof(AbstractModel.AfterArrowImpaled),
            [typeof(ImpaledPower), typeof(ArrowSlot), typeof(PlayerChoiceContext), typeof(CardModel)]));
    private static readonly MethodMirrorRegistry<AbstractModel, ArrowMirrorContext> Dislodged = new(
        MirrorMethodSpec.Hook(nameof(AbstractModel.AfterArrowDislodged),
            [typeof(ImpaledPower), typeof(ArrowSlot), typeof(PlayerChoiceContext), typeof(CardModel)]));

    static ArrowMirrors()
    {
        Shot.Register<WhistlingArrows>((card, c) =>
        {
            if (card.Owner != c.Player || c.Arrow is null) return;
            PredictedCard? predicted = c.State.GetPlayerCombatState(card.Owner).FindCard(card);
            if (predicted is not null && c.State.GetPlayerCombatState(card.Owner).DiscardPile.Cards.Contains(predicted))
                c.Simulator.AddToPile(predicted, PileType.Hand);
        });
        Shot.Register<QuietusPower>((power, c) =>
        {
            if (c.Arrow is not null && c.Source is not null && c.Player.Creature == power.Owner)
                c.Arrow.DestroyAfterImpaled = true;
        });
        Shot.Register<CoveringFirePower>((power, c) =>
        {
            if (c.Player.Creature != power.Owner || c.Source is null) return;
            foreach (var ally in c.CombatState.GetTeammatesOf(power.Owner))
                if (ally.IsPlayer && ally != power.Owner && c.State.GetCreature(ally).IsAlive)
                    c.Simulator.GainBlock(ally, power.Amount, ValueProp.Unpowered);
        });
        Impaled.Register<ExplosiveArrowsPower>((power, c) =>
        {
            if (c.Impaled!.Arrows.FirstOrDefault()?.owner.Creature == power.Owner)
                c.Simulator.Damage(c.State.HittableEnemies.ToArray(), power.Amount, ValueProp.Unpowered, power.Owner);
        });
        Dislodged.Register<EpicRetrieval>((card, c) =>
        {
            if (c.Impaled!.Applier == card.Owner.Creature && c.Source?.References(card) == true)
                c.Simulator.GainEnergy(card.Owner, card.DynamicVars.Energy.IntValue);
        });
        Dislodged.Register<Twine>((relic, c) =>
        {
            if (c.Arrow!.owner == relic.Owner)
                c.Simulator.GainBlock(relic.Owner.Creature, relic.DynamicVars.Block);
        });
        Dislodged.Register<YahPower>((power, c) =>
        {
            if (c.Arrow!.owner.Creature != power.Owner) return;
            var data = power.GetInternalData<YahPower.Data>();
            if (data.triggeredThisTurn) return;
            data.triggeredThisTurn = true;
            c.Simulator.GainEnergy(power.Owner.Player!, power.Amount);
        });
    }

    public static void AfterShot(ArrowMirrorContext context) => Invoke(Shot, context);
    public static void AfterImpaled(ArrowMirrorContext context) => Invoke(Impaled, context);
    public static void AfterDislodged(ArrowMirrorContext context) => Invoke(Dislodged, context);

    private static void Invoke(MethodMirrorRegistry<AbstractModel, ArrowMirrorContext> registry, ArrowMirrorContext context)
        => HookMirrors.InvokeArrowHooks(registry, context);
}

internal sealed class ArrowMirrorContext : CombatMirrorContext
{
    public required Player Player { get; init; }
    public required ArrowSlot? Arrow { get; init; }
    public ImpaledPower? Impaled { get; init; }
    public PredictedCard? Source { get; init; }
}
