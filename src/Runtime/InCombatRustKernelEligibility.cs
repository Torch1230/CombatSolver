using System.Reflection;
using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Modifiers;
using MegaCrit.Sts2.Core.Models.PotionPools;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Timeline;
using STS2RitsuLib;

namespace CombatSolver;

internal readonly record struct RustKernelEligibilityResult(
    bool IsEligible,
    string? FailureCode,
    string? Detail)
{
    public static RustKernelEligibilityResult Eligible() => new(true, null, null);

    public static RustKernelEligibilityResult Ineligible(string failureCode, string detail) =>
        new(false, failureCode, detail);
}

internal static class InCombatRustKernelEligibility
{
    private static readonly Assembly VanillaGameAssembly = typeof(RunState).Assembly;

    private static readonly PropertyInfo? BaseLibCardModifiersProperty = ResolveBaseLibCardModifiersProperty();

    private static readonly HashSet<Type> SupportedVanillaModifierTypes =
    [
        typeof(AllStar),
        typeof(BigGameHunter),
        typeof(CharacterCards),
        typeof(CursedRun),
        typeof(DeadlyEvents),
        typeof(Draft),
        typeof(Flight),
        typeof(Hoarder),
        typeof(Insanity),
        typeof(Midas),
        typeof(Murderous),
        typeof(NightTerrors),
        typeof(SealedDeck),
        typeof(Specialized),
        typeof(Terminal),
        typeof(Vintage),
    ];

    public static RustKernelEligibilityResult AssessHostAndCombat(
        CombatState state,
        SolverSettingsSnapshot settings)
    {
        if (!settings.UseRustCombatKernel)
        {
            return RustKernelEligibilityResult.Ineligible(
                "user_disabled",
                "In-combat Rust combat kernel is disabled in settings.");
        }

        return AssessHostEligibility(state);
    }

    public static RustKernelEligibilityResult AssessHostEligibility(CombatState state)
    {
        if (!CombatManager.Instance.IsInProgress
            || state.CurrentSide != CombatSide.Player
            || !RunManager.Instance.IsSingleplayerOrFakeMultiplayer
            || state.Players.Count != 1)
        {
            return RustKernelEligibilityResult.Ineligible(
                "live_combat_precondition",
                "Rust combat kernel requires single-player active combat on the player turn.");
        }

        if (state.RunState is not RunState run || run.Players.Count != 1)
        {
            return RustKernelEligibilityResult.Ineligible(
                "live_combat_precondition",
                "Rust combat kernel requires a concrete single-player RunState.");
        }

        if (run.GameMode is not (GameMode.Standard or GameMode.Daily or GameMode.Dread or GameMode.Blitz))
        {
            return RustKernelEligibilityResult.Ineligible(
                "non_standard_mode",
                $"Game mode {run.GameMode} routes to HotKernel.");
        }

        foreach (ModifierModel modifier in run.Modifiers.Concat(state.Modifiers))
        {
            Type modifierType = modifier.GetType();
            if (modifierType.Assembly != VanillaGameAssembly || !SupportedVanillaModifierTypes.Contains(modifierType))
            {
                return RustKernelEligibilityResult.Ineligible(
                    "modifiers_present",
                    $"Combat or run has unsupported modifier {modifierType.FullName}.");
            }
        }

        if (run.RunModels.Any(m => m.GetType().Assembly != VanillaGameAssembly))
        {
            return RustKernelEligibilityResult.Ineligible(
                "non_standard_run_models",
                "Run models contain non-vanilla entries.");
        }

        if (state.Encounter is null
            || state.Encounter.GetType().Assembly != VanillaGameAssembly
            || state.Encounter.RoomType is not (RoomType.Monster or RoomType.Elite or RoomType.Boss))
        {
            return RustKernelEligibilityResult.Ineligible(
                "encounter_unmapped",
                $"Encounter '{state.Encounter?.Id.Entry ?? "null"}' is outside mapped vanilla combat rooms.");
        }

        if (state.Dominion is not null && state.Dominion.GetType().Assembly != VanillaGameAssembly)
        {
            return RustKernelEligibilityResult.Ineligible(
                "gameplay_mod",
                $"Combat has non-vanilla Dominion {state.Dominion.GetType().FullName}.");
        }

        if (state.BadgeModels.Any(b => b.GetType().Assembly != VanillaGameAssembly))
        {
            return RustKernelEligibilityResult.Ineligible(
                "gameplay_mod",
                "Combat has non-vanilla BadgeModels.");
        }

        if (TryFindActiveCombatModViolation(state, out string combatModReason))
        {
            return RustKernelEligibilityResult.Ineligible("gameplay_mod", combatModReason);
        }

        Player player = LocalContext.GetMe(state) ?? state.Players[0];
        PlayerCombatState? pcs = player.PlayerCombatState;
        if (pcs is null || pcs.Phase != PlayerTurnPhase.Play)
        {
            return RustKernelEligibilityResult.Ineligible(
                "live_combat_precondition",
                "Player combat state is missing or not in PlayerTurnPhase.Play.");
        }

        if (run.ExtraFields.TestSubjectKills < 0 || player.ExtraFields.WongoPoints < 0)
        {
            return RustKernelEligibilityResult.Ineligible(
                "extra_field_unsupported",
                "Run or player extra fields contain state outside Rust combat kernel support.");
        }

        if (!HasFullEpochUnlocks(player.UnlockState.ToSerializable().UnlockedEpochs))
        {
            return RustKernelEligibilityResult.Ineligible(
                "partial_unlocks",
                "Partial epoch unlock state routes to HotKernel.");
        }

        int maxHandSize = RitsuLibFramework.GetMaxHandSize(player);
        if (maxHandSize != CardPile.MaxCardsInHand)
        {
            return RustKernelEligibilityResult.Ineligible(
                "gameplay_mod",
                $"Non-vanilla max hand size {maxHandSize} (expected {CardPile.MaxCardsInHand}).");
        }

        try
        {
            VanillaRootGate.ValidateVanillaOnly(state);
        }
        catch (IncompatibleGameplayModException ex)
        {
            return RustKernelEligibilityResult.Ineligible("model_unmapped", ex.Message);
        }

        if (TryFindNonVanillaDeckOrActs(run, player, out string nonVanillaReason))
        {
            return RustKernelEligibilityResult.Ineligible("model_unmapped", nonVanillaReason);
        }

        if (TryFindNonVanillaReachablePools(player, out string poolReason))
        {
            return RustKernelEligibilityResult.Ineligible("model_unmapped", poolReason);
        }

        IEnumerable<CardModel> allLiveCards = player.Deck.Cards
            .Concat(pcs.AllPiles.SelectMany(static p => p.Cards));
        if (HasBaseLibCardModifiers(allLiveCards))
        {
            return RustKernelEligibilityResult.Ineligible(
                "gameplay_mod",
                "Combat cards contain BaseLib CardModifier attachments not modeled by Rust combat kernel.");
        }

        return RustKernelEligibilityResult.Eligible();
    }

    public static RustKernelEligibilityResult AssessSearchPolicy(SearchPolicySnapshot searchPolicy)
    {
        ArgumentNullException.ThrowIfNull(searchPolicy);

        if (searchPolicy.VerifyIncrementalSearch)
        {
            return RustKernelEligibilityResult.Ineligible(
                "policy_unsupported",
                "VerifyIncrementalSearch runs on HotKernel.");
        }
        if (searchPolicy.MeasurePhasePerformance)
        {
            return RustKernelEligibilityResult.Ineligible(
                "policy_unsupported",
                "MeasurePhasePerformance runs on HotKernel.");
        }
        if (searchPolicy.IncludeTurnSetup)
        {
            return RustKernelEligibilityResult.Ineligible(
                "policy_unsupported",
                "IncludeTurnSetup runs on HotKernel.");
        }
        if (searchPolicy.TranspositionPruningDisabledMask != 0)
        {
            return RustKernelEligibilityResult.Ineligible(
                "policy_unsupported",
                "TranspositionPruningDisabledMask is not supported by Rust combat kernel.");
        }

        GrowthValues growth = searchPolicy.GrowthBudgets;
        Dictionary<string, int>? extras = growth.Extras.ToDictionary();
        if (growth.ShowOff != 0 || (extras is not null && extras.Values.Any(static v => v != 0)))
        {
            return RustKernelEligibilityResult.Ineligible(
                "policy_unsupported",
                "ShowOff or third-party GrowthBudgets are nonzero and not supported by Rust combat kernel.");
        }

        return RustKernelEligibilityResult.Eligible();
    }

    private static bool HasFullEpochUnlocks(IEnumerable<string>? unlockedEpochs)
    {
        if (unlockedEpochs is null)
            return false;
        HashSet<string> unlocked = unlockedEpochs as HashSet<string>
            ?? new HashSet<string>(unlockedEpochs, StringComparer.Ordinal);
        foreach (string requiredEpoch in EpochModel.AllEpochIds)
        {
            if (!unlocked.Contains(requiredEpoch))
                return false;
        }
        return true;
    }

    private static bool TryFindNonVanillaDeckOrActs(RunState run, Player player, out string reason)
    {
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
            if (card.Enchantment is { } ench && ench.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla enchantment {ench.GetType().FullName}.";
                return true;
            }
            if (card.Affliction is { } aff && aff.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla affliction {aff.GetType().FullName}.";
                return true;
            }
        }
        reason = string.Empty;
        return false;
    }

    private static bool TryFindNonVanillaReachablePools(Player player, out string reason)
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

    private static bool TryFindActiveCombatModViolation(CombatState state, out string reason)
    {
        foreach (AbstractModel subscriber in ModHelper.IterateAllCombatStateSubscribers(state))
        {
            Type type = subscriber.GetType();
            Mod? mod = AssemblyInfo.ModForType(type, out bool isBaseGame);
            if (isBaseGame)
                continue;
            if (mod?.manifest?.affectsGameplay is false)
                continue;

            string modId = mod?.manifest?.id ?? type.Assembly.GetName().Name ?? "unknown";
            if (string.Equals(modId, Entry.ModId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(modId, "STS2-RitsuLib", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            reason = $"Active combat subscriber {type.FullName} (mod={modId}).";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static PropertyInfo? ResolveBaseLibCardModifiersProperty()
    {
        Type? extType = Type.GetType("BaseLib.Patches.Content.CardModelExtensions, BaseLib", throwOnError: false);
        return extType?.GetProperty("CardModifiers", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static bool HasBaseLibCardModifiers(IEnumerable<CardModel> cards)
    {
        if (BaseLibCardModifiersProperty is null)
            return false;
        foreach (CardModel card in cards)
        {
            if (BaseLibCardModifiersProperty.GetValue(null, [card]) is System.Collections.ICollection { Count: > 0 })
                return true;
        }
        return false;
    }
}
