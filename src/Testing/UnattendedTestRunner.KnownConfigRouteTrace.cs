using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed class KnownRouteTraceConfig
{
    public string Sample { get; init; } = "KnownConfig";
    public string? EncounterId { get; init; }
    public int? MinTurn { get; init; }
    public int? MaxTurn { get; init; }
    public int? RetentionStep { get; init; }
    public KnownRouteTraceActionConfig[] Actions { get; init; } = [];
}

internal sealed class KnownRouteTraceActionConfig
{
    public string Kind { get; init; } = "play";
    public string CardId { get; init; } = string.Empty;
    public int UpgradeLevel { get; init; } = -1;
    public int Occurrence { get; init; }
    public int TargetIndex { get; init; } = -1;
    public uint? TargetCombatId { get; init; }
    public int PotionSlot { get; init; } = -1;
    public string PotionId { get; init; } = string.Empty;
    public KnownRouteTraceChoiceItemConfig[] Choice { get; init; } = [];
}

internal sealed class KnownRouteTraceChoiceItemConfig
{
    public string CardId { get; init; } = string.Empty;
    public int UpgradeLevel { get; init; } = 0;
    public int Occurrence { get; init; }
}

internal sealed partial class UnattendedTestRunner
{
    // Config-driven known-route trace. The route comes from a request JSON file and is treated
    // as a diagnostic needle only: it is strictly replayed on the restored root to freeze state
    // fingerprints, never injected as a search prefix, candidate, score or policy override.
    private async Task<int> RunKnownConfigRouteTraceAsync(CombatState combat, Player player)
    {
        if (string.IsNullOrWhiteSpace(_request.KnownRouteTraceConfigPath))
            throw new InvalidOperationException("Known-config 路径诊断缺少配置文件路径。");
        KnownRouteTraceConfig config = JsonSerializer.Deserialize<KnownRouteTraceConfig>(
            File.ReadAllText(_request.KnownRouteTraceConfigPath), UnattendedTestFiles.JsonOptions)
            ?? throw new InvalidOperationException("Known-config 路径诊断配置为空。");
        if (config.Actions.Length == 0)
            throw new InvalidOperationException("Known-config 路径诊断没有可冻结的动作。");
        if (config.EncounterId is { } encounterId
            && !string.Equals(combat.Encounter?.Id.Entry, encounterId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Known-config 路径诊断要求遭遇 {encounterId}，实际 {combat.Encounter?.Id.Entry}。");
        int rootTurn = player.PlayerCombatState?.TurnNumber
            ?? throw new InvalidOperationException("Known-config 路径诊断要求可操作的玩家回合根。");
        if (config.MinTurn is { } minTurn && rootTurn < minTurn
            || config.MaxTurn is { } maxTurn && rootTurn > maxTurn)
            throw new InvalidOperationException(
                $"Known-config 路径诊断要求回合 [{config.MinTurn ?? 0},{config.MaxTurn ?? 9999}]，实际 {rootTurn}。");

        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SearchPolicySnapshot capturedPolicy = SolverController.CaptureSearchPolicy(
            SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat),
            BattleDamageTracker.Observe(combat), capturedPolicy);
        List<PlanAction> actions = [];
        List<SimulationSnapshot> owned = [];
        List<KnownRoutePrefix> prefixes = [];
        ContinuationStamp liveBefore = ContinuationStamp.CaptureLive(combat);
        MoveStateSnapshot actualBefore = CaptureActual(combat, player, combat.Enemies[0]);
        try
        {
            using (SimulationNotificationIsolation.Enter())
            {
                SimulationSnapshot initial = driver.ReplayDiagnosticPrefix(actions);
                owned.Add(initial);
                AssertSnapshotEqual(CaptureSimulated(initial.Simulator,
                    (SimulatedCombatState)initial.Simulator.State.CombatState, player, combat.Enemies[0]),
                    actualBefore, config.Sample, "InitialRoot");
                SimulationSnapshot parent = initial;
                foreach (KnownRouteTraceActionConfig step in config.Actions)
                {
                    EnsureWithinDeadline();
                    if (parent.PlayerDead || parent.AllEnemiesDead || !parent.Simulator.IsInProgress)
                        throw new InvalidOperationException(
                            $"Known-config 路线在步骤 {actions.Count} 之前已终局，不能把跳过的后缀计为可冻结前缀。");
                    PlanAction action = BuildKnownConfigAction(step, parent, player);
                    actions.Add(action);
                    parent.ReleaseSimulator();
                    parent = driver.ReplayDiagnosticPrefix(actions);
                    owned.Add(parent);
                    if (parent.HasRisk || parent.PlayerDead || parent.BoundaryReason != SearchBoundaryReason.None)
                        throw new InvalidOperationException(
                            $"Known-config 路线步骤 {actions.Count}（{DescribeKnownConfigStep(step)}）未通过严格模拟回放。");
                    prefixes.Add(FreezeKnownRoutePrefix(action, CaptureSimulated(parent.Simulator,
                        (SimulatedCombatState)parent.Simulator.State.CombatState, player, combat.Enemies[0]),
                        parent));
                    Entry.Logger.Info($"[CombatSolver/Test] KNOWN_CONFIG_PREFIX step={actions.Count} " +
                        $"state={parent.StateKey.First:x16}/{parent.StateKey.Second:x16} " +
                        $"score={parent.Score:F0} hp={parent.PlayerHp} proj_hp={parent.ProjectedPlayerHp} " +
                        $"enemy={parent.EnemyHp} loss={parent.CumulativePlayerHpLost} " +
                        $"setup={parent.PersistentBuffValue} latent={parent.LatentSetupValue} " +
                        $"turn={parent.Turn} shuffles={parent.ShufflesCrossed}");
                }
            }
        }
        finally
        {
            foreach (SimulationSnapshot snapshot in owned)
                snapshot.ReleaseSimulator();
            if (ContinuationStamp.CaptureLive(combat) != liveBefore)
                throw new InvalidOperationException("Known-config 路线回放修改了实战根。");
        }
        if (config.RetentionStep is { } retentionStep && (retentionStep < 1 || retentionStep > prefixes.Count))
            throw new InvalidOperationException("Known-config 路径诊断的保留边界越界。");
        return await RunKnownRoutePathTraceAsync(combat, player, prefixes, config.Sample,
            "known_config_route_path", observedRetentionStep: config.RetentionStep);
    }

    private static string DescribeKnownConfigStep(KnownRouteTraceActionConfig step)
    {
        string choice = step.Choice.Length == 0
            ? string.Empty
            : $" choice=[{string.Join(',', step.Choice.Select(item =>
                $"{item.CardId}+{item.UpgradeLevel}#{item.Occurrence}"))}]";
        return step.Kind switch
        {
            "endTurn" => "endTurn",
            "potion" => $"potion {step.PotionId}@slot{step.PotionSlot}",
            _ => $"play {step.CardId}#{step.Occurrence}{choice}",
        };
    }

    private PlanAction BuildKnownConfigAction(KnownRouteTraceActionConfig step,
        SimulationSnapshot parent, Player player)
    {
        int turn = parent.Turn;
        if (string.Equals(step.Kind, "endTurn", StringComparison.OrdinalIgnoreCase))
            return new PlanAction(PlanActionKind.EndTurn, turn);
        if (string.Equals(step.Kind, "potion", StringComparison.OrdinalIgnoreCase))
        {
            if (step.PotionSlot < 0 || string.IsNullOrWhiteSpace(step.PotionId))
                throw new InvalidOperationException("Known-config 用药步骤需要 PotionSlot 与 PotionId。");
            return new PlanAction(PlanActionKind.UsePotion, turn, PotionSlot: step.PotionSlot,
                PotionId: step.PotionId, TargetIndex: step.TargetIndex, TargetCombatId: step.TargetCombatId);
        }
        if (!string.Equals(step.Kind, "play", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Known-config 路线包含未知动作类型 {step.Kind}。");
        SimPlayerCombatState playerState = parent.Simulator.State.GetPlayerCombatState(player);
        PlanAction descriptor = new(PlanActionKind.PlayCard, turn,
            CardId: step.CardId, CardOccurrence: step.Occurrence);
        PredictedCard card = CombatBeamSolver.FindCardForReplay(playerState.Hand.Cards, descriptor)
            ?? throw new InvalidOperationException(
                $"Known-config 路线找不到手牌 {step.CardId}#{step.Occurrence}（升级 {step.UpgradeLevel}）：hand=" +
                string.Join(',', playerState.Hand.Cards.Select(candidate =>
                    $"{candidate.Preview.Id.Entry}+{candidate.Preview.CurrentUpgradeLevel}")));
        if (step.UpgradeLevel >= 0 && card.Preview.CurrentUpgradeLevel != step.UpgradeLevel)
            throw new InvalidOperationException(
                $"Known-config 手牌 {step.CardId}#{step.Occurrence} 升级 {card.Preview.CurrentUpgradeLevel} 与配置 {step.UpgradeLevel} 不符。");
        string stateKey = CardChoiceSupport.ChoiceCardKey(card);
        int stateOccurrence = playerState.Hand.Cards
            .TakeWhile(candidate => !ReferenceEquals(candidate, card))
            .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == stateKey);
        PlanCardChoice? choice = step.Choice.Length == 0
            ? null : BuildKnownConfigChoice(step, parent, player);
        int replayCount = Math.Max(0, card.Preview.GetEnchantedReplayCount());
        return new PlanAction(PlanActionKind.PlayCard, turn,
            CardId: step.CardId, CardOccurrence: step.Occurrence,
            TargetIndex: step.TargetIndex, TargetCombatId: step.TargetCombatId,
            CardStateKey: stateKey, CardStateOccurrence: stateOccurrence,
            Choice: choice, ReplayCount: replayCount);
    }

    private PlanCardChoice BuildKnownConfigChoice(
        KnownRouteTraceActionConfig step, SimulationSnapshot parent, Player player)
    {
        PlanAction descriptor = new(PlanActionKind.PlayCard, parent.Turn,
            CardId: step.CardId, CardOccurrence: step.Occurrence);
        PredictedCard playedCard = CombatBeamSolver.FindCardForReplay(
            parent.Simulator.State.GetPlayerCombatState(player).Hand.Cards, descriptor)
            ?? throw new InvalidOperationException(
                $"Known-config 选择步骤找不到出牌 {step.CardId}#{step.Occurrence}。");
        CardChoiceSpec spec = CardChoiceSupport.GetSpec(parent.Simulator, playedCard)
            ?? throw new InvalidOperationException(
                $"Known-config 出牌 {step.CardId} 在当前父状态没有登记的选牌需求。");
        if (step.Choice.Length < spec.MinCount || step.Choice.Length > spec.MaxCount)
            throw new InvalidOperationException(
                $"Known-config {step.CardId} 选择数量 {step.Choice.Length} 越界 [{spec.MinCount},{spec.MaxCount}]。");
        List<PlanCardToken> tokens = [];
        foreach (KnownRouteTraceChoiceItemConfig item in step.Choice)
        {
            PredictedCard selected = spec.SourceCards
                .Where(card => string.Equals(card.Preview.Id.Entry, item.CardId, StringComparison.Ordinal)
                    && card.Preview.CurrentUpgradeLevel == item.UpgradeLevel)
                .Skip(item.Occurrence)
                .FirstOrDefault()
                ?? throw new InvalidPlannedChoiceBranchException(
                    $"Known-config 选择项 {item.CardId}+{item.UpgradeLevel}#{item.Occurrence} 不在 {step.CardId} 的候选内：" +
                    $"候选={string.Join(',', spec.Options.Select(CardChoiceSupport.ChoiceCardKey))}。");
            string tokenKey = CardChoiceSupport.ChoiceCardKey(selected);
            int sourceOccurrence = parent.Simulator.State.GetPlayerCombatState(player).Hand.Cards
                .TakeWhile(candidate => !ReferenceEquals(candidate, selected))
                .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == tokenKey);
            int optionOccurrence = ReferenceEquals(spec.SourceCards, spec.Options)
                ? sourceOccurrence
                : spec.Options.TakeWhile(candidate => !ReferenceEquals(candidate, selected))
                    .Count(candidate => CardChoiceSupport.ChoiceCardKey(candidate) == tokenKey);
            tokens.Add(new PlanCardToken(selected.Preview.Id.Entry, selected.Preview.CurrentUpgradeLevel,
                tokenKey, sourceOccurrence, optionOccurrence, selected.Preview.Id.Entry));
        }
        return new PlanCardChoice(spec.Effect, spec.SourcePile, tokens, ContextId: spec.ContextId);
    }
}
