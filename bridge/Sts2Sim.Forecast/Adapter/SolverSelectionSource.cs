using Sts2Sim.Core.Combat;
using Sts2Sim.Core.Entities.Cards;
using Sts2Sim.Core.Models;

namespace CombatSolver;

internal sealed class ChoiceScriptNode
{
    public ChoiceScriptNode(ChoiceScriptNode? parent, int[] stepIndices, string[] stepCardIds, int branchOrdinal = 0)
    {
        Parent = parent;
        StepIndices = stepIndices;
        StepCardIds = stepCardIds;
        BranchOrdinal = branchOrdinal;
        Depth = (parent?.Depth ?? 0) + 1;
        TotalCardIdCount = (parent?.TotalCardIdCount ?? 0) + stepCardIds.Length;
    }

    public ChoiceScriptNode? Parent { get; }
    public int[] StepIndices { get; }
    public string[] StepCardIds { get; }
    public int BranchOrdinal { get; }
    public int Depth { get; }
    public int TotalCardIdCount { get; }

    public IReadOnlyList<IReadOnlyList<int>> MaterializeIndices()
    {
        var result = new IReadOnlyList<int>[Depth];
        ChoiceScriptNode? cur = this;
        for (int i = Depth - 1; i >= 0; i--)
        {
            result[i] = cur!.StepIndices;
            cur = cur.Parent;
        }
        return result;
    }

    public IReadOnlyList<string> MaterializeCardIds()
    {
        if (TotalCardIdCount == 0)
            return Array.Empty<string>();
        var result = new string[TotalCardIdCount];
        int end = TotalCardIdCount;
        ChoiceScriptNode? cur = this;
        while (cur is not null)
        {
            string[] ids = cur.StepCardIds;
            end -= ids.Length;
            Array.Copy(ids, 0, result, end, ids.Length);
            cur = cur.Parent;
        }
        return result;
    }

    public bool MatchesObserved(List<string> observed)
    {
        if (observed.Count != TotalCardIdCount)
            return false;
        int end = TotalCardIdCount;
        ChoiceScriptNode? cur = this;
        while (cur is not null)
        {
            string[] ids = cur.StepCardIds;
            end -= ids.Length;
            for (int i = 0; i < ids.Length; i++)
            {
                if (!string.Equals(observed[end + i], ids[i], StringComparison.Ordinal))
                    return false;
            }
            cur = cur.Parent;
        }
        return true;
    }

    public int[] MaterializeBranchPath()
    {
        var path = new int[Depth];
        ChoiceScriptNode? cur = this;
        for (int i = Depth - 1; i >= 0; i--)
        {
            path[i] = cur!.BranchOrdinal;
            cur = cur.Parent;
        }
        return path;
    }
}

/// <summary>Replays an explicit choice prefix; an exhausted prefix requests a new branch.</summary>
internal sealed class SolverSelectionSource : ICardSelectionDecisionSource
{
    private readonly IReadOnlyList<IReadOnlyList<int>> _script;
    private int _cursor;

    public SolverSelectionSource(IReadOnlyList<IReadOnlyList<int>>? script = null, int cursor = 0)
    {
        if (script is null || script.Count == 0)
        {
            _script = Array.Empty<IReadOnlyList<int>>();
        }
        else if (script is IReadOnlyList<int>[])
        {
            _script = script;
        }
        else
        {
            var copy = new IReadOnlyList<int>[script.Count];
            for (int i = 0; i < script.Count; i++)
                copy[i] = script[i];
            _script = copy;
        }
        _cursor = cursor;
    }

    internal SolverSelectionSource(ChoiceScriptNode? chain, int cursor = 0)
    {
        _script = chain is null ? Array.Empty<IReadOnlyList<int>>() : chain.MaterializeIndices();
        _cursor = cursor;
    }

    public CardSelectionRequest? PendingChoice { get; private set; }
    public int ChoicesConsumed => _cursor;

    public Task<IReadOnlyList<CardModel>> ChooseCardsAsync(CardSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_cursor >= _script.Count)
        {
            PendingChoice = request;
            throw new SolverSelectionRequiredException(request);
        }

        IReadOnlyList<int> indices = _script[_cursor++];
        if (indices.Count == 0)
            return Task.FromResult<IReadOnlyList<CardModel>>(Array.Empty<CardModel>());

        IReadOnlyList<CardModel> candidates = request.Candidates;
        var cards = new CardModel[indices.Count];
        for (int i = 0; i < indices.Count; i++)
        {
            int index = indices[i];
            if ((uint)index >= (uint)candidates.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(indices), index,
                    "Choice index is outside the current Core request.");
            }
            cards[i] = candidates[index];
        }
        return Task.FromResult<IReadOnlyList<CardModel>>(cards);
    }

    public SolverSelectionSource Fork()
    {
        var fork = new SolverSelectionSource(_script, _cursor);
        fork.PendingChoice = PendingChoice;
        return fork;
    }

    public SolverSelectionSource ForReplay(int replayCursor) => new(_script, replayCursor);

    public SolverSelectionSource AppendForReplay(IReadOnlyList<int> indices, int replayCursor)
    {
        var next = new IReadOnlyList<int>[_script.Count + 1];
        for (int i = 0; i < _script.Count; i++)
            next[i] = _script[i];
        var step = new int[indices.Count];
        for (int i = 0; i < indices.Count; i++)
            step[i] = indices[i];
        next[_script.Count] = step;
        return new SolverSelectionSource(next, replayCursor);
    }

    public void ClearPendingChoice() => PendingChoice = null;
}
