using System.Collections.Frozen;

namespace Sts2Sim.PreCombatBridge;

internal static class InCombatBridgeAbi
{
    public const int Version = 1;
    public const string SupportedGameVersion = BridgeAbi.SupportedGameVersion;
    public const string SupportedGameCommit = BridgeAbi.SupportedGameCommit;
    public const int MaxSoftTimeBudgetMilliseconds = 600_000;

    public static readonly FrozenSet<string> AllowlistedFailureCodes = BridgeAbi.AllowlistedFailureCodes
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

internal sealed record InCombatPolicyDto(
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
    int? PotionFreeBeamWidth,
    int? PotionBeamWidth,
    int MaxExpandedNodes,
    int MaxCardBranchesPerNode,
    int MaxPileChoiceBranchesPerAction,
    int MaxHandChoiceBranchesPerAction,
    int SoftTimeBudgetMilliseconds,
    string? TheftPolicy);

internal sealed record InCombatBridgeRequestDto(
    int AbiVersion,
    string RequestId,
    string NormalizedSaveJson,
    InCombatLiveStateDto LiveState,
    int PriorBattleHpLost,
    int SearchBudgetMilliseconds,
    int OverallTimeoutMilliseconds,
    int MaxDegreeOfParallelism,
    InCombatPolicyDto Policy);

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

internal sealed record InCombatResultDto(
    IReadOnlyList<InCombatActionTraceDto> Actions,
    IReadOnlyList<InCombatTurnOutcomeDto> TurnOutcomes,
    InCombatTerminalSnapshotDto Terminal,
    IReadOnlyList<BridgePotionUseDto> PotionUses,
    long ExpandedNodes,
    double SearchElapsedMilliseconds);

internal sealed record InCombatProgressDto(
    long ExpandedNodes,
    int MaxTurnReached,
    double ElapsedMilliseconds,
    int CurrentBestProjectedHpLoss,
    bool CurrentBestWon,
    int? CurrentBestCombatEndedTurn,
    int CurrentBestPotionCount,
    InCombatResultDto? CurrentBest);

internal sealed record InCombatBridgeResponseDto(
    int AbiVersion,
    string Outcome,
    string? FailureCode,
    string? FieldPath,
    string? Error,
    InCombatResultDto? Result,
    InCombatResultDto? BestSoFar,
    int EffectiveDegreeOfParallelism,
    double TotalElapsedMilliseconds)
{
    public static InCombatBridgeResponseDto Succeeded(
        InCombatResultDto result,
        int effectiveDegreeOfParallelism,
        double totalElapsedMilliseconds) =>
        new(
            InCombatBridgeAbi.Version,
            "Succeeded",
            null,
            null,
            null,
            result,
            null,
            effectiveDegreeOfParallelism,
            totalElapsedMilliseconds);

    public static InCombatBridgeResponseDto CapabilityMiss(
        string failureCode,
        string? fieldPath,
        string error,
        double totalElapsedMilliseconds)
    {
        if (!InCombatBridgeAbi.AllowlistedFailureCodes.Contains(failureCode))
            throw new ArgumentOutOfRangeException(nameof(failureCode), $"FailureCode '{failureCode}' is not allowlisted.");
        return new(
            InCombatBridgeAbi.Version,
            "CapabilityMiss",
            failureCode,
            fieldPath,
            string.IsNullOrWhiteSpace(error) ? failureCode : error,
            null,
            null,
            1,
            Math.Max(0d, totalElapsedMilliseconds));
    }

    public static InCombatBridgeResponseDto CancelledOrTimedOut(
        string outcome,
        string error,
        InCombatResultDto? bestSoFar,
        int effectiveDegreeOfParallelism,
        double totalElapsedMilliseconds) =>
        new(
            InCombatBridgeAbi.Version,
            outcome,
            null,
            null,
            error,
            null,
            bestSoFar,
            effectiveDegreeOfParallelism,
            Math.Max(0d, totalElapsedMilliseconds));

    public static InCombatBridgeResponseDto NonSuccess(
        string outcome,
        string? fieldPath,
        string error,
        double totalElapsedMilliseconds) =>
        new(
            InCombatBridgeAbi.Version,
            outcome,
            null,
            fieldPath,
            string.IsNullOrWhiteSpace(error) ? outcome : error,
            null,
            null,
            1,
            Math.Max(0d, totalElapsedMilliseconds));
}
