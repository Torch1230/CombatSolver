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
    private static bool HasUncompensatedDeathGap(IReadOnlyList<PredictionGap> gaps)
    {
        for (int index = 0; index < gaps.Count; index++)
        {
            if (!gaps[index].Compensated
                && gaps[index].Method.Contains("Death", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private SimulationSnapshot Snapshot(
        CombatPredictionSimulator simulator,
        int turn,
        int actionCount,
        int shufflesCrossed,
        SearchBoundaryReason boundary,
        IReadOnlySet<uint> processedEnemyDeaths)
        => SnapshotCore(simulator, turn, actionCount, shufflesCrossed, boundary, processedEnemyDeaths, null);

    // The reader must prove that every omitted root field is invariant for its admitted program.
    // It supplies completed values only; SnapshotCore owns all scoring and ordering formulas.
    internal SimulationSnapshot SnapshotFromReadView(
        CompletedStateReadView view, int turn, int actionCount, int shufflesCrossed,
        SearchBoundaryReason boundary, IReadOnlySet<uint> processedEnemyDeaths)
    {
        SimulationSnapshot result = SnapshotCore(view.Root, turn, actionCount, shufflesCrossed, boundary, processedEnemyDeaths, view);
        result.ReleaseSimulator();
        return result;
    }

    private SimulationSnapshot SnapshotCore(
        CombatPredictionSimulator simulator, int turn, int actionCount, int shufflesCrossed,
        SearchBoundaryReason boundary, IReadOnlySet<uint> processedEnemyDeaths, CompletedStateReadView? view)
    {
        CreatureReadValues player = ReadCreatureValues(simulator, _player.Creature, view);
        int playerBlock = view?.Block ?? player.Block;
        SimulatedCombatState combat = (SimulatedCombatState)simulator.State.CombatState;
        EnemyEvaluationValues enemyValues = GetEnemyEvaluation(simulator, combat, view);
        int enemyHp = enemyValues.Hp, enemyBlock = enemyValues.Block, rawEnemyHp = enemyValues.RawHp;
        int maxCurrentEnemyHp = enemyValues.MaxHp, revivingEnemyCount = enemyValues.Reviving;
        int aliveEnemyCount = enemyValues.Alive;
        ulong aliveEnemyMask = enemyValues.AliveMask;
        StateFingerprint enemyCombatDistributionKey = enemyValues.Distribution;
        if (boundary == SearchBoundaryReason.None && combat.HasPendingChoice)
            boundary = SearchBoundaryReason.PendingChoice;
        // Later effects may restore HP after native pending loss has already been committed.
        CombatTerminalStamp? terminal = view is null ? simulator.TerminalStamp : view.TerminalStamp;
        bool dead = player.IsDead || terminal is { Outcome: CombatTerminalOutcome.Defeat };
        bool won = boundary != SearchBoundaryReason.EventDefeat
            && !dead
            && !combat.HasPendingChoice
            && terminal is { Outcome: CombatTerminalOutcome.Victory };
        CoverageSummary coverage = view is null ? GetCoverageSummary(simulator)
            : new(view.PredictionGaps, view.PredictionGaps.Any(gap => !gap.Compensated));
        IReadOnlyList<PredictionGap> predictionGaps = coverage.Gaps;
        bool risk = coverage.HasUncompensatedRisk;
        bool uncertainVictory = won && HasUncompensatedDeathGap(predictionGaps);
        if (uncertainVictory)
            boundary = SearchBoundaryReason.UnsupportedEffect;

        SimPlayerCombatState playerState = simulator.State.GetPlayerCombatState(_player);
        IReadOnlyList<PredictedCard> hand = view?.Hand ?? playerState.Hand.Cards;
        IReadOnlyList<PredictedCard> draw = view?.Draw ?? playerState.DrawPile.Cards;
        IReadOnlyList<PredictedCard> discard = view?.Discard ?? playerState.DiscardPile.Cards;
        IReadOnlyList<PredictedCard> exhaust = view?.Exhaust ?? playerState.ExhaustPile.Cards;
        int energy = view?.Energy ?? playerState.Energy;
        SearchMeasurement fingerprintMeasurement = _run.Performance.Begin();
        StateFingerprint key = BuildStateKey(
            turn,
            player,
            playerState,
            combat,
            simulator,
            shufflesCrossed,
            processedEnemyDeaths, view);
        StateFingerprint unorderedPileKey = BuildUnorderedPileKey(hand, draw, discard, exhaust);
        StateFingerprint cyclePileShapeKey = view is null ? BuildCyclePileShapeKey(playerState)
            : BuildCyclePileShapeKey(hand, draw, discard, exhaust);
        SearchMeasurement projectedShuffleMeasurement = _run.Performance.Begin();
        // Projected shuffle needs these piles in this exact pre-sort order. The remaining
        // snapshot metrics are order-independent, so they can reuse the shuffled list instead
        // of materializing a second deck-sized backing array.
        using SnapshotListBuffer<PredictedCard>.Lease liveCardsLease =
            _run.SnapshotLiveCards.Rent();
        List<PredictedCard> liveCards = liveCardsLease.Items;
        liveCards.EnsureCapacity(discard.Count + draw.Count + hand.Count);
        liveCards.AddRange(discard);
        liveCards.AddRange(draw);
        liveCards.AddRange(hand);
        (StateFingerprint projectedShuffleOrderKey, int projectedShuffleOrderValue) =
            BuildProjectedShuffleOrder(simulator, liveCards, view);
        int liveCardCount = liveCards.Count;
        _run.Performance.End(
            SearchMetricPhase.ProjectedShuffle,
            projectedShuffleMeasurement);
        _run.Performance.End(SearchMetricPhase.Fingerprint, fingerprintMeasurement);

        int roundIndex = turn - _startTurnNumber;
        SearchMeasurement threatMeasurement = _run.Performance.Begin();
        ThreatProjection threat;
        if (won)
        {
            // A lethal player action ends combat immediately. Enemy intent from that round must
            // never lower the route's projected HP or leak into battle-loss reporting.
            threat = new ThreatProjection(player.CurrentHp, 0);
        }
        else if (boundary != SearchBoundaryReason.None)
        {
            threat = new ThreatProjection(player.CurrentHp, 0);
        }
        else if (!_run.ThreatProjectionCache.TryGetValue((key, roundIndex), out threat))
        {
            threat = ProjectHpAfterThreat(simulator, player, roundIndex, playerBlock, view);
            _run.ThreatProjectionCache.Add((key, roundIndex), threat);
        }
        int projectedHp = threat.Hp;
        _run.Performance.End(SearchMetricPhase.ThreatProjection, threatMeasurement);
        int cumulativePlayerHpLost = combat.GetCumulativeHpLost(_player.Creature);
        int recoveredPlayerHp = combat.GetRecoveredHp(_player.Creature);
        int deathSaveRelicHpRestored = combat.DeathSaveRelicHpRestored;
        double hpWeight = SolverWeights.Hp;
        double score = dead || projectedHp <= 0
            ? SolverWeights.DeathPenalty
            : projectedHp * hpWeight;
        score += (player.MaxHp - root.InitialPlayerMaxHp) * hpWeight;
        score -= cumulativePlayerHpLost * hpWeight;
        // A one-shot death save is a cross-combat resource. The HP it puts back is still sitting in
        // projectedHp, so it is taken out again and charged a second time as the price of spending it. The
        // projected part is included because walking into a lethal intent has to look as expensive as
        // actually taking it, or retention keeps the route that plans to die and drops the one that does not.
        score -= ActEndingBossPolicy.DeathSaveRelicBeamCost(
            deathSaveRelicHpRestored + threat.DeathSaveRelicHpRestored,
            _strategicBossHpRelief) * hpWeight;
        int exhaustedTheHunts = exhaust.Count(card => card.Preview is TheHunt);
        int rewardedTheHunts = Math.Max(0, combat.GetAmount<TheHuntPower>(_player.Creature));
        int missedTheHuntRewards = Math.Max(0, exhaustedTheHunts - rewardedTheHunts);
        // 「不考虑局外收益」在快照源头把这两个量清零，下游十几处读到的是同一个 0。
        //
        // 不逐处判断是有理由的：局外收益既是分数项，也是终局排序键、Beam 保路泳道、必留泳道、
        // Pareto 维度、循环进展信号和 BeamRetentionPolicy.CompareFinalCandidates 的比较键。
        // 逐处列举漏过两次（先漏了终局排序里排在结束回合之前的 GrowthRewards.Total，后漏了
        // CompareFinalCandidates），每次的表现都是「开了开关还是拿钱」。源头清零按构造不会漏。
        //
        // 早先这么做过一次，搜索会卡死：分道结构一并塌掉之后，Beam 名额被同一类候选占满，预算
        // 全烧在同一个回合层里出牌、推不到下一回合。那条现在由「节点预算按回合层分配」兜住——
        // 单层再也吃不掉整份节点预算，塌掉分道只会让某一层内部少一点多样性，不会再拖住整场搜索。
        int realizedLongTermResourceValue = _ignoreLongTermRewards ? 0 : combat.LongTermResourceValue;
        int longTermResourceValue = _ignoreLongTermRewards
            ? 0
            : realizedLongTermResourceValue
                - missedTheHuntRewards * CorePowerSupport.TheHuntLongTermResourceValue;
        GrowthValues growthRewards = _ignoreLongTermRewards ? default : combat.GrowthRewards;
        score += Math.Min(
            realizedLongTermResourceValue * SolverWeights.LongTermResourceBeamValue,
            SolverWeights.LongTermResourceBeamCap);
        int growthHpCredit = _growthBudgets.Credit(growthRewards);
        score += (double)growthHpCredit * hpWeight;
        int angerCopiesGenerated = combat.AngerCopiesGenerated;
        score += angerCopiesGenerated * SolverWeights.AngerCopyBeamPenalty;
        if (won && !uncertainVictory)
            score += SolverWeights.VictoryBonus;
        score += enemyHp * SolverWeights.EnemyHp;
        // 负偏置登记过的牌和状态牌、诅咒同样按牌库杂质计。
        //
        // 少了这一条，消耗一张非状态非诅咒的牌在打分里的收益**正好是零**：牌库里少一张不进任何
        // 项（retainedAttackValue 有上限，攻击牌多的时候早就顶满，少一张也不掉），于是「打出净化
        // 消耗两张废牌」严格劣于「不打净化」——省下那点能量总是更划算。移除估值的偏置只排选择
        // 分支的先后，排不出一个本来就没有的收益。这就是玩家实测里净化根本不被打出的原因。
        int liveDeckClutter = liveCards.Count(card =>
            (card.Preview.Type is CardType.Status or CardType.Curse
                || CardRemovalValueMirrors.Offset(card.Preview) < 0d)
            && !LeavesHandAtTurnEnd(simulator, playerState, card));
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
                (int)Math.Round(ReadCardValue(liveCard, view?.Invariants)));
        }
        ThreatFocus focus = GetThreatFocus(simulator, combat, view);
        IReadOnlyList<PowerModel> effectivePowers = combat.EffectivePowers();
        // Requirements and evaluation inspect the same immutable snapshot. Native
        // GetTypeForAmount boxes its enum comparisons, so keep this one-pass decision
        // instead of asking Contributes again for every power during evaluation.
        Span<bool> contributes = effectivePowers.Count <= 64
            ? stackalloc bool[effectivePowers.Count]
            : new bool[effectivePowers.Count];
        StrategicEffectRequirements strategicRequirements = StrategicEffectRequirements.None;
        for (int powerIndex = 0; powerIndex < effectivePowers.Count; powerIndex++)
        {
            PowerModel power = effectivePowers[powerIndex];
            contributes[powerIndex] = StrategicEffectMirrors.Contributes(power, _player.Creature);
            if (!contributes[powerIndex])
                continue;
            strategicRequirements |= StrategicEffectModel.Requirements(power);
        }
        StrategicEffectContext? strategicContext = null;
        StrategicEffectVector strategicEffects = StrategicEffectVector.Zero;
        int offensivePersistentBuffValue = 0;
        PersistentSetupTraits persistentSetupTraits = PersistentSetupTraits.None;
        for (int powerIndex = 0; powerIndex < effectivePowers.Count; powerIndex++)
        {
            PowerModel power = effectivePowers[powerIndex];
            if (!contributes[powerIndex])
                continue;
            strategicContext ??= GetStrategicContext(
                liveCards,
                enemyHp,
                focus.TotalThreat,
                focus.IncomingHitCount,
                strategicRequirements, view?.Invariants);
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
        int retainedHandValue = hand
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
            : ReadCreatureValues(simulator, currentOsty, view).CurrentHp;
        int ostyMaxHp = combat.GetOstyMaxHp(simulator, _player);
        persistentBuffValue += liveCards.Count(card => card.Preview is Soul);
        // Where + Sum 两个闭包加一个装箱的 ForkableList 枚举器，换成按下标累加：
        // 筛选条件、累加顺序与每项的整数运算完全不变。
        IReadOnlyList<Creature> knownEnemies = combat.KnownEnemies;
        int delayedDamageValue = 0;
        for (int enemyIndex = 0; enemyIndex < knownEnemies.Count; enemyIndex++)
        {
            Creature enemy = knownEnemies[enemyIndex];
            CreatureReadValues enemyState = ReadCreatureValues(simulator, enemy, view);
            if (!enemyState.Present || !enemyState.IsAlive)
                continue;
            int poison = Math.Max(0, combat.GetAmount<PoisonPower>(enemy));
            int demise = Math.Max(0, combat.GetAmount<DemisePower>(enemy));
            int doom = Math.Max(0, combat.GetAmount<DoomPower>(enemy));
            int currentHp = Math.Max(0, enemyState.CurrentHp);
            int cappedDoom = Math.Min(currentHp, doom);
            int doomProgress = currentHp <= 0
                ? 0
                : (int)Math.Min(currentHp, (long)cappedDoom * cappedDoom / currentHp);
            // Enumerable.Sum 对 int 是 checked 累加，这里照搬同样的溢出语义。
            delayedDamageValue = checked(delayedDamageValue + poison + demise + doomProgress);
        }
        int reactiveDamageValue = Math.Max(
            0,
            combat.GetAmount<SleightOfFleshPower>(_player.Creature));
        bool hasBlockDamagePayoff = liveCards.Any(card => card.Preview is BodySlam);
        int offensiveProgressValue = offensivePersistentBuffValue
            + delayedDamageValue
            + reactiveDamageValue
            + (hasBlockDamagePayoff ? Math.Max(0, playerBlock) : 0);
        int enemyStrengthSuppression = 0;
        int enemyWeakTurns = 0;
        int vulnerable = 0;
        int focusTargetVulnerableTurns = 0;
        uint? mostVulnerableTargetCombatId = null;
        int mostVulnerableTurns = 0;
        StateFingerprintBuilder enemyControlDistribution = new();
        for (int enemyIndex = 0; enemyIndex < knownEnemies.Count; enemyIndex++)
        {
            Creature enemy = knownEnemies[enemyIndex];
            CreatureReadValues enemyState = ReadCreatureValues(simulator, enemy, view);
            if (!enemyState.Present || !enemyState.IsAlive)
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
        int sandpitRemaining = ReadSandpitRemaining(simulator, effectivePowers, _player.Creature, view);
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
            CalculateReachableHandPotential(simulator, combat, hand, energy, playerState.Stars);
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
            recoveredPlayerHp,
            deathSaveRelicHpRestored,
            longTermResourceValue,
            angerCopiesGenerated,
            projectedHp,
            playerBlock,
            enemyHp,
            enemyBlock,
            aliveEnemyCount,
            aliveEnemyMask,
            rawEnemyHp,
            maxCurrentEnemyHp,
            enemyCombatDistributionKey,
            enemyValues.Durability,
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
            liveCardCount,
            outstandingStolenResource,
            offensiveProgressValue,
            energy,
            playerState.Stars,
            view?.HistoryEntries ?? simulator.History.Entries.Count,
            hand.Count,
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
            simulator,
            terminal)
        {
            GrowthHpCredit = growthHpCredit,
            GrowthRewards = growthRewards,
        };
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
            (first, second) = CyclePileValues(pile.Cards);
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
        IReadOnlyList<PredictedCard> hand, int energy, int stars)
    {
        int handCount = hand.Count;
        Span<(int Energy, int Stars, int Value)> playable = handCount <= 64
            ? stackalloc (int, int, int)[handCount]
            : new (int, int, int)[handCount];
        int playableCount = 0;
        int zeroCostPlayableCount = 0;
        for (int index = 0; index < hand.Count; index++)
        {
            PredictedCard card = hand[index];
            if (!combat.CanPlayCardAtResources(simulator, card, energy, stars, out int energyCost, out int starCost))
                continue;
            energyCost = Math.Max(0, energyCost);
            starCost = Math.Max(0, starCost);
            int value = Math.Max(1, (int)Math.Ceiling(CardChoiceSupport.CardValue(card.Preview)));
            playable[playableCount++] = (energyCost, starCost, value);
            if (energyCost == 0
                && starCost == 0
                && !card.Preview.EnergyCost.CostsX
                && !card.Preview.HasStarCostX)
            {
                zeroCostPlayableCount++;
            }
        }

        return (ReachableHandValue.Calculate(playable[..playableCount], energy, stars),
            zeroCostPlayableCount);
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

    private StateFingerprint BuildUnorderedPileKey(IReadOnlyList<PredictedCard> hand,
        IReadOnlyList<PredictedCard> draw, IReadOnlyList<PredictedCard> discard, IReadOnlyList<PredictedCard> exhaust)
    {
        StateFingerprintBuilder unordered = new();
        AppendUnorderedPileKey(ref unordered, hand, 'H');
        AppendUnorderedPileKey(ref unordered, draw, 'D');
        AppendUnorderedPileKey(ref unordered, discard, 'C');
        AppendUnorderedPileKey(ref unordered, exhaust, 'X');
        return unordered.Finish();
    }

    private void AppendUnorderedPileKey(
        ref StateFingerprintBuilder unordered,
        IReadOnlyList<PredictedCard> pile,
        char marker)
    {
        ulong first = 0;
        ulong second = 0;
        for (int index = 0; index < pile.Count; index++)
        {
            PredictedCard card = pile[index];
            StateFingerprint cardKey = BuildCardStateFingerprint(card);
            first += StateFingerprintBuilder.MixFirst(cardKey.First);
            second += StateFingerprintBuilder.MixSecond(cardKey.Second);
        }
        // Keep the unordered key's values and append order exactly unchanged.
        unordered.Add(marker);
        unordered.Add(pile.Count);
        unordered.Add(first);
        unordered.Add(second);
    }

    private (StateFingerprint Key, int Value) BuildProjectedShuffleOrder(
        CombatPredictionSimulator simulator,
        List<PredictedCard> cards, CompletedStateReadView? view = null)
    {
        // StableShuffle sorts a second List copy before shuffling. This list is already private
        // to the snapshot, so performing the same sort and shuffle in place avoids
        // another deck-sized backing array without changing RNG consumption or ordering.
        var shuffleRng = simulator.Rng.Shuffle.Clone();
        PredictionRngState? shuffleState = view?.ShuffleRng;
        if (shuffleState is { } captured)
        {
            shuffleRng._counter = captured.Counter;
            shuffleRng._random._s0 = captured.State0;
            shuffleRng._random._s1 = captured.State1;
            shuffleRng._random._s2 = captured.State2;
            shuffleRng._random._s3 = captured.State3;
        }
        StableShuffleProjection(cards, shuffleRng);

        StateFingerprintBuilder key = new();
        key.Add(shuffleState?.Counter ?? simulator.Rng.Shuffle.Counter());
        key.Add(cards.Count);
        int value = 0;
        for (int index = 0; index < cards.Count; index++)
        {
            StateFingerprint cardKey = BuildCardStateFingerprint(cards[index]);
            key.Add(cardKey.First);
            key.Add(cardKey.Second);
            value += (int)Math.Round(
                ReadCardValue(cards[index], view?.Invariants) * (cards.Count - index));
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

    /// <summary>手里这张牌会不会在本回合结束时自己消耗掉。</summary>
    /// <remarks>
    /// 虚无牌回合结束时若仍在手牌就自己消耗，判据与
    /// <c>CombatPredictionSimulator.EndTurn</c> 的虚无分支一致。
    ///
    /// 这类牌不该计入牌库杂物：无论路线是否提前把它移除，到本回合边界它都已经不在了，
    /// 同一笔杂物减少两边都会拿到。把它算进来等于给"提前移除"发一笔本来就会到账的钱，
    /// 于是一次有限的消耗看上去比移除真正长期占位的牌更划算。
    ///
    /// 只排除手牌。抽牌堆和弃牌堆里的同一张牌本回合不会自己走，那时它确实是杂物。
    /// </remarks>
    private static bool LeavesHandAtTurnEnd(
        CombatPredictionSimulator simulator,
        SimPlayerCombatState playerState,
        PredictedCard card)
    {
        if (card.GetPile(playerState)?.Type is not PileType.Hand)
            return false;
        if (card.Preview.HasTurnEndInHandEffect)
            return false;
        return card.HasKeyword(simulator.State, CardKeyword.Ethereal)
            && Hook.ShouldEtherealTrigger(simulator.State.CombatState, card.Preview);
    }

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
        SimulatedCombatState combat, CompletedStateReadView? view)
    {
        IReadOnlyList<ForecastMove> moves = combat.CurrentMonsterMoves(view?.EnemyRoster ?? combat.Enemies);
        uint? bestCombatId = null;
        int bestPressure = 0;
        int bestRemainingHp = int.MaxValue;
        int bestThreat = 0;
        int totalThreat = 0;
        int incomingHitCount = 0;
        IReadOnlyList<Creature> knownEnemies = combat.KnownEnemies;
        for (int enemyIndex = 0; enemyIndex < knownEnemies.Count; enemyIndex++)
        {
            Creature enemy = knownEnemies[enemyIndex];
            CreatureReadValues enemyState = ReadCreatureValues(simulator, enemy, view);
            if (!enemyState.Present
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
                for (int moveIndex = 0; moveIndex < moves.Count; moveIndex++)
                {
                    ForecastMove move = moves[moveIndex];
                    if (!ReferenceEquals(move.Owner, enemy))
                        continue;
                    IReadOnlyList<ForecastAttackHit> attackHits = move.AttackHits;
                    for (int hitIndex = 0; hitIndex < attackHits.Count; hitIndex++)
                    {
                        incomingHitCount++;
                        currentThreat += Math.Max(
                            0,
                            combat.AdjustMonsterMoveDamage(
                                enemy,
                                move.Move.Id,
                                attackHits[hitIndex].BaseDamage));
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

    /// <summary>
    /// What the incoming enemy intent leaves the player at, and how much of that HP only exists because a
    /// one-shot death-save relic would have to be spent to get there.
    /// </summary>
    private readonly record struct ThreatProjection(int Hp, int DeathSaveRelicHpRestored);

    private ThreatProjection ProjectHpAfterThreat(
        CombatPredictionSimulator simulator,
        CreatureReadValues player,
        int roundIndex, int? initialBlock = null, CompletedStateReadView? view = null)
    {
        int hp = player.CurrentHp;
        int block = initialBlock ?? player.Block;
        SimulatedCombatState simulatedCombat = (SimulatedCombatState)simulator.State.CombatState;
        Creature? osty = simulatedCombat.GetOsty(_player);
        int ostyHp = osty == null ? 0 : ReadCreatureValues(simulator, osty, view).CurrentHp;
        ProjectedDeathPrevention deathPrevention = BuildProjectedDeathPrevention(
            simulator,
            simulatedCombat,
            player.MaxHp);
        IReadOnlyList<ForecastMove> moves = simulatedCombat.CurrentMonsterMoves(view?.EnemyRoster ?? simulatedCombat.Enemies);
        for (int moveIndex = 0; moveIndex < moves.Count; moveIndex++)
        {
            ForecastMove move = moves[moveIndex];
            if (!ReadCreatureValues(simulator, move.Owner, view).IsAlive)
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
            IReadOnlyList<ForecastAttackHit> attackHits = move.AttackHits;
            for (int hitIndex = 0; hitIndex < attackHits.Count; hitIndex++)
            {
                int baseDamage = simulatedCombat.AdjustMonsterMoveDamage(
                    move.Owner,
                    move.Move.Id,
                    attackHits[hitIndex].BaseDamage);
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
        return new ThreatProjection(hp, deathPrevention.RelicHpRestored);
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
        /// <summary>HP a projected Lizard Tail revive would restore, priced the same way a real one is.</summary>
        public int RelicHpRestored { get; private set; }

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
            // Projected HP can run below zero; the real revive heals from zero, so the restored amount is
            // the full heal either way.
            RelicHpRestored += lizardTailHeal;
            hp = lizardTailHeal;
        }
    }

    private StateFingerprint BuildStateKey(
        int turn,
        CreatureReadValues player,
        SimPlayerCombatState playerState,
        SimulatedCombatState simulatedCombat,
        CombatPredictionSimulator simulator,
        int shufflesCrossed,
        IReadOnlySet<uint> processedEnemyDeaths, CompletedStateReadView? view = null)
    {
        StateFingerprintBuilder key = new();
        key.Add(turn);
        key.Add(player.CurrentHp);
        key.Add(player.MaxHp);
        key.Add(view?.Block ?? player.Block);
        key.Add(view?.Energy ?? playerState.Energy);
        key.Add(playerState.Stars);
        key.Add(shufflesCrossed);
        Player owner = _player;
        if (simulatedCombat.GetOsty(owner) is { } osty)
        {
            key.Add(ReadCreatureValues(simulator, osty, view).CurrentHp);
            key.Add(simulatedCombat.GetOstyMaxHp(simulator, owner));
            key.Add(simulatedCombat.IsOstyHittable(simulator, owner));
        }
        else
        {
            key.Add(0);
            key.Add(0);
            key.Add(false);
        }
        IReadOnlyList<Creature> knownEnemies = simulatedCombat.KnownEnemies;
        for (int enemyIndex = 0; enemyIndex < knownEnemies.Count; enemyIndex++)
        {
            Creature enemy = knownEnemies[enemyIndex];
            CreatureReadValues enemyState = ReadCreatureValues(simulator, enemy, view);
            key.Add(enemy.Monster?.Id.Entry);
            key.Add(enemy.CombatId ?? uint.MaxValue);
            key.Add(enemyState.Present);
            key.Add(enemyState.CurrentHp);
            key.Add(enemyState.MaxHp);
            key.Add(enemyState.Block);
        }
        SearchMeasurement pileFingerprintMeasurement = _run.Performance.Begin();
        if (view is null)
        {
            AppendPile(ref key, playerState.Hand, 'H');
            AppendPile(ref key, playerState.DrawPile, 'D');
            AppendPile(ref key, playerState.DiscardPile, 'C');
            AppendPile(ref key, playerState.ExhaustPile, 'X');
        }
        else
        {
            AppendPileValues(ref key, view.Hand, 'H');
            AppendPileValues(ref key, view.Draw, 'D');
            AppendPileValues(ref key, view.Discard, 'C');
            AppendPileValues(ref key, view.Exhaust, 'X');
        }
        AppendOrbs(ref key, simulator, playerState.OrbQueue);
        _run.Performance.End(SearchMetricPhase.PileFingerprint, pileFingerprintMeasurement);
        AppendRngState(ref key, view?.ShuffleRng ?? simulator.Rng.Shuffle.CaptureState());
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
        simulatedCombat.AppendFingerprint(ref key, simulator, view?.CardHistory, view?.EnemyRoster);
        _run.Performance.End(SearchMetricPhase.CombatFingerprint, combatFingerprintMeasurement);
        return key.Finish();
    }

    private static void AppendRngState(ref StateFingerprintBuilder key, Rng rng)
        => AppendRngState(ref key, rng.CaptureState());

    private static void AppendRngState(ref StateFingerprintBuilder key, PredictionRngState state)
    {
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
        AppendLocalCardKeywords(ref key, preview);
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

    private static void AppendLocalCardKeywords(
        ref StateFingerprintBuilder key,
        CardModel card)
    {
        ulong commonKeywordMask = 0;
        SortedSet<int>? overflowKeywords = null;
        foreach (CardKeyword keyword in card.GetKeywordsWithSources(KeywordSources.Local))
        {
            int value = (int)keyword;
            if ((uint)value < 64)
            {
                commonKeywordMask |= 1UL << value;
                continue;
            }
            (overflowKeywords ??= []).Add(value);
        }
        key.Add(commonKeywordMask);
        key.Add(overflowKeywords?.Count ?? 0);
        if (overflowKeywords == null)
            return;
        foreach (int keyword in overflowKeywords)
            key.Add(keyword);
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
        StateFingerprint fingerprint = BuildPileValuesFingerprint(pile.Cards);
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
