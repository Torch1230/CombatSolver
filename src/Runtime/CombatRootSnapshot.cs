using System.Diagnostics;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal readonly record struct SearchablePotionSlotSnapshot(
    int Slot,
    string PotionId,
    int StrategicHpCost);

internal sealed class CombatRootSnapshot
{
    private readonly CombatPredictionSimulator _rootSimulator;

    public Player PlayerIdentity { get; }
    public IReadOnlyList<Creature> Enemies { get; }
    public IntentForecast Forecast { get; }
    public LiveCombatStamp LiveStamp { get; }
    public ContinuationStamp ContinuationStamp { get; }
    public int PlayerCount { get; }
    public int StartTurnNumber { get; }
    public int InitialPlayerHp { get; }
    public int InitialPlayerMaxHp { get; }
    public int PotionSlotCount { get; }
    public IReadOnlyList<SearchablePotionSlotSnapshot> SearchablePotions { get; }
    public int SearchablePotionCount { get; }
    public int? MinimumSearchablePotionStrategicCost { get; }
    public int ZeroCostSearchablePotionCount { get; }
    public ulong InitialAliveEnemyMask { get; }
    public CombatSide CurrentSide { get; }
    public PlayerTurnPhase PlayerPhase { get; }
    public RoomType? EncounterRoomType { get; }
    public BossHpRelief BossHpRelief { get; }
    public bool IsActEndingBoss => BossHpRelief != BossHpRelief.None;
    public double CaptureElapsedMilliseconds { get; }
    public int CapturedCardCount { get; }
    public int CapturedPowerCount { get; }
    public int CapturedHookListenerCount { get; }
    public int CapturedRunModSubscriberCount { get; }
    public int CapturedCombatModSubscriberCount { get; }
    public bool CapturedBaseLibCardModifiers { get; }
    public bool HasUnusedCardReplayAllocator { get; }
    public bool HasRenewablePotionShapedRock { get; }

    private CombatRootSnapshot(
        Player playerIdentity,
        IReadOnlyList<Creature> enemies,
        IntentForecast forecast,
        LiveCombatStamp liveStamp,
        ContinuationStamp continuationStamp,
        CombatPredictionSimulator rootSimulator,
        int playerCount,
        int startTurnNumber,
        int initialPlayerHp,
        int initialPlayerMaxHp,
        int potionSlotCount,
        IReadOnlyList<SearchablePotionSlotSnapshot> searchablePotions,
        ulong initialAliveEnemyMask,
        CombatSide currentSide,
        PlayerTurnPhase playerPhase,
        RoomType? encounterRoomType,
        BossHpRelief bossHpRelief,
        double captureElapsedMilliseconds,
        int capturedCardCount,
        int capturedPowerCount,
        int capturedHookListenerCount,
        int capturedRunModSubscriberCount,
        int capturedCombatModSubscriberCount,
        bool capturedBaseLibCardModifiers,
        bool hasUnusedCardReplayAllocator,
        bool hasRenewablePotionShapedRock)
    {
        PlayerIdentity = playerIdentity;
        Enemies = enemies;
        Forecast = forecast;
        LiveStamp = liveStamp;
        ContinuationStamp = continuationStamp;
        _rootSimulator = rootSimulator;
        PlayerCount = playerCount;
        StartTurnNumber = startTurnNumber;
        InitialPlayerHp = initialPlayerHp;
        InitialPlayerMaxHp = initialPlayerMaxHp;
        PotionSlotCount = potionSlotCount;
        SearchablePotions = searchablePotions;
        SearchablePotionCount = searchablePotions.Count;
        MinimumSearchablePotionStrategicCost = searchablePotions.Count == 0
            ? null
            : searchablePotions.Min(potion => potion.StrategicHpCost);
        ZeroCostSearchablePotionCount = searchablePotions.Count(potion =>
            potion.StrategicHpCost == 0);
        InitialAliveEnemyMask = initialAliveEnemyMask;
        CurrentSide = currentSide;
        PlayerPhase = playerPhase;
        EncounterRoomType = encounterRoomType;
        BossHpRelief = bossHpRelief;
        CaptureElapsedMilliseconds = captureElapsedMilliseconds;
        CapturedCardCount = capturedCardCount;
        CapturedPowerCount = capturedPowerCount;
        CapturedHookListenerCount = capturedHookListenerCount;
        CapturedRunModSubscriberCount = capturedRunModSubscriberCount;
        CapturedCombatModSubscriberCount = capturedCombatModSubscriberCount;
        CapturedBaseLibCardModifiers = capturedBaseLibCardModifiers;
        HasUnusedCardReplayAllocator = hasUnusedCardReplayAllocator;
        HasRenewablePotionShapedRock = hasRenewablePotionShapedRock;
    }

    public static CombatRootSnapshot Capture(CombatState state)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("Combat root snapshot must be captured on the main thread.");
        Stopwatch stopwatch = Stopwatch.StartNew();

        PowerDynamicVarWarmup.EnsureMaterialized(state);
        CardDynamicVarWarmup.EnsureMaterialized(state);

        // Listener enumeration and third-party owner discovery are part of root capture.
        // Take the baseline first so any semantic mutation in those callbacks is rejected by
        // the existing after-capture stamp without paying for another full serialization.
        ContinuationStamp continuationBefore = ContinuationStamp.CaptureLive(state);
        LiveCombatStamp liveBefore = LiveCombatStamp.FromContinuation(continuationBefore);

        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有战斗状态。");
        AbstractModel[] liveCombatHookListeners = state.IterateHookListeners().ToArray();
        if (liveCombatHookListeners.Any(PredictionModModelSupport.IsBaseLibCardModifier))
        {
            PredictionModModelSupport.RegisterBaseLibCardModifierOwners(
                state.Players
                    .Where(candidate => candidate.PlayerCombatState != null)
                    .SelectMany(candidate => candidate.PlayerCombatState!.AllCards));
        }
        IntentForecast forecast = IntentForecaster.Build(state, SolverWeights.SetupValueHorizonTurns);

        SimulatedCombatState simulatedCombat = new(state, liveCombatHookListeners);
        CombatPredictionSimulator simulator = new(simulatedCombat);
        ContinuationStamp projected = ContinuationStamp.CapturePredicted(
            player,
            simulator,
            playerState.TurnNumber,
            forecast,
            playerState.TurnNumber);
        bool hasUnusedCardReplayAllocator = simulatedCombat.RelicsOf(player)
            .OfType<ThrowingAxe>()
            .Any(relic => !relic.IsMelted && !relic._usedThisCombat);
        bool hasRenewablePotionShapedRock = simulatedCombat.RelicsOf(player)
            .OfType<PetrifiedToad>()
            .Any(relic => !relic.IsMelted);
        SearchablePotionSlotSnapshot[] searchablePotions = player.PotionSlots
            .Select((potion, slot) => (Potion: potion, Slot: slot))
            .Where(item => item.Potion != null && PotionOnUseSupport.CanSearch(item.Potion))
            .Select(item => new SearchablePotionSlotSnapshot(
                item.Slot,
                item.Potion!.Id.Entry,
                PotionUsePolicy.StrategicHpCost(
                    item.Potion,
                    hasRenewablePotionShapedRock)))
            .ToArray();
        if (!string.Equals(
                continuationBefore.StateText,
                projected.StateText,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Combat root projection differs from the captured live state: " +
                continuationBefore.DescribeFirstDifference(projected));
        }

        ContinuationStamp continuationAfter = ContinuationStamp.CaptureLive(state);
        LiveCombatStamp liveAfter = LiveCombatStamp.FromContinuation(continuationAfter);
        if (!string.Equals(liveBefore.StateText, liveAfter.StateText, StringComparison.Ordinal)
            || !string.Equals(
                continuationBefore.StateText,
                continuationAfter.StateText,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Combat state changed while the root snapshot was being captured.");
        }

        ulong aliveEnemyMask = 0;
        for (int index = 0; index < state.Enemies.Count; index++)
        {
            if (state.Enemies[index].IsAlive)
                aliveEnemyMask |= 1UL << index;
        }
        int cardCount = state.Players
            .Where(candidate => candidate.PlayerCombatState != null)
            .Sum(candidate => candidate.PlayerCombatState!.AllCards.Count());
        int powerCount = state.Creatures.Sum(creature => creature.Powers.Count);
        stopwatch.Stop();

        return new CombatRootSnapshot(
            player,
            Array.AsReadOnly(state.Enemies.ToArray()),
            forecast,
            liveBefore,
            continuationBefore,
            simulator,
            state.Players.Count,
            playerState.TurnNumber,
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.PotionSlots.Count,
            Array.AsReadOnly(searchablePotions),
            aliveEnemyMask,
            state.CurrentSide,
            playerState.Phase,
            state.Encounter?.RoomType,
            ActEndingBossPolicy.ResolveHpRelief(state),
            stopwatch.Elapsed.TotalMilliseconds,
            cardCount,
            powerCount,
            simulatedCombat.RootHookListenerCount,
            simulatedCombat.RootRunModSubscriberCount,
            simulatedCombat.RootCombatModSubscriberCount,
            simulatedCombat.RootHasBaseLibCardModifiers,
            hasUnusedCardReplayAllocator,
            hasRenewablePotionShapedRock);
    }

    public CombatPredictionSimulator ForkSimulator() => _rootSimulator.Fork();
}
