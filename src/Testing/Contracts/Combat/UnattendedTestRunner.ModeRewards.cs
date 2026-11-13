using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertBlitzRewardOutlookAsync(CombatState combat, Player player)
    {
        if (combat.RunState.GameMode != GameMode.Blitz) throw new InvalidOperationException("Blitz reward contract requires Blitz.");
        var room = (CombatRoom)combat.RunState.CurrentRoom!;
        while (player.PotionSlots.Any(potion => potion == null))
            InjectPotionForTest(player, "BLOCK_POTION");
        for (int sample = 0; sample < 8; sample++)
        {
            SetStage("blitz_reward_" + sample);
            var rngBefore = player.PlayerRng.ToSerializable();
            string before = System.Text.Json.JsonSerializer.Serialize(rngBefore);
            var forecast = PotionRewardOutlook.Capture(player, combat, player.Relics);
            string after = System.Text.Json.JsonSerializer.Serialize(player.PlayerRng.ToSerializable());
            if (before != after) throw new InvalidOperationException("Reward prediction changed live RNG.");
            var rewards = new RewardsSet(player).WithRewardsFromRoom(room);
            await rewards.GenerateWithoutOffering();
            var potions = rewards.Rewards.OfType<PotionReward>().ToArray();
            if (potions.Length > 1 || (forecast.Forecast == PotionRewardForecast.Drop) != (potions.Length == 1)
                || potions.Length == 1 && forecast.ForecastPotionId != potions[0].Potion!.Id.Entry)
                throw new InvalidOperationException($"Blitz reward mismatch: forecast={forecast.Forecast}/{forecast.ForecastPotionId}, native={string.Join(',', potions.Select(p => p.Potion!.Id.Entry))}");
            int expectedCredit = potions.Length == 1 && !forecast.ProcureBlocked
                ? PotionUsePolicy.StrategicHpCost(potions[0].Potion!) : 0;
            if (!forecast.BeltFull || forecast.ReplacementHpCredit != expectedCredit)
                throw new InvalidOperationException($"Blitz full-belt credit mismatch: forecast={forecast.ReplacementHpCredit}, native={expectedCredit}.");
        }
        _completedChecks.Add($"BlitzRewards:act={combat.RunState.CurrentActIndex}:room={room.RoomType}:weak={room.Encounter.IsWeak}:8NativeSamples:LiveRngUnchanged:FullBeltReplacementCredit");
    }

    private async Task AssertModeRulesIsolationAsync(CombatState combat)
    {
        CombatRulesSnapshot live = CombatRulesSnapshot.Capture(combat);
        foreach (int level in Enumerable.Range(0, 6))
        {
            var manager = new AscensionManager(GameMode.Blitz, level);
            ulong bits = 0;
            foreach (AscensionLevel item in Enum.GetValues<AscensionLevel>())
                if (manager.HasLevel(item)) bits |= 1UL << (int)item;
            var blitz = new CombatRulesSnapshot(GameMode.Blitz, level, bits, null);
            await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            {
                var rules = index == 0 ? blitz : live;
                using NativeCombatRulesScope outer = new(rules);
                if (AscensionHelper.IsDread() != rules.IsDread) throw new InvalidOperationException("Native Dread query escaped the root.");
                foreach (AscensionLevel item in Enum.GetValues<AscensionLevel>())
                    if (AscensionHelper.HasAscension(item) != ((rules.Ascensions & (1UL << (int)item)) != 0))
                        throw new InvalidOperationException("Native ascension query escaped the root.");
                using (NativeCombatRulesScope inner = new(blitz))
                    if (AscensionHelper.IsDread()) throw new InvalidOperationException("Nested rules scope mismatch.");
                if (AscensionHelper.IsDread() != rules.IsDread) throw new InvalidOperationException("Rules scope did not restore its parent.");
            })));
        }
        if (NativeCombatRulesScope.Current != null || AscensionHelper.IsDread() != live.IsDread)
            throw new InvalidOperationException("Native rules scope leaked into live gameplay.");
        _completedChecks.Add("ModeRules:Blitz0Through5:ConcurrentRoots:NestedScopes:LiveUnchanged");
    }
}
