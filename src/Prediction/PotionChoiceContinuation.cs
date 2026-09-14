using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Potions;

namespace CombatSolver;

internal sealed record PotionChoiceFrame(PotionModel Potion, int HistoryStart, int ShuffleEventsBefore);

// The completed prefix is an ordinary stable state; its history owns immutable generated
// options, and each selected option is cloned by the existing potion choice application.
internal sealed class PotionChoiceContinuation : IDisposable
{
    private readonly object _gate = new();
    private CombatPredictionSimulator? _seed;
    private ForkableSet<uint>? _deaths;
    private PotionChoiceFrame? _frame;

    internal static bool Supports(PotionModel potion)
        => !PotionChoiceMirrors.RequiresChoice(potion)
            && potion is AttackPotion or SkillPotion or PowerPotion or ColorlessPotion
                or Ashwater or DropletOfPrecognition or GamblersBrew or LiquidMemories or TouchOfInsanity;

    internal PotionChoiceContinuation(CombatPredictionSimulator seed, ForkableSet<uint> deaths, PotionChoiceFrame frame)
    {
        seed.AssertForkable();
        _seed = seed; _deaths = deaths; _frame = frame;
    }

    internal (CombatPredictionSimulator Simulator, ForkableSet<uint> Deaths, PotionChoiceFrame Frame)
        Fork(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_seed is null, this);
            return (_seed.Fork(), _deaths!.Fork(), _frame!);
        }
    }

    public void Dispose()
    {
        lock (_gate) { _seed = null; _deaths = null; _frame = null; }
    }
}
