using System.Buffers;
using CombatSolver;
using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Players;
using Sts2Sim.Core.Entities.Powers;
using Sts2Sim.Core.Models;
using Sts2Sim.Core.MonsterMoves.Intents;

namespace CombatSolver.Beam;

/// <summary>Score ordering, setup and progress portfolios, and exact transpositions. Method: CombatBeamSolver.BeamRetentionPolicy.cs and CrossTurnPlanning.cs @ d231e9e5.</summary>
internal static class Retention
{
    public static List<SearchNode> Select(IEnumerable<SearchNode> candidates, int width,
        bool preservePotionFree = false) =>
        SelectWithKey(
            candidates,
            width,
            static node => node.Fingerprint,
            preservePotionFree,
            includeParetoProgress: false);

    public static List<SearchNode> SelectWithKey(
        IEnumerable<SearchNode> candidates,
        int width,
        Func<SearchNode, CombatStateDescriptionDigest> keySelector,
        bool preservePotionFree = false,
        bool includeParetoProgress = false,
        bool aggressivePowerCommitment = false,
        bool secondRankBand = false,
        bool enablePowerCommitmentSeats = true)
    {
        var scratch = RetentionScratch.Acquire();
        try
        {
            if (candidates is IReadOnlyList<SearchNode> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    SearchNode node = list[i];
                    scratch.Entries.Add(new DecoratedEntry(node, keySelector(node), i));
                }
            }
            else
            {
                int i = 0;
                foreach (SearchNode node in candidates)
                {
                    scratch.Entries.Add(new DecoratedEntry(node, keySelector(node), i++));
                }
            }

            List<DecoratedEntry> entries = scratch.Entries;
            if (entries.Count == 0 || width <= 0)
                return [];

            var retained = new List<SearchNode>(Math.Min(entries.Count, width));
            entries.Sort(PrimaryComparer.Instance);

            HashSet<CombatStateDescriptionDigest> seenExact = scratch.SeenExact;
            HashSet<CombatStateDescriptionDigest> representedBuckets = scratch.RepresentedBuckets;

            bool TryAdmitUniqueBucket(SearchNode node, CombatStateDescriptionDigest bucketKey)
            {
                if (seenExact.Contains(node.Fingerprint) || !representedBuckets.Add(bucketKey))
                    return false;
                seenExact.Add(node.Fingerprint);
                retained.Add(node);
                return true;
            }

            int ordinarySlots = aggressivePowerCommitment
                ? (int)Math.Max(1L, (long)width * 5L / 8L)
                : (int)Math.Max(1L, (long)width * 3L / 4L);
            if (secondRankBand && entries.Count > width)
            {
                List<DecoratedEntry> ordinaryEntries = scratch.RotatedOrdinaryEntries;
                HashSet<CombatStateDescriptionDigest> leaderSeenExact = scratch.RotatedSeenExact;
                HashSet<CombatStateDescriptionDigest> leaderBuckets = scratch.RotatedBuckets;
                for (int i = 0; i < entries.Count; i++)
                {
                    DecoratedEntry candidate = entries[i];
                    if (!leaderSeenExact.Contains(candidate.Node.Fingerprint) && leaderBuckets.Add(candidate.Key))
                    {
                        leaderSeenExact.Add(candidate.Node.Fingerprint);
                        ordinaryEntries.Add(candidate);
                    }
                }
                BeamWidthPortfolio.MoveLeadingBandToTail(ordinaryEntries, width);
                for (int i = 0; i < ordinaryEntries.Count; i++)
                {
                    DecoratedEntry entry = ordinaryEntries[i];
                    TryAdmitUniqueBucket(entry.Node, entry.Key);
                    if (retained.Count >= ordinarySlots) break;
                }
            }
            else
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    DecoratedEntry entry = entries[i];
                    TryAdmitUniqueBucket(entry.Node, entry.Key);
                    if (retained.Count >= ordinarySlots) break;
                }
            }

            // Keep a small portfolio of paths that changed persistent combat setup.
            // They can be temporarily behind immediate damage and block while their
            // payoff appears in later turns. No card identity or encounter rule is used.
            Dictionary<SetupSignatureKey, int> setupCounts = scratch.SetupCounts;
            List<SetupToken> arena = scratch.SetupArena;
            for (int i = 0; i < retained.Count; i++)
            {
                SetupSignatureKey sig = BuildSetupSignature(retained[i], arena);
                setupCounts[sig] = setupCounts.GetValueOrDefault(sig) + 1;
            }

            int maxPerSetupSignature = aggressivePowerCommitment ? 2 : 1;
            int setupEnd = (int)Math.Min(
                (long)width,
                (long)ordinarySlots + (aggressivePowerCommitment ? Math.Max(2L, width / 8L) : Math.Max(1L, width / 6L)));
            if (aggressivePowerCommitment)
            {
                for (int pass = 0; pass < 2 && retained.Count < setupEnd; pass++)
                {
                    bool wantBuff = pass == 0;
                    for (int i = 0; i < entries.Count; i++)
                    {
                        if (retained.Count >= setupEnd) break;
                        DecoratedEntry entry = entries[i];
                        if (HasPositivePlayerBuff(entry.Node) != wantBuff) continue;
                        if (seenExact.Contains(entry.Node.Fingerprint) || representedBuckets.Contains(entry.Key)) continue;
                        SetupSignatureKey sig = BuildSetupSignature(entry.Node, arena);
                        int count = setupCounts.GetValueOrDefault(sig);
                        if (count < maxPerSetupSignature && TryAdmitUniqueBucket(entry.Node, entry.Key))
                            setupCounts[sig] = count + 1;
                    }
                }
            }
            else
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (retained.Count >= setupEnd) break;
                    DecoratedEntry entry = entries[i];
                    if (seenExact.Contains(entry.Node.Fingerprint) || representedBuckets.Contains(entry.Key)) continue;
                    SetupSignatureKey sig = BuildSetupSignature(entry.Node, arena);
                    int count = setupCounts.GetValueOrDefault(sig);
                    if (count < maxPerSetupSignature && TryAdmitUniqueBucket(entry.Node, entry.Key))
                        setupCounts[sig] = count + 1;
                }
            }
            int protectedEnd = retained.Count;

            if (retained.Count < width)
            {
                List<ProgressEntry> progress = scratch.ProgressEntries;
                for (int i = 0; i < entries.Count; i++)
                {
                    DecoratedEntry entry = entries[i];
                    SearchNode node = entry.Node;
                    (double enemyRemaining, int hpMargin) = includeParetoProgress
                        ? ComputeProgressMetrics(node)
                        : (EnemyRemaining(node), 0);
                    progress.Add(new ProgressEntry(node, entry.Key, enemyRemaining, hpMargin, i));
                }

                if (includeParetoProgress)
                {
                    progress.Sort(ParetoComparer.Instance);
                    // 在进攻推进席位中优先保留不被支配的 (EnemyRemaining, EffectiveHpMargin) 前沿点，
                    // 避免同一低血量高伤害分支的等价次优变体挤占温和换血的高价值进攻路线。
                    int bestHpMargin = int.MinValue;
                    for (int i = 0; i < progress.Count; i++)
                    {
                        if (retained.Count >= width) break;
                        ProgressEntry entry = progress[i];
                        if (!entry.Node.State.Players[0].Creature.IsAlive) continue;
                        if (entry.HpMargin <= bestHpMargin) continue;
                        bestHpMargin = entry.HpMargin;
                        TryAdmitUniqueBucket(entry.Node, entry.Key);
                    }
                }

                // Reserve the remaining slots for routes with enemy HP progress that
                // short-term protection scores can hide. The same Core simulation still
                // decides whether the aggressive line survives subsequent turns.
                if (retained.Count < width)
                {
                    progress.Sort(EnemyProgressComparer.Instance);
                    for (int i = 0; i < progress.Count; i++)
                    {
                        if (retained.Count >= width) break;
                        ProgressEntry entry = progress[i];
                        TryAdmitUniqueBucket(entry.Node, entry.Key);
                    }
                }
            }

            for (int i = 0; i < entries.Count; i++)
            {
                if (retained.Count >= width) break;
                DecoratedEntry entry = entries[i];
                TryAdmitUniqueBucket(entry.Node, entry.Key);
            }

            // 当桶多样性选择后 Beam 仍有空余名额时，仅按精确 Fingerprint 去重回填，
            // 避免不同弃牌堆/伤害历史顺序的精确状态在容量未满时被摘要桶误吞。
            if (retained.Count < width)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (retained.Count >= width) break;
                    SearchNode node = entries[i].Node;
                    if (seenExact.Add(node.Fingerprint))
                        retained.Add(node);
                }
            }

            if (enablePowerCommitmentSeats && retained.Count > protectedEnd)
            {
                List<SearchNode> commitmentPool = scratch.PowerCommitmentPool;
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Node.PowerCommitment is not null)
                        commitmentPool.Add(entries[i].Node);
                }
                if (commitmentPool.Count > 0)
                {
                    int quota = PowerCommitmentSeatPolicy.SeatQuota(width, aggressive: aggressivePowerCommitment);
                    IReadOnlyList<SearchNode> powerReps =
                        PowerCommitmentRetention.RankBeamRepresentatives(commitmentPool, quota);
                    var repSet = new HashSet<SearchNode>(powerReps, ReferenceEqualityComparer.Instance);
                    for (int r = 0; r < powerReps.Count; r++)
                    {
                        SearchNode rep = powerReps[r];
                        if (seenExact.Contains(rep.Fingerprint))
                            continue;
                        CombatStateDescriptionDigest repKey = keySelector(rep);
                        if (representedBuckets.Contains(repKey))
                            continue;
                        int replaceIndex = -1;
                        for (int k = retained.Count - 1; k >= protectedEnd; k--)
                        {
                            if (!repSet.Contains(retained[k]))
                            {
                                replaceIndex = k;
                                break;
                            }
                        }
                        if (replaceIndex < 0)
                            break;
                        retained[replaceIndex] = rep;
                        seenExact.Add(rep.Fingerprint);
                        representedBuckets.Add(repKey);
                    }
                }
            }

            if (preservePotionFree && width > 1)
            {
                bool allUsePotion = true;
                for (int i = 0; i < retained.Count; i++)
                {
                    if (retained[i].PotionUses == 0)
                    {
                        allUsePotion = false;
                        break;
                    }
                }
                if (allUsePotion)
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        if (entries[i].Node.PotionUses == 0)
                        {
                            retained.RemoveAt(retained.Count - 1);
                            retained.Add(entries[i].Node);
                            break;
                        }
                    }
                }
            }
            return retained;
        }
        finally
        {
            RetentionScratch.Release(scratch);
        }
    }

    private static (double EnemyRemaining, int HpMargin) ComputeProgressMetrics(SearchNode node)
    {
        CombatState state = node.State;
        Creature creature = state.Players[0].Creature;
        IReadOnlyList<Creature> enemies = state.Enemies;
        double enemyRemaining = 0d;
        if (!creature.IsAlive)
        {
            for (int e = 0; e < enemies.Count; e++)
            {
                Creature enemy = enemies[e];
                if (enemy.IsAlive)
                    enemyRemaining += enemy.CurrentHp + enemy.Block * 0.5;
            }
            return (enemyRemaining, int.MinValue);
        }

        int threat = 0;
        Creature[] singleTarget = RentSingleTarget(creature);
        try
        {
            for (int e = 0; e < enemies.Count; e++)
            {
                Creature enemy = enemies[e];
                if (!enemy.IsAlive) continue;
                enemyRemaining += enemy.CurrentHp + enemy.Block * 0.5;
                if (enemy.Monster?.NextMove?.Intents is { } intents)
                {
                    for (int ii = 0; ii < intents.Count; ii++)
                    {
                        if (intents[ii] is AttackIntent attack)
                            threat += Math.Max(0, attack.GetTotalDamage(singleTarget, enemy));
                    }
                }
            }
        }
        finally
        {
            singleTarget[0] = null!;
        }

        int exposed = Math.Max(0, threat - creature.Block);
        return (enemyRemaining, creature.CurrentHp - exposed);
    }

    private static double EnemyRemaining(SearchNode node)
    {
        double total = 0d;
        IReadOnlyList<Creature> enemies = node.State.Enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            Creature enemy = enemies[i];
            if (enemy.IsAlive)
                total += enemy.CurrentHp + enemy.Block * 0.5;
        }
        return total;
    }

    private static bool HasPositivePlayerBuff(SearchNode node)
    {
        IReadOnlyList<PowerModel> powers = node.State.Players[0].Creature.Powers;
        for (int i = 0; i < powers.Count; i++)
        {
            PowerModel power = powers[i];
            if (power.Type == PowerType.Buff && power.Amount > 0)
                return true;
        }
        return false;
    }

    [ThreadStatic]
    private static Creature[]? _singleTargetScratch;

    private static Creature[] RentSingleTarget(Creature creature)
    {
        Creature[]? arr = _singleTargetScratch;
        if (arr is null)
        {
            arr = new Creature[1];
            _singleTargetScratch = arr;
        }
        arr[0] = creature;
        return arr;
    }

    private static SetupSignatureKey BuildSetupSignature(SearchNode node, List<SetupToken> arena)
    {
        int start = arena.Count;
        CombatState state = node.State;
        Player player = state.Players[0];

        IReadOnlyList<PowerModel> playerPowers = player.Creature.Powers;
        int buffStart = arena.Count;
        for (int i = 0; i < playerPowers.Count; i++)
        {
            PowerModel power = playerPowers[i];
            if (power.Type == PowerType.Buff)
                arena.Add(new SetupToken(-1, power.Id.Entry, power.Amount));
        }
        SortArenaSlice(arena, buffStart, arena.Count);

        IReadOnlyList<Creature> enemies = state.Enemies;
        arena.Add(new SetupToken(int.MinValue, null, enemies.Count));
        for (int e = 0; e < enemies.Count; e++)
        {
            IReadOnlyList<PowerModel> enemyPowers = enemies[e].Powers;
            int debuffStart = arena.Count;
            for (int p = 0; p < enemyPowers.Count; p++)
            {
                PowerModel power = enemyPowers[p];
                if (power.Type == PowerType.Debuff)
                    arena.Add(new SetupToken(e, power.Id.Entry, power.Amount));
            }
            SortArenaSlice(arena, debuffStart, arena.Count);
        }

        IReadOnlyList<PotionModel?> slots = player.PotionSlots;
        for (int s = 0; s < slots.Count; s++)
        {
            arena.Add(new SetupToken(-2 - s, slots[s]?.Id.Entry, 0m));
        }

        int length = arena.Count - start;
        var hash = new HashCode();
        hash.Add(length);
        for (int i = start; i < arena.Count; i++)
        {
            SetupToken tok = arena[i];
            hash.Add(tok.SectionTag);
            if (tok.EntryId is not null)
                hash.Add(tok.EntryId, StringComparer.Ordinal);
            else
                hash.Add(-1);
            hash.Add(tok.Amount);
        }
        return new SetupSignatureKey(arena, start, length, hash.ToHashCode());
    }

    private static void SortArenaSlice(List<SetupToken> arena, int start, int end)
    {
        for (int i = start + 1; i < end; i++)
        {
            SetupToken key = arena[i];
            int j = i - 1;
            while (j >= start && string.CompareOrdinal(arena[j].EntryId, key.EntryId) > 0)
            {
                arena[j + 1] = arena[j];
                j--;
            }
            arena[j + 1] = key;
        }
    }

    private readonly record struct SetupToken(int SectionTag, string? EntryId, decimal Amount);

    private readonly struct SetupSignatureKey(List<SetupToken> arena, int start, int length, int hash)
        : IEquatable<SetupSignatureKey>
    {
        private readonly List<SetupToken> _arena = arena;
        private readonly int _start = start;
        private readonly int _length = length;
        private readonly int _hash = hash;

        public bool Equals(SetupSignatureKey other)
        {
            if (_length != other._length || _hash != other._hash)
                return false;
            for (int i = 0; i < _length; i++)
            {
                SetupToken a = _arena[_start + i];
                SetupToken b = other._arena[other._start + i];
                if (a.SectionTag != b.SectionTag
                    || a.Amount != b.Amount
                    || !string.Equals(a.EntryId, b.EntryId, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is SetupSignatureKey other && Equals(other);
        public override int GetHashCode() => _hash;
    }

    private readonly record struct DecoratedEntry(SearchNode Node, CombatStateDescriptionDigest Key, int InputIndex);
    private readonly record struct ProgressEntry(SearchNode Node, CombatStateDescriptionDigest Key, double EnemyRemaining, int HpMargin, int OrderedIndex);

    private sealed class PrimaryComparer : IComparer<DecoratedEntry>
    {
        public static readonly PrimaryComparer Instance = new();
        public int Compare(DecoratedEntry x, DecoratedEntry y)
        {
            int c = y.Node.Score.CompareTo(x.Node.Score);
            if (c != 0) return c;
            c = x.Node.Depth.CompareTo(y.Node.Depth);
            if (c != 0) return c;
            c = x.Key.First.CompareTo(y.Key.First);
            if (c != 0) return c;
            c = x.Key.Second.CompareTo(y.Key.Second);
            if (c != 0) return c;
            c = x.Node.Fingerprint.First.CompareTo(y.Node.Fingerprint.First);
            if (c != 0) return c;
            c = x.Node.Fingerprint.Second.CompareTo(y.Node.Fingerprint.Second);
            if (c != 0) return c;
            return x.InputIndex.CompareTo(y.InputIndex);
        }
    }

    private sealed class ParetoComparer : IComparer<ProgressEntry>
    {
        public static readonly ParetoComparer Instance = new();
        public int Compare(ProgressEntry x, ProgressEntry y)
        {
            int c = x.EnemyRemaining.CompareTo(y.EnemyRemaining);
            if (c != 0) return c;
            c = y.HpMargin.CompareTo(x.HpMargin);
            if (c != 0) return c;
            c = y.Node.Score.CompareTo(x.Node.Score);
            if (c != 0) return c;
            return x.OrderedIndex.CompareTo(y.OrderedIndex);
        }
    }

    private sealed class EnemyProgressComparer : IComparer<ProgressEntry>
    {
        public static readonly EnemyProgressComparer Instance = new();
        public int Compare(ProgressEntry x, ProgressEntry y)
        {
            int c = x.EnemyRemaining.CompareTo(y.EnemyRemaining);
            if (c != 0) return c;
            c = y.Node.Score.CompareTo(x.Node.Score);
            if (c != 0) return c;
            return x.OrderedIndex.CompareTo(y.OrderedIndex);
        }
    }

    private sealed class RetentionScratch
    {
        [ThreadStatic]
        private static RetentionScratch? _current;

        public readonly List<DecoratedEntry> Entries = new(64);
        public readonly List<DecoratedEntry> RotatedOrdinaryEntries = new(64);
        public readonly List<ProgressEntry> ProgressEntries = new(64);
        public readonly List<SearchNode> PowerCommitmentPool = new(32);
        public readonly HashSet<CombatStateDescriptionDigest> SeenExact = new(64);
        public readonly HashSet<CombatStateDescriptionDigest> RepresentedBuckets = new(64);
        public readonly HashSet<CombatStateDescriptionDigest> RotatedSeenExact = new(64);
        public readonly HashSet<CombatStateDescriptionDigest> RotatedBuckets = new(64);
        public readonly Dictionary<SetupSignatureKey, int> SetupCounts = new(32);
        public readonly List<SetupToken> SetupArena = new(256);

        public static RetentionScratch Acquire()
        {
            RetentionScratch? scratch = _current;
            _current = null;
            return scratch ?? new RetentionScratch();
        }

        public static void Release(RetentionScratch scratch)
        {
            scratch.Entries.Clear();
            scratch.RotatedOrdinaryEntries.Clear();
            scratch.ProgressEntries.Clear();
            scratch.PowerCommitmentPool.Clear();
            scratch.SeenExact.Clear();
            scratch.RepresentedBuckets.Clear();
            scratch.RotatedSeenExact.Clear();
            scratch.RotatedBuckets.Clear();
            scratch.SetupCounts.Clear();
            scratch.SetupArena.Clear();
            _current = scratch;
        }
    }
}
