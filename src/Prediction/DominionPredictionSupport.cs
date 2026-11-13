using System.Reflection;
using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Models.Dread;
using MegaCrit.Sts2.Core.Models.Dread.Dominions;

namespace CombatSolver;

internal static class DominionPredictionSupport
{
    private static readonly FieldInfo Run = typeof(DominionModel).GetField("_runState", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo EnteringPlayers = typeof(DominionModel).GetField("_playersEnteringCombat", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static DominionModel Capture(DominionModel source)
    {
        if (source.GetType().Assembly != typeof(DominionModel).Assembly
            || source is not (AeonglassDominion or CeremonialBeastDominion or InsatiableDominion
            or KaiserCrabDominion or KinDominion or KnowledgeDemonDominion or LagavulinMatriarchDominion
            or QueenDominion or SoulFyshDominion or TestSubjectDominion or VantomDominion or WaterfallGiantDominion))
            throw new NotSupportedException($"Dominion {source.GetType().FullName} requires prediction semantics.");
        if (EnteringPlayers.GetValue(source) != null)
            throw new InvalidOperationException("Capture the combat root after the dominion's combat-start effects complete.");
        DominionModel clone = PredictionUtils.CloneModelForSimulation(source);
        Run.SetValue(clone, null);
        return clone;
    }
}
