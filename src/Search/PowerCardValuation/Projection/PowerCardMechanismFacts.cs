using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

/// <summary>
/// 各角色能力牌证据与投影共用的冻结事实读取。只读快照与模拟状态，不修改任何分支。
/// 未登记能力的卡组不会走到这里（请求级快速旁路会先跳过能力框架）。
/// </summary>
internal sealed partial class CombatBeamSolver
{
    /// <summary>
    /// 未来仍可打出的牌：手牌、抽牌堆、弃牌堆。消耗堆不参与，已消耗的牌正常情况下不会再回来，
    /// 不能继续提供触发证据或抬高投影。
    /// </summary>
    private PredictedCard[] PowerLiveCards(SearchNode node)
    {
        SimPlayerCombatState state = node.Snapshot.Simulator.State.GetPlayerCombatState(_player);
        return state.Hand.Cards
            .Concat(state.DrawPile.Cards)
            .Concat(state.DiscardPile.Cards)
            .ToArray();
    }

    private PredictedCard[] PowerHandCards(SearchNode node)
        => node.Snapshot.Simulator.State.GetPlayerCombatState(_player).Hand.Cards.ToArray();

    private bool PowerHasType(SearchNode node, CardType type)
        => PowerLiveCards(node).Any(card => card.Preview.Type == type);

    private bool PowerHasTag(SearchNode node, CardTag tag)
        => PowerLiveCards(node).Any(card => card.Preview.Tags.Contains(tag));

    private bool PowerHasKeyword(SearchNode node, CardKeyword keyword)
    {
        CombatPredictionSimulator simulator = node.Snapshot.Simulator;
        return PowerLiveCards(node).Any(card => card.HasKeyword(simulator.State, keyword));
    }

    private bool PowerHasDynamicVar(SearchNode node, string fragment)
        => PowerLiveCards(node).Any(card => card.Preview.DynamicVars._vars.Keys.Any(key =>
            key.Contains(fragment, StringComparison.OrdinalIgnoreCase)));

    private bool PowerHasEnergyCostAtLeast(SearchNode node, int minimum)
    {
        CombatPredictionSimulator simulator = node.Snapshot.Simulator;
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(_player);
        return PowerLiveCards(node).Any(card =>
            card.GetEnergyCostWithModifiers(simulator, state) >= minimum);
    }

    private int PowerCountCardsCostAtLeast(SearchNode node, int minimum)
    {
        CombatPredictionSimulator simulator = node.Snapshot.Simulator;
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(_player);
        return PowerLiveCards(node).Count(card =>
            card.GetEnergyCostWithModifiers(simulator, state) >= minimum);
    }

    private bool PowerHasStarCostCard(SearchNode node)
        => PowerLiveCards(node).Any(card =>
            card.Preview.CurrentStarCost > 0 || card.Preview.HasStarCostX);

    private bool PowerHasDebuffSource(SearchNode node)
        => PowerLiveCards(node).Any(card =>
            card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Vulnerable", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Weak", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Doom", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Poison", StringComparison.OrdinalIgnoreCase)));

    private int PowerRemainingTurns(SearchNode node)
    {
        CombatPredictionSimulator simulator = node.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        int drawPerTurn = PersistentPowerSupport.GetModifiedHandDraw(
            combat,
            _player,
            MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount);
        return EstimateRemainingTurns(node.Snapshot, Math.Max(1, drawPerTurn));
    }

    private int PowerEnemyHp(SearchNode node)
    {
        CombatPredictionSimulator simulator = node.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        long total = 0;
        foreach (var enemy in combat.KnownEnemies)
        {
            if (!combat.ContainsCreature(enemy))
                continue;
            var state = simulator.State.GetCreature(enemy);
            if (state.IsAlive)
                total += Math.Max(0, state.CurrentHp);
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    private int PowerEnemyPowerTotal<TPower>(SearchNode node)
        where TPower : PowerModel
    {
        CombatPredictionSimulator simulator = node.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        long total = 0;
        foreach (var enemy in combat.KnownEnemies)
        {
            if (!combat.ContainsCreature(enemy)
                || !simulator.State.GetCreature(enemy).IsAlive)
            {
                continue;
            }
            total += Math.Max(0, combat.GetAmount<TPower>(enemy));
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    private int PowerEnemyCountWithPower<TPower>(SearchNode node)
        where TPower : PowerModel
    {
        CombatPredictionSimulator simulator = node.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        return combat.KnownEnemies.Count(enemy =>
            combat.ContainsCreature(enemy)
            && simulator.State.GetCreature(enemy).IsAlive
            && combat.GetAmount<TPower>(enemy) > 0);
    }

    private int PowerPlayerPowerAmount<TPower>(SearchNode node)
        where TPower : PowerModel
    {
        SimulatedCombatState combat =
            (SimulatedCombatState)node.Snapshot.Simulator.State.CombatState;
        return Math.Max(0, combat.GetAmount<TPower>(_player.Creature));
    }

    private int PowerStars(SearchNode node)
        => Math.Max(0, node.Snapshot.Simulator.State.GetPlayerCombatState(_player).Stars);

    private SimOrbQueue PowerOrbQueue(SearchNode node)
        => node.Snapshot.Simulator.State.GetPlayerCombatState(_player).OrbQueue;

    private int PowerOrbCount(SearchNode node) => PowerOrbQueue(node).Orbs.Count;

    private int PowerOrbCapacity(SearchNode node) => PowerOrbQueue(node).Capacity;

    private int PowerOrbCount<TOrb>(SearchNode node) where TOrb : OrbModel
        => PowerOrbQueue(node).Orbs.Count(orb => orb is TOrb);

    private int PowerDistinctOrbTypes(SearchNode node)
        => PowerOrbQueue(node).Orbs
            .Select(orb => orb.GetType())
            .Distinct()
            .Count();

    private bool PowerHasOsty(SearchNode node)
    {
        var osty = node.Snapshot.Simulator.State.GetOsty(_player);
        return osty != null && node.Snapshot.Simulator.State.GetCreature(osty).IsAlive;
    }

    private int PowerOstyCount(SearchNode node)
        => PowerHasOsty(node) ? 1 : 0;

    private int PowerIncomingDamage(SearchNode node)
        => Math.Max(0, node.Snapshot.PlayerHp - node.Snapshot.ProjectedPlayerHp);

    private int PowerForecastIncomingHits(SearchNode child, int turnOffset)
    {
        int roundIndex = child.Turn - _startTurnNumber + turnOffset;
        if (roundIndex < 0 || roundIndex >= _forecast.Rounds.Count)
            return 0;
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        int hits = 0;
        foreach (ForecastMove move in _forecast.Rounds[roundIndex])
        {
            if (!combat.ContainsCreature(move.Owner)
                || !simulator.State.GetCreature(move.Owner).IsAlive)
            {
                continue;
            }
            hits += move.AttackHits.Count;
        }
        return hits;
    }

    private int PowerStrength(SearchNode node)
        => PowerPlayerPowerAmount<StrengthPower>(node);

    private int PowerDexterity(SearchNode node)
        => PowerPlayerPowerAmount<DexterityPower>(node);

    private int PowerFocus(SearchNode node)
        => PowerPlayerPowerAmount<FocusPower>(node);

    private int PowerEnergyUnit(SearchNode node)
    {
        PredictedCard[] cards = PowerLiveCards(node);
        if (cards.Length == 0)
            return 3;
        long total = 0;
        foreach (PredictedCard card in cards)
            total += Math.Max(0, (int)Math.Round(CardChoiceSupport.CardValue(card.Preview)));
        return Math.Max(3, (int)(total / cards.Length));
    }

    private int PowerDrawPerTurn(SearchNode node)
    {
        SimulatedCombatState combat =
            (SimulatedCombatState)node.Snapshot.Simulator.State.CombatState;
        return Math.Max(
            1,
            PersistentPowerSupport.GetModifiedHandDraw(
                combat,
                _player,
                MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount));
    }

    private int PowerMaxEnergy(SearchNode node)
    {
        SimulatedCombatState combat =
            (SimulatedCombatState)node.Snapshot.Simulator.State.CombatState;
        return Math.Max(1, PersistentPowerSupport.GetModifiedMaxEnergy(combat, _player));
    }

    /// <summary>
    /// 本回合手牌里可支付的星能花费上限：按每张星能牌的实际星能费用求和（X 费按剩余星能计），
    /// 再受当前星能总量限制。用于群星之子按真实花费点数兑现格挡。
    /// </summary>
    private int PowerStarSpendCapacity(SearchNode node)
    {
        SimPlayerCombatState state = node.Snapshot.Simulator.State.GetPlayerCombatState(_player);
        int stars = Math.Max(0, state.Stars);
        if (stars == 0)
            return 0;
        CombatPredictionSimulator simulator = node.Snapshot.Simulator;
        long spend = 0;
        foreach (PredictedCard card in state.Hand.Cards)
        {
            if (card.HasKeyword(simulator.State, CardKeyword.Unplayable))
                continue;
            if (card.Preview.HasStarCostX)
            {
                spend += stars;
                continue;
            }
            spend += Math.Max(0, card.Preview.CurrentStarCost);
        }
        return (int)Math.Min(stars, spend);
    }

    private int PowerCountType(SearchNode node, CardType type)
        => PowerLiveCards(node).Count(card => card.Preview.Type == type);

    private int PowerMaxBlockValue(SearchNode node)
        => PowerLiveCards(node)
            .Where(card => card.Preview.DynamicVars.TryGetValue("Block", out _))
            .Select(card => Math.Max(0, card.Preview.DynamicVars["Block"].IntValue))
            .DefaultIfEmpty(0)
            .Max();

    private int PowerCountWithBlockVar(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Block", StringComparison.OrdinalIgnoreCase)));

    private int PowerMaxAttackDamage(SearchNode node)
        => PowerLiveCards(node)
            .Where(card => card.Preview.DynamicVars.TryGetValue("Damage", out _))
            .Select(card => Math.Max(0, card.Preview.DynamicVars["Damage"].IntValue))
            .DefaultIfEmpty(0)
            .Max();

    private int PowerTotalAttackDamage(SearchNode node)
    {
        long total = 0;
        foreach (PredictedCard card in PowerLiveCards(node))
        {
            if (card.Preview.Type != CardType.Attack)
                continue;
            if (!card.Preview.DynamicVars.TryGetValue("Damage", out var damageVar))
                continue;
            int hits = card.Preview.DynamicVars.TryGetValue("Repeat", out var repeatVar)
                ? Math.Max(1, repeatVar.IntValue)
                : 1;
            total += Math.Max(0, damageVar.IntValue) * hits;
        }
        return (int)Math.Min(int.MaxValue, total);
    }

    private int PowerCountWithExhaust(SearchNode node)
    {
        CombatPredictionSimulator simulator = node.Snapshot.Simulator;
        return PowerLiveCards(node).Count(card =>
            card.HasKeyword(simulator.State, CardKeyword.Exhaust)
            || card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Exhaust", StringComparison.OrdinalIgnoreCase)));
    }

    private int PowerCountWithVulnerable(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Vulnerable", StringComparison.OrdinalIgnoreCase)));

    private int PowerCountWithWeak(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Weak", StringComparison.OrdinalIgnoreCase)));

    private int PowerCountWithDoom(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Doom", StringComparison.OrdinalIgnoreCase)));

    private int PowerCountWithEthereal(SearchNode node)
    {
        CombatPredictionSimulator simulator = node.Snapshot.Simulator;
        return PowerLiveCards(node).Count(card =>
            card.HasKeyword(simulator.State, CardKeyword.Ethereal));
    }

    private int PowerCountWithStarCost(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.CurrentStarCost > 0 || card.Preview.HasStarCostX);

    private int PowerCountZeroCostAttack(SearchNode node)
    {
        CombatPredictionSimulator simulator = node.Snapshot.Simulator;
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(_player);
        return PowerLiveCards(node).Count(card =>
            card.Preview.Type == CardType.Attack
            && Math.Max(0, card.GetEnergyCostWithModifiers(simulator, state)) == 0);
    }

    private int PowerCountStrike(SearchNode node)
        => PowerLiveCards(node).Count(card => card.Preview.Tags.Contains(CardTag.Strike));

    private int PowerCountWithSelfDamage(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("SelfDamage", StringComparison.OrdinalIgnoreCase)
                || key.Contains("HpLoss", StringComparison.OrdinalIgnoreCase)
                || key.Contains("LoseHp", StringComparison.OrdinalIgnoreCase)));

    private int PowerCountWithStatusVar(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Status", StringComparison.OrdinalIgnoreCase))
            || card.Preview.CanonicalKeywords.Contains(CardKeyword.Ethereal)
                && card.Preview.DynamicVars._vars.Keys.Any(key =>
                    key.Contains("Card", StringComparison.OrdinalIgnoreCase)));

    private int PowerCountOrbChannel(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Orb", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Lightning", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Frost", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Dark", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Glass", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Plasma", StringComparison.OrdinalIgnoreCase)));

    private bool PowerHasStatusOrCurseInHand(SearchNode node)
        => PowerHandCards(node).Any(card => card.Preview.Type is CardType.Status or CardType.Curse);

    private bool PowerHasCardGenerationSource(SearchNode node)
        => PowerCountCardGeneration(node) > 0;

    private int PowerCountCardGeneration(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.Type != CardType.Attack
            && card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Cards", StringComparison.OrdinalIgnoreCase)
                || key.Contains("Generated", StringComparison.OrdinalIgnoreCase)));

    private bool PowerHasForgeSource(SearchNode node)
        => PowerLiveCards(node).Any(card =>
            card.Preview.Id.Entry is "FURNACE" or "SEEKING_EDGE"
            || card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Forge", StringComparison.OrdinalIgnoreCase)));

    private bool PowerHasSovereignBladeSource(SearchNode node)
        => PowerLiveCards(node).Any(card =>
            card.Preview.Id.Entry is "FURNACE" or "SEEKING_EDGE" or "SWORD_SAGE" or "PARRY"
            || card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Sovereign", StringComparison.OrdinalIgnoreCase)));

    private bool PowerHasStarGainSource(SearchNode node)
        => PowerLiveCards(node).Any(card =>
            card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Star", StringComparison.OrdinalIgnoreCase)));

    private int PowerCountSouls(SearchNode node)
        => PowerLiveCards(node).Count(card =>
            card.Preview.Id.Entry.Contains("SOUL", StringComparison.OrdinalIgnoreCase)
            || card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Soul", StringComparison.OrdinalIgnoreCase)));

    private bool PowerHasOstyOrSummonSource(SearchNode node)
        => PowerHasOsty(node)
        || PowerLiveCards(node).Any(card =>
            card.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Summon", StringComparison.OrdinalIgnoreCase))
            || card.Preview.Id.Entry.Contains("OSTY", StringComparison.OrdinalIgnoreCase));
}
