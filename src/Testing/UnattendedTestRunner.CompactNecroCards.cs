using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task PrepareCompactNecroCardsAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, mode == 1 ? 2 : 0, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "CAPTURE_SPIRIT", "GRAVEBLAST", "DEFILE", "WISP" })
            for (int upgrade = 0; upgrade < 2; upgrade++)
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand", UpgradeLevels = upgrade });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "ASCENDERS_BANE", Pile = "Hand" });
        if (mode == 0)
            foreach (string id in new[] { "DEFEND_NECROBINDER", "DEFEND_NECROBINDER", "WISP" })
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Discard" });
        else await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "WISP", Pile = "Hand", UpgradeLevels = 1 });
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private async Task AssertCompactNecroCardsAsync(CombatState combat, Player player)
    {
        for (int mode = 0; mode < 2; mode++)
        {
            await PrepareCompactNecroCardsAsync(combat, player, mode);
            int nativeRetrievals = 0;
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactNecroCards", "compact-necro-cards",
                [("GRAVEBLAST", 0), ("CAPTURE_SPIRIT", 0), ("GRAVEBLAST", 1), ("CAPTURE_SPIRIT", 1), ("WISP", 0), ("DEFILE", 1)],
                (lane, before, step) =>
                {
                    if (step is 1 or 3)
                    {
                        int amount = step == 1 ? 3 : 4;
                        var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                        var damage = events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.Damage);
                        if (damage.Value != amount || damage.Dealer != 0
                            || (damage.Flags & (int)(ResumableDiscardProgram.DamageTraits.Unpowered | ResumableDiscardProgram.DamageTraits.Unblockable))
                                != (int)(ResumableDiscardProgram.DamageTraits.Unpowered | ResumableDiscardProgram.DamageTraits.Unblockable)
                            || events.Any(item => item.Kind == ResumableDiscardProgram.EventKind.AttackFinish)
                            || events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.Generated) != amount
                            || lane.Creature(1).Block != before.Creature(1).Block
                            || lane.ShuffleRng.Counter != before.ShuffleRng.Counter + amount)
                            throw new InvalidOperationException("CaptureSpirit changed direct HP loss or random generation semantics.");
                    }
                    if (step == 4 && lane.Energy != before.Energy + 1)
                        throw new InvalidOperationException("Wisp failed to gain its captured energy.");
                    // Root hand order is fixed above. Both untouched ethereal instances
                    // must leave hand before the first next-turn draw and choices.
                    if (step == 6 && (!lane.Cards(ResumableDiscardProgram.Pile.Exhaust).Contains(4)
                        || !lane.Cards(ResumableDiscardProgram.Pile.Exhaust).Contains(8)))
                        throw new InvalidOperationException("Defile or AscendersBane missed the native hand-end exhaustion.");
                }, observeNativeChoice: (action, values) =>
                {
                    if (action.CardId != "GRAVEBLAST") return false;
                    var enemy = combat.Enemies.Single();
                    if (new CreatureVitals(enemy.CurrentHp, enemy.MaxHp, enemy.Block) != values.Creature(1))
                        throw new InvalidOperationException("Native retrieval did not observe the completed attack.");
                    nativeRetrievals++;
                    return true;
                });
            if (mode == 0 && nativeRetrievals == 0) throw new InvalidOperationException("Native discard retrieval selector was not reached.");
            _completedChecks.Add($"CompactNecroCards:Mode{mode}:DirectHpLoss:RandomSouls:EmptyAndNonemptyDiscard:AttackBeforeRetrieve:WispEnergy:EtherealHandEnd");
        }
    }

    private async Task AssertCompactNecroTerminalAsync(CombatState combat, Player player)
    {
        bool capture = _request.ScenarioId == "COMPACT-CAPTURE-SPIRIT-TERMINAL";
        string id = capture ? "CAPTURE_SPIRIT" : "GRAVEBLAST";
        await PrepareCompactOstyAsync(combat, player, 0, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        for (int index = 0; index < 2; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Discard" });
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), capture ? 3 : 1);
        await SetBlockAsync(combat.Enemies.Single(), capture ? 30 : 0);
        SetEnergy(player, 3);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        await AssertCompactPetCardRouteAsync(combat, player, 0, "CompactNecroTerminal", "compact-necro-terminal", [(id, 0)],
            (lane, before, _) =>
            {
                int generated = capture ? 3 : 0;
                if (!lane.Terminal || lane.CardCount != before.CardCount + generated
                    || lane.Count(ResumableDiscardProgram.Pile.Unplaced) != generated || lane.ShuffleRng != before.ShuffleRng)
                    throw new InvalidOperationException("Terminal generation lost unplaced identities, consumed RNG or failed to commit victory.");
            }, rounds: 0, requirePending: false);
        _completedChecks.Add($"CompactNecroTerminal:{id}:LastEnemyDeath:{(capture ? "UnplacedGenerationHistory" : "NoChoiceAfterDeath")}:NoRandomConsumption:FullStateKeysContinuation");
    }
}
