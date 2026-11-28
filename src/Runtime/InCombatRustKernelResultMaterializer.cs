using CombatSolver.HotKernel;
using CombatSolver.HotSearch;

namespace CombatSolver;

internal static class InCombatRustKernelResultMaterializer
{
    public static bool TryMaterialize(
        CombatRootSnapshot root,
        SolverDisplayNames displayNames,
        BattleDamageSnapshot battleDamage,
        SearchPolicySnapshot policy,
        RustKernelResultDto rustResult,
        int effectiveParallelism,
        TimeSpan totalElapsed,
        out SolverResult? result,
        out string? failureCode,
        out string? failureDetail)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(displayNames);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rustResult);

        result = null;
        failureCode = null;
        failureDetail = null;

        HotRootBundle rootBundle = root.HotRoot;
        HotRules rules = rootBundle.Rules;
        CombatHotState state = rootBundle.RootState;
        HotColdSidecar? cold = rootBundle.RootCold;
        HotExecScratch scratch = new();
        HotExecScratch probeScratch = new();

        if (rustResult.Actions.Count == 0 && state.TerminalOutcome == HotCombatOutcome.Ongoing)
        {
            failureCode = "empty_route";
            failureDetail = "Rust combat kernel returned an empty action trace while combat is ongoing.";
            return false;
        }

        short[] rootPotionCosts = HotPotionPolicy.BuildRootSlotCosts(
            root, in state, rules, policy.PotionStrategy.Thresholds);
        HotSearchPolicy hotPolicy = new(root, battleDamage, policy, rules, in state, rootPotionCosts);

        var hotActions = new HotAction[rustResult.Actions.Count];
        var stepChoices = new HotChoiceSelection[rustResult.Actions.Count][];

        int explicitPotionCount = 0;
        int totalPotionCount = 0;
        int optionalPaidPotionCount = 0;
        int optionalPotionStrategicCost = 0;
        int rawPotionStrategicCost = 0;
        ushort potionMask = 0;
        byte ambergrisCount = 0;

        for (int stepIdx = 0; stepIdx < rustResult.Actions.Count; stepIdx++)
        {
            RustKernelActionTraceDto trace = rustResult.Actions[stepIdx];
            HotStatePacker.CanonicalizeForPack(ref state, ref cold);
            HotParallelExpander.AssignDeterministicCardUids(ref state);

            if (state.TerminalOutcome != HotCombatOutcome.Ongoing)
            {
                failureCode = "replay_after_terminal";
                failureDetail = $"Rust trace step {stepIdx} ({trace.Kind}) occurred after terminal outcome {state.TerminalOutcome}.";
                return false;
            }
            if (state.PlayerTurnNumber != trace.Turn)
            {
                failureCode = "replay_turn_mismatch";
                failureDetail = $"Rust trace step {stepIdx} expected turn {trace.Turn}, HotKernel is on turn {state.PlayerTurnNumber}.";
                return false;
            }

            if (!TryResolveHotAction(in state, cold, rules, trace, stepIdx, out HotAction hotAction, out string? actionErr))
            {
                failureCode = "replay_action_illegal";
                failureDetail = actionErr;
                return false;
            }

            if (!TryResolveStepChoices(
                    in state,
                    cold,
                    rules,
                    probeScratch,
                    hotAction,
                    trace.Choices,
                    stepIdx,
                    out HotChoiceSelection[] resolvedChoices,
                    out string? choiceErr))
            {
                failureCode = "replay_choice_mismatch";
                failureDetail = choiceErr;
                return false;
            }

            int[] potionSlotsBefore = SnapshotPotionSlots(in state);
            ushort generatedMaskBefore = state.CombatGeneratedPotionMask;
            if (hotAction.Kind == HotActionKind.UsePotion)
            {
                explicitPotionCount++;
                totalPotionCount++;
                int slot = hotAction.SlotOrHandIdx;
                ushort pDefId = state.Potions[slot];
                bool isGen = (generatedMaskBefore & (1 << slot)) != 0;
                if (!isGen)
                {
                    potionMask |= (ushort)(1 << slot);
                    if (HotPotionPolicy.ResolvePotionId(rules.Potions[pDefId]) == HotCustomPotionId.Ambergris)
                        ambergrisCount++;
                    bool isForced = (hotPolicy.ForcedPotionSlotMask & (1 << slot)) != 0;
                    short slotCost = (uint)slot < (uint)rootPotionCosts.Length && rootPotionCosts[slot] >= 0
                        ? rootPotionCosts[slot]
                        : HotPotionPolicy.ComputePotionDefCost(
                            rules.Potions[pDefId],
                            hotPolicy.HasRenewablePotionShapedRock,
                            hotPolicy.InitialPlayerMaxHp,
                            hotPolicy.PotionThresholds);
                    rawPotionStrategicCost += slotCost;
                    if (!isForced && slotCost > 0)
                    {
                        optionalPaidPotionCount++;
                        optionalPotionStrategicCost += slotCost;
                    }
                }
            }

            HotStepResult stepRes = HotCombatEngine.Step(ref state, ref cold, rules, scratch, hotAction, resolvedChoices);
            if (!stepRes.Executed || stepRes.NeedsChoice)
            {
                failureCode = "replay_step_failed";
                failureDetail = $"HotCombatEngine.Step failed at step {stepIdx} ({trace.Kind}): executed={stepRes.Executed}, needsChoice={stepRes.NeedsChoice}.";
                return false;
            }

            for (int slot = 0; slot < potionSlotsBefore.Length; slot++)
            {
                if (hotAction.Kind == HotActionKind.UsePotion && slot == hotAction.SlotOrHandIdx)
                    continue;
                int beforeDef = potionSlotsBefore[slot];
                if (beforeDef != 0 && state.Potions[slot] == 0)
                {
                    totalPotionCount++;
                    bool isGen = (generatedMaskBefore & (1 << slot)) != 0;
                    if (!isGen)
                    {
                        potionMask |= (ushort)(1 << slot);
                        short autoCost = (uint)slot < (uint)rootPotionCosts.Length && rootPotionCosts[slot] >= 0
                            ? rootPotionCosts[slot]
                            : HotPotionPolicy.ComputePotionDefCost(
                                rules.Potions[beforeDef],
                                hotPolicy.HasRenewablePotionShapedRock,
                                hotPolicy.InitialPlayerMaxHp,
                                hotPolicy.PotionThresholds);
                        rawPotionStrategicCost += autoCost;
                    }
                }
            }

            hotActions[stepIdx] = hotAction;
            stepChoices[stepIdx] = resolvedChoices;
        }

        HotFastSummary summary = HotEvaluator.ExtractStage1(in state, in hotPolicy);
        if (!VerifyTerminalMatch(in state, in summary, rustResult.Terminal, out string? divergence))
        {
            failureCode = "replay_state_divergence";
            failureDetail = divergence;
            return false;
        }

        bool won = hotActions.Length > 0 && summary.IsVictory;
        bool survives = !summary.IsDefeat && summary.PlayerHp > 0;
        int endMaxHp = Math.Max(1, summary.PlayerMaxHp);
        int postCombatHeal = HotRouteQuality.RankedPostCombatRelicHeal(
            hotPolicy.PostCombatRelicHeal,
            won,
            summary.PlayerHp,
            endMaxHp);
        int netHpDeltaCenti = HotRouteQuality.ComputeNetPersistentHpDeltaCenti(
            endPlayerHp: summary.PlayerHp,
            postCombatMonotoneHeal: postCombatHeal,
            initialPlayerHp: hotPolicy.InitialPlayerHp,
            endPlayerMaxHp: endMaxHp,
            initialPlayerMaxHp: hotPolicy.InitialPlayerMaxHp,
            deathSaveHpRestored: summary.DeathSaveHpRestored,
            growthHpCredit: summary.GrowthHpCredit,
            bossHpRelief: hotPolicy.StrategicBossHpRelief,
            ignoreLongTermRewards: hotPolicy.IgnoreLongTermRewards,
            relicHpCredit: summary.RelicHpCredit,
            relicHealingHpCredit: summary.RelicHealingHpCredit,
            feedMaxHpExcluded: summary.FeedCount * hotPolicy.FeedMaxHpPerKill);

        int netPotionCostCenti = HotPotionPolicy.ComputeNetPotionOpportunityCostCenti(
            optionalPotionStrategicCost,
            optionalPaidPotionCount,
            alchemizeCount: summary.AlchemizeCount,
            endEmptyPotionSlots: summary.EmptyPotionSlots,
            in hotPolicy);

        int strategicDeficitWholeHp = HotRouteQuality.RoundCentiToHp(-netHpDeltaCenti);
        int optionalPotionWholeHp = netPotionCostCenti > 0
            ? Math.Max(1, HotRouteQuality.RoundCentiToHp(netPotionCostCenti))
            : 0;

        HotEvalLookup evalLookup = new(rules);
        HotEvaluator.EvaluateState(
            in state,
            cold,
            rules,
            in hotPolicy,
            in evalLookup,
            in summary,
            netPotionCostCenti,
            hotActions.Length,
            HotEvalMode.Balanced,
            out HotEvalFeatures terminalEval);

        HotRouteKey winningKey = new(
            Won: won,
            Survives: survives,
            DeathSaveUseCount: summary.DeathSaveUseCount,
            OutstandingStolen: summary.OutstandingStolen,
            ForfeitsGold: summary.ForfeitsGold,
            StrategicHpDeficit: strategicDeficitWholeHp,
            OptionalPotionStrategicCost: optionalPotionWholeHp,
            RawPotionStrategicCost: rawPotionStrategicCost,
            GrowthHpCredit: summary.GrowthHpCredit,
            GrowthRewardCount: summary.GrowthRewardCount,
            CombatEndedTurn: won ? summary.Turn : int.MaxValue,
            ProjectedBattleHpLost: hotPolicy.BattleHpLostSoFar + summary.HpLost,
            ProjectedBattlePotionCount: hotPolicy.BattlePotionsUsedSoFar + totalPotionCount,
            ExplicitPotionCount: explicitPotionCount,
            PlayerHp: summary.PlayerHp,
            EnemyHp: summary.EnemyHpSum,
            Score: terminalEval.Score,
            PotionMask: potionMask,
            AmbergrisCount: ambergrisCount)
        {
            HasCentiPrecision = true,
            NetPersistentHpDeltaCenti = netHpDeltaCenti,
            NetPotionOpportunityCostCenti = netPotionCostCenti,
            OptionalPaidPotionCount = (byte)Math.Clamp(optionalPaidPotionCount, 0, (int)byte.MaxValue),
            EndPlayerMaxHp = endMaxHp,
            AlchemizeCount = summary.AlchemizeCount,
            EmptyPotionSlots = summary.EmptyPotionSlots,
            RouteTurn = (byte)Math.Clamp((int)summary.Turn, 0, (int)byte.MaxValue),
            IsEventDefeat = summary.IsEventDefeat,
            RelicHpCredit = summary.RelicHpCredit,
            RelicHealingHpCredit = summary.RelicHealingHpCredit,
            RelicSatisfiedMask = summary.RelicSatisfiedMask,
            RelicSatisfiedPriority = summary.RelicSatisfiedPriority,
            RelicDistance = summary.RelicDistance,
            AllGrowthTargetsSatisfied = !hotPolicy.HasPureGrowthTargets || summary.AllGrowthTargetsSatisfied,
            AllRelicTargetsSatisfied = !hotPolicy.HasRelicTargets || summary.AllRelicTargetsSatisfied,
        };

        SearchBoundaryReason boundary = ParseBoundaryReason(rustResult.Terminal.SearchBoundary);
        if (won || !survives || summary.IsEventDefeat)
            boundary = summary.IsEventDefeat ? SearchBoundaryReason.EventDefeat : SearchBoundaryReason.None;

        int expandedNodes = (int)Math.Clamp(rustResult.ExpandedNodes, 0L, int.MaxValue);
        int transitionCount = (int)Math.Clamp(rustResult.TransitionCount ?? rustResult.ExpandedNodes, 0L, int.MaxValue);
        long choiceBranchesLong = Math.Max(0L, rustResult.ChoiceReplayAttempts ?? 0L);
        int choiceBranches = (int)Math.Clamp(choiceBranchesLong, 0L, int.MaxValue);
        int searchedTurns = Math.Max(1, summary.Turn - root.StartTurnNumber + 1);
        TimeSpan searchElapsed = TimeSpan.FromMilliseconds(Math.Max(0d, rustResult.SearchElapsedMilliseconds));

        var metrics = new HotMaterializeMetrics(
            ExpandedNodes: expandedNodes,
            TotalExpandedNodes: Math.Max(0L, rustResult.ExpandedNodes),
            TransitionCount: transitionCount,
            TotalTransitionCount: Math.Max(0L, rustResult.TransitionCount ?? rustResult.ExpandedNodes),
            ChoiceBranchesEvaluated: choiceBranches,
            TotalChoiceBranchesEvaluated: choiceBranchesLong,
            DuplicateCardBranchesPruned: 0,
            TranspositionBranchesPruned: 0,
            PrimaryIncumbentBranchesPruned: 0,
            PrimaryIncumbentUpdates: 0,
            StandPatProbes: 0,
            EndTurnPrefixCaptures: 0,
            EndTurnPrefixReuses: 0,
            ParallelExpansionWaves: 0,
            ParallelExpansionWorkItems: 0,
            MaxParallelExpansionConcurrency: effectiveParallelism,
            TranspositionCount: 0,
            TranspositionLimitBypasses: 0,
            StandPatCacheCount: 0,
            SearchedTurns: searchedTurns,
            BoundaryReason: boundary,
            OnlyDeathRoutesFound: rustResult.Terminal.OnlyDeathRoutes && !won,
            WorkerAllocatedBytes: 0L,
            TotalWorkerAllocatedBytes: 0L,
            Gen0Collections: 0,
            Gen1Collections: 0,
            Gen2Collections: 0,
            GcPauseDuration: TimeSpan.Zero,
            Elapsed: searchElapsed,
            TotalSearchElapsed: totalElapsed,
            PotionHpSaved: 0,
            PotionHpRequired: optionalPotionWholeHp,
            PotionBranchesRejected: 0);

        SolverResult materialized = HotPlanMaterializer.MaterializeFromSteps(
            root,
            displayNames,
            battleDamage,
            policy,
            rootBundle,
            in hotPolicy,
            rootPotionCosts,
            hotActions,
            stepChoices,
            in winningKey,
            in metrics);
        materialized.SingleSessionSearch = true;
        materialized.IsPreliminary = false;
        materialized.EscalationStage = 1;
        materialized.AssertCompleteTurnOutcomes();

        result = materialized;
        return true;
    }

    private static bool TryResolveHotAction(
        in CombatHotState state,
        HotColdSidecar? cold,
        HotRules rules,
        RustKernelActionTraceDto trace,
        int stepIdx,
        out HotAction action,
        out string? error)
    {
        switch (trace.Kind)
        {
            case "EndTurn":
                action = HotAction.EndTurn();
                error = null;
                return true;

            case "PlayCard":
            {
                int seenSameEntry = 0;
                int matchedHandIdx = -1;
                for (int handIdx = 0; handIdx < state.HandCount; handIdx++)
                {
                    ref readonly HotCard card = ref state.Cards[handIdx];
                    string entry = rules.Cards[card.CardDefId].Entry;
                    if (!string.Equals(entry, trace.CardId, StringComparison.Ordinal))
                        continue;
                    if (seenSameEntry == trace.CardOccurrence)
                    {
                        matchedHandIdx = handIdx;
                        break;
                    }
                    seenSameEntry++;
                }
                if (matchedHandIdx < 0)
                {
                    action = default;
                    error = $"Step {stepIdx} PlayCard '{trace.CardId}'#{trace.CardOccurrence} not found in HotKernel hand (count={state.HandCount}).";
                    return false;
                }

                ref readonly HotCard found = ref state.Cards[matchedHandIdx];
                if (found.UpgradeLevel != trace.CardUpgradeLevel)
                {
                    action = default;
                    error = $"Step {stepIdx} PlayCard '{trace.CardId}' upgrade mismatch: Rust={trace.CardUpgradeLevel}, HotKernel={found.UpgradeLevel}.";
                    return false;
                }

                string enchEntry = found.EnchId > 0 && found.EnchId < rules.Enchantments.Length
                    ? rules.Enchantments[found.EnchId].Entry
                    : string.Empty;
                if (!string.Equals(enchEntry, trace.CardEnchantmentId ?? string.Empty, StringComparison.Ordinal))
                {
                    action = default;
                    error = $"Step {stepIdx} PlayCard '{trace.CardId}' enchantment mismatch: Rust='{trace.CardEnchantmentId}', HotKernel='{enchEntry}'.";
                    return false;
                }

                int targetSlot = ResolveTargetEnemySlot(in state, trace.TargetCombatId, trace.TargetIndex);
                if (!HotCombatEngine.CanPlayCard(in state, cold, rules, matchedHandIdx, targetSlot))
                {
                    action = default;
                    error = $"Step {stepIdx} PlayCard '{trace.CardId}'@{matchedHandIdx} target={targetSlot} is illegal in HotKernel.";
                    return false;
                }

                action = HotAction.PlayCard(matchedHandIdx, targetSlot);
                error = null;
                return true;
            }

            case "UsePotion":
            {
                int slot = trace.PotionSlot;
                if (slot < 0 || slot >= state.PotionSlotCount || slot >= HotLimits.MaxPotions)
                {
                    action = default;
                    error = $"Step {stepIdx} UsePotion slot {slot} out of bounds.";
                    return false;
                }
                ushort pDefId = state.Potions[slot];
                if (pDefId == 0 || pDefId >= rules.Potions.Length)
                {
                    action = default;
                    error = $"Step {stepIdx} UsePotion slot {slot} is empty in HotKernel.";
                    return false;
                }
                string pEntry = rules.Potions[pDefId].Entry;
                if (!string.Equals(pEntry, trace.PotionId, StringComparison.Ordinal))
                {
                    action = default;
                    error = $"Step {stepIdx} UsePotion slot {slot} ID mismatch: Rust='{trace.PotionId}', HotKernel='{pEntry}'.";
                    return false;
                }

                int targetSlot = ResolveTargetEnemySlot(in state, trace.TargetCombatId, trace.TargetIndex);
                if (!HotCombatEngine.CanUsePotion(in state, rules, slot, targetSlot))
                {
                    action = default;
                    error = $"Step {stepIdx} UsePotion '{trace.PotionId}'@{slot} target={targetSlot} is illegal in HotKernel.";
                    return false;
                }

                action = HotAction.UsePotion(slot, targetSlot);
                error = null;
                return true;
            }

            default:
                action = default;
                error = $"Step {stepIdx} unknown Rust action kind '{trace.Kind}'.";
                return false;
        }
    }

    private static bool TryResolveStepChoices(
        in CombatHotState stateBefore,
        HotColdSidecar? coldBefore,
        HotRules rules,
        HotExecScratch probeScratch,
        HotAction action,
        IReadOnlyList<RustKernelChoiceTraceDto> traceChoices,
        int stepIdx,
        out HotChoiceSelection[] resolved,
        out string? error)
    {
        if (traceChoices.Count == 0)
        {
            CombatHotState quickProbe = stateBefore;
            HotColdSidecar? quickCold = coldBefore;
            HotStepResult quickRes = HotCombatEngine.Step(ref quickProbe, ref quickCold, rules, probeScratch, action);
            if (quickRes.NeedsChoice)
            {
                if (probeScratch.PendingChoiceMin == 0
                    || TryPickForcedPileChoice(in quickProbe, rules, probeScratch, out _))
                {
                    var optList = new List<HotChoiceSelection>(4);
                    while (true)
                    {
                        CombatHotState optProbe = stateBefore;
                        HotColdSidecar? optCold = coldBefore;
                        HotStepResult optRes = HotCombatEngine.Step(
                            ref optProbe, ref optCold, rules, probeScratch, action, optList.ToArray());
                        if (!optRes.NeedsChoice)
                        {
                            if (!optRes.Executed)
                            {
                                resolved = [];
                                error = $"Step {stepIdx} optional no-choice probe failed to execute.";
                                return false;
                            }
                            resolved = optList.ToArray();
                            error = null;
                            return true;
                        }
                        if (optList.Count >= HotLimits.MaxChoicesPerAction)
                        {
                            resolved = [];
                            error = $"Step {stepIdx} HotKernel required mandatory choice '{probeScratch.PendingChoiceKind}' missing from Rust trace.";
                            return false;
                        }
                        if (probeScratch.PendingChoiceMin == 0)
                        {
                            optList.Add(new HotChoiceSelection(HotChoiceKind.None, 0, 0));
                        }
                        else if (TryPickForcedPileChoice(in optProbe, rules, probeScratch, out HotChoiceSelection forced))
                        {
                            optList.Add(forced);
                        }
                        else
                        {
                            resolved = [];
                            error = $"Step {stepIdx} HotKernel required mandatory choice '{probeScratch.PendingChoiceKind}' missing from Rust trace.";
                            return false;
                        }
                    }
                }

                resolved = [];
                error = $"Step {stepIdx} HotKernel requested choice '{probeScratch.PendingChoiceKind}' missing from Rust trace.";
                return false;
            }

            resolved = [];
            error = null;
            return true;
        }

        var selections = new List<HotChoiceSelection>(traceChoices.Count * 2);
        int promptIdx = 0;
        int cardInPromptIdx = 0;

        for (int guard = 0; guard <= HotLimits.MaxChoicesPerAction; guard++)
        {
            CombatHotState probeState = stateBefore;
            HotColdSidecar? probeCold = coldBefore;
            HotStepResult res = HotCombatEngine.Step(
                ref probeState, ref probeCold, rules, probeScratch, action, selections.ToArray());

            if (!res.NeedsChoice)
            {
                if (!res.Executed)
                {
                    resolved = [];
                    error = $"Step {stepIdx} choice replay did not execute action.";
                    return false;
                }
                while (promptIdx < traceChoices.Count && traceChoices[promptIdx].Cards.Count == 0)
                    promptIdx++;
                if (promptIdx < traceChoices.Count)
                {
                    resolved = [];
                    error = $"Step {stepIdx} left unconsumed Rust choice prompt {promptIdx}/{traceChoices.Count} ('{traceChoices[promptIdx].SourceId}').";
                    return false;
                }

                resolved = selections.ToArray();
                error = null;
                return true;
            }

            if (guard == HotLimits.MaxChoicesPerAction)
                break;

            if (promptIdx >= traceChoices.Count)
            {
                if (probeScratch.PendingChoiceMin == 0)
                {
                    selections.Add(new HotChoiceSelection(HotChoiceKind.None, 0, 0));
                    continue;
                }
                if (TryPickForcedPileChoice(in probeState, rules, probeScratch, out HotChoiceSelection forcedTail))
                {
                    selections.Add(forcedTail);
                    continue;
                }
                resolved = [];
                error = $"Step {stepIdx} exhausted Rust choice prompts while HotKernel requested '{probeScratch.PendingChoiceKind}'.";
                return false;
            }

            RustKernelChoiceTraceDto currentPrompt = traceChoices[promptIdx];
            if (currentPrompt.Cards.Count == 0)
            {
                if (probeScratch.PendingChoiceMin == 0)
                {
                    selections.Add(new HotChoiceSelection(HotChoiceKind.None, 0, 0));
                    promptIdx++;
                    cardInPromptIdx = 0;
                    continue;
                }
                resolved = [];
                error = $"Step {stepIdx} Rust prompt '{currentPrompt.SourceId}' was empty for mandatory HotKernel choice.";
                return false;
            }

            if (cardInPromptIdx >= currentPrompt.Cards.Count)
            {
                if (probeScratch.PendingChoiceMin == 0)
                {
                    selections.Add(new HotChoiceSelection(HotChoiceKind.None, 0, 0));
                    promptIdx++;
                    cardInPromptIdx = 0;
                    continue;
                }
                promptIdx++;
                cardInPromptIdx = 0;
                if (promptIdx >= traceChoices.Count)
                {
                    if (TryPickForcedPileChoice(in probeState, rules, probeScratch, out HotChoiceSelection forcedAfterPrompt))
                    {
                        selections.Add(forcedAfterPrompt);
                        continue;
                    }
                    resolved = [];
                    error = $"Step {stepIdx} ran out of Rust choices for HotKernel prompt '{probeScratch.PendingChoiceKind}'.";
                    return false;
                }
                currentPrompt = traceChoices[promptIdx];
                if (currentPrompt.Cards.Count == 0)
                {
                    if (probeScratch.PendingChoiceMin == 0)
                    {
                        selections.Add(new HotChoiceSelection(HotChoiceKind.None, 0, 0));
                        promptIdx++;
                        continue;
                    }
                    resolved = [];
                    error = $"Step {stepIdx} Rust prompt '{currentPrompt.SourceId}' was empty for mandatory HotKernel choice.";
                    return false;
                }
            }

            RustKernelChoiceCardTokenDto token = currentPrompt.Cards[cardInPromptIdx];
            HotChoiceKind pendingKind = probeScratch.PendingChoiceKind;
            if (!TryMatchChoiceToken(in probeState, rules, probeScratch, pendingKind, token, out HotChoiceSelection matchedSelection, out string? matchErr))
            {
                if (cardInPromptIdx == 0 && TryPickForcedPileChoice(in probeState, rules, probeScratch, out HotChoiceSelection forcedMid))
                {
                    selections.Add(forcedMid);
                    continue;
                }
                resolved = [];
                error = $"Step {stepIdx} prompt {promptIdx}[{cardInPromptIdx}]: {matchErr}";
                return false;
            }

            selections.Add(matchedSelection);
            cardInPromptIdx++;
            bool reachedPromptCap = probeScratch.PendingChoiceMax > 0
                && (probeScratch.PendingChoiceMax == 1 || cardInPromptIdx >= probeScratch.PendingChoiceMax);
            if (cardInPromptIdx >= currentPrompt.Cards.Count
                && (probeScratch.PendingChoiceMin > 0 || reachedPromptCap))
            {
                promptIdx++;
                cardInPromptIdx = 0;
            }
        }

        resolved = [];
        error = $"Step {stepIdx} exceeded MaxChoicesPerAction ({HotLimits.MaxChoicesPerAction}).";
        return false;
    }

    private static bool TryPickForcedPileChoice(
        in CombatHotState probeState,
        HotRules rules,
        HotExecScratch probeScratch,
        out HotChoiceSelection selection)
    {
        if (probeScratch.PendingChoiceMin == 0)
        {
            selection = default;
            return false;
        }

        HotPileKind? maybePile = probeScratch.PendingChoiceKind switch
        {
            HotChoiceKind.HandCard => HotPileKind.Hand,
            HotChoiceKind.DrawPile => HotPileKind.Draw,
            HotChoiceKind.DiscardPile => HotPileKind.Discard,
            HotChoiceKind.ExhaustPile => HotPileKind.Exhaust,
            _ => null,
        };
        if (maybePile is not { } pile)
        {
            selection = default;
            return false;
        }

        int zoneStart = HotPiles.ZoneStart(in probeState, pile);
        int zoneCount = HotPiles.ZoneCount(in probeState, pile);
        ushort sourceDefId = probeScratch.PendingChoiceSourceCardDefId;
        HotCustomCardId filterSource = sourceDefId > 0 && sourceDefId < rules.Cards.Length
            ? rules.Cards[sourceDefId].CardId
            : HotCustomCardId.None;
        if (filterSource == HotCustomCardId.KiChant && pile == HotPileKind.Draw)
        {
            int topLimit = probeScratch.PendingChoiceMax > 0 ? probeScratch.PendingChoiceMax : 3;
            if (zoneCount > topLimit)
                zoneCount = topLimit;
        }

        ReadOnlySpan<ushort> allowedUids = HotCombatEngine.GetPendingFilteredChoiceUids();
        ReadOnlySpan<ushort> excludedUids = HotCombatEngine.GetPendingExcludedChoiceUids();

        int eligibleCount = 0;
        ushort firstUid = 0;
        ushort firstDefId = 0;
        for (int i = 0; i < zoneCount; i++)
        {
            ref readonly HotCard card = ref probeState.Cards[zoneStart + i];
            bool isAllowed = (allowedUids.IsEmpty || ContainsUid(allowedUids, card.CardUid))
                && (excludedUids.IsEmpty || !ContainsUid(excludedUids, card.CardUid))
                && HotCombatEngine.MatchesChoiceFilter(in probeState, in card, rules, filterSource);
            if (!isAllowed)
                continue;
            if (eligibleCount == 0)
            {
                firstUid = card.CardUid;
                firstDefId = card.CardDefId;
            }
            eligibleCount++;
            if (eligibleCount > probeScratch.PendingChoiceMin)
            {
                selection = default;
                return false;
            }
        }

        if (eligibleCount > 0 && eligibleCount <= probeScratch.PendingChoiceMin)
        {
            selection = new HotChoiceSelection(probeScratch.PendingChoiceKind, firstUid, firstDefId);
            return true;
        }

        selection = default;
        return false;
    }

    private static bool TryMatchChoiceToken(
        in CombatHotState probeState,
        HotRules rules,
        HotExecScratch probeScratch,
        HotChoiceKind pendingKind,
        RustKernelChoiceCardTokenDto token,
        out HotChoiceSelection selection,
        out string? error)
    {
        if (pendingKind == HotChoiceKind.GeneratedCard)
        {
            ReadOnlySpan<ushort> genOpts = HotCombatEngine.GetPendingGeneratedChoiceOptions();
            int seenMatch = 0;
            for (int i = 0; i < genOpts.Length; i++)
            {
                ushort defId = genOpts[i];
                if (defId == 0 || defId >= rules.Cards.Length)
                    continue;
                if (!string.Equals(rules.Cards[defId].Entry, token.CardId, StringComparison.Ordinal))
                    continue;
                if (seenMatch == token.OptionOccurrence)
                {
                    selection = new HotChoiceSelection(HotChoiceKind.GeneratedCard, (ushort)i, defId);
                    error = null;
                    return true;
                }
                seenMatch++;
            }
            for (int i = 0; i < genOpts.Length; i++)
            {
                ushort defId = genOpts[i];
                if (defId != 0 && defId < rules.Cards.Length
                    && string.Equals(rules.Cards[defId].Entry, token.CardId, StringComparison.Ordinal))
                {
                    selection = new HotChoiceSelection(HotChoiceKind.GeneratedCard, (ushort)i, defId);
                    error = null;
                    return true;
                }
            }
            selection = default;
            error = $"Generated card '{token.CardId}' not found among {genOpts.Length} options.";
            return false;
        }

        HotPileKind? maybePile = pendingKind switch
        {
            HotChoiceKind.HandCard => HotPileKind.Hand,
            HotChoiceKind.DrawPile => HotPileKind.Draw,
            HotChoiceKind.DiscardPile => HotPileKind.Discard,
            HotChoiceKind.ExhaustPile => HotPileKind.Exhaust,
            _ => null,
        };
        if (maybePile is not { } pile)
        {
            selection = default;
            error = $"Unsupported HotChoiceKind '{pendingKind}'.";
            return false;
        }

        ReadOnlySpan<ushort> allowedUids = HotCombatEngine.GetPendingFilteredChoiceUids();
        ReadOnlySpan<ushort> excludedUids = HotCombatEngine.GetPendingExcludedChoiceUids();

        int zoneStart = HotPiles.ZoneStart(in probeState, pile);
        int zoneCount = HotPiles.ZoneCount(in probeState, pile);
        ushort sourceDefId = probeScratch.PendingChoiceSourceCardDefId;
        HotCustomCardId filterSource = sourceDefId > 0 && sourceDefId < rules.Cards.Length
            ? rules.Cards[sourceDefId].CardId
            : HotCustomCardId.None;
        if (filterSource == HotCustomCardId.KiChant && pile == HotPileKind.Draw)
        {
            int topLimit = probeScratch.PendingChoiceMax > 0 ? probeScratch.PendingChoiceMax : 3;
            if (zoneCount > topLimit)
                zoneCount = topLimit;
        }
        int sourceOcc = 0;
        int filteredOcc = 0;
        ushort fallbackUid = 0;
        ushort fallbackDefId = 0;

        for (int i = 0; i < zoneCount; i++)
        {
            ref readonly HotCard card = ref probeState.Cards[zoneStart + i];
            string entry = rules.Cards[card.CardDefId].Entry;
            bool idAndUpgradeMatch = string.Equals(entry, token.CardId, StringComparison.Ordinal)
                && card.UpgradeLevel == token.UpgradeLevel;
            bool isAllowed = (allowedUids.IsEmpty || ContainsUid(allowedUids, card.CardUid))
                && (excludedUids.IsEmpty || !ContainsUid(excludedUids, card.CardUid))
                && HotCombatEngine.MatchesChoiceFilter(in probeState, in card, rules, filterSource);

            if (idAndUpgradeMatch)
            {
                if (token.SourceOccurrence >= 0 && sourceOcc == token.SourceOccurrence && isAllowed)
                {
                    selection = new HotChoiceSelection(pendingKind, card.CardUid, card.CardDefId);
                    error = null;
                    return true;
                }
                if (isAllowed)
                {
                    if (token.SourceOccurrence < 0 && filteredOcc == token.OptionOccurrence)
                    {
                        selection = new HotChoiceSelection(pendingKind, card.CardUid, card.CardDefId);
                        error = null;
                        return true;
                    }
                    if (fallbackUid == 0)
                    {
                        fallbackUid = card.CardUid;
                        fallbackDefId = card.CardDefId;
                    }
                    filteredOcc++;
                }
                sourceOcc++;
            }
        }

        if (fallbackUid != 0)
        {
            selection = new HotChoiceSelection(pendingKind, fallbackUid, fallbackDefId);
            error = null;
            return true;
        }

        selection = default;
        error = $"Choice card '{token.CardId}+{token.UpgradeLevel}' (srcOcc={token.SourceOccurrence}, optOcc={token.OptionOccurrence}) not found in {pile}.";
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

    private static int ResolveTargetEnemySlot(in CombatHotState state, uint? targetCombatId, int targetIndex)
    {
        if (targetCombatId is uint cid)
        {
            for (int r = 0; r < state.RosterCount; r++)
            {
                byte slot = state.ActiveEnemyRoster[r];
                if (state.Enemies[slot].Vital.CombatId == cid)
                    return slot;
            }
        }
        if (targetIndex >= 0 && targetIndex < state.RosterCount)
            return state.ActiveEnemyRoster[targetIndex];
        return -1;
    }

    private static int[] SnapshotPotionSlots(in CombatHotState s)
    {
        int count = Math.Min((int)s.PotionSlotCount, HotLimits.MaxPotions);
        int[] snap = new int[count];
        for (int i = 0; i < count; i++)
            snap[i] = s.Potions[i];
        return snap;
    }

    private static bool VerifyTerminalMatch(
        in CombatHotState state,
        in HotFastSummary summary,
        RustKernelTerminalSnapshotDto terminal,
        out string? divergence)
    {
        bool replayedDead = summary.IsDefeat || state.Player.CurrentHp <= 0;
        bool replayedWon = summary.IsVictory && !replayedDead;

        if (replayedDead != terminal.PlayerDead)
        {
            divergence = $"PlayerDead mismatch: Rust={terminal.PlayerDead}, HotKernel={replayedDead}.";
            return false;
        }
        if (replayedWon != terminal.AllEnemiesDead)
        {
            divergence = $"AllEnemiesDead mismatch: Rust={terminal.AllEnemiesDead}, HotKernel={replayedWon}.";
            return false;
        }
        if (summary.PlayerHp != terminal.PlayerHp)
        {
            divergence = $"PlayerHp mismatch: Rust={terminal.PlayerHp}, HotKernel={summary.PlayerHp}.";
            return false;
        }
        if (summary.PlayerMaxHp != terminal.PlayerMaxHp)
        {
            divergence = $"PlayerMaxHp mismatch: Rust={terminal.PlayerMaxHp}, HotKernel={summary.PlayerMaxHp}.";
            return false;
        }
        if (summary.PlayerBlock != terminal.PlayerBlock)
        {
            divergence = $"PlayerBlock mismatch: Rust={terminal.PlayerBlock}, HotKernel={summary.PlayerBlock}.";
            return false;
        }
        if (summary.EnemyHpSum != terminal.EnemyHp)
        {
            divergence = $"EnemyHp mismatch: Rust={terminal.EnemyHp}, HotKernel={summary.EnemyHpSum}.";
            return false;
        }
        if (summary.AliveEnemyCount != terminal.AliveEnemyCount)
        {
            divergence = $"AliveEnemyCount mismatch: Rust={terminal.AliveEnemyCount}, HotKernel={summary.AliveEnemyCount}.";
            return false;
        }
        if (state.PlayerTurnNumber != terminal.Turn)
        {
            divergence = $"Turn mismatch: Rust={terminal.Turn}, HotKernel={state.PlayerTurnNumber}.";
            return false;
        }

        divergence = null;
        return true;
    }

    private static SearchBoundaryReason ParseBoundaryReason(string? boundary)
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
            _ => SearchBoundaryReason.NodeLimit,
        };
}
