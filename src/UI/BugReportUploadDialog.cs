using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization.Fonts;

namespace CombatSolver;

internal sealed partial class BugReportUploadDialog : CanvasLayer
{
    private const float ViewportMargin = 16f;
    private const float PreferredWidth = 420f;
    private static int _openCount;
    private readonly PanelContainer _dialogPanel;
    private readonly TextEdit _description;
    private readonly ScrollContainer _contentScroll;
    private readonly VBoxContainer _content;
    private bool _dragging;
    private bool _closed;
    private bool _truncatingDescription;
    private Vector2 _dragOffset;

    public event Action<string>? UploadConfirmed;
    public event Action? DialogClosed;
    internal static bool IsOpen => _openCount > 0;

    public BugReportUploadDialog(string contactQq)
    {
        Name = "BugReportUploadDialog";
        Layer = 130;

        ColorRect backdrop = new()
        {
            Color = new Color(0f, 0f, 0f, 0.55f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(backdrop);

        _dialogPanel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(PreferredWidth, 0),
        };
        _dialogPanel.AddThemeStyleboxOverride("panel", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Surface,
            SolverUiTokens.Palette.Border,
            SolverUiTokens.Radius.Large,
            SolverUiTokens.Spacing.Lg,
            SolverUiTokens.Spacing.Lg,
            shadow: true));
        backdrop.AddChild(_dialogPanel);

        VBoxContainer shell = new()
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        shell.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _dialogPanel.AddChild(shell);

        shell.AddChild(CreateHeaderRow());

        ColorRect divider = new()
        {
            Color = SolverUiTokens.Palette.BorderSubtle,
            CustomMinimumSize = new Vector2(0, 1),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        shell.AddChild(divider);

        _contentScroll = new ScrollContainer
        {
            Name = "ContentScroll",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };
        _content = new VBoxContainer
        {
            Name = "Content",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _content.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        _contentScroll.AddChild(_content);
        shell.AddChild(_contentScroll);

        _content.AddChild(SolverUiTokens.CreateLabel(
            SolverText.Format($"问题描述（选填，最多 {CombatBugReportDescription.MaximumPlayerDescriptionCharacters} 字）"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextSecondary));
        _description = new TextEdit
        {
            CustomMinimumSize = new Vector2(0, 130),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _description.TextChanged += OnDescriptionTextChanged;
        _description.AddThemeFontSizeOverride("font_size", SolverUiTokens.Type.Body);
        _description.AddThemeColorOverride("font_color", SolverUiTokens.Palette.TextPrimary);
        _description.AddThemeStyleboxOverride("normal", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.Background,
            SolverUiTokens.Palette.BorderSubtle,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        _description.AddThemeStyleboxOverride("focus", SolverUiTokens.CreateBox(
            SolverUiTokens.Palette.SurfaceRaised,
            SolverUiTokens.Palette.Accent,
            SolverUiTokens.Radius.Small,
            SolverUiTokens.Spacing.Sm,
            SolverUiTokens.Spacing.Xs));
        _content.AddChild(_description);

        Label contactHint = SolverUiTokens.CreateLabel(
            string.IsNullOrWhiteSpace(contactQq)
                ? SolverText.Get("未设置联系QQ；可在“求解器设置”里填写“反馈联系QQ”，以后自动带上。")
                : SolverText.Format($"联系QQ：{contactQq}（可在“求解器设置”里修改）"),
            SolverUiTokens.Type.Caption,
            SolverUiTokens.Palette.TextMuted);
        contactHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _content.AddChild(contactHint);

        HBoxContainer buttons = new()
        {
            Alignment = BoxContainer.AlignmentMode.End,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        buttons.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        Button cancel = SolverUiTokens.CreateButton(SolverText.Get("取消"), SolverButtonStyle.Secondary);
        cancel.CustomMinimumSize = new Vector2(88, SolverUiTokens.Size.ButtonHeight);
        cancel.Pressed += Close;
        buttons.AddChild(cancel);
        Button confirm = SolverUiTokens.CreateButton(SolverText.Get("确认上传"), SolverButtonStyle.Danger);
        confirm.CustomMinimumSize = new Vector2(110, SolverUiTokens.Size.ButtonHeight);
        confirm.Pressed += () =>
        {
            UploadConfirmed?.Invoke(_description.Text);
            Close();
        };
        buttons.AddChild(confirm);
        shell.AddChild(buttons);
    }

    public override void _Ready()
    {
        GetViewport().SizeChanged += OnViewportSizeChanged;
        TaskHelper.RunSafely(ApplyViewportBoundsAsync(center: true));
    }

    public override void _EnterTree()
        => _openCount++;

    private async Task ApplyViewportBoundsAsync(bool center)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!GodotObject.IsInstanceValid(this) || !GodotObject.IsInstanceValid(_dialogPanel))
            return;
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        Vector2 naturalSize = _dialogPanel.GetCombinedMinimumSize();
        Vector2 targetSize = ResolveDialogSize(viewportSize, naturalSize);
        _dialogPanel.CustomMinimumSize = targetSize;
        _dialogPanel.Size = targetSize;
        _dialogPanel.Position = center
            ? ((viewportSize - targetSize) / 2f).Round()
            : ClampPosition(_dialogPanel.Position, viewportSize, targetSize);
    }

    private void OnViewportSizeChanged()
        => TaskHelper.RunSafely(ApplyViewportBoundsAsync(center: false));

    private static Vector2 ResolveDialogSize(Vector2 viewportSize, Vector2 naturalSize)
    {
        float maximumWidth = Math.Max(0f, viewportSize.X - ViewportMargin * 2f);
        float maximumHeight = Math.Max(0f, viewportSize.Y - ViewportMargin * 2f);
        return new Vector2(
            Math.Min(PreferredWidth, maximumWidth),
            Math.Min(naturalSize.Y, maximumHeight));
    }

    private static Vector2 ClampPosition(Vector2 position, Vector2 viewportSize, Vector2 panelSize)
        => new(
            Math.Clamp(position.X, 0f, Math.Max(0f, viewportSize.X - panelSize.X)),
            Math.Clamp(position.Y, 0f, Math.Max(0f, viewportSize.Y - panelSize.Y)));

    private void OnHeaderGuiInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            _dragging = button.Pressed;
            if (_dragging)
                _dragOffset = GetViewport().GetMousePosition() - _dialogPanel.Position;
            return;
        }
        if (!_dragging || inputEvent is not InputEventMouseMotion)
            return;
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        Vector2 position = GetViewport().GetMousePosition() - _dragOffset;
        _dialogPanel.Position = ClampPosition(position, viewportSize, _dialogPanel.Size);
    }

    private Control CreateHeaderRow()
    {
        HBoxContainer header = new()
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Move,
        };
        header.AddThemeConstantOverride("separation", SolverUiTokens.Spacing.Sm);
        header.GuiInput += OnHeaderGuiInput;

        ColorRect marker = new()
        {
            Color = SolverUiTokens.Palette.Accent,
            CustomMinimumSize = new Vector2(4, 20),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        header.AddChild(marker);

        Label title = SolverUiTokens.CreateLabel(
            SolverText.Get("上传问题包"),
            SolverUiTokens.Type.Title,
            SolverUiTokens.Palette.TextPrimary,
            FontType.Bold);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(title);

        Button close = SolverUiTokens.CreateButton("×", SolverButtonStyle.Secondary);
        close.CustomMinimumSize = new Vector2(28, SolverUiTokens.Size.ButtonHeight);
        close.Pressed += Close;
        header.AddChild(close);

        return header;
    }

    public override void _ExitTree()
    {
        if (GodotObject.IsInstanceValid(GetViewport()))
            GetViewport().SizeChanged -= OnViewportSizeChanged;
        _openCount--;
        if (_closed)
            return;
        _closed = true;
        DialogClosed?.Invoke();
    }

    private void OnDescriptionTextChanged()
    {
        if (_truncatingDescription
            || _description.Text.Length <= CombatBugReportDescription.MaximumPlayerDescriptionCharacters)
        {
            return;
        }
        _truncatingDescription = true;
        _description.Text = _description.Text[..CombatBugReportDescription.MaximumPlayerDescriptionCharacters];
        _truncatingDescription = false;
    }

    private void Close() => QueueFree();

    internal bool ExerciseResponsiveBoundsForTesting()
    {
        foreach (Vector2 viewport in new[]
                 {
                     new Vector2(1920, 1080),
                     new Vector2(1280, 720),
                     new Vector2(960, 540),
                 })
        {
            Vector2 size = ResolveDialogSize(viewport, new Vector2(PreferredWidth, 900));
            Vector2 position = ClampPosition(new Vector2(9999, 9999), viewport, size);
            if (size.X > viewport.X - ViewportMargin * 2f
                || size.Y > viewport.Y - ViewportMargin * 2f
                || position.X + size.X > viewport.X
                || position.Y + size.Y > viewport.Y)
            {
                return false;
            }
        }
        return _contentScroll.VerticalScrollMode == ScrollContainer.ScrollMode.Auto
            && _contentScroll.GetParent() == _dialogPanel.GetChild(0)
            && _content.GetParent() == _contentScroll;
    }
}
