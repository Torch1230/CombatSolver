namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal sealed partial class ResumableDiscardProgram
{
    internal bool HasPanache => _panache != null;
    internal int PanacheCount => _panache?.Count(State) ?? 0;
    internal PanachePowerValues Panache(int index) => _panache!.Read(State, index);

    private void AfterCardPanache(int card)
    {
        // Native AfterCardPlayed deliberately bypasses the usual ending gate so
        // the killing card also finishes its completion callbacks.
        // The admitted listener list can neither add instances nor kill the player.
        // Do not stop the captured listener pass when an earlier instance kills the last enemy.
        for (int index = 0; index < PanacheCount; index++)
        {
            var value = Panache(index);
            if (value.Amount == 0) continue;
            int left = value.CardsLeft;
            if (value.AlreadyApplied && --left <= 0)
            {
                Emit(EventKind.PanacheStart, card, target: index);
                for (int target = 1; target < EnemyEnd; target++)
                {
                    if (!CreaturePresent(target) || Creature(target).CurrentHp <= 0) continue;
                    DamageValues damage = _combat!.Damage(State, target, value.Amount);
                    RecordDamage(card, target, damage, DamageTraits.Unpowered | DamageTraits.NoCard);
                }
                Emit(EventKind.PanacheFinish, card, target: index);
                left = 5;
            }
            _panache!.Write(State, index, value with { CardsLeft = left, AlreadyApplied = true });
        }
    }

    private void CapturePanacheTurnStart()
    {
        for (int index = 0; index < PanacheCount; index++)
        {
            var value = Panache(index);
            if (value.Amount != 0) _panache!.Write(State, index, value with { AmountOnTurnStart = value.Amount });
        }
    }

    private void ResetPanacheTurn()
    {
        for (int index = 0; index < PanacheCount; index++)
        {
            var value = Panache(index);
            if (value.Amount != 0) _panache!.Write(State, index, value with { CardsLeft = 5 });
        }
    }

    private void ClearPanache()
    {
        for (int index = 0; index < PanacheCount; index++)
        {
            var value = Panache(index);
            _panache!.Write(State, index, value with { Amount = 0, Order = 0 });
        }
    }
}
