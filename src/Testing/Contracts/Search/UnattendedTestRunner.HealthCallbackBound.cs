using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.ValueProps;
using STS2RitsuLib.Combat.Healing;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertHealthCallbackBoundAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        foreach (var potion in player.PotionSlots.ToArray()) potion?.Discard();
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        await CreatureCmd.SetCurrentHp(player.Creature, 40);
        var game = NGame.Instance ?? throw new InvalidOperationException("Native game node unavailable.");
        for (int i = 0; i < 4; i++) await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
        CombatRootSnapshot RequireOrdinary()
        {
            var root = CombatRootSnapshot.Capture(live);
            if (!root.UsesComponentHealingCertificate || root.InitialRemainingHealingUpperBound != 0)
                throw new InvalidOperationException("Ordinary zero-healing root rejected: " + root.ComponentHealingRejection);
            return root;
        }
        int unknownCalls = 0;
        CombatRootSnapshot RequireRefusal(string reason)
        {
            var root = CombatRootSnapshot.Capture(live);
            if (root.CanCertifyRemainingHealing || root.UsesComponentHealingCertificate
                || root.UsesPruningComponentHealingCertificate || root.UsesKnownNativeHealingPolicy
                || root.HasOnlyPostCombatHealing || PrimaryIncumbentTable.CanShareRoot(root)
                || root.InitialRemainingHealingUpperBound != int.MaxValue
                || root.ComponentHealingRejection?.StartsWith(reason, StringComparison.Ordinal) != true || unknownCalls != 0)
                throw new InvalidOperationException("Health callback refusal missing: " + reason + ":" + root.ComponentHealingRejection);
            var parent = root.ForkSimulator();
            string before = DescribeContinuationContractState(parent, root, player);
            var children = Enumerable.Range(0, 16).Select(_ => parent.Fork()).ToArray();
            Parallel.For(0, children.Length, i =>
            {
                if (StrategicHpRecoveryBound.ComponentHealingUpperBound(children[i], player, 0) != int.MaxValue
                    || StrategicHpRecoveryBound.RemainingHealingUpperBound(children[i], player, 0) != int.MaxValue
                    || DescribeContinuationContractState(children[i], root, player) != before)
                    throw new InvalidOperationException("Owned Fork health admission/state/history/RNG differed.");
            });
            if (DescribeContinuationContractState(parent, root, player) != before || unknownCalls != 0)
                throw new InvalidOperationException("Admission/Fork invoked a live callback or changed parent.");
            _completedChecks.Add("HealthCallbackBound:Refused:" + reason + ":16OwnedForkFullStateHistoryRng:NoCallbackDuringCapture");
            return root;
        }
        var ordinary = RequireOrdinary();
        var predicted = ordinary.ForkSimulator();
        using (SimulationNotificationIsolation.Enter()) predicted.Damage(player.Creature, 4, ValueProp.Unblockable | ValueProp.Unpowered, null);
        await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), player.Creature, 4, ValueProp.Unblockable | ValueProp.Unpowered, null!);
        string expected = ContinuationStamp.CapturePredicted(player, predicted, ordinary.StartTurnNumber,
            ordinary.Forecast, ordinary.StartTurnNumber).StateText;
        string actual = ContinuationStamp.CaptureLive(live).StateText;
        if (expected != actual) throw new InvalidOperationException("Ordinary native damage full continuation/RNG differed.");
        await CreatureCmd.SetCurrentHp(player.Creature, 40);
        void UnknownHealth(int before, int after) { unknownCalls++; }
        player.Creature.CurrentHpChanged += UnknownHealth;
        try { RequireRefusal("health-callback:CurrentHpChanged:"); }
        finally { player.Creature.CurrentHpChanged -= UnknownHealth; }
        player.Creature.MaxHpChanged += UnknownHealth;
        try { RequireRefusal("health-callback:MaxHpChanged:"); }
        finally { player.Creature.MaxHpChanged -= UnknownHealth; }
        Creature enemy = live.Enemies.First();
        enemy.CurrentHpChanged += UnknownHealth;
        try { RequireRefusal("health-callback:CurrentHpChanged:"); }
        finally { enemy.CurrentHpChanged -= UnknownHealth; }
        enemy.MaxHpChanged += UnknownHealth;
        try { RequireRefusal("health-callback:MaxHpChanged:"); }
        finally { enemy.MaxHpChanged -= UnknownHealth; }
        var tracker = CombatManager.Instance.StateTracker;
        void UnknownTracker(CombatState _) { unknownCalls++; }
        tracker.CombatStateChanged += UnknownTracker;
        try { RequireRefusal("health-tracker-callback:"); }
        finally { tracker.CombatStateChanged -= UnknownTracker; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var originalHealthAudiences = new List<(Creature Creature, FieldInfo Field, Delegate Original)>();
        int removedTrackerBindings = 0;
        foreach (Creature creature in live.Creatures)
            foreach (string eventName in new[] { "CurrentHpChanged", "MaxHpChanged" })
            {
                FieldInfo field = typeof(Creature).GetField(eventName, flags)!;
                if (field.GetValue(creature) is not Delegate callbacks) continue;
                originalHealthAudiences.Add((creature, field, callbacks));
                foreach (var callback in callbacks.GetInvocationList().Where(callback => ReferenceEquals(callback.Target, tracker)))
                {
                    removedTrackerBindings++;
                    field.SetValue(creature, Delegate.Remove((Delegate?)field.GetValue(creature), callback));
                }
            }
        int energyCallbacks = 0;
        bool energyHealed = false;
        void HealFromEnergyNotification(CombatState _)
        {
            energyCallbacks++;
            if (!energyHealed) { energyHealed = true; player.Creature.HealInternal(2); }
        }
        tracker.CombatStateChanged += HealFromEnergyNotification;
        try
        {
            if (removedTrackerBindings == 0) throw new InvalidOperationException("No HP tracker bridge removed.");
            var rejected = RequireRefusal("health-tracker-callback:");
            var parent = rejected.ForkSimulator();
            string before = DescribeContinuationContractState(parent, rejected, player);
            if (energyCallbacks != 0) throw new InvalidOperationException("Capture invoked independent tracker callback.");
            int hpBefore = player.Creature.CurrentHp;
            player.PlayerCombatState!.Energy++;
            for (int i = 0; i < 10 && !energyHealed; i++)
                await game.ToSignal(game.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!energyHealed || energyCallbacks < 1 || player.Creature.CurrentHp != hpBefore + 2
                || DescribeContinuationContractState(parent, rejected, player) != before)
                throw new InvalidOperationException("Independent energy notification healing or parent isolation differed.");
            _completedChecks.Add("HealthCallbackBound:NoHpTrackerBridge:NativeEnergyNotificationAddsTwoHp:InfiniteBound:ParentFullStateHistoryRngUnchanged");
        }
        finally
        {
            tracker.CombatStateChanged -= HealFromEnergyNotification;
            foreach (var (creature, field, original) in originalHealthAudiences) field.SetValue(creature, original);
        }
        await CreatureCmd.SetCurrentHp(player.Creature, 40);
        var trackerCallbacks = (Delegate)typeof(CombatStateTracker).GetField("CombatStateChanged", flags)!.GetValue(tracker)!;
        var panel = trackerCallbacks.GetInvocationList().Single(callback => callback.Method.DeclaringType?.FullName
            == "STS2RitsuLib.Settings.RitsuDebugToolsPanel").Target!;
        var browser = (Node)panel.GetType().GetField("_currentBrowser", flags)!.GetValue(panel)!;
        Type detailType = typeof(HealHook).Assembly.GetType("STS2RitsuLib.Settings.RitsuDebugLiveDetailContainer", true)!;
        var detail = (Node)Activator.CreateInstance(detailType, true)!;
        detailType.GetMethod("RegisterRefresh", flags)!.Invoke(detail, [(Action)(() => unknownCalls++)]);
        browser.AddChild(detail);
        try { RequireRefusal("health-detail-callbacks:"); }
        finally { browser.RemoveChild(detail); detail.QueueFree(); }
        var harmony = new Harmony("CombatSolver.HealthCallbackBound.Contract");
        foreach (MethodInfo method in new[] { AccessTools.PropertySetter(typeof(Creature), "CurrentHp"),
            AccessTools.Method(typeof(CombatStateTracker), "NotifyCombatStateChanged", [typeof(string)]),
            AccessTools.Method(typeof(PlayerCombatState), "RecalculateCardValues"),
            AccessTools.Method(panel.GetType(), "RefreshCurrentState"), AccessTools.Method(detailType, "RefreshState") })
        {
            try
            {
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(UnattendedTestRunner), nameof(UnreviewedHealthPostfix)));
                RequireRefusal("health-");
            }
            finally { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); }
        }
        int healingCalls = 0;
        void RestoreAfterDamage(int before, int after)
        {
            healingCalls++;
            if (after < before) player.Creature.HealInternal(2);
        }
        player.Creature.CurrentHpChanged += RestoreAfterDamage;
        try
        {
            var rejected = RequireRefusal("health-callback:CurrentHpChanged:");
            var parent = rejected.ForkSimulator();
            string before = DescribeContinuationContractState(parent, rejected, player);
            var child = parent.Fork();
            using (SimulationNotificationIsolation.Enter()) child.Damage(player.Creature, 6, ValueProp.Unblockable | ValueProp.Unpowered, null);
            if (healingCalls != 0) throw new InvalidOperationException("Shadow invoked live healing callback.");
            await CreatureCmd.Damage(new BlockingPlayerChoiceContext(), player.Creature, 6, ValueProp.Unblockable | ValueProp.Unpowered, null!);
            if (healingCalls != 2 || player.Creature.CurrentHp != child.State.GetCreature(player.Creature).CurrentHp + 2
                || DescribeContinuationContractState(parent, rejected, player) != before)
                throw new InvalidOperationException("Native event healing or frozen parent isolation differed.");
            _completedChecks.Add("HealthCallbackBound:NativeDamageEventAddsTwoHp:InfiniteBound:ShadowDoesNotInvokeLive:ParentFullStateHistoryRngUnchanged");
        }
        finally { player.Creature.CurrentHpChanged -= RestoreAfterDamage; }
        RequireOrdinary();
        _completedChecks.Add("HealthCallbackBound:OrdinaryNativeDamageFullContinuationRng:UnknownPlayerEnemyTrackerDetailAndPatches:FreshRootRestored:NoCompleteUiClosureClaim");
    }

    private static void UnreviewedHealthPostfix() { }
}
