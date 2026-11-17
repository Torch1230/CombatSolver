using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using STS2RitsuLib.Combat.Healing;

namespace CombatSolver;

// These notifications bypass model hooks and may restore HP. This rejects
// unreviewed audiences at the stable root; it is not a complete UI certificate.
internal static class PredictionCardCallbackAudit
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly;
    private static readonly string[] EventNames =
    [
        nameof(CardModel.EnergyCostChanged), nameof(CardModel.KeywordsChanged),
        nameof(CardModel.ReplayCountChanged), nameof(CardModel.Played),
        nameof(CardModel.Drawn), nameof(CardModel.StarCostChanged),
        nameof(CardModel.Upgraded), nameof(CardModel.Forged),
    ];
    private static readonly FieldInfo?[] EventFields = EventNames
        .Select(name => typeof(CardModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        .ToArray();
    private static readonly MethodInfo? TrackerNotification = typeof(CombatStateTracker)
        .GetMethod("OnCardValueChanged", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
    private static readonly Type? HandCardHolderType = typeof(CardModel).Assembly
        .GetType("MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder");
    private static readonly MethodInfo? HandCardFlash = HandCardHolderType?.GetMethod(
        "Flash", Fields, null, Type.EmptyTypes, null);
    private static readonly Type? RitsuDebugPanelType = typeof(HealHook).Assembly
        .GetType("STS2RitsuLib.Settings.RitsuDebugToolsPanel");
    private static readonly MethodInfo? RitsuPanelRefresh = RitsuDebugPanelType?.GetMethod(
        "ScheduleStateRefresh", Fields, null, Type.EmptyTypes, null);

    private static bool IsPatched(MethodInfo method)
        => Harmony.GetPatchInfo(method) is { } patches
            && patches.Prefixes.Count + patches.Postfixes.Count + patches.Transpilers.Count + patches.Finalizers.Count != 0;

    internal static string? Capture(IEnumerable<CardModel> cards)
    {
        if (!PredictionPlayerPotionCallbackAudit.IsAuditedGameMvid(typeof(CardModel).Module.ModuleVersionId)
            || EventFields.Any(field => field?.FieldType != typeof(Action))
            || TrackerNotification is null)
            return "card-event-layout";
        if (IsPatched(TrackerNotification))
            return "card-tracker-patch:OnCardValueChanged";

        CombatStateTracker tracker = CombatManager.Instance.StateTracker;
        HashSet<CardModel> seen = new(ReferenceEqualityComparer.Instance);
        foreach (CardModel card in cards)
        {
            if (!seen.Add(card))
                continue;
            foreach (FieldInfo? field in EventFields)
            {
                if (field!.GetValue(card) is not Delegate audience)
                    continue;
                foreach (Delegate callback in audience.GetInvocationList())
                {
                    if (ReferenceEquals(callback.Target, tracker)
                        && callback.Target?.GetType() == typeof(CombatStateTracker)
                        && callback.Method == TrackerNotification)
                        continue;
                    if (HandCardHolderType is not null && HandCardFlash is not null
                        && PredictionPlayerPotionCallbackAudit.IsAuditedGameMvid(HandCardHolderType.Module.ModuleVersionId)
                        && callback.Target?.GetType() == HandCardHolderType
                        && callback.Method == HandCardFlash
                        && !IsPatched(HandCardFlash))
                        continue;
                    if (RitsuDebugPanelType is not null && RitsuPanelRefresh is not null
                        && PredictionRitsuHealingAudit.IsAuditedRuntimeMvid(RitsuDebugPanelType.Module.ModuleVersionId)
                        && callback.Target?.GetType() == RitsuDebugPanelType
                        && callback.Method == RitsuPanelRefresh
                        && !IsPatched(RitsuPanelRefresh)
                        && PredictionHealthCallbackAudit.CaptureRitsuDebugPanel(callback.Target!) is null)
                        continue;
                    return "card-callback:" + field.Name + ":" + card.Id.Entry + ":"
                        + callback.Method.DeclaringType?.FullName + ":" + callback.Method.Name;
                }
            }
        }
        return null;
    }
}
