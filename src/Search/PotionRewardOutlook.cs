using CombatSolver.HotKernel;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

/// <summary>
/// What the reward roll after this fight will do, as far as it can be read at root capture.
/// </summary>
internal enum PotionRewardForecast
{
    /// <summary>The roll could not be mirrored; only the saved odds are known.</summary>
    Unknown,
    /// <summary>This room offers no combat rewards at all (the final boss).</summary>
    NoRewards,
    /// <summary>The roll will not give a potion.</summary>
    NoDrop,
    /// <summary>The roll gives a potion; <see cref="PotionRewardOutlook.ForecastPotionId"/> names it.</summary>
    Drop,
}

/// <summary>
/// What the player can expect from the potion reward once this fight is won.
/// </summary>
internal readonly record struct PotionRewardOutlook(
    float DropChance,
    bool BeltFull,
    bool ProcureBlocked,
    PotionRewardForecast Forecast,
    string? ForecastPotionId,
    int ForecastPotionStrategicHpCost)
{
    public const float EliteDropBonus = 0.125f;
    public bool Enabled { get; init; }

    public static PotionRewardOutlook None => default;

    public PotionRewardOutlook(float dropChance, bool beltFull, bool procureBlocked)
        : this(dropChance, beltFull, procureBlocked, PotionRewardForecast.Unknown, null, 0)
    {
    }

    /// <summary>
    /// HP the expected reward is worth to a route that spends a paid potion, in the strategic-cost scale.
    /// </summary>
    public int ReplacementHpCredit => BeltFull && !ProcureBlocked && Forecast == PotionRewardForecast.Drop
        ? ForecastPotionStrategicHpCost
        : 0;

    /// <summary>
    /// Reads the outlook from the live run on the main thread as part of root capture.
    /// </summary>
    public static PotionRewardOutlook Capture(
        Player player,
        CombatState state,
        IEnumerable<RelicModel> relics,
        PotionHpThresholds? thresholds = null)
    {
        if (state.Encounter?.RoomType is not { } room || !room.IsCombatRoom())
            return None;
        PotionHpThresholds effectiveThresholds = thresholds ?? PotionHpThresholds.Default;
        bool beltFull = player.PotionSlots.Count > 0 && player.PotionSlots.All(potion => potion != null);
        bool procureBlocked = relics.OfType<Sozu>().Any();
        if (player.RunState.GameMode == GameMode.Blitz)
            return CaptureBlitz(player, state, beltFull, procureBlocked, effectiveThresholds);
        bool forced = Hook.ShouldForcePotionReward(player.RunState, player, room);
        float chance = forced ? 1f : DropChanceFor(player.PlayerOdds.PotionReward.CurrentValue, room);
        PotionRewardOutlook odds = new(chance, beltFull, procureBlocked) { Enabled = true };

        IRunState runState = player.RunState;
        if (room == RoomType.Boss && runState.CurrentActIndex >= runState.Acts.Count - 1)
            return odds with { Forecast = PotionRewardForecast.NoRewards };
        if (IsTutorialRewardSet(player, room, runState))
            return odds;

        Rng rewards = new(player.PlayerRng.Rewards.ToSerializable());
        bool drop = forced || rewards.NextFloat() < chance;
        if (!drop)
            return odds with { Forecast = PotionRewardForecast.NoDrop };
        ReplayGoldPopulate(rewards, state.Encounter, room, runState.CurrentRoom as CombatRoom);
        PotionModel potion = PotionFactory.CreateRandomPotionOutOfCombat(player, rewards);
        return odds with
        {
            Forecast = PotionRewardForecast.Drop,
            ForecastPotionId = potion.Id.Entry,
            ForecastPotionStrategicHpCost = ResolveCatalogCost(potion, effectiveThresholds, player.Creature.MaxHp),
        };
    }

    private static PotionRewardOutlook CaptureBlitz(
        Player player,
        CombatState state,
        bool beltFull,
        bool procureBlocked,
        PotionHpThresholds thresholds)
    {
        var run = player.RunState;
        var encounter = state.Encounter!;
        RoomType room = encounter.RoomType;
        if (room == RoomType.Boss && run.CurrentActIndex >= 2)
            return new(0, beltFull, procureBlocked, PotionRewardForecast.NoRewards, null, 0) { Enabled = true };
        bool drop = room == RoomType.Boss || room == RoomType.Monster && !encounter.IsWeak;
        if (!drop)
            return new(0, beltFull, procureBlocked, PotionRewardForecast.NoDrop, null, 0) { Enabled = true };

        Rng rewards = new(player.PlayerRng.Rewards.ToSerializable());
        CardRarity rarity = room == RoomType.Boss ? CardRarity.Rare : CardRarity.Common;
        CardCreationOptions options = CardCreationOptions.ForNonCombatWithUniformOdds(
            [player.Character.CardPool], card => card.Rarity == rarity)
            .WithFlags(CardCreationFlags.NoUpgradeRoll | CardCreationFlags.IsFromCombat | CardCreationFlags.IsCardReward);
        List<CardModel> selected = [];
        for (int index = 0; index < 3; index++)
        {
            CardCreationOptions modified = Hook.ModifyCardRewardCreationOptions(run, player, options);
            var candidates = modified.GetPossibleCards(player).Except(selected)
                .Where(card => card.IsAllowedInRun(run) && card.Rarity is not (CardRarity.Basic or CardRarity.Ancient)).ToArray();
            selected.Add(rewards.NextItem(candidates) ?? throw new InvalidOperationException("Empty Blitz reward card pool."));
        }
        HashSet<CardModel> enchanted = [];
        foreach (AbstractModel listener in run.IterateHookListeners(null))
        {
            if (listener is RelicModel relic && (relic.Owner != player || relic.IsMelted)) continue;
            switch (listener)
            {
                case FresnelLens:
                    foreach (CardModel card in selected.Where(card => ModelDb.Enchantment<Nimble>().CanEnchant(card))) enchanted.Add(card);
                    break;
                case Glitter:
                case SilkenTress { IsUsed: false }:
                    enchanted.UnionWith(selected.Where(card => ModelDb.Enchantment<Glam>().CanEnchant(card)));
                    break;
                case WingCharm:
                    CardModel[] candidates = selected.Where(card => !enchanted.Contains(card)
                        && ModelDb.Enchantment<Swift>().CanEnchant(card)).ToArray();
                    if (candidates.Length > 0) enchanted.Add(rewards.NextItem(candidates)!);
                    break;
                case MegaCrit.Sts2.Core.Models.Blitz.BlitzRunModel when room != RoomType.Boss:
                    if (run.CurrentActIndex == 1) _ = rewards.NextItem(selected);
                    else if (run.CurrentActIndex == 2) _ = selected.UnstableShuffle(rewards).Take(2).ToArray();
                    break;
            }
        }
        PotionModel potion = PotionFactory.CreateRandomPotionOutOfCombat(player, rewards);
        return new(1, beltFull, procureBlocked, PotionRewardForecast.Drop, potion.Id.Entry,
            ResolveCatalogCost(potion, thresholds, player.Creature.MaxHp)) { Enabled = true };
    }

    private static int ResolveCatalogCost(PotionModel potion, PotionHpThresholds thresholds, int playerMaxHp)
    {
        if (thresholds.TryGetOverride(potion.Id.Entry, out int overrideHp))
            return overrideHp;
        if (potion.Rarity == PotionRarity.Token)
            return 0;
        HotCustomPotionId id = HotPotionCatalog.FindByEntry(potion.Id.Entry);
        if (id == HotCustomPotionId.Ambergris)
            return PotionHpThresholds.DefaultAmbergrisCost(playerMaxHp);
        if (id != HotCustomPotionId.None)
            return HotPotionCatalog.Get(id).BaseStrategicHpCost;
        return PotionHpThresholds.DefaultStandard;
    }

    public static float DropChanceFor(float currentOdds, RoomType roomType)
        => Math.Clamp(currentOdds + (roomType == RoomType.Elite ? EliteDropBonus : 0f), 0f, 1f);

    private static bool IsTutorialRewardSet(Player player, RoomType room, IRunState runState)
    {
        if (room is not (RoomType.Monster or RoomType.Elite)
            || player.UnlockState.NumberOfRuns != 0
            || player.UnlockState.EpochUnlockCount() != 0
            || player.Character is not Ironclad)
        {
            return false;
        }
        if (room == RoomType.Elite)
            return true;
        int monsterRooms = runState.MapPointHistory
            .SelectMany(entries => entries)
            .Count(entry => entry.Rooms.FindIndex(r => r.RoomType == RoomType.Monster) >= 0);
        return monsterRooms <= 7;
    }

    private static void ReplayGoldPopulate(Rng rewards, EncounterModel encounter, RoomType room, CombatRoom? combatRoom)
    {
        int min = encounter.MinGoldReward;
        int max = encounter.MaxGoldReward;
        if (room == RoomType.Monster)
        {
            float proportion = combatRoom?.GoldProportion ?? 1f;
            if (proportion <= 0f)
                return;
            min = (int)Math.Round(min * proportion);
            max = (int)Math.Round(max * proportion);
        }
        rewards.NextInt(min, max + 1);
    }
}
