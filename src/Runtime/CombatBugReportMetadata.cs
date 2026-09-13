using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Platform;

namespace CombatSolver;

internal sealed record BugReportMonster(string Id, string Name);
internal sealed record BugReportCombat(
    string SessionId, string? EncounterId, string? EncounterName, string? EncounterType,
    string? CharacterId, string? CharacterName, BugReportMonster[] Monsters,
    int Ascension, int Act, int Floor, string ControlMode);

// Freeze display labels and stable model IDs on the main thread, including defeated enemies.
internal static class CombatBugReportMetadata
{
    internal const string EntryPath = "report.json";
    internal const int MaximumBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static BugReportCombat CaptureCombat(CombatState state, string sessionId, BugReportCombat? previous)
    {
        var player = LocalContext.GetMe(state);
        BugReportMonster[] monsters = (previous?.Monsters ?? [])
            .Concat(state.Enemies.Where(enemy => enemy.Monster != null)
                .Select(enemy => new BugReportMonster(enemy.Monster!.Id.Entry, enemy.Name)))
            .DistinctBy(monster => monster.Id).OrderBy(monster => monster.Id, StringComparer.Ordinal).ToArray();
        return new BugReportCombat(sessionId, state.Encounter?.Id.Entry,
            state.Encounter?.Title.GetFormattedText(), state.Encounter?.RoomType.ToString(),
            player?.Character.Id.Entry, player?.Character.Title.GetFormattedText(), monsters,
            state.RunState.AscensionLevel, state.RunState.CurrentActIndex + 1, state.RunState.TotalFloor,
            SolverController.ControlModeForBugReport);
    }

    internal static string Serialize(string reportId, string? description, BugReportCombat? combat,
        CombatBugReportClassificationSnapshot classification, ManualProjectionComparison? comparison)
    {
        if (!Guid.TryParseExact(reportId, "N", out _)) throw new InvalidDataException("问题包编号格式无效。");
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            reportId,
            createdAt = DateTimeOffset.UtcNow,
            modVersion = CombatBugReportDescription.CurrentModVersion,
            gameVersion = ReleaseInfoManager.Instance.ReleaseInfo?.Version,
            playerDescription = description ?? string.Empty,
            submitterName = PlatformUtil.GetPlayerNameRaw(PlatformUtil.PrimaryPlatform,
                PlatformUtil.GetLocalPlayerId(PlatformUtil.PrimaryPlatform)),
            runStatistics = RunStatistics.Snapshot,
            combat,
            classification,
            comparisonKind = comparison == null ? null : "projected_comparison",
            manualProjectionComparison = comparison,
            hpLoss = comparison == null ? null : new
            {
                kind = "manual_projection",
                before = comparison.PreviousProjectedBattleHpLost,
                after = comparison.CurrentProjectedBattleHpLost,
                reduction = comparison.PreviousProjectedBattleHpLost - comparison.CurrentProjectedBattleHpLost,
            },
            bundle = new { schemaVersion = 2, checkpointIndexPath = "replay/checkpoint.json" },
        }, Json);
    }
}
