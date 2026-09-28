using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed partial class ScenarioBuilder
    {
        private async Task<ScenarioContext> BuildMultiplayerProbeAsync()
        {
            MultiplayerProbeInput input = runner._multiplayerProbe = MultiplayerProbeInput.Load(runner._request);
            CharacterModel character = ResolveUnique(ModelDb.AllCharacters, "IRONCLAD", "角色");
            EncounterModel encounter = ResolveUnique(ModelDb.All.OfType<EncounterModel>(), runner._request.EncounterId, "遭遇");
            runner.SetStage("multiplayer_create_run");
            RunState run;
            if (input.IsVirtual)
            {
                run = RunState.CreateForNewRun(
                    Enumerable.Range(0, input.PlayerCount).Select(seat => Player.CreateForNewRun(
                        character, SaveManager.Instance.GenerateUnlockStateFromProgress(), (ulong)seat + 1)).ToArray(),
                    ActModel.GetDefaultList().Select(act => act.ToMutable()).ToArray(), [],
                    GameMode.Standard, 0, runner._request.Seed);
                RunManager.Instance.SetUpNewSingleplayer(run, shouldSave: false);
            }
            else
            {
                run = await BuildNetworkProbeRunAsync(input, character);
            }
            await PreloadManager.LoadRunAssets(run.Players.Select(player => player.Character));
            await PreloadManager.LoadActAssets(run.Acts[0]);
            await RunManager.Instance.FinalizeStartingRelics();
            RunManager.Instance.Launch();
            runner._host.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.SetActInternal(0);
            RunManager.Instance.RunLocationTargetedBuffer.OnLocationChanged(run.RunLocation);
            RunManager.Instance.MapSelectionSynchronizer.OnLocationChanged(run.MapLocation);
            foreach (Player player in run.Players)
            {
                ClearRunDeck(run, player);
                if (input.ContentCardIds.Length > 0 && ReferenceEquals(player, run.Players[0]))
                {
                    foreach (string cardId in input.ContentCardIds)
                    {
                        await InjectRunCardAsync(run, player, new UnattendedCardInjection
                        {
                            CardId = cardId, UpgradeLevels = input.ContentUpgradeLevel,
                        });
                    }
                }
                else
                {
                    string[] cardIds = ["STRIKE_IRONCLAD", "DEFEND_IRONCLAD", "SURVIVOR", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD"];
                    if (input.ContentCardIds.Length > 0
                        && ReferenceEquals(player, run.Players[input.ContentTargetSeat])
                        && input.ContentTeammateCardIdAfter.Length > 0)
                        cardIds[2] = input.ContentTeammateCardIdAfter;
                    foreach (string cardId in cardIds)
                        await InjectRunCardAsync(run, player, new UnattendedCardInjection { CardId = cardId });
                }
            }
            runner._writer.WriteGeneratedArtifact("environment.json", new
            {
                GameVersion = NGame.GetGameVersion(), GameModule = typeof(RunState).Module.ModuleVersionId,
                input.Mode, input.Seat, input.PlayerCount, Transport = RunManager.Instance.NetService.Type.ToString(),
                LocalPlayer = LocalContext.NetId, Players = run.Players.Select(player => player.NetId).ToArray(),
            });
            runner.SetStage("multiplayer_enter_encounter");
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster, encounter.ToMutable(), showTransition: false);
            await runner.WaitForMultiplayerProbeAsync(() => CombatManager.Instance.DebugOnlyGetState() is { } combat
                && combat.Players.Count == input.PlayerCount && combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 1 }));
            CombatState = CombatManager.Instance.DebugOnlyGetState()!;
            StartedTurn = 1;
            Player local = LocalContext.GetMe(CombatState) ?? throw new InvalidOperationException("Missing probe local player.");
            await runner.MultiplayerProbeBarrierAsync("root", CombatState);
            if (input.VerifyRootProjection || input.VerifyActionDifferential)
            {
                CombatRootSnapshot root = CombatRootSnapshot.Capture(CombatState);
                if (root.PlayerCount != input.PlayerCount)
                    throw new InvalidOperationException($"Multiplayer root captured {root.PlayerCount} players; expected {input.PlayerCount}.");
                runner._completedChecks.Add($"MultiplayerRootProjection:Players={root.PlayerCount}:Seat={input.Seat}");
                if (input.VerifyActionDifferential)
                    VerifyMultiplayerRootForkContracts(root, CombatState, local);
            }
            return new ScenarioContext(character, encounter, CombatState, local, 1, [], [], []);
        }

        private void VerifyMultiplayerRootForkContracts(CombatRootSnapshot root, CombatState combat, Player local)
        {
            Player teammate = combat.Players.First(player => player.NetId != local.NetId);
            ContinuationStamp actual = ContinuationStamp.CaptureLive(combat);
            SearchPolicySnapshot policy = SolverController.CaptureSearchPolicy(
                SolverSettings.Capture(), combat, includeTurnSetup: false, theftPolicy: null);
            CombatBeamSolver driver = new(root, SolverDisplayNames.Capture(combat),
                BattleDamageTracker.Observe(combat), policy);
            StateFingerprint rootKey = driver.CaptureStateKeyForTesting(root.ForkSimulator());
            ContinuationStamp Stamp(CombatPredictionSimulator simulator) => ContinuationStamp.CapturePredicted(
                local, simulator, 1, root.Forecast, 1);
            void AssertDifferent(CombatPredictionSimulator simulator, string difference)
            {
                if (Stamp(simulator) == actual)
                    throw new InvalidOperationException($"Multiplayer root missed {difference} difference.");
                if (driver.CaptureStateKeyForTesting(simulator) == rootKey)
                    throw new InvalidOperationException($"Multiplayer state key missed {difference} difference.");
            }

            CombatPredictionSimulator blockFork = root.ForkSimulator();
            blockFork.State.GetCreature(teammate.Creature).GainBlock(1);
            AssertDifferent(blockFork, "teammate block");

            CombatPredictionSimulator ownerFork = root.ForkSimulator();
            PredictedCard card = ownerFork.State.GetPlayerCombatState(teammate).Hand.Cards.First();
            card.MutablePreview.Owner = null!;
            card.MutablePreview.Owner = local;
            AssertDifferent(ownerFork, "card owner");

            CombatPredictionSimulator rngFork = root.ForkSimulator();
            rngFork.Rng.Shuffle.NextInt(2);
            AssertDifferent(rngFork, "RNG internal state");

            CombatPredictionSimulator sibling = root.ForkSimulator();
            if (Stamp(sibling) != actual || driver.CaptureStateKeyForTesting(sibling) != rootKey)
                throw new InvalidOperationException("Multiplayer root was changed by a sibling fork.");
            runner._completedChecks.Add("MultiplayerRootFork:StampAndStateKey:TeammateBlock:CardOwner:Rng:SiblingsIsolated");
        }

        private async Task<RunState> BuildNetworkProbeRunAsync(MultiplayerProbeInput input, CharacterModel character)
        {
            var listener = new MultiplayerProbeLobbyListener();
            INetGameService service;
            StartRunLobby? lobby = null;
            bool handedToRun = false;
            if (input.Mode == "host")
            {
                var host = new NetHostGameService(PeerVersionInfo.LocalDefault());
                service = host;
                NetErrorInfo? failure = host.StartENetHost(input.Port, input.PlayerCount);
                if (failure != null) throw new InvalidOperationException($"ENet host failed: {failure}");
                lobby = new StartRunLobby(GameMode.Standard, host, listener, input.PlayerCount);
                lobby.AddLocalHostPlayer(SaveManager.Instance.GenerateUnlockStateFromProgress(), 0);
                lobby.SetSeed(runner._request.Seed);
                runner._writer.WriteGeneratedArtifact("listening.json", new { input.Port });
            }
            else
            {
                await runner.WaitForMultiplayerProbeAsync(() => File.Exists(Path.Combine(input.CoordinationDirectory, "peer-0", "listening.json")));
                var client = new NetClientGameService(PeerVersionInfo.LocalDefault());
                service = client;
            }
            try
            {
                if (lobby == null)
                {
                    var flow = new JoinFlow((NetClientGameService)service);
                    JoinResult joined = await flow.Begin(new ENetClientConnectionInitializer(
                        (ulong)input.Seat + 1, "127.0.0.1", input.Port), runner._host.GetTree());
                    if (joined.sessionState != RunSessionState.InLobby || joined.joinResponse == null)
                        throw new InvalidOperationException("Probe expected an initial lobby join.");
                    lobby = new StartRunLobby(joined.gameMode, service, listener, -1);
                    lobby.InitializeFromMessage(joined.joinResponse.Value);
                }
                lobby.SetLocalCharacter(character);
                runner.SetStage("multiplayer_lobby_ready");
                while (lobby.Players.Count != input.PlayerCount || lobby.Players.Any(player => player.character.Id != character.Id))
                {
                    runner.EnsureWithinDeadline();
                    service.Update();
                    if (listener.Started.Task.IsFaulted) await listener.Started.Task;
                    await runner.NextFrameAsync();
                }
                lobby.SetReady(true);
                while (!listener.Started.Task.IsCompleted)
                {
                    runner.EnsureWithinDeadline();
                    service.Update();
                    await runner.NextFrameAsync();
                }
                var begin = await listener.Started.Task;
                RunState run = RunState.CreateForNewRun(lobby.Players.Select(player => Player.CreateForNewRun(
                    player.character, UnlockState.FromSerializable(player.unlockState), player.id)).ToArray(),
                    begin.Acts.Select(act => act.ToMutable()).ToArray(), begin.Modifiers, GameMode.Standard, 0, begin.Seed);
                RunManager.Instance.SetUpNewMultiplayer(run, lobby, shouldSave: false);
                lobby.CleanUp(disconnectSession: false);
                handedToRun = true;
                return run;
            }
            finally
            {
                if (!handedToRun)
                {
                    if (lobby != null) lobby.CleanUp(disconnectSession: true);
                    else if (service.IsConnected) service.Disconnect(NetError.Quit);
                }
            }
        }
    }
}
