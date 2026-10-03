using System.Net;
using System.Net.Http;
using System.Text;
using System.IO.Compression;
using Godot;
using NetHttpClient = System.Net.Http.HttpClient;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertBugReportUploadBoundariesAsync()
    {
        AssertShowcaseModEligibility();
        if (!CombatBugReportUploader.ProductionTransportUsesDirectConnectionForTesting)
            throw new InvalidOperationException("正式上传仍然继承游戏进程代理设置。");
        string directory = ProjectSettings.GlobalizePath("user://combat-solver-test-upload");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"upload-{_request.RunId}.zip");
        string submissionId = Guid.NewGuid().ToString("N");
        const string description = "玩家描述";
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (Stream data = archive.CreateEntry("diagnostics/test.bin", CompressionLevel.NoCompression).Open())
                data.Write(new byte[200 * 1024]);
            using StreamWriter metadata = new(archive.CreateEntry("report.json").Open());
            metadata.Write(CombatBugReportMetadata.Serialize(submissionId, description, null,
                new CombatBugReportClassificationSnapshot(0, 0, 0, 0, 0, []), null));
        }
        int archiveBytes = checked((int)new FileInfo(path).Length);
        try
        {
            List<CombatBugReportUploadProgress> progress = [];
            using FakeUploadHandler successHandler = new(
                HttpStatusCode.Created,
                $"{{\"id\":\"server-report-1\",\"sizeBytes\":{archiveBytes}}}");
            using NetHttpClient successClient = new(successHandler);
            CombatBugReportUploadReceipt receipt = await CombatBugReportUploader.UploadForTestingAsync(
                successClient,
                path,
                description,
                "123456",
                submissionId,
                new DirectTestProgress<CombatBugReportUploadProgress>(progress.Add));
            if (receipt.ReportId != "server-report-1"
                || receipt.SubmissionId != submissionId
                || receipt.SizeBytes != archiveBytes
                || receipt.StatusCode != HttpStatusCode.Created
                || progress.Count == 0
                || progress[^1].Percentage != 100
                || !successHandler.SawMultipartReport
                || !successHandler.SawDescription
                || !successHandler.SawContact
                || !successHandler.SawMetadata
                || !successHandler.SawSubmissionId
                || !successHandler.SawUploadToken)
            {
                throw new InvalidOperationException(
                    "问题包上传成功回执、multipart 字段或传输进度不完整：" +
                    $"report_id={receipt.ReportId == "server-report-1"} " +
                    $"submission_id={receipt.SubmissionId == submissionId} " +
                    $"size={receipt.SizeBytes} " +
                    $"status={(int)receipt.StatusCode} " +
                    $"progress_count={progress.Count} " +
                    $"progress_last={(progress.Count == 0 ? -1 : progress[^1].Percentage)} " +
                    $"report={successHandler.SawMultipartReport} " +
                    $"description={successHandler.SawDescription} " +
                    $"contact={successHandler.SawContact} " +
                    $"token={successHandler.SawUploadToken}。");
            }

            using FakeUploadHandler numericIdHandler = new(
                HttpStatusCode.OK,
                $"{{\"id\":123,\"sizeBytes\":{archiveBytes}}}");
            using NetHttpClient numericIdClient = new(numericIdHandler);
            try
            {
                await CombatBugReportUploader.UploadForTestingAsync(
                    numericIdClient,
                    path,
                    description,
                    string.Empty,
                    submissionId);
                throw new InvalidOperationException("无效服务端编号被误报为上传成功。");
            }
            catch (InvalidDataException)
            {
            }

            using FakeUploadHandler arrayResponseHandler = new(HttpStatusCode.OK, "[]");
            using NetHttpClient arrayResponseClient = new(arrayResponseHandler);
            try
            {
                await CombatBugReportUploader.UploadForTestingAsync(
                    arrayResponseClient,
                    path,
                    description,
                    string.Empty,
                    submissionId);
                throw new InvalidOperationException("非对象服务端响应被误报为上传成功。");
            }
            catch (InvalidDataException)
            {
            }

            using FakeUploadHandler wrongSizeHandler = new(
                HttpStatusCode.Created,
                "{\"id\":\"server-report-2\",\"sizeBytes\":1}");
            using NetHttpClient wrongSizeClient = new(wrongSizeHandler);
            try
            {
                await CombatBugReportUploader.UploadForTestingAsync(
                    wrongSizeClient,
                    path,
                    description,
                    string.Empty,
                    submissionId);
                throw new InvalidOperationException("服务端文件大小不一致仍被误报为上传成功。");
            }
            catch (InvalidDataException)
            {
            }

            string oversizedResponse = new string('x', 70_000) + "\nUNBOUNDED_RESPONSE_TAIL";
            using FakeUploadHandler failureHandler = new(
                HttpStatusCode.InternalServerError,
                oversizedResponse);
            using NetHttpClient failureClient = new(failureHandler);
            try
            {
                await CombatBugReportUploader.UploadForTestingAsync(
                    failureClient,
                    path,
                    description,
                    string.Empty,
                    submissionId);
                throw new InvalidOperationException("服务端错误响应没有终止上传。");
            }
            catch (HttpRequestException ex)
            {
                if (ex.Message.Length > 1_100
                    || ex.Message.Contains("UNBOUNDED_RESPONSE_TAIL", StringComparison.Ordinal)
                    || ex.Message.Contains('\n'))
                {
                    throw new InvalidOperationException("服务端错误响应没有限制长度或折叠换行。", ex);
                }
            }

            using FakeUploadHandler unusedHandler = new(HttpStatusCode.OK, "{}");
            using NetHttpClient unusedClient = new(unusedHandler);
            try
            {
                await CombatBugReportUploader.UploadForTestingAsync(
                    unusedClient,
                    path,
                    new string('界', CombatBugReportUploader.MaximumDescriptionUtf8Bytes),
                    string.Empty,
                    submissionId);
                throw new InvalidOperationException("超长问题描述没有在发出请求前失败。");
            }
            catch (InvalidDataException)
            {
            }
            if (unusedHandler.RequestCount != 0)
                throw new InvalidOperationException("超长问题描述仍然访问了上传服务。");

            using CancellationTokenSource bodyCancellation = new();
            using LegacyCopyUploadHandler bodyCancellationHandler = new(archiveBytes);
            using NetHttpClient bodyCancellationClient = new(bodyCancellationHandler);
            Task<CombatBugReportUploadReceipt> bodyCancellationTask =
                CombatBugReportUploader.UploadForTestingAsync(
                    bodyCancellationClient,
                    path,
                    description,
                    string.Empty,
                    submissionId,
                    new DirectTestProgress<CombatBugReportUploadProgress>(value =>
                    {
                        if (value.BytesSent > 0)
                            bodyCancellation.Cancel();
                    }),
                    bodyCancellation.Token);
            await AssertUploadCanceledPromptlyAsync(
                bodyCancellationTask,
                bodyCancellation,
                "无取消参数的旧传输入口");

            using CancellationTokenSource confirmationCancellation = new();
            using AwaitingConfirmationUploadHandler confirmationHandler = new();
            using NetHttpClient confirmationClient = new(confirmationHandler);
            List<CombatBugReportUploadProgress> confirmationProgress = [];
            Task<CombatBugReportUploadReceipt> confirmationTask =
                CombatBugReportUploader.UploadForTestingAsync(
                    confirmationClient,
                    path,
                    description,
                    string.Empty,
                    submissionId,
                    new DirectTestProgress<CombatBugReportUploadProgress>(confirmationProgress.Add),
                    confirmationCancellation.Token);
            await confirmationHandler.BodyCopied.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (confirmationProgress.Count == 0 || confirmationProgress[^1].Percentage != 100)
                throw new InvalidOperationException("等待服务端确认前没有完成文件字节进度。");
            confirmationCancellation.Cancel();
            await AssertUploadCanceledPromptlyAsync(
                confirmationTask,
                confirmationCancellation,
                "等待服务端确认阶段");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertShowcaseModEligibility()
    {
        ShowcaseModDeclaration[] auxiliaryStack =
        [
            new("CombatSolver", "战斗路线求解器", false),
            new("STS2-RitsuLib", "RitsuLib", false),
            new("CombatShowcaseRecorder", "录像对局库", false),
            new("STS2-RitsuMetrics", "RitsuMetrics", false),
            new("QuickSL", "QuickSL", false),
            new("Loadout", "Loadout", true),
            new("LegacyRng107Compat", "RNG 兼容层", true),
        ];
        if (CombatShowcaseModEligibility.FindGameplayModificationNames(auxiliaryStack).Length != 0)
            throw new InvalidOperationException("录像对局错误拒绝了辅助 Mod。");

        ShowcaseModDeclaration[] gameplayStack =
        [
            .. auxiliaryStack,
            new("NewCharacter", "新角色", true),
            new("BalanceRewrite", "数值重制", true),
        ];
        string[] rejected = CombatShowcaseModEligibility.FindGameplayModificationNames(gameplayStack);
        if (rejected.Length != 2
            || !rejected.Contains("新角色 (NewCharacter)", StringComparer.Ordinal)
            || !rejected.Contains("数值重制 (BalanceRewrite)", StringComparer.Ordinal))
            throw new InvalidOperationException("录像对局没有拒绝玩法修改 Mod。");
    }

    private sealed class DirectTestProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static async Task AssertUploadCanceledPromptlyAsync(
        Task<CombatBugReportUploadReceipt> uploadTask,
        CancellationTokenSource cancellation,
        string stage)
    {
        try
        {
            await uploadTask.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException($"{stage}取消后仍返回成功。");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException($"{stage}取消后两秒内没有结束。", ex);
        }
    }

    private sealed class FakeUploadHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public bool SawMultipartReport { get; private set; }
        public bool SawDescription { get; private set; }
        public bool SawContact { get; private set; }
        public bool SawUploadToken { get; private set; }
        public bool SawMetadata { get; private set; }
        public bool SawSubmissionId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            SawUploadToken = request.Headers.Contains("X-CombatSolver-Key");
            if (request.Content is MultipartFormDataContent multipart)
            {
                string[] names = multipart
                    .Select(content => content.Headers.ContentDisposition?.Name?.Trim('"'))
                    .Where(name => name != null)
                    .Cast<string>()
                    .ToArray();
                SawMultipartReport = names.Contains("report", StringComparer.Ordinal);
                SawDescription = names.Contains("description", StringComparer.Ordinal);
                SawContact = names.Contains("contact", StringComparer.Ordinal);
                SawMetadata = names.Contains("metadata", StringComparer.Ordinal);
                SawSubmissionId = names.Contains("submissionId", StringComparer.Ordinal);
            }
            using MemoryStream serialized = new();
            if (request.Content != null)
                await request.Content.CopyToAsync(serialized, cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8),
            };
        }
    }

    private sealed class LegacyCopyUploadHandler(int archiveBytes) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using MemoryStream serialized = new();
            if (request.Content != null)
                await request.Content.CopyToAsync(serialized);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    $"{{\"id\":\"legacy-copy\",\"sizeBytes\":{archiveBytes}}}",
                    Encoding.UTF8),
            };
        }
    }

    private sealed class AwaitingConfirmationUploadHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> BodyCopied { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content != null)
                await request.Content.CopyToAsync(Stream.Null, cancellationToken);
            BodyCopied.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("等待服务端确认的测试处理器意外恢复。");
        }
    }
}
