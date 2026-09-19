using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal static class CombatShowcaseCollector
{
    internal const int ProtocolVersion = 1;
    private const long MaximumBundleBytes = 8L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveUploads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static RootCapture? _root;
    private static bool _openingCaptureDecisionMade;
    private static CancellationTokenSource _uploadCancellation = new();

    internal sealed record RootCapture(
        CombatState State,
        CombatBugReportExporter.ReplayCheckpointMaterial Checkpoint,
        int StartHp,
        int StartMaxHp,
        int StartTurn,
        string RootSha256,
        int DeckSize,
        object[] Cards,
        object[] Relics,
        object[] Potions,
        string CharacterId,
        string CharacterName,
        string EncounterId,
        string BossName,
        string GameVersion,
        string SolverVersion,
        string RitsuLibVersion);

    internal static void BeginCombat()
    {
        _root = null;
        _openingCaptureDecisionMade = false;
    }

    internal static void SettingsChanged()
    {
        if (SolverSettings.Current.OnlineStatisticsEnabled)
            return;
        lock (Gate)
        {
            _uploadCancellation.Cancel();
            _uploadCancellation.Dispose();
            _uploadCancellation = new CancellationTokenSource();
        }
        _root = null;
        string directory = PendingDirectory();
        if (!Directory.Exists(directory))
            return;
        foreach (string path in Directory.GetFiles(directory, "*.zip"))
            File.Delete(path);
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    internal static void TryCaptureInitialRoot(CombatState state, SearchReason reason)
    {
        if (_root != null || _openingCaptureDecisionMade || reason != SearchReason.AutoTurnStart
            || !SolverSettings.Current.OnlineStatisticsEnabled)
            return;
        _openingCaptureDecisionMade = true;
        if (!IsEligibleOpening(state, out Player? player, out string ritsuVersion, out string rejection))
        {
            Entry.Logger.Info($"[CombatSolver/Showcase] OPENING_SKIPPED reason={rejection}");
            return;
        }

        CombatBugReportExporter.ReplayCheckpointMaterial checkpoint =
            CombatBugReportExporter.CaptureReplayCheckpoint(state);
        checkpoint = checkpoint with
        {
            NativeState = CombatShowcaseNativeState.CaptureNormalized(state),
        };
        _root = new RootCapture(
            state,
            checkpoint,
            player.Creature.CurrentHp,
            player.Creature.MaxHp,
            player.PlayerCombatState!.TurnNumber,
            Convert.ToHexString(SHA256.HashData(checkpoint.NativeState)).ToLowerInvariant(),
            player.Deck.Cards.Count,
            CaptureCards(player),
            CaptureRelics(player),
            CapturePotions(player),
            player.Character.Id.Entry,
            player.Character.Title.GetFormattedText(),
            state.Encounter!.Id.Entry,
            state.Encounter.Title.GetFormattedText(),
            typeof(CombatState).Assembly.GetName().Version?.ToString() ?? throw new InvalidDataException("游戏程序集缺少版本号。"),
            typeof(Entry).Assembly.GetName().Version?.ToString(3) ?? throw new InvalidDataException("CombatSolver 程序集缺少版本号。"),
            ritsuVersion);
        Entry.Logger.Info($"[CombatSolver/Showcase] OPENING_CAPTURED root={_root.RootSha256} boss={_root.EncounterId}");
    }

    internal static void TryQueueCompletedRoute(CombatState state, SolverResult result)
    {
        RootCapture? root = _root;
        _root = null;
        if (root == null)
            return;
        if (!ReferenceEquals(root.State, state))
        {
            SkipRoute("combat_state_changed");
            return;
        }
        if (!SolverSettings.Current.OnlineStatisticsEnabled)
        {
            SkipRoute("online_statistics_disabled");
            return;
        }
        if (result.StartTurnNumber != root.StartTurn)
        {
            SkipRoute($"start_turn_{result.StartTurnNumber}");
            return;
        }
        if (result.CombatEndedTurn is not { } endedTurn || !result.Snapshot.AllEnemiesDead)
        {
            SkipRoute("not_complete_victory");
            return;
        }
        if (result.ProjectedBattleHpLost != 0)
        {
            SkipRoute($"projected_hp_lost_{result.ProjectedBattleHpLost}");
            return;
        }
        if (result.SoldHp != 0)
        {
            SkipRoute($"sold_hp_{result.SoldHp}");
            return;
        }
        if (result.Snapshot.DeathSaveUseCount != 0 || result.Snapshot.ProjectedDeathSaveUseCount != 0)
        {
            SkipRoute("death_save_used");
            return;
        }
        if (result.Snapshot.PlayerMaxHp < root.StartMaxHp)
        {
            SkipRoute($"max_hp_{root.StartMaxHp}_to_{result.Snapshot.PlayerMaxHp}");
            return;
        }

        int turnCount = endedTurn - root.StartTurn + 1;
        if (turnCount <= 0)
            throw new InvalidDataException("录像路线结束回合早于开始回合。");
        byte[] solverResult = SolvedRouteCache.SerializeRoute(result);
        int potionUses = result.BestNode.Actions.Count(static action => action.Kind == PlanActionKind.UsePotion);
        byte[] route = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            startTurnNumber = root.StartTurn,
            combatEndedTurn = endedTurn,
            turnCount,
            projectedBattleHpLost = 0,
            soldHp = 0,
            deathSaveUseCount = 0,
            potionUseCount = potionUses,
            allEnemiesDead = true,
            finalPlayerMaxHp = result.Snapshot.PlayerMaxHp,
            actions = result.BestNode.Actions.Select(static action => new
            {
                turn = action.Turn,
                kind = action.Kind.ToString(),
                action.CardId,
                action.PotionId,
                action.PotionSlot,
                action.TargetCombatId,
                choices = action.GetActionChoicesInExecutionOrder(),
            }).ToArray(),
            solverResult = Convert.ToBase64String(solverResult),
        }, JsonOptions);
        byte[] archive = BuildArchive(root, route, endedTurn, turnCount, potionUses);
        if (archive.LongLength > MaximumBundleBytes)
            throw new InvalidDataException($"录像包超过 {MaximumBundleBytes} 字节上限。");

        string directory = PendingDirectory();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(path, archive);
        Entry.Logger.Info($"[CombatSolver/Showcase] BUNDLE_QUEUED file={Path.GetFileName(path)} bytes={archive.LongLength}");
        _ = UploadPendingAsync(path, CurrentCancellationToken());
    }

    private static void SkipRoute(string reason)
        => Entry.Logger.Info($"[CombatSolver/Showcase] ROUTE_SKIPPED reason={reason}");

    internal static async Task FlushPendingAsync()
    {
        if (!SolverSettings.Current.OnlineStatisticsEnabled)
        {
            SettingsChanged();
            return;
        }
        string directory = PendingDirectory();
        if (!Directory.Exists(directory))
            return;
        foreach (string path in Directory.GetFiles(directory, "*.zip").OrderBy(static path => path, StringComparer.Ordinal))
            await UploadPendingAsync(path, CurrentCancellationToken()).ConfigureAwait(false);
    }

    private static bool IsEligibleOpening(
        CombatState state,
        out Player player,
        out string ritsuVersion,
        out string rejection)
    {
        player = LocalContext.GetMe(state)!;
        ritsuVersion = string.Empty;
        rejection = string.Empty;
        BattleDamageSnapshot damage = BattleDamageTracker.Observe(state);
        if (state.Players.Count != 1)
            return Reject("player_count", out rejection);
        if (player?.PlayerCombatState?.TurnNumber != 1)
            return Reject("not_first_turn", out rejection);
        if (state.RunState.GameMode != GameMode.Standard)
            return Reject("not_standard_mode", out rejection);
        if (state.RunState.AscensionLevel != 10)
            return Reject($"ascension_{state.RunState.AscensionLevel}", out rejection);
        if (state.RunState.CurrentActIndex != 2)
            return Reject($"act_index_{state.RunState.CurrentActIndex}", out rejection);
        if (state.Encounter?.RoomType != RoomType.Boss)
            return Reject("not_boss_room", out rejection);
        if (damage.HpLostSoFar != 0 || damage.PotionsUsedSoFar != 0 || HasPlayerCardPlay(player))
            return Reject("player_action_already_observed", out rejection);

        var mods = ModManager.GetLoadedMods().Where(static mod => mod.manifest?.id != null).ToArray();
        string[] gameplayMods = CombatShowcaseModEligibility.FindGameplayModificationNames(
            mods.Select(static mod => new ShowcaseModDeclaration(
                mod.manifest!.id!,
                mod.manifest.name ?? string.Empty,
                mod.manifest.affectsGameplay)));
        if (gameplayMods.Length > 0)
            return Reject("gameplay_mods=" + string.Join(",", gameplayMods), out rejection);

        var ritsu = mods.SingleOrDefault(static mod => mod.manifest?.id == "STS2-RitsuLib");
        if (ritsu?.manifest == null)
            return Reject("ritsulib_missing", out rejection);
        ritsuVersion = ritsu.manifest.version?.ToString()
            ?? throw new InvalidDataException("RitsuLib 清单缺少版本号。");
        return true;
    }

    private static bool Reject(string reason, out string rejection)
    {
        rejection = reason;
        return false;
    }

    private static bool HasPlayerCardPlay(Player player)
        => CombatManager.Instance.History.CardPlaysStarted.Any(entry => entry.CardPlay.Player == player);

    private static object[] CaptureCards(Player player)
    {
        Dictionary<string, (CardModel Card, JsonElement State, int Count)> groups = new(StringComparer.Ordinal);
        foreach (CardModel card in player.Deck.Cards)
        {
            JsonElement state = CombatBugReportExporter.CaptureShowcaseObjectState(card);
            string key = JsonSerializer.Serialize(new
            {
                id = card.Id.Entry,
                card.Title,
                card.CurrentUpgradeLevel,
                enchantment = card.Enchantment?.Id.Entry,
                enchantmentAmount = card.Enchantment?.Amount,
                enchantmentStatus = card.Enchantment?.Status.ToString(),
                affliction = card.Affliction?.Id.Entry,
                afflictionAmount = card.Affliction?.Amount,
                keywords = card.Keywords.OrderBy(static value => value).ToArray(),
                state,
            }, JsonOptions);
            groups[key] = groups.TryGetValue(key, out var group)
                ? (group.Card, group.State, group.Count + 1)
                : (card, state, 1);
        }
        return groups.Values.Select(static group =>
        {
            CardModel card = group.Card;
            return (object)new
            {
                id = card.Id.Entry,
                name = card.Title,
                count = group.Count,
                upgradeLevel = card.CurrentUpgradeLevel,
                enchantment = card.Enchantment?.Id.Entry,
                affliction = card.Affliction?.Id.Entry,
                state = new
                {
                    enchantmentAmount = card.Enchantment?.Amount,
                    enchantmentStatus = card.Enchantment?.Status.ToString(),
                    afflictionAmount = card.Affliction?.Amount,
                    keywords = card.Keywords.Select(static value => value.ToString()).OrderBy(static value => value).ToArray(),
                    fields = group.State,
                },
            };
        }).ToArray();
    }

    private static object[] CaptureRelics(Player player)
        => player.Relics.Select((relic, index) => (object)new
        {
            id = relic.Id.Entry,
            name = relic.Title.GetFormattedText(),
            order = index,
            state = CombatBugReportExporter.CaptureShowcaseObjectState(relic),
        }).ToArray();

    private static object[] CapturePotions(Player player)
        => Enumerable.Range(0, player.PotionSlots.Count).Select(slot =>
        {
            PotionModel? potion = player.GetPotionAtSlotIndex(slot);
            return (object)new
            {
                slot,
                id = potion?.Id.Entry,
                name = potion?.Title.GetFormattedText(),
                state = potion == null ? (JsonElement?)null : CombatBugReportExporter.CaptureShowcaseObjectState(potion),
            };
        }).ToArray();

    private static byte[] BuildArchive(RootCapture root, byte[] route, int endedTurn, int turnCount, int potionUses)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["run-state.save"] = root.Checkpoint.RunState,
            ["replay-state.json"] = root.Checkpoint.ReplayState,
            ["native-state.bin"] = root.Checkpoint.NativeState,
            ["route.json"] = route,
        };
        string bundleId = Guid.NewGuid().ToString("N");
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            bundleId,
            createdAt = DateTimeOffset.UtcNow,
            gameVersion = root.GameVersion,
            solverVersion = root.SolverVersion,
            ritsuLibVersion = root.RitsuLibVersion,
            routeProtocolVersion = ProtocolVersion,
            standardEnvironment = true,
            act = 3,
            ascension = 10,
            characterId = root.CharacterId,
            characterName = root.CharacterName,
            encounterId = root.EncounterId,
            bossName = root.BossName,
            startHp = root.StartHp,
            startMaxHp = root.StartMaxHp,
            startTurnNumber = root.StartTurn,
            combatEndedTurn = endedTurn,
            turnCount,
            deckSize = root.DeckSize,
            cards = root.Cards,
            relics = root.Relics,
            potions = root.Potions,
            potionUseCount = potionUses,
            projectedBattleHpLost = 0,
            soldHp = 0,
            deathSaveUseCount = 0,
            startRootSha256 = root.RootSha256,
            files = files.ToDictionary(static pair => pair.Key, static pair => new
            {
                sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant(),
                sizeBytes = pair.Value.LongLength,
            }, StringComparer.Ordinal),
        }, JsonOptions);
        files["showcase.json"] = metadata;
        using MemoryStream output = new();
        using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using Stream stream = entry.Open();
                stream.Write(content);
            }
        }
        return output.ToArray();
    }

    private static async Task UploadPendingAsync(string path, CancellationToken cancellationToken)
    {
        if (!ActiveUploads.TryAdd(path, 0))
            return;
        ShowcaseTransport? transport = ShowcaseTransport.TryCreate();
        if (transport == null)
        {
            ActiveUploads.TryRemove(path, out _);
            return;
        }
        try
        {
            using FileStream archiveStream = new(
                path,
                FileMode.Open,
                System.IO.FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            using ZipArchive archive = new(archiveStream, ZipArchiveMode.Read);
            ZipArchiveEntry entry = archive.GetEntry("showcase.json")
                ?? throw new InvalidDataException("录像包缺少 showcase.json。");
            using StreamReader reader = new(entry.Open(), Encoding.UTF8);
            string metadata = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await transport.UploadAsync(path, metadata, cancellationToken).ConfigureAwait(false);
            File.Delete(path);
            Entry.Logger.Info($"[CombatSolver/Showcase] UPLOAD_CONFIRMED file={Path.GetFileName(path)}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Entry.Logger.Warn($"[CombatSolver/Showcase] UPLOAD_PENDING error={error.Message}");
        }
        finally
        {
            transport.Dispose();
            ActiveUploads.TryRemove(path, out _);
        }
    }

    private static CancellationToken CurrentCancellationToken()
    {
        lock (Gate)
            return _uploadCancellation.Token;
    }

    private static string PendingDirectory()
        => ProjectSettings.GlobalizePath("user://combat-solver-showcases/pending");
}

internal sealed partial class CombatShowcaseUploadNode : Node
{
    private double _elapsed = 25;
    private Task? _flush;

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_elapsed < 30 || _flush is { IsCompleted: false })
            return;
        _elapsed = 0;
        _flush = CombatShowcaseCollector.FlushPendingAsync();
    }
}

internal sealed class ShowcaseTransport : IDisposable
{
    private readonly System.Net.Http.HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _token;

    private ShowcaseTransport(System.Net.Http.HttpClient client, Uri endpoint, string token)
        => (_client, _endpoint, _token) = (client, endpoint, token);

    internal static ShowcaseTransport? TryCreate()
    {
        Dictionary<string, string?> metadata = typeof(Entry).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        if (!metadata.TryGetValue("ShowcaseEndpoint", out string? endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
            || !metadata.TryGetValue("ShowcaseUploadToken", out string? token)
            || string.IsNullOrWhiteSpace(token)
            || !metadata.TryGetValue("ShowcaseCertificateSha256", out string? certificatePin)
            || string.IsNullOrWhiteSpace(certificatePin))
            return null;
        byte[] pin = Convert.FromHexString(certificatePin);
        HttpClientHandler handler = new()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate != null
                && errors is System.Net.Security.SslPolicyErrors.None or System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors
                && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pin)
                && DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime()
                && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime(),
        };
        return new ShowcaseTransport(new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) }, uri, token);
    }

    internal async Task UploadAsync(string path, string metadata, CancellationToken cancellationToken)
    {
        await using FileStream input = new(
            path,
            FileMode.Open,
            System.IO.FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        using StreamContent file = new(input);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using MultipartFormDataContent form = new()
        {
            { file, "bundle", Path.GetFileName(path) },
            { new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata" },
        };
        using HttpRequestMessage request = new(HttpMethod.Post, _endpoint) { Content = form };
        request.Headers.Add("X-CombatSolver-Showcase-Key", _token);
        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"录像库返回 HTTP {(int)response.StatusCode}。");
    }

    public void Dispose() => _client.Dispose();
}
