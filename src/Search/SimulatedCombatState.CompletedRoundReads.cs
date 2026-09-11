using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver;

internal sealed partial class SimulatedCombatState
{
    internal void AssertCompletedRoundRoot()
    {
        AssertForkable();
        if (_drawNextTurn?.Count > 0 || _skipNextMove?.Count > 0)
            throw new NotSupportedException("Completed round root has unrepresented deferred draw or skipped moves.");
    }

    // Reusable history-map storage for one completed evaluation lane. Each read
    // restores the captured map shape, then imports committed side-start markers.
    // BeginSideTurn is history bookkeeping only; no card, Power or hook executes.
    internal sealed class CompletedRoundReadBinding
    {
        private readonly SimulatedCombatState _combat;
        private readonly Player _player;
        private readonly Creature _enemy;
        private readonly List<Action> _restore = [];
        private readonly int _rootTurn;

        internal CompletedRoundReadBinding(SimulatedCombatState combat, Player player, Creature enemy)
        {
            combat.AssertForkable();
            _combat = combat; _player = player; _enemy = enemy;
            _rootTurn = combat.GetPlayerTurnNumber(player);
            Own(ref combat._attacksPlayedThisTurn);
            Own(ref combat._shivsPlayedThisTurn);
            Own(ref combat._blockCardsPlayedThisTurn);
            Own(ref combat._skillCardsPlayedThisTurn);
            Own(ref combat._cardsExhaustedThisTurn);
            Own(ref combat._cardsDiscardedThisTurn);
            Own(ref combat._creatureAttacksThisTurn);
            Own(ref combat._cardPlaySeriesStartedThisTurn);
            Own(ref combat._zeroCostAttackStartsThisTurn);
            Own(ref combat._cardPlayStartsThisTurn);
            Own(ref combat._cardsPlayedThisTurn);
            Own(ref combat._manualCardsPlayedThisTurn);
            Own(ref combat._energySpentThisTurn);
            Own(ref combat._starsGainedThisTurn);
            Own(ref combat._nonHandDrawsThisTurn);
            Own(ref combat._statusCardsDrawnThisTurn);
            Own(ref combat._poweredAttackHitsThisTurn);
            Own(ref combat._doomAppliersThisTurn);
            Own(ref combat._skillsPlayedThisTurn);
            Own(ref combat._fetchCardsPlayedThisTurn);
        }

        internal void Read(int round, int playerTurn, bool enemySide, bool beganEnemy, bool beganPlayer)
        {
            foreach (var restore in _restore) restore();
            _combat._roundNumber = round;
            _combat._currentSide = enemySide ? CombatSide.Enemy : CombatSide.Player;
            _combat._playerTurnNumbers![_player] = playerTurn;
            if (beganEnemy)
            {
                _combat.ResetTurnHistoryWindow();
                _combat.ResetPowerLifecycleTurn(_player.Creature);
                if (beganPlayer) _combat.ResetPowerLifecycleTurn(_enemy);
                _combat.BeginSideTurn(enemySide ? _enemy : _player.Creature);
            }
            if (beganPlayer != (playerTurn != _rootTurn))
                throw new InvalidOperationException("Completed round clock disagrees with committed phase history.");
        }

        private void Own<TKey, TValue>(ref ForkableDictionary<TKey, TValue>? field) where TKey : notnull
        {
            var root = field?.ToArray() ?? [];
            var target = new ForkableDictionary<TKey, TValue>();
            field = target;
            _restore.Add(() =>
            {
                target.Clear();
                foreach (var item in root) target.Add(item.Key, item.Value);
            });
        }

        private void Own<T>(ref ForkableSet<T>? field)
        {
            var root = field?.ToArray() ?? [];
            var target = new ForkableSet<T>();
            field = target;
            _restore.Add(() =>
            {
                target.Clear();
                foreach (var item in root) target.Add(item);
            });
        }
    }
}
