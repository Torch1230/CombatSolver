using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>
/// 能力牌投影共用的冻结事实读取与有界前沿工具。所有角色共用；不包含逐卡规则。
/// </summary>
internal sealed partial class CombatBeamSolver
{
    private int PowerAmountGain<TPower>(SearchNode parent, SearchNode child)
        where TPower : PowerModel
    {
        SimulatedCombatState parentCombat =
            (SimulatedCombatState)parent.Snapshot.Simulator.State.CombatState;
        SimulatedCombatState childCombat =
            (SimulatedCombatState)child.Snapshot.Simulator.State.CombatState;
        return Math.Max(
            0,
            childCombat.GetAmount<TPower>(_player.Creature)
                - parentCombat.GetAmount<TPower>(_player.Creature));
    }

    private static int CurrentIncomingDamage(SearchNode node)
        => Math.Max(0, node.Snapshot.PlayerHp - node.Snapshot.ProjectedPlayerHp);

    private static int SaturatingProduct(params int[] values)
    {
        long product = 1;
        foreach (int value in values)
        {
            int factor = Math.Max(0, value);
            if (factor == 0)
                return 0;
            if (product > int.MaxValue / factor)
                return int.MaxValue;
            product *= factor;
        }
        return (int)product;
    }

    internal static int SaturatingPowerCommitmentAdd(int left, int right)
        => (int)Math.Clamp((long)left + right, 0L, int.MaxValue);

    private static int EstimateRemainingTurns(
        SimulationSnapshot snapshot,
        int drawPerTurn)
    {
        int damagePerTurn = Math.Max(
            1,
            snapshot.RetainedAttackValue * Math.Max(1, drawPerTurn)
                / Math.Max(1, snapshot.LiveDeckSize));
        return Math.Clamp(
            (snapshot.EnemyHp + damagePerTurn - 1) / damagePerTurn,
            1,
            SolverWeights.SetupValueHorizonTurns);
    }

    private int ForecastIncomingDamage(SearchNode child, int turnOffset)
    {
        int roundIndex = child.Turn - _startTurnNumber + turnOffset;
        if (roundIndex < 0 || roundIndex >= _forecast.Rounds.Count)
            return 0;
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        long total = 0;
        foreach (ForecastMove move in _forecast.Rounds[roundIndex])
        {
            if (!combat.ContainsCreature(move.Owner)
                || !simulator.State.GetCreature(move.Owner).IsAlive)
            {
                continue;
            }
            total += move.AttackHits.Sum(hit => Math.Max(0, hit.Damage));
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    private static int MarginalFrontierValue(
        IReadOnlyList<PowerTurnFrontierState> baseline,
        IReadOnlyList<PowerTurnFrontierState> powered)
        => SaturatingPowerCommitmentAdd(
            PowerTurnFrontier.DefensiveDamageUplift(baseline, powered),
            PowerTurnFrontier.DefensiveHpUplift(baseline, powered) * 8);

    /// <summary>
    /// 有界防伤：本回合及预计窗口内真正能减少战损的部分优先，另有少量额度给未跨阈值的格挡。
    /// 没有预计来袭伤害时只给一半信用，避免把无法兑现的格挡估成完整收益。
    /// </summary>
    internal static int BoundedPrevention(int rawBlock, int incomingDamage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rawBlock);
        int ceiling = Math.Max(Math.Max(0, incomingDamage), rawBlock / 2);
        return Math.Min(rawBlock, ceiling);
    }

    private int PowerGrowthFrontierPotential(
        SearchNode child,
        int blockPerSkillBonus = 0,
        int blockPerCardBonus = 0,
        int damagePerDraw = 0,
        int damageTargets = 1,
        int damagePerShiv = 0,
        int firstShivDamageBonus = 0,
        int damagePerCard = 0,
        int damagePerUnblockedAttackHit = 0,
        int weakAttackBonusPercent = 0,
        int damagePerAttack = 0)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        int incomingDamage = PowerIncomingDamage(child);
        PowerTurnCardOption[] baselineOptions = BuildCurrentHandOptions(child);
        if (baselineOptions.Length == 0)
            return 0;
        PowerTurnCardOption[] poweredOptions = weakAttackBonusPercent > 0
            ? BuildCurrentHandOptions(child, weakAttackBonusPercent: weakAttackBonusPercent)
            : baselineOptions;
        return Math.Min(
            PowerEnemyHp(child),
            MarginalFrontierValue(
                PowerTurnFrontier.Build(child.Snapshot.Energy, incomingDamage, baselineOptions),
                PowerTurnFrontier.Build(
                    child.Snapshot.Energy,
                    incomingDamage,
                    poweredOptions,
                    blockPerSkillBonus,
                    blockPerCardBonus,
                    damagePerDraw,
                    Math.Max(1, damageTargets),
                    damagePerShiv,
                    firstShivDamageBonus,
                    damagePerCard,
                    damagePerUnblockedAttackHit,
                    damagePerAttack)));
    }

    private int PowerPerTurnDamagePotential(
        SearchNode child,
        int damagePerTurn,
        int turns,
        int targets)
        => Math.Min(
            PowerEnemyHp(child),
            SaturatingProduct(damagePerTurn, Math.Max(0, turns), Math.Max(1, targets)));

    private int PowerPerTurnBlockPotential(
        SearchNode child,
        int blockPerTurn,
        int turns,
        int incomingDamage)
        => BoundedPrevention(SaturatingProduct(blockPerTurn, Math.Max(0, turns)), incomingDamage);

    private int PowerPerTriggerDamagePotential(SearchNode child, int perTrigger, int triggers)
        => Math.Min(PowerEnemyHp(child), SaturatingProduct(perTrigger, Math.Max(0, triggers)));

    private int PowerPerTriggerBlockPotential(
        SearchNode child,
        int perTrigger,
        int triggers,
        int incomingDamage)
        => BoundedPrevention(SaturatingProduct(perTrigger, Math.Max(0, triggers)), incomingDamage);

    private int PowerPerTurnResourcePotential(int perTurn, int turns)
        => SaturatingProduct(perTurn, Math.Max(0, turns));

    private int PowerPerTriggerResourcePotential(int perTrigger, int triggers)
        => SaturatingProduct(perTrigger, Math.Max(0, triggers));

    private PowerTurnCardOption[] BuildCurrentHandOptions(
        SearchNode child,
        int? shivTargetsOverride = null,
        int weakAttackBonusPercent = 0)
    {
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        return BuildProjectedCardOptions(
            child,
            playerState.Hand.Cards,
            playerState.Hand.Cards.Count,
            shivTargetsOverride,
            weakAttackBonusPercent);
    }

    private PowerTurnCardOption[] BuildProjectedCardOptions(
        SearchNode child,
        IReadOnlyList<PredictedCard> cards,
        int projectedHandCount,
        int? shivTargetsOverride = null,
        int weakAttackBonusPercent = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(weakAttackBonusPercent);
        ArgumentOutOfRangeException.ThrowIfNegative(projectedHandCount);
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        Creature[] aliveEnemies = combat.KnownEnemies
            .Where(enemy => combat.ContainsCreature(enemy)
                && simulator.State.GetCreature(enemy).IsAlive)
            .ToArray();
        int weakTargets = aliveEnemies.Count(enemy =>
            combat.GetAmount<WeakPower>(enemy) > 0);
        int minimumEnemyBlock = aliveEnemies
            .Select(enemy => Math.Max(0, simulator.State.GetCreature(enemy).Block))
            .DefaultIfEmpty(0)
            .Min();
        int shivTargets = shivTargetsOverride ?? (
            combat.GetAmount<FanOfKnivesPower>(_player.Creature) > 0
                ? Math.Max(1, child.Snapshot.AliveEnemyCount)
                : 1);
        return cards
            .Where(card => !card.HasKeyword(simulator.State, CardKeyword.Unplayable))
            .Select(card =>
            {
                int energyCost = Math.Max(
                    0,
                    card.GetEnergyCostWithModifiers(simulator, playerState));
                bool isShiv = card.Preview.Tags.Contains(CardTag.Shiv);
                int baseDamage = card.Preview.Type == CardType.Attack
                    && card.Preview.DynamicVars.TryGetValue("Damage", out var damageVar)
                        ? Math.Max(0, damageVar.IntValue)
                        : 0;
                int damage = baseDamage;
                if (isShiv)
                    damage = (int)Math.Min(int.MaxValue, (long)damage * shivTargets);
                if (baseDamage > 0 && weakTargets > 0 && weakAttackBonusPercent > 0)
                {
                    int affectedTargets = isShiv ? weakTargets : 1;
                    damage = SaturatingPowerCommitmentAdd(
                        damage,
                        (int)Math.Min(
                            int.MaxValue,
                            (long)baseDamage * affectedTargets * weakAttackBonusPercent / 100));
                }
                int block = card.Preview.Type == CardType.Skill
                    && card.Preview.DynamicVars.TryGetValue("Block", out var blockVar)
                        ? Math.Max(0, blockVar.IntValue)
                        : 0;
                int cardCountValue = card.Preview.DynamicVars.TryGetValue("Cards", out var cardsVar)
                    ? cardsVar.IntValue
                    : 0;
                int draws = SilentCardFlowFacts.DrawCount(
                    card.Preview.Id.Entry,
                    cardCountValue,
                    projectedHandCount);
                int attackHits = baseDamage == 0
                    ? 0
                    : CardMechanismFacts.AttackHits(
                        card.Preview.Id.Entry,
                        card.Preview.DynamicVars.TryGetValue("Repeat", out var repeatVar)
                            ? repeatVar.IntValue
                            : 0);
                int unblockedAttackHits = baseDamage == 0
                    || (long)baseDamage * attackHits <= minimumEnemyBlock
                        ? 0
                        : Math.Max(1, attackHits - minimumEnemyBlock / baseDamage);
                return new PowerTurnCardOption(
                    energyCost,
                    damage,
                    block,
                    CardAccess: draws,
                    Draws: draws,
                    IsShiv: isShiv,
                    UnblockedAttackHits: unblockedAttackHits);
            })
            .Where(option => option.Damage > 0
                || option.Block > 0
                || option.CardAccess > 0)
            .ToArray();
    }
}
