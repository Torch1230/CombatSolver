using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed record SolverCardTextIdentity(string Id, int Upgrade, string OriginalTitle);
internal sealed record SolverRelicTextIdentity(string Id, string OriginalTitle, string Summary);

// Stable presentation metadata survives route reuse; it carries no live model or search node.
internal sealed record SolverActionTextIdentity(
    string CardId, int Upgrade, string PotionId, bool EndTurn, bool DirectEndTurn,
    IReadOnlyList<IReadOnlyList<SolverCardTextIdentity>> Choices,
    IReadOnlyList<SolverRelicTextIdentity> Relics)
{
    public string CardEnchantmentId { get; init; } = "";
    public static SolverOverlayActionSnapshot Refresh(SolverOverlayActionSnapshot snapshot)
    {
        if (snapshot.TextIdentity is not { } identity) return snapshot;
        string title = identity.EndTurn ? SolverText.Get(identity.DirectEndTurn ? "直接结束" : "结束回合")
            : identity.PotionId.Length > 0 ? SolverUiModelNames.Potion(identity.PotionId, snapshot.Title)
            : SolverUiModelNames.Card(identity.CardId, identity.Upgrade, snapshot.Title);
        string? choices = identity.Choices.Count == 0 ? null : string.Join(" / ", identity.Choices.Select(choice =>
            choice.Count == 0 ? SolverText.Get("不选") : SolverText.Format($"选 {string.Join("、", choice.Select(card => SolverUiModelNames.Card(card.Id, card.Upgrade, card.OriginalTitle)))}")));
        if (identity.CardEnchantmentId == "INKY")
            title += $"（{ModelDb.Enchantment<MegaCrit.Sts2.Core.Models.Enchantments.Inky>().Title.GetFormattedText()}）";
        string[] relics = identity.Relics.Select(relic => SolverUiModelNames.Relic(relic.Id, relic.OriginalTitle)
            + SolverRelicEffectText.Format(relic.Summary)).ToArray();
        string tooltip = (identity.EndTurn ? SolverText.Get("结束回合") : title)
            + (identity.PotionId.Length > 0 || snapshot.VisualKind == SolverOverlayActionVisualKind.Potion ? SolverText.Get("（药水）") : "")
            + (snapshot.TargetName.Length > 0 ? $"→{snapshot.TargetName}" : "")
            + (relics.Length > 0 ? $" [{string.Join("、", relics)}]" : "")
            + (choices == null ? "" : $"（{choices}）")
            + (snapshot.Kills.Count > 0 ? SolverText.Format($"，击杀 {string.Join("、", snapshot.Kills)}") : "");
        return snapshot with { Title = title, ChoiceText = choices, RelicLabels = relics, Tooltip = tooltip };
    }
}

// Accessed only at the main-thread UI projection boundary. Language changes invalidate display caches.
internal static class SolverUiModelNames
{
    private static string? _language;
    private static readonly Dictionary<string, CardModel> Cards = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Id, int Upgrade), string> CardTitles = [];

    public static string Card(string id, int upgrade, string original)
    {
        if (id.Length == 0) return original;
        string language = LocManager.Instance.Language;
        if (_language != language)
        {
            Cards.Clear();
            CardTitles.Clear();
            foreach (CardModel card in ModelDb.AllCards) Cards.TryAdd(card.Id.Entry, card);
            _language = language;
        }
        if (CardTitles.TryGetValue((id, upgrade), out string? title)) return title;
        if (!Cards.TryGetValue(id, out CardModel? canonical)) return original;
        CardModel model = canonical.ToMutable();
        for (int level = 0; level < upgrade; level++)
        {
            model.UpgradeInternal();
            model.FinalizeUpgradeInternal();
        }
        return CardTitles[(id, upgrade)] = model.Title;
    }

    public static string Potion(string id, string original)
        => LocString.GetIfExists("potions", id + ".title")?.GetFormattedText() ?? original;

    public static string Relic(string id, string original)
        => LocString.GetIfExists("relics", id + ".title")?.GetFormattedText() ?? original;
}
