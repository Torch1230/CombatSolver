using System.Net.Http;
using HttpClient = System.Net.Http.HttpClient;
using Environment = System.Environment;
using System.Net.Http.Json;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed record OnlinePresencePayload(
    string SessionId, string Name, string Character, int? Floor,
    string Encounter, int? HpLoss, string Version, bool InCombat = false, long? BattleUpdatedAt = null, bool InRun = false,
    RunStatisticsSnapshot? RunStatistics = null);

// Capture scalar values on the main thread; only the immutable payload reaches HTTP.
internal sealed partial class OnlinePresence : Node
{
    private static OnlinePresence? _instance;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> InstallationIdentity = new(ReadIdentity);
    private HttpClient? _client;
    private string? _identity;
    private OnlinePresencePayload? _latestBattle;
    private SolverResult? _capturedResult;
    private double _elapsed = 30;
    private Task? _pending;
    private CancellationTokenSource? _request;
    private readonly string _version = typeof(Entry).Assembly.GetName().Version!.ToString(3);
    private static readonly ClientUpdateNotice Updates = new(typeof(Entry).Assembly.GetName().Version!.ToString(3));
    internal static string? AvailableUpdateVersion => Updates.AvailableVersion;
    private string? _displayedUpdate;

    public static void Start(NGame host)
    {
        if (_instance != null || IsHeadless()) return;
        _instance = new OnlinePresence { Name = "CombatSolverOnlinePresence" };
        host.AddChild(_instance);
    }

    internal static bool IsHeadless()
        => string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase);

    public static void SettingsChanged()
    {
        RunStatistics.SettingsChanged();
        if (_instance == null) return;
        _instance._request?.Cancel();
        _instance._elapsed = 30;
    }

    public override void _Process(double delta)
    {
        // Saved-run loading publishes State before awaiting the save counter, then installs
        // NetService. Capture only after that initialization boundary has completed.
        if (RunManager.Instance.IsInProgress && RunManager.Instance.NetService is null) return;
        if (_displayedUpdate != AvailableUpdateVersion)
        {
            _displayedUpdate = AvailableUpdateVersion;
            SolverOverlay.RefreshControls();
        }
        if (!SolverSettings.Current.OnlineStatisticsEnabled || SolverController.IsMultiplayerSession || UnattendedTestRunner.IsActive) return;
        SolverResult? result = SolverController.CurrentResultForBugReport;
        if (CombatManager.Instance.IsInProgress && result?.CombatEndedTurn.HasValue == true && result != _capturedResult)
        {
            _latestBattle = RetainLatestBattle(Capture("", _version), _latestBattle);
            _capturedResult = result;
        }
        _elapsed += delta;
        if (_elapsed < 30 || _pending is { IsCompleted: false }) return;
        _elapsed = 0;
        if (_client == null && !ConfigureClient()) return;
        _identity ??= LoadIdentity();
        OnlinePresencePayload payload = RetainLatestBattle(Capture(_identity, _version), _latestBattle);
        if (payload.HpLoss.HasValue) _latestBattle = payload;
        _request?.Dispose();
        _request = new CancellationTokenSource();
        _pending = SendAsync(payload, _request.Token);
    }

    internal static OnlinePresencePayload Capture(string identity, string version)
    {
        var platform = PlatformUtil.PrimaryPlatform;
        string name = PlatformUtil.GetPlayerNameRaw(platform, PlatformUtil.GetLocalPlayerId(platform));
        var run = RunManager.Instance.DebugOnlyGetState();
        var player = LocalContext.GetMe(run);
        CombatState? combat = CombatManager.Instance.IsInProgress
            ? CombatManager.Instance.DebugOnlyGetState() : null;
        SolverResult? result = combat == null ? null : SolverController.CurrentResultForBugReport;
        return new OnlinePresencePayload(identity, Clean(name,128),
            Clean(player?.Character.Title.GetFormattedText() ?? "",128), run?.TotalFloor,
            Clean(combat == null ? "" : string.Join("、",combat.Enemies.Select(enemy => enemy.Name)),512),
            result?.CombatEndedTurn.HasValue == true ? result.ProjectedBattleHpLost : null, version,
            combat != null, result?.CombatEndedTurn.HasValue == true ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null,
            RunManager.Instance.IsInProgress, RunStatistics.Snapshot);
    }

    internal static OnlinePresencePayload RetainLatestBattle(OnlinePresencePayload current, OnlinePresencePayload? previous)
    {
        if (current.InCombat && current.HpLoss.HasValue && current.Character.Length > 0
            && current.Floor.HasValue && current.Encounter.Length > 0)
            return current;
        if (previous?.HpLoss.HasValue == true)
            return previous with { SessionId = current.SessionId, Name = current.Name, Version = current.Version, InCombat = current.InCombat, InRun = current.InRun, RunStatistics = current.RunStatistics };
        return current with { Character = "", Floor = null, Encounter = "", HpLoss = null, BattleUpdatedAt = null };
    }

    private static string Clean(string value, int limit)
    {
        string text = string.Concat(value.Where(c => !char.IsControl(c)));
        return text.Length <= limit ? text : text[..limit];
    }

    private bool ConfigureClient()
    {
        _client = CreateStatisticsClient();
        return _client != null;
    }

    private static HttpClientHandler CreateHandler(byte[] pin)
    {
        HttpClientHandler handler = new() { AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            certificate != null
            && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0
            && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pin)
            && DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime()
            && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime();
        return handler;
    }

    internal static async Task VerifyTransportForTestingAsync()
    {
        using OnlinePresence presence = new();
        if (!presence.ConfigureClient()) throw new InvalidOperationException("Presence transport fixture requires private endpoint configuration.");
        using HttpClient client = presence._client!;
        using HttpResponseMessage response = await client.PostAsJsonAsync("v1/heartbeat", new { });
        if (response.StatusCode != System.Net.HttpStatusCode.BadRequest)
            throw new InvalidOperationException("Presence endpoint did not reject an empty payload.");
        using HttpClient invalidPinClient = new(CreateHandler(new byte[32]))
        {
            BaseAddress = client.BaseAddress,
            Timeout = TimeSpan.FromSeconds(8),
        };
        try
        {
            using HttpResponseMessage unexpected = await invalidPinClient.PostAsJsonAsync("v1/heartbeat", new { });
            throw new InvalidOperationException("Presence TLS accepted a mismatched certificate pin.");
        }
        catch (HttpRequestException) { }
    }

    internal static HttpClient? CreateStatisticsClient()
    {
        var metadata = typeof(Entry).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(item => item.Key, item => item.Value);
        if (!metadata.TryGetValue("PresenceEndpoint", out string? address)) return null;
        Uri endpoint = new(address!, UriKind.Absolute);
        if (endpoint.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Presence endpoint requires HTTPS.");
        byte[] pin = Convert.FromHexString(metadata["PresenceCertificateSha256"]!);
        if (pin.Length != 32) throw new InvalidOperationException("Presence certificate pin must be SHA-256.");
        return new HttpClient(CreateHandler(pin)) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(8) };
    }

    internal static string LoadIdentity() => InstallationIdentity.Value;

    private static string ReadIdentity()
    {
        string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CombatSolver");
        System.IO.Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "presence-id");
        if (System.IO.File.Exists(path)) return Guid.Parse(System.IO.File.ReadAllText(path)).ToString("N");
        string id = Guid.NewGuid().ToString("N");
        System.IO.File.WriteAllText(path,id);
        return id;
    }

    private async Task SendAsync(OnlinePresencePayload payload, CancellationToken token)
    {
        try
        {
            using HttpResponseMessage response = await _client!.PostAsJsonAsync("v1/heartbeat",payload,Json,token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                Entry.Logger.Warn($"[CombatSolver/Online] Heartbeat HTTP {(int)response.StatusCode}");
            else
                await Updates.ReadResponseAsync(response, token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            Entry.Logger.Warn("[CombatSolver/Online] Heartbeat connection failed.");
        }
        catch (JsonException)
        {
            Entry.Logger.Warn("[CombatSolver/Online] Invalid heartbeat update response.");
        }
        catch (OperationCanceledException) { }
    }

    public override void _ExitTree()
    {
        _request?.Cancel();
        _client?.Dispose();
        if (ReferenceEquals(_instance,this)) _instance = null;
    }
}
