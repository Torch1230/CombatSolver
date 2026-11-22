namespace CombatSolver;

internal sealed class SearchTakeoverAttemptGate
{
    private int _backendAttempt;
    private bool _progressPublished;
    private int? _publishedPreviewAttempt;
    private int? _renderedPreviewAttempt;

    public int CurrentBackendAttempt => Volatile.Read(ref _backendAttempt);

    public int? RenderedTurnPreviewBackendAttempt =>
        _renderedPreviewAttempt is { } attempt
        && attempt == _backendAttempt
        && _publishedPreviewAttempt == _backendAttempt
            ? attempt
            : null;

    public void Reset()
    {
        _backendAttempt = 0;
        _progressPublished = false;
        _publishedPreviewAttempt = null;
        _renderedPreviewAttempt = null;
    }

    public bool RecordPublishedProgress(int backendAttempt, bool hasCurrentTurnPreview)
    {
        if (backendAttempt != _backendAttempt)
            return false;
        _progressPublished = true;
        _publishedPreviewAttempt = hasCurrentTurnPreview ? backendAttempt : null;
        return true;
    }

    public bool RecordRenderedProgress(int backendAttempt, bool hasCurrentTurnPreview)
    {
        if (backendAttempt != _backendAttempt)
            return false;
        _renderedPreviewAttempt = hasCurrentTurnPreview ? backendAttempt : null;
        return true;
    }

    public void AdvanceAttemptOnFallback(int failedBackendAttempt, bool clearRenderedPreview = true)
    {
        if (_backendAttempt == failedBackendAttempt)
            _backendAttempt++;
        _publishedPreviewAttempt = null;
        if (clearRenderedPreview)
            _renderedPreviewAttempt = null;
    }

    public bool CanRequestApplyCurrentTurn(int? expectedBackendAttempt)
    {
        if (expectedBackendAttempt is { } expected && expected != _backendAttempt)
            return false;
        if (!expectedBackendAttempt.HasValue && !_progressPublished && _backendAttempt == 0)
            return true;
        return _publishedPreviewAttempt == _backendAttempt
            && _renderedPreviewAttempt == _backendAttempt;
    }
}
