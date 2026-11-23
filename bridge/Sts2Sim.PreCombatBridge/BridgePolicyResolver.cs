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

        if (searchBudgetMilliseconds < 1)
            throw new BridgeRequestFailedException("searchBudgetMilliseconds", "SearchBudgetMilliseconds must be at least 1.");
        if (overallTimeoutMilliseconds < 1)
            throw new BridgeRequestFailedException("overallTimeoutMilliseconds", "OverallTimeoutMilliseconds must be at least 1.");
        if (maxDegreeOfParallelism < 1)
            throw new BridgeRequestFailedException("maxDegreeOfParallelism", "MaxDegreeOfParallelism must be at least 1.");

        // 严格拒绝已激活但尚未在桥接端映射的策略特性。
        if (dto.UseNoveltyPortfolio)
        {
            throw new BridgeCapabilityMissException(
                "policy_unsupported",
                "policy.useNoveltyPortfolio",
                "UseNoveltyPortfolio is active and not supported by the Sts2Sim bridge.");
        }
        if (dto.UseEarlyTurnExploration)
        {
            throw new BridgeCapabilityMissException(
                "policy_unsupported",
                "policy.useEarlyTurnExploration",
                "UseEarlyTurnExploration is active and not supported by the Sts2Sim bridge.");
        }
        if (dto.GrowthBudgets is null)
            throw new BridgeRequestFailedException("policy.growthBudgets", "Missing growthBudgets.");
        if (dto.GrowthBudgets.ShowOff != 0)
        {
            throw new BridgeCapabilityMissException(
                "policy_unsupported",
                "policy.growthBudgets.showOff",
                "Nonzero ShowOff growth budget is not supported by the Sts2Sim bridge.");
        }
        if (dto.GrowthBudgets.ThirdParty is { Count: > 0 } thirdParty &&
            thirdParty.Values.Any(value => value != 0))
        {
            throw new BridgeCapabilityMissException(
                "policy_unsupported",
                "policy.growthBudgets.thirdParty",
                "Nonzero third-party growth budgets are not supported by the Sts2Sim bridge.");
        }

        if (dto.BeamWidth < 1 || dto.MaxExpandedNodes < 1 || dto.MaxCardBranchesPerNode < 1 ||
            dto.MaxPileChoiceBranchesPerAction < 1 || dto.MaxHandChoiceBranchesPerAction < 1)
        {
            throw new BridgeRequestFailedException("policy", "Beam search branch and width limits must all be positive.");
        }
        if (dto.AcceptableBattleHpLoss < 0 || dto.BrightestFlameMaxHpLossLimit < 0)
        {
            throw new BridgeRequestFailedException("policy", "HP loss limits cannot be negative.");
        }

        if (!Enum.TryParse(dto.PotionPolicy, ignoreCase: false, out SolverPotionPolicy potionPolicy) ||
            !Enum.IsDefined(potionPolicy))
        {
            throw new BridgeRequestFailedException("policy.potionPolicy", $"Unknown PotionPolicy '{dto.PotionPolicy}'.");
        }
        if (!Enum.TryParse(dto.ActTransitionBossHpStrategy, ignoreCase: false, out BossHpStrategy actTransitionBossStrategy) ||
            !Enum.IsDefined(actTransitionBossStrategy))
        {
            throw new BridgeRequestFailedException(
                "policy.actTransitionBossHpStrategy",
                $"Unknown ActTransitionBossHpStrategy '{dto.ActTransitionBossHpStrategy}'.");
        }
        if (!Enum.TryParse(dto.FinalBossHpStrategy, ignoreCase: false, out BossHpStrategy finalBossStrategy) ||
            !Enum.IsDefined(finalBossStrategy))
        {
            throw new BridgeRequestFailedException(
                "policy.finalBossHpStrategy",
                $"Unknown FinalBossHpStrategy '{dto.FinalBossHpStrategy}'.");
        }

        if (dto.PotionDirectives is null)
            throw new BridgeRequestFailedException("policy.potionDirectives", "Missing potionDirectives.");
        var directives = new List<PotionSlotDirective>(dto.PotionDirectives.Count);
        for (int i = 0; i < dto.PotionDirectives.Count; i++)
        {
            BridgePotionDirectiveDto item = dto.PotionDirectives[i];
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

        if (dto.RelicCounterRules is null)
            throw new BridgeRequestFailedException("policy.relicCounterRules", "Missing relicCounterRules.");
        var counterRules = new List<RelicCounterRule>(dto.RelicCounterRules.Count);
        for (int i = 0; i < dto.RelicCounterRules.Count; i++)
        {
            BridgeRelicCounterRuleDto item = dto.RelicCounterRules[i];
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

        BridgeGrowthBudgetsDto g = dto.GrowthBudgets;
        int[] growthValues =
        [
            g.HandOfGreed, g.TheHunt, g.Feed, g.Royalties, g.Alchemize,
            g.GeneticAlgorithm, g.TheScythe, g.Goopy, g.ForbiddenGrimoire, g.MadScience,
        ];
        if (growthValues.Any(v => v is < 0 or > 1000))
        {
            throw new BridgeRequestFailedException("policy.growthBudgets", "Growth budgets must be in 0..1000.");
        }

        var growthBudgets = new GrowthBudgets
        {
            HandOfGreed = g.HandOfGreed,
            TheHunt = g.TheHunt,
            Feed = g.Feed,
            Royalties = g.Royalties,
            Alchemize = g.Alchemize,
            GeneticAlgorithm = g.GeneticAlgorithm,
            TheScythe = g.TheScythe,
            Goopy = g.Goopy,
            ForbiddenGrimoire = g.ForbiddenGrimoire,
            MadScience = g.MadScience,
        };

        // Simultaneously populate PotionPolicy, PotionStrategy.DefaultPolicy, and PotionDirectives
        // so Disabled does not silently fall back to Smart.
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
            PredictPotionReward = dto.PredictPotionReward,
            RelicStrategyEnabled = dto.RelicStrategyEnabled,
            RelicCounterRules = counterRules,
            GrowthBudgets = growthBudgets,
            BrightestFlameMaxHpLossLimit = dto.BrightestFlameMaxHpLossLimit,
            IgnoreLongTermRewards = dto.IgnoreLongTermRewards,
            ActTransitionBossHpStrategy = actTransitionBossStrategy,
            FinalBossHpStrategy = finalBossStrategy,
            AcceptableBattleHpLoss = dto.AcceptableBattleHpLoss,
            StopAtAcceptableBattleHpLoss = dto.StopAtAcceptableBattleHpLoss,
        };

        // 遵循请求的 searchBudgetMilliseconds，不用 SearchTimeLimitSeconds 覆盖。
        int effectiveSoftMs = dto.SoftTimeBudgetMilliseconds > 0
            ? Math.Min(dto.SoftTimeBudgetMilliseconds, searchBudgetMilliseconds)
            : searchBudgetMilliseconds;

        var budget = new BeamBudget(
            dto.BeamWidth,
            dto.MaxExpandedNodes,
            dto.MaxCardBranchesPerNode,
            dto.MaxPileChoiceBranchesPerAction,
            dto.MaxHandChoiceBranchesPerAction,
            TimeSpan.FromMilliseconds(effectiveSoftMs));

        return (policy, budget, new PreCombatPortfolioOptions(dto.UseBeamWidthPortfolio));
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
            TheftPolicy = theftApplicable ? SolverTheftPolicy.PreserveResources : null,
            Act3BossStrategy = isAct3Boss,
        };
    }
}
