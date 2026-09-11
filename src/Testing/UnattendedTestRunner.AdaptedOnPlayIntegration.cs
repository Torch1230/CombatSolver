using CombatSolver.Engine.Common;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private Action? _releaseAdaptedOnPlayIntegration;

    private static class IntegrationOnPlayPatch
    {
        public static int NativeCalls;

        // Replace Defend's block with an energy refund so executing the vanilla mirror
        // as well as the adapted mirror produces an observable native/predicted mismatch.
        public static bool Prefix(DefendIronclad __instance, ref Task __result)
        {
            NativeCalls++;
            __result = PlayerCmd.GainEnergy(1, __instance.Owner);
            return false;
        }

        public static void ExtraPostfix() { }
    }

    private void RegisterAdaptedOnPlayIntegration()
    {
        var target = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(DefendIronclad))!;
        var prefix = AccessTools.Method(typeof(IntegrationOnPlayPatch), nameof(IntegrationOnPlayPatch.Prefix));
        const string owner = "CombatSolver.Unattended.AdaptedOnPlay";
        AdaptedCardOnPlayMirrors.Register<DefendIronclad>("integration-v1", target,
            [new(HarmonyPatchType.Prefix, prefix, owner, Priority.Normal, [], [])],
            static (card, context) => context.Simulator.GainEnergy(card.Owner, 1));
        var previousMocks = AssemblyInfo.MockTypes;
        Mod mod = new()
        {
            path = "unattended-adapted-onplay",
            manifest = new() { id = "ADAPTED-ONPLAY-TEST", name = "Adapted OnPlay Test", affectsGameplay = true },
        };
        AssemblyInfo.MockTypes = previousMocks == null ? [] : new(previousMocks);
        AssemblyInfo.MockTypes[typeof(IntegrationOnPlayPatch)] = (mod, false);
        IntegrationOnPlayPatch.NativeCalls = 0;
        Harmony harmony = new(owner);
        _releaseAdaptedOnPlayIntegration = () =>
        {
            harmony.Unpatch(target, HarmonyPatchType.All, owner);
            AssemblyInfo.MockTypes = previousMocks;
        };
        harmony.Patch(target, prefix: new HarmonyMethod(prefix) { priority = Priority.Normal });
    }

    private async Task AssertAdaptedOnPlayIntegrationAsync(CombatState combat, Player player)
    {
        var root = CombatRootSnapshot.Capture(combat);
        var parent = root.ForkSimulator();
        var child = parent.Fork();
        var original = ((SimulatedCombatState)parent.State.CombatState).AdaptedOnPlay!;
        if (!ReferenceEquals(original, ((SimulatedCombatState)child.State.CombatState).AdaptedOnPlay))
            throw new InvalidOperationException("Fork did not preserve the immutable OnPlay selection.");
        ContinuationStamp before = ContinuationStamp.CaptureLive(combat);
        var target = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(DefendIronclad))!;
        var extra = AccessTools.Method(typeof(IntegrationOnPlayPatch), nameof(IntegrationOnPlayPatch.ExtraPostfix));
        Harmony changed = new("CombatSolver.Unattended.AdaptedOnPlay.Extra");
        try
        {
            changed.Patch(target, postfix: new HarmonyMethod(extra));
            if (ContinuationStamp.CaptureLive(combat) == before
                || original.Stamp != ((SimulatedCombatState)child.State.CombatState).AdaptedOnPlay!.Stamp)
                throw new InvalidOperationException("Patch change did not invalidate live identity or mutated the frozen root.");
            bool rejected = false;
            try { _ = CombatRootSnapshot.Capture(combat); }
            catch (PredictionUnsupportedException) { rejected = true; }
            if (!rejected)
                throw new InvalidOperationException("An unregistered patch composition was accepted.");
        }
        finally { changed.Unpatch(target, extra); }
        if (ContinuationStamp.CaptureLive(combat) != before)
            throw new InvalidOperationException("Restoring the patch composition changed live combat state.");
        _completedChecks.Add("AdaptedOnPlay:FrozenFork:LatePatchInvalidatesStamp:UnreviewedCompositionRejected");
        await AssertReportRoundAsync(combat, player);
        if (IntegrationOnPlayPatch.NativeCalls != 1)
            throw new InvalidOperationException("The native replacement was not executed exactly once.");
        _completedChecks.Add("AdaptedOnPlay:NativeReplacementOnce:FullSnapshot:IncrementalReplay:Fork:Turn1To2");
    }

    private void AssertAdaptedOnPlayCachedRoute()
    {
        if (!SolverController.CanExecuteCurrentTurn)
            throw new InvalidOperationException("The initial search did not produce an executable cached route.");
        var target = AdaptedCardOnPlayMirrors.ResolveOnPlay(typeof(DefendIronclad))!;
        var extra = AccessTools.Method(typeof(IntegrationOnPlayPatch), nameof(IntegrationOnPlayPatch.ExtraPostfix));
        Harmony changed = new("CombatSolver.Unattended.AdaptedOnPlay.CachedRoute");
        try
        {
            changed.Patch(target, postfix: new HarmonyMethod(extra));
            if (SolverController.CanExecuteCurrentTurn)
                throw new InvalidOperationException("The cached route remained executable after its OnPlay composition changed.");
        }
        finally { changed.Unpatch(target, extra); }
        if (!SolverController.CanExecuteCurrentTurn)
            throw new InvalidOperationException("Restoring the unchanged composition did not restore cached route validity.");
        _completedChecks.Add("AdaptedOnPlay:ControllerCachedRoute:PatchChangeDisablesExecution:UnpatchRestoresValidity");
    }
}
