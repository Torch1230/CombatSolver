using System.Reflection;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

/// <summary>
/// Root-capture guard against third-party Harmony patches that replace gameplay behavior the engine mirrors.
/// </summary>
/// <remarks>
/// Mirrors read live model data, so third-party patches to canonical data (energy cost, dynamic vars, keywords,
/// rarity) are followed automatically except for explicitly rejected gameplay mods. A replaced <see cref="CardModel.OnPlay"/>
/// is different in kind: <c>CardOnPlayInferrer</c> reads the original, unpatched IL by design, and the
/// bespoke mirrors are keyed on the vanilla card type. The engine therefore keeps executing the vanilla recipe it
/// was written against and silently produces a route for a card the game no longer plays that way, which the
/// project's "unknown semantics must fail explicitly" constraint forbids.
/// </remarks>
internal static class PredictionModPatchAudit
{
    private const string OnPlayName = "OnPlay";
    private static readonly string[] IncompatibleModIds = ["WheelchairSpire", "PengoTarot", "BetterCharacterRelics"];

    private readonly record struct ForeignPatch(string ModId, string ModName, string Description);

    /// <summary>
    /// Throws when any card reachable from the captured root has a third-party patch on its mirrored OnPlay.
    /// </summary>
    /// <remarks>
    /// This is a best-effort boundary: card types that only appear later through in-combat generation are not
    /// visible at capture time and are not audited here.
    /// </remarks>
    public static void ValidateCardOnPlay(IEnumerable<CardModel> cards)
    {
        ValidateLoadedMods(ModManager.GetLoadedMods());
        HashSet<Type> checkedTypes = [];
        foreach (CardModel card in cards)
        {
            // Harmony patches can be installed or removed between root captures.
            if (!checkedTypes.Add(card.GetType())
                || FindForeignOnPlayPatch(card.GetType()) is not { } foreign)
                continue;
            throw new IncompatibleGameplayModException(
                foreign.ModId,
                foreign.ModName,
                foreign.Description,
                "combat");
        }
    }

    internal static void ValidateLoadedMods(IEnumerable<Mod> mods)
    {
        foreach (Mod mod in mods)
        {
            string? incompatibleId = IncompatibleModIds.FirstOrDefault(id =>
                string.Equals(mod.manifest?.id, id, StringComparison.OrdinalIgnoreCase)
                || mod.assemblies.Any(assembly => string.Equals(
                    assembly.GetName().Name, id, StringComparison.OrdinalIgnoreCase)));
            if (incompatibleId is null)
                continue;
            throw new IncompatibleGameplayModException(
                mod.manifest?.id ?? string.Empty,
                mod.manifest?.name ?? incompatibleId,
                $"{incompatibleId} gameplay changes",
                "combat");
        }
    }

    private static ForeignPatch? FindForeignOnPlayPatch(Type cardType)
    {
        MethodInfo? onPlay = AccessTools.Method(
            cardType,
            OnPlayName,
            [typeof(PlayerChoiceContext), typeof(CardPlay)]);
        if (onPlay == null)
            return null;

        Patches? patches = Harmony.GetPatchInfo(onPlay);
        if (patches == null)
            return null;

        foreach (Patch patch in patches.Prefixes
                     .Concat(patches.Postfixes)
                     .Concat(patches.Transpilers)
                     .Concat(patches.Finalizers))
        {
            if (TryDescribeForeignPatch(patch, onPlay) is { } foreign)
                return foreign;
        }
        return null;
    }

    private static ForeignPatch? TryDescribeForeignPatch(Patch patch, MethodInfo target)
    {
        Type? patchType = patch.PatchMethod.DeclaringType;
        if (patchType == null)
            throw new PredictionUnsupportedException(
                $"Unknown Harmony patch {patch.PatchMethod} (owner={patch.owner}) on {target}.");

        var mod = AssemblyInfo.ModForType(patchType, out bool isBaseGame);
        if (isBaseGame)
            return null;
        // Same policy as the ModHelper subscriber audit: mods that declare themselves gameplay-neutral are trusted.
        if (mod?.manifest?.affectsGameplay is false)
            return null;
        if (mod?.manifest?.id is not { Length: > 0 } modId)
            throw new PredictionUnsupportedException(
                $"Unknown Harmony patch {patchType.FullName}.{patch.PatchMethod.Name} " +
                $"(owner={patch.owner}) on mirrored {target.DeclaringType?.FullName}.{target.Name}.");
        if (string.Equals(modId, Entry.ModId, StringComparison.OrdinalIgnoreCase))
            return null;

        return new ForeignPatch(
            modId,
            mod.manifest.name ?? string.Empty,
            $"Harmony patch {patchType.FullName}.{patch.PatchMethod.Name} on mirrored "
            + $"{target.DeclaringType?.FullName}.{target.Name}");
    }
}
