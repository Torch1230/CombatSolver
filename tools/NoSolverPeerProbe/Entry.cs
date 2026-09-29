using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Ftue;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

namespace NoSolverPeerProbe;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static void Initialize()
    {
        new Harmony("NoSolverPeerProbe.HeadlessFtue").Patch(
            AccessTools.Method(typeof(NCombatRulesFtue), nameof(NCombatRulesFtue.Create), Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(Entry), nameof(SkipCombatRulesFtue)));
        NGame host = NGame.Instance ?? throw new InvalidOperationException("No Solver peer has no game host.");
        _ = RunAsync(host);
    }

    public static bool SkipCombatRulesFtue(ref NCombatRulesFtue? __result)
    {
        __result = null;
        return false;
    }

    private static async Task RunAsync(NGame host)
    {
        string requestPath = Path.Combine(OS.GetUserDataDir(), "combat_solver_test_request.json");
        string resultPath = Path.Combine(OS.GetUserDataDir(), "combat_solver_test_result.json");
        string? runId = null;
        string? evidenceDirectory = null;
        try
        {
            await WaitAsync(host, () => File.Exists(requestPath));
            using JsonDocument request = JsonDocument.Parse(File.ReadAllText(requestPath));
            JsonElement root = request.RootElement;
            runId = root.GetProperty("runId").GetString()
                ?? throw new InvalidDataException("Missing run ID.");
            evidenceDirectory = root.GetProperty("evidenceDirectory").GetString()
                ?? throw new InvalidDataException("Missing evidence directory.");
            string seed = root.GetProperty("seed").GetString()
                ?? throw new InvalidDataException("Missing seed.");
            string inputPath = root.GetProperty("multiplayerProbePath").GetString()
                ?? throw new InvalidDataException("Missing multiplayer probe path.");
            using JsonDocument input = JsonDocument.Parse(File.ReadAllText(inputPath));
            JsonElement probe = input.RootElement;
            string coordination = probe.GetProperty("coordinationDirectory").GetString()
                ?? throw new InvalidDataException("Missing coordination directory.");
            int seat = probe.GetProperty("seat").GetInt32();
            int port = probe.GetProperty("port").GetInt32();
            int playerCount = probe.GetProperty("playerCount").GetInt32();
            bool verifyEnetControllerRng = probe.GetProperty("verifyEnetControllerRng").GetBoolean();
            if (seat != 1 || playerCount != 2 || probe.GetProperty("mode").GetString() != "client")
                throw new InvalidDataException("No Solver peer expects seat 1 in a two-player ENet game.");
            if (NGame.GetGameVersion().TrimStart('v') !=
                probe.GetProperty("expectedGameVersion").GetString()?.TrimStart('v'))
                throw new InvalidDataException("No Solver peer game version differs.");
            string[] loadedMods = ModManager.GetLoadedMods()
                .Select(mod => mod.manifest?.id ?? "<missing>").OrderBy(id => id).ToArray();
            Directory.CreateDirectory(evidenceDirectory);
            WriteJson(Path.Combine(evidenceDirectory, "environment.json"), new
            {
                GameVersion = NGame.GetGameVersion(),
                LoadedMods = loadedMods,
                Seat = seat,
            });
            if (loadedMods.Contains("CombatSolver", StringComparer.Ordinal))
                throw new InvalidOperationException(
                    "No Solver peer loaded the wrong mod set: " + string.Join(',', loadedMods));
            await WaitAsync(host, () => ModelDb.All.OfType<CharacterModel>()
                .Any(model => model.Id.Entry == "IRONCLAD")
                && ModelDb.All.OfType<EncounterModel>()
                    .Any(model => model.Id.Entry == (verifyEnetControllerRng
                        ? "CULTISTS_NORMAL" : "FUZZY_WURM_CRAWLER_WEAK")));
            CharacterModel character = ModelDb.AllCharacters.Single(model => model.Id.Entry == "IRONCLAD");
            EncounterModel encounter = ModelDb.All.OfType<EncounterModel>()
                .Single(model => model.Id.Entry == (verifyEnetControllerRng
                    ? "CULTISTS_NORMAL" : "FUZZY_WURM_CRAWLER_WEAK"));
            RunState run = await JoinAsync(host, character, coordination, port, playerCount);
            await PreloadManager.LoadRunAssets(run.Players.Select(player => player.Character));
            await PreloadManager.LoadActAssets(run.Acts[0]);
            await RunManager.Instance.FinalizeStartingRelics();
            RunManager.Instance.Launch();
            host.RootSceneContainer.SetCurrentScene(NRun.Create(run));
            await RunManager.Instance.SetActInternal(0);
            RunManager.Instance.RunLocationTargetedBuffer.OnLocationChanged(run.RunLocation);
            RunManager.Instance.MapSelectionSynchronizer.OnLocationChanged(run.MapLocation);
            foreach (Player player in run.Players)
            {
                CardModel[] oldCards = player.Deck.Cards.ToArray();
                player.Deck.Clear(silent: true);
                foreach (CardModel old in oldCards)
                    run.RemoveCard(old);
                foreach (string id in new[]
                    { "STRIKE_IRONCLAD", "DEFEND_IRONCLAD", verifyEnetControllerRng && player == run.Players[1]
                        ? "LARGESSE" : "SURVIVOR", "STRIKE_IRONCLAD", "DEFEND_IRONCLAD" })
                {
                    CardModel canonical = ModelDb.AllCards.Single(card => card.Id.Entry == id);
                    CardModel card = run.CreateCard(canonical, player);
                    if (!(await CardPileCmd.Add(card, PileType.Deck)).success)
                        throw new InvalidOperationException($"Could not inject {id}.");
                }
            }
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Monster,
                encounter.ToMutable(), showTransition: false);
            await WaitAsync(host, () => CombatManager.Instance.DebugOnlyGetState() is { } combat
                && combat.Players.Count == playerCount && combat.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 1 }));
            CombatState state = CombatManager.Instance.DebugOnlyGetState()!;
            string[] combatMods = ModManager.GetLoadedMods()
                .Select(mod => mod.manifest?.id ?? "<missing>").OrderBy(id => id).ToArray();
            if (combatMods.Contains("CombatSolver", StringComparer.Ordinal)
                || !combatMods.Contains("NoSolverPeerProbe", StringComparer.Ordinal))
                throw new InvalidOperationException("No Solver peer combat mod set differs: "
                    + string.Join(',', combatMods));
            WriteJson(Path.Combine(evidenceDirectory, "environment.json"), new
            {
                GameVersion = NGame.GetGameVersion(),
                LoadedMods = combatMods,
                SolverLoaded = false,
                Seat = seat,
            });
            Player local = LocalContext.GetMe(state)
                ?? throw new InvalidOperationException("No Solver peer has no local player.");
            if (!ReferenceEquals(local, state.Players[seat]))
                throw new InvalidOperationException("No Solver peer joined the wrong seat.");
            await BarrierAsync(host, state, coordination, evidenceDirectory, "root", playerCount);
            if (verifyEnetControllerRng)
            {
                await WaitAsync(host, () => File.Exists(Path.Combine(coordination, "peer-0", "first-attack.signal")));
                Player recipient = state.Players[0];
                CardModel largesse = local.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == "LARGESSE");
                HashSet<CardModel> recipientCardsBefore = [.. recipient.PlayerCombatState!.AllCards];
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new PlayCardAction(largesse, recipient.Creature));
                await WaitAsync(host, () => !local.PlayerCombatState.Hand.Cards.Contains(largesse)
                    && recipient.PlayerCombatState.AllCards.Any(card => !recipientCardsBefore.Contains(card)));
                File.WriteAllText(Path.Combine(evidenceDirectory, "largesse-complete.signal"), "Largesse complete");
                await BarrierAsync(host, state, coordination, evidenceDirectory, "enet-rng-drift", playerCount);
            }
            else if (probe.TryGetProperty("verifyControllerFullAuto", out JsonElement fullAuto) && fullAuto.GetBoolean())
            {
                await WaitAsync(host, () => CombatManager.Instance.IsPlayerReadyToEndTurn(state.Players[0]));
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(local, 1));
                await WaitAsync(host, () => state.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                await BarrierAsync(host, state, coordination, evidenceDirectory,
                    "full-auto-second-turn", playerCount);
            }
            else
            {
                using (CardSelectCmd.UseSelector(new NetworkSelector(state), localOnly: true))
                {
                    foreach (Player player in state.Players)
                    {
                        await PlayAsync(host, state, player, "DEFEND_IRONCLAD", null,
                            () => player.PlayerCombatState!.Energy == 2 && player.Creature.Block == 5);
                        await BarrierAsync(host, state, coordination, evidenceDirectory,
                            $"defend-{player.NetId}", playerCount);
                        int hp = state.Enemies.Single().CurrentHp;
                        await PlayAsync(host, state, player, "STRIKE_IRONCLAD", state.Enemies.Single(),
                            () => player.PlayerCombatState!.Energy == 1 && state.Enemies.Single().CurrentHp == hp - 6);
                        await BarrierAsync(host, state, coordination, evidenceDirectory,
                            $"strike-{player.NetId}", playerCount);
                        await PlayAsync(host, state, player, "SURVIVOR", null,
                            () => player.PlayerCombatState!.Energy == 0 && player.Creature.Block == 13
                                && player.PlayerCombatState.Hand.Cards.Count == 1
                                && player.PlayerCombatState.DiscardPile.Cards.Count == 4);
                        await BarrierAsync(host, state, coordination, evidenceDirectory,
                            $"choice-{player.NetId}", playerCount);
                    }
                }
                foreach (Player player in state.Players)
                {
                    if (LocalContext.IsMe(player))
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(player, 1));
                    if (player != state.Players[^1])
                    {
                        await WaitAsync(host, () => CombatManager.Instance.IsPlayerReadyToEndTurn(player));
                        await BarrierAsync(host, state, coordination, evidenceDirectory,
                            $"ready-{player.NetId}", playerCount);
                    }
                }
                await WaitAsync(host, () => state.Players.All(player =>
                    player.PlayerCombatState is { Phase: PlayerTurnPhase.Play, TurnNumber: 2 }));
                await BarrierAsync(host, state, coordination, evidenceDirectory, "second-turn", playerCount);
            }
            WriteJson(resultPath, new
            {
                SchemaVersion = 1,
                RunId = runId,
                Status = "Passed",
                Stage = "passed",
                Error = (string?)null
            });
            WriteJson(Path.Combine(evidenceDirectory, "result.json"), new
            {
                SchemaVersion = 1,
                RunId = runId,
                Status = "Passed",
                Stage = "passed",
                SolverLoaded = false,
                LoadedMods = combatMods,
                Error = (string?)null,
            });
            host.GetTree().Quit();
        }
        catch (Exception error)
        {
            object result = new
            {
                SchemaVersion = 1,
                RunId = runId,
                Status = "Failed",
                Stage = "no_solver_peer",
                Error = error.ToString()
            };
            WriteJson(resultPath, result);
            if (evidenceDirectory != null)
                WriteJson(Path.Combine(evidenceDirectory, "result.json"), result);
            host.GetTree().Quit(1);
        }
    }

    private static async Task<RunState> JoinAsync(NGame host, CharacterModel character,
        string coordination, int port, int playerCount)
    {
        await WaitAsync(host, () => File.Exists(Path.Combine(coordination, "peer-0", "listening.json")));
        var service = new NetClientGameService(PeerVersionInfo.LocalDefault());
        var flow = new JoinFlow(service);
        JoinResult joined = await flow.Begin(new ENetClientConnectionInitializer(2, "127.0.0.1", checked((ushort)port)), host.GetTree());
        if (joined.sessionState != RunSessionState.InLobby || joined.joinResponse == null)
            throw new InvalidOperationException("No Solver peer did not join the lobby.");
        var listener = new LobbyListener();
        var lobby = new StartRunLobby(joined.gameMode, service, listener, -1);
        lobby.InitializeFromMessage(joined.joinResponse.Value);
        lobby.SetLocalCharacter(character);
        while (lobby.Players.Count != playerCount || lobby.Players.Any(player => player.character.Id != character.Id))
        {
            service.Update();
            if (listener.Started.Task.IsFaulted) await listener.Started.Task;
            await NextFrameAsync(host);
        }
        lobby.SetReady(true);
        while (!listener.Started.Task.IsCompleted)
        {
            service.Update();
            await NextFrameAsync(host);
        }
        var begin = await listener.Started.Task;
        RunState run = RunState.CreateForNewRun(lobby.Players.Select(player => Player.CreateForNewRun(
            player.character, UnlockState.FromSerializable(player.unlockState), player.id)).ToArray(),
            begin.Acts.Select(act => act.ToMutable()).ToArray(), begin.Modifiers,
            GameMode.Standard, 0, begin.Seed);
        RunManager.Instance.SetUpNewMultiplayer(run, lobby, shouldSave: false);
        lobby.CleanUp(disconnectSession: false);
        return run;
    }

    private static async Task PlayAsync(NGame host, CombatState combat, Player actor,
        string id, MegaCrit.Sts2.Core.Entities.Creatures.Creature? target, Func<bool> observed)
    {
        if (LocalContext.IsMe(actor))
        {
            CardModel card = actor.PlayerCombatState!.Hand.Cards.First(value => value.Id.Entry == id);
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
        }
        await WaitAsync(host, observed);
    }

    private static async Task BarrierAsync(NGame host, CombatState state, string coordination,
        string evidenceDirectory, string stage, int playerCount)
    {
        await WaitAsync(host, () => !RunManager.Instance.ActionExecutor.IsRunning
            && RunManager.Instance.ActionQueueSet.IsEmpty);
        var rng = state.RunState.Rng;
        WriteJson(Path.Combine(evidenceDirectory, stage + ".json"), new
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
                DescribeRng(rng.Shuffle), DescribeRng(rng.CombatCardGeneration),
                DescribeRng(rng.CombatPotionGeneration), DescribeRng(rng.CombatCardSelection),
                DescribeRng(rng.CombatEnergyCosts), DescribeRng(rng.CombatTargets),
                DescribeRng(rng.CombatOrbGeneration), DescribeRng(rng.MonsterAi),
                DescribeRng(rng.Niche),
            },
        });
        string[] files = Enumerable.Range(0, playerCount)
            .Select(seat => Path.Combine(coordination, $"peer-{seat}", stage + ".json")).ToArray();
        await WaitAsync(host, () => files.All(File.Exists));
        JsonNode reference = JsonNode.Parse(File.ReadAllText(files[0]))!;
        if (!JsonNode.DeepEquals(reference, JsonNode.Parse(File.ReadAllText(files[1]))))
            throw new InvalidOperationException($"No Solver peer differs at {stage}.");
    }

    private static string DescribeRng(MegaCrit.Sts2.Core.Random.Rng rng)
    {
        var state = rng.ToSerializable();
        return $"{state.counter}:{state.state0}:{state.state1}:{state.state2}:{state.state3}";
    }

    private static async Task WaitAsync(NGame host, Func<bool> condition)
    {
        while (!condition()) await NextFrameAsync(host);
    }

    private static async Task NextFrameAsync(NGame host)
        => await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    private sealed class LobbyListener : IStartRunLobbyListener
    {
        public TaskCompletionSource<(string Seed, List<ActModel> Acts, IReadOnlyList<ModifierModel> Modifiers)>
            Started
        { get; } = new();
        public void BeginRun(string seed, List<ActModel> acts, IReadOnlyList<ModifierModel> modifiers)
            => Started.SetResult((seed, acts, modifiers));
        public void LocalPlayerDisconnected(NetErrorInfo info)
            => Started.TrySetException(new InvalidOperationException($"Peer disconnected: {info}"));
        public void RemotePlayerDisconnected(StartRunLobbyPlayer player)
            => Started.TrySetException(new InvalidOperationException($"Peer lost player {player.id}"));
        public void PlayerConnected(StartRunLobbyPlayer player) { }
        public void PlayerChanged(StartRunLobbyPlayer player, bool isRandomCharacterResolution) { }
        public void AscensionChanged() { }
        public void SeedChanged() { }
        public void ModifiersChanged() { }
        public void MaxAscensionChanged() { }
    }

    private sealed class NetworkSelector(CombatState combat) : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            CardModel selected = options.Single(card => card.Id.Entry == "DEFEND_IRONCLAD");
            Player player = selected.Owner;
            if (!LocalContext.IsMe(player) || minSelect != 1 || maxSelect != 1)
                throw new InvalidOperationException("No Solver peer received an unexpected choice.");
            int slot = combat.Players.ToList().IndexOf(player);
            var synchronizer = RunManager.Instance.PlayerChoiceSynchronizer;
            if (slot < 0 || slot >= synchronizer.ChoiceIds.Count || synchronizer.ChoiceIds[slot] == 0)
                throw new InvalidOperationException("No Solver peer choice ID is missing.");
            synchronizer.SyncLocalChoice(player, synchronizer.ChoiceIds[slot] - 1,
                PlayerChoiceResult.FromMutableCombatCards([selected]));
            return Task.FromResult<IEnumerable<CardModel>>([selected]);
        }

        public CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
            => throw new InvalidOperationException("No Solver peer does not select rewards.");
    }
}
