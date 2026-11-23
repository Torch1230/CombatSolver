using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Powers;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
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
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

/// <summary>Root-level policy operands captured before <see cref="CombatState.Clone()"/> replaces <see cref="RunState"/> with a combat snapshot.</summary>
internal readonly record struct PolicyRootOperands(
    CombatSolver.Beam.Policy.BossHpRelief BossHpRelief,
    CombatSolver.Beam.Policy.PotionRewardOutlook PotionReward)
{
    public static PolicyRootOperands Capture(CombatState root, CombatPolicy policy) =>
        new(
            BossHpPolicy.Resolve(root, policy),
            policy.PredictPotionReward && !policy.IgnoreLongTermRewards
                ? CombatSolver.Beam.Policy.PotionRewardOutlook.Capture(root)
                : default);
}

/// <summary>Core-state ranking using the primary terms of CombatBeamSolver.StateEvaluation.cs and SolverWeights.cs @ d231e9e5.</summary>
internal sealed class StateEvaluator(
    CombatState root,
    CombatPolicy policy,
    bool aggressivePowerCommitment = false,
    PolicyRootOperands? rootPolicyOperands = null)
{
    // Comparison only: early-stop growth targets remain a per-leaf Beam concern.
    // The initialized leaf supplies a valid combat graph to the existing constructor;
    // all common resource operands are replaced from the pre-start baseline below.
    internal StateEvaluator(OpeningComparisonBaseline baseline, CombatState initializedLeaf,
        CombatPolicy openingPolicy) : this(initializedLeaf, openingPolicy)
    {
        CombatState openingRoot = baseline.Root;
        _initialHp = baseline.InitialHp;
        _initialMaxHp = baseline.InitialMaxHp;
        _initialCumulativeHpLost = 0;
        _initialPotionCount = openingRoot.Players[0].PotionSlots.Count(potion => potion is not null);
        _forcedPotions = openingRoot.Players[0].PotionSlots.Select((potion, slot) => (potion, slot))
            .Where(item => item.potion is not null && openingPolicy.EffectivePotionStrategy.Resolve(
                item.slot, item.potion.Id.Entry, openingPolicy.PotionPolicy)
                == CombatSolver.Beam.Policy.SolverPotionDirective.Force)
            .Select(item => (item.slot, item.potion!.Id.Entry)).ToArray();
        _potionReward = openingPolicy.PredictPotionReward && !openingPolicy.IgnoreLongTermRewards
            ? CombatSolver.Beam.Policy.PotionRewardOutlook.Capture(openingRoot) : default;
        _unconditionalPostCombatHeal = openingRoot.Players[0].Relics
            .Count(relic => relic is BurningBlood && !relic.IsMelted) * 6;
        _theftApplicable = TheftLedger.IsApplicable(openingRoot);
        _bossHpRelief = BossHpPolicy.Resolve(openingRoot, openingPolicy);
        _relicCounters = RelicCounterLedger.Capture(openingRoot, openingPolicy);
    }

    private readonly (int Slot, string Id)[] _forcedPotions = root.Players[0].PotionSlots
        .Select((potion, slot) => (potion, slot))
        .Where(item => item.potion is not null && policy.EffectivePotionStrategy.Resolve(item.slot,
            item.potion.Id.Entry, policy.PotionPolicy) == CombatSolver.Beam.Policy.SolverPotionDirective.Force)
        .Select(item => (item.slot, item.potion!.Id.Entry)).ToArray();
    private readonly CombatSolver.Beam.Policy.PotionRewardOutlook _potionReward =
        rootPolicyOperands?.PotionReward
        ?? (policy.PredictPotionReward && !policy.IgnoreLongTermRewards
            ? CombatSolver.Beam.Policy.PotionRewardOutlook.Capture(root) : default);
    private readonly int _initialPotionCount = root.Players[0].PotionSlots.Count(potion => potion is not null);
    private readonly int _initialCumulativeHpLost = root.Players[0].Creature.CumulativeHpLost;
    private readonly int _initialHp = root.Players[0].Creature.CurrentHp;
    private readonly int _initialMaxHp = root.Players[0].Creature.MaxHp;
    private readonly int _unconditionalPostCombatHeal = root.Players[0].Relics
        .Count(relic => relic is BurningBlood && !relic.IsMelted) * 6;
    private readonly bool _theftApplicable = TheftLedger.IsApplicable(root);
    private readonly CombatSolver.Beam.Policy.BossHpRelief _bossHpRelief =
        rootPolicyOperands?.BossHpRelief ?? BossHpPolicy.Resolve(root, policy);
    internal CombatSolver.Beam.Policy.BossHpRelief BossHpRelief => _bossHpRelief;
    internal CombatSolver.Beam.Policy.PotionRewardOutlook PotionReward => _potionReward;
    private readonly RelicCounterLedger _relicCounters = RelicCounterLedger.Capture(root, policy);
    private readonly CombatSolver.Beam.Policy.GrowthOpportunityTargets _growthTargets =
        CombatSolver.Beam.Policy.GrowthOpportunityTargets.Capture(
        root, policy.IgnoreLongTermRewards ? new GrowthBudgets() : policy.GrowthBudgets);
    public bool CaptureGrowth => !policy.IgnoreLongTermRewards && policy.GrowthBudgets.HasAny();
    // Entrench.GainBlock uses ValueProp.Unpowered in the game and Core.
    internal static bool BenefitsFromDexterity(CardModel card) => card.GainsBlock && card is not Entrench;

    public int OutstandingTheft(SearchNode node) => _theftApplicable
        && policy.TheftPolicy == CombatSolver.Beam.Policy.SolverTheftPolicy.PreserveResources
            ? TheftLedger.Outstanding(node.State, node.EscapedTheft) : 0;

    public bool IsForced(PlannedAction action) => action.Kind == "UsePotion"
        && _forcedPotions.Any(item => item.Slot == action.PotionSlot
            && string.Equals(item.Id, action.PotionId, StringComparison.Ordinal));

    public bool RequiresPotionUse => _forcedPotions.Length > 0
        || policy.PotionPolicy == CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne;
    public bool UsesSmartPotionPolicy => policy.PotionPolicy
        == CombatSolver.Beam.Policy.SolverPotionPolicy.Smart
        || policy.PotionPolicy == CombatSolver.Beam.Policy.SolverPotionPolicy.Disabled
            && (policy.EffectivePotionStrategy.Preset
                == CombatSolver.Beam.Policy.PotionStrategyPreset.AllSmart
                || policy.EffectivePotionStrategy.Directives.Any(directive =>
                    directive.Directive
                        == CombatSolver.Beam.Policy.SolverPotionDirective.Smart));
    public bool RequiresAtLeastOnePotion => policy.PotionPolicy
        == CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne;

    public int MinimumRequiredPotionUses => Math.Max(
        _forcedPotions.Length,
        policy.PotionPolicy == CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne ? 1 : 0);

    public bool AllowsMaxHpLoss(int loss) => policy.BrightestFlameMaxHpLossLimit is not int limit
        || loss <= limit;

    public bool SatisfiesPotionPolicy(SearchNode node) =>
        node.ForcedPotionUses >= _forcedPotions.Length
        && (policy.PotionPolicy != CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne
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
        if (policy.PredictPotionReward && !policy.IgnoreLongTermRewards)
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
        int cost = 0;
        int ambergris = 0;
        foreach (PlannedAction action in candidate.Route().Where(action => action.Kind == "UsePotion"
            && !IsForced(action)))
        {
            PotionModel potion = ModelDb.All<PotionModel>()
                .Single(item => item.Id.Entry == action.PotionId);
            cost += CombatSolver.Beam.Policy.PotionRewardOutlook.StrategicHpCost(potion);
            if (action.PotionId == "AMBERGRIS") ambergris++;
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
        int growthCredit = policy.IgnoreLongTermRewards ? 0 : node.Growth.Credit(policy.GrowthBudgets);
        return strategicLoss - growthCredit - _relicCounters.Evaluate(node.State).HpCredit;
    }

    public int PolicyHpCost(SearchNode node, int initialHp)
    {
        int strategicLoss = StrategicHpDeficit(node);
        if (!policy.PredictPotionReward || policy.IgnoreLongTermRewards)
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
            applyAggressivePowerCommitment: false);

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
            aggressivePowerCommitment);

    private double ScoreCore(CombatState state, int actionCount, int potionUses,
        int forcedPotionUses, GrowthCounts growth, int deathSaveHpRestored,
        int escapedTheft, bool applyAggressivePowerCommitment)
    {
        var engine = state.Engine ?? throw new InvalidOperationException("Combat has no engine.");
        var player = state.Players[0];
        var creature = player.Creature;
        if (!engine.IsInProgress && !engine.Won || !creature.IsAlive)
            return SolverWeights.DeathPenalty;

        // Upstream primary weights: HP 30,000, enemy HP -10,000, victory +10 billion.
        int bufferCharges = !policy.EnableBufferProjection ? 0 : creature.Powers.OfType<BufferPower>()
            .Sum(power => Math.Max(0, (int)power.Amount));
        int threat = 0;
        int incomingHits = 0;
        foreach (var enemy in state.Enemies.Where(enemy => enemy.IsAlive))
        {
            foreach (AttackIntent intent in enemy.Monster?.NextMove?.Intents.OfType<AttackIntent>() ?? [])
            {
                threat += Math.Max(0, intent.GetTotalDamage([creature], enemy));
                if (bufferCharges > 0)
                    incomingHits += Math.Max(0, intent.Repeats);
            }
        }
        // StrategicEffectModel.BufferPrevention @ d231e9e5 estimates each
        // Buffer charge against the average incoming hit, capped by damage.
        int averageHit = Math.Max(1, threat / Math.Max(1, incomingHits));
        int bufferPrevention = (int)Math.Min(threat, (long)bufferCharges * averageHit);
        int exposed = Math.Max(0, threat - creature.Block - bufferPrevention);
        double score = (creature.CurrentHp - exposed) * SolverWeights.Hp;
        // A fully blocked or nonattack turn leaves Buffer available later.
        // Upstream values that reserve at eight prevented HP per charge when
        // no incoming damage is projected; cap at the player's max HP.
        if (threat <= creature.Block && bufferCharges > 0)
            score += Math.Min(creature.MaxHp, (long)bufferCharges * 8) * SolverWeights.Hp;
        // StateEvaluation.Snapshot charges accumulated damage even after a heal,
        // and treats permanent max HP as a resource separate from current HP.
        score += (creature.MaxHp - _initialMaxHp) * SolverWeights.Hp;
        score -= Math.Max(0, creature.CumulativeHpLost - _initialCumulativeHpLost)
            * SolverWeights.Hp;
        score -= (deathSaveHpRestored + deathSaveHpRestored
            * SolverWeights.DeathSavePremiumPercent / 100) * SolverWeights.Hp;
        if (_theftApplicable && policy.TheftPolicy
            == CombatSolver.Beam.Policy.SolverTheftPolicy.PreserveResources)
            score += TheftLedger.Outstanding(state, escapedTheft)
                * SolverWeights.OutstandingStolenResourcePenalty;
        score += state.Enemies.Where(enemy => enemy.IsAlive)
            .Sum(enemy => enemy.CurrentHp + enemy.Block * 0.5) * SolverWeights.EnemyHp;
        score += Math.Min(creature.Block, threat) * SolverWeights.Hp * 0.5;
        score += Math.Min(creature.Block - Math.Min(creature.Block, threat), creature.MaxHp) * 2_000d;

        // Current-turn resource and reachable hand are deliberately bounded; neither
        // can overtake a full HP point or a completed victory on its own.
        var combat = player.PlayerCombatState!;
        score += Math.Min(combat.Energy, SolverWeights.CurrentEnergyBeamCap)
            * SolverWeights.CurrentEnergyBeamValue;
        score += Math.Min(combat.Stars, 6) * 20_000d;
        var playableCards = combat.Hand.Cards.Where(card => card.CanPlay(out _)).ToArray();
        score += Math.Min(playableCards.Length, 10) * 3_000d;
        // FootworkPowerCardValuationModel @ d231e9e5 values Dexterity from later
        // block-card triggers. DexterityPower.ModifyBlockAdditive in the game
        // adds its amount to each powered card block. Credit only payable cards
        // already in the Core hand and only currently uncovered incoming damage.
        int dexterity = !policy.EnableDexterityProjection ? 0 : creature.Powers.OfType<DexterityPower>()
            .Sum(power => Math.Max(0, (int)power.Amount));
        if (dexterity > 0 && exposed > 0)
        {
            int remainingEnergy = combat.Energy;
            int remainingStars = combat.Stars;
            int reachableBlockCards = 0;
            foreach (CardModel card in playableCards.Where(BenefitsFromDexterity)
                .OrderBy(card => Math.Max(0, card.EnergyCost))
                .ThenBy(card => Math.Max(0, card.StarCost)))
            {
                int energyCost = Math.Max(0, card.EnergyCost);
                int starCost = Math.Max(0, card.StarCost);
                if (energyCost > remainingEnergy || starCost > remainingStars) continue;
                remainingEnergy -= energyCost;
                remainingStars -= starCost;
                reachableBlockCards++;
            }
            score += Math.Min((long)exposed, (long)dexterity * reachableBlockCards)
                * SolverWeights.Hp;
        }
        // Generic power polarity supplies a small setup signal without card-specific rules.
        score += creature.Powers.Sum(power => power.Type switch
        {
            PowerType.Buff => Math.Clamp(power.Amount, 0, 8) * 12_000d,
            PowerType.Debuff => -Math.Clamp(power.Amount, 0, 8) * 12_000d,
            _ => 0d,
        });
        if (applyAggressivePowerCommitment)
        {
            foreach (PowerModel power in creature.Powers)
            {
                if (power.Type == PowerType.Buff && power.Amount > 0)
                    score += 120_000d + Math.Clamp((double)power.Amount, 0d, 8d) * 78_000d;
            }
        }
        score += state.Enemies.Where(enemy => enemy.IsAlive).Sum(enemy => enemy.Powers.Sum(power =>
            power.Type switch
            {
                PowerType.Buff => -Math.Clamp(power.Amount, 0, 8) * 8_000d,
                PowerType.Debuff => Math.Clamp(power.Amount, 0, 8) * 8_000d,
                _ => 0d,
            }));
        score -= Math.Max(0, _initialPotionCount - player.PotionSlots.Count(p => p is not null))
            * SolverWeights.PotionMinimumHpSaved * SolverWeights.Hp;
        // PotionRewardOutlook.cs credits at most one postcombat replacement. The
        // one-HP floor mirrors PotionUsePolicy.ApplyReplacementCredit.
        if (potionUses > 0 && policy.PredictPotionReward && !policy.IgnoreLongTermRewards)
            score += Math.Min(_potionReward.ReplacementHpCredit, Math.Max(0, potionUses * 9 - 1))
                * SolverWeights.Hp;
        if (!policy.IgnoreLongTermRewards)
            score += growth.Credit(policy.GrowthBudgets) * SolverWeights.Hp;
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
        if (policy.PotionPolicy == CombatSolver.Beam.Policy.SolverPotionPolicy.RequireAtLeastOne
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
        var player = state.Players[0];
        var creature = player.Creature;
        if (!creature.IsAlive || creature.CurrentHp <= 0)
            return new ThreatProjection(0, 0, 0, 0);

        // 先检查 SandpitPower：仅剩 1 层且施加者仍存活时，敌方回合开始即必定致死。
        foreach (Creature owner in state.Creatures)
        {
            if (!owner.IsAlive) continue;
            foreach (PowerModel power in owner.Powers)
            {
                if (power is SandpitPower { Amount: 1 } sandpit
                    && ReferenceEquals(sandpit.Target, creature)
                    && sandpit.Owner.IsAlive)
                {
                    return new ThreatProjection(0, 0, 0, 0);
                }
            }
        }

        // 检查是否存在尚未在推演器中建模的死亡拦截监听者。
        foreach (AbstractModel model in IterateEligibleListeners(state))
        {
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
        if (creature.Powers.OfType<BufferPower>().Any() || (osty?.Powers.OfType<BufferPower>().Any() ?? false))
        {
            remainingBuffer = new Dictionary<BufferPower, int>(ReferenceEqualityComparer.Instance);
            foreach (BufferPower buffer in creature.Powers.OfType<BufferPower>())
                remainingBuffer[buffer] = (int)buffer.Amount;
            if (osty is not null)
            {
                foreach (BufferPower buffer in osty.Powers.OfType<BufferPower>())
                    remainingBuffer[buffer] = (int)buffer.Amount;
            }
        }

        Dictionary<SlipperyPower, int>? remainingSlippery = null;
        if (creature.Powers.OfType<SlipperyPower>().Any() || (osty?.Powers.OfType<SlipperyPower>().Any() ?? false))
        {
            remainingSlippery = new Dictionary<SlipperyPower, int>(ReferenceEqualityComparer.Instance);
            foreach (SlipperyPower slippery in creature.Powers.OfType<SlipperyPower>())
                remainingSlippery[slippery] = (int)slippery.Amount;
            if (osty is not null)
            {
                foreach (SlipperyPower slippery in osty.Powers.OfType<SlipperyPower>())
                    remainingSlippery[slippery] = (int)slippery.Amount;
            }
        }

        BeatingRemnant? beatingRemnant = player.Relics.OfType<BeatingRemnant>().FirstOrDefault(relic => !relic.IsMelted);
        int beatingRemnantRemaining = beatingRemnant is null
            ? int.MaxValue
            : (int)beatingRemnant.ModifyHpLostAfterOsty(creature, 20m, ValueProp.Move, null, null);

        var deathPrevention = BuildProjectedDeathPrevention(player, creature.MaxHp);
        bool gambitActive = creature.Powers.OfType<TheGambitPower>().Any();

        if (state.RunState.GameMode == Sts2Sim.Core.Runs.GameMode.Dread && state.CurrentSide == CombatSide.Player)
        {
            foreach (InsatiableDominionPower miasma in creature.Powers.OfType<InsatiableDominionPower>())
            {
                int miasmaDamage = Math.Max(0, miasma.Damage);
                ProjectHit(
                    state,
                    creature,
                    attacker: null,
                    miasmaDamage,
                    ValueProp.Unpowered,
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

        foreach (Creature enemy in state.Enemies)
        {
            if (!enemy.IsAlive || enemy.Monster is not { } monster) continue;
            if (state.CurrentSide == CombatSide.Enemy && monster.SpawnedThisTurn) continue;
            if (monster.NextMove is not { } nextMove) continue;

            foreach (AbstractIntent intent in nextMove.Intents)
            {
                if (intent is not AttackIntent attackIntent) continue;
                int repeats = Math.Max(0, attackIntent.Repeats);
                int baseDamage = Math.Max(0, attackIntent.GetSingleDamage([creature], enemy));
                for (int hit = 0; hit < repeats; hit++)
                {
                    ProjectHit(
                        state,
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

        return new ThreatProjection(
            Math.Max(0, hp),
            deathPrevention.DeathSaveHpRestored,
            deathPrevention.UseCount,
            Math.Max(0, initialBlock - block));
    }

    private static void ProjectHit(
        CombatState state,
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

        decimal hpLoss = EvaluateBeforeOstyHpLoss(state, playerCreature, damageRemaining, attacker, props);

        Creature target = Hook.ModifyUnblockedDamageTarget(state, playerCreature, hpLoss, props, attacker);
        if (ReferenceEquals(target, osty) && ostyHp <= 0)
            target = playerCreature;
        else if (ReferenceEquals(target, playerCreature)
            && osty is not null
            && ostyHp > 0
            && osty.Powers.OfType<DieForYouPower>().Any())
        {
            target = osty;
        }

        hpLoss = EvaluateAfterOstyHpLoss(
            state,
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
                    state,
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

        foreach (SlipperyPower slippery in actualTarget.Powers.OfType<SlipperyPower>())
        {
            if (remainingSlippery.TryGetValue(slippery, out int charges) && charges > 0)
            {
                remainingSlippery[slippery] = charges - 1;
            }
        }
    }

    private static decimal EvaluateBeforeOstyHpLoss(
        CombatState state,
        Creature target,
        decimal amount,
        Creature? attacker,
        ValueProp props)
    {
        decimal result = amount;
        foreach (AbstractModel item in IterateEligibleListeners(state))
            result = item.ModifyHpLostBeforeOsty(target, result, props, attacker, cardSource: null);
        foreach (AbstractModel item in IterateEligibleListeners(state))
            result = item.ModifyHpLostBeforeOstyLate(target, result, props, attacker, cardSource: null);
        return result;
    }

    private static decimal EvaluateAfterOstyHpLoss(
        CombatState state,
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
        foreach (AbstractModel item in IterateEligibleListeners(state))
        {
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

        foreach (AbstractModel item in IterateEligibleListeners(state))
        {
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

    private static IEnumerable<AbstractModel> IterateEligibleListeners(CombatState state)
    {
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
                yield return model;
        }
    }

    private static ProjectedDeathPrevention BuildProjectedDeathPrevention(
        Sts2Sim.Core.Entities.Players.Player player,
        int playerMaxHp)
    {
        int fairyCount = 0;
        for (int i = 0; i < player.PotionSlots.Count; i++)
        {
            if (player.PotionSlots[i] is FairyInABottle)
                fairyCount++;
        }

        bool lizardTailAvailable = player.Relics
            .OfType<LizardTail>()
            .Any(relic => !relic.IsMelted && !relic.IsUsedUp);

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
