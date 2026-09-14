using System.Reflection;
using System.Runtime.CompilerServices;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Modding;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Reject<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { checks++; return; }
    throw new InvalidOperationException(message);
}
if (args.Contains("--empty"))
{
    Check(AdaptedCardOnPlayMirrors.CaptureLiveStamp() is null, "Empty registration added configuration state.");
    Check(PredictionModPatchAudit.CaptureCardOnPlay([new TestCard()]) is null, "Empty registration created a root selection table.");
    Console.WriteLine($"ADAPTED_ONPLAY_EMPTY_OK checks={checks}");
    return;
}
MethodInfo Method(Type type, string name) => AccessTools.Method(type, name)!;
MethodInfo target = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(TestCard))!;
MethodInfo target2 = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(ComposedCard))!;
MethodInfo prefix = Method(typeof(TestPatches), nameof(TestPatches.Replace));
MethodInfo post = Method(typeof(TestPatches), nameof(TestPatches.Post));
MethodInfo before = Method(typeof(TestPatches), nameof(TestPatches.Before));
MethodInfo after = Method(typeof(TestPatches), nameof(TestPatches.After));
const string owner = "combat-solver-neutral-onplay-check";
AdaptedOnPlayPatch Declaration(HarmonyPatchType kind, MethodInfo method, int priority = Priority.Normal)
    => new(kind, method, owner, priority, [], []);
AdaptedOnPlayPatch[] composition = [Declaration(HarmonyPatchType.Prefix, prefix), Declaration(HarmonyPatchType.Postfix, post)];
Reject<ArgumentException>(() => AdaptedCardOnPlayMirrors.Register<TestCard>("wrong-target", target2, composition, (_, _) => { }), "Wrong target accepted.");
Reject<ArgumentException>(() => AdaptedCardOnPlayMirrors.Register<TestCard>("wrong-overload", AccessTools.Method(typeof(TestCard), "OnPlay", [typeof(int)])!,
    composition, (_, _) => { }), "Wrong overload accepted.");
Reject<ArgumentException>(() => AdaptedCardOnPlayMirrors.Register<TestCard>("empty", target, [], (_, _) => { }), "Empty composition accepted.");
AdaptedCardOnPlayMirrors.Register<TestCard>("replacement-v1", target, composition, (card, _) => card.Value = 43);
AdaptedCardOnPlayMirrors.Register<ComposedCard>("composition-v1", target2,
    [Declaration(HarmonyPatchType.Prefix, before), Declaration(HarmonyPatchType.Postfix, after)], (card, _) => card.Value = (card.Value + 5 + 10) * 2);
MethodInfo asyncTarget = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(AsyncCard))!;
AdaptedCardOnPlayMirrors.Register<AsyncCard>("async-contract", asyncTarget,
    [Declaration(HarmonyPatchType.Prefix, prefix)], (card, _) => card.Value++);
Check(AdaptedCardOnPlayMirrors.DescribeRegisteredCompositions().Count == 3
    && AdaptedCardOnPlayMirrors.DescribeRegisteredCompositions()[0].Mirror.Registrations.Count == 1,
    "Conditional registrations do not expose standard descriptors.");
Reject<ArgumentException>(() => AdaptedCardOnPlayMirrors.Register<TestCard>("conflict", target, composition, (_, _) => { }), "Conflicting registration accepted.");
// Input arrays can be edited after registration without changing the frozen declaration.
composition[0] = Declaration(HarmonyPatchType.Finalizer, post);
TestCard live = new();
ComposedCard live2 = new();
CardModel[] cards = [live, live2];
AdaptedOnPlaySnapshot baseline = PredictionModPatchAudit.CaptureCardOnPlay(cards)!;
Check(!baseline.TryInvoke(new(), new(live), new(), out _), "Unpatched card did not fall back.");
string originalStamp = baseline.Stamp;
Reject<InvalidOperationException>(() => AdaptedCardOnPlayMirrors.Register<OtherCard>("late", AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(OtherCard))!,
    [Declaration(HarmonyPatchType.Prefix, prefix)], (_, _) => { }), "Late registration accepted.");
Harmony harmony = new(owner);
try
{
    harmony.Patch(target, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(post));
    harmony.Patch(target2, prefix: new HarmonyMethod(before), postfix: new HarmonyMethod(after));
    AdaptedOnPlaySnapshot snapshot = PredictionModPatchAudit.CaptureCardOnPlay(cards)!;
    Check(snapshot.Stamp != originalStamp, "Patch installation did not invalidate old configuration.");
    Check(snapshot.Stamp == AdaptedCardOnPlayMirrors.CaptureLiveStamp(), "Stable configuration stamp drifted.");
    live.Play(); live2.Play();
    TestCard predicted = new(); ComposedCard predicted2 = new();
    Check(snapshot.TryInvoke(new(), new(predicted), new(), out var dispatch), "Exact replacement not selected.");
    Check(snapshot.TryInvoke(new(), new(predicted2), new(), out _), "Exact composition not selected.");
    Check(predicted.Value == live.Value && live.Value == 43, "Replacement native/predicted effects differ.");
    Check(predicted2.Value == live2.Value && live2.Value == 30, "Prefix/native/postfix composition differs.");
    Check(dispatch.Kind == CombatSolver.Engine.Common.Mirrors.MirrorDispatchKind.Handled, "Registered dispatch not exact.");
    Patches wrongOwner = new([new(prefix, 0, "wrong-owner", Priority.Normal, [], [], false)],
        [new(post, 1, owner, Priority.Normal, [], [], false)], [], [], [], []);
    Reject<PredictionUnsupportedException>(() => AdaptedCardOnPlayMirrors.Select(typeof(TestCard), target, wrongOwner), "Wrong Harmony owner accepted.");
    Reject<PredictionUnsupportedException>(() => snapshot.TryInvoke(new(), new(new OtherCard()), new(), out _), "Unaudited dynamic type accepted.");
    AssemblyInfo.Unknown = true;
    Reject<PredictionUnsupportedException>(() => PredictionModPatchAudit.CaptureCardOnPlay(cards), "Registered unknown source accepted.");
    AssemblyInfo.Unknown = false;
    ModManager.Mods.Add(new() { manifest = new() { id = "WheelchairSpire" } });
    Reject<IncompatibleGameplayModException>(() => PredictionModPatchAudit.CaptureCardOnPlay(cards), "Denied mod bypassed audit.");
    ModManager.Mods.Clear();
    MethodInfo extra = Method(typeof(TestPatches), nameof(TestPatches.Extra));
    harmony.Patch(target, prefix: new HarmonyMethod(extra));
    Reject<PredictionUnsupportedException>(() => PredictionModPatchAudit.CaptureCardOnPlay(cards), "Additional same-owner patch accepted.");
    Check(AdaptedCardOnPlayMirrors.CaptureLiveStamp() != snapshot.Stamp, "Additional patch left old stamp valid.");
    harmony.Unpatch(target, extra);
    harmony.Unpatch(target, prefix);
    harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.First });
    Reject<PredictionUnsupportedException>(() => PredictionModPatchAudit.CaptureCardOnPlay(cards), "Changed priority accepted.");
    harmony.Unpatch(target, prefix);
    harmony.Patch(target, prefix: new HarmonyMethod(prefix) { before = ["another-owner"] });
    Reject<PredictionUnsupportedException>(() => PredictionModPatchAudit.CaptureCardOnPlay(cards), "Changed ordering constraints accepted.");
    harmony.Unpatch(target, prefix);
    harmony.Patch(target, prefix: new HarmonyMethod(prefix));
    Check(PredictionModPatchAudit.CaptureCardOnPlay(cards) is not null, "Exact reinstall rejected.");
    harmony.Unpatch(target, prefix);
    harmony.Unpatch(target, post);
    harmony.Patch(target, prefix: new HarmonyMethod(post));
    Reject<PredictionUnsupportedException>(() => PredictionModPatchAudit.CaptureCardOnPlay(cards), "Wrong patch category accepted.");
    harmony.Unpatch(target, prefix);
    harmony.Unpatch(target, post);
    AdaptedOnPlaySnapshot unloaded = PredictionModPatchAudit.CaptureCardOnPlay(cards)!;
    Check(!unloaded.TryInvoke(new(), new(new TestCard()), new(), out _), "Removed patches still select replacement.");
    Check(unloaded.Stamp != snapshot.Stamp, "Unloaded patch kept stale plans valid.");
    TestCard frozen = new();
    Check(snapshot.TryInvoke(new(), new(frozen), new(), out _) && frozen.Value == 43, "Worker read current Harmony state instead of its root.");
    MethodInfo otherTarget = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(OtherCard))!;
    harmony.Patch(otherTarget, prefix: new HarmonyMethod(extra));
    Reject<IncompatibleGameplayModException>(() => PredictionModPatchAudit.CaptureCardOnPlay([new OtherCard()]), "Unregistered foreign target accepted.");
    Check(unloaded.Stamp != AdaptedCardOnPlayMirrors.CaptureLiveStamp(), "Later card patch omitted from invalidation stamp.");
    // Inspect real Harmony ordering without executing this artificial pair.
    Patch p1 = new(before, 0, "a", Priority.Normal, [], [], false);
    Patch p2 = new(after, 1, "b", Priority.Normal, [], [], false);
    Patches ordered = new([p1, p2], [], [], [], [], []);
    Patches reversed = new([new(after, 0, "b", Priority.Normal, [], [], false), new(before, 1, "a", Priority.Normal, [], [], false)], [], [], [], [], []);
    Check(AdaptedCardOnPlayMirrors.DescribeActual(target, ordered, false) != AdaptedCardOnPlayMirrors.DescribeActual(target, reversed, false), "Effective same-priority order collapsed.");
    Patches inner = new([], [], [], [], [p1], []);
    Reject<PredictionUnsupportedException>(() => AdaptedCardOnPlayMirrors.DescribeActual(target, inner, false), "Inner patch silently omitted.");
    Patches duplicate = new([p1, p1], [], [], [], [], []);
    Reject<PredictionUnsupportedException>(() => AdaptedCardOnPlayMirrors.DescribeActual(target, duplicate, false), "Duplicate method identities accepted.");
    Patches factory = new([new(Method(typeof(TestPatches), nameof(TestPatches.Factory)), 0, owner, Priority.Normal, [], [], false)], [], [], [], [], []);
    Reject<PredictionUnsupportedException>(() => AdaptedCardOnPlayMirrors.DescribeActual(target, factory, false), "Patch factory accepted.");
    Check(TestPatches.FactoryCalls == 0, "Audit executed a patch factory before rejecting it.");
    MethodInfo moveNext = AccessTools.Method(asyncTarget.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext");
    harmony.Patch(moveNext, prefix: new HarmonyMethod(extra));
    Reject<PredictionUnsupportedException>(() => AdaptedCardOnPlayMirrors.CaptureLiveStamp(), "Async body patch did not reject stale plans.");
    harmony.Unpatch(moveNext, extra);
    Console.WriteLine($"ADAPTED_ONPLAY_OK checks={checks}");
}
finally
{
    harmony.UnpatchAll(owner);
    ModManager.Mods.Clear();
    AssemblyInfo.Unknown = false;
}

internal class TestCard : CardModel
{
    public void OnPlay(int value) => Value = value;
    [MethodImpl(MethodImplOptions.NoInlining)]
    protected override void OnPlay(PlayerChoiceContext context, CardPlay play) => Value += 10;
}
internal class ComposedCard : CardModel
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    protected override void OnPlay(PlayerChoiceContext context, CardPlay play) => Value += 10;
}
internal class OtherCard : CardModel
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    protected override void OnPlay(PlayerChoiceContext context, CardPlay play) => Value++;
}
internal static class TestPatches
{
    public static int FactoryCalls;
    public static MethodInfo Factory(MethodBase original)
    {
        FactoryCalls++;
        return AccessTools.Method(typeof(TestPatches), nameof(Extra));
    }
    public static bool Replace(CardModel __instance) { __instance.Value = 40; return false; }
    public static void Post(CardModel __instance) => __instance.Value += 3;
    public static void Before(CardModel __instance) => __instance.Value += 5;
    public static void After(CardModel __instance) => __instance.Value *= 2;
    public static void Extra() { }
}

internal class AsyncCard : CardModel
{
    protected override async void OnPlay(PlayerChoiceContext context, CardPlay play)
    {
        await Task.Yield();
        Value++;
    }
}
