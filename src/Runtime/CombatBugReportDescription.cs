using System.Text;

namespace CombatSolver;

internal enum CombatBugReportIssueKind
{
    BetterWorldline,
    ManualHpLossIncreased,
    RecalculationHpLossIncreased,
    SearchSetupFailure,
    IncompatibleGameplayMod,
    SearchFailure,
    SearchActionReplayFailure,
    SearchCapacityFailure,
    PotionPolicyUnsatisfied,
    SearchResultStale,
    DeploymentFailure,
    TurnSetupFailure,
    TurnSetupStateMismatch,
    UnexpectedChoice,
    ChoiceExecutionFailure,
    RelicReplayMismatch,
    TimeoutFailure,
    UnsupportedCombatSemantic,
    FullAutoStoppedAfterWorseRecalculation,
    FullAutoStoppedAtDeathTurn,
    FullAutoStoppedAtLiveRiskDeath,
    FullAutoStoppedAtLiveRiskWorsening,
}

internal sealed record CombatBugReportIssue(
    CombatBugReportIssueKind Kind,
    int Count,
    string? Detail);

internal sealed class CombatBugReportIssueLedger
{
    private readonly Dictionary<CombatBugReportIssueKind, CombatBugReportIssue> _issues = [];

    public void Record(CombatBugReportIssueKind kind, string? detail = null)
    {
        string? normalizedDetail = CombatBugReportDescription.NormalizeDetail(detail);
        int count = _issues.TryGetValue(kind, out CombatBugReportIssue? previous)
            ? previous.Count + 1
            : 1;
        _issues[kind] = new CombatBugReportIssue(kind, count, normalizedDetail ?? previous?.Detail);
    }

    public void RecordFailure(CombatBugReportIssueKind primaryKind, Exception exception)
    {
        Exception failure = exception.GetBaseException();
        if (failure is IncompatibleGameplayModException incompatible)
        {
            Record(CombatBugReportIssueKind.IncompatibleGameplayMod, incompatible.PlayerFacingModName);
            return;
        }
        string detail = $"{failure.GetType().Name}：{failure.Message}";
        Record(primaryKind, detail);
        if (failure is SearchTransitionException
            || failure.Message.Contains("搜索动作回放失败", StringComparison.Ordinal))
        {
            Record(CombatBugReportIssueKind.SearchActionReplayFailure, detail);
        }
        if (failure is OutOfMemoryException
            || failure.Message.Contains("totalSize is too large", StringComparison.OrdinalIgnoreCase)
            || failure.Message.Contains("容量不足", StringComparison.Ordinal))
        {
            Record(CombatBugReportIssueKind.SearchCapacityFailure, detail);
        }
        if (failure is PotionPolicyUnsatisfiedException)
            Record(CombatBugReportIssueKind.PotionPolicyUnsatisfied, detail);
        if (failure is TimeoutException)
            Record(CombatBugReportIssueKind.TimeoutFailure, detail);
        if (failure is NotSupportedException and not IncompatibleGameplayModException)
            Record(CombatBugReportIssueKind.UnsupportedCombatSemantic, detail);
        if (IsUnexpectedChoiceFailure(failure.Message))
            Record(CombatBugReportIssueKind.UnexpectedChoice, detail);
        if (IsChoiceExecutionFailure(failure.Message))
            Record(CombatBugReportIssueKind.ChoiceExecutionFailure, detail);
        if (failure.Message.Contains("遗物标注回放与选中状态不一致", StringComparison.Ordinal))
            Record(CombatBugReportIssueKind.RelicReplayMismatch, detail);
    }

    public IReadOnlyList<CombatBugReportIssue> Snapshot()
        => _issues.Values.OrderBy(issue => issue.Kind).ToArray();

    public bool RequiresPlayerUpload
        => _issues.Keys.Any(kind => kind is not (
            CombatBugReportIssueKind.ManualHpLossIncreased
            or CombatBugReportIssueKind.IncompatibleGameplayMod
            or CombatBugReportIssueKind.FullAutoStoppedAtDeathTurn));

    private static bool IsUnexpectedChoiceFailure(string message)
        => message.Contains("计划外选择", StringComparison.Ordinal)
           || message.Contains("计划外的", StringComparison.Ordinal)
           || message.Contains("未计划选择", StringComparison.Ordinal)
           || message.Contains("路线未提供的选牌", StringComparison.Ordinal)
           || message.Contains("没有返回计划选择", StringComparison.Ordinal)
           || message.Contains("计划选牌没有触发", StringComparison.Ordinal);

    private static bool IsChoiceExecutionFailure(string message)
        => message.Contains("选牌", StringComparison.Ordinal)
           || message.Contains("选择", StringComparison.Ordinal)
           || message.Contains("卡牌节点", StringComparison.Ordinal)
           || message.Contains("确认按钮", StringComparison.Ordinal)
           || message.Contains("Hand Exhaust", StringComparison.OrdinalIgnoreCase);
}

internal sealed record CombatBugReportClassificationSnapshot(
    int StateMismatchReplans,
    int DeploymentDriftReplans,
    int ContinuationMissingReplans,
    int PlanExhaustedReplans,
    int ManualDivergenceReplans,
    IReadOnlyList<CombatBugReportIssue> Issues);

internal static class CombatBugReportDescription
{
    public const int MaximumPlayerDescriptionCharacters = 4_000;
    private const int MaximumDetailLength = 320;

    public static string AppendAutomaticClassification(
        string playerDescription,
        CombatBugReportClassificationSnapshot snapshot)
    {
        if (playerDescription.Length > MaximumPlayerDescriptionCharacters)
        {
            throw new InvalidDataException(
                $"玩家问题描述最长 {MaximumPlayerDescriptionCharacters} 个字符。");
        }
        List<string> classifications = BuildClassifications(snapshot);
        StringBuilder builder = new();
        if (!string.IsNullOrWhiteSpace(playerDescription))
        {
            builder.Append(playerDescription.TrimEnd());
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.AppendLine("【CombatSolver 自动分类】");
        builder.Append("- CombatSolver 版本：");
        builder.AppendLine(CurrentModVersion);
        if (classifications.Count == 0)
        {
            builder.Append("- 未检测到求解器已记录的异常信号");
            return builder.ToString();
        }

        for (int index = 0; index < classifications.Count; index++)
        {
            builder.Append("- ");
            builder.Append(classifications[index]);
            if (index + 1 < classifications.Count)
                builder.AppendLine();
        }
        return builder.ToString();
    }

    internal static string CurrentModVersion
    {
        get
        {
            Version version = typeof(CombatBugReportDescription).Assembly.GetName().Version
                ?? throw new InvalidOperationException("无法读取 CombatSolver 程序集版本。");
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static string AppendSubmissionId(string description, string submissionId)
    {
        if (!Guid.TryParseExact(submissionId, "N", out _))
            throw new InvalidDataException("提交编号格式无效。");
        return description.TrimEnd() +
               System.Environment.NewLine + System.Environment.NewLine +
               $"【CombatSolver 提交编号】{submissionId}";
    }

    internal static string? NormalizeDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return null;
        string normalized = string.Join(' ', detail
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= MaximumDetailLength
            ? normalized
            : normalized[..MaximumDetailLength] + "…";
    }

    private static List<string> BuildClassifications(CombatBugReportClassificationSnapshot snapshot)
    {
        List<string> lines = [];
        int unexpectedReplans = snapshot.StateMismatchReplans + snapshot.DeploymentDriftReplans;
        if (unexpectedReplans > 0)
        {
            lines.Add(
                $"计划外重算：{unexpectedReplans} 次（状态不一致 {snapshot.StateMismatchReplans}，" +
                $"执行漂移 {snapshot.DeploymentDriftReplans}）");
        }
        if (snapshot.ContinuationMissingReplans > 0)
            lines.Add($"续接路线缺失后重算：{snapshot.ContinuationMissingReplans} 次");
        if (snapshot.PlanExhaustedReplans > 0)
            lines.Add($"本回合路线耗尽后重算：{snapshot.PlanExhaustedReplans} 次");
        if (snapshot.ManualDivergenceReplans > 0)
            lines.Add($"手操偏离原路线后重算：{snapshot.ManualDivergenceReplans} 次");

        foreach (CombatBugReportIssue issue in snapshot.Issues)
        {
            string label = Label(issue.Kind);
            string count = issue.Count > 1 ? $"（{issue.Count} 次）" : string.Empty;
            string detail = issue.Detail == null ? string.Empty : $"：{issue.Detail}";
            lines.Add(label + count + detail);
        }
        return lines;
    }

    private static string Label(CombatBugReportIssueKind kind)
        => kind switch
        {
            CombatBugReportIssueKind.BetterWorldline => "找到更优世界线",
            CombatBugReportIssueKind.ManualHpLossIncreased => "手操后预计战损上升",
            CombatBugReportIssueKind.RecalculationHpLossIncreased => "重算后预计战损上升",
            CombatBugReportIssueKind.SearchSetupFailure => "搜索初始化失败",
            CombatBugReportIssueKind.IncompatibleGameplayMod => "第三方 Mod 不兼容",
            CombatBugReportIssueKind.SearchFailure => "计算失败",
            CombatBugReportIssueKind.SearchActionReplayFailure => "搜索动作回放失败",
            CombatBugReportIssueKind.SearchCapacityFailure => "搜索内存或容量错误",
            CombatBugReportIssueKind.PotionPolicyUnsatisfied => "药水策略未满足",
            CombatBugReportIssueKind.SearchResultStale => "计算期间状态变化，过期结果已丢弃",
            CombatBugReportIssueKind.DeploymentFailure => "自动执行中止",
            CombatBugReportIssueKind.TurnSetupFailure => "回合准备选牌失败",
            CombatBugReportIssueKind.TurnSetupStateMismatch => "回合准备计划与实机状态不一致",
            CombatBugReportIssueKind.UnexpectedChoice => "未计划的选牌",
            CombatBugReportIssueKind.ChoiceExecutionFailure => "选牌页面执行失败",
            CombatBugReportIssueKind.RelicReplayMismatch => "遗物标注回放与选中状态不一致",
            CombatBugReportIssueKind.TimeoutFailure => "等待游戏状态超时",
            CombatBugReportIssueKind.UnsupportedCombatSemantic => "存在尚未支持的战斗语义",
            CombatBugReportIssueKind.FullAutoStoppedAfterWorseRecalculation => "全自动因重算后战损上升而暂停",
            CombatBugReportIssueKind.FullAutoStoppedAtDeathTurn => "全自动因预计本回合死亡而暂停",
            CombatBugReportIssueKind.FullAutoStoppedAtLiveRiskDeath => "全自动因结束回合实机复核将死亡而暂停",
            CombatBugReportIssueKind.FullAutoStoppedAtLiveRiskWorsening => "全自动因结束回合实机复核战损上升而暂停",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
}
