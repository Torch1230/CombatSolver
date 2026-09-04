using System.Collections.Concurrent;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// 判断一个 ModHelper 订阅器是否与战斗预测无关：它在 <see cref="AbstractModel"/> 上覆写的所有
/// hook 要么只在战斗之外分发（地图生成、进幕、事件抽取、休息处、商店），要么只在战斗开始时分发一次、
/// 早于求解器在回合开始捕获预测根，且求解器从不镜像它们。这样的订阅器不可能改变模拟结果，
/// 不必把整个 Mod 判为不兼容。判定沿继承链向上收集全部覆写（含中间基类与泛型基类），
/// 任何一个落在清单之外的 hook 都视为参与战斗。
/// </summary>
internal static class PredictionModHookSubscriberInertness
{
    /// <summary>
    /// 与战斗预测无关的 hook。前一组只在战斗之外分发，每一项都核对过游戏里的调用点：
    /// 地图与进幕在 RunManager，事件抽取在 ActModel，未知节点房型在 UnknownMapPointOdds，
    /// 休息处在 RestSite 选项，商店在 Merchant 条目与 CardFactory 的商店牌池。
    /// 后两组分别是战斗开始 hook 与战斗结束 hook：前者在 CombatManager.TurnStarted 之前就已经执行完毕，
    /// 效果已经落在被捕获的根状态里（与 KnownPreRootSubscriberTypeNames 同一道理）；后者在胜负判定
    /// 之后才由 CombatRoom 分发，搜索到战斗结束就停止。求解器的镜像从不触发这两组中的任何一个。
    /// 战斗期间也会触发的 hook（例如 AfterRoomEntered、金币、药水、奖励选项、加牌入牌组）刻意不在其中。
    /// </summary>
    private static readonly HashSet<string> PredictionInertHookNames = new(StringComparer.Ordinal)
    {
        nameof(AbstractModel.ModifyGeneratedMap),
        nameof(AbstractModel.ModifyGeneratedMapLate),
        nameof(AbstractModel.AfterMapGenerated),
        nameof(AbstractModel.AfterActEntered),
        nameof(AbstractModel.ModifyNextEvent),
        nameof(AbstractModel.ModifyUnknownMapPointRoomTypes),
        nameof(AbstractModel.ModifyOddsIncreaseForUnrolledRoomType),
        nameof(AbstractModel.AfterRestSiteHeal),
        nameof(AbstractModel.AfterRestSiteSmith),
        nameof(AbstractModel.ModifyRestSiteHealAmount),
        nameof(AbstractModel.TryModifyRestSiteHealRewards),
        nameof(AbstractModel.TryModifyRestSiteOptions),
        nameof(AbstractModel.AfterItemPurchased),
        nameof(AbstractModel.ModifyMerchantCardPool),
        nameof(AbstractModel.ModifyMerchantCardRarity),
        nameof(AbstractModel.ModifyMerchantCardCreationResults),
        nameof(AbstractModel.ModifyMerchantPrice),
        nameof(AbstractModel.BeforeCombatStart),
        nameof(AbstractModel.BeforeCombatStartLate),
        nameof(AbstractModel.AfterCombatVictoryEarly),
        nameof(AbstractModel.AfterCombatVictory),
        nameof(AbstractModel.AfterCombatEnd),
        nameof(AbstractModel.BeforeCombatRewardOffered),
    };

    /// <summary>
    /// <see cref="AbstractModel"/> 上不是 hook 的虚成员。覆写它们不会让订阅器收到任何战斗事件：
    /// <c>ShouldReceiveCombatHooks</c> 只决定战斗 hook 是否投递，投递到一个没有覆写任何战斗 hook 的
    /// 模型上仍是空操作；其余是比较与展示用途。
    /// </summary>
    private static readonly HashSet<string> NonHookMemberNames = new(StringComparer.Ordinal)
    {
        nameof(AbstractModel.ShouldReceiveCombatHooks),
        nameof(AbstractModel.IsMock),
        nameof(AbstractModel.PreviewOutsideOfCombat),
        nameof(AbstractModel.CompareTo),
    };

    private static readonly ConcurrentDictionary<Type, (bool Inert, string Overrides)> Cache = new();

    /// <summary>
    /// 订阅器类型是否与战斗无关。<paramref name="overriddenHooks"/> 返回沿继承链收集到的全部
    /// <see cref="AbstractModel"/> hook 覆写名（逗号分隔，用于日志）。
    /// 反射失败、类型不是 <see cref="AbstractModel"/>、或任一覆写不在清单内，都返回 false。
    /// </summary>
    public static bool IsCombatInert(Type subscriberType, out string overriddenHooks)
    {
        (bool inert, string overrides) = Cache.GetOrAdd(subscriberType, Classify);
        overriddenHooks = overrides;
        return inert;
    }

    private static (bool Inert, string Overrides) Classify(Type subscriberType)
    {
        if (!typeof(AbstractModel).IsAssignableFrom(subscriberType) || subscriberType.IsAbstract)
            return (false, string.Empty);

        SortedSet<string> hooks = new(StringComparer.Ordinal);
        try
        {
            for (Type? type = subscriberType;
                 type is not null && type != typeof(AbstractModel);
                 type = type.BaseType)
            {
                foreach (MethodInfo method in type.GetMethods(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.DeclaredOnly))
                {
                    if (!method.IsVirtual || method.IsAbstract)
                        continue;
                    MethodInfo baseDefinition = method.GetBaseDefinition();
                    if (baseDefinition.DeclaringType != typeof(AbstractModel))
                        continue;
                    string name = StripAccessorPrefix(baseDefinition.Name);
                    if (NonHookMemberNames.Contains(name))
                        continue;
                    hooks.Add(name);
                }
            }
        }
        catch (Exception)
        {
            // 反射失败就无法证明它与战斗无关，交给原有的拒绝路径。
            return (false, string.Empty);
        }

        string overrides = string.Join(",", hooks);
        foreach (string hook in hooks)
        {
            if (!PredictionInertHookNames.Contains(hook))
                return (false, overrides);
        }
        return (true, overrides);
    }

    private static string StripAccessorPrefix(string name)
    {
        if (name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal))
            return name[4..];
        return name;
    }
}
