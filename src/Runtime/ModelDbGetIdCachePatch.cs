using System.Collections.Concurrent;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Patching.Models;

namespace CombatSolver;

/// <summary>
/// <c>ModelDb.GetId(Type)</c> 只由类型决定：<c>GetEntry(type) = StringHelper.Slugify(type.Name)</c>，
/// <c>GetCategory(type) = ModelId.SlugifyCategory(GetCategoryType(type).Name)</c>，两者都是纯函数，
/// 结果缓存进 <c>ModelId</c> 不可变 record。但原生实现每次调用都跑三次正则，图鉴/纪元池枚举
/// （<c>Epoch.get_Cards</c> → <c>ModelDb.Card</c>）在搜索热路径里对每个模型重复触发它。
/// 这里只缓存 <c>Type → ModelId</c> 的纯值映射，不缓存 <c>ModelDb</c> 的内容字典或任何模型实例。
/// </summary>
internal sealed class ModelDbGetIdCachePatch : IPatchMethod
{
    private static readonly ConcurrentDictionary<Type, ModelId> Cache = new();

    public static string PatchId => "combat_solver_model_db_get_id_cache";

    public static string Description => "缓存 ModelDb.GetId(Type) 的纯类型→ModelId 映射";

    public static ModPatchTarget[] GetTargets() =>
    [
        new(typeof(ModelDb), "GetId", [typeof(Type)]),
    ];

    /// <summary>命中数只用于诊断；缓存是纯值映射，不需要失效。</summary>
    public static int CachedEntryCount => Cache.Count;

    [HarmonyPriority(Priority.First)]
    public static bool Prefix(Type type, ref ModelId __result)
    {
        // null 参数保持原生失败路径，不把 NullReferenceException 换成字典异常。
        if (type is null)
            return true;
        if (Cache.TryGetValue(type, out ModelId? cached))
        {
            __result = cached;
            return false;
        }
        return true;
    }

    public static void Postfix(Type type, ModelId __result)
    {
        // 原方法抛异常时 Postfix 不会运行，失败不会被固化。
        if (type is not null && __result is not null)
            Cache.TryAdd(type, __result);
    }
}
