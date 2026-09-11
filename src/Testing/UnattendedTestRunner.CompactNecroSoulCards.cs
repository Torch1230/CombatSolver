using System.Text.Json;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Extensions;
using CombatSolver.Engine.InCombat.Simulation.Compact;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Runs;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Bury, Reap, Parse, Poke, Reanimate, PullAggro, GraveWarden and Reave are the next
    // Necrobinder cards whose complete native OnPlay is representable by the compact
    // instruction domain. Reap's Retain, Parse's Ethereal and the random draw-pile Soul
    // insertion of GraveWarden/Reave are the non-OnPlay parts of that closure; the
    // upgraded Reave keeps its plain Soul variant for the native CardCmd.Upgrade ending gate.
    private static readonly string[] CompactNecroSoulCardIds =
        ["BURY", "REAP", "PARSE", "POKE", "PULL_AGGRO", "REANIMATE", "GRAVE_WARDEN", "REAVE"];

    private async Task PrepareCompactNecroSoulCardsAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, mode, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        foreach (string id in CompactNecroSoulCardIds)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand", UpgradeLevels = mode });
        // Parse's draw, both random Soul insertion positions and the next turn's hand draw
        // need a captured draw pile; this fixture never reshuffles.
        for (int index = 0; index < 8; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Draw" });
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private async Task AssertCompactNecroSoulCardsAsync(CombatState combat, Player player)
    {
        object compilation = AssertCompactNecroSoulCardCompilation(combat, player, out int admitted, out int unsupported,
            out int admittedWithTemplates, out int unsupportedWithTemplates, out string firstUnsupported);
        List<object> evidence = [];
        for (int mode = 0; mode < 2; mode++)
        {
            await PrepareCompactNecroSoulCardsAsync(combat, player, mode);
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactNecroSoulCards", "compact-necro-soul-cards",
                [(CompactNecroSoulCardIds[0], mode), (CompactNecroSoulCardIds[1], mode), (CompactNecroSoulCardIds[2], mode),
                    (CompactNecroSoulCardIds[3], mode), (CompactNecroSoulCardIds[4], mode), (CompactNecroSoulCardIds[5], mode),
                    (CompactNecroSoulCardIds[6], mode), (CompactNecroSoulCardIds[7], mode)],
                (lane, before, step) => AssertNecroSoulStep(lane, before, step, mode), verifyAttackStarts: true);
            _completedChecks.Add($"CompactNecroSoulCards:Mode{mode}:EightCompiledCards:PlainAndUpgraded:KeywordAndResultPiles:"
                + "PetDealerAttack:SummonBeforeBlock:SoulRandomDrawInsertion:AttackStarts:Rounds");
            evidence.Add(new { mode });
        }
        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-necro-soul-cards.json"),
                JsonSerializer.Serialize(new { character = player.Character.Id.Entry, encounter = combat.Encounter?.Id.Entry,
                    seed = _request.Seed, compilation,
                    census = new { admitted, unsupported, firstUnsupported, admittedWithTemplates, unsupportedWithTemplates },
                    modes = evidence }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // Every branch of one route step is checked against the previous compact state and the
    // compiled definition of the played card, so the assertions stay tied to the decompiled
    // native commands.
    private static void AssertNecroSoulStep(ResumableDiscardProgram lane, ResumableDiscardProgram before, int step, int mode)
    {
        var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
        const int ownerStrength = 11, petStrength = 2;
        int pet = lane.PetIndex;
        switch (step)
        {
            case 0:
                // Bury: single-target attack from the player, 52/63 printed plus owner Strength.
                if (before.Creature(1).CurrentHp - lane.Creature(1).CurrentHp != 52 + 11 * mode + ownerStrength - 3
                    || lane.Creature(0).CurrentHp != before.Creature(0).CurrentHp
                    || lane.Creature(pet).CurrentHp != before.Creature(pet).CurrentHp
                    || events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.AttackFinish && item.Dealer == 0) != 1
                    || lane.Count(ResumableDiscardProgram.Pile.Hand) != before.Count(ResumableDiscardProgram.Pile.Hand) - 1
                    || lane.Count(ResumableDiscardProgram.Pile.Discard) != before.Count(ResumableDiscardProgram.Pile.Discard) + 1)
                    throw new InvalidOperationException("Bury did not resolve one player attack before its result pile move.");
                break;
            case 1:
                // Reap: the same attack shape with its canonical Retain keyword on the played instance.
                int reapCard = events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.Start).Card;
                ResumableDiscardProgram.Card reap = lane.Definition(reapCard);
                if (before.Creature(1).CurrentHp - lane.Creature(1).CurrentHp != 27 + 6 * mode + ownerStrength
                    || events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.AttackFinish && item.Dealer == 0) != 1
                    || !reap.Retain || reap.Cost != 3 || reap.ResultPile != ResumableDiscardProgram.Pile.Discard
                    || reap.Effects[0] is not { Kind: CardInstructionKind.AttackTarget, Amount: var reapDamage }
                    || reapDamage != 27 + 6 * mode || !lane.Cards(ResumableDiscardProgram.Pile.Discard).Contains(reapCard))
                    throw new InvalidOperationException("Reap lost its attack, Retain keyword or result location.");
                break;
            case 2:
                // Parse: one owner draw of the printed count, Ethereal still marked for hand end.
                int parseCard = events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.Start).Card;
                ResumableDiscardProgram.Card parse = lane.Definition(parseCard);
                int draw = 3 + mode;
                if (lane.Count(ResumableDiscardProgram.Pile.Hand) != before.Count(ResumableDiscardProgram.Pile.Hand) - 1 + draw
                    || lane.Count(ResumableDiscardProgram.Pile.Draw) != before.Count(ResumableDiscardProgram.Pile.Draw) - draw
                    || !parse.Ethereal || parse.Cost != 1
                    || parse.Effects[0] is not { Kind: CardInstructionKind.Draw, Amount: var parseDraw } || parseDraw != draw)
                    throw new InvalidOperationException("Parse did not draw its exact count or lost its Ethereal keyword:"
                        + $" hand={before.Count(ResumableDiscardProgram.Pile.Hand)}/{lane.Count(ResumableDiscardProgram.Pile.Hand)}"
                        + $" draw={before.Count(ResumableDiscardProgram.Pile.Draw)}/{lane.Count(ResumableDiscardProgram.Pile.Draw)}"
                        + $" expected={draw} ethereal={parse.Ethereal}.");
                break;
            case 3:
                // Poke: the captured pet is the real dealer, so its Strength applies instead of the owner's.
                var poke = events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.Damage);
                if (poke.Dealer != pet || before.Creature(1).CurrentHp - lane.Creature(1).CurrentHp != 6 + 3 * mode + petStrength
                    || lane.Creature(pet).CurrentHp != before.Creature(pet).CurrentHp)
                    throw new InvalidOperationException($"Poke did not attack as the pet: dealer={poke.Dealer}/{pet}.");
                break;
            case 4:
                // PullAggro: the summon command finishes before the owner block command.
                int summon = 4 + mode, block = 7 + 2 * mode;
                int summonEvent = Array.FindIndex(events, item => item.Kind == ResumableDiscardProgram.EventKind.SummonPet);
                int blockEvent = Array.FindIndex(events, item => item.Kind == ResumableDiscardProgram.EventKind.Block);
                if (lane.Creature(pet).MaxHp - before.Creature(pet).MaxHp != summon
                    || lane.Creature(pet).CurrentHp - before.Creature(pet).CurrentHp != summon
                    || lane.Creature(0).Block - before.Creature(0).Block != block
                    || summonEvent < 0 || blockEvent < 0 || summonEvent > blockEvent)
                    throw new InvalidOperationException("PullAggro did not summon before gaining block.");
                break;
            case 5:
                // Reanimate: summon and the canonical Exhaust result pile.
                int reanimate = 20 + 5 * mode;
                if (lane.Creature(pet).MaxHp - before.Creature(pet).MaxHp != reanimate
                    || lane.Creature(pet).CurrentHp - before.Creature(pet).CurrentHp != reanimate
                    || lane.Count(ResumableDiscardProgram.Pile.Exhaust) != before.Count(ResumableDiscardProgram.Pile.Exhaust) + 1
                    || !lane.Cards(ResumableDiscardProgram.Pile.Exhaust).Any(card => lane.Definition(card).Effects.Count == 1
                        && lane.Definition(card).Effects[0] is { Kind: CardInstructionKind.SummonPet, Amount: var amount } && amount == reanimate))
                    throw new InvalidOperationException("Reanimate did not summon before exhausting itself.");
                break;
            case 6:
                // GraveWarden: block, then one plain Soul at a random draw-pile position.
                if (lane.Creature(0).Block - before.Creature(0).Block != 8 + 3 * mode
                    || !IsSoulInsertion(events, lane, expectedUpgraded: false, count: 1)
                    || lane.ShuffleRng.Counter != before.ShuffleRng.Counter + 1)
                    throw new InvalidOperationException("GraveWarden did not insert one plain Soul with the shuffle stream.");
                break;
            case 7:
                // Reave: the attack resolves first, then the Soul variant of the played card.
                if (before.Creature(1).CurrentHp - lane.Creature(1).CurrentHp != 10 + 3 * mode + ownerStrength
                    || !IsSoulInsertion(events, lane, expectedUpgraded: mode == 1, count: 1)
                    || lane.ShuffleRng.Counter != before.ShuffleRng.Counter + 1)
                    throw new InvalidOperationException("Reave did not attack before inserting its Soul variant.");
                break;
            default:
                // A completed round leaves the play area empty. The retained and ethereal
                // copies that stay in hand are covered by the dedicated hand-end scenario;
                // here the played Reap can legitimately return through a shuffle.
                if (lane.Count(ResumableDiscardProgram.Pile.Play) != 0)
                    throw new InvalidOperationException($"A completed round kept cards in the play area: step={step}.");
                break;
        }
    }

    // A generated Soul is only distinguishable from the other admitted cards by its compiled
    // draw count, which is exactly the native upgrade difference (Cards 2 -> 3).
    private static bool IsSoulInsertion(ResumableDiscardProgram.Event[] events, ResumableDiscardProgram lane,
        bool expectedUpgraded, int count)
    {
        int[] generated = events.Where(item => item.Kind == ResumableDiscardProgram.EventKind.Generated
                && item.Flags == (int)ResumableDiscardProgram.Pile.Draw && item.Target == 0)
            .Select(item => item.Card).ToArray();
        return generated.Length == count && generated.All(card => lane.Definition(card).Effects.Count == 1
            && lane.Definition(card).Effects[0] is { Kind: CardInstructionKind.Draw, Amount: var amount }
            && amount == (expectedUpgraded ? 3 : 2));
    }

    private static bool IsSoulVariant(ResumableDiscardProgram lane, int card, int draw)
        => lane.Definition(card).Effects.Count == 1
            && lane.Definition(card).Effects[0] is { Kind: CardInstructionKind.Draw, Amount: var amount } && amount == draw;

    // Retain and Ethereal are the two non-OnPlay keywords of this batch. This fixture keeps an
    // unplayed Reap and Parse in hand across a real player end turn, so the flush must keep the
    // retained copy while exhausting the ethereal one.
    private async Task AssertCompactNecroHandEndAsync(CombatState combat, Player player)
    {
        // No turn-start relic: Tools of the Trade would open a discard choice that may
        // legitimately discard the retained copy before this fixture can observe it.
        await PrepareCompactOstyAsync(combat, player, 0, withTurnRelic: false);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "REAP", "PARSE" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        for (int index = 0; index < 8; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Draw" });
        SetEnergy(player, 3);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        await AssertCompactPetCardRouteAsync(combat, player, 0, "CompactNecroHandEnd", "compact-necro-hand-end", [],
            (lane, before, _) =>
            {
                int[] retained = lane.Cards(ResumableDiscardProgram.Pile.Hand).Where(card => lane.IsRetained(card)).ToArray();
                if (retained.Length != 1
                    || lane.Definition(retained[0]).Effects[0] is not { Kind: CardInstructionKind.AttackTarget, Amount: 27 }
                    || !lane.Cards(ResumableDiscardProgram.Pile.Exhaust).Any(card => lane.Definition(card).Ethereal
                        && lane.Definition(card).Effects[0] is { Kind: CardInstructionKind.Draw, Amount: 3 })
                    || lane.Count(ResumableDiscardProgram.Pile.Play) != 0)
                    throw new InvalidOperationException("The player flush lost Reap's Retain or Parse's Ethereal exhaustion:"
                        + string.Join(' ', lane.Cards(ResumableDiscardProgram.Pile.Hand).Select(card => $"{card}:{lane.IsRetained(card)}"))
                        + " / " + string.Join(' ', lane.Cards(ResumableDiscardProgram.Pile.Exhaust).Select(card => $"{card}:{lane.Definition(card).Ethereal}"))
                        + $" / play={lane.Count(ResumableDiscardProgram.Pile.Play)}");
            }, rounds: 1, requirePending: false);
        _completedChecks.Add("CompactNecroHandEnd:RetainedReapKeptInHand:EtherealParseExhaustedAtFlush:FullNativeComparison");
    }

    // An upgraded Reave whose own attack kills the last primary enemy records the plain Soul
    // variant: the native CardCmd.Upgrade returns before upgrading while CombatManager.IsEnding
    // is true, and the refused combat-pile insertion leaves the identity in history only.
    private async Task AssertCompactReaveTerminalAsync(CombatState combat, Player player)
    {
        await PrepareCompactOstyAsync(combat, player, 0, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "REAVE", Pile = "Hand", UpgradeLevels = 1 });
        for (int index = 0; index < 2; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Discard" });
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 1);
        SetEnergy(player, 3);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        await AssertCompactPetCardRouteAsync(combat, player, 1, "CompactReaveTerminal", "compact-reave-terminal", [("REAVE", 1)],
            (lane, before, _) =>
            {
                var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                int[] unplaced = events.Where(item => item.Kind == ResumableDiscardProgram.EventKind.Generated
                        && item.Flags == (int)ResumableDiscardProgram.Pile.Unplaced && item.Target == 0)
                    .Select(item => item.Card).ToArray();
                if (!lane.Terminal || lane.DefeatTerminal || lane.CreaturePresent(1)
                    || unplaced.Length != 1 || !IsSoulVariant(lane, unplaced[0], 2)
                    || lane.Count(ResumableDiscardProgram.Pile.Unplaced) != 1 || lane.ShuffleRng != before.ShuffleRng)
                    throw new InvalidOperationException("Upgraded Reave upgraded or inserted a Soul while the combat was ending:"
                        + $" terminal={lane.Terminal}/{lane.DefeatTerminal} present={lane.CreaturePresent(1)}"
                        + $" unplaced=[{string.Join(',', unplaced)}] draws=[{string.Join(',', unplaced.Select(card => lane.Definition(card).Effects[0].Amount))}]"
                        + $" count={lane.Count(ResumableDiscardProgram.Pile.Unplaced)} rng={lane.ShuffleRng}/{before.ShuffleRng}");
            }, rounds: 0, requirePending: false);
        _completedChecks.Add("CompactReaveTerminal:LastEnemyDeath:UnplacedPlainSoulFromNativeUpgradeGate:NoRandomConsumption:FullNativeComparison");
    }

    // Poke is the second admitted PetAttackTarget after Snap. Its native OnPlay lives inside
    // Osty.CheckMissingWithAnim, so this fixture separates the three captured states: absent
    // (the same root must stay rejected), dead (legal play, skipped attack) and revived (the
    // real pet is the dealer again). Each state is driven by the shared pet route helper, so
    // native, compact lane and materialized projection are compared over the same actions.
    private async Task AssertCompactPokePetStateAsync(CombatState combat, Player player)
    {
        if (player.Osty != null) throw new InvalidOperationException("Poke pet-state fixture requires an initially absent pet.");
        var enemy = combat.Enemies.Single();
        foreach (var relic in player.Relics.ToArray()) await RelicCmd.Remove(relic);
        foreach (var power in combat.Creatures.SelectMany(creature => creature.Powers).ToArray()) await PowerCmd.Remove(power);
        ClearRunDeck((RunState)combat.RunState, player);
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "POKE", Pile = "Hand" });
        await CreatureCmd.SetMaxHp(player.Creature, 300); await CreatureCmd.SetCurrentHp(player.Creature, 300);
        await CreatureCmd.SetMaxHp(enemy, 300); await CreatureCmd.SetCurrentHp(enemy, 300);
        await SetBlockAsync(enemy, 3);
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

        // Absent pet: production admission forbids first creation, so the same captured root
        // must be rejected and no pet may be simulated into existence. The native no-op is
        // observed on its own fixture.
        var absentRoot = CombatRootSnapshot.Capture(combat).ForkSimulator();
        string absentReason;
        using (SimulationNotificationIsolation.Enter())
            if (CompactCombatRoot.TryCreate(absentRoot, player, out _, out absentReason)
                || !absentReason.Contains("captured pet identity", StringComparison.Ordinal))
                throw new InvalidOperationException($"An absent-pet Poke root was not rejected by pet admission: {absentReason}");
        int absentAttacks = CountNativeCreatureAttacks(), absentDamages = CountNativeDamageReceived();
        int absentEnemyHp = enemy.CurrentHp, absentEnemyBlock = enemy.Block, absentEnergy = player.PlayerCombatState!.Energy;
        if (!player.PlayerCombatState.Hand.Cards.Single().TryManualPlay(enemy))
            throw new InvalidOperationException("Native absent-pet Poke was rejected.");
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        if (player.Osty != null || enemy.CurrentHp != absentEnemyHp || enemy.Block != absentEnemyBlock
            || CountNativeCreatureAttacks() != absentAttacks || CountNativeDamageReceived() != absentDamages
            || player.PlayerCombatState.Hand.Cards.Count != 0 || player.PlayerCombatState.Energy != absentEnergy)
            throw new InvalidOperationException("Native absent-pet Poke was not a legal no-op:"
                + $" pet={player.Osty != null} hp={enemy.CurrentHp}/{absentEnemyHp} block={enemy.Block}/{absentEnemyBlock}"
                + $" attacks={CountNativeCreatureAttacks()}/{absentAttacks} damages={CountNativeDamageReceived()}/{absentDamages}"
                + $" hand={player.PlayerCombatState.Hand.Cards.Count} energy={player.PlayerCombatState.Energy}/{absentEnergy}");
        var absent = new { rejected = true, reason = absentReason, petCreated = player.Osty != null,
            enemyHp = new[] { absentEnemyHp, enemy.CurrentHp }, enemyBlock = new[] { absentEnemyBlock, enemy.Block },
            creatureAttacked = new[] { absentAttacks, CountNativeCreatureAttacks() },
            damageReceived = new[] { absentDamages, CountNativeDamageReceived() } };

        // Dead pet: mode 2 kills the summoned pet before the root is captured, so the Poke
        // play is legal and the native command is skipped whole.
        await PrepareCompactOstyAsync(combat, player, 2, withTurnRelic: false);
        var osty = player.Osty ?? throw new InvalidOperationException("Dead-pet fixture lost the captured identity.");
        if (osty.CurrentHp != 0) throw new InvalidOperationException($"Dead-pet fixture left the pet alive: {osty.CurrentHp}.");
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "POKE", Pile = "Hand" });
        for (int index = 0; index < 4; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Draw" });
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        int deadAttacks = CountNativeCreatureAttacks(), deadDamages = CountNativeDamageReceived(), deadEnemyHp = enemy.CurrentHp;
        await AssertCompactPetCardRouteAsync(combat, player, 0, "CompactPokePetStateDead", "compact-poke-pet-state-dead",
            [("POKE", 0)], (lane, before, _) =>
            {
                var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                int card = events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.Start).Card;
                if (events.Any(item => item.Kind is ResumableDiscardProgram.EventKind.Damage or ResumableDiscardProgram.EventKind.AttackFinish)
                    || lane.Creature(1).CurrentHp != before.Creature(1).CurrentHp || lane.Creature(lane.PetIndex).CurrentHp != 0
                    || lane.Energy != before.Energy
                    || lane.Definition(card).Effects[0] is not { Kind: CardInstructionKind.PetAttackTarget, Amount: 6 }
                    || !lane.Cards(ResumableDiscardProgram.Pile.Discard).Contains(card))
                    throw new InvalidOperationException("A dead captured pet Poke did not skip the whole attack command.");
            }, rounds: 0, requirePending: false);
        if (CountNativeCreatureAttacks() != deadAttacks || CountNativeDamageReceived() != deadDamages
            || enemy.CurrentHp != deadEnemyHp || player.Osty != osty || osty.CurrentHp != 0)
            throw new InvalidOperationException("Native dead-pet Poke changed damage, attack history or the retained dead pet:"
                + $" attacks={CountNativeCreatureAttacks()}/{deadAttacks} damages={CountNativeDamageReceived()}/{deadDamages}"
                + $" hp={enemy.CurrentHp}/{deadEnemyHp} pet={osty.CurrentHp}");
        var dead = new { petHp = osty.CurrentHp, enemyHp = new[] { deadEnemyHp, enemy.CurrentHp },
            creatureAttacked = new[] { deadAttacks, CountNativeCreatureAttacks() },
            damageReceived = new[] { deadDamages, CountNativeDamageReceived() } };

        // Revived pet: Reanimate revives the same captured identity, so the following Poke
        // must resolve as a real pet attack again.
        await ClearPlayerPilesAsync(player);
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "REANIMATE", Pile = "Hand" });
        await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "POKE", Pile = "Hand" });
        for (int index = 0; index < 4; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Draw" });
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
        int reviveAttacks = CountNativeCreatureAttacks(), reviveEnemyHp = enemy.CurrentHp;
        await AssertCompactPetCardRouteAsync(combat, player, 1, "CompactPokePetStateRevived", "compact-poke-pet-state-revived",
            [("REANIMATE", 0), ("POKE", 0)], (lane, before, step) =>
            {
                var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                int pet = lane.PetIndex;
                if (step == 0)
                {
                    if (events.Any(item => item.Kind == ResumableDiscardProgram.EventKind.Damage)
                        || lane.Creature(pet).CurrentHp != 20 || lane.Creature(pet).MaxHp != 20 || lane.CreatureDeathCompleted(pet))
                        throw new InvalidOperationException("Reanimate did not revive the captured dead pet.");
                }
                else
                {
                    var poke = events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.Damage);
                    var finish = events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.AttackFinish);
                    if (poke.Dealer != pet || finish.Dealer != pet || lane.CreatureDeathCompleted(pet)
                        || before.Creature(1).CurrentHp - lane.Creature(1).CurrentHp <= 0)
                        throw new InvalidOperationException("The revived pet did not deal Poke's damage:"
                            + $" dealer={poke.Dealer}/{pet} finish={finish.Dealer} loss={before.Creature(1).CurrentHp - lane.Creature(1).CurrentHp}");
                }
            }, rounds: 0, requirePending: false);
        var attacked = CombatManager.Instance.History.Entries.OfType<CreatureAttackedEntry>().Skip(reviveAttacks).ToArray();
        if (attacked.Length != 1 || attacked[0].Actor != osty || osty.CurrentHp != 20 || osty.MaxHp != 20 || enemy.CurrentHp >= reviveEnemyHp)
            throw new InvalidOperationException("Native revived-pet Poke did not attack as the captured pet:"
                + $" entries={attacked.Length} actor={(attacked.Length == 1 ? attacked[0].Actor.CombatId : -1)}/{osty.CombatId}"
                + $" pet={osty.CurrentHp}/{osty.MaxHp} hp={enemy.CurrentHp}/{reviveEnemyHp}");
        var revived = new { petHp = osty.CurrentHp, petMaxHp = osty.MaxHp, dealer = attacked[0].Actor.CombatId,
            enemyHp = new[] { reviveEnemyHp, enemy.CurrentHp },
            attackResults = attacked[0].DamageResults.Select(result => $"{result.Receiver.CombatId}:{result.BlockedDamage}:{result.UnblockedDamage}:{result.OverkillDamage}:{result.WasTargetKilled}").ToArray() };

        if (!string.IsNullOrWhiteSpace(_request.EvidenceDirectory))
        {
            Directory.CreateDirectory(_request.EvidenceDirectory);
            File.WriteAllText(Path.Combine(_request.EvidenceDirectory, "compact-poke-pet-state.json"),
                JsonSerializer.Serialize(new { absent, dead, revived }, new JsonSerializerOptions { WriteIndented = true }));
        }
        _completedChecks.Add("CompactPokePetState:AbsentRootRejected:NativeAbsentNoOp:DeadPetSkippedAttack:ReviveThenPetDealer:FullNativeComparison");
    }

    private static int CountNativeCreatureAttacks()
        => CombatManager.Instance.History.Entries.OfType<CreatureAttackedEntry>().Count();

    private static int CountNativeDamageReceived()
        => CombatManager.Instance.History.Entries.OfType<DamageReceivedEntry>().Count();

    // Production compilation is the admitted frontier. Every instruction below is one
    // decompiled native command of these eight cards, and every unrepresented instance state
    // stays rejected instead of being narrowed into the admitted shape.
    private static object AssertCompactNecroSoulCardCompilation(CombatState combat, Player player, out int admitted,
        out int unsupported, out int admittedWithTemplates, out int unsupportedWithTemplates, out string firstUnsupported)
    {
        const int plainSoul = 8, upgradedSoul = 9;
        (string Id, int Cost, CardCategory Category, ResumableDiscardProgram.Pile Pile, bool Ethereal, bool Retain,
            CardInstruction[] Base, CardInstruction[] Upgraded)[] cases =
        [
            ("BURY", 4, CardCategory.Attack, ResumableDiscardProgram.Pile.Discard, false, false,
                [new(CardInstructionKind.AttackTarget, 52)], [new(CardInstructionKind.AttackTarget, 63)]),
            ("REAP", 3, CardCategory.Attack, ResumableDiscardProgram.Pile.Discard, false, true,
                [new(CardInstructionKind.AttackTarget, 27)], [new(CardInstructionKind.AttackTarget, 33)]),
            ("PARSE", 1, CardCategory.Skill, ResumableDiscardProgram.Pile.Discard, true, false,
                [new(CardInstructionKind.Draw, 3)], [new(CardInstructionKind.Draw, 4)]),
            ("POKE", 0, CardCategory.Attack, ResumableDiscardProgram.Pile.Discard, false, false,
                [new(CardInstructionKind.PetAttackTarget, 6)], [new(CardInstructionKind.PetAttackTarget, 9)]),
            ("REANIMATE", 3, CardCategory.Skill, ResumableDiscardProgram.Pile.Exhaust, false, false,
                [new(CardInstructionKind.SummonPet, 20)], [new(CardInstructionKind.SummonPet, 25)]),
            ("PULL_AGGRO", 2, CardCategory.Skill, ResumableDiscardProgram.Pile.Discard, false, false,
                [new(CardInstructionKind.SummonPet, 4), new(CardInstructionKind.GainBlock, 7)],
                [new(CardInstructionKind.SummonPet, 5), new(CardInstructionKind.GainBlock, 9)]),
            ("GRAVE_WARDEN", 1, CardCategory.Skill, ResumableDiscardProgram.Pile.Discard, false, false,
                [new(CardInstructionKind.GainBlock, 8), new(CardInstructionKind.GenerateCards, 1,
                    CardTemplate: plainSoul, Placement: CardGenerationPlacement.RandomDraw)],
                [new(CardInstructionKind.GainBlock, 11), new(CardInstructionKind.GenerateCards, 1,
                    CardTemplate: plainSoul, Placement: CardGenerationPlacement.RandomDraw)]),
            ("REAVE", 1, CardCategory.Attack, ResumableDiscardProgram.Pile.Discard, false, false,
                [new(CardInstructionKind.AttackTarget, 10), new(CardInstructionKind.GenerateCards, 1,
                    CardTemplate: plainSoul, Placement: CardGenerationPlacement.RandomDraw)],
                [new(CardInstructionKind.AttackTarget, 13), new(CardInstructionKind.GenerateCards, 1,
                    CardTemplate: upgradedSoul, Placement: CardGenerationPlacement.RandomDraw, EndingCardTemplate: plainSoul)]),
        ];
        List<object> results = [];
        foreach (var item in cases)
        {
            CardModel canonical = CompactNecroSoulCanonical(item.Id);
            ResumableDiscardProgram.Card program = CompactCardProgramCompiler.Compile(canonical, includeAttacks: true,
                soulTemplate: plainSoul, upgradedSoulTemplate: upgradedSoul);
            if (program.Cost != item.Cost || program.Category != item.Category || program.ResultPile != item.Pile
                || program.Ethereal != item.Ethereal || program.Retain != item.Retain || program.Sly || program.CostsX
                || program.Unplayable || program.DrawCost != null || program.Effects.Count != item.Base.Length
                || !Enumerable.Range(0, item.Base.Length).All(index => program.Effects[index] == item.Base[index]))
                throw new InvalidOperationException($"Compact Necrobinder Soul card program differs from the native OnPlay: {item.Id}.");
            CardModel upgraded = PredictionUtils.CreateCard(canonical, player);
            PredictionUtils.UpgradeCard(upgraded);
            ResumableDiscardProgram.Card upgradedProgram = CompactCardProgramCompiler.Compile(upgraded, includeAttacks: true,
                soulTemplate: plainSoul, upgradedSoulTemplate: upgradedSoul);
            if (!upgraded.IsUpgraded || upgradedProgram.Effects.Count != item.Upgraded.Length
                || !Enumerable.Range(0, item.Upgraded.Length).All(index => upgradedProgram.Effects[index] == item.Upgraded[index]))
                throw new InvalidOperationException($"Compact Necrobinder Soul card upgrade changed its admitted commands: {item.Id}.");
            // Cards that never generate must not embed any captured template index.
            if (item.Id is not ("GRAVE_WARDEN" or "REAVE"))
            {
                ResumableDiscardProgram.Card other = CompactCardProgramCompiler.Compile(canonical, includeAttacks: true,
                    shivTemplate: 0, inkyShivTemplate: 1, soulTemplate: plainSoul + 4, upgradedSoulTemplate: upgradedSoul + 4);
                if (other.Effects.Count != program.Effects.Count
                    || !Enumerable.Range(0, program.Effects.Count).All(index => other.Effects[index] == program.Effects[index]))
                    throw new InvalidOperationException($"Compact Necrobinder Soul card {item.Id} depends on unrelated generation templates.");
            }
            CardModel unrepresented = PredictionUtils.CreateCard(canonical, player);
            unrepresented.AddKeyword(CardKeyword.Eternal);
            ExpectNecroSoulCompileRejected(unrepresented, item.Id);
            ExpectNecroSoulCompileRejected(canonical, item.Id, includeAttacks: false);
            results.Add(new { item.Id, cost = program.Cost, category = program.Category.ToString(), pile = program.ResultPile.ToString(),
                ethereal = program.Ethereal, retain = program.Retain,
                instructions = item.Base.Select(instruction => instruction.ToString()).ToArray(),
                upgradedInstructions = item.Upgraded.Select(instruction => instruction.ToString()).ToArray() });
        }
        // The two Soul generators are rejected without their captured generation closure.
        ExpectNecroSoulCompileRejected(CompactNecroSoulCanonical("GRAVE_WARDEN"), "GRAVE_WARDEN", soulTemplate: -1, upgradedSoulTemplate: -1);
        ExpectNecroSoulCompileRejected(CompactNecroSoulCanonical("REAVE"), "REAVE", soulTemplate: -1, upgradedSoulTemplate: -1);
        var census = CombatRootSnapshot.Capture(combat).ForkSimulator();
        if (!census.TryGetRootEligibleCharacterCardsForCombat(player, combat.RunState.CardMultiplayerConstraint, out var pool)
            || pool.Count != 78)
            throw new InvalidOperationException("Compact Necrobinder Soul card census requires the frozen 78-candidate character pool.");
        admitted = 0; unsupported = 0; admittedWithTemplates = 0; unsupportedWithTemplates = 0;
        firstUnsupported = ""; string firstUnsupportedWithTemplates = "";
        List<string> defaultAdmitted = [], templateAdmitted = [];
        foreach (CardModel candidate in pool)
        {
            int beforeDefault = admitted, beforeTemplates = admittedWithTemplates;
            TryCompile(candidate, -1, -1, ref admitted, ref unsupported, ref firstUnsupported);
            TryCompile(candidate, plainSoul, upgradedSoul, ref admittedWithTemplates, ref unsupportedWithTemplates,
                ref firstUnsupportedWithTemplates);
            if (admitted != beforeDefault) defaultAdmitted.Add(candidate.Id.Entry);
            if (admittedWithTemplates != beforeTemplates) templateAdmitted.Add(candidate.Id.Entry);
        }
        // Dirge and CaptureSpirit are the two pre-existing pool candidates whose admitted
        // program only exists with a captured generation template; GraveWarden and Reave join
        // them in this batch.
        string[] unlockedByTemplates = [.. templateAdmitted.Except(defaultAdmitted).Order()];
        int newCards = pool.Count(card => CompactNecroSoulCardIds.Contains(card.Id.Entry));
        if (newCards != CompactNecroSoulCardIds.Length || defaultAdmitted.Count != admitted || unsupported == 0
            || !unlockedByTemplates.SequenceEqual(new[] { "CAPTURE_SPIRIT", "DIRGE", "GRAVE_WARDEN", "REAVE" })
            || !CompactNecroSoulCardIds.All(templateAdmitted.Contains))
            throw new InvalidOperationException("Compact Necrobinder Soul card census lost the frozen pool, the eight candidates "
                + $"or the four template-dependent generators: new={newCards}, admitted={admitted}, withTemplates={admittedWithTemplates}, "
                + $"unlocked=[{string.Join(',', unlockedByTemplates)}].");
        return new { instructions = results, poolSize = pool.Count, newCards, admittedWithTemplates, firstUnsupportedWithTemplates };

        static void TryCompile(CardModel candidate, int soulTemplate, int upgradedSoulTemplate,
            ref int admitted, ref int unsupported, ref string firstUnsupported)
        {
            try
            {
                _ = CompactCardProgramCompiler.Compile(candidate, includeAttacks: true,
                    soulTemplate: soulTemplate, upgradedSoulTemplate: upgradedSoulTemplate);
                admitted++;
            }
            catch (NotSupportedException)
            {
                unsupported++;
                if (firstUnsupported.Length == 0) firstUnsupported = candidate.Id.Entry;
            }
        }

        static void ExpectNecroSoulCompileRejected(CardModel card, string id, bool includeAttacks = true,
            int soulTemplate = plainSoul, int upgradedSoulTemplate = upgradedSoul)
        {
            try
            {
                _ = CompactCardProgramCompiler.Compile(card, includeAttacks, soulTemplate: soulTemplate,
                    upgradedSoulTemplate: upgradedSoulTemplate);
            }
            catch (NotSupportedException) { return; }
            throw new InvalidOperationException($"Compact Necrobinder Soul card state {id} was not explicitly rejected.");
        }
    }

    private static CardModel CompactNecroSoulCanonical(string id) => id switch
    {
        "BURY" => CanonicalModels.Card<Bury>(),
        "REAP" => CanonicalModels.Card<Reap>(),
        "PARSE" => CanonicalModels.Card<Parse>(),
        "POKE" => CanonicalModels.Card<Poke>(),
        "REANIMATE" => CanonicalModels.Card<Reanimate>(),
        "PULL_AGGRO" => CanonicalModels.Card<PullAggro>(),
        "GRAVE_WARDEN" => CanonicalModels.Card<GraveWarden>(),
        _ => CanonicalModels.Card<Reave>()
    };
}
