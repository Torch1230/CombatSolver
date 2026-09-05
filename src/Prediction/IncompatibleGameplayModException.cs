namespace CombatSolver;

internal sealed class IncompatibleGameplayModException : NotSupportedException
{
    public string ModId { get; }
    public string ModName { get; }

    /// <summary>What was found, e.g. a ModHelper subscriber type or a Harmony patch on a mirrored method.</summary>
    public string Subject { get; }

    /// <summary>Where it was found, e.g. <c>run</c> or <c>combat</c>.</summary>
    public string Scope { get; }

    public IncompatibleGameplayModException(
        string modId,
        string modName,
        string subject,
        string scope)
        : base($"Unsupported gameplay {scope} extension {subject} from mod {DescribeMod(modName, modId)}.")
    {
        ModId = modId;
        ModName = modName;
        Subject = subject;
        Scope = scope;
    }

    public string PlayerFacingModName => DescribeMod(ModName, ModId);

    private static string DescribeMod(string modName, string modId)
    {
        if (string.IsNullOrWhiteSpace(modName))
            return modId;
        return string.Equals(modName, modId, StringComparison.OrdinalIgnoreCase)
            ? modName
            : $"{modName}（{modId}）";
    }
}
