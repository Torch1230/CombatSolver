using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Models;

namespace CombatSolver.Engine.InCombat.Mirrors;

// Keep hook membership/order frozen, but follow a card's branch-owned COW preview.
internal readonly struct CardHookReceiver(AbstractModel original, PredictedCard? card)
{
    public AbstractModel Current => card?.Preview ?? original;
}
