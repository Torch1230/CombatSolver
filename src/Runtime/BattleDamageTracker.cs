using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal readonly record struct BattleDamageSnapshot(
    int HpLostSoFar,
    int SoldHpCommitted,
    int PotionsUsedSoFar,
    string[] PotionIdsUsedSoFar);

internal static class BattleDamageTracker
{
    private static ICombatState? _combat;
    private static int? _lastObservedHp;
    private static int _hpLostSoFar;
    private static int _soldHpCommitted;
    private static int _potionHistoryCountAtStart;
    private static int _historyEntryCountAtLastObservation;
    private static int? _plannedTurn;
    private static int _plannedTurnStartHp;
    private static int _plannedSoldHp;

    public static void Begin(ICombatState? combat)
    {
        Reset();
        _combat = combat;
        _lastObservedHp = GetSinglePlayer(combat)?.Creature.CurrentHp;
        _potionHistoryCountAtStart = CountPotionHistoryEntries();
        _historyEntryCountAtLastObservation = CombatManager.Instance.History.Entries.Count();
        Entry.Logger.Info($"[CombatSolver/Test] BATTLE_DAMAGE_RESET start_hp={_lastObservedHp?.ToString() ?? "-"}");
    }

    public static BattleDamageSnapshot Observe(CombatState combat)
    {
        if (!ReferenceEquals(_combat, combat))
            Begin(combat);

        Player? player = GetSinglePlayer(combat);
        if (player == null)
        {
            string[] usedPotions = PotionIdsUsedSoFar();
            return new BattleDamageSnapshot(_hpLostSoFar, _soldHpCommitted, usedPotions.Length, usedPotions);
        }

        int currentHp = player.Creature.CurrentHp;
        var historyEntries = CombatManager.Instance.History.Entries;
        int historyHpLost = historyEntries
            .Skip(_historyEntryCountAtLastObservation)
            .OfType<DamageReceivedEntry>()
            .Where(entry => ReferenceEquals(entry.Receiver, player.Creature))
            .Sum(entry => Math.Max(0, entry.Result.UnblockedDamage));
        int turn = player.PlayerCombatState?.TurnNumber ?? -1;
        if (_plannedTurn is int plannedTurn && turn > plannedTurn)
        {
            int actualLoss = Math.Max(0, _plannedTurnStartHp - currentHp);
            int committed = Math.Min(_plannedSoldHp, actualLoss);
            _soldHpCommitted += committed;
            Entry.Logger.Info(
                $"[CombatSolver/Test] BATTLE_SELL_COMMIT turn={plannedTurn} actual_hp_lost={actualLoss} planned_sold_hp={_plannedSoldHp} committed_sold_hp={committed} battle_sold_hp={_soldHpCommitted}");
            ClearPlan();
        }

        int observedHpDrop = _lastObservedHp is int previousHp
            ? Math.Max(0, previousHp - currentHp)
            : 0;
        _hpLostSoFar += Math.Max(observedHpDrop, historyHpLost);
        _lastObservedHp = currentHp;
        _historyEntryCountAtLastObservation = historyEntries.Count();
        string[] potionIds = PotionIdsUsedSoFar();
        return new BattleDamageSnapshot(_hpLostSoFar, _soldHpCommitted, potionIds.Length, potionIds);
    }

    public static void RegisterPlan(CombatState combat, SolverResult result)
    {
        Player? player = GetSinglePlayer(combat);
        if (player?.PlayerCombatState == null)
            return;

        int turn = player.PlayerCombatState.TurnNumber;
        _plannedTurn = turn;
        _plannedTurnStartHp = player.Creature.CurrentHp;
        _plannedSoldHp = result.SoldHpByTurn.GetValueOrDefault(turn);
        Entry.Logger.Info(
            $"[CombatSolver/Test] BATTLE_SELL_PLAN turn={turn} planned_sold_hp={_plannedSoldHp} battle_sold_hp={_soldHpCommitted} battle_hp_lost={_hpLostSoFar}");
    }

    public static void Reset()
    {
        _combat = null;
        _lastObservedHp = null;
        _hpLostSoFar = 0;
        _soldHpCommitted = 0;
        _potionHistoryCountAtStart = 0;
        _historyEntryCountAtLastObservation = 0;
        ClearPlan();
    }

    private static Player? GetSinglePlayer(ICombatState? combat)
        => combat?.Players.Count == 1 ? combat.Players[0] : null;

    private static string[] PotionIdsUsedSoFar()
        => CombatManager.Instance.History.Entries.OfType<PotionUsedEntry>()
            .Skip(_potionHistoryCountAtStart)
            .Select(entry => entry.Potion.Id.Entry)
            .ToArray();

    private static int CountPotionHistoryEntries()
        => CombatManager.Instance.History.Entries.OfType<PotionUsedEntry>().Count();

    private static void ClearPlan()
    {
        _plannedTurn = null;
        _plannedTurnStartHp = 0;
        _plannedSoldHp = 0;
    }
}
