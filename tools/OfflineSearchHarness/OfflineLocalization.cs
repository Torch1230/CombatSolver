using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Localization;

namespace OfflineSearchHarness;

/// <summary>
/// 离线本地化。<c>LocManager.Initialize()</c> 会用 <c>Godot.FileAccess</c> 读 <c>res://localization</c>，
/// 构造函数还会经 <c>PlatformUtil</c> 查系统语言、用 <c>Callable.CallDeferred</c> 广播语言变更，都是原生调用。
/// 这里不跑构造函数，只把一个空表的 LocManager 装成单例：文案由 <see cref="GameBootstrap"/> 里的
/// LocString 补丁返回键名，真正被读到的只有 <c>Language</c>（模组用它决定排版）。
/// </summary>
internal static class OfflineLocalization
{
    public static string Install(string language = "eng")
    {
        LocManager manager = (LocManager)RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
        CultureInfo culture = Culture(language);

        Set(manager, "_tables", new Dictionary<string, LocTable>());
        Set(manager, "_languageKeyCount", new Dictionary<string, int>());
        Set(manager, "_localeChangeCallbacks", new List<LocManager.LocaleChangeCallback>());
        SetProperty(manager, nameof(LocManager.Language), language);
        SetProperty(manager, nameof(LocManager.CultureInfo), culture);
        SetProperty(manager, nameof(LocManager.StringComparer),
            StringComparer.Create(culture, CompareOptions.None));
        SetProperty(manager, nameof(LocManager.ValidationErrors), Array.Empty<LocValidationError>());
        SetProperty(manager, nameof(LocManager.OverridesActive), false);

        // SmartFormat 的扩展注册是纯托管的，装上以防有地方真去格式化。
        typeof(LocManager)
            .GetMethod("LoadLocFormatters", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(manager, null);

        typeof(LocManager)
            .GetProperty(nameof(LocManager.Instance), BindingFlags.Static | BindingFlags.Public)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(null, [manager]);

        return $"language={LocManager.Instance.Language} tables=0";
    }

    private static CultureInfo Culture(string language) => language switch
    {
        "zhs" => CultureInfo.GetCultureInfo("zh-hans"),
        "zht" => CultureInfo.GetCultureInfo("zh-hant"),
        "jpn" => CultureInfo.GetCultureInfo("ja"),
        _ => CultureInfo.GetCultureInfo("en"),
    };

    private static void Set(object instance, string field, object value)
        => (typeof(LocManager).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(LocManager), field))
            .SetValue(instance, value);

    private static void SetProperty(object instance, string property, object? value)
    {
        PropertyInfo info = typeof(LocManager).GetProperty(property,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(nameof(LocManager), property);
        MethodInfo? setter = info.GetSetMethod(nonPublic: true);
        if (setter != null)
        {
            setter.Invoke(instance, [value]);
            return;
        }
        (typeof(LocManager).GetField($"<{property}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(LocManager), property))
            .SetValue(instance, value);
    }
}
