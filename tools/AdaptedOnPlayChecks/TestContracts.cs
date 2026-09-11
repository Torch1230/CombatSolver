// Real Harmony and production admission/dispatch code; game identities and effects are small managed fixtures.
namespace MegaCrit.Sts2.Core.Models
{
    internal abstract class AbstractModel;
    internal abstract class CardModel : AbstractModel
    {
        public int Value;
        protected virtual void OnPlay(GameActions.Multiplayer.PlayerChoiceContext context, Entities.Cards.CardPlay play) { }
        public void Play() => OnPlay(new(), new());
    }
}
namespace MegaCrit.Sts2.Core.Entities.Cards { internal sealed class CardPlay; }
namespace MegaCrit.Sts2.Core.GameActions.Multiplayer { internal sealed class PlayerChoiceContext; }
namespace MegaCrit.Sts2.Core.Modding
{
    internal sealed class ModManifest { public string id = "neutral-test"; public string name = "Neutral Test"; public bool affectsGameplay = true; }
    internal sealed class Mod
    {
        public ModManifest? manifest = new();
        public List<System.Reflection.Assembly> assemblies = [];
    }
    internal static class ModManager
    {
        public static List<Mod> Mods = [];
        public static IEnumerable<Mod> GetLoadedMods() => Mods;
    }
    internal static class AssemblyInfo
    {
        public static bool Unknown;
        public static Mod? ModForType(Type type, out bool isBaseGame)
        {
            isBaseGame = false;
            return Unknown ? null : new Mod();
        }
    }
}
namespace CombatSolver
{
    internal static class Entry
    {
        public const string ModId = "CombatSolver";
        public static Log Logger = new();
        public sealed class Log { public void Info(string message) { } }
    }
    internal sealed class IncompatibleGameplayModException(string id, string name, string description, string scope)
        : InvalidOperationException($"{id}/{name}/{scope}: {description}");
}
namespace CombatSolver.Engine.Common
{
    internal sealed class PredictedCard(MegaCrit.Sts2.Core.Models.CardModel preview)
    {
        public MegaCrit.Sts2.Core.Models.CardModel Preview => preview;
        public MegaCrit.Sts2.Core.Models.CardModel MutablePreview => preview;
    }
    internal sealed class PredictionTrace
    {
        internal readonly struct TraceScope : IDisposable { public void Dispose() { } }
    }
}
namespace CombatSolver.Engine.InCombat.Simulation { internal sealed class CombatPredictionSimulator; }
namespace CombatSolver.Engine.InCombat.Mirrors.Cards.OnPlay
{
    internal sealed class CardOnPlayMirrorContext : Common.Mirrors.IMethodMirrorContext<MegaCrit.Sts2.Core.Models.CardModel>
    {
        public required Simulation.CombatPredictionSimulator Simulator { get; init; }
        public required Common.PredictedCard Card { get; init; }
        public required MegaCrit.Sts2.Core.Entities.Cards.CardPlay CardPlay { get; init; }
        public Common.PredictionTrace.TraceScope PushDispatchSource(MegaCrit.Sts2.Core.Models.CardModel receiver,
            Common.Mirrors.MirrorMethodSpec method) => new();
        public void RecordMethodNotMirroredRisk() => throw new InvalidOperationException("Unexpected unsupported dispatch.");
        public void RecordMethodMirrorIncompleteRisk() => throw new InvalidOperationException("Unexpected inferred dispatch.");
    }
}
