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
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors;
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
        int rawEnemyHp = 0;
        int maxCurrentEnemyHp = 0;
        int revivingEnemyCount = 0;
        int aliveEnemyCount = 0;
        ulong aliveEnemyMask = 0;
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
            enemyHp += effectiveHp;
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
        int longTermResourceValue = combat.LongTermResourceValue;
        LongTermGoals longTermGoals = combat.LongTermGoals;
        LongTermGoals longTermGoalCardsPlayed = combat.LongTermGoalCardsPlayed;
        score += longTermResourceValue * SolverWeights.LongTermResourceBeamValue;
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
        int persistentBuffValue = 0;
        int offensivePersistentBuffValue = 0;
        PersistentSetupTraits persistentSetupTraits = PersistentSetupTraits.None;
        foreach (PowerModel power in combat.EffectivePowers())
        {
            if (!ReferenceEquals(power.Owner, _player.Creature)
                || power.Amount <= 0
                || power.TypeForCurrentAmount != PowerType.Buff
                || power is ITemporaryPower)
            {
                continue;
            }
            int setupValue = PersistentPowerSetupValue(power);
            persistentBuffValue += setupValue;
            if (PersistentPowerAdvancesCombat(power, retainedAttackValue > 0))
                offensivePersistentBuffValue += setupValue;
            persistentSetupTraits |= PersistentPowerSetupTrait(power);
        }
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
                latentSetupValue += LatentCardSetupValue(preview);
            }
        }
        int replayPotentialValue = ReplayPotentialValue(liveCards);
        ThreatFocus focus = BuildThreatFocus(simulator, combat);
        int futureResourceValue = combat.GetAmount<EnergyNextTurnPower>(_player.Creature) * 16
            + combat.GetAmount<DrawCardsNextTurnPower>(_player.Creature) * 8
            + combat.GetAmount<StarNextTurnPower>(_player.Creature) * 8
            + combat.GetAmount<RetainHandPower>(_player.Creature) * 4;
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

        return new SimulationSnapshot(
            score,
            key,
            unorderedPileKey,
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
            aliveEnemyCount,
            aliveEnemyMask,
            rawEnemyHp,
            maxCurrentEnemyHp,
            enemyCombatDistributionKey,
            revivingEnemyCount,
            persistentBuffValue,
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
        foreach (PredictedCard card in pile.Cards)
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

    private static int PersistentPowerSetupValue(PowerModel power)
    {
        int amount = Math.Max(1, power.Amount);
        return power switch
        {
            EchoFormPower => amount * 12,
            BufferPower => amount * 8,
            CuriousPower => amount * 10,
            FocusPower => amount * 6,
            DemonFormPower or CreativeAiPower => amount * 5,
            ThunderPower => amount * 3,
            LightningRodPower => amount * 4,
            MasterPlannerPower => amount * 4,
            StrengthPower or DexterityPower => amount * 2,
            _ => amount,
        };
    }

    private static bool PersistentPowerAdvancesCombat(PowerModel power, bool hasAttackPayoff)
        => power switch
        {
            StrengthPower or DemonFormPower or EchoFormPower => hasAttackPayoff,
            CuriousPower or FocusPower or CreativeAiPower or ThunderPower or LightningRodPower => true,
            _ => false,
        };

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
            _ => PersistentSetupTraits.None,
        };

    private static int LatentCardSetupValue(CardModel card)
        => card switch
        {
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
            int progress = Math.Max(0, maxHp - remainingHp);
            if (progress == 0)
                continue;

            int currentThreat = 0;
            if (!combat.WillSkipNextMove(enemy))
            {
                foreach (ForecastMove move in moves)
                {
                    if (!ReferenceEquals(move.Owner, enemy))
                        continue;
                    foreach (ForecastAttackHit hit in move.AttackHits)
                    {
                        currentThreat += Math.Max(
                            0,
                            combat.AdjustMonsterMoveDamage(enemy, move.Move.Id, hit.BaseDamage));
                    }
                }
            }

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
        return new ThreatFocus(bestCombatId, bestPressure, bestRemainingHp, bestThreat);
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
        StateFingerprint fingerprint = key.Finish();
        card.SetCachedFingerprint(fingerprint.First, fingerprint.Second);
        _run.Performance.End(SearchMetricPhase.CardFingerprintMiss, measurement);
        return fingerprint;
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
        foreach (PredictedCard card in pile.Cards)
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
