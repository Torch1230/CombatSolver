using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace CombatSolver;

internal static class NativeTestCardSelectors
{
    internal static ICardSelector? Current
    {
        get
        {
            var run = RunManager.Instance.DebugOnlyGetState();
            return run is null ? null : RunManager.Instance.CardSelectorStack.GetSelector(run.Players.Single());
        }
    }

    internal static IDisposable Push(ICardSelector selector, bool localOnly = false)
    {
        var manager = RunManager.Instance;
        return localOnly
            ? manager.CardSelectorStack.UseLocalSelector(selector)
            : manager.CardSelectorStack.PushSelector(manager.DebugOnlyGetState()!.Players.Single(), selector);
    }
}
