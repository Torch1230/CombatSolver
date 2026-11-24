using System.Text.Json;
using System.Text.Json.Serialization;

namespace CombatSolver;

internal static class RustKernelAbi
{
    public const int Version = 1;
    public const int MaxSoftTimeBudgetMilliseconds = 600_000;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

internal enum RustKernelAvailability
{
    Absent,
    Ready,
    CorruptOrIncompatible,
}

internal sealed record InCombatRustHostMetadata(
    string CharacterId,
    byte Ascension,
    string GameMode,
    string? DominionId,
    IReadOnlyList<string> RunModelIds,
    bool EncounterIsWeak,
    int InitialEncounterMonsterCount,
    int TotalFloor,
    int ActIndex,
    int MapCol,
    int MapRow);

internal sealed record RustKernelRngStreamDto(
    int Counter,
    ulong S0,
    ulong S1,
    ulong S2,
    ulong S3);

internal sealed record RustKernelCardIntEntryDto(
    int CardHandle,
    int Value);

internal sealed record RustKernelCreatureDecimalEntryDto(
    int CreatureHandle,
    decimal Value);

internal sealed record RustKernelCardPairEntryDto(
    int OriginalCardHandle,
    int CloneCardHandle);

internal sealed record RustKernelFieldValueDto(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? IntValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? DecimalValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? BoolValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StringValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? IntArrayValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RustKernelCardIntEntryDto>? CardIntMapValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RustKernelCreatureDecimalEntryDto>? CreatureDecimalMapValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RustKernelCardPairEntryDto>? CardPairArrayValue = null);

internal sealed record RustKernelLocalEnergyModifierDto(
    string Kind,
    int Amount,
    bool IsReduceOnly);

internal sealed record RustKernelEnchantmentDto(
    string EnchantmentId,
    int Amount,
    string Status,
    IReadOnlyDictionary<string, RustKernelFieldValueDto> PrivateFields);

internal sealed record RustKernelAfflictionDto(
    string AfflictionId,
    int Amount);

internal sealed record RustKernelCardDto(
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
    IReadOnlyList<RustKernelLocalEnergyModifierDto> LocalEnergyModifiers,
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
    RustKernelEnchantmentDto? Enchantment,
    RustKernelAfflictionDto? Affliction,
    IReadOnlyDictionary<string, RustKernelFieldValueDto> PrivateFields);

internal sealed record RustKernelRelicDto(
    string RelicId,
    bool IsWax,
    bool IsMelted,
    int StackCount,
    int? FloorAddedToDeck,
    IReadOnlyDictionary<string, RustKernelFieldValueDto> PrivateFields);

internal sealed record RustKernelOrbDto(
    string OrbId,
    decimal? RawEvokeValue,
    decimal? RawPassiveValue);

internal sealed record RustKernelArrowSlotDto(
    string Status,
    bool DestroyAfterImpaled);

internal sealed record RustKernelPlayerCountersDto(
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

internal sealed record RustKernelPlayerDto(
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
    RustKernelPlayerCountersDto Counters,
    IReadOnlyList<int> HandHandles,
    IReadOnlyList<int> DrawHandles,
    IReadOnlyList<int> DiscardHandles,
    IReadOnlyList<int> ExhaustHandles,
    IReadOnlyList<int> PlayHandles,
    IReadOnlyList<int> DeckHandles,
    IReadOnlyList<string?> PotionSlots,
    int OrbCapacity,
    IReadOnlyList<RustKernelOrbDto> Orbs,
    IReadOnlyList<RustKernelArrowSlotDto> Arrows);

internal sealed record RustKernelPowerDto(
    string PowerId,
    int Amount,
    int AmountOnTurnStart,
    bool SkipNextDurationTick,
    int? ApplierCreatureHandle,
    IReadOnlyDictionary<string, RustKernelFieldValueDto> PrivateFields);

internal sealed record RustKernelMonsterDto(
    RustKernelRngStreamDto Rng,
    bool IsPerformingMove,
    bool SpawnedThisTurn,
    string CurrentStateId,
    string? NextMoveId,
    bool NextMoveCanTransitionAway,
    bool PerformedFirstMove,
    IReadOnlyList<string> StateLogIds,
    string? TransientMoveFollowUpStateId,
    IReadOnlyDictionary<string, RustKernelFieldValueDto> SetupFields,
    IReadOnlyDictionary<string, RustKernelFieldValueDto> PrivateFields);

internal sealed record RustKernelCreatureDto(
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
    RustKernelMonsterDto? Monster,
    IReadOnlyList<RustKernelPowerDto> Powers);

internal sealed record RustKernelDamageHistoryEntryDto(
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

internal sealed record RustKernelTurnKeyDto(
    int RoundNumber,
    string Side,
    IReadOnlyList<int?> PlayerTurnNumbers);

internal sealed record RustKernelExhaustHistoryDto(
    RustKernelTurnKeyDto Turn,
    int ActorIndex);

internal sealed record RustKernelBlockGainHistoryDto(
    RustKernelTurnKeyDto Turn,
    int ActorIndex,
    int PlayOrdinal);

internal sealed record RustKernelOrbChannelHistoryDto(
    RustKernelTurnKeyDto Turn,
    int ActorIndex,
    string OrbId);

internal sealed record RustKernelEnergySpentHistoryDto(
    RustKernelTurnKeyDto Turn,
    int ActorIndex,
    int Amount);

internal sealed record RustKernelCardDrawnHistoryDto(
    RustKernelTurnKeyDto Turn,
    int ActorIndex,
    string CardType,
    bool FromHandDraw);

internal sealed record RustKernelActorEventHistoryDto(
    string Kind,
    RustKernelTurnKeyDto Turn,
    int ActorIndex);

internal sealed record RustKernelSemanticHistoryDto(
    IReadOnlyList<RustKernelExhaustHistoryDto> Exhausts,
    IReadOnlyList<RustKernelBlockGainHistoryDto> CardBlockGains,
    IReadOnlyList<RustKernelOrbChannelHistoryDto> OrbChannels,
    IReadOnlyList<RustKernelEnergySpentHistoryDto> EnergySpent,
    IReadOnlyList<RustKernelCardDrawnHistoryDto> CardDrawn,
    IReadOnlyList<RustKernelActorEventHistoryDto> ActorEvents);

internal sealed record RustKernelLiveStateDto(
    string EncounterId,
    string RoomType,
    IReadOnlyList<string> EncounterSlots,
    string CurrentSide,
    int RoundNumber,
    bool IsPlayerExtraTurn,
    bool GoldWasStolen,
    uint NextCreatureId,
    string RunRngSeed,
    IReadOnlyDictionary<string, RustKernelRngStreamDto> RunRngs,
    ulong PlayerRngSeed,
    IReadOnlyDictionary<string, RustKernelRngStreamDto> PlayerRngs,
    RustKernelPlayerDto Player,
    IReadOnlyList<RustKernelCardDto> Cards,
    IReadOnlyList<RustKernelRelicDto> Relics,
    IReadOnlyList<RustKernelCreatureDto> Creatures,
    IReadOnlyList<int> AllyHandles,
    IReadOnlyList<int> EnemyHandles,
    IReadOnlyList<int> EscapedHandles,
    IReadOnlyList<int> RemovedHandles,
    IReadOnlyList<int> SpawnedEnemyHandles,
    IReadOnlyList<int> PetHandles,
    IReadOnlyList<RustKernelDamageHistoryEntryDto> DamageHistory,
    RustKernelSemanticHistoryDto SemanticHistory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? UnlockedCharacterCards = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? UnlockedColorlessCards = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? UnlockedCurseCards = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? OtherCharacterCards = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? TarotCards = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? UnlockedPotions = null);

internal sealed record RustKernelPotionDirectiveDto(
    int Slot,
    string PotionId,
    string Directive);

internal sealed record RustKernelRelicCounterRuleDto(
    string Id,
    bool Enabled,
    int Minimum,
    int Maximum,
    int HpAllowance,
    int Priority);

internal sealed record RustKernelGrowthBudgetsDto(
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, int>? ThirdParty = null);

internal sealed record RustKernelNoveltySearchOptionsDto(
    int Width,
    int FamiliarAllowance,
    int MaxOpen,
    int MaxNoveltyEntries,
    int MaxActions,
    int MaxTurns);

internal sealed record RustKernelNoveltyPortfolioBudgetDto(
    int TimeDivisor,
    int MaximumMilliseconds,
    int MaximumExpandedNodes);

internal sealed record RustKernelPolicyDto(
    string PotionPolicy,
    IReadOnlyList<RustKernelPotionDirectiveDto> PotionDirectives,
    IReadOnlyDictionary<string, int> PotionHpThresholdOverrides,
    bool PredictPotionReward,
    bool RelicStrategyEnabled,
    IReadOnlyList<RustKernelRelicCounterRuleDto> RelicCounterRules,
    RustKernelGrowthBudgetsDto GrowthBudgets,
    int? BrightestFlameMaxHpLossLimit,
    bool IgnoreLongTermRewards,
    string? ActTransitionBossHpStrategy,
    string? FinalBossHpStrategy,
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RustKernelNoveltySearchOptionsDto? NoveltySearch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RustKernelNoveltyPortfolioBudgetDto? NoveltyBudget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EarlyTurnExplorationDepth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EarlyTurnExplorationBudgetMilliseconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PortfolioSchedulingMode = null);

internal sealed record RustKernelRequestDto(
    int AbiVersion,
    string RequestId,
    RustKernelLiveStateDto LiveState,
    string CharacterId,
    byte Ascension,
    string GameMode,
    string? DominionId,
    IReadOnlyList<string> RunModelIds,
    bool EncounterIsWeak,
    int InitialEncounterMonsterCount,
    int TotalFloor,
    int ActIndex,
    int MapCol,
    int MapRow,
    int PriorBattleHpLost,
    int SearchBudgetMilliseconds,
    int OverallTimeoutMilliseconds,
    int MaxDegreeOfParallelism,
    RustKernelPolicyDto Policy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PotionRewardReplacementHpCredit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsNonFinalAct3Boss = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? InitialBrightestFlameMaxHpSpent = null);

internal sealed record RustKernelChoiceCardTokenDto(
    string CardId,
    int UpgradeLevel,
    int OptionOccurrence,
    int SourceOccurrence);

internal sealed record RustKernelChoiceTraceDto(
    string Timing,
    string SourceId,
    string SourcePile,
    IReadOnlyList<RustKernelChoiceCardTokenDto> Cards);

internal sealed record RustKernelActionTraceDto(
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
    IReadOnlyList<RustKernelChoiceTraceDto> Choices,
    IReadOnlyList<string> FlashedRelicIds,
    IReadOnlyList<uint> KilledEnemyCombatIds);

internal sealed record RustKernelTurnOutcomeDto(
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

internal sealed record RustKernelTerminalSnapshotDto(
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

internal sealed record RustKernelPotionUseDto(
    string Id,
    int? Turn,
    int? Slot);

internal sealed record RustKernelResultDto(
    IReadOnlyList<RustKernelActionTraceDto> Actions,
    IReadOnlyList<RustKernelTurnOutcomeDto> TurnOutcomes,
    RustKernelTerminalSnapshotDto Terminal,
    IReadOnlyList<RustKernelPotionUseDto> PotionUses,
    long ExpandedNodes,
    double SearchElapsedMilliseconds,
    long? ExpandedParents = null,
    long? TransitionCount = null,
    long? ChoiceReplayAttempts = null,
    int? ObservedPeakParallelism = null,
    string? ActivePhaseLabel = null);

internal sealed record RustKernelProgressDto(
    long ExpandedNodes,
    int MaxTurnReached,
    double ElapsedMilliseconds,
    int CurrentBestProjectedHpLoss,
    bool CurrentBestWon,
    int? CurrentBestCombatEndedTurn,
    int CurrentBestPotionCount,
    RustKernelResultDto? CurrentBest,
    long? ExpandedParents = null,
    long? TransitionCount = null,
    long? ChoiceReplayAttempts = null,
    int? ObservedPeakParallelism = null,
    string? ActivePhaseLabel = null);

internal sealed record RustKernelResponseDto(
    int AbiVersion,
    string Outcome,
    string? FailureCode,
    string? FieldPath,
    string? Error,
    RustKernelResultDto? Result,
    RustKernelResultDto? BestSoFar,
    int EffectiveDegreeOfParallelism,
    double TotalElapsedMilliseconds);

internal static class RustKernelWireValidator
{
    public static void ValidatePolicy(RustKernelPolicyDto policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.PotionPolicy))
            throw new InvalidDataException("RustKernel policy.potionPolicy must not be empty.");
        if (policy.BeamWidth <= 0)
            throw new InvalidDataException("RustKernel policy.beamWidth must be positive.");
        if (policy.MaxExpandedNodes <= 0)
            throw new InvalidDataException("RustKernel policy.maxExpandedNodes must be positive.");
        if (policy.MaxCardBranchesPerNode <= 0
            || policy.MaxPileChoiceBranchesPerAction <= 0
            || policy.MaxHandChoiceBranchesPerAction <= 0)
        {
            throw new InvalidDataException("RustKernel branch caps must be positive.");
        }
        if (policy.SoftTimeBudgetMilliseconds <= 0)
            throw new InvalidDataException("RustKernel softTimeBudgetMilliseconds must be positive.");
        foreach (RustKernelPotionDirectiveDto d in policy.PotionDirectives)
        {
            if (d.Slot < 0 || string.IsNullOrWhiteSpace(d.PotionId) || string.IsNullOrWhiteSpace(d.Directive))
                throw new InvalidDataException("RustKernel potion directive contains invalid slot/id/directive.");
        }
        foreach (KeyValuePair<string, int> kv in policy.PotionHpThresholdOverrides)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is < 0 or > 100)
                throw new InvalidDataException($"RustKernel potion threshold override '{kv.Key}'={kv.Value} is invalid.");
        }
    }

    public static RustKernelProgressDto DeserializeAndValidateProgress(byte[] utf8)
    {
        RustKernelProgressDto? progress = JsonSerializer.Deserialize<RustKernelProgressDto>(utf8, RustKernelAbi.JsonOptions);
        if (progress is null)
            throw new InvalidDataException("RustKernel progress payload deserialized to null.");
        if (progress.ExpandedNodes < 0 || progress.MaxTurnReached < 0 || progress.ElapsedMilliseconds < 0d)
            throw new InvalidDataException("RustKernel progress payload contains negative metrics.");
        return progress;
    }

    public static RustKernelResponseDto DeserializeAndValidateResponse(byte[] utf8)
    {
        RustKernelResponseDto? response = JsonSerializer.Deserialize<RustKernelResponseDto>(utf8, RustKernelAbi.JsonOptions);
        if (response is null)
            throw new InvalidDataException("RustKernel response payload deserialized to null.");
        ValidateResponse(response);
        return response;
    }

    public static void ValidateResponse(RustKernelResponseDto response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.AbiVersion != RustKernelAbi.Version)
        {
            throw new InvalidDataException(
                $"RustKernel response ABI mismatch: expected {RustKernelAbi.Version}, got {response.AbiVersion}.");
        }
        if (response.Outcome is not ("Succeeded" or "CapabilityMiss" or "Failed" or "Cancelled" or "TimedOut"))
            throw new InvalidDataException($"Unknown RustKernel response outcome '{response.Outcome}'.");
        if (response.EffectiveDegreeOfParallelism < 1)
            throw new InvalidDataException("RustKernel response effectiveDegreeOfParallelism must be >= 1.");
        if (response.Outcome == "Succeeded" && response.Result is null)
            throw new InvalidDataException("RustKernel Succeeded response is missing Result.");
    }
}
