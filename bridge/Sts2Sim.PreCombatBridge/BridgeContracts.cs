using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2Sim.PreCombatBridge;

internal static class BridgeAbi
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

    public static readonly JsonSerializerOptions IndentedJsonOptions = new(JsonOptions)
    {
        WriteIndented = true,
    };

    public static readonly FrozenSet<string> AllowlistedFailureCodes = new[]
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
}

internal sealed class BridgeCapabilityMissException(string failureCode, string? fieldPath, string message)
    : Exception(message)
{
    public string FailureCode { get; } = failureCode;
    public string? FieldPath { get; } = fieldPath;
}

internal sealed class BridgeRequestFailedException(string? fieldPath, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string? FieldPath { get; } = fieldPath;
}

internal sealed record BridgeMapCoordDto(int Col, int Row);

internal sealed record BridgeMapStepDto(
    BridgeMapCoordDto Coordinate,
    string RoomType,
    string MapPointType);

internal sealed record BridgeSpoilsMapEntryDto(
    int DeckIndex,
    BridgeMapCoordDto? Coord);

internal sealed record BridgeSpoilsMapVerificationDto(
    int ExpectedDeckCount,
    IReadOnlyList<string> ExpectedCardIds,
    IReadOnlyList<BridgeSpoilsMapEntryDto> Entries);

internal sealed record BridgePotionDirectiveDto(
    int Slot,
    string PotionId,
    string Directive);

internal sealed record BridgeRelicCounterRuleDto(
    string Id,
    bool Enabled,
    int Minimum,
    int Maximum,
    int HpAllowance,
    int Priority);

internal sealed record BridgeGrowthBudgetsDto(
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

internal sealed record BridgePolicyDto(
    string PotionPolicy,
    IReadOnlyList<BridgePotionDirectiveDto> PotionDirectives,
    bool PredictPotionReward,
    bool RelicStrategyEnabled,
    IReadOnlyList<BridgeRelicCounterRuleDto> RelicCounterRules,
    BridgeGrowthBudgetsDto GrowthBudgets,
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
    int SoftTimeBudgetMilliseconds);

internal sealed record BridgeRequestDto(
    int AbiVersion,
    string RequestId,
    string NormalizedSaveJson,
    bool HasLiveSidecar,
    int? LiveActFloor,
    int? NextRoomId,
    BridgeSpoilsMapVerificationDto? SpoilsMapVerification,
    string EncounterId,
    int TargetActFloor,
    int TargetMapColumn,
    string RoomKind,
    string MapPointKind,
    bool IsSecondBoss,
    IReadOnlyList<BridgeMapStepDto> InterveningMapPoints,
    int? PlayerCurrentHpOverride,
    ulong? SimulationSeed,
    int SearchBudgetMilliseconds,
    int OverallTimeoutMilliseconds,
    int MaxDegreeOfParallelism,
    BridgePolicyDto Policy);

internal sealed record BridgePotionUseDto(
    string Id,
    int Turn,
    int Slot);

internal sealed record BridgeResponseDto(
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
    IReadOnlyList<BridgePotionUseDto>? PotionUses,
    long? ExpandedNodes,
    int? EffectiveDegreeOfParallelism,
    double? SearchElapsedMilliseconds,
    double? TotalElapsedMilliseconds)
{
    public static BridgeResponseDto Succeeded(
        int projectedHpLoss,
        int finalHp,
        int? combatEndedTurn,
        bool onlyDeathRoutes,
        string confidence,
        string searchBoundary,
        IReadOnlyList<BridgePotionUseDto> potionUses,
        long expandedNodes,
        double searchElapsedMilliseconds,
        double totalElapsedMilliseconds,
        int effectiveDegreeOfParallelism = 1) =>
        new(
            BridgeAbi.Version,
            "Succeeded",
            null,
            null,
            null,
            projectedHpLoss,
            finalHp,
            combatEndedTurn,
            onlyDeathRoutes,
            confidence,
            searchBoundary,
            potionUses,
            expandedNodes,
            effectiveDegreeOfParallelism,
            searchElapsedMilliseconds,
            totalElapsedMilliseconds);

    public static BridgeResponseDto CapabilityMiss(
        string failureCode,
        string? fieldPath,
        string error,
        double totalElapsedMilliseconds)
    {
        if (!BridgeAbi.AllowlistedFailureCodes.Contains(failureCode))
            throw new ArgumentOutOfRangeException(nameof(failureCode), $"FailureCode '{failureCode}' is not allowlisted.");
        return new(
            BridgeAbi.Version,
            "CapabilityMiss",
            failureCode,
            fieldPath,
            string.IsNullOrWhiteSpace(error) ? failureCode : error,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            1,
            null,
            Math.Max(0d, totalElapsedMilliseconds));
    }

    public static BridgeResponseDto NonSuccess(
        string outcome,
        string? fieldPath,
        string error,
        double totalElapsedMilliseconds) =>
        new(
            BridgeAbi.Version,
            outcome,
            null,
            fieldPath,
            string.IsNullOrWhiteSpace(error) ? outcome : error,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            1,
            null,
            Math.Max(0d, totalElapsedMilliseconds));
}

internal sealed record BridgeBundleFileManifestEntry(
    string Name,
    string Sha256,
    string? Mvid);

internal sealed record BridgeBundleManifestDto(
    int AbiVersion,
    string SupportedGameVersion,
    string SupportedGameCommit,
    IReadOnlyList<BridgeBundleFileManifestEntry> Files,
    string SimulatorCommit);

internal sealed record BridgeBundleIdentityDto(
    int AbiVersion,
    string SupportedGameVersion,
    string SupportedGameCommit,
    string BridgeMvid,
    string SolverMvid,
    string CoreMvid);
