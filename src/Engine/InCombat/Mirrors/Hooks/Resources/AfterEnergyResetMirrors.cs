using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.Common.Mirrors;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver.Engine.InCombat.Mirrors.Hooks.Resources;

using Registry = MethodMirrorRegistry<PowerModel, AfterEnergyResetMirrorContext>;

/// <summary>
/// 回合开始重置能量之后，能力那一轮结算。对应 <c>Hook.AfterEnergyReset</c> 的能力部分。
/// </summary>
/// <remarks>
/// <para>
/// 这一份原来是 <c>PersistentPowerSupport.TriggerAfterEnergyReset</c> 里的一个 switch，判据是写死的
/// 五个原版类型。写死的判据有两个后果：第三方能力没有登记的地方；而且**没登记也不报**——
/// 别的钩子走 <see cref="MethodMirrorRegistry{TBase, TContext}" />，重写了却没登记会记一条
/// <c>MethodNotMirrored</c> 风险，这里因为不经过注册表，只是静默跳过。
/// </para>
/// <para>
/// 发现它是因为一个真实的 mod：观者的斋戒挂 <c>EnergyDownPower</c>，重写 <c>AfterEnergyReset</c>
/// 去扣一点能量。求解器从此每回合都以为自己多一点能量，执行下来和预测对不上，于是每回合重算，
/// 而路线上没有任何提示说哪里算错了。
/// </para>
/// <para>
/// 改成注册表之后：原版那五个照原样登记成 handler，行为逐位不变；
/// <see cref="EnergyNextTurnPower" /> 登记成 Ignored——它的能量是在回合开始算能量时由
/// <c>ConsumeEnergyNextTurn</c> 一次性取走的，在这里再结算一次就是重复计数；
/// 其余任何重写了这个钩子的类型，登记过就按登记的算，没登记就记一条风险，不再静默。
/// </para>
/// </remarks>
internal static class AfterEnergyResetMirrors
{
    private static readonly MirrorMethodSpec AfterEnergyReset = MirrorMethodSpec.Hook(
        nameof(AbstractModel.AfterEnergyReset),
        [typeof(Player)]);

    private static readonly Registry Registry = CreateRegistry();

    public static void Invoke(PowerModel power, AfterEnergyResetMirrorContext context)
    {
        Registry.Invoke(power, context);
    }

    private static Registry CreateRegistry()
    {
        var registry = new Registry(AfterEnergyReset);

        registry.Register<GenesisPower>(HandleGenesisPower);
        registry.Register<LightningRodPower>(HandleLightningRodPower);
        registry.Register<RadiancePower>(HandleRadiancePower);
        registry.Register<SpinnerPower>(HandleSpinnerPower);
        registry.Register<StarNextTurnPower>(HandleStarNextTurnPower);

        // 回合开始算能量的时候已经用 ConsumeEnergyNextTurn 取走并清零了，这里再给一次就是双份。
        registry.RegisterIgnored<EnergyNextTurnPower>();

        return registry;
    }

    private static void HandleGenesisPower(GenesisPower power, AfterEnergyResetMirrorContext context)
    {
        context.Simulator.GainStars(context.Player, power.Amount);
    }

    private static void HandleLightningRodPower(LightningRodPower power, AfterEnergyResetMirrorContext context)
    {
        context.Simulator.OrbChannel<LightningOrb>(context.Player);
        // 起了选择就停在这里：调用方看见 HasPendingChoice 会中止整轮，层数留到下次重新结算。
        if (context.Simulator.HasPendingChoice)
            return;
        context.Combat.SetAmount<LightningRodPower>(context.Player.Creature, power.Amount - 1);
    }

    private static void HandleRadiancePower(RadiancePower power, AfterEnergyResetMirrorContext context)
    {
        if (context.Combat.GetAmount<NoEnergyGainPower>(context.Player.Creature) <= 0)
        {
            context.Simulator.State.GetPlayerCombatState(context.Player)
                .GainEnergy(power.DynamicVars.Energy.IntValue);
        }
        context.Combat.SetAmount<RadiancePower>(context.Player.Creature, power.Amount - 1);
    }

    private static void HandleSpinnerPower(SpinnerPower power, AfterEnergyResetMirrorContext context)
    {
        context.Simulator.OrbChannel<GlassOrb>(context.Player, power.Amount);
    }

    private static void HandleStarNextTurnPower(StarNextTurnPower power, AfterEnergyResetMirrorContext context)
    {
        context.Simulator.GainStars(context.Player, power.Amount);
        if (context.Simulator.HasPendingChoice)
            return;
        context.Combat.SetAmount<StarNextTurnPower>(context.Player.Creature, 0);
    }
}

internal sealed class AfterEnergyResetMirrorContext : CombatMirrorContext<PowerModel>
{
    /// <summary>刚刚重置过能量的那名玩家。原版每个重写第一件事都是拿它和自己的主人比对。</summary>
    public required Player Player { get; init; }

    public SimulatedCombatState Combat => CombatState as SimulatedCombatState
        ?? throw new InvalidOperationException("回合开始的能力结算缺少分支回合状态。");
}
