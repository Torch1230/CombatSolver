using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CombatSolver;

internal sealed record PresenceHeartbeatResponse([property: JsonRequired] string? LatestVersion);

// Only confirmed server responses change the notice. HTTP failures retain the last confirmation.
internal sealed class ClientUpdateNotice(string currentVersion)
{
    private readonly Version _current = ParseRelease(currentVersion);
    private string? _availableVersion;

    internal string? AvailableVersion => Volatile.Read(ref _availableVersion);

    internal async Task ReadResponseAsync(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.NoContent)
            return; // Older monitoring servers do not advertise a version.
        PresenceHeartbeatResponse body = await response.Content
            .ReadFromJsonAsync<PresenceHeartbeatResponse>(cancellationToken: token).ConfigureAwait(false)
            ?? throw new JsonException("Heartbeat response is null.");
        string? available = null;
        if (body.LatestVersion is { } latest)
        {
            Version version = ParseRelease(latest);
            if (version > _current) available = version.ToString(3);
        }
        token.ThrowIfCancellationRequested();
        Volatile.Write(ref _availableVersion, available);
    }

    private static Version ParseRelease(string value)
    {
        string[] parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0
            || part.Length > 1 && part[0] == '0'
            || part.Any(c => c < '0' || c > '9'))
            || !Version.TryParse(value, out Version? version))
            throw new JsonException("Release version must contain three numeric components.");
        return version;
    }
}
