using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCombatDiagnosticLogAsync(CombatState combat, Player player)
    {
        foreach (RelicModel relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (PowerModel power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray())
            await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await CreatureCmd.SetMaxHp(player.Creature, 80);
        await CreatureCmd.SetCurrentHp(player.Creature, 60);
        await SetBlockAsync(player.Creature, 0);
        var enemy = combat.Enemies.First();
        await SetBlockAsync(enemy, 0);
        await CreatureCmd.SetCurrentHp(enemy, 100);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        SetEnergy(player, 3);
        CombatRootSnapshot root = CombatRootSnapshot.Capture(combat);
        SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(SolverSettings.Capture(), combat, false, null);
        CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat), BattleDamageTracker.Observe(combat), policy);
        PlanAction action = new(PlanActionKind.PlayCard, root.StartTurnNumber, CardId: "STRIKE_IRONCLAD", TargetCombatId: enemy.CombatId);
        SimulationSnapshot initial = InvokeForcedTerminalReplay(driver, [], null, root.StartTurnNumber, null);
        SimulationSnapshot selected = InvokeForcedTerminalReplay(driver, [action], initial, root.StartTurnNumber, null);
        SearchNode parent = ForcedTerminalAnnotationNode(initial, null, null);
        SearchNode node = ForcedTerminalAnnotationNode(selected, parent, action);
        SearchReplayEvidence evidence = new(node);
        ActionRelicTriggerRecorder recorder = new();
        SimulationSnapshot replay = (SimulationSnapshot)InvokeForcedTerminalMethod(driver, "Replay",
            [new PlanAction[] { action }, null, root.StartTurnNumber, 0, recorder, null, evidence,
                null, null, null, null, null, true, true, null])!;
        try
        {
            if (selected.StateKey != replay.StateKey || evidence.FirstScalarDifference != null)
                throw new InvalidOperationException("正常逐动作取证改变了回放结果或产生虚假差异。");
            AssertSnapshotEqual(CaptureSimulated(selected.Simulator, (SimulatedCombatState)selected.Simulator.State.CombatState, player, enemy),
                CaptureSimulated(replay.Simulator, (SimulatedCombatState)replay.Simulator.State.CombatState, player, enemy), "DiagnosticLog", "ReplayObserverEquivalence");
            if (!recorder.HealthChanges.Any(change => change.Kind == "damage" && change.Source.Id == "STRIKE_IRONCLAD" && change.Before > change.After))
                throw new InvalidOperationException("伤害来源未被记录。");
            evidence.Publish(policy.Diagnostics, "fixture_selected_route", recorder);
            SearchReplayEvidence mismatch = new(node);
            replay.Simulator.State.GetCreature(player.Creature).CurrentHp -= 5;
            InvokeForcedTerminalMethod(driver, "LogAnnotatedReplayState", [replay.Simulator, action, 0, replay.Turn, mismatch]);
            if (mismatch.FirstScalarDifference != 0 || mismatch.FirstActualState == null)
                throw new InvalidOperationException("未捕获首个 5 HP 分叉点及完整回放状态。");
            mismatch.Publish(policy.Diagnostics, "fixture_injected_hp_difference", recorder);
            bool failed = false;
            try
            {
                InvokeForcedTerminalMethod(driver, "ReplayAction",
                    [parent, action with { CardId = "MISSING_DIAGNOSTIC_FIXTURE_CARD" }, null, null, null]);
            }
            catch (SearchTransitionException) { failed = true; }
            if (!failed) throw new InvalidOperationException("失败分支取证吞掉了搜索异常。");
        }
        finally { initial.ReleaseSimulator(); selected.ReleaseSimulator(); replay.ReleaseSimulator(); }

        CombatPredictionSimulator health = root.ForkSimulator();
        ActionRelicTriggerRecorder healthRecorder = new();
        healthRecorder.BeginAction(0);
        health.ActionRelicTriggers = healthRecorder;
        health.Damage(player.Creature, 7, ValueProp.Unblockable | ValueProp.Unpowered, enemy);
        health.Heal(player.Creature, 3);
        MoveStateSnapshot predicted = CaptureSimulated(health, (SimulatedCombatState)health.State.CombatState, player, enemy);
        await CreatureCmd.Damage(new ThrowingPlayerChoiceContext(), player.Creature, 7, ValueProp.Unblockable | ValueProp.Unpowered, enemy);
        await CreatureCmd.Heal(player.Creature, 3);
        AssertSnapshotEqual(predicted, CaptureActual(combat, player, enemy), "DiagnosticLog", "DamageHealNative");
        if (!healthRecorder.HealthChanges.Any(change => change.Kind == "heal" && change.After - change.Before == 3))
            throw new InvalidOperationException("治疗数值未被记录。");

        await CreatureCmd.SetCurrentHp(enemy, 1);
        CombatRootSnapshot shortRoot = CombatRootSnapshot.Capture(combat);
        SolverDisplayNames names = SolverDisplayNames.Capture(combat);
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(combat);
        SolverResult shortResult = await Task.Run(() => new CombatBeamSolver(shortRoot, names, damage, policy,
            searchProfile: SolverSearchProfile.Default with { BeamWidth = 4, MaxExpandedNodes = 100, SoftTimeBudgetMilliseconds = 1000 },
            potionPolicyOverride: SolverPotionPolicy.Disabled).Solve());
        if (shortResult.CombatEndedTurn == null)
            throw new InvalidOperationException("正式搜索未完成单步终局物化。");
        _completedChecks.Add("DiagnosticLog:ProductionFinalMaterialization");

        string path = await CombatBugReportExporter.ExportCurrentAsync(playerDescription: "日志合同验证，不上传");
        using ZipArchive archive = ZipFile.OpenRead(path);
        AssertBugReportArchive(archive, "current", "solver_only");
        if (archive.Entries.Any(entry => entry.FullName.Contains("godot", StringComparison.OrdinalIgnoreCase))
            || archive.GetEntry("diagnostics/logs/index.json") == null)
            throw new InvalidDataException("问题包没有使用独立日志。");
        var entries = archive.Entries.Where(entry => entry.FullName.StartsWith("diagnostics/logs/combat/")).ToArray();
        string text = string.Join('\n', entries.Select(entry => { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }));
        if (!text.Contains("fixture_injected_hp_difference") || !text.Contains("firstScalarDifference")
            || !text.Contains("FAILED_CANDIDATE") || !text.Contains("MISSING_DIAGNOSTIC_FIXTURE_CARD")
            || !text.Contains("selected_route"))
            throw new InvalidDataException("提交包缺少已冻结的首个差异证据。");
        _completedChecks.Add("DiagnosticLog:SelectedReplayEquivalence+FirstHpDifference+DamageHealNative+IndependentArchive");
        _completedChecks.Add("DiagnosticLogArchive:" + path);
        await AssertRejectedPredictionReportAsync(combat);
    }

    private static void UnreviewedHealPostfix() { }

    private async Task AssertRejectedPredictionReportAsync(CombatState combat)
    {
        const string rejection = "Unreviewed native Heal patch combination.";
        const string description = "诊断包兼容性合同";
        string reportId = Guid.NewGuid().ToString("N");
        MethodInfo heal = AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.Heal),
            [typeof(Creature), typeof(decimal), typeof(bool)])!;
        var harmony = new Harmony("combatsolver.testing.diagnostic-log-rejected-prediction");
        harmony.Patch(heal, postfix: new HarmonyMethod(
            AccessTools.Method(typeof(UnattendedTestRunner), nameof(UnreviewedHealPostfix))));
        try
        {
            try
            {
                ContinuationStamp.CaptureLive(combat);
                throw new InvalidOperationException("Unknown Heal postfix passed the live search boundary.");
            }
            catch (PredictionUnsupportedException error) when (error.Message == rejection) { }

            string path = await CombatBugReportExporter.ExportCurrentAsync(
                playerDescription: description, submissionId: reportId);
            using (ZipArchive archive = ZipFile.OpenRead(path))
            using (Stream stream = archive.GetEntry("diagnostics/combat-state.json")!.Open())
            using (JsonDocument state = JsonDocument.Parse(stream))
            {
                JsonElement root = state.RootElement;
                if (!root.GetProperty("combatActive").GetBoolean()
                    || root.GetProperty("exactContinuationState").ValueKind != JsonValueKind.Null
                    || root.GetProperty("exactContinuationStateRejection").GetString() != rejection
                    || archive.GetEntry("diagnostics/logs/index.json") == null)
                    throw new InvalidDataException("Rejected prediction report lost its diagnostics or claimed an exact snapshot.");
            }
            long bytes = new FileInfo(path).Length;
            using var handler = new FakeUploadHandler(HttpStatusCode.Created,
                $"{{\"id\":\"{reportId}\",\"sizeBytes\":{bytes}}}");
            using var client = new System.Net.Http.HttpClient(handler);
            var receipt = await CombatBugReportUploader.UploadForTestingAsync(
                client, path, description, string.Empty, reportId);
            if (receipt.ReportId != reportId || receipt.SizeBytes != bytes
                || !handler.SawMultipartReport || !handler.SawMetadata || !handler.SawSubmissionId)
                throw new InvalidOperationException("The uploader did not send the rejected-prediction diagnostic archive.");
        }
        finally { harmony.Unpatch(heal, HarmonyPatchType.All, harmony.Id); }
        _completedChecks.Add("DiagnosticLog:UnknownHealPostfixRejected+ArchiveDiagnostics+MultipartReceipt");
    }
}
