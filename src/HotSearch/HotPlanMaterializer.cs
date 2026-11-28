using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Potions;

namespace CombatSolver.HotSearch;

internal readonly record struct HotMaterializeMetrics(
    int ExpandedNodes,
    long TotalExpandedNodes,
    int TransitionCount,
    long TotalTransitionCount,
    int ChoiceBranchesEvaluated,
    long TotalChoiceBranchesEvaluated,
    int DuplicateCardBranchesPruned,
    int TranspositionBranchesPruned,
    int PrimaryIncumbentBranchesPruned,
    int PrimaryIncumbentUpdates,
    int StandPatProbes,
    int EndTurnPrefixCaptures,
    int EndTurnPrefixReuses,
    int ParallelExpansionWaves,
    int ParallelExpansionWorkItems,
    int MaxParallelExpansionConcurrency,
    int TranspositionCount,
    int TranspositionLimitBypasses,
    int StandPatCacheCount,
    int SearchedTurns,
    SearchBoundaryReason BoundaryReason,
    bool OnlyDeathRoutesFound,
    long WorkerAllocatedBytes,
    long TotalWorkerAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    TimeSpan GcPauseDuration,
    TimeSpan Elapsed,
    TimeSpan TotalSearchElapsed,
    int PotionHpSaved,
    int PotionHpRequired,
    int PotionBranchesRejected,
    bool WasLosslesslyExhausted = false);

/// <summary>
/// Replays ONLY the single winning <see cref="HotAction"/> trace from the root <see cref="CombatHotState"/>
/// to emit a 100% UI / Live-Deployment / OfflineSearchHarness-compatible <see cref="SolverResult"/>,
/// including exact <c>CardStateKey</c>, <c>CardStateOccurrence</c>, <c>TargetCombatId</c>,
/// <see cref="PlanCardChoice"/>/<see cref="PlanCardToken"/>, per-turn outcome tables, and
/// <see cref="CachedContinuation"/> stamps via <see cref="HotContinuationStamp.CaptureFromHot"/>.
/// </summary>
internal static class HotPlanMaterializer
{
    public static SolverResult Materialize(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        HotRootBundle rootBundle,
        in HotSearchPolicy hotPolicy,
        short[] rootPotionCosts,
        HotNodeArena nodeArena,
        int winningNodeId,
        in HotRouteKey winningKey,
        in HotMaterializeMetrics metrics)
    {
        int[] nodePath = nodeArena.ExtractActionNodePath(winningNodeId);
        HotAction[] actions = new HotAction[nodePath.Length];
        HotChoiceSelection[][] stepChoices = new HotChoiceSelection[nodePath.Length][];
        for (int i = 0; i < nodePath.Length; i++)
        {
            ref readonly HotSearchNode stepNode = ref nodeArena.GetRef(nodePath[i]);
            actions[i] = stepNode.Action;
            ReadOnlySpan<HotChoiceSelection> choices = nodeArena.GetNodeChoices(in stepNode);
            stepChoices[i] = choices.IsEmpty ? Array.Empty<HotChoiceSelection>() : choices.ToArray();
        }

        return MaterializeFromSteps(
            root,
            displayNames,
            battleDamage,
            policy,
            rootBundle,
            in hotPolicy,
            rootPotionCosts,
            actions,
            stepChoices,
            in winningKey,
            in metrics);
    }

    public static SolverResult MaterializeFromSteps(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        HotRootBundle rootBundle,
        in HotSearchPolicy hotPolicy,
        short[] rootPotionCosts,
        ReadOnlySpan<HotAction> actions,
        IReadOnlyList<HotChoiceSelection[]> stepChoices,
        in HotRouteKey winningKey,
        in HotMaterializeMetrics metrics)
    {
        HotRules rules = rootBundle.Rules;

        CombatHotState state = rootBundle.RootState;
        HotColdSidecar? cold = rootBundle.RootCold;
        HotExecScratch scratch = new();

        List<PlanAction> planActions = new(actions.Length);
        List<PredictedPotionUse> potionUses = [];
        List<CachedContinuation> continuations = [];

        Dictionary<int, int> hpLostByTurn = [];
        Dictionary<int, int> hpRecoveredByTurn = [];
        Dictionary<int, int> enemyHpLostByTurn = [];
        Dictionary<int, int> soldHpByTurn = [];
        Dictionary<int, int> maxBlockByTurn = [];
        Dictionary<int, int> actualBlockByTurn = [];
        Dictionary<int, int> energyLeftByTurn = [];
        Dictionary<int, int> potionCountByTurn = [];
        Dictionary<int, int> potionCostByTurn = [];
        Dictionary<int, IReadOnlyList<string>> killsAfterAction = [];

        Dictionary<uint, Creature> rootEnemiesByCombatId = new(root.Enemies.Count);
        for (int i = 0; i < root.Enemies.Count; i++)
        {
            if (root.Enemies[i].CombatId is uint cid)
                rootEnemiesByCombatId[cid] = root.Enemies[i];
        }

        int currentTurn = state.PlayerTurnNumber;
        int turnStartHpLost = state.Counters[(int)HotCounterSlot.PlayerCumulativeHpLost];
        int turnStartHpRecovered = state.Counters[(int)HotCounterSlot.PlayerRecoveredHp];
        int turnEnemyHpLostAccum = 0;
        Span<int> enemyHpBeforeStep = stackalloc int[HotLimits.MaxEnemies];
        int turnPeakBlock = state.Player.Block;
        int shufflesCrossed = 0;
        int lastShuffleEvents = state.ShuffleEventCount;
        int? combatEndedTurn = null;
        int? deathTurn = null;
        PlanCardChoice[] turnSetupChoices = Array.Empty<PlanCardChoice>();
        ContinuationStamp? turnSetupPlayState = null;
        bool hadTurnSetup = false;

        for (int stepIdx = 0; stepIdx < actions.Length; stepIdx++)
        {
            HotAction hotAction = actions[stepIdx];
            ReadOnlySpan<HotChoiceSelection> choices = stepIdx < stepChoices.Count
                ? stepChoices[stepIdx].AsSpan()
                : ReadOnlySpan<HotChoiceSelection>.Empty;

            // Match exact canonicalization + deterministic CardUid assignment performed by HotParallelExpander
            HotStatePacker.CanonicalizeForPack(ref state, ref cold);
            HotParallelExpander.AssignDeterministicCardUids(ref state);

            int actionTurn = state.PlayerTurnNumber;
            int blockBeforeAction = state.Player.Block;
            int energyBeforeAction = state.Energy;
            if (blockBeforeAction > turnPeakBlock)
                turnPeakBlock = blockBeforeAction;

            ulong aliveMaskBefore = CaptureAliveEnemySlotMask(in state);
            int enemyCountBefore = SnapshotAliveEnemyHp(in state, enemyHpBeforeStep);
            int[] potionSlotsBefore = SnapshotPotionSlots(in state);
            ushort generatedPotionMaskBefore = state.CombatGeneratedPotionMask;

            // Depth-0 TurnSetup edge: materialize TurnSetupChoices + post-setup Play-phase ContinuationStamp,
            // and do NOT emit a regular Turn-1 PlanAction.
            if (stepIdx == 0 && hotAction.Kind == HotActionKind.TurnSetup)
            {
                hadTurnSetup = true;
                if (choices.Length > 0)
                {
                    List<PlanCardChoice> setupMaterialized = MaterializeChoicesForAction(
                        in state,
                        cold,
                        rules,
                        displayNames,
                        hotAction,
                        choices,
                        timingOverride: PlanChoiceTiming.PlayerTurnStart);
                    turnSetupChoices = setupMaterialized.ToArray();
                }

                HotStepResult setupRes = HotCombatEngine.Step(ref state, ref cold, rules, scratch, hotAction, choices);
                if (!setupRes.Executed)
                    throw new InvalidOperationException($"HotPlanMaterializer TurnSetup replay failed at step {stepIdx} ({hotAction}).");

                turnEnemyHpLostAccum += AccumulateRealEnemyHpLoss(enemyHpBeforeStep, enemyCountBefore, in state);

                if (state.ShuffleEventCount != lastShuffleEvents)
                {
                    shufflesCrossed++;
                    lastShuffleEvents = state.ShuffleEventCount;
                }

                if (state.Player.Block > turnPeakBlock && state.PlayerTurnNumber == actionTurn)
                    turnPeakBlock = state.Player.Block;

                RecordAutomaticPotionUses(
                    potionSlotsBefore,
                    generatedPotionMaskBefore,
                    in state,
                    explicitSlot: -1,
                    rules,
                    rootPotionCosts,
                    in hotPolicy,
                    actionTurn,
                    potionUses,
                    potionCountByTurn,
                    potionCostByTurn);

                bool setupVictory = state.TerminalOutcome == HotCombatOutcome.Victory
                    && (state.Player.Flags & HotCreatureFlags.Alive) != 0
                    && state.Player.CurrentHp > 0;
                bool setupDefeat = state.TerminalOutcome == HotCombatOutcome.Defeat
                    || (state.Player.Flags & HotCreatureFlags.Alive) == 0
                    || state.Player.CurrentHp <= 0;
                bool setupEventDefeat = state.TerminalOutcome == HotCombatOutcome.EventDefeat && !setupDefeat;

                if (setupVictory && combatEndedTurn == null)
                    combatEndedTurn = actionTurn;
                if (setupDefeat && deathTurn == null)
                    deathTurn = actionTurn;

                // Capture Play-phase ContinuationStamp immediately after TurnSetup completes
                // (exact boundary checked by PlayerTurnSetupPatches.cs:829 and SolverController.ActivateTurnSetupResult).
                turnSetupPlayState = new ContinuationStamp(HotContinuationStamp.CaptureFromHot(in state, cold, rules));

                if (setupVictory || setupDefeat || setupEventDefeat || stepIdx == actions.Length - 1)
                {
                    int curHpLost = state.Counters[(int)HotCounterSlot.PlayerCumulativeHpLost];
                    int curHpRecovered = state.Counters[(int)HotCounterSlot.PlayerRecoveredHp];

                    hpLostByTurn[actionTurn] = Math.Max(0, curHpLost - turnStartHpLost);
                    hpRecoveredByTurn[actionTurn] = Math.Max(0, curHpRecovered - turnStartHpRecovered);
                    enemyHpLostByTurn[actionTurn] = turnEnemyHpLostAccum;
                    soldHpByTurn[actionTurn] = 0;
                    actualBlockByTurn[actionTurn] = state.Player.Block;
                    maxBlockByTurn[actionTurn] = Math.Max(turnPeakBlock, state.Player.Block);
                    energyLeftByTurn[actionTurn] = state.Energy;
                }

                continue;
            }

            // Annotate action metadata & choices BEFORE executing the action on the replay state
            PlanAction planAction = BuildPlanAction(
                in state,
                cold,
                rules,
                displayNames,
                rootEnemiesByCombatId,
                rootPotionCosts,
                in hotPolicy,
                hotAction,
                choices,
                actionTurn,
                out short explicitPotionCost);

            // Execute step on replay state
            HotStepResult res = HotCombatEngine.Step(ref state, ref cold, rules, scratch, hotAction, choices);
            if (!res.Executed)
                throw new InvalidOperationException($"HotPlanMaterializer replay failed at step {stepIdx} ({hotAction}).");

            turnEnemyHpLostAccum += AccumulateRealEnemyHpLoss(enemyHpBeforeStep, enemyCountBefore, in state);

            if (state.ShuffleEventCount != lastShuffleEvents)
            {
                shufflesCrossed++;
                lastShuffleEvents = state.ShuffleEventCount;
            }

            if (state.Player.Block > turnPeakBlock && state.PlayerTurnNumber == actionTurn)
                turnPeakBlock = state.Player.Block;

            // Record explicit or automatic potion uses
            if (hotAction.Kind == HotActionKind.UsePotion)
            {
                potionUses.Add(new PredictedPotionUse(
                    hotAction.SlotOrHandIdx,
                    planAction.PotionId,
                    explicitPotionCost,
                    Automatic: false));
                potionCountByTurn[actionTurn] = potionCountByTurn.GetValueOrDefault(actionTurn) + 1;
                potionCostByTurn[actionTurn] = potionCostByTurn.GetValueOrDefault(actionTurn) + explicitPotionCost;
            }

            RecordAutomaticPotionUses(
                potionSlotsBefore,
                generatedPotionMaskBefore,
                in state,
                hotAction.Kind == HotActionKind.UsePotion ? hotAction.SlotOrHandIdx : -1,
                rules,
                rootPotionCosts,
                in hotPolicy,
                actionTurn,
                potionUses,
                potionCountByTurn,
                potionCostByTurn);

            // Record enemy kills caused by this action (keyed by planActions index, NOT raw stepIdx!; exclude escaped enemies!)
            ulong aliveMaskAfter = CaptureAliveEnemySlotMask(in state);
            ulong newlyKilled = aliveMaskBefore & ~aliveMaskAfter;
            if ((planAction.IsExecutable || planAction.Kind == PlanActionKind.EndTurn) && newlyKilled != 0)
            {
                List<string> killedNames = [];
                for (int eSlot = 0; eSlot < state.EnemyCount; eSlot++)
                {
                    if ((newlyKilled & (1UL << eSlot)) == 0)
                        continue;
                    if ((state.Enemies[eSlot].Vital.Flags & HotCreatureFlags.Escaped) != 0)
                        continue;
                    killedNames.Add(ResolveEnemyDisplayName(in state, eSlot, rules, displayNames, rootEnemiesByCombatId));
                }
                if (killedNames.Count > 0)
                    killsAfterAction[planActions.Count] = killedNames;
            }

            bool endedByTurn = hotAction.Kind == HotActionKind.EndTurn || state.PlayerTurnNumber > actionTurn;
            if (endedByTurn && hotAction.Kind != HotActionKind.EndTurn)
                planAction = planAction with { EndsPlayerTurn = true };

            planActions.Add(planAction);

            bool isVictory = state.TerminalOutcome == HotCombatOutcome.Victory
                && (state.Player.Flags & HotCreatureFlags.Alive) != 0
                && state.Player.CurrentHp > 0;
            bool isDefeat = state.TerminalOutcome == HotCombatOutcome.Defeat
                || (state.Player.Flags & HotCreatureFlags.Alive) == 0
                || state.Player.CurrentHp <= 0;
            bool isEventDefeat = state.TerminalOutcome == HotCombatOutcome.EventDefeat && !isDefeat;

            if (isVictory && combatEndedTurn == null)
                combatEndedTurn = actionTurn;
            if (isDefeat && deathTurn == null)
                deathTurn = actionTurn;

            bool isFinalStep = stepIdx == actions.Length - 1;
            if (endedByTurn || isVictory || isDefeat || isEventDefeat || isFinalStep)
            {
                int curHpLost = state.Counters[(int)HotCounterSlot.PlayerCumulativeHpLost];
                int curHpRecovered = state.Counters[(int)HotCounterSlot.PlayerRecoveredHp];

                int turnHpLost = Math.Max(0, curHpLost - turnStartHpLost);
                int turnHpRecovered = Math.Max(0, curHpRecovered - turnStartHpRecovered);
                int turnEnemyHpLost = turnEnemyHpLostAccum;
                int actualBlock = endedByTurn ? blockBeforeAction : state.Player.Block;
                int energyLeft = endedByTurn ? energyBeforeAction : state.Energy;

                hpLostByTurn[actionTurn] = turnHpLost;
                hpRecoveredByTurn[actionTurn] = turnHpRecovered;
                enemyHpLostByTurn[actionTurn] = turnEnemyHpLost;
                soldHpByTurn[actionTurn] = 0;
                actualBlockByTurn[actionTurn] = actualBlock;
                maxBlockByTurn[actionTurn] = Math.Max(turnPeakBlock, actualBlock);
                energyLeftByTurn[actionTurn] = energyLeft;

                if (endedByTurn && !isVictory && !isDefeat && !isEventDefeat && !isFinalStep)
                {
                    currentTurn = state.PlayerTurnNumber;
                    turnStartHpLost = curHpLost;
                    turnStartHpRecovered = curHpRecovered;
                    turnEnemyHpLostAccum = 0;
                    turnPeakBlock = state.Player.Block;

                    // Capture exact cross-turn continuation stamp via HotContinuationStamp.CaptureFromHot!
                    string stampText = HotContinuationStamp.CaptureFromHot(in state, cold, rules);
                    continuations.Add(new CachedContinuation(
                        ExpectedState: new ContinuationStamp(stampText),
                        StartTurnNumber: currentTurn,
                        ForecastOffset: Math.Max(0, currentTurn - root.StartTurnNumber)));
                }
            }
        }

        // Ensure StartTurnNumber always has baseline outcome entries if the route was empty (0 actions).
        if (actions.Length == 0)
        {
            hpLostByTurn[root.StartTurnNumber] = 0;
            hpRecoveredByTurn[root.StartTurnNumber] = 0;
            enemyHpLostByTurn[root.StartTurnNumber] = 0;
            soldHpByTurn[root.StartTurnNumber] = 0;
            actualBlockByTurn[root.StartTurnNumber] = state.Player.Block;
            maxBlockByTurn[root.StartTurnNumber] = state.Player.Block;
            energyLeftByTurn[root.StartTurnNumber] = state.Energy;
        }

        HotFastSummary finalSummary = HotEvaluator.ExtractStage1(in state, in hotPolicy);
        bool finalWon = (planActions.Count > 0 || hadTurnSetup) && finalSummary.IsVictory;
        bool finalDead = finalSummary.IsDefeat;
        SearchBoundaryReason effectiveBoundary = finalSummary.IsEventDefeat
            ? SearchBoundaryReason.EventDefeat
            : metrics.BoundaryReason;
        int postCombatHeal = finalWon
            ? hotPolicy.PostCombatRelicHeal.HealFor(finalSummary.PlayerHp, finalSummary.PlayerMaxHp)
            : 0;

        GrowthValues realizedGrowth = policy.IgnoreLongTermRewards
            ? default
            : new(
                HandOfGreed: state.Growth[0],
                TheHunt: state.Growth[1],
                Feed: state.Growth[2],
                Royalties: state.Growth[3],
                Alchemize: state.Growth[4],
                GeneticAlgorithm: state.Growth[5],
                TheScythe: state.Growth[6],
                Goopy: state.Growth[7],
                ForbiddenGrimoire: state.Growth[8],
                MadScience: state.Growth[9],
                ShowOff: state.Growth[10]);

        int realizedLongTermResourceValue = policy.IgnoreLongTermRewards
            ? 0
            : state.LongTermResourceValue;
        RelicCounterEvaluation realizedRelicCounters = hotPolicy.HasRelicTargets
            ? hotPolicy.EvaluateRelicCounters(in state, won: finalWon)
            : default;

        SolverSnapshot snapshot = new(
            HasRisk: false,
            PlayerDead: finalDead,
            AllEnemiesDead: finalSummary.AliveEnemyCount == 0
                && finalSummary.RevivingEnemyCount == 0
                && !finalDead
                && !finalSummary.IsEventDefeat,
            PlayerHp: finalSummary.PlayerHp,
            PlayerMaxHp: finalSummary.PlayerMaxHp,
            CumulativePlayerHpLost: finalSummary.HpLost,
            RecoveredPlayerHp: finalSummary.HpRecovered,
            DeathSaveRelicHpRestored: state.DeathSaveRelicHpRestored,
            LongTermResourceValue: realizedLongTermResourceValue,
            AngerCopiesGenerated: state.AngerCopiesGenerated,
            ProjectedPlayerHp: finalSummary.PlayerHp,
            PlayerBlock: finalSummary.PlayerBlock,
            EnemyHp: finalSummary.EnemyHpSum,
            AliveEnemyCount: finalSummary.AliveEnemyCount,
            Energy: finalSummary.Energy,
            Stars: finalSummary.Stars,
            HandCount: state.HandCount,
            OutstandingStolenResource: finalSummary.OutstandingStolen,
            Turn: finalSummary.Turn,
            ShufflesCrossed: shufflesCrossed,
            BoundaryReason: effectiveBoundary,
            PredictionGaps: Array.Empty<PredictionGap>())
        {
            DeathSavePotionHpRestored = state.DeathSavePotionHpRestored,
            DeathSaveUseCount = finalSummary.DeathSaveUseCount,
            ProjectedDeathSaveUseCount = finalSummary.DeathSaveUseCount,
            GrowthHpCredit = finalSummary.GrowthHpCredit,
            GrowthRewards = realizedGrowth,
            RelicCounters = realizedRelicCounters,
            ForfeitsEncounterGold = finalSummary.ForfeitsGold,
            UnrecoveredGold = state.OutstandingStolenGold > 0 ? state.OutstandingStolenGold : null,
            UnrecoveredCards = state.OutstandingStolenCards > 0 ? state.OutstandingStolenCards : null,
        };

        SelectedSearchPlan selectedPlan = new(
            Actions: planActions.ToArray(),
            ActionCount: planActions.Count,
            Score: winningKey.Score);

        string[] plannedPotionIds = potionUses.Select(static u => u.PotionId).ToArray();
        int explicitPotionCount = potionUses.Count(static u => !u.Automatic);
        int searchedTurns = Math.Max(
            1,
            planActions.Count == 0
                ? 1
                : planActions.Max(static a => a.Turn) - root.StartTurnNumber + 1);

        SolverResult result = new()
        {
            ComparisonQuality = HotRouteQuality.ToInterimResult(in winningKey, in hotPolicy) with
            {
                CombatEndedTurn = combatEndedTurn,
            },
            ComparisonRootState = root.ContinuationStamp.StateText,
            TotalSearchElapsed = metrics.TotalSearchElapsed,
            TotalWorkerAllocatedBytes = metrics.TotalWorkerAllocatedBytes,
            TotalGen0Collections = metrics.Gen0Collections,
            TotalGen1Collections = metrics.Gen1Collections,
            TotalGen2Collections = metrics.Gen2Collections,
            TotalGcPauseDuration = metrics.GcPauseDuration,
            StartTurnNumber = root.StartTurnNumber,
            TurnSetupChoices = turnSetupChoices,
            TurnSetupPlayState = turnSetupPlayState,
            BestNode = selectedPlan,
            PotionUses = potionUses.ToArray(),
            Snapshot = snapshot,
            Forecast = root.Forecast,
            ExpandedNodes = metrics.ExpandedNodes,
            TotalExpandedNodes = metrics.TotalExpandedNodes,
            DominatedActionsPruned = 0,
            TopQueueActionsDropped = 0,
            ActionAdmissionRepresentativesProtected = 0,
            DuplicateCardBranchesPruned = metrics.DuplicateCardBranchesPruned,
            ChoiceBranchesEvaluated = metrics.ChoiceBranchesEvaluated,
            TotalChoiceBranchesEvaluated = metrics.TotalChoiceBranchesEvaluated,
            ShuffleBranchesPruned = 0,
            SoldHpBranchesPruned = 0,
            HpInvestmentBranchesProtected = 0,
            ReplayCount = 0,
            ForkCount = 0,
            RoundReplayPrefixCaptures = metrics.EndTurnPrefixCaptures,
            RoundReplayPrefixReuses = metrics.EndTurnPrefixReuses,
            TransitionCount = metrics.TransitionCount,
            TotalTransitionCount = metrics.TotalTransitionCount,
            ReusedNodeSnapshots = 0,
            TranspositionBranchesPruned = metrics.TranspositionBranchesPruned,
            RepeatableNoProgressBranchesPruned = 0,
            PrimaryIncumbentBranchesPruned = metrics.PrimaryIncumbentBranchesPruned,
            PrimaryIncumbentUpdates = metrics.PrimaryIncumbentUpdates,
            StandPatProbes = metrics.StandPatProbes,
            ParallelExpansionWaves = metrics.ParallelExpansionWaves,
            ParallelExpansionWorkItems = metrics.ParallelExpansionWorkItems,
            MaxParallelExpansionConcurrency = metrics.MaxParallelExpansionConcurrency,
            TransitionCacheHits = 0,
            TranspositionCount = metrics.TranspositionCount,
            TranspositionLimitBypasses = metrics.TranspositionLimitBypasses,
            StandPatCacheCount = metrics.StandPatCacheCount,
            WorkerAllocatedBytes = metrics.WorkerAllocatedBytes,
            Gen0Collections = metrics.Gen0Collections,
            Gen1Collections = metrics.Gen1Collections,
            Gen2Collections = metrics.Gen2Collections,
            GcPauseDuration = metrics.GcPauseDuration,
            WorkerYieldCount = 0,
            SearchedTurns = searchedTurns,
            BoundaryReason = effectiveBoundary,
            UnavoidableHpLost = Math.Max(0, battleDamage.HpLostSoFar - battleDamage.SoldHpCommitted) + finalSummary.HpLost,
            SoldHp = battleDamage.SoldHpCommitted,
            FutureSoldHp = 0,
            BattleHpLostSoFar = battleDamage.HpLostSoFar,
            BattleHpRecoveredOrGainedSoFar = battleDamage.HpRecoveredOrGainedSoFar,
            ProjectedBattleHpLost = battleDamage.HpLostSoFar + finalSummary.HpLost,
            BattlePotionsUsedSoFar = battleDamage.PotionsUsedSoFar,
            BattlePotionIdsUsedSoFar = battleDamage.PotionIdsUsedSoFar,
            PlannedPotionIds = plannedPotionIds,
            PotionRewardOutlook = root.PotionRewardOutlook,
            PotionCount = potionUses.Count,
            ExplicitPotionCount = explicitPotionCount,
            PotionHpSaved = metrics.PotionHpSaved,
            PotionHpRequired = metrics.PotionHpRequired,
            PotionBranchesRejected = metrics.PotionBranchesRejected,
            TheftPolicy = hotPolicy.TheftPolicy,
            OutstandingStolenResource = finalSummary.OutstandingStolen,
            SoldHpByTurn = soldHpByTurn,
            HpLostByTurn = hpLostByTurn,
            HpRecoveredByTurn = hpRecoveredByTurn,
            PostCombatRelicHeal = postCombatHeal,
            EnemyHpLostByTurn = enemyHpLostByTurn,
            MaxBlockByTurn = maxBlockByTurn,
            ActualBlockByTurn = actualBlockByTurn,
            EnergyLeftByTurn = energyLeftByTurn,
            PotionCountByTurn = potionCountByTurn,
            PotionStrategicCostByTurn = potionCostByTurn,
            KillsAfterAction = killsAfterAction,
            CombatEndedTurn = combatEndedTurn,
            DeathTurn = deathTurn,
            OnlyDeathRoutesFound = metrics.OnlyDeathRoutesFound,
            WasLosslesslyExhausted = metrics.WasLosslesslyExhausted,
            IsActEndingBoss = hotPolicy.IsActEndingBoss,
            BossHpRelief = hotPolicy.EncounterBossHpRelief,
            Elapsed = metrics.Elapsed,
            Continuations = continuations,
        };

        result.AssertCompleteTurnOutcomes();
        return result;
    }

    private static PlanAction BuildPlanAction(
        in CombatHotState stateBefore,
        HotColdSidecar? coldBefore,
        HotRules rules,
        SolverDisplayNames displayNames,
        Dictionary<uint, Creature> rootEnemiesByCombatId,
        short[] rootPotionCosts,
        in HotSearchPolicy hotPolicy,
        HotAction action,
        ReadOnlySpan<HotChoiceSelection> choices,
        int turn,
        out short explicitPotionCost)
    {
        explicitPotionCost = 0;

        int targetIndex = -1;
        uint? targetCombatId = null;
        string targetName = string.Empty;

        if (action.TargetEnemySlot >= 0 && action.TargetEnemySlot < stateBefore.EnemyCount)
        {
            byte eSlot = (byte)action.TargetEnemySlot;
            targetIndex = FindRosterIndex(in stateBefore, eSlot);
            targetCombatId = stateBefore.Enemies[eSlot].Vital.CombatId;
            targetName = ResolveEnemyDisplayName(in stateBefore, eSlot, rules, displayNames, rootEnemiesByCombatId);
        }

        // Materialize choices by probing paused states if this action carries selections
        PlanCardChoice? primaryChoice = null;
        List<PlanCardChoice>? nestedChoices = null;
        List<PlanCardChoice>? turnStartChoices = null;

        if (choices.Length > 0)
        {
            List<PlanCardChoice> materializedChoices = MaterializeChoicesForAction(
                in stateBefore,
                coldBefore,
                rules,
                displayNames,
                action,
                choices,
                timingOverride: null);

            if (action.Kind == HotActionKind.EndTurn)
            {
                turnStartChoices = materializedChoices;
            }
            else
            {
                for (int i = 0; i < materializedChoices.Count; i++)
                {
                    PlanCardChoice choice = materializedChoices[i];
                    if (choice.Timing != PlanChoiceTiming.Action)
                    {
                        turnStartChoices ??= [];
                        turnStartChoices.Add(choice);
                    }
                    else if (primaryChoice == null)
                    {
                        primaryChoice = choice;
                    }
                    else
                    {
                        nestedChoices ??= [];
                        nestedChoices.Add(choice);
                    }
                }
            }
        }

        switch (action.Kind)
        {
            case HotActionKind.EndTurn:
                return new PlanAction(
                    Kind: PlanActionKind.EndTurn,
                    Turn: turn,
                    TurnStartChoices: turnStartChoices?.ToArray(),
                    EndsPlayerTurn: true);

            case HotActionKind.UsePotion:
            {
                int slot = action.SlotOrHandIdx;
                ushort pDefId = slot >= 0 && slot < stateBefore.PotionSlotCount ? stateBefore.Potions[slot] : (ushort)0;
                string potionId = pDefId > 0 && pDefId < rules.Potions.Length ? rules.Potions[pDefId].Entry : string.Empty;
                string potionTitle = string.IsNullOrEmpty(potionId) ? string.Empty : displayNames.Potion(potionId);
                explicitPotionCost = ResolvePotionSlotCost(
                    slot,
                    pDefId,
                    stateBefore.CombatGeneratedPotionMask,
                    rootPotionCosts,
                    rules,
                    in hotPolicy);

                return new PlanAction(
                    Kind: PlanActionKind.UsePotion,
                    Turn: turn,
                    TargetIndex: targetIndex,
                    TargetCombatId: targetCombatId,
                    TargetName: targetName,
                    Choice: primaryChoice,
                    NestedChoices: nestedChoices?.ToArray(),
                    PotionSlot: slot,
                    PotionId: potionId,
                    PotionTitle: potionTitle,
                    TurnStartChoices: turnStartChoices?.ToArray());
            }

            case HotActionKind.PlayCard:
            default:
            {
                int handIdx = action.SlotOrHandIdx;
                ref readonly HotCard card = ref stateBefore.Cards[handIdx];
                HotCardDef cardDef = rules.Cards[card.CardDefId];
                string cardId = cardDef.Entry;
                string stateKey = HotContinuationStamp.FormatChoiceCardKey(in card, coldBefore, rules);

                int cardOccurrence = 0;
                int stateOccurrence = 0;
                for (int i = 0; i < handIdx; i++)
                {
                    ref readonly HotCard prior = ref stateBefore.Cards[i];
                    if (string.Equals(rules.Cards[prior.CardDefId].Entry, cardId, StringComparison.Ordinal))
                        cardOccurrence++;
                    if (string.Equals(HotContinuationStamp.FormatChoiceCardKey(in prior, coldBefore, rules), stateKey, StringComparison.Ordinal))
                        stateOccurrence++;
                }

                string enchId = card.EnchId > 0 && card.EnchId < rules.Enchantments.Length
                    ? rules.Enchantments[card.EnchId].Entry
                    : string.Empty;

                return new PlanAction(
                    Kind: PlanActionKind.PlayCard,
                    Turn: turn,
                    CardId: cardId,
                    CardOccurrence: cardOccurrence,
                    TargetIndex: targetIndex,
                    TargetCombatId: targetCombatId,
                    CardTitle: displayNames.Card(cardId, card.UpgradeLevel),
                    TargetName: targetName,
                    Choice: primaryChoice,
                    NestedChoices: nestedChoices?.ToArray(),
                    TurnStartChoices: turnStartChoices?.ToArray(),
                    ReplayCount: card.BaseReplayCount + card.ExtraReplayThisTurn,
                    CardStateKey: stateKey,
                    CardStateOccurrence: stateOccurrence,
                    CardUpgradeLevel: card.UpgradeLevel,
                    CardEnchantmentId: enchId);
            }
        }
    }

    private readonly record struct ChoicePromptMeta(
        string SourceId,
        PlanChoiceEffect Effect,
        int MaxSelect,
        bool IsOptional,
        bool IsTouchOfInsanity,
        bool IsChoicesParadox,
        int KiChantTopLimit,
        bool ConsumesTurnStartHandQueue);

    private sealed class OpenMultiSelectPrompt
    {
        public int ResultIndex;
        public HotChoiceKind Kind;
        public HotPileKind Pile;
        public PlanChoiceTiming Timing;
        public ushort SourceCardDefId;
        public string SourceId = string.Empty;
        public PlanChoiceEffect Effect;
        public int MaxSelect;
        public bool IsOptional;
        public bool IsTouchOfInsanity;
        public int KiChantTopLimit;
        public CombatHotState StartState;
        public HotColdSidecar? StartCold;
        public ushort[]? AllowedUids;
        public int StartCandidateCount;
        public readonly List<ushort> PickedUids = new(4);
        public readonly List<int> PickedOptionIndices = new(4);
        public readonly List<PlanCardToken> PickedTokens = new(4);

        public bool ContainsUid(ushort uid)
        {
            for (int i = 0; i < PickedUids.Count; i++)
            {
                if (PickedUids[i] == uid)
                    return true;
            }
            return false;
        }
    }

    private static List<PlanCardChoice> MaterializeChoicesForAction(
        in CombatHotState stateBefore,
        HotColdSidecar? coldBefore,
        HotRules rules,
        SolverDisplayNames displayNames,
        HotAction action,
        ReadOnlySpan<HotChoiceSelection> choices,
        PlanChoiceTiming? timingOverride)
    {
        List<PlanCardChoice> result = new(choices.Length);
        HotExecScratch probeScratch = new();
        OpenMultiSelectPrompt? openPrompt = null;
        int turnStartHandQueueTurn = -1;
        int turnStartHandQueueCursor = 0;
        bool seenTurn1Toolbox = false;

        for (int cIdx = 0; cIdx < choices.Length; cIdx++)
        {
            CombatHotState probeState = stateBefore;
            HotColdSidecar? probeCold = coldBefore;
            _ = HotCombatEngine.Step(ref probeState, ref probeCold, rules, probeScratch, action, choices[..cIdx]);

            HotChoiceSelection sel = choices[cIdx];
            HotChoiceKind pendingKind = probeScratch.NeedsChoice ? probeScratch.PendingChoiceKind : sel.Kind;
            ushort sourceCardDefId = probeScratch.PendingChoiceSourceCardDefId;
            PlanChoiceTiming timing = ResolveChoiceTiming(in stateBefore, in probeState, action, timingOverride);

            if (timing == PlanChoiceTiming.PlayerTurnStart && probeState.PlayerTurnNumber != turnStartHandQueueTurn)
            {
                turnStartHandQueueTurn = probeState.PlayerTurnNumber;
                turnStartHandQueueCursor = 0;
            }

            // Optional multi-select loop terminator (HotChoiceKind.None after >= 1 picks):
            // finalize & close the active prompt without emitting a duplicate empty PlanCardChoice!
            if (sel.Kind == HotChoiceKind.None)
            {
                if (openPrompt != null
                    && openPrompt.IsOptional
                    && openPrompt.Kind == pendingKind
                    && openPrompt.SourceCardDefId == sourceCardDefId
                    && openPrompt.Timing == timing)
                {
                    FinalizeOpenPrompt(openPrompt, result);
                    openPrompt = null;
                    continue;
                }

                if (openPrompt != null)
                {
                    FinalizeOpenPrompt(openPrompt, result);
                    openPrompt = null;
                }

                ChoicePromptMeta emptyMeta = ResolvePromptMetadata(
                    in stateBefore,
                    in probeState,
                    probeCold,
                    rules,
                    probeScratch,
                    action,
                    pendingKind,
                    sourceCardDefId,
                    timing,
                    turnStartHandQueueCursor,
                    ref seenTurn1Toolbox);
                if (emptyMeta.ConsumesTurnStartHandQueue)
                    turnStartHandQueueCursor++;

                result.Add(new PlanCardChoice(
                    emptyMeta.Effect,
                    MapPileType(pendingKind),
                    Array.Empty<PlanCardToken>(),
                    emptyMeta.SourceId,
                    string.Empty,
                    timing));
                continue;
            }

            // Check if this pick continues an open multi-select prompt (e.g. GamblingChip, GamblersBrew,
            // Ashwater, Purity, NeowsFury, Guards, Prepared+, HiddenDaggers, Dredge,
            // Charge, Seance, PhotonCut, ToolsOfTheTrade, Tyranny, Entropy, ForegoneConclusion, Stratagem).
            if (openPrompt != null
                && pendingKind != HotChoiceKind.GeneratedCard
                && openPrompt.Kind == pendingKind
                && openPrompt.SourceCardDefId == sourceCardDefId
                && openPrompt.Timing == timing
                && openPrompt.PickedUids.Count < openPrompt.MaxSelect
                && sel.CardUidOrIndex != 0
                && !openPrompt.ContainsUid(sel.CardUidOrIndex)
                && HotCombatEngine.FindCardIndexByUid(in openPrompt.StartState, openPrompt.Pile, sel.CardUidOrIndex) >= 0)
            {
                // Resolve SourceOccurrence & OptionOccurrence against StartState (the initial pre-selection screen!)
                ReadOnlySpan<ushort> contAllowedUids = openPrompt.AllowedUids != null
                    ? openPrompt.AllowedUids.AsSpan()
                    : ReadOnlySpan<ushort>.Empty;
                PlanCardToken contToken = BuildPlanCardToken(
                    in stateBefore,
                    in openPrompt.StartState,
                    openPrompt.StartCold,
                    rules,
                    displayNames,
                    action,
                    sel,
                    openPrompt.Kind,
                    openPrompt.SourceCardDefId,
                    openPrompt.IsTouchOfInsanity,
                    isChoicesParadox: false,
                    openPrompt.KiChantTopLimit,
                    contAllowedUids,
                    out int contOptionIndex,
                    out _);

                openPrompt.PickedUids.Add(sel.CardUidOrIndex);
                openPrompt.PickedOptionIndices.Add(contOptionIndex);
                openPrompt.PickedTokens.Add(contToken);

                if (openPrompt.PickedUids.Count >= openPrompt.MaxSelect)
                {
                    FinalizeOpenPrompt(openPrompt, result);
                    openPrompt = null;
                }
                continue;
            }

            // Starting a new prompt: finalize any previously open prompt first.
            if (openPrompt != null)
            {
                FinalizeOpenPrompt(openPrompt, result);
                openPrompt = null;
            }

            ChoicePromptMeta meta = ResolvePromptMetadata(
                in stateBefore,
                in probeState,
                probeCold,
                rules,
                probeScratch,
                action,
                pendingKind,
                sourceCardDefId,
                timing,
                turnStartHandQueueCursor,
                ref seenTurn1Toolbox);
            if (meta.ConsumesTurnStartHandQueue)
                turnStartHandQueueCursor++;

            ReadOnlySpan<ushort> pendingAllowedUids = pendingKind != HotChoiceKind.GeneratedCard
                ? HotCombatEngine.GetPendingFilteredChoiceUids()
                : ReadOnlySpan<ushort>.Empty;

            PlanCardToken token = BuildPlanCardToken(
                in stateBefore,
                in probeState,
                probeCold,
                rules,
                displayNames,
                action,
                sel,
                pendingKind,
                sourceCardDefId,
                meta.IsTouchOfInsanity,
                meta.IsChoicesParadox,
                meta.KiChantTopLimit,
                pendingAllowedUids,
                out int optionIndex,
                out int totalCandidates);

            int resultIdx = result.Count;
            result.Add(new PlanCardChoice(
                meta.Effect,
                MapPileType(pendingKind),
                [token],
                meta.SourceId,
                string.Empty,
                timing));

            int effectiveMax = totalCandidates > 0 ? Math.Min(meta.MaxSelect, totalCandidates) : meta.MaxSelect;
            if (pendingKind != HotChoiceKind.GeneratedCard && (effectiveMax > 1 || (meta.IsOptional && meta.MaxSelect > 1)))
            {
                OpenMultiSelectPrompt nextPrompt = new()
                {
                    ResultIndex = resultIdx,
                    Kind = pendingKind,
                    Pile = MapHotPileKind(pendingKind),
                    Timing = timing,
                    SourceCardDefId = sourceCardDefId,
                    SourceId = meta.SourceId,
                    Effect = meta.Effect,
                    MaxSelect = Math.Max(1, effectiveMax),
                    IsOptional = meta.IsOptional,
                    IsTouchOfInsanity = meta.IsTouchOfInsanity,
                    KiChantTopLimit = meta.KiChantTopLimit,
                    StartState = probeState,
                    StartCold = probeCold,
                    AllowedUids = pendingAllowedUids.IsEmpty ? null : pendingAllowedUids.ToArray(),
                    StartCandidateCount = totalCandidates,
                };
                nextPrompt.PickedUids.Add(sel.CardUidOrIndex);
                nextPrompt.PickedOptionIndices.Add(optionIndex);
                nextPrompt.PickedTokens.Add(token);

                if (nextPrompt.PickedUids.Count >= meta.MaxSelect
                    || (!meta.IsOptional && nextPrompt.PickedUids.Count >= nextPrompt.MaxSelect))
                {
                    FinalizeOpenPrompt(nextPrompt, result);
                }
                else
                {
                    openPrompt = nextPrompt;
                }
            }
        }

        if (openPrompt != null)
            FinalizeOpenPrompt(openPrompt, result);

        return result;
    }

    private static void FinalizeOpenPrompt(OpenMultiSelectPrompt prompt, List<PlanCardChoice> result)
    {
        int count = prompt.PickedTokens.Count;
        if (count == 0)
            return;

        PlanCardToken[] tokens = new PlanCardToken[count];
        for (int i = 0; i < count; i++)
            tokens[i] = prompt.PickedTokens[i];

        // Vanilla 0.113.1 CardSelectCmd implicit all-card selection (when MinSelect == MaxSelect
        // and eligible option count <= MinSelect) returns options in exact candidate order, which
        // NativeChoiceRuntime.ValidateImplicitSelection asserts strictly via SequenceEqual.
        if (!prompt.IsOptional && count > 1 && prompt.StartCandidateCount > 0 && count == prompt.StartCandidateCount)
        {
            int[] indices = new int[count];
            for (int i = 0; i < count; i++)
                indices[i] = prompt.PickedOptionIndices[i];

            for (int i = 1; i < count; i++)
            {
                int keyIdx = indices[i];
                PlanCardToken keyTok = tokens[i];
                int j = i - 1;
                while (j >= 0 && indices[j] > keyIdx)
                {
                    indices[j + 1] = indices[j];
                    tokens[j + 1] = tokens[j];
                    j--;
                }
                indices[j + 1] = keyIdx;
                tokens[j + 1] = keyTok;
            }
        }

        PlanCardChoice existing = result[prompt.ResultIndex];
        result[prompt.ResultIndex] = existing with { Cards = tokens };
    }

    private static PlanChoiceTiming ResolveChoiceTiming(
        in CombatHotState stateBefore,
        in CombatHotState probeState,
        HotAction action,
        PlanChoiceTiming? timingOverride)
    {
        if (timingOverride is PlanChoiceTiming forced)
            return forced;

        if (action.Kind == HotActionKind.TurnSetup)
            return PlanChoiceTiming.PlayerTurnStart;

        if (probeState.CurrentSide == HotCombatSide.Enemy)
            return PlanChoiceTiming.EnemyTurn;

        if (probeState.PlayerTurnNumber > stateBefore.PlayerTurnNumber)
            return PlanChoiceTiming.PlayerTurnStart;

        if (action.Kind == HotActionKind.EndTurn)
            return PlanChoiceTiming.PlayerTurnEnd;

        if (probeState.PlayerPhase == (byte)PlayerTurnPhase.End
            || probeState.PlayerPhase == (byte)PlayerTurnPhase.AutoPostPlay)
        {
            return PlanChoiceTiming.PlayerTurnEnd;
        }

        return PlanChoiceTiming.Action;
    }

    private static ChoicePromptMeta ResolvePromptMetadata(
        in CombatHotState stateBefore,
        in CombatHotState probeState,
        HotColdSidecar? probeCold,
        HotRules rules,
        HotExecScratch probeScratch,
        HotAction action,
        HotChoiceKind pendingKind,
        ushort sourceCardDefId,
        PlanChoiceTiming timing,
        int turnStartHandQueueCursor,
        ref bool seenTurn1Toolbox)
    {
        bool isOptional = probeScratch.NeedsChoice && probeScratch.PendingChoiceMin == 0;

        // 1. Card-sourced choice (sourceCardDefId > 0)
        if (sourceCardDefId > 0 && sourceCardDefId < rules.Cards.Length)
        {
            HotCardDef cardDef = rules.Cards[sourceCardDefId];
            HotCustomCardId cardId = cardDef.CardId;
            string sourceId = cardDef.Entry;
            PlanChoiceEffect effect = InferPlanChoiceEffect(sourceCardDefId, pendingKind, rules);

            int maxSelect = 1;
            int kiChantTopLimit = 0;

            switch (cardId)
            {
                case HotCustomCardId.KiChant:
                    maxSelect = 1;
                    kiChantTopLimit = probeScratch.PendingChoiceMax > 0 ? probeScratch.PendingChoiceMax : 3;
                    break;

                case HotCustomCardId.Guards:
                    maxSelect = Math.Max(1, probeScratch.PendingChoiceMax > 0
                        ? (int)probeScratch.PendingChoiceMax
                        : Math.Min((int)probeState.HandCount, HotLimits.MaxChoicesPerAction));
                    isOptional = true;
                    break;

                case HotCustomCardId.Purity:
                {
                    bool hasCard = TryFindSourceCard(in stateBefore, in probeState, action, sourceCardDefId, out HotCard played);
                    byte upg = hasCard ? played.UpgradeLevel : (byte)0;
                    int cardsVar = hasCard
                        ? GetCardVarInt(in played, cardDef, "Cards", upg > 0 ? 5 : 3)
                        : (upg > 0 ? 5 : 3);
                    maxSelect = Math.Max(1, Math.Min(cardsVar, (int)probeState.HandCount));
                    isOptional = true;
                    break;
                }

                case HotCustomCardId.NeowsFury:
                {
                    bool hasCard = TryFindSourceCard(in stateBefore, in probeState, action, sourceCardDefId, out HotCard played);
                    int cardsVar = hasCard ? GetCardVarInt(in played, cardDef, "Cards", 2) : 2;
                    int maxHand = probeState.MaxHandSize > 0 ? probeState.MaxHandSize : 10;
                    int handSpace = Math.Max(0, maxHand - probeState.HandCount);
                    maxSelect = Math.Max(1, Math.Min(Math.Min(cardsVar, handSpace), (int)probeState.DiscardCount));
                    isOptional = true;
                    break;
                }

                case HotCustomCardId.Prepared:
                {
                    bool hasCard = TryFindSourceCard(in stateBefore, in probeState, action, sourceCardDefId, out HotCard played);
                    int magic = hasCard ? cardDef.GetMagic(played.UpgradeLevel) : cardDef.BaseMagic;
                    int count = hasCard ? GetCardVarInt(in played, cardDef, "Cards", magic > 0 ? magic : 1) : (magic > 0 ? magic : 1);
                    maxSelect = Math.Max(1, Math.Min(count, (int)probeState.HandCount));
                    break;
                }

                case HotCustomCardId.HiddenDaggers:
                {
                    bool hasCard = TryFindSourceCard(in stateBefore, in probeState, action, sourceCardDefId, out HotCard played);
                    int count = hasCard ? GetCardVarInt(in played, cardDef, "Cards", 2) : 2;
                    maxSelect = Math.Max(1, Math.Min(count, (int)probeState.HandCount));
                    break;
                }

                case HotCustomCardId.PhotonCut:
                case HotCustomCardId.Glimmer:
                {
                    bool hasCard = TryFindSourceCard(in stateBefore, in probeState, action, sourceCardDefId, out HotCard played);
                    int putBack = hasCard ? GetCardVarInt(in played, cardDef, "PutBack", 1) : 1;
                    maxSelect = Math.Max(1, Math.Min(putBack, (int)probeState.HandCount));
                    break;
                }

                case HotCustomCardId.Charge:
                {
                    bool hasCard = TryFindSourceCard(in stateBefore, in probeState, action, sourceCardDefId, out HotCard played);
                    int fallback = Math.Max(1, hasCard ? cardDef.GetMagic(played.UpgradeLevel) : (int)cardDef.BaseMagic);
                    int count = hasCard ? GetCardVarInt(in played, cardDef, "Cards", fallback) : fallback;
                    maxSelect = Math.Max(1, Math.Min(count, (int)probeState.DrawCount));
                    break;
                }

                case HotCustomCardId.Dredge:
                {
                    bool hasCard = TryFindSourceCard(in stateBefore, in probeState, action, sourceCardDefId, out HotCard played);
                    int cardsVar = hasCard ? GetCardVarInt(in played, cardDef, "Cards", 3) : 3;
                    int maxHand = probeState.MaxHandSize > 0 ? probeState.MaxHandSize : 10;
                    int handSpace = Math.Max(0, maxHand - probeState.HandCount);
                    maxSelect = Math.Max(1, Math.Min(Math.Min(cardsVar, handSpace), (int)probeState.DiscardCount));
                    break;
                }

                case HotCustomCardId.Seance:
                {
                    bool hasCard = TryFindSourceCard(in stateBefore, in probeState, action, sourceCardDefId, out HotCard played);
                    int count = hasCard ? GetCardVarInt(in played, cardDef, "Cards", 1) : 1;
                    maxSelect = Math.Max(1, Math.Min(count, (int)probeState.DrawCount));
                    break;
                }

                default:
                    maxSelect = Math.Max(1, (int)probeScratch.PendingChoiceMax);
                    break;
            }

            return new ChoicePromptMeta(
                SourceId: sourceId,
                Effect: effect,
                MaxSelect: maxSelect,
                IsOptional: isOptional,
                IsTouchOfInsanity: false,
                IsChoicesParadox: false,
                KiChantTopLimit: kiChantTopLimit,
                ConsumesTurnStartHandQueue: false);
        }

        // 2. EnemyTurn choice (KnowledgeDemon curse choice)
        if (timing == PlanChoiceTiming.EnemyTurn && pendingKind == HotChoiceKind.GeneratedCard)
        {
            return new ChoicePromptMeta(
                SourceId: "KNOWLEDGE_DEMON",
                Effect: PlanChoiceEffect.ApplyKnowledgeCurse,
                MaxSelect: 1,
                IsOptional: false,
                IsTouchOfInsanity: false,
                IsChoicesParadox: false,
                KiChantTopLimit: 0,
                ConsumesTurnStartHandQueue: false);
        }

        // 3. Action-phase sourceCardDefId == 0: Potions (or StratagemPower triggered by mid-action shuffle!)
        if (timing == PlanChoiceTiming.Action && action.Kind == HotActionKind.UsePotion)
        {
            int slot = action.SlotOrHandIdx;
            ushort pDefId = slot >= 0 && slot < stateBefore.PotionSlotCount ? stateBefore.Potions[slot] : (ushort)0;
            if (pDefId > 0 && pDefId < rules.Potions.Length)
            {
                HotPotionDef pDef = rules.Potions[pDefId];
                HotCustomPotionId pid = HotPotionPolicy.ResolvePotionId(pDef);

                switch (pid)
                {
                    case HotCustomPotionId.Ashwater when pendingKind == HotChoiceKind.HandCard:
                        return new ChoicePromptMeta(
                            pDef.Entry,
                            PlanChoiceEffect.Exhaust,
                            Math.Max(1, Math.Min((int)probeState.HandCount, HotLimits.MaxChoicesPerAction)),
                            IsOptional: true,
                            IsTouchOfInsanity: false,
                            IsChoicesParadox: false,
                            KiChantTopLimit: 0,
                            ConsumesTurnStartHandQueue: false);

                    case HotCustomPotionId.GamblersBrew when pendingKind == HotChoiceKind.HandCard:
                        return new ChoicePromptMeta(
                            pDef.Entry,
                            PlanChoiceEffect.DiscardAndDraw,
                            Math.Max(1, Math.Min((int)probeState.HandCount, HotLimits.MaxChoicesPerAction)),
                            IsOptional: true,
                            IsTouchOfInsanity: false,
                            IsChoicesParadox: false,
                            KiChantTopLimit: 0,
                            ConsumesTurnStartHandQueue: false);

                    case HotCustomPotionId.TouchOfInsanity when pendingKind == HotChoiceKind.HandCard:
                        return new ChoicePromptMeta(
                            pDef.Entry,
                            PlanChoiceEffect.SetFreeThisCombat,
                            MaxSelect: 1,
                            IsOptional: false,
                            IsTouchOfInsanity: true,
                            IsChoicesParadox: false,
                            KiChantTopLimit: 0,
                            ConsumesTurnStartHandQueue: false);

                    case HotCustomPotionId.DropletOfPrecognition when pendingKind == HotChoiceKind.DrawPile:
                        return new ChoicePromptMeta(
                            pDef.Entry,
                            PlanChoiceEffect.MoveToHand,
                            MaxSelect: 1,
                            IsOptional: false,
                            IsTouchOfInsanity: false,
                            IsChoicesParadox: false,
                            KiChantTopLimit: 0,
                            ConsumesTurnStartHandQueue: false);

                    case HotCustomPotionId.LiquidMemories when pendingKind == HotChoiceKind.DiscardPile:
                        return new ChoicePromptMeta(
                            pDef.Entry,
                            PlanChoiceEffect.MoveToHandFreeThisTurn,
                            MaxSelect: 1,
                            IsOptional: false,
                            IsTouchOfInsanity: false,
                            IsChoicesParadox: false,
                            KiChantTopLimit: 0,
                            ConsumesTurnStartHandQueue: false);

                    case HotCustomPotionId.AttackPotion:
                    case HotCustomPotionId.SkillPotion:
                    case HotCustomPotionId.PowerPotion:
                    case HotCustomPotionId.ColorlessPotion:
                        if (pendingKind == HotChoiceKind.GeneratedCard)
                        {
                            return new ChoicePromptMeta(
                                pDef.Entry,
                                PlanChoiceEffect.GenerateToHand,
                                MaxSelect: 1,
                                IsOptional: true,
                                IsTouchOfInsanity: false,
                                IsChoicesParadox: false,
                                KiChantTopLimit: 0,
                                ConsumesTurnStartHandQueue: false);
                        }
                        break;
                }
            }
        }

        // 4. DrawPile choice with sourceCardDefId == 0: ForegoneConclusionPower (BeforeHandDraw) or StratagemPower (AfterShuffle)
        if (pendingKind == HotChoiceKind.DrawPile)
        {
            if (timing == PlanChoiceTiming.PlayerTurnStart)
            {
                int fcAmt = HotCombatEngine.GetPowerAmountById(in probeState, rules, 0, HotPowerId.ForegoneConclusionPower);
                if (fcAmt > 0)
                {
                    ushort fcDefId = rules.GetPowerDefId(HotPowerId.ForegoneConclusionPower);
                    string fcEntry = fcDefId > 0 && fcDefId < rules.Powers.Length ? rules.Powers[fcDefId].Entry : "FOREGONE_CONCLUSION_POWER";
                    return new ChoicePromptMeta(
                        fcEntry,
                        PlanChoiceEffect.MoveToHand,
                        Math.Max(1, Math.Min(fcAmt, (int)probeState.DrawCount)),
                        IsOptional: false,
                        IsTouchOfInsanity: false,
                        IsChoicesParadox: false,
                        KiChantTopLimit: 0,
                        ConsumesTurnStartHandQueue: false);
                }
            }

            int stratAmt = HotCombatEngine.GetPowerAmountById(in probeState, rules, 0, HotPowerId.StratagemPower);
            if (stratAmt > 0)
            {
                ushort stratDefId = rules.GetPowerDefId(HotPowerId.StratagemPower);
                string stratEntry = stratDefId > 0 && stratDefId < rules.Powers.Length ? rules.Powers[stratDefId].Entry : "STRATAGEM_POWER";
                return new ChoicePromptMeta(
                    stratEntry,
                    PlanChoiceEffect.MoveToHand,
                    Math.Max(1, Math.Min(stratAmt, (int)probeState.DrawCount)),
                    IsOptional: false,
                    IsTouchOfInsanity: false,
                    IsChoicesParadox: false,
                    KiChantTopLimit: 0,
                    ConsumesTurnStartHandQueue: false);
            }
        }

        // 5. PlayerTurnStart GeneratedCard with sourceCardDefId == 0:
        //    KnowledgeDemonDominion (BeforeHandDraw), Toolbox (BeforeHandDraw), ChoicesParadox (AfterSideTurnStart).
        if (pendingKind == HotChoiceKind.GeneratedCard && timing == PlanChoiceTiming.PlayerTurnStart)
        {
            ReadOnlySpan<ushort> opts = HotCombatEngine.GetPendingGeneratedChoiceOptions();

            if (rules.Dominion == HotDominionId.KnowledgeDemonDominion
                && probeState.PlayerTurnNumber <= 1
                && opts.Length > 0
                && (opts[0] == rules.GetCardDefId(HotCustomCardId.Sloth)
                    || opts[0] == rules.GetCardDefId(HotCustomCardId.Disintegration)))
            {
                return new ChoicePromptMeta(
                    rules.DominionId ?? "KNOWLEDGE_DEMON_DOMINION",
                    PlanChoiceEffect.ApplyKnowledgeCurse,
                    MaxSelect: 1,
                    IsOptional: false,
                    IsTouchOfInsanity: false,
                    IsChoicesParadox: false,
                    KiChantTopLimit: 0,
                    ConsumesTurnStartHandQueue: false);
            }

            bool hasToolbox = rules.HasRelic(HotRelicId.Toolbox) && probeState.PlayerTurnNumber <= 1;
            bool hasChoicesParadox = rules.HasRelic(HotRelicId.ChoicesParadox) && probeState.PlayerTurnNumber <= 1;

            if (hasToolbox && !seenTurn1Toolbox && (!hasChoicesParadox || (opts.Length <= 3 && AreAllColorlessOptions(opts, rules))))
            {
                seenTurn1Toolbox = true;
                ushort tbDefId = rules.GetRelicDefId(HotRelicId.Toolbox);
                string tbEntry = tbDefId > 0 && tbDefId < rules.Relics.Length ? rules.Relics[tbDefId].Entry : "TOOLBOX";
                return new ChoicePromptMeta(
                    tbEntry,
                    PlanChoiceEffect.GenerateToHand,
                    MaxSelect: 1,
                    IsOptional: false,
                    IsTouchOfInsanity: false,
                    IsChoicesParadox: false,
                    KiChantTopLimit: 0,
                    ConsumesTurnStartHandQueue: false);
            }

            if (hasChoicesParadox)
            {
                ushort cpDefId = rules.GetRelicDefId(HotRelicId.ChoicesParadox);
                string cpEntry = cpDefId > 0 && cpDefId < rules.Relics.Length ? rules.Relics[cpDefId].Entry : "CHOICES_PARADOX";
                return new ChoicePromptMeta(
                    cpEntry,
                    PlanChoiceEffect.GenerateToHand,
                    MaxSelect: 1,
                    IsOptional: false,
                    IsTouchOfInsanity: false,
                    IsChoicesParadox: true,
                    KiChantTopLimit: 0,
                    ConsumesTurnStartHandQueue: false);
            }
        }

        // 6. PlayerTurnStart HandCard with sourceCardDefId == 0:
        //    min=0 -> GamblingChip; min=1 -> [ToolsOfTheTrade/Tyranny/Entropy in s.Powers order, then ToastyMittens?].
        if (pendingKind == HotChoiceKind.HandCard && timing == PlanChoiceTiming.PlayerTurnStart)
        {
            if (isOptional)
            {
                ushort gcDefId = rules.GetRelicDefId(HotRelicId.GamblingChip);
                string gcEntry = gcDefId > 0 && gcDefId < rules.Relics.Length ? rules.Relics[gcDefId].Entry : "GAMBLING_CHIP";
                return new ChoicePromptMeta(
                    gcEntry,
                    PlanChoiceEffect.DiscardAndDraw,
                    Math.Max(1, Math.Min((int)probeState.HandCount, HotLimits.MaxChoicesPerAction)),
                    IsOptional: true,
                    IsTouchOfInsanity: false,
                    IsChoicesParadox: false,
                    KiChantTopLimit: 0,
                    ConsumesTurnStartHandQueue: false);
            }

            if (TryResolveTurnStartMandatoryHandPrompt(
                    in probeState,
                    rules,
                    turnStartHandQueueCursor,
                    out string turnStartSource,
                    out PlanChoiceEffect turnStartEffect,
                    out int turnStartMaxSelect))
            {
                return new ChoicePromptMeta(
                    turnStartSource,
                    turnStartEffect,
                    Math.Max(1, Math.Min(turnStartMaxSelect, (int)probeState.HandCount)),
                    IsOptional: false,
                    IsTouchOfInsanity: false,
                    IsChoicesParadox: false,
                    KiChantTopLimit: 0,
                    ConsumesTurnStartHandQueue: true);
            }
        }

        return new ChoicePromptMeta(
            SourceId: string.Empty,
            Effect: InferPlanChoiceEffect(sourceCardDefId, pendingKind, rules),
            MaxSelect: Math.Max(1, (int)probeScratch.PendingChoiceMax),
            IsOptional: isOptional,
            IsTouchOfInsanity: false,
            IsChoicesParadox: false,
            KiChantTopLimit: 0,
            ConsumesTurnStartHandQueue: false);
    }

    private static bool TryResolveTurnStartMandatoryHandPrompt(
        in CombatHotState probeState,
        HotRules rules,
        int cursor,
        out string sourceId,
        out PlanChoiceEffect effect,
        out int maxSelect)
    {
        int seen = 0;

        // DispatchHooks matches 0.113.1 CombatState.IterateHookListeners:
        // Player Powers fire BEFORE Player Relics on AfterPlayerTurnStart / AfterSideTurnStart.
        int powerCount = probeState.PowerCount;
        for (int i = 0; i < powerCount; i++)
        {
            ref readonly HotPower p = ref probeState.Powers[i];
            if (p.OwnerSlot != 0 || p.Amount <= 0 || p.PowerDefId >= rules.Powers.Length)
                continue;

            HotPowerDef pDef = rules.Powers[p.PowerDefId];
            PlanChoiceEffect? matched = pDef.PowerId switch
            {
                HotPowerId.ToolsOfTheTradePower => PlanChoiceEffect.Discard,
                HotPowerId.TyrannyPower => PlanChoiceEffect.Exhaust,
                HotPowerId.EntropyPower => PlanChoiceEffect.Transform,
                _ => null,
            };
            if (matched == null)
                continue;

            if (seen == cursor)
            {
                sourceId = pDef.Entry;
                effect = matched.Value;
                maxSelect = p.Amount;
                return true;
            }
            seen++;
        }

        if (rules.HasRelic(HotRelicId.ToastyMittens))
        {
            if (seen == cursor)
            {
                ushort defId = rules.GetRelicDefId(HotRelicId.ToastyMittens);
                sourceId = defId > 0 && defId < rules.Relics.Length ? rules.Relics[defId].Entry : "TOASTY_MITTENS";
                effect = PlanChoiceEffect.Exhaust;
                maxSelect = 1;
                return true;
            }
            seen++;
        }

        sourceId = string.Empty;
        effect = PlanChoiceEffect.Discard;
        maxSelect = 1;
        return false;
    }

    private static bool AreAllColorlessOptions(ReadOnlySpan<ushort> options, HotRules rules)
    {
        if (options.IsEmpty)
            return false;
        for (int i = 0; i < options.Length; i++)
        {
            ushort id = options[i];
            if (id == 0 || id >= rules.Cards.Length || rules.Cards[id].Domain != HotCardDomain.Colorless)
                return false;
        }
        return true;
    }

    private static bool TryFindSourceCard(
        in CombatHotState stateBefore,
        in CombatHotState pausedState,
        HotAction action,
        ushort sourceCardDefId,
        out HotCard found)
    {
        // First check the currently executing card in PlayPile (covers replays, AutoPlay, DistilledChaos, Sly!)
        int playStart = pausedState.PlayStart;
        int playEnd = playStart + pausedState.PlayCount;
        for (int i = playEnd - 1; i >= playStart; i--)
        {
            if (pausedState.Cards[i].CardDefId == sourceCardDefId)
            {
                found = pausedState.Cards[i];
                return true;
            }
        }

        if (action.Kind == HotActionKind.PlayCard
            && (uint)action.SlotOrHandIdx < (uint)stateBefore.HandCount
            && stateBefore.Cards[action.SlotOrHandIdx].CardDefId == sourceCardDefId)
        {
            found = stateBefore.Cards[action.SlotOrHandIdx];
            return true;
        }

        found = default;
        return false;
    }

    private static int GetCardVarInt(in HotCard card, HotCardDef def, string varName, int fallback)
    {
        string[] names = def.OrderedVarNames;
        for (int i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], varName, StringComparison.Ordinal))
                return (int)HotRules.GetCardVarValue(in card, def, i);
        }
        return fallback;
    }

    private static PlanCardToken BuildPlanCardToken(
        in CombatHotState stateBefore,
        in CombatHotState pausedState,
        HotColdSidecar? pausedCold,
        HotRules rules,
        SolverDisplayNames displayNames,
        HotAction action,
        HotChoiceSelection sel,
        HotChoiceKind pendingKind,
        ushort sourceCardDefId,
        bool isTouchOfInsanity,
        bool isChoicesParadox,
        int kiChantTopLimit,
        ReadOnlySpan<ushort> allowedUids,
        out int optionIndex,
        out int totalCandidateCount)
    {
        optionIndex = -1;
        totalCandidateCount = 0;

        // Fix 3: GeneratedCard tokens compute real UpgradeLevel (Abundance, Quasar+, Splash+),
        // real StateKey (including CostsX, HasStarCostX, IsSlyThisTurn, ShowOff, Disintegration,
        // and ChoicesParadox Retain), real OptionOccurrence, and real Title.
        if (sel.Kind == HotChoiceKind.GeneratedCard || pendingKind == HotChoiceKind.GeneratedCard)
        {
            ReadOnlySpan<ushort> generatedOptions = HotCombatEngine.GetPendingGeneratedChoiceOptions();
            totalCandidateCount = generatedOptions.Length;

            ushort chosenDefId = sel.CardDefId;
            if ((chosenDefId == 0 || chosenDefId >= rules.Cards.Length)
                && sel.CardUidOrIndex < generatedOptions.Length)
            {
                chosenDefId = generatedOptions[sel.CardUidOrIndex];
            }

            if (chosenDefId == 0 || chosenDefId >= rules.Cards.Length)
                return new PlanCardToken(string.Empty, 0, string.Empty, sel.CardUidOrIndex, 0, string.Empty);

            HotCardDef genDef = rules.Cards[chosenDefId];
            string genId = genDef.Entry;

            bool upgradeGeneratedOptions = ShouldUpgradeGeneratedChoiceOptions(
                in stateBefore,
                in pausedState,
                rules,
                action,
                sourceCardDefId);

            int upgradeLevel = upgradeGeneratedOptions && genDef.MaxUpgradeLevel > 0 ? 1 : 0;
            HotCard generatedCard = BuildSyntheticGeneratedChoiceCard(chosenDefId, genDef, (byte)upgradeLevel, isChoicesParadox);
            string genStateKey = HotContinuationStamp.FormatChoiceCardKey(in generatedCard, cold: null, rules);
            if (genDef.CardId == HotCustomCardId.Disintegration
                && pausedState.CurrentSide == HotCombatSide.Enemy
                && TryResolveKnowledgeDemonDisintegrationDamage(in pausedState, rules, out int disintDmg)
                && disintDmg != 6)
            {
                genStateKey = genStateKey.Replace(
                    "|vars=DisintegrationPower=6|",
                    $"|vars=DisintegrationPower={disintDmg}|",
                    StringComparison.Ordinal);
            }

            int resolvedOptIdx = sel.CardUidOrIndex;
            if ((uint)resolvedOptIdx >= (uint)generatedOptions.Length || generatedOptions[resolvedOptIdx] != chosenDefId)
            {
                resolvedOptIdx = -1;
                for (int i = 0; i < generatedOptions.Length; i++)
                {
                    if (generatedOptions[i] == chosenDefId)
                    {
                        resolvedOptIdx = i;
                        break;
                    }
                }
            }

            int genOptionOccurrence = 0;
            if (resolvedOptIdx > 0)
            {
                for (int i = 0; i < resolvedOptIdx; i++)
                {
                    ushort priorDefId = generatedOptions[i];
                    if (priorDefId == 0 || priorDefId >= rules.Cards.Length)
                        continue;
                    HotCardDef priorDef = rules.Cards[priorDefId];
                    int priorUpgrade = upgradeGeneratedOptions && priorDef.MaxUpgradeLevel > 0 ? 1 : 0;
                    if (priorUpgrade == upgradeLevel && string.Equals(priorDef.Entry, genId, StringComparison.Ordinal))
                        genOptionOccurrence++;
                }
            }

            optionIndex = resolvedOptIdx;
            return new PlanCardToken(
                CardId: genId,
                UpgradeLevel: upgradeLevel,
                StateKey: genStateKey,
                SourceOccurrence: resolvedOptIdx >= 0 ? resolvedOptIdx : sel.CardUidOrIndex,
                OptionOccurrence: genOptionOccurrence,
                Title: displayNames.Card(genId, upgradeLevel));
        }

        // Fix 2: Pile choices compute SourceOccurrence in the raw pile, and OptionOccurrence in the
        // exact filtered (and for PileType.Draw, (Rarity, CardModel.CompareTo)-sorted) option list
        // matching STS2 0.113.1 CardSelectCmd + NativeChoiceRuntime.ResolvePlannedCards!
        HotPileKind pile = MapHotPileKind(pendingKind);
        int zoneStart = HotPiles.ZoneStart(in pausedState, pile);
        int zoneCount = HotPiles.ZoneCount(in pausedState, pile);
        HotCustomCardId sourceCardId = sourceCardDefId > 0 && sourceCardDefId < rules.Cards.Length
            ? rules.Cards[sourceCardDefId].CardId
            : HotCustomCardId.None;
        int foundIdx = HotCombatEngine.FindCardIndexByUid(in pausedState, pile, sel.CardUidOrIndex);

        if (foundIdx < 0)
        {
            bool hasValidDefId = sel.CardDefId > 0 && sel.CardDefId < rules.Cards.Length;

            // (a) Probe target pile by CardDefId (filtered first, then full pile) to recover foundIdx
            // and run the normal occurrence/candidate path.
            if (hasValidDefId)
            {
                int probeLimit = sourceCardId == HotCustomCardId.KiChant && pile == HotPileKind.Draw && kiChantTopLimit > 0
                    ? Math.Min(zoneCount, kiChantTopLimit)
                    : zoneCount;
                for (int offset = 0; offset < probeLimit; offset++)
                {
                    int idx = zoneStart + offset;
                    ref readonly HotCard candidate = ref pausedState.Cards[idx];
                    if (candidate.CardDefId != sel.CardDefId)
                        continue;
                    if (!allowedUids.IsEmpty && !ContainsUid(allowedUids, candidate.CardUid))
                        continue;
                    if (!MatchesLiveChoiceFilter(in pausedState, in candidate, pausedCold, rules, sourceCardId, isTouchOfInsanity))
                        continue;
                    foundIdx = idx;
                    break;
                }

                if (foundIdx < 0)
                {
                    for (int offset = 0; offset < zoneCount; offset++)
                    {
                        int idx = zoneStart + offset;
                        if (pausedState.Cards[idx].CardDefId == sel.CardDefId)
                        {
                            foundIdx = idx;
                            break;
                        }
                    }
                }
            }

            // (b) If still not found in the target pile, scan all zones of pausedState, then stateBefore,
            // by CardUid to recover real UpgradeLevel & FormatChoiceCardKey.
            if (foundIdx < 0 && sel.CardUidOrIndex != 0)
            {
                int pausedTotal = pausedState.TotalCardCount;
                for (int i = 0; i < pausedTotal; i++)
                {
                    ref readonly HotCard crossCard = ref pausedState.Cards[i];
                    if (crossCard.CardUid == sel.CardUidOrIndex
                        && crossCard.CardDefId > 0 && crossCard.CardDefId < rules.Cards.Length)
                    {
                        string crossId = rules.Cards[crossCard.CardDefId].Entry;
                        string crossKey = HotContinuationStamp.FormatChoiceCardKey(in crossCard, pausedCold, rules);
                        return new PlanCardToken(
                            crossId,
                            crossCard.UpgradeLevel,
                            crossKey,
                            0,
                            0,
                            displayNames.Card(crossId, crossCard.UpgradeLevel));
                    }
                }

                int beforeTotal = stateBefore.TotalCardCount;
                for (int i = 0; i < beforeTotal; i++)
                {
                    ref readonly HotCard crossCard = ref stateBefore.Cards[i];
                    if (crossCard.CardUid == sel.CardUidOrIndex
                        && crossCard.CardDefId > 0 && crossCard.CardDefId < rules.Cards.Length)
                    {
                        string crossId = rules.Cards[crossCard.CardDefId].Entry;
                        string crossKey = HotContinuationStamp.FormatChoiceCardKey(in crossCard, pausedCold, rules);
                        return new PlanCardToken(
                            crossId,
                            crossCard.UpgradeLevel,
                            crossKey,
                            0,
                            0,
                            displayNames.Card(crossId, crossCard.UpgradeLevel));
                    }
                }
            }

            // (c) Last resort: synthesize a canonical HotCard token + FormatChoiceCardKey instead of bare fallbackId.
            if (foundIdx < 0)
            {
                if (hasValidDefId)
                {
                    HotCardDef fallbackDef = rules.Cards[sel.CardDefId];
                    string fallbackId = fallbackDef.Entry;
                    HotCard syntheticFallback = BuildSyntheticGeneratedChoiceCard(sel.CardDefId, fallbackDef, (byte)0, false);
                    string fallbackKey = HotContinuationStamp.FormatChoiceCardKey(in syntheticFallback, cold: null, rules);
                    return new PlanCardToken(fallbackId, 0, fallbackKey, 0, 0, displayNames.Card(fallbackId, 0));
                }
                return new PlanCardToken(string.Empty, 0, string.Empty, 0, 0, displayNames.Card(string.Empty, 0));
            }
        }

        ref readonly HotCard card = ref pausedState.Cards[foundIdx];
        string cardId = rules.Cards[card.CardDefId].Entry;
        string stateKey = HotContinuationStamp.FormatChoiceCardKey(in card, pausedCold, rules);

        int sourceOccurrence = 0;
        for (int i = zoneStart; i < foundIdx; i++)
        {
            ref readonly HotCard prior = ref pausedState.Cards[i];
            if (prior.UpgradeLevel == card.UpgradeLevel
                && string.Equals(rules.Cards[prior.CardDefId].Entry, cardId, StringComparison.Ordinal))
            {
                sourceOccurrence++;
            }
        }

        int scanCount = zoneCount;
        if (sourceCardId == HotCustomCardId.KiChant && pile == HotPileKind.Draw && kiChantTopLimit > 0)
        {
            int limit = Math.Min(zoneCount, kiChantTopLimit);
            // Only cap to top-N if foundIdx lies within the top-N window (defensive guard).
            if (foundIdx < zoneStart + limit)
                scanCount = limit;
        }

        Span<int> candidates = stackalloc int[HotLimits.MaxCards];
        int candCount = 0;
        bool foundInFiltered = false;
        for (int offset = 0; offset < scanCount; offset++)
        {
            int idx = zoneStart + offset;
            ref readonly HotCard candidate = ref pausedState.Cards[idx];
            if (!allowedUids.IsEmpty && !ContainsUid(allowedUids, candidate.CardUid))
                continue;
            if (!MatchesLiveChoiceFilter(in pausedState, in candidate, pausedCold, rules, sourceCardId, isTouchOfInsanity))
                continue;
            candidates[candCount++] = idx;
            if (idx == foundIdx)
                foundInFiltered = true;
        }

        // Defensive fallback if an unexpected filter did not include foundIdx: fall back to unfiltered pile.
        if (!foundInFiltered)
        {
            candCount = 0;
            for (int offset = 0; offset < scanCount; offset++)
                candidates[candCount++] = zoneStart + offset;
        }

        // STS2 0.113.1 CardSelectCmd.FromCombatPile sorts PileType.Draw stably by (x.Rarity, x).
        if (pile == HotPileKind.Draw && candCount > 1)
        {
            StableSortDrawCandidates(candidates[..candCount], in pausedState, pausedCold, rules);
        }

        totalCandidateCount = candCount;
        int optionOccurrence = 0;
        for (int i = 0; i < candCount; i++)
        {
            int idx = candidates[i];
            if (idx == foundIdx)
            {
                optionIndex = i;
                break;
            }
            ref readonly HotCard prior = ref pausedState.Cards[idx];
            if (prior.UpgradeLevel == card.UpgradeLevel
                && string.Equals(rules.Cards[prior.CardDefId].Entry, cardId, StringComparison.Ordinal))
            {
                optionOccurrence++;
            }
        }

        return new PlanCardToken(
            CardId: cardId,
            UpgradeLevel: card.UpgradeLevel,
            StateKey: stateKey,
            SourceOccurrence: sourceOccurrence,
            OptionOccurrence: optionOccurrence,
            Title: displayNames.Card(cardId, card.UpgradeLevel));
    }

    private static bool ShouldUpgradeGeneratedChoiceOptions(
        in CombatHotState stateBefore,
        in CombatHotState pausedState,
        HotRules rules,
        HotAction action,
        ushort sourceCardDefId)
    {
        if (sourceCardDefId == 0 || sourceCardDefId >= rules.Cards.Length)
            return false;

        HotCustomCardId sourceCardId = rules.Cards[sourceCardDefId].CardId;
        if (sourceCardId == HotCustomCardId.Abundance)
            return true;

        if (sourceCardId is HotCustomCardId.Quasar or HotCustomCardId.Splash)
            return TryFindSourceCard(in stateBefore, in pausedState, action, sourceCardDefId, out HotCard played)
                && played.UpgradeLevel > 0;

        return false;
    }

    private static bool ContainsUid(ReadOnlySpan<ushort> uids, ushort target)
    {
        for (int i = 0; i < uids.Length; i++)
        {
            if (uids[i] == target)
                return true;
        }
        return false;
    }

    private static HotCard BuildSyntheticGeneratedChoiceCard(
        ushort cardDefId,
        HotCardDef def,
        byte upgradeLevel,
        bool applyChoicesParadoxRetain)
    {
        HotCard generated = new()
        {
            CardDefId = cardDefId,
            CardUid = 0,
            UpgradeLevel = upgradeLevel,
            BaseEnergyCost = upgradeLevel > 0 ? def.UpgradedCost : def.BaseCost,
            BaseStarCost = def.BaseStarCost,
            BaseReplayCount = 0,
            ExtraReplayThisTurn = 0,
            LocalKeywordsMask = (HotCardKeywordMask)def.BaseKeywords,
            KeywordsAddedOutsideMask = HotCardKeywordMask.None,
            KeywordsRemovedOutsideMask = HotCardKeywordMask.None,
            Flags = HotCombatEngine.GetInitialCardFlags(def),
        };
        if (upgradeLevel > 0)
            HotCombatEngine.ApplyUpgradeKeywords(ref generated, def.CardId);
        if (applyChoicesParadoxRetain)
        {
            if ((generated.LocalKeywordsMask & HotCardKeywordMask.Retain) == 0)
            {
                generated.LocalKeywordsMask |= HotCardKeywordMask.Retain;
                generated.KeywordsAddedOutsideMask |= HotCardKeywordMask.Retain;
            }
            generated.KeywordsRemovedOutsideMask &= ~HotCardKeywordMask.Retain;
        }
        // Match STS2 v0.113.1 CardModel.IsSlyThisTurn (CardModel.cs:700-709):
        // true iff Keywords contains CardKeyword.Sly (or HasSingleTurnSly).
        if ((generated.LocalKeywordsMask & HotCardKeywordMask.Sly) != 0)
            generated.Flags |= HotCardFlags.IsSlyThisTurn;
        else
            generated.Flags &= ~HotCardFlags.IsSlyThisTurn;
        // Match freshly constructed ShowOff._currentAim = 5, _increasedAim = 0 (ShowOff.cs:19-21).
        if (def.PrivateKind == HotCardPrivateKind.ShowOff)
        {
            short baseAim = (short)GetCardVarInt(in generated, def, "BaseAim", 5);
            generated.CardPrivate = baseAim & 0xFFFF;
        }
        return generated;
    }

    private static bool TryResolveKnowledgeDemonDisintegrationDamage(
        in CombatHotState s,
        HotRules rules,
        out int damage)
    {
        for (int r = 0; r < s.RosterCount; r++)
        {
            byte slot = s.ActiveEnemyRoster[r];
            ref readonly HotEnemy enemy = ref s.Enemies[slot];
            if ((enemy.Vital.Flags & HotCreatureFlags.Alive) == 0)
                continue;
            if (enemy.MonsterDefId > 0
                && enemy.MonsterDefId < rules.Monsters.Length
                && rules.Monsters[enemy.MonsterDefId].MonsterId == HotMonsterId.KnowledgeDemon)
            {
                int counter = enemy.AiRegs[0];
                if ((uint)counter < 3u)
                {
                    damage = (rules.RulesSnapshot.IsDread ? 8 : 6) + counter;
                    return true;
                }
            }
        }
        damage = 0;
        return false;
    }

    private static bool MatchesLiveChoiceFilter(
        in CombatHotState s,
        in HotCard card,
        HotColdSidecar? cold,
        HotRules rules,
        HotCustomCardId sourceCardId,
        bool isTouchOfInsanity)
    {
        if (card.CardDefId == 0 || card.CardDefId >= rules.Cards.Length)
            return false;

        HotCardDef def = rules.Cards[card.CardDefId];

        if (isTouchOfInsanity)
        {
            // STS2 0.113.1 TouchOfInsanity: c.CostsEnergyOrStars(includeGlobalModifiers: false) || c.CostsEnergyOrStars(true)
            bool costsX = (card.Flags & HotCardFlags.CostsX) != 0;
            bool starCostsX = (card.Flags & HotCardFlags.HasStarCostX) != 0;
            int localEnergy = costsX ? 0 : HotCostModifiers.GetLocalEnergyCost(in card, cold);
            int localStar = starCostsX ? 0 : HotCostModifiers.GetEffectiveStarCost(in card, cold);
            if (localEnergy > 0 || localStar > 0)
                return true;

            int globalEnergy = costsX ? 0 : HotCombatEngine.GetEffectiveEnergyCost(in s, in card, cold, rules, isInPlayablePile: true);
            int globalStar = starCostsX ? 0 : HotCombatEngine.GetEffectiveStarCost(in s, in card, cold, rules, isInPlayablePile: true);
            return globalEnergy > 0 || globalStar > 0;
        }

        return sourceCardId switch
        {
            HotCustomCardId.Armaments => card.UpgradeLevel < def.MaxUpgradeLevel,
            HotCustomCardId.DualWield => def.Type is CardType.Attack or CardType.Power,
            HotCustomCardId.SecretWeapon => def.Type == CardType.Attack,
            HotCustomCardId.SecretTechnique => def.Type == CardType.Skill,
            HotCustomCardId.HandTrick => def.Type == CardType.Skill && (card.Flags & HotCardFlags.IsSlyThisTurn) == 0,
            HotCustomCardId.HeirloomHammer => def.Domain == HotCardDomain.Colorless,
            HotCustomCardId.SculptingStrike => (card.LocalKeywordsMask & HotCardKeywordMask.Ethereal) == 0,
            HotCustomCardId.Snap => !HotCombatEngine.HasEffectiveRetainKeyword(in s, in card, rules),
            HotCustomCardId.DecisionsDecisions => def.Type == CardType.Skill && (card.LocalKeywordsMask & HotCardKeywordMask.Unplayable) == 0,
            HotCustomCardId.LockedIn => def.IsShootTag,
            _ => true,
        };
    }

    private static void StableSortDrawCandidates(
        Span<int> indices,
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules)
    {
        // Insertion sort is strictly stable (preserves original DrawPile tie order) and zero-allocation.
        for (int i = 1; i < indices.Length; i++)
        {
            int key = indices[i];
            int j = i - 1;
            while (j >= 0 && CompareDrawSelectionOrder(in s.Cards[indices[j]], in s.Cards[key], in s, cold, rules) > 0)
            {
                indices[j + 1] = indices[j];
                j--;
            }
            indices[j + 1] = key;
        }
    }

    /// <summary>
    /// Exact match of STS2 0.113.1 <c>CardSelectCmd.FromCombatPile(PileType.Draw)</c>:
    /// <c>orderby x.Rarity, x</c> using <c>CardModel.CompareTo</c>
    /// (Rarity -> Entry Ordinal -> UpgradeLevel -> Enchantment -> Affliction -> EnergyCost.GetResolved() -> BaseReplayCount -> Ordered DynamicVars).
    /// </summary>
    private static int CompareDrawSelectionOrder(
        in HotCard a,
        in HotCard b,
        in CombatHotState s,
        HotColdSidecar? cold,
        HotRules rules)
    {
        HotCardDef defA = rules.Cards[a.CardDefId];
        HotCardDef defB = rules.Cards[b.CardDefId];

        int cmp = defA.Rarity.CompareTo(defB.Rarity);
        if (cmp != 0)
            return cmp;

        cmp = string.CompareOrdinal(defA.Entry, defB.Entry);
        if (cmp != 0)
            return cmp;

        cmp = a.UpgradeLevel.CompareTo(b.UpgradeLevel);
        if (cmp != 0)
            return cmp;

        if (a.EnchId != b.EnchId)
        {
            if (a.EnchId == 0)
                return -1;
            if (b.EnchId == 0)
                return 1;
            cmp = string.CompareOrdinal(rules.Enchantments[a.EnchId].Entry, rules.Enchantments[b.EnchId].Entry);
            if (cmp != 0)
                return cmp;
        }

        if (a.AfflId != b.AfflId)
        {
            if (a.AfflId == 0)
                return -1;
            if (b.AfflId == 0)
                return 1;
            cmp = string.CompareOrdinal(rules.Afflictions[a.AfflId].Entry, rules.Afflictions[b.AfflId].Entry);
            if (cmp != 0)
                return cmp;
        }

        int costA = (a.Flags & HotCardFlags.CostsX) != 0
            ? a.CapturedEnergy
            : Math.Max(0, HotCombatEngine.GetEffectiveEnergyCost(in s, in a, cold, rules, isInPlayablePile: false));
        int costB = (b.Flags & HotCardFlags.CostsX) != 0
            ? b.CapturedEnergy
            : Math.Max(0, HotCombatEngine.GetEffectiveEnergyCost(in s, in b, cold, rules, isInPlayablePile: false));
        cmp = costA.CompareTo(costB);
        if (cmp != 0)
            return cmp;

        cmp = a.BaseReplayCount.CompareTo(b.BaseReplayCount);
        if (cmp != 0)
            return cmp;

        string[] namesA = defA.OrderedVarNames;
        if (namesA.Length == 0)
            return 0;

        string[] namesB = defB.OrderedVarNames;
        if (ReferenceEquals(namesA, namesB))
        {
            for (int i = 0; i < namesA.Length; i++)
            {
                decimal valA = HotRules.GetCardVarValue(in a, defA, i);
                decimal valB = HotRules.GetCardVarValue(in b, defB, i);
                cmp = valA.CompareTo(valB);
                if (cmp != 0)
                    return cmp;
            }
            return 0;
        }

        for (int i = 0; i < namesA.Length; i++)
        {
            int idxB = Array.BinarySearch(namesB, namesA[i], StringComparer.Ordinal);
            if (idxB < 0)
                continue;
            decimal valA = HotRules.GetCardVarValue(in a, defA, i);
            decimal valB = HotRules.GetCardVarValue(in b, defB, idxB);
            cmp = valA.CompareTo(valB);
            if (cmp != 0)
                return cmp;
        }

        return 0;
    }

    private static HotPileKind MapHotPileKind(HotChoiceKind kind)
        => kind switch
        {
            HotChoiceKind.HandCard => HotPileKind.Hand,
            HotChoiceKind.DrawPile => HotPileKind.Draw,
            HotChoiceKind.DiscardPile => HotPileKind.Discard,
            HotChoiceKind.ExhaustPile => HotPileKind.Exhaust,
            _ => HotPileKind.Hand,
        };

    private static PileType MapPileType(HotChoiceKind kind)
        => kind switch
        {
            HotChoiceKind.HandCard => PileType.Hand,
            HotChoiceKind.DrawPile => PileType.Draw,
            HotChoiceKind.DiscardPile => PileType.Discard,
            HotChoiceKind.ExhaustPile => PileType.Exhaust,
            _ => PileType.None,
        };

    private static PlanChoiceEffect InferPlanChoiceEffect(ushort sourceCardDefId, HotChoiceKind kind, HotRules rules)
    {
        if (kind == HotChoiceKind.GeneratedCard)
            return PlanChoiceEffect.GenerateToHand;

        HotCustomCardId id = sourceCardDefId > 0 && sourceCardDefId < rules.Cards.Length
            ? rules.Cards[sourceCardDefId].CardId
            : HotCustomCardId.None;

        return id switch
        {
            HotCustomCardId.Headbutt or HotCustomCardId.ThinkingAhead or HotCustomCardId.Glimmer
                or HotCustomCardId.WahKi or HotCustomCardId.CosmicIndifference or HotCustomCardId.PhotonCut
                => PlanChoiceEffect.MoveToDrawTop,
            HotCustomCardId.Survivor or HotCustomCardId.Acrobatics or HotCustomCardId.Prepared
                or HotCustomCardId.DaggerThrow or HotCustomCardId.HiddenDaggers or HotCustomCardId.TheHermit
                => PlanChoiceEffect.Discard,
            HotCustomCardId.TrueGrit or HotCustomCardId.BurningPact or HotCustomCardId.Purity
                or HotCustomCardId.Brand or HotCustomCardId.Scavenge or HotCustomCardId.Cleanse
                => PlanChoiceEffect.Exhaust,
            HotCustomCardId.Armaments => PlanChoiceEffect.Upgrade,
            HotCustomCardId.Nightmare => PlanChoiceEffect.Nightmare,
            HotCustomCardId.DualWield or HotCustomCardId.HeirloomHammer => PlanChoiceEffect.Duplicate,
            HotCustomCardId.Begone or HotCustomCardId.Charge or HotCustomCardId.Guards or HotCustomCardId.Seance
                or HotCustomCardId.Florescence
                => PlanChoiceEffect.Transform,
            HotCustomCardId.Transfigure => PlanChoiceEffect.Modify,
            HotCustomCardId.HandTrick => PlanChoiceEffect.ApplySly,
            HotCustomCardId.SculptingStrike => PlanChoiceEffect.ApplyEthereal,
            HotCustomCardId.Snap => PlanChoiceEffect.ApplyRetain,
            HotCustomCardId.DecisionsDecisions => PlanChoiceEffect.AutoPlayRepeated,
            _ => kind is HotChoiceKind.DrawPile or HotChoiceKind.DiscardPile or HotChoiceKind.ExhaustPile
                ? PlanChoiceEffect.MoveToHand
                : PlanChoiceEffect.Discard,
        };
    }

    private static int FindRosterIndex(in CombatHotState s, byte enemySlot)
    {
        for (int r = 0; r < s.RosterCount; r++)
        {
            if (s.ActiveEnemyRoster[r] == enemySlot)
                return r;
        }
        return enemySlot;
    }

    private static string ResolveEnemyDisplayName(
        in CombatHotState s,
        int enemySlot,
        HotRules rules,
        SolverDisplayNames displayNames,
        Dictionary<uint, Creature> rootEnemiesByCombatId)
    {
        ref readonly HotEnemy enemy = ref s.Enemies[enemySlot];
        if (rootEnemiesByCombatId.TryGetValue(enemy.Vital.CombatId, out Creature? rootCreature))
            return displayNames.Creature(rootCreature);

        string entry = enemy.MonsterDefId > 0 && enemy.MonsterDefId < rules.Monsters.Length
            ? rules.Monsters[enemy.MonsterDefId].Entry
            : (enemySlot < rules.EnemyMonsterEntries.Length ? rules.EnemyMonsterEntries[enemySlot] : string.Empty);
        return string.IsNullOrEmpty(entry) ? $"Enemy#{enemySlot}" : displayNames.Monster(entry);
    }

    private static int SnapshotAliveEnemyHp(in CombatHotState s, Span<int> dst)
    {
        int enemyCount = Math.Min((int)s.EnemyCount, Math.Min(HotLimits.MaxEnemies, dst.Length));
        for (int e = 0; e < enemyCount; e++)
        {
            ref readonly HotEnemy enemy = ref s.Enemies[e];
            dst[e] = (enemy.Vital.Flags & HotCreatureFlags.Alive) != 0
                ? Math.Max(0, enemy.Vital.CurrentHp)
                : 0;
        }
        return enemyCount;
    }

    private static int AccumulateRealEnemyHpLoss(ReadOnlySpan<int> hpBeforeStep, int enemyCountBefore, in CombatHotState after)
    {
        int realLoss = 0;
        for (int e = 0; e < enemyCountBefore; e++)
        {
            int beforeHp = hpBeforeStep[e];
            if (beforeHp <= 0)
                continue;
            // Works uniformly for Alive, Killed (CurrentHp == 0), and Escaped (CreatureEscaped preserves un-dealt CurrentHp).
            int afterHp = Math.Max(0, after.Enemies[e].Vital.CurrentHp);
            if (afterHp < beforeHp)
                realLoss += beforeHp - afterHp;
        }
        return realLoss;
    }

    private static ulong CaptureAliveEnemySlotMask(in CombatHotState s)
    {
        ulong mask = 0UL;
        for (int r = 0; r < s.RosterCount; r++)
        {
            byte slot = s.ActiveEnemyRoster[r];
            if ((s.Enemies[slot].Vital.Flags & HotCreatureFlags.Alive) != 0)
                mask |= 1UL << slot;
        }
        return mask;
    }

    private static int[] SnapshotPotionSlots(in CombatHotState s)
    {
        int count = Math.Min((int)s.PotionSlotCount, HotLimits.MaxPotions);
        int[] snap = new int[count];
        for (int i = 0; i < count; i++)
            snap[i] = s.Potions[i];
        return snap;
    }

    private static short ResolvePotionSlotCost(
        int slot,
        int potionDefId,
        ushort generatedPotionMask,
        short[] rootPotionCosts,
        HotRules rules,
        in HotSearchPolicy hotPolicy)
    {
        if (potionDefId <= 0 || potionDefId >= rules.Potions.Length)
            return 0;
        if ((uint)slot < HotLimits.MaxPotions && (generatedPotionMask & (1 << slot)) != 0)
            return 0;
        if ((uint)slot < (uint)rootPotionCosts.Length && rootPotionCosts[slot] >= 0)
            return rootPotionCosts[slot];
        return HotPotionPolicy.ComputePotionDefCost(
            rules.Potions[potionDefId],
            hotPolicy.HasRenewablePotionShapedRock);
    }

    private static void RecordAutomaticPotionUses(
        int[] beforeSlots,
        ushort beforeGeneratedMask,
        in CombatHotState after,
        int explicitSlot,
        HotRules rules,
        short[] rootPotionCosts,
        in HotSearchPolicy hotPolicy,
        int turn,
        List<PredictedPotionUse> potionUses,
        Dictionary<int, int> potionCountByTurn,
        Dictionary<int, int> potionCostByTurn)
    {
        for (int slot = 0; slot < beforeSlots.Length; slot++)
        {
            if (slot == explicitSlot)
                continue;
            int beforeDef = beforeSlots[slot];
            if (beforeDef != 0 && after.Potions[slot] == 0)
            {
                string pId = beforeDef < rules.Potions.Length ? rules.Potions[beforeDef].Entry : string.Empty;
                short cost = ResolvePotionSlotCost(
                    slot,
                    beforeDef,
                    beforeGeneratedMask,
                    rootPotionCosts,
                    rules,
                    in hotPolicy);
                potionUses.Add(new PredictedPotionUse(slot, pId, cost, Automatic: true));
                potionCountByTurn[turn] = potionCountByTurn.GetValueOrDefault(turn) + 1;
                potionCostByTurn[turn] = potionCostByTurn.GetValueOrDefault(turn) + cost;
            }
        }
    }
}
