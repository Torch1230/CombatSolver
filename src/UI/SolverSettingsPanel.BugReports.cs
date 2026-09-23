using Godot;
using MegaCrit.Sts2.Core.Helpers;

namespace CombatSolver;

internal sealed partial class SolverSettingsPanel
{
    private CheckButton _detailedDiagnosticLogs = null!;
    private Button _exportBugReport = null!;
    private Button _uploadBugReport = null!;
    private ProgressBar _uploadProgress = null!;
    private BugReportUploadDialog? _uploadDialog;
    private CancellationTokenSource? _uploadCancellation;
    private volatile bool _exportInProgress;
    private volatile bool _uploadInProgress;
    private volatile bool _uploadCancelRequested;
    private long _uploadBytesSent;
    private long _uploadTotalBytes;
    private int _lastRenderedUploadPercentage = -1;
    private double _uploadProgressTarget;
    private string? _uploadSubmissionId;
    private UploadCompletion? _uploadCompletion;

    internal bool UploadProgressConfiguredForTesting
        => !_uploadProgress.Visible
           && _uploadProgress.MinValue == 0
           && _uploadProgress.MaxValue == 100
           && !_uploadProgress.ShowPercentage
           && MapUploadProgressBarValue(100) == 95
           && FormatUploadProgressStatus(1024, 1024, 100).Contains(
               SolverText.IsEnglish ? "waiting for server confirmation" : "等待服务器确认",
               StringComparison.Ordinal)
           && _uploadBugReport.Text == SolverText.Get("上传问题包");

    public override void _Process(double delta)
    {
        if (!_uploadInProgress)
            return;
        if (TryApplyUploadCompletion())
            return;
        if (!_uploadCancelRequested)
            RefreshUploadProgressTarget();
        // Byte counts arrive in bursts; the bar eases toward the latest percentage between them.
        if (_uploadProgress.Value != _uploadProgressTarget)
        {
            _uploadProgress.Value = SolverUiMotion.Approach(
                _uploadProgress.Value,
                _uploadProgressTarget,
                SolverUiMotion.Blend(delta, SolverUiMotion.ReadoutTimeConstantSeconds),
                0.05d);
        }
    }

    private void RefreshUploadProgressTarget()
    {
        long total = Interlocked.Read(ref _uploadTotalBytes);
        long sent = Interlocked.Read(ref _uploadBytesSent);
        if (total <= 0)
            return;
        int percentage = (int)Math.Clamp(sent * 100L / total, 0L, 100L);
        if (percentage == _lastRenderedUploadPercentage)
            return;
        _lastRenderedUploadPercentage = percentage;
        _uploadProgressTarget = MapUploadProgressBarValue(percentage);
        SetStatus(
            FormatUploadProgressStatus(sent, total, percentage),
            SolverUiTokens.Palette.TextSecondary);
    }

    public override void _ExitTree()
    {
        _uploadCancellation?.Cancel();
        if (_uploadDialog != null && GodotObject.IsInstanceValid(_uploadDialog))
            _uploadDialog.QueueFree();
    }

    internal bool ExerciseUploadCompletionTransitionForTesting()
    {
        if (_uploadInProgress || _exportInProgress || HasOpenUploadDialog())
            return false;

        CancellationTokenSource successCancellation = new();
        _uploadInProgress = true;
        _uploadCancelRequested = false;
        _uploadCancellation = successCancellation;
        _uploadSubmissionId = "test_success";
        SetProcess(true);
        RefreshBugReportControls();
        PublishUploadCompletion(new UploadCompletion(
            UploadCompletionKind.Succeeded,
            "测试上传成功",
            1024));
        bool successRemainsActiveUntilConsumed = _uploadInProgress
                                                 && _uploadBugReport.Text == SolverText.Get("取消上传");
        bool successApplied = TryApplyUploadCompletion();
        bool successReturnedToIdle = successApplied
                                     && !_uploadInProgress
                                     && _uploadCancellation == null
                                     && !_uploadProgress.Visible
                                     && _uploadBugReport.Text == SolverText.Get("上传问题包");

        CancellationTokenSource canceledCancellation = new();
        _uploadInProgress = true;
        _uploadCancelRequested = true;
        _uploadCancellation = canceledCancellation;
        _uploadSubmissionId = "test_canceled";
        SetProcess(true);
        RefreshBugReportControls();
        PublishUploadCompletion(new UploadCompletion(
            UploadCompletionKind.Canceled,
            "测试上传已取消",
            0));
        bool cancellationRemainsActiveUntilConsumed = _uploadInProgress
                                                      && _uploadBugReport.Disabled
                                                      && _uploadBugReport.Text == SolverText.Get("正在取消…");
        bool cancellationApplied = TryApplyUploadCompletion();
        bool cancellationReturnedToIdle = cancellationApplied
                                          && !_uploadInProgress
                                          && _uploadCancellation == null
                                          && !_uploadProgress.Visible
                                          && _uploadBugReport.Text == SolverText.Get("上传问题包");
        return successRemainsActiveUntilConsumed
               && successReturnedToIdle
               && cancellationRemainsActiveUntilConsumed
               && cancellationReturnedToIdle;
    }

    private Control CreateBugReportsPage()
    {
        VBoxContainer content = CreatePageContent("BugReportSettingsPage");
        content.AddChild(CreateSectionHeading(SolverText.Get("问题反馈")));
        GridContainer feedbackGrid = CreateSettingsGrid();
        _detailedDiagnosticLogs = CreateToggle();
        _detailedDiagnosticLogs.Toggled += OnDetailedDiagnosticLogsToggled;
        AddBasicRow(
            feedbackGrid,
            SolverText.Get("搜索分支调试日志"),
            _detailedDiagnosticLogs,
            SolverText.Get("每场战斗默认记录操作、选中路线与错误证据。此开关额外记录搜索候选细节，会增加开销并将并行搜索切为单线程，仅排查时开启。"));
        AddBasicRow(
            feedbackGrid,
            SolverText.Get("反馈联系QQ（选填）"),
            CreateContactQqInput(),
            SolverText.Get("上传问题包时随附，方便开发者回访；只需填一次，上传弹窗会自动带上。"));
        content.AddChild(feedbackGrid);

        HBoxContainer actions = new()
        {
            MouseFilter = MouseFilterEnum.Pass,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        actions.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _uploadBugReport = SolverUiTokens.CreateButton(SolverText.Get("上传问题包"), SolverButtonStyle.Primary);
        _uploadBugReport.CustomMinimumSize = new Vector2(150, SolverUiTokens.Size.ButtonHeight);
        _uploadBugReport.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _uploadBugReport.Pressed += OnUploadBugReportPressed;
        actions.AddChild(_uploadBugReport);
        _exportBugReport = SolverUiTokens.CreateButton(SolverText.Get("导出问题包"), SolverButtonStyle.Secondary);
        _exportBugReport.CustomMinimumSize = new Vector2(150, SolverUiTokens.Size.ButtonHeight);
        _exportBugReport.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _exportBugReport.Pressed += OnExportBugReportPressed;
        actions.AddChild(_exportBugReport);
        content.AddChild(actions);

        _uploadProgress = new ProgressBar
        {
            Visible = false,
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 18),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        content.AddChild(_uploadProgress);
        return CreatePageScroll(content);
    }

    private void ReloadBugReportsPage(SolverSettingsData data)
        => _detailedDiagnosticLogs.ButtonPressed = data.EnableDetailedDiagnosticLogs;

    private LineEdit CreateContactQqInput()
    {
        LineEdit input = CreateInput(SolverText.Get("未设置"));
        _reloadInputs.Add(data => input.Text = data.ReporterContactQq ?? string.Empty);
        bool Commit()
        {
            string text = input.Text.Trim();
            if (text.Length > 64)
            {
                ShowInvalid(input, SolverText.Get("联系QQ最长 64 个字符"));
                return false;
            }
            return SaveSetting(
                input,
                SolverSettings.Current with
                {
                    ReporterContactQq = text.Length == 0 ? null : text,
                },
                SolverText.Get("反馈联系方式已保存"));
        }
        input.FocusExited += () => Commit();
        input.TextSubmitted += _ => Commit();
        _commitInputs.Add(Commit);
        return input;
    }

    private void OnDetailedDiagnosticLogsToggled(bool enabled)
    {
        if (_loading)
            return;
        SolverSettings.Update(SolverSettings.Current with { EnableDetailedDiagnosticLogs = enabled });
        SetStatus(SolverText.Get("已保存，下次搜索生效"), SolverUiTokens.Palette.Success);
    }

    private void OnExportBugReportPressed()
    {
        if (_exportInProgress || _uploadInProgress || HasOpenUploadDialog())
            return;
        _exportInProgress = true;
        RefreshBugReportControls();
        SetStatus(SolverText.Get("正在打包日志和当前战斗…"), SolverUiTokens.Palette.TextSecondary);
        TaskHelper.RunSafely(ExportBugReportAsync());
    }

    private async Task ExportBugReportAsync()
    {
        try
        {
            string path = await CombatBugReportExporter.ExportCurrentAsync();
            PostUi(() =>
            {
                OS.ShellShowInFileManager(path);
                SetStatus(SolverText.Format($"已导出到桌面：{Path.GetFileName(path)}"), SolverUiTokens.Palette.Success);
            });
        }
        catch (Exception ex)
        {
            Entry.Logger.Error($"[CombatSolver/Test] BUG_REPORT_EXPORT_FAILED exception={ex}");
            PostUi(() => SetStatus(
                SolverText.Format($"导出失败：{DescribeUiFailure(ex)}"),
                SolverUiTokens.Palette.Danger));
        }
        finally
        {
            _exportInProgress = false;
            PostUi(RefreshBugReportControls);
        }
    }

    private void OnUploadBugReportPressed()
    {
        if (_uploadInProgress)
        {
            _uploadCancelRequested = true;
            _uploadCancellation?.Cancel();
            Entry.Logger.Info(
                $"[CombatSolver/Test] BUG_REPORT_UPLOAD_CANCEL_REQUESTED submission_id={_uploadSubmissionId ?? "unknown"}");
            SetStatus(SolverText.Get("正在取消上传…"), SolverUiTokens.Palette.Warning);
            RefreshBugReportControls();
            return;
        }
        if (_exportInProgress || HasOpenUploadDialog())
            return;
        _uploadProgress.Visible = false;
        BugReportUploadDialog dialog = new(SolverSettings.Current.ReporterContactQq ?? string.Empty);
        _uploadDialog = dialog;
        dialog.UploadConfirmed += OnUploadConfirmed;
        dialog.DialogClosed += () => OnUploadDialogClosed(dialog);
        GetTree().Root.AddChild(dialog);
        RefreshBugReportControls();
    }

    private void OnUploadConfirmed(string description)
    {
        if (_uploadInProgress || _exportInProgress)
            return;
        _uploadInProgress = true;
        _uploadCancelRequested = false;
        _uploadCancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _uploadCompletion, null);
        Interlocked.Exchange(ref _uploadBytesSent, 0);
        Interlocked.Exchange(ref _uploadTotalBytes, 0);
        _lastRenderedUploadPercentage = -1;
        _uploadProgress.Value = 0;
        _uploadProgressTarget = 0;
        _uploadProgress.Visible = true;
        SetProcess(true);
        RefreshBugReportControls();
        SetStatus(SolverText.Get("正在打包问题包…"), SolverUiTokens.Palette.TextSecondary);
        string submissionId = Guid.NewGuid().ToString("N");
        _uploadSubmissionId = submissionId;
        TaskHelper.RunSafely(UploadBugReportAsync(
            description,
            submissionId,
            _uploadCancellation.Token));
    }

    private async Task UploadBugReportAsync(
        string description,
        string submissionId,
        CancellationToken cancellationToken)
    {
        string? path = null;
        try
        {
            path = await CombatBugReportExporter.ExportCurrentAsync(playerDescription: description, submissionId: submissionId);
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo archive = new(path);
            Interlocked.Exchange(ref _uploadBytesSent, 0);
            Interlocked.Exchange(ref _uploadTotalBytes, archive.Length);
            Entry.Logger.Info(
                $"[CombatSolver/Test] BUG_REPORT_UPLOAD_STARTED submission_id={submissionId} zip_bytes={archive.Length}");
            string contactQq = SolverSettings.Current.ReporterContactQq ?? string.Empty;
            DirectProgress<CombatBugReportUploadProgress> progress = new(value =>
            {
                Interlocked.Exchange(ref _uploadBytesSent, value.BytesSent);
                Interlocked.Exchange(ref _uploadTotalBytes, value.TotalBytes);
            });
            CombatBugReportUploadReceipt receipt = await CombatBugReportUploader.UploadAsync(
                path,
                description,
                contactQq,
                submissionId,
                progress,
                cancellationToken);
            Entry.Logger.Info(
                $"[CombatSolver/Test] BUG_REPORT_UPLOAD_CONFIRMED submission_id={submissionId} report_id={receipt.ReportId} status={(int)receipt.StatusCode} zip_bytes={receipt.SizeBytes}");
            string? cleanupWarning = DeleteUploadedArchive(path);
            PublishUploadCompletion(new UploadCompletion(
                UploadCompletionKind.Succeeded,
                cleanupWarning == null
                    ? SolverText.Format($"已上传 {FormatByteCount(receipt.SizeBytes)}，反馈编号：{receipt.ReportId}")
                    : SolverText.Format($"已上传 {FormatByteCount(receipt.SizeBytes)}，反馈编号：{receipt.ReportId}；{cleanupWarning}"),
                receipt.SizeBytes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Entry.Logger.Info($"[CombatSolver/Test] BUG_REPORT_UPLOAD_CANCELED submission_id={submissionId}");
            PublishUploadCompletion(new UploadCompletion(
                UploadCompletionKind.Canceled,
                path == null
                    ? SolverText.Get("上传已取消")
                    : SolverText.Get("上传已取消；未收到服务器确认，问题包已保留"),
                0));
        }
        catch (TaskCanceledException ex)
        {
            Entry.Logger.Error(
                $"[CombatSolver/Test] BUG_REPORT_UPLOAD_UNCONFIRMED submission_id={submissionId} exception={ex}");
            PublishUploadCompletion(new UploadCompletion(
                UploadCompletionKind.Failed,
                SolverText.Get("上传结果未确认；问题包已保留，请勿立即重复提交"),
                0));
        }
        catch (Exception ex)
        {
            Entry.Logger.Error(
                $"[CombatSolver/Test] BUG_REPORT_UPLOAD_FAILED submission_id={submissionId} exception={ex}");
            PublishUploadCompletion(new UploadCompletion(
                UploadCompletionKind.Failed,
                path == null
                    ? SolverText.Format($"打包失败：{DescribeUiFailure(ex)}")
                    : SolverText.Format($"上传未完成：{DescribeUiFailure(ex)}（问题包已保留）"),
                0));
        }
    }

    private void PublishUploadCompletion(UploadCompletion completion)
        => Interlocked.Exchange(ref _uploadCompletion, completion);

    private bool TryApplyUploadCompletion()
    {
        UploadCompletion? completion = Interlocked.Exchange(ref _uploadCompletion, null);
        if (completion == null)
            return false;
        string submissionId = _uploadSubmissionId ?? "unknown";
        _uploadInProgress = false;
        _uploadCancelRequested = false;
        CancellationTokenSource? completedCancellation = _uploadCancellation;
        _uploadCancellation = null;
        _uploadSubmissionId = null;
        completedCancellation?.Dispose();
        SetProcess(false);
        _uploadProgress.Value = 0;
        _uploadProgressTarget = 0;
        _uploadProgress.Visible = false;
        SetStatus(
            completion.Message,
            completion.Kind switch
            {
                UploadCompletionKind.Succeeded => SolverUiTokens.Palette.Success,
                UploadCompletionKind.Canceled => SolverUiTokens.Palette.Warning,
                _ => SolverUiTokens.Palette.Danger,
            });
        RefreshBugReportControls();
        Entry.Logger.Info(
            $"[CombatSolver/Test] BUG_REPORT_UPLOAD_UI_COMPLETED submission_id={submissionId} kind={completion.Kind} confirmed_bytes={completion.ConfirmedBytes}");
        return true;
    }

    private void OnUploadDialogClosed(BugReportUploadDialog dialog)
    {
        if (!ReferenceEquals(_uploadDialog, dialog))
            return;
        _uploadDialog = null;
        if (CanUpdateUi())
            RefreshBugReportControls();
    }

    private bool HasOpenUploadDialog()
        => _uploadDialog != null && GodotObject.IsInstanceValid(_uploadDialog);

    private void RefreshBugReportControls()
    {
        bool dialogOpen = HasOpenUploadDialog();
        _exportBugReport.Disabled = _exportInProgress || _uploadInProgress || dialogOpen;
        if (_uploadInProgress)
        {
            _uploadBugReport.Disabled = _uploadCancelRequested;
            _uploadBugReport.Text = _uploadCancelRequested ? SolverText.Get("正在取消…") : SolverText.Get("取消上传");
            SolverUiTokens.ApplyButtonStyle(_uploadBugReport, SolverButtonStyle.Danger);
            return;
        }
        _uploadBugReport.Disabled = _exportInProgress || dialogOpen;
        _uploadBugReport.Text = SolverText.Get("上传问题包");
        SolverUiTokens.ApplyButtonStyle(_uploadBugReport, SolverButtonStyle.Primary);
    }

    private bool CanUpdateUi()
        => GodotObject.IsInstanceValid(this)
           && GodotObject.IsInstanceValid(_status)
           && GodotObject.IsInstanceValid(_uploadBugReport)
           && GodotObject.IsInstanceValid(_uploadProgress);

    private void PostUi(Action action)
        => SolverDispatcher.Post(() =>
        {
            if (CanUpdateUi())
                action();
        });

    private static string? DeleteUploadedArchive(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Entry.Logger.Warn($"[CombatSolver/Test] BUG_REPORT_UPLOAD_CLEANUP_FAILED path={path} exception={ex}");
            return SolverText.Get("本地问题包未能删除");
        }
    }

    private static string DescribeUiFailure(Exception exception)
        => CombatBugReportDescription.NormalizeDetail(exception.GetBaseException().Message)
           ?? exception.GetType().Name;

    private static string FormatByteCount(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024L * 1024)
            return $"{bytes / 1024d:0.##} KB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024):0.##} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
    }

    private static double MapUploadProgressBarValue(int transmittedPercentage)
        => transmittedPercentage >= 100
            ? 95
            : Math.Min(94, transmittedPercentage * 0.95);

    private static string FormatUploadProgressStatus(long sent, long total, int percentage)
        => sent >= total
            ? SolverText.Format($"已发送 {FormatByteCount(total)}，正在等待服务器确认…")
            : SolverText.Format($"正在上传… {FormatByteCount(sent)} / {FormatByteCount(total)}（{percentage}%）");

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private enum UploadCompletionKind
    {
        Succeeded,
        Canceled,
        Failed,
    }

    private sealed record UploadCompletion(
        UploadCompletionKind Kind,
        string Message,
        long ConfirmedBytes);
}
