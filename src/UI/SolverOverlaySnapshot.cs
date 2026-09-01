using MegaCrit.Sts2.Core.Models;

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
    int ReplayCount);

internal sealed record SolverOverlayTurnSnapshot(
    int Turn,
    IReadOnlyList<string> TurnStartChoices,
    IReadOnlyList<SolverOverlayActionSnapshot> Actions,
    SolverOverlayActionSnapshot? EndTurnAction,
    int HpLoss,
    int EnergyLeft,
    bool CombatEnded);

internal sealed record SolverOverlaySnapshot(
    int StartTurnNumber,
    string StatusText,
    SolverOverlayTone StatusTone,
    string SummaryText,
    int ProjectedBattlePotionCount,
    int ProjectedBattleHpLost,
    string HpOutcomeText,
    bool OnlyDeathRoutesFound,
    IReadOnlyList<SolverOverlayTurnSnapshot> Turns,
    string DetailsText,
    bool HasRisk)
{
    public static SolverOverlaySnapshot Capture(SolverResult result, bool unexpectedReplan)
        => Capture(result, result.StartTurnNumber, unexpectedReplan, pendingTurnSetup: false);

    public static SolverOverlaySnapshot CapturePendingTurnSetup(
        SolverResult result,
        int turn,
        bool unexpectedReplan)
        => Capture(result, turn, unexpectedReplan, pendingTurnSetup: true);

    private static SolverOverlaySnapshot Capture(
        SolverResult result,
        int startTurnNumber,
        bool unexpectedReplan,
        bool pendingTurnSetup)
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
        bool goalOverridden = result.LongTermGoalOutcome
            is LongTermGoalOutcome.NoCompliantRoute
            or LongTermGoalOutcome.CompliantRouteWouldDie;
        SolverOverlayTone statusTone = pendingTurnSetup
            ? SolverOverlayTone.Accent
            : goalOverridden
            ? SolverOverlayTone.Danger
            : result.ProjectedBattleHpLossIncrease > 0
            ? SolverOverlayTone.Danger
            : result.WasReused
                ? SolverOverlayTone.Success
                : SolverOverlayTone.Accent;
        // The HP increase still shows in hpOutcomeText, so the status line is free to carry the stronger news:
        // the player switched something on and the route did the opposite.
        string statusText = pendingTurnSetup
            ? "等待回合开始选择"
            : goalOverridden
            ? result.LongTermGoalOutcome == LongTermGoalOutcome.CompliantRouteWouldDie
                ? "强制跨战斗收益未执行：留牌会死"
                : "强制跨战斗收益未执行：无可行留牌路线"
            : result.ProjectedBattleHpLossIncrease > 0
            ? "重算后战损上升"
            : result.WasReused
                ? "方案已复用"
                : "方案就绪";
        string summaryText = result.CombatEndedTurn == startTurnNumber
            ? $"[color={SolverUiTokens.Palette.SuccessHex}]本回合结束战斗  │  {confidence}[/color]"
            : $"[color={SolverUiTokens.Palette.TextSecondaryHex}]预计路线 [b]{searchedTurns}[/b] 回合  │  {confidence}[/color]";
        string hpOutcomeText = result.ProjectedBattleHpLost > 0
            ? result.ProjectedBattleHpLossIncrease > 0
                ? $"本局扣血  已 {result.BattleHpLostSoFar}    预计 {result.ProjectedBattleHpLost} HP    重算增加 {result.ProjectedBattleHpLossIncrease} HP"
                : $"本局扣血  已 {result.BattleHpLostSoFar}    预计 {result.ProjectedBattleHpLost} HP"
            : "本局扣血  0 HP";

        SolverOverlayTurnSnapshot[] turns = Enumerable.Range(0, searchedTurns)
            .Select(index => CaptureTurn(result, startTurnNumber + index))
            .ToArray();
        return new SolverOverlaySnapshot(
            startTurnNumber,
            statusText,
            statusTone,
            summaryText,
            result.ProjectedBattlePotionCount,
            result.ProjectedBattleHpLost,
            hpOutcomeText,
            result.OnlyDeathRoutesFound,
            turns,
            BuildDetails(result, startTurnNumber, unmirrored, compensated, unexpectedReplan),
            hasRisk);
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
        PlanAction? endTurn = result.BestNode.Actions
            .LastOrDefault(action => action.Turn == turn && action.Kind == PlanActionKind.EndTurn);
        return new SolverOverlayTurnSnapshot(
            turn,
            turnStartChoices,
            actions,
            endTurn == null ? null : CaptureAction(endTurn, []),
            result.HpLostByTurn.GetValueOrDefault(turn),
            result.EnergyLeftByTurn.GetValueOrDefault(turn),
            result.CombatEndedTurn == turn);
    }

    private static SolverOverlayActionSnapshot CaptureAction(
        PlanAction action,
        IReadOnlyList<string> kills)
    {
        string? choiceText = action.Choice == null
            ? null
            : $"选 {string.Join("、", action.Choice.Cards.Select(card => card.Title))}";
        string[] relicLabels = action.RelicEffects?
            .Select(effect => effect.RelicTitle + effect.Summary)
            .ToArray()
            ?? [];
        string[] copiedKills = kills.ToArray();
        string tooltip = SolverResult.Describe(action)
            + (copiedKills.Length > 0 ? $"，击杀 {string.Join("、", copiedKills)}" : string.Empty);
        return new SolverOverlayActionSnapshot(
            action.Kind == PlanActionKind.EndTurn ? "直接结束" : action.ActionTitle,
            action.TargetName,
            choiceText,
            relicLabels,
            copiedKills,
            tooltip,
            ResolveVisualKind(action),
            action.ReplayCount);
    }

    private static SolverOverlayActionVisualKind ResolveVisualKind(PlanAction action)
    {
        if (action.Kind == PlanActionKind.UsePotion)
            return SolverOverlayActionVisualKind.Potion;
        CardModel? card = ModelDb.AllCards.FirstOrDefault(candidate =>
            candidate.Id.Entry.Equals(action.CardId, StringComparison.Ordinal));
        return card?.Type.ToString() switch
        {
            "Attack" => SolverOverlayActionVisualKind.Attack,
            "Skill" => SolverOverlayActionVisualKind.Skill,
            "Power" => SolverOverlayActionVisualKind.Power,
            "Curse" or "Status" => SolverOverlayActionVisualKind.Negative,
            _ => SolverOverlayActionVisualKind.Other,
        };
    }

    private static string FormatTurnStartChoice(PlanCardChoice choice)
    {
        string source = choice.SourceId switch
        {
            "TOOLS_OF_THE_TRADE_POWER" => "必备工具",
            "TYRANNY_POWER" => "暴政",
            "ENTROPY_POWER" => "熵",
            "TOASTY_MITTENS" => "烤手套",
            "CHOICES_PARADOX" => "选择悖论",
            _ => choice.SourceId,
        };
        string effect = choice.Effect switch
        {
            PlanChoiceEffect.Discard => "弃",
            PlanChoiceEffect.Exhaust => "耗尽",
            PlanChoiceEffect.Transform => "变换",
            PlanChoiceEffect.GenerateToHand => "选择",
            _ => choice.Effect.ToString(),
        };
        return $"{source}：{effect} {string.Join('、', choice.Cards.Select(card => card.Title))}";
    }

    private static string FormatWorldLine(RouteWorldLine line)
    {
        string label = line.PotionCount == 0
            ? "不交药"
            : string.Join('+', line.PotionTitles);
        string body = $"{label} 掉 {line.HpLost}{(line.Won ? string.Empty : "（未确认胜利）")}";
        return line.IsSelected ? $"[b]{body}[/b]" : body;
    }

    private static string FormatGoals(LongTermGoals goals)
    {
        if (goals == LongTermGoals.None)
            return "无";
        List<string> parts = [];
        if (goals.HasFlag(LongTermGoals.FatalKillBonus))
            parts.Add("斩杀收尾");
        if (goals.HasFlag(LongTermGoals.PersistentGrowth))
            parts.Add("成长牌");
        return string.Join('、', parts);
    }

    private static string BuildDetails(
        SolverResult result,
        int displayedTurn,
        IReadOnlyList<string> unmirrored,
        IReadOnlyList<string> compensated,
        bool unexpectedReplan)
    {
        string searchDetails = result.WasReused
            ? $"[color={SolverUiTokens.Palette.TextMutedHex}]搜索[/color]  跨回合状态一致，复用既有路线  │  本回合 0 节点"
            : $"[color={SolverUiTokens.Palette.TextMutedHex}]搜索[/color]  {(result.DeepSearchTriggered ? "深化" : "快速")}  │  {result.ExpandedNodes} 节点  │  置换剪枝 {result.TranspositionBranchesPruned}  │  {result.TotalSearchElapsed.TotalMilliseconds:F0} ms";
        List<string> detailLines =
        [
            searchDetails,
            $"[color={SolverUiTokens.Palette.TextMutedHex}]运行[/color]  后台分配 {FormatMegabytes(result.TotalWorkerAllocatedBytes)} MB  │  GC {result.TotalGen0Collections}/{result.TotalGen1Collections}/{result.TotalGen2Collections}  │  暂停 {result.TotalGcPauseDuration.TotalMilliseconds:F1} ms  │  延迟探测 {result.StandPatProbes}",
            $"[color={SolverUiTokens.Palette.TextMutedHex}]战损[/color]  本局已发生 {result.BattleHpLostSoFar}  │  路线未来卖血 {result.FutureSoldHp}  │  本局累计卖血 {result.SoldHp}/{result.SoldHpThreshold}",
            $"[color={SolverUiTokens.Palette.TextMutedHex}]药水[/color]  本局已喝 {result.BattlePotionsUsedSoFar} 瓶  │  路线还要用 {result.PotionCount} 瓶  │  预计省血 {result.PotionHpSaved}/{result.PotionHpRequired} HP  │  门槛淘汰 {result.PotionBranchesRejected}",
            $"[color={SolverUiTokens.Palette.TextMutedHex}]防守[/color]  本回合最高可起防 {result.MaxBlockByTurn.GetValueOrDefault(displayedTurn)}  │  路线实际起防 {result.ActualBlockByTurn.GetValueOrDefault(displayedTurn)}  │  卖血 {result.SoldHpByTurn.GetValueOrDefault(displayedTurn)}",
            $"[color={SolverUiTokens.Palette.TextMutedHex}]边界[/color]  {BoundaryText(result.BoundaryReason)}  │  停止洗牌分支 {result.ShuffleBranchesPruned}  │  不可避免战损 {result.UnavoidableHpLost}",
        ];
        if (result.WorldLines.Count > 1)
        {
            detailLines.Insert(
                4,
                $"[color={SolverUiTokens.Palette.TextMutedHex}]世界线[/color]  " +
                string.Join("  │  ", result.WorldLines.Select(FormatWorldLine)) +
                "  （同一次搜索的候选，非精确重算）");
        }
        if (result.LongTermGoalOutcome != LongTermGoalOutcome.Off)
        {
            detailLines.Insert(4, result.LongTermGoalOutcome switch
            {
                LongTermGoalOutcome.CompliantRouteWouldDie =>
                    $"[color={SolverUiTokens.Palette.DangerHex}][b]强制跨战斗收益未执行[/b][/color]  " +
                    $"留着这些牌的 {result.CompliantRouteCount} 条路线全部会死亡，已改为正常使用",
                LongTermGoalOutcome.NoCompliantRoute =>
                    $"[color={SolverUiTokens.Palette.DangerHex}][b]强制跨战斗收益未执行[/b][/color]  " +
                    "候选路线里没有「要么斩杀、要么留牌」的走法，已改为正常使用",
                _ =>
                    $"[color={SolverUiTokens.Palette.TextMutedHex}]跨战斗收益[/color]  " +
                    $"已拿到 {FormatGoals(result.BankedLongTermGoals)}  │  " +
                    $"代价 {result.LongTermGoalHpPrice} HP" +
                    (result.LongTermGoalPotionPrice > 0
                        ? $" + {result.LongTermGoalPotionPrice} 瓶药水"
                        : string.Empty),
            });
        }
        if (result.TheftPolicy is { } theftPolicy)
        {
            detailLines.Insert(
                3,
                $"[color={SolverUiTokens.Palette.TextMutedHex}]偷窃策略[/color]  " +
                $"{(theftPolicy == SolverTheftPolicy.PreserveResources ? "保牌/保钱" : "放走")}  │  " +
                $"路线结束时未追回 {result.OutstandingStolenResource}");
        }
        if (unmirrored.Count > 0)
            detailLines.Add($"[color={SolverUiTokens.Palette.DangerHex}][b]未镜像[/b][/color]  {JoinCoverage(unmirrored)}");
        if (compensated.Count > 0)
            detailLines.Add($"[color={SolverUiTokens.Palette.SuccessHex}][b]求解器已补偿[/b][/color]  {JoinCoverage(compensated)}");
        if (result.Forecast.ApproximationDetails.Count > 0)
        {
            detailLines.Add(
                $"[color={SolverUiTokens.Palette.WarningHex}][b]近似预测[/b][/color]  " +
                JoinCoverage(result.Forecast.ApproximationDetails));
        }
        if (result.ProjectedBattleHpLossIncrease > 0)
        {
            detailLines.Add(
                $"[color={SolverUiTokens.Palette.DangerHex}][b]路线失配[/b][/color]  " +
                $"完整路线原预计 {result.PreviousProjectedBattleHpLost} HP，重算后 {result.ProjectedBattleHpLost} HP，" +
                $"增加 {result.ProjectedBattleHpLossIncrease} HP。" +
                SolverUiTokens.BugReportUploadInstruction);
        }
        if (unexpectedReplan)
        {
            detailLines.Add(
                $"[color={SolverUiTokens.Palette.DangerHex}][b]计划外重算[/b][/color]  " +
                "求解器执行后的预测状态与实机不一致。" +
                SolverUiTokens.BugReportUploadInstruction);
        }
        return string.Join('\n', detailLines);
    }

    private static string ConfidenceText(SolverResult result)
    {
        if (result.Forecast.HasUnsupportedIntent
            || result.Snapshot.PredictionGaps.Any(gap => !gap.Compensated))
        {
            return "低可信度";
        }
        return result.Forecast.IsExactForModeledDamage ? "高可信度" : "中等可信度";
    }

    private static string BoundaryText(SearchBoundaryReason reason) => reason switch
    {
        SearchBoundaryReason.Shuffle => "下次洗牌",
        SearchBoundaryReason.NoCards => "无牌可抽",
        SearchBoundaryReason.UnsupportedEffect => "未镜像死亡效果",
        SearchBoundaryReason.DynamicResolution => "真实结算后重搜",
        SearchBoundaryReason.PendingChoice => "展开选牌",
        SearchBoundaryReason.EventDefeat => "事件挑战失败",
        SearchBoundaryReason.NodeLimit => "节点上限",
        SearchBoundaryReason.TurnLimit => "回合上限",
        SearchBoundaryReason.TimeLimit => "时间预算",
        _ => "战斗结束",
    };

    private static string JoinCoverage(IReadOnlyList<string> entries)
    {
        const int maxVisible = 3;
        string text = string.Join("、", entries.Take(maxVisible));
        return entries.Count > maxVisible ? $"{text} 等 {entries.Count} 项（完整列表见日志）" : text;
    }

    private static string FormatMegabytes(long bytes)
        => (bytes / (1024d * 1024d)).ToString("F1");
}
