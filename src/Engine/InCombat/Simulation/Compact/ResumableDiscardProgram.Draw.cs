namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal sealed partial class ResumableDiscardProgram
{
    // Each native Draw invocation owns its requested count, returned-card position and
    // unfinished drawn card. Nested hooks share this reversible buffer, never a CLR stack.
    private const int DrawFrameWidth = 3;
    private const int DrawRequested = 0, DrawReturned = 1, DrawPendingCard = 2;

    private bool DrawCards(int card, int count, int resumeIp)
    {
        if (_drawFrames != null) return DrawWithHooks(card, count, resumeIp);
        if (Read(Frame + DrawIndexOffset) == 0) State.Write(Frame + FirstDrawnOffset, -1);
        while (Read(Frame + DrawIndexOffset) < count)
        {
            int drawn = BeginDrawCard(card, resumeIp, card < 0, deferred: false);
            if (drawn == -2) return false;
            if (drawn < 0) break;
            ApplyDrawCost(drawn);
            if (Read(Frame + DrawIndexOffset) == 0) State.Write(Frame + FirstDrawnOffset, drawn);
            State.Write(Frame + DrawIndexOffset, Read(Frame + DrawIndexOffset) + 1);
        }
        return true;
    }

    private bool DrawWithHooks(int card, int count, int resumeIp)
    {
        var frames = _drawFrames!;
        if (frames.Count(State) == 0)
        {
            State.Write(Frame + FirstDrawnOffset, -1);
            frames.Append(State, [count, 0, -1]);
        }
        while (frames.Count(State) != 0)
        {
            int offset = frames.Count(State) - DrawFrameWidth;
            int pending = (int)frames.Read(State, offset + DrawPendingCard);
            if (pending >= 0)
            {
                // Native player powers precede all card enchantments. Finish the child
                // draw and its choices before this card's Slither consumes its RNG value.
                ApplyDrawCost(pending);
                Emit(EventKind.DrawResolved, pending);
                frames.Write(State, offset + DrawPendingCard, -1);
                continue;
            }
            int returned = (int)frames.Read(State, offset + DrawReturned);
            int drawn = returned < frames.Read(State, offset + DrawRequested)
                ? BeginDrawCard(card, resumeIp, offset == 0 && card < 0, deferred: true) : -1;
            if (drawn == -2) return false;
            if (drawn < 0)
            {
                frames.Truncate(State, offset);
                if (offset != 0)
                    Emit(EventKind.DrawPowerFinish, (int)frames.Read(State, offset - DrawFrameWidth + DrawPendingCard));
                continue;
            }
            frames.Write(State, offset + DrawReturned, returned + 1);
            frames.Write(State, offset + DrawPendingCard, drawn);
            if (offset == 0)
            {
                if (returned == 0) State.Write(Frame + FirstDrawnOffset, drawn);
                State.Write(Frame + DrawIndexOffset, returned + 1);
            }
            int amount = Power(_pagestormIndex).Amount;
            if (amount > 0 && IsEthereal(drawn))
            {
                Emit(EventKind.DrawPowerStart, drawn, _pagestormIndex);
                frames.Append(State, [amount, 0, -1]);
            }
        }
        return true;
    }

    // -2 is a pending shuffle choice, -1 is the native empty/ending/full-hand return.
    // Both the simple and nested callers use this one draw command and its entry gates.
    private int BeginDrawCard(int source, int resumeIp, bool fromHandDraw, bool deferred)
    {
        if (Count(Pile.Hand) >= 10 || Ending) return -1;
        if (Count(Pile.Draw) == 0 && Count(Pile.Discard) != 0)
        {
            State.Write(Frame + DrawResumeIpOffset, resumeIp);
            Shuffle(source);
            if (NeedsChoice) return -2;
        }
        if (Count(Pile.Draw) == 0 || Count(Pile.Hand) >= 10) return -1;
        int drawn = CardAt(Pile.Draw, 0);
        Move(drawn, Pile.Hand);
        Emit(EventKind.Draw, drawn, fromHandDraw ? 1 : 0, flags: deferred ? 1 : 0);
        return drawn;
    }

    private void ApplyDrawCost(int card)
    {
        if (Definition(card).DrawCost != null)
            Emit(EventKind.CostChanged, card, _drawCosts!.Draw(State, card));
    }
}
