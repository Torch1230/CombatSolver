namespace CombatSolver.Api;

internal enum PreCombatHarmonyTargetKind
{
    None = 0,
    Rng = 1,
    CombatGameplay = 2,
}

internal readonly record struct PreCombatHarmonyPatchDescriptor(
    string? DeclaringNamespace,
    string? RootDeclaringTypeName,
    string MethodName,
    bool IsMonsterModel,
    bool IsBaseGamePatch,
    bool IsTrustedOwner,
    bool? AffectsGameplay,
    bool HasKnownModId,
    string PatchDisplayName,
    string TargetDisplayName,
    string Owner,
    bool IsConcreteCardGameplayOverride = false);

internal readonly record struct PreCombatCardOnPlayAuditOutcome(
    bool HasForeignPatch,
    bool HasBuiltInAdaptation,
    string? ForeignDescription);

internal static partial class PreCombatSts2SimEligibility
{
    private static readonly HashSet<string> PureCardDisplayOrMetadataMembers = new(StringComparer.Ordinal)
    {
        "Title",
        "TitleLocString",
        "Description",
        "GetDescription",
        "GetDescriptionForPile",
        "GetDescriptionForUpgradePreview",
        "GetDescriptionOrFallback",
        "PortraitPath",
        "PortraitPngPath",
        "BetaPortraitPath",
        "OverlayPath",
        "AllPortraitPaths",
        "ExtraRunAssetPaths",
        "Portrait",
        "BetaPortrait",
        "Overlay",
        "IconPath",
        "FrameMaterial",
        "FramePath",
        "BannerMaterial",
        "VisualCardPool",
        "HasBuiltInOverlay",
        "ExtraHoverTips",
        "HoverTips",
        "ShouldGlowGold",
        "ShouldGlowGoldInternal",
        "ShouldGlowRed",
        "ShouldGlowRedInternal",
        "HasBakedPortrait",
        "AddExtraArgsToDescription",
        "OnEnqueuePlayVfx",
        "CreateHoverTip",
        "ToString",
        "Equals",
        "GetHashCode",
        "CompareTo",
    };

    private static readonly HashSet<string> ConcreteCardGameplayExactNames = new(StringComparer.Ordinal)
    {
        "OnPlay",
        "CanPlay",
        "ShouldPlay",
        "IsPlayable",
        "OnTurnEndInHand",
        "OnUpgrade",
        "OnDowngrade",
        "AfterDowngraded",
        "OnExhaust",
        "OnDiscard",
        "OnDrawn",
        "OnRetain",
        "OnShuffle",
        "OnAddedToDeck",
        "OnRemovedFromDeck",
        "OnCreatedInCombat",
        "OnEnterCombat",
        "GetResultLocationForCardPlay",
        "CalculateVars",
        "GetDamage",
        "GetBlock",
        "GetHits",
        "BuffFromPlay",
        "BuffFromClawPlay",
        "BuffFromFlorescence",
        "AddDamage",
        "SetRepeats",
        "CreatedThroughForge",
        "FakeUpgrade",
        "IncreaseAim",
    };

    private static bool NamespaceEqualsOrStartsWith(string? ns, string prefix) =>
        ns is not null
        && (string.Equals(ns, prefix, StringComparison.Ordinal)
            || (ns.Length > prefix.Length
                && ns.StartsWith(prefix, StringComparison.Ordinal)
                && ns[prefix.Length] == '.'));

    internal static bool IsPureCardDisplayOrMetadataMember(string memberName)
    {
        string normalized = memberName.StartsWith("get_", StringComparison.Ordinal)
            || memberName.StartsWith("set_", StringComparison.Ordinal)
            ? memberName[4..]
            : memberName;
        return PureCardDisplayOrMetadataMembers.Contains(normalized);
    }

    internal static bool TryExtractCompilerGeneratedEnclosingMethod(string name, out string enclosingMethodName)
    {
        int index = 0;
        while (index < name.Length)
        {
            int open = name.IndexOf('<', index);
            if (open < 0)
                break;
            int close = name.IndexOf('>', open + 1);
            if (close < 0)
                break;

            int nextOpen = name.IndexOf('<', open + 1);
            if (nextOpen >= 0 && nextOpen < close)
            {
                index = nextOpen;
                continue;
            }

            if (close > open + 1)
            {
                ReadOnlySpan<char> after = name.AsSpan(close + 1);
                if (after.StartsWith("d__", StringComparison.Ordinal)
                    || after.StartsWith("g__", StringComparison.Ordinal)
                    || after.StartsWith("b__", StringComparison.Ordinal))
                {
                    enclosingMethodName = name[(open + 1)..close];
                    return true;
                }
            }

            index = close + 1;
        }

        enclosingMethodName = string.Empty;
        return false;
    }

    internal static string ResolveEffectiveMemberName(string methodName, string? targetDisplayName = null)
    {
        if (TryExtractCompilerGeneratedEnclosingMethod(methodName, out string fromMethod))
            return fromMethod;
        if (string.Equals(methodName, "MoveNext", StringComparison.Ordinal)
            && targetDisplayName is not null
            && TryExtractCompilerGeneratedEnclosingMethod(targetDisplayName, out string fromTarget))
        {
            return fromTarget;
        }
        return methodName;
    }

    internal static bool IsConcreteCardGameplayMethodName(string methodName, string? targetDisplayName = null)
    {
        string effectiveName = ResolveEffectiveMemberName(methodName, targetDisplayName);

        if (IsPureCardDisplayOrMetadataMember(effectiveName))
            return false;

        string normalizedName = effectiveName.StartsWith("get_", StringComparison.Ordinal)
            || effectiveName.StartsWith("set_", StringComparison.Ordinal)
            ? effectiveName[4..]
            : effectiveName;

        return ConcreteCardGameplayExactNames.Contains(normalizedName)
            || normalizedName.StartsWith("BuffFrom", StringComparison.Ordinal)
            || normalizedName.StartsWith("Before", StringComparison.Ordinal)
            || normalizedName.StartsWith("After", StringComparison.Ordinal)
            || normalizedName.StartsWith("Modify", StringComparison.Ordinal)
            || normalizedName.StartsWith("TryModify", StringComparison.Ordinal)
            || normalizedName.StartsWith("Should", StringComparison.Ordinal);
    }

    internal static PreCombatHarmonyTargetKind ClassifyCriticalHarmonyTarget(
        string? declaringNamespace,
        string? rootDeclaringTypeName,
        string methodName,
        bool isMonsterModel,
        bool isConcreteCardGameplayOverride = false,
        string? targetDisplayName = null)
    {
        if (NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Random"))
            return PreCombatHarmonyTargetKind.Rng;

        if (isConcreteCardGameplayOverride)
            return PreCombatHarmonyTargetKind.CombatGameplay;

        if (isMonsterModel && string.Equals(methodName, "GenerateMoveStateMachine", StringComparison.Ordinal))
            return PreCombatHarmonyTargetKind.CombatGameplay;

        if (string.Equals(declaringNamespace, "MegaCrit.Sts2.Core.MonsterMoves.Intents", StringComparison.Ordinal)
            && ((string.Equals(rootDeclaringTypeName, "AttackIntent", StringComparison.Ordinal)
                    && (string.Equals(methodName, ".ctor", StringComparison.Ordinal)
                        || string.Equals(methodName, "GetSingleDamage", StringComparison.Ordinal)))
                || (rootDeclaringTypeName is "SingleAttackIntent" or "MultiAttackIntent"
                    && string.Equals(methodName, ".ctor", StringComparison.Ordinal))))
        {
            return PreCombatHarmonyTargetKind.CombatGameplay;
        }

        if (NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Models.Cards"))
        {
            return IsConcreteCardGameplayMethodName(methodName, targetDisplayName)
                ? PreCombatHarmonyTargetKind.CombatGameplay
                : PreCombatHarmonyTargetKind.None;
        }

        return PreCombatHarmonyTargetKind.None;
    }

    internal static bool IsForeignPatchDescriptor(
        in PreCombatHarmonyPatchDescriptor patch,
        bool trustGameplayNeutral)
    {
        if (patch.IsBaseGamePatch)
            return false;
        if (trustGameplayNeutral && patch.IsTrustedOwner)
            return false;
        if (patch.HasKnownModId)
            return !(trustGameplayNeutral && patch.AffectsGameplay is false);
        return true;
    }

    internal static bool TryFindCriticalHarmonyPatchViolation(
        IEnumerable<PreCombatHarmonyPatchDescriptor> patches,
        out string reason)
    {
        foreach (PreCombatHarmonyPatchDescriptor patch in patches)
        {
            PreCombatHarmonyTargetKind targetKind = ClassifyCriticalHarmonyTarget(
                patch.DeclaringNamespace,
                patch.RootDeclaringTypeName,
                patch.MethodName,
                patch.IsMonsterModel,
                patch.IsConcreteCardGameplayOverride,
                patch.TargetDisplayName);
            if (targetKind == PreCombatHarmonyTargetKind.None)
                continue;

            bool trustGameplayNeutral = targetKind != PreCombatHarmonyTargetKind.Rng;
            if (IsForeignPatchDescriptor(patch, trustGameplayNeutral))
            {
                reason = $"Foreign Harmony patch {patch.PatchDisplayName} (owner={patch.Owner}) on {patch.TargetDisplayName}.";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    internal static bool ShouldRejectCardOnPlayForSts2Sim(
        in PreCombatCardOnPlayAuditOutcome outcome,
        out string reason)
    {
        if (outcome.HasForeignPatch)
        {
            reason = string.IsNullOrWhiteSpace(outcome.ForeignDescription)
                ? "Foreign Harmony patch on reachable card OnPlay."
                : outcome.ForeignDescription;
            return true;
        }

        reason = string.Empty;
        return false;
    }
}
