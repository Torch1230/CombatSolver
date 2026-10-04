using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Steamworks;

if (args.Length != 3 || (args[2] != "--apply" && args[2] != "--validate-only"))
    throw new ArgumentException("Usage: WorkshopMetadata <repository> <english|schinese> <--validate-only|--apply>");

var metadataDirectory = Path.Combine(Path.GetFullPath(args[0]), "docs", "workshop");
using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(metadataDirectory, "metadata.json")));
var root = document.RootElement;
var appId = root.GetProperty("appId").GetUInt32();
var itemId = root.GetProperty("publishedFileId").GetUInt64();
var language = args[1];
var translation = root.GetProperty("languages").GetProperty(language);
var title = translation.GetProperty("title").GetString()!;
var description = File.ReadAllText(Path.Combine(metadataDirectory, translation.GetProperty("descriptionFile").GetString()!));
if (string.IsNullOrWhiteSpace(title) || Encoding.UTF8.GetByteCount(title) >= 129)
    throw new InvalidDataException("Workshop title must contain 1–128 UTF-8 bytes.");
if (string.IsNullOrWhiteSpace(description) || Encoding.UTF8.GetByteCount(description) >= 8000)
    throw new InvalidDataException("Workshop description must contain 1–7999 UTF-8 bytes.");

Console.WriteLine($"Item={itemId} Language={language} Title={title} DescriptionBytes={Encoding.UTF8.GetByteCount(description)}");
if (args[2] == "--validate-only")
    return;

Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
Environment.SetEnvironmentVariable("SteamGameId", appId.ToString());
var initialized = SteamAPI.InitEx(out var initializationError);
if (initialized != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
    throw new InvalidOperationException($"Steam initialization failed: {initialized}: {initializationError}");

try
{
    if ((uint)SteamUtils.GetAppID() != appId)
        throw new InvalidOperationException("Steam connected to a different App ID.");
    var handle = SteamUGC.StartItemUpdate(new AppId_t(appId), new PublishedFileId_t(itemId));
    if ((ulong)handle == ulong.MaxValue)
        throw new InvalidOperationException("Steam rejected StartItemUpdate.");

    Require(SteamUGC.SetItemUpdateLanguage(handle, language), "SetItemUpdateLanguage");
    Require(SteamUGC.SetItemTitle(handle, title), "SetItemTitle");
    Require(SteamUGC.SetItemDescription(handle, description), "SetItemDescription");

    SubmitItemUpdateResult_t? completed = null;
    var ioFailure = false;
    using var callback = CallResult<SubmitItemUpdateResult_t>.Create((result, failed) =>
    {
        completed = result;
        ioFailure = failed;
    });
    // A null change note updates this language's metadata without adding a release note.
    var call = SteamUGC.SubmitItemUpdate(handle, null!);
    if ((ulong)call == 0)
        throw new InvalidOperationException("Steam rejected SubmitItemUpdate.");
    callback.Set(call);
    var timer = Stopwatch.StartNew();
    while (completed is null)
    {
        SteamAPI.RunCallbacks();
        if (timer.Elapsed > TimeSpan.FromSeconds(60))
            throw new TimeoutException("Steam metadata update timed out; inspect the Steam workshop log before retrying.");
        Thread.Sleep(100);
    }

    var response = completed.Value;
    Console.WriteLine($"SubmitItemUpdate={response.m_eResult} Item={response.m_nPublishedFileId} LegalAgreement={response.m_bUserNeedsToAcceptWorkshopLegalAgreement}");
    if (ioFailure || response.m_eResult != EResult.k_EResultOK || (ulong)response.m_nPublishedFileId != itemId)
        throw new InvalidOperationException($"Steam metadata update failed: {response.m_eResult}; IOFailure={ioFailure}");
    if (response.m_bUserNeedsToAcceptWorkshopLegalAgreement)
        throw new InvalidOperationException("Steam requires the account owner to accept the Workshop legal agreement.");
}
finally
{
    SteamAPI.Shutdown();
}

static void Require(bool accepted, string operation)
{
    if (!accepted)
        throw new InvalidOperationException($"Steam rejected {operation}.");
}
