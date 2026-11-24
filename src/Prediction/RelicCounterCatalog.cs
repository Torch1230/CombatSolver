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

    internal static int ReadLiveCounter(RelicModel relic, int period) => relic switch
    {
        // Read underlying saved counter properties directly instead of DisplayAmount,
        // because DisplayAmount transiently returns Period during the 1s flash animation.
        HappyFlower flower => RelicCounterPolicy.NormalizeCounter(flower.TurnsSeen, period),
        FakeHappyFlower flower => RelicCounterPolicy.NormalizeCounter(flower.TurnsSeen, period),
        Pendulum pendulum => RelicCounterPolicy.NormalizeCounter(pendulum.TurnsSeen, period),
        PollinousCore core => RelicCounterPolicy.NormalizeCounter(core.TurnsSeen, period),
        PenNib penNib => RelicCounterPolicy.NormalizeCounter(penNib.AttacksPlayed, period),
        Nunchaku nunchaku => RelicCounterPolicy.NormalizeCounter(nunchaku.AttacksPlayed, period),
        TuningFork fork => RelicCounterPolicy.NormalizeCounter(fork.SkillsPlayed, period),
        JossPaper paper => RelicCounterPolicy.NormalizeCounter(paper.CardsExhausted, period),
        IronClub club => RelicCounterPolicy.NormalizeCounter(club.CardsPlayed, period),
        GalacticDust dust => RelicCounterPolicy.NormalizeCounter(dust.StarsSpent, period),
        MeatOnTheBone => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(relic), relic.GetType().Name, "Unsupported relic counter model."),
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

        Dictionary<RelicCounterId, int> ownedCounters = [];
        foreach (RelicModel relic in combat.Players.SelectMany(player => player.Relics))
        {
            if (relic.IsMelted)
                continue;
            if (Identify(relic) is not { } id || ownedCounters.ContainsKey(id))
                continue;
            int period = PeriodOf(id);
            ownedCounters[id] = ReadLiveCounter(relic, period);
        }

        if (ownedCounters.Count == 0)
            return Array.Empty<RelicCounterTarget>();

        List<RelicCounterCandidate> candidates = [];
        foreach (RelicCounterRule rule in rules)
        {
            if (!rule.Enabled || !ownedCounters.TryGetValue(rule.Id, out int currentValue))
                continue;
            int period = PeriodOf(rule.Id);
            if (rule.Maximum >= period)
                throw new ArgumentException("Relic range exceeds its counter period.");
            if (rule.Minimum < 0 || rule.Maximum < rule.Minimum)
                throw new ArgumentException("Invalid relic counter policy.", nameof(rules));
            if (rule.Minimum <= 0 && rule.Maximum >= period - 1)
                continue;
            RelicCounterTarget target = new(rule.Id, rule.Minimum, rule.Maximum, rule.HpAllowance, period, rule.Priority);
            candidates.Add(new RelicCounterCandidate(target, currentValue));
        }

        RelicCounterTarget[] resolved = RelicCounterPolicy.ResolveConflicts(candidates);
        return resolved.Length == 0 ? Array.Empty<RelicCounterTarget>() : Array.AsReadOnly(resolved);
    }

    private static int PeriodOf(RelicCounterId id) => All.Single(entry => entry.Id == id).Period;
}
