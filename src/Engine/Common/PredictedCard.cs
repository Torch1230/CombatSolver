using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.Common;

internal sealed class PredictedCard : IComparable<PredictedCard>
{
    private sealed class PreviewStorage(CardModel original, CardModel? preview)
    {
        public CardModel Original { get; } = original;
        public CardModel? Preview { get; } = preview;
        public volatile bool Shared;

        private bool _hasCachedFingerprint;
        private ulong _cachedFingerprintFirst;
        private ulong _cachedFingerprintSecond;
        private string? _cachedChoiceKey;

        public bool TryGetCachedFingerprint(out ulong first, out ulong second)
        {
            if (!Volatile.Read(ref _hasCachedFingerprint))
            {
                first = 0;
                second = 0;
                return false;
            }
            first = _cachedFingerprintFirst;
            second = _cachedFingerprintSecond;
            return true;
        }

        public void SetCachedFingerprint(ulong first, ulong second)
        {
            _cachedFingerprintFirst = first;
            _cachedFingerprintSecond = second;
            Volatile.Write(ref _hasCachedFingerprint, true);
        }

        public bool TryGetCachedChoiceKey(out string key)
        {
            string? cached = Volatile.Read(ref _cachedChoiceKey);
            key = cached ?? string.Empty;
            return cached is not null;
        }

        public void SetCachedChoiceKey(string key)
            => Volatile.Write(ref _cachedChoiceKey, key);

        public void InvalidateCaches()
        {
            Volatile.Write(ref _hasCachedFingerprint, false);
            Volatile.Write(ref _cachedChoiceKey, null);
        }
    }

    private PreviewStorage _previewStorage;
    private SimCardPile? _ownerPile;
    private Action? _mutationObserver;
    private bool _observeEveryPreviewMutation;
    private bool _isolateAttachedModelsOnFork;

    public PredictedCard(CardModel original, CardModel? preview = null)
    {
        _previewStorage = new PreviewStorage(original, preview);
    }

    private PredictedCard(PreviewStorage previewStorage)
        => _previewStorage = previewStorage;

    public CardModel Original => _previewStorage.Original;

    public CardModel Preview => _previewStorage.Preview ?? _previewStorage.Original;

    internal SimCardPile? OwnerPile => _ownerPile;

    internal bool HasExternallyMutableAttachedModels => _isolateAttachedModelsOnFork;

    public CardModel MutablePreview
    {
        get
        {
            _ownerPile?.InvalidateFingerprint();
            if (_observeEveryPreviewMutation)
                NotifyHookListenerStructureChanged();
            CardModel? preview = _previewStorage.Preview;
            if (preview is null)
            {
                preview = PredictionUtils.CloneCardStateForSimulation(_previewStorage.Original);
                _previewStorage = new PreviewStorage(_previewStorage.Original, preview);
                if (!_observeEveryPreviewMutation)
                    NotifyHookListenerStructureChanged();
            }
            else if (_previewStorage.Shared)
            {
                preview = PredictionUtils.CloneCardStateForSimulation(preview);
                _previewStorage = new PreviewStorage(_previewStorage.Original, preview);
                if (!_observeEveryPreviewMutation)
                    NotifyHookListenerStructureChanged();
            }
            else
            {
                _previewStorage.InvalidateCaches();
            }
            return preview;
        }
    }

    internal void MaterializePreview()
    {
        if (_previewStorage.Preview is not null)
            return;
        _previewStorage = new PreviewStorage(
            _previewStorage.Original,
            PredictionUtils.CloneCardStateForSimulation(_previewStorage.Original));
    }

    public static List<PredictedCard> FromCards(IEnumerable<CardModel> cards)
    {
        return cards.Select(card => new PredictedCard(card)).ToList();
    }

    public static PredictedCard FromGenerated(CardModel card)
    {
        return new(card, card);
    }

    public static PredictedCard Create(CardModel canonicalCard, Player player)
    {
        return FromGenerated(PredictionUtils.CreateCard(canonicalCard, player));
    }

    public bool References(object? card)
    {
        return ReferenceEquals(_previewStorage.Original, card)
            || ReferenceEquals(_previewStorage.Preview, card);
    }

    // Clones the prediction wrapper state only. Combat effects that generate a gameplay
    // clone of a card should use CombatPredictedCardExtensions.CreateClone instead.
    public PredictedCard Clone()
    {
        return new PredictedCard(_previewStorage.Original, _previewStorage.Preview is { } preview
            ? PredictionUtils.CloneCardStateForSimulation(preview)
            : null)
        {
            _isolateAttachedModelsOnFork = _isolateAttachedModelsOnFork,
        };
    }

    internal PredictedCard Fork(PredictionForkContext context)
    {
        PreviewStorage forkStorage;
        if (_isolateAttachedModelsOnFork)
        {
            CardModel source = _previewStorage.Preview ?? _previewStorage.Original;
            forkStorage = new PreviewStorage(
                _previewStorage.Original,
                PredictionUtils.CloneCardStateForSimulation(source));
        }
        else
        {
            _previewStorage.Shared = true;
            forkStorage = _previewStorage;
        }
        PredictedCard fork = new(forkStorage)
        {
            _isolateAttachedModelsOnFork = _isolateAttachedModelsOnFork,
        };
        context.Register(this, fork);
        return fork;
    }

    internal bool TryGetCachedFingerprint(out ulong first, out ulong second)
    {
        if (_isolateAttachedModelsOnFork)
        {
            first = 0;
            second = 0;
            return false;
        }
        return _previewStorage.TryGetCachedFingerprint(out first, out second);
    }

    internal void SetCachedFingerprint(ulong first, ulong second)
    {
        if (_isolateAttachedModelsOnFork)
            return;
        _previewStorage.SetCachedFingerprint(first, second);
    }

    internal bool TryGetCachedChoiceKey(out string key)
    {
        if (_isolateAttachedModelsOnFork)
        {
            key = string.Empty;
            return false;
        }
        return _previewStorage.TryGetCachedChoiceKey(out key);
    }

    internal void SetCachedChoiceKey(string key)
    {
        if (!_isolateAttachedModelsOnFork)
            _previewStorage.SetCachedChoiceKey(key);
    }

    internal void InvalidateCaches()
    {
        _ownerPile?.InvalidateFingerprint();
        _previewStorage.InvalidateCaches();
    }

    internal void SetOwnerPile(SimCardPile? pile)
    {
        _ownerPile = pile;
        if (_isolateAttachedModelsOnFork)
            pile?.DisableFingerprintCache();
    }

    internal void SetMutationObserver(Action? observer, bool observeEveryPreviewMutation = false)
    {
        _mutationObserver = observer;
        _observeEveryPreviewMutation = observer is not null && observeEveryPreviewMutation;
    }

    internal void EnableAttachedModelForkIsolation()
    {
        _isolateAttachedModelsOnFork = true;
        _previewStorage.InvalidateCaches();
        _ownerPile?.DisableFingerprintCache();
        PredictionModModelSupport.RegisterBaseLibCardModifierOwner(Preview);
    }

    // The hook-listener cache contains the preview card object plus its optional attached
    // affliction/enchantment. Ordinary vanilla card-field mutations keep those identities stable
    // and do not require rebuilding a deck-sized listener list. A BaseLib CardModifier can change
    // its opaque DirectModifiers membership during any mutable access, so callers opt back into the
    // conservative per-access invalidation policy while such modifiers are present.
    internal void NotifyHookListenerStructureChanged()
    {
        _mutationObserver?.Invoke();
    }

    public int CompareTo(PredictedCard? other)
    {
        return Preview.CompareTo(other?.Preview);
    }
}
