using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Unlocks;

namespace CombatSolver.Api;

internal readonly record struct PreCombatEligibilityResult(
    bool IsEligible,
    string? FailureCode,
    string? Detail,
    string Fingerprint)
{
    public static PreCombatEligibilityResult Eligible(string fingerprint) =>
        new(true, null, null, fingerprint);

    public static PreCombatEligibilityResult Ineligible(string failureCode, string detail, string fingerprint) =>
        new(false, failureCode, detail, fingerprint);
}

internal static class PreCombatSts2SimEligibility
{
    private static readonly Assembly VanillaGameAssembly = typeof(RunState).Assembly;
    private static readonly HashSet<string> TrustedPatchOwnerIds = new(StringComparer.OrdinalIgnoreCase)
    {
        Entry.ModId,
        "STS2-RitsuLib",
    };

    private static readonly string[] PresentationOnlyNamespacePrefixes =
    [
        "MegaCrit.Sts2.Core.Achievements",
        "MegaCrit.Sts2.Core.Animation",
        "MegaCrit.Sts2.Core.Assets",
        "MegaCrit.Sts2.Core.Audio",
        "MegaCrit.Sts2.Core.ControllerInput",
        "MegaCrit.Sts2.Core.HoverTips",
        "MegaCrit.Sts2.Core.Leaderboard",
        "MegaCrit.Sts2.Core.Localization",
        "MegaCrit.Sts2.Core.Logging",
        "MegaCrit.Sts2.Core.Nodes",
        "MegaCrit.Sts2.Core.Platform",
        "MegaCrit.Sts2.Core.RichTextTags",
        "MegaCrit.Sts2.Core.TextEffects",
    ];

    public static PreCombatEligibilityResult AssessMandatoryLiveHost(RunState run)
    {
        if (run.GameMode == GameMode.Dread)
        {
            return Ineligible(
                "dread_mode",
                "Dread mode routes directly to the legacy solver.",
                run,
                null);
        }
        if (run.GameMode != GameMode.Standard)
        {
            return Ineligible(
                "non_standard_mode",
                $"Game mode {run.GameMode} routes to the legacy solver.",
                run,
                null);
        }
        if (run.IsCustomGame)
        {
            return Ineligible(
                "custom_game",
                "Custom games route to the legacy solver.",
                run,
                null);
        }
        if (RunManager.Instance.DailyTime is not null)
        {
            return Ineligible(
                "daily_run",
                "Daily runs route to the legacy solver.",
                run,
                null);
        }
        if (run.Modifiers.Count > 0)
        {
            return Ineligible(
                "modifiers_present",
                $"Run has {run.Modifiers.Count} modifier(s).",
                run,
                null);
        }
        if (run.RunModels.Count != 1 || run.RunModels[0] is not MultiplayerScalingModel)
        {
            return Ineligible(
                "non_standard_run_models",
                "Run models differ from vanilla single-player Standard.",
                run,
                null);
        }

        ReleaseInfo? release = ReleaseInfoManager.Instance.ReleaseInfo;
        if (release is null
            || !string.Equals(release.Version, PreCombatSts2SimAbi.SupportedGameVersion, StringComparison.Ordinal)
            || !string.Equals(release.Commit, PreCombatSts2SimAbi.SupportedGameCommit, StringComparison.Ordinal))
        {
            return Ineligible(
                "game_build_unsupported",
                $"Host game release '{release?.Version ?? "null"}@{release?.Commit ?? "null"}' " +
                $"does not match {PreCombatSts2SimAbi.SupportedGameVersion}@{PreCombatSts2SimAbi.SupportedGameCommit}.",
                run,
                null);
        }

        if (TryFindIneligibleMod(out string modReason))
        {
            return Ineligible("gameplay_mod", modReason, run, null);
        }

        if (TryFindForeignVanillaHarmonyPatch(out string patchReason))
        {
            return Ineligible("gameplay_patch", patchReason, run, null);
        }

        string fingerprint = BuildLiveFingerprint(
            isEligible: true,
            failureCode: null,
            run,
            player: null);
        return PreCombatEligibilityResult.Eligible(fingerprint);
    }

    public static PreCombatEligibilityResult AssessLiveHostAndRun(RunState run)
    {
        PreCombatEligibilityResult mandatoryHost = AssessMandatoryLiveHost(run);
        if (!mandatoryHost.IsEligible)
            return mandatoryHost;

        if (!RunManager.Instance.IsSingleplayerOrFakeMultiplayer || run.Players.Count != 1)
        {
            return Ineligible(
                "live_between_room_precondition",
                "Pre-combat forecast requires a single-player run.",
                run,
                null);
        }
        if (CombatManager.Instance.IsInProgress)
        {
            return Ineligible(
                "live_between_room_precondition",
                "Pre-combat forecast cannot run during active combat.",
                run,
                null);
        }

        Player player = LocalContext.GetMe(run) ?? run.Players[0];
        if (!player.IsActiveForHooks
            || !player.CanUseOrRemovePotions
            || player.Creature.Powers.Count != 0
            || player.Creature.Block != 0)
        {
            return Ineligible(
                "live_between_room_precondition",
                "Live player between-room state has unsupported active combat/hook flags, powers, or block.",
                run,
                player);
        }

        if (player.ExtraFields.WongoPoints != 0
            || player.ExtraFields.IsPlayingRotatingCharacter
            || run.ExtraFields.FreedRepy
            || run.ExtraFields.TestSubjectKills != 0
            || run.ExtraFields.UsedDebugCommand
            || !run.ExtraFields.StartedWithNeow)
        {
            return Ineligible(
                "extra_field_unsupported",
                "Run or player extra fields contain state outside the bridge cohort.",
                run,
                player);
        }

        if (!HasFullEpochUnlocks(player.UnlockState.ToSerializable().UnlockedEpochs))
        {
            return Ineligible(
                "partial_unlocks",
                "Partial epoch unlock state routes to the legacy solver.",
                run,
                player);
        }

        if (TryFindNonVanillaModel(run, player, out string modelReason))
        {
            return Ineligible("model_unmapped", modelReason, run, player);
        }

        if (TryFindForeignCardOnPlayPatch(player.Deck.Cards, out string patchReason))
        {
            return Ineligible("gameplay_patch", patchReason, run, player);
        }

        string fingerprint = BuildLiveFingerprint(
            isEligible: true,
            failureCode: null,
            run,
            player);
        return PreCombatEligibilityResult.Eligible(fingerprint);
    }

    public static PreCombatEligibilityResult AssessPlanningRun(
        RunState liveRun,
        PreCombatEligibilityResult mandatoryLiveHost,
        string liveFingerprint,
        SerializableRun plannedRun,
        RunState plannedState,
        bool hadPreFinishedRoom,
        int targetActFloor,
        int targetMapColumn,
        bool isSecondBoss)
    {
        if (!mandatoryLiveHost.IsEligible)
            return mandatoryLiveHost;

        if (AssessMandatoryLiveHost(liveRun) is { IsEligible: false } refreshedHostExclusion)
            return refreshedHostExclusion;

        if (hadPreFinishedRoom || plannedRun.PreFinishedRoom is not null)
        {
            return IneligiblePlanning(
                "planning_pre_finished_room",
                "Planning snapshot contains a pre_finished_room.");
        }
        if (plannedRun.GameMode == GameMode.Dread)
            return IneligiblePlanning("dread_mode", "Planning run uses Dread mode.");
        if (plannedRun.GameMode != GameMode.Standard)
            return IneligiblePlanning("non_standard_mode", $"Planning run mode is {plannedRun.GameMode}.");
        if (plannedRun.IsCustomGame)
            return IneligiblePlanning("custom_game", "Planning run is marked as custom.");
        if (plannedRun.DailyTime is not null)
            return IneligiblePlanning("daily_run", "Planning run has DailyTime set.");
        if (plannedRun.Modifiers.Count > 0)
            return IneligiblePlanning("modifiers_present", "Planning run contains modifiers.");
        if (plannedRun.Players is null || plannedRun.Players.Count != 1 || plannedState.Players.Count != 1)
            return IneligiblePlanning("live_between_room_precondition", "Planning run must contain exactly one player.");
        if (plannedState.RunModels.Count != 1 || plannedState.RunModels[0] is not MultiplayerScalingModel)
            return IneligiblePlanning("non_standard_run_models", "Planning run models differ from vanilla single-player Standard.");

        var plannedPlayer = plannedRun.Players[0];
        if (!HasFullEpochUnlocks(plannedPlayer.UnlockState?.UnlockedEpochs))
            return IneligiblePlanning("partial_unlocks", "Planning run does not have full epoch unlocks.");

        var extras = plannedRun.ExtraFields;
        var playerExtras = plannedPlayer.ExtraFields;
        if (!extras.StartedWithNeow
            || extras.FreedRepy
            || extras.TestSubjectKills != 0
            || extras.UsedDebugCommand
            || playerExtras.WongoPoints != 0
            || playerExtras.IsPlayingRotatingCharacter)
        {
            return IneligiblePlanning("extra_field_unsupported", "Planning run contains unsupported extra fields.");
        }

        if (plannedPlayer.Deck.Any(card => string.Equals(card.Id?.ToString(), "CARD.SPOILS_MAP", StringComparison.Ordinal)))
        {
            return IneligiblePlanning(
                "planning_spoils_map_unsaved",
                "Planning snapshot deck contains SpoilsMap whose live coordinate is omitted from SerializableRun.");
        }

        var targetCoord = new MegaCrit.Sts2.Core.Map.MapCoord(targetMapColumn, targetActFloor - 1);
        if (plannedRun.VisitedMapCoords.Contains(targetCoord))
        {
            return IneligiblePlanning(
                "planning_revisited_coord",
                $"Planning target coordinate {targetCoord} is already visited in plannedRun.");
        }

        if (plannedRun.CurrentActIndex < 0
            || plannedRun.CurrentActIndex >= plannedRun.Acts.Count)
        {
            return IneligiblePlanning("map_topology_unsupported", "Planning act index is out of range.");
        }
        var plannedAct = plannedRun.Acts[plannedRun.CurrentActIndex];
        if (plannedAct.SavedMap is null
            || (!plannedAct.SavedMap.Points.Any(p => p.Coord == targetCoord)
                && plannedAct.SavedMap.BossPoint?.Coord != targetCoord
                && plannedAct.SavedMap.SecondBossPoint?.Coord != targetCoord
                && plannedAct.SavedMap.StartingPoint?.Coord != targetCoord))
        {
            return IneligiblePlanning(
                "map_topology_unsupported",
                $"Planned saved_map does not contain target coordinate {targetCoord}.");
        }
        bool liveSecondBoss = liveRun.Map.SecondBossMapPoint?.coord == targetCoord;
        bool plannedSecondBoss = plannedAct.SavedMap.SecondBossPoint?.Coord == targetCoord;
        if (isSecondBoss != liveSecondBoss || isSecondBoss != plannedSecondBoss)
        {
            return IneligiblePlanning(
                "map_topology_unsupported",
                "Planned target second-boss status disagrees with effective map.");
        }

        Player restoredPlannedPlayer = plannedState.Players[0];
        if (TryFindNonVanillaModel(plannedState, restoredPlannedPlayer, out string modelReason)
            || plannedPlayer.Relics.Any(static r => ModelDb.GetByIdOrNull<RelicModel>(r.Id!)?.GetType().Assembly != VanillaGameAssembly)
            || plannedPlayer.Deck.Any(static c => ModelDb.GetByIdOrNull<CardModel>(c.Id!)?.GetType().Assembly != VanillaGameAssembly)
            || plannedPlayer.Potions.Any(static p => ModelDb.GetByIdOrNull<PotionModel>(p.Id!)?.GetType().Assembly != VanillaGameAssembly))
        {
            return IneligiblePlanning(
                "model_unmapped",
                string.IsNullOrEmpty(modelReason)
                    ? "Planning snapshot references non-vanilla models."
                    : modelReason);
        }

        if (TryFindForeignCardOnPlayPatch(restoredPlannedPlayer.Deck.Cards, out string cardPatchReason))
            return IneligiblePlanning("gameplay_patch", cardPatchReason);

        return PreCombatEligibilityResult.Eligible(liveFingerprint);
    }

    public static string? TryDescribeDirectIneligibility(RunState run)
    {
        if (run.GameMode == GameMode.Dread)
            return "dread_mode";
        if (run.GameMode != GameMode.Standard)
            return $"non_standard_mode:{run.GameMode}";
        if (run.IsCustomGame)
            return "custom_game";
        if (RunManager.Instance.DailyTime is not null)
            return "daily_run";
        if (run.Modifiers.Count > 0)
            return "modifiers_present";
        if (TryFindIneligibleMod(out string modReason))
            return $"gameplay_mod:{modReason}";
        return null;
    }

    private static bool TryFindIneligibleMod(out string reason)
    {
        foreach (Mod mod in ModManager.GetLoadedMods())
        {
            if (mod.manifest is null)
            {
                reason = $"Loaded mod at '{mod.path}' has no manifest.";
                return true;
            }
            string? id = mod.manifest.id;
            if (string.IsNullOrWhiteSpace(id))
            {
                reason = $"Loaded mod at '{mod.path}' has an empty manifest id.";
                return true;
            }
            if (mod.manifest.affectsGameplay)
            {
                reason = $"Loaded mod '{id}' declares affects_gameplay=true.";
                return true;
            }
            if (string.Equals(id, Entry.ModId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, "STS2-RitsuLib", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
        }
        reason = string.Empty;
        return false;
    }

    private static bool TryFindNonVanillaModel(RunState run, Player player, out string reason)
    {
        if (player.Character.GetType().Assembly != VanillaGameAssembly)
        {
            reason = $"Non-vanilla character {player.Character.GetType().FullName}.";
            return true;
        }
        foreach (ActModel act in run.Acts)
        {
            if (act.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla act {act.GetType().FullName}.";
                return true;
            }
        }
        foreach (CardModel card in player.Deck.Cards)
        {
            if (card.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla deck card {card.GetType().FullName}.";
                return true;
            }
            if (card.Enchantment is { } enchantment && enchantment.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla enchantment {enchantment.GetType().FullName}.";
                return true;
            }
        }
        foreach (RelicModel relic in player.Relics)
        {
            if (relic.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla relic {relic.GetType().FullName}.";
                return true;
            }
        }
        foreach (PotionModel? potion in player.PotionSlots)
        {
            if (potion is not null && potion.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla potion {potion.GetType().FullName}.";
                return true;
            }
        }
        reason = string.Empty;
        return false;
    }

    private static bool TryFindForeignVanillaHarmonyPatch(out string reason)
    {
        foreach (MethodBase method in Harmony.GetAllPatchedMethods())
        {
            Type? declaring = method.DeclaringType;
            if (declaring is null || declaring.Assembly != VanillaGameAssembly)
                continue;
            if (IsPresentationOnlyNamespace(declaring.Namespace))
                continue;

            Patches? info = Harmony.GetPatchInfo(method);
            if (info is null)
                continue;
            foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
            {
                if (IsForeignPatch(patch))
                {
                    reason = $"Foreign Harmony patch {patch.PatchMethod.DeclaringType?.FullName}.{patch.PatchMethod.Name} " +
                             $"(owner={patch.owner}) on {declaring.FullName}.{method.Name}.";
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }

    private static bool TryFindForeignCardOnPlayPatch(IEnumerable<CardModel> deckCards, out string reason)
    {
        try
        {
            _ = PredictionModPatchAudit.CaptureCardOnPlay(deckCards);
        }
        catch (Exception ex) when (ex is IncompatibleGameplayModException or PredictionUnsupportedException)
        {
            reason = ex.Message;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsPresentationOnlyNamespace(string? ns)
    {
        if (string.IsNullOrWhiteSpace(ns))
            return false;
        foreach (string prefix in PresentationOnlyNamespacePrefixes)
        {
            if (string.Equals(ns, prefix, StringComparison.Ordinal)
                || (ns.StartsWith(prefix, StringComparison.Ordinal)
                    && ns.Length > prefix.Length
                    && ns[prefix.Length] == '.'))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsForeignPatch(Patch patch)
    {
        Type? patchType = patch.PatchMethod.DeclaringType;
        if (patchType is null)
            return true;

        Mod? mod = AssemblyInfo.ModForType(patchType, out bool isBaseGame);
        if (isBaseGame)
            return false;
        if (mod?.manifest?.id is { } modId)
            return !TrustedPatchOwnerIds.Contains(modId);
        if (patchType.Assembly == typeof(Entry).Assembly || patchType.Assembly == VanillaGameAssembly)
            return false;
        return true;
    }

    private static bool HasFullEpochUnlocks(IEnumerable<string>? unlockedEpochs)
    {
        if (unlockedEpochs is null)
            return false;
        HashSet<string> unlocked = unlockedEpochs as HashSet<string> ?? new HashSet<string>(unlockedEpochs, StringComparer.Ordinal);
        foreach (string requiredEpoch in EpochModel.AllEpochIds)
        {
            if (!unlocked.Contains(requiredEpoch))
                return false;
        }
        return true;
    }

    private static PreCombatEligibilityResult Ineligible(
        string code,
        string detail,
        RunState run,
        Player? player)
    {
        string fingerprint = BuildLiveFingerprint(false, code, run, player);
        return PreCombatEligibilityResult.Ineligible(code, detail, fingerprint);
    }

    private static PreCombatEligibilityResult IneligiblePlanning(string code, string detail)
    {
        string fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"planning_ineligible|{code}|{detail}")));
        return PreCombatEligibilityResult.Ineligible(code, detail, fingerprint);
    }

    private static string BuildLiveFingerprint(
        bool isEligible,
        string? failureCode,
        RunState run,
        Player? player)
    {
        string mods = string.Join(
            ';',
            ModManager.GetLoadedMods()
                .OrderBy(static m => m.manifest?.id ?? m.path, StringComparer.Ordinal)
                .Select(static m =>
                    $"{m.manifest?.id ?? "<null>"}@{m.manifest?.version ?? "<null>"}:gameplay={m.manifest?.affectsGameplay}"));
        ReleaseInfo? release = ReleaseInfoManager.Instance.ReleaseInfo;
        string runModels = string.Join(',', run.RunModels.Select(static m => m.GetType().FullName));
        string precondition = player is null
            ? "player=null"
            : $"active={player.IsActiveForHooks}|pot={player.CanUseOrRemovePotions}|" +
              $"pow={player.Creature.Powers.Count}|blk={player.Creature.Block}|" +
              $"wongo={player.ExtraFields.WongoPoints}|rot={player.ExtraFields.IsPlayingRotatingCharacter}|" +
              $"epochs={player.UnlockState.EpochUnlockCount()}";
        string raw = string.Join(
            '|',
            $"eligible={isEligible}",
            $"code={failureCode ?? "none"}",
            $"mode={run.GameMode}",
            $"custom={run.IsCustomGame}",
            $"daily={RunManager.Instance.DailyTime?.ToUnixTimeSeconds().ToString() ?? "null"}",
            $"mods_count={run.Modifiers.Count}",
            $"run_models={runModels}",
            $"release={release?.Version ?? "null"}@{release?.Commit ?? "null"}",
            $"neow={run.ExtraFields.StartedWithNeow}",
            $"repy={run.ExtraFields.FreedRepy}",
            $"ts_kills={run.ExtraFields.TestSubjectKills}",
            $"debug={run.ExtraFields.UsedDebugCommand}",
            precondition,
            mods);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
