using CombatSolver.Engine.Common.Mirrors;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;

internal static partial class AfterPlayerTurnStartMirrors
{
    private static partial void RegisterVanilla(
        MethodMirrorRegistry<AbstractModel, AfterPlayerTurnStartMirrorContext> registry, string hook)
    {
        if (hook == nameof(AbstractModel.AfterPlayerTurnStartLate))
        {
            registry.Register<BloodVial>(ApplyRelic);
            registry.Register<FakeBloodVial>(ApplyRelic);
        }
        else if (hook == nameof(AbstractModel.AfterPlayerTurnStart))
        {
            registry.Register<EntropyPower>(ApplyPower);
            registry.Register<CrimsonMantlePower>(ApplyPower);
            registry.Register<HibernatePower>(ApplyPower);
            registry.Register<InfernoPower>(ApplyPower);
            registry.Register<LoopPower>(ApplyPower);
            registry.Register<RollingBoulderPower>(ApplyPower);
            registry.Register<SummonNextTurnPower>(ApplyPower);
            registry.Register<ToolsOfTheTradePower>(ApplyPower);
            registry.Register<TyrannyPower>(ApplyPower);
            registry.Register<Bellows>(ApplyRelic);
            registry.Register<BoneTea>(ApplyRelic);
            registry.Register<ChoicesParadox>(ApplyRelic);
            registry.Register<EmotionChip>(ApplyRelic);
            registry.Register<FestivePopper>(ApplyRelic);
            registry.Register<GamblingChip>(ApplyRelic);
            registry.Register<MercuryHourglass>(ApplyRelic);
            registry.Register<MrStruggles>(ApplyRelic);
            registry.Register<RoyalPoison>(ApplyRelic);
            registry.Register<ToastyMittens>(ApplyRelic);
            registry.Register<VexingPuzzlebox>(ApplyRelic);
        }
        // 0.111.0 的原版正式模型没有 Early 有效覆写；不把普通或 Late 效果搬到这里。
    }

    private static void ApplyPower(PowerModel power, AfterPlayerTurnStartMirrorContext context)
        => TurnStartPowerSupport.ApplyAfterPlayerTurnStartPower(context.Simulator,
            RequireCombat(context), context.Player, context.Choices, power);

    private static void ApplyRelic(RelicModel relic, AfterPlayerTurnStartMirrorContext context)
    {
        if (ReferenceEquals(relic.Owner, context.Player))
            RequireCombat(context).ApplyRelicAfterPlayerTurnStart(context.Simulator,
                context.Player, context.Choices, relic);
    }

    private static SimulatedCombatState RequireCombat(AfterPlayerTurnStartMirrorContext context)
        => context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("AfterPlayerTurnStart requires branch combat state.");
}
