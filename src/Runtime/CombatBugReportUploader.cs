using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Net.Security;
using System.Security.Cryptography;

namespace CombatSolver;

internal readonly record struct CombatBugReportUploadProgress(long BytesSent, long TotalBytes)
{
    public int Percentage => TotalBytes <= 0
        ? 0
        : (int)Math.Clamp(BytesSent * 100L / TotalBytes, 0L, 100L);
}

internal sealed record CombatBugReportUploadReceipt(
    string ReportId,
    string SubmissionId,
    long SizeBytes,
    HttpStatusCode StatusCode);

internal static class CombatBugReportUploader
{
    public const long MaximumArchiveBytes = 128L * 1024 * 1024;
    public const int MaximumDescriptionUtf8Bytes = 64 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;
    private const int MaximumDisplayedResponseCharacters = 1_000;
    private const string UploadEndpoint = "https://43.249.195.13:10289/api/v2/reports";
    private const string UploadTokenHeaderName = "X-CombatSolver-Key";

    // 仅用于过滤扫描器和误触的公共流量，不是访问控制。
    private const string UploadToken = "9d61747056101b511150372adcf98bf61aa49394803dcdd4";

    private static readonly HttpClientHandler ProductionHandler = CreateProductionHandler();
    internal static HttpClientHandler CreateProductionHandler()
    {
        byte[] pin = Convert.FromHexString("AFFF5C405F7112E452ED7EBAE61420FE9CF900A70DEB017B4E03371BA3769870");
        return new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate != null
                && (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) == 0
                && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pin)
                && DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime()
                && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime(),
        };
    }
    private static readonly HttpClient Client = new(ProductionHandler)
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    internal static bool ProductionTransportUsesDirectConnectionForTesting
        => !ProductionHandler.UseProxy;

    public static Task<CombatBugReportUploadReceipt> UploadAsync(
        string zipPath,
        string description,
        string contact,
        string submissionId,
        IProgress<CombatBugReportUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => UploadCoreAsync(
            Client,
            new Uri(UploadEndpoint),
            zipPath,
            description,
            contact,
            submissionId,
            progress,
            cancellationToken);

    internal static Task<CombatBugReportUploadReceipt> UploadForTestingAsync(
        HttpClient client,
        string zipPath,
        string description,
        string contact,
        string submissionId,
        IProgress<CombatBugReportUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => UploadCoreAsync(
            client,
            new Uri("https://combat-solver.test/reports"),
            zipPath,
            description,
            contact,
            submissionId,
            progress,
            cancellationToken);

    private static async Task<CombatBugReportUploadReceipt> UploadCoreAsync(
        HttpClient client,
        Uri endpoint,
        string zipPath,
        string description,
        string contact,
        string submissionId,
        IProgress<CombatBugReportUploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateInputs(zipPath, description, contact, submissionId);
        string metadata = ReadMetadata(zipPath, submissionId, description);
        await using FileStream stream = new(
            zipPath,
            FileMode.Open,
            System.IO.FileAccess.Read,
            FileShare.Read);
        long archiveBytes = stream.Length;
        using ProgressFileContent fileContent = new(stream, progress, cancellationToken);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using MultipartFormDataContent form = new()
        {
            { fileContent, "report", Path.GetFileName(zipPath) },
            { new StringContent(description, Encoding.UTF8), "description" },
            { new StringContent(contact, Encoding.UTF8), "contact" },
            { new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata" },
            { new StringContent(submissionId, Encoding.UTF8), "submissionId" },
        };
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint) { Content = form };
        request.Headers.Add(UploadTokenHeaderName, UploadToken);

        using CancellationTokenRegistration abortRequest = cancellationToken.Register(request.Dispose);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested
                                   && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("问题包上传已取消。", ex, cancellationToken);
        }
        using (response)
        {
            string body = await ReadResponseBodyAsync(response.Content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"上传服务器返回 HTTP {(int)response.StatusCode}：{DescribeResponse(body)}");
            }

            ServerReceipt serverReceipt = ReadServerReceipt(body);
            if (serverReceipt.SizeBytes != archiveBytes)
            {
                throw new InvalidDataException(
                    $"上传服务器确认的文件大小不一致：本地 {archiveBytes} 字节，服务器 {serverReceipt.SizeBytes} 字节。");
            }

            return new CombatBugReportUploadReceipt(
                serverReceipt.ReportId,
                submissionId,
                serverReceipt.SizeBytes,
                response.StatusCode);
        }
    }

    internal static string ReadMetadata(string zipPath, string submissionId, string description)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry entry = archive.GetEntry(CombatBugReportMetadata.EntryPath)
            ?? throw new InvalidDataException("问题包缺少 report.json。");
        if (entry.Length > CombatBugReportMetadata.MaximumBytes) throw new InvalidDataException("问题包元数据过大。");
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        string metadata = reader.ReadToEnd();
        using JsonDocument document = JsonDocument.Parse(metadata);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 2
            || root.GetProperty("reportId").GetString() != submissionId
            || root.GetProperty("playerDescription").GetString() != description)
            throw new InvalidDataException("提交信息与问题包元数据不一致。");
        return metadata;
    }

    private static void ValidateInputs(
        string zipPath,
        string description,
        string contact,
        string submissionId)
    {
        FileInfo archive = new(zipPath);
        if (!archive.Exists)
            throw new FileNotFoundException("待上传的问题包不存在。", zipPath);
        if (archive.Length <= 0 || archive.Length > MaximumArchiveBytes)
        {
            throw new InvalidDataException(
                $"问题包大小必须在 1–{MaximumArchiveBytes} 字节之间，实际为 {archive.Length} 字节。");
        }
        int descriptionBytes = Encoding.UTF8.GetByteCount(description);
        if (descriptionBytes > MaximumDescriptionUtf8Bytes)
        {
            throw new InvalidDataException(
                $"问题描述超过 {MaximumDescriptionUtf8Bytes} 字节，实际为 {descriptionBytes} 字节。");
        }
        if (contact.Length > 64)
            throw new InvalidDataException("联系信息最长 64 个字符。");
        if (!Guid.TryParseExact(submissionId, "N", out _))
            throw new InvalidDataException("提交编号格式无效。");
    }

    private static async Task<string> ReadResponseBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using Stream input = await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream output = new(MaximumResponseBytes);
        byte[] buffer = new byte[8 * 1024];
        int remaining = MaximumResponseBytes;
        while (remaining > 0)
        {
            int read = await input.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
                break;
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static ServerReceipt ReadServerReceipt(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidDataException("上传服务器没有返回文件确认信息。");
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("id", out JsonElement id)
                || id.ValueKind != JsonValueKind.String
                || !document.RootElement.TryGetProperty("sizeBytes", out JsonElement sizeBytes)
                || !sizeBytes.TryGetInt64(out long confirmedSize))
            {
                throw new InvalidDataException("上传服务器返回的文件确认信息不完整。");
            }
            string? value = id.GetString();
            string? reportId = CombatBugReportDescription.NormalizeDetail(value);
            if (string.IsNullOrWhiteSpace(reportId) || confirmedSize < 0)
                throw new InvalidDataException("上传服务器返回的文件确认信息无效。");
            return new ServerReceipt(reportId, confirmedSize);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("上传服务器返回的文件确认信息不是有效 JSON。", ex);
        }
    }

    private static string DescribeResponse(string body)
    {
        string description = CombatBugReportDescription.NormalizeDetail(body) ?? "无响应正文";
        return description.Length <= MaximumDisplayedResponseCharacters
            ? description
            : description[..MaximumDisplayedResponseCharacters] + "…";
    }

    private readonly record struct ServerReceipt(string ReportId, long SizeBytes);

    private sealed class ProgressFileContent(
        FileStream source,
        IProgress<CombatBugReportUploadProgress>? progress,
        CancellationToken requestCancellationToken) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = source.Length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => WriteContentAsync(stream, requestCancellationToken);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            if (!requestCancellationToken.CanBeCanceled
                || requestCancellationToken == cancellationToken)
            {
                return WriteContentAsync(stream, cancellationToken);
            }
            if (!cancellationToken.CanBeCanceled)
                return WriteContentAsync(stream, requestCancellationToken);
            return WriteContentWithLinkedCancellationAsync(stream, cancellationToken);
        }

        private async Task WriteContentWithLinkedCancellationAsync(
            Stream destination,
            CancellationToken transportCancellationToken)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                requestCancellationToken,
                transportCancellationToken);
            await WriteContentAsync(destination, linked.Token);
        }

        private async Task WriteContentAsync(Stream destination, CancellationToken cancellationToken)
        {
            long total = source.Length;
            long sent = 0;
            source.Position = 0;
            progress?.Report(new CombatBugReportUploadProgress(0, total));
            byte[] buffer = new byte[80 * 1024];
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sent += read;
                progress?.Report(new CombatBugReportUploadProgress(sent, total));
            }
        }
    }
}
