using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CombatSolver.Api;

internal static class InCombatSts2SimAbi
{
    public const int Version = 1;
    public const int MaxSoftTimeBudgetMilliseconds = 600_000;

    public static readonly FrozenSet<string> AllowlistedCapabilityMissCodes =
        PreCombatSts2SimAbi.AllowlistedCapabilityMissCodes
            .Concat(
            [
                "live_state_mismatch",
                "live_private_state_unmapped",
                "live_monster_state_unmapped",
                "live_card_cost_unsupported",
                "live_combat_precondition",
                "user_disabled",
            ])
            .ToFrozenSet(StringComparer.Ordinal);
}

internal sealed record InCombatCardIntEntryDto(int CardHandle, int Value);

internal sealed record InCombatCreatureDecimalEntryDto(int CreatureHandle, decimal Value);

internal sealed record InCombatCardPairEntryDto(int OriginalCardHandle, int CloneCardHandle);

internal sealed record InCombatFieldValueDto(
    string Kind,
    int? IntValue = null,
    decimal? DecimalValue = null,
    bool? BoolValue = null,
    string? StringValue = null,
    IReadOnlyList<int>? IntArrayValue = null,
    IReadOnlyList<InCombatCardIntEntryDto>? CardIntMapValue = null,
    IReadOnlyList<InCombatCreatureDecimalEntryDto>? CreatureDecimalMapValue = null,
    IReadOnlyList<InCombatCardPairEntryDto>? CardPairArrayValue = null);

internal sealed record InCombatLocalEnergyModifierDto(
    string Kind,
    int Amount,
    bool IsReduceOnly);

internal sealed record InCombatEnchantmentDto(
    string EnchantmentId,
    int Amount,
    string Status,
    IReadOnlyDictionary<string, InCombatFieldValueDto> PrivateFields);

internal sealed record InCombatAfflictionDto(
    string AfflictionId,
    int Amount);

internal sealed record InCombatCardDto(
    int Handle,
    string CardId,
    int UpgradeLevel,
    int? DeckCardHandle,
    int? CloneOfHandle,
    bool IsDupe,
    bool ExhaustOnNextPlay,
    int BaseReplayCount,
    int CapturedEnergyXValue,
    int? FloorAddedToDeck,
    int CanonicalBaseEnergyCost,
    int BaseEnergyCost,
    bool CostsXEnergy,
    IReadOnlyList<InCombatLocalEnergyModifierDto> LocalEnergyModifiers,
    int? TemporaryStarCostOverrideThisTurn,
    bool TemporaryFreeThisTurn,
    bool TemporaryFreeUntilPlayed,
    bool TemporaryFreeThisCombat,
    int? TemporaryCostOverrideThisTurn,
    int? TemporaryCostOverrideThisTurnOrUntilPlayed,
    int? TemporaryCostOverrideThisCombat,
    bool TemporaryRetainThisTurn,
    bool TemporarySlyThisTurn,
    IReadOnlyList<string> LocalKeywords,
    IReadOnlyList<string> KeywordsAddedFromOutside,
    IReadOnlyList<string> KeywordsRemovedFromOutside,
    InCombatEnchantmentDto? Enchantment,
    InCombatAfflictionDto? Affliction,
    IReadOnlyDictionary<string, InCombatFieldValueDto> PrivateFields);

internal sealed record InCombatRelicDto(
    string RelicId,
    bool IsWax,
    bool IsMelted,
    int StackCount,
    int? FloorAddedToDeck,
    IReadOnlyDictionary<string, InCombatFieldValueDto> PrivateFields);

internal sealed record InCombatOrbDto(
    string OrbId,
    decimal? RawEvokeValue,
    decimal? RawPassiveValue);

internal sealed record InCombatArrowSlotDto(
    string Status,
    bool DestroyAfterImpaled);

internal sealed record InCombatPlayerCountersDto(
    decimal StarsGainedThisTurn,
    int SkillCardsPlayedThisTurn,
    int AttackCardsPlayedThisTurn,
    int ShivsPlayedThisTurn,
    int CardsDiscardedThisTurn,
    int CardsPlayedThisCombat,
    int CardsGeneratedThisCombat,
    int SadnessGeneratedThisCombat,
    int CardsDrawnThisCombat,
    int CardsPlayedThisTurn,
    int ManualCardsPlayedThisTurn,
    int CardPlaysStartedThisTurn,
    int AttackOrSkillCardPlaysStartedThisTurn,
    int AttackCardsStartedThisTurn,
    int ZeroCostAttacksStartedThisTurn,
    int FirstInSeriesCardPlaysStartedThisTurn,
    int WahStartedThisTurn,
    int ChoStartedThisTurn,
    int KiStartedThisTurn,
    int FirstWahPlayOrdinalThisTurn,
    int FirstChoPlayOrdinalThisTurn,
    int FirstKiPlayOrdinalThisTurn,
    int WahFinishedThisTurn,
    int ChoFinishedThisTurn,
    int KiFinishedThisTurn,
    int MelodyCardsFinishedThisTurn);

internal sealed record InCombatPlayerDto(
    int CurrentHp,
    int MaxHp,
    int Block,
    int CumulativeHpLost,
    int Gold,
    int MaxEnergy,
    int BaseOrbSlotCount,
    bool IsActiveForHooks,
    bool CanUseOrRemovePotions,
    int Energy,
    int Stars,
    string Phase,
    int TurnNumber,
    InCombatPlayerCountersDto Counters,
    IReadOnlyList<int> HandHandles,
    IReadOnlyList<int> DrawHandles,
    IReadOnlyList<int> DiscardHandles,
    IReadOnlyList<int> ExhaustHandles,
    IReadOnlyList<int> PlayHandles,
    IReadOnlyList<int> DeckHandles,
    IReadOnlyList<string?> PotionSlots,
    int OrbCapacity,
    IReadOnlyList<InCombatOrbDto> Orbs,
    IReadOnlyList<InCombatArrowSlotDto> Arrows);

internal sealed record InCombatPowerDto(
    string PowerId,
    int Amount,
    int AmountOnTurnStart,
    bool SkipNextDurationTick,
    int? ApplierCreatureHandle,
    IReadOnlyDictionary<string, InCombatFieldValueDto> PrivateFields);

internal sealed record InCombatRngStreamDto(
    int Counter,
    ulong S0,
    ulong S1,
    ulong S2,
    ulong S3);

internal sealed record InCombatMonsterDto(
    InCombatRngStreamDto Rng,
    bool IsPerformingMove,
    bool SpawnedThisTurn,
    string CurrentStateId,
    string? NextMoveId,
    bool NextMoveCanTransitionAway,
    bool PerformedFirstMove,
    IReadOnlyList<string> StateLogIds,
    string? TransientMoveFollowUpStateId,
    IReadOnlyDictionary<string, InCombatFieldValueDto> SetupFields,
    IReadOnlyDictionary<string, InCombatFieldValueDto> PrivateFields);

internal sealed record InCombatCreatureDto(
    int Handle,
    uint? CombatId,
    string Side,
    string? SlotName,
    bool IsPlayer,
    int? PetOwnerPlayerIndex,
    string? MonsterId,
    int CurrentHp,
    int MaxHp,
    int Block,
    int CumulativeHpLost,
    bool IsDetached,
    InCombatMonsterDto? Monster,
    IReadOnlyList<InCombatPowerDto> Powers);

internal sealed record InCombatDamageHistoryEntryDto(
    int ReceiverCreatureHandle,
    int? DealerCreatureHandle,
    int OriginalTargetCreatureHandle,
    int Props,
    int IntendedHpLoss,
    int BlockedDamage,
    int UnblockedDamage,
    int OverkillDamage,
    bool WasTargetKilled,
    bool WasBlockBroken,
    bool WasFullyBlocked,
    int? CardSourceHandle,
    int RoundNumber,
    string CurrentSide,
    IReadOnlyList<int> PlayerTurnNumbers);

internal sealed record InCombatTurnKeyDto(
    int RoundNumber,
    string Side,
    IReadOnlyList<int?> PlayerTurnNumbers);

internal sealed record InCombatExhaustHistoryDto(
    InCombatTurnKeyDto Turn,
    int ActorIndex);

internal sealed record InCombatBlockGainHistoryDto(
    InCombatTurnKeyDto Turn,
    int ActorIndex,
    int PlayOrdinal);

internal sealed record InCombatOrbChannelHistoryDto(
    InCombatTurnKeyDto Turn,
    int ActorIndex,
    string OrbId);

internal sealed record InCombatEnergySpentHistoryDto(
    InCombatTurnKeyDto Turn,
    int ActorIndex,
    int Amount);

internal sealed record InCombatCardDrawnHistoryDto(
    InCombatTurnKeyDto Turn,
    int ActorIndex,
    string CardType,
    bool FromHandDraw);

internal sealed record InCombatActorEventHistoryDto(
    string Kind,
    InCombatTurnKeyDto Turn,
    int ActorIndex);

internal sealed record InCombatSemanticHistoryDto(
    IReadOnlyList<InCombatExhaustHistoryDto> Exhausts,
    IReadOnlyList<InCombatBlockGainHistoryDto> CardBlockGains,
    IReadOnlyList<InCombatOrbChannelHistoryDto> OrbChannels,
    IReadOnlyList<InCombatEnergySpentHistoryDto> EnergySpent,
    IReadOnlyList<InCombatCardDrawnHistoryDto> CardDrawn,
    IReadOnlyList<InCombatActorEventHistoryDto> ActorEvents);

internal sealed record InCombatVerifiedCardDto(
    string CardId,
    int UpgradeLevel,
    int ResolvedEnergyCost,
    IReadOnlyList<string> LocalKeywords);

internal sealed record InCombatVerifiedPowerDto(
    string PowerId,
    int Amount);

internal sealed record InCombatVerifiedCreatureDto(
    uint? CombatId,
    string? MonsterId,
    int CurrentHp,
    int MaxHp,
    int Block,
    string? NextMoveId,
    IReadOnlyList<InCombatVerifiedPowerDto> Powers);

internal sealed record InCombatVerificationDto(
    int PlayerHp,
    int PlayerMaxHp,
    int PlayerBlock,
    int PlayerEnergy,
    int PlayerStars,
    int PlayerGold,
    int OrbCapacity,
    IReadOnlyList<string> OrbIds,
    IReadOnlyList<string> ArrowStatuses,
    IReadOnlyList<string?> PotionIds,
    IReadOnlyList<InCombatVerifiedCardDto> Hand,
    IReadOnlyList<InCombatVerifiedCardDto> DrawPile,
    IReadOnlyList<InCombatVerifiedCardDto> DiscardPile,
    IReadOnlyList<InCombatVerifiedCardDto> ExhaustPile,
    IReadOnlyList<InCombatVerifiedCreatureDto> Enemies,
    IReadOnlyList<InCombatVerifiedPowerDto> PlayerPowers);

internal sealed record InCombatLiveStateDto(
    string EncounterId,
    string RoomType,
    IReadOnlyList<string> EncounterSlots,
    string CurrentSide,
    int RoundNumber,
    bool IsPlayerExtraTurn,
    bool GoldWasStolen,
    uint NextCreatureId,
    string RunRngSeed,
    IReadOnlyDictionary<string, InCombatRngStreamDto> RunRngs,
    ulong PlayerRngSeed,
    IReadOnlyDictionary<string, InCombatRngStreamDto> PlayerRngs,
    InCombatPlayerDto Player,
    IReadOnlyList<InCombatCardDto> Cards,
    IReadOnlyList<InCombatRelicDto> Relics,
    IReadOnlyList<InCombatCreatureDto> Creatures,
    IReadOnlyList<int> AllyHandles,
    IReadOnlyList<int> EnemyHandles,
    IReadOnlyList<int> EscapedHandles,
    IReadOnlyList<int> RemovedHandles,
    IReadOnlyList<int> SpawnedEnemyHandles,
    IReadOnlyList<int> PetHandles,
    IReadOnlyList<InCombatDamageHistoryEntryDto> DamageHistory,
    InCombatSemanticHistoryDto SemanticHistory,
    InCombatVerificationDto Verification);

internal sealed record InCombatSts2SimPolicyDto(
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
    int? PotionFreeBeamWidth,
    int? PotionBeamWidth,
    int MaxExpandedNodes,
    int MaxCardBranchesPerNode,
    int MaxPileChoiceBranchesPerAction,
    int MaxHandChoiceBranchesPerAction,
    int SoftTimeBudgetMilliseconds,
    string? TheftPolicy,
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

internal sealed record InCombatSts2SimRequestDto(
    int AbiVersion,
    string RequestId,
    string NormalizedSaveJson,
    InCombatLiveStateDto LiveState,
    int PriorBattleHpLost,
    int SearchBudgetMilliseconds,
    int OverallTimeoutMilliseconds,
    int MaxDegreeOfParallelism,
    InCombatSts2SimPolicyDto Policy);

internal sealed record InCombatChoiceCardTokenDto(
    string CardId,
    int UpgradeLevel,
    int OptionOccurrence,
    int SourceOccurrence);

internal sealed record InCombatChoiceTraceDto(
    string Timing,
    string SourceId,
    string SourcePile,
    IReadOnlyList<InCombatChoiceCardTokenDto> Cards);

internal sealed record InCombatActionTraceDto(
    int Turn,
    string Kind,
    string CardId,
    int CardOccurrence,
    int CardUpgradeLevel,
    string CardEnchantmentId,
    int TargetIndex,
    uint? TargetCombatId,
    string PotionId,
    int PotionSlot,
    IReadOnlyList<InCombatChoiceTraceDto> Choices,
    IReadOnlyList<string> FlashedRelicIds,
    IReadOnlyList<uint> KilledEnemyCombatIds);

internal sealed record InCombatTurnOutcomeDto(
    int Turn,
    int HpLost,
    int HpRecovered,
    int EnemyHpLost,
    int SoldHp,
    int MaxBlock,
    int ActualBlock,
    int EnergyLeft,
    int PotionCount,
    int PotionStrategicCost,
    bool CombatEnded);

internal sealed record InCombatTerminalSnapshotDto(
    bool PlayerDead,
    bool AllEnemiesDead,
    int PlayerHp,
    int PlayerMaxHp,
    int CumulativePlayerHpLost,
    int RecoveredPlayerHp,
    int ProjectedPlayerHp,
    int PlayerBlock,
    int EnemyHp,
    int AliveEnemyCount,
    int Energy,
    int Stars,
    int HandCount,
    int OutstandingStolenResource,
    int Turn,
    int ShufflesCrossed,
    int PostCombatRelicHeal,
    int ProjectedBattleHpLost,
    int? CombatEndedTurn,
    int? DeathTurn,
    bool OnlyDeathRoutes,
    string Confidence,
    string SearchBoundary,
    bool HasRisk);

internal sealed record InCombatSts2SimResultDto(
    IReadOnlyList<InCombatActionTraceDto> Actions,
    IReadOnlyList<InCombatTurnOutcomeDto> TurnOutcomes,
    InCombatTerminalSnapshotDto Terminal,
    IReadOnlyList<Sts2SimPotionUseDto> PotionUses,
    long ExpandedNodes,
    double SearchElapsedMilliseconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ExpandedParents = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TransitionCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ChoiceReplayAttempts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ObservedPeakParallelism = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActivePhaseLabel = null);

internal sealed record InCombatSts2SimProgressDto(
    long ExpandedNodes,
    int MaxTurnReached,
    double ElapsedMilliseconds,
    int CurrentBestProjectedHpLoss,
    bool CurrentBestWon,
    int? CurrentBestCombatEndedTurn,
    int CurrentBestPotionCount,
    InCombatSts2SimResultDto? CurrentBest,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ExpandedParents = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TransitionCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ChoiceReplayAttempts = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ObservedPeakParallelism = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActivePhaseLabel = null);

internal sealed record InCombatSts2SimResponseDto(
    int AbiVersion,
    string Outcome,
    string? FailureCode,
    string? FieldPath,
    string? Error,
    InCombatSts2SimResultDto? Result,
    InCombatSts2SimResultDto? BestSoFar,
    int EffectiveDegreeOfParallelism,
    double TotalElapsedMilliseconds);

internal static class InCombatSts2SimWireValidator
{
    public static void ValidatePolicy(
        InCombatSts2SimPolicyDto policy,
        Sts2SimBridgeCapabilitiesDto? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        PreCombatSts2SimWireValidator.ValidatePortfolioPolicyOptions(
            policy.UseNoveltyPortfolio,
            policy.UseEarlyTurnExploration,
            policy.PortfolioOptionsVersion,
            policy.BeamWidthPortfolioWidths,
            policy.BeamWidthPortfolioPlainBaselineMember,
            policy.OffensiveRefinementPortfolio,
            policy.BoundedOffensiveRefinementPortfolio,
            policy.AdaptiveNoveltyRefinement,
            policy.NoveltySearch,
            policy.NoveltyBudget,
            policy.EarlyTurnExplorationDepth,
            policy.EarlyTurnExplorationBudgetMilliseconds,
            policy.PortfolioSchedulingMode,
            capabilities);
    }

    public static InCombatSts2SimResponseDto DeserializeAndValidateResponse(byte[] utf8Json)
    {
        if (utf8Json is null || utf8Json.Length == 0)
            throw new InvalidDataException("Sts2Sim in-combat bridge returned an empty response payload.");

        InCombatSts2SimResponseDto? response = JsonSerializer.Deserialize<InCombatSts2SimResponseDto>(
            utf8Json,
            PreCombatSts2SimAbi.JsonOptions);
        if (response is null)
            throw new InvalidDataException("Sts2Sim in-combat bridge response deserialized to null.");

        ValidateResponse(response);
        return response;
    }

    public static InCombatSts2SimProgressDto DeserializeAndValidateProgress(byte[] utf8Json)
    {
        if (utf8Json is null || utf8Json.Length == 0)
            throw new InvalidDataException("Sts2Sim in-combat progress callback received empty bytes.");

        InCombatSts2SimProgressDto? progress = JsonSerializer.Deserialize<InCombatSts2SimProgressDto>(
            utf8Json,
            PreCombatSts2SimAbi.JsonOptions);
        if (progress is null)
            throw new InvalidDataException("Sts2Sim in-combat progress deserialized to null.");

        ValidateProgress(progress);
        return progress;
    }

    public static void ValidateProgress(InCombatSts2SimProgressDto progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.ExpandedNodes < 0 || !double.IsFinite(progress.ElapsedMilliseconds) || progress.ElapsedMilliseconds < 0d)
            throw new InvalidDataException("Sts2Sim in-combat progress reported invalid counters.");
        ValidateExtendedMetrics(
            progress.ExpandedParents,
            progress.TransitionCount,
            progress.ChoiceReplayAttempts,
            progress.ObservedPeakParallelism,
            "progress");
        if (progress.CurrentBest is not null)
            ValidateResult(progress.CurrentBest, "progress.currentBest");
    }

    public static void ValidateResponse(InCombatSts2SimResponseDto response)
    {
        if (response.AbiVersion != InCombatSts2SimAbi.Version)
        {
            throw new InvalidDataException(
                $"Sts2Sim in-combat ABI mismatch: expected {InCombatSts2SimAbi.Version}, got {response.AbiVersion}.");
        }
        if (response.EffectiveDegreeOfParallelism is not (>= 1 and <= 64))
        {
            throw new InvalidDataException(
                $"Sts2Sim in-combat bridge reported unexpected EffectiveDegreeOfParallelism={response.EffectiveDegreeOfParallelism}.");
        }
        if (!double.IsFinite(response.TotalElapsedMilliseconds) || response.TotalElapsedMilliseconds < 0d)
        {
            throw new InvalidDataException(
                $"Sts2Sim in-combat bridge response contained invalid TotalElapsedMilliseconds={response.TotalElapsedMilliseconds}.");
        }

        switch (response.Outcome)
        {
            case "Succeeded":
                if (response.FailureCode is not null || response.FieldPath is not null || response.Error is not null)
                    throw new InvalidDataException("Succeeded in-combat response cannot specify FailureCode, FieldPath, or Error.");
                if (response.Result is null)
                    throw new InvalidDataException("Succeeded in-combat response is missing Result.");
                if (response.BestSoFar is not null)
                    throw new InvalidDataException("Succeeded in-combat response must not specify BestSoFar.");
                ValidateResult(response.Result, "result");
                break;

            case "CapabilityMiss":
                if (string.IsNullOrWhiteSpace(response.FailureCode)
                    || !InCombatSts2SimAbi.AllowlistedCapabilityMissCodes.Contains(response.FailureCode))
                {
                    throw new InvalidDataException(
                        $"In-combat CapabilityMiss returned unallowlisted FailureCode '{response.FailureCode}'.");
                }
                if (string.IsNullOrWhiteSpace(response.Error))
                    throw new InvalidDataException("In-combat CapabilityMiss requires an Error message.");
                if (response.Result is not null || response.BestSoFar is not null)
                    throw new InvalidDataException("In-combat CapabilityMiss must not populate Result or BestSoFar.");
                break;

            case "Cancelled":
            case "TimedOut":
                if (string.IsNullOrWhiteSpace(response.Error))
                    throw new InvalidDataException($"In-combat {response.Outcome} requires an Error message.");
                if (response.Result is not null)
                    throw new InvalidDataException($"In-combat {response.Outcome} must not populate Result.");
                if (response.BestSoFar is not null)
                    ValidateResult(response.BestSoFar, "bestSoFar");
                break;

            case "Failed":
                if (string.IsNullOrWhiteSpace(response.Error))
                    throw new InvalidDataException("In-combat Failed requires an Error message.");
                if (response.Result is not null || response.BestSoFar is not null)
                    throw new InvalidDataException("In-combat Failed must not populate Result or BestSoFar.");
                break;

            default:
                throw new InvalidDataException($"In-combat bridge returned unknown Outcome '{response.Outcome}'.");
        }
    }

    private static void ValidateResult(InCombatSts2SimResultDto result, string path)
    {
        if (result.Actions is null)
            throw new InvalidDataException($"{path}.actions cannot be null.");
        if (result.TurnOutcomes is null)
            throw new InvalidDataException($"{path}.turnOutcomes cannot be null.");
        if (result.Terminal is null)
            throw new InvalidDataException($"{path}.terminal cannot be null.");
        if (result.PotionUses is null)
            throw new InvalidDataException($"{path}.potionUses cannot be null.");
        if (result.ExpandedNodes < 0 || !double.IsFinite(result.SearchElapsedMilliseconds) || result.SearchElapsedMilliseconds < 0d)
            throw new InvalidDataException($"{path} has invalid search telemetry.");
        ValidateExtendedMetrics(
            result.ExpandedParents,
            result.TransitionCount,
            result.ChoiceReplayAttempts,
            result.ObservedPeakParallelism,
            path);

        for (int i = 0; i < result.Actions.Count; i++)
        {
            InCombatActionTraceDto action = result.Actions[i];
            if (action.Turn < 1 || string.IsNullOrWhiteSpace(action.Kind) || action.Choices is null)
                throw new InvalidDataException($"{path}.actions[{i}] is malformed.");
        }

        InCombatTerminalSnapshotDto term = result.Terminal;
        if (string.IsNullOrWhiteSpace(term.SearchBoundary)
            || !PreCombatSts2SimAbi.AllowedSearchBoundaries.Contains(term.SearchBoundary))
        {
            throw new InvalidDataException($"{path}.terminal has invalid SearchBoundary '{term.SearchBoundary}'.");
        }
        for (int i = 0; i < result.PotionUses.Count; i++)
        {
            Sts2SimPotionUseDto use = result.PotionUses[i];
            if (string.IsNullOrWhiteSpace(use.Id)
                || use.Turn is not { } potionTurn || potionTurn < 1
                || use.Slot is not { } potionSlot || potionSlot < 0)
            {
                throw new InvalidDataException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{path}.potionUses[{i}] is malformed."));
            }
        }
    }

    private static void ValidateExtendedMetrics(
        long? expandedParents,
        long? transitionCount,
        long? choiceReplayAttempts,
        int? observedPeakParallelism,
        string path)
    {
        if (expandedParents is < 0)
            throw new InvalidDataException($"{path}.expandedParents cannot be negative ({expandedParents}).");
        if (transitionCount is < 0)
            throw new InvalidDataException($"{path}.transitionCount cannot be negative ({transitionCount}).");
        if (choiceReplayAttempts is < 0)
            throw new InvalidDataException($"{path}.choiceReplayAttempts cannot be negative ({choiceReplayAttempts}).");
        if (observedPeakParallelism is not (null or (>= 1 and <= 64)))
        {
            throw new InvalidDataException(
                $"{path}.observedPeakParallelism must be between 1 and 64 ({observedPeakParallelism}).");
        }
    }
}
