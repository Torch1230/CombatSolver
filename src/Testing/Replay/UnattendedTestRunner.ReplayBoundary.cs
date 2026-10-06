using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private async Task AssertReplayBoundaryContractAsync(Player player)
    {
        using (System.Text.Json.JsonDocument savedStars = System.Text.Json.JsonDocument.Parse("""
            [{"<Cost>k__BackingField":3,"<ClearsWhenTurnEnds>k__BackingField":true,"<ClearsWhenCardIsPlayed>k__BackingField":false},
             {"<Cost>k__BackingField":0,"<ClearsWhenTurnEnds>k__BackingField":false,"<ClearsWhenCardIsPlayed>k__BackingField":true}]
            """))
        {
            var costs = savedStars.RootElement.EnumerateArray().Select(RestoreReplayTemporaryStarCost).ToArray();
            if (costs[0].Cost != 3 || !costs[0].ClearsWhenTurnEnds || costs[0].ClearsWhenCardIsPlayed
                || costs[1].Cost != 0 || costs[1].ClearsWhenTurnEnds || !costs[1].ClearsWhenCardIsPlayed)
                throw new InvalidOperationException("Replay must restore nested star costs and both expiration flags exactly.");
        }
        HashSet<uint> completed = [6];
        if (!IsRecordedActionWindow(6, 6, true, completed)
            || !IsRecordedActionWindow(null, 6, false, completed)
            || IsRecordedActionWindow(7, 6, true, completed)
            || IsRecordedActionWindow(null, 6, true, completed)
            || IsRecordedActionWindow(null, 7, false, completed))
            throw new InvalidOperationException("Replay must submit a queued input in its recorded parent window or after that parent completed with an idle executor.");
        foreach (string recorded in new[] { "H=A;Y=2/1;R=9", "H=A;Y=2/1/3;R=9" })
            if (!ReplayContinuationMatches(recorded, "H=A;Y=2/1/3/2;R=9"))
                throw new InvalidOperationException("Legacy history schema was rejected.");
        const string legacyLimit = "H=A;Y=0/0/0/0;R=9";
        const string currentLimit = legacyLimit + ";max_hand_size=10";
        if (!ReplayContinuationMatches(legacyLimit, currentLimit, allowLegacyDefaultHandLimit: true)
            || ReplayContinuationMatches(legacyLimit, currentLimit))
            throw new InvalidOperationException("Legacy default hand limit requires a verified native checkpoint.");
        foreach (string invalid in new[] {
            legacyLimit + ";max_hand_size=13", legacyLimit + ";max_hand_size=0",
            legacyLimit + ";max_hand_size=10;max_hand_size=10",
            "H=A;Y=0/0/0/0;max_hand_size=10;R=9", "H=B;Y=0/0/0/0;R=9;max_hand_size=10",
            "H=A;Y=0/0/0/0;R=10;max_hand_size=10",
        })
            if (ReplayContinuationMatches(legacyLimit, invalid, allowLegacyDefaultHandLimit: true))
                throw new InvalidOperationException("Legacy hand-limit migration accepted a real state difference.");
        if (ReplayContinuationMatches(currentLimit,
                legacyLimit + ";max_hand_size=13", allowLegacyDefaultHandLimit: true)
            || ReplayContinuationMatches(legacyLimit + ";max_hand_size=13",
                currentLimit, allowLegacyDefaultHandLimit: true))
            throw new InvalidOperationException("Explicit recorded hand limits must remain strict.");
        _completedChecks.Add("ReplayLegacyDefaultHandLimit:NativeGate:RejectNonDefaultExplicitDuplicateAndStateDrift");
        string[] legacyStarts = ["H=A;Y=0/0/0;R=9", "H=A;Y=0/0/0/0;R=9"];
        const string currentStart = "H=A;Y=0/0/0/0;FlameHp=0;AttackStarts=0;R=9";
        foreach (string legacyStart in legacyStarts)
            if (!ReplayContinuationMatches(legacyStart, currentStart, allowLegacyZeroCounter: true)
                || ReplayContinuationMatches(legacyStart, currentStart))
                throw new InvalidOperationException("Zero legacy FlameHp is limited to an explicit native combat-start boundary.");
        foreach (string invalid in new[] {
            "H=A;Y=0/0/0/0;FlameHp=1;AttackStarts=0;R=9", "H=A;FlameHp=0;Y=0/0/0/0;AttackStarts=0;R=9",
            "H=A;Y=0/0/0/0;FlameHp=0;FlameHp=0;AttackStarts=0;R=9", "H=A;Y=0/0/0/0;AttackStarts=0;FlameHp=0;R=9",
            "H=A;Y=0/0/0/0;FlameHp=0;AttackStarts=1;R=9", "H=B;Y=0/0/0/0;FlameHp=0;AttackStarts=0;R=9",
            "H=A;Y=0/1/0/0;FlameHp=0;AttackStarts=0;R=9", "H=A;Y=0/0/0/0;FlameHp=0;AttackStarts=0;R=10",
        })
            if (legacyStarts.Any(legacyStart =>
                    ReplayContinuationMatches(legacyStart, invalid, allowLegacyZeroCounter: true)))
                throw new InvalidOperationException("Legacy combat-start migration accepted a real state difference.");
        foreach (string recorded in new[]
                 {
                     "H=A;Y=2/0;R=9", "H=A;Y=2/1/4;R=9",
                     "H=A;Y=2/1/3/1;R=9", "H=B;Y=2/1/3;R=9",
                 })
            if (ReplayContinuationMatches(recorded, "H=A;Y=2/1/3/2;R=9"))
                throw new InvalidOperationException("A recorded state mismatch was accepted.");
        const string legacyCards = "D=A/private=-/baselib=-,B/private=1/4/baselib=-,;Y=0/0/0/0;R=9";
        const string currentCards = "D=A/private=-/keywords=[]/baselib=-,B/private=1/4/keywords=[Exhaust]/baselib=-,;Y=0/0/0/0;R=9";
        IReadOnlyDictionary<char, IReadOnlyList<string>> savedKeywords =
            new Dictionary<char, IReadOnlyList<string>> { ['D'] = [string.Empty, "Exhaust"] };
        if (!ReplayContinuationMatches(legacyCards, currentCards, legacyCardKeywords: savedKeywords)
            || ReplayContinuationMatches(
                legacyCards,
                currentCards.Replace("[Exhaust]", "[Retain]", StringComparison.Ordinal),
                legacyCardKeywords: savedKeywords)
            || ReplayContinuationMatches(
                legacyCards,
                currentCards.Replace("private=1/4", "private=1/5", StringComparison.Ordinal),
                legacyCardKeywords: savedKeywords))
        {
            throw new InvalidOperationException("Legacy card keyword migration did not preserve exact saved keywords and card state.");
        }

        const string unmodifiedCard = "H=A/private=-/keywords=[]/baselib=-,;Y=0/0/0/0;R=9";
        const string modifiedCard = "H=A/private=-|cost-state=1:1[0:0:0:False,]/stars=0:0[]/keywords=[]/baselib=-,;Y=0/0/0/0;R=9";
        if (ReplayContinuationMatches(unmodifiedCard, modifiedCard)
            || ReplayContinuationMatches(modifiedCard,
                modifiedCard.Replace("cost-state=1:", "cost-state=2:", StringComparison.Ordinal))
            || ReplayContinuationMatches(modifiedCard,
                modifiedCard.Replace("/stars=0:", "/stars=1:", StringComparison.Ordinal)))
            throw new InvalidOperationException("Replay must preserve actual energy and star cost differences.");

        const string cost = "|cost-state=1:1[0:0:0:False,]/stars=0:0[]";
        Dictionary<char, IReadOnlyList<string>> savedCosts = new() { ['H'] = [cost] };
        if (!ReplayContinuationMatches(unmodifiedCard, modifiedCard, legacyCardCosts: savedCosts)
            || ReplayContinuationMatches(unmodifiedCard,
                modifiedCard.Replace("0:0:0:False", "0:0:1:False", StringComparison.Ordinal), legacyCardCosts: savedCosts)
            || ReplayContinuationMatches(unmodifiedCard,
                modifiedCard.Replace("/stars=0:", "/stars=1:", StringComparison.Ordinal), legacyCardCosts: savedCosts)
            || ReplayContinuationMatches(unmodifiedCard,
                modifiedCard.Replace("private=-", "private=1", StringComparison.Ordinal), legacyCardCosts: savedCosts)
            || ReplayContinuationMatches(unmodifiedCard, modifiedCard,
                legacyCardCosts: new Dictionary<char, IReadOnlyList<string>> { ['H'] = [string.Empty] }))
            throw new InvalidOperationException("Legacy cost migration must compare saved ordered layers, stars and all other state.");

        RecordedCardIdentity oldOption = new(0, "CARD.A", 7, 0, "A|private=-|baselib=-");
        RecordedCardIdentity newOption = oldOption with { State = oldOption.State + cost };
        RecordedChoiceContext oldChoice = new("Discard", "test", 1, 1, [oldOption]);
        RecordedChoiceContext newChoice = oldChoice with { Options = [newOption] };
        if (RecordedChoiceMatches(oldChoice, newChoice, false, out _)
            || !RecordedChoiceMatches(oldChoice, newChoice, true, out bool migrated) || !migrated
            || RecordedChoiceMatches(oldChoice, newChoice with { Source = "other" }, true, out _)
            || RecordedChoiceMatches(oldChoice, newChoice with { Options = [newOption with { NativeId = 8 }] }, true, out _)
            || RecordedChoiceMatches(oldChoice, newChoice with
                { Options = [newOption with { State = newOption.State.Replace("private=-", "private=1", StringComparison.Ordinal) }] }, true, out _)
            || RecordedChoiceMatches(newChoice, newChoice with
                { Options = [newOption with { State = newOption.State.Replace("/stars=0:", "/stars=1:", StringComparison.Ordinal) }] }, true, out _))
            throw new InvalidOperationException("Legacy choices require explicit evidence and preserve native identity, source and recorded state.");

        using NativeReplayDriver driver = new(this, [], 0, player);
        InvalidDataException failure = new("replay_boundary_original_failure");
        driver.ObserveBoundary(() => throw failure);
        try
        {
            await driver.AdvanceAsync(Task.CompletedTask);
        }
        catch (InvalidDataException error) when (ReferenceEquals(error, failure))
        {
            return;
        }
        throw new InvalidOperationException("Replay lost the original boundary failure.");
    }
}
