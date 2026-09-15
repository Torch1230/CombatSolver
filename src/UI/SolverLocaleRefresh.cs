using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace CombatSolver;

internal static class SolverLocaleRefresh
{
    private static readonly HashSet<Action> Refreshers = [];
    private static bool _subscribed;
    private static bool _queued;

    public static void Bind(Control owner, Action refresh)
    {
        if (!_subscribed)
        {
            LocManager.Instance.SubscribeToLocaleChange(Queue);
            _subscribed = true;
        }
        owner.TreeEntered += () => { Refreshers.Add(refresh); refresh(); };
        owner.TreeExiting += () => Refreshers.Remove(refresh);
    }

    private static void Queue()
    {
        if (_queued) return;
        _queued = true;
        Callable.From(Refresh).CallDeferred();
    }

    private static void Refresh()
    {
        _queued = false;
        // A control can enter the tree between two coalesced language changes.
        // Even a round trip back to the previous language must refresh that control.
        foreach (Action refresh in Refreshers.ToArray()) refresh();
    }

    internal static int SubscriptionCountForTesting => Refreshers.Count;
}
