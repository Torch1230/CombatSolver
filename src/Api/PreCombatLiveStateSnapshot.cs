using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace CombatSolver.Api;

internal sealed record PreCombatModSnapshot(
    string Id,
    string Version,
    string SourcePath);

internal sealed record PreCombatLiveStateSnapshot(
    RunState LiveRun,
    byte[] SerializedRun,
    string StateToken,
    string CharacterId,
    string Seed,
    int ActIndex,
    string GameRoot,
    string UserDataRoot,
    IReadOnlyList<PreCombatModSnapshot> Mods)
{
    public static PreCombatLiveStateSnapshot Capture(RunState run)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("Pre-combat requests must be captured on the game main thread.");
        if (!ReferenceEquals(RunManager.Instance.DebugOnlyGetState(), run))
            throw new InvalidOperationException("The supplied run is not the active run.");
        if (!RunManager.Instance.IsSingleplayerOrFakeMultiplayer || run.Players.Count != 1)
            throw new NotSupportedException("Pre-combat forecasts currently support single-player runs only.");
        if (CombatManager.Instance.IsInProgress)
            throw new NotSupportedException("Pre-combat forecasts cannot be captured while a combat is active.");

        SerializableRun save = CaptureNormalizedSave();
        byte[] serialized = PreCombatRunSerialization.SerializeNormalized(save);
        string stateToken = ComputeToken(serialized, run);
        string characterId = run.Players[0].Character.Id.Entry;
        string seed = run.Rng.StringSeed;
        string executablePath = OS.GetExecutablePath();
        string gameRoot = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("The game executable has no parent directory.");
        string userDataRoot = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "SlayTheSpire2");
        PreCombatModSnapshot[] mods = ModManager.GetLoadedMods()
            .Where(static mod => mod.manifest?.id != null)
            .Select(static mod =>
            {
                string id = mod.manifest!.id!;
                string version = mod.manifest.version ?? "unknown";
                return new PreCombatModSnapshot(
                    id,
                    version,
                    PreCombatForecastWorker.ResolvePinnedModSource(id, version, mod.path));
            })
            .OrderBy(static mod => mod.Id, StringComparer.Ordinal)
            .ToArray();

        return new PreCombatLiveStateSnapshot(
            run,
            serialized,
            stateToken,
            characterId,
            seed,
            run.CurrentActIndex,
            gameRoot,
            userDataRoot,
            mods);
    }

    public static string CaptureToken(RunState run)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("The live-state token must be captured on the game main thread.");
        if (!ReferenceEquals(RunManager.Instance.DebugOnlyGetState(), run))
            throw new InvalidOperationException("The supplied run is not the active run.");
        SerializableRun save = CaptureNormalizedSave();
        byte[] serialized = PreCombatRunSerialization.SerializeNormalized(save);
        return ComputeToken(serialized, run);
    }

    private static SerializableRun CaptureNormalizedSave()
    {
        return RunManager.Instance.ToSave(null);
    }

    private static string ComputeToken(byte[] serialized, RunState run)
    {
        string roomIdentity = run.CurrentRoom == null
            ? "none"
            : $"{run.CurrentRoom.RoomType}:{run.CurrentRoom.ModelId}";
        byte[] suffix = Encoding.UTF8.GetBytes(
            $"|act_floor={run.ActFloor}|room_count={run.CurrentRoomCount}|room={roomIdentity}|combat={CombatManager.Instance.IsInProgress}");
        byte[] payload = new byte[serialized.Length + suffix.Length];
        serialized.CopyTo(payload, 0);
        suffix.CopyTo(payload, serialized.Length);
        return Convert.ToHexString(SHA256.HashData(payload));
    }
}
