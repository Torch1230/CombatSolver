namespace CombatSolver;

internal readonly record struct ShowcaseModDeclaration(
    string Id,
    string Name,
    bool AffectsGameplay);

internal static class CombatShowcaseModEligibility
{
    private static readonly HashSet<string> CapturedRootToolModIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Loadout",
        "LegacyRng107Compat",
    };

    internal static string[] FindGameplayModificationNames(IEnumerable<ShowcaseModDeclaration> mods)
        => mods
            .Where(static mod => mod.AffectsGameplay && !IsCapturedRootTool(mod.Id))
            .Select(static mod => string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : $"{mod.Name} ({mod.Id})")
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    private static bool IsCapturedRootTool(string id)
        => string.Equals(id, Entry.ModId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(id, "STS2-RitsuLib", StringComparison.OrdinalIgnoreCase)
           || CapturedRootToolModIds.Contains(id);
}
