using System.Text.Json;
using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using CombatSolver.Engine.Common;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private MultiplayerProbeInput? _multiplayerProbe;

    private sealed record MultiplayerProbeInput
    {
        public required string Mode { get; init; }
        public required int PlayerCount { get; init; }
        public required int Seat { get; init; }
        public required ushort Port { get; init; }
        public required string CoordinationDirectory { get; init; }
        public required string ExpectedGameVersion { get; init; }
        public bool VerifyRootProjection { get; init; }
        public bool VerifyActionDifferential { get; init; }
        public bool VerifyRoundDifferential { get; init; }
        public bool VerifyEnemyPowerScaling { get; init; }
        public bool VerifySearch { get; init; }
        public bool VerifyControllerSearch { get; init; }
        public bool VerifyControllerAutomaticCalculation { get; init; }
        public bool VerifyControllerFullAuto { get; init; }
        public bool VerifyControllerDeploy { get; init; }
        public bool VerifyControllerTeammateDrift { get; init; }
        public bool VerifyControllerTeammateKillsTarget { get; init; }
        public bool VerifyControllerMidDeploymentKill { get; init; }
        public bool VerifyControllerMidDeploymentDamage { get; init; }
        public bool VerifyControllerTargetedDeploy { get; init; }
        public bool VerifyControllerStyleSelection { get; init; }
        public bool VerifyControllerStyleDeploy { get; init; }
        public bool ContentSearchOnly { get; init; }
        public int ContentSearchTurnDepth { get; init; } = 1;
        public bool VerifyPureSupport { get; init; }
        public bool VerifyNoPureSupport { get; init; }
        public bool VerifyGroupBenefitSearch { get; init; }
        public bool VerifyTargetedSupport { get; init; }
        public bool VerifyMultipleSupport { get; init; }
        public bool VerifyAllyTarget { get; init; }
        public bool VerifyAllyAfterEnergyGain { get; init; }
        public bool VerifySelfPotion { get; init; }
        public bool VerifyPotionAccounting { get; init; }
        public string[] ContentCardIds { get; init; } = [];
        public int ContentUpgradeLevel { get; init; }
        public int ContentTargetSeat { get; init; } = 1;
        public int ContentExtraDrawCardsPerPlayer { get; init; }
        public int ContentStokeHandCards { get; init; }
        public int ContentBeatDownDiscardAttacks { get; init; }
        public int ContentActorBlock { get; init; }
        public int ContentTargetBlock { get; init; }
        public int ContentActorEnergy { get; init; } = 10;
        public int ContentCacophonyCardsRemaining { get; init; }
        public bool VerifyContentRound { get; init; }
        public bool ContentTeammateStrikeBefore { get; init; }
        public bool ContentTeammateStrikeAfter { get; init; }
        public string ContentTeammateCardIdAfter { get; init; } = "";
        public bool ContentReplayTransferredBall { get; init; }
        public bool IsVirtual => Mode == "virtual";
        public int PeerCount => IsVirtual ? 1 : PlayerCount;

        public static MultiplayerProbeInput Load(UnattendedTestRequest request)
        {
            var input = JsonSerializer.Deserialize<MultiplayerProbeInput>(
                File.ReadAllText(request.MultiplayerProbePath!), UnattendedTestFiles.JsonOptions)
                ?? throw new InvalidDataException("Multiplayer probe input is empty.");
            if (input.ContentCardIds is null)
                throw new InvalidDataException("Multiplayer content card IDs cannot be null.");
            if (request.ScenarioId != (input.ContentCardIds.Length == 0 ? "MULTIPLAYER-P0" : "MULTIPLAYER-CONTENT")
                || !request.ExitOnComplete
                || input.Mode is not ("virtual" or "host" or "client")
                || input.PlayerCount is not (2 or 4)
                || input.VerifyControllerDeploy && !input.VerifyControllerSearch
                || input.VerifyControllerAutomaticCalculation && input.VerifyControllerFullAuto
                || (input.VerifyControllerAutomaticCalculation || input.VerifyControllerFullAuto)
                    && input.VerifyControllerSearch
                || input.VerifyControllerTeammateDrift && !input.VerifyControllerDeploy
                || input.VerifyControllerTeammateKillsTarget && !input.VerifyControllerTeammateDrift
                || input.VerifyControllerMidDeploymentKill && !input.VerifyControllerDeploy
                || input.VerifyControllerMidDeploymentDamage && !input.VerifyControllerDeploy
                || input.VerifyPotionAccounting && !input.VerifySelfPotion
                || input.VerifyControllerTargetedDeploy && !input.ContentCardIds.Contains("BLAZE")
                || input.VerifyControllerStyleSelection
                    && !input.ContentCardIds.Contains("INFLAME")
                    && !input.ContentCardIds.Contains("DEFEND_IRONCLAD")
                || input.VerifyControllerStyleDeploy && !input.VerifyControllerStyleSelection
                || input.VerifyAllyAfterEnergyGain && !input.VerifyAllyTarget
                || input.ContentCardIds.Length > 0 && (input.Mode != "virtual"
                    || input.ContentCardIds.Length > 5
                    || input.ContentCardIds.Any(string.IsNullOrWhiteSpace)
                    || input.ContentCardIds.Distinct(StringComparer.Ordinal).Count() != input.ContentCardIds.Length
                    || input.ContentUpgradeLevel is not (0 or 1)
                    || input.ContentExtraDrawCardsPerPlayer is < 0 or > 5
                    || input.ContentStokeHandCards is < 0 or > 5
                    || input.ContentStokeHandCards > 0 && !input.ContentCardIds.Contains("STOKE")
                    || input.ContentBeatDownDiscardAttacks is < 0 or > 3
                    || input.ContentBeatDownDiscardAttacks > 0 && !input.ContentCardIds.Contains("BEAT_DOWN")
                    || input.ContentActorBlock is < 0 or > 100
                    || input.ContentTargetBlock is < 0 or > 100
                    || input.ContentActorEnergy is < 0 or > 20
                    || input.ContentSearchOnly && !input.VerifySearch
                    || input.ContentSearchTurnDepth is < 1 or > 2
                    || input.ContentCacophonyCardsRemaining is < 0 or > 33
                    || input.ContentTeammateStrikeBefore && input.ContentTeammateStrikeAfter
                    || input.ContentTeammateStrikeAfter && input.ContentTeammateCardIdAfter.Length > 0
                    || input.ContentTargetSeat <= 0 || input.ContentTargetSeat >= input.PlayerCount)
                || input.Seat < 0 || input.Seat >= input.PeerCount
                || (input.Mode == "host" && input.Seat != 0)
                || (input.Mode == "client" && input.Seat == 0)
                || input.Port == 0 || !Path.IsPathFullyQualified(input.CoordinationDirectory)
                || !Directory.Exists(input.CoordinationDirectory))
                throw new InvalidDataException("Invalid isolated multiplayer probe configuration.");
            string evidence = Path.GetFullPath(Path.Combine(input.CoordinationDirectory, $"peer-{input.Seat}"));
            if (request.EvidenceDirectory == null || Path.GetFullPath(request.EvidenceDirectory) != evidence)
                throw new InvalidDataException("Multiplayer evidence directory must match the configured peer.");
            string version = NGame.GetGameVersion();
            if (version.TrimStart('v') != input.ExpectedGameVersion.TrimStart('v'))
                throw new InvalidDataException($"Game version mismatch: expected={input.ExpectedGameVersion} actual={version}");
            return input;
        }
    }

    // Lobby callbacks carry the native begin-run result. The probe owns the lobby until handoff to RunManager.
    private sealed class MultiplayerProbeLobbyListener : IStartRunLobbyListener
    {
        public TaskCompletionSource<(string Seed, List<ActModel> Acts, IReadOnlyList<ModifierModel> Modifiers)> Started { get; } = new();
        public void BeginRun(string seed, List<ActModel> acts, IReadOnlyList<ModifierModel> modifiers)
            => Started.SetResult((seed, acts, modifiers));
        public void LocalPlayerDisconnected(NetErrorInfo info)
            => Started.TrySetException(new InvalidOperationException($"Probe lobby disconnected: {info}"));
        public void RemotePlayerDisconnected(StartRunLobbyPlayer player)
            => Started.TrySetException(new InvalidOperationException($"Probe lobby lost player {player.id}"));
        public void PlayerConnected(StartRunLobbyPlayer player) { }
        public void PlayerChanged(StartRunLobbyPlayer player, bool isRandomCharacterResolution) { }
        public void AscensionChanged() { }
        public void SeedChanged() { }
        public void ModifiersChanged() { }
        public void MaxAscensionChanged() { }
    }

    private async Task WaitForMultiplayerProbeAsync(Func<bool> condition)
    {
        while (!condition())
        {
            EnsureWithinDeadline();
            await NextFrameAsync();
        }
        EnsureWithinDeadline();
    }

    private async Task MultiplayerProbeBarrierAsync(string stage, CombatState state)
    {
        MultiplayerProbeInput input = _multiplayerProbe!;
        SetStage("multiplayer_" + stage);
        await WaitForMultiplayerProbeAsync(() => !RunManager.Instance.ActionExecutor.IsRunning
            && RunManager.Instance.ActionQueueSet.IsEmpty);
        var rng = state.RunState.Rng;
        static string DescribeRng(PredictionRngState value)
            => $"{value.Counter}:{value.State0}:{value.State1}:{value.State2}:{value.State3}";
        var snapshot = new
        {
            NativeState = NetFullCombatState.FromRun(state.RunState, justFinishedAction: null).ToString(),
            Players = state.Players.Select(player => new
            {
                player.NetId,
                Phase = player.PlayerCombatState!.Phase.ToString(),
                player.PlayerCombatState.TurnNumber,
                Ready = CombatManager.Instance.IsPlayerReadyToEndTurn(player),
            }).ToArray(),
            Rng = new[]
            {
                DescribeRng(rng.Shuffle.CaptureState()), DescribeRng(rng.CombatCardGeneration.CaptureState()),
                DescribeRng(rng.CombatPotionGeneration.CaptureState()), DescribeRng(rng.CombatCardSelection.CaptureState()),
                DescribeRng(rng.CombatEnergyCosts.CaptureState()), DescribeRng(rng.CombatTargets.CaptureState()),
                DescribeRng(rng.CombatOrbGeneration.CaptureState()), DescribeRng(rng.MonsterAi.CaptureState()),
                DescribeRng(rng.Niche.CaptureState()),
            },
        };
        _writer.WriteGeneratedArtifact(stage + ".json", snapshot);
        string[] files = Enumerable.Range(0, input.PeerCount)
            .Select(seat => Path.Combine(input.CoordinationDirectory, $"peer-{seat}", stage + ".json")).ToArray();
        await WaitForMultiplayerProbeAsync(() => files.All(File.Exists));
        JsonNode reference = JsonNode.Parse(File.ReadAllText(files[0]))!;
        for (int seat = 1; seat < files.Length; seat++)
            if (!JsonNode.DeepEquals(reference, JsonNode.Parse(File.ReadAllText(files[seat]))))
                throw new InvalidDataException($"Multiplayer peer state differs at {stage}: peer=0/{seat}; inspect checkpoint artifacts.");
        _completedChecks.Add($"MultiplayerCheckpoint:{stage}:Peers={input.PeerCount}:NativeState:Phases:FullRng");
    }
}
