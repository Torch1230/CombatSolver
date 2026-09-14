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
        => CaptureCardOnPlay(cards);

    internal static AdaptedOnPlaySnapshot? CaptureCardOnPlay(IEnumerable<CardModel> cards)
    {
        ValidateLoadedMods(ModManager.GetLoadedMods());
        bool adapted = AdaptedCardOnPlayMirrors.Seal();
        Dictionary<Type, AdaptedCardOnPlayMirrors.Registration?>? selections = adapted ? [] : null;
        HashSet<Type> checkedTypes = [];
        foreach (CardModel card in cards)
        {
            // Harmony patches can be installed or removed between root captures.
            Type type = card.GetType();
            if (!checkedTypes.Add(type)) continue;
            MethodInfo target = AdaptedCardOnPlayMirrors.ResolveOnPlay(type)
                ?? throw new PredictionUnsupportedException($"Missing OnPlay for {type.FullName}.");
            Patches? patches = Harmony.GetPatchInfo(target);
            ForeignPatch? firstForeign = null;
            if (patches is not null)
                foreach (var group in AdaptedCardOnPlayMirrors.Groups(patches))
                    foreach (Patch patch in group.Patches)
                    {
                        // Resolve every source even when the full combination is registered.
                        ForeignPatch? foreign = TryDescribeForeignPatch(patch, target);
                        firstForeign ??= foreign;
                    }
            var selected = adapted ? AdaptedCardOnPlayMirrors.Select(type, target, patches) : null;
            if (selected is null && firstForeign is { } unsupported)
                throw new IncompatibleGameplayModException(unsupported.ModId, unsupported.ModName,
                    unsupported.Description, "combat");
            selections?.Add(type, selected);
        }
        return selections is null ? null : new(selections, AdaptedCardOnPlayMirrors.CaptureLiveStamp()!);
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
