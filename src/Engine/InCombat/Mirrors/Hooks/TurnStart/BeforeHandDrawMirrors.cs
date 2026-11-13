using CombatSolver.Engine.Common;
using CombatSolver.Engine.Common.Mirrors;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Dread.Dominions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;

internal static class BeforeHandDrawMirrors
{
    private static readonly MethodMirrorRegistry<AbstractModel, BeforeHandDrawMirrorContext> Registry = Create();
    private static readonly object Gate = new();
    private static bool _sealed;
    internal static void Seal() { lock (Gate) _sealed = true; }
    public static void Register<T>(Action<T, BeforeHandDrawMirrorContext> handler) where T : AbstractModel
    {
        lock (Gate)
        {
            if (_sealed) throw new InvalidOperationException("Register BeforeHandDraw mirrors before root capture.");
            Registry.Register(handler);
        }
    }
    internal static void Invoke(AbstractModel model, BeforeHandDrawMirrorContext context)
    {
        Seal();
        if (Registry.Invoke(model, context).Kind == MirrorDispatchKind.Unsupported)
            throw PredictionUnsupportedException.ForContent($"No BeforeHandDraw mirror for {model.GetType().FullName}.", model.GetType());
    }
    private static MethodMirrorRegistry<AbstractModel, BeforeHandDrawMirrorContext> Create()
    {
        var registry = new MethodMirrorRegistry<AbstractModel, BeforeHandDrawMirrorContext>(MirrorMethodSpec.Hook(
            nameof(AbstractModel.BeforeHandDraw), [typeof(Player), typeof(PlayerChoiceContext), typeof(ICombatState)]));
        registry.Register<AeonglassDominionPower>(Power);
        registry.Register<CallOfTheVoidPower>(Power);
        registry.Register<CreativeAiPower>(Power);
        registry.Register<EnnuiPower>(Power);
        registry.Register<ForegoneConclusionPower>(Power);
        registry.Register<HelloWorldPower>(Power);
        registry.Register<InfiniteBladesPower>(Power);
        registry.Register<MythicFormPower>(Power);
        registry.Register<NightmarePower>(Power);
        registry.Register<SentryModePower>(Power);
        registry.Register<SpectrumShiftPower>(Power);
        registry.Register<BlessedAntler>(Relic);
        registry.Register<BizarreLamp>(Relic);
        registry.Register<FuneraryMask>(Relic);
        registry.Register<JeweledMask>(Relic);
        registry.Register<NinjaScroll>(Relic);
        registry.Register<Ocarina>(Relic);
        registry.Register<Pendulum>(Relic);
        registry.Register<PollinousCore>(Relic);
        registry.Register<RadiantPearl>(Relic);
        registry.Register<Toolbox>(Relic);
        registry.Register<Bolas>(Card);
        registry.Register<Bounce>(Card);
        registry.Register<ThrummingHatchet>(Card);
        registry.Register<KnowledgeDemonDominion>(static (model, context) =>
            KnowledgeDemonChoiceSupport.ResolveDominion(context.Simulator, context.Branch, context.Player, context.Choices, model.Id.Entry));
        return registry;
    }
    private static void Power(PowerModel model, BeforeHandDrawMirrorContext context)
        => TurnStartPowerSupport.ApplyBeforeHandDrawPower(context.Simulator, context.Branch, context.Player, context.Choices, model);
    private static void Relic(RelicModel model, BeforeHandDrawMirrorContext context)
    {
        if (model.Owner == context.Player)
            context.Branch.ApplyRelicBeforeHandDraw(context.Simulator, context.Player, context.Choices, model);
    }
    private static void Card(CardModel model, BeforeHandDrawMirrorContext context)
    {
        if (model.Owner == context.Player)
            context.Branch.ApplyReturningCardBeforeHandDraw(context.Simulator, context.Player, context.Choices, model);
    }
}

internal sealed class BeforeHandDrawMirrorContext : CombatMirrorContext
{
    public required Player Player { get; init; }
    public required TurnStartChoiceCursor Choices { get; init; }
    internal SimulatedCombatState Branch => (SimulatedCombatState)CombatState;
}
