namespace CombatSolver.Engine.InCombat.Simulation.Compact;

internal sealed partial class ResumableDiscardProgram
{
    internal bool HasRounds => _round != null;
    internal int RoundNumber => _round!.Round(State);
    internal int PlayerTurn => _round!.PlayerTurn(State);
    internal int TerminalPlayerTurn => _round!.TerminalTurn(State);
    internal bool EnemySide => _round!.EnemySide(State);
    internal bool BeganEnemyTurn => _round!.BeganEnemy(State);
    internal bool BeganPlayerTurn => PlayerTurn != _round!.Root.PlayerTurn;
    internal bool SingleTurnSly(int card) => Definition(card).SingleTurnSly && _round?.CleanedCards(State) != true;
    private bool IsSly(int card) => Definition(card).Sly || SingleTurnSly(card);
    private int Tools => _powers!.Amount(State, 0, BasicPowerKind.ToolsOfTheTrade);
    private int InstructionCount(int card) => card < 0 ? 2 : Definition(card).Effects.Count;
    private CardInstruction RoundInstruction => Read(Frame + EffectIndexOffset) switch
    {
        0 => new(CardInstructionKind.Draw, checked(_round!.Root.BaseDraw + Tools)),
        1 => new(CardInstructionKind.Discard, Tools),
        _ => throw new InvalidOperationException("Unknown player-start instruction.")
    };

    internal void BeginNextPlayerTurn(HandEndStaging staging)
    {
        if (_round == null || !Complete || EnemySide || Terminal || Ending)
            throw new InvalidOperationException("Round advance requires admitted completed player choice.");
        EndHandEffects(staging);
        if (CheckWinCondition()) return;
        // Native flush does not invoke discard hooks, history or Sly. Retain is
        // captured separately from temporary flags removed by the cleanup below.
        for (int index = 0; index < Count(Pile.Hand);)
        {
            int card = CardAt(Pile.Hand, index);
            if (Definition(card).Retain) index++;
            else MoveHandEndResult(card, Pile.Discard);
        }
        CleanupCards();
        EndSidePowerEffects(enemySide: false);
        _round.BeginEnemy(State);
        Emit(EventKind.BeginSide, -2);
        CapturePowerTurnStart(1);
        ClearCreatureBlock(1);
        TriggerPoison(-2, 1);
        if (CheckWinCondition()) return;
        ExecuteMonsterMove(1, CurrentMonsterMove);
        if (CheckWinCondition()) return;
        CleanupCards();
        EndSidePowerEffects(enemySide: true);
        AdvanceMonsterMove(1);
        _round.BeginPlayer(State);
        Emit(EventKind.BeginSide, -1);
        CapturePowerTurnStart(0);
        ClearCreatureBlock(0);
        State.Write(EnergySlot, _round.Root.MaxEnergy);
        Emit(EventKind.ResetEnergy, -1, Energy);
        // A synthetic frame shares draw / shuffle / choice / child-card machinery.
        // It has no card identity, payment, play history or result pile.
        State.Write(DepthSlot, 1);
        for (int offset = 0; offset < FrameWidth; offset++) State.Write(Frame + offset, 0);
        State.Write(Frame + CardOffset, -1);
        State.Write(Frame + IpOffset, 1);
        State.Write(Frame + TargetOffset, -1);
    }

    private void CleanupCards()
    {
        _round!.CleanCards(State);
        Emit(EventKind.CleanupCards, -1);
    }
}
