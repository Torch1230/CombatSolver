using System.Text.RegularExpressions;

namespace CombatSolver;

/// <summary>Formats the built-in recorder's compact effect grammar at the UI boundary.</summary>
internal static partial class SolverRelicEffectText
{
    [GeneratedRegex(@"\A：(格挡|抽|敏捷|能量|伤害|全体伤害|力量)([+×-]?\d+(?:[.,]\d+)?)(?: 敏捷([+-]?\d+))?\z")]
    private static partial Regex NumericEffect();

    public static string Format(string summary)
    {
        if (!SolverText.IsEnglish || summary.Length == 0) return summary;
        string? fixedText = summary switch
        {
            "：手牌0费" => ": Hand costs 0",
            "：本张免费" => ": Free card",
            "：复制到手牌" => ": Copy to hand",
            "：升级" => ": Upgrade",
            "：额外回合" => ": Extra turn",
            "：复活" => ": Revive",
            _ => null,
        };
        if (fixedText != null) return fixedText;
        Match match = NumericEffect().Match(summary);
        // Third-party annotations are authored by that mod; preserve their text verbatim.
        if (!match.Success) return summary;
        string effect = match.Groups[1].Value switch
        {
            "格挡" => "Block",
            "抽" => "Draw",
            "敏捷" => "Dexterity",
            "能量" => "Energy",
            "伤害" => "Damage",
            "全体伤害" => "Damage to all",
            "力量" => "Strength",
            _ => throw new InvalidOperationException("Unrecognized built-in relic effect."),
        };
        return $": {effect} {match.Groups[2].Value}"
            + (match.Groups[3].Success ? $" Dexterity {match.Groups[3].Value}" : "");
    }
}
