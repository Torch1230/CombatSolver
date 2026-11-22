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
    string Owner);

internal readonly record struct PreCombatCardOnPlayAuditOutcome(
    bool HasForeignPatch,
    bool HasBuiltInAdaptation,
    string? ForeignDescription);

internal static partial class PreCombatSts2SimEligibility
{
    private static bool NamespaceEqualsOrStartsWith(string? ns, string prefix) =>
        ns is not null
        && (string.Equals(ns, prefix, StringComparison.Ordinal)
            || (ns.Length > prefix.Length
                && ns.StartsWith(prefix, StringComparison.Ordinal)
                && ns[prefix.Length] == '.'));

    internal static PreCombatHarmonyTargetKind ClassifyCriticalHarmonyTarget(
        string? declaringNamespace,
        string? rootDeclaringTypeName,
        string methodName,
        bool isMonsterModel)
    {
        if (NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Random"))
            return PreCombatHarmonyTargetKind.Rng;

        if (isMonsterModel && string.Equals(methodName, "GenerateMoveStateMachine", StringComparison.Ordinal))
            return PreCombatHarmonyTargetKind.CombatGameplay;

        if (NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Commands"))
        {
            return rootDeclaringTypeName is "VfxCmd" or "SfxCmd"
                ? PreCombatHarmonyTargetKind.None
                : PreCombatHarmonyTargetKind.CombatGameplay;
        }

        if (NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Hooks")
            || NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Combat")
            || NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.GameActions")
            || NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.MonsterMoves")
            || NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Models.Powers")
            || NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Models.Relics")
            || NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Models.Potions")
            || NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Models.Orbs")
            || NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Models.Enchantments")
            || NamespaceEqualsOrStartsWith(declaringNamespace, "MegaCrit.Sts2.Core.Models.Monsters"))
        {
            return PreCombatHarmonyTargetKind.CombatGameplay;
        }

        if ((string.Equals(declaringNamespace, "MegaCrit.Sts2.Core.Entities.Creatures", StringComparison.Ordinal)
                && string.Equals(rootDeclaringTypeName, "Creature", StringComparison.Ordinal))
            || (string.Equals(declaringNamespace, "MegaCrit.Sts2.Core.Entities.Players", StringComparison.Ordinal)
                && string.Equals(rootDeclaringTypeName, "PlayerCombatState", StringComparison.Ordinal))
            || (string.Equals(declaringNamespace, "MegaCrit.Sts2.Core.Entities.Cards", StringComparison.Ordinal)
                && rootDeclaringTypeName is "CardPile" or "CardEnergyCost" or "CardPlay")
            || (string.Equals(declaringNamespace, "MegaCrit.Sts2.Core.Models", StringComparison.Ordinal)
                && rootDeclaringTypeName is "CardModel" or "PowerModel" or "RelicModel" or "PotionModel" or "OrbModel" or "EnchantmentModel" or "MonsterModel"))
        {
            return PreCombatHarmonyTargetKind.CombatGameplay;
        }

        return PreCombatHarmonyTargetKind.None;
    }

    internal static bool IsForeignPatchDescriptor(
        in PreCombatHarmonyPatchDescriptor patch,
        bool trustGameplayNeutral)
    {
        if (patch.IsBaseGamePatch || patch.IsTrustedOwner)
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
                patch.IsMonsterModel);
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
