using System.Globalization;
using CombatSolver.Api;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver;

internal static class InCombatSts2SimResultMaterializer
{
    public static SolverResult MaterializeResult(
        InCombatSts2SimResultDto dto,
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot searchPolicy,
        TimeSpan totalElapsed,
        int effectiveDegreeOfParallelism,
        SolverResultScope resultScope,
        CancellationToken continuationCancellation,
        Sts2SimAttemptWorkSnapshot? attemptTotals = null)
    {
        PlanAction[] actions = BuildActions(dto.Actions, root, displayNames);
        Dictionary<int, IReadOnlyList<string>> killsAfterAction = BuildKillsAfterAction(
            dto.Actions, root, displayNames);

        bool renewableRock = root.HasRenewablePotionShapedRock;
        Dictionary<int, int> hpLostByTurn = [];
        Dictionary<int, int> hpRecoveredByTurn = [];
        Dictionary<int, int> enemyHpLostByTurn = [];
        Dictionary<int, int> soldHpByTurn = [];
        Dictionary<int, int> maxBlockByTurn = [];
        Dictionary<int, int> actualBlockByTurn = [];
        Dictionary<int, int> energyLeftByTurn = [];
        Dictionary<int, int> potionCountByTurn = [];
        Dictionary<int, int> potionStrategicCostByTurn = [];
        Dictionary<int, ExternalRouteTurnExpectation> turnExpectations = [];

        foreach (InCombatTurnOutcomeDto turnOutcome in dto.TurnOutcomes)
        {
            if (turnOutcome.Turn < 1)
                throw new InvalidDataException($"Sts2Sim TurnOutcome reported invalid Turn={turnOutcome.Turn}.");
            ExternalRouteTurnExpectation expectation = new(
                turnOutcome.Turn,
                turnOutcome.HpLost,
                turnOutcome.HpRecovered,
                turnOutcome.EnemyHpLost,
                turnOutcome.EnergyLeft,
                turnOutcome.CombatEnded);
            if (!turnExpectations.TryAdd(turnOutcome.Turn, expectation))
                throw new InvalidDataException($"Sts2Sim result contains duplicate TurnOutcome for turn {turnOutcome.Turn}.");
            hpLostByTurn[turnOutcome.Turn] = turnOutcome.HpLost;
            hpRecoveredByTurn[turnOutcome.Turn] = turnOutcome.HpRecovered;
            enemyHpLostByTurn[turnOutcome.Turn] = turnOutcome.EnemyHpLost;
            soldHpByTurn[turnOutcome.Turn] = turnOutcome.SoldHp;
            maxBlockByTurn[turnOutcome.Turn] = turnOutcome.MaxBlock;
            actualBlockByTurn[turnOutcome.Turn] = turnOutcome.ActualBlock;
            energyLeftByTurn[turnOutcome.Turn] = turnOutcome.EnergyLeft;
            if (turnOutcome.PotionCount > 0)
                potionCountByTurn[turnOutcome.Turn] = turnOutcome.PotionCount;
            if (turnOutcome.PotionStrategicCost > 0)
                potionStrategicCostByTurn[turnOutcome.Turn] = turnOutcome.PotionStrategicCost;
        }

        foreach (PlanAction action in actions)
        {
            if (!turnExpectations.ContainsKey(action.Turn))
                throw new InvalidDataException($"Sts2Sim result is missing TurnOutcome for action turn {action.Turn}.");
        }
        if (actions.Length == 0 && !turnExpectations.ContainsKey(root.StartTurnNumber))
            throw new InvalidDataException($"Sts2Sim result is missing TurnOutcome for start turn {root.StartTurnNumber}.");

        InCombatTerminalSnapshotDto terminal = dto.Terminal;
        ExternalRouteTerminalExpectation terminalExpectation = new(
            Turn: terminal.Turn,
            PlayerDead: terminal.PlayerDead,
            AllEnemiesDead: terminal.AllEnemiesDead,
            PlayerHp: terminal.PlayerHp,
            PlayerMaxHp: terminal.PlayerMaxHp,
            PlayerBlock: terminal.PlayerBlock,
            EnemyHp: terminal.EnemyHp,
            AliveEnemyCount: terminal.AliveEnemyCount,
            Energy: terminal.Energy,
            Stars: terminal.Stars,
            HandCount: terminal.HandCount,
            CombatEndedTurn: terminal.CombatEndedTurn,
            DeathTurn: terminal.DeathTurn);

        ExternalRouteVerificationResult verification = CombatBeamSolver.VerifyAndAnnotateExternalRoute(
            root,
            displayNames,
            battleDamage,
            searchPolicy,
            actions,
            turnExpectations,
            terminalExpectation,
            verifyCurrentTurnOnly: resultScope == SolverResultScope.CurrentTurnAdoption,
            continuationCancellation);

        SearchBoundaryReason terminalBoundary = ParseSearchBoundary(terminal.SearchBoundary);
        SearchBoundaryReason resultBoundary = resultScope == SolverResultScope.RouteAdoption
            ? SearchBoundaryReason.None
            : terminalBoundary;

        IReadOnlyList<Sts2SimPotionUseDto> admittedPotionDtos = dto.PotionUses;
        int? combatEndedTurn = terminal.CombatEndedTurn;
        int? deathTurn = terminal.DeathTurn;
        int postCombatRelicHeal = terminal.PostCombatRelicHeal;
        bool onlyDeathRoutes = terminal.OnlyDeathRoutes;
        int projectedBattleHpLost = terminal.ProjectedBattleHpLost;
        int outstandingStolenResource = terminal.OutstandingStolenResource;
        int snapshotTurn = terminal.Turn;
        bool snapshotPlayerDead = terminal.PlayerDead;
        bool snapshotAllEnemiesDead = terminal.AllEnemiesDead;
        int snapshotPlayerHp = terminal.PlayerHp;
        int snapshotPlayerMaxHp = terminal.PlayerMaxHp;
        int snapshotCumulativeHpLost = terminal.CumulativePlayerHpLost;
        int snapshotRecoveredHp = terminal.RecoveredPlayerHp;
        int snapshotProjectedHp = terminal.ProjectedPlayerHp;
        int snapshotPlayerBlock = terminal.PlayerBlock;
        int snapshotEnemyHp = terminal.EnemyHp;
        int snapshotAliveEnemies = terminal.AliveEnemyCount;
        int snapshotEnergy = terminal.Energy;
        int snapshotStars = terminal.Stars;
        int snapshotHandCount = terminal.HandCount;
        int snapshotShuffles = terminal.ShufflesCrossed;
        bool snapshotHasRisk = terminal.HasRisk;
        IReadOnlyList<CachedContinuation> continuations;

        switch (verification.Status)
        {
            case ExternalRouteVerificationStatus.Annotated:
                actions = verification.AnnotatedActions.ToArray();
                continuations = resultScope == SolverResultScope.CurrentTurnAdoption
                    ? []
                    : verification.Continuations;
                break;

            case ExternalRouteVerificationStatus.UnsupportedFutureContinuation:
                if (verification.VerifiedPrefixActionCount <= 0
                    || verification.VerifiedLastTurn < root.StartTurnNumber
                    || verification.VerifiedPrefixSummary is not { } prefixSummary)
                {
                    throw new InvalidOperationException(
                        $"Sts2Sim route unsupported before completing turn {root.StartTurnNumber}: {verification.FailureReason}");
                }

                int verifiedTurn = verification.VerifiedLastTurn;
                actions = verification.AnnotatedActions.Take(verification.VerifiedPrefixActionCount).ToArray();
                killsAfterAction = killsAfterAction
                    .Where(kv => kv.Key < actions.Length)
                    .ToDictionary(static kv => kv.Key, static kv => kv.Value);
                admittedPotionDtos = dto.PotionUses
                    .Where(use => use.Turn <= verifiedTurn)
                    .ToArray();
                TrimTurnDictionary(hpLostByTurn, verifiedTurn);
                TrimTurnDictionary(hpRecoveredByTurn, verifiedTurn);
                TrimTurnDictionary(enemyHpLostByTurn, verifiedTurn);
                TrimTurnDictionary(soldHpByTurn, verifiedTurn);
                TrimTurnDictionary(maxBlockByTurn, verifiedTurn);
                TrimTurnDictionary(actualBlockByTurn, verifiedTurn);
                TrimTurnDictionary(energyLeftByTurn, verifiedTurn);
                TrimTurnDictionary(potionCountByTurn, verifiedTurn);
                TrimTurnDictionary(potionStrategicCostByTurn, verifiedTurn);

                terminalBoundary = SearchBoundaryReason.UnsupportedEffect;
                resultBoundary = SearchBoundaryReason.UnsupportedEffect;
                combatEndedTurn = null;
                deathTurn = null;
                postCombatRelicHeal = 0;
                onlyDeathRoutes = false;
                projectedBattleHpLost = battleDamage.HpLostSoFar
                    + hpLostByTurn.Where(kv => kv.Key >= root.StartTurnNumber).Sum(static kv => kv.Value);
                outstandingStolenResource = prefixSummary.OutstandingStolenResource;
                snapshotTurn = prefixSummary.Turn;
                snapshotPlayerDead = prefixSummary.PlayerDead;
                snapshotAllEnemiesDead = prefixSummary.AllEnemiesDead;
                snapshotPlayerHp = prefixSummary.PlayerHp;
                snapshotPlayerMaxHp = prefixSummary.PlayerMaxHp;
                snapshotCumulativeHpLost = prefixSummary.CumulativePlayerHpLost;
                snapshotRecoveredHp = prefixSummary.RecoveredPlayerHp;
                snapshotProjectedHp = prefixSummary.ProjectedPlayerHp;
                snapshotPlayerBlock = prefixSummary.PlayerBlock;
                snapshotEnemyHp = prefixSummary.EnemyHp;
                snapshotAliveEnemies = prefixSummary.AliveEnemyCount;
                snapshotEnergy = prefixSummary.Energy;
                snapshotStars = prefixSummary.Stars;
                snapshotHandCount = prefixSummary.HandCount;
                snapshotShuffles = prefixSummary.ShufflesCrossed;
                snapshotHasRisk = true;
                continuations = resultScope == SolverResultScope.CurrentTurnAdoption
                    ? []
                    : verification.Continuations
                        .Where(c => c.StartTurnNumber <= verifiedTurn)
                        .ToArray();

                Entry.Logger.Info(
                    $"[CombatSolver/InCombatSts2Sim] STS2SIM_FUTURE_CONTINUATION_TRUNCATED " +
                    $"verified_turn={verifiedTurn} verified_actions={actions.Length} reason={verification.FailureReason}");
                break;

            default:
                throw new InvalidOperationException(
                    $"Sts2Sim route rejected by host replay verification: {verification.FailureReason}");
        }

        PredictedPotionUse[] potionUses = admittedPotionDtos
            .Select(use =>
            {
                int slot = use.Slot ?? -1;
                int strategicCost = PotionUsePolicy.StrategicHpCost(use.Id, renewableRock);
                return new PredictedPotionUse(slot, use.Id, strategicCost, Automatic: false);
            })
            .ToArray();
        string[] plannedPotionIds = admittedPotionDtos.Select(static use => use.Id).ToArray();
        int potionHpRequired = potionUses.Sum(static use => use.StrategicHpCost);
        int explicitPotionCount = actions.Count(static a => a.Kind == PlanActionKind.UsePotion);

        int maxActionTurn = actions.Length > 0
            ? actions.Max(static a => a.Turn)
            : root.StartTurnNumber;
        int searchedTurns = Math.Max(1, maxActionTurn - root.StartTurnNumber + 1);

        SolverSnapshot snapshot = new(
            HasRisk: snapshotHasRisk,
            PlayerDead: snapshotPlayerDead,
            AllEnemiesDead: snapshotAllEnemiesDead,
            PlayerHp: snapshotPlayerHp,
            PlayerMaxHp: snapshotPlayerMaxHp,
            CumulativePlayerHpLost: snapshotCumulativeHpLost,
            RecoveredPlayerHp: snapshotRecoveredHp,
            DeathSaveRelicHpRestored: 0,
            LongTermResourceValue: 0,
            AngerCopiesGenerated: 0,
            ProjectedPlayerHp: snapshotProjectedHp,
            PlayerBlock: snapshotPlayerBlock,
            EnemyHp: snapshotEnemyHp,
            AliveEnemyCount: snapshotAliveEnemies,
            Energy: snapshotEnergy,
            Stars: snapshotStars,
            HandCount: snapshotHandCount,
            OutstandingStolenResource: outstandingStolenResource,
            Turn: snapshotTurn,
            ShufflesCrossed: snapshotShuffles,
            BoundaryReason: terminalBoundary,
            PredictionGaps: []);

        SelectedSearchPlan selectedPlan = new(
            actions,
            actions.Length,
            0d);

        Sts2SimMaterializedResultMetrics metrics = Sts2SimPortfolioPolicyBridge.ResolveResultMetrics(
            dto,
            effectiveDegreeOfParallelism);
        long totalExpandedNodes = attemptTotals is { } totals
            ? Math.Max(metrics.TotalExpandedNodes, totals.ExpandedParents)
            : metrics.TotalExpandedNodes;
        long totalTransitionCount = attemptTotals is { } totals2
            ? Math.Max(metrics.TotalTransitionCount, totals2.TransitionCount)
            : metrics.TotalTransitionCount;
        long totalChoiceBranchesEvaluated = attemptTotals is { } totals3
            ? Math.Max(metrics.TotalChoiceBranchesEvaluated, totals3.ChoiceReplayAttempts)
            : metrics.TotalChoiceBranchesEvaluated;
        int maxParallelExpansionConcurrency;
        if (dto.ObservedPeakParallelism is >= 1)
        {
            maxParallelExpansionConcurrency = attemptTotals is { } totals4
                ? Math.Max(metrics.MaxParallelExpansionConcurrency, totals4.ObservedPeakParallelism)
                : metrics.MaxParallelExpansionConcurrency;
        }
        else if (attemptTotals is { HasMeasuredPeakParallelism: true, ObservedPeakParallelism: >= 1 } measuredTotals)
        {
            maxParallelExpansionConcurrency = measuredTotals.ObservedPeakParallelism;
        }
        else
        {
            maxParallelExpansionConcurrency = attemptTotals is { } totals4
                ? Math.Max(metrics.MaxParallelExpansionConcurrency, totals4.ObservedPeakParallelism)
                : metrics.MaxParallelExpansionConcurrency;
        }
        TimeSpan effectiveTotalElapsed = attemptTotals is { } totals5 && totals5.Elapsed > totalElapsed
            ? totals5.Elapsed
            : totalElapsed;

        SolverResult result = new()
        {
            ResultScope = resultScope,
            SingleSessionSearch = true,
            TotalSearchElapsed = effectiveTotalElapsed,
            TotalWorkerAllocatedBytes = 0L,
            TotalGen0Collections = 0,
            TotalGen1Collections = 0,
            TotalGen2Collections = 0,
            TotalGcPauseDuration = TimeSpan.Zero,
            StartTurnNumber = root.StartTurnNumber,
            TurnSetupChoices = [],
            BestNode = selectedPlan,
            PotionUses = potionUses,
            Snapshot = snapshot,
            Forecast = root.Forecast,
            ExpandedNodes = metrics.ExpandedNodes,
            TotalExpandedNodes = totalExpandedNodes,
            DominatedActionsPruned = 0,
            TopQueueActionsDropped = 0,
            ActionAdmissionRepresentativesProtected = 0,
            DuplicateCardBranchesPruned = 0,
            ChoiceBranchesEvaluated = metrics.ChoiceBranchesEvaluated,
            TotalChoiceBranchesEvaluated = totalChoiceBranchesEvaluated,
            ChoiceReplayAttempts = metrics.ChoiceBranchesEvaluated,
            ShuffleBranchesPruned = 0,
            SoldHpBranchesPruned = 0,
            HpInvestmentBranchesProtected = 0,
            ReplayCount = 0,
            ForkCount = 0,
            TransitionCount = metrics.TransitionCount,
            TotalTransitionCount = totalTransitionCount,
            ReusedNodeSnapshots = 0,
            TranspositionBranchesPruned = 0,
            RepeatableNoProgressBranchesPruned = 0,
            StandPatProbes = 0,
            MaxParallelExpansionConcurrency = maxParallelExpansionConcurrency,
            TransitionCacheHits = 0,
            WorkerAllocatedBytes = 0L,
            Gen0Collections = 0,
            Gen1Collections = 0,
            Gen2Collections = 0,
            GcPauseDuration = TimeSpan.Zero,
            WorkerYieldCount = 0,
            SearchedTurns = searchedTurns,
            BoundaryReason = resultBoundary,
            UnavoidableHpLost = projectedBattleHpLost,
            SoldHp = 0,
            FutureSoldHp = 0,
            BattleHpLostSoFar = battleDamage.HpLostSoFar,
            BattleHpRecoveredOrGainedSoFar = battleDamage.HpRecoveredOrGainedSoFar,
            ProjectedBattleHpLost = projectedBattleHpLost,
            BattlePotionsUsedSoFar = battleDamage.PotionsUsedSoFar,
            BattlePotionIdsUsedSoFar = battleDamage.PotionIdsUsedSoFar,
            PlannedPotionIds = plannedPotionIds,
            PotionRewardOutlook = root.PotionRewardOutlook,
            PotionCount = plannedPotionIds.Length,
            ExplicitPotionCount = explicitPotionCount,
            PotionHpSaved = 0,
            PotionHpRequired = potionHpRequired,
            PotionBranchesRejected = 0,
            TheftPolicy = searchPolicy.TheftPolicy,
            OutstandingStolenResource = outstandingStolenResource,
            SoldHpByTurn = soldHpByTurn,
            HpLostByTurn = hpLostByTurn,
            HpRecoveredByTurn = hpRecoveredByTurn,
            PostCombatRelicHeal = postCombatRelicHeal,
            EnemyHpLostByTurn = enemyHpLostByTurn,
            MaxBlockByTurn = maxBlockByTurn,
            ActualBlockByTurn = actualBlockByTurn,
            EnergyLeftByTurn = energyLeftByTurn,
            PotionCountByTurn = potionCountByTurn,
            PotionStrategicCostByTurn = potionStrategicCostByTurn,
            KillsAfterAction = killsAfterAction,
            CombatEndedTurn = combatEndedTurn,
            DeathTurn = deathTurn,
            OnlyDeathRoutesFound = onlyDeathRoutes,
            IsActEndingBoss = root.IsActEndingBoss,
            BossHpRelief = root.BossHpRelief,
            Elapsed = totalElapsed,
            Continuations = continuations,
        };
        result.AssertCompleteTurnOutcomes();
        return result;

        static void TrimTurnDictionary(Dictionary<int, int> table, int maxTurn)
        {
            foreach (int turn in table.Keys.Where(t => t > maxTurn).ToArray())
                table.Remove(turn);
        }
    }

    public static SolverProgress MaterializeProgress(
        InCombatSts2SimProgressDto progressDto,
        int candidateVersion,
        int backendAttempt,
        Sts2SimAttemptWorkTracker attemptWork,
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot searchPolicy,
        int effectiveDegreeOfParallelism,
        int requestBudgetMilliseconds,
        CancellationToken routeAdoptionCancellation)
    {
        ArgumentNullException.ThrowIfNull(attemptWork);
        attemptWork.RecordProgress(progressDto, effectiveDegreeOfParallelism);

        SolverInterimResult? interimResult = null;
        SolverCurrentTurnPreview? turnPreview = null;
        SolverSpeculativeRoutePreview? speculativePreview = null;
        SolverRouteAdoptionSeed? adoptionSeed = null;

        Sts2SimAttemptWorkSnapshot snapshotAfterProgress = attemptWork.Snapshot();
        int previewParallelism = progressDto.ObservedPeakParallelism is >= 1
            ? progressDto.ObservedPeakParallelism.Value
            : (snapshotAfterProgress.HasMeasuredPeakParallelism && snapshotAfterProgress.ObservedPeakParallelism >= 1
                ? snapshotAfterProgress.ObservedPeakParallelism
                : Math.Max(1, effectiveDegreeOfParallelism));
        if (progressDto.CurrentBest is { Actions.Count: > 0 } bestDto)
        {
            TimeSpan elapsed = TimeSpan.FromMilliseconds(Math.Max(0d, progressDto.ElapsedMilliseconds));
            SolverResult previewResult = MaterializeResult(
                bestDto,
                root,
                displayNames,
                battleDamage,
                searchPolicy,
                elapsed,
                previewParallelism,
                SolverResultScope.CurrentTurnAdoption,
                routeAdoptionCancellation,
                snapshotAfterProgress);

            bool won = !previewResult.Snapshot.PlayerDead
                && previewResult.Snapshot.AllEnemiesDead
                && previewResult.CombatEndedTurn.HasValue;
            interimResult = new SolverInterimResult(
                Won: won,
                OutstandingStolenResource: previewResult.Snapshot.OutstandingStolenResource,
                ProjectedBattleHpLost: previewResult.ProjectedBattleHpLost,
                StrategicHpDeficit: previewResult.ProjectedBattleHpLost,
                PotionStrategicCost: previewResult.PotionHpRequired,
                ProjectedBattlePotionCount: previewResult.ProjectedBattlePotionCount,
                EnemyHp: previewResult.Snapshot.EnemyHp,
                Score: 0d,
                CombatEndedTurn: previewResult.CombatEndedTurn)
            {
                TheftPolicy = searchPolicy.TheftPolicy,
                ForfeitsEncounterGold = false,
                Survives = !previewResult.Snapshot.PlayerDead && previewResult.Snapshot.ProjectedPlayerHp > 0,
            };

            turnPreview = SolverCurrentTurnPreview.FromResult(previewResult, candidateVersion);
            speculativePreview = SolverSpeculativeRoutePreview.FromResult(previewResult, candidateVersion);
            adoptionSeed = new SolverRouteAdoptionSeed(
                candidateVersion,
                previewResult.BestNode.Actions,
                () =>
                {
                    Sts2SimAttemptWorkSnapshot latest = attemptWork.Snapshot();
                    TimeSpan adoptedElapsed = latest.Elapsed > elapsed ? latest.Elapsed : elapsed;
                    int adoptedParallelism = latest.HasMeasuredPeakParallelism && latest.ObservedPeakParallelism >= 1
                        ? (progressDto.ObservedPeakParallelism is >= 1
                            ? Math.Max(progressDto.ObservedPeakParallelism.Value, latest.ObservedPeakParallelism)
                            : latest.ObservedPeakParallelism)
                        : Math.Max(previewParallelism, latest.ObservedPeakParallelism);
                    return MaterializeResult(
                        bestDto,
                        root,
                        displayNames,
                        battleDamage,
                        searchPolicy,
                        adoptedElapsed,
                        adoptedParallelism,
                        SolverResultScope.RouteAdoption,
                        routeAdoptionCancellation,
                        latest);
                },
                backendAttempt: backendAttempt);
        }

        Sts2SimMaterializedProgressMetrics metrics = Sts2SimPortfolioPolicyBridge.ResolveProgressMetrics(progressDto);
        long cumulativeReviewedWorldlines = Math.Max(metrics.ReviewedWorldlines, attemptWork.Snapshot().ExpandedParents);
        int currentTurn = Math.Max(root.StartTurnNumber, progressDto.MaxTurnReached);
        int completedTurns = Math.Max(0, currentTurn - root.StartTurnNumber);

        return new SolverProgress(
            StartTurnNumber: root.StartTurnNumber,
            CurrentTurnNumber: currentTurn,
            CompletedTurnLayers: completedTurns,
            PlayDepth: 0,
            ExpandedNodes: metrics.ExpandedNodes,
            ReviewedWorldlines: cumulativeReviewedWorldlines,
            MaxNodes: searchPolicy.Profile.MaxExpandedNodes,
            FrontierNodes: 0,
            EndedNodes: 0,
            ElapsedMilliseconds: (long)Math.Max(0d, progressDto.ElapsedMilliseconds),
            Phase: metrics.Phase,
            CurrentBestResult: interimResult,
            CurrentTurnPreview: turnPreview,
            SpeculativeRoutePreview: speculativePreview,
            RouteAdoptionSeed: adoptionSeed,
            RequestBudgetMilliseconds: requestBudgetMilliseconds,
            BackendAttempt: backendAttempt);
    }

    private static PlanAction[] BuildActions(
        IReadOnlyList<InCombatActionTraceDto> rawActions,
        CombatRootSnapshot root,
        SolverDisplayNames displayNames)
    {
        var actions = new PlanAction[rawActions.Count];
        for (int i = 0; i < rawActions.Count; i++)
        {
            InCombatActionTraceDto src = rawActions[i];
            PlanActionKind kind = ParseActionKind(src.Kind);
            bool endsPlayerTurn = kind != PlanActionKind.EndTurn
                && i + 1 < rawActions.Count
                && rawActions[i + 1].Turn > src.Turn;

            Sts2SimPartitionedActionChoices partitioned = Sts2SimPortfolioPolicyBridge.PartitionActionChoices(
                src.Kind,
                src.CardId,
                src.PotionId,
                src.Choices);
            PlanCardChoice? primaryChoice = null;
            List<PlanCardChoice> nestedChoiceList = [];
            for (int c = 0; c < partitioned.ActionChoices.Count; c++)
            {
                bool isPrimary = c == partitioned.PrimaryIndex;
                PlanCardChoice mapped = MapChoice(partitioned.ActionChoices[c], displayNames, isPrimary);
                if (isPrimary)
                    primaryChoice = mapped;
                else
                    nestedChoiceList.Add(mapped);
            }
            PlanCardChoice[]? nestedChoices = nestedChoiceList.Count > 0
                ? nestedChoiceList.ToArray()
                : null;
            PlanCardChoice[]? mappedTurnStart = partitioned.NonActionChoices.Count > 0
                ? partitioned.NonActionChoices
                    .Select(choice => MapChoice(choice, displayNames, isPrimaryActionChoice: false))
                    .ToArray()
                : null;

            PlanRelicEffect[]? relicEffects = src.FlashedRelicIds.Count > 0
                ? src.FlashedRelicIds
                    .Select(id => new PlanRelicEffect(id, displayNames.Relic(id), string.Empty))
                    .ToArray()
                : null;

            string cardTitle = kind == PlanActionKind.PlayCard && !string.IsNullOrEmpty(src.CardId)
                ? displayNames.Card(src.CardId, src.CardUpgradeLevel)
                : string.Empty;
            string potionTitle = kind == PlanActionKind.UsePotion && !string.IsNullOrEmpty(src.PotionId)
                ? displayNames.Potion(src.PotionId)
                : string.Empty;
            string targetName = ResolveTargetName(root, displayNames, src.TargetCombatId);

            actions[i] = new PlanAction(
                Kind: kind,
                Turn: src.Turn,
                CardId: src.CardId ?? string.Empty,
                CardOccurrence: src.CardOccurrence,
                TargetIndex: src.TargetIndex,
                TargetCombatId: src.TargetCombatId,
                CardTitle: cardTitle,
                TargetName: targetName,
                Choice: primaryChoice,
                NestedChoices: nestedChoices,
                NestedChoicesBeforePrimary: partitioned.NestedChoicesBeforePrimary,
                PotionSlot: src.PotionSlot,
                PotionId: src.PotionId ?? string.Empty,
                PotionTitle: potionTitle,
                TurnStartChoices: mappedTurnStart,
                RelicEffects: relicEffects,
                ReplayCount: 0,
                CardStateKey: string.Empty,
                CardStateOccurrence: 0,
                EndsPlayerTurn: endsPlayerTurn,
                CardUpgradeLevel: src.CardUpgradeLevel,
                CardEnchantmentId: src.CardEnchantmentId ?? string.Empty);
        }
        return actions;
    }

    private static Dictionary<int, IReadOnlyList<string>> BuildKillsAfterAction(
        IReadOnlyList<InCombatActionTraceDto> rawActions,
        CombatRootSnapshot root,
        SolverDisplayNames displayNames)
    {
        var kills = new Dictionary<int, IReadOnlyList<string>>();
        for (int i = 0; i < rawActions.Count; i++)
        {
            InCombatActionTraceDto src = rawActions[i];
            if (src.KilledEnemyCombatIds.Count == 0)
                continue;

            var names = new string[src.KilledEnemyCombatIds.Count];
            for (int k = 0; k < src.KilledEnemyCombatIds.Count; k++)
            {
                string resolved = ResolveTargetName(root, displayNames, src.KilledEnemyCombatIds[k]);
                names[k] = string.IsNullOrEmpty(resolved)
                    ? string.Create(CultureInfo.InvariantCulture, $"#{src.KilledEnemyCombatIds[k]}")
                    : resolved;
            }
            kills[i] = names;
        }
        return kills;
    }

    private static PlanCardChoice MapChoice(
        InCombatChoiceTraceDto src,
        SolverDisplayNames displayNames,
        bool isPrimaryActionChoice)
    {
        PlanChoiceTiming timing = src.Timing switch
        {
            "Action" => PlanChoiceTiming.Action,
            "PlayerTurnEnd" => PlanChoiceTiming.PlayerTurnEnd,
            "EnemyTurn" => PlanChoiceTiming.EnemyTurn,
            "PlayerTurnStart" => PlanChoiceTiming.PlayerTurnStart,
            _ => throw new InvalidDataException($"Unknown Sts2Sim choice timing '{src.Timing}'."),
        };
        PileType sourcePile = src.SourcePile switch
        {
            "Hand" => PileType.Hand,
            "Draw" => PileType.Draw,
            "Discard" => PileType.Discard,
            "Exhaust" => PileType.Exhaust,
            _ => PileType.None,
        };
        string rawSourceId = src.SourceId ?? string.Empty;
        PlanChoiceEffect effect = InferChoiceEffect(rawSourceId, sourcePile);
        string contextId = InferChoiceContextId(rawSourceId, timing);
        string mappedSourceId = isPrimaryActionChoice ? string.Empty : rawSourceId;
        PlanCardToken[] tokens = src.Cards
            .Select(card => new PlanCardToken(
                card.CardId,
                card.UpgradeLevel,
                string.Empty,
                sourcePile == PileType.None ? card.OptionOccurrence : card.SourceOccurrence,
                card.OptionOccurrence,
                displayNames.Card(card.CardId, card.UpgradeLevel)))
            .ToArray();

        return new PlanCardChoice(
            effect,
            sourcePile,
            tokens,
            mappedSourceId,
            contextId,
            timing);
    }

    private static string InferChoiceContextId(string sourceId, PlanChoiceTiming timing)
    {
        if (timing != PlanChoiceTiming.PlayerTurnStart)
            return string.Empty;
        return sourceId switch
        {
            "GAMBLING_CHIP" => "AFTER_PLAYER_TURN_START",
            "TOOLBOX" => "BEFORE_HAND_DRAW",
            _ => string.Empty,
        };
    }

    private static PlanChoiceEffect InferChoiceEffect(string sourceId, PileType sourcePile) => sourceId switch
    {
        "KNOWLEDGE_DEMON" => PlanChoiceEffect.ApplyKnowledgeCurse,
        "ABUNDANCE" or "DISCOVERY" or "QUASAR" or "SPLASH"
            or "ATTACK_POTION" or "SKILL_POTION" or "POWER_POTION" or "COLORLESS_POTION"
            or "TOOLBOX" => PlanChoiceEffect.GenerateToHand,
        "SEEKER_STRIKE" or "KI_CHANT" or "LOCKED_IN" or "ARROW_STAB" or "HOLOGRAM" or "GRAVEBLAST"
            or "SECRET_WEAPON" or "SECRET_TECHNIQUE" or "WISH" or "DREDGE" or "NEOWS_FURY"
            or "DROPLET_OF_PRECOGNITION" or "FOREGONE_CONCLUSION_POWER" or "STRATAGEM_POWER"
            => PlanChoiceEffect.MoveToHand,
        "RECALL" or "HEADBUTT" or "COSMIC_INDIFFERENCE" or "THINKING_AHEAD" or "GLIMMER" or "PHOTON_CUT"
            => PlanChoiceEffect.MoveToDrawTop,
        "THE_HERMIT" or "SURVIVOR" or "ACROBATICS" or "DAGGER_THROW" or "PREPARED" or "HIDDEN_DAGGERS"
            or "TOOLS_OF_THE_TRADE_POWER" => PlanChoiceEffect.Discard,
        "GAMBLERS_BREW" or "GAMBLING_CHIP" => PlanChoiceEffect.DiscardAndDraw,
        "TRUE_GRIT" or "BURNING_PACT" or "SCAVENGE" or "PURITY" or "BRAND" or "CLEANSE"
            or "ASHWATER" or "TYRANNY_POWER" or "TOASTY_MITTENS" => PlanChoiceEffect.Exhaust,
        "ARMAMENTS" => PlanChoiceEffect.Upgrade,
        "BEGONE" or "CHARGE" or "GUARDS" or "SEANCE" or "ENTROPY_POWER" => PlanChoiceEffect.Transform,
        "DUAL_WIELD" or "HEIRLOOM_HAMMER" => PlanChoiceEffect.Duplicate,
        "TRANSFIGURE" => PlanChoiceEffect.Modify,
        "NIGHTMARE" => PlanChoiceEffect.Nightmare,
        "HAND_TRICK" => PlanChoiceEffect.ApplySly,
        "SCULPTING_STRIKE" => PlanChoiceEffect.ApplyEthereal,
        "SNAP" or "WELL_LAID_PLANS_POWER" => PlanChoiceEffect.ApplyRetain,
        "DECISIONS_DECISIONS" => PlanChoiceEffect.AutoPlayRepeated,
        "LIQUID_MEMORIES" => PlanChoiceEffect.MoveToHandFreeThisTurn,
        "TOUCH_OF_INSANITY" => PlanChoiceEffect.SetFreeThisCombat,
        _ => sourcePile switch
        {
            PileType.None => PlanChoiceEffect.GenerateToHand,
            PileType.Hand => PlanChoiceEffect.Modify,
            _ => PlanChoiceEffect.MoveToHand,
        },
    };

    private static PlanActionKind ParseActionKind(string kind)
        => kind switch
        {
            "PlayCard" => PlanActionKind.PlayCard,
            "UsePotion" => PlanActionKind.UsePotion,
            "EndTurn" => PlanActionKind.EndTurn,
            _ => throw new InvalidDataException($"Unknown Sts2Sim action kind '{kind}'."),
        };

    private static SearchBoundaryReason ParseSearchBoundary(string boundary)
        => boundary switch
        {
            "None" => SearchBoundaryReason.None,
            "Shuffle" => SearchBoundaryReason.Shuffle,
            "NoCards" => SearchBoundaryReason.NoCards,
            "UnsupportedEffect" => SearchBoundaryReason.UnsupportedEffect,
            "DynamicResolution" => SearchBoundaryReason.DynamicResolution,
            "PendingChoice" => SearchBoundaryReason.PendingChoice,
            "EventDefeat" => SearchBoundaryReason.EventDefeat,
            "TurnLimit" => SearchBoundaryReason.TurnLimit,
            "NodeLimit" => SearchBoundaryReason.NodeLimit,
            "TimeLimit" => SearchBoundaryReason.TimeLimit,
            "MemoryNoProgress" => SearchBoundaryReason.MemoryNoProgress,
            _ => throw new InvalidDataException($"Unknown Sts2Sim search boundary '{boundary}'."),
        };

    private static string ResolveTargetName(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        uint? targetCombatId)
    {
        if (targetCombatId is not uint combatId)
            return string.Empty;

        Creature? enemy = root.Enemies.FirstOrDefault(e => e.CombatId == combatId);
        if (enemy is not null)
            return displayNames.Creature(enemy, root.Enemies);

        if (root.PlayerIdentity.Creature.CombatId == combatId)
            return displayNames.Creature(root.PlayerIdentity.Creature);

        return string.Empty;
    }
}
