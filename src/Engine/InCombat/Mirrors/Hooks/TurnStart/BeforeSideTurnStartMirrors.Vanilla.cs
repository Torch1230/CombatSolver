using CombatSolver.Engine.Common.Mirrors;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.TurnStart;

internal static partial class BeforeSideTurnStartMirrors
{
    private static partial void RegisterVanilla(MethodMirrorRegistry<AbstractModel, BeforeSideTurnStartMirrorContext> registry)
    {
        registry.Register<BeatingRemnant>(ApplyRelic);
        registry.Register<BrilliantScarf>(ApplyRelic);
        registry.Register<DemonTongue>(ApplyRelic);
        registry.Register<Kunai>(ApplyRelic);
        registry.Register<MusicBox>(ApplyRelic);
        registry.Register<OrnamentalFan>(ApplyRelic);
        registry.Register<RainbowRing>(ApplyRelic);
        registry.Register<Regalite>(ApplyRelic);
        registry.Register<Shuriken>(ApplyRelic);
        registry.Register<VelvetChoker>(ApplyRelic);
        registry.Register<Pocketwatch>(ApplyRelic);
        registry.Register<BagOfMarbles>(ApplyRelic);
        registry.Register<CrackedCore>(ApplyRelic);
        registry.Register<MiniRegent>(ApplyRelic);
        registry.Register<RedMask>(ApplyRelic);
        registry.Register<PlatingPower>(ApplyPower);
        registry.Register<AggressionPower>(ApplyPower);
        registry.Register<HardenedShellPower>(ApplyPower);
        registry.Register<SlothPower>(ApplyPower);
        registry.Register<VoidFormPower>(ApplyPower);
        // 沿用覆盖目录的原版合同：演出/显示刷新没有预测状态写入。
        registry.RegisterIgnored<EchoFormPower>();
        registry.RegisterIgnored<PaelsFlesh>();
        registry.RegisterIgnored<RippleBasin>();
        // 回合末已消费的格挡标记；不在分支中维护 live 的显示保护标记。
        registry.RegisterIgnored<Orichalcum>();
        registry.RegisterIgnored<FakeOrichalcum>();
        // 仅首次玩家回合生效，已在首个可搜索根前由原生执行并捕获（含 RNG）。
        registry.RegisterIgnored<PowerCell>();
        registry.RegisterIgnored<TwistedFunnel>();
    }

    private static void ApplyRelic(RelicModel relic, BeforeSideTurnStartMirrorContext context)
    {
        if (!relic.IsMelted && context.Participants.Contains(relic.Owner.Creature))
            RequireCombat(context).PrepareRelicBeforeSideTurnStart(context.Simulator, relic);
    }

    private static void ApplyPower(PowerModel power, BeforeSideTurnStartMirrorContext context)
        => TurnStartPowerSupport.ApplyBeforeSideTurnStartPower(context.Simulator,
            RequireCombat(context), power, context.Participants);

    private static SimulatedCombatState RequireCombat(BeforeSideTurnStartMirrorContext context)
        => context.CombatState as SimulatedCombatState
            ?? throw new InvalidOperationException("BeforeSideTurnStart requires branch combat state.");
}
