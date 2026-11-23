using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    // Legacy event choices lack the new derived cost key. They may be compared in
    // their recorded schema only when the target has independently saved full costs.
    // The target must subsequently pass native bytes AND these ordered cost layers.
    private bool HasLegacyCostReplayEvidence()
    {
        string? version = _checkpointImport?["index"]?["build"]?["solverVersion"]?.GetValue<string>();
        if (!Version.TryParse(version, out Version? recorded) || recorded >= new Version(0, 48, 0)
            || _writer.ReplayVerification?["gameModuleComparison"]?["matches"]?.GetValue<bool>() != true
            || string.IsNullOrWhiteSpace(_request.ReplayStatePath))
            return false;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_request.ReplayStatePath));
        foreach (JsonElement pile in document.RootElement.GetProperty("players")[0].GetProperty("piles").EnumerateArray())
        foreach (JsonElement card in pile.GetProperty("cards").EnumerateArray())
        {
            JsonElement energy = card.GetProperty("energyCost").GetProperty("fields");
            if (!energy.TryGetProperty("CardEnergyCost._base", out _)
                || !energy.TryGetProperty("CardEnergyCost._localModifiers", out _)
                || !card.GetProperty("fields").TryGetProperty("CardModel._baseStarCost", out _)
                || !card.GetProperty("fields").TryGetProperty("CardModel._temporaryStarCosts", out _))
                return false;
        }
        return true;
    }

    private static bool RecordedChoiceMatches(RecordedChoiceContext? expected, RecordedChoiceContext? actual,
        bool allowLegacyCosts, out bool migrated)
    {
        migrated = false;
        if (expected == null) return true; // Older recordings did not capture this context.
        if (actual == null || expected.Surface != actual.Surface || expected.Source != actual.Source
            || expected.Min != actual.Min || expected.Max != actual.Max
            || expected.Options.Length != actual.Options.Length)
            return false;
        for (int index = 0; index < expected.Options.Length; index++)
        {
            RecordedCardIdentity left = expected.Options[index], right = actual.Options[index];
            if (left.Index != right.Index || left.ModelId != right.ModelId
                || left.NativeId != right.NativeId || left.Upgrade != right.Upgrade)
                return false;
            if (left.State == right.State) continue;
            if (!allowLegacyCosts || left.State.Contains("|cost-state=", StringComparison.Ordinal))
                return false;
            int start = right.State.IndexOf("|cost-state=", StringComparison.Ordinal);
            if (start < 0 || left.State != right.State[..start])
                return false;
            migrated = true;
        }
        return true;
    }

    private static IReadOnlyDictionary<char, IReadOnlyList<string>> LoadLegacyReplayCardCosts(
        CombatState state, string replayStatePath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(replayStatePath));
        var player = state.Players.Single().PlayerCombatState!;
        Dictionary<char, IReadOnlyList<string>> costs = [];
        foreach (JsonElement pile in document.RootElement.GetProperty("players")[0].GetProperty("piles").EnumerateArray())
        {
            var current = RequiredString(pile, "pile") switch
            {
                "Hand" => player.Hand, "Draw" => player.DrawPile,
                "Discard" => player.DiscardPile, "Exhaust" => player.ExhaustPile,
                "Play" => player.PlayPile,
                _ => throw new InvalidDataException("legacy_cost_unknown_pile"),
            };
            char marker = current == player.Hand ? 'H' : current == player.DrawPile ? 'D'
                : current == player.DiscardPile ? 'C' : current == player.ExhaustPile ? 'X' : '\0';
            if (marker == '\0') continue;
            JsonElement[] saved = pile.GetProperty("cards").EnumerateArray().ToArray();
            if (saved.Length != current.Cards.Count)
                throw new InvalidDataException("legacy_cost_pile_count_mismatch");
            List<string> values = [];
            for (int index = 0; index < saved.Length; index++)
            {
                CardModel live = current.Cards[index];
                if (live.Id.Entry != RequiredString(saved[index], "id")
                    || live.CurrentUpgradeLevel != saved[index].GetProperty("currentUpgradeLevel").GetInt32())
                    throw new InvalidDataException("legacy_cost_card_identity_mismatch");
                CardModel evidence = (CardModel)live.MutableClone();
                evidence.BaseStarCost = saved[index].GetProperty("fields").GetProperty("CardModel._baseStarCost").GetInt32();
                RestoreReplayCardCosts(evidence, saved[index]);
                StringBuilder expected = new();
                AppendLegacyCardCostState(expected, evidence);
                StringBuilder actual = new();
                AppendLegacyCardCostState(actual, live);
                if (expected.ToString() != actual.ToString())
                    throw new InvalidDataException($"legacy_cost_layers_mismatch:{marker}[{index}]:{live.Id.Entry}");
                values.Add(expected.ToString());
            }
            costs.Add(marker, values);
        }
        return costs;
    }

    private static void AppendLegacyCardCostState(StringBuilder key, CardModel card)
    {
        List<LocalCostModifier> energy = card.EnergyCost._localModifiers;
        List<TemporaryCardCost> stars = card._temporaryStarCosts;
        if (energy.Count == 0 && stars.Count == 0)
            return;
        key.Append("|cost-state=").Append(card.EnergyCost.GetWithModifiers(CostModifiers.None))
            .Append(':').Append(energy.Count).Append('[');
        for (int i = 0; i < energy.Count; i++)
        {
            LocalCostModifier m = energy[i];
            key.Append(m.Amount).Append(':').Append((int)m.Type)
                .Append(':').Append((int)m.Expiration).Append(':').Append(m.IsReduceOnly).Append(',');
        }
        key.Append("]/stars=").Append(card.BaseStarCost).Append(':').Append(stars.Count).Append('[');
        for (int i = 0; i < stars.Count; i++)
        {
            TemporaryCardCost s = stars[i];
            key.Append(s.Cost).Append(':').Append(s.ClearsWhenTurnEnds)
                .Append(':').Append(s.ClearsWhenCardIsPlayed).Append(',');
        }
        key.Append(']');
    }

    private static bool LegacyCardCostContinuationMatches(string expectedField, string actualField,
        IReadOnlyList<string> savedCosts, IReadOnlyList<string>? savedKeywords)
    {
        IReadOnlyList<string> expected = SplitReplayPileItems(expectedField[2..]);
        IReadOnlyList<string> actual = SplitReplayPileItems(actualField[2..]);
        if (expected.Count != actual.Count || expected.Count != savedCosts.Count) return false;
        List<string> migrated = [];
        for (int index = 0; index < actual.Count; index++)
        {
            string card = actual[index];
            if (!expected[index].Contains("|cost-state=", StringComparison.Ordinal))
            {
                int start = card.IndexOf("|cost-state=", StringComparison.Ordinal);
                int end = start < 0 ? -1 : card.IndexOf("/keywords=[", start, StringComparison.Ordinal);
                string cost = start < 0 ? string.Empty : end < 0 ? "invalid" : card[start..end];
                if (cost != savedCosts[index]) return false;
                if (start >= 0) card = card[..start] + card[end..];
            }
            migrated.Add(card);
        }
        string migratedField = actualField[..2] + string.Join(',', migrated) + ",";
        return expectedField == migratedField || savedKeywords != null
            && LegacyCardKeywordContinuationMatches(expectedField, migratedField, savedKeywords);
    }
}
