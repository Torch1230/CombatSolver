using System.Diagnostics;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
using CombatSolver.Engine.InCombat.Mirrors.Orbs;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Death;
using CombatSolver.Engine.InCombat.Simulation;
using BufferCard = MegaCrit.Sts2.Core.Models.Cards.Buffer;

namespace CombatSolver;


internal sealed partial class CombatBeamSolver
{
    private SimulationSnapshot Snapshot(
        CombatPredictionSimulator simulator,
        int turn,
        int actionCount,
        int shufflesCrossed,
        SearchBoundaryReason boundary,
        IReadOnlySet<uint> processedEnemyDeaths)
    {
        SimCreatureState player = simulator.State.GetCreature(_player.Creature);
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        int enemyHp = 0;
        int enemyBlock = 0;
        int rawEnemyHp = 0;
        int maxCurrentEnemyHp = 0;
        int revivingEnemyCount = 0;
        int aliveEnemyCount = 0;
        ulong aliveEnemyMask = 0;
        EnemyDurabilityVectorBuilder enemyDurabilityBuilder =
            new(combat.KnownEnemies.Count);
        StateFingerprintBuilder enemyCombatDistribution = new();
        for (int index = 0; index < combat.KnownEnemies.Count; index++)
        {
            Creature creature = combat.KnownEnemies[index];
            SimCreatureState enemy = simulator.State.GetCreature(creature);
            int effectiveHp = combat.EffectiveEnemyHp(creature, enemy);
            enemyCombatDistribution.Add(creature.CombatId ?? uint.MaxValue);
            enemyCombatDistribution.Add(enemy.CurrentHp);
            enemyCombatDistribution.Add(enemy.Block);
            enemyCombatDistribution.Add(effectiveHp);
            enemyCombatDistribution.Add(combat.ContainsCreature(creature));
            enemyDurabilityBuilder.Set(index, new EnemyDurabilityEntry(
                creature.CombatId ?? uint.MaxValue,
                Math.Max(0, effectiveHp) + Math.Max(0, enemy.Block)));
            enemyHp += effectiveHp;
            if (effectiveHp > 0 && combat.ContainsCreature(creature))
                enemyBlock += Math.Max(0, enemy.Block);
            rawEnemyHp += Math.Max(0, enemy.CurrentHp);
            maxCurrentEnemyHp = Math.Max(maxCurrentEnemyHp, Math.Max(0, enemy.CurrentHp));
            if (enemy.CurrentHp <= 0 && effectiveHp > 0)
                revivingEnemyCount++;
            if (effectiveHp > 0 && combat.ContainsCreature(creature))
            {
                aliveEnemyCount++;
                aliveEnemyMask |= 1UL << index;
            }
        }
        StateFingerprint enemyCombatDistributionKey = enemyCombatDistribution.Finish();
        if (boundary == SearchBoundaryReason.None && combat.HasPendingChoice)
            boundary = SearchBoundaryReason.PendingChoice;
        bool dead = player.IsDead;
        bool won = boundary != SearchBoundaryReason.EventDefeat
            && !dead
            && !combat.HasPendingChoice
            && (!simulator.IsInProgress || simulator.IsEnding);
        CoverageSummary coverage = GetCoverageSummary(simulator);
        IReadOnlyList<PredictionGap> predictionGaps = coverage.Gaps;
        bool risk = coverage.HasUncompensatedRisk;
        bool uncertainVictory = won && predictionGaps.Any(gap =>
            !gap.Compensated && gap.Method.Contains("Death", StringComparison.Ordinal));
        if (uncertainVictory)
            boundary = SearchBoundaryReason.UnsupportedEffect;

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        SearchMeasurement fingerprintMeasurement = _run.Performance.Begin();
        StateFingerprint key = BuildStateKey(
            turn,
            player,
            playerState,
            combat,
            simulator,
            shufflesCrossed,
            processedEnemyDeaths);
        StateFingerprint unorderedPileKey = BuildUnorderedPileKey(playerState);
        StateFingerprint cyclePileShapeKey = BuildCyclePileShapeKey(playerState);
        SearchMeasurement projectedShuffleMeasurement = _run.Performance.Begin();
        // Projected shuffle needs these piles in this exact pre-sort order. The remaining
        // snapshot metrics are order-independent, so they can reuse the shuffled list instead
        // of materializing a second deck-sized backing array.
        List<PredictedCard> liveCards = [
            .. playerState.DiscardPile.Cards,
            .. playerState.DrawPile.Cards,
            .. playerState.Hand.Cards,
        ];
        (StateFingerprint projectedShuffleOrderKey, int projectedShuffleOrderValue) =
            BuildProjectedShuffleOrder(simulator, liveCards);
        _run.Performance.End(
            SearchMetricPhase.ProjectedShuffle,
            projectedShuffleMeasurement);
        _run.Performance.End(SearchMetricPhase.Fingerprint, fingerprintMeasurement);

        int roundIndex = turn - _startTurnNumber;
        SearchMeasurement threatMeasurement = _run.Performance.Begin();
        int projectedHp;
        if (won)
        {
            // A lethal player action ends combat immediately. Enemy intent from that round must
            // never lower the route's projected HP or leak into battle-loss reporting.
            projectedHp = player.CurrentHp;
        }
        else if (boundary != SearchBoundaryReason.None)
        {
            projectedHp = player.CurrentHp;
        }
        else if (!_run.ThreatProjectionCache.TryGetValue((key, roundIndex), out projectedHp))
        {
            projectedHp = ProjectHpAfterThreat(simulator, player, roundIndex);
            _run.ThreatProjectionCache.Add((key, roundIndex), projectedHp);
        }
        _run.Performance.End(SearchMetricPhase.ThreatProjection, threatMeasurement);
        int cumulativePlayerHpLost = combat.GetCumulativeHpLost(_player.Creature);
        double hpWeight = SolverWeights.Hp;
        double score = dead || projectedHp <= 0
            ? SolverWeights.DeathPenalty
            : projectedHp * hpWeight;
        score += (player.MaxHp - root.InitialPlayerMaxHp) * hpWeight;
        score -= cumulativePlayerHpLost * hpWeight;
        int exhaustedTheHunts = playerState.ExhaustPile.Cards.Count(card => card.Preview is TheHunt);
        int rewardedTheHunts = Math.Max(0, combat.GetAmount<TheHuntPower>(_player.Creature));
        int missedTheHuntRewards = Math.Max(0, exhaustedTheHunts - rewardedTheHunts);
        int realizedLongTermResourceValue = combat.LongTermResourceValue;
        int longTermResourceValue = realizedLongTermResourceValue
            - missedTheHuntRewards * CorePowerSupport.TheHuntLongTermResourceValue;
        LongTermGoals longTermGoals = combat.LongTermGoals;
        LongTermGoals longTermGoalCardsPlayed = combat.LongTermGoalCardsPlayed;
        score += realizedLongTermResourceValue * SolverWeights.LongTermResourceBeamValue;
        int angerCopiesGenerated = combat.AngerCopiesGenerated;
        score += angerCopiesGenerated * SolverWeights.AngerCopyBeamPenalty;
        if (won && !uncertainVictory)
            score += SolverWeights.VictoryBonus;
        score += enemyHp * SolverWeights.EnemyHp;
        int liveDeckClutter = liveCards.Count(card =>
            card.Preview.Type is CardType.Status or CardType.Curse);
        score += liveDeckClutter * SolverWeights.LiveDeckClutterPenalty;
        int outstandingStolenResource = TheftEncounterStrategy.OutstandingStolenResource(simulator, combat);
        if (_theftPolicy == SolverTheftPolicy.PreserveResources)
            score += outstandingStolenResource * SolverWeights.OutstandingStolenResourcePenalty;
        int retainedAttackValue = 0;
        foreach (PredictedCard liveCard in liveCards)
        {
            if (liveCard.Preview.Type != CardType.Attack)
                continue;
            retainedAttackValue += Math.Max(
                1,
                (int)Math.Round(CardChoiceSupport.CardValue(liveCard.Preview)));
        }
        ThreatFocus focus = BuildThreatFocus(simulator, combat);
        IReadOnlyList<PowerModel> effectivePowers = combat.EffectivePowers();
        StrategicEffectRequirements strategicRequirements = StrategicEffectRequirements.None;
        for (int powerIndex = 0; powerIndex < effectivePowers.Count; powerIndex++)
        {
            PowerModel power = effectivePowers[powerIndex];
            if (!ReferenceEquals(power.Owner, _player.Creature)
                || power.Amount <= 0
                || power.TypeForCurrentAmount != PowerType.Buff
                || power is ITemporaryPower)
            {
                continue;
            }
            strategicRequirements |= StrategicEffectModel.Requirements(power);
        }
        StrategicEffectContext? strategicContext = null;
        StrategicEffectVector strategicEffects = StrategicEffectVector.Zero;
        int offensivePersistentBuffValue = 0;
        PersistentSetupTraits persistentSetupTraits = PersistentSetupTraits.None;
        for (int powerIndex = 0; powerIndex < effectivePowers.Count; powerIndex++)
        {
            PowerModel power = effectivePowers[powerIndex];
            if (!ReferenceEquals(power.Owner, _player.Creature)
                || power.Amount <= 0
                || power.TypeForCurrentAmount != PowerType.Buff
                || power is ITemporaryPower)
            {
                continue;
            }
            strategicContext ??= StrategicEffectContext.Build(
                liveCards,
                enemyHp,
                focus.TotalThreat,
                focus.IncomingHitCount,
                simulator.State.GetCreature(_player.Creature).Block,
                strategicRequirements);
            StrategicEffectVector effect = StrategicEffectModel.Evaluate(
                power,
                strategicContext.Value);
            strategicEffects += effect;
            offensivePersistentBuffValue += effect.DamagePotential + effect.ScalingPotential;
            persistentSetupTraits |= PersistentPowerSetupTrait(power);
        }
        int persistentBuffValue = strategicEffects.RetentionValue;
        if (playerState.OrbQueue.Orbs.Count > 0)
        {
            persistentSetupTraits |= PersistentSetupTraits.OrbEngine;
            persistentBuffValue += OrbRetentionValue(
                simulator,
                playerState.OrbQueue.Orbs,
                aliveEnemyCount);
        }
        strategicContext ??= StrategicEffectContext.Build(
            liveCards,
            enemyHp,
            focus.TotalThreat,
            focus.IncomingHitCount,
            simulator.State.GetCreature(_player.Creature).Block,
            strategicRequirements
                | StrategicEffectRequirements.RemainingTurns
                | StrategicEffectRequirements.BlockSkillPlays
                | StrategicEffectRequirements.BestCardValue);
        int latentSetupValue = 0;
        PersistentSetupTraits latentSetupTraits = PersistentSetupTraits.None;
        foreach (PredictedCard latentCard in liveCards)
        {
            CardModel preview = latentCard.Preview;
            PersistentSetupTraits trait = LatentCardSetupTrait(preview);
            latentSetupTraits |= trait;
            if (trait == PersistentSetupTraits.None
                || !persistentSetupTraits.HasFlag(trait))
            {
                latentSetupValue = Math.Min(
                    SolverWeights.LatentSetupBeamCap,
                    latentSetupValue + LatentCardSetupValue(preview, strategicContext.Value));
            }
        }
        int replayPotentialValue = ReplayPotentialValue(liveCards);
        int retainedHandValue = playerState.Hand.Cards
            .Where(card => card.Preview.ShouldRetainThisTurn)
            .Sum(card => Math.Max(
                1,
                (int)Math.Ceiling(CardChoiceSupport.CardValue(card.Preview) * 2d)));
        int freeCardOpportunityValue = FreeCardOpportunityValue(
            simulator,
            combat,
            playerState,
            _player.Creature)
            + VoidFormOpportunityValue(
                simulator,
                combat,
                playerState,
                _player.Creature);
        int summonNextTurn = combat.GetAmount<SummonNextTurnPower>(_player.Creature);
        int summonNextTurnValue = summonNextTurn == 0
            ? 0
            : summonNextTurn
                * (4 + Math.Min(12, liveCards.Count(card =>
                    card.Preview.Tags.Contains(CardTag.OstyAttack)) * 2));
        int futureResourceValue = combat.GetAmount<EnergyNextTurnPower>(_player.Creature) * 16
            + combat.GetAmount<DrawCardsNextTurnPower>(_player.Creature) * 8
            + combat.GetAmount<StarNextTurnPower>(_player.Creature) * 8
            + combat.GetAmount<RetainHandPower>(_player.Creature) * 4
            + summonNextTurnValue
            + retainedHandValue
            + freeCardOpportunityValue;
        Creature? currentOsty = combat.GetOsty(_player);
        int ostyHp = currentOsty == null
            ? 0
            : simulator.State.GetCreature(currentOsty).CurrentHp;
        int ostyMaxHp = combat.GetOstyMaxHp(simulator, _player);
        persistentBuffValue += liveCards.Count(card => card.Preview is Soul);
        int delayedDamageValue = combat.KnownEnemies
            .Where(enemy => combat.ContainsCreature(enemy) && simulator.State.GetCreature(enemy).IsAlive)
            .Sum(enemy =>
            {
                int poison = Math.Max(0, combat.GetAmount<PoisonPower>(enemy));
                int demise = Math.Max(0, combat.GetAmount<DemisePower>(enemy));
                int doom = Math.Max(0, combat.GetAmount<DoomPower>(enemy));
                int currentHp = Math.Max(0, simulator.State.GetCreature(enemy).CurrentHp);
                int cappedDoom = Math.Min(currentHp, doom);
                int doomProgress = currentHp <= 0
                    ? 0
                    : (int)Math.Min(currentHp, (long)cappedDoom * cappedDoom / currentHp);
                return poison + demise + doomProgress;
            });
        int reactiveDamageValue = Math.Max(
            0,
            combat.GetAmount<SleightOfFleshPower>(_player.Creature));
        bool hasBlockDamagePayoff = liveCards.Any(card => card.Preview is BodySlam);
        int offensiveProgressValue = offensivePersistentBuffValue
            + delayedDamageValue
            + reactiveDamageValue
            + (hasBlockDamagePayoff ? Math.Max(0, player.Block) : 0);
        int enemyStrengthSuppression = 0;
        int enemyWeakTurns = 0;
        int vulnerable = 0;
        int focusTargetVulnerableTurns = 0;
        uint? mostVulnerableTargetCombatId = null;
        int mostVulnerableTurns = 0;
        StateFingerprintBuilder enemyControlDistribution = new();
        foreach (Creature enemy in combat.KnownEnemies)
        {
            if (!combat.ContainsCreature(enemy) || !simulator.State.GetCreature(enemy).IsAlive)
                continue;
            int strengthSuppression = -combat.GetAmount<StrengthPower>(enemy);
            int weakTurns = Math.Max(0, combat.GetAmount<WeakPower>(enemy));
            int vulnerableTurns = Math.Max(0, combat.GetAmount<VulnerablePower>(enemy));
            enemyStrengthSuppression += strengthSuppression;
            enemyWeakTurns += weakTurns;
            vulnerable += vulnerableTurns;
            if (enemy.CombatId == focus.CombatId)
                focusTargetVulnerableTurns = vulnerableTurns;
            if (vulnerableTurns > mostVulnerableTurns)
            {
                mostVulnerableTurns = vulnerableTurns;
                mostVulnerableTargetCombatId = enemy.CombatId;
            }
            enemyControlDistribution.Add(enemy.CombatId ?? uint.MaxValue);
            enemyControlDistribution.Add(strengthSuppression);
            enemyControlDistribution.Add(weakTurns);
            enemyControlDistribution.Add(vulnerableTurns);
        }
        StateFingerprint enemyControlDistributionKey = enemyControlDistribution.Finish();
        int sandpitRemaining = combat.EffectivePowers()
            .OfType<SandpitPower>()
            .Where(power => ReferenceEquals(power.Target, _player.Creature)
                && simulator.State.GetCreature(power.Owner).IsAlive)
            .Sum(power => Math.Max(0, power.Amount));
        int vulnerableAttackWindow = Math.Min(
            SolverWeights.VulnerableAttackWindowCap,
            retainedAttackValue);
        score += (long)focusTargetVulnerableTurns
            * vulnerableAttackWindow
            * SolverWeights.VulnerableAttackMultiplierBeamValue;
        score += (long)Math.Max(0, vulnerable - focusTargetVulnerableTurns)
            * vulnerableAttackWindow
            * SolverWeights.OffTargetVulnerableAttackMultiplierBeamValue;
        score += actionCount * SolverWeights.ActionPenalty;
        if (risk)
            score += SolverWeights.RiskPenalty;

        combat.TryGetPocketwatchState(
            _player,
            out int pocketwatchCardsPlayedThisTurn,
            out int pocketwatchCardsPlayedLastTurn,
            out int pocketwatchCardThreshold);
        int potionUseCount = combat.PotionUses.Count;
        int potionStrategicCost = combat.PotionUses.Sum(use => use.StrategicHpCost);
        int automaticPotionUseCount = combat.PotionUses.Count(use => use.Automatic);
        (int reachableHandValue, int zeroCostPlayableCount) =
            CalculateReachableHandPotential(simulator, combat, playerState);
        StateFingerprint potionInventoryKey = BuildPotionInventoryKey(combat);
        StateFingerprint cycleShapeKey = BuildCycleShapeKey(
            cyclePileShapeKey,
            aliveEnemyMask,
            potionInventoryKey,
            boundary);
        return new SimulationSnapshot(
            score,
            key,
            unorderedPileKey,
            cycleShapeKey,
            projectedShuffleOrderKey,
            projectedShuffleOrderValue,
            risk,
            dead,
            won,
            player.CurrentHp,
            player.MaxHp,
            cumulativePlayerHpLost,
            longTermResourceValue,
            longTermGoals,
            longTermGoalCardsPlayed,
            angerCopiesGenerated,
            projectedHp,
            player.Block,
            enemyHp,
            enemyBlock,
            aliveEnemyCount,
            aliveEnemyMask,
            rawEnemyHp,
            maxCurrentEnemyHp,
            enemyCombatDistributionKey,
            enemyDurabilityBuilder.Build(),
            revivingEnemyCount,
            persistentBuffValue,
            strategicEffects,
            persistentSetupTraits,
            latentSetupValue,
            latentSetupTraits,
            focus.CombatId,
            focus.Pressure,
            focus.RemainingHp,
            focus.CurrentThreat,
            focusTargetVulnerableTurns,
            mostVulnerableTargetCombatId,
            retainedAttackValue,
            replayPotentialValue,
            futureResourceValue,
            ostyHp,
            ostyMaxHp,
            delayedDamageValue,
            reactiveDamageValue,
            enemyStrengthSuppression,
            enemyWeakTurns,
            vulnerable,
            enemyControlDistributionKey,
            sandpitRemaining,
            liveDeckClutter,
            liveCards.Count,
            outstandingStolenResource,
            offensiveProgressValue,
            playerState.Energy,
            playerState.Stars,
            simulator.History.Entries.Count,
            playerState.Hand.Cards.Count,
            reachableHandValue,
            zeroCostPlayableCount,
            combat.CanTriggerArtOfWarNextTurn(_player),
            pocketwatchCardsPlayedThisTurn,
            pocketwatchCardsPlayedLastTurn,
            pocketwatchCardThreshold,
            potionUseCount,
            potionStrategicCost,
            automaticPotionUseCount,
            turn,
            shufflesCrossed,
            processedEnemyDeaths,
            boundary,
            predictionGaps,
            simulator);
    }

    private static StateFingerprint BuildCycleShapeKey(
        StateFingerprint pileShapeKey,
        ulong aliveEnemyMask,
        StateFingerprint potionInventoryKey,
        SearchBoundaryReason boundary)
    {
        StateFingerprintBuilder key = new();
        key.Add(pileShapeKey.First);
        key.Add(pileShapeKey.Second);
        key.Add(aliveEnemyMask);
        key.Add(potionInventoryKey.First);
        key.Add(potionInventoryKey.Second);
        key.Add((int)boundary);
        return key.Finish();
    }

    private static StateFingerprint BuildCyclePileShapeKey(
        SimPlayerCombatState playerState)
    {
        StateFingerprintBuilder key = new();
        AppendCyclePileShape(ref key, playerState.Hand, 'H');
        AppendCyclePileShape(ref key, playerState.DrawPile, 'D');
        AppendCyclePileShape(ref key, playerState.DiscardPile, 'C');
        AppendCyclePileShape(ref key, playerState.ExhaustPile, 'X');
        return key.Finish();
    }

    private static void AppendCyclePileShape(
        ref StateFingerprintBuilder key,
        SimCardPile pile,
        char marker)
    {
        // Cycle detection deliberately uses structural card identity only. Full card state
        // remains in StateKey and in every replayed PlanAction; ignoring mutable counters here
        // lets a bounded probe observe setup loops whose payoff appears only after N plays.
        if (!pile.TryGetCachedCycleShapeFingerprint(out ulong first, out ulong second))
        {
            first = 0;
            second = 0;
            foreach (PredictedCard card in pile.Cards)
            {
                CardModel preview = card.Preview;
                StateFingerprintBuilder cardKeyBuilder = new();
                cardKeyBuilder.Add(preview.Id.Entry);
                cardKeyBuilder.Add(preview.CurrentUpgradeLevel);
                StateFingerprint cardKey = cardKeyBuilder.Finish();
                first += StateFingerprintBuilder.MixFirst(cardKey.First);
                second += StateFingerprintBuilder.MixSecond(cardKey.Second);
            }
            pile.SetCachedCycleShapeFingerprint(first, second);
        }
        key.Add(marker);
        key.Add(pile.Cards.Count);
        key.Add(first);
        key.Add(second);
    }

    private StateFingerprint BuildPotionInventoryKey(SimulatedCombatState combat)
    {
        StateFingerprintBuilder key = new();
        key.Add(root.PotionSlotCount);
        for (int slot = 0; slot < root.PotionSlotCount; slot++)
        {
            PotionModel? potion = combat.GetPotionAtSlot(_player, slot);
            key.Add(potion?.Id.Entry ?? "-");
            key.Add(potion != null && combat.IsPotionAvailable(_player, slot));
        }
        return key.Finish();
    }

    private static (int Value, int ZeroCostPlayableCount) CalculateReachableHandPotential(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        SimPlayerCombatState playerState)
    {
        List<(int Energy, int Stars, int Value)> playable = [];
        int totalEnergyCost = 0;
        int totalStarCost = 0;
        int zeroCostPlayableCount = 0;
        foreach (PredictedCard card in playerState.Hand)
        {
            if (!combat.CanPlayCard(simulator, card))
                continue;
            int energyCost = card.Preview.EnergyCost.CostsX
                ? Math.Max(0, playerState.Energy)
                : Math.Max(0, card.GetEnergyCostWithModifiers(simulator, playerState));
            int starCost = card.Preview.HasStarCostX
                ? Math.Max(0, playerState.Stars)
                : Math.Max(0, card.GetStarCostWithModifiers(simulator, playerState));
            int value = Math.Max(1, (int)Math.Ceiling(CardChoiceSupport.CardValue(card.Preview)));
            playable.Add((energyCost, starCost, value));
            totalEnergyCost += energyCost;
            totalStarCost += starCost;
            if (energyCost == 0
                && starCost == 0
                && !card.Preview.EnergyCost.CostsX
                && !card.Preview.HasStarCostX)
            {
                zeroCostPlayableCount++;
            }
        }

        int energyCapacity = Math.Min(Math.Max(0, playerState.Energy), totalEnergyCost);
        int starCapacity = Math.Min(Math.Max(0, playerState.Stars), totalStarCost);
        int[,] best = new int[energyCapacity + 1, starCapacity + 1];
        foreach ((int energyCost, int starCost, int value) in playable)
        {
            for (int energy = energyCapacity; energy >= energyCost; energy--)
            for (int stars = starCapacity; stars >= starCost; stars--)
            {
                best[energy, stars] = Math.Max(
                    best[energy, stars],
                    best[energy - energyCost, stars - starCost] + value);
            }
        }
        return (best[energyCapacity, starCapacity], zeroCostPlayableCount);
    }

    /// <summary>
    /// Void Form waives the cost of the first N cards played each turn. Unlike the free-attack powers it is not
    /// limited to one card type and it waives stars as well as energy, so the slots are worth what the most
    /// expensive cards in hand would have cost - not what the whole hand would have cost.
    /// </summary>
    /// <remarks>
    /// Without this the hand looks strictly cheaper than it is. The cost hook only asks whether any free slot is
    /// left, not which card would occupy it, so while the counter is below the amount every card in hand reports
    /// zero and <see cref="CalculateReachableHandPotential"/> concludes the entire hand is affordable.
    ///
    /// X-cost cards are excluded on purpose. <c>GetStarCostWithModifiers</c> and its energy counterpart return the
    /// whole resource pool for them before the cost-modifier hook runs, so an X card still spends everything
    /// inside the free window: the slot is consumed and buys nothing. Counting it here would recreate the same
    /// over-statement one card at a time.
    /// </remarks>
    private static int VoidFormOpportunityValue(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        SimPlayerCombatState playerState,
        Creature owner)
    {
        if (combat.GetPower<VoidFormPower>(owner) is not { } power)
            return 0;
        // Peek so that merely scoring a state does not make every later fork copy the counter.
        int freeUses = power.Amount
            - simulator.StateStore.Peek(power, () => new VoidFormPredictionState(power)).CardsPlayedThisTurn;
        if (freeUses <= 0)
            return 0;

        return playerState.Hand.Cards
            .Where(card => !card.Preview.EnergyCost.CostsX
                && !card.Preview.HasStarCostX
                && combat.CanPlayCard(simulator, card))
            .Select(card =>
            {
                int normalEnergy = Math.Max(
                    0,
                    (int)Math.Ceiling((double)card.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local)));
                int currentEnergy = Math.Max(0, card.GetEnergyCostWithModifiers(simulator, playerState));
                int normalStars = Math.Max(0, card.Preview.CurrentStarCost);
                int currentStars = Math.Max(0, card.GetStarCostWithModifiers(simulator, playerState));
                return Math.Max(0, normalEnergy - currentEnergy) * 16
                    + Math.Max(0, normalStars - currentStars) * 8
                    + Math.Min(16, Math.Max(1, (int)Math.Ceiling(CardChoiceSupport.CardValue(card.Preview))));
            })
            .OrderDescending()
            .Take(freeUses)
            .Sum();
    }

    internal static int CaptureVoidFormOpportunityValueForTesting(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        SimPlayerCombatState playerState,
        Creature owner)
        => VoidFormOpportunityValue(simulator, combat, playerState, owner);

    private static int FreeCardOpportunityValue(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        SimPlayerCombatState playerState,
        Creature owner)
        => FreeCardOpportunityValue(
                simulator,
                combat,
                playerState,
                CardType.Attack,
                combat.GetAmount<FreeAttackPower>(owner))
            + FreeCardOpportunityValue(
                simulator,
                combat,
                playerState,
                CardType.Skill,
                combat.GetAmount<FreeSkillPower>(owner))
            + FreeCardOpportunityValue(
                simulator,
                combat,
                playerState,
                CardType.Power,
                combat.GetAmount<FreePowerPower>(owner));

    private static int FreeCardOpportunityValue(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        SimPlayerCombatState playerState,
        CardType cardType,
        int freeUses)
    {
        if (freeUses <= 0)
            return 0;

        return playerState.Hand.Cards
            .Where(card => card.Preview.Type == cardType
                && !card.Preview.EnergyCost.CostsX
                && combat.CanPlayCard(simulator, card))
            .Select(card =>
            {
                int normalCost = Math.Max(
                    0,
                    (int)Math.Ceiling((double)card.Preview.EnergyCost.GetWithModifiers(CostModifiers.Local)));
                int currentCost = Math.Max(0, card.GetEnergyCostWithModifiers(simulator, playerState));
                int savedEnergy = Math.Max(0, normalCost - currentCost);
                return savedEnergy * 16
                    + Math.Min(16, Math.Max(1, (int)Math.Ceiling(CardChoiceSupport.CardValue(card.Preview))));
            })
            .OrderDescending()
            .Take(freeUses)
            .Sum();
    }

    private StateFingerprint BuildUnorderedPileKey(SimPlayerCombatState playerState)
    {
        StateFingerprintBuilder key = new();
        AppendUnorderedPile(ref key, playerState.Hand, 'H');
        AppendUnorderedPile(ref key, playerState.DrawPile, 'D');
        AppendUnorderedPile(ref key, playerState.DiscardPile, 'C');
        AppendUnorderedPile(ref key, playerState.ExhaustPile, 'X');
        return key.Finish();
    }

    private void AppendUnorderedPile(
        ref StateFingerprintBuilder key,
        SimCardPile pile,
        char marker)
    {
        ulong first = 0;
        ulong second = 0;
        foreach (PredictedCard card in pile)
        {
            StateFingerprint cardKey = BuildCardStateFingerprint(card);
            first += StateFingerprintBuilder.MixFirst(cardKey.First);
            second += StateFingerprintBuilder.MixSecond(cardKey.Second);
        }
        key.Add(marker);
        key.Add(pile.Cards.Count);
        key.Add(first);
        key.Add(second);
    }

    private (StateFingerprint Key, int Value) BuildProjectedShuffleOrder(
        CombatPredictionSimulator simulator,
        List<PredictedCard> cards)
    {
        // StableShuffle sorts a second List copy before shuffling. This list is already private
        // to the snapshot, so performing the same sort and shuffle in place avoids
        // another deck-sized backing array without changing RNG consumption or ordering.
        var shuffleRng = simulator.Rng.Shuffle.Clone();
        StableShuffleProjection(cards, shuffleRng);

        StateFingerprintBuilder key = new();
        key.Add(simulator.Rng.Shuffle.Counter());
        key.Add(cards.Count);
        int value = 0;
        for (int index = 0; index < cards.Count; index++)
        {
            StateFingerprint cardKey = BuildCardStateFingerprint(cards[index]);
            key.Add(cardKey.First);
            key.Add(cardKey.Second);
            value += (int)Math.Round(
                CardChoiceSupport.CardValue(cards[index].Preview) * (cards.Count - index));
        }
        return (key.Finish(), value);
    }

    internal static void StableShuffleProjection(
        List<PredictedCard> cards,
        MegaCrit.Sts2.Core.Random.Rng rng)
    {
        cards.Sort();
        cards.UnstableShuffle(rng);
    }

    private static int ReplayPotentialValue(IEnumerable<PredictedCard> cards)
    {
        int total = 0;
        foreach (PredictedCard card in cards)
        {
            int replayCount = Math.Max(0, card.Preview.GetEnchantedReplayCount());
            if (replayCount == 0
                || card.Preview.Type is not (CardType.Attack or CardType.Skill or CardType.Power))
            {
                continue;
            }

            double perPlayValue = Math.Max(4d, CardChoiceSupport.CardValue(card.Preview));
            total += (int)Math.Ceiling(perPlayValue * replayCount);
            if (total >= SolverWeights.ReplayPotentialBeamCap)
                return SolverWeights.ReplayPotentialBeamCap;
        }
        return total;
    }

    private static PersistentSetupTraits PersistentPowerSetupTrait(PowerModel power)
        => power switch
        {
            CuriousPower => PersistentSetupTraits.Curious,
            EchoFormPower => PersistentSetupTraits.EchoForm,
            BufferPower => PersistentSetupTraits.Buffer,
            FocusPower => PersistentSetupTraits.Focus,
            ThunderPower => PersistentSetupTraits.Thunder,
            LightningRodPower => PersistentSetupTraits.OrbEngine,
            DemonFormPower or CreativeAiPower => PersistentSetupTraits.RecurringScaling,
            BarricadePower or RagePower or UnmovablePower or BlockNextTurnPower or DexterityPower
                => PersistentSetupTraits.BlockEngine,
            StratagemPower or WellLaidPlansPower => PersistentSetupTraits.FutureChoice,
            _ => PersistentSetupTraits.None,
        };

    private static int OrbRetentionValue(
        CombatPredictionSimulator simulator,
        IReadOnlyList<OrbModel> orbs,
        int aliveEnemyCount)
    {
        decimal value = 0m;
        foreach (OrbModel orb in orbs)
        {
            decimal passive = Math.Max(0m, OrbMirrors.GetPassiveValue(simulator, orb));
            value += orb switch
            {
                GlassOrb => passive * aliveEnemyCount,
                LightningOrb or FrostOrb or DarkOrb or PlasmaOrb => passive,
                _ => passive,
            };
        }
        return checked((int)Math.Ceiling(value));
    }

    private static int LatentCardSetupValue(
        CardModel card,
        StrategicEffectContext context)
        => card switch
        {
            Rage => Math.Min(
                SolverWeights.LatentSetupBeamCap,
                context.AttackPlays
                    * Math.Max(
                        1,
                        (int)Math.Ceiling(
                            CardChoiceSupport.DynamicVarBaseValue(
                                card.DynamicVars,
                                "Power")))),
            Prolong => Math.Min(
                SolverWeights.LatentSetupBeamCap,
                context.PlayerBlock),
            EchoForm => 12,
            BufferCard => 8,
            MadScience madScience when madScience.TinkerTimeType == CardType.Power
                && madScience.TinkerTimeRider == TinkerTime.RiderEffect.Curious => 10,
            MadScience madScience when madScience.TinkerTimeType == CardType.Power => 5,
            Defragment or Hotfix => 6,
            LightningRod => 4,
            Thunder => 3,
            DemonForm or CreativeAi => 5,
            MasterPlanner => 4,
            _ when card.Type == CardType.Power => 2,
            _ => 0,
        };

    private static PersistentSetupTraits LatentCardSetupTrait(CardModel card)
        => card switch
        {
            EchoForm => PersistentSetupTraits.EchoForm,
            BufferCard => PersistentSetupTraits.Buffer,
            MadScience madScience when madScience.TinkerTimeType == CardType.Power
                && madScience.TinkerTimeRider == TinkerTime.RiderEffect.Curious
                => PersistentSetupTraits.Curious,
            MadScience madScience when madScience.TinkerTimeType == CardType.Power
                => PersistentSetupTraits.RecurringScaling,
            Defragment or Hotfix => PersistentSetupTraits.Focus,
            LightningRod => PersistentSetupTraits.OrbEngine,
            Thunder => PersistentSetupTraits.Thunder,
            DemonForm or CreativeAi => PersistentSetupTraits.RecurringScaling,
            _ => PersistentSetupTraits.None,
        };

    private static ThreatFocus BuildThreatFocus(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat)
    {
        IReadOnlyList<ForecastMove> moves = combat.CurrentMonsterMoves();
        uint? bestCombatId = null;
        int bestPressure = 0;
        int bestRemainingHp = int.MaxValue;
        int bestThreat = 0;
        int totalThreat = 0;
        int incomingHitCount = 0;
        foreach (Creature enemy in combat.KnownEnemies)
        {
            SimCreatureState enemyState = simulator.State.GetCreature(enemy);
            if (!combat.ContainsCreature(enemy)
                || !enemyState.IsAlive
                || enemy.CombatId is not uint combatId)
            {
                continue;
            }

            int remainingHp = Math.Max(0, enemyState.CurrentHp);
            int maxHp = Math.Max(1, enemyState.MaxHp);
            int currentThreat = 0;
            if (!combat.WillSkipNextMove(enemy))
            {
                foreach (ForecastMove move in moves)
                {
                    if (!ReferenceEquals(move.Owner, enemy))
                        continue;
                    foreach (ForecastAttackHit hit in move.AttackHits)
                    {
                        incomingHitCount++;
                        currentThreat += Math.Max(
                            0,
                            combat.AdjustMonsterMoveDamage(enemy, move.Move.Id, hit.BaseDamage));
                    }
                }
            }
            totalThreat += currentThreat;
            int progress = Math.Max(0, maxHp - remainingHp);
            if (progress == 0)
                continue;

            int pressure = (int)Math.Min(
                int.MaxValue,
                (long)progress * (16 + Math.Min(64, currentThreat)) * 1024 / maxHp);
            if (pressure < bestPressure
                || pressure == bestPressure && currentThreat < bestThreat
                || pressure == bestPressure && currentThreat == bestThreat && remainingHp >= bestRemainingHp)
            {
                continue;
            }
            bestCombatId = combatId;
            bestPressure = pressure;
            bestRemainingHp = remainingHp;
            bestThreat = currentThreat;
        }
        return new ThreatFocus(
            bestCombatId,
            bestPressure,
            bestRemainingHp,
            bestThreat,
            totalThreat,
            incomingHitCount);
    }

    private CoverageSummary GetCoverageSummary(CombatPredictionSimulator simulator)
    {
        if (!simulator.HasRisk)
            return CoverageSummary.None;
        PredictionRiskSignature signature = simulator.History.RiskSignature;
        if (_run.CoverageCache.TryGetValue(signature, out CoverageSummary? cached))
            return cached;
        IReadOnlyList<PredictionGap> gaps = PredictionCoverage.Collect(simulator);
        CoverageSummary summary = new(gaps, gaps.Any(gap => !gap.Compensated));
        _run.CoverageCache.Add(signature, summary);
        return summary;
    }

    private int ProjectHpAfterThreat(
        CombatPredictionSimulator simulator,
        SimCreatureState player,
        int roundIndex)
    {
        int hp = player.CurrentHp;
        int block = player.Block;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        Creature? osty = simulatedCombat.GetOsty(_player);
        int ostyHp = osty == null ? 0 : simulator.State.GetCreature(osty).CurrentHp;
        ProjectedDeathPrevention deathPrevention = BuildProjectedDeathPrevention(
            simulator,
            simulatedCombat,
            player.MaxHp);
        IReadOnlyList<ForecastMove> moves = simulatedCombat.CurrentMonsterMoves();
        foreach (ForecastMove move in moves)
        {
            if (!simulator.State.GetCreature(move.Owner).IsAlive)
                continue;
            if (simulatedCombat.WillSkipNextMove(move.Owner))
                continue;
            if (simulatedCombat.TryGetForcedMoveId(move.Owner, out string forcedMove))
            {
                if (forcedMove != "EXPLODE_MOVE"
                    || !simulatedCombat.TryGetForcedAttackDamage(move.Owner, out int forcedDamage))
                {
                    continue;
                }
                ProjectThreatHit(
                    simulator,
                    simulatedCombat,
                    move.Owner,
                    forcedDamage,
                    osty,
                    ref ostyHp,
                    ref block,
                    ref hp,
                    ref deathPrevention);
                continue;
            }
            foreach (ForecastAttackHit hit in move.AttackHits)
            {
                int baseDamage = simulatedCombat.AdjustMonsterMoveDamage(move.Owner, move.Move.Id, hit.BaseDamage);
                ProjectThreatHit(
                    simulator,
                    simulatedCombat,
                    move.Owner,
                    baseDamage,
                    osty,
                    ref ostyHp,
                    ref block,
                    ref hp,
                    ref deathPrevention);
            }
        }
        return hp;
    }

    private void ProjectThreatHit(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        Creature attacker,
        int baseDamage,
        Creature? osty,
        ref int ostyHp,
        ref int block,
        ref int playerHp,
        ref ProjectedDeathPrevention deathPrevention)
    {
        int adjustedHit = CorePowerSupport.AdjustForecastAttack(
            simulator,
            combat,
            attacker,
            _player.Creature,
            baseDamage);
        int blocked = Math.Min(block, adjustedHit);
        block -= blocked;
        decimal hpLoss = HookMirrors.ModifyHpLost(
            simulator,
            _player.Creature,
            adjustedHit - blocked,
            ValueProp.Move,
            attacker,
            null,
            HpLossHookPhase.BeforeOsty,
            out _);
        Creature target = Hook.ModifyUnblockedDamageTarget(
            combat,
            _player.Creature,
            hpLoss,
            ValueProp.Move,
            attacker);
        if (ReferenceEquals(target, _player.Creature)
            && osty is not null
            && ostyHp > 0
            && combat.GetAmount<DieForYouPower>(osty) > 0)
        {
            target = osty;
        }
        if (ReferenceEquals(target, osty) && ostyHp <= 0)
            target = _player.Creature;
        hpLoss = HookMirrors.ModifyHpLost(
            simulator,
            target,
            hpLoss,
            ValueProp.Move,
            attacker,
            null,
            HpLossHookPhase.AfterOsty,
            out _);
        int loss = Math.Max(0, (int)Math.Floor(hpLoss));
        if (ReferenceEquals(target, osty))
        {
            int absorbed = Math.Min(ostyHp, loss);
            ostyHp -= absorbed;
            playerHp -= loss - absorbed;
            deathPrevention.TryRevive(ref playerHp);
            return;
        }
        playerHp -= loss;
        deathPrevention.TryRevive(ref playerHp);
    }

    private ProjectedDeathPrevention BuildProjectedDeathPrevention(
        CombatPredictionSimulator simulator,
        SimulatedCombatState combat,
        int playerMaxHp)
    {
        int fairyCount = 0;
        for (int slot = 0; slot < root.PotionSlotCount; slot++)
        {
            if (combat.GetPotionAtSlot(_player, slot) is FairyInABottle)
                fairyCount++;
        }

        LizardTail? lizardTail = combat.RelicsOf(_player)
            .OfType<LizardTail>()
            .FirstOrDefault(relic => !LizardTailMirrors.WasUsed(relic, simulator));
        return new ProjectedDeathPrevention(
            fairyCount,
            (int)FairyInABottleMirrors.HealAmount(playerMaxHp),
            lizardTail != null,
            lizardTail == null ? 0 : (int)LizardTailMirrors.HealAmount(lizardTail, playerMaxHp));
    }

    private struct ProjectedDeathPrevention(
        int fairyCount,
        int fairyHeal,
        bool lizardTailAvailable,
        int lizardTailHeal)
    {
        public void TryRevive(ref int hp)
        {
            if (hp > 0)
                return;
            if (fairyCount > 0)
            {
                fairyCount--;
                hp = fairyHeal;
                return;
            }
            if (!lizardTailAvailable)
                return;
            lizardTailAvailable = false;
            hp = lizardTailHeal;
        }
    }

    private StateFingerprint BuildStateKey(
        int turn,
        SimCreatureState player,
        SimPlayerCombatState playerState,
        SimulatedCombatState simulatedCombat,
        CombatPredictionSimulator simulator,
        int shufflesCrossed,
        IReadOnlySet<uint> processedEnemyDeaths)
    {
        StateFingerprintBuilder key = new();
        key.Add(turn);
        key.Add(player.CurrentHp);
        key.Add(player.MaxHp);
        key.Add(player.Block);
        key.Add(playerState.Energy);
        key.Add(playerState.Stars);
        key.Add(shufflesCrossed);
        Player owner = _player;
        if (simulatedCombat.GetOsty(owner) is { } osty)
        {
            key.Add(simulator.State.GetCreature(osty).CurrentHp);
            key.Add(simulatedCombat.GetOstyMaxHp(simulator, owner));
            key.Add(simulatedCombat.IsOstyHittable(simulator, owner));
        }
        else
        {
            key.Add(0);
            key.Add(0);
            key.Add(false);
        }
        foreach (Creature enemy in simulatedCombat.KnownEnemies)
        {
            SimCreatureState enemyState = simulator.State.GetCreature(enemy);
            key.Add(enemy.Monster?.Id.Entry);
            key.Add(enemy.CombatId ?? uint.MaxValue);
            key.Add(simulatedCombat.ContainsCreature(enemy));
            key.Add(enemyState.CurrentHp);
            key.Add(enemyState.MaxHp);
            key.Add(enemyState.Block);
        }
        SearchMeasurement pileFingerprintMeasurement = _run.Performance.Begin();
        AppendPile(ref key, playerState.Hand, 'H');
        AppendPile(ref key, playerState.DrawPile, 'D');
        AppendPile(ref key, playerState.DiscardPile, 'C');
        AppendPile(ref key, playerState.ExhaustPile, 'X');
        AppendOrbs(ref key, simulator, playerState.OrbQueue);
        _run.Performance.End(SearchMetricPhase.PileFingerprint, pileFingerprintMeasurement);
        AppendRngState(ref key, simulator.Rng.Shuffle);
        AppendRngState(ref key, simulator.Rng.CombatCardGeneration);
        AppendRngState(ref key, simulator.Rng.CombatPotionGeneration);
        AppendRngState(ref key, simulator.Rng.CombatCardSelection);
        AppendRngState(ref key, simulator.Rng.CombatEnergyCosts);
        AppendRngState(ref key, simulator.Rng.CombatTargets);
        AppendRngState(ref key, simulator.Rng.CombatOrbGeneration);
        AppendRngState(ref key, simulator.Rng.MonsterAi);
        AppendRngState(ref key, simulator.Rng.Niche);
        ulong deathsFirst = 0;
        ulong deathsSecond = 0;
        foreach (uint combatId in processedEnemyDeaths)
        {
            deathsFirst += StateFingerprintBuilder.MixFirst(combatId);
            deathsSecond += StateFingerprintBuilder.MixSecond(combatId);
        }
        key.Add(processedEnemyDeaths.Count);
        key.Add(deathsFirst);
        key.Add(deathsSecond);
        SearchMeasurement combatFingerprintMeasurement = _run.Performance.Begin();
        simulatedCombat.AppendFingerprint(ref key, simulator);
        _run.Performance.End(SearchMetricPhase.CombatFingerprint, combatFingerprintMeasurement);
        return key.Finish();
    }

    private static void AppendRngState(ref StateFingerprintBuilder key, Rng rng)
    {
        PredictionRngState state = rng.CaptureState();
        key.Add(state.Counter);
        key.Add(state.State0);
        key.Add(state.State1);
        key.Add(state.State2);
        key.Add(state.State3);
    }

    internal static StateFingerprint CaptureRngStateFingerprintForTesting(Rng rng)
    {
        StateFingerprintBuilder key = new();
        AppendRngState(ref key, rng);
        return key.Finish();
    }

    private static void AppendOrbs(
        ref StateFingerprintBuilder key,
        CombatPredictionSimulator simulator,
        SimOrbQueue queue)
    {
        key.Add('O');
        key.Add(queue.Capacity);
        key.Add(queue.Orbs.Count);
        foreach (OrbModel orb in queue.Orbs)
        {
            key.Add(orb.Id.Entry);
            key.Add(Engine.InCombat.Mirrors.Orbs.OrbMirrors.GetPassiveValue(simulator, orb));
            key.Add(Engine.InCombat.Mirrors.Orbs.OrbMirrors.GetEvokeValue(simulator, orb));
        }
    }

    private StateFingerprint BuildPlayableCardKey(PredictedCard card)
        => BuildCardStateFingerprint(card);

    private StateFingerprint BuildCardStateFingerprint(PredictedCard card)
    {
        if (card.TryGetCachedFingerprint(out ulong cachedFirst, out ulong cachedSecond))
            return new StateFingerprint(cachedFirst, cachedSecond);
        SearchMeasurement measurement = _run.Performance.Begin();
        StateFingerprint fingerprint = CaptureCardStateFingerprintForTesting(card);
        card.SetCachedFingerprint(fingerprint.First, fingerprint.Second);
        _run.Performance.End(SearchMetricPhase.CardFingerprintMiss, measurement);
        return fingerprint;
    }

    internal static StateFingerprint CaptureCardStateFingerprintForTesting(PredictedCard card)
    {
        CardModel preview = card.Preview;
        StateFingerprintBuilder key = new();
        key.Add(preview.Id.Entry);
        key.Add(preview.CurrentUpgradeLevel);
        key.Add(preview.EnergyCost.CostsX);
        key.Add(preview.EnergyCost.GetWithModifiers(CostModifiers.Local));
        key.Add(preview.HasStarCostX);
        key.Add(preview.CurrentStarCost);
        key.Add(preview.BaseReplayCount);
        key.Add(preview.ExhaustOnNextPlay);
        key.Add(preview.IsSlyThisTurn);
        key.Add(preview.ShouldRetainThisTurn);
        key.Add(preview.DeckVersion != null);
        key.Add(preview.HasBeenRemovedFromState);
        EnchantmentStateSupport.Append(ref key, preview.Enchantment);
        key.Add(preview.Affliction?.Id.Entry);
        key.Add(preview.Affliction?.Amount ?? 0);
        AppendDynamicVars(ref key, preview, preview.DynamicVars);
        switch (preview)
        {
            case Claw claw:
                key.Add(claw.ExtraDamageFromClawPlays);
                break;
            case GeneticAlgorithm geneticAlgorithm:
                key.Add(geneticAlgorithm.IncreasedBlock);
                break;
            case Maul maul:
                key.Add(maul._extraDamageFromMaulPlays);
                break;
            case MadScience madScience:
                key.Add((int)madScience.TinkerTimeType);
                key.Add((int)madScience.TinkerTimeRider);
                break;
            case Rampage rampage:
                key.Add(rampage.ExtraDamageFromPlays);
                break;
            case TheScythe scythe:
                key.Add(scythe.IncreasedDamage);
                break;
        }
        if (card.HasExternallyMutableAttachedModels)
            AppendBaseLibCardModifiers(ref key, preview);
        return key.Finish();
    }

    private static void AppendBaseLibCardModifiers(
        ref StateFingerprintBuilder key,
        CardModel card)
    {
        CardAttachedModelCollection modifiers =
            PredictionModModelSupport.GetCardAttachedListeners(card);
        key.Add('M');
        key.Add(modifiers.Count);
        for (int index = 0; index < modifiers.Count; index++)
        {
            AbstractModel modifier = modifiers[index];
            Type type = modifier.GetType();
            key.Add(type.Assembly.GetName().Name);
            key.Add(type.FullName);
            BaseLibCardModifierFingerprintState state =
                PredictionModModelSupport.CaptureBaseLibCardModifierFingerprintState(modifier);
            key.Add(state.Amount);
            key.Add(state.Priority);
            key.Add(state.IntProperties.Length);
            foreach ((string name, int value) in state.IntProperties)
            {
                key.Add(name);
                key.Add(value);
            }
            key.Add(state.AdditionalProperties.Length);
            foreach ((string name, string value) in state.AdditionalProperties)
            {
                key.Add(name);
                key.Add(value);
            }
        }
    }

    private void AppendPile(ref StateFingerprintBuilder key, SimCardPile pile, char marker)
    {
        key.Add(marker);
        key.Add(pile.Cards.Count);
        if (pile.TryGetCachedFingerprint(out ulong cachedFirst, out ulong cachedSecond))
        {
            key.Add(cachedFirst);
            key.Add(cachedSecond);
            return;
        }
        SearchMeasurement measurement = _run.Performance.Begin();
        StateFingerprintBuilder pileKey = new();
        pileKey.Add(pile.Cards.Count);
        foreach (PredictedCard card in pile)
        {
            StateFingerprint cardFingerprint = BuildCardStateFingerprint(card);
            pileKey.Add(cardFingerprint.First);
            pileKey.Add(cardFingerprint.Second);
        }
        StateFingerprint fingerprint = pileKey.Finish();
        pile.SetCachedFingerprint(fingerprint.First, fingerprint.Second);
        _run.Performance.End(SearchMetricPhase.PileFingerprintMiss, measurement);
        key.Add(fingerprint.First);
        key.Add(fingerprint.Second);
    }

    private static void AppendDynamicVars(
        ref StateFingerprintBuilder key,
        AbstractModel model,
        IReadOnlyDictionary<string, MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar> dynamicVars)
    {
        ulong first = 0;
        ulong second = 0;
        int count = 0;
        foreach ((string name, MegaCrit.Sts2.Core.Localization.DynamicVars.DynamicVar value) in dynamicVars)
        {
            if (!SemanticStateFieldPolicy.IsSemantic(model, name, value))
                continue;
            StateFingerprintBuilder item = new();
            item.Add(name);
            item.Add(value.BaseValue);
            if (value is MegaCrit.Sts2.Core.Localization.DynamicVars.StringVar stringVar)
                item.Add(stringVar.StringValue);
            StateFingerprint fingerprint = item.Finish();
            first += StateFingerprintBuilder.MixFirst(fingerprint.First);
            second += StateFingerprintBuilder.MixSecond(fingerprint.Second);
            count++;
        }
        key.Add(count);
        key.Add(first);
        key.Add(second);
    }

}
