using System.Buffers;
using System.Collections.Concurrent;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Powers;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Hooks;
using Sts2Sim.Core.MonsterMoves;
using Sts2Sim.Core.MonsterMoves.Intents;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Afflictions;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Dread;
using Sts2Sim.Core.Models.Potions;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.ValueProps;
using CombatSolver;
using CombatSolver.Beam.Novelty;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

/// <summary>Root-level policy operands captured before <see cref="CombatState.Clone()"/> replaces <see cref="Sts2Sim.Core.Runs.RunState"/> with a combat snapshot.</summary>
internal readonly record struct PolicyRootOperands(
    CombatSolver.Beam.Policy.BossHpRelief BossHpRelief,
    CombatSolver.Beam.Policy.PotionRewardOutlook PotionReward,
    int TotalFloor = 0)
{
    public static PolicyRootOperands Capture(CombatState root, CombatPolicy policy) =>
        new(
            BossHpPolicy.Resolve(root, policy),
            policy.PredictPotionReward && !policy.IgnoreLongTermRewards
                ? CombatSolver.Beam.Policy.PotionRewardOutlook.Capture(root)
                : default,
            root.RunState.TotalFloor);
}

internal sealed record StateEvaluatorBaseline(
    CombatPolicy Policy,
    (int Slot, string Id)[] ForcedPotions,
    CombatSolver.Beam.Policy.PotionRewardOutlook PotionReward,
    int InitialPotionCount,
    int InitialCumulativeHpLost,
    int InitialHp,
    int InitialMaxHp,
    int UnconditionalPostCombatHeal,
    bool CanCertifyRemainingHealing,
    int CertifiedMaxPostCombatHeal,
    bool HasVisibleHealingSource,
    bool HasReachableRegisteredPower,
    int TotalFloor,
    int RootTurnNumber,
    bool TheftApplicable,
    CombatSolver.Beam.Policy.BossHpRelief BossHpRelief,
    RelicCounterLedger RelicCounters,
    CombatSolver.Beam.Policy.GrowthOpportunityTargets GrowthTargets);

internal readonly record struct NodeEvalMetrics(
    int ProjectedHp,
    int PersistentBuffValue,
    int FutureResourceValue,
    int LatentSetupValue,
    int ReachableHandValue,
    int ZeroCostPlayableCount,
    int DelayedDamageValue,
    int ReactiveDamageValue,
    int ReplayPotentialValue,
    int LongTermResourceValue,
    int SandpitRemaining,
    int EnemyStrengthSuppression,
    int EnemyWeakTurns,
    int EnemyVulnerableTurns,
    int LiveDeckClutter,
    int LiveCardCount,
    uint? FocusTargetCombatId,
    bool HasPositiveOrbEngine);

/// <summary>Core-state ranking using the primary terms of CombatBeamSolver.StateEvaluation.cs and SolverWeights.cs @ d231e9e5.</summary>
internal sealed class StateEvaluator
{
    [ThreadStatic]
    private static Creature[]? _singleTargetScratch;

    [ThreadStatic]
    private static List<AbstractModel>? _eligibleListenerScratch;

    private readonly CombatPolicy _policy;
    private readonly BeamMemberSearchMode _memberMode;
    private readonly ConcurrentDictionary<string, int> _potionCostCache = new(StringComparer.Ordinal);
    private readonly (int Slot, string Id)[] _forcedPotions;
    private readonly CombatSolver.Beam.Policy.PotionRewardOutlook _potionReward;
    private readonly int _initialPotionCount;
    private readonly int _initialCumulativeHpLost;
    private readonly int _initialHp;
    private readonly int _initialMaxHp;
    private readonly int _unconditionalPostCombatHeal;
    private readonly int _certifiedMaxPostCombatHeal;
    private readonly bool _canCertifyRemainingHealing;
    private readonly bool _hasVisibleHealingSource;
    private readonly bool _hasReachableRegisteredPower;
    private readonly int _totalFloor;
    private readonly int _rootTurnNumber;
    private readonly bool _theftApplicable;
    private readonly CombatSolver.Beam.Policy.BossHpRelief _bossHpRelief;
    private readonly RelicCounterLedger _relicCounters;
    private readonly CombatSolver.Beam.Policy.GrowthOpportunityTargets _growthTargets;

    public StateEvaluator(
        CombatState root,
        CombatPolicy policy,
        bool aggressivePowerCommitment = false)
        : this(
            root,
            policy,
            aggressivePowerCommitment ? BeamMemberSearchMode.AggressivePowerCommitment : BeamMemberSearchMode.Default,
            rootBaseline: null)
    {
    }

    public StateEvaluator(
        CombatState root,
        CombatPolicy policy,
        bool aggressivePowerCommitment,
        PolicyRootOperands? rootPolicyOperands)
        : this(
            CaptureBaseline(root, policy, rootPolicyOperands),
            aggressivePowerCommitment)
    {
    }

    public StateEvaluator(
        CombatState root,
        CombatPolicy policy,
        BeamMemberSearchMode memberMode,
        StateEvaluator? rootBaseline = null)
        : this(
            rootBaseline is not null
                ? (rootBaseline._baseline ?? CaptureFromEvaluator(rootBaseline)) with { Policy = policy }
                : CaptureBaseline(root, policy),
            memberModeOverride: memberMode)
    {
    }

    internal StateEvaluator(
        StateEvaluatorBaseline baseline,
        bool aggressivePowerCommitment = false,
        BeamMemberSearchMode? memberModeOverride = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        _baseline = baseline;
        _policy = baseline.Policy;
        _memberMode = memberModeOverride ?? (aggressivePowerCommitment
            ? BeamMemberSearchMode.AggressivePowerCommitment
            : BeamMemberSearchMode.Default);
        _forcedPotions = baseline.ForcedPotions;
        _potionReward = baseline.PotionReward;
        _initialPotionCount = baseline.InitialPotionCount;
        _initialCumulativeHpLost = baseline.InitialCumulativeHpLost;
        _initialHp = baseline.InitialHp;
        _initialMaxHp = baseline.InitialMaxHp;
        _unconditionalPostCombatHeal = baseline.UnconditionalPostCombatHeal;
        _canCertifyRemainingHealing = baseline.CanCertifyRemainingHealing;
        _certifiedMaxPostCombatHeal = baseline.CertifiedMaxPostCombatHeal;
        _hasVisibleHealingSource = baseline.HasVisibleHealingSource;
        _hasReachableRegisteredPower = baseline.HasReachableRegisteredPower;
        _totalFloor = baseline.TotalFloor;
        _rootTurnNumber = baseline.RootTurnNumber;
        _theftApplicable = baseline.TheftApplicable;
        _bossHpRelief = baseline.BossHpRelief;
        _relicCounters = baseline.RelicCounters;
        _growthTargets = baseline.GrowthTargets;
    }

    private readonly StateEvaluatorBaseline? _baseline;

    // Comparison only: early-stop growth targets remain a per-leaf Beam concern.
    // The initialized leaf supplies a valid combat graph to the existing constructor;
    // all common resource operands are replaced from the pre-start baseline below.
    internal StateEvaluator(OpeningComparisonBaseline baseline, CombatState initializedLeaf,
        CombatPolicy openingPolicy)
    {
        _policy = openingPolicy;
        _memberMode = BeamMemberSearchMode.Default;
        CombatState openingRoot = baseline.Root;
        _initialHp = baseline.InitialHp;
        _initialMaxHp = baseline.InitialMaxHp;
        _initialCumulativeHpLost = 0;
        _initialPotionCount = CountNonNullPotions(openingRoot.Players[0].PotionSlots);
        _forcedPotions = CollectForcedPotions(openingRoot.Players[0].PotionSlots, openingPolicy);
        _potionReward = openingPolicy.PredictPotionReward && !openingPolicy.IgnoreLongTermRewards
            ? CombatSolver.Beam.Policy.PotionRewardOutlook.Capture(openingRoot)
            : default;
        _unconditionalPostCombatHeal = CountActiveBurningBlood(openingRoot.Players[0].Relics) * 6;
        (_canCertifyRemainingHealing, _certifiedMaxPostCombatHeal) = HealingCertificateAnalyzer.Analyze(openingRoot);
        _hasVisibleHealingSource = HealingCertificateAnalyzer.HasVisibleHealingSource(openingRoot);
        _hasReachableRegisteredPower = PowerCardValuationModels.Registry.HasReachableRegisteredPower(openingRoot);
        _totalFloor = openingRoot.RunState.TotalFloor;
        _rootTurnNumber = openingRoot.Players[0].PlayerCombatState?.TurnNumber ?? 1;
        _theftApplicable = TheftLedger.IsApplicable(openingRoot);
        _bossHpRelief = BossHpPolicy.Resolve(openingRoot, openingPolicy);
        _relicCounters = RelicCounterLedger.Capture(openingRoot, openingPolicy);
        _growthTargets = CombatSolver.Beam.Policy.GrowthOpportunityTargets.Capture(
            initializedLeaf,
            openingPolicy.IgnoreLongTermRewards ? new GrowthBudgets() : openingPolicy.GrowthBudgets);
    }

    internal static StateEvaluatorBaseline CaptureBaseline(
        CombatState root,
        CombatPolicy policy,
        PolicyRootOperands? rootPolicyOperands = null)
    {
        (bool canCertify, int maxPostCombatHeal) = HealingCertificateAnalyzer.Analyze(root);
        return new(
            policy,
            CollectForcedPotions(root.Players[0].PotionSlots, policy),
            rootPolicyOperands?.PotionReward
                ?? (policy.PredictPotionReward && !policy.IgnoreLongTermRewards
                    ? CombatSolver.Beam.Policy.PotionRewardOutlook.Capture(root)
                    : default),
            CountNonNullPotions(root.Players[0].PotionSlots),
            root.Players[0].Creature.CumulativeHpLost,
            root.Players[0].Creature.CurrentHp,
            root.Players[0].Creature.MaxHp,
            CountActiveBurningBlood(root.Players[0].Relics) * 6,
            canCertify,
            maxPostCombatHeal,
            HealingCertificateAnalyzer.HasVisibleHealingSource(root),
            PowerCardValuationModels.Registry.HasReachableRegisteredPower(root),
            rootPolicyOperands is { TotalFloor: > 0 } ? rootPolicyOperands.Value.TotalFloor : root.RunState.TotalFloor,
            root.Players[0].PlayerCombatState?.TurnNumber ?? 1,
            TheftLedger.IsApplicable(root),
            rootPolicyOperands?.BossHpRelief ?? CombatSolver.Beam.Policy.BossHpPolicy.Resolve(root, policy),
            RelicCounterLedger.Capture(root, policy),
            CombatSolver.Beam.Policy.GrowthOpportunityTargets.Capture(
                root, policy.IgnoreLongTermRewards ? new GrowthBudgets() : policy.GrowthBudgets));
    }

    private static StateEvaluatorBaseline CaptureFromEvaluator(StateEvaluator source) => new(
        source._policy,
        source._forcedPotions,
        source._potionReward,
        source._initialPotionCount,
        source._initialCumulativeHpLost,
        source._initialHp,
        source._initialMaxHp,
        source._unconditionalPostCombatHeal,
        source._canCertifyRemainingHealing,
        source._certifiedMaxPostCombatHeal,
        source._hasVisibleHealingSource,
        source._hasReachableRegisteredPower,
        source._totalFloor,
        source._rootTurnNumber,
        source._theftApplicable,
        source._bossHpRelief,
        source._relicCounters,
        source._growthTargets);

    private CombatPolicy policy => _policy;

    internal StateEvaluatorBaseline Baseline => _baseline ?? CaptureFromEvaluator(this);
    internal CombatPolicy Policy => _policy;
    internal CombatSolver.Beam.Policy.BossHpRelief BossHpRelief => _bossHpRelief;
    internal CombatSolver.Beam.Policy.PotionRewardOutlook PotionReward => _potionReward;
    public bool AggressivePowerCommitment => _memberMode == BeamMemberSearchMode.AggressivePowerCommitment;
    public BeamMemberSearchMode MemberMode => _memberMode;
    public int InitialHp => _initialHp;
    public int InitialMaxHp => _initialMaxHp;
    public int InitialCumulativeHpLost => _initialCumulativeHpLost;
    public bool CanCertifyRemainingHealing => _canCertifyRemainingHealing;
    public bool HasVisibleHealingSource => _hasVisibleHealingSource;
    public bool HasReachableRegisteredPower => _hasReachableRegisteredPower;
    public int TotalFloor => _totalFloor;
    public int RootTurnNumber => _rootTurnNumber;
    public bool HasGrowthTargets => CaptureGrowth;
    public bool HasRelicTargets => _relicCounters.HasTargets;
    public bool HasForcedDirectives => _forcedPotions.Length > 0
        || policy.PotionPolicy == CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne
        || policy.TheftPolicy == CombatSolver.Beam.Policy.SolverTheftPolicy.PreserveResources
        || _relicCounters.HasTargets
        || CaptureGrowth;

    private static Creature[] RentSingleTarget(Creature creature)
    {
        Creature[]? arr = _singleTargetScratch;
        if (arr is null)
        {
            arr = new Creature[1];
            _singleTargetScratch = arr;
        }
        arr[0] = creature;
        return arr;
    }

    private static void ClearSingleTarget(Creature[] arr) => arr[0] = null!;
    public bool CaptureGrowth => !_policy.IgnoreLongTermRewards && _policy.GrowthBudgets.HasAny();
    // Entrench.GainBlock uses ValueProp.Unpowered in the game and Core.
    internal static bool BenefitsFromDexterity(CardModel card) => card.GainsBlock && card is not Entrench;

    private static (int Slot, string Id)[] CollectForcedPotions(
        IReadOnlyList<PotionModel?> slots,
        CombatPolicy combatPolicy)
    {
        List<(int Slot, string Id)>? forced = null;
        for (int slot = 0; slot < slots.Count; slot++)
        {
            PotionModel? potion = slots[slot];
            if (potion is not null && combatPolicy.EffectivePotionStrategy.Resolve(
                slot, potion.Id.Entry, combatPolicy.PotionPolicy) == CombatSolver.Beam.Policy.SolverPotionDirective.Force)
            {
                forced ??= [];
                forced.Add((slot, potion.Id.Entry));
            }
        }
        return forced is null ? [] : forced.ToArray();
    }

    private static int CountNonNullPotions(IReadOnlyList<PotionModel?> slots)
    {
        int count = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] is not null)
                count++;
        }
        return count;
    }

    private static int CountActiveBurningBlood(IReadOnlyList<RelicModel> relics)
    {
        int count = 0;
        for (int i = 0; i < relics.Count; i++)
        {
            if (relics[i] is BurningBlood && !relics[i].IsMelted)
                count++;
        }
        return count;
    }

    internal int GetPotionStrategicCost(string potionId)
    {
        if (_potionCostCache.TryGetValue(potionId, out int cached))
            return cached;

        PotionModel? potion = ModelDb.GetByIdOrNull<PotionModel>(new ModelId("POTION", potionId));
        if (potion is null)
        {
            foreach (PotionModel item in ModelDb.All<PotionModel>())
            {
                if (string.Equals(item.Id.Entry, potionId, StringComparison.Ordinal))
                {
                    potion = item;
                    break;
                }
            }
        }
        if (potion is null)
            throw new InvalidOperationException($"Unknown potion ID '{potionId}'.");

        int cost = CombatSolver.Beam.Policy.PotionRewardOutlook.StrategicHpCost(potion);
        _potionCostCache.TryAdd(potionId, cost);
        return cost;
    }

    private bool HasSameForcedPolicy(StateEvaluator other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (_forcedPotions.Length != other._forcedPotions.Length)
            return false;
        for (int i = 0; i < _forcedPotions.Length; i++)
        {
            if (_forcedPotions[i].Slot != other._forcedPotions[i].Slot
                || !string.Equals(_forcedPotions[i].Id, other._forcedPotions[i].Id, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    public int OutstandingTheft(SearchNode node) => _theftApplicable
        && _policy.TheftPolicy == CombatSolver.Beam.Policy.SolverTheftPolicy.PreserveResources
            ? TheftLedger.Outstanding(node.State, node.EscapedTheft) : 0;

    public bool IsForced(PlannedAction action)
    {
        if (action.Kind != "UsePotion" || _forcedPotions.Length == 0)
            return false;
        for (int i = 0; i < _forcedPotions.Length; i++)
        {
            if (_forcedPotions[i].Slot == action.PotionSlot
                && string.Equals(_forcedPotions[i].Id, action.PotionId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public bool RequiresPotionUse => _forcedPotions.Length > 0
        || _policy.PotionPolicy == CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne;
    public bool UsesSmartPotionPolicy => UsesSmartPotionPolicyCore();

    private bool UsesSmartPotionPolicyCore()
    {
        if (_policy.PotionPolicy == CombatSolver.Beam.Policy.SolverPotionPolicy.Smart)
            return true;
        if (_policy.PotionPolicy != CombatSolver.Beam.Policy.SolverPotionPolicy.Disabled)
            return false;
        if (_policy.EffectivePotionStrategy.Preset == CombatSolver.Beam.Policy.PotionStrategyPreset.AllSmart)
            return true;
        IReadOnlyList<CombatSolver.Beam.Policy.PotionSlotDirective> directives = _policy.EffectivePotionStrategy.Directives;
        for (int i = 0; i < directives.Count; i++)
        {
            if (directives[i].Directive == CombatSolver.Beam.Policy.SolverPotionDirective.Smart)
                return true;
        }
        return false;
    }

    public bool RequiresAtLeastOnePotion => _policy.PotionPolicy
        == CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne;

    public int MinimumRequiredPotionUses => Math.Max(
        _forcedPotions.Length,
        _policy.PotionPolicy == CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne ? 1 : 0);

    public bool AllowsMaxHpLoss(int loss) => _policy.BrightestFlameMaxHpLossLimit is not int limit
        || loss <= limit;

    public bool SatisfiesPotionPolicy(SearchNode node) =>
        node.ForcedPotionUses >= _forcedPotions.Length
        && (_policy.PotionPolicy != CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne
            || node.PotionUses > 0);

    public bool SatisfiesTheftPolicy(SearchNode node) => OutstandingTheft(node) == 0;
    public bool SatisfiesRelicTargets(SearchNode node) =>
        _relicCounters.Evaluate(node.State).AllSatisfied;
    public bool SatisfiesGrowthTargets(SearchNode node) => _growthTargets.SatisfiedBy(node.Growth);
    public int SatisfiedRelicTargets(SearchNode node) =>
        _relicCounters.Evaluate(node.State).Satisfied;
    public int BattleHpLost(SearchNode node) => Math.Max(0,
        node.State.Players[0].Creature.CumulativeHpLost - _initialCumulativeHpLost);

    public bool IsSmartPotionEligible(SearchNode candidate, SearchNode? potionFreeBaseline)
    {
        if (candidate.PotionUses == 0 || candidate.PotionUses == candidate.ForcedPotionUses)
            return true;
        if (potionFreeBaseline is null)
            return candidate.Ended;
        if (candidate.Ended && !potionFreeBaseline.Ended)
            return true;
        if (candidate.Won && !potionFreeBaseline.Won)
            return true;
        int saved = Math.Max(0, StrategicHpDeficit(potionFreeBaseline)
            - StrategicHpDeficit(candidate));
        (int cost, int ambergris) = OptionalPotionCost(candidate);
        if (ambergris > 0)
        {
            int rawHpSaved = Math.Max(0, candidate.State.Players[0].Creature.CurrentHp
                - potionFreeBaseline.State.Players[0].Creature.CurrentHp);
            int ambergrisRequired = cost + ambergris * ((int)Math.Ceiling(
                candidate.State.Players[0].Creature.MaxHp * 0.4) - 9);
            if (rawHpSaved < ambergrisRequired) return false;
        }
        if (_policy.PredictPotionReward && !_policy.IgnoreLongTermRewards)
            cost = Math.Max(1, cost - _potionReward.ReplacementHpCredit);
        if (_bossHpRelief == CombatSolver.Beam.Policy.BossHpRelief.ActClearHeal)
            cost *= 5;
        else if (_bossHpRelief == CombatSolver.Beam.Policy.BossHpRelief.RunEnding)
            cost = int.MaxValue / 4;
        return saved >= cost;
    }

    public bool IsRequiredPotionEligible(SearchNode candidate, SearchNode? potionFreeBaseline,
        bool anyRouteWon)
    {
        if (candidate.PotionUses == 0 || anyRouteWon && !candidate.Won)
            return false;
        if (candidate.PotionUses == 1 || potionFreeBaseline is not { Won: true })
            return true;
        int saved = Math.Max(0, StrategicHpDeficit(potionFreeBaseline)
            - StrategicHpDeficit(candidate));
        (int cost, _) = OptionalPotionCost(candidate);
        return saved >= Math.Max(0, cost - 9);
    }

    private (int Cost, int AmbergrisCount) OptionalPotionCost(SearchNode candidate)
    {
        if (candidate.PolicyEvaluator is { } sourceEval && HasSameForcedPolicy(sourceEval))
            return (candidate.OptionalPotionCost, candidate.OptionalAmbergrisCount);

        int cost = 0;
        int ambergris = 0;
        for (SearchRouteStep? current = candidate.Path; current?.Action is not null; current = current.Previous)
        {
            PlannedAction action = current.Action;
            if (action.Kind == "UsePotion" && !IsForced(action) && action.PotionId is { } potionId)
            {
                cost += GetPotionStrategicCost(potionId);
                if (potionId == "AMBERGRIS")
                    ambergris++;
            }
        }
        return (cost, ambergris);
    }

    public int StrategicHpDeficit(SearchNode node)
    {
        var creature = node.State.Players[0].Creature;
        int cumulative = BattleHpLost(node);
        int maxHpDeficit = Math.Max(0, _initialMaxHp - creature.MaxHp);
        int recovered = Math.Max(0, creature.CurrentHp - _initialHp + cumulative
            - node.DeathSaveHpRestored);
        if (node.Won && _unconditionalPostCombatHeal > 0)
            recovered += Math.Min(_unconditionalPostCombatHeal,
                Math.Max(0, creature.MaxHp - creature.CurrentHp));
        int recoveryScale = node.Won ? _bossHpRelief switch
        {
            CombatSolver.Beam.Policy.BossHpRelief.ActClearHeal => 5,
            CombatSolver.Beam.Policy.BossHpRelief.RunEnding => int.MaxValue / 4,
            _ => 1,
        } : 1;
        int deathSavePremium = node.DeathSaveHpRestored
            * SolverWeights.DeathSavePremiumPercent / 100;
        int strategicLoss = cumulative + maxHpDeficit - recovered / recoveryScale
            + deathSavePremium;
        int growthCredit = _policy.IgnoreLongTermRewards ? 0 : node.Growth.Credit(_policy.GrowthBudgets);
        return strategicLoss - growthCredit - _relicCounters.Evaluate(node.State).HpCredit;
    }

    public int PolicyHpCost(SearchNode node, int initialHp)
    {
        int strategicLoss = StrategicHpDeficit(node);
        if (!_policy.PredictPotionReward || _policy.IgnoreLongTermRewards)
            return strategicLoss;
        int optionalUses = Math.Max(0, node.PotionUses - node.ForcedPotionUses);
        int cost = optionalUses * 9;
        int credit = optionalUses > 0 ? Math.Min(_potionReward.ReplacementHpCredit,
            Math.Max(0, cost - 1)) : 0;
        return strategicLoss + cost - credit;
    }

    public double EvaluateBaselineScore(SearchNode node) =>
        ScoreCore(
            node.State,
            node.Depth,
            node.PotionUses,
            node.ForcedPotionUses,
            node.Growth,
            node.DeathSaveHpRestored,
            node.EscapedTheft,
            applyAggressivePowerCommitment: false,
            applyBaseScoreOnly: false,
            enemyHpWeightScale: 1.0d,
            out _);

    public double EvaluateForNode(
        CombatState state,
        int actionCount,
        int potionUses,
        int forcedPotionUses,
        GrowthCounts growth,
        int deathSaveHpRestored,
        int escapedTheft,
        out NodeEvalMetrics metrics)
    {
        double score = ScoreCore(
            state,
            actionCount,
            potionUses,
            forcedPotionUses,
            growth,
            deathSaveHpRestored,
            escapedTheft,
            applyAggressivePowerCommitment: _memberMode == BeamMemberSearchMode.AggressivePowerCommitment,
            applyBaseScoreOnly: _memberMode == BeamMemberSearchMode.BaseScoreOnly,
            enemyHpWeightScale: _memberMode == BeamMemberSearchMode.OffensiveRefinement ? 1.5d : 1.0d,
            out int projectedHp);

        int longTermResourceValue = _policy.IgnoreLongTermRewards
            ? 0
            : growth.Credit(_policy.GrowthBudgets);
        Creature playerCreature = state.Players[0].Creature;
        Creature[] targetScratch = RentSingleTarget(playerCreature);
        try
        {
            metrics = CoreNoveltyFeatures.ComputeNodeMetrics(
                state,
                _policy,
                projectedHp,
                longTermResourceValue,
                targetScratch);
        }
        finally
        {
            ClearSingleTarget(targetScratch);
        }
        return score;
    }

    public double Score(CombatState state, int actionCount, int potionUses,
        int forcedPotionUses, GrowthCounts growth, int deathSaveHpRestored,
        int escapedTheft) =>
        ScoreCore(
            state,
            actionCount,
            potionUses,
            forcedPotionUses,
            growth,
            deathSaveHpRestored,
            escapedTheft,
            applyAggressivePowerCommitment: _memberMode == BeamMemberSearchMode.AggressivePowerCommitment,
            applyBaseScoreOnly: _memberMode == BeamMemberSearchMode.BaseScoreOnly,
            enemyHpWeightScale: _memberMode == BeamMemberSearchMode.OffensiveRefinement ? 1.5d : 1.0d,
            out _);

    public int StrategicHpLowerBound(SearchNode node)
    {
        if (!_canCertifyRemainingHealing)
            return int.MaxValue;
        Player player = node.State.Players[0];
        Creature creature = player.Creature;
        if (creature.MaxHp > _initialMaxHp
            || HealingCertificateAnalyzer.ContainsPresentPotion(player)
            || HealingCertificateAnalyzer.ContainsUnsafePower(creature.Powers))
        {
            return int.MaxValue;
        }
        IReadOnlyList<Creature> allies = node.State.Allies;
        for (int i = 0; i < allies.Count; i++)
        {
            if (HealingCertificateAnalyzer.ContainsUnsafePower(allies[i].Powers))
                return int.MaxValue;
        }
        IReadOnlyList<Creature> enemies = node.State.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            if (HealingCertificateAnalyzer.ContainsUnsafePower(enemies[i].Powers))
                return int.MaxValue;
        }
        int cumulative = BattleHpLost(node);
        int maxHpDeficit = Math.Max(0, _initialMaxHp - creature.MaxHp);
        int recoveredSoFar = Math.Max(0, creature.CurrentHp - _initialHp + cumulative - node.DeathSaveHpRestored);
        int healUpper = _certifiedMaxPostCombatHeal;
        int recoveryScale = _bossHpRelief switch
        {
            CombatSolver.Beam.Policy.BossHpRelief.ActClearHeal => 5,
            CombatSolver.Beam.Policy.BossHpRelief.RunEnding => int.MaxValue / 4,
            _ => 1,
        };
        int deathSavePremium = node.DeathSaveHpRestored * SolverWeights.DeathSavePremiumPercent / 100;
        int growthCredit = _policy.IgnoreLongTermRewards ? 0 : node.Growth.Credit(_policy.GrowthBudgets);
        int relicCredit = _relicCounters.HasTargets ? _relicCounters.Evaluate(node.State).HpCredit : 0;
        return cumulative + maxHpDeficit - (recoveredSoFar + healUpper) / recoveryScale
            + deathSavePremium - growthCredit - relicCredit;
    }

    public bool IsProvenZeroDamageRoute(SearchNode node)
    {
        if (!node.Won
            || node.NodeBoundary != ForecastSearchBoundary.None
            || HasGrowthTargets
            || HasRelicTargets)
        {
            return false;
        }
        Creature creature = node.State.Players[0].Creature;
        return creature.IsAlive
            && node.PotionUses == 0
            && node.DeathSaveUseCount == 0
            && BattleHpLost(node) == 0
            && creature.MaxHp >= _initialMaxHp
            && creature.CurrentHp >= creature.MaxHp
            && SatisfiesTheftPolicy(node);
    }

    private double ScoreCore(CombatState state, int actionCount, int potionUses,
        int forcedPotionUses, GrowthCounts growth, int deathSaveHpRestored,
        int escapedTheft, bool applyAggressivePowerCommitment,
        bool applyBaseScoreOnly, double enemyHpWeightScale,
        out int projectedHp)
    {
        CombatEngine engine = state.Engine ?? throw new InvalidOperationException("Combat has no engine.");
        Player player = state.Players[0];
        Creature creature = player.Creature;
        if (!engine.IsInProgress && !engine.Won || !creature.IsAlive)
        {
            projectedHp = 0;
            return SolverWeights.DeathPenalty;
        }

        // Upstream primary weights: HP 30,000, enemy HP -10,000, victory +10 billion.
        IReadOnlyList<PowerModel> playerPowers = creature.Powers;
        int bufferCharges = 0;
        int dexterity = 0;
        double playerPowerPolarity = 0d;
        double aggressivePowerScore = 0d;
        bool checkBuffer = _policy.EnableBufferProjection;
        bool checkDex = _policy.EnableDexterityProjection;
        for (int i = 0; i < playerPowers.Count; i++)
        {
            PowerModel power = playerPowers[i];
            if (checkBuffer && power is BufferPower)
                bufferCharges += Math.Max(0, (int)power.Amount);
            else if (checkDex && power is DexterityPower)
                dexterity += Math.Max(0, (int)power.Amount);

            if (!applyBaseScoreOnly)
            {
                playerPowerPolarity += power.Type switch
                {
                    PowerType.Buff => Math.Clamp(power.Amount, 0, 8) * 12_000d,
                    PowerType.Debuff => -Math.Clamp(power.Amount, 0, 8) * 12_000d,
                    _ => 0d,
                };
            }
            if (applyAggressivePowerCommitment && power.Type == PowerType.Buff && power.Amount > 0)
                aggressivePowerScore += 120_000d + Math.Clamp((double)power.Amount, 0d, 8d) * 78_000d;
        }

        int threat = 0;
        int incomingHits = 0;
        double enemyRemainingHp = 0d;
        double enemyPowerScore = 0d;
        IReadOnlyList<Creature> enemies = state.Enemies;
        Creature[] targetScratch = RentSingleTarget(creature);
        try
        {
            for (int e = 0; e < enemies.Count; e++)
            {
                Creature enemy = enemies[e];
                if (!enemy.IsAlive)
                    continue;
                enemyRemainingHp += enemy.CurrentHp + enemy.Block * 0.5;
                if (enemy.Monster?.NextMove?.Intents is { } intents)
                {
                    for (int ii = 0; ii < intents.Count; ii++)
                    {
                        if (intents[ii] is AttackIntent attack)
                        {
                            threat += Math.Max(0, attack.GetTotalDamage(targetScratch, enemy));
                            if (bufferCharges > 0)
                                incomingHits += Math.Max(0, attack.Repeats);
                        }
                    }
                }
                if (!applyBaseScoreOnly)
                {
                    IReadOnlyList<PowerModel> enemyPowers = enemy.Powers;
                    for (int p = 0; p < enemyPowers.Count; p++)
                    {
                        PowerModel power = enemyPowers[p];
                        enemyPowerScore += power.Type switch
                        {
                            PowerType.Buff => -Math.Clamp(power.Amount, 0, 8) * 8_000d,
                            PowerType.Debuff => Math.Clamp(power.Amount, 0, 8) * 8_000d,
                            _ => 0d,
                        };
                    }
                }
            }
        }
        finally
        {
            ClearSingleTarget(targetScratch);
        }

        // StrategicEffectModel.BufferPrevention @ d231e9e5 estimates each
        // Buffer charge against the average incoming hit, capped by damage.
        int averageHit = Math.Max(1, threat / Math.Max(1, incomingHits));
        int bufferPrevention = (int)Math.Min(threat, (long)bufferCharges * averageHit);
        int exposed = Math.Max(0, threat - creature.Block - bufferPrevention);
        projectedHp = (!engine.IsInProgress && engine.Won)
            ? Math.Max(0, creature.CurrentHp)
            : Math.Max(0, creature.CurrentHp - exposed);
        double score = (creature.CurrentHp - exposed) * SolverWeights.Hp;
        // A fully blocked or nonattack turn leaves Buffer available later.
        // Upstream values that reserve at eight prevented HP per charge when
        // no incoming damage is projected; cap at the player's max HP.
        if (!applyBaseScoreOnly && threat <= creature.Block && bufferCharges > 0)
            score += Math.Min(creature.MaxHp, (long)bufferCharges * 8) * SolverWeights.Hp;
        // StateEvaluation.Snapshot charges accumulated damage even after a heal,
        // and treats permanent max HP as a resource separate from current HP.
        score += (creature.MaxHp - _initialMaxHp) * SolverWeights.Hp;
        score -= Math.Max(0, creature.CumulativeHpLost - _initialCumulativeHpLost)
            * SolverWeights.Hp;
        score -= (deathSaveHpRestored + deathSaveHpRestored
            * SolverWeights.DeathSavePremiumPercent / 100) * SolverWeights.Hp;
        if (_theftApplicable && _policy.TheftPolicy
            == CombatSolver.Beam.Policy.SolverTheftPolicy.PreserveResources)
            score += TheftLedger.Outstanding(state, escapedTheft)
                * SolverWeights.OutstandingStolenResourcePenalty;
        score += enemyRemainingHp * SolverWeights.EnemyHp * enemyHpWeightScale;
        score += Math.Min(creature.Block, threat) * SolverWeights.Hp * 0.5;
        if (!applyBaseScoreOnly)
            score += Math.Min(creature.Block - Math.Min(creature.Block, threat), creature.MaxHp) * 2_000d;

        if (!applyBaseScoreOnly)
        {
            // Current-turn resource and reachable hand are deliberately bounded; neither
            // can overtake a full HP point or a completed victory on its own.
            PlayerCombatState combat = player.PlayerCombatState!;
            score += Math.Min(combat.Energy, SolverWeights.CurrentEnergyBeamCap)
                * SolverWeights.CurrentEnergyBeamValue;
            score += Math.Min(combat.Stars, 6) * 20_000d;

            IReadOnlyList<CardModel> handCards = combat.Hand.Cards;
            int playableCount = 0;
            bool needDexterityBlock = dexterity > 0 && exposed > 0;
            int blockCardCount = 0;
            Span<ulong> blockCardCostsStack = needDexterityBlock ? stackalloc ulong[16] : default;
            ulong[]? blockCardCostsPooled = null;
            try
            {
                for (int c = 0; c < handCards.Count; c++)
                {
                    CardModel card = handCards[c];
                    if (!card.CanPlay(out _))
                        continue;
                    playableCount++;
                    if (needDexterityBlock && BenefitsFromDexterity(card))
                    {
                        int energyCost = Math.Max(0, card.EnergyCost);
                        int starCost = Math.Max(0, card.StarCost);
                        ulong packed = ((ulong)(uint)energyCost << 32) | (uint)starCost;
                        if (blockCardCostsPooled is null && blockCardCount < blockCardCostsStack.Length)
                        {
                            blockCardCostsStack[blockCardCount++] = packed;
                        }
                        else
                        {
                            if (blockCardCostsPooled is null)
                            {
                                blockCardCostsPooled = ArrayPool<ulong>.Shared.Rent(Math.Max(32, handCards.Count));
                                blockCardCostsStack[..blockCardCount].CopyTo(blockCardCostsPooled);
                            }
                            else if (blockCardCount >= blockCardCostsPooled.Length)
                            {
                                ulong[] grown = ArrayPool<ulong>.Shared.Rent(blockCardCostsPooled.Length * 2);
                                Array.Copy(blockCardCostsPooled, grown, blockCardCount);
                                ArrayPool<ulong>.Shared.Return(blockCardCostsPooled);
                                blockCardCostsPooled = grown;
                            }
                            blockCardCostsPooled[blockCardCount++] = packed;
                        }
                    }
                }

                score += Math.Min(playableCount, 10) * 3_000d;
                if (needDexterityBlock && blockCardCount > 0)
                {
                    Span<ulong> costs = blockCardCostsPooled is not null
                        ? blockCardCostsPooled.AsSpan(0, blockCardCount)
                        : blockCardCostsStack[..blockCardCount];
                    for (int i = 1; i < costs.Length; i++)
                    {
                        ulong key = costs[i];
                        int j = i - 1;
                        while (j >= 0 && costs[j] > key)
                        {
                            costs[j + 1] = costs[j];
                            j--;
                        }
                        costs[j + 1] = key;
                    }

                    int remainingEnergy = combat.Energy;
                    int remainingStars = combat.Stars;
                    int reachableBlockCards = 0;
                    for (int i = 0; i < costs.Length; i++)
                    {
                        ulong packed = costs[i];
                        int energyCost = (int)(uint)(packed >> 32);
                        int starCost = (int)(uint)packed;
                        if (energyCost > remainingEnergy || starCost > remainingStars) continue;
                        remainingEnergy -= energyCost;
                        remainingStars -= starCost;
                        reachableBlockCards++;
                    }
                    score += Math.Min((long)exposed, (long)dexterity * reachableBlockCards)
                        * SolverWeights.Hp;
                }
            }
            finally
            {
                if (blockCardCostsPooled is not null)
                    ArrayPool<ulong>.Shared.Return(blockCardCostsPooled);
            }

            score += playerPowerPolarity;
            score += aggressivePowerScore;
            score += enemyPowerScore;
        }
        else if (applyAggressivePowerCommitment)
        {
            score += aggressivePowerScore;
        }

        score -= Math.Max(0, _initialPotionCount - CountNonNullPotions(player.PotionSlots))
            * SolverWeights.PotionMinimumHpSaved * SolverWeights.Hp;
        // PotionRewardOutlook.cs credits at most one postcombat replacement. The
        // one-HP floor mirrors PotionUsePolicy.ApplyReplacementCredit.
        if (potionUses > 0 && _policy.PredictPotionReward && !_policy.IgnoreLongTermRewards)
            score += Math.Min(_potionReward.ReplacementHpCredit, Math.Max(0, potionUses * 9 - 1))
                * SolverWeights.Hp;
        if (!_policy.IgnoreLongTermRewards)
            score += growth.Credit(_policy.GrowthBudgets) * SolverWeights.Hp;
        if (_relicCounters.HasTargets)
        {
            CombatSolver.Beam.Policy.RelicCounterEvaluation counters =
                _relicCounters.Evaluate(state);
            score += counters.HpCredit * SolverWeights.Hp;
            score += counters.PriorityScore * 0.1 - counters.Distance * 0.001;
        }
        score += actionCount * SolverWeights.ActionPenalty;
        // Forced/required uses are search constraints, not potion value. They must survive
        // beam pruning even when spending the potion costs HP (PotionStrategySnapshot.cs).
        score += forcedPotionUses * 1_000_000_000d;
        if (_policy.PotionPolicy == CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne
            && potionUses > 0) score += 1_000_000_000d;
        if (!engine.IsInProgress && engine.Won) score += SolverWeights.VictoryBonus;
        return score;
    }

    internal readonly record struct ThreatProjection(
        int Hp,
        int DeathSaveHpRestored,
        int DeathSaveUseCount,
        int BlockConsumed = 0);

    internal int ComputeProjectedPlayerHp(SearchNode node, ForecastSearchBoundary nodeBoundary)
    {
        // 仅在胜局或节点自身的真实终止边界（EventDefeat / UnsupportedEffect）下抑制来袭威胁推演；
        // 请求级限制与停在未执行选择处的活体父节点均需保留真实的致死威胁推演。
        if (node.Won
            || nodeBoundary is ForecastSearchBoundary.EventDefeat
                or ForecastSearchBoundary.UnsupportedEffect)
        {
            return node.State.Players[0].Creature.CurrentHp;
        }
        return ProjectHpAfterThreat(node.State).Hp;
    }

    internal ThreatProjection ProjectHpAfterThreat(CombatState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Player player = state.Players[0];
        Creature creature = player.Creature;
        if (!creature.IsAlive || creature.CurrentHp <= 0)
            return new ThreatProjection(0, 0, 0, 0);

        // 先检查 SandpitPower：仅剩 1 层且施加者仍存活时，敌方回合开始即必定致死。
        IReadOnlyList<Creature> allies = state.Allies;
        for (int a = 0; a < allies.Count; a++)
        {
            if (HasFatalSandpit(allies[a], creature))
                return new ThreatProjection(0, 0, 0, 0);
        }
        IReadOnlyList<Creature> enemies = state.Enemies;
        for (int e = 0; e < enemies.Count; e++)
        {
            if (HasFatalSandpit(enemies[e], creature))
                return new ThreatProjection(0, 0, 0, 0);
        }

        List<AbstractModel> eligibleListeners = _eligibleListenerScratch ?? new List<AbstractModel>(32);
        _eligibleListenerScratch = null;
        try
        {
            CollectEligibleListeners(state, eligibleListeners);

            // 检查是否存在尚未在推演器中建模的死亡拦截监听者。
            for (int i = 0; i < eligibleListeners.Count; i++)
            {
                AbstractModel model = eligibleListeners[i];
                if (model is FairyInABottle or LizardTail) continue;
                if (!model.ShouldDie(creature) || !model.ShouldDieLate(creature))
                {
                    throw new ProjectionUnsupportedException(
                        $"Threat projection does not support death preventer '{model.GetType().Name}'.");
                }
            }

            int hp = creature.CurrentHp;
            int initialBlock = creature.Block;
            int block = initialBlock;
            Creature? osty = player.Osty;
            int ostyHp = osty is { IsAlive: true } ? osty.CurrentHp : 0;

            Dictionary<BufferPower, int>? remainingBuffer = null;
            PopulateBufferMap(creature.Powers, ref remainingBuffer);
            if (osty is not null)
                PopulateBufferMap(osty.Powers, ref remainingBuffer);

            Dictionary<SlipperyPower, int>? remainingSlippery = null;
            PopulateSlipperyMap(creature.Powers, ref remainingSlippery);
            if (osty is not null)
                PopulateSlipperyMap(osty.Powers, ref remainingSlippery);

            BeatingRemnant? beatingRemnant = null;
            IReadOnlyList<RelicModel> relics = player.Relics;
            for (int r = 0; r < relics.Count; r++)
            {
                if (relics[r] is BeatingRemnant candidate && !candidate.IsMelted)
                {
                    beatingRemnant = candidate;
                    break;
                }
            }
            int beatingRemnantRemaining = beatingRemnant is null
                ? int.MaxValue
                : (int)beatingRemnant.ModifyHpLostAfterOsty(creature, 20m, ValueProp.Move, null, null);

            ProjectedDeathPrevention deathPrevention = BuildProjectedDeathPrevention(player, creature.MaxHp);
            bool gambitActive = HasPower<TheGambitPower>(creature.Powers);

            if (state.RunState.GameMode == Sts2Sim.Core.Runs.GameMode.Dread && state.CurrentSide == CombatSide.Player)
            {
                foreach (PowerModel power in creature.Powers)
                {
                    if (power is not InsatiableDominionPower miasma) continue;
                    ProjectThreatHit(state, eligibleListeners, creature, null, Math.Max(0, miasma.Damage),
                        ValueProp.Unpowered, osty, ref ostyHp, ref block, ref hp, ref gambitActive,
                        ref beatingRemnantRemaining, ref deathPrevention, remainingBuffer, remainingSlippery);
                }
            }

            Creature[] targetScratch = RentSingleTarget(creature);
            try
            {
                for (int e = 0; e < enemies.Count; e++)
                {
                    Creature enemy = enemies[e];
                    if (!enemy.IsAlive || enemy.Monster is not { } monster) continue;
                    if (state.CurrentSide == CombatSide.Enemy && monster.SpawnedThisTurn) continue;
                    if (monster.NextMove is not { } nextMove) continue;

                    IReadOnlyList<AbstractIntent> intents = nextMove.Intents;
                    for (int ii = 0; ii < intents.Count; ii++)
                    {
                        if (intents[ii] is not AttackIntent attackIntent) continue;
                        int repeats = Math.Max(0, attackIntent.Repeats);
                        int baseDamage = Math.Max(0, attackIntent.GetSingleDamage(targetScratch, enemy));
                        for (int hit = 0; hit < repeats; hit++)
                        {
                            ProjectThreatHit(
                                state,
                                eligibleListeners,
                                creature,
                                enemy,
                                baseDamage,
                                ValueProp.Move,
                                osty,
                                ref ostyHp,
                                ref block,
                                ref hp,
                                ref gambitActive,
                                ref beatingRemnantRemaining,
                                ref deathPrevention,
                                remainingBuffer,
                                remainingSlippery);
                        }
                    }
                }
            }
            finally
            {
                ClearSingleTarget(targetScratch);
            }

            return new ThreatProjection(
                Math.Max(0, hp),
                deathPrevention.DeathSaveHpRestored,
                deathPrevention.UseCount,
                Math.Max(0, initialBlock - block));
        }
        finally
        {
            eligibleListeners.Clear();
            _eligibleListenerScratch = eligibleListeners;
        }
    }

    private static bool HasFatalSandpit(Creature owner, Creature target)
    {
        if (!owner.IsAlive) return false;
        IReadOnlyList<PowerModel> powers = owner.Powers;
        for (int i = 0; i < powers.Count; i++)
        {
            if (powers[i] is SandpitPower { Amount: 1 } sandpit
                && ReferenceEquals(sandpit.Target, target)
                && sandpit.Owner.IsAlive)
            {
                return true;
            }
        }
        return false;
    }

    private static void PopulateBufferMap(
        IReadOnlyList<PowerModel> powers,
        ref Dictionary<BufferPower, int>? map)
    {
        for (int i = 0; i < powers.Count; i++)
        {
            if (powers[i] is BufferPower buffer)
            {
                map ??= new Dictionary<BufferPower, int>(ReferenceEqualityComparer.Instance);
                map[buffer] = (int)buffer.Amount;
            }
        }
    }

    private static void PopulateSlipperyMap(
        IReadOnlyList<PowerModel> powers,
        ref Dictionary<SlipperyPower, int>? map)
    {
        for (int i = 0; i < powers.Count; i++)
        {
            if (powers[i] is SlipperyPower slippery)
            {
                map ??= new Dictionary<SlipperyPower, int>(ReferenceEqualityComparer.Instance);
                map[slippery] = (int)slippery.Amount;
            }
        }
    }

    private static bool HasPower<TPower>(IReadOnlyList<PowerModel> powers) where TPower : PowerModel
    {
        for (int i = 0; i < powers.Count; i++)
        {
            if (powers[i] is TPower)
                return true;
        }
        return false;
    }

    private static void ProjectThreatHit(
        CombatState state,
        List<AbstractModel> eligibleListeners,
        Creature playerCreature,
        Creature? attacker,
        int baseDamage,
        ValueProp props,
        Creature? osty,
        ref int ostyHp,
        ref int block,
        ref int playerHp,
        ref bool gambitActive,
        ref int beatingRemnantRemaining,
        ref ProjectedDeathPrevention deathPrevention,
        Dictionary<BufferPower, int>? remainingBuffer,
        Dictionary<SlipperyPower, int>? remainingSlippery)
    {
        decimal adjustedDamage = Hook.ModifyDamage(
            state,
            playerCreature,
            attacker,
            baseDamage,
            props,
            cardSource: null,
            cardPlay: null,
            out _);

        if (Hook.ModifyPreBlockDamageTarget(state, playerCreature, adjustedDamage, props, attacker) is not null)
        {
            throw new ProjectionUnsupportedException(
                "Pre-block damage redirection (e.g., Wuppy's Loyalty) is not supported by forecast threat projection.");
        }

        decimal blocked = Math.Min(block, adjustedDamage);
        block -= (int)blocked;
        decimal damageRemaining = Math.Max(adjustedDamage - blocked, 0m);

        decimal hpLoss = EvaluateBeforeOstyHpLoss(eligibleListeners, playerCreature, damageRemaining, attacker, props);

        Creature target = Hook.ModifyUnblockedDamageTarget(state, playerCreature, hpLoss, props, attacker);
        if (ReferenceEquals(target, osty) && ostyHp <= 0)
            target = playerCreature;
        else if (ReferenceEquals(target, playerCreature)
            && osty is not null
            && ostyHp > 0
            && HasPower<DieForYouPower>(osty.Powers))
        {
            target = osty;
        }

        hpLoss = EvaluateAfterOstyHpLoss(
            eligibleListeners,
            target,
            playerCreature,
            hpLoss,
            attacker,
            props,
            beatingRemnantRemaining,
            remainingBuffer,
            remainingSlippery);

        int loss = Math.Max(0, (int)Math.Clamp(hpLoss, 0m, 999_999_999m));
        if (ReferenceEquals(target, osty))
        {
            int absorbed = Math.Min(ostyHp, loss);
            ostyHp -= absorbed;
            ConsumeSlipperyAfterDamage(osty, absorbed, remainingSlippery);
            int spillover = loss - absorbed;
            if (spillover > 0)
            {
                decimal overflowLoss = EvaluateAfterOstyHpLoss(
                    eligibleListeners,
                    playerCreature,
                    playerCreature,
                    spillover,
                    attacker,
                    props,
                    beatingRemnantRemaining,
                    remainingBuffer,
                    remainingSlippery);
                int playerLoss = Math.Max(0, (int)Math.Clamp(overflowLoss, 0m, 999_999_999m));
                playerHp -= playerLoss;
                if (beatingRemnantRemaining != int.MaxValue)
                    beatingRemnantRemaining = Math.Max(0, beatingRemnantRemaining - playerLoss);
                ConsumeSlipperyAfterDamage(playerCreature, playerLoss, remainingSlippery);
                if (playerLoss > 0 && gambitActive)
                {
                    gambitActive = false;
                    playerHp = 0;
                }
                deathPrevention.TryRevive(ref playerHp);
            }
            return;
        }

        playerHp -= loss;
        if (beatingRemnantRemaining != int.MaxValue)
            beatingRemnantRemaining = Math.Max(0, beatingRemnantRemaining - loss);
        ConsumeSlipperyAfterDamage(playerCreature, loss, remainingSlippery);
        if (loss > 0 && gambitActive)
        {
            gambitActive = false;
            playerHp = 0;
        }
        deathPrevention.TryRevive(ref playerHp);
    }

    private static void ConsumeSlipperyAfterDamage(
        Creature actualTarget,
        int unblockedDamage,
        Dictionary<SlipperyPower, int>? remainingSlippery)
    {
        if (unblockedDamage < 1 || remainingSlippery is null)
            return;

        IReadOnlyList<PowerModel> powers = actualTarget.Powers;
        for (int i = 0; i < powers.Count; i++)
        {
            if (powers[i] is SlipperyPower slippery
                && remainingSlippery.TryGetValue(slippery, out int charges)
                && charges > 0)
            {
                remainingSlippery[slippery] = charges - 1;
            }
        }
    }

    private static decimal EvaluateBeforeOstyHpLoss(
        List<AbstractModel> eligibleListeners,
        Creature target,
        decimal amount,
        Creature? attacker, ValueProp props)
    {
        decimal result = amount;
        for (int i = 0; i < eligibleListeners.Count; i++)
            result = eligibleListeners[i].ModifyHpLostBeforeOsty(target, result, props, attacker, cardSource: null);
        for (int i = 0; i < eligibleListeners.Count; i++)
            result = eligibleListeners[i].ModifyHpLostBeforeOstyLate(target, result, props, attacker, cardSource: null);
        return result;
    }

    private static decimal EvaluateAfterOstyHpLoss(
        List<AbstractModel> eligibleListeners,
        Creature target,
        Creature playerCreature,
        decimal amount,
        Creature? attacker,
        ValueProp props,
        int beatingRemnantRemaining,
        Dictionary<BufferPower, int>? remainingBuffer,
        Dictionary<SlipperyPower, int>? remainingSlippery)
    {
        decimal result = amount;
        for (int i = 0; i < eligibleListeners.Count; i++)
        {
            AbstractModel item = eligibleListeners[i];
            if (item is BeatingRemnant)
            {
                if (ReferenceEquals(target, playerCreature) && beatingRemnantRemaining != int.MaxValue)
                    result = Math.Min(result, Math.Max(0, beatingRemnantRemaining));
                continue;
            }
            if (item is SlipperyPower slippery
                && remainingSlippery is not null
                && remainingSlippery.TryGetValue(slippery, out int slipperyCharges)
                && slipperyCharges <= 0)
            {
                continue;
            }
            result = item.ModifyHpLostAfterOsty(target, result, props, attacker, cardSource: null);
        }

        for (int i = 0; i < eligibleListeners.Count; i++)
        {
            AbstractModel item = eligibleListeners[i];
            if (item is BufferPower buffer
                && remainingBuffer is not null
                && remainingBuffer.TryGetValue(buffer, out int bufferCharges)
                && bufferCharges <= 0)
            {
                continue;
            }
            decimal before = result;
            result = item.ModifyHpLostAfterOstyLate(target, result, props, attacker, cardSource: null);
            if (item is BufferPower consumedBuffer
                && remainingBuffer is not null
                && decimal.Truncate(before) != decimal.Truncate(result))
            {
                remainingBuffer[consumedBuffer] = remainingBuffer.GetValueOrDefault(consumedBuffer) - 1;
            }
        }

        return result;
    }

    private static void CollectEligibleListeners(CombatState state, List<AbstractModel> destination)
    {
        destination.Clear();
        foreach (AbstractModel model in state.RunState.IterateHookListeners(state))
        {
            bool eligible = model switch
            {
                PowerModel power => power.Owner.CombatState is not null
                    && (power.Owner.Player?.IsActiveForHooks ?? true),
                MonsterModel monster => monster.Creature.CombatState is not null,
                OrbModel orb => !orb.HasBeenRemovedFromState && orb.Owner.IsActiveForHooks,
                CardModel or RelicModel or PotionModel or AfflictionModel or EnchantmentModel or RunModel or DominionModel => true,
                _ => throw new ProjectionUnsupportedException(
                    $"Threat projection encountered unknown hook listener '{model.GetType().Name}'."),
            };
            if (eligible)
                destination.Add(model);
        }
    }

    private static ProjectedDeathPrevention BuildProjectedDeathPrevention(
        Player player,
        int playerMaxHp)
    {
        int fairyCount = 0;
        IReadOnlyList<PotionModel?> slots = player.PotionSlots;
        for (int i = 0; i < slots.Count; i++)
        {
            if (slots[i] is FairyInABottle)
                fairyCount++;
        }

        bool lizardTailAvailable = false;
        IReadOnlyList<RelicModel> relics = player.Relics;
        for (int i = 0; i < relics.Count; i++)
        {
            if (relics[i] is LizardTail tail && !tail.IsMelted && !tail.IsUsedUp)
            {
                lizardTailAvailable = true;
                break;
            }
        }

        int fairyHeal = Math.Max(1, (int)(playerMaxHp * 0.30m));
        int tailHeal = lizardTailAvailable ? Math.Max(1, (int)(playerMaxHp * 0.5m)) : 0;
        return new ProjectedDeathPrevention(fairyCount, fairyHeal, lizardTailAvailable, tailHeal);
    }

    private struct ProjectedDeathPrevention(
        int fairyCount,
        int fairyHeal,
        bool lizardTailAvailable,
        int lizardTailHeal)
    {
        public int DeathSaveHpRestored { get; private set; }
        public int UseCount { get; private set; }

        public void TryRevive(ref int hp)
        {
            if (hp > 0)
                return;
            if (fairyCount > 0)
            {
                fairyCount--;
                hp = fairyHeal;
                DeathSaveHpRestored += fairyHeal;
                UseCount++;
                return;
            }
            if (!lizardTailAvailable)
                return;
            lizardTailAvailable = false;
            hp = lizardTailHeal;
            DeathSaveHpRestored += lizardTailHeal;
            UseCount++;
        }
    }
}
