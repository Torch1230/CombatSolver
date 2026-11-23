using System.Collections.Frozen;
using System.Diagnostics;
using CombatSolver.HotKernel;
using CombatSolver.HotSearch;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

internal readonly record struct SearchablePotionSlotSnapshot(
    int Slot,
    string PotionId,
    int StrategicHpCost);

internal sealed class CombatRootSnapshot
{
    public HotRootBundle HotRoot { get; }
    public Player PlayerIdentity { get; }
    public IReadOnlyList<Creature> Enemies { get; }
    public IntentForecast Forecast { get; }
    public LiveCombatStamp LiveStamp { get; }
    public ContinuationStamp ContinuationStamp { get; }
    public int PlayerCount { get; }
    public int StartTurnNumber { get; }
    public int TotalFloor { get; }
    public int InitialPlayerHp { get; }
    public int InitialPlayerMaxHp { get; }
    public int InitialBrightestFlameMaxHpSpent => HotRoot.RootState.BrightestFlameMaxHpSpent;
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
    public IReadOnlySet<string> PlayerCardIds { get; }
    internal HotCombatRulesSnapshot Rules => HotRoot.Rules.RulesSnapshot;
    public bool HasRenewablePotionShapedRock { get; }
    public PostCombatRelicHealProfile PostCombatRelicHeal { get; }
    public PotionRewardOutlook PotionRewardOutlook { get; }

    private CombatRootSnapshot(
        HotRootBundle hotRoot,
        Player playerIdentity,
        IReadOnlyList<Creature> enemies,
        IntentForecast forecast,
        LiveCombatStamp liveStamp,
        ContinuationStamp continuationStamp,
        int playerCount,
        int startTurnNumber,
        int totalFloor,
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
        IReadOnlySet<string> playerCardIds,
        bool hasRenewablePotionShapedRock,
        PostCombatRelicHealProfile postCombatRelicHeal,
        PotionRewardOutlook potionRewardOutlook)
    {
        HotRoot = hotRoot;
        PlayerIdentity = playerIdentity;
        Enemies = enemies;
        Forecast = forecast;
        LiveStamp = liveStamp;
        ContinuationStamp = continuationStamp;
        PlayerCount = playerCount;
        StartTurnNumber = startTurnNumber;
        TotalFloor = totalFloor;
        InitialPlayerHp = initialPlayerHp;
        InitialPlayerMaxHp = initialPlayerMaxHp;
        PotionSlotCount = potionSlotCount;
        SearchablePotions = searchablePotions;
        SearchablePotionCount = searchablePotions.Count;
        MinimumSearchablePotionStrategicCost = searchablePotions.Count == 0
            ? null
            : searchablePotions.Min(static potion => potion.StrategicHpCost);
        ZeroCostSearchablePotionCount = searchablePotions.Count(static potion =>
            potion.StrategicHpCost == 0);
        InitialAliveEnemyMask = initialAliveEnemyMask;
        CurrentSide = currentSide;
        PlayerPhase = playerPhase;
        EncounterRoomType = encounterRoomType;
        BossHpRelief = bossHpRelief;
        CaptureElapsedMilliseconds = captureElapsedMilliseconds;
        CapturedCardCount = capturedCardCount;
        CapturedPowerCount = capturedPowerCount;
        PlayerCardIds = playerCardIds;
        HasRenewablePotionShapedRock = hasRenewablePotionShapedRock;
        PostCombatRelicHeal = postCombatRelicHeal;
        PotionRewardOutlook = potionRewardOutlook;
    }

    public static CombatRootSnapshot Capture(CombatState state, bool predictPotionReward = false)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("Combat root snapshot must be captured on the main thread.");

        Stopwatch stopwatch = Stopwatch.StartNew();
        HotRootBundle hotRoot = HotRootBuilder.BuildFromLive(state);
        CombatHotState rootState = hotRoot.RootState;
        ContinuationStamp continuationStamp = ContinuationStamp.CaptureFromHot(
            in rootState,
            hotRoot.RootCold,
            hotRoot.Rules);
        LiveCombatStamp liveStamp = LiveCombatStamp.FromContinuation(continuationStamp);

        Player player = LocalContext.GetMe(state)
            ?? throw new InvalidOperationException("找不到本地玩家。");
        PlayerCombatState playerState = player.PlayerCombatState
            ?? throw new InvalidOperationException("玩家没有战斗状态。");

        IntentForecast forecast = IntentForecaster.Build(state, HotSearchConstants.SetupValueHorizonTurns);

        HotRules rules = hotRoot.Rules;
        bool hasRenewablePotionShapedRock = rules.HasRenewablePotionShapedRock;
        PostCombatRelicHealProfile postCombatRelicHeal = new(
            rules.PostCombatRelicUnconditionalHeal,
            rules.PostCombatRelicWoundedHeal,
            rules.PostCombatRelicWoundedHpPercent);
        PotionRewardOutlook potionRewardOutlook = predictPotionReward
            ? PotionRewardOutlook.Capture(player, state, player.Relics.Where(static relic => !relic.IsMelted))
            : PotionRewardOutlook.None;

        List<SearchablePotionSlotSnapshot> searchablePotions = [];
        int slotCount = Math.Min((int)hotRoot.RootState.PotionSlotCount, HotLimits.MaxPotions);
        for (int slot = 0; slot < slotCount; slot++)
        {
            ushort defId = hotRoot.RootState.Potions[slot];
            if (defId == 0 || defId >= rules.Potions.Length)
                continue;
            HotPotionDef def = rules.Potions[defId];
            if (!def.IsCombatUsable)
                continue;
            searchablePotions.Add(new SearchablePotionSlotSnapshot(
                slot,
                def.Entry,
                HotPotionPolicy.ComputePotionDefCost(def, hasRenewablePotionShapedRock)));
        }

        ulong aliveEnemyMask = 0;
        for (int index = 0; index < state.Enemies.Count; index++)
        {
            if (state.Enemies[index].IsAlive)
                aliveEnemyMask |= 1UL << index;
        }

        IReadOnlySet<string> playerCardIds = playerState.Hand.Cards
            .Concat(playerState.DrawPile.Cards)
            .Concat(playerState.DiscardPile.Cards)
            .Select(static card => card.Id.Entry)
            .ToFrozenSet(StringComparer.Ordinal);

        stopwatch.Stop();

        return new CombatRootSnapshot(
            hotRoot,
            player,
            Array.AsReadOnly(state.Enemies.ToArray()),
            forecast,
            liveStamp,
            continuationStamp,
            state.Players.Count,
            playerState.TurnNumber,
            state.RunState.TotalFloor,
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.PotionSlots.Count,
            Array.AsReadOnly(searchablePotions.ToArray()),
            aliveEnemyMask,
            state.CurrentSide,
            playerState.Phase,
            state.Encounter?.RoomType,
            ActEndingBossPolicy.ResolveHpRelief(state),
            stopwatch.Elapsed.TotalMilliseconds,
            hotRoot.RootState.TotalCardCount,
            hotRoot.RootState.PowerCount,
            playerCardIds,
            hasRenewablePotionShapedRock,
            postCombatRelicHeal,
            potionRewardOutlook);
    }
}
