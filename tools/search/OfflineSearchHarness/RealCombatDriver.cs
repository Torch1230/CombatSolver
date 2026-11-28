using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Dread;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

namespace OfflineSearchHarness;

internal enum RealCombatActionKind
{
    PlayCard,
    UsePotion,
    EndTurn,
}

internal readonly record struct RealCombatAction(
    RealCombatActionKind Kind,
    CardModel? Card = null,
    PotionModel? Potion = null,
    Creature? Target = null)
{
    public static RealCombatAction PlayCard(CardModel card, Creature? target = null)
        => new(RealCombatActionKind.PlayCard, Card: card, Target: target);

    public static RealCombatAction UsePotion(PotionModel potion, Creature? target = null)
        => new(RealCombatActionKind.UsePotion, Potion: potion, Target: target);

    public static RealCombatAction EndTurn()
        => new(RealCombatActionKind.EndTurn);
}

internal sealed record CardInjectionSpec(
    string CardId,
    int UpgradeLevel = 0,
    string? EnchantmentId = null,
    int EnchantmentAmount = 1,
    string? AfflictionId = null,
    int AfflictionAmount = 1,
    Dictionary<string, int>? DynamicVars = null,
    Dictionary<string, string>? EnumMembers = null);

internal sealed record RelicInjectionSpec(
    string RelicId,
    Dictionary<string, int>? IntegerMembers = null,
    Dictionary<string, bool>? BooleanMembers = null);

internal sealed record PowerInjectionSpec(
    string PowerId,
    int Amount,
    bool OnPlayer = true,
    int EnemyIndex = -1);

internal sealed record CombatSetupSpec
{
    public string CharacterId { get; init; } = "IRONCLAD";
    public string EncounterId { get; init; } = "FUZZY_WURM_CRAWLER_WEAK";
    public string? Boss1EncounterId { get; init; }
    public string? Boss2EncounterId { get; init; }
    public string Seed { get; init; } = "FUZZ0001";
    public GameMode GameMode { get; init; } = GameMode.Standard;
    public string? DominionId { get; init; }
    public int Ascension { get; init; }
    public int ActIndex { get; init; }
    public int? PlayerMaxHp { get; init; }
    public int? PlayerHp { get; init; }
    public int? PlayerGold { get; init; }
    public int? WuppyMaxHp { get; init; }
    public int? WuppyCurrentHp { get; init; }
    public bool ClearStarterDeck { get; init; }
    public bool ClearStarterRelics { get; init; }
    public CardInjectionSpec[] Cards { get; init; } = [];
    public string[] Relics { get; init; } = [];
    public RelicInjectionSpec[] RelicSpecs { get; init; } = [];
    public int? PotionSlotCount { get; init; }
    public string[] Potions { get; init; } = [];
    public PowerInjectionSpec[] InitialPowers { get; init; } = [];
    public string[] InitialOrbs { get; init; } = [];
    public int ExtraEnergy { get; init; }
    public int ExtraStars { get; init; }
}

/// <summary>
/// 纯 .NET 实机 sts2.dll 战斗驱动器：统一建局（Standard / Dread 全 12 Dominion / Blitz）、
/// 注入装备/状态、通过 MainLoopContext 执行 PlayCard / UsePotion / EndTurn 并稳定排空到
/// 下一安全玩家出牌边界或战斗结束（Victory / Defeat）。
/// </summary>
internal static class RealCombatDriver
{
    public static void CleanupActiveRun()
    {
        try
        {
            CardSelectorStack._testSelector = null;
        }
        catch
        {
            // ignore if uninitialized
        }

        if (RunManager.Instance.IsInProgress)
            RunManager.Instance.CleanUp(graceful: false);
        else if (CombatManager.Instance.IsInProgress)
            CombatManager.Instance.Reset(graceful: false);
    }

    public static HotCombatOutcome GetLiveOutcome(CombatState combat)
    {
        Player? player = LocalContext.GetMe(combat);
        if (player == null || player.Creature.IsDead || combat.RunState.IsGameOver)
            return HotCombatOutcome.Defeat;
        if (!CombatManager.Instance.IsInProgress || CombatManager.Instance.IsOverOrEnding)
        {
            if (combat.Encounter is BattlewornDummyEventEncounter { RanOutOfTime: true })
                return HotCombatOutcome.EventDefeat;
            return HotCombatOutcome.Victory;
        }
        return HotCombatOutcome.Ongoing;
    }

    /// <summary>
    /// 在纯 .NET 宿主中构建指定配置（含 Standard / Dread / Blitz）的实机战斗并推进到玩家第 1 回合出牌阶段。
    /// </summary>
    public static CombatState StartCombat(MainLoopContext loop, CombatSetupSpec spec, TimeSpan? timeout = null)
    {
        CleanupActiveRun();
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(60);

        using IDisposable setupSelectorScope = CardSelectorStack.SetTestSelector(new FallbackFirstCardSelector());
        Task enterTask = EnterCombatAsync(spec);
        loop.RunUntilCompleted(enterTask, limit, $"StartCombat({spec.CharacterId}/{spec.EncounterId}/{spec.GameMode})");

        CombatState combat = WaitForPlayableOrEnded(loop, limit);
        if (GetLiveOutcome(combat) == HotCombatOutcome.Ongoing)
        {
            Task postTask = ApplyPostStartInjectionsAsync(combat, spec);
            loop.RunUntilCompleted(postTask, TimeSpan.FromSeconds(20), "ApplyPostStartInjections");
            loop.DrainPending();
        }

        return combat;
    }

    /// <summary>
    /// 校验附魔是否能安全贴到目标卡牌。
    /// 原版 <c>Inky</c> 未覆写 <c>CanEnchant</c>（游戏内事件仅提供敌方目标牌），若贴到 Self/None 牌上
    /// 会在 <c>Inky.OnPlay</c> 对 <c>cardPlay.Target == null</c> 施加 WeakPower 触发 NRE。
    /// </summary>
    public static bool CanSafelyEnchant(EnchantmentModel enchantment, CardModel card)
    {
        if (!enchantment.CanEnchant(card))
            return false;
        if (string.Equals(enchantment.Id.Entry, "INKY", StringComparison.Ordinal))
            return card.TargetType is TargetType.AnyEnemy or TargetType.AllEnemies;
        return true;
    }

    private static (CardType Type, MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect Rider) ResolveMadScienceVariant(
        string seed,
        int cardIndex)
    {
        (CardType, MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect)[] variants =
        [
            (CardType.Attack, MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Sapping),
            (CardType.Attack, MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Violence),
            (CardType.Attack, MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Choking),
            (CardType.Skill, MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Energized),
            (CardType.Skill, MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Wisdom),
            (CardType.Power, MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Expertise),
            (CardType.Power, MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect.Curious),
        ];
        uint hash = 2166136261u;
        foreach (char ch in seed)
            hash = (hash ^ ch) * 16777619u;
        hash = (hash ^ (uint)cardIndex) * 16777619u;
        return variants[(int)(hash % (uint)variants.Length)];
    }

    /// <summary>
    /// 执行单步实机动作（PlayCard / UsePotion / EndTurn），通过 MainLoopContext 排空
    /// ActionExecutor.FinishedExecutingActions() 与回合循环，直到回到玩家出牌阶段或战斗终结。
    /// </summary>
    public static HotCombatOutcome RunRealCombatAction(
        MainLoopContext loop,
        CombatState combat,
        RealCombatAction action,
        TimeSpan? timeout = null,
        Func<Exception?>? externalErrorProbe = null)
    {
        Player player = LocalContext.GetMe(combat)
            ?? throw new InvalidOperationException("实机战斗缺少本地玩家。");
        PlayerCombatState pcs = player.PlayerCombatState
            ?? throw new InvalidOperationException("实机战斗缺少 PlayerCombatState。");

        int turnBefore = pcs.TurnNumber;
        ActionExecutor executor = RunManager.Instance.ActionExecutor;
        List<GameAction> observedActions = [];
        void OnBeforeExecuted(GameAction ga) => observedActions.Add(ga);

        executor.BeforeActionExecuted += OnBeforeExecuted;
        try
        {
            switch (action.Kind)
            {
                case RealCombatActionKind.PlayCard:
                {
                    CardModel card = action.Card
                        ?? throw new ArgumentException("PlayCard 缺少 CardModel。", nameof(action));
                    if (!card.TryManualPlay(action.Target))
                    {
                        card.CanPlay(out UnplayableReason reason, out AbstractModel? preventer);
                        throw new InvalidOperationException(
                            $"实机卡牌 {card.Id.Entry} TryManualPlay 返回 false " +
                            $"(target={action.Target?.Monster?.Id.Entry ?? "null"}, reason={reason}, preventer={preventer?.Id.Entry ?? "-"}, " +
                            $"energy={pcs.Energy}, stars={pcs.Stars})。");
                    }
                    break;
                }
                case RealCombatActionKind.UsePotion:
                {
                    PotionModel potion = action.Potion
                        ?? throw new ArgumentException("UsePotion 缺少 PotionModel。", nameof(action));
                    potion.EnqueueManualUse(action.Target);
                    break;
                }
                case RealCombatActionKind.EndTurn:
                {
                    CombatManager.Instance.OnEndedTurnLocally();
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                        new EndPlayerTurnAction(player, turnBefore));
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action.Kind, "未知实机动作类型。");
            }

            TimeSpan limit = timeout ?? TimeSpan.FromSeconds(30);
            DateTime deadline = DateTime.UtcNow + limit;

            while (true)
            {
                loop.DrainPending();

                // 检查选牌器等异步回调是否已捕获失败（如 StartTurnLoop 内部抛出的选牌失配，避免白等 30s 超时）
                if (externalErrorProbe?.Invoke() is { } externalError)
                    throw externalError;

                // 检查已执行动作是否内部抛错（非交互模式下 GameAction.Exception 记录失败原因）
                foreach (GameAction ga in observedActions)
                {
                    if (ga.Exception is { } actionError)
                        throw new InvalidOperationException($"实机动作 {ga} 执行异常：{actionError.Message}", actionError);
                }

                Task finished = executor.FinishedExecutingActions();
                if (finished.IsFaulted)
                    finished.GetAwaiter().GetResult();

                bool queueIdle = finished.IsCompleted && !executor.IsRunning;

                // 战斗已完全结束（等待 IsEnding 的异步收尾也跑完）
                if (!CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsEnding && queueIdle)
                {
                    loop.DrainPending();
                    return GetLiveOutcome(combat);
                }

                // 战斗仍在继续，检查是否到达安全的玩家出牌边界
                if (queueIdle
                    && CombatManager.Instance.IsInProgress
                    && !CombatManager.Instance.IsOverOrEnding
                    && combat.CurrentSide == CombatSide.Player
                    && player.PlayerCombatState is { Phase: PlayerTurnPhase.Play } curPcs
                    && !CombatManager.Instance.PlayerActionsDisabled
                    && !CombatManager.Instance.IsPlayerReadyToEndTurn(player))
                {
                    if (action.Kind != RealCombatActionKind.EndTurn || curPcs.TurnNumber > turnBefore)
                    {
                        loop.DrainPending();
                        // 再次确认排空后的状态稳定（防止刚入队了自动结算/胜负判定）
                        if (!executor.IsRunning && executor.FinishedExecutingActions().IsCompleted)
                        {
                            if (!CombatManager.Instance.IsInProgress && !CombatManager.Instance.IsEnding)
                                return GetLiveOutcome(combat);
                            if (CombatManager.Instance.IsInProgress
                                && !CombatManager.Instance.IsOverOrEnding
                                && combat.CurrentSide == CombatSide.Player
                                && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                                && !CombatManager.Instance.PlayerActionsDisabled
                                && !CombatManager.Instance.IsPlayerReadyToEndTurn(player))
                            {
                                return HotCombatOutcome.Ongoing;
                            }
                        }
                    }
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"实机动作 {action.Kind} 在 {limit.TotalSeconds:0.#}s 内未排空：" +
                        $"in_progress={CombatManager.Instance.IsInProgress} ending={CombatManager.Instance.IsEnding} " +
                        $"side={combat.CurrentSide} phase={player.PlayerCombatState?.Phase} " +
                        $"turn={player.PlayerCombatState?.TurnNumber} (before={turnBefore}) " +
                        $"exec_running={executor.IsRunning} disabled={CombatManager.Instance.PlayerActionsDisabled} " +
                        $"ready_end={CombatManager.Instance.IsPlayerReadyToEndTurn(player)}");
                }

                loop.PumpOnce(1);
            }
        }
        finally
        {
            executor.BeforeActionExecuted -= OnBeforeExecuted;
        }
    }

    private static async Task EnterCombatAsync(CombatSetupSpec spec)
    {
        CharacterModel character = ResolveModel(ModelDb.AllCharacters, spec.CharacterId, "角色");
        EncounterModel encounter = ResolveModel(ModelDb.All.OfType<EncounterModel>(), spec.EncounterId, "遭遇");

        if (RunManager.Instance.IsInProgress)
            throw new InvalidOperationException("已经有进行中的跑局。");

        UnlockState unlockState = UnlockState.all;
        List<ActModel> acts = ActModel.GetDefaultList().Select(a => a.ToMutable()).ToList();
        int clampedAscension = spec.GameMode == GameMode.Blitz
            ? Math.Clamp(spec.Ascension, 0, 5)
            : Math.Clamp(spec.Ascension, 0, 10);
        RunState runState = RunState.CreateForNewRun(
            [Player.CreateForNewRun(character, unlockState, 1uL)],
            acts,
            [],
            spec.GameMode,
            isCustom: false,
            clampedAscension,
            spec.Seed);

        RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);
        await RunManager.Instance.FinalizeStartingRelics();
        RunManager.Instance.Launch();

        int clampedAct = Math.Clamp(spec.ActIndex, 0, Math.Max(0, runState.Acts.Count - 1));
        if (spec.GameMode == GameMode.Blitz)
        {
            // Blitz 模式下 EnterAct(0) 会进入 Neow 事件房并生成事件/遗物 HoverTip，直接初始化当前幕后进目标战斗房即可。
            await RunManager.Instance.SetActInternal(clampedAct);
            await MegaCrit.Sts2.Core.Hooks.Hook.AfterActEntered(runState);
        }
        else
        {
            await RunManager.Instance.EnterAct(0, doTransition: false);
            if (clampedAct != 0)
                await RunManager.Instance.SetActInternal(clampedAct);
        }

        if (clampedAct == runState.Acts.Count - 1)
        {
            if (!string.IsNullOrWhiteSpace(spec.Boss1EncounterId))
                runState.Act.SetBossEncounter(ResolveModel(ModelDb.All.OfType<EncounterModel>(), spec.Boss1EncounterId, "Boss 1"));
            if (!string.IsNullOrWhiteSpace(spec.Boss2EncounterId))
                runState.Act.SetSecondBossEncounter(ResolveModel(ModelDb.All.OfType<EncounterModel>(), spec.Boss2EncounterId, "Boss 2"));
        }

        Player player = LocalContext.GetMe(runState)
            ?? throw new InvalidOperationException("创建跑局后找不到本地玩家。");

        // Dread 模式：显式设置指定的 Dominion（若有）
        if (spec.GameMode == GameMode.Dread && !string.IsNullOrWhiteSpace(spec.DominionId))
        {
            DominionModel dominion = ResolveModel(
                ModelDb.All.OfType<DominionModel>(), spec.DominionId, "Dominion");
            runState.SetDominion(dominion);
        }

        // 遗物准备：使用 AddRelicInternal 避免非战斗遗物触发地图/事件弹窗，同时保留完整战斗 Hook
        if (spec.ClearStarterRelics)
        {
            foreach (RelicModel existing in player.Relics.ToArray())
                player.RemoveRelicInternal(existing, silent: true);
        }

        HashSet<ModelId> ownedRelicIds = player.Relics.Select(r => r.Id).ToHashSet();
        foreach (string relicId in spec.Relics)
        {
            RelicModel canonical = ResolveModel(ModelDb.AllRelics, relicId, "遗物");
            if (!ownedRelicIds.Add(canonical.Id))
                continue;
            RelicModel mutable = canonical.ToMutable();
            if (mutable is MegaCrit.Sts2.Core.Models.Relics.Wuppy wuppyRelic)
            {
                int wuppyMaxHp = Math.Max(1, spec.WuppyMaxHp ?? MegaCrit.Sts2.Core.Models.Relics.Wuppy.weakHp);
                wuppyRelic.MaxHp = wuppyMaxHp;
                wuppyRelic.CurrentHp = Math.Clamp(spec.WuppyCurrentHp ?? wuppyMaxHp, 0, wuppyMaxHp);
            }
            player.AddRelicInternal(mutable, -1, silent: true);
        }

        foreach (RelicInjectionSpec relicSpec in spec.RelicSpecs)
        {
            RelicModel canonical = ResolveModel(ModelDb.AllRelics, relicSpec.RelicId, "遗物");
            if (!ownedRelicIds.Add(canonical.Id))
                continue;
            RelicModel mutable = canonical.ToMutable();
            if (mutable is MegaCrit.Sts2.Core.Models.Relics.Wuppy wuppyRelic)
            {
                int wuppyMaxHp = Math.Max(1, spec.WuppyMaxHp ?? MegaCrit.Sts2.Core.Models.Relics.Wuppy.weakHp);
                wuppyRelic.MaxHp = wuppyMaxHp;
                wuppyRelic.CurrentHp = Math.Clamp(spec.WuppyCurrentHp ?? wuppyMaxHp, 0, wuppyMaxHp);
            }
            player.AddRelicInternal(mutable, -1, silent: true);
            if (relicSpec.IntegerMembers != null)
            {
                foreach ((string memberName, int value) in relicSpec.IntegerMembers)
                    SetRelicStateMember(mutable, memberName, value);
            }
            if (relicSpec.BooleanMembers != null)
            {
                foreach ((string memberName, bool value) in relicSpec.BooleanMembers)
                    SetRelicStateMember(mutable, memberName, value);
            }
        }

        // 牌组准备
        if (spec.ClearStarterDeck)
        {
            foreach (CardModel card in player.Deck.Cards.ToArray())
            {
                player.Deck.RemoveInternal(card, silent: true);
                runState.RemoveCard(card);
            }
        }

        for (int cardSpecIndex = 0; cardSpecIndex < spec.Cards.Length; cardSpecIndex++)
        {
            CardInjectionSpec cardSpec = spec.Cards[cardSpecIndex];
            CardModel canonical = ResolveModel(ModelDb.AllCards, cardSpec.CardId, "卡牌");
            CardModel card = runState.CreateCard(canonical, player);
            if (card is MegaCrit.Sts2.Core.Models.Cards.MadScience madScience
                && madScience.TinkerTimeType == CardType.None)
            {
                (CardType ttType, MegaCrit.Sts2.Core.Models.Events.TinkerTime.RiderEffect ttRider) =
                    ResolveMadScienceVariant(spec.Seed, cardSpecIndex);
                madScience.TinkerTimeType = ttType;
                madScience.TinkerTimeRider = ttRider;
            }
            for (int u = 0; u < cardSpec.UpgradeLevel && card.IsUpgradable; u++)
                CardCmd.Upgrade(card, CardPreviewStyle.None);
            if (cardSpec.DynamicVars != null)
            {
                foreach ((string key, int value) in cardSpec.DynamicVars)
                {
                    if (!card.DynamicVars.TryGetValue(key, out var dynamicVar))
                        throw new InvalidOperationException($"卡牌 {card.Id.Entry} 不存在动态变量 {key}。");
                    dynamicVar.BaseValue = value;
                }
            }
            if (cardSpec.EnumMembers != null)
                ApplyCardEnumMembers(card, cardSpec.EnumMembers);
            if (!string.IsNullOrWhiteSpace(cardSpec.EnchantmentId))
            {
                EnchantmentModel ench = ResolveModel(
                    ModelDb.DebugEnchantments, cardSpec.EnchantmentId, "附魔").ToMutable();
                if (CanSafelyEnchant(ench, card))
                    CardCmd.Enchant(ench, card, Math.Max(1, cardSpec.EnchantmentAmount));
            }
            await CardPileCmd.Add(card, PileType.Deck);
        }

        // 药水槽准备
        foreach (PotionModel? p in player.PotionSlots.ToArray())
            p?.Discard();
        int desiredSlots = spec.PotionSlotCount ?? Math.Max(player.PotionSlots.Count, spec.Potions.Length);
        if (desiredSlots > player.PotionSlots.Count)
            player.SetMaxPotionCountInternal(desiredSlots);
        for (int i = 0; i < spec.Potions.Length && i < player.PotionSlots.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(spec.Potions[i]))
                continue;
            PotionModel canonicalPotion = ResolveModel(ModelDb.AllPotions, spec.Potions[i], "药水");
            PotionModel mutablePotion = canonicalPotion.ToMutable();
            player.AddPotionInternal(mutablePotion, i, silent: true);
        }

        // HP / MaxHP / Gold 调整
        if (spec.PlayerMaxHp is { } maxHp && maxHp > 0)
            player.Creature.SetMaxHpInternal(maxHp);
        if (spec.PlayerHp is { } hp && hp > 0)
            player.Creature.SetCurrentHpInternal(Math.Min(hp, player.Creature.MaxHp));
        if (spec.PlayerGold is { } gold && gold >= 0)
            player.Gold = gold;

        // 进战斗房：在 Dread 且指定了 Dominion 时避免触发 Boss 房自动卸载 Dominion
        EncounterModel mutableEncounter = encounter.ToMutable();
        RoomType roomType = encounter.RoomType is RoomType.Monster or RoomType.Elite or RoomType.Boss
            ? encounter.RoomType
            : RoomType.Monster;
        if (spec.GameMode == GameMode.Dread && !string.IsNullOrWhiteSpace(spec.DominionId) && roomType == RoomType.Boss)
            roomType = RoomType.Monster;
        MapPointType mapPointType = roomType switch
        {
            RoomType.Elite => MapPointType.Elite,
            RoomType.Boss => MapPointType.Boss,
            _ => MapPointType.Monster,
        };

        await RunManager.Instance.EnterRoomDebug(roomType, mapPointType, mutableEncounter);
    }

    private static async Task ApplyPostStartInjectionsAsync(CombatState combat, CombatSetupSpec spec)
    {
        Player player = LocalContext.GetMe(combat)!;
        PlayerCombatState pcs = player.PlayerCombatState!;
        BlockingPlayerChoiceContext choiceContext = new();

        // 1. 痛苦（Affliction）是纯战斗内属性，必须在开战后贴到战斗牌实例上
        foreach (CardInjectionSpec cardSpec in spec.Cards)
        {
            if (string.IsNullOrWhiteSpace(cardSpec.AfflictionId))
                continue;
            AfflictionModel afflCanonical = ResolveModel(
                ModelDb.DebugAfflictions, cardSpec.AfflictionId, "痛苦");
            CardModel? targetCard = pcs.AllCards.FirstOrDefault(c =>
                string.Equals(c.Id.Entry, cardSpec.CardId, StringComparison.OrdinalIgnoreCase)
                && c.Affliction == null
                && afflCanonical.CanAfflict(c));
            if (targetCard == null)
            {
                targetCard = pcs.AllCards.FirstOrDefault(c =>
                    c.Affliction == null && afflCanonical.CanAfflict(c));
            }
            if (targetCard != null)
            {
                AfflictionModel afflMutable = afflCanonical.ToMutable();
                await CardCmd.Afflict(afflMutable, targetCard, Math.Max(1, cardSpec.AfflictionAmount));
            }
        }

        // 2. 初始充能球
        if (spec.InitialOrbs.Length > 0)
        {
            if (pcs.OrbQueue.Capacity < spec.InitialOrbs.Length)
                await OrbCmd.AddSlots(player, spec.InitialOrbs.Length - pcs.OrbQueue.Capacity);
            foreach (string orbId in spec.InitialOrbs)
            {
                OrbModel orb = ResolveModel(ModelDb.Orbs, orbId, "充能球").ToMutable();
                await OrbCmd.Channel(choiceContext, orb, player);
            }
        }

        // 3. 初始 Power（玩家或敌人）
        foreach (PowerInjectionSpec powerSpec in spec.InitialPowers)
        {
            PowerModel canonical = ResolveModel(ModelDb.AllPowers, powerSpec.PowerId, "Power");
            if (powerSpec.OnPlayer)
            {
                await PowerCmd.Apply(choiceContext, canonical.ToMutable(), player.Creature,
                    powerSpec.Amount, player.Creature, null, silent: true);
            }
            else if (combat.Enemies.Count > 0)
            {
                if (powerSpec.EnemyIndex >= 0 && powerSpec.EnemyIndex < combat.Enemies.Count)
                {
                    Creature enemy = combat.Enemies[powerSpec.EnemyIndex];
                    if (enemy.IsAlive)
                        await PowerCmd.Apply(choiceContext, canonical.ToMutable(), enemy,
                            powerSpec.Amount, player.Creature, null, silent: true);
                }
                else
                {
                    foreach (Creature enemy in combat.Enemies.Where(e => e.IsAlive).ToArray())
                        await PowerCmd.Apply(choiceContext, canonical.ToMutable(), enemy,
                            powerSpec.Amount, player.Creature, null, silent: true);
                }
            }
        }

        // 4. 额外能量 / 星能
        if (spec.ExtraEnergy > 0)
            await PlayerCmd.GainEnergy(spec.ExtraEnergy, player);
        if (spec.ExtraStars > 0)
            await PlayerCmd.GainStars(spec.ExtraStars, player);
    }

    private static CombatState WaitForPlayableOrEnded(MainLoopContext loop, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        bool sawActiveCombat = false;
        while (true)
        {
            loop.DrainPending();
            CombatState? state = CombatManager.Instance.DebugOnlyGetState();
            Player? player = state == null ? null : LocalContext.GetMe(state);
            if (state != null)
            {
                if (CombatManager.Instance.IsInProgress || CombatManager.Instance.IsStarting || CombatManager.Instance.IsEnding)
                    sawActiveCombat = true;

                if (sawActiveCombat
                    && !CombatManager.Instance.IsInProgress
                    && !CombatManager.Instance.IsEnding
                    && !CombatManager.Instance.IsStarting)
                {
                    return state;
                }
                if (CombatManager.Instance.IsInProgress
                    && !CombatManager.Instance.IsOverOrEnding
                    && state.CurrentSide == CombatSide.Player
                    && player?.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                    && !RunManager.Instance.ActionExecutor.IsRunning)
                {
                    return state;
                }
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"等待玩家第 1 回合出牌阶段超时：in_progress={CombatManager.Instance.IsInProgress} " +
                    $"starting={CombatManager.Instance.IsStarting} ending={CombatManager.Instance.IsEnding} " +
                    $"side={state?.CurrentSide.ToString() ?? "-"} " +
                    $"phase={player?.PlayerCombatState?.Phase.ToString() ?? "-"}");
            }
            loop.PumpOnce(2);
        }
    }

    internal static TModel ResolveModel<TModel>(IEnumerable<TModel> candidates, string id, string kind)
        where TModel : AbstractModel
    {
        TModel? match = candidates.FirstOrDefault(m =>
            m.Id.Entry.Equals(id, StringComparison.OrdinalIgnoreCase)
            || m.Id.ToString().Equals(id, StringComparison.OrdinalIgnoreCase)
            || m.GetType().Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException($"找不到{kind}：{id}。");
    }

    private static void SetRelicStateMember<T>(RelicModel relic, string memberName, T value)
    {
        for (Type? type = relic.GetType(); type != null; type = type.BaseType)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly;
            if (type.GetProperty(memberName, flags) is { } property)
            {
                if (property.PropertyType != typeof(T) || property.SetMethod == null)
                {
                    throw new InvalidOperationException(
                        $"遗物 {relic.Id.Entry} 状态属性 {memberName} 不是可写的 {typeof(T).Name}。");
                }
                property.SetValue(relic, value);
                return;
            }
            if (type.GetField(memberName, flags) is { } field)
            {
                if (field.FieldType != typeof(T) || field.IsInitOnly)
                {
                    throw new InvalidOperationException(
                        $"遗物 {relic.Id.Entry} 状态字段 {memberName} 不是可写的 {typeof(T).Name}。");
                }
                field.SetValue(relic, value);
                return;
            }
        }
        throw new InvalidOperationException($"遗物 {relic.Id.Entry} 不存在状态成员 {memberName}。");
    }

    private static void ApplyCardEnumMembers(CardModel card, IReadOnlyDictionary<string, string> enumMembers)
    {
        foreach ((string memberName, string rawValue) in enumMembers)
        {
            bool applied = false;
            for (Type? type = card.GetType(); type != null; type = type.BaseType)
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.DeclaredOnly;
                if (type.GetProperty(memberName, flags) is { } property)
                {
                    if (!property.PropertyType.IsEnum || property.SetMethod == null)
                        throw new InvalidOperationException($"卡牌 {card.Id.Entry} 属性 {memberName} 不是可写的枚举。");
                    object parsed = Enum.Parse(property.PropertyType, rawValue, ignoreCase: true);
                    property.SetValue(card, parsed);
                    applied = true;
                    break;
                }
                if (type.GetField(memberName, flags) is { } field)
                {
                    if (!field.FieldType.IsEnum || field.IsInitOnly)
                        throw new InvalidOperationException($"卡牌 {card.Id.Entry} 字段 {memberName} 不是可写的枚举。");
                    object parsed = Enum.Parse(field.FieldType, rawValue, ignoreCase: true);
                    field.SetValue(card, parsed);
                    applied = true;
                    break;
                }
            }
            if (!applied)
                throw new InvalidOperationException($"卡牌 {card.Id.Entry} 不存在枚举成员 {memberName}。");
        }
    }
}

/// <summary>
/// 建局/Blitz Neow/回合前设置期间使用的确定性首项选牌器（不触碰任何 Godot UI）。
/// </summary>
internal sealed class FallbackFirstCardSelector : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        List<CardModel> list = options.ToList();
        int count = Math.Clamp(minSelect, 0, Math.Min(maxSelect, list.Count));
        return Task.FromResult<IEnumerable<CardModel>>(list.Take(count).ToList());
    }

    public CardRewardSelection GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
        => new() { card = options.Count > 0 ? options[0].Card : null };
}
