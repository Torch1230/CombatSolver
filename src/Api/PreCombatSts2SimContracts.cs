using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CombatSolver.Api;

internal static class PreCombatSts2SimAbi
{
    public const int Version = 1;
    public const string SupportedGameVersion = "v0.113.1";
    public const string SupportedGameCommit = "d09d48e5";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static readonly FrozenSet<string> AllowlistedCapabilityMissCodes = new[]
    {
        "dread_mode",
        "non_standard_mode",
        "custom_game",
        "daily_run",
        "modifiers_present",
        "non_standard_run_models",
        "game_build_unsupported",
        "partial_unlocks",
        "gameplay_mod",
        "gameplay_patch",
        "bridge_absent",
        "live_between_room_precondition",
        "save_schema_unsupported",
        "encounter_unmapped",
        "room_kind_mismatch",
        "model_unmapped",
        "saved_property_unsupported",
        "extra_field_unsupported",
        "enchantment_props_unsupported",
        "map_topology_unsupported",
        "planning_pre_finished_room",
        "planning_spoils_map_unsaved",
        "planning_revisited_coord",
        "opening_choice_unsupported",
        "projection_unsupported",
        "policy_unsupported",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly FrozenSet<string> AllowedSearchBoundaries = new[]
    {
        "None",
        "NodeLimit",
        "TimeLimit",
        "TurnLimit",
        "PendingChoice",
        "EventDefeat",
        "UnsupportedEffect",
    }.ToFrozenSet(StringComparer.Ordinal);
}

internal sealed record Sts2SimMapCoordDto(int Col, int Row);

internal sealed record Sts2SimMapStepDto(
    Sts2SimMapCoordDto Coordinate,
    string RoomType,
    string MapPointType);

internal sealed record Sts2SimSpoilsMapEntryDto(
    int DeckIndex,
    Sts2SimMapCoordDto? Coord);

internal sealed record Sts2SimSpoilsMapVerificationDto(
    int ExpectedDeckCount,
    IReadOnlyList<string> ExpectedCardIds,
    IReadOnlyList<Sts2SimSpoilsMapEntryDto> Entries);

internal sealed record Sts2SimPotionDirectiveDto(
    int Slot,
    string PotionId,
    string Directive);

internal sealed record Sts2SimRelicCounterRuleDto(
    string Id,
    bool Enabled,
    int Minimum,
    int Maximum,
    int HpAllowance,
    int Priority);

internal sealed record Sts2SimGrowthBudgetsDto(
    int HandOfGreed,
    int TheHunt,
    int Feed,
    int Royalties,
    int Alchemize,
    int GeneticAlgorithm,
    int TheScythe,
    int Goopy,
    int ForbiddenGrimoire,
    int MadScience,
    int ShowOff,
    IReadOnlyDictionary<string, int>? ThirdParty);

internal sealed record Sts2SimNoveltySearchOptionsDto(
    int Width,
    int FamiliarAllowance,
    int MaxOpen,
    int MaxNoveltyEntries,
    int MaxActions,
    int MaxTurns);

internal sealed record Sts2SimNoveltyPortfolioBudgetDto(
    int TimeDivisor,
    int MaximumMilliseconds,
    int MaximumExpandedNodes);

internal sealed record Sts2SimPolicyDto(
    string PotionPolicy,
    IReadOnlyList<Sts2SimPotionDirectiveDto> PotionDirectives,
    bool PredictPotionReward,
    bool RelicStrategyEnabled,
    IReadOnlyList<Sts2SimRelicCounterRuleDto> RelicCounterRules,
    Sts2SimGrowthBudgetsDto GrowthBudgets,
    int? BrightestFlameMaxHpLossLimit,
    bool IgnoreLongTermRewards,
    string ActTransitionBossHpStrategy,
    string FinalBossHpStrategy,
    int AcceptableBattleHpLoss,
    bool StopAtAcceptableBattleHpLoss,
    bool UseBeamWidthPortfolio,
    bool UseNoveltyPortfolio,
    bool UseEarlyTurnExploration,
    int BeamWidth,
    int MaxExpandedNodes,
    int MaxCardBranchesPerNode,
    int MaxPileChoiceBranchesPerAction,
    int MaxHandChoiceBranchesPerAction,
    int SoftTimeBudgetMilliseconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PortfolioOptionsVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? BeamWidthPortfolioWidths = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BeamWidthPortfolioPlainBaselineMember = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ReallocatedRefinementPortfolio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? OffensiveRefinementPortfolio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BoundedOffensiveRefinementPortfolio = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? StopPortfolioAtHpTarget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AdaptiveNoveltyRefinement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Sts2SimNoveltySearchOptionsDto? NoveltySearch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Sts2SimNoveltyPortfolioBudgetDto? NoveltyBudget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EarlyTurnExplorationDepth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EarlyTurnExplorationBudgetMilliseconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PortfolioSchedulingMode = null);

internal sealed record Sts2SimBridgeRequestDto(
    int AbiVersion,
    string RequestId,
    string NormalizedSaveJson,
    bool HasLiveSidecar,
    int? LiveActFloor,
    int? NextRoomId,
    Sts2SimSpoilsMapVerificationDto? SpoilsMapVerification,
    string EncounterId,
    int TargetActFloor,
    int TargetMapColumn,
    string RoomKind,
    string MapPointKind,
    bool IsSecondBoss,
    IReadOnlyList<Sts2SimMapStepDto> InterveningMapPoints,
    int? PlayerCurrentHpOverride,
    ulong? SimulationSeed,
    int SearchBudgetMilliseconds,
    int OverallTimeoutMilliseconds,
    int MaxDegreeOfParallelism,
    Sts2SimPolicyDto Policy);

internal sealed record Sts2SimPotionUseDto(
    string Id,
    int? Turn,
    int? Slot);

internal sealed record Sts2SimBridgeResponseDto(
    int AbiVersion,
    string Outcome,
    string? FailureCode,
    string? FieldPath,
    string? Error,
    int? ProjectedHpLoss,
    int? FinalHp,
    int? CombatEndedTurn,
    bool? OnlyDeathRoutes,
    string? Confidence,
    string? SearchBoundary,
    IReadOnlyList<Sts2SimPotionUseDto>? PotionUses,
    long? ExpandedNodes,
    int? EffectiveDegreeOfParallelism,
    double? SearchElapsedMilliseconds,
    double? TotalElapsedMilliseconds);

internal sealed record Sts2SimBundleFileManifestEntry(
    string Name,
    string Sha256,
    string? Mvid);

internal sealed record Sts2SimBundleManifest(
    int AbiVersion,
    string SupportedGameVersion,
    string SupportedGameCommit,
    IReadOnlyList<Sts2SimBundleFileManifestEntry> Files);

internal sealed record Sts2SimBundleIdentityDto(
    int AbiVersion,
    string SupportedGameVersion,
    string SupportedGameCommit,
    string BridgeMvid,
    string SolverMvid,
    string CoreMvid);

internal static class PreCombatSts2SimWireValidator
{
    public static void ValidatePolicy(
        Sts2SimPolicyDto policy,
        Sts2SimBridgeCapabilitiesDto? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidatePortfolioPolicyOptions(
            policy.UseNoveltyPortfolio,
            policy.UseEarlyTurnExploration,
            policy.PortfolioOptionsVersion,
            policy.BeamWidthPortfolioWidths,
            policy.BeamWidthPortfolioPlainBaselineMember,
            policy.ReallocatedRefinementPortfolio,
            policy.OffensiveRefinementPortfolio,
            policy.BoundedOffensiveRefinementPortfolio,
            policy.StopPortfolioAtHpTarget,
            policy.AdaptiveNoveltyRefinement,
            policy.NoveltySearch,
            policy.NoveltyBudget,
            policy.EarlyTurnExplorationDepth,
            policy.EarlyTurnExplorationBudgetMilliseconds,
            policy.PortfolioSchedulingMode,
            capabilities);
    }

    internal static void ValidatePortfolioPolicyOptions(
        bool useNoveltyPortfolio,
        bool useEarlyTurnExploration,
        int? portfolioOptionsVersion,
        IReadOnlyList<int>? beamWidthPortfolioWidths,
        bool? beamWidthPortfolioPlainBaselineMember,
        bool? reallocatedRefinementPortfolio,
        bool? offensiveRefinementPortfolio,
        bool? boundedOffensiveRefinementPortfolio,
        bool? stopPortfolioAtHpTarget,
        bool? adaptiveNoveltyRefinement,
        Sts2SimNoveltySearchOptionsDto? noveltySearch,
        Sts2SimNoveltyPortfolioBudgetDto? noveltyBudget,
        int? earlyTurnExplorationDepth,
        int? earlyTurnExplorationBudgetMilliseconds,
        string? portfolioSchedulingMode,
        Sts2SimBridgeCapabilitiesDto? capabilities)
    {
        if (portfolioOptionsVersion is not null && portfolioOptionsVersion is not (1 or 2))
        {
            throw new InvalidDataException(
                $"Sts2Sim policy reported unsupported PortfolioOptionsVersion={portfolioOptionsVersion}.");
        }
        if (beamWidthPortfolioWidths is not null)
        {
            if (beamWidthPortfolioWidths.Count > 16)
            {
                throw new InvalidDataException(
                    $"Sts2Sim policy BeamWidthPortfolioWidths count {beamWidthPortfolioWidths.Count} exceeds maximum 16.");
            }
            for (int i = 0; i < beamWidthPortfolioWidths.Count; i++)
            {
                if (beamWidthPortfolioWidths[i] < 1)
                {
                    throw new InvalidDataException(
                        $"Sts2Sim policy BeamWidthPortfolioWidths[{i}] must be >= 1, got {beamWidthPortfolioWidths[i]}.");
                }
            }
        }
        if (offensiveRefinementPortfolio == true && boundedOffensiveRefinementPortfolio == true)
        {
            throw new InvalidDataException(
                "Sts2Sim policy cannot enable both OffensiveRefinementPortfolio and BoundedOffensiveRefinementPortfolio.");
        }
        if (noveltySearch is not null)
        {
            if (noveltySearch.Width is not (1 or 2)
                || noveltySearch.FamiliarAllowance < 0
                || noveltySearch.MaxOpen < 1
                || noveltySearch.MaxNoveltyEntries < 1
                || noveltySearch.MaxActions < 1
                || noveltySearch.MaxTurns < 1)
            {
                throw new InvalidDataException("Sts2Sim policy NoveltySearch contains invalid bounds.");
            }
        }
        if (noveltyBudget is not null)
        {
            if (noveltyBudget.TimeDivisor < 2
                || noveltyBudget.MaximumMilliseconds < 1
                || noveltyBudget.MaximumExpandedNodes < 1)
            {
                throw new InvalidDataException("Sts2Sim policy NoveltyBudget contains invalid bounds.");
            }
        }
        if (earlyTurnExplorationDepth is < 0 or > 8)
        {
            throw new InvalidDataException(
                $"Sts2Sim policy EarlyTurnExplorationDepth must be between 0 and 8, got {earlyTurnExplorationDepth}.");
        }
        if (earlyTurnExplorationBudgetMilliseconds is < 0)
        {
            throw new InvalidDataException(
                $"Sts2Sim policy EarlyTurnExplorationBudgetMilliseconds cannot be negative ({earlyTurnExplorationBudgetMilliseconds}).");
        }
        if (earlyTurnExplorationDepth is > 0)
        {
            if (earlyTurnExplorationBudgetMilliseconds is null or < 1)
            {
                throw new InvalidDataException(
                    "Sts2Sim policy requires positive EarlyTurnExplorationBudgetMilliseconds when EarlyTurnExplorationDepth > 0.");
            }
            if (capabilities is not null)
            {
                if (capabilities.PortfolioOptionsVersion < 2 || !capabilities.SupportsEarlyTurnExploration)
                {
                    throw new InvalidDataException(
                        "Sts2Sim policy requested EarlyTurnExploration without negotiated support.");
                }
                if (earlyTurnExplorationDepth.Value > capabilities.MaxEarlyTurnExplorationDepth)
                {
                    throw new InvalidDataException(
                        $"Sts2Sim policy EarlyTurnExplorationDepth {earlyTurnExplorationDepth.Value} exceeds capability limit {capabilities.MaxEarlyTurnExplorationDepth}.");
                }
                if (earlyTurnExplorationBudgetMilliseconds.Value > capabilities.MaxEarlyTurnExplorationBudgetMilliseconds)
                {
                    throw new InvalidDataException(
                        $"Sts2Sim policy EarlyTurnExplorationBudgetMilliseconds {earlyTurnExplorationBudgetMilliseconds.Value} exceeds capability limit {capabilities.MaxEarlyTurnExplorationBudgetMilliseconds}.");
                }
            }
        }
        if (portfolioSchedulingMode is not null
            && !string.Equals(portfolioSchedulingMode, "Parallel", StringComparison.Ordinal)
            && !string.Equals(portfolioSchedulingMode, "Sequential", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Sts2Sim policy reported invalid PortfolioSchedulingMode '{portfolioSchedulingMode}'.");
        }
        if (capabilities is not null)
        {
            if (portfolioOptionsVersion is > 1 && capabilities.PortfolioOptionsVersion < 2)
            {
                throw new InvalidDataException(
                    $"Sts2Sim policy requested PortfolioOptionsVersion={portfolioOptionsVersion} against v{capabilities.PortfolioOptionsVersion} bridge.");
            }
            if (useNoveltyPortfolio && (capabilities.PortfolioOptionsVersion < 2 || !capabilities.SupportsNoveltyPortfolio))
            {
                throw new InvalidDataException("Sts2Sim policy requested UseNoveltyPortfolio without negotiated support.");
            }
            if (adaptiveNoveltyRefinement is not null
                && (capabilities.PortfolioOptionsVersion < 2 || !capabilities.SupportsAdaptiveNoveltyRefinement))
            {
                throw new InvalidDataException("Sts2Sim policy specified AdaptiveNoveltyRefinement without negotiated support.");
            }
            if (noveltySearch is not null
                && (capabilities.PortfolioOptionsVersion < 2
                    || (!capabilities.SupportsNoveltyPortfolio
                        && !(adaptiveNoveltyRefinement == true && capabilities.SupportsAdaptiveNoveltyRefinement))))
            {
                throw new InvalidDataException("Sts2Sim policy specified NoveltySearch without negotiated novelty support.");
            }
            if (noveltyBudget is not null
                && (capabilities.PortfolioOptionsVersion < 2
                    || (!capabilities.SupportsNoveltyPortfolio && !capabilities.SupportsAdaptiveNoveltyRefinement)))
            {
                throw new InvalidDataException("Sts2Sim policy specified NoveltyBudget without negotiated novelty support.");
            }
            if ((useEarlyTurnExploration
                    || earlyTurnExplorationDepth is not null
                    || earlyTurnExplorationBudgetMilliseconds is not null)
                && (capabilities.PortfolioOptionsVersion < 2 || !capabilities.SupportsEarlyTurnExploration))
            {
                throw new InvalidDataException("Sts2Sim policy requested EarlyTurnExploration without negotiated support.");
            }
            if ((beamWidthPortfolioWidths is not null
                    || beamWidthPortfolioPlainBaselineMember is not null
                    || reallocatedRefinementPortfolio is not null
                    || offensiveRefinementPortfolio is not null
                    || boundedOffensiveRefinementPortfolio is not null
                    || stopPortfolioAtHpTarget is not null)
                && (capabilities.PortfolioOptionsVersion < 2 || !capabilities.SupportsBeamWidthPortfolioFull))
            {
                throw new InvalidDataException("Sts2Sim policy specified v2 BeamWidthPortfolio options without negotiated support.");
            }
            if (portfolioSchedulingMode is not null
                && (capabilities.PortfolioOptionsVersion < 2 || !capabilities.SupportsParallelPortfolioScheduling))
            {
                throw new InvalidDataException("Sts2Sim policy specified PortfolioSchedulingMode without negotiated support.");
            }
        }
    }

    public static Sts2SimBridgeResponseDto DeserializeAndValidateResponse(byte[] utf8Json)
    {
        if (utf8Json is null || utf8Json.Length == 0)
            throw new InvalidDataException("Sts2Sim bridge returned an empty response payload.");

        Sts2SimBridgeResponseDto? response = JsonSerializer.Deserialize<Sts2SimBridgeResponseDto>(
            utf8Json,
            PreCombatSts2SimAbi.JsonOptions);
        if (response is null)
            throw new InvalidDataException("Sts2Sim bridge response deserialized to null.");

        ValidateResponse(response);
        return response;
    }

    public static void ValidateResponse(Sts2SimBridgeResponseDto response)
    {
        if (response.AbiVersion != PreCombatSts2SimAbi.Version)
        {
            throw new InvalidDataException(
                $"Sts2Sim bridge ABI mismatch: expected {PreCombatSts2SimAbi.Version}, got {response.AbiVersion}.");
        }
        if (response.EffectiveDegreeOfParallelism is not (>= 1 and <= 64))
        {
            throw new InvalidDataException(
                $"Sts2Sim bridge reported unexpected EffectiveDegreeOfParallelism={response.EffectiveDegreeOfParallelism}.");
        }
        if (response.TotalElapsedMilliseconds is not { } totalMs || !double.IsFinite(totalMs) || totalMs < 0d)
        {
            throw new InvalidDataException(
                $"Sts2Sim bridge response contained invalid TotalElapsedMilliseconds={response.TotalElapsedMilliseconds}.");
        }

        switch (response.Outcome)
        {
            case "Succeeded":
                if (response.FailureCode is not null
                    || response.FieldPath is not null
                    || response.Error is not null)
                {
                    throw new InvalidDataException(
                        "Succeeded bridge response cannot specify FailureCode, FieldPath, or Error.");
                }
                if (response.ProjectedHpLoss is not { } loss || loss < 0)
                    throw new InvalidDataException("Succeeded bridge response lacks a non-negative ProjectedHpLoss.");
                if (response.FinalHp is not { } finalHp || finalHp < 0)
                    throw new InvalidDataException("Succeeded bridge response lacks a non-negative FinalHp.");
                if (response.CombatEndedTurn is { } endedTurn && endedTurn < 1)
                    throw new InvalidDataException("Succeeded bridge response has non-positive CombatEndedTurn.");
                if (response.OnlyDeathRoutes is not { } onlyDeath)
                    throw new InvalidDataException("Succeeded bridge response lacks OnlyDeathRoutes.");
                if (string.IsNullOrWhiteSpace(response.SearchBoundary)
                    || !PreCombatSts2SimAbi.AllowedSearchBoundaries.Contains(response.SearchBoundary))
                {
                    throw new InvalidDataException(
                        $"Bridge response returned unknown SearchBoundary '{response.SearchBoundary}'.");
                }
                if (response.CombatEndedTurn.HasValue)
                {
                    if (onlyDeath)
                        throw new InvalidDataException("Succeeded victory response cannot specify OnlyDeathRoutes=true.");
                    if (finalHp < 1)
                        throw new InvalidDataException("Succeeded victory response must retain FinalHp >= 1.");
                    if (!string.Equals(response.SearchBoundary, "None", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Succeeded victory response must report SearchBoundary='None', got '{response.SearchBoundary}'.");
                    }
                }
                else if (!onlyDeath && finalHp > 0 && string.Equals(response.SearchBoundary, "None", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Living bounded non-victory Succeeded response cannot report SearchBoundary='None'.");
                }
                PreCombatForecastConfidence expectedConfidence = onlyDeath
                    ? PreCombatForecastConfidence.DeathOnly
                    : response.CombatEndedTurn.HasValue
                        ? PreCombatForecastConfidence.Complete
                        : PreCombatForecastConfidence.Bounded;
                if (!string.Equals(response.Confidence, expectedConfidence.ToString(), StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Bridge confidence '{response.Confidence}' disagrees with expected '{expectedConfidence}'.");
                }
                if (response.PotionUses is null)
                    throw new InvalidDataException("Succeeded bridge response lacks PotionUses list.");
                for (int i = 0; i < response.PotionUses.Count; i++)
                {
                    Sts2SimPotionUseDto use = response.PotionUses[i];
                    if (string.IsNullOrWhiteSpace(use.Id)
                        || use.Turn is not { } potionTurn || potionTurn < 1
                        || use.Slot is not { } potionSlot || potionSlot < 0)
                    {
                        throw new InvalidDataException(
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"Bridge PotionUses[{i}] is malformed: id='{use.Id}', turn={use.Turn}, slot={use.Slot}."));
                    }
                }
                if (response.ExpandedNodes is not { } nodes || nodes < 0)
                    throw new InvalidDataException("Succeeded bridge response lacks non-negative ExpandedNodes.");
                if (response.SearchElapsedMilliseconds is not { } searchMs || !double.IsFinite(searchMs) || searchMs < 0d)
                    throw new InvalidDataException("Succeeded bridge response lacks finite non-negative SearchElapsedMilliseconds.");
                break;

            case "CapabilityMiss":
                if (string.IsNullOrWhiteSpace(response.FailureCode)
                    || !PreCombatSts2SimAbi.AllowlistedCapabilityMissCodes.Contains(response.FailureCode))
                {
                    throw new InvalidDataException(
                        $"Bridge CapabilityMiss returned unallowlisted FailureCode '{response.FailureCode}'.");
                }
                if (string.IsNullOrWhiteSpace(response.Error))
                    throw new InvalidDataException("Bridge CapabilityMiss requires an Error message.");
                EnsureNullMetrics(response);
                break;

            case "Cancelled":
            case "TimedOut":
            case "Failed":
                if (string.IsNullOrWhiteSpace(response.Error))
                    throw new InvalidDataException($"Bridge {response.Outcome} requires an Error message.");
                EnsureNullMetrics(response);
                break;

            default:
                throw new InvalidDataException($"Bridge returned unknown Outcome '{response.Outcome}'.");
        }
    }

    private static void EnsureNullMetrics(Sts2SimBridgeResponseDto response)
    {
        if (response.ProjectedHpLoss is not null
            || response.FinalHp is not null
            || response.CombatEndedTurn is not null
            || response.OnlyDeathRoutes is not null
            || response.Confidence is not null
            || response.SearchBoundary is not null
            || response.PotionUses is not null
            || response.ExpandedNodes is not null
            || response.SearchElapsedMilliseconds is not null)
        {
            throw new InvalidDataException(
                $"Non-success bridge outcome '{response.Outcome}' must not populate forecast result metrics.");
        }
    }
}
