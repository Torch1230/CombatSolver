using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Timeline;
using MegaCrit.Sts2.Core.Unlocks;
using STS2RitsuLib;

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

internal static partial class PreCombatSts2SimEligibility
{
    private static readonly Assembly VanillaGameAssembly = typeof(RunState).Assembly;
    private static readonly HashSet<string> TrustedPatchOwnerIds = new(StringComparer.OrdinalIgnoreCase)
    {
        Entry.ModId,
        "STS2-RitsuLib",
    };

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

        if (TryFindActiveRunModViolation(run, out string modReason))
        {
            return Ineligible("gameplay_mod", modReason, run, null);
        }

        if (TryFindCriticalHarmonyPatch(out string patchReason))
        {
            return Ineligible("gameplay_patch", patchReason, run, null);
        }

        Player? livePlayer = LocalContext.GetMe(run) ?? (run.Players.Count > 0 ? run.Players[0] : null);
        if (livePlayer is not null && TryFindForeignCardOnPlayPatch(livePlayer.Deck.Cards, out string deckCardPatchReason))
        {
            return Ineligible("gameplay_patch", deckCardPatchReason, run, null);
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

        if (HasUnsupportedExtraFields(new PreCombatExtraFieldsState(
                StartedWithNeow: run.ExtraFields.StartedWithNeow,
                FreedRepy: run.ExtraFields.FreedRepy,
                TestSubjectKills: run.ExtraFields.TestSubjectKills,
                UsedDebugCommand: run.ExtraFields.UsedDebugCommand,
                WongoPoints: player.ExtraFields.WongoPoints,
                IsPlayingRotatingCharacter: player.ExtraFields.IsPlayingRotatingCharacter)))
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

        if (TryFindNonVanillaReachablePools(player, out string poolReason))
        {
            return Ineligible("model_unmapped", poolReason, run, player);
        }

        int maxHandSize = RitsuLibFramework.GetMaxHandSize(player);
        if (maxHandSize != CardPile.MaxCardsInHand)
        {
            return Ineligible(
                "gameplay_mod",
                $"Non-vanilla max hand size {maxHandSize} (expected {CardPile.MaxCardsInHand}).",
                run,
                player);
        }

        if (player.Deck.Cards.Any(PredictionModModelSupport.HasBaseLibCardModifiers))
        {
            return Ineligible(
                "gameplay_mod",
                "Deck contains BaseLib CardModifier attachments not modeled by Sts2Sim.",
                run,
                player);
        }

        if (TryFindForeignCardOnPlayPatch(
                player.Deck.Cards.Concat(EnumerateReachableNativeCards(player)),
                out string patchReason))
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
        if (HasUnsupportedExtraFields(new PreCombatExtraFieldsState(
                StartedWithNeow: extras.StartedWithNeow,
                FreedRepy: extras.FreedRepy,
                TestSubjectKills: extras.TestSubjectKills,
                UsedDebugCommand: extras.UsedDebugCommand,
                WongoPoints: playerExtras.WongoPoints,
                IsPlayingRotatingCharacter: playerExtras.IsPlayingRotatingCharacter)))
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

        if (TryFindNonVanillaReachablePools(restoredPlannedPlayer, out string poolReason))
            return IneligiblePlanning("model_unmapped", poolReason);

        if (restoredPlannedPlayer.Deck.Cards.Any(PredictionModModelSupport.HasBaseLibCardModifiers))
        {
            return IneligiblePlanning(
                "gameplay_mod",
                "Planning deck contains BaseLib CardModifier attachments not modeled by Sts2Sim.");
        }

        if (TryFindForeignCardOnPlayPatch(
                restoredPlannedPlayer.Deck.Cards.Concat(EnumerateReachableNativeCards(restoredPlannedPlayer)),
                out string cardPatchReason))
        {
            return IneligiblePlanning("gameplay_patch", cardPatchReason);
        }

        return PreCombatEligibilityResult.Eligible(liveFingerprint);
    }

    public static string? TryDescribeDirectIneligibility(RunState run)
    {
        PreCombatEligibilityResult eligibility = AssessMandatoryLiveHost(run);
        if (eligibility.IsEligible)
            return null;
        return string.IsNullOrEmpty(eligibility.Detail)
            ? eligibility.FailureCode
            : $"{eligibility.FailureCode}:{eligibility.Detail}";
    }

    private static bool TryFindActiveRunModViolation(RunState run, out string reason)
    {
        foreach (AbstractModel subscriber in ModHelper.IterateAllRunStateSubscribers(run))
        {
            Type type = subscriber.GetType();
            Mod? mod = AssemblyInfo.ModForType(type, out bool isBaseGame);
            if (isBaseGame)
                continue;
            if (mod?.manifest?.affectsGameplay is false)
                continue;
            if (PredictionModHookSubscriberInertness.IsPreCombatInert(subscriber, out _))
                continue;

            string modId = mod?.manifest?.id ?? type.Assembly.GetName().Name ?? "unknown";
            reason = $"Active combat run subscriber {type.FullName} (mod={modId}).";
            return true;
        }

        try
        {
            PredictionRitsuCapabilityAudit.Validate();
            PredictionRitsuHealingAudit.ValidateGlobalListeners();
        }
        catch (PredictionUnsupportedException ex)
        {
            reason = ex.Message;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    internal static bool TryFindNonVanillaReachablePools(Player player, out string reason)
    {
        CardPoolModel[] cardPools =
        [
            player.Character.CardPool,
            ModelDb.CardPool<ColorlessCardPool>(),
            ModelDb.CardPool<StatusCardPool>(),
            ModelDb.CardPool<CurseCardPool>(),
        ];
        foreach (CardPoolModel pool in cardPools)
        {
            if (pool.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla card pool {pool.GetType().FullName}.";
                return true;
            }
            foreach (CardModel card in pool.AllCards)
            {
                if (card.GetType().Assembly != VanillaGameAssembly)
                {
                    reason = $"Non-vanilla reachable pool card {card.GetType().FullName}.";
                    return true;
                }
            }
        }

        PotionPoolModel[] potionPools =
        [
            player.Character.PotionPool,
            ModelDb.PotionPool<SharedPotionPool>(),
        ];
        foreach (PotionPoolModel pool in potionPools)
        {
            if (pool.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla potion pool {pool.GetType().FullName}.";
                return true;
            }
            foreach (PotionModel potion in pool.AllPotions)
            {
                if (potion.GetType().Assembly != VanillaGameAssembly)
                {
                    reason = $"Non-vanilla reachable pool potion {potion.GetType().FullName}.";
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }

    internal static IEnumerable<CardModel> EnumerateReachableNativeCards(Player player)
    {
        CardPoolModel[] mainPools =
        [
            player.Character.CardPool,
            ModelDb.CardPool<ColorlessCardPool>(),
        ];
        foreach (CardPoolModel pool in mainPools)
        {
            foreach (CardModel card in pool.AllCards)
            {
                if (card.CanBeGeneratedInCombat && card.Rarity is not (CardRarity.Ancient or CardRarity.Event))
                    yield return card;
            }
        }

        foreach (CardModel card in ModelDb.CardPool<StatusCardPool>().AllCards)
            yield return card;
        foreach (CardModel card in ModelDb.CardPool<CurseCardPool>().AllCards)
            yield return card;
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

    private static bool TryFindCriticalHarmonyPatch(out string reason)
    {
        foreach (MethodBase method in Harmony.GetAllPatchedMethods())
        {
            Type? declaring = method.DeclaringType;
            if (declaring is null || declaring.Assembly != VanillaGameAssembly)
                continue;

            Type rootDeclaring = declaring;
            while (rootDeclaring.DeclaringType is not null)
                rootDeclaring = rootDeclaring.DeclaringType;

            bool isConcreteCardGameplayOverride = typeof(CardModel).IsAssignableFrom(rootDeclaring)
                && rootDeclaring != typeof(CardModel)
                && IsConcreteCardGameplayOverride(rootDeclaring, declaring, method);
            string targetDisplayName = $"{declaring.FullName}.{method.Name}";

            PreCombatHarmonyTargetKind targetKind = ClassifyCriticalHarmonyTarget(
                rootDeclaring.Namespace,
                rootDeclaring.Name,
                method.Name,
                typeof(MonsterModel).IsAssignableFrom(rootDeclaring),
                isConcreteCardGameplayOverride,
                targetDisplayName);
            if (targetKind == PreCombatHarmonyTargetKind.None)
                continue;

            Patches? info = Harmony.GetPatchInfo(method);
            if (info is null)
                continue;

            bool trustGameplayNeutral = targetKind != PreCombatHarmonyTargetKind.Rng;
            foreach (Patch patch in info.Prefixes
                         .Concat(info.Postfixes)
                         .Concat(info.Transpilers)
                         .Concat(info.Finalizers)
                         .Concat(info.InnerPrefixes)
                         .Concat(info.InnerPostfixes))
            {
                PreCombatHarmonyPatchDescriptor descriptor = DescribeHarmonyPatch(
                    patch,
                    rootDeclaring,
                    method.Name,
                    targetDisplayName,
                    isConcreteCardGameplayOverride);
                if (IsForeignPatchDescriptor(descriptor, trustGameplayNeutral))
                {
                    reason = $"Foreign Harmony patch {descriptor.PatchDisplayName} " +
                             $"(owner={descriptor.Owner}) on {descriptor.TargetDisplayName}.";
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }

    internal static bool TryFindForeignCardOnPlayPatch(IEnumerable<CardModel> deckCards, out string reason)
    {
        HashSet<Type> checkedTypes = [];
        try
        {
            foreach (CardModel card in deckCards)
            {
                Type cardType = card.GetType();
                if (!checkedTypes.Add(cardType))
                    continue;

                _ = PredictionModPatchAudit.AuditCardOnPlay(
                    cardType,
                    adapted: false,
                    out PredictionModPatchAudit.ForeignPatch? firstForeign);
                var outcome = new PreCombatCardOnPlayAuditOutcome(
                    HasForeignPatch: firstForeign is not null,
                    HasBuiltInAdaptation: false,
                    ForeignDescription: firstForeign is { } foreign
                        ? $"{foreign.ModName} ({foreign.ModId}): {foreign.Description}"
                        : null);
                if (ShouldRejectCardOnPlayForSts2Sim(outcome, out reason))
                    return true;
            }
        }
        catch (Exception ex) when (ex is IncompatibleGameplayModException or PredictionUnsupportedException)
        {
            reason = ex.Message;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsConcreteCardGameplayOverride(Type cardType, Type declaring, MethodBase patchedMethod)
    {
        if (declaring == cardType)
        {
            if (TryExtractCompilerGeneratedEnclosingMethod(patchedMethod.Name, out string directEnclosing))
                return HasCardGameplayOverrideNamed(cardType, directEnclosing);
            return patchedMethod is MethodInfo directMethod
                && IsCardModelOrAbstractModelGameplayOverride(directMethod);
        }

        for (Type? current = cardType;
             current is not null && current != typeof(CardModel) && current != typeof(AbstractModel);
             current = current.BaseType)
        {
            foreach (MethodInfo candidate in current.GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (candidate.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType == declaring
                    || candidate.GetCustomAttribute<IteratorStateMachineAttribute>()?.StateMachineType == declaring)
                {
                    return IsCardModelOrAbstractModelGameplayOverride(candidate);
                }
            }
        }

        if (TryExtractCompilerGeneratedEnclosingMethod(patchedMethod.Name, out string methodEnclosing))
            return HasCardGameplayOverrideNamed(cardType, methodEnclosing);
        if (TryExtractCompilerGeneratedEnclosingMethod(declaring.Name, out string typeEnclosing))
            return HasCardGameplayOverrideNamed(cardType, typeEnclosing);
        return false;
    }

    private static bool HasCardGameplayOverrideNamed(Type cardType, string enclosingName)
    {
        for (Type? current = cardType;
             current is not null && current != typeof(CardModel) && current != typeof(AbstractModel);
             current = current.BaseType)
        {
            foreach (MethodInfo candidate in current.GetMethods(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (string.Equals(candidate.Name, enclosingName, StringComparison.Ordinal)
                    && IsCardModelOrAbstractModelGameplayOverride(candidate))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsCardModelOrAbstractModelGameplayOverride(MethodInfo method)
    {
        if (!method.IsVirtual)
            return false;
        MethodInfo baseDefinition = method.GetBaseDefinition();
        Type? baseDeclaring = baseDefinition.DeclaringType;
        if (baseDeclaring != typeof(CardModel) && baseDeclaring != typeof(AbstractModel))
            return false;
        return !IsPureCardDisplayOrMetadataMember(baseDefinition.Name);
    }

    private static PreCombatHarmonyPatchDescriptor DescribeHarmonyPatch(
        Patch patch,
        Type rootDeclaring,
        string methodName,
        string targetDisplayName,
        bool isConcreteCardGameplayOverride)
    {
        Type? patchType = patch.PatchMethod.DeclaringType;
        bool isBaseGamePatch = false;
        bool isTrustedOwner = false;
        bool? affectsGameplay = null;
        bool hasKnownModId = false;
        string patchDisplayName;
        if (patchType is null)
        {
            patchDisplayName = patch.PatchMethod.ToString() ?? "<null>";
        }
        else
        {
            patchDisplayName = $"{patchType.FullName}.{patch.PatchMethod.Name}";
            Mod? mod = AssemblyInfo.ModForType(patchType, out bool isBaseGame);
            if (isBaseGame)
            {
                isBaseGamePatch = true;
            }
            else if (mod?.manifest?.id is { } modId)
            {
                hasKnownModId = true;
                isTrustedOwner = TrustedPatchOwnerIds.Contains(modId);
                affectsGameplay = mod.manifest.affectsGameplay;
            }
            else if (patchType.Assembly == typeof(Entry).Assembly || patchType.Assembly == VanillaGameAssembly)
            {
                isBaseGamePatch = true;
            }
        }

        return new PreCombatHarmonyPatchDescriptor(
            DeclaringNamespace: rootDeclaring.Namespace,
            RootDeclaringTypeName: rootDeclaring.Name,
            MethodName: methodName,
            IsMonsterModel: typeof(MonsterModel).IsAssignableFrom(rootDeclaring),
            IsBaseGamePatch: isBaseGamePatch,
            IsTrustedOwner: isTrustedOwner,
            AffectsGameplay: affectsGameplay,
            HasKnownModId: hasKnownModId,
            PatchDisplayName: patchDisplayName,
            TargetDisplayName: targetDisplayName,
            Owner: patch.owner ?? string.Empty,
            IsConcreteCardGameplayOverride: isConcreteCardGameplayOverride);
    }

    internal static bool HasFullEpochUnlocks(IEnumerable<string>? unlockedEpochs)
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
            $"debug={run.ExtraFields.UsedDebugCommand}",
            precondition,
            mods);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
