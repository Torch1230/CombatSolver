using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Combat.StateDescription;
using Sts2Sim.Core.Entities.Creatures;
using Sts2Sim.Core.Entities.Powers;
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
        bool aggressivePowerCommitment = false)
    {
        var seenExact = new HashSet<CombatStateDescriptionDigest>();
        var representedBuckets = new HashSet<CombatStateDescriptionDigest>();
        var retained = new List<SearchNode>(width);
        (SearchNode Node, CombatStateDescriptionDigest Key)[] ordered = candidates
            .Select(node => (Node: node, Key: keySelector(node)))
            .OrderByDescending(entry => entry.Node.Score)
            .ThenBy(entry => entry.Node.Depth)
            .ThenBy(entry => entry.Key.First)
            .ThenBy(entry => entry.Key.Second)
            .ThenBy(entry => entry.Node.Fingerprint.First)
            .ThenBy(entry => entry.Node.Fingerprint.Second)
            .ToArray();

        bool TryAdmitUniqueBucket(SearchNode node, CombatStateDescriptionDigest bucketKey)
        {
            if (seenExact.Contains(node.Fingerprint) || !representedBuckets.Add(bucketKey))
                return false;
            seenExact.Add(node.Fingerprint);
            retained.Add(node);
            return true;
        }

        int ordinarySlots = aggressivePowerCommitment
            ? Math.Max(1, width * 5 / 8)
            : Math.Max(1, width * 3 / 4);
        foreach ((SearchNode node, CombatStateDescriptionDigest key) in ordered)
        {
            TryAdmitUniqueBucket(node, key);
            if (retained.Count >= ordinarySlots) break;
        }

        // Keep a small portfolio of paths that changed persistent combat setup.
        // They can be temporarily behind immediate damage and block while their
        // payoff appears in later turns. No card identity or encounter rule is used.
        var setupCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (SearchNode existing in retained)
        {
            string signature = SetupSignature(existing);
            setupCounts[signature] = setupCounts.GetValueOrDefault(signature) + 1;
        }
        int maxPerSetupSignature = aggressivePowerCommitment ? 2 : 1;
        int setupEnd = Math.Min(
            width,
            ordinarySlots + (aggressivePowerCommitment ? Math.Max(2, width / 8) : Math.Max(1, width / 6)));
        IEnumerable<(SearchNode Node, CombatStateDescriptionDigest Key)> setupCandidates = aggressivePowerCommitment
            ? ordered.OrderByDescending(entry => HasPositivePlayerBuff(entry.Node))
            : ordered;
        foreach ((SearchNode node, CombatStateDescriptionDigest key) in setupCandidates)
        {
            if (retained.Count >= setupEnd) break;
            if (seenExact.Contains(node.Fingerprint) || representedBuckets.Contains(key)) continue;
            string signature = SetupSignature(node);
            int count = setupCounts.GetValueOrDefault(signature);
            if (count < maxPerSetupSignature && TryAdmitUniqueBucket(node, key))
                setupCounts[signature] = count + 1;
        }
        if (includeParetoProgress && retained.Count < width)
        {
            // 在进攻推进席位中优先保留不被支配的 (EnemyRemaining, EffectiveHpMargin) 前沿点，
            // 避免同一低血量高伤害分支的等价次优变体挤占温和换血的高价值进攻路线。
            int bestHpMargin = int.MinValue;
            foreach ((SearchNode node, CombatStateDescriptionDigest key) in ordered
                .OrderBy(entry => EnemyRemaining(entry.Node))
                .ThenByDescending(entry => EffectiveHpMargin(entry.Node))
                .ThenByDescending(entry => entry.Node.Score))
            {
                if (retained.Count >= width) break;
                if (!node.State.Players[0].Creature.IsAlive) continue;
                int hpMargin = EffectiveHpMargin(node);
                if (hpMargin <= bestHpMargin) continue;
                bestHpMargin = hpMargin;
                TryAdmitUniqueBucket(node, key);
            }
        }
        // Reserve the remaining slots for routes with enemy HP progress that
        // short-term protection scores can hide. The same Core simulation still
        // decides whether the aggressive line survives subsequent turns.
        foreach ((SearchNode node, CombatStateDescriptionDigest key) in ordered
            .OrderBy(entry => EnemyRemaining(entry.Node))
            .ThenByDescending(entry => entry.Node.Score))
        {
            if (retained.Count >= width) break;
            TryAdmitUniqueBucket(node, key);
        }
        foreach ((SearchNode node, CombatStateDescriptionDigest key) in ordered)
        {
            if (retained.Count >= width) break;
            TryAdmitUniqueBucket(node, key);
        }
        // 当桶多样性选择后 Beam 仍有空余名额时，仅按精确 Fingerprint 去重回填，
        // 避免不同弃牌堆/伤害历史顺序的精确状态在容量未满时被摘要桶误吞。
        if (retained.Count < width)
        {
            foreach ((SearchNode node, _) in ordered)
            {
                if (retained.Count >= width) break;
                if (seenExact.Add(node.Fingerprint))
                    retained.Add(node);
            }
        }
        if (preservePotionFree && width > 1 && retained.All(node => node.PotionUses > 0)
            && ordered.FirstOrDefault(entry => entry.Node.PotionUses == 0).Node is { } free)
        {
            retained.RemoveAt(retained.Count - 1);
            retained.Add(free);
        }
        return retained;
    }

    private static int EffectiveHpMargin(SearchNode node)
    {
        CombatState state = node.State;
        Creature creature = state.Players[0].Creature;
        if (!creature.IsAlive) return int.MinValue;
        int threat = 0;
        foreach (Creature enemy in state.Enemies)
        {
            if (!enemy.IsAlive) continue;
            foreach (AttackIntent intent in enemy.Monster?.NextMove?.Intents.OfType<AttackIntent>() ?? [])
            {
                threat += Math.Max(0, intent.GetTotalDamage([creature], enemy));
            }
        }

        int exposed = Math.Max(0, threat - creature.Block);
        return creature.CurrentHp - exposed;
    }

    private static double EnemyRemaining(SearchNode node) => node.State.Enemies
        .Where(enemy => enemy.IsAlive)
        .Sum(enemy => enemy.CurrentHp + enemy.Block * 0.5);

    private static bool HasPositivePlayerBuff(SearchNode node) =>
        node.State.Players[0].Creature.Powers.Any(power => power.Type == PowerType.Buff && power.Amount > 0);

    private static string SetupSignature(SearchNode node)
    {
        var state = node.State;
        var player = state.Players[0];
        string powers = string.Join(',', player.Creature.Powers
            .Where(power => power.Type == PowerType.Buff)
            .OrderBy(power => power.Id.Entry, StringComparer.Ordinal)
            .Select(power => $"{power.Id.Entry}:{power.Amount}"));
        string enemyDebuffs = string.Join(',', state.Enemies.Select((enemy, index) =>
            $"{index}[{string.Join(';', enemy.Powers
                .Where(power => power.Type == PowerType.Debuff)
                .OrderBy(power => power.Id.Entry, StringComparer.Ordinal)
                .Select(power => $"{power.Id.Entry}:{power.Amount}"))}]"));
        string potions = string.Join(',', player.PotionSlots.Select(potion => potion?.Id.Entry ?? ""));
        return $"{powers}|{enemyDebuffs}|{potions}";
    }
}
