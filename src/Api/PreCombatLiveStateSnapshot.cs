using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
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
    string AccountDataRoot,
    byte[] SolverSettings,
    IReadOnlyList<PreCombatModSnapshot> Mods,
    SolverSettingsData SolverSettingsData,
    SolverSettingsSnapshot SolverSettingsSnapshot,
    PreCombatEligibilityResult MandatoryLiveHostEligibility,
    PreCombatEligibilityResult Eligibility,
    bool HasLiveSidecar,
    int LiveActFloor,
    int NextRoomId,
    Sts2SimSpoilsMapVerificationDto? SpoilsMapVerification,
    IReadOnlyList<(int Slot, string PotionId)> EffectivePotions,
    byte[]? SerializedPlanningRun = null)
{
    internal PreCombatLiveStateSnapshot WithPlanningRun(
        SerializableRun plannedRun,
        RunState plannedState,
        int targetActFloor,
        int targetMapColumn,
        bool isSecondBoss)
    {
        bool hadPreFinishedRoom = plannedRun.PreFinishedRoom is not null;
        var typeInfo = JsonSerializationUtility.GetTypeInfo<SerializableRun>();
        var root = JsonSerializer.SerializeToNode(plannedRun, typeInfo)!.AsObject();
        PreCombatRunSerialization.NormalizeRoot(root);
        var ownedCopy = JsonSerializer.Deserialize(root, typeInfo)
            ?? throw new InvalidDataException("The planning snapshot is empty.");
        PreCombatEligibilityResult planningEligibility = PreCombatSts2SimEligibility.AssessPlanningRun(
            LiveRun,
            MandatoryLiveHostEligibility,
            Eligibility.Fingerprint,
            ownedCopy,
            plannedState,
            hadPreFinishedRoom,
            targetActFloor,
            targetMapColumn,
            isSecondBoss);
        var plannedPlayer = ownedCopy.Players[0];
        IReadOnlyList<(int Slot, string PotionId)> plannedPotions =
            PreCombatSts2SimPolicyMapper.CaptureEffectivePotions(plannedPlayer);
        string plannedCharacterId = plannedPlayer.CharacterId?.Entry ?? CharacterId;
        string plannedSeed = ownedCopy.SerializableRng?.Seed ?? Seed;
        return this with
        {
            CharacterId = plannedCharacterId,
            Seed = plannedSeed,
            ActIndex = ownedCopy.CurrentActIndex,
            Eligibility = planningEligibility,
            HasLiveSidecar = false,
            SpoilsMapVerification = null,
            EffectivePotions = plannedPotions,
            SerializedPlanningRun = PreCombatRunSerialization.SerializeNormalized(ownedCopy),
        };
    }

    public static string? TryDescribeDirectIneligibility(RunState run)
    {
        if (!NGame.IsMainThread())
            return null;
        if (!ReferenceEquals(RunManager.Instance.DebugOnlyGetState(), run))
            return null;
        return PreCombatSts2SimEligibility.TryDescribeDirectIneligibility(run);
    }

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
        SolverSettingsData settingsData = global::CombatSolver.SolverSettings.Current;
        SolverSettingsSnapshot settingsSnapshot = global::CombatSolver.SolverSettings.Capture();
        byte[] serializedSettings = global::CombatSolver.SolverSettings.CaptureSerializedSettings();
        PreCombatEligibilityResult mandatoryLiveHostEligibility = PreCombatSts2SimEligibility.AssessMandatoryLiveHost(run);
        PreCombatEligibilityResult eligibility = mandatoryLiveHostEligibility.IsEligible
            ? PreCombatSts2SimEligibility.AssessLiveHostAndRun(run)
            : mandatoryLiveHostEligibility;
        Player player = LocalContext.GetMe(run) ?? run.Players[0];
        Sts2SimSpoilsMapVerificationDto spoilsVerification = CaptureSpoilsMapVerification(player);
        string stateToken = ComputeToken(serialized, run, serializedSettings, eligibility, spoilsVerification);
        string characterId = player.Character.Id.Entry;
        string seed = run.Rng.StringSeed;
        string executablePath = OS.GetExecutablePath();
        string gameRoot = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("The game executable has no parent directory.");
        string userDataRoot = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "SlayTheSpire2");
        PreCombatModSnapshot[] mods = ModManager.GetLoadedMods()
            .Where(static mod => mod.manifest?.id != null)
            .Select(static mod => new PreCombatModSnapshot(
                mod.manifest!.id!,
                mod.manifest.version ?? "unknown",
                mod.path))
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
            ProjectSettings.GlobalizePath(UserDataPathProvider.GetAccountScopedBasePath(null)),
            serializedSettings,
            mods,
            settingsData,
            settingsSnapshot,
            mandatoryLiveHostEligibility,
            eligibility,
            HasLiveSidecar: true,
            LiveActFloor: run.ActFloor,
            NextRoomId: run.NextRoomId,
            SpoilsMapVerification: spoilsVerification,
            EffectivePotions: PreCombatSts2SimPolicyMapper.CaptureEffectivePotions(player));
    }

    public static string CaptureToken(RunState run)
    {
        if (!NGame.IsMainThread())
            throw new InvalidOperationException("The live-state token must be captured on the main thread.");
        if (!ReferenceEquals(RunManager.Instance.DebugOnlyGetState(), run))
            throw new InvalidOperationException("The supplied run is not the active run.");
        SerializableRun save = CaptureNormalizedSave();
        byte[] serialized = PreCombatRunSerialization.SerializeNormalized(save);
        byte[] serializedSettings = global::CombatSolver.SolverSettings.CaptureSerializedSettings();
        PreCombatEligibilityResult eligibility = PreCombatSts2SimEligibility.AssessLiveHostAndRun(run);
        Player player = LocalContext.GetMe(run) ?? run.Players[0];
        Sts2SimSpoilsMapVerificationDto spoilsVerification = CaptureSpoilsMapVerification(player);
        return ComputeToken(serialized, run, serializedSettings, eligibility, spoilsVerification);
    }

    private static SerializableRun CaptureNormalizedSave()
    {
        return RunManager.Instance.ToSave(null);
    }

    private static Sts2SimSpoilsMapVerificationDto CaptureSpoilsMapVerification(Player player)
    {
        IReadOnlyList<CardModel> deck = player.Deck.Cards;
        var cardIds = new string[deck.Count];
        var spoilsEntries = new List<Sts2SimSpoilsMapEntryDto>();
        for (int index = 0; index < deck.Count; index++)
        {
            CardModel card = deck[index];
            cardIds[index] = card.Id.ToString();
            if (card is SpoilsMap spoils)
            {
                Sts2SimMapCoordDto? coord = spoils.SpoilsCoord is { } c
                    ? new Sts2SimMapCoordDto(c.col, c.row)
                    : null;
                spoilsEntries.Add(new Sts2SimSpoilsMapEntryDto(index, coord));
            }
        }
        return new Sts2SimSpoilsMapVerificationDto(deck.Count, cardIds, spoilsEntries);
    }

    private static string ComputeToken(
        byte[] serialized,
        RunState run,
        byte[] serializedSettings,
        PreCombatEligibilityResult eligibility,
        Sts2SimSpoilsMapVerificationDto spoilsVerification)
    {
        string roomIdentity = run.CurrentRoom == null
            ? "none"
            : $"{run.CurrentRoom.RoomType}:{run.CurrentRoom.ModelId}";
        string canonicalSpoils = string.Join(
            ';',
            spoilsVerification.Entries.Select(static entry =>
                entry.Coord is { } coord
                    ? $"{entry.DeckIndex}:{coord.Col},{coord.Row}"
                    : $"{entry.DeckIndex}:null"));
        byte[] suffix = Encoding.UTF8.GetBytes(
            $"|act_floor={run.ActFloor}|room_count={run.CurrentRoomCount}|room={roomIdentity}|combat={CombatManager.Instance.IsInProgress}" +
            $"|solver_settings={Convert.ToHexString(SHA256.HashData(serializedSettings))}" +
            $"|next_room_id={run.NextRoomId}|spoils={canonicalSpoils}|eligibility={eligibility.Fingerprint}");
        byte[] payload = new byte[serialized.Length + suffix.Length];
        serialized.CopyTo(payload, 0);
        suffix.CopyTo(payload, serialized.Length);
        return Convert.ToHexString(SHA256.HashData(payload));
    }
}
