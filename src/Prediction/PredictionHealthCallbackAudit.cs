using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using STS2RitsuLib.Combat.Healing;
using STS2RitsuLib.Combat.SecondaryResources;

namespace CombatSolver;

// Additional rejection checks for known notification gaps, not a complete UI or
// future-source certificate. Capture at the stable root; retain only the reason.
internal static class PredictionHealthCallbackAudit
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly FieldInfo? CurrentHp = typeof(Creature).GetField("CurrentHpChanged", Members);
    private static readonly FieldInfo? MaxHp = typeof(Creature).GetField("MaxHpChanged", Members);
    private static readonly FieldInfo? TrackerState = typeof(CombatStateTracker).GetField("_state", Members);
    private static readonly FieldInfo? TrackerAudience = typeof(CombatStateTracker).GetField("CombatStateChanged", Members);
    private static readonly Type? TopBarType = typeof(Creature).Assembly.GetType("MegaCrit.sts2.Core.Nodes.TopBar.NTopBarHp");
    private static readonly Type? CubexType = typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Models.Monsters.CubexConstruct");
    private static readonly Type? PanelType = typeof(HealHook).Assembly.GetType("STS2RitsuLib.Settings.RitsuDebugToolsPanel");
    private static readonly Type? DetailType = typeof(HealHook).Assembly.GetType("STS2RitsuLib.Settings.RitsuDebugLiveDetailContainer");
    private static readonly Type? SecondaryTrackerType = typeof(HealHook).Assembly
        .GetType("STS2RitsuLib.Combat.SecondaryResources.SecondaryResourceCombatUiStateTracker");
    private static readonly MethodInfo? SecondaryUiUpdate = typeof(SecondaryResourceUiRuntime)
        .GetMethod(nameof(SecondaryResourceUiRuntime.UpdateCombatUi), Members, null, [typeof(Node), typeof(Player)], null);

    private static readonly (Type? Type, string MethodName)[] NativeDisplayBindings =
    [
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NEndTurnButton"), "OnCombatStateChanged"),
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand"), "OnCombatStateChanged"),
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCreatureStateDisplay"), "OnCombatStateChanged"),
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Orbs.NOrbManager"), "OnCombatStateChanged"),
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NEnergyCounter"), "OnCombatStateChanged"),
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NIntent"), "OnCombatStateChanged"),
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Potions.NPotionPopup"), "OnCombatStateChanged"),
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NFoolCombatUi"), "OnSymbolsChanged"),
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NFoolCombatUi"), "RefreshVisibility"),
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds.NCeremonialBeastBgVfx"), "UpdateState"),
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NCreature"), "ShowCreatureHoverTips"),
        (typeof(Creature).Assembly.GetType("MegaCrit.Sts2.Core.Nodes.Combat.NPower"), "ShowPowerHoverTips"),
    ];

    private static MethodInfo? Method(Type? type, string name, params Type[] arguments)
        => type?.GetMethod(name, Members, null, arguments, null);

    private static bool IsPatched(MethodInfo method)
        => HasPatches(method) || method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } async
            && (async.StateMachineType.GetMethod("MoveNext", Members) is not { } moveNext || HasPatches(moveNext));

    private static bool HasPatches(MethodInfo method)
        => Harmony.GetPatchInfo(method) is { } patches
            && (patches.Prefixes.Count + patches.Postfixes.Count + patches.Transpilers.Count + patches.Finalizers.Count != 0);

    private static bool IsBinding(Delegate callback, Type? type, string name, params Type[] arguments)
        => type is not null && callback.Target?.GetType() == type
            && callback.Method == Method(type, name, arguments) && !IsPatched(callback.Method);

    internal static string? Capture(CombatState state)
    {
        if (!PredictionPlayerPotionCallbackAudit.IsAuditedGameMvid(typeof(Creature).Module.ModuleVersionId)
            || CurrentHp?.FieldType != typeof(Action<int, int>) || MaxHp?.FieldType != typeof(Action<int, int>))
            return "health-event-layout";
        bool hasTracker = false;
        var tracker = CombatManager.Instance.StateTracker;
        foreach (Creature creature in state.Creatures.Concat(state.Players.Select(player => player.Osty).OfType<Creature>()).Distinct())
        {
            foreach (FieldInfo field in new[] { CurrentHp, MaxHp })
            {
                if (field.GetValue(creature) is not Delegate callbacks)
                    continue;
                foreach (Delegate callback in callbacks.GetInvocationList())
                {
                    if (IsBinding(callback, typeof(CombatStateTracker), "OnCreatureValueChanged", typeof(int), typeof(int))
                        && ReferenceEquals(callback.Target, tracker))
                        hasTracker = true;
                    else if (IsBinding(callback, TopBarType, "UpdateHealth", typeof(int), typeof(int))) { }
                    else if (ReferenceEquals(field, CurrentHp)
                        && (IsBinding(callback, typeof(CombatReplayOutcome), "OnHpChanged", typeof(int), typeof(int))
                            || IsBinding(callback, CubexType, "OnHpChanged", typeof(int), typeof(int))
                                && ReferenceEquals(callback.Target, creature.Monster))) { }
                    else return "health-callback:" + field.Name + ":" + creature.CombatId + ":"
                        + callback.Method.DeclaringType?.FullName + ":" + callback.Method.Name;
                }
            }
        }
        foreach (string name in new[] { "set_CurrentHp", "set_MaxHp", "SetCurrentHpInternal", "SetMaxHpInternal", "HealInternal" })
        {
            Type argument = name.StartsWith("set_", StringComparison.Ordinal) ? typeof(int) : typeof(decimal);
            if (Method(typeof(Creature), name, argument) is not { } method || IsPatched(method))
                return "health-entry-patch:" + name;
        }
        // The tracker also forwards pile/history/energy changes. An empty HP
        // audience cannot exclude its independently reachable combat callback.
        if (tracker is null)
            return hasTracker ? "health-tracker-layout" : null;
        if (tracker.GetType() != typeof(CombatStateTracker) || TrackerState?.FieldType != typeof(CombatState)
            || TrackerAudience?.FieldType != typeof(Action<CombatState>))
            return "health-tracker-layout";
        var audience = TrackerAudience.GetValue(tracker) as Delegate;
        if (!ReferenceEquals(TrackerState.GetValue(tracker), state))
            return !hasTracker && audience is null ? null : "health-tracker-state";
        foreach (var (type, name) in new[]
        {
            (typeof(CombatStateTracker), "CallCombatStateChangedDeferred"),
            (typeof(PlayerCombatState), "RecalculateCardValues"),
        })
            if (Method(type, name) is not { } method || IsPatched(method))
                return "health-tracker-patch:" + name;
        if (Method(typeof(CombatStateTracker), "NotifyCombatStateChanged", typeof(string)) is not { } notify)
            return "health-tracker-notify-layout";
        MethodInfo? own = Method(typeof(CombatStateTrackerIsolationPatch), "Prefix", typeof(string));
        if (own is null || IsPatched(own) || Harmony.GetPatchInfo(notify) is { } patches
            && (patches.Prefixes.Any(patch => patch.PatchMethod != own) || patches.Prefixes.Count > 1
                || patches.Postfixes.Count + patches.Transpilers.Count + patches.Finalizers.Count != 0))
            return "health-tracker-patch:NotifyCombatStateChanged";
        if (audience is null)
            return null;
        foreach (Delegate callback in audience.GetInvocationList())
        {
            if (NativeDisplayBindings.Any(binding => binding.Type is not null
                && PredictionPlayerPotionCallbackAudit.IsAuditedGameMvid(binding.Type.Module.ModuleVersionId)
                && IsBinding(callback, binding.Type, binding.MethodName, typeof(CombatState))))
                continue;
            if (IsSafeSecondaryResourceTracker(callback))
                continue;
            if (!PredictionRitsuHealingAudit.IsAuditedRuntimeMvid(PanelType?.Module.ModuleVersionId ?? Guid.Empty)
                || !IsBinding(callback, PanelType, "OnCombatStateChanged", typeof(CombatState)))
                return "health-tracker-callback:" + callback.Method.DeclaringType?.FullName + ":" + callback.Method.Name;
            if (CaptureRitsuDebugPanel(callback.Target!) is { } rejected)
                return rejected;
        }
        return null;
    }

    private static bool IsSafeSecondaryResourceTracker(Delegate callback)
        => PredictionRitsuHealingAudit.IsAuditedRuntimeMvid(SecondaryTrackerType?.Module.ModuleVersionId ?? Guid.Empty)
            && IsBinding(callback, SecondaryTrackerType, "OnCombatStateChanged", typeof(CombatState))
            && !ModSecondaryResourceRegistry.HasAny
            && SecondaryUiUpdate is not null && !IsPatched(SecondaryUiUpdate);

    internal static string? CaptureRitsuDebugPanel(object panel)
    {
        // The observed default bindings retain their existing admission. Reading
        // their detail callbacks closes one reproduced gap, not their call graph.
        if (!PredictionRitsuHealingAudit.IsAuditedRuntimeMvid(DetailType?.Module.ModuleVersionId ?? Guid.Empty)
            || PanelType?.GetField("_currentBrowser", Members) is not { } browserField
            || DetailType!.GetField("_refreshCallbacks", Members) is not { FieldType: var callbacksType } callbacksField
            || callbacksType != typeof(List<Action>))
            return "health-detail-layout";
        foreach (string name in new[] { "ScheduleStateRefresh", "RefreshCurrentState" })
            if (Method(PanelType, name) is not { } method || IsPatched(method))
                return "health-panel-patch:" + name;
        if (Method(PanelType, "RefreshLiveDetails", typeof(Node)) is not { } refresh || IsPatched(refresh)
            || Method(DetailType, "RefreshState") is not { } detailRefresh || IsPatched(detailRefresh))
            return "health-detail-patch";
        if (browserField.GetValue(panel) is not Node browser)
            return null;
        Stack<Node> pending = new();
        pending.Push(browser);
        int visited = 0;
        while (pending.TryPop(out Node? node))
        {
            if (++visited > 4096)
                return "health-detail-inspection-limit";
            if (DetailType.IsInstanceOfType(node))
            {
                if (node.GetType() != DetailType || callbacksField.GetValue(node) is not List<Action> callbacks)
                    return "health-detail-layout";
                if (callbacks.Count != 0)
                    return "health-detail-callbacks:" + callbacks.Count;
            }
            foreach (Node child in node.GetChildren())
                pending.Push(child);
        }
        return null;
    }
}
