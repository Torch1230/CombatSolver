using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.Common;

/// <summary>Identifies action boundaries that affect prediction scope and causal ownership.</summary>
internal enum PredictionActionKind
{
    /// <summary>A manual or automatic card-play lifecycle.</summary>
    CardPlay,

    /// <summary>A potion-use lifecycle.</summary>
    PotionUse,

    /// <summary>A dynamic-variable calculation.</summary>
    DynamicVariableCalculation
}

/// <summary>Identifies either one mirrored model method or one higher-level prediction action.</summary>
/// <remarks>
/// <see cref="Method"/> and <see cref="Action"/> are mutually exclusive. Callers should use
/// <see cref="ForMethod"/> or <see cref="ForAction"/> so exactly one discriminator is populated.
/// </remarks>
/// <param name="Method">The exact reflected base method represented by this invocation.</param>
/// <param name="Action">The higher-level prediction action represented by this invocation.</param>
internal readonly record struct PredictionInvocation(
    MethodInfo? Method,
    PredictionActionKind? Action)
{
    /// <summary>Creates an invocation for one exact mirrored base method.</summary>
    public static PredictionInvocation ForMethod(MethodInfo method) => new(method, null);

    /// <summary>Creates an invocation for one higher-level prediction action.</summary>
    public static PredictionInvocation ForAction(PredictionActionKind action) => new(null, action);
}

/// <summary>
/// Represents one immutable model-source frame in a prediction trace.
/// </summary>
/// <remarks>
/// Frame reference identity is stable after its active scope is popped and is used for history ownership, causal
/// grouping, and root/nested scope classification. Frames are linked from the current frame toward the root.
/// </remarks>
internal sealed class PredictionTraceFrame
{
    /// <summary>The exact enclosing frame, or <see langword="null"/> for a top-level frame.</summary>
    public required PredictionTraceFrame? Parent { get; init; }

    /// <summary>The exact model identity responsible for this frame.</summary>
    public required AbstractModel Source { get; init; }

    /// <summary>The method or action represented by this frame.</summary>
    public required PredictionInvocation Invocation { get; init; }

    /// <summary>Enumerates this frame followed by each enclosing frame from nearest to farthest.</summary>
    public IEnumerable<PredictionTraceFrame> Ancestors()
    {
        var current = this;
        do
        {
            yield return current;
            current = current.Parent;
        } while (current is not null);
    }
}

/// <summary>Maintains the strictly nested frame stack for one prediction simulation.</summary>
/// <remarks>
/// A trace is mutable only while simulation scopes are active and is not safe for concurrent use. Disposing scopes
/// out of LIFO order is a programming error; popped frame objects remain valid immutable identities for history.
/// </remarks>
internal sealed class PredictionTrace
{
    // 绝大多数镜像分发在自己的作用域里一条历史都不记，帧对象建了就直接丢。改为只把
    // 来源/调用记进一段可复用的栈，等真的有人读 Current 时才物化，并把结果缓存在槽里。
    // 帧的引用身份因此保持原样：同一个活动作用域内每次读 Current 都拿到同一个对象，
    // 作用域弹出后槽被清空，下一次 Push 到同一深度会得到一个全新的对象；物化更深的帧时
    // 父帧一律取自缓存，所以 Parent 链上的对象和逐层 Push 时建出来的完全一致。
    private struct Slot
    {
        public AbstractModel? Source;
        public PredictionInvocation Invocation;
        public PredictionTraceFrame? Materialized;
    }

    private Slot[] _slots = new Slot[16];
    private int _depth;

    /// <summary>Gets whether any scope is active, without materializing its frame.</summary>
    public bool HasCurrentFrame => _depth > 0;

    /// <summary>Gets the active innermost frame, or <see langword="null"/> when no scope is active.</summary>
    public PredictionTraceFrame? Current => _depth == 0 ? null : Materialize(_depth - 1);

    /// <summary>Pushes one source/invocation frame and returns the scope that pops that exact frame.</summary>
    /// <param name="source">The exact model identity responsible for work performed in the new scope.</param>
    /// <param name="invocation">The method or action that establishes the new scope.</param>
    /// <returns>An idempotent disposable scope that must be disposed in strict LIFO order.</returns>
    public TraceScope Push(AbstractModel source, PredictionInvocation invocation)
    {
        if (_depth == _slots.Length)
            Array.Resize(ref _slots, _slots.Length * 2);
        _slots[_depth].Source = source;
        _slots[_depth].Invocation = invocation;
        _slots[_depth].Materialized = null;
        _depth++;
        return new TraceScope(this, _depth);
    }

    private PredictionTraceFrame Materialize(int index)
    {
        if (_slots[index].Materialized is { } cached)
            return cached;
        int start = index;
        while (start > 0 && _slots[start - 1].Materialized is null)
            start--;
        PredictionTraceFrame? frame = start == 0 ? null : _slots[start - 1].Materialized;
        for (int slotIndex = start; slotIndex <= index; slotIndex++)
        {
            frame = new PredictionTraceFrame
            {
                Parent = frame,
                Source = _slots[slotIndex].Source
                    ?? throw new InvalidOperationException("Prediction trace slot has no source."),
                Invocation = _slots[slotIndex].Invocation,
            };
            _slots[slotIndex].Materialized = frame;
        }
        return frame!;
    }

    private void Pop(int depth)
    {
        if (_depth != depth)
        {
            throw new InvalidOperationException("Prediction trace scopes are unbalanced.");
        }

        _depth = depth - 1;
        // 已经交出去的帧对象仍然有效；这里只是让槽不再钉住它和它的整条父链。
        _slots[_depth].Materialized = null;
        _slots[_depth].Source = null;
    }

    internal struct TraceScope(PredictionTrace trace, int depth) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            trace.Pop(depth);
            _disposed = true;
        }
    }
}
