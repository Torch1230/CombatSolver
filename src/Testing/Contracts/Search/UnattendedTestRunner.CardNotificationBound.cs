using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCardNotificationBoundAsync(CombatState live, Player player)
    {
        CombatRootSnapshot initial = CombatRootSnapshot.Capture(live);
        SimulatedCombatState initialCombat = (SimulatedCombatState)initial.ForkSimulator().State.CombatState;
        if (initialCombat.RootHealingCallbackRejection is { } initialRejection)
            throw new InvalidOperationException("Untouched IRONCLAD combat UI callback rejected: " + initialRejection);

        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        foreach (PotionModel? potion in player.PotionSlots.ToArray()) potion?.Discard();
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live, player, new() { CardId = "BULLET_TIME", Pile = "Hand" });
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        await CreatureCmd.SetCurrentHp(player.Creature, 40);
        SetEnergy(player, 10);
        CardModel nativeCard = player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == "BULLET_TIME");
        CardModel observed = player.PlayerCombatState.Hand.Cards.Single(card => card.Id.Entry == "STRIKE_IRONCLAD");
        CombatRootSnapshot normalRoot = CombatRootSnapshot.Capture(live);
        if (!normalRoot.UsesComponentHealingCertificate || normalRoot.InitialRemainingHealingUpperBound != 0)
            throw new InvalidOperationException("Card callback fixture normal root rejected: " + normalRoot.ComponentHealingRejection);
        int callbacks = 0;
        void UnknownStarCostChanged()
        {
            callbacks++;
            player.Creature.SetCurrentHpInternal(player.Creature.CurrentHp + 2);
        }
        observed.StarCostChanged += UnknownStarCostChanged;
        try
        {
            CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
            CombatPredictionSimulator parent = root.ForkSimulator();
            string before = DescribeContinuationContractState(parent, root, player);
            string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            CombatPredictionSimulator Play(CombatPredictionSimulator sim)
            {
                using IDisposable isolation = SimulationNotificationIsolation.Enter();
                var combat = (SimulatedCombatState)sim.State.CombatState;
                combat.BeginActionChoices((IReadOnlyList<PlanCardChoice>?)null);
                try
                {
                    using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                        if (!sim.ManualPlay(sim.State.FindCard(nativeCard)!, null, out _))
                            throw new InvalidOperationException("Callback fixture could not simulate Bullet Time.");
                    if (!CombatBeamSolver.SettleReplayActionBoundary(sim, combat))
                        throw new InvalidOperationException("Callback fixture suspended unexpectedly.");
                }
                finally { combat.EndActionChoices(); }
                return sim;
            }
            CombatPredictionSimulator prediction = Play(parent.Fork());
            int shadowHp = prediction.State.GetCreature(player.Creature).CurrentHp;
            int bound = StrategicHpRecoveryBound.ComponentHealingUpperBound(prediction, player, 0);
            var children = Enumerable.Range(0, 16).Select(_ => parent.Fork()).ToArray();
            Parallel.For(0, 16, index =>
            {
                var child = Play(children[index]);
                if (DescribeContinuationContractState(child, root, player) != DescribeContinuationContractState(prediction, root, player)
                    || StrategicHpRecoveryBound.ComponentHealingUpperBound(child, player, 0) != bound)
                    throw new InvalidOperationException("Callback fixture full state/Fork/RNG differs.");
            });
            if (callbacks != 0 || shadowHp != 40
                || DescribeContinuationContractState(parent, root, player) != before
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException("Callback ran in prediction or changed its parent/live state.");

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
                () => { if (!nativeCard.TryManualPlay(null)) throw new InvalidOperationException("Native Bullet Time could not play."); },
                deadline.Token);
            await action.CompletionTask.WaitAsync(deadline.Token);
            if (callbacks != 1 || player.Creature.CurrentHp != 42
                || observed.EnergyCost.GetWithModifiers(CostModifiers.Local) != 0)
                throw new InvalidOperationException(
                    $"Native star-cost callback not reached: calls={callbacks}, hp={player.Creature.CurrentHp}, cost={observed.EnergyCost.GetWithModifiers(CostModifiers.Local)}.");
            _completedChecks.Add($"CardNotificationGap:NativeBulletTime:StarCostChanged:NativeHp=42:ShadowHp={shadowHp}:Bound={bound}:RootCertified={root.UsesComponentHealingCertificate}:16ForkStateHistoryRNGParentLiveIsolation");
            if (root.UsesComponentHealingCertificate || bound != int.MaxValue
                || root.ComponentHealingRejection?.StartsWith("card-callback:StarCostChanged:", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Unknown card notification callback can restore 2 HP but retained finite recovery proof: "
                    + $"root={root.UsesComponentHealingCertificate}, bound={bound}, reason={root.ComponentHealingRejection}.");
        }
        finally { observed.StarCostChanged -= UnknownStarCostChanged; }

        // A rejected root stays rejected after the live audience disappears;
        // a fresh root samples the new audience without changing older Forks.
        foreach (var power in live.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_IRONCLAD", Pile = "Hand" });
        CardModel probe = player.PlayerCombatState.Hand.Cards.Single();
        int forbiddenCalls = 0;
        Action unknown = () => forbiddenCalls++;
        string[] notifications =
        [
            "EnergyCostChanged", "KeywordsChanged", "ReplayCountChanged", "Played",
            "Drawn", "StarCostChanged", "Upgraded", "Forged",
        ];
        int refusals = 0;
        CombatRootSnapshot? frozenRefusal = null;
        void CheckAudiences(CardModel card, string location)
        {
            CombatRootSnapshot clean = CombatRootSnapshot.Capture(live);
            if (!clean.UsesComponentHealingCertificate || clean.InitialRemainingHealingUpperBound != 0)
                throw new InvalidOperationException("Ordinary card notification root rejected at " + location
                    + ":" + clean.ComponentHealingRejection);
            var cleanParent = clean.ForkSimulator();
            string cleanBefore = DescribeContinuationContractState(cleanParent, clean, player);
            foreach (string name in notifications)
            {
                EventInfo notification = typeof(CardModel).GetEvent(name)!;
                notification.AddEventHandler(card, unknown);
                try
                {
                    string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
                    var rejected = CombatRootSnapshot.Capture(live);
                    if (rejected.CanCertifyRemainingHealing || rejected.UsesComponentHealingCertificate
                        || rejected.UsesKnownNativeHealingPolicy || rejected.HasOnlyPostCombatHealing
                        || PrimaryIncumbentTable.CanShareRoot(rejected)
                        || rejected.InitialRemainingHealingUpperBound != int.MaxValue
                        || rejected.ComponentHealingRejection?.StartsWith("card-callback:" + name + ":", StringComparison.Ordinal) != true)
                        throw new InvalidOperationException("Card callback refusal missing at " + location + ":" + name
                            + ":" + rejected.ComponentHealingRejection);
                    var sim = rejected.ForkSimulator();
                    if (StrategicHpRecoveryBound.RemainingHealingUpperBound(sim, player, 0) != int.MaxValue
                        || StrategicHpRecoveryBound.ComponentHealingUpperBound(sim, player, 0) != int.MaxValue
                        || DescribeContinuationContractState(cleanParent, clean, player) != cleanBefore
                        || ContinuationStamp.CaptureLive(live).StateText != liveBefore || forbiddenCalls != 0)
                        throw new InvalidOperationException("Card callback capture changed a frozen parent/live state.");
                    frozenRefusal = rejected;
                    refusals++;
                }
                finally { notification.RemoveEventHandler(card, unknown); }
            }
            if (frozenRefusal!.UsesComponentHealingCertificate
                || StrategicHpRecoveryBound.ComponentHealingUpperBound(frozenRefusal.ForkSimulator(), player, 0) != int.MaxValue
                || !CombatRootSnapshot.Capture(live).UsesComponentHealingCertificate)
                throw new InvalidOperationException("Card callback removal mutated old admission or did not restore fresh roots.");
        }
        foreach (PileType pile in new[] { PileType.Hand, PileType.Draw, PileType.Discard, PileType.Exhaust, PileType.Play })
        {
            await CardPileCmd.Add(probe, pile);
            CheckAudiences(probe, pile.ToString());
        }
        // Remove only the pile membership: native CombatState._allCards still owns
        // this temporarily absent instance, as for a pending return.
        probe.Pile!.RemoveInternal(probe, silent: true);
        CheckAudiences(probe, "Floating");
        await CardPileCmd.Add(probe, PileType.Hand);
        CheckAudiences(player.Deck.Cards.First(), "PermanentDeck");
        _completedChecks.Add($"CardNotificationBound:UntouchedIroncladUiAccepted:EightEvents:FiveCombatPiles:Floating:PermanentDeck:Refusals={refusals}:LateBindingAndRemoval:FrozenRootParentLiveIsolation");
    }
}
