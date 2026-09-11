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
    private async Task PrepareCompactKeywordsAsync(CombatState combat, Player player, int mode)
    {
        await PrepareCompactOstyAsync(combat, player, mode == 2 ? 2 : 1, withTurnRelic: true);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "SCULPTING_STRIKE", "SNAP" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand", UpgradeLevels = mode == 2 ? 1 : 0 });
        if (mode == 0)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "MALAISE", Pile = "Hand" });
        else
        {
            for (int index = 0; index < 3; index++)
                await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = "Hand" });
            CardCmd.ApplyKeyword(player.PlayerCombatState!.Hand.Cards[2], CardKeyword.Ethereal, CardKeyword.Retain);
        }
        for (int index = 0; index < 4; index++)
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = "DEFEND_NECROBINDER", Pile = index < 2 ? "Draw" : "Discard" });
        await PowerCmd.Apply<VeilpiercerPower>(new BlockingPlayerChoiceContext(), player.Creature, 2, player.Creature, null);
        SetEnergy(player, 20);
        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
    }

    private async Task AssertCompactKeywordsAsync(CombatState combat, Player player)
    {
        // Both top bits must survive an X rewrite, including a negative encoded long.
        foreach (int definition in new[] { 0, 1, int.MaxValue })
        foreach (int captured in new[] { 0, 1, 999_999_999 })
        foreach (CardKeywordFlags flags in new[] { CardKeywordFlags.None, CardKeywordFlags.Ethereal,
                     CardKeywordFlags.Retain, CardKeywordFlags.Ethereal | CardKeywordFlags.Retain })
        {
            var value = new CardInstanceValue(definition, captured, flags);
            if (CardInstanceValue.Decode(value.Data) != value
                || CardInstanceValue.Decode((value with { CapturedX = 999_999_999 - captured }).Data)
                    != value with { CapturedX = 999_999_999 - captured })
                throw new InvalidOperationException("Card instance fields overlap or truncate X/keyword values.");
        }
        for (int mode = 0; mode < 3; mode++)
        {
            await PrepareCompactKeywordsAsync(combat, player, mode);
            (string Id, int? Upgrade)[] steps = mode == 0
                ? [("SCULPTING_STRIKE", 0), ("SNAP", 0), ("MALAISE", 0)]
                : [("SNAP", mode == 2 ? 1 : 0), ("SCULPTING_STRIKE", mode == 2 ? 1 : 0)];
            int nativeChoices = 0;
            await AssertCompactPetCardRouteAsync(combat, player, mode, "CompactKeywords", "compact-keywords", steps,
                (lane, before, step) =>
                {
                    var events = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt).ToArray();
                    if (step < 2)
                    {
                        bool snap = steps[step].Id == "SNAP";
                        var added = events.Single(item => item.Kind == ResumableDiscardProgram.EventKind.KeywordAdded);
                        if ((CardKeywordFlags)added.Flags != (snap ? CardKeywordFlags.Retain : CardKeywordFlags.Ethereal)
                            || !(snap ? lane.IsRetained(added.Card) : lane.IsEthereal(added.Card)))
                            throw new InvalidOperationException("Selected instance did not receive its persistent keyword.");
                        int attacks = events.Count(item => item.Kind == ResumableDiscardProgram.EventKind.AttackFinish);
                        if (attacks != (snap && mode == 2 ? 0 : 1))
                            throw new InvalidOperationException("Missing Osty must skip only the attack, preserving the keyword choice.");
                    }
                    else if (mode == 0 && step == 2)
                    {
                        if (!lane.IsEthereal(2) || !lane.IsRetained(2) || lane.CapturedX(2) != before.Energy || lane.Energy != 0)
                            throw new InvalidOperationException("X payment lost either added keyword or its captured energy.");
                    }
                    if (step < steps.Length) return;
                    foreach (int card in before.Cards(ResumableDiscardProgram.Pile.Hand))
                    {
                        if (before.IsEthereal(card) && !events.Any(item => item.Kind == ResumableDiscardProgram.EventKind.ResultMoved
                            && item.Card == card && item.Value == (int)ResumableDiscardProgram.Pile.Exhaust))
                            throw new InvalidOperationException("Dynamic ethereal must resolve before hand flush, including retained cards.");
                        if (before.IsRetained(card) && !before.IsEthereal(card)
                            && events.Any(item => item.Kind == ResumableDiscardProgram.EventKind.ResultMoved && item.Card == card
                                && item.Value == (int)ResumableDiscardProgram.Pile.Discard))
                            throw new InvalidOperationException("Persistent retain was lost at the hand flush.");
                    }
                }, verifyEnergyCosts: true, chooseBranch: (action, step) =>
                {
                    if (step >= 2) return true;
                    string id = mode == 0 ? "MALAISE" : "DEFEND_NECROBINDER";
                    int occurrence = mode == 0 ? 0 : step == 0 ? 2 : 1;
                    return action.GetActionChoicesInExecutionOrder().SelectMany(choice => choice.Cards)
                        .Any(token => token.CardId == id && token.SourceOccurrence == occurrence);
                }, observeNativeChoice: (action, values) =>
                {
                    if (action.CardId is not ("SCULPTING_STRIKE" or "SNAP")) return false;
                    var enemy = combat.Enemies.Single();
                    if (new CreatureVitals(enemy.CurrentHp, enemy.MaxHp, enemy.Block) != values.Creature(1))
                        throw new InvalidOperationException("Native keyword choice did not follow its attack.");
                    foreach (var card in player.PlayerCombatState!.Hand.Cards.Where(card => card.Keywords.Contains(CardKeyword.Ethereal)
                                 && card.EnergyCost._base >= 0 && !card.EnergyCost.CostsX))
                        if (card.EnergyCost.GetWithModifiers(CostModifiers.All) != 0)
                            throw new InvalidOperationException("Native keyword choice lost free ethereal energy cost.");
                    nativeChoices++;
                    return true;
                });
            if (nativeChoices == 0) throw new InvalidOperationException("Native filtered hand choice was not observed.");
            _completedChecks.Add($"CompactKeywords:Mode{mode}:FilteredDuplicateInstances:PersistentKeywords:HandEnd:GlobalCosts:XBits:NativeChoice{nativeChoices}:TwoRounds");
        }
    }

    private async Task AssertCompactKeywordEmptyAsync(CombatState combat, Player player)
    {
        await PrepareCompactKeywordsAsync(combat, player, 0);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "SCULPTING_STRIKE", "SNAP" })
        {
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
            CardCmd.ApplyKeyword(player.PlayerCombatState!.Hand.Cards.Last(), CardKeyword.Ethereal, CardKeyword.Retain);
        }
        await AssertCompactPetCardRouteAsync(combat, player, 3, "CompactKeywordEmpty", "compact-keyword-empty",
            [("SCULPTING_STRIKE", 0), ("SNAP", 0)], (lane, before, _) =>
            {
                if (lane.Energy != before.Energy || Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount)
                    .Select(lane.EventAt).Any(item => item.Kind is ResumableDiscardProgram.EventKind.Select or ResumableDiscardProgram.EventKind.KeywordAdded))
                    throw new InvalidOperationException("Empty filtered hand must continue without a choice or keyword command.");
            }, rounds: 0, requirePending: false, verifyEnergyCosts: true);
    }

    private async Task AssertCompactKeywordGeneratedAsync(CombatState combat, Player player)
    {
        await PrepareCompactKeywordsAsync(combat, player, 0);
        await ClearPlayerPilesAsync(player);
        foreach (string id in new[] { "CAPTURE_SPIRIT", "SOUL", "SCULPTING_STRIKE", "SNAP" })
            await InjectCardAsync(combat, player, new UnattendedCardInjection { CardId = id, Pile = "Hand" });
        await AssertCompactPetCardRouteAsync(combat, player, 5, "CompactKeywordGenerated", "compact-keyword-generated",
            [("CAPTURE_SPIRIT", 0), ("SOUL", 0), ("SCULPTING_STRIKE", 0), ("SNAP", 0)], (lane, before, step) =>
            {
                if (step is 2 or 3)
                {
                    var added = Enumerable.Range(before.EventCount, lane.EventCount - before.EventCount).Select(lane.EventAt)
                        .Single(item => item.Kind == ResumableDiscardProgram.EventKind.KeywordAdded);
                    if (step == 3 && added.Card < 4 || !(step == 2 ? lane.IsEthereal(added.Card) : lane.IsRetained(added.Card)))
                        throw new InvalidOperationException("Generated instance lost its independent keyword state.");
                }
                if (step == 4 && !Enumerable.Range(4, before.CardCount - 4).Any(card => before.IsEthereal(card)
                    && before.IsRetained(card) && lane.Cards(ResumableDiscardProgram.Pile.Exhaust).Contains(card)))
                    throw new InvalidOperationException("Generated soul with both keywords did not exhaust at hand end.");
            }, verifyEnergyCosts: true, chooseBranch: (action, step) => step is not (2 or 3)
                || action.GetActionChoicesInExecutionOrder().SelectMany(choice => choice.Cards)
                    .Any(token => token.CardId == "SOUL" && token.SourceOccurrence == 1));
    }

    private async Task AssertCompactKeywordTerminalAsync(CombatState combat, Player player)
    {
        await PrepareCompactKeywordsAsync(combat, player, 0);
        string id = _request.ScenarioId == "COMPACT-SNAP-TERMINAL" ? "SNAP" : "SCULPTING_STRIKE";
        await CreatureCmd.SetCurrentHp(combat.Enemies.Single(), 1);
        await SetBlockAsync(combat.Enemies.Single(), 0);
        await AssertCompactPetCardRouteAsync(combat, player, 4, "CompactKeywordTerminal", "compact-keyword-terminal",
            [(id, 0)], (lane, _, _) =>
            {
                if (!lane.Terminal || Enumerable.Range(0, lane.EventCount).Select(lane.EventAt)
                    .Any(item => item.Kind is ResumableDiscardProgram.EventKind.Select or ResumableDiscardProgram.EventKind.KeywordAdded))
                    throw new InvalidOperationException("Terminal hand selection must not request or modify cards.");
            }, rounds: 0, requirePending: false, verifyEnergyCosts: true);
    }
}
