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
        CancellationToken continuationCancellation)
    {
        PlanAction[] actions = BuildActions(dto.Actions, root, displayNames);
        Dictionary<int, IReadOnlyList<string>> killsAfterAction = BuildKillsAfterAction(
            dto.Actions, root, displayNames);

        bool renewableRock = root.HasRenewablePotionShapedRock;
        PredictedPotionUse[] potionUses = dto.PotionUses
            .Select(use =>
            {
                int slot = use.Slot ?? -1;
                int strategicCost = PotionUsePolicy.StrategicHpCost(use.Id, renewableRock);
                return new PredictedPotionUse(slot, use.Id, strategicCost, Automatic: false);
            })
            .ToArray();
        string[] plannedPotionIds = dto.PotionUses.Select(static use => use.Id).ToArray();
        int potionHpRequired = potionUses.Sum(static use => use.StrategicHpCost);
        int explicitPotionCount = actions.Count(static a => a.Kind == PlanActionKind.UsePotion);

        Dictionary<int, int> hpLostByTurn = [];
        Dictionary<int, int> hpRecoveredByTurn = [];
        Dictionary<int, int> enemyHpLostByTurn = [];
        Dictionary<int, int> soldHpByTurn = [];
        Dictionary<int, int> maxBlockByTurn = [];
        Dictionary<int, int> actualBlockByTurn = [];
        Dictionary<int, int> energyLeftByTurn = [];
        Dictionary<int, int> potionCountByTurn = [];
        Dictionary<int, int> potionStrategicCostByTurn = [];

        foreach (InCombatTurnOutcomeDto turnOutcome in dto.TurnOutcomes)
        {
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

        int maxActionTurn = actions.Length > 0
            ? actions.Max(static a => a.Turn)
            : root.StartTurnNumber;
        foreach (PlanAction action in actions)
        {
            hpLostByTurn.TryAdd(action.Turn, 0);
            hpRecoveredByTurn.TryAdd(action.Turn, 0);
            enemyHpLostByTurn.TryAdd(action.Turn, 0);
            soldHpByTurn.TryAdd(action.Turn, 0);
            maxBlockByTurn.TryAdd(action.Turn, 0);
            actualBlockByTurn.TryAdd(action.Turn, 0);
            energyLeftByTurn.TryAdd(action.Turn, 0);
        }

        InCombatTerminalSnapshotDto terminal = dto.Terminal;
        SearchBoundaryReason terminalBoundary = ParseSearchBoundary(terminal.SearchBoundary);
        SearchBoundaryReason resultBoundary = resultScope == SolverResultScope.RouteAdoption
            ? SearchBoundaryReason.None
            : terminalBoundary;

        int searchedTurns = Math.Max(1, maxActionTurn - root.StartTurnNumber + 1);
        IReadOnlyList<CachedContinuation> continuations = [];
        if (resultScope != SolverResultScope.CurrentTurnAdoption)
        {
            _ = CombatBeamSolver.TryReplaySingleRouteForContinuation(
                root,
                displayNames,
                battleDamage,
                searchPolicy,
                actions,
                continuationCancellation,
                out continuations);
        }

        SolverSnapshot snapshot = new(
            HasRisk: terminal.HasRisk,
            PlayerDead: terminal.PlayerDead,
            AllEnemiesDead: terminal.AllEnemiesDead,
            PlayerHp: terminal.PlayerHp,
            PlayerMaxHp: terminal.PlayerMaxHp,
            CumulativePlayerHpLost: terminal.CumulativePlayerHpLost,
            RecoveredPlayerHp: terminal.RecoveredPlayerHp,
            DeathSaveRelicHpRestored: 0,
            LongTermResourceValue: 0,
            AngerCopiesGenerated: 0,
            ProjectedPlayerHp: terminal.ProjectedPlayerHp,
            PlayerBlock: terminal.PlayerBlock,
            EnemyHp: terminal.EnemyHp,
            AliveEnemyCount: terminal.AliveEnemyCount,
            Energy: terminal.Energy,
            Stars: terminal.Stars,
            HandCount: terminal.HandCount,
            OutstandingStolenResource: terminal.OutstandingStolenResource,
            Turn: terminal.Turn,
            ShufflesCrossed: terminal.ShufflesCrossed,
            BoundaryReason: terminalBoundary,
            PredictionGaps: []);

        SelectedSearchPlan selectedPlan = new(
            actions,
            actions.Length,
            0d);

        int clampedExpanded = (int)Math.Clamp(dto.ExpandedNodes, 0L, int.MaxValue);
        SolverResult result = new()
        {
            ResultScope = resultScope,
            SingleSessionSearch = true,
            TotalSearchElapsed = totalElapsed,
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
            ExpandedNodes = clampedExpanded,
            TotalExpandedNodes = dto.ExpandedNodes,
            DominatedActionsPruned = 0,
            TopQueueActionsDropped = 0,
            ActionAdmissionRepresentativesProtected = 0,
            DuplicateCardBranchesPruned = 0,
            ChoiceBranchesEvaluated = 0,
            TotalChoiceBranchesEvaluated = 0,
            ShuffleBranchesPruned = 0,
            SoldHpBranchesPruned = 0,
            HpInvestmentBranchesProtected = 0,
            ReplayCount = 0,
            ForkCount = 0,
            TransitionCount = 0,
            TotalTransitionCount = 0,
            ReusedNodeSnapshots = 0,
            TranspositionBranchesPruned = 0,
            RepeatableNoProgressBranchesPruned = 0,
            StandPatProbes = 0,
            MaxParallelExpansionConcurrency = effectiveDegreeOfParallelism,
            TransitionCacheHits = 0,
            WorkerAllocatedBytes = 0L,
            Gen0Collections = 0,
            Gen1Collections = 0,
            Gen2Collections = 0,
            GcPauseDuration = TimeSpan.Zero,
            WorkerYieldCount = 0,
            SearchedTurns = searchedTurns,
            BoundaryReason = resultBoundary,
            UnavoidableHpLost = terminal.ProjectedBattleHpLost,
            SoldHp = 0,
            FutureSoldHp = 0,
            BattleHpLostSoFar = battleDamage.HpLostSoFar,
            BattleHpRecoveredOrGainedSoFar = battleDamage.HpRecoveredOrGainedSoFar,
            ProjectedBattleHpLost = terminal.ProjectedBattleHpLost,
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
            OutstandingStolenResource = terminal.OutstandingStolenResource,
            SoldHpByTurn = soldHpByTurn,
            HpLostByTurn = hpLostByTurn,
            HpRecoveredByTurn = hpRecoveredByTurn,
            PostCombatRelicHeal = terminal.PostCombatRelicHeal,
            EnemyHpLostByTurn = enemyHpLostByTurn,
            MaxBlockByTurn = maxBlockByTurn,
            ActualBlockByTurn = actualBlockByTurn,
            EnergyLeftByTurn = energyLeftByTurn,
            PotionCountByTurn = potionCountByTurn,
            PotionStrategicCostByTurn = potionStrategicCostByTurn,
            KillsAfterAction = killsAfterAction,
            CombatEndedTurn = terminal.CombatEndedTurn,
            DeathTurn = terminal.DeathTurn,
            OnlyDeathRoutesFound = terminal.OnlyDeathRoutes,
            IsActEndingBoss = root.IsActEndingBoss,
            BossHpRelief = root.BossHpRelief,
            Elapsed = totalElapsed,
            Continuations = resultScope == SolverResultScope.CurrentTurnAdoption ? [] : continuations,
        };
        result.AssertCompleteTurnOutcomes();
        return result;
    }

    public static SolverProgress MaterializeProgress(
        InCombatSts2SimProgressDto progressDto,
        int candidateVersion,
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot searchPolicy,
        int effectiveDegreeOfParallelism,
        int requestBudgetMilliseconds,
        CancellationToken routeAdoptionCancellation)
    {
        SolverInterimResult? interimResult = null;
        SolverCurrentTurnPreview? turnPreview = null;
        SolverSpeculativeRoutePreview? speculativePreview = null;
        SolverRouteAdoptionSeed? adoptionSeed = null;

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
                effectiveDegreeOfParallelism,
                SolverResultScope.CurrentTurnAdoption,
                CancellationToken.None);

            InCombatTerminalSnapshotDto terminal = bestDto.Terminal;
            bool won = !terminal.PlayerDead && terminal.AllEnemiesDead && terminal.CombatEndedTurn.HasValue;
            interimResult = new SolverInterimResult(
                Won: won,
                OutstandingStolenResource: terminal.OutstandingStolenResource,
                ProjectedBattleHpLost: terminal.ProjectedBattleHpLost,
                StrategicHpDeficit: terminal.ProjectedBattleHpLost,
                PotionStrategicCost: previewResult.PotionHpRequired,
                ProjectedBattlePotionCount: previewResult.ProjectedBattlePotionCount,
                EnemyHp: terminal.EnemyHp,
                Score: 0d,
                CombatEndedTurn: terminal.CombatEndedTurn)
            {
                TheftPolicy = searchPolicy.TheftPolicy,
                ForfeitsEncounterGold = false,
                Survives = !terminal.PlayerDead && terminal.ProjectedPlayerHp > 0,
            };

            turnPreview = SolverCurrentTurnPreview.FromResult(previewResult, candidateVersion);
            speculativePreview = SolverSpeculativeRoutePreview.FromResult(previewResult, candidateVersion);
            adoptionSeed = new SolverRouteAdoptionSeed(
                candidateVersion,
                previewResult.BestNode.Actions,
                () => MaterializeResult(
                    bestDto,
                    root,
                    displayNames,
                    battleDamage,
                    searchPolicy,
                    elapsed,
                    effectiveDegreeOfParallelism,
                    SolverResultScope.RouteAdoption,
                    routeAdoptionCancellation));
        }

        int clampedExpanded = (int)Math.Clamp(progressDto.ExpandedNodes, 0L, int.MaxValue);
        int currentTurn = Math.Max(root.StartTurnNumber, progressDto.MaxTurnReached);
        int completedTurns = Math.Max(0, currentTurn - root.StartTurnNumber);

        return new SolverProgress(
            StartTurnNumber: root.StartTurnNumber,
            CurrentTurnNumber: currentTurn,
            CompletedTurnLayers: completedTurns,
            PlayDepth: 0,
            ExpandedNodes: clampedExpanded,
            ReviewedWorldlines: progressDto.ExpandedNodes,
            MaxNodes: searchPolicy.Profile.MaxExpandedNodes,
            FrontierNodes: 0,
            EndedNodes: 0,
            ElapsedMilliseconds: (long)Math.Max(0d, progressDto.ElapsedMilliseconds),
            Phase: "正在搜索·Sts2Sim",
            CurrentBestResult: interimResult,
            CurrentTurnPreview: turnPreview,
            SpeculativeRoutePreview: speculativePreview,
            RouteAdoptionSeed: adoptionSeed,
            RequestBudgetMilliseconds: requestBudgetMilliseconds);
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

            List<PlanCardChoice> actionChoices = [];
            List<PlanCardChoice> turnStartChoices = [];
            foreach (InCombatChoiceTraceDto choiceDto in src.Choices)
            {
                PlanCardChoice mapped = MapChoice(choiceDto, displayNames);
                if (mapped.Timing == PlanChoiceTiming.Action)
                    actionChoices.Add(mapped);
                else
                    turnStartChoices.Add(mapped);
            }

            PlanCardChoice? primaryChoice = actionChoices.Count > 0 ? actionChoices[0] : null;
            PlanCardChoice[]? nestedChoices = actionChoices.Count > 1
                ? actionChoices.Skip(1).ToArray()
                : null;
            PlanCardChoice[]? mappedTurnStart = turnStartChoices.Count > 0
                ? turnStartChoices.ToArray()
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
                NestedChoicesBeforePrimary: 0,
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
        SolverDisplayNames displayNames)
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
        PlanChoiceEffect effect = InferChoiceEffect(src.SourceId, sourcePile);
        PlanCardToken[] tokens = src.Cards
            .Select(card => new PlanCardToken(
                card.CardId,
                card.UpgradeLevel,
                string.Empty,
                card.SourceOccurrence,
                card.OptionOccurrence,
                displayNames.Card(card.CardId, card.UpgradeLevel)))
            .ToArray();

        return new PlanCardChoice(
            effect,
            sourcePile,
            tokens,
            src.SourceId ?? string.Empty,
            string.Empty,
            timing);
    }

    private static PlanChoiceEffect InferChoiceEffect(string sourceId, PileType sourcePile)
    {
        if (string.Equals(sourceId, "KNOWLEDGE_DEMON", StringComparison.Ordinal))
            return PlanChoiceEffect.ApplyKnowledgeCurse;
        return sourcePile switch
        {
            PileType.Hand => PlanChoiceEffect.Modify,
            PileType.None => PlanChoiceEffect.GenerateToHand,
            _ => PlanChoiceEffect.MoveToHand,
        };
    }

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
