using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Mirrors.Hooks.Card;
using CombatSolver.Engine.InCombat.Simulation;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task PrepareCompactPanacheAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, 1, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        for (int index = 0; index < 7; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection
            {
                CardId = index < 2 ? "PANACHE" : index == 2 ? "SURVIVOR" : index == 3 ? "PREPARED" : "DEFEND_NECROBINDER",
                UpgradeLevels = index is 1 or 3 ? 1 : 0, Pile = "Hand",
                EnchantmentId = index == 1 ? "SWIFT" : null, EnchantmentAmount = index == 1 ? 3 : 0
            });
        for (int index = 0; index < 5; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = index < 2 ? "Draw" : "Discard" });
        var choice = new BlockingPlayerChoiceContext();
        for (int index = 0; index < mode; index++)
        {
            var power = await PowerCmd.Apply<PanachePower>(choice, player.Creature, index == 0 ? 6 : 9, player.Creature, null)
                ?? throw new InvalidOperationException("Panache root instance was not applied.");
            power.DynamicVars["CardsLeft"].BaseValue = index == 0 ? 1 : 3;
            power.GetInternalData<PanachePower.Data>().alreadyApplied = mode == 2;
            power.AmountOnTurnStart = 37;
            if (index == 0 && mode == 2) power._target = combat.Enemies.Single();
        }
        await PowerCmd.Apply<WeakPower>(choice, player.Creature, 2, combat.Enemies.Single(), null);
        await PowerCmd.Apply<VulnerablePower>(choice, combat.Enemies.Single(), 2, player.Creature, null);
        await CreatureCmd.SetMaxHp(combat.Enemies.Single(), 10_000);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 10_000);
        SetEnergy(player, 20);
    }

    private async Task AssertCompactPanacheAsync(CombatState combat, Player player)
    {
        for (int mode = 0; mode < 3; mode++)
        {
            await PrepareCompactPanacheAsync(combat, player, mode);
            if (mode == 1) AssertPanacheFlagKey(combat, player);
            int nativePendingInstances = 0;
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactPanache", "compact-panache",
                [("PANACHE", 0), ("PANACHE", 1), ("SURVIVOR", 0), ("DEFEND_NECROBINDER", 0), ("DEFEND_NECROBINDER", 0), ("DEFEND_NECROBINDER", 0)],
                (lane, before, step) =>
                {
                    if (step < 2 && lane.PanacheCount != before.PanacheCount + 1)
                        throw new InvalidOperationException("Playing a Power must create one independent counter.");
                    var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                    int? amount = null;
                    foreach (var item in events)
                    {
                        if (item.Kind == ResumableDiscardProgram.EventKind.PanacheStart) amount = lane.Panache(item.Target).Amount;
                        if (item.Kind == ResumableDiscardProgram.EventKind.PanacheFinish) amount = null;
                        if (amount != null && item.Kind == ResumableDiscardProgram.EventKind.Damage
                            && ((item.Flags & (int)(ResumableDiscardProgram.DamageTraits.Unpowered | ResumableDiscardProgram.DamageTraits.NoCard)) == 0 || item.Dealer != 0))
                            throw new InvalidOperationException("Independent Power damage inherited a card or attack source.");
                    }
                    if (Enumerable.Range(0, lane.PanacheCount).Any(index => lane.Panache(index).CardsLeft is < 1 or > 5 || !lane.Panache(index).AlreadyApplied))
                        throw new InvalidOperationException("Independent completion counters are outside their lifecycle.");
                }, forceOpeningPower: true, chooseBranch: (action, step) => step != 2 || action.GetActionChoicesInExecutionOrder()
                    .SelectMany(choice => choice.Cards).Any(token => token.CardId == "PREPARED"), observeNativeChoice: (action, _) =>
                {
                    if (action.CardId != "PANACHE") return false;
                    var newest = player.Creature.Powers.OfType<PanachePower>().Last();
                    if (newest.Amount != 14 || newest.GetInternalData<PanachePower.Data>().alreadyApplied
                        || newest.DynamicVars["CardsLeft"].IntValue != 5)
                        throw new InvalidOperationException("Native enchantment choice must precede the new instance's first completion hook.");
                    nativePendingInstances++;
                    return true;
                });
            if (nativePendingInstances == 0) throw new InvalidOperationException("Independent Power creation did not reach its native suspended enchantment.");
            _completedChecks.Add($"CompactPanache:Mode{mode}:RootInstances{mode}:TwoNewAmounts:NestedSlyCompletion:TwoRounds:FullHistoryAndContinuations");
        }
    }

    private static void AssertPanacheFlagKey(CombatState combat, Player player)
    {
        var root = CombatRootSnapshot.Capture(combat);
        using var isolation = SimulationNotificationIsolation.Enter();
        var first = root.ForkSimulator();
        var second = first.Fork();
        var a = (SimulatedCombatState)first.State.CombatState;
        var b = (SimulatedCombatState)second.State.CombatState;
        var power = b.EffectivePowers().OfType<PanachePower>().Single();
        second.StateStore.Get(power, () => new PanachePredictionState(power)).AlreadyApplied = true;
        StateFingerprintBuilder left = new(), right = new();
        a.AppendFingerprint(ref left, first); b.AppendFingerprint(ref right, second);
        if (left.Finish() == right.Finish() || PowerPredictionStateSupport.PanacheAlreadyApplied(first, a.EffectivePowers().OfType<PanachePower>().Single()))
            throw new InvalidOperationException("Panache activation must distinguish branch keys without mutating its parent.");
        var card = player.PlayerCombatState!.Hand.Cards[0];
        if (!first.ManualPlay(first.State.FindCard(card)!, null, out _) || !second.ManualPlay(second.State.FindCard(card)!, null, out _))
            throw new InvalidOperationException("Panache flag witness unexpectedly suspended.");
        var enemy = combat.Enemies.Single();
        if (first.State.GetCreature(enemy).CurrentHp == second.State.GetCreature(enemy).CurrentHp)
            throw new InvalidOperationException("Different Panache flags did not produce the expected distinct future.");
    }

    private async Task AssertCompactPanacheTerminalAsync(CombatState combat, Player player, bool cardKills = false)
    {
        await PrepareCompactPanacheAsync(combat, player, 2);
        await ClearPlayerPilesAsync(player);
        string cardId = cardKills ? "STRIKE_NECROBINDER" : "DEFEND_NECROBINDER";
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = cardId, Pile = "Hand" });
        foreach (var power in player.Creature.Powers.OfType<PanachePower>()) power.DynamicVars["CardsLeft"].BaseValue = 1;
        await SetBlockAsync(combat.Enemies.Single(), 0);
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 1);
        await AssertCompactPetCardRouteAsync(combat, player, 3, "CompactPanacheTerminal", "compact-panache-terminal", [(cardId, 0)],
            (lane, before, _) =>
            {
                var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                if (!lane.Terminal || events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.PanacheStart) != 2
                    || events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.Damage) != 1
                    || Enumerable.Range(0, lane.PanacheCount).Any(index => lane.Panache(index).CardsLeft != 5))
                    throw new InvalidOperationException("Last-kill damage changed the completion dispatch entry or remaining listener counters.");
            }, rounds: 0, requirePending: false);
        _completedChecks.Add($"CompactPanacheTerminal:CardKills{cardKills}:DispatchEntryAndRemainingListeners:OneNativeDamage:BeforeTeardown:FullState");
    }

    private async Task AssertCompactPanachePlayerDeathAsync(CombatState combat, Player player)
    {
        await PrepareCompactPanacheAsync(combat, player, 1);
        await PowerCmd.Apply<DoomPower>(new BlockingPlayerChoiceContext(), player.Creature, player.Creature.CurrentHp, player.Creature, null);
        await PowerCmd.Apply<PoisonPower>(new BlockingPlayerChoiceContext(), combat.Enemies.Single(), 3, player.Creature, null);
        await AssertCompactPetCardRouteAsync(combat, player, 4, "CompactPanachePlayerDeath", "compact-panache-player-death", [("PANACHE", 0)],
            (lane, _, step) =>
            {
                if (step == 1 && (!lane.DefeatTerminal || lane.PanacheCount != 2
                    || Enumerable.Range(0, lane.PanacheCount).Any(index => lane.Panache(index).Amount != 0)))
                    throw new InvalidOperationException("Owner death must retire both the captured slot and the new independent instance.");
            }, rounds: 1, requirePending: false, forceOpeningPower: true);
        _completedChecks.Add("CompactPanachePlayerDeath:NativeOwnerDoom:CapturedAndNewInstancesRemoved:PetCleanup:EnemyBlockClears:NewPoisonDispatchSkipped:FullState:BeforeTeardown");
    }
}
