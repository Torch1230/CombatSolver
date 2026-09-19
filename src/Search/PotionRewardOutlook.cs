using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;

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
/// <remarks>
/// Vanilla rolls one potion reward per combat room through <see cref="PotionRewardOdds.Roll"/> on the player's
/// reward RNG, a seeded counter stream that is saved with the run and untouched during combat. The odds start at
/// 40%, rise 10% after every combat that rolled nothing and fall 10% after every drop, elites add 12.5%, and
/// White Beast Statue forces the drop. Reward generation then populates the rewards in a fixed order on the same
/// stream: the gold amount, the potion (a rarity roll, then a pick from the unlocked pools), then the cards.
/// Cloning the stream at root capture and replaying those draws therefore tells exactly whether a potion drops
/// and which one, the same way the search replays the shuffle stream to predict draws. When the mirror cannot
/// be trusted (the tutorial reward set) the outlook keeps only the saved odds.
///
/// The outlook matters only when the belt is full: a potion spent in this fight frees the slot the reward would
/// otherwise have nowhere to go, so the reward's value comes back as a credit against the strategic cost of
/// spending. With an open slot the reward lands either way and the credit is zero. Sozu blocks procurement
/// entirely, so it also zeroes the credit.
/// </remarks>
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
    /// <remarks>
    /// A mirrored drop is valued at the tier of the potion it will give; a mirrored miss is worth nothing. When
    /// only the odds are known, a random potion is valued at the baseline tier times the drop chance. The credit
    /// is a route-level amount: one freed slot receives at most one reward, so it is applied once per route
    /// rather than once per potion.
    /// </remarks>
    public int ReplacementHpCredit
    {
        get
        {
            if (!BeltFull || ProcureBlocked)
                return 0;
            return Forecast switch
            {
                PotionRewardForecast.Drop => ForecastPotionStrategicHpCost,
                PotionRewardForecast.NoDrop or PotionRewardForecast.NoRewards => 0,
                _ => (int)Math.Round(Math.Clamp(DropChance, 0f, 1f) * SolverWeights.PotionMinimumHpSaved),
            };
        }
    }

    /// <summary>
    /// Reads the outlook from the live run on the main thread as part of root capture.
    /// </summary>
    public static PotionRewardOutlook Capture(
        Player player,
        CombatState state,
        IEnumerable<RelicModel> relics)
    {
        if (state.Encounter?.RoomType is not { } room || !room.IsCombatRoom())
            return None;
        bool beltFull = player.PotionSlots.Count > 0 && player.PotionSlots.All(potion => potion != null);
        bool procureBlocked = relics.OfType<Sozu>().Any();
        bool forced = Hook.ShouldForcePotionReward(player.RunState, player, room);
        float chance = forced ? 1f : DropChanceFor(player.PlayerOdds.PotionReward.CurrentValue, room);
        PotionRewardOutlook odds = new(chance, beltFull, procureBlocked) { Enabled = true };

        IRunState runState = player.RunState;
        if (room == RoomType.Boss && runState.CurrentActIndex >= runState.Acts.Count - 1)
            return odds with { Forecast = PotionRewardForecast.NoRewards };
        if (IsTutorialRewardSet(player, room, runState))
            return odds;

        // RewardsSet.GenerateRewardsFor rolls the potion on the reward stream while building the list, then
        // GenerateWithoutOffering populates gold and potion in that order before it reaches the cards.
        Rng rewards = player.PlayerRng.Rewards.Clone();
        bool drop = forced || rewards.NextFloat() < chance;
        if (!drop)
            return odds with { Forecast = PotionRewardForecast.NoDrop };
        ReplayGoldPopulate(rewards, state.Encounter, room, runState.CurrentRoom as CombatRoom);
        PotionModel potion = PotionFactory.CreateRandomPotionOutOfCombat(player, rewards);
        return odds with
        {
            Forecast = PotionRewardForecast.Drop,
            ForecastPotionId = potion.Id.Entry,
            ForecastPotionStrategicHpCost = PotionUsePolicy.StrategicHpCost(potion),
        };
    }

    /// <summary>
    /// Mirrors the roll threshold in <see cref="PotionRewardOdds.Roll"/>: the saved odds plus half the elite
    /// bonus, clamped to a probability.
    /// </summary>
    public static float DropChanceFor(float currentOdds, RoomType roomType)
        => Math.Clamp(currentOdds + (roomType == RoomType.Elite ? EliteDropBonus : 0f), 0f, 1f);

    /// <summary>
    /// The very first Ironclad run replaces the early monster and elite rewards with scripted tutorial rewards
    /// (<c>RewardsSet.TryGenerateTutorialRewards</c>), which pick their potion without the reward stream; the
    /// mirror stays silent there rather than guess which fights the script still covers.
    /// </summary>
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

    /// <summary>
    /// <c>GoldReward.Populate</c> draws once for the amount. Monster rooms skip the gold reward entirely when the
    /// room's gold proportion is zero; elites and bosses always include it.
    /// </summary>
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
