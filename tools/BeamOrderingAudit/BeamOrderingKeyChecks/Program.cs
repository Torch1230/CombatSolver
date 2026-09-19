using System.Text.RegularExpressions;
using CombatSolver;

// Demo 1: 用一个与目标同序的"排序键"替换中途排序的加权标量，并量化成本与收益。
//
// 判据来自生产代码本身：BeamOrderingKey.CompareTo 直接委托
// SolverInterimResultOrdering.ComparePrimaryQuality，所以"键与目标同序"是构造性的，
// 不靠人工对符号。本 demo 检查的是标量做不到的四件事：带语义、全序性质、
// 加权标量和目标的真实冲突，以及键结构体能否直接落在既有 LINQ 调用点上。

/// <summary>
/// 中途排序键。高位逐位与 ComparePrimaryQuality 相同，末位保留原有附加分只作兜底。
/// 约定"越大越好"，与 BeamRankScore 的既有用法（OrderByDescending / Max / MaxBy）一致。
/// </summary>
internal readonly record struct BeamOrderingKey(
    bool Victory,
    int DeathSaveUseCount,
    int StrategicHpDeficit,
    int GrowthHpCredit,
    int GrowthRewardCount,
    int CombatEndedTurn,
    double SupplementalScore) : IComparable<BeamOrderingKey>
{
    /// <summary>
    /// 负值表示 this 更差，正值表示 this 更好，与 BeamRankScore 的"越大越好"一致。
    /// 委托给生产比较器，参数位置即 ComparePrimaryQuality 的语义位置。
    /// </summary>
    public int CompareTo(BeamOrderingKey other)
    {
        int comparison = -SolverInterimResultOrdering.ComparePrimaryQuality(
            Victory,
            StrategicHpDeficit,
            CombatEndedTurn,
            other.Victory,
            other.StrategicHpDeficit,
            other.CombatEndedTurn,
            GrowthHpCredit,
            other.GrowthHpCredit,
            GrowthRewardCount,
            other.GrowthRewardCount,
            DeathSaveUseCount,
            other.DeathSaveUseCount);
        return comparison != 0
            ? comparison
            : SupplementalScore.CompareTo(other.SupplementalScore);
    }

    /// <summary>
    /// 并列带判据：只看目标自己的键，不看兜底附加分。目标尚未能区分的一批节点
    /// 必须整体保留，而不是按浮点相等逐个丢弃。
    /// </summary>
    public bool SameBand(BeamOrderingKey other)
        => Victory == other.Victory
            && DeathSaveUseCount == other.DeathSaveUseCount
            && StrategicHpDeficit == other.StrategicHpDeficit
            && GrowthHpCredit == other.GrowthHpCredit
            && GrowthRewardCount == other.GrowthRewardCount
            && CombatEndedTurn == other.CombatEndedTurn;
}

internal static class Program
{
    private const string ProductionComparatorPath = "../../../src/Search/SolverInterimResultOrdering.cs";
    private const string ProductionResultPath = "../../../src/Runtime/SolverProgress.cs";
    private static int _assertions;

    private static int Main()
    {
        (string Name, Action Check)[] checks =
        [
            ("key agrees with the production comparator", KeyAgreesWithProduction),
            ("key ordering is a total preorder", TotalPreorder),
            ("band covers the objective's own tie set", BandCoversObjectiveTies),
            ("legacy exact-double band is empty at a boundary", LegacyBandIsEmpty),
            ("legacy weighted score loses to a leading key", SupplementalNeverOutranksLeadingKey),
            ("key struct reuses existing LINQ call sites", PlumbingReuse),
            ("band must be capped and ordered by the supplemental score", BandMustBeCapped),
            ("local result stub matches production", StubFidelity),
        ];
        int failures = 0;
        foreach ((string name, Action check) in checks)
        {
            int before = _assertions;
            try
            {
                check();
                Console.WriteLine($"ok   {name} (+{_assertions - before})");
            }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }
        Console.WriteLine($"\n{checks.Length - failures}/{checks.Length} groups, {_assertions} assertions.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>键的符号必须与生产比较器完全一致，包括可选参数的实际位置。</summary>
    private static void KeyAgreesWithProduction()
    {
        foreach (BeamOrderingKey left in Grid())
        {
            foreach (BeamOrderingKey right in Grid())
            {
                int expected = -SolverInterimResultOrdering.ComparePrimaryQuality(
                    left.Victory, left.StrategicHpDeficit, left.CombatEndedTurn,
                    right.Victory, right.StrategicHpDeficit, right.CombatEndedTurn,
                    left.GrowthHpCredit, right.GrowthHpCredit,
                    left.GrowthRewardCount, right.GrowthRewardCount,
                    left.DeathSaveUseCount, right.DeathSaveUseCount);
                int observed = left.CompareTo(right);
                // 附加分只在目标完全并列时才允许出现差异。
                Equal(Math.Sign(expected), Math.Sign(observed),
                    $"sign for {Describe(left)} vs {Describe(right)}");
            }
        }
    }

    private static void TotalPreorder()
    {
        List<BeamOrderingKey> keys = [.. Grid()];
        foreach (BeamOrderingKey a in keys)
        {
            Equal(0, a.CompareTo(a), "reflexive tie");
            foreach (BeamOrderingKey b in keys)
            {
                Equal(-Math.Sign(b.CompareTo(a)), Math.Sign(a.CompareTo(b)),
                    $"antisymmetry for {Describe(a)} vs {Describe(b)}");
                foreach (BeamOrderingKey c in keys)
                {
                    if (a.CompareTo(b) <= 0 && b.CompareTo(c) <= 0)
                        Check(a.CompareTo(c) <= 0, "transitivity");
                }
            }
        }
    }

    /// <summary>带必须等于"目标尚不能区分"的集合：同带节点两两同序，跨带节点必有先后。</summary>
    private static void BandCoversObjectiveTies()
    {
        List<BeamOrderingKey> cohort = BoundaryCohort();
        List<BeamOrderingKey> sameBand = [.. cohort.Where(node => node.SameBand(cohort[0]))];
        Check(sameBand.Count > 1, "the boundary cohort must contain a real band");
        foreach (BeamOrderingKey a in sameBand)
        {
            foreach (BeamOrderingKey b in sameBand)
            {
                // 同带 = 目标完全无法区分 = 比较器返回并列。
                Equal(0, SolverInterimResultOrdering.ComparePrimaryQuality(
                    a.Victory, a.StrategicHpDeficit, a.CombatEndedTurn,
                    b.Victory, b.StrategicHpDeficit, b.CombatEndedTurn,
                    a.GrowthHpCredit, b.GrowthHpCredit,
                    a.GrowthRewardCount, b.GrowthRewardCount,
                    a.DeathSaveUseCount, b.DeathSaveUseCount),
                    $"band members must be indistinguishable: {Describe(a)} vs {Describe(b)}");
            }
        }
        foreach (BeamOrderingKey outside in cohort.Where(node => !node.SameBand(cohort[0])))
        {
            Check(cohort[0].CompareTo(outside) != 0, "a different band must not compare equal");
        }
    }

    /// <summary>
    /// 现行 SamePrimary 用 double 精确相等判并列。带里节点的附加分各不相同，所以带恒为空集。
    /// 这就是"多样化机制存在但几乎不触发"的成因。
    /// </summary>
    private static void LegacyBandIsEmpty()
    {
        List<double> legacyScores = [.. BoundaryCohort().Select(node => node.SupplementalScore)];
        Check(legacyScores.Distinct().Count() == legacyScores.Count,
            "legacy supplemental scores must be distinct in a realistic cohort");
        double boundary = legacyScores[0];
        int legacyBand = legacyScores.Count(score => score.Equals(boundary));
        Equal(1, legacyBand, "the legacy exact-double band can only ever hold the boundary node");
        Check(BoundaryCohort().Count(node => node.SameBand(BoundaryCohort()[0])) > legacyBand,
            "the objective-aligned band must be strictly wider than the legacy band");
    }

    /// <summary>
    /// 加权标量把量纲不同的项相加，必然存在"标量更优但目标更差"的节点对。取一对构造证明，
    /// 并断言键站目标那一边。注意：这证明冲突可能发生，不下任何频率结论。
    /// </summary>
    private static void SupplementalNeverOutranksLeadingKey()
    {
        // 目标：A 与 B 都未胜，A 的战略 HP 亏损更小 → A 优于 B。
        // 标量：B 的附加分远高 → 现行排序把 B 排在 A 前面。
        var better = new BeamOrderingKey(
            Victory: false, DeathSaveUseCount: 0, StrategicHpDeficit: 10,
            GrowthHpCredit: 0, GrowthRewardCount: 0, CombatEndedTurn: 5,
            SupplementalScore: 100);
        var worseButHigherScore = new BeamOrderingKey(
            Victory: false, DeathSaveUseCount: 0, StrategicHpDeficit: 40,
            GrowthHpCredit: 0, GrowthRewardCount: 0, CombatEndedTurn: 5,
            SupplementalScore: 100_000);
        Check(worseButHigherScore.SupplementalScore > better.SupplementalScore,
            "the constructed pair must invert under the legacy scalar");
        Check(better.CompareTo(worseButHigherScore) > 0,
            "the key must keep the objectively better node ahead of a larger supplemental score");
        Check(!better.SameBand(worseButHigherScore),
            "an objective difference must leave the band");
        // 反过来：目标完全并列时，附加分才允许决定顺序。
        var tie = better with { SupplementalScore = worseButHigherScore.SupplementalScore };
        Check(better.SameBand(tie), "an equal objective must stay in the band");
        Check(tie.CompareTo(better) > 0, "the supplemental score decides inside the band");
    }

    /// <summary>
    /// 把 double 换成键结构体后，既有 LINQ 调用点必须原样可用（BeamRetentionPolicy 里
    /// 约 40 处 OrderByDescending / Max / MaxBy / GroupBy().Max 依赖同一形态）。
    /// </summary>
    private static void PlumbingReuse()
    {
        List<(string Name, BeamOrderingKey Key)> nodes =
        [
            ("a", new BeamOrderingKey(true, 0, 30, 0, 0, 5, 100)),
            ("b", new BeamOrderingKey(true, 0, 30, 0, 0, 5, 200)),
            ("c", new BeamOrderingKey(true, 0, 12, 0, 0, 9, 10)),
            ("d", new BeamOrderingKey(false, 0, 0, 0, 0, 2, 999)),
        ];
        Equal("c", nodes.MaxBy(node => node.Key)!.Name, "MaxBy picks the best key");
        Equal("d", nodes.OrderBy(node => node.Key).First().Name,
            "ascending order leads with the worst key under a larger-is-better convention");
        Equal("c", nodes.OrderByDescending(node => node.Key).First().Name,
            "descending order leads with the best key");
        Equal(nodes[2].Key, nodes.GroupBy(node => node.Key.Victory).Max(group => group.Max(node => node.Key)),
            "GroupBy then Max");
        Equal("a", nodes.OrderBy(node => node.Name).ThenBy(node => node.Key).First().Name,
            "ThenBy accepts the key");
        Equal(1, Comparer<BeamOrderingKey>.Default.Compare(nodes[1].Key, nodes[0].Key),
            "Comparer<T>.Default sees the key");
        // 结构体相等是逐字段的：这正好是"目标并列"需要的语义，而不是浮点相等。
        Check(nodes[0].Key.Equals(new BeamOrderingKey(true, 0, 30, 0, 0, 5, 100)),
            "record struct equality is structural");
    }

    /// <summary>
    /// 这条不是收益声明，是危险约束。目标键在"未完成"节点上会大批并列：它们都还没胜利、
    /// 药水数和回合也常相同，而前瞻信息（能量、增益、铺垫、延迟伤害）不在目标键里。
    /// 所以带不能无限扩大——必须有上界，且带内顺序必须继续由兜底附加分决定，
    /// 否则保留哪几个就取决于枚举顺序。外部证据同样警告：更"正确"的键可能因代价而更差。
    /// </summary>
    private static void BandMustBeCapped()
    {
        const int cap = 16;
        List<BeamOrderingKey> frontier = [.. Enumerable.Range(0, 64).Select(index =>
            new BeamOrderingKey(false, 0, 12, 0, 0, 3, 500 - index))];
        Equal(frontier.Count, frontier.Count(node => node.SameBand(frontier[0])),
            "unfinished nodes must tie on every objective key");
        Check(frontier.Count(node => node.SameBand(frontier[0])) > cap,
            "the uncapped band would exceed a plausible retention cap");

        List<BeamOrderingKey> retained = [.. frontier.OrderByDescending(node => node).Take(cap)];
        Equal(cap, retained.Count, "the cap must be met exactly");
        Equal(cap, retained.Select(node => node.SupplementalScore).Distinct().Count(),
            "the cap must not retain several nodes with one supplemental score");
        Check(retained.All(node => node.SupplementalScore > 500 - cap - 1),
            "within a band the cap must follow the supplemental score, not enumeration order");
    }

    /// <summary>本地 stub 只为让生产比较器文件编译通过；一旦漂移，本检查必须报警。</summary>
    private static void StubFidelity()
    {
        string source = File.ReadAllText(ProductionResultPath);
        Match declaration = Regex.Match(source,
            @"internal sealed record SolverInterimResult\((?<body>.*?)\)\s*\{",
            RegexOptions.Singleline);
        Check(declaration.Success, "production SolverInterimResult declaration not found");
        string[] names = [.. declaration.Groups["body"].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Replace(line, @"^\s*(?:[\w<>?,\[\]]+)\s+(\w+).*$", "$1").Trim().TrimEnd(','))
            .Where(name => name.Length > 0)];
        string[] expected =
        [
            "Won", "OutstandingStolenResource", "ProjectedBattleHpLost", "StrategicHpDeficit",
            "PotionStrategicCost", "ProjectedBattlePotionCount", "EnemyHp", "Score", "CombatEndedTurn",
        ];
        Equal(expected.Length, names.Length, "stub field count must match production");
        for (int index = 0; index < expected.Length; index++)
            Equal(expected[index], names[index], $"stub field {index}");
        string comparator = File.ReadAllText(ProductionComparatorPath);
        Check(comparator.Contains("SolverTheftPolicy.PreserveResources", StringComparison.Ordinal),
            "production comparator still references SolverTheftPolicy.PreserveResources");
    }

    /// <summary>边界带：目标键完全并列，只有兜底附加分不同 —— 现行 SamePrimary 判据下的真实形态。</summary>
    private static List<BeamOrderingKey> BoundaryCohort() =>
    [
        new(true, 0, 24, 3, 1, 4, 612.5),
        new(true, 0, 24, 3, 1, 4, 604.25),
        new(true, 0, 24, 3, 1, 4, 597.125),
        new(true, 0, 24, 3, 1, 4, 590.0),
        // 目标可区分的邻居：不应落进带里。
        new(true, 0, 25, 3, 1, 4, 700.0),
        new(false, 0, 24, 3, 1, 4, 800.0),
    ];

    private static IEnumerable<BeamOrderingKey> Grid()
    {
        foreach (bool victory in new[] { false, true })
            foreach (int deficit in new[] { 0, 7, 24 })
                foreach (int turn in new[] { 1, 4, 9 })
                    foreach (int credit in new[] { 0, 6 })
                        foreach (int reward in new[] { 0, 2 })
                            foreach (int deathSaves in new[] { 0, 1 })
                                yield return new BeamOrderingKey(
                                    victory, deathSaves, deficit, credit, reward, turn, deficit * 3.5);
    }

    private static string Describe(BeamOrderingKey key)
        => $"(won={key.Victory},death={key.DeathSaveUseCount},deficit={key.StrategicHpDeficit}," +
            $"turn={key.CombatEndedTurn},score={key.SupplementalScore})";

    private static void Check(bool condition, string context)
    {
        _assertions++;
        if (!condition)
            throw new CheckFailure(context);
    }

    private static void Equal<T>(T expected, T actual, string context)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new CheckFailure($"{context}: expected {expected}, observed {actual}.");
    }

    private sealed class CheckFailure(string message) : Exception(message);
}

// 仅为让生产比较器文件在本工程内编译通过的最小 stub。字段顺序与默认值由 StubFidelity 校验。
internal enum SolverTheftPolicy
{
    PreserveResources,
    LetEscape,
}

internal sealed record SolverInterimResult(
    bool Won,
    int OutstandingStolenResource,
    int ProjectedBattleHpLost,
    int StrategicHpDeficit,
    int PotionStrategicCost,
    int ProjectedBattlePotionCount,
    int EnemyHp,
    double Score,
    int? CombatEndedTurn = null)
{
    public SolverTheftPolicy? TheftPolicy { get; init; }
    public bool Survives { get; init; }
    public int DeathSaveUseCount { get; init; }
    public int GrowthHpCredit { get; init; }
    public int GrowthRewardCount { get; init; }
}
