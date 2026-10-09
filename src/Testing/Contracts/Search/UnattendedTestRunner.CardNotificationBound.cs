using System.Reflection;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertCardNotificationBoundAsync(CombatState live, Player player)
    {
        await ClearPlayerPilesAsync(player);
        foreach (PotionModel? potion in player.PotionSlots.ToArray()) potion?.Discard();
        await InjectCardAsync(live, player, new() { CardId = "TRANSFIGURE", Pile = "Hand" });
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_NECROBINDER", Pile = "Hand" });
        await CreatureCmd.SetCurrentHp(player.Creature, 40);
        SetEnergy(player, 10);
        CardModel nativeCard = player.PlayerCombatState!.Hand.Cards.Single(card => card.Id.Entry == "TRANSFIGURE");
        CardModel observed = player.PlayerCombatState.Hand.Cards.Single(card => card.Id.Entry == "STRIKE_NECROBINDER");
        CombatRootSnapshot normalRoot = CombatRootSnapshot.Capture(live);
        if (!normalRoot.UsesPruningComponentHealingCertificate || normalRoot.InitialPruningHealingUpperBound != 0)
            throw new InvalidOperationException("Card callback fixture normal root rejected: " + normalRoot.PruningComponentHealingRejection);
        int callbacks = 0;
        void UnknownReplayChanged()
        {
            callbacks++;
            player.Creature.SetCurrentHpInternal(player.Creature.CurrentHp + 2);
        }
        observed.ReplayCountChanged += UnknownReplayChanged;
        try
        {
            CombatRootSnapshot root = CombatRootSnapshot.Capture(live);
            CombatPredictionSimulator parent = root.ForkSimulator();
            string before = DescribeContinuationContractState(parent, root, player);
            string liveBefore = ContinuationStamp.CaptureLive(live).StateText;
            SolverDisplayNames names = SolverDisplayNames.Capture(live);
            CombatPredictionSimulator Play(CombatPredictionSimulator sim, IReadOnlyList<PlanCardChoice>? choices)
            {
                using IDisposable isolation = SimulationNotificationIsolation.Enter();
                var combat = (SimulatedCombatState)sim.State.CombatState;
                combat.BeginActionChoices(choices);
                try
                {
                    using (combat.BeginCardExecutionScope(new ForkableSet<uint>()))
                        if (!sim.ManualPlay(sim.State.FindCard(nativeCard)!, null, out _) && !sim.HasPendingChoice)
                            throw new InvalidOperationException("Callback fixture could not simulate Transfigure.");
                    if (!sim.HasPendingChoice && !CombatBeamSolver.SettleReplayActionBoundary(sim, combat))
                        throw new InvalidOperationException("Callback fixture suspended at a later boundary.");
                }
                finally { combat.EndActionChoices(); }
                return sim;
            }
            CombatPredictionSimulator discovery = Play(parent.Fork(), null);
            var request = ((SimulatedCombatState)discovery.State.CombatState).PendingTurnStartChoice
                ?? throw new InvalidOperationException("Callback fixture missed Transfigure choice.");
            PlanCardChoice selected = CardChoiceSupport.BuildChoices(request.Spec!, names, 32, 32)
                .First(choice => choice.Cards.Any(card => card.CardId == observed.Id.Entry));
            CombatPredictionSimulator prediction = Play(parent.Fork(), [selected]);
            int shadowHp = prediction.State.GetCreature(player.Creature).CurrentHp;
            int bound = StrategicHpRecoveryBound.ComponentHealingUpperBound(prediction, player, 0, useReviewedSources: true);
            var children = Enumerable.Range(0, 16).Select(_ => parent.Fork()).ToArray();
            Parallel.For(0, 16, index =>
            {
                var child = Play(children[index], [selected]);
                if (DescribeContinuationContractState(child, root, player) != DescribeContinuationContractState(prediction, root, player)
                    || StrategicHpRecoveryBound.ComponentHealingUpperBound(child, player, 0, useReviewedSources: true) != bound)
                    throw new InvalidOperationException("Callback fixture full state/Fork/RNG differs.");
            });
            if (callbacks != 0 || shadowHp != 40
                || DescribeContinuationContractState(parent, root, player) != before
                || ContinuationStamp.CaptureLive(live).StateText != liveBefore)
                throw new InvalidOperationException("Callback ran in prediction or changed its parent/live state.");

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var session = NativeChoiceRuntime.Begin(live, player, "test:card-notification-proof-gap");
            session.SetPlanAndStartDriving(NGame.Instance!, [selected with { SourceId = nativeCard.Id.Entry }], deadline.Token);
            GameAction action = await SolverController.EnqueueAndCaptureActionAsync(
                queued => queued is PlayCardAction played && ReferenceEquals(played.NetCombatCard.ToCardModelOrNull(), nativeCard),
                () => { if (!nativeCard.TryManualPlay(null)) throw new InvalidOperationException("Native Transfigure could not play."); }, deadline.Token);
            await session.AwaitProducerAndCompleteAsync(action.CompletionTask).WaitAsync(deadline.Token);
            if (callbacks != 1 || player.Creature.CurrentHp != 42 || observed.BaseReplayCount != 1)
                throw new InvalidOperationException($"Native replay callback not reached: calls={callbacks}, hp={player.Creature.CurrentHp}, replay={observed.BaseReplayCount}.");
            _completedChecks.Add($"CardNotificationGap:NativeTransfigure:ReplayChanged:NativeHp=42:ShadowHp={shadowHp}:Bound={bound}:RootCertified={root.UsesPruningComponentHealingCertificate}:16ForkStateHistoryRNGParentLiveIsolation");
            if (root.UsesPruningComponentHealingCertificate || bound != int.MaxValue)
                throw new InvalidOperationException("Unknown card replay callback can restore 2 HP but retained finite recovery proof: "
                    + $"root={root.UsesPruningComponentHealingCertificate}, bound={bound}, reason={root.PruningComponentHealingRejection}.");
        }
        finally { observed.ReplayCountChanged -= UnknownReplayChanged; }

        // A rejected root stays rejected after the live audience disappears;
        // a fresh root samples the new audience without changing older Forks.
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(live, player, new() { CardId = "STRIKE_NECROBINDER", Pile = "Hand" });
        CardModel probe = player.PlayerCombatState.Hand.Cards.Single();
        int forbiddenCalls = 0;
        Action unknown = () => forbiddenCalls++;
        string[] notifications = ["EnergyCostChanged", "KeywordsChanged", "ReplayCountChanged", "Played",
            "Drawn", "StarCostChanged", "Upgraded", "Forged"];
        int refusals = 0;
        CombatRootSnapshot? frozenRefusal = null;
        void CheckAudiences(CardModel card, string location)
        {
            CombatRootSnapshot clean = CombatRootSnapshot.Capture(live);
            if (!clean.UsesPruningComponentHealingCertificate || clean.InitialPruningHealingUpperBound != 0)
                throw new InvalidOperationException("Ordinary card notification root rejected at " + location
                    + ":" + clean.PruningComponentHealingRejection);
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
                        || rejected.UsesPruningComponentHealingCertificate || rejected.UsesKnownNativeHealingPolicy
                        || rejected.HasOnlyPostCombatHealing || PrimaryIncumbentTable.CanShareRoot(rejected)
                        || rejected.InitialRemainingHealingUpperBound != int.MaxValue
                        || rejected.InitialPruningHealingUpperBound != int.MaxValue
                        || rejected.PruningComponentHealingRejection?.StartsWith("card-callback:" + name + ":", StringComparison.Ordinal) != true)
                        throw new InvalidOperationException("Card callback refusal missing at " + location + ":" + name
                            + ":" + rejected.PruningComponentHealingRejection);
                    var sim = rejected.ForkSimulator();
                    if (StrategicHpRecoveryBound.RemainingHealingUpperBound(sim, player, 0) != int.MaxValue
                        || StrategicHpRecoveryBound.ComponentHealingUpperBound(sim, player, 0, useReviewedSources: true) != int.MaxValue
                        || DescribeContinuationContractState(cleanParent, clean, player) != cleanBefore
                        || ContinuationStamp.CaptureLive(live).StateText != liveBefore || forbiddenCalls != 0)
                        throw new InvalidOperationException("Card callback capture changed a frozen parent/live state.");
                    frozenRefusal = rejected;
                    refusals++;
                }
                finally { notification.RemoveEventHandler(card, unknown); }
            }
            if (frozenRefusal!.UsesPruningComponentHealingCertificate
                || StrategicHpRecoveryBound.ComponentHealingUpperBound(frozenRefusal.ForkSimulator(), player, 0,
                    useReviewedSources: true) != int.MaxValue
                || !CombatRootSnapshot.Capture(live).UsesPruningComponentHealingCertificate)
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
        _completedChecks.Add($"CardNotificationBound:EightEvents:FiveCombatPiles:Floating:PermanentDeck:Refusals={refusals}:LateBindingAndRemoval:FrozenRootParentLiveIsolation");
    }
}
