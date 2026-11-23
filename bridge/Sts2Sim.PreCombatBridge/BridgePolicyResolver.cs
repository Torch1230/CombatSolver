using System.Diagnostics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;
using CombatSolver.Beam;
using CombatSolver.Beam.Policy;

namespace Sts2Sim.PreCombatBridge;

internal static class BridgePolicyResolver
{
    public static (CombatPolicy BasePolicy, BeamBudget Budget, PreCombatPortfolioOptions Portfolio)
        ValidateAndBuildBasePolicy(
            BridgePolicyDto dto,
            int searchBudgetMilliseconds,
            int overallTimeoutMilliseconds,
            int maxDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return ValidateAndBuildCore(
            dto.PotionPolicy,
            dto.PotionDirectives,
            dto.PredictPotionReward,
            dto.RelicStrategyEnabled,
            dto.RelicCounterRules,
            dto.GrowthBudgets,
            dto.BrightestFlameMaxHpLossLimit,
            dto.IgnoreLongTermRewards,
            dto.ActTransitionBossHpStrategy,
            dto.FinalBossHpStrategy,
            dto.AcceptableBattleHpLoss,
            dto.StopAtAcceptableBattleHpLoss,
            dto.UseBeamWidthPortfolio,
            dto.UseNoveltyPortfolio,
            dto.UseEarlyTurnExploration,
            dto.BeamWidth,
            potionFreeBeamWidth: null,
            potionBeamWidth: null,
            dto.MaxExpandedNodes,
            dto.MaxCardBranchesPerNode,
            dto.MaxPileChoiceBranchesPerAction,
            dto.MaxHandChoiceBranchesPerAction,
            dto.SoftTimeBudgetMilliseconds,
            theftPolicyName: null,
            dto.PortfolioOptionsVersion,
            dto.BeamWidthPortfolioWidths,
            dto.BeamWidthPortfolioPlainBaselineMember,
            dto.ReallocatedRefinementPortfolio,
            dto.OffensiveRefinementPortfolio,
            dto.BoundedOffensiveRefinementPortfolio,
            dto.StopPortfolioAtHpTarget,
            dto.AdaptiveNoveltyRefinement,
            dto.NoveltySearch,
            dto.NoveltyBudget,
            dto.EarlyTurnExplorationDepth,
            dto.EarlyTurnExplorationBudgetMilliseconds,
            dto.PortfolioSchedulingMode,
            searchBudgetMilliseconds,
            overallTimeoutMilliseconds,
            maxDegreeOfParallelism,
            maxSoftTimeMs: int.MaxValue);
    }

    public static (CombatPolicy BasePolicy, BeamBudget Budget, PreCombatPortfolioOptions Portfolio)
        ValidateAndBuildInCombatPolicy(
            InCombatPolicyDto dto,
            int searchBudgetMilliseconds,
            int overallTimeoutMilliseconds,
            int maxDegreeOfParallelism)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (dto.SoftTimeBudgetMilliseconds > InCombatBridgeAbi.MaxSoftTimeBudgetMilliseconds)
        {
            throw new BridgeRequestFailedException(
                "policy.softTimeBudgetMilliseconds",
                $"SoftTimeBudgetMilliseconds {dto.SoftTimeBudgetMilliseconds} exceeds {InCombatBridgeAbi.MaxSoftTimeBudgetMilliseconds}.");
        }
        return ValidateAndBuildCore(
            dto.PotionPolicy,
            dto.PotionDirectives,
            dto.PredictPotionReward,
            dto.RelicStrategyEnabled,
            dto.RelicCounterRules,
            dto.GrowthBudgets,
            dto.BrightestFlameMaxHpLossLimit,
            dto.IgnoreLongTermRewards,
            dto.ActTransitionBossHpStrategy,
            dto.FinalBossHpStrategy,
            dto.AcceptableBattleHpLoss,
            dto.StopAtAcceptableBattleHpLoss,
            dto.UseBeamWidthPortfolio,
            dto.UseNoveltyPortfolio,
            dto.UseEarlyTurnExploration,
            dto.BeamWidth,
            dto.PotionFreeBeamWidth,
            dto.PotionBeamWidth,
            dto.MaxExpandedNodes,
            dto.MaxCardBranchesPerNode,
            dto.MaxPileChoiceBranchesPerAction,
            dto.MaxHandChoiceBranchesPerAction,
            dto.SoftTimeBudgetMilliseconds,
            dto.TheftPolicy,
            dto.PortfolioOptionsVersion,
            dto.BeamWidthPortfolioWidths,
            dto.BeamWidthPortfolioPlainBaselineMember,
            dto.ReallocatedRefinementPortfolio,
            dto.OffensiveRefinementPortfolio,
            dto.BoundedOffensiveRefinementPortfolio,
            dto.StopPortfolioAtHpTarget,
            dto.AdaptiveNoveltyRefinement,
            dto.NoveltySearch,
            dto.NoveltyBudget,
            dto.EarlyTurnExplorationDepth,
            dto.EarlyTurnExplorationBudgetMilliseconds,
            dto.PortfolioSchedulingMode,
            searchBudgetMilliseconds,
            overallTimeoutMilliseconds,
            maxDegreeOfParallelism,
            maxSoftTimeMs: InCombatBridgeAbi.MaxSoftTimeBudgetMilliseconds);
    }

    private static (CombatPolicy BasePolicy, BeamBudget Budget, PreCombatPortfolioOptions Portfolio)
        ValidateAndBuildCore(
            string potionPolicyName,
            IReadOnlyList<BridgePotionDirectiveDto> rawPotionDirectives,
            bool predictPotionReward,
            bool relicStrategyEnabled,
            IReadOnlyList<BridgeRelicCounterRuleDto> rawRelicCounterRules,
            BridgeGrowthBudgetsDto? growthDto,
            int? brightestFlameMaxHpLossLimit,
            bool ignoreLongTermRewards,
            string actTransitionBossHpStrategyName,
            string finalBossHpStrategyName,
            int acceptableBattleHpLoss,
            bool stopAtAcceptableBattleHpLoss,
            bool useBeamWidthPortfolio,
            bool useNoveltyPortfolio,
            bool useEarlyTurnExploration,
            int beamWidth,
            int? potionFreeBeamWidth,
            int? potionBeamWidth,
            int maxExpandedNodes,
            int maxCardBranchesPerNode,
            int maxPileChoiceBranchesPerAction,
            int maxHandChoiceBranchesPerAction,
            int softTimeBudgetMilliseconds,
            string? theftPolicyName,
            int? portfolioOptionsVersion,
            IReadOnlyList<int>? beamWidthPortfolioWidths,
            bool? beamWidthPortfolioPlainBaselineMember,
            bool? reallocatedRefinementPortfolio,
            bool? offensiveRefinementPortfolio,
            bool? boundedOffensiveRefinementPortfolio,
            bool? stopPortfolioAtHpTarget,
            bool? adaptiveNoveltyRefinement,
            BridgeNoveltySearchOptionsDto? noveltySearch,
            BridgeNoveltyPortfolioBudgetDto? noveltyBudget,
            int? earlyTurnExplorationDepth,
            int? earlyTurnExplorationBudgetMilliseconds,
            string? portfolioSchedulingMode,
            int searchBudgetMilliseconds,
            int overallTimeoutMilliseconds,
            int maxDegreeOfParallelism,
            int maxSoftTimeMs)
    {
        if (searchBudgetMilliseconds < 1)
            throw new BridgeRequestFailedException("searchBudgetMilliseconds", "SearchBudgetMilliseconds must be at least 1.");
        if (overallTimeoutMilliseconds < 1)
            throw new BridgeRequestFailedException("overallTimeoutMilliseconds", "OverallTimeoutMilliseconds must be at least 1.");
        if (maxDegreeOfParallelism < 1)
            throw new BridgeRequestFailedException("maxDegreeOfParallelism", "MaxDegreeOfParallelism must be at least 1.");

        if (portfolioOptionsVersion is not null && portfolioOptionsVersion is not (1 or 2))
        {
            throw new BridgeRequestFailedException(
                "policy.portfolioOptionsVersion",
                $"PortfolioOptionsVersion must be 1 or 2 when specified, got {portfolioOptionsVersion}.");
        }
        if (beamWidthPortfolioWidths is not null)
        {
            if (beamWidthPortfolioWidths.Count > 16)
            {
                throw new BridgeRequestFailedException(
                    "policy.beamWidthPortfolioWidths",
                    $"BeamWidthPortfolioWidths count {beamWidthPortfolioWidths.Count} exceeds maximum 16.");
            }
            for (int i = 0; i < beamWidthPortfolioWidths.Count; i++)
            {
                if (beamWidthPortfolioWidths[i] < 1)
                {
                    throw new BridgeRequestFailedException(
                        $"policy.beamWidthPortfolioWidths[{i}]",
                        $"BeamWidthPortfolioWidths entries must be >= 1, got {beamWidthPortfolioWidths[i]}.");
                }
            }
        }
        if (offensiveRefinementPortfolio == true && boundedOffensiveRefinementPortfolio == true)
        {
            throw new BridgeRequestFailedException(
                "policy.offensiveRefinementPortfolio",
                "OffensiveRefinementPortfolio and BoundedOffensiveRefinementPortfolio cannot both be enabled.");
        }
        if (noveltySearch is not null
            && (noveltySearch.Width is not (1 or 2)
                || noveltySearch.FamiliarAllowance < 0
                || noveltySearch.MaxOpen < 1
                || noveltySearch.MaxNoveltyEntries < 1
                || noveltySearch.MaxActions < 1
                || noveltySearch.MaxTurns < 1))
        {
            throw new BridgeRequestFailedException(
                "policy.noveltySearch",
                "NoveltySearch options contain out-of-range values.");
        }
        if (noveltyBudget is not null
            && (noveltyBudget.TimeDivisor < 2
                || noveltyBudget.MaximumMilliseconds < 1
                || noveltyBudget.MaximumExpandedNodes < 1))
        {
            throw new BridgeRequestFailedException(
                "policy.noveltyBudget",
                "NoveltyBudget options contain out-of-range values.");
        }
        if (earlyTurnExplorationDepth is < 0 or > 8)
        {
            throw new BridgeRequestFailedException(
                "policy.earlyTurnExplorationDepth",
                $"EarlyTurnExplorationDepth must be between 0 and 8, got {earlyTurnExplorationDepth}.");
        }
        if (earlyTurnExplorationBudgetMilliseconds is < 0)
        {
            throw new BridgeRequestFailedException(
                "policy.earlyTurnExplorationBudgetMilliseconds",
                $"EarlyTurnExplorationBudgetMilliseconds cannot be negative ({earlyTurnExplorationBudgetMilliseconds}).");
        }
        if (earlyTurnExplorationDepth is > 0
            && (earlyTurnExplorationBudgetMilliseconds is null or < 1
                || earlyTurnExplorationBudgetMilliseconds.Value > BridgeAbi.MaxEarlyTurnExplorationBudgetMilliseconds))
        {
            throw new BridgeRequestFailedException(
                "policy.earlyTurnExplorationBudgetMilliseconds",
                $"EarlyTurnExplorationBudgetMilliseconds must be in 1..{BridgeAbi.MaxEarlyTurnExplorationBudgetMilliseconds} when EarlyTurnExplorationDepth > 0.");
        }
        if (portfolioSchedulingMode is not null
            && !string.Equals(portfolioSchedulingMode, "Parallel", StringComparison.Ordinal)
            && !string.Equals(portfolioSchedulingMode, "Sequential", StringComparison.Ordinal))
        {
            throw new BridgeRequestFailedException(
                "policy.portfolioSchedulingMode",
                $"PortfolioSchedulingMode must be 'Parallel' or 'Sequential', got '{portfolioSchedulingMode}'.");
        }

        int effectivePortfolioVersion = portfolioOptionsVersion ?? 1;
        if (effectivePortfolioVersion < 2)
        {
            if (useNoveltyPortfolio)
            {
                throw new BridgeCapabilityMissException(
                    "policy_unsupported",
                    "policy.useNoveltyPortfolio",
                    "UseNoveltyPortfolio requires portfolioOptionsVersion=2.");
            }
            if (adaptiveNoveltyRefinement == true)
            {
                throw new BridgeCapabilityMissException(
                    "policy_unsupported",
                    "policy.adaptiveNoveltyRefinement",
                    "AdaptiveNoveltyRefinement requires portfolioOptionsVersion=2.");
            }
            if (useEarlyTurnExploration || earlyTurnExplorationDepth is > 0)
            {
                throw new BridgeCapabilityMissException(
                    "policy_unsupported",
                    "policy.useEarlyTurnExploration",
                    "UseEarlyTurnExploration requires portfolioOptionsVersion=2.");
            }
            if (beamWidthPortfolioWidths is not null
                || beamWidthPortfolioPlainBaselineMember == false
                || offensiveRefinementPortfolio == true
                || boundedOffensiveRefinementPortfolio == true
                || reallocatedRefinementPortfolio == false
                || stopPortfolioAtHpTarget == false
                || noveltySearch is not null
                || noveltyBudget is not null
                || earlyTurnExplorationBudgetMilliseconds is > 0
                || portfolioSchedulingMode is not null)
            {
                throw new BridgeCapabilityMissException(
                    "policy_unsupported",
                    "policy.portfolioOptionsVersion",
                    "v2 portfolio options require portfolioOptionsVersion=2.");
            }
        }
        if (earlyTurnExplorationDepth is > BridgeAbi.MaxEarlyTurnExplorationDepth)
        {
            throw new BridgeCapabilityMissException(
                "policy_unsupported",
                "policy.earlyTurnExplorationDepth",
                $"EarlyTurnExplorationDepth {earlyTurnExplorationDepth.Value} exceeds supported maximum {BridgeAbi.MaxEarlyTurnExplorationDepth}.");
        }
        if (growthDto is null)
            throw new BridgeRequestFailedException("policy.growthBudgets", "Missing growthBudgets.");
        if (growthDto.ShowOff != 0)
        {
            throw new BridgeCapabilityMissException(
                "policy_unsupported",
                "policy.growthBudgets.showOff",
                "Nonzero ShowOff growth budget is not supported by the Sts2Sim bridge.");
        }
        if (growthDto.ThirdParty is { Count: > 0 } thirdParty &&
            thirdParty.Values.Any(value => value != 0))
        {
            throw new BridgeCapabilityMissException(
                "policy_unsupported",
                "policy.growthBudgets.thirdParty",
                "Nonzero third-party growth budgets are not supported by the Sts2Sim bridge.");
        }

        if (beamWidth < 1 || maxExpandedNodes < 1 || maxCardBranchesPerNode < 1 ||
            maxPileChoiceBranchesPerAction < 1 || maxHandChoiceBranchesPerAction < 1 ||
            potionFreeBeamWidth is < 1 || potionBeamWidth is < 1)
        {
            throw new BridgeRequestFailedException("policy", "Beam search branch and width limits must all be positive.");
        }
        if (acceptableBattleHpLoss < 0 || brightestFlameMaxHpLossLimit < 0)
        {
            throw new BridgeRequestFailedException("policy", "HP loss limits cannot be negative.");
        }

        if (!Enum.TryParse(potionPolicyName, ignoreCase: false, out SolverPotionPolicy potionPolicy) ||
            !Enum.IsDefined(potionPolicy))
        {
            throw new BridgeRequestFailedException("policy.potionPolicy", $"Unknown PotionPolicy '{potionPolicyName}'.");
        }
        if (!Enum.TryParse(actTransitionBossHpStrategyName, ignoreCase: false, out BossHpStrategy actTransitionBossStrategy) ||
            !Enum.IsDefined(actTransitionBossStrategy))
        {
            throw new BridgeRequestFailedException(
                "policy.actTransitionBossHpStrategy",
                $"Unknown ActTransitionBossHpStrategy '{actTransitionBossHpStrategyName}'.");
        }
        if (!Enum.TryParse(finalBossHpStrategyName, ignoreCase: false, out BossHpStrategy finalBossStrategy) ||
            !Enum.IsDefined(finalBossStrategy))
        {
            throw new BridgeRequestFailedException(
                "policy.finalBossHpStrategy",
                $"Unknown FinalBossHpStrategy '{finalBossHpStrategyName}'.");
        }

        SolverTheftPolicy? explicitTheft = null;
        if (!string.IsNullOrEmpty(theftPolicyName))
        {
            if (!Enum.TryParse(theftPolicyName, ignoreCase: false, out SolverTheftPolicy parsedTheft) ||
                !Enum.IsDefined(parsedTheft))
            {
                throw new BridgeRequestFailedException("policy.theftPolicy", $"Unknown TheftPolicy '{theftPolicyName}'.");
            }
            explicitTheft = parsedTheft;
        }

        if (rawPotionDirectives is null)
            throw new BridgeRequestFailedException("policy.potionDirectives", "Missing potionDirectives.");
        var directives = new List<PotionSlotDirective>(rawPotionDirectives.Count);
        for (int i = 0; i < rawPotionDirectives.Count; i++)
        {
            BridgePotionDirectiveDto item = rawPotionDirectives[i];
            if (item.Slot < 0 || string.IsNullOrWhiteSpace(item.PotionId))
            {
                throw new BridgeRequestFailedException(
                    $"policy.potionDirectives[{i}]",
                    "Potion directive has invalid slot or potionId.");
            }
            if (!Enum.TryParse(item.Directive, ignoreCase: false, out SolverPotionDirective directive) ||
                !Enum.IsDefined(directive))
            {
                throw new BridgeRequestFailedException(
                    $"policy.potionDirectives[{i}].directive",
                    $"Unknown potion directive '{item.Directive}'.");
            }
            directives.Add(new PotionSlotDirective(item.Slot, item.PotionId, directive));
        }

        if (rawRelicCounterRules is null)
            throw new BridgeRequestFailedException("policy.relicCounterRules", "Missing relicCounterRules.");
        var counterRules = new List<RelicCounterRule>(rawRelicCounterRules.Count);
        for (int i = 0; i < rawRelicCounterRules.Count; i++)
        {
            BridgeRelicCounterRuleDto item = rawRelicCounterRules[i];
            if (!Enum.TryParse(item.Id, ignoreCase: false, out RelicCounterId counterId) ||
                !Enum.IsDefined(counterId))
            {
                if (item.Enabled)
                {
                    throw new BridgeCapabilityMissException(
                        "policy_unsupported",
                        $"policy.relicCounterRules[{i}].id",
                        $"Relic counter rule '{item.Id}' is not supported by the Sts2Sim bridge.");
                }
                throw new BridgeRequestFailedException(
                    $"policy.relicCounterRules[{i}].id",
                    $"Unknown relic counter ID '{item.Id}'.");
            }
            if (item.Minimum < 0 || item.Maximum < item.Minimum || item.HpAllowance < 0 || item.Priority is < 1 or > 3)
            {
                throw new BridgeRequestFailedException(
                    $"policy.relicCounterRules[{i}]",
                    $"Invalid relic counter rule bounds for '{item.Id}'.");
            }
            counterRules.Add(new RelicCounterRule(
                counterId,
                item.Enabled,
                item.Minimum,
                item.Maximum,
                item.HpAllowance,
                item.Priority));
        }
        if (counterRules.Select(r => r.Id).Distinct().Count() != counterRules.Count)
        {
            throw new BridgeRequestFailedException("policy.relicCounterRules", "Duplicate relic counter rule.");
        }

        int[] growthValues =
        [
            growthDto.HandOfGreed, growthDto.TheHunt, growthDto.Feed, growthDto.Royalties, growthDto.Alchemize,
            growthDto.GeneticAlgorithm, growthDto.TheScythe, growthDto.Goopy, growthDto.ForbiddenGrimoire, growthDto.MadScience,
        ];
        if (growthValues.Any(v => v is < 0 or > 1000))
        {
            throw new BridgeRequestFailedException("policy.growthBudgets", "Growth budgets must be in 0..1000.");
        }

        var growthBudgets = new GrowthBudgets
        {
            HandOfGreed = growthDto.HandOfGreed,
            TheHunt = growthDto.TheHunt,
            Feed = growthDto.Feed,
            Royalties = growthDto.Royalties,
            Alchemize = growthDto.Alchemize,
            GeneticAlgorithm = growthDto.GeneticAlgorithm,
            TheScythe = growthDto.TheScythe,
            Goopy = growthDto.Goopy,
            ForbiddenGrimoire = growthDto.ForbiddenGrimoire,
            MadScience = growthDto.MadScience,
        };

        var potionStrategy = new PotionStrategy
        {
            DefaultPolicy = potionPolicy,
            Directives = directives,
        };

        var policy = new CombatPolicy
        {
            PotionPolicy = potionPolicy,
            PotionStrategy = potionStrategy,
            PotionDirectives = directives,
            PredictPotionReward = predictPotionReward,
            RelicStrategyEnabled = relicStrategyEnabled,
            RelicCounterRules = counterRules,
            GrowthBudgets = growthBudgets,
            BrightestFlameMaxHpLossLimit = brightestFlameMaxHpLossLimit,
            IgnoreLongTermRewards = ignoreLongTermRewards,
            ActTransitionBossHpStrategy = actTransitionBossStrategy,
            FinalBossHpStrategy = finalBossStrategy,
            AcceptableBattleHpLoss = acceptableBattleHpLoss,
            StopAtAcceptableBattleHpLoss = stopAtAcceptableBattleHpLoss,
            TheftPolicy = explicitTheft,
        };

        int boundedSoft = softTimeBudgetMilliseconds > 0
            ? Math.Min(softTimeBudgetMilliseconds, searchBudgetMilliseconds)
            : searchBudgetMilliseconds;
        int effectiveSoftMs = Math.Min(boundedSoft, maxSoftTimeMs);

        var budget = new BeamBudget(
            beamWidth,
            maxExpandedNodes,
            maxCardBranchesPerNode,
            maxPileChoiceBranchesPerAction,
            maxHandChoiceBranchesPerAction,
            TimeSpan.FromMilliseconds(effectiveSoftMs))
        {
            PotionFreeBeamWidth = potionFreeBeamWidth,
            PotionBeamWidth = potionBeamWidth,
        };

        PreCombatPortfolioOptions portfolio = effectivePortfolioVersion >= 2
            ? BuildV2PortfolioOptions(
                useBeamWidthPortfolio,
                effectivePortfolioVersion,
                beamWidthPortfolioWidths,
                beamWidthPortfolioPlainBaselineMember,
                reallocatedRefinementPortfolio,
                offensiveRefinementPortfolio,
                boundedOffensiveRefinementPortfolio,
                stopPortfolioAtHpTarget,
                useNoveltyPortfolio,
                adaptiveNoveltyRefinement,
                noveltySearch,
                noveltyBudget,
                useEarlyTurnExploration,
                earlyTurnExplorationDepth,
                earlyTurnExplorationBudgetMilliseconds,
                portfolioSchedulingMode,
                searchBudgetMilliseconds,
                effectiveSoftMs)
            : new PreCombatPortfolioOptions(
                useBeamWidthPortfolio,
                PortfolioOptionsVersion: 1,
                ReallocatedRefinementPortfolio: false,
                StopPortfolioAtHpTarget: true,
                SchedulingMode: maxDegreeOfParallelism > 1
                    ? PortfolioSchedulingMode.Parallel
                    : PortfolioSchedulingMode.Sequential);

        return (policy, budget, portfolio);
    }

    private static PreCombatPortfolioOptions BuildV2PortfolioOptions(
        bool useBeamWidthPortfolio,
        int portfolioOptionsVersion,
        IReadOnlyList<int>? beamWidthPortfolioWidths,
        bool? beamWidthPortfolioPlainBaselineMember,
        bool? reallocatedRefinementPortfolio,
        bool? offensiveRefinementPortfolio,
        bool? boundedOffensiveRefinementPortfolio,
        bool? stopPortfolioAtHpTarget,
        bool useNoveltyPortfolio,
        bool? adaptiveNoveltyRefinement,
        BridgeNoveltySearchOptionsDto? noveltySearch,
        BridgeNoveltyPortfolioBudgetDto? noveltyBudget,
        bool useEarlyTurnExploration,
        int? earlyTurnExplorationDepth,
        int? earlyTurnExplorationBudgetMilliseconds,
        string? portfolioSchedulingMode,
        int searchBudgetMilliseconds,
        int effectiveSoftMs)
    {
        int resolvedEarlyDepth = earlyTurnExplorationDepth
            ?? (useEarlyTurnExploration ? BridgeAbi.MaxEarlyTurnExplorationDepth : 0);
        bool resolvedUseEarly = resolvedEarlyDepth > 0;
        int resolvedEarlyBudgetMs = resolvedUseEarly
            ? (earlyTurnExplorationBudgetMilliseconds
                ?? Math.Min(
                    BridgeAbi.MaxEarlyTurnExplorationBudgetMilliseconds,
                    Math.Max(searchBudgetMilliseconds, effectiveSoftMs)))
            : 0;
        if (resolvedUseEarly
            && (resolvedEarlyBudgetMs < 1 || resolvedEarlyBudgetMs > BridgeAbi.MaxEarlyTurnExplorationBudgetMilliseconds))
        {
            throw new BridgeRequestFailedException(
                "policy.earlyTurnExplorationBudgetMilliseconds",
                $"EarlyTurnExplorationBudgetMilliseconds must be in 1..{BridgeAbi.MaxEarlyTurnExplorationBudgetMilliseconds}.");
        }

        PortfolioSchedulingMode schedulingMode = string.Equals(portfolioSchedulingMode, "Sequential", StringComparison.Ordinal)
            ? PortfolioSchedulingMode.Sequential
            : PortfolioSchedulingMode.Parallel;

        return new PreCombatPortfolioOptions(
            UseBeamWidthPortfolio: useBeamWidthPortfolio,
            PortfolioOptionsVersion: portfolioOptionsVersion,
            BeamWidthPortfolioWidths: beamWidthPortfolioWidths?.ToArray(),
            BeamWidthPortfolioPlainBaselineMember: beamWidthPortfolioPlainBaselineMember ?? true,
            ReallocatedRefinementPortfolio: reallocatedRefinementPortfolio ?? true,
            OffensiveRefinementPortfolio: offensiveRefinementPortfolio ?? false,
            BoundedOffensiveRefinementPortfolio: boundedOffensiveRefinementPortfolio ?? false,
            StopPortfolioAtHpTarget: stopPortfolioAtHpTarget ?? true,
            UseNoveltyPortfolio: useNoveltyPortfolio,
            AdaptiveNoveltyRefinement: adaptiveNoveltyRefinement ?? false,
            NoveltyWidth: noveltySearch?.Width ?? 2,
            NoveltyFamiliarAllowance: noveltySearch?.FamiliarAllowance ?? 0,
            NoveltyMaxOpen: noveltySearch?.MaxOpen ?? 2048,
            NoveltyMaxEntries: noveltySearch?.MaxNoveltyEntries ?? 1_000_000,
            NoveltyMaxActions: noveltySearch?.MaxActions ?? 128,
            NoveltyMaxTurns: noveltySearch?.MaxTurns ?? 32,
            NoveltyBudgetTimeDivisor: noveltyBudget?.TimeDivisor ?? 2,
            NoveltyBudgetMaximumMilliseconds: noveltyBudget?.MaximumMilliseconds ?? 5_000,
            NoveltyBudgetMaximumExpandedNodes: noveltyBudget?.MaximumExpandedNodes ?? 2_500,
            UseEarlyTurnExploration: resolvedUseEarly,
            EarlyTurnExplorationDepth: resolvedEarlyDepth,
            EarlyTurnExplorationBudgetMilliseconds: resolvedEarlyBudgetMs,
            SchedulingMode: schedulingMode);
    }

    public static CombatPolicy ResolveForCombatRoot(CombatState combatRoot, CombatPolicy basePolicy)
    {
        ArgumentNullException.ThrowIfNull(combatRoot);
        ArgumentNullException.ThrowIfNull(basePolicy);

        bool theftApplicable = TheftLedger.IsApplicable(combatRoot);
        string? encounterId = combatRoot.RunState is RunState concreteRun &&
                              concreteRun.CurrentRoom is CombatRoom combatRoom
            ? combatRoom.Encounter?.IdEntry
            : null;
        bool isAct3Boss = combatRoot.RunState is RunState run &&
                          run.CurrentActIndex == 2 &&
                          (encounterId is "TEST_SUBJECT_BOSS" or "AEONGLASS_BOSS" or "QUEEN_BOSS");

        return basePolicy with
        {
            TheftPolicy = basePolicy.TheftPolicy ?? (theftApplicable ? SolverTheftPolicy.PreserveResources : null),
            Act3BossStrategy = isAct3Boss,
        };
    }
}
