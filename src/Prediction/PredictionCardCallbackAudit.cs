using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

// These notifications bypass model hooks and may restore HP. This rejects
// unreviewed audiences at the stable root; it is not a complete UI certificate.
internal static class PredictionCardCallbackAudit
{
    private static readonly Guid GameMvid = new("8a76776c-0ce1-4d4f-90bd-8cce653dad8e");
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic
        | BindingFlags.DeclaredOnly;
    private static readonly string[] EventNames =
    [
        nameof(CardModel.EnergyCostChanged), nameof(CardModel.KeywordsChanged),
        nameof(CardModel.ReplayCountChanged), nameof(CardModel.Played),
        nameof(CardModel.Drawn), nameof(CardModel.StarCostChanged),
        nameof(CardModel.Upgraded), nameof(CardModel.Forged),
    ];
    private static readonly FieldInfo?[] EventFields = EventNames
        .Select(name => typeof(CardModel).GetField(name, Fields)).ToArray();
    private static readonly MethodInfo? TrackerNotification = typeof(CombatStateTracker)
        .GetMethod("OnCardValueChanged", Fields, null, Type.EmptyTypes, null);

    internal static string? Capture(IEnumerable<CardModel> cards)
    {
        if (typeof(CardModel).Module.ModuleVersionId != GameMvid
            || EventFields.Any(field => field?.FieldType != typeof(Action))
            || TrackerNotification is null)
            return "card-event-layout";
        if (Harmony.GetPatchInfo(TrackerNotification) is { } patches
            && patches.Prefixes.Count + patches.Postfixes.Count + patches.Transpilers.Count + patches.Finalizers.Count != 0)
            return "card-tracker-patch:OnCardValueChanged";

        CombatStateTracker tracker = CombatManager.Instance.StateTracker;
        HashSet<CardModel> seen = new(ReferenceEqualityComparer.Instance);
        foreach (CardModel card in cards)
        {
            if (!seen.Add(card)) continue;
            foreach (FieldInfo? field in EventFields)
            {
                if (field!.GetValue(card) is not Delegate audience) continue;
                foreach (Delegate callback in audience.GetInvocationList())
                {
                    // The preceding health audit already checks this tracker's
                    // forwarding entry and current audience. Preserve only the
                    // exact native binding, not other methods in its assembly.
                    if (ReferenceEquals(callback.Target, tracker)
                        && callback.Target?.GetType() == typeof(CombatStateTracker)
                        && callback.Method == TrackerNotification) continue;
                    return "card-callback:" + field.Name + ":" + card.Id.Entry + ":"
                        + callback.Method.DeclaringType?.FullName + ":" + callback.Method.Name;
                }
            }
        }
        return null;
    }
}
