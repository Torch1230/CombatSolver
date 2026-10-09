using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertPlayerPotionCallbackBoundAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        foreach (var potion in player.PotionSlots.ToArray()) potion?.Discard();
        InjectPotionForTest(player, "ENERGY_POTION");
        await CreatureCmd.SetCurrentHp(player.Creature, 40);
        SetEnergy(player, 3);
        var field = typeof(Player).GetField("UsedPotionRemoved", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Native used-potion event backing field unavailable.");
        string BaselineTargets() => field.GetValue(player) is not Delegate callbacks ? "empty"
            : string.Join(',', callbacks.GetInvocationList().Select(callback =>
                callback.Method.DeclaringType?.FullName + ":" + callback.Method.Name));
        string baselineTargets = BaselineTargets();
        if (!baselineTargets.Contains(typeof(NPotionContainer).FullName!, StringComparison.Ordinal))
            throw new InvalidOperationException("Native potion UI callback not exercised.");
        var ordinary = CombatRootSnapshot.Capture(live);
        if (!ordinary.UsesComponentHealingCertificate || ordinary.InitialRemainingHealingUpperBound != 0)
            throw new InvalidOperationException("Ordinary zero-healing root not certified: " + ordinary.ComponentHealingRejection);
        Task? hpWrite = null;
        int calls = 0;
        void OnUsed(PotionModel potion)
        {
            if (++calls != 1) throw new InvalidOperationException("Unexpected callback count.");
            hpWrite = CreatureCmd.SetCurrentHp(player.Creature, player.Creature.MaxHp);
        }
        player.UsedPotionRemoved += OnUsed;
        try
        {
            CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
            if (root.UsesComponentHealingCertificate || root.CanCertifyRemainingHealing
                || root.UsesPruningComponentHealingCertificate || root.UsesKnownNativeHealingPolicy
                || root.HasOnlyPostCombatHealing || root.InitialRemainingHealingUpperBound != int.MaxValue
                || root.ComponentHealingRejection?.StartsWith("used-potion-callback:", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Unknown callback was not refused by all bound consumers.");
            var parent = root.ForkSimulator();
            var fork = parent.Fork();
            string parentBefore = DescribeContinuationContractState(parent, root, player);
            string forkBefore = DescribeContinuationContractState(fork, root, player);
            int parentBound = StrategicHpRecoveryBound.ComponentHealingUpperBound(parent, player, 0);
            int forkBound = StrategicHpRecoveryBound.ComponentHealingUpperBound(fork, player, 0);
            var ownedForks = Enumerable.Range(0, 16).Select(_ => parent.Fork()).ToArray();
            Parallel.For(0, ownedForks.Length, index =>
            {
                if (StrategicHpRecoveryBound.ComponentHealingUpperBound(ownedForks[index], player, 0) != int.MaxValue
                    || StrategicHpRecoveryBound.RemainingHealingUpperBound(ownedForks[index], player, 0) != int.MaxValue
                    || DescribeContinuationContractState(ownedForks[index], root, player) != parentBefore)
                    throw new InvalidOperationException("Frozen unknown callback proof changed across owned Forks.");
            });
            if (parentBound != int.MaxValue || forkBound != int.MaxValue || calls != 0)
                throw new InvalidOperationException("Root/Fork bound changed or capture invoked callback.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var potion = player.GetPotionAtSlotIndex(0)!;
            int beforeHp = player.Creature.CurrentHp;
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is UsePotionAction use && use.PotionIndex == 0 && ReferenceEquals(use.Player, player),
                () => potion.EnqueueManualUse(player.Creature), deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            await (hpWrite ?? throw new InvalidOperationException("No direct-HP callback task.")).WaitAsync(deadline.Token);
            int actualGain = player.Creature.CurrentHp - beforeHp;
            if (calls != 1 || actualGain != 40 || player.GetPotionAtSlotIndex(0) is not null
                || DescribeContinuationContractState(parent, root, player) != parentBefore
                || DescribeContinuationContractState(fork, root, player) != forkBefore)
                throw new InvalidOperationException("Native HP reset or frozen parent/Fork isolation differed.");
            _completedChecks.Add($"UnknownPotionHpCallback:UnknownSourceRefused:InitialBound=Infinity:ParentBound={parentBound}:ForkBound={forkBound}:NativeHpGain={actualGain}:CallbackCalls={calls}:SetCurrentHp:NativeUsePotionAction:NoCallbackDuringCapture:ParentForkFullStateHistoryRngStable:BaselineTargets={baselineTargets}:NoWrongWinningRoutePruneClaim");
        }
        finally { player.UsedPotionRemoved -= OnUsed; }
        if (!CombatRootSnapshot.Capture(live).UsesComponentHealingCertificate)
            throw new InvalidOperationException("Removing the unknown callback did not restore fresh-root certification.");
        var input = RunManager.Instance.InputSynchronizer;
        var tracker = RunManager.Instance.HoveredModelTracker;
        int hoverCalls = 0;
        void UnknownHover(ulong _) { hoverCalls++; }
        void RequireUnknownHoverRefusal()
        {
            var rejected = CombatRootSnapshot.Capture(live);
            if (rejected.CanCertifyRemainingHealing || rejected.UsesPruningComponentHealingCertificate
                || rejected.UsesKnownNativeHealingPolicy || PrimaryIncumbentTable.CanShareRoot(rejected)
                || rejected.InitialRemainingHealingUpperBound != int.MaxValue || hoverCalls != 0)
                throw new InvalidOperationException("Unknown hover dependency was certified or invoked.");
        }
        input.StateChanged += UnknownHover;
        try { RequireUnknownHoverRefusal(); }
        finally { input.StateChanged -= UnknownHover; }
        tracker.HoverChanged += UnknownHover;
        try { RequireUnknownHoverRefusal(); }
        finally { tracker.HoverChanged -= UnknownHover; }
        var previousMock = input.mockWaitSmall;
        try { input.mockWaitSmall = () => Task.CompletedTask; RequireUnknownHoverRefusal(); }
        finally { input.mockWaitSmall = previousMock; }
        var uiHelper = typeof(NPotionContainer).GetMethod("RemoveUsed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Native potion UI helper not found.");
        var patch = new Harmony("CombatSolver.PlayerPotionCallbackBound.Contract");
        try
        {
            patch.Patch(uiHelper, postfix: new HarmonyMethod(typeof(UnattendedTestRunner), nameof(UnreviewedPotionUiPostfix)));
            var patched = CombatRootSnapshot.Capture(live);
            if (patched.CanCertifyRemainingHealing || patched.UsesPruningComponentHealingCertificate
                || patched.UsesKnownNativeHealingPolicy || PrimaryIncumbentTable.CanShareRoot(patched))
                throw new InvalidOperationException("Patched UI callback dependency was certified.");
        }
        finally { patch.Unpatch(uiHelper, HarmonyPatchType.All, patch.Id); }
        if (!CombatRootSnapshot.Capture(live).UsesComponentHealingCertificate)
            throw new InvalidOperationException("Unpatching native UI dependency did not restore fresh-root admission.");
        _completedChecks.Add("PlayerPotionCallbackBound:16OwnedForkFullStateHistoryRngMetadata:UnknownDirectAndHoverEventsAndMockAndPatchedHelperRefused:NativeUiAccepted:FreshRootRecertifiedAfterRemoval:MetadataOnlyCapture");
    }
    private static void UnreviewedPotionUiPostfix() { }
}
