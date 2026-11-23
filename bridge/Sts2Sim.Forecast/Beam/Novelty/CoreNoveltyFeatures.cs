using System.Collections.Frozen;
using System.Numerics;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Content;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Orbs;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Powers;
using Sts2Sim.Core.Hooks;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Cards;
using Sts2Sim.Core.Models.Orbs;
using Sts2Sim.Core.Models.Powers;
using Sts2Sim.Core.MonsterMoves.Intents;
using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam.Novelty;

[Flags]
internal enum SearchRouteTraits
{
    None = 0,
    Scaling = 1 << 0,
    LongTermResource = 1 << 1,
    HpInvestment = 1 << 2,
    ReactiveDamage = 1 << 3,
    Resource = 1 << 4,
    Control = 1 << 5,
    RevivalWindow = 1 << 6,
    DeclinedExtraTurn = 1 << 7,
}

[Flags]
internal enum PersistentSetupTraits
{
    None = 0,
    Strength = 1 << 0,
    Dexterity = 1 << 1,
    Focus = 1 << 2,
    BlockRetention = 1 << 3,
    OrbEngine = 1 << 4,
    PassiveDamage = 1 << 5,
    ExhaustEngine = 1 << 6,
    PowerEngine = 1 << 7,
}

internal readonly record struct EarlyTurnOpeningKey(
    string Kind,
    string CardId,
    string PotionId,
    uint? TargetCombatId);

internal readonly record struct EarlyTurnStrategyKey(
    EarlyTurnOpeningKey Opening,
    SearchRouteTraits Traits,
    PersistentSetupTraits Setup,
    int PotionCount,
    int PersistentBuffBand,
    int HandValueBand,
    uint? FocusTargetCombatId);

internal readonly record struct NoveltyScalarFeatures(
    int Energy,
    int Stars,
    int HandCount,
    int ZeroCostPlayableCount,
    int ReachableHandValue,
    int PersistentBuffValue,
    int LatentSetupValue,
    int FutureResourceValue,
    int RetainedAttackValue,
    int ReplayPotentialValue,
    int DelayedDamageValue,
    int ReactiveDamageValue,
    int StrategicDamagePotential,
    int StrategicPreventionPotential,
    int StrategicResourcePotential,
    int StrategicCardAccessPotential,
    int StrategicScalingPotential,
    int EnemyStrengthSuppression,
    int EnemyWeakTurns,
    int EnemyVulnerableTurns);

internal static class CoreNoveltyFeatures
{
    internal static void CaptureNoveltyFacts(
        SearchNode node,
        CombatPolicy policy,
        List<BfwsFact> destination,
        Dictionary<(string Id, int Upgrade), int> zoneCountsScratch,
        List<(string Id, int Upgrade)> zoneOrderScratch)
    {
        destination.Clear();
        CombatState state = node.State;
        Player player = state.Players[0];
        Creature playerCreature = player.Creature;
        PlayerCombatState pcs = player.PlayerCombatState!;

        NoveltyScalarFeatures scalars = ComputeScalarFeatures(
            state, player, playerCreature, pcs, policy, out _, out _);

        ReadOnlySpan<int> values =
        [
            scalars.Energy,
            scalars.Stars,
            scalars.HandCount,
            scalars.ZeroCostPlayableCount,
            scalars.ReachableHandValue,
            scalars.PersistentBuffValue,
            scalars.LatentSetupValue,
            scalars.FutureResourceValue,
            scalars.RetainedAttackValue,
            scalars.ReplayPotentialValue,
            scalars.DelayedDamageValue,
            scalars.ReactiveDamageValue,
            scalars.StrategicDamagePotential,
            scalars.StrategicPreventionPotential,
            scalars.StrategicResourcePotential,
            scalars.StrategicCardAccessPotential,
            scalars.StrategicScalingPotential,
            scalars.EnemyStrengthSuppression,
            scalars.EnemyWeakTurns,
            scalars.EnemyVulnerableTurns,
        ];

        for (int i = 0; i < values.Length; i++)
        {
            int v = values[i];
            if (v > 0)
            {
                int encoded = i < 4 || i >= 18
                    ? Math.Min(7, v)
                    : Math.Min(7, 1 + BitOperations.Log2((uint)v));
                destination.Add(new BfwsFact(BfwsFactKind.Scalar, i, Value: encoded));
            }
        }

        destination.Add(new BfwsFact(BfwsFactKind.Energy, Value: pcs.Energy));
        destination.Add(new BfwsFact(BfwsFactKind.Block, Value: playerCreature.Block / 5));
        destination.Add(new BfwsFact(BfwsFactKind.Hp, Value: playerCreature.CurrentHp / 5));

        for (int zone = 0; zone < 4; zone++)
        {
            IReadOnlyList<CardModel> cards = zone switch
            {
                0 => pcs.Hand.Cards,
                1 => pcs.DrawPile.Cards,
                2 => pcs.DiscardPile.Cards,
                _ => pcs.ExhaustPile.Cards,
            };
            zoneCountsScratch.Clear();
            zoneOrderScratch.Clear();
            for (int c = 0; c < cards.Count; c++)
            {
                CardModel card = cards[c];
                var key = (card.Id.Entry, card.CurrentUpgradeLevel);
                if (zoneCountsScratch.TryGetValue(key, out int count))
                    zoneCountsScratch[key] = count + 1;
                else
                {
                    zoneCountsScratch.Add(key, 1);
                    zoneOrderScratch.Add(key);
                }
            }

            for (int i = 0; i < zoneOrderScratch.Count; i++)
            {
                (string Id, int Upgrade) entry = zoneOrderScratch[i];
                destination.Add(new BfwsFact(
                    BfwsFactKind.Zone,
                    zone,
                    entry.Id,
                    entry.Upgrade,
                    Math.Min(3, zoneCountsScratch[entry])));
            }
        }

        int drawTopCount = Math.Min(3, pcs.DrawPile.Cards.Count);
        for (int i = 0; i < drawTopCount; i++)
        {
            destination.Add(new BfwsFact(BfwsFactKind.Draw, i, pcs.DrawPile.Cards[i].Id.Entry));
        }

        AppendPowerFacts(playerCreature.Powers, ownerTag: 0, destination);
        IReadOnlyList<Creature> allies = state.Allies;
        for (int a = 0; a < allies.Count; a++)
        {
            Creature ally = allies[a];
            if (!ReferenceEquals(ally, playerCreature) && ally.IsAlive)
                AppendPowerFacts(ally.Powers, ownerTag: 1, destination);
        }
        IReadOnlyList<Creature> enemies = state.Enemies;
        for (int e = 0; e < enemies.Count; e++)
        {
            Creature enemy = enemies[e];
            if (!enemy.IsAlive)
                continue;
            AppendPowerFacts(enemy.Powers, ownerTag: 1, destination);
        }
    }

    internal static (int EnemyHpPartition, int PotionCount) ComputePartition(SearchNode node)
    {
        IReadOnlyList<Creature> enemies = node.State.Enemies;
        int enemyHp = 0;
        for (int i = 0; i < enemies.Count; i++)
        {
            if (enemies[i].IsAlive)
                enemyHp += enemies[i].CurrentHp;
        }
        return (enemyHp / 10, node.PotionUses);
    }

    internal static NodeEvalMetrics ComputeNodeMetrics(
        CombatState state,
        CombatPolicy policy,
        int projectedHp,
        int longTermResourceValue,
        Creature[]? targetScratch = null)
    {
        Player player = state.Players[0];
        Creature playerCreature = player.Creature;
        PlayerCombatState? pcs = player.PlayerCombatState;
        NoveltyScalarFeatures scalars = ComputeScalarFeaturesCore(
            state,
            player,
            playerCreature,
            pcs,
            policy,
            targetScratch,
            includeRetainedAttackValue: false,
            out _,
            out _,
            out uint? focusTargetCombatId,
            out int sandpitRemaining,
            out bool hasPositiveOrbEngine,
            out int liveDeckClutter,
            out int liveCardCount);

        return new NodeEvalMetrics(
            ProjectedHp: projectedHp,
            PersistentBuffValue: scalars.PersistentBuffValue,
            FutureResourceValue: scalars.FutureResourceValue,
            LatentSetupValue: scalars.LatentSetupValue,
            ReachableHandValue: scalars.ReachableHandValue,
            ZeroCostPlayableCount: scalars.ZeroCostPlayableCount,
            DelayedDamageValue: scalars.DelayedDamageValue,
            ReactiveDamageValue: scalars.ReactiveDamageValue,
            ReplayPotentialValue: scalars.ReplayPotentialValue,
            LongTermResourceValue: longTermResourceValue,
            SandpitRemaining: sandpitRemaining,
            EnemyStrengthSuppression: scalars.EnemyStrengthSuppression,
            EnemyWeakTurns: scalars.EnemyWeakTurns,
            EnemyVulnerableTurns: scalars.EnemyVulnerableTurns,
            LiveDeckClutter: liveDeckClutter,
            LiveCardCount: liveCardCount,
            FocusTargetCombatId: focusTargetCombatId,
            HasPositiveOrbEngine: hasPositiveOrbEngine);
    }

    internal static uint? ComputeFocusTargetCombatId(CombatState state, Creature[]? targetScratch = null)
    {
        Creature playerCreature = state.Players[0].Creature;
        Creature[] singleTarget = targetScratch ?? [playerCreature];
        uint? bestCombatId = null;
        int bestPressure = 0;
        int bestRemainingHp = int.MaxValue;
        int bestThreat = 0;
        IReadOnlyList<Creature> enemies = state.Enemies;
        for (int e = 0; e < enemies.Count; e++)
        {
            Creature enemy = enemies[e];
            if (!enemy.IsAlive || enemy.CombatId is not uint combatId)
                continue;
            int currentThreat = 0;
            if (enemy.Monster?.NextMove?.Intents is { } intents)
            {
                for (int ii = 0; ii < intents.Count; ii++)
                {
                    if (intents[ii] is AttackIntent attack)
                        currentThreat += Math.Max(0, attack.GetTotalDamage(singleTarget, enemy));
                }
            }
            int remainingHp = Math.Max(0, enemy.CurrentHp);
            int maxHp = Math.Max(1, enemy.MaxHp);
            int progress = Math.Max(0, maxHp - remainingHp);
            if (progress <= 0)
                continue;
            int pressure = (int)Math.Min(int.MaxValue, (long)progress * (16 + Math.Min(64, currentThreat)) * 1024 / maxHp);
            if (pressure < bestPressure
                || (pressure == bestPressure && currentThreat < bestThreat)
                || (pressure == bestPressure && currentThreat == bestThreat && remainingHp >= bestRemainingHp))
            {
                continue;
            }
            bestCombatId = combatId;
            bestPressure = pressure;
            bestRemainingHp = remainingHp;
            bestThreat = currentThreat;
        }
        return bestCombatId;
    }

    internal static SearchRouteTraits ClassifyCardTraits(
        SearchRouteTraits current,
        in NodeEvalMetrics beforeMetrics,
        in NodeEvalMetrics afterMetrics,
        CombatState beforeState,
        CombatState afterState,
        bool isPureCardAction = true,
        bool declinedExtraTurn = false)
    {
        SearchRouteTraits traits = current;
        if (declinedExtraTurn)
            traits |= SearchRouteTraits.DeclinedExtraTurn;
        if (afterMetrics.PersistentBuffValue > beforeMetrics.PersistentBuffValue
            || afterMetrics.DelayedDamageValue > beforeMetrics.DelayedDamageValue
            || afterMetrics.ReplayPotentialValue > beforeMetrics.ReplayPotentialValue
            || afterMetrics.LongTermResourceValue > beforeMetrics.LongTermResourceValue)
        {
            traits |= SearchRouteTraits.Scaling;
        }
        if (afterMetrics.LongTermResourceValue > beforeMetrics.LongTermResourceValue)
            traits |= SearchRouteTraits.LongTermResource;
        Creature beforePlayer = beforeState.Players[0].Creature;
        Creature afterPlayer = afterState.Players[0].Creature;
        if (afterPlayer.CurrentHp < beforePlayer.CurrentHp
            || afterPlayer.MaxHp < beforePlayer.MaxHp
            || afterPlayer.CumulativeHpLost > beforePlayer.CumulativeHpLost)
        {
            traits |= SearchRouteTraits.HpInvestment;
        }
        if (afterMetrics.ReactiveDamageValue > beforeMetrics.ReactiveDamageValue)
            traits |= SearchRouteTraits.ReactiveDamage;
        PlayerCombatState? beforePcs = beforeState.Players[0].PlayerCombatState;
        PlayerCombatState? afterPcs = afterState.Players[0].PlayerCombatState;
        int beforeEnergy = beforePcs?.Energy ?? 0;
        int afterEnergy = afterPcs?.Energy ?? 0;
        int beforeStars = beforePcs?.Stars ?? 0;
        int afterStars = afterPcs?.Stars ?? 0;
        int beforeHandCount = beforePcs?.Hand.Cards.Count ?? 0;
        int afterHandCount = afterPcs?.Hand.Cards.Count ?? 0;
        if (afterEnergy > beforeEnergy
            || afterStars > beforeStars
            || afterHandCount >= beforeHandCount
            || afterMetrics.ReachableHandValue > beforeMetrics.ReachableHandValue
            || afterMetrics.ZeroCostPlayableCount > beforeMetrics.ZeroCostPlayableCount
            || afterMetrics.FutureResourceValue > beforeMetrics.FutureResourceValue)
        {
            traits |= SearchRouteTraits.Resource;
        }
        if (afterMetrics.SandpitRemaining > beforeMetrics.SandpitRemaining
            || afterMetrics.EnemyStrengthSuppression > beforeMetrics.EnemyStrengthSuppression
            || afterMetrics.EnemyWeakTurns > beforeMetrics.EnemyWeakTurns
            || afterMetrics.EnemyVulnerableTurns > beforeMetrics.EnemyVulnerableTurns
            || afterMetrics.LiveDeckClutter < beforeMetrics.LiveDeckClutter
            || afterMetrics.DelayedDamageValue > beforeMetrics.DelayedDamageValue
            || !isPureCardAction)
        {
            traits |= SearchRouteTraits.Control;
        }
        if (OpensRevivalWindow(beforeState, afterState))
            traits |= SearchRouteTraits.RevivalWindow;
        return traits;
    }

    internal static SearchRouteTraits ClassifyPotionTraits(
        SearchRouteTraits current,
        in NodeEvalMetrics beforeMetrics,
        in NodeEvalMetrics afterMetrics,
        CombatState beforeState,
        CombatState afterState)
    {
        SearchRouteTraits traits = current;
        if (afterMetrics.PersistentBuffValue > beforeMetrics.PersistentBuffValue
            || afterMetrics.DelayedDamageValue > beforeMetrics.DelayedDamageValue)
        {
            traits |= SearchRouteTraits.Scaling;
        }
        PlayerCombatState? beforePcs = beforeState.Players[0].PlayerCombatState;
        PlayerCombatState? afterPcs = afterState.Players[0].PlayerCombatState;
        if ((afterPcs?.Energy ?? 0) > (beforePcs?.Energy ?? 0)
            || (afterPcs?.Stars ?? 0) > (beforePcs?.Stars ?? 0)
            || (afterPcs?.Hand.Cards.Count ?? 0) > (beforePcs?.Hand.Cards.Count ?? 0)
            || afterMetrics.FutureResourceValue > beforeMetrics.FutureResourceValue)
        {
            traits |= SearchRouteTraits.Resource;
        }
        if (afterMetrics.SandpitRemaining > beforeMetrics.SandpitRemaining
            || afterMetrics.EnemyStrengthSuppression > beforeMetrics.EnemyStrengthSuppression
            || afterMetrics.EnemyWeakTurns > beforeMetrics.EnemyWeakTurns
            || afterMetrics.LiveDeckClutter < beforeMetrics.LiveDeckClutter
            || afterMetrics.DelayedDamageValue > beforeMetrics.DelayedDamageValue
            || (TotalAliveEnemyHp(afterState) == TotalAliveEnemyHp(beforeState)
                && afterState.Players[0].Creature.Block == beforeState.Players[0].Creature.Block))
        {
            traits |= SearchRouteTraits.Control;
        }
        if (OpensRevivalWindow(beforeState, afterState))
            traits |= SearchRouteTraits.RevivalWindow;
        return traits;
    }

    private static int TotalAliveEnemyHp(CombatState state)
    {
        int total = 0;
        IReadOnlyList<Creature> enemies = state.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            if (enemies[i].IsAlive)
                total += enemies[i].CurrentHp;
        }
        return total;
    }

    internal static SearchRouteTraits ClassifyRoundTransitionTraits(
        SearchRouteTraits current,
        CombatState beforeState,
        CombatState afterState)
    {
        return OpensRevivalWindow(beforeState, afterState)
            ? current | SearchRouteTraits.RevivalWindow
            : current;
    }

    private static bool OpensRevivalWindow(CombatState beforeState, CombatState afterState)
    {
        int beforeDead = 0;
        int beforeAlive = 0;
        int afterAlive = 0;
        IReadOnlyList<Creature> beforeEnemies = beforeState.Enemies;
        for (int i = 0; i < beforeEnemies.Count; i++)
        {
            if (beforeEnemies[i].IsAlive)
                beforeAlive++;
            else
                beforeDead++;
        }
        IReadOnlyList<Creature> afterEnemies = afterState.Enemies;
        int afterDead = 0;
        for (int i = 0; i < afterEnemies.Count; i++)
        {
            if (afterEnemies[i].IsAlive)
                afterAlive++;
            else
                afterDead++;
        }
        return afterEnemies.Count > beforeEnemies.Count && afterAlive > 0
            || (afterDead < beforeDead && afterAlive > beforeAlive);
    }

    internal static EarlyTurnStrategyKey ExtractEarlyTurnStrategyKey(SearchNode node, CombatPolicy policy)
    {
        PlannedAction? first = null;
        for (SearchRouteStep? cur = node.Path; cur?.Action is not null; cur = cur.Previous)
            first = cur.Action;

        first ??= new PlannedAction(node.Turn, "EndTurn", null, 0, -1, null, -1, [], []);
        EarlyTurnOpeningKey openingKey = new(
            first.Kind,
            first.CardId ?? string.Empty,
            first.PotionId ?? string.Empty,
            node.OpeningTargetCombatId);

        CombatState state = node.State;
        Player player = state.Players[0];
        Creature creature = player.Creature;
        PlayerCombatState? pcs = player.PlayerCombatState;

        PersistentSetupTraits setup = PersistentSetupTraits.None;
        if (node.HasPositiveOrbEngine || (pcs?.OrbQueue.Orbs.Count ?? 0) > 0)
            setup |= PersistentSetupTraits.OrbEngine;

        IReadOnlyList<PowerModel> powers = creature.Powers;
        for (int i = 0; i < powers.Count; i++)
        {
            PowerModel p = powers[i];
            if (p.Amount <= 0)
                continue;
            switch (p)
            {
                case StrengthPower:
                    setup |= PersistentSetupTraits.Strength;
                    break;
                case DexterityPower:
                    setup |= PersistentSetupTraits.Dexterity;
                    break;
                case FocusPower:
                    setup |= PersistentSetupTraits.Focus;
                    break;
                case BarricadePower or BlurPower:
                    setup |= PersistentSetupTraits.BlockRetention;
                    break;
                case DarkEmbracePower or FeelNoPainPower:
                    setup |= PersistentSetupTraits.ExhaustEngine;
                    break;
                case NoxiousFumesPower or InfiniteBladesPower or ThornsPower or EnvenomPower or AccuracyPower:
                    setup |= PersistentSetupTraits.PassiveDamage;
                    break;
                case EchoFormPower or DemonFormPower or CorruptionPower or CreativeAiPower or AfterimagePower
                    or CuriousPower or WraithFormPower or VoidFormPower or OrbitPower or AutomationPower
                    or MasterPlannerPower or IterationPower or LethalityPower or DanseMacabrePower
                    or DemesnePower or PagestormPower:
                    setup |= PersistentSetupTraits.PowerEngine;
                    break;
            }
        }

        _ = policy;
        uint? focusEnemyCombatId = node.FocusTargetCombatId ?? ComputeFocusTargetCombatId(state);

        return new EarlyTurnStrategyKey(
            openingKey,
            node.RouteTraits,
            setup,
            Math.Min(3, node.PotionUses),
            node.PersistentBuffValue / 8,
            node.ReachableHandValue / 8,
            focusEnemyCombatId);
    }

    internal static NoveltyScalarFeatures ComputeScalarFeatures(
        CombatState state,
        Player player,
        Creature playerCreature,
        PlayerCombatState? pcs,
        CombatPolicy policy,
        out int totalThreat,
        out int incomingHitCount) =>
        ComputeScalarFeaturesCore(
            state,
            player,
            playerCreature,
            pcs,
            policy,
            targetScratch: null,
            includeRetainedAttackValue: true,
            out totalThreat,
            out incomingHitCount,
            out _,
            out _,
            out _,
            out _,
            out _);

    private struct PileStatsScratch
    {
        public int AttackCount;
        public int SkillCount;
        public int BlockSkillCount;
        public int PowerCount;
        public int ExhaustCount;
        public int ShivCount;
        public int StatusCount;
        public int SkillEnergy;
        public int PowerEnergy;
        public int TotalCardValue;
        public int BestCardValue;
        public int TotalAttackValue;
        public int ReplayPotential;
        public int ZeroCostPlayable;
        public int ReachableHandValue;
        public int RetainedAttackValue;
        public int LiveCards;
        public int LiveDeckClutter;
    }

    [Flags]
    private enum PileQueries
    {
        None = 0,
        NonHandCost = 1,
        Exhaust = 2,
        Shiv = 4,
        RetainedAttack = 8,
        All = NonHandCost | Exhaust | Shiv | RetainedAttack,
    }

    private static readonly FrozenDictionary<Type, PileQueries> PileQueryDependencies = BuildPileQueryDependencies();

    private static FrozenDictionary<Type, PileQueries> BuildPileQueryDependencies()
    {
        Type[] costParameters = [typeof(CardModel), typeof(decimal), typeof(decimal).MakeByRefType()];
        Type[] keywordParameters = [typeof(CardModel), typeof(ISet<CardKeyword>)];

        // Keep the original property reads whenever an active modifier could run, even
        // when its result would be unused. Its callback and exceptions are observable.
        return ContentRegistry.AllTypes.ToFrozenDictionary(type => type, type =>
        {
            PileQueries queries = PileQueries.None;
            if (type == typeof(CuriousPower) || type == typeof(CorruptionPower)
                || type.GetMethod(nameof(AbstractModel.TryModifyEnergyCostInCombat), costParameters)!.DeclaringType != typeof(AbstractModel)
                || type.GetMethod(nameof(AbstractModel.TryModifyEnergyCostInCombatLate), costParameters)!.DeclaringType != typeof(AbstractModel)
                || (typeof(CardModel).IsAssignableFrom(type)
                    && type.GetProperty(nameof(CardModel.EnergyCost))!.DeclaringType != typeof(CardModel)))
                queries |= PileQueries.NonHandCost;
            if (type == typeof(FeelNoPainPower) || type == typeof(DarkEmbracePower))
                queries |= PileQueries.Exhaust;
            if (type == typeof(AccuracyPower))
                queries |= PileQueries.Shiv;
            if (type.GetMethod(nameof(AbstractModel.TryModifyKeywordsInCombat), keywordParameters)!.DeclaringType != typeof(AbstractModel))
                queries |= PileQueries.Exhaust | PileQueries.RetainedAttack;
            return queries;
        });
    }

    private static PileQueries RequiredPileQueries(CombatState state, Player player, bool includeRetainedAttackValue)
    {
        var gameAssembly = typeof(AbstractModel).Assembly;
        if (!player.IsActiveForHooks || state.RunState.GetType().Assembly != gameAssembly
            || !state.Allies.Contains(player.Creature))
            return PileQueries.All;

        PileQueries queries = includeRetainedAttackValue ? PileQueries.RetainedAttack : PileQueries.None;
        // Inspect the current combat, including unregistered listeners and card attachments.
        // Extensions retain the eager property reads and their original exception behavior.
        foreach (AbstractModel listener in state.RunState.IterateHookListeners(state))
        {
            // Pile membership can change without reassigning the owner. Card getters dispatch
            // through their owner's combat, whose listeners this scan does not cover.
            if (listener is CardModel card && !ReferenceEquals(card.CombatState, state))
                return PileQueries.All;
            if (listener is null || !PileQueryDependencies.TryGetValue(listener.GetType(), out PileQueries needed))
                return PileQueries.All;
            queries |= needed;
        }
        return queries;
    }

    private static NoveltyScalarFeatures ComputeScalarFeaturesCore(
        CombatState state,
        Player player,
        Creature playerCreature,
        PlayerCombatState? pcs,
        CombatPolicy policy,
        Creature[]? targetScratch,
        bool includeRetainedAttackValue,
        out int totalThreat,
        out int incomingHitCount,
        out uint? focusTargetCombatId,
        out int sandpitRemaining,
        out bool hasPositiveOrbEngine,
        out int liveDeckClutter,
        out int liveCardCount)
    {
        totalThreat = 0;
        incomingHitCount = 0;
        focusTargetCombatId = null;
        sandpitRemaining = 0;
        int bestFocusPressure = 0;
        int bestFocusRemainingHp = int.MaxValue;
        int bestFocusThreat = 0;
        int enemyHp = 0;
        int strengthSuppression = 0;
        int weakTurns = 0;
        int vulnerableTurns = 0;
        int delayedDamage = 0;

        Creature[] singleTarget = targetScratch ?? [playerCreature];
        IReadOnlyList<Creature> allies = state.Allies;
        for (int a = 0; a < allies.Count; a++)
        {
            Creature ally = allies[a];
            if (!ally.IsAlive)
                continue;
            IReadOnlyList<PowerModel> allyPowers = ally.Powers;
            for (int p = 0; p < allyPowers.Count; p++)
            {
                if (allyPowers[p] is SandpitPower sp
                    && ReferenceEquals(sp.Target, playerCreature)
                    && sp.Owner.IsAlive)
                {
                    sandpitRemaining = checked(sandpitRemaining + Math.Max(0, (int)sp.Amount));
                }
            }
        }

        IReadOnlyList<Creature> enemies = state.Enemies;
        for (int e = 0; e < enemies.Count; e++)
        {
            Creature enemy = enemies[e];
            if (!enemy.IsAlive)
                continue;
            enemyHp += enemy.CurrentHp;
            int enemyThreat = 0;
            if (enemy.Monster?.NextMove?.Intents is { } intents)
            {
                for (int ii = 0; ii < intents.Count; ii++)
                {
                    if (intents[ii] is AttackIntent attack)
                    {
                        int dmg = Math.Max(0, attack.GetTotalDamage(singleTarget, enemy));
                        enemyThreat += dmg;
                        totalThreat += dmg;
                        incomingHitCount += Math.Max(0, attack.Repeats);
                    }
                }
            }

            if (enemy.CombatId is uint combatId)
            {
                int remainingHp = Math.Max(0, enemy.CurrentHp);
                int maxHp = Math.Max(1, enemy.MaxHp);
                int progress = Math.Max(0, maxHp - remainingHp);
                if (progress > 0)
                {
                    int pressure = (int)Math.Min(int.MaxValue, (long)progress * (16 + Math.Min(64, enemyThreat)) * 1024 / maxHp);
                    if (pressure > bestFocusPressure
                        || (pressure == bestFocusPressure && enemyThreat > bestFocusThreat)
                        || (pressure == bestFocusPressure && enemyThreat == bestFocusThreat && remainingHp < bestFocusRemainingHp))
                    {
                        focusTargetCombatId = combatId;
                        bestFocusPressure = pressure;
                        bestFocusRemainingHp = remainingHp;
                        bestFocusThreat = enemyThreat;
                    }
                }
            }

            int enemyCurrentHp = Math.Max(0, enemy.CurrentHp);
            int enemyPoison = 0;
            int enemyDemise = 0;
            int enemyDoom = 0;
            IReadOnlyList<PowerModel> enemyPowers = enemy.Powers;
            for (int p = 0; p < enemyPowers.Count; p++)
            {
                PowerModel power = enemyPowers[p];
                if (power is SandpitPower sp
                    && ReferenceEquals(sp.Target, playerCreature)
                    && sp.Owner.IsAlive)
                {
                    sandpitRemaining = checked(sandpitRemaining + Math.Max(0, (int)sp.Amount));
                }
                if (power is WeakPower && power.Amount > 0)
                    weakTurns += (int)power.Amount;
                else if (power is VulnerablePower && power.Amount > 0)
                    vulnerableTurns += (int)power.Amount;
                else if (power is StrengthPower && power.Amount < 0)
                    strengthSuppression += -(int)power.Amount;
                else if (power is PoisonPower && power.Amount > 0)
                    enemyPoison += (int)power.Amount;
                else if (power is DemisePower && power.Amount > 0)
                    enemyDemise += (int)power.Amount;
                else if (power is DoomPower && power.Amount > 0)
                    enemyDoom += (int)power.Amount;
            }
            int cappedDoom = Math.Min(enemyCurrentHp, enemyDoom);
            int doomProgress = enemyCurrentHp <= 0
                ? 0
                : (int)Math.Min(enemyCurrentHp, (long)cappedDoom * cappedDoom / enemyCurrentHp);
            delayedDamage = checked(delayedDamage + enemyPoison + enemyDemise + doomProgress);
        }

        int handCount = pcs?.Hand.Cards.Count ?? 0;
        var stats = new PileStatsScratch { BestCardValue = 1 };
        if (pcs is not null)
        {
            PileQueries queries = RequiredPileQueries(state, player, includeRetainedAttackValue);
            AccumulatePileStatic(pcs.Hand.Cards, isHand: true, pcs.Energy, pcs.Stars, queries, ref stats);
            AccumulatePileStatic(pcs.DrawPile.Cards, isHand: false, pcs.Energy, pcs.Stars, queries, ref stats);
            AccumulatePileStatic(pcs.DiscardPile.Cards, isHand: false, pcs.Energy, pcs.Stars, queries, ref stats);
        }
        liveDeckClutter = stats.LiveDeckClutter;
        liveCardCount = stats.LiveCards;

        int deckSize = Math.Max(1, stats.LiveCards);
        int damagePerCycle = Math.Max(1, stats.TotalAttackValue);
        int estimatedCycles = (int)Math.Ceiling((double)Math.Max(1, enemyHp) / damagePerCycle);
        int remainingTurns = Math.Clamp(estimatedCycles * Math.Max(1, (int)Math.Ceiling(deckSize / 5d)), 1, 8);
        int baseDraw = Math.Max(1, (int)Hook.ModifyHandDraw(state, player, 5m));
        int reachableCards = stats.LiveCards == 0 ? 0 : Math.Min(deckSize * 2, remainingTurns * Math.Min(10, baseDraw));

        int attackPlays = ScalePlays(stats.AttackCount, deckSize, reachableCards);
        int skillPlays = ScalePlays(stats.SkillCount, deckSize, reachableCards);
        int blockSkillPlays = ScalePlays(stats.BlockSkillCount, deckSize, reachableCards);
        int powerPlays = Math.Min(stats.PowerCount, reachableCards);
        int exhaustPlays = Math.Min(stats.ExhaustCount, reachableCards);
        int avgCardValue = Math.Max(1, stats.TotalCardValue / deckSize);
        int avgAttackValue = stats.AttackCount > 0 ? Math.Max(1, stats.TotalAttackValue / stats.AttackCount) : 0;

        int dmgPot = 0, prevPot = 0, resPot = 0, accessPot = 0, scalePot = 0;
        int reactiveDamage = 0;
        int latentSetup = Math.Min(120, stats.PowerCount * 10);
        int futureResource = 0;
        bool hasOrbScalingPower = false;

        IReadOnlyList<PowerModel> playerPowers = playerCreature.Powers;
        int maxEnergy = pcs?.MaxEnergy ?? 3;
        for (int i = 0; i < playerPowers.Count; i++)
        {
            PowerModel power = playerPowers[i];
            int amount = Math.Max(0, (int)power.Amount);
            if (amount == 0)
                continue;
            switch (power)
            {
                case AfterimagePower:
                    prevPot += Math.Min(totalThreat > 0 ? totalThreat * Math.Min(2, remainingTurns) : amount * reachableCards, amount * reachableCards);
                    break;
                case BufferPower:
                    int avgHit = Math.Max(1, totalThreat / Math.Max(1, incomingHitCount));
                    prevPot += totalThreat == 0 ? amount * 8 : Math.Min(totalThreat, amount * avgHit);
                    break;
                case FeelNoPainPower:
                    prevPot += amount * exhaustPlays;
                    break;
                case DarkEmbracePower:
                    accessPot += amount * exhaustPlays * avgCardValue;
                    break;
                case ThornsPower:
                    dmgPot += Math.Min(enemyHp, amount * incomingHitCount);
                    break;
                case SleightOfFleshPower:
                    reactiveDamage += amount;
                    dmgPot += Math.Min(enemyHp, amount);
                    break;
                case EchoFormPower:
                    dmgPot += Math.Min(enemyHp, stats.BestCardValue * Math.Min(amount, Math.Max(1, reachableCards)) * remainingTurns);
                    break;
                case EnvenomPower:
                    dmgPot += Math.Min(enemyHp, amount * attackPlays);
                    break;
                case AccuracyPower:
                    dmgPot += Math.Min(enemyHp, amount * ScalePlays(stats.ShivCount, deckSize, reachableCards));
                    break;
                case StrengthPower:
                    dmgPot += Math.Min(enemyHp, amount * Math.Max(1, attackPlays));
                    break;
                case LethalityPower when attackPlays > 0:
                    dmgPot += Math.Min(enemyHp, avgAttackValue * Math.Min(attackPlays, remainingTurns) * amount / 100);
                    break;
                case ReaperFormPower when attackPlays > 0:
                    dmgPot += Math.Min(enemyHp, avgAttackValue * attackPlays * amount);
                    break;
                case DexterityPower:
                    prevPot += amount * Math.Max(1, blockSkillPlays);
                    break;
                case DemonFormPower when attackPlays > 0:
                    dmgPot += Math.Min(enemyHp, amount * Math.Max(1, attackPlays / Math.Max(1, remainingTurns)) * remainingTurns * (remainingTurns + 1) / 2);
                    break;
                case CuriousPower:
                    resPot += Math.Min(stats.PowerEnergy, amount * powerPlays) * Math.Max(3, avgCardValue);
                    break;
                case CorruptionPower:
                    resPot += stats.SkillEnergy * Math.Max(3, avgCardValue);
                    break;
                case OrbitPower or AutomationPower:
                    resPot += amount * remainingTurns * Math.Max(3, avgCardValue);
                    break;
                case CreativeAiPower:
                    accessPot += amount * remainingTurns * avgCardValue;
                    break;
                case IterationPower:
                    accessPot += amount * Math.Min(remainingTurns, ScalePlays(stats.StatusCount, deckSize, reachableCards)) * avgCardValue;
                    break;
                case MasterPlannerPower:
                    accessPot += skillPlays * avgCardValue;
                    break;
                case DemesnePower:
                    resPot += amount * remainingTurns * Math.Max(3, avgCardValue);
                    accessPot += amount * remainingTurns * avgCardValue;
                    break;
                case PagestormPower:
                    accessPot += amount * remainingTurns * avgCardValue;
                    break;
                case DanseMacabrePower:
                    prevPot += amount * remainingTurns;
                    break;
                case PrepTimePower:
                    dmgPot += Math.Min(enemyHp, amount * Math.Min(remainingTurns, attackPlays));
                    break;
                case FocusPower or FurnacePower:
                    if (power is FocusPower)
                        hasOrbScalingPower = true;
                    scalePot += amount * remainingTurns * 2;
                    break;
                case ThunderPower:
                    hasOrbScalingPower = true;
                    dmgPot += Math.Min(enemyHp, amount * remainingTurns);
                    break;
                case LightningRodPower:
                    hasOrbScalingPower = true;
                    scalePot += amount * remainingTurns;
                    break;
                case EnergyNextTurnPower:
                    futureResource += amount * 16;
                    break;
                case DrawCardsNextTurnPower or StarNextTurnPower:
                    futureResource += amount * 8;
                    break;
                case RetainHandPower:
                    futureResource += amount * 4;
                    break;
                case SummonNextTurnPower:
                    futureResource += amount * 6;
                    break;
                default:
                    if (power.Type == PowerType.Buff && power is not ITemporaryPower)
                        scalePot += amount;
                    break;
            }
        }

        IReadOnlyList<OrbModel>? orbs = pcs?.OrbQueue.Orbs;
        int orbRetention = 0;
        if (orbs is not null)
        {
            for (int o = 0; o < orbs.Count; o++)
            {
                OrbModel orb = orbs[o];
                orbRetention += orb switch
                {
                    DarkOrb dark => (int)Math.Min(enemyHp, dark.EvokeVal + dark.PassiveVal),
                    PlasmaOrb plasma => (int)(plasma.PassiveVal * 12m + plasma.EvokeVal * 8m),
                    FrostOrb frost => (int)(frost.PassiveVal * 4m),
                    LightningOrb lightning => (int)(lightning.PassiveVal * 3m),
                    _ => (int)Math.Max(1m, orb.PassiveVal * 2m),
                };
            }
        }
        hasPositiveOrbEngine = hasOrbScalingPower || (orbs?.Count ?? 0) > 0;

        int persistentBuff = (int)Math.Clamp(
            (long)dmgPot + prevPot + resPot + accessPot + scalePot + orbRetention + Math.Max(0, maxEnergy - 3) * 8L,
            0L,
            int.MaxValue);

        return new NoveltyScalarFeatures(
            Energy: pcs?.Energy ?? 0,
            Stars: pcs?.Stars ?? 0,
            HandCount: handCount,
            ZeroCostPlayableCount: stats.ZeroCostPlayable,
            ReachableHandValue: stats.ReachableHandValue,
            PersistentBuffValue: persistentBuff,
            LatentSetupValue: latentSetup,
            FutureResourceValue: futureResource,
            RetainedAttackValue: stats.RetainedAttackValue,
            ReplayPotentialValue: stats.ReplayPotential,
            DelayedDamageValue: delayedDamage,
            ReactiveDamageValue: reactiveDamage,
            StrategicDamagePotential: dmgPot,
            StrategicPreventionPotential: prevPot,
            StrategicResourcePotential: resPot,
            StrategicCardAccessPotential: accessPot,
            StrategicScalingPotential: scalePot,
            EnemyStrengthSuppression: strengthSuppression,
            EnemyWeakTurns: weakTurns,
            EnemyVulnerableTurns: vulnerableTurns);

        static int ScalePlays(int matching, int total, int reachable) =>
            matching == 0 ? 0 : Math.Max(1, (int)Math.Ceiling((double)matching * reachable / total));
    }

    private static void AccumulatePileStatic(
        IReadOnlyList<CardModel> cards,
        bool isHand,
        int energy,
        int stars,
        PileQueries queries,
        ref PileStatsScratch stats)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            CardModel card = cards[i];
            stats.LiveCards++;
            if (card.Type is CardType.Status or CardType.Curse)
                stats.LiveDeckClutter++;
            double cv = EstimateCardValue(card);
            int iv = Math.Max(1, (int)Math.Ceiling(cv));
            stats.TotalCardValue += iv;
            stats.BestCardValue = Math.Max(stats.BestCardValue, iv);

            int cost = (isHand || (queries & PileQueries.NonHandCost) != 0) && !card.CostsXEnergy
                ? Math.Max(0, card.EnergyCost)
                : 0;
            int replays = Math.Max(0, card.BaseReplayCount);
            for (int ec = 0; ec < card.Enchantments.Count; ec++)
                replays = card.Enchantments[ec].EnchantPlayCount(replays);
            if (replays > 0)
                stats.ReplayPotential += replays * iv;

            if (isHand)
            {
                if (card.CanPlay(out _))
                {
                    if (cost == 0)
                        stats.ZeroCostPlayable++;
                    if (cost <= energy && Math.Max(0, card.StarCost) <= stars)
                        stats.ReachableHandValue += iv;
                }
                if ((queries & PileQueries.RetainedAttack) != 0 && card.Type == CardType.Attack
                    && (card.HasKeyword(CardKeyword.Retain) || card.TemporaryRetainThisTurn))
                    stats.RetainedAttackValue += iv;
            }

            switch (card.Type)
            {
                case CardType.Attack:
                    stats.AttackCount++;
                    stats.TotalAttackValue += iv;
                    break;
                case CardType.Skill:
                    stats.SkillCount++;
                    stats.SkillEnergy += cost;
                    if (card.GainsBlock)
                        stats.BlockSkillCount++;
                    break;
                case CardType.Power:
                    stats.PowerCount++;
                    stats.PowerEnergy += cost;
                    break;
                case CardType.Status:
                    stats.StatusCount++;
                    break;
            }
            if ((queries & PileQueries.Exhaust) != 0 && card.HasKeyword(CardKeyword.Exhaust))
                stats.ExhaustCount++;
            if ((queries & PileQueries.Shiv) != 0 && card.Tags.Contains(CardTag.Shiv))
                stats.ShivCount++;
        }
    }

    private static double EstimateCardValue(CardModel card)
    {
        if (card is ICardChoiceValueProvider cv && cv.CardChoiceValue > 0d)
            return cv.CardChoiceValue;
        if (card is ICardChoiceBaseValueProvider cb && cb.CardChoiceBaseValues is { } bv)
            return Math.Max(1d, bv.Damage + bv.Block + bv.Cards * 3d + bv.Energy * 4d + bv.Stars * 3d);
        return 1d;
    }

    private static void AppendPowerFacts(
        IReadOnlyList<PowerModel> powers,
        int ownerTag,
        List<BfwsFact> destination)
    {
        for (int i = 0; i < powers.Count; i++)
        {
            PowerModel p = powers[i];
            int amt = (int)Math.Clamp(p.Amount, int.MinValue, int.MaxValue);
            if (amt == 0)
                continue;
            destination.Add(new BfwsFact(BfwsFactKind.Power, ownerTag, p.Id.Entry, amt));
        }
    }
}
