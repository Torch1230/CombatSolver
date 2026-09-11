namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal sealed partial class ResumableDiscardProgram
{
    // Native starts pile entries before awaiting the ordered effect chain. The
    // phase caller must supply its admitted staging contract; timing is not guessed here.
    internal enum HandEndStaging { Sequential, Together }

    internal void EndHandEffects(HandEndStaging staging)
    {
        if (!_handEndAdmitted || !Complete || !Enum.IsDefined(staging))
            throw new InvalidOperationException("Hand-end effects require an idle program and an admitted staging order.");
        if (Terminal || Ending) return;
        // The closed phase has at most ten cards and no choices or callbacks. Its local
        // selection cannot escape; all surviving state and history enter the same journal.
        Span<int> hand = stackalloc int[10];
        int count = Count(Pile.Hand);
        for (int index = 0; index < count; index++) hand[index] = CardAt(Pile.Hand, index);
        foreach (int card in hand[..count])
            if (Definition(card).HandEndDamage == null && Definition(card).Ethereal)
                MoveHandEndResult(card, Pile.Exhaust);
        if (staging == HandEndStaging.Together)
            foreach (int card in hand[..count])
                if (Definition(card).HandEndDamage != null) StageHandEndCard(card);
        foreach (int card in hand[..count])
        {
            if (Definition(card).HandEndDamage is not { } amount) continue;
            if (staging == HandEndStaging.Sequential) StageHandEndCard(card);
            Emit(EventKind.HandEndStart, card);
            if (!Ending && Creature(0).CurrentHp > 0)
                RecordDamage(card, 0, _combat!.Damage(State, 0, amount), DamageTraits.Unpowered);
            Emit(EventKind.HandEndFinish, card);
            MoveHandEndResult(card, Definition(card).Ethereal ? Pile.Exhaust : Pile.Discard);
        }
    }

    private void StageHandEndCard(int card)
    {
        if (Ending) return;
        Move(card, Pile.Play);
        Emit(EventKind.HandEndMoved, card);
    }

    private void MoveHandEndResult(int card, Pile destination)
    {
        if (Ending) return;
        Move(card, destination);
        Emit(EventKind.ResultMoved, card, (int)destination);
    }
}
