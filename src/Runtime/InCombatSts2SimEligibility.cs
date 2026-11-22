using System.Reflection;
using CombatSolver.Api;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;

namespace CombatSolver;

internal readonly record struct InCombatEligibilityResult(
    bool IsEligible,
    string? FailureCode,
    string? Detail)
{
    public static InCombatEligibilityResult Eligible() => new(true, null, null);

    public static InCombatEligibilityResult Ineligible(string failureCode, string detail) =>
        new(false, failureCode, detail);
}

internal static class InCombatSts2SimEligibility
{
    private static readonly Assembly VanillaGameAssembly = typeof(RunState).Assembly;

    public static InCombatEligibilityResult Assess(
        CombatState state,
        SolverSettingsSnapshot settings,
        SearchPolicySnapshot searchPolicy)
    {
        if (!settings.UseSts2SimInCombat)
        {
            return InCombatEligibilityResult.Ineligible(
                "user_disabled",
                "In-combat Sts2Sim simulator is disabled in settings.");
        }

        if (!CombatManager.Instance.IsInProgress
            || state.CurrentSide != CombatSide.Player
            || !RunManager.Instance.IsSingleplayerOrFakeMultiplayer
            || state.Players.Count != 1)
        {
            return InCombatEligibilityResult.Ineligible(
                "live_combat_precondition",
                "In-combat Sts2Sim requires single-player active combat on the player turn.");
        }

        if (state.RunState is not RunState run || run.Players.Count != 1)
        {
            return InCombatEligibilityResult.Ineligible(
                "live_combat_precondition",
                "In-combat Sts2Sim requires a concrete single-player RunState.");
        }

        PreCombatEligibilityResult hostResult = PreCombatSts2SimEligibility.AssessMandatoryLiveHost(run, forInCombat: true);
        if (!hostResult.IsEligible)
        {
            return InCombatEligibilityResult.Ineligible(
                hostResult.FailureCode ?? "gameplay_mod",
                hostResult.Detail ?? "Mandatory live host check excluded Sts2Sim.");
        }

        if (TryFindActiveCombatModViolation(state, out string combatModReason))
        {
            return InCombatEligibilityResult.Ineligible("gameplay_mod", combatModReason);
        }

        if (state.Encounter is null
            || state.Encounter.GetType().Assembly != VanillaGameAssembly
            || state.Encounter.RoomType is not (RoomType.Monster or RoomType.Elite or RoomType.Boss))
        {
            return InCombatEligibilityResult.Ineligible(
                "encounter_unmapped",
                $"Encounter '{state.Encounter?.Id.Entry ?? "null"}' is outside mapped vanilla combat rooms.");
        }

        if (state.Modifiers.Count > 0 || state.Dominion is not null)
        {
            return InCombatEligibilityResult.Ineligible(
                "modifiers_present",
                "Combat has modifiers or dominion outside vanilla Standard.");
        }
        if (state.BadgeModels.Any(b => b.GetType().Assembly != VanillaGameAssembly))
        {
            return InCombatEligibilityResult.Ineligible(
                "gameplay_mod",
                "Combat has non-vanilla BadgeModels.");
        }

        Player player = LocalContext.GetMe(state) ?? state.Players[0];
        PlayerCombatState? pcs = player.PlayerCombatState;
        if (pcs is null || pcs.Phase != PlayerTurnPhase.Play)
        {
            return InCombatEligibilityResult.Ineligible(
                "live_combat_precondition",
                "Player combat state is missing or not in PlayerTurnPhase.Play.");
        }

        if (player.ExtraFields.WongoPoints != 0
            || player.ExtraFields.IsPlayingRotatingCharacter
            || run.ExtraFields.FreedRepy
            || run.ExtraFields.UsedDebugCommand
            || !run.ExtraFields.StartedWithNeow)
        {
            return InCombatEligibilityResult.Ineligible(
                "extra_field_unsupported",
                "Run or player extra fields contain state outside Sts2Sim bridge support.");
        }

        if (!PreCombatSts2SimEligibility.HasFullEpochUnlocks(player.UnlockState.ToSerializable().UnlockedEpochs))
        {
            return InCombatEligibilityResult.Ineligible(
                "partial_unlocks",
                "Partial epoch unlock state routes to the built-in solver.");
        }

        int actHistoryCount = run.CurrentActIndex >= 0 && run.CurrentActIndex < run.MapPointHistory.Count
            ? run.MapPointHistory[run.CurrentActIndex].Count
            : 0;
        if (run.VisitedMapCoords.Count != actHistoryCount)
        {
            return InCombatEligibilityResult.Ineligible(
                "map_topology_unsupported",
                $"VisitedMapCoords count {run.VisitedMapCoords.Count} disagrees with current act history count {actHistoryCount}.");
        }

        int maxHandSize = RitsuLibFramework.GetMaxHandSize(player);
        if (maxHandSize != CardPile.MaxCardsInHand)
        {
            return InCombatEligibilityResult.Ineligible(
                "gameplay_mod",
                $"Non-vanilla max hand size {maxHandSize} (expected {CardPile.MaxCardsInHand}).");
        }

        if (TryFindUnsupportedSearchPolicy(searchPolicy, out string policyReason))
        {
            return InCombatEligibilityResult.Ineligible("policy_unsupported", policyReason);
        }

        if (TryFindNonVanillaCombatModels(state, player, pcs, out string modelReason))
        {
            return InCombatEligibilityResult.Ineligible("model_unmapped", modelReason);
        }

        if (PreCombatSts2SimEligibility.TryFindNonVanillaReachablePools(player, out string poolReason))
        {
            return InCombatEligibilityResult.Ineligible("model_unmapped", poolReason);
        }

        IEnumerable<CardModel> allLiveCards = player.Deck.Cards
            .Concat(pcs.AllPiles.SelectMany(static p => p.Cards));
        if (allLiveCards.Any(PredictionModModelSupport.HasBaseLibCardModifiers))
        {
            return InCombatEligibilityResult.Ineligible(
                "gameplay_mod",
                "Combat cards contain BaseLib CardModifier attachments not modeled by Sts2Sim.");
        }

        if (PreCombatSts2SimEligibility.TryFindForeignCardOnPlayPatch(
                allLiveCards.Concat(PreCombatSts2SimEligibility.EnumerateReachableNativeCards(player)),
                out string cardPatchReason))
        {
            return InCombatEligibilityResult.Ineligible("gameplay_patch", cardPatchReason);
        }

        return InCombatEligibilityResult.Eligible();
    }

    private static bool TryFindUnsupportedSearchPolicy(SearchPolicySnapshot policy, out string reason)
    {
        if (policy.VerifyIncrementalSearch)
        {
            reason = "VerifyIncrementalSearch requires the built-in incremental search verifier.";
            return true;
        }
        if (policy.MeasurePhasePerformance)
        {
            reason = "MeasurePhasePerformance targets built-in solver metrics.";
            return true;
        }
        if (policy.IncludeTurnSetup)
        {
            reason = "Turn-setup root exploration uses the built-in solver.";
            return true;
        }
        if (policy.UseNoveltyPortfolio)
        {
            reason = "UseNoveltyPortfolio is active and not mapped to Sts2Sim bridge.";
            return true;
        }
        if (policy.EarlyTurnExplorationDepth > 0)
        {
            reason = "EarlyTurnExploration is active and not mapped to Sts2Sim bridge.";
            return true;
        }
        if (policy.BossTempoSearch is not null)
        {
            reason = "BossTempoSearch experiment flags are not mapped to Sts2Sim bridge.";
            return true;
        }
        if (policy.PileOrderInvariantMask != 0
            || policy.TranspositionPruningDisabledMask != 0
            || policy.StateKeySalt != 0
            || policy.MemoryNoProgressRecoveryLimit != 0
            || policy.BeamWidthPortfolioWidths != null
            || !policy.BeamWidthPortfolioPlainBaselineMember
            || policy.Profile.BeamWeightPerturbation != null
            || policy.DevelopmentStrategy != null
            || policy.PortfolioExperiment != null)
        {
            reason = "Built-in solver ablation options are active.";
            return true;
        }
        GrowthValues growth = policy.GrowthBudgets;
        Dictionary<string, int>? extras = growth.Extras.ToDictionary();
        if (growth.ShowOff != 0 || (extras is not null && extras.Values.Any(static v => v != 0)))
        {
            reason = "ShowOff or third-party GrowthBudgets are nonzero and not mapped to Sts2Sim bridge.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool TryFindNonVanillaCombatModels(
        CombatState state,
        Player player,
        PlayerCombatState pcs,
        out string reason)
    {
        if (player.Character.GetType().Assembly != VanillaGameAssembly)
        {
            reason = $"Non-vanilla character {player.Character.GetType().FullName}.";
            return true;
        }
        foreach (ActModel act in state.RunState.Acts)
        {
            if (act.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla act {act.GetType().FullName}.";
                return true;
            }
        }
        foreach (CardModel card in player.Deck.Cards.Concat(pcs.AllPiles.SelectMany(static p => p.Cards)))
        {
            if (card.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla card {card.GetType().FullName}.";
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
        foreach (OrbModel orb in pcs.OrbQueue.Orbs)
        {
            if (orb.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla orb {orb.GetType().FullName}.";
                return true;
            }
        }
        foreach (Creature creature in state.Allies.Concat(state.Enemies).Concat(state.EscapedCreatures))
        {
            if (creature.Monster is { } m && m.GetType().Assembly != VanillaGameAssembly)
            {
                reason = $"Non-vanilla monster {m.GetType().FullName}.";
                return true;
            }
            foreach (PowerModel power in creature.Powers)
            {
                if (power.GetType().Assembly != VanillaGameAssembly)
                {
                    reason = $"Non-vanilla power {power.GetType().FullName}.";
                    return true;
                }
            }
        }

        reason = string.Empty;
        return false;
    }

    internal static bool TryFindActiveCombatModViolation(CombatState state, out string reason)
    {
        foreach (AbstractModel subscriber in ModHelper.IterateAllCombatStateSubscribers(state))
        {
            Type type = subscriber.GetType();
            Mod? mod = AssemblyInfo.ModForType(type, out bool isBaseGame);
            if (isBaseGame)
                continue;
            if (mod?.manifest?.affectsGameplay is false)
                continue;
            if (PredictionModHookSubscriberInertness.IsCombatInert(subscriber, out _))
                continue;

            string modId = mod?.manifest?.id ?? type.Assembly.GetName().Name ?? "unknown";
            if (string.Equals(modId, Entry.ModId, StringComparison.OrdinalIgnoreCase))
                continue;

            reason = $"Active combat subscriber {type.FullName} (mod={modId}).";
            return true;
        }

        reason = string.Empty;
        return false;
    }
}
