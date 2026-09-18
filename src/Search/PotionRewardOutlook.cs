using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Rooms;

namespace CombatSolver;

/// <summary>
/// What the player can expect from the potion reward roll once this fight is won.
/// </summary>
/// <remarks>
/// Vanilla rolls one potion reward per combat room through <see cref="PotionRewardOdds.Roll"/>: the odds start
/// at 40%, rise 10% after every combat that rolled nothing and fall 10% after every drop, elites add 12.5%, and
/// White Beast Statue forces the drop. The odds are saved with the run, so the chance of the next drop is known
/// exactly at root capture and does not depend on anything the search does inside the fight.
///
/// The outlook matters only when the belt is full: a potion spent in this fight frees the slot the reward would
/// otherwise have nowhere to go, so the reward's expected value comes back as a credit against the strategic
/// cost of spending. With an open slot the reward lands either way and the credit is zero. Sozu blocks
/// procurement entirely, so it also zeroes the credit.
/// </remarks>
internal readonly record struct PotionRewardOutlook(
    float DropChance,
    bool BeltFull,
    bool ProcureBlocked)
{
    public const float EliteDropBonus = 0.125f;

    public static PotionRewardOutlook None => default;

    /// <summary>
    /// HP the expected reward is worth to a route that spends a paid potion, in the strategic-cost scale.
    /// </summary>
    /// <remarks>
    /// The reward is a random potion, so it is valued at the baseline tier regardless of which potion the route
    /// spends. The credit is a route-level amount: one freed slot receives at most one reward, so it is applied
    /// once per route rather than once per potion.
    /// </remarks>
    public int ReplacementHpCredit
        => BeltFull && !ProcureBlocked
            ? (int)Math.Round(Math.Clamp(DropChance, 0f, 1f) * SolverWeights.PotionMinimumHpSaved)
            : 0;

    /// <summary>
    /// Reads the outlook from the live run on the main thread as part of root capture.
    /// </summary>
    public static PotionRewardOutlook Capture(
        Player player,
        RoomType? roomType,
        IEnumerable<RelicModel> relics)
    {
        if (roomType is not { } room || !room.IsCombatRoom())
            return None;
        bool beltFull = player.PotionSlots.Count > 0 && player.PotionSlots.All(potion => potion != null);
        bool procureBlocked = relics.OfType<Sozu>().Any();
        float chance = Hook.ShouldForcePotionReward(player.RunState, player, room)
            ? 1f
            : DropChanceFor(player.PlayerOdds.PotionReward.CurrentValue, room);
        return new PotionRewardOutlook(chance, beltFull, procureBlocked);
    }

    /// <summary>
    /// Mirrors the roll threshold in <see cref="PotionRewardOdds.Roll"/>: the saved odds plus half the elite
    /// bonus, clamped to a probability.
    /// </summary>
    public static float DropChanceFor(float currentOdds, RoomType roomType)
        => Math.Clamp(currentOdds + (roomType == RoomType.Elite ? EliteDropBonus : 0f), 0f, 1f);
}
