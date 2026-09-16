namespace CombatSolver;

/// <summary>一个成员在生产路径上的实测开销；跳过的成员这三项为零。</summary>
internal readonly record struct BeamWidthPortfolioMemberCost(
    long ElapsedMilliseconds,
    long AllocatedBytes,
    long ManagedHeapBytesAfter);

/// <summary>
/// 逐成员明细：既有的 <see cref="BeamWidthPortfolioMember" /> 加上生产路径才有的开销与选中标记。
/// </summary>
internal sealed record BeamWidthPortfolioMemberReport(
    int BeamWidth,
    bool SecondRankBand,
    bool BaseScoreOnly,
    int NodeBudget,
    bool Ran,
    bool Selected,
    bool Compared,
    string? SkippedReason,
    long ExpandedNodes,
    long TransitionCount,
    string? Termination,
    bool? Terminal,
    bool? Won,
    int? BattleHpLost,
    int? PotionCount,
    long ElapsedMilliseconds,
    long AllocatedBytes,
    long ManagedHeapBytesAfter);

/// <summary>
/// 请求级的组合诊断。开关关闭时也照样记录——那时是单成员一行，A/B 才能直接并排比。
/// </summary>
/// <remarks>
/// 协调器在一次请求里可能跑不止一轮搜索（无胜利时抬节点上限重搜），每轮的成员按顺序追加，
/// <see cref="FirstRoutePublishedMilliseconds" /> 只记第一次。
/// </remarks>
internal sealed class BeamWidthPortfolioTelemetry
{
    private readonly Lock _gate = new();
    private readonly List<BeamWidthPortfolioMemberReport> _members = [];
    private double? _firstRoutePublishedMilliseconds;
    private long _peakManagedHeapBytes;

    /// <summary>基线成员完成并按今天的方式发布给覆盖层的时刻，相对本次搜索请求开始。</summary>
    public double? FirstRoutePublishedMilliseconds
    {
        get { lock (_gate) return _firstRoutePublishedMilliseconds; }
    }

    /// <summary>各成员结束后 <c>GC.GetTotalMemory(false)</c> 的最大值。</summary>
    public long PeakManagedHeapBytes
    {
        get { lock (_gate) return _peakManagedHeapBytes; }
    }

    public IReadOnlyList<BeamWidthPortfolioMemberReport> Members
    {
        get { lock (_gate) return _members.ToArray(); }
    }

    public void RecordFirstRoutePublished(double elapsedMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        lock (_gate)
            _firstRoutePublishedMilliseconds ??= elapsedMilliseconds;
    }

    public void RecordMember(BeamWidthPortfolioMemberReport member)
    {
        ArgumentNullException.ThrowIfNull(member);
        lock (_gate)
        {
            _members.Add(member);
            if (member.Ran && member.ManagedHeapBytesAfter > _peakManagedHeapBytes)
                _peakManagedHeapBytes = member.ManagedHeapBytesAfter;
        }
    }
}
