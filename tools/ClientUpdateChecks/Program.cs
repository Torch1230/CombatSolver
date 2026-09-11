using System.Net;
using System.Text;
using System.Text.Json;
using CombatSolver;

var notice = new ClientUpdateNotice("0.35.3");
int checks = 0;
void Equal(string? expected) { checks++; if (notice.AvailableVersion != expected) throw new Exception($"Expected {expected}, got {notice.AvailableVersion}"); }
async Task Read(string body, HttpStatusCode status = HttpStatusCode.OK, CancellationToken token = default)
{
    using var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    await notice.ReadResponseAsync(response, token);
}
Equal(null);
foreach (string version in new[] {"0.35.3", "0.35.2", "0.9.99"}) { await Read(JsonSerializer.Serialize(new {latestVersion=version})); Equal(null); }
foreach (string version in new[] {"0.35.10", "0.36.0", "1.0.0"}) { await Read(JsonSerializer.Serialize(new {latestVersion=version})); Equal(version); }
await Read("", HttpStatusCode.NoContent); Equal("1.0.0");
foreach (string body in new[] {"{}", "null", "oops", "{\"latestVersion\":4}", "{\"latestVersion\":\"v1.0.0\"}", "{\"latestVersion\":\"1.0\"}", "{\"latestVersion\":\"1.0.0-beta\"}", "{\"latestVersion\":\"01.0.0\"}", "{\"latestVersion\":\"2147483648.0.0\"}", "{\"latestVersion\":\"[b]1.0.0\"}"})
{
    try { await Read(body); throw new Exception("Invalid response accepted"); }
    catch (JsonException) { checks++; }
    Equal("1.0.0");
}
try { await Read("{}", HttpStatusCode.ServiceUnavailable); throw new Exception("HTTP failure accepted"); }
catch (HttpRequestException) { checks++; }
Equal("1.0.0");
using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
try { await Read("{\"latestVersion\":null}", token:cancellation.Token); throw new Exception("Cancellation ignored"); }
catch (OperationCanceledException) { checks++; }
Equal("1.0.0");
await Read("{\"latestVersion\":\"0.35.3\"}"); Equal(null);
await Read("{\"latestVersion\":\"0.36.0\"}"); Equal("0.36.0");
await Read("{\"latestVersion\":null}"); Equal(null);
Console.WriteLine($"CLIENT_UPDATE_CHECKS_OK checks={checks}");
