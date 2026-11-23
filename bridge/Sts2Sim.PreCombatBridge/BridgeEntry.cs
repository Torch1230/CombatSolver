using System.Diagnostics;
using System.Text.Json;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Map;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using Sts2Sim.Core.Runs.Transplant;
using CombatSolver.Beam;
using CombatSolver.Beam.Policy;

namespace Sts2Sim.PreCombatBridge;

public static class BridgeEntry
{
    private static readonly object InitSync = new();
    private static bool _initialized;

    public static int GetAbiVersion() => BridgeAbi.Version;

    public static int GetInCombatAbiVersion() => InCombatBridgeAbi.Version;

    public static string GetBundleIdentityJson()
    {
        var dto = new BridgeBundleIdentityDto(
            BridgeAbi.Version,
            BridgeAbi.SupportedGameVersion,
            BridgeAbi.SupportedGameCommit,
            typeof(BridgeEntry).Module.ModuleVersionId.ToString("D"),
            typeof(BeamSearch).Module.ModuleVersionId.ToString("D"),
            typeof(CombatState).Module.ModuleVersionId.ToString("D"));
        return JsonSerializer.Serialize(dto, BridgeAbi.JsonOptions);
    }

    public static string GetCapabilitiesJson()
    {
        var dto = new BridgeCapabilitiesDto(
            PortfolioOptionsVersion: BridgeAbi.PortfolioOptionsVersion,
            SupportsBeamWidthPortfolioFull: true,
            SupportsNoveltyPortfolio: true,
            SupportsAdaptiveNoveltyRefinement: true,
            SupportsEarlyTurnExploration: true,
            MaxEarlyTurnExplorationDepth: BridgeAbi.MaxEarlyTurnExplorationDepth,
            MaxEarlyTurnExplorationBudgetMilliseconds: BridgeAbi.MaxEarlyTurnExplorationBudgetMilliseconds,
            SupportsParallelPortfolioScheduling: true,
            SupportsExtendedMetrics: true,
            SupportsCosmeticTestSubjectKills: true);
        return JsonSerializer.Serialize(dto, BridgeAbi.JsonOptions);
    }

    public static void Prewarm()
    {
        if (Volatile.Read(ref _initialized))
            return;

        lock (InitSync)
        {
            if (_initialized)
                return;

            ModelDb.Init(ContentRegistry.AllTypes);
            ModelDb.Prewarm();
            EncounterCatalog.EnsureInitialized();
            Volatile.Write(ref _initialized, true);
        }
    }

    public static async Task<byte[]> RunJsonAsync(byte[] requestUtf8Json, CancellationToken cancellationToken)
    {
        var totalWatch = Stopwatch.StartNew();
        using var overallCts = new CancellationTokenSource();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prewarm();
            cancellationToken.ThrowIfCancellationRequested();

            if (requestUtf8Json is null || requestUtf8Json.Length == 0)
                throw new BridgeRequestFailedException(null, "Bridge request payload cannot be empty.");

            BridgeRequestDto? request;
            try
            {
                request = JsonSerializer.Deserialize<BridgeRequestDto>(requestUtf8Json, BridgeAbi.JsonOptions);
            }
            catch (JsonException error)
            {
                throw new BridgeRequestFailedException(null, $"Malformed bridge request JSON: {error.Message}", error);
            }

            if (request is null)
                throw new BridgeRequestFailedException(null, "Bridge request JSON deserialized to null.");
            if (request.AbiVersion != BridgeAbi.Version)
            {
                throw new BridgeRequestFailedException(
                    "abiVersion",
                    $"Unsupported bridge ABI version {request.AbiVersion}; expected {BridgeAbi.Version}.");
            }
            if (string.IsNullOrWhiteSpace(request.RequestId))
                throw new BridgeRequestFailedException("requestId", "RequestId cannot be empty.");
            if (string.IsNullOrWhiteSpace(request.EncounterId))
                throw new BridgeRequestFailedException("encounterId", "EncounterId cannot be empty.");
            if (request.Policy is null)
                throw new BridgeRequestFailedException("policy", "Missing policy object.");

            (CombatPolicy basePolicy, BeamBudget budget, PreCombatPortfolioOptions portfolio) =
                BridgePolicyResolver.ValidateAndBuildBasePolicy(
                    request.Policy,
                    request.SearchBudgetMilliseconds,
                    request.OverallTimeoutMilliseconds,
                    request.MaxDegreeOfParallelism);
            int effectiveDop = Math.Clamp(request.MaxDegreeOfParallelism, 1, Math.Max(1, Environment.ProcessorCount));

            double remainingOverallMs = request.OverallTimeoutMilliseconds - totalWatch.Elapsed.TotalMilliseconds;
            if (remainingOverallMs <= 0d)
                overallCts.Cancel();
            else
                overallCts.CancelAfter(TimeSpan.FromMilliseconds(remainingOverallMs));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                overallCts.Token);
            CancellationToken effectiveToken = linkedCts.Token;
            effectiveToken.ThrowIfCancellationRequested();

            RunState run = BridgeSaveRestore.Restore(request);
            effectiveToken.ThrowIfCancellationRequested();
            if (!EncounterCatalog.TryGet(request.EncounterId, out EncounterCatalogEntry encounterEntry))
            {
                throw new BridgeCapabilityMissException(
                    "encounter_unmapped",
                    "encounterId",
                    $"Encounter '{request.EncounterId}' is not registered in EncounterCatalog.");
            }

            RoomType roomKind = ParseRoomKind(request.RoomKind, "roomKind");
            if (encounterEntry.RoomType != roomKind)
            {
                throw new BridgeCapabilityMissException(
                    "room_kind_mismatch",
                    "roomKind",
                    $"Encounter '{request.EncounterId}' has room type {encounterEntry.RoomType}, but request specified {roomKind}.");
            }

            MapPointType declaredMapPointKind = ParseMapPointKind(request.MapPointKind, "mapPointKind");
            ForecastInterveningStep[] interveningSteps = ParseInterveningSteps(request.InterveningMapPoints);

            effectiveToken.ThrowIfCancellationRequested();
            run.ApplyInterveningMapPoints(interveningSteps, request.TargetActFloor - 1);
            effectiveToken.ThrowIfCancellationRequested();

            if (request.IsSecondBoss)
            {
                run.SetSecondBossEncounterOnCurrentAct(encounterEntry.Definition);
            }

            if (request.PlayerCurrentHpOverride is int hpOverride)
            {
                run.ApplyPlayerCurrentHpOverride(hpOverride);
            }

            ulong? encounterSeedOverride = request.SimulationSeed is ulong simSeed
                ? run.ApplySimulationSeed(simSeed)
                : null;

            var targetSpec = new ForecastTargetSpec(
                encounterEntry.Definition,
                roomKind,
                request.TargetActFloor,
                request.TargetMapColumn,
                declaredMapPointKind,
                request.IsSecondBoss);

            var openingRunner = new ForecastOpeningRunner(effectiveToken);
            BeamForecastResult forecast;
            try
            {
                ForecastRoomEntryResult entry = await run.EnterForecastCombatAsync(
                    targetSpec,
                    openingRunner,
                    encounterSeedOverride).ConfigureAwait(false);

                CombatState combatRoot = entry.Room.Engine.State;
                CombatPolicy resolvedPolicy = BridgePolicyResolver.ResolveForCombatRoot(combatRoot, basePolicy);
                forecast = await BeamSearch.SolveForForecastAsync(
                    combatRoot,
                    budget,
                    resolvedPolicy,
                    portfolio,
                    entry.OpeningPreRootBattleHpLost,
                    effectiveToken,
                    effectiveDop).ConfigureAwait(false);
            }
            catch (ForecastOpeningPlanningRequiredException)
            {
                // fail-open: 开局出现可探索的小型必选抉择时，切换到共享预算的多叶子开局规划器完成求解。
                forecast = await openingRunner.SolveOpeningAndCombatAsync(
                    budget,
                    combatRoot => BridgePolicyResolver.ResolveForCombatRoot(combatRoot, basePolicy),
                    portfolio,
                    effectiveToken,
                    effectiveDop).ConfigureAwait(false);
            }

            BridgePotionUseDto[] wirePotionUses = forecast.PotionUses
                .Select(use => new BridgePotionUseDto(use.Id, use.Turn, use.Slot))
                .ToArray();

            var response = BridgeResponseDto.Succeeded(
                forecast.ProjectedHpLoss,
                forecast.FinalHp,
                forecast.CombatEndedTurn,
                forecast.OnlyDeathRoutes,
                forecast.Confidence.ToString(),
                forecast.SearchBoundary.ToString(),
                wirePotionUses,
                forecast.ExpandedNodes,
                forecast.SearchElapsedMilliseconds,
                totalWatch.Elapsed.TotalMilliseconds,
                effectiveDop);

            return JsonSerializer.SerializeToUtf8Bytes(response, BridgeAbi.JsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // fail-open: 外部取消请求转换为 ABI v1 Cancelled 响应，保持度量字段为空。
            var cancelled = BridgeResponseDto.NonSuccess(
                "Cancelled",
                null,
                "The pre-combat bridge request was cancelled.",
                totalWatch.Elapsed.TotalMilliseconds);
            return JsonSerializer.SerializeToUtf8Bytes(cancelled, BridgeAbi.JsonOptions);
        }
        catch (OperationCanceledException) when (overallCts.IsCancellationRequested)
        {
            // fail-open: 达到 overallTimeoutMilliseconds 硬期限后转换为 ABI v1 TimedOut 响应。
            var timedOut = BridgeResponseDto.NonSuccess(
                "TimedOut",
                null,
                "The pre-combat bridge request exceeded overallTimeoutMilliseconds.",
                totalWatch.Elapsed.TotalMilliseconds);
            return JsonSerializer.SerializeToUtf8Bytes(timedOut, BridgeAbi.JsonOptions);
        }
        catch (BridgeCapabilityMissException miss)
        {
            // fail-open: 已分类的能力缺失转换为 ABI v1 CapabilityMiss 响应，供调用方回退决策。
            var capabilityMiss = BridgeResponseDto.CapabilityMiss(
                miss.FailureCode,
                miss.FieldPath,
                miss.Message,
                totalWatch.Elapsed.TotalMilliseconds);
            return JsonSerializer.SerializeToUtf8Bytes(capabilityMiss, BridgeAbi.JsonOptions);
        }
        catch (ForecastCapabilityMissException miss)
        {
            // fail-open: 核心层上报的能力缺失转换为 ABI v1 CapabilityMiss 响应。
            var capabilityMiss = BridgeResponseDto.CapabilityMiss(
                miss.FailureCode,
                miss.FieldPath,
                miss.Message,
                totalWatch.Elapsed.TotalMilliseconds);
            return JsonSerializer.SerializeToUtf8Bytes(capabilityMiss, BridgeAbi.JsonOptions);
        }
        catch (OpeningChoiceUnsupportedException openingUnsupported)
        {
            // fail-open: 开局选择超出桥接受支持子集，映射为 opening_choice_unsupported。
            var capabilityMiss = BridgeResponseDto.CapabilityMiss(
                "opening_choice_unsupported",
                openingUnsupported.FieldPath ?? "openingChoice",
                openingUnsupported.Message,
                totalWatch.Elapsed.TotalMilliseconds);
            return JsonSerializer.SerializeToUtf8Bytes(capabilityMiss, BridgeAbi.JsonOptions);
        }
        catch (ProjectionUnsupportedException projectionUnsupported)
        {
            // fail-open: 威胁推演遇到未建模机制，映射为 projection_unsupported。
            var capabilityMiss = BridgeResponseDto.CapabilityMiss(
                "projection_unsupported",
                "projection",
                projectionUnsupported.Message,
                totalWatch.Elapsed.TotalMilliseconds);
            return JsonSerializer.SerializeToUtf8Bytes(capabilityMiss, BridgeAbi.JsonOptions);
        }
        catch (TransplantMappingException mappingError)
        {
            // fail-open: 模型或 SavedProperty 移植映射错误按协议细分为 CapabilityMiss 或 Failed。
            BridgeResponseDto mapped = ClassifyTransplantMappingError(mappingError, totalWatch.Elapsed.TotalMilliseconds);
            return JsonSerializer.SerializeToUtf8Bytes(mapped, BridgeAbi.JsonOptions);
        }
        catch (BridgeRequestFailedException failed)
        {
            // fail-open: 结构性请求或一致性校验失败转换为 ABI v1 Failed 响应。
            var response = BridgeResponseDto.NonSuccess(
                "Failed",
                failed.FieldPath,
                failed.Message,
                totalWatch.Elapsed.TotalMilliseconds);
            return JsonSerializer.SerializeToUtf8Bytes(response, BridgeAbi.JsonOptions);
        }
        catch (Exception error)
        {
            // fail-open: 未预期的桥接或求解器异常转换为 ABI v1 Failed 响应，避免跨越边界抛未处理异常。
            var response = BridgeResponseDto.NonSuccess(
                "Failed",
                null,
                error.Message,
                totalWatch.Elapsed.TotalMilliseconds);
            return JsonSerializer.SerializeToUtf8Bytes(response, BridgeAbi.JsonOptions);
        }
    }

    internal static BridgeResponseDto ClassifyTransplantMappingError(
        TransplantMappingException error,
        double totalElapsedMilliseconds) =>
        error.ErrorKind switch
        {
            TransplantMappingErrorKind.EnchantmentPropsUnsupported => BridgeResponseDto.CapabilityMiss(
                "enchantment_props_unsupported",
                error.FieldPath,
                error.Message,
                totalElapsedMilliseconds),
            TransplantMappingErrorKind.SavedPropertyUnsupported => BridgeResponseDto.CapabilityMiss(
                "saved_property_unsupported",
                error.FieldPath,
                error.Message,
                totalElapsedMilliseconds),
            TransplantMappingErrorKind.ModelUnmapped => BridgeResponseDto.CapabilityMiss(
                "model_unmapped",
                error.FieldPath,
                error.Message,
                totalElapsedMilliseconds),
            _ => BridgeResponseDto.NonSuccess("Failed", error.FieldPath, error.Message, totalElapsedMilliseconds),
        };

    /// <summary>
    /// Runs an in-combat forecast request. When <paramref name="progressCallback"/> is <see langword="null"/>,
    /// <see cref="InCombatBridgeResponseDto.ProgressCallbacksDrained"/> is omitted (<see langword="null"/>).
    /// When provided, returning with <c>ProgressCallbacksDrained == true</c> guarantees the callback gate is closed
    /// and all admitted host invocations have finished (zero post-return host calls). If bounded shutdown expires
    /// while a previously admitted callback has not exited, the gate seals against any further admissions and returns
    /// with <c>ProgressCallbacksDrained == false</c>.
    /// </summary>
    public static async Task<byte[]> RunInCombatJsonAsync(
        byte[] requestUtf8Json,
        Action<byte[]>? progressCallback,
        CancellationToken cancellationToken)
    {
        var totalWatch = Stopwatch.StartNew();
        using var overallCts = new CancellationTokenSource();
        int effectiveDop = 1;
        InCombatBeamResult? partialBest = null;
        bool? progressCallbacksDrained = progressCallback is null ? null : true;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prewarm();
            cancellationToken.ThrowIfCancellationRequested();

            if (requestUtf8Json is null || requestUtf8Json.Length == 0)
                throw new BridgeRequestFailedException(null, "In-combat bridge request payload cannot be empty.");

            InCombatBridgeRequestDto? request;
            try
            {
                request = JsonSerializer.Deserialize<InCombatBridgeRequestDto>(requestUtf8Json, BridgeAbi.JsonOptions);
            }
            catch (JsonException error)
            {
                throw new BridgeRequestFailedException(null, $"Malformed in-combat bridge request JSON: {error.Message}", error);
            }

            if (request is null)
                throw new BridgeRequestFailedException(null, "In-combat bridge request JSON deserialized to null.");
            if (request.AbiVersion != InCombatBridgeAbi.Version)
            {
                throw new BridgeRequestFailedException(
                    "abiVersion",
                    $"Unsupported in-combat bridge ABI version {request.AbiVersion}; expected {InCombatBridgeAbi.Version}.");
            }
            if (string.IsNullOrWhiteSpace(request.RequestId))
                throw new BridgeRequestFailedException("requestId", "RequestId cannot be empty.");
            if (request.LiveState is null)
                throw new BridgeRequestFailedException("liveState", "Missing liveState object.");
            if (request.Policy is null)
                throw new BridgeRequestFailedException("policy", "Missing policy object.");
            if (request.PriorBattleHpLost < 0)
                throw new BridgeRequestFailedException("priorBattleHpLost", "PriorBattleHpLost cannot be negative.");

            (CombatPolicy basePolicy, BeamBudget budget, PreCombatPortfolioOptions portfolio) =
                BridgePolicyResolver.ValidateAndBuildInCombatPolicy(
                    request.Policy,
                    request.SearchBudgetMilliseconds,
                    request.OverallTimeoutMilliseconds,
                    request.MaxDegreeOfParallelism);
            effectiveDop = Math.Clamp(request.MaxDegreeOfParallelism, 1, Math.Max(1, Environment.ProcessorCount));

            double remainingOverallMs = request.OverallTimeoutMilliseconds - totalWatch.Elapsed.TotalMilliseconds;
            if (remainingOverallMs <= 0d)
                overallCts.Cancel();
            else
                overallCts.CancelAfter(TimeSpan.FromMilliseconds(remainingOverallMs));
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                overallCts.Token);
            CancellationToken effectiveToken = linkedCts.Token;
            effectiveToken.ThrowIfCancellationRequested();

            RunState run = BridgeSaveRestore.RestoreLive(request.NormalizedSaveJson);
            effectiveToken.ThrowIfCancellationRequested();
            LiveCombatSnapshotSpec liveSpec = LiveCombatTransplantMapper.Map(request.LiveState, effectiveToken);
            effectiveToken.ThrowIfCancellationRequested();

            CombatRoom room = CombatState.RestoreLiveCombat(run, liveSpec);
            effectiveToken.ThrowIfCancellationRequested();
            CombatState combatRoot = room.Engine.State;
            CombatPolicy resolvedPolicy = BridgePolicyResolver.ResolveForCombatRoot(combatRoot, basePolicy);

            var progressGate = new ProgressCallbackGate();
            Func<InCombatProgressUpdate, bool>? progressBridge = progressCallback is null
                ? null
                : update =>
                {
                    if (progressGate.IsClosed
                        || (!update.IsFinalMetricsFlush && effectiveToken.IsCancellationRequested))
                    {
                        return false;
                    }
                    byte[] wireBytes = JsonSerializer.SerializeToUtf8Bytes(ToProgressDto(update), BridgeAbi.JsonOptions);
                    if (progressGate.IsClosed
                        || (!update.IsFinalMetricsFlush && effectiveToken.IsCancellationRequested))
                    {
                        return false;
                    }
                    return progressGate.TryInvoke(
                        () => progressCallback(wireBytes),
                        canInvoke: () => update.IsFinalMetricsFlush || !effectiveToken.IsCancellationRequested);
                };

            InCombatBeamResult result;
            try
            {
                result = await BeamSearch.SolveForInCombatWithDeliveryTrackingAsync(
                    combatRoot,
                    budget,
                    resolvedPolicy,
                    portfolio,
                    request.PriorBattleHpLost,
                    progressBridge,
                    effectiveToken,
                    effectiveDop,
                    onPartialResult: partial => Volatile.Write(ref partialBest, partial),
                    hardDeadlineToken: overallCts.Token).ConfigureAwait(false);
            }
            finally
            {
                if (progressCallback is not null)
                {
                    progressCallbacksDrained = progressGate.Close(
                        overallCts.IsCancellationRequested
                            ? TimeSpan.FromMilliseconds(25)
                            : InCombatSearchTracker.CallbackCloseDrainBudget);
                }
            }

            var response = InCombatBridgeResponseDto.Succeeded(
                ToResultDto(result),
                effectiveDop,
                totalWatch.Elapsed.TotalMilliseconds,
                progressCallbacksDrained);
            return JsonSerializer.SerializeToUtf8Bytes(response, BridgeAbi.JsonOptions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // fail-open: 外部取消请求转换为 Cancelled 响应，并携带已完成的最优部分结果（如有）。
            InCombatBeamResult? captured = Volatile.Read(ref partialBest);
            var cancelled = InCombatBridgeResponseDto.CancelledOrTimedOut(
                "Cancelled",
                "The in-combat bridge request was cancelled.",
                captured is null ? null : ToResultDto(captured),
                effectiveDop,
                totalWatch.Elapsed.TotalMilliseconds,
                progressCallbacksDrained);
            return JsonSerializer.SerializeToUtf8Bytes(cancelled, BridgeAbi.JsonOptions);
        }
        catch (OperationCanceledException) when (overallCts.IsCancellationRequested)
        {
            // fail-open: 达到 overallTimeoutMilliseconds 硬期限后转换为 TimedOut 响应并附带部分最优路线。
            InCombatBeamResult? captured = Volatile.Read(ref partialBest);
            var timedOut = InCombatBridgeResponseDto.CancelledOrTimedOut(
                "TimedOut",
                "The in-combat bridge request exceeded overallTimeoutMilliseconds.",
                captured is null ? null : ToResultDto(captured),
                effectiveDop,
                totalWatch.Elapsed.TotalMilliseconds,
                progressCallbacksDrained);
            return JsonSerializer.SerializeToUtf8Bytes(timedOut, BridgeAbi.JsonOptions);
        }
        catch (BridgeCapabilityMissException miss)
        {
            // fail-open: 桥接层明确不支持或能力缺失转换为 CapabilityMiss，供 CombatSolver 回退内置模拟器。
            var capabilityMiss = InCombatBridgeResponseDto.CapabilityMiss(
                miss.FailureCode,
                miss.FieldPath,
                miss.Message,
                totalWatch.Elapsed.TotalMilliseconds,
                progressCallbacksDrained);
            return JsonSerializer.SerializeToUtf8Bytes(capabilityMiss, BridgeAbi.JsonOptions);
        }
        catch (ForecastCapabilityMissException miss)
        {
            // fail-open: 核心层移植校验与私有状态映射失败转换为 CapabilityMiss。
            var capabilityMiss = InCombatBridgeResponseDto.CapabilityMiss(
                miss.FailureCode,
                miss.FieldPath,
                miss.Message,
                totalWatch.Elapsed.TotalMilliseconds,
                progressCallbacksDrained);
            return JsonSerializer.SerializeToUtf8Bytes(capabilityMiss, BridgeAbi.JsonOptions);
        }
        catch (ProjectionUnsupportedException projectionUnsupported)
        {
            // fail-open: 威胁推演遇到未建模机制，映射为 projection_unsupported。
            var capabilityMiss = InCombatBridgeResponseDto.CapabilityMiss(
                "projection_unsupported",
                "projection",
                projectionUnsupported.Message,
                totalWatch.Elapsed.TotalMilliseconds,
                progressCallbacksDrained);
            return JsonSerializer.SerializeToUtf8Bytes(capabilityMiss, BridgeAbi.JsonOptions);
        }
        catch (TransplantMappingException mappingError)
        {
            // fail-open: 存档移植映射错误按协议细分为 CapabilityMiss 或 Failed。
            BridgeResponseDto mapped = ClassifyTransplantMappingError(mappingError, totalWatch.Elapsed.TotalMilliseconds);
            InCombatBridgeResponseDto converted = mapped.Outcome == "CapabilityMiss"
                ? InCombatBridgeResponseDto.CapabilityMiss(
                    mapped.FailureCode!,
                    mapped.FieldPath,
                    mapped.Error ?? mapped.FailureCode!,
                    totalWatch.Elapsed.TotalMilliseconds,
                    progressCallbacksDrained)
                : InCombatBridgeResponseDto.NonSuccess(
                    "Failed",
                    mapped.FieldPath,
                    mapped.Error ?? "Failed",
                    totalWatch.Elapsed.TotalMilliseconds,
                    progressCallbacksDrained);
            return JsonSerializer.SerializeToUtf8Bytes(converted, BridgeAbi.JsonOptions);
        }
        catch (BridgeRequestFailedException failed)
        {
            // fail-open: 结构性请求校验失败转换为 Failed 响应。
            var response = InCombatBridgeResponseDto.NonSuccess(
                "Failed",
                failed.FieldPath,
                failed.Message,
                totalWatch.Elapsed.TotalMilliseconds,
                progressCallbacksDrained);
            return JsonSerializer.SerializeToUtf8Bytes(response, BridgeAbi.JsonOptions);
        }
        catch (Exception error)
        {
            // fail-open: 未预期的桥接或求解器异常转换为 Failed 响应，避免跨越加载边界抛未处理异常。
            var response = InCombatBridgeResponseDto.NonSuccess(
                "Failed",
                null,
                error.Message,
                totalWatch.Elapsed.TotalMilliseconds,
                progressCallbacksDrained);
            return JsonSerializer.SerializeToUtf8Bytes(response, BridgeAbi.JsonOptions);
        }
    }

    private static InCombatProgressDto ToProgressDto(InCombatProgressUpdate update) =>
        new(
            update.ExpandedNodes,
            update.MaxTurnReached,
            update.ElapsedMilliseconds,
            update.CurrentBestProjectedHpLoss,
            update.CurrentBestWon,
            update.CurrentBestCombatEndedTurn,
            update.CurrentBestPotionCount,
            update.CurrentBest is null ? null : ToResultDto(update.CurrentBest),
            update.ExpandedParents,
            update.TransitionCount,
            update.ChoiceReplayAttempts,
            update.ObservedPeakParallelism,
            update.ActivePhaseLabel);

    private static InCombatResultDto ToResultDto(InCombatBeamResult r) =>
        new(
            r.Actions.Select(a => new InCombatActionTraceDto(
                a.Turn,
                a.Kind,
                a.CardId,
                a.CardOccurrence,
                a.CardUpgradeLevel,
                a.CardEnchantmentId,
                a.TargetIndex,
                a.TargetCombatId,
                a.PotionId,
                a.PotionSlot,
                a.Choices.Select(ch => new InCombatChoiceTraceDto(
                    ch.Timing,
                    ch.SourceId,
                    ch.SourcePile,
                    ch.Cards.Select(tok => new InCombatChoiceCardTokenDto(
                        tok.CardId,
                        tok.UpgradeLevel,
                        tok.OptionOccurrence,
                        tok.SourceOccurrence)).ToArray())).ToArray(),
                a.FlashedRelicIds,
                a.KilledEnemyCombatIds)).ToArray(),
            r.TurnOutcomes.Select(t => new InCombatTurnOutcomeDto(
                t.Turn,
                t.HpLost,
                t.HpRecovered,
                t.EnemyHpLost,
                t.SoldHp,
                t.MaxBlock,
                t.ActualBlock,
                t.EnergyLeft,
                t.PotionCount,
                t.PotionStrategicCost,
                t.CombatEnded)).ToArray(),
            new InCombatTerminalSnapshotDto(
                r.Terminal.PlayerDead,
                r.Terminal.AllEnemiesDead,
                r.Terminal.PlayerHp,
                r.Terminal.PlayerMaxHp,
                r.Terminal.CumulativePlayerHpLost,
                r.Terminal.RecoveredPlayerHp,
                r.Terminal.ProjectedPlayerHp,
                r.Terminal.PlayerBlock,
                r.Terminal.EnemyHp,
                r.Terminal.AliveEnemyCount,
                r.Terminal.Energy,
                r.Terminal.Stars,
                r.Terminal.HandCount,
                r.Terminal.OutstandingStolenResource,
                r.Terminal.Turn,
                r.Terminal.ShufflesCrossed,
                r.Terminal.PostCombatRelicHeal,
                r.Terminal.ProjectedBattleHpLost,
                r.Terminal.CombatEndedTurn,
                r.Terminal.DeathTurn,
                r.Terminal.OnlyDeathRoutes,
                r.Terminal.Confidence.ToString(),
                r.Terminal.SearchBoundary.ToString(),
                r.Terminal.HasRisk),
            r.PotionUses.Select(u => new BridgePotionUseDto(u.Id, u.Turn, u.Slot)).ToArray(),
            r.ExpandedNodes,
            r.SearchElapsedMilliseconds,
            r.ExpandedParents,
            r.TransitionCount,
            r.ChoiceReplayAttempts,
            r.ObservedPeakParallelism,
            r.ActivePhaseLabel);

    private static RoomType ParseRoomKind(string raw, string path) => raw switch
    {
        "Normal" => RoomType.Monster,
        "Elite" => RoomType.Elite,
        "Boss" => RoomType.Boss,
        _ => throw new BridgeRequestFailedException(
            path,
            $"RoomKind '{raw}' is invalid; expected Normal, Elite, or Boss."),
    };

    private static MapPointType ParseMapPointKind(string raw, string path) => raw switch
    {
        "Normal" => MapPointType.Monster,
        "Elite" => MapPointType.Elite,
        "Boss" => MapPointType.Boss,
        "Unknown" => MapPointType.Unknown,
        "Event" => MapPointType.Ancient,
        _ => throw new BridgeRequestFailedException(
            path,
            $"MapPointKind '{raw}' is invalid; expected Normal, Elite, Boss, Unknown, or Event."),
    };

    private static ForecastInterveningStep[] ParseInterveningSteps(IReadOnlyList<BridgeMapStepDto>? steps)
    {
        if (steps is null)
            throw new BridgeRequestFailedException("interveningMapPoints", "Missing interveningMapPoints list.");

        var result = new ForecastInterveningStep[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            BridgeMapStepDto step = steps[i];
            if (step?.Coordinate is null)
                throw new BridgeRequestFailedException($"interveningMapPoints[{i}].coordinate", "Missing coordinate.");

            if (!Enum.TryParse(step.RoomType, ignoreCase: false, out RoomType roomType) ||
                !Enum.IsDefined(roomType))
            {
                throw new BridgeRequestFailedException(
                    $"interveningMapPoints[{i}].roomType",
                    $"Unknown roomType '{step.RoomType}'.");
            }
            if (!Enum.TryParse(step.MapPointType, ignoreCase: false, out MapPointType mapPointType) ||
                !Enum.IsDefined(mapPointType))
            {
                throw new BridgeRequestFailedException(
                    $"interveningMapPoints[{i}].mapPointType",
                    $"Unknown mapPointType '{step.MapPointType}'.");
            }

            result[i] = new ForecastInterveningStep(
                new MapCoord(step.Coordinate.Col, step.Coordinate.Row),
                roomType,
                mapPointType);
        }

        return result;
    }
}
