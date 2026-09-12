using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal static class RelicCounterCatalog
{
    internal sealed record Entry(RelicCounterId Id, Func<RelicModel> Canonical, int Period);
    public static readonly IReadOnlyList<Entry> All = Array.AsReadOnly(new Entry[]
    {
        new(RelicCounterId.HappyFlower, () => ModelDb.Relic<HappyFlower>(), 3),
        new(RelicCounterId.FakeHappyFlower, () => ModelDb.Relic<FakeHappyFlower>(), 5),
        new(RelicCounterId.Pendulum, () => ModelDb.Relic<Pendulum>(), 3),
        new(RelicCounterId.PollinousCore, () => ModelDb.Relic<PollinousCore>(), 4),
        new(RelicCounterId.PenNib, () => ModelDb.Relic<PenNib>(), 10),
        new(RelicCounterId.Nunchaku, () => ModelDb.Relic<Nunchaku>(), 10),
        new(RelicCounterId.TuningFork, () => ModelDb.Relic<TuningFork>(), 10),
        new(RelicCounterId.JossPaper, () => ModelDb.Relic<JossPaper>(), 5),
        new(RelicCounterId.IronClub, () => ModelDb.Relic<IronClub>(), 4),
        new(RelicCounterId.GalacticDust, () => ModelDb.Relic<GalacticDust>(), 10),
        new(RelicCounterId.MeatOnTheBone, () => ModelDb.Relic<MeatOnTheBone>(), 2),
    });

    public static RelicCounterId? Identify(RelicModel relic) => relic switch
    {
        HappyFlower => RelicCounterId.HappyFlower, FakeHappyFlower => RelicCounterId.FakeHappyFlower,
        Pendulum => RelicCounterId.Pendulum, PollinousCore => RelicCounterId.PollinousCore,
        PenNib => RelicCounterId.PenNib, Nunchaku => RelicCounterId.Nunchaku,
        TuningFork => RelicCounterId.TuningFork, JossPaper => RelicCounterId.JossPaper,
        IronClub => RelicCounterId.IronClub, GalacticDust => RelicCounterId.GalacticDust,
        MeatOnTheBone => RelicCounterId.MeatOnTheBone,
        _ => null,
    };

    public static string UnavailableReason(RelicModel relic) => relic switch
    {
        VelvetChoker or PaelsLegion or Shuriken or Pocketwatch or PaelsFlesh or StoneCalendar
            or Metronome or OrnamentalFan or LetterOpener or Kusarigama or Kunai or BrilliantScarf
            => "战斗或回合结束后重置，不会带入下一场。",
        ToyBox or PaelsTooth or WongosMysteryTicket or PumpkinCandle or BoneTea or EmberTea or FishingRod or SwordOfStone
            => "随战斗或精英场数固定变化，出牌无法调整。",
        WingedBoots or SilverCrucible or Girya or LastingCandy or PaelsWing or BookOfFiveRings
            => "由地图、奖励或牌库操作改变，当前不参与战斗末卡数。",
        _ => "尚未适配计数语义。",
    };

    public static IReadOnlyList<RelicCounterTarget> Capture(ICombatState combat, bool enabled, IEnumerable<RelicCounterRule> rules)
    {
        if (!enabled) return Array.Empty<RelicCounterTarget>();
        var owned = combat.Players.SelectMany(player => player.Relics).Where(relic => !relic.IsMelted)
            .Select(Identify).OfType<RelicCounterId>().ToHashSet();
        return Array.AsReadOnly(rules.Where(rule => rule.Enabled && owned.Contains(rule.Id))
            .Select(rule =>
            {
                int period = All.Single(entry => entry.Id == rule.Id).Period;
                if (rule.Maximum >= period) throw new ArgumentException("Relic range exceeds its counter period.");
                return new RelicCounterTarget(rule.Id, rule.Minimum, rule.Maximum, rule.HpAllowance, period, rule.Priority);
            })
            .Where(target => target.Minimum > 0 || target.Maximum < target.Period - 1)
            .OrderBy(target => target.Id).ToArray());
    }
}
