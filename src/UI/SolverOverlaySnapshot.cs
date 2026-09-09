using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace CombatSolver;

internal enum SolverOverlayTone
{
    Accent,
    Success,
    Danger,
}

internal enum SolverOverlayActionVisualKind
{
    Other,
    Attack,
    Skill,
    Power,
    Negative,
    Potion,
}

internal sealed record SolverOverlayActionSnapshot(
    string Title,
    string TargetName,
    string? ChoiceText,
    IReadOnlyList<string> RelicLabels,
    IReadOnlyList<string> Kills,
    string Tooltip,
    SolverOverlayActionVisualKind VisualKind,
    int ReplayCount,
    SolverActionTextIdentity? TextIdentity = null);

internal sealed record SolverOverlayTurnSnapshot(
    int Turn,
    IReadOnlyList<string> TurnStartChoices,
    IReadOnlyList<SolverOverlayActionSnapshot> Actions,
    SolverOverlayActionSnapshot? EndTurnAction,
    int? EnemyHpDamageLost,
    int HpLoss,
    int HpRecovered,
    int EnergyLeft,
    bool CombatEnded);

internal sealed record SolverOverlaySnapshot(
    int StartTurnNumber,
    string StatusText,
    SolverOverlayTone StatusTone,
    string SummaryText,
    string ReviewSummaryText,
    int ProjectedBattlePotionCount,
    int ProjectedBattleHpLost,
    bool ProjectedBattleHpLossKnown,
    string HpOutcomeText,
    int RouteHpRecovered,
    int RoutePostCombatRelicHeal,
    bool OnlyDeathRoutesFound,
    IReadOnlyList<SolverOverlayTurnSnapshot> Turns,
    string DetailsText,
    bool HasRisk,
    string? SearchLimitWarningText)
{
    public SearchObjectiveOutcome? Objective { get; init; }
    public string LocalizedSummaryText => Objective is { } objective
        ? SummaryText + "\n" + SearchObjectiveText.Summary(objective) : SummaryText;
    public string LocalizedDetailsText => Objective is { } objective
        ? SearchObjectiveText.Details(objective) + "\n" + DetailsText : DetailsText;

    public static SolverOverlaySnapshot Capture(SolverResult result, bool unexpectedReplan)
        => CaptureWithReviewedWorldlines(result, unexpectedReplan, reviewedWorldlinesTotal: 0);

    internal static SolverOverlaySnapshot CaptureWithReviewedWorldlines(
        SolverResult result,
        bool unexpectedReplan,
        long reviewedWorldlinesTotal)
        => Capture(
            result,
            result.StartTurnNumber,
            unexpectedReplan,
            pendingTurnSetup: false,
            reviewedWorldlinesTotal);

    public static SolverOverlaySnapshot CapturePendingTurnSetup(
        SolverResult result,
        int turn,
        bool unexpectedReplan,
        long reviewedWorldlinesTotal = 0)
        => Capture(result, turn, unexpectedReplan, pendingTurnSetup: true, reviewedWorldlinesTotal);


    public static SolverOverlaySnapshot CaptureCurrentTurn(SolverCurrentTurnPreview preview)
    {
        SolverOverlayActionSnapshot[] actions = preview.Actions
            .Where(action => action.IsExecutable)
            .Select(action => CaptureAction(action, []))
            .ToArray();
        PlanAction? endTurn = preview.Actions
            .LastOrDefault(action => action.Kind == PlanActionKind.EndTurn);
        SolverOverlayTurnSnapshot currentTurn = new(
            preview.Turn,
            TurnStartChoices: [],
            actions,
            endTurn == null ? null : CaptureAction(endTurn, [], actions.Length == 0),
            EnemyHpDamageLost: preview.EnemyHpLost,
            preview.HpLost,
            preview.HpRecovered,
            preview.EnergyLeft,
            preview.CombatEnded);
        SolverOverlayTurnSnapshot[] turns = preview.FrontierTurns is { Count: > 0 } frontier
            ? frontier.Select(BuildOverlayTurn).ToArray()
            : [currentTurn];
        int furthestTurn = preview.FrontierTurns is { Count: > 0 } frontierTurns
            ? frontierTurns[^1].Turn
            : preview.Turn;
        bool combatEnded = turns.Any(turn => turn.CombatEnded);
        int projectedBattleHpLost = combatEnded
            ? turns.Sum(turn => turn.HpLoss)
            : 0;
        string outcome = combatEnded
            ? SolverText.Format($"预计战损 {projectedBattleHpLost} HP")
            : SolverText.Get("预计战损 未知");
        return new SolverOverlaySnapshot(
            preview.Turn,
            SolverText.Format($"搜索前沿预览 · 已规划至第 {furthestTurn} 回合"),
            SolverOverlayTone.Accent,
            SolverText.Format($"[color={SolverUiTokens.Palette.TextSecondaryHex}]搜索前沿预览，尚未验证完整胜利  │  {outcome}[/color]"),
            string.Empty,
            preview.Actions.Count(action => action.Kind == PlanActionKind.UsePotion),
            projectedBattleHpLost,
            combatEnded,
            outcome,
            RouteHpRecovered: turns.Sum(turn => turn.HpRecovered),
            RoutePostCombatRelicHeal: 0,
            OnlyDeathRoutesFound: false,
            turns,
            DetailsText: string.Empty,
            HasRisk: false,
            SearchLimitWarningText: null);
    }

    public static SolverOverlaySnapshot CaptureSpeculativeRoute(
        SolverSpeculativeRoutePreview preview)
    {
        if (preview.Turns.Count == 0)
            throw new InvalidOperationException("动态候选路线没有可展示回合。");
        SolverOverlayTurnSnapshot[] turns = preview.Turns
            .Select(BuildOverlayTurn)
            .ToArray();
        int furthestTurn = turns[^1].Turn;
        string hpOutcomeText = preview.CombatEnded
            ? SolverText.Format($"预计战损 {preview.ProjectedBattleHpLost} HP")
            : SolverText.Get("预计战损 未知");
        return new SolverOverlaySnapshot(
            preview.StartTurnNumber,
            SolverText.Format($"求解器当前考虑 · 已演化至第 {furthestTurn} 回合"),
            SolverOverlayTone.Accent,
            SolverText.Format($"[color={SolverUiTokens.Palette.WarningHex}]求解器当前考虑，尚未验证  │  ") +
            SolverText.Format($"路线可能继续变化或回跳[/color]"),
            string.Empty,
            preview.ProjectedBattlePotionCount,
            preview.ProjectedBattleHpLost,
            preview.CombatEnded,
            hpOutcomeText,
            RouteHpRecovered: turns.Sum(turn => turn.HpRecovered),
            RoutePostCombatRelicHeal: 0,
            preview.OnlyDeathRoutesFound,
            turns,
            DetailsText: string.Empty,
            HasRisk: preview.HasRisk,
            SearchLimitWarningText: null);
    }

    private static SolverOverlayTurnSnapshot BuildOverlayTurn(SolverFrontierTurn frontier)
    {
        SolverOverlayActionSnapshot[] frontierActions = frontier.Actions
            .Where(action => action.IsExecutable)
            .Select(action => CaptureAction(action, []))
            .ToArray();
        PlanAction? frontierEndTurn = frontier.Actions
            .LastOrDefault(action => action.Kind == PlanActionKind.EndTurn);
        return new SolverOverlayTurnSnapshot(
            frontier.Turn,
            TurnStartChoices: [],
            frontierActions,
            frontierEndTurn == null ? null : CaptureAction(frontierEndTurn, [], frontierActions.Length == 0),
            EnemyHpDamageLost: frontier.EnemyHpLost,
            frontier.HpLost,
            frontier.HpRecovered,
            frontier.EnergyLeft,
            frontier.CombatEnded);
    }

    private static SolverOverlaySnapshot Capture(
        SolverResult result,
        int startTurnNumber,
        bool unexpectedReplan,
        bool pendingTurnSetup,
        long reviewedWorldlinesTotal)
    {
        int searchedTurns = result.StartTurnNumber + result.SearchedTurns - startTurnNumber;
        if (searchedTurns <= 0)
        {
            throw new InvalidOperationException(
                $"既有路线不包含等待选择的第 {startTurnNumber} 回合。");
        }
        IReadOnlyList<string> unmirrored = result.UnmirroredDetails().ToArray();
        IReadOnlyList<string> compensated = result.CompensatedDetails().ToArray();
        bool hasRisk = !result.Forecast.IsExactForModeledDamage
            || unmirrored.Count > 0
            || compensated.Count > 0;
        string confidence = ConfidenceText(result);
        SolverOverlayTone statusTone = pendingTurnSetup
            ? SolverOverlayTone.Accent
            : result.ProjectedBattleHpLossIncrease > 0
            ? SolverOverlayTone.Danger
            : result.WasReused || result.WasRestoredFromCache
                ? SolverOverlayTone.Success
                : SolverOverlayTone.Accent;
        string statusText = pendingTurnSetup
            ? SolverText.Get("等待回合开始选择")
            : result.ProjectedBattleHpLossIncrease > 0
            ? SolverText.Get("重算后战损上升")
            : result.WasRestoredFromCache
                ? SolverText.Get("路线已恢复")
            : result.WasReused
                ? SolverText.Get("方案已复用")
                : SolverText.Get("方案就绪");
        string summaryText = result.CombatEndedTurn == startTurnNumber
            ? SolverText.Format($"[color={SolverUiTokens.Palette.SuccessHex}]本回合结束战斗  │  {confidence}[/color]")
            : SolverText.Format($"[color={SolverUiTokens.Palette.TextSecondaryHex}]预计路线 [b]{searchedTurns}[/b] 回合  │  {confidence}[/color]");
        string reviewSummaryText = result.WasRestoredFromCache
            ? SolverText.Get("已恢复本场战斗记录的路线")
            : result.WasReused
            ? SolverText.Format($"路线已复用，共查阅了 {reviewedWorldlinesTotal:N0} 条世界线")
            : SolverText.Format($"花费了 {result.TotalSearchElapsed.TotalSeconds:F1} 秒，共查阅了 {reviewedWorldlinesTotal:N0} 条世界线");
        bool projectedBattleHpLossKnown = result.CombatEndedTurn.HasValue;
        int routeHpRecovered = result.HpRecoveredByTurn.Values.Sum();
        string hpOutcomeText = !projectedBattleHpLossKnown
            ? SolverText.Get("预计战损 未知")
            : result.ProjectedBattleHpLost > 0
                ? result.ProjectedBattleHpLossIncrease > 0
                    ? SolverText.Format($"本局扣血  已 {result.BattleHpLostSoFar}    预计 {result.ProjectedBattleHpLost} HP    重算增加 {result.ProjectedBattleHpLossIncrease} HP")
                    : SolverText.Format($"本局扣血  已 {result.BattleHpLostSoFar}    预计 {result.ProjectedBattleHpLost} HP")
                : SolverText.Get("本局扣血  0 HP");

        SolverOverlayTurnSnapshot[] turns = Enumerable.Range(0, searchedTurns)
            .Select(index => CaptureTurn(result, startTurnNumber + index))
            .ToArray();
        return new SolverOverlaySnapshot(
            startTurnNumber,
            statusText,
            statusTone,
            summaryText,
            reviewSummaryText,
            result.ProjectedBattlePotionCount,
            result.ProjectedBattleHpLost,
            projectedBattleHpLossKnown,
            hpOutcomeText,
            routeHpRecovered,
            result.PostCombatRelicHeal,
            result.OnlyDeathRoutesFound,
            turns,
            BuildDetails(result, startTurnNumber, unmirrored, compensated, unexpectedReplan),
            hasRisk,
            BuildSearchLimitWarning(result.BoundaryReason))
        {
            Objective = result.Snapshot.Objective,
        };
    }

    private static SolverOverlayTurnSnapshot CaptureTurn(SolverResult result, int turn)
    {
        IEnumerable<PlanCardChoice> initialSetupChoices = turn == result.StartTurnNumber && !result.WasReused
            ? result.TurnSetupChoices
            : [];
        IEnumerable<PlanCardChoice> continuedTurnChoices = result.BestNode.Actions
            .FirstOrDefault(action => action.Turn == turn - 1 && action.TurnStartChoices is { Count: > 0 })
            ?.TurnStartChoices
            ?? [];
        IReadOnlyList<string> turnStartChoices = initialSetupChoices
            .Concat(continuedTurnChoices)
            .Where(choice => choice.Effect != PlanChoiceEffect.ApplyKnowledgeCurse)
            .Select(FormatTurnStartChoice)
            .ToArray();
        SolverOverlayActionSnapshot[] actions = result.BestNode.Actions
            .Select((action, actionIndex) => (Action: action, Index: actionIndex))
            .Where(item => item.Action.Turn == turn && item.Action.IsExecutable)
            .Select(item =>
            {
                result.KillsAfterAction.TryGetValue(item.Index, out IReadOnlyList<string>? kills);
                return CaptureAction(item.Action, kills ?? []);
            })
            .ToArray();
        int endTurnIndex = -1;
        for (int index = result.BestNode.Actions.Count - 1; index >= 0; index--)
        {
            if (result.BestNode.Actions[index].Turn == turn
                && result.BestNode.Actions[index].Kind == PlanActionKind.EndTurn)
            {
                endTurnIndex = index;
                break;
            }
        }
        PlanAction? endTurn = endTurnIndex >= 0
            ? result.BestNode.Actions[endTurnIndex]
            : null;
        IReadOnlyList<string> endTurnKills = endTurnIndex >= 0
            && result.KillsAfterAction.TryGetValue(endTurnIndex, out IReadOnlyList<string>? recordedKills)
                ? recordedKills
                : [];
        int? enemyHpLost = result.EnemyHpLostByTurn.TryGetValue(turn, out int materializedEnemyHpLost)
            ? materializedEnemyHpLost
            : null;
        return new SolverOverlayTurnSnapshot(
            turn,
            turnStartChoices,
            actions,
            endTurn == null ? null : CaptureAction(endTurn, endTurnKills, actions.Length == 0),
            enemyHpLost,
            result.HpLostByTurn.GetValueOrDefault(turn),
            result.HpRecoveredByTurn.GetValueOrDefault(turn),
            result.EnergyLeftByTurn.GetValueOrDefault(turn),
            result.CombatEndedTurn == turn);
    }

    internal static SolverOverlayActionSnapshot CaptureAction(
        PlanAction action,
        IReadOnlyList<string> kills,
        bool isDirectEndTurn = false)
    {
        SolverOverlayActionSnapshot snapshot = new(
            action.ActionTitle,
            action.TargetName,
            null,
            [],
            kills.ToArray(),
            "",
            ResolveVisualKind(action),
            action.ReplayCount,
            new SolverActionTextIdentity(
                action.CardId, action.CardUpgradeLevel, action.PotionId,
                action.Kind == PlanActionKind.EndTurn, isDirectEndTurn,
                action.GetActionChoicesInExecutionOrder().Select(choice =>
                    (IReadOnlyList<SolverCardTextIdentity>)choice.Cards.Select(card =>
                        new SolverCardTextIdentity(card.CardId, card.UpgradeLevel, card.Title)).ToArray()).ToArray(),
                action.RelicEffects?.Select(effect => new SolverRelicTextIdentity(effect.RelicId, effect.RelicTitle, effect.Summary)).ToArray() ?? []));
        return SolverActionTextIdentity.Refresh(snapshot);
    }

    // ModelDb.AllCards 是惰性 LINQ 查询，每次枚举都重跑 SelectMany/Distinct 并分配整套 HashSet；
    // 进度刷新会对路线里每个动作各查一次。按牌 Id 建一次只读索引即可（ModelDb 在 Init 后不变）。
    private static Dictionary<string, SolverOverlayActionVisualKind>? _visualKindsByCardId;

    private static SolverOverlayActionVisualKind ResolveVisualKind(PlanAction action)
    {
        if (action.Kind == PlanActionKind.UsePotion)
            return SolverOverlayActionVisualKind.Potion;
        Dictionary<string, SolverOverlayActionVisualKind> index =
            _visualKindsByCardId ??= BuildVisualKindIndex();
        return action.CardId is { } cardId && index.TryGetValue(cardId, out SolverOverlayActionVisualKind kind)
            ? kind
            : SolverOverlayActionVisualKind.Other;
    }

    private static Dictionary<string, SolverOverlayActionVisualKind> BuildVisualKindIndex()
    {
        Dictionary<string, SolverOverlayActionVisualKind> index = new(StringComparer.Ordinal);
        foreach (CardModel card in ModelDb.AllCards)
        {
            // 与原实现 FirstOrDefault 一致：同名 Id 只保留首个匹配。
            index.TryAdd(card.Id.Entry, card.Type.ToString() switch
            {
                "Attack" => SolverOverlayActionVisualKind.Attack,
                "Skill" => SolverOverlayActionVisualKind.Skill,
                "Power" => SolverOverlayActionVisualKind.Power,
                "Curse" or "Status" => SolverOverlayActionVisualKind.Negative,
                _ => SolverOverlayActionVisualKind.Other,
            });
        }
        return index;
    }

    private static string FormatTurnStartChoice(PlanCardChoice choice)
    {
        string source = choice.SourceId switch
        {
            "TOOLS_OF_THE_TRADE_POWER" => ModelDb.Power<ToolsOfTheTradePower>().Title.GetFormattedText(),
            "TYRANNY_POWER" => ModelDb.Power<TyrannyPower>().Title.GetFormattedText(),
            "ENTROPY_POWER" => ModelDb.Power<EntropyPower>().Title.GetFormattedText(),
            "TOASTY_MITTENS" => ModelDb.Relic<ToastyMittens>().Title.GetFormattedText(),
            "CHOICES_PARADOX" => ModelDb.Relic<ChoicesParadox>().Title.GetFormattedText(),
            _ => choice.SourceId,
        };
        string effect = choice.Effect switch
        {
            PlanChoiceEffect.Discard => SolverText.Get("弃"),
            PlanChoiceEffect.Exhaust => SolverText.Get("耗尽"),
            PlanChoiceEffect.Transform => SolverText.Get("变换"),
            PlanChoiceEffect.GenerateToHand or PlanChoiceEffect.ModDefined => SolverText.Get("选择"),
            _ => choice.Effect.ToString(),
        };
        return $"{source}：{effect} {string.Join('、', choice.Cards.Select(card => SolverUiModelNames.Card(card.CardId, card.UpgradeLevel, card.Title)))}";
    }

    private static string BuildDetails(
        SolverResult result,
        int displayedTurn,
        IReadOnlyList<string> unmirrored,
        IReadOnlyList<string> compensated,
        bool unexpectedReplan)
    {
        string searchDetails = result.WasRestoredFromCache
            ? SolverText.Format($"[color={SolverUiTokens.Palette.TextMutedHex}]搜索[/color]  战斗状态一致，恢复已记录路线  │  本次 0 节点")
            : result.WasReused
            ? SolverText.Format($"[color={SolverUiTokens.Palette.TextMutedHex}]搜索[/color]  跨回合状态一致，复用既有路线  │  本回合 0 节点")
            : SolverText.Format($"[color={SolverUiTokens.Palette.TextMutedHex}]搜索[/color]  {(result.DeepSearchTriggered ? SolverText.Get("深化") : SolverText.Get("快速"))}  │  {result.ExpandedNodes} 节点  │  置换剪枝 {result.TranspositionBranchesPruned}  │  {result.TotalSearchElapsed.TotalMilliseconds:F0} ms");
        List<string> detailLines =
        [
            searchDetails,
            SolverText.Format($"[color={SolverUiTokens.Palette.TextMutedHex}]运行[/color]  后台分配 {FormatMegabytes(result.TotalWorkerAllocatedBytes)} MB  │  GC {result.TotalGen0Collections}/{result.TotalGen1Collections}/{result.TotalGen2Collections}  │  暂停 {result.TotalGcPauseDuration.TotalMilliseconds:F1} ms  │  延迟探测 {result.StandPatProbes}"),
            SolverText.Format($"[color={SolverUiTokens.Palette.TextMutedHex}]战损[/color]  本局已发生 {result.BattleHpLostSoFar}  │  路线未来卖血 {result.FutureSoldHp}  │  本局累计卖血 {result.SoldHp}/{result.SoldHpThreshold}"),
            SolverText.Format($"[color={SolverUiTokens.Palette.TextMutedHex}]药水[/color]  本局已喝 {result.BattlePotionsUsedSoFar} 瓶  │  路线还要用 {result.PotionCount} 瓶  │  预计省血 {result.PotionHpSaved}/{result.PotionHpRequired} HP  │  门槛淘汰 {result.PotionBranchesRejected}"),
            SolverText.Format($"[color={SolverUiTokens.Palette.TextMutedHex}]防守[/color]  本回合最高可起防 {result.MaxBlockByTurn.GetValueOrDefault(displayedTurn)}  │  路线实际起防 {result.ActualBlockByTurn.GetValueOrDefault(displayedTurn)}  │  卖血 {result.SoldHpByTurn.GetValueOrDefault(displayedTurn)}"),
            SolverText.Format($"[color={SolverUiTokens.Palette.TextMutedHex}]边界[/color]  {BoundaryText(result.BoundaryReason)}  │  停止洗牌分支 {result.ShuffleBranchesPruned}  │  不可避免战损 {result.UnavoidableHpLost}"),
        ];
        if (result.TheftPolicy is { } theftPolicy)
        {
            detailLines.Insert(
                3,
                SolverText.Format($"[color={SolverUiTokens.Palette.TextMutedHex}]偷窃策略[/color]  ") +
                $"{(theftPolicy == SolverTheftPolicy.PreserveResources ? SolverText.Get("保牌/保钱") : SolverText.Get("放走"))}  │  " +
                SolverText.Format($"路线结束时未追回 {result.OutstandingStolenResource}"));
        }
        if (unmirrored.Count > 0)
            detailLines.Add(SolverText.Format($"[color={SolverUiTokens.Palette.DangerHex}][b]未镜像[/b][/color]  {JoinCoverage(unmirrored)}"));
        if (compensated.Count > 0)
            detailLines.Add(SolverText.Format($"[color={SolverUiTokens.Palette.SuccessHex}][b]求解器已补偿[/b][/color]  {JoinCoverage(compensated)}"));
        if (result.Forecast.ApproximationDetails.Count > 0)
        {
            detailLines.Add(
                SolverText.Format($"[color={SolverUiTokens.Palette.WarningHex}][b]近似预测[/b][/color]  ") +
                JoinCoverage(result.Forecast.ApproximationDetails));
        }
        if (result.ProjectedBattleHpLossIncrease > 0)
        {
            detailLines.Add(
                SolverText.Format($"[color={SolverUiTokens.Palette.DangerHex}][b]路线失配[/b][/color]  ") +
                SolverText.Format($"完整路线原预计 {result.PreviousProjectedBattleHpLost} HP，重算后 {result.ProjectedBattleHpLost} HP，") +
                SolverText.Format($"增加 {result.ProjectedBattleHpLossIncrease} HP。") +
                SolverUiTokens.BugReportUploadInstruction);
        }
        if (unexpectedReplan)
        {
            detailLines.Add(
                SolverText.Format($"[color={SolverUiTokens.Palette.DangerHex}][b]计划外重算[/b][/color]  ") +
                SolverText.Get("求解器执行后的预测状态与实机不一致。") +
                SolverUiTokens.BugReportUploadInstruction);
        }
        return string.Join('\n', detailLines);
    }

    private static string ConfidenceText(SolverResult result)
    {
        if (result.Forecast.HasUnsupportedIntent
            || result.Snapshot.PredictionGaps.Any(gap => !gap.Compensated))
        {
            return SolverText.Get("低可信度");
        }
        return result.Forecast.IsExactForModeledDamage ? SolverText.Get("高可信度") : SolverText.Get("中等可信度");
    }

    internal static string? BuildSearchLimitWarning(SearchBoundaryReason reason) => reason switch
    {
        SearchBoundaryReason.TimeLimit
            => SolverText.Get("计算尚未彻底穷尽，已达到当前设置的【时间上限】；现展示目前找到的最佳路线。若想探索更优世界线，可在 设置 > 性能 中提高上限后重新计算。"),
        SearchBoundaryReason.NodeLimit
            => SolverText.Get("计算尚未彻底穷尽，已达到当前设置的【节点上限】；现展示目前找到的最佳路线。若想探索更优世界线，可在 设置 > 性能 中提高上限后重新计算。"),
        _ => null,
    };

    private static string BoundaryText(SearchBoundaryReason reason) => reason switch
    {
        SearchBoundaryReason.Shuffle => SolverText.Get("下次洗牌"),
        SearchBoundaryReason.NoCards => SolverText.Get("无牌可抽"),
        SearchBoundaryReason.UnsupportedEffect => SolverText.Get("未镜像死亡效果"),
        SearchBoundaryReason.DynamicResolution => SolverText.Get("真实结算后重搜"),
        SearchBoundaryReason.PendingChoice => SolverText.Get("展开选牌"),
        SearchBoundaryReason.EventDefeat => SolverText.Get("事件挑战失败"),
        SearchBoundaryReason.NodeLimit => SolverText.Get("节点上限"),
        SearchBoundaryReason.TurnLimit => SolverText.Get("回合上限"),
        SearchBoundaryReason.TimeLimit => SolverText.Get("时间预算"),
        _ => SolverText.Get("战斗结束"),
    };

    private static string JoinCoverage(IReadOnlyList<string> entries)
    {
        const int maxVisible = 3;
        string text = string.Join("、", entries.Take(maxVisible));
        return entries.Count > maxVisible ? SolverText.Format($"{text} 等 {entries.Count} 项（完整列表见日志）") : text;
    }

    private static string FormatMegabytes(long bytes)
        => (bytes / (1024d * 1024d)).ToString("F1");
}
