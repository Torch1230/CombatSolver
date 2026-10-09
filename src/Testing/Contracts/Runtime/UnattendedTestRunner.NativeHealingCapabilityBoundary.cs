using System.Collections;
using System.Reflection;
using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using STS2RitsuLib.Combat.Healing;
using STS2RitsuLib.Models.Capabilities;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private sealed class NativeHealingBoundaryCapability : IModelCapability,
        IModelCapabilityCloneHandler, IHealHookListener
    {
        internal static int CloneCalls;
        internal static int HealCalls;
        public string CapabilityId => "combatsolver.testing.native-healing-boundary";
        public AbstractModel? Owner { get; private set; }
        public void Attach(AbstractModel owner, bool isInternal = false) => Owner = owner;
        public void Detach(bool isInternal = false) => Owner = null;
        public IModelCapability CloneFor(AbstractModel clonedOwner)
        {
            CloneCalls++;
            return new NativeHealingBoundaryCapability();
        }
        public decimal ModifyHealMultiplicative(HealContext context, decimal amount)
        {
            HealCalls++;
            return 2m;
        }
    }

    // The native persistence slot must be registered during mod initialization,
    // before Ritsu finalizes registrations. Only this explicit fixture activates it.
    internal static void PrepareNativeHealingCapabilityBoundary()
    {
        string request = UnattendedTestFiles.GlobalPath(UnattendedTestFiles.RequestUri);
        if (!File.Exists(request)) return;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(request));
        if (!document.RootElement.TryGetProperty("scenarioId", out JsonElement scenario)
            || scenario.GetString() != "NATIVE-HEALING-CAPABILITY-BOUNDARY") return;
        ModelCapabilities.EnsureInitialized();
        ModelCapabilityRegistry.Register<NativeHealingBoundaryCapability>(
            "combatsolver.testing.native-healing-boundary", () => new());
    }

    private async Task AssertNativeHealingCapabilityBoundaryAsync(CombatState live, Player player)
    {
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in live.Creatures.SelectMany(c => c.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live, player, new() { CardId = "DEFEND_REGENT", Pile = "Hand", TreatAsDeckCard = true });
        foreach (var potion in player.PotionSlots.ToArray()) potion?.Discard();
        await CreatureCmd.SetCurrentHp(player.Creature, 35);
        await PowerCmd.Apply<RegenPower>(new ThrowingPlayerChoiceContext(), player.Creature, 5, player.Creature, null);
        var card = player.PlayerCombatState!.AllCards.Single(c => c.Id.Entry == "DEFEND_REGENT");
        ModelCapabilitySet current = ModelCapabilities.Get(card);
        ModelCapabilitySet future = ModelCapabilities.Get(ModelDb.Card<SecretWeapon>());
        if (current.Count != 0 || future.Count != 0)
            throw new InvalidOperationException("Capability fixture sources are not empty.");
        var root = CombatRootSnapshot.Capture(live);
        var parent = root.ForkSimulator();
        if (!root.UsesComponentHealingCertificate
            || StrategicHpRecoveryBound.ComponentHealingUpperBound(parent, player, 0) != 15)
            throw new InvalidOperationException("Empty registered capability host changed clean certification.");
        string Stamp(CombatPredictionSimulator simulator) => DescribeContinuationContractState(simulator, root, player);
        string parentBefore = Stamp(parent), liveBefore = ContinuationStamp.CaptureLive(live).StateText;
        int initialClones = NativeHealingBoundaryCapability.CloneCalls;
        int initialHeals = NativeHealingBoundaryCapability.HealCalls;
        void Reject(string expected)
        {
            foreach (Action capture in new Action[] { () => CombatRootSnapshot.Capture(live), () => ContinuationStamp.CaptureLive(live) })
            {
                try { capture(); }
                catch (PredictionUnsupportedException error) when (error.Message.Contains(expected, StringComparison.Ordinal)) { continue; }
                throw new InvalidOperationException("Unknown model capability source was admitted.");
            }
            if (NativeHealingBoundaryCapability.CloneCalls != initialClones
                || NativeHealingBoundaryCapability.HealCalls != initialHeals)
                throw new InvalidOperationException("Capability inspection executed unknown behavior.");
        }
        foreach (ModelCapabilitySet host in new[] { current, future })
        {
            var capability = new NativeHealingBoundaryCapability();
            host.Apply(capability);
            try { Reject("Unsupported RitsuLib attached model capability"); }
            finally
            {
                if (!host.Remove(capability)) throw new InvalidOperationException("Owned capability was not removed.");
            }
        }
        MethodInfo load = typeof(ModelCapabilitySet).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Instance,
            null, [typeof(ModelCapabilitySaveDocument)], null)!;
        load.Invoke(future, [new ModelCapabilitySaveDocument
        {
            Capabilities = [new() { Id = "combatsolver.testing.unregistered-future-source" }]
        }]);
        try
        {
            if (future.Count != 0) throw new InvalidOperationException("Unknown saved source became an active capability.");
            Reject("Unsupported RitsuLib saved model capability");
        }
        finally { future.Clear(UnknownModelCapabilityPolicy.Remove); }
        Type defaults = typeof(ModelCapabilities).Assembly.GetType("STS2RitsuLib.Models.Capabilities.ModelCapabilityDefaults")!;
        FieldInfo modifiers = defaults.GetField("Modifiers", BindingFlags.NonPublic | BindingFlags.Static)!;
        FieldInfo cache = defaults.GetField("_modifiersByOwnerType", BindingFlags.NonPublic | BindingFlags.Static)!;
        var sync = (Lock)defaults.GetField("SyncRoot", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var values = (IList)modifiers.GetValue(null)!;
        if (values.Count != 0) throw new InvalidOperationException("Existing default modifiers in capability fixture.");
        object previousCache = cache.GetValue(null)!;
        int builds = 0;
        Action<AbstractModel, ModelCapabilityList> factory = (_, list) => { builds++; list.Add(new NativeHealingBoundaryCapability()); };
        MethodInfo registration = defaults.GetMethod("Modify", BindingFlags.Public | BindingFlags.Static, null,
            [typeof(string), typeof(string), typeof(Type), typeof(Action<AbstractModel, ModelCapabilityList>), typeof(int)], null)!;
        registration.Invoke(null, ["combatsolver.testing", "native-healing-boundary", typeof(RegenPower), factory, 0]);
        object owned = values[0]!;
        try
        {
            Reject("Unsupported RitsuLib default capability modifier");
            if (builds != 0) throw new InvalidOperationException("Default source inspection invoked its factory.");
        }
        finally
        {
            using (sync.EnterScope()) { values.Remove(owned); cache.SetValue(null, previousCache); }
            RitsuEmptyCapabilityFastPath.InvalidateDefaultCapabilitySources();
        }
        // Fork consumes the unchanged clean root after withdrawing the unknown
        // definitions. Late extension mutation during worker cloning is not certified.
        var children = Enumerable.Range(0, 16).Select(_ => parent.Fork()).ToArray();
        await Task.WhenAll(children.Select(child => Task.Run(() =>
        {
            using var isolation = SimulationNotificationIsolation.Enter();
            if (Stamp(child) != parentBefore || Stamp(child.Fork()) != parentBefore)
                throw new InvalidOperationException("Capability boundary changed complete Fork state/history/RNG.");
        })));
        if (Stamp(parent) != parentBefore || ContinuationStamp.CaptureLive(live).StateText != liveBefore
            || !CombatRootSnapshot.Capture(live).UsesComponentHealingCertificate)
            throw new InvalidOperationException("Capability cleanup changed parent/live or clean-root qualification.");
        _completedChecks.Add("NativeHealingCapabilityBoundary:RegisteredEmptyHostsAccepted:CurrentAndFuturePrototypeRejected:UnknownSavedSourceWithEmptyActiveListRejected:DefaultModifierRejected:LateLiveRejected:NoUnknownCloneHealFactoryCalls:16ForkFullStateHistoryRngAfterCleanup:ParentLiveUnchanged:FreshRootRecovers");
    }
}
