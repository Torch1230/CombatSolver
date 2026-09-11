using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private static int CountNativeAttackStarts(CombatState combat, Player player)
        => CombatManager.Instance.History.CardPlaysStarted.Count(entry => entry.HappenedThisTurn(combat)
            && entry.CardPlay.Card.Type == CardType.Attack && entry.CardPlay.Player == player);

    private async Task PrepareCompactLethalityAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, mode == 2 ? 0 : 1, withTurnRelic: true);
        var enemy = combat.Enemies.Single();
        if (mode == 2)
        {
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_NECROBINDER", Pile = "Hand" });
            if (!player.PlayerCombatState!.Hand.Cards.Last().TryManualPlay(enemy)) throw new InvalidOperationException("Native attack-history seed rejected.");
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        }
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "LETHALITY", Pile = "Hand", UpgradeLevels = mode == 2 ? 0 : 1 });
        foreach (string id in new[] { "HANG", "UNLEASH", "GRAVEBLAST", "CAPTURE_SPIRIT" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        for (int index = 0; index < 5; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "STRIKE_NECROBINDER", Pile = index < 2 ? "Draw" : "Discard" });
        await CreatureCmd.SetMaxHp(enemy, 10_000); await CreatureCmd.SetCurrentHp(enemy, 10_000);
        var choice = new BlockingPlayerChoiceContext();
        await PowerCmd.Apply<WeakPower>(choice, player.Creature, 2, enemy, null);
        await PowerCmd.Apply<VulnerablePower>(choice, enemy, 2, player.Creature, null);
        await PowerCmd.Apply<HangPower>(choice, enemy, 3, player.Creature, null);
        if (mode != 0) await PowerCmd.Apply<LethalityPower>(choice, player.Creature, 50, player.Creature, null);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers)) power.AmountOnTurnStart = 37;
        SetEnergy(player, 20);
        if (CountNativeAttackStarts(combat, player) != (mode == 2 ? 2 : 0))
            throw new InvalidOperationException("Lethality fixture failed to establish its native root history.");
    }

    private async Task AssertCompactLethalityAsync(CombatState combat, Player player)
    {
        for (int mode = 0; mode < 3; mode++)
        {
            await PrepareCompactLethalityAsync(combat, player, mode);
            string first = mode == 1 ? "UNLEASH" : "HANG", second = mode == 1 ? "HANG" : "UNLEASH";
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactLethality", "compact-lethality",
                [("LETHALITY", mode == 2 ? 0 : 1), (first, 0), (second, 0), ("GRAVEBLAST", 0), ("CAPTURE_SPIRIT", 0)],
                (lane, before, step) =>
                {
                    if (step >= 5)
                    {
                        if (lane.AttackCardStarts != 0) throw new InvalidOperationException("Lethality history survived the side-turn window.");
                        return;
                    }
                    int attacks = step is >= 1 and <= 3 ? 1 : 0;
                    if (lane.AttackCardStarts != before.AttackCardStarts + attacks)
                        throw new InvalidOperationException("Only attack-card starts may consume the first-card condition.");
                    if (step is < 1 or > 3) return;
                    bool pet = (step == 1 ? first : step == 2 ? second : "GRAVEBLAST") == "UNLEASH";
                    bool hang = (step == 1 ? first : step == 2 ? second : "GRAVEBLAST") == "HANG";
                    decimal amount = pet ? 6 + before.Creature(before.PetIndex).CurrentHp + 2 : (hang ? 10 : 4) + 11;
                    if (!pet) amount *= 0.75m;
                    amount *= 1.5m;
                    if (hang) amount *= 3;
                    if (before.AttackCardStarts == 0) amount *= 1m + (mode == 0 ? 75m : mode == 1 ? 125m : 100m) / 100m;
                    var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                    if (events.Where(item => item.Kind is ResumableDiscardProgram.EventKind.Damage or ResumableDiscardProgram.EventKind.DamageBlocked).Sum(item => item.Value) != (int)amount
                        || events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.Damage).Dealer != (pet ? before.PetIndex : 0))
                        throw new InvalidOperationException("Lethality first-card multiplier, Strength order or pet source differs.");
                }, observeNativeChoice: (_, lane) =>
                {
                    if (CountNativeAttackStarts(combat, player) != lane.AttackCardStarts)
                        throw new InvalidOperationException("Native selector did not observe the attack start before completion.");
                    return true;
                }, forceOpeningPower: true, verifyAttackStarts: true);
            // Resume in the actual later turn: the first newly drawn attack gets the bonus
            // again, including after a root originally captured with two attacks played.
            var card = player.PlayerCombatState!.Hand.Cards.First(card => card.Type == CardType.Attack);
            SetEnergy(player, 20);
            await AssertCompactPetCardRouteAsync(combat, player, mode + 3, "CompactLethalityReset", "compact-lethality-reset",
                [(card.Id.Entry, card.CurrentUpgradeLevel)], (lane, before, step) =>
                {
                    if (step == 0 && (before.AttackCardStarts != 0 || lane.AttackCardStarts != 1))
                        throw new InvalidOperationException("Fresh-turn attack did not restart the native window.");
                }, rounds: 1, requirePending: false, verifyAttackStarts: true);
            _completedChecks.Add($"CompactLethality:Mode{mode}:FirstCardOwnerAndPet:HangWeakVulnerable:RootHistory:NativePendingCount:ThreeRounds:ReactivatedAfterReset");
        }
    }
}
