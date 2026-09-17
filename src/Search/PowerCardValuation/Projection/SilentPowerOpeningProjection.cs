using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private int SilentPowerOpeningProjectionPotential(
        string cardId,
        SearchNode parent,
        SearchNode child)
        => cardId switch
        {
            "ABRASIVE" => FootworkProjectionPotential(parent, child),
            "ACCELERANT" => AccelerantProjectionPotential(parent, child),
            "ACCURACY" => AccuracyProjectionPotential(parent, child),
            "AFTERIMAGE" => AfterimageProjectionPotential(child),
            "ENVENOM" => EnvenomProjectionPotential(parent, child),
            "FAN_OF_KNIVES" => FanOfKnivesProjectionPotential(parent, child),
            "FOOTWORK" => FootworkProjectionPotential(parent, child),
            "INFINITE_BLADES" => InfiniteBladesProjectionPotential(parent, child),
            "MASTER_PLANNER" => MasterPlannerProjectionPotential(child),
            "NOXIOUS_FUMES" => NoxiousFumesProjectionPotential(parent, child),
            "PHANTOM_BLADES" => PhantomBladesProjectionPotential(parent, child),
            "SERPENT_FORM" => SerpentFormProjectionPotential(parent, child),
            "SPEEDSTER" => SpeedsterProjectionPotential(child),
            "TOOLS_OF_THE_TRADE" => ToolsOfTheTradeProjectionPotential(child),
            "TRACKING" => TrackingProjectionPotential(parent, child),
            "WELL_LAID_PLANS" => WellLaidPlansProjectionPotential(child),
            "WRAITH_FORM" => WraithFormProjectionPotential(parent, child),
            _ => 0,
        };

    private int AfterimageProjectionPotential(SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        PowerTurnCardOption[] options = BuildCurrentHandOptions(child);
        if (options.Length == 0)
            return 0;
        int incomingDamage = Math.Max(
            0,
            child.Snapshot.PlayerHp - child.Snapshot.ProjectedPlayerHp);
        IReadOnlyList<PowerTurnFrontierState> baseline = PowerTurnFrontier.Build(
            child.Snapshot.Energy,
            incomingDamage,
            options);
        IReadOnlyList<PowerTurnFrontierState> powered = PowerTurnFrontier.Build(
            child.Snapshot.Energy,
            incomingDamage,
            options,
            blockPerCardBonus: 1);
        return MarginalFrontierValue(baseline, powered);
    }

    private int FootworkProjectionPotential(SearchNode parent, SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        CombatPredictionSimulator parentSimulator = parent.Snapshot.Simulator;
        CombatPredictionSimulator childSimulator = child.Snapshot.Simulator;
        SimulatedCombatState parentCombat =
            (SimulatedCombatState)parentSimulator.State.CombatState;
        SimulatedCombatState childCombat =
            (SimulatedCombatState)childSimulator.State.CombatState;
        int dexterityGain = Math.Max(
            0,
            childCombat.GetAmount<DexterityPower>(_player.Creature)
                - parentCombat.GetAmount<DexterityPower>(_player.Creature));
        if (dexterityGain == 0)
            return 0;

        PowerTurnCardOption[] options = BuildCurrentHandOptions(child);
        if (options.Length == 0)
            return 0;
        int incomingDamage = Math.Max(
            0,
            child.Snapshot.PlayerHp - child.Snapshot.ProjectedPlayerHp);
        IReadOnlyList<PowerTurnFrontierState> baseline = PowerTurnFrontier.Build(
            child.Snapshot.Energy,
            incomingDamage,
            options);
        IReadOnlyList<PowerTurnFrontierState> powered = PowerTurnFrontier.Build(
            child.Snapshot.Energy,
            incomingDamage,
            options,
            blockPerSkillBonus: dexterityGain);
        return MarginalFrontierValue(baseline, powered);
    }

    private int MasterPlannerProjectionPotential(SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState state = simulator.State.GetPlayerCombatState(_player);
        int drawPerTurn = PersistentPowerSupport.GetModifiedHandDraw(
            combat,
            _player,
            MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount);
        if (drawPerTurn == 0)
            return 0;
        int remainingTurns = EstimateRemainingTurns(child.Snapshot, drawPerTurn);
        int discardWindows = state.Hand.Cards
            .Concat(state.DrawPile.Cards)
            .Concat(state.DiscardPile.Cards)
            .Sum(card => SilentDiscardWindowFacts.Capacity(
                card.Preview.Id.Entry,
                card.Preview.DynamicVars.TryGetValue("Cards", out var cardsVar)
                    ? cardsVar.IntValue
                    : 0,
                state.Hand.Cards.Count));
        discardWindows = SaturatingPowerCommitmentAdd(
            discardWindows,
            combat.GetAmount<ToolsOfTheTradePower>(_player.Creature)
                * Math.Max(0, remainingTurns - 1));
        int futureEnergy = PersistentPowerSupport.GetModifiedMaxEnergy(combat, _player);
        int cycleTurns = Math.Max(
            1,
            (child.Snapshot.LiveDeckSize + drawPerTurn - 1) / drawPerTurn);
        int turnsUntilShuffle = 1
            + (state.DrawPile.Cards.Count + drawPerTurn - 1) / drawPerTurn;
        List<MasterPlannerSkillFact> skills = [];

        void AddSkill(PredictedCard card, int turnsUntilSeed, int turnsUntilPayoff)
        {
            if (card.Preview.Type != CardType.Skill
                || card.Preview.IsSlyThisTurn
                || card.HasKeyword(simulator.State, CardKeyword.Unplayable))
            {
                return;
            }
            int energyCost = turnsUntilSeed == 0
                ? Math.Max(0, card.GetEnergyCostWithModifiers(simulator, state))
                : card.Preview.EnergyCost.CostsX
                    ? futureEnergy
                    : Math.Max(
                        0,
                        (int)Math.Ceiling((double)card.Preview.EnergyCost
                            .GetWithModifiers(CostModifiers.Local)));
            skills.Add(new MasterPlannerSkillFact(
                Math.Max(1, (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))),
                energyCost,
                turnsUntilSeed,
                turnsUntilPayoff));
        }

        foreach (PredictedCard card in state.Hand.Cards)
            AddSkill(card, 0, turnsUntilShuffle);
        for (int index = 0; index < state.DrawPile.Cards.Count; index++)
        {
            int seedTurn = 1 + index / drawPerTurn;
            AddSkill(state.DrawPile.Cards[index], seedTurn, seedTurn + cycleTurns);
        }
        for (int index = 0; index < state.DiscardPile.Cards.Count; index++)
        {
            int seedTurn = turnsUntilShuffle + index / drawPerTurn;
            AddSkill(state.DiscardPile.Cards[index], seedTurn, seedTurn + cycleTurns);
        }
        MasterPlannerProjectionResult projection = MasterPlannerProjection.Evaluate(
            child.Snapshot.Energy,
            futureEnergy,
            remainingTurns,
            discardWindows,
            skills.ToArray());
        return projection.CardAccessValue;
    }

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

    private int SpeedsterProjectionPotential(SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        PowerTurnCardOption[] options = BuildCurrentHandOptions(child);
        if (options.Length == 0)
            return 0;
        int incomingDamage = Math.Max(
            0,
            child.Snapshot.PlayerHp - child.Snapshot.ProjectedPlayerHp);
        IReadOnlyList<PowerTurnFrontierState> baseline = PowerTurnFrontier.Build(
            child.Snapshot.Energy,
            incomingDamage,
            options);
        IReadOnlyList<PowerTurnFrontierState> powered = PowerTurnFrontier.Build(
            child.Snapshot.Energy,
            incomingDamage,
            options,
            damagePerDraw: 2,
            damageTargets: child.Snapshot.AliveEnemyCount);
        return Math.Min(child.Snapshot.EnemyHp, MarginalFrontierValue(baseline, powered));
    }

    private int WellLaidPlansProjectionPotential(SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        RetainedHandCardFact[] retainedCards = playerState.Hand.Cards
            .Select(card => new RetainedHandCardFact(
                Math.Max(0, (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))),
                Math.Max(0, card.GetEnergyCostWithModifiers(simulator, playerState))))
            .ToArray();
        int drawCount = PersistentPowerSupport.GetModifiedHandDraw(
            combat,
            _player,
            MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount);
        int[] nextDrawValues = playerState.DrawPile.Cards
            .Take(drawCount)
            .Select(card => Math.Max(
                0,
                (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))))
            .ToArray();
        RetainedHandTransitionResult transition = RetainedHandTransition.Evaluate(
            PersistentPowerSupport.GetModifiedMaxEnergy(combat, _player),
            combat.GetMaxHandSize(_player),
            drawCount,
            retainedCards,
            nextDrawValues);
        return Math.Max(0, transition.NetValue);
    }

    private int ToolsOfTheTradeProjectionPotential(SearchNode child)
    {
        Interlocked.Increment(ref _run.PowerFrontierEvaluations);
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        int drawCount = PersistentPowerSupport.GetModifiedHandDraw(
            combat,
            _player,
            MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount);
        int remainingTurns = EstimateRemainingTurns(child.Snapshot, drawCount);
        if (remainingTurns <= 1)
            return 0;
        DrawDiscardCardFact[] retainedCards = playerState.Hand.Cards
            .Where(card => card.Preview.ShouldRetainThisTurn)
            .Select(card => new DrawDiscardCardFact(
                Math.Max(0, (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))),
                card.Preview.IsSlyThisTurn
                    ? Math.Max(1, (int)Math.Round(CardChoiceSupport.CardValue(card.Preview)))
                    : 0))
            .ToArray();
        DrawDiscardCardFact[] nextDrawCards = playerState.DrawPile.Cards
            .Take(drawCount + 1)
            .Select(card => new DrawDiscardCardFact(
                Math.Max(0, (int)Math.Round(CardChoiceSupport.CardValue(card.Preview))),
                card.Preview.IsSlyThisTurn
                    ? Math.Max(1, (int)Math.Round(CardChoiceSupport.CardValue(card.Preview)))
                    : 0))
            .ToArray();
        DrawDiscardTransitionResult transition = DrawDiscardTransition.Evaluate(
            combat.GetMaxHandSize(_player),
            drawCount,
            retainedCards,
            nextDrawCards);
        return Math.Max(0, transition.NetValue);
    }

    private PowerTurnCardOption[] BuildCurrentHandOptions(
        SearchNode child,
        int? shivTargetsOverride = null,
        int weakAttackBonusPercent = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(weakAttackBonusPercent);
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        int handCount = playerState.Hand.Cards.Count;
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
        return playerState.Hand.Cards
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
                int cards = card.Preview.DynamicVars.TryGetValue("Cards", out var cardsVar)
                    ? cardsVar.IntValue
                    : 0;
                int draws = SilentCardFlowFacts.DrawCount(
                    card.Preview.Id.Entry,
                    cards,
                    handCount);
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

    private static int MarginalFrontierValue(
        IReadOnlyList<PowerTurnFrontierState> baseline,
        IReadOnlyList<PowerTurnFrontierState> powered)
        => SaturatingPowerCommitmentAdd(
            PowerTurnFrontier.DefensiveDamageUplift(baseline, powered),
            PowerTurnFrontier.DefensiveHpUplift(baseline, powered) * 8);
}
