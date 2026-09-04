using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    public void RecordCardExhausted(Creature actor)
        => (_cardsExhaustedThisTurn ??= [])[actor] = GetCardsExhaustedThisTurn(actor) + 1;

    public void RecordCardDiscarded(Creature actor)
        => (_cardsDiscardedThisTurn ??= [])[actor] = GetCardsDiscardedThisTurn(actor) + 1;

    public void RecordCreatureAttacked(Creature actor)
        => (_creatureAttacksThisTurn ??= [])[actor] = GetCreatureAttacksThisTurn(actor) + 1;

    public void RecordEnergySpent(Player player, int amount)
    {
        if (amount > 0)
            (_energySpentThisTurn ??= [])[player] = GetEnergySpentThisTurn(player) + amount;
    }

    public void AfterEnergySpent(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        int amount)
        => PowerLifecycleSupport.AfterEnergySpent(simulator, this, card, amount);

    public void AfterStarsSpent(
        CombatPredictionSimulator simulator,
        PredictedCard card,
        int amount)
        => PowerLifecycleSupport.AfterStarsSpent(simulator, this, card, amount);

    public void RecordStarsGained(Player player, int amount)
    {
        if (amount > 0)
            (_starsGainedThisTurn ??= [])[player] = GetStarsGainedThisTurn(player) + amount;
    }

    public void RecordCardDrawn(PredictedCard card, bool fromHandDraw)
    {
        Player player = card.Preview.Owner;
        if (!fromHandDraw)
            (_nonHandDrawsThisTurn ??= [])[player] = GetNonHandDrawsThisTurn(player) + 1;
        if (card.Preview.Type == CardType.Status)
        {
            (_statusCardsDrawnThisTurn ??= [])[player] =
                GetStatusCardsDrawnThisTurn(player) + 1;
        }
    }

    public int GetStatusCardsDrawnThisTurn(Player player)
    {
        if (_statusCardsDrawnThisTurn?.TryGetValue(player, out int value) == true)
            return value;
        value = _rootHistory.CardsDrawn.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.Actor.Player == player
            && entry.Card.Type == CardType.Status);
        (_statusCardsDrawnThisTurn ??= [])[player] = value;
        return value;
    }

    public static void AppendLiveTurnCardHistory(
        StringBuilder text,
        CombatState combatState,
        Player player)
    {
        int statusCardsDrawn = CombatManager.Instance.History.Entries
            .OfType<CardDrawnEntry>()
            .Count(entry =>
            entry.HappenedThisTurn(combatState)
            && entry.Actor.Player == player
            && entry.Card.Type == CardType.Status);
        int zeroCostAttackStarts = CombatManager.Instance.History.CardPlaysStarted.Count(entry =>
            entry.HappenedThisTurn(combatState)
            && entry.CardPlay.Player == player
            && entry.CardPlay.Card.Type == CardType.Attack
            && entry.CardPlay.Resources.EnergyValue == 0);
        AppendTurnCardHistory(text, statusCardsDrawn, zeroCostAttackStarts);
    }

    public void AppendPredictedTurnCardHistory(StringBuilder text, Player player)
        => AppendTurnCardHistory(
            text,
            GetStatusCardsDrawnThisTurn(player),
            GetZeroCostAttackStartsThisTurn(player.Creature));

    private static void AppendTurnCardHistory(
        StringBuilder text,
        int statusCardsDrawn,
        int zeroCostAttackStarts)
        => text.Append(";Y=")
            .Append(statusCardsDrawn)
            .Append('/')
            .Append(zeroCostAttackStarts);

    public void AfterCardEnteredCombat(CombatPredictionSimulator simulator, PredictedCard card)
    {
        RegisterGeneratedCombatCard(card);
        CardModel preview = card.MutablePreview;
        if (preview.IsClone)
            return;
        Creature owner = preview.Owner.Creature;
        switch (preview)
        {
            case BansheesCry bansheesCry:
            {
                int etherealPlays = simulator.History.Entries
                    .OfType<CombatPredictionCardPlayFinishedEntry>()
                    .Count(entry => entry.WasEthereal && ReferenceEquals(entry.CardPlay.Player, preview.Owner));
                bansheesCry.EnergyCost.AddThisCombat(-etherealPlays * bansheesCry.DynamicVars.Energy.IntValue);
                break;
            }
            case Pinpoint pinpoint:
                pinpoint.EnergyCost.AddThisTurn(-GetSkillCardsPlayedThisTurn(owner));
                break;
            case Stomp stomp:
                stomp.EnergyCost.AddThisTurn(-GetAttacksPlayedThisTurn(owner));
                break;
            case Flatten flatten when simulator.State.GetOsty(preview.Owner) is { } osty
                                      && GetCreatureAttacksThisTurn(osty) > 0:
                flatten.EnergyCost.SetThisTurn(0);
                break;
        }
        NormalizeCardAfflictions(simulator);
    }

    public void AfterCardRemovedFromCombat(PredictedCard card)
        => UnregisterGeneratedCombatCard(card);

    public void AfterHandEmptied(CombatPredictionSimulator simulator, Player player)
        => TriggerRelicsAfterHandEmptied(simulator, player);

    public void RecordDamageReceived(Creature receiver, Creature? dealer, DamageResult result)
    {
        if (result.UnblockedDamage > 0)
        {
            (_unblockedDamageThisTurn ??= []).Add(receiver);
            (_cumulativeHpLost ??= [])[receiver] =
                GetCumulativeHpLost(receiver) + result.UnblockedDamage;
        }
        if (dealer == null || !result.Props.IsPoweredAttack())
            return;
        var key = (dealer, receiver);
        (_poweredAttackHitsThisTurn ??= [])[key] = GetPoweredAttackHitsThisTurn(dealer, receiver) + 1;
    }

    /// <summary>
    /// Records HP a heal actually put back. <paramref name="amount"/> is already clamped by max HP, so an
    /// overheal never reaches here and a route cannot be credited for HP it did not restore.
    /// </summary>
    /// <remarks>
    /// This is the counterpart of <see cref="GetCumulativeHpLost"/> on the same axis: cumulative loss is the
    /// gross damage a route took, recovery is how much of it the route bought back. Both only ever grow, so a
    /// route's strategic HP result stays monotonic in the search.
    /// </remarks>
    public void RecordHpRecovered(Creature receiver, int amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "生命回复量必须为正数。");
        (_recoveredHp ??= [])[receiver] = GetRecoveredHp(receiver) + amount;
    }

    public int GetCumulativeHpLost(Creature receiver)
        => _cumulativeHpLost?.GetValueOrDefault(receiver) ?? 0;

    public int GetRecoveredHp(Creature receiver)
        => _recoveredHp?.GetValueOrDefault(receiver) ?? 0;

    public bool HasLostHpThisTurn(Creature receiver)
    {
        if (_unblockedDamageThisTurn?.Contains(receiver) == true)
            return true;
        bool live = _rootHistory.DamageReceived.Any(entry =>
            entry.HappenedThisTurn(this)
            && entry.Receiver == receiver
            && entry.Result.UnblockedDamage > 0);
        if (live)
            (_unblockedDamageThisTurn ??= []).Add(receiver);
        return live;
    }

    public bool WasCardExhaustedThisTurn(Creature actor)
        => GetCardsExhaustedThisTurn(actor) > 0;

    public bool WasDoomAppliedThisTurn(Creature applier)
    {
        if (_doomAppliersThisTurn?.Contains(applier) == true)
            return true;
        bool live = _rootHistory.PowerReceived.Any(entry =>
            entry.HappenedThisTurn(this)
            && entry.Power is DoomPower
            && entry.Applier == applier);
        if (live)
            (_doomAppliersThisTurn ??= []).Add(applier);
        return live;
    }

    public int GetPoweredAttackHitsThisTurn(Creature dealer, Creature receiver)
    {
        var key = (dealer, receiver);
        if (_poweredAttackHitsThisTurn?.TryGetValue(key, out int value) == true)
            return value;
        value = _rootHistory.DamageReceived.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.Dealer == dealer
            && entry.Receiver == receiver
            && entry.Result.Props.IsPoweredAttack());
        (_poweredAttackHitsThisTurn ??= [])[key] = value;
        return value;
    }

    public int GetCardsDiscardedThisTurn(Creature actor)
    {
        if (_cardsDiscardedThisTurn?.TryGetValue(actor, out int value) == true)
            return value;
        value = _rootHistory.CardsDiscarded.Count(entry =>
            entry.HappenedThisTurn(this) && entry.Actor == actor);
        (_cardsDiscardedThisTurn ??= [])[actor] = value;
        return value;
    }

    public int GetCreatureAttacksThisTurn(Creature actor)
    {
        if (_creatureAttacksThisTurn?.TryGetValue(actor, out int value) == true)
            return value;
        value = _rootHistory.CreatureAttacked.Count(entry =>
            entry.HappenedThisTurn(this) && entry.Actor == actor);
        (_creatureAttacksThisTurn ??= [])[actor] = value;
        return value;
    }

    public int GetEnergySpentThisTurn(Player player)
    {
        if (_energySpentThisTurn?.TryGetValue(player, out int value) == true)
            return value;
        value = _rootHistory.EnergySpent
            .Where(entry => entry.HappenedThisTurn(this) && entry.Actor.Player == player)
            .Sum(entry => entry.Amount);
        (_energySpentThisTurn ??= [])[player] = value;
        return value;
    }

    public int GetStarsGainedThisTurn(Player player)
    {
        if (_starsGainedThisTurn?.TryGetValue(player, out int value) == true)
            return value;
        value = _rootHistory.StarsModified
            .Where(entry => entry.HappenedThisTurn(this)
                && entry.Actor.Player == player
                && entry.Amount > 0)
            .Sum(entry => entry.Amount);
        (_starsGainedThisTurn ??= [])[player] = value;
        return value;
    }

    public int GetNonHandDrawsThisTurn(Player player)
    {
        if (_nonHandDrawsThisTurn?.TryGetValue(player, out int value) == true)
            return value;
        value = _rootHistory.CardsDrawn.Count(entry =>
            entry.HappenedThisTurn(this)
            && entry.Actor.Player == player
            && !entry.FromHandDraw);
        (_nonHandDrawsThisTurn ??= [])[player] = value;
        return value;
    }

    private int GetCardsExhaustedThisTurn(Creature actor)
    {
        if (_cardsExhaustedThisTurn?.TryGetValue(actor, out int value) == true)
            return value;
        value = _rootHistory.CardsExhausted.Count(entry =>
            entry.HappenedThisTurn(this) && entry.Actor == actor);
        (_cardsExhaustedThisTurn ??= [])[actor] = value;
        return value;
    }

    private static void AddPoweredAttackHits(
        ref StateFingerprintBuilder fingerprint,
        ForkableDictionary<(Creature Dealer, Creature Receiver), int>? values)
    {
        ulong first = 0;
        ulong second = 0;
        int count = 0;
        if (values != null)
        {
            foreach (((Creature dealer, Creature receiver), int value) in values)
            {
                StateFingerprintBuilder item = new();
                item.Add(dealer.CombatId ?? uint.MaxValue);
                item.Add(receiver.CombatId ?? uint.MaxValue);
                item.Add(value);
                AddUnorderedItem(item.Finish(), ref first, ref second);
                count++;
            }
        }
        AddUnordered(ref fingerprint, 'h', count, first, second);
    }
}
