using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Helpers;
using Sts2Sim.Core.Models.Blitz;
using Sts2Sim.Core.Models.Enchantments;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Ascension;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Potions;
using Sts2Sim.Core.Entities.Rngs;
using Sts2Sim.Core.Factories;
using Sts2Sim.Core.Hooks;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.Models.Relics;
using Sts2Sim.Core.Random;
using Sts2Sim.Core.Rooms;
using Sts2Sim.Core.Runs;

namespace CombatSolver.Beam.Policy;

// Method: PotionRewardOutlook.cs @ d231e9e5. The reward stream is cloned,
// then replayed in RewardsSet.Generate order without touching the live run.
public enum PotionRewardForecast { Unknown, NoRewards, NoDrop, Drop }

public readonly record struct PotionRewardOutlook(
    float DropChance, bool BeltFull, bool ProcureBlocked,
    PotionRewardForecast Forecast, string? ForecastPotionId,
    int ForecastPotionStrategicHpCost)
{
    private static readonly HashSet<string> HighValue = new(StringComparer.Ordinal)
    {
        "GLOWWATER_POTION", "SWIFT_POTION", "GAMBLERS_BREW", "DUPLICATOR",
        "OROBIC_ACID", "POT_OF_GHOULS",
    };
    private static readonly HashSet<string> ElevatedValue = new(StringComparer.Ordinal)
    {
        "DISTILLED_CHAOS", "CLARITY", "RADIANT_TINCTURE", "CURE_ALL",
        "LIQUID_MEMORIES", "BOTTLED_POTENTIAL", "TOUCH_OF_INSANITY",
    };

    public bool Enabled { get; init; }
    public int ReplacementHpCredit => BeltFull && !ProcureBlocked
        && Forecast == PotionRewardForecast.Drop ? ForecastPotionStrategicHpCost : 0;

    public static PotionRewardOutlook Capture(CombatState state)
    {
        if (state.RunState.CurrentRoom is not CombatRoom room
            || room.RoomType is not (RoomType.Monster or RoomType.Elite or RoomType.Boss))
            return default;
        Player player = state.Players[0];
        bool full = player.PotionSlots.Count > 0 && player.PotionSlots.All(potion => potion is not null);
        bool blocked = player.Relics.OfType<Sozu>().Any();
        if (state.RunState.GameMode == GameMode.Blitz)
            return CaptureBlitz(state, room, player, full, blocked);
        bool forced = Hook.ShouldForcePotionReward(state.RunState, room.RoomType);
        float chance = forced ? 1f : Math.Clamp(player.Odds.PotionReward.CurrentValue
            + (room.RoomType == RoomType.Elite ? 0.125f : 0f), 0f, 1f);
        var outlook = new PotionRewardOutlook(chance, full, blocked,
            PotionRewardForecast.Unknown, null, 0) { Enabled = true };
        if (state.RunState is not RunState run)
            return outlook;
        if (room.RoomType == RoomType.Boss && run.CurrentActIndex >= run.Acts.Count - 1)
            return outlook with { Forecast = PotionRewardForecast.NoRewards };

        // Core RewardsSet.Generate uses one shared stream in sequential mode and
        // independent semantic keys in keyed training mode; clone both explicitly.
        PlayerRngSet rngs = player.PlayerRng.CloneExact();
        string rewardKey = $"{player.CurrentSemanticLocationKey}/reward_kind={room.RoomType}";
        Rng odds = rngs.ForSemanticKey(PlayerRngType.Rewards,
            $"{rewardKey}/slot=potion_presence");
        bool drop = forced || odds.NextFloat() < chance;
        if (!drop) return outlook with { Forecast = PotionRewardForecast.NoDrop };

        Rng gold = rngs.ForSemanticKey(PlayerRngType.Rewards, $"{rewardKey}/slot=gold");
        ReplayGoldDraw(run, room, gold);
        Rng potionRng = rngs.ForSemanticKey(PlayerRngType.Rewards, $"{rewardKey}/slot=potion");
        PotionModel? potion = PotionFactory.CreateRandomOutOfCombat(player, potionRng);
        return potion is null ? outlook : outlook with
        {
            Forecast = PotionRewardForecast.Drop,
            ForecastPotionId = potion.Id.Entry,
            ForecastPotionStrategicHpCost = StrategicHpCost(potion),
        };
    }

    private static PotionRewardOutlook CaptureBlitz(
        CombatState state,
        CombatRoom room,
        Player player,
        bool full,
        bool blocked)
    {
        if (state.RunState is not RunState run)
        {
            float fallbackChance = room.RoomType == RoomType.Elite ||
                (room.RoomType == RoomType.Monster && (room.Encounter?.IsWeak ?? false))
                ? 0f : 1f;
            return new PotionRewardOutlook(fallbackChance, full, blocked,
                PotionRewardForecast.Unknown, null, 0) { Enabled = true };
        }

        if (room.RoomType == RoomType.Boss && run.CurrentActIndex >= run.Acts.Count - 1)
        {
            return new PotionRewardOutlook(0f, full, blocked,
                PotionRewardForecast.NoRewards, null, 0) { Enabled = true };
        }

        if (room.RoomType == RoomType.Elite ||
            (room.RoomType == RoomType.Monster && (room.Encounter?.IsWeak ?? false)))
        {
            return new PotionRewardOutlook(0f, full, blocked,
                PotionRewardForecast.NoDrop, null, 0) { Enabled = true };
        }

        var outlook = new PotionRewardOutlook(1f, full, blocked,
            PotionRewardForecast.Unknown, null, 0) { Enabled = true };

        PlayerRngSet rngs = player.PlayerRng.CloneExact();
        string rewardKey = $"{player.CurrentSemanticLocationKey}/reward_kind={room.RoomType}";
        Rng cardRng = rngs.ForSemanticKey(PlayerRngType.Rewards, $"{rewardKey}/slot=card");
        CardRarity rarity = room.RoomType == RoomType.Boss ? CardRarity.Rare : CardRarity.Common;
        ReplayBlitzCardRewardDraws(run, room, player, rarity, cardRng);

        Rng potionRng = rngs.ForSemanticKey(PlayerRngType.Rewards, $"{rewardKey}/slot=potion");
        PotionModel? potion = PotionFactory.CreateRandomOutOfCombat(player, potionRng);
        return potion is null ? outlook : outlook with
        {
            Forecast = PotionRewardForecast.Drop,
            ForecastPotionId = potion.Id.Entry,
            ForecastPotionStrategicHpCost = StrategicHpCost(potion),
        };
    }

    private static void ReplayBlitzCardRewardDraws(
        RunState run,
        CombatRoom room,
        Player player,
        CardRarity rarity,
        Rng cardRng)
    {
        CardCreationOptions creationOptions = BlitzRunModel.GetCardRewardOptions(player, rarity)
            .WithFlags(CardCreationFlags.IsCardReward);
        var selectedIds = new HashSet<ModelId>();
        var cards = new List<CardModel>(3);
        for (int i = 0; i < 3; i++)
        {
            CardCreationOptions selectionOptions = Hook.ModifyCardRewardCreationOptions(run, player, creationOptions);
            List<CardModel> remaining = selectionOptions.GetPossibleCards(player)
                .Where(card => !selectedIds.Contains(card.Id) && card.IsAllowedInRun(run))
                .Distinct()
                .ToList();
            IEnumerable<CardModel> candidates = remaining.Where(c =>
                c.Rarity is not (CardRarity.Basic or CardRarity.Ancient));
            CardModel picked = cardRng.NextItem(candidates)
                ?? throw new InvalidOperationException("No eligible distinct card remains for Blitz reward prediction.");
            selectedIds.Add(picked.Id);
            var clone = (CardModel)picked.MutableClone();
            clone.AssignOwner(player);
            cards.Add(clone);
        }

        // 只复现会影响附魔槽位或消耗奖励 RNG 的后期规则，不触发会修改遗物次数的 AfterModifyingCardRewardOptions。
        var nimble = (Nimble)ModelDb.Get(typeof(Nimble));
        var glam = (Glam)ModelDb.Get(typeof(Glam));
        var swift = (Swift)ModelDb.Get(typeof(Swift));
        bool hasFresnel = player.Relics.Any(relic => relic is FresnelLens && !relic.IsMelted);
        bool hasGlitter = player.Relics.Any(relic => relic is Glitter && !relic.IsMelted);
        bool hasUnusedSilkenTress = player.Relics.Any(relic => relic is SilkenTress s && !s.IsMelted && !s.IsUsedUp);
        bool[] enchanted = new bool[cards.Count];
        for (int i = 0; i < cards.Count; i++)
        {
            CardModel card = cards[i];
            if (hasFresnel && nimble.CanEnchant(card))
            {
                enchanted[i] = true;
                continue;
            }
            if ((hasGlitter || hasUnusedSilkenTress) && glam.CanEnchant(card))
            {
                enchanted[i] = true;
            }
        }

        foreach (RelicModel relic in player.Relics)
        {
            if (relic is not WingCharm || relic.IsMelted)
                continue;

            List<int> eligible = [];
            for (int i = 0; i < cards.Count; i++)
            {
                if (!enchanted[i] && swift.CanEnchant(cards[i]))
                    eligible.Add(i);
            }

            if (eligible.Count > 0)
            {
                int chosenIndex = cardRng.NextItem(eligible);
                enchanted[chosenIndex] = true;
            }
        }

        if (room.RoomType != RoomType.Boss && cards.Count > 0)
        {
            if (run.CurrentActIndex == 1)
                _ = cardRng.NextItem(cards);
            else if (run.CurrentActIndex == 2)
                cards.UnstableShuffle(cardRng);
        }
    }

    private static void ReplayGoldDraw(RunState run, CombatRoom room, Rng rng)
    {
        if (room.GoldProportion <= 0f) return;
        (double low, double high) = room.RoomType switch
        {
            RoomType.Monster => (10, 20),
            RoomType.Elite => (35, 45),
            _ => (100, 100),
        };
        if (run.Ascension.HasLevel(AscensionLevel.Poverty))
        {
            low *= 0.75;
            high *= 0.75;
        }
        int min = (int)Math.Round((room.Encounter?.MinGoldReward ?? (int)low) * room.GoldProportion);
        int max = (int)Math.Round((room.Encounter?.MaxGoldReward ?? (int)high) * room.GoldProportion);
        rng.NextInt(min, max + 1);
    }

    public static int StrategicHpCost(PotionModel potion)
    {
        if (potion.Rarity == PotionRarity.Token) return 0;
        string id = potion.Id.Entry;
        return HighValue.Contains(id) ? 18 : ElevatedValue.Contains(id) ? 14 : 9;
    }
}
