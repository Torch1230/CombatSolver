using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;

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
    int PotionBranchesRejected);

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
        HotRules rules = rootBundle.Rules;
        int[] nodePath = nodeArena.ExtractActionNodePath(winningNodeId);

        CombatHotState state = rootBundle.RootState;
        HotColdSidecar? cold = rootBundle.RootCold;
        HotExecScratch scratch = new();

        List<PlanAction> planActions = new(nodePath.Length);
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
        int turnStartEnemyHp = ComputeTotalAliveEnemyHp(in state);
        int turnPeakBlock = state.Player.Block;
        int shufflesCrossed = 0;
        int lastShuffleEvents = state.ShuffleEventCount;
        int? combatEndedTurn = null;
        int? deathTurn = null;

        for (int stepIdx = 0; stepIdx < nodePath.Length; stepIdx++)
        {
            ref readonly HotSearchNode stepNode = ref nodeArena.GetRef(nodePath[stepIdx]);
            HotAction hotAction = stepNode.Action;
            ReadOnlySpan<HotChoiceSelection> choices = nodeArena.GetNodeChoices(in stepNode);

            // Match exact canonicalization + deterministic CardUid assignment performed by HotParallelExpander
            HotStatePacker.CanonicalizeForPack(ref state, ref cold);
            HotParallelExpander.AssignDeterministicCardUids(ref state);

            int actionTurn = state.PlayerTurnNumber;
            int blockBeforeAction = state.Player.Block;
            int energyBeforeAction = state.Energy;
            if (blockBeforeAction > turnPeakBlock)
                turnPeakBlock = blockBeforeAction;

            ulong aliveMaskBefore = CaptureAliveEnemySlotMask(in state);
            int[] potionSlotsBefore = SnapshotPotionSlots(in state);

            // Annotate action metadata & choices BEFORE executing the action on the replay state
            PlanAction planAction = BuildPlanAction(
                in state,
                cold,
                rules,
                displayNames,
                rootEnemiesByCombatId,
                hotAction,
                choices,
                actionTurn,
                out short explicitPotionCost);

            // Execute step on replay state
            HotStepResult res = HotCombatEngine.Step(ref state, ref cold, rules, scratch, hotAction, choices);
            if (!res.Executed)
                throw new InvalidOperationException($"HotPlanMaterializer replay failed at step {stepIdx} ({hotAction}).");

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
                in state,
                hotAction.Kind == HotActionKind.UsePotion ? hotAction.SlotOrHandIdx : -1,
                rules,
                in hotPolicy,
                actionTurn,
                potionUses,
                potionCountByTurn,
                potionCostByTurn);

            // Record enemy kills caused by this action
            ulong aliveMaskAfter = CaptureAliveEnemySlotMask(in state);
            ulong newlyKilled = aliveMaskBefore & ~aliveMaskAfter;
            if (planAction.IsExecutable && newlyKilled != 0)
            {
                List<string> killedNames = [];
                for (int eSlot = 0; eSlot < state.EnemyCount; eSlot++)
                {
                    if ((newlyKilled & (1UL << eSlot)) == 0)
                        continue;
                    killedNames.Add(ResolveEnemyDisplayName(in state, eSlot, rules, displayNames, rootEnemiesByCombatId));
                }
                if (killedNames.Count > 0)
                    killsAfterAction[stepIdx] = killedNames;
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

            if (isVictory && combatEndedTurn == null)
                combatEndedTurn = state.TerminalTurn > 0 ? state.TerminalTurn : actionTurn;
            if (isDefeat && deathTurn == null)
                deathTurn = state.TerminalTurn > 0 ? state.TerminalTurn : actionTurn;

            bool isFinalStep = stepIdx == nodePath.Length - 1;
            if (endedByTurn || isVictory || isDefeat || isFinalStep)
            {
                int curHpLost = state.Counters[(int)HotCounterSlot.PlayerCumulativeHpLost];
                int curHpRecovered = state.Counters[(int)HotCounterSlot.PlayerRecoveredHp];
                int curEnemyHp = ComputeTotalAliveEnemyHp(in state);

                int turnHpLost = Math.Max(0, curHpLost - turnStartHpLost);
                int turnHpRecovered = Math.Max(0, curHpRecovered - turnStartHpRecovered);
                int turnEnemyHpLost = Math.Max(0, turnStartEnemyHp - curEnemyHp);
                int actualBlock = endedByTurn ? blockBeforeAction : state.Player.Block;
                int energyLeft = endedByTurn ? energyBeforeAction : state.Energy;

                hpLostByTurn[actionTurn] = turnHpLost;
                hpRecoveredByTurn[actionTurn] = turnHpRecovered;
                enemyHpLostByTurn[actionTurn] = turnEnemyHpLost;
                soldHpByTurn[actionTurn] = 0;
                actualBlockByTurn[actionTurn] = actualBlock;
                maxBlockByTurn[actionTurn] = Math.Max(turnPeakBlock, actualBlock);
                energyLeftByTurn[actionTurn] = energyLeft;

                if (endedByTurn && !isVictory && !isDefeat)
                {
                    currentTurn = state.PlayerTurnNumber;
                    turnStartHpLost = curHpLost;
                    turnStartHpRecovered = curHpRecovered;
                    turnStartEnemyHp = curEnemyHp;
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
        if (nodePath.Length == 0)
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
        bool finalWon = planActions.Count > 0 && finalSummary.IsVictory;
        bool finalDead = finalSummary.IsDefeat;
        int postCombatHeal = finalWon
            ? hotPolicy.PostCombatRelicHeal.HealFor(finalSummary.PlayerHp, finalSummary.PlayerMaxHp)
            : 0;

        GrowthValues realizedGrowth = new(
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

        SolverSnapshot snapshot = new(
            HasRisk: false,
            PlayerDead: finalDead,
            AllEnemiesDead: finalSummary.AliveEnemyCount == 0 && !finalDead,
            PlayerHp: finalSummary.PlayerHp,
            PlayerMaxHp: finalSummary.PlayerMaxHp,
            CumulativePlayerHpLost: finalSummary.HpLost,
            RecoveredPlayerHp: finalSummary.HpRecovered,
            DeathSaveRelicHpRestored: state.DeathSaveRelicHpRestored,
            LongTermResourceValue: state.LongTermResourceValue,
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
            BoundaryReason: metrics.BoundaryReason,
            PredictionGaps: Array.Empty<PredictionGap>())
        {
            DeathSavePotionHpRestored = state.DeathSavePotionHpRestored,
            DeathSaveUseCount = finalSummary.DeathSaveUseCount,
            ProjectedDeathSaveUseCount = finalSummary.DeathSaveUseCount,
            GrowthHpCredit = finalSummary.GrowthHpCredit,
            GrowthRewards = realizedGrowth,
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
            metrics.SearchedTurns > 0
                ? metrics.SearchedTurns
                : planActions.Count == 0
                    ? 1
                    : planActions.Max(static a => a.Turn) - root.StartTurnNumber + 1);

        SolverResult result = new()
        {
            ComparisonQuality = HotRouteQuality.ToInterimResult(in winningKey, in hotPolicy),
            ComparisonRootState = root.ContinuationStamp.StateText,
            TotalSearchElapsed = metrics.TotalSearchElapsed,
            TotalWorkerAllocatedBytes = metrics.TotalWorkerAllocatedBytes,
            TotalGen0Collections = metrics.Gen0Collections,
            TotalGen1Collections = metrics.Gen1Collections,
            TotalGen2Collections = metrics.Gen2Collections,
            TotalGcPauseDuration = metrics.GcPauseDuration,
            StartTurnNumber = root.StartTurnNumber,
            TurnSetupChoices = Array.Empty<PlanCardChoice>(),
            TurnSetupPlayState = null,
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
            BoundaryReason = metrics.BoundaryReason,
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
                choices);

            if (action.Kind == HotActionKind.EndTurn)
            {
                turnStartChoices = materializedChoices;
            }
            else if (materializedChoices.Count == 1)
            {
                primaryChoice = materializedChoices[0];
            }
            else if (materializedChoices.Count > 1)
            {
                primaryChoice = materializedChoices[0];
                nestedChoices = materializedChoices.GetRange(1, materializedChoices.Count - 1);
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
                ushort pDefId = slot < stateBefore.PotionSlotCount ? stateBefore.Potions[slot] : (ushort)0;
                string potionId = pDefId > 0 && pDefId < rules.Potions.Length ? rules.Potions[pDefId].Entry : string.Empty;
                string potionTitle = string.IsNullOrEmpty(potionId) ? string.Empty : displayNames.Potion(potionId);
                if (pDefId > 0 && pDefId < rules.Potions.Length)
                    explicitPotionCost = HotPotionPolicy.ComputePotionDefCost(rules.Potions[pDefId], rules.HasRenewablePotionShapedRock);

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
                    PotionTitle: potionTitle);
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
                    ReplayCount: card.BaseReplayCount + card.ExtraReplayThisTurn,
                    CardStateKey: stateKey,
                    CardStateOccurrence: stateOccurrence,
                    CardUpgradeLevel: card.UpgradeLevel,
                    CardEnchantmentId: enchId);
            }
        }
    }

    private static List<PlanCardChoice> MaterializeChoicesForAction(
        in CombatHotState stateBefore,
        HotColdSidecar? coldBefore,
        HotRules rules,
        SolverDisplayNames displayNames,
        HotAction action,
        ReadOnlySpan<HotChoiceSelection> choices)
    {
        List<PlanCardChoice> result = new(choices.Length);
        HotExecScratch probeScratch = new();

        for (int cIdx = 0; cIdx < choices.Length; cIdx++)
        {
            CombatHotState probeState = stateBefore;
            HotColdSidecar? probeCold = coldBefore;
            _ = HotCombatEngine.Step(ref probeState, ref probeCold, rules, probeScratch, action, choices[..cIdx]);

            HotChoiceSelection sel = choices[cIdx];
            HotChoiceKind pendingKind = probeScratch.NeedsChoice ? probeScratch.PendingChoiceKind : sel.Kind;
            ushort sourceCardDefId = probeScratch.PendingChoiceSourceCardDefId;
            string sourceId = sourceCardDefId > 0 && sourceCardDefId < rules.Cards.Length
                ? rules.Cards[sourceCardDefId].Entry
                : string.Empty;

            PlanChoiceTiming timing = action.Kind == HotActionKind.EndTurn
                ? (probeState.CurrentSide == HotCombatSide.Player && probeState.PlayerTurnNumber > stateBefore.PlayerTurnNumber
                    ? PlanChoiceTiming.PlayerTurnStart
                    : PlanChoiceTiming.PlayerTurnEnd)
                : PlanChoiceTiming.Action;

            PileType sourcePile = MapPileType(pendingKind);
            PlanChoiceEffect effect = InferPlanChoiceEffect(sourceCardDefId, pendingKind, rules);

            if (sel.Kind == HotChoiceKind.None)
            {
                result.Add(new PlanCardChoice(effect, sourcePile, Array.Empty<PlanCardToken>(), sourceId, string.Empty, timing));
                continue;
            }

            PlanCardToken token = BuildPlanCardToken(in probeState, probeCold, rules, displayNames, sel, pendingKind);

            // Group consecutive multi-select picks from the same card/pile prompt into one PlanCardChoice
            if (result.Count > 0
                && probeScratch.PendingChoiceMax > 1
                && result[^1].Effect == effect
                && result[^1].SourcePile == sourcePile
                && result[^1].Timing == timing
                && string.Equals(result[^1].SourceId, sourceId, StringComparison.Ordinal)
                && result[^1].Cards.Count > 0
                && result[^1].Cards.Count < probeScratch.PendingChoiceMax)
            {
                PlanCardChoice prev = result[^1];
                PlanCardToken[] merged = new PlanCardToken[prev.Cards.Count + 1];
                for (int t = 0; t < prev.Cards.Count; t++)
                    merged[t] = prev.Cards[t];
                merged[prev.Cards.Count] = token;
                result[^1] = prev with { Cards = merged };
            }
            else
            {
                result.Add(new PlanCardChoice(effect, sourcePile, [token], sourceId, string.Empty, timing));
            }
        }

        return result;
    }

    private static PlanCardToken BuildPlanCardToken(
        in CombatHotState pausedState,
        HotColdSidecar? pausedCold,
        HotRules rules,
        SolverDisplayNames displayNames,
        HotChoiceSelection sel,
        HotChoiceKind pendingKind)
    {
        if (sel.Kind == HotChoiceKind.GeneratedCard || pendingKind == HotChoiceKind.GeneratedCard)
        {
            string genId = sel.CardDefId > 0 && sel.CardDefId < rules.Cards.Length
                ? rules.Cards[sel.CardDefId].Entry
                : string.Empty;
            return new PlanCardToken(
                CardId: genId,
                UpgradeLevel: 0,
                StateKey: genId,
                SourceOccurrence: sel.CardUidOrIndex,
                OptionOccurrence: 0,
                Title: displayNames.Card(genId, 0));
        }

        HotPileKind pile = pendingKind switch
        {
            HotChoiceKind.HandCard => HotPileKind.Hand,
            HotChoiceKind.DrawPile => HotPileKind.Draw,
            HotChoiceKind.DiscardPile => HotPileKind.Discard,
            HotChoiceKind.ExhaustPile => HotPileKind.Exhaust,
            _ => HotPileKind.Hand,
        };

        int zoneStart = HotPiles.ZoneStart(in pausedState, pile);
        int zoneCount = HotPiles.ZoneCount(in pausedState, pile);
        int foundIdx = HotCombatEngine.FindCardIndexByUid(in pausedState, pile, sel.CardUidOrIndex);

        if (foundIdx < 0)
        {
            string fallbackId = sel.CardDefId > 0 && sel.CardDefId < rules.Cards.Length
                ? rules.Cards[sel.CardDefId].Entry
                : string.Empty;
            return new PlanCardToken(fallbackId, 0, fallbackId, 0, 0, displayNames.Card(fallbackId, 0));
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

        return new PlanCardToken(
            CardId: cardId,
            UpgradeLevel: card.UpgradeLevel,
            StateKey: stateKey,
            SourceOccurrence: sourceOccurrence,
            OptionOccurrence: sourceOccurrence,
            Title: displayNames.Card(cardId, card.UpgradeLevel));
    }

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
                or HotCustomCardId.Recall or HotCustomCardId.CosmicIndifference or HotCustomCardId.PhotonCut
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
                => PlanChoiceEffect.Transform,
            HotCustomCardId.Transfigure => PlanChoiceEffect.Modify,
            HotCustomCardId.HandTrick => PlanChoiceEffect.ApplySly,
            HotCustomCardId.SculptingStrike => PlanChoiceEffect.ApplyEthereal,
            HotCustomCardId.Snap => PlanChoiceEffect.ApplyRetain,
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

    private static int ComputeTotalAliveEnemyHp(in CombatHotState s)
    {
        int sum = 0;
        for (int r = 0; r < s.RosterCount; r++)
        {
            ref readonly HotEnemy e = ref s.Enemies[s.ActiveEnemyRoster[r]];
            if ((e.Vital.Flags & HotCreatureFlags.Alive) != 0)
                sum += Math.Max(0, e.Vital.CurrentHp);
        }
        return sum;
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

    private static void RecordAutomaticPotionUses(
        int[] beforeSlots,
        in CombatHotState after,
        int explicitSlot,
        HotRules rules,
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
                short cost = beforeDef < rules.Potions.Length
                    ? HotPotionPolicy.ComputePotionDefCost(rules.Potions[beforeDef], hotPolicy.HasRenewablePotionShapedRock)
                    : (short)0;
                potionUses.Add(new PredictedPotionUse(slot, pId, cost, Automatic: true));
                potionCountByTurn[turn] = potionCountByTurn.GetValueOrDefault(turn) + 1;
                potionCostByTurn[turn] = potionCostByTurn.GetValueOrDefault(turn) + cost;
            }
        }
    }
}
