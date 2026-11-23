using System.Globalization;
using System.Text.Json;
using MegaCrit.Sts2.Core.Localization;

namespace CombatSolver;

/// <summary>UI-only source-string catalog. Format templates before inserting game names or player input.</summary>
internal static class SolverText
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> English =
        new(LoadEnglish, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static bool IsEnglish => LocManager.Instance.Language is not ("zhs" or "zht");

    public static string Get(string source) => IsEnglish ? English.Value[source] : source;

    public static string Format(FormattableString source)
        => string.Format(CultureInfo.CurrentCulture, Get(source.Format), source.GetArguments());

    internal static void WarmUp() => _ = English.Value;

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using Stream stream = typeof(SolverText).Assembly.GetManifestResourceStream("CombatSolver.UI.English.json")
            ?? throw new InvalidOperationException("Missing embedded English UI catalog.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException("Empty English UI catalog.");
    }
}
