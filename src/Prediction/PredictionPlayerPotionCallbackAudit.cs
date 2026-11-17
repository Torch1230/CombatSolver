using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

// Capture only immutable admission metadata. Never retain delegates or UI targets
// in a prediction branch. Admission protects UsedPotionRemoved and the declared
// hover-sync dependencies; it is not an audit of every public/UI event.
internal static class PredictionPlayerPotionCallbackAudit
{
    private static readonly Guid AuditedGameMvid = new("ad562b4e-7352-4ca2-ae45-0ff44cd284bc");
    private static readonly Guid AuditedGameHotfixMvid = new("101ed96a-a88b-482f-9ea5-9b8f1ad4f4fe");

    internal static bool IsAuditedGameMvid(Guid mvid)
        => mvid == AuditedGameMvid || mvid == AuditedGameHotfixMvid;

    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public
        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static readonly FieldInfo? UsedField = typeof(Player).GetField("UsedPotionRemoved", InstanceFields);
    private static readonly MethodInfo? NativeUiCallback = typeof(NPotionContainer).GetMethod(
        "OnUsedPotionRemoved", InstanceFields, null, [typeof(PotionModel)], null);
    private static readonly MethodInfo?[] NativeUiDependencies =
    [
        typeof(NPotionContainer).GetMethod("RemoveUsed", InstanceFields),
        typeof(NPotionContainer).GetMethod("OnPotionHolderUnfocused", InstanceFields),
        typeof(NPotionHolder).GetMethod("RemoveUsedPotion", InstanceFields),
        typeof(HoveredModelTracker).GetMethod("OnLocalPotionUnhovered", InstanceFields),
        typeof(HoveredModelTracker).GetMethod("SynchronizeLocalHoveredModel", InstanceFields),
        typeof(HoveredModelTracker).GetMethod("OnPlayerStateChanged", InstanceFields),
        typeof(PeerInputSynchronizer).GetMethod("SyncLocalHoveredModel", InstanceFields),
        typeof(PeerInputSynchronizer).GetMethod("GetOrCreateStateForPlayer", InstanceFields),
        typeof(PeerInputSynchronizer).GetMethod("GetStateForPlayer", InstanceFields),
        typeof(PeerInputSynchronizer).GetMethod("TrySendSyncMessage", InstanceFields),
        typeof(PeerInputSynchronizer).GetMethod("GetTicksMsec", InstanceFields),
        typeof(PeerInputSynchronizer).GetMethod("QueueSyncMessage", InstanceFields),
        typeof(PeerInputSynchronizer).GetMethod("SendSyncMessageAfterSmallDelay", InstanceFields),
        typeof(PeerInputSynchronizer).GetMethod("SendSyncMessage", InstanceFields),
        typeof(PeerInputSynchronizer).GetMethod("GetHoveredModelData", InstanceFields),
        typeof(HoveredModelData).GetMethod("FromModel", BindingFlags.Public | BindingFlags.Static),
        typeof(HoveredModelData).GetMethod("Equals", [typeof(HoveredModelData)]),
    ];

    private static bool IsPatched(MethodInfo method)
        => HasPatches(method) || method.GetCustomAttribute<AsyncStateMachineAttribute>() is { } async
            && (async.StateMachineType.GetMethod("MoveNext", InstanceFields) is not { } moveNext
                || HasPatches(moveNext));

    private static bool HasPatches(MethodInfo method)
        => Harmony.GetPatchInfo(method) is { } patches
            && (patches.Prefixes.Count != 0 || patches.Postfixes.Count != 0
                || patches.Transpilers.Count != 0 || patches.Finalizers.Count != 0);

    internal static string? Capture(IEnumerable<Player> players)
    {
        if (!IsAuditedGameMvid(typeof(Player).Module.ModuleVersionId)
            || UsedField?.FieldType != typeof(Action<PotionModel>))
            return "used-potion-event-layout";
        bool hasNativeUi = false;
        foreach (Player player in players)
        {
            if (UsedField.GetValue(player) is not Delegate callbacks)
                continue;
            foreach (Delegate callback in callbacks.GetInvocationList())
            {
                // The pinned native callback removes the potion's UI holder and
                // hover display. An arbitrary native method or derived UI target
                // does not establish the same non-gameplay behavior.
                if (NativeUiCallback is null || callback.Method != NativeUiCallback
                    || callback.Target?.GetType() != typeof(NPotionContainer)
                    || IsPatched(NativeUiCallback)
                    || NativeUiDependencies.Any(method => method is null
                        || !IsAuditedGameMvid(method.Module.ModuleVersionId) || IsPatched(method)))
                    return "used-potion-callback:" + callback.Method.DeclaringType?.FullName
                        + ":" + callback.Method.Name;
                hasNativeUi = true;
            }
        }
        if (hasNativeUi)
            return CaptureNativeHoverEnvironment();
        return null;
    }

    private static string? CaptureNativeHoverEnvironment()
    {
        // This is called only while capturing the stable live root. Background
        // consumers receive the reason, never this manager or its event targets.
        var manager = RunManager.Instance;
        var tracker = manager.HoveredModelTracker;
        var input = manager.InputSynchronizer;
        if (tracker?.GetType() != typeof(HoveredModelTracker)
            || input?.GetType() != typeof(PeerInputSynchronizer)
            || input.NetService.GetType() != typeof(NetSingleplayerGameService)
            || input.mockDelay is not null || input.mockWaitSmall is not null || input.mockGetTicksMsec is not null)
            return "used-potion-hover-environment";
        if (typeof(NetSingleplayerGameService).GetMethods(InstanceFields)
            .Where(method => method.Name is "SendMessage" or "get_IsConnected" or "get_NetId")
            .Any(IsPatched))
            return "used-potion-hover-service-patch";
        MethodInfo? onChanged = typeof(HoveredModelTracker).GetMethod("OnPlayerStateChanged", InstanceFields);
        foreach (var (source, eventName, allowedTarget, allowedMethod) in new (object, string, object?, MethodInfo?)[]
            { (input, "StateAdded", null, null), (input, "StateChanged", tracker, onChanged),
                (tracker, "HoverChanged", null, null) })
        {
            FieldInfo? field = source.GetType().GetField(eventName, InstanceFields);
            if (field?.FieldType != typeof(Action<ulong>))
                return "used-potion-hover-layout:" + eventName;
            if (field.GetValue(source) is not Delegate callbacks)
                continue;
            foreach (Delegate callback in callbacks.GetInvocationList())
            {
                if (allowedMethod is null || callback.Method != allowedMethod
                    || !ReferenceEquals(callback.Target, allowedTarget) || IsPatched(allowedMethod))
                    return "used-potion-hover-callback:" + eventName + ":"
                        + callback.Method.DeclaringType?.FullName + ":" + callback.Method.Name;
            }
        }
        return null;
    }
}
