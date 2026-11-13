using System.Text;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    private Dictionary<ShowOffPower, PredictedCard>? _showOffSources;

    internal void ApplyShowOff(PredictedCard card)
    {
        var model = (ShowOff)card.Preview;
        BeginCardPowerApplication(model);
        using var scope = new CardPowerApplicationScope(this, model);
        ApplyWithBeforeApplied<ShowOffPower>(model.Owner.Creature, model.DynamicVars["ShowOffPower"].IntValue,
            model.Owner.Creature, null, (_, power) =>
            {
                var showOff = (ShowOffPower)power;
                showOff.ShowOffInstance = (ShowOff)card.MutablePreview;
                (_showOffSources ??= []).Add(showOff, card);
            });
    }

    internal void GrowShowOff(ShowOffPower power)
    {
        if (_showOffSources is null || !_showOffSources.TryGetValue(power, out var card)) return;
        var source = (ShowOff)card.MutablePreview;
        source.IncreasedAim += power.Amount;
        source.CurrentAim = source.DynamicVars["BaseAim"].IntValue + source.IncreasedAim;
        power.ShowOffInstance = source;
        if (source.DeckVersion is not null)
        {
            RecordLongTermResource(power.Amount);
            RecordGrowthReward(GrowthSource.ShowOff);
        }
    }

    internal void AppendShowOffState(StringBuilder text, Player player, CombatPredictionSimulator simulator)
    {
        text.Append(";showOff=");
        var cards = simulator.State.GetPlayerCombatState(player).AllCards.ToArray();
        foreach (var power in EffectivePowers().OfType<ShowOffPower>().Where(power => power.Owner == player.Creature))
        {
            if (_showOffSources is null || !_showOffSources.TryGetValue(power, out var source)) { text.Append("null/"); continue; }
            text.Append(Array.IndexOf(cards, source)).Append(':').Append(CardChoiceSupport.ChoiceCardKey(source)).Append('/');
        }
    }

    internal static void AppendLiveShowOffState(StringBuilder text, Player player)
    {
        text.Append(";showOff=");
        var cards = player.PlayerCombatState!.AllCards.ToArray();
        foreach (var power in player.Creature.Powers.OfType<ShowOffPower>())
        {
            if (power.ShowOffInstance is not { } source) { text.Append("null/"); continue; }
            text.Append(Array.IndexOf(cards, source)).Append(':').Append(CardChoiceSupport.ChoiceCardKey(source)).Append('/');
        }
    }

    internal void AddShowOffFingerprint(ref StateFingerprintBuilder key, Player player, CombatPredictionSimulator simulator)
    {
        if (_showOffSources is not { Count: > 0 }) return;
        var cards = simulator.State.GetPlayerCombatState(player).AllCards.ToArray();
        key.Add("showOff");
        foreach (var power in EffectivePowers().OfType<ShowOffPower>().Where(power => power.Owner == player.Creature))
        {
            if (!_showOffSources.TryGetValue(power, out var source)) { key.Add(-2); continue; }
            key.Add(Array.IndexOf(cards, source));
            key.Add(CardChoiceSupport.ChoiceCardKey(source));
        }
    }
}
