using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// 一个被登记的战略效果挂在谁身上。
///
/// 绝大多数增益挂在玩家身上，收益也归玩家，那是 <see cref="Player"/>。少数效果挂在**敌人**
/// 身上而收益归玩家——观者的以手拒之就是这样：反弹格挡这层 Power 施加在敌人身上，玩家每打中
/// 它一次就起一次甲。这类效果在"只看玩家身上的增益"那一圈里会被整个跳过，所以需要单独说明。
/// </summary>
internal enum StrategicEffectHost
{
    /// <summary>挂在玩家身上，并且是增益（<see cref="PowerType.Buff"/>）。</summary>
    Player,

    /// <summary>挂在敌人身上，收益归玩家。不检查增益/减益——它在宿主眼里通常是减益。</summary>
    Enemy,
}

/// <summary>
/// 第三方 Power 的战略估值登记表。
///
/// <see cref="StrategicEffectModel"/> 的 <c>Requirements</c> 和 <c>Evaluate</c> 是按原版 Power
/// 类型写死的 <c>switch</c>，第三方类型全部落进默认分支、按叠加层数记一点 Scaling。对大多数
/// mod 的 Power 来说这个兜底够用，但对"改变玩家该按什么顺序出牌"的效果不够：
/// <c>ClassifyActionOptionFamilies</c> 判一张牌算不算 <c>ImmediateDefense</c>，四个判据里唯一
/// 能被 Power 影响的就是 <c>StrategicEffects.PreventionPotential</c>。这一项不动，一张自己不
/// 给甲的防御牌会被归成纯进攻牌，在族内代表里被伤害更高的攻击牌压掉，只能排到攻击后面——
/// 而它的收益恰恰依赖排在攻击前面。
///
/// 所以这里开一个登记点。登记之后：
/// <list type="bullet">
/// <item>估值走登记的委托，不再落默认分支；</item>
/// <item><see cref="StrategicEffectHost.Enemy"/> 的类型会被"只看玩家身上的增益"那一圈放行。</item>
/// </list>
///
/// 求解器本身不认识任何第三方类型，登记由 mod 在加载时自己做。
/// </summary>
internal static class StrategicEffectMirrors
{
    private readonly record struct Entry(
        StrategicEffectRequirements Requirements,
        Func<PowerModel, StrategicEffectContext, StrategicEffectVector> Evaluate,
        StrategicEffectHost Host);

    private static readonly Dictionary<Type, Entry> Registry = [];

    /// <summary>
    /// 为一个 Power 类型登记战略估值。
    /// </summary>
    /// <param name="requirements">
    /// 估值要读 <see cref="StrategicEffectContext"/> 的哪几项。只有被要求的项才会被算出来，
    /// 没要求的留在便宜的默认值上，所以这里要如实填，多填浪费、少填读到的是默认值。
    /// </param>
    /// <param name="evaluate">
    /// 由这一层 Power 得出一个 <see cref="StrategicEffectVector"/>。用
    /// <c>StrategicEffectVector.Prevention</c> 这一组工厂方法构造，不要自己拼字段——
    /// 那几个方法带着各自的上限逻辑。
    /// </param>
    /// <param name="host">这层 Power 挂在谁身上，见 <see cref="StrategicEffectHost"/>。</param>
    public static void Register<TPower>(
        StrategicEffectRequirements requirements,
        Func<PowerModel, StrategicEffectContext, StrategicEffectVector> evaluate,
        StrategicEffectHost host = StrategicEffectHost.Player)
        where TPower : PowerModel
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        Registry[typeof(TPower)] = new Entry(requirements, evaluate, host);
    }

    /// <summary>登记表是否为空。空表时两处热路径可以整段跳过字典查找。</summary>
    public static bool IsEmpty => Registry.Count == 0;

    /// <summary>
    /// 这层 Power 该不该进战略估值。
    ///
    /// 原版规则：挂在玩家身上、层数为正、当前层数下是增益、不是临时 Power。
    /// 登记为 <see cref="StrategicEffectHost.Enemy"/> 的类型换一套：挂在**别人**身上、层数为正、
    /// 不是临时 Power。不查增益/减益——它在敌人眼里是减益，查了就永远进不来。
    /// </summary>
    public static bool Contributes(PowerModel power, Creature playerCreature)
    {
        if (power.Amount <= 0 || power is ITemporaryPower)
            return false;
        bool onPlayer = ReferenceEquals(power.Owner, playerCreature);
        if (Registry.Count > 0
            && Registry.TryGetValue(power.GetType(), out Entry entry)
            && entry.Host == StrategicEffectHost.Enemy)
        {
            return !onPlayer;
        }
        return onPlayer && power.TypeForCurrentAmount == PowerType.Buff;
    }

    public static bool TryGetRequirements(
        PowerModel power,
        out StrategicEffectRequirements requirements)
    {
        if (Registry.Count > 0 && Registry.TryGetValue(power.GetType(), out Entry entry))
        {
            requirements = entry.Requirements;
            return true;
        }
        requirements = StrategicEffectRequirements.None;
        return false;
    }

    public static bool TryEvaluate(
        PowerModel power,
        StrategicEffectContext context,
        out StrategicEffectVector effect)
    {
        if (Registry.Count > 0 && Registry.TryGetValue(power.GetType(), out Entry entry))
        {
            effect = entry.Evaluate(power, context);
            return true;
        }
        effect = StrategicEffectVector.Zero;
        return false;
    }
}
