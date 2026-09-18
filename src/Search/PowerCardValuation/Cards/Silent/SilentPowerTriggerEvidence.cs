using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class CombatBeamSolver
{
    private bool SilentPowerHasTriggerEvidence(
        string cardId,
        SearchNode parent,
        SearchNode child)
    {
        CombatPredictionSimulator simulator = child.Snapshot.Simulator;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        PredictedCard[] liveCards = playerState.Hand.Cards
            .Concat(playerState.DrawPile.Cards)
            .Concat(playerState.DiscardPile.Cards)
            .ToArray();
        int? drawPerTurn = null;
        int? remainingTurns = null;
        int DrawPerTurn() => drawPerTurn ??= PersistentPowerSupport.GetModifiedHandDraw(
            combat,
            _player,
            MegaCrit.Sts2.Core.Combat.CombatManager.baseHandDrawCount);
        int RemainingTurns() => remainingTurns ??= EstimateRemainingTurns(
            child.Snapshot,
            Math.Max(1, DrawPerTurn()));
        bool HasAttack() => liveCards.Any(candidate =>
            candidate.Preview.Type == CardType.Attack);
        bool HasBlockSkill() => liveCards.Any(candidate =>
            candidate.Preview.Type == CardType.Skill
            && candidate.Preview.DynamicVars._vars.Keys.Any(key =>
                key.Contains("Block", StringComparison.OrdinalIgnoreCase)));
        bool HasShiv() => liveCards.Any(candidate =>
            candidate.Preview.Tags.Contains(CardTag.Shiv)
            || CardMechanismFacts.ImmediateShivSupply(
                candidate.Preview.Id.Entry,
                DynamicVarValue(candidate, "Cards"),
                DynamicVarValue(candidate, "Shivs")) > 0
            || candidate.Preview.Id.Entry is "INFINITE_BLADES" or "FAN_OF_KNIVES");
        bool HasPoison() => combat.KnownEnemies.Any(enemy =>
                combat.ContainsCreature(enemy)
                && simulator.State.GetCreature(enemy).IsAlive
                && combat.GetAmount<PoisonPower>(enemy) > 0)
            || liveCards.Any(candidate => HasDynamicVar(candidate, "Poison"));
        bool HasWeak() => combat.KnownEnemies.Any(enemy =>
                combat.ContainsCreature(enemy)
                && simulator.State.GetCreature(enemy).IsAlive
                && combat.GetAmount<WeakPower>(enemy) > 0)
            || liveCards.Any(candidate => HasDynamicVar(candidate, "Weak"));
        int ReachableCardPlays() => ConservativeReachableCardPlays(
            simulator,
            combat,
            playerState,
            liveCards,
            RemainingTurns(),
            DrawPerTurn());
        int DrawTriggers() => liveCards.Sum(candidate => SilentCardFlowFacts.DrawCount(
            candidate.Preview.Id.Entry,
            DynamicVarValue(candidate, "Cards"),
            playerState.Hand.Cards.Count));

        return cardId switch
        {
            "ABRASIVE" => HasBlockSkill()
                || child.Snapshot.ProjectedPlayerHp < child.Snapshot.PlayerHp,
            "ACCELERANT" => HasPoison(),
            "ACCURACY" => HasShiv(),
            "AFTERIMAGE" => ReachableCardPlays() >= 5,
            "ENVENOM" => HasAttack() && RemainingTurns() > 1,
            "FAN_OF_KNIVES" => child.Snapshot.AliveEnemyCount > 0,
            "FOOTWORK" => HasBlockSkill(),
            "INFINITE_BLADES" => RemainingTurns() > 1,
            "MASTER_PLANNER" => true,
            "NOXIOUS_FUMES" => RemainingTurns() > 1
                && child.Snapshot.AliveEnemyCount > 0,
            "PHANTOM_BLADES" => HasShiv(),
            "SERPENT_FORM" => ReachableCardPlays() > 0,
            "SPEEDSTER" => DrawTriggers() > 0,
            "TOOLS_OF_THE_TRADE" => RemainingTurns() > 1,
            "TRACKING" => HasWeak() && HasAttack(),
            "WELL_LAID_PLANS" => RemainingTurns() > 1
                && liveCards.Length > 0,
            "WRAITH_FORM" => WraithTimingHasEvidence(
                parent,
                child,
                RemainingTurns()),
            _ => false,
        };
    }

    private bool WraithTimingHasEvidence(
        SearchNode parent,
        SearchNode child,
        int remainingTurns)
    {
        int intangibleTurns = PowerAmountGain<IntangiblePower>(parent, child);
        return intangibleTurns > 0
            && SilentWraithOpeningWindow.ShouldProtect(
                remainingTurns,
                intangibleTurns,
                parent.Snapshot.ProjectedPlayerHp);
    }

    private int SilentPowerTriggerProjectionFloor(string cardId, SearchNode child)
        => cardId switch
        {
            "FAN_OF_KNIVES" => Math.Max(1, child.Snapshot.AliveEnemyCount),
            "FOOTWORK" => 1,
            "INFINITE_BLADES" => 1,
            "NOXIOUS_FUMES" => Math.Max(1, child.Snapshot.AliveEnemyCount),
            "SERPENT_FORM" => 1,
            "SPEEDSTER" => 1,
            "TOOLS_OF_THE_TRADE" => 1,
            "WELL_LAID_PLANS" => 1,
            _ => 0,
        };

    private int ConservativeReachableCardPlays(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        SimPlayerCombatState playerState,
        IReadOnlyList<PredictedCard> liveCards,
        int remainingTurns,
        int drawPerTurn)
    {
        if (remainingTurns <= 0 || liveCards.Count == 0)
            return 0;
        int current = AffordableCardCount(
            playerState.Hand.Cards,
            childEnergy: playerState.Energy,
            drawLimit: playerState.Hand.Cards.Count,
            simulator,
            playerState,
            currentTurn: true);
        int futureTurns = Math.Max(0, remainingTurns - 1);
        if (futureTurns == 0 || drawPerTurn <= 0)
            return current;
        int futureEnergy = PersistentPowerSupport.GetModifiedMaxEnergy(combat, _player);
        int drawIndex = 0;
        int future = 0;
        for (int turn = 0;
             turn < futureTurns && drawIndex < playerState.DrawPile.Cards.Count;
             turn++)
        {
            PredictedCard[] actualDrawWindow = playerState.DrawPile.Cards
                .Skip(drawIndex)
                .Take(drawPerTurn)
                .ToArray();
            future = SaturatingPowerCommitmentAdd(
                future,
                AffordableCardCount(
                    actualDrawWindow,
                    childEnergy: futureEnergy,
                    drawLimit: actualDrawWindow.Length,
                    simulator,
                    playerState,
                    currentTurn: false));
            drawIndex += actualDrawWindow.Length;
        }
        return SaturatingPowerCommitmentAdd(current, future);
    }

    private static int AffordableCardCount(
        IReadOnlyList<PredictedCard> cards,
        int childEnergy,
        int drawLimit,
        CombatPredictionSimulator simulator,
        SimPlayerCombatState playerState,
        bool currentTurn)
    {
        int energy = Math.Max(0, childEnergy);
        int count = 0;
        foreach (int cost in cards
                     .Where(card => !card.HasKeyword(simulator.State, CardKeyword.Unplayable))
                     .Select(card => currentTurn
                         ? Math.Max(0, card.GetEnergyCostWithModifiers(simulator, playerState))
                         : card.Preview.EnergyCost.CostsX
                             ? energy
                             : Math.Max(
                                 0,
                                 (int)Math.Ceiling((double)card.Preview.EnergyCost
                                     .GetWithModifiers(CostModifiers.Local))))
                     .OrderBy(cost => cost)
                     .Take(Math.Max(0, drawLimit)))
        {
            if (cost > energy)
                break;
            energy -= cost;
            count++;
        }
        return count;
    }

    private static bool HasDynamicVar(PredictedCard card, string fragment)
        => card.Preview.DynamicVars._vars.Keys.Any(key =>
            key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static int DynamicVarValue(PredictedCard card, string key)
        => card.Preview.DynamicVars.TryGetValue(key, out var dynamicVar)
            ? dynamicVar.IntValue
            : 0;
}
