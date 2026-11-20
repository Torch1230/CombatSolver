// Value-only nodes let the real representative selector run without game models.
namespace CombatSolver
{
    internal sealed record SearchNode
    {
        public PowerCommitment? PowerCommitment { get; init; }
        public SimulationSnapshot Snapshot { get; init; } = new();
        public bool IsTerminal { get; init; }
        public int PotionCount { get; init; }
        public int Turn { get; init; } = 1;
        public int ActionCount { get; init; }
        public double Score { get; init; }
    }

    internal sealed record SimulationSnapshot
    {
        public bool PlayerDead { get; init; }
        public int ProjectedPlayerHp { get; init; } = 50;
        public int OffensiveProgressValue { get; init; }
    }
}

namespace MegaCrit.Sts2.Core.Models
{
    public abstract class CardModel
    {
        public bool IsUpgraded { get; init; }
        public TestDynamicVars DynamicVars { get; } = new();
    }

    public sealed record TestDynamicVar(int IntValue);
    public sealed class TestDynamicVars : Dictionary<string, TestDynamicVar>
    {
        public TestDynamicVar Cards => this["Cards"];
    }
}

namespace MegaCrit.Sts2.Core.Models.Cards
{
    using MegaCrit.Sts2.Core.Models;

    // Silent
    public sealed class Abrasive : CardModel;
    public sealed class Accelerant : CardModel;
    public sealed class Accuracy : CardModel;
    public sealed class Afterimage : CardModel;
    public sealed class Envenom : CardModel;
    public sealed class FanOfKnives : CardModel;
    public sealed class Footwork : CardModel;
    public sealed class InfiniteBlades : CardModel;
    public sealed class MasterPlanner : CardModel;
    public sealed class NoxiousFumes : CardModel;
    public sealed class PhantomBlades : CardModel;
    public sealed class SerpentForm : CardModel;
    public sealed class Speedster : CardModel;
    public sealed class ToolsOfTheTrade : CardModel;
    public sealed class Tracking : CardModel;
    public sealed class WellLaidPlans : CardModel;
    public sealed class WraithForm : CardModel;

    // Ironclad
    public sealed class Inflame : CardModel;
    public sealed class DemonForm : CardModel;
    public sealed class Rupture : CardModel;
    public sealed class Cruelty : CardModel;
    public sealed class DarkEmbrace : CardModel;
    public sealed class FeelNoPain : CardModel;
    public sealed class Corruption : CardModel;
    public sealed class Barricade : CardModel;
    public sealed class CrimsonMantle : CardModel;
    public sealed class StoneArmor : CardModel;
    public sealed class Unmovable : CardModel;
    public sealed class Juggernaut : CardModel;
    public sealed class Inferno : CardModel;
    public sealed class Vicious : CardModel;
    public sealed class Aggression : CardModel;
    public sealed class Hellraiser : CardModel;
    public sealed class Juggling : CardModel;
    public sealed class Stampede : CardModel;
    public sealed class Pyre : CardModel;

    // Defect
    public sealed class BiasedCognition : CardModel;
    public sealed class Defragment : CardModel;
    public sealed class Capacitor : CardModel;
    public sealed class ConsumingShadow : CardModel;
    public sealed class Coolant : CardModel;
    public sealed class Loop : CardModel;
    public sealed class Spinner : CardModel;
    public sealed class Storm : CardModel;
    public sealed class Thunder : CardModel;
    public sealed class TrashToTreasure : CardModel;
    public sealed class Buffer : CardModel;
    public sealed class BulkUp : CardModel;
    public sealed class EchoForm : CardModel;
    public sealed class Feral : CardModel;
    public sealed class CreativeAi : CardModel;
    public sealed class Iteration : CardModel;
    public sealed class MachineLearning : CardModel;
    public sealed class Hailstorm : CardModel;
    public sealed class Smokestack : CardModel;
    public sealed class Subroutine : CardModel;

    // Regent
    public sealed class Arsenal : CardModel;
    public sealed class BlackHole : CardModel;
    public sealed class ChildOfTheStars : CardModel;
    public sealed class Furnace : CardModel;
    public sealed class Genesis : CardModel;
    public sealed class SeekingEdge : CardModel;
    public sealed class TheSealedThrone : CardModel;
    public sealed class SpectrumShift : CardModel;
    public sealed class MonarchsGaze : CardModel;
    public sealed class NeutronAegis : CardModel;
    public sealed class Orbit : CardModel;
    public sealed class PaleBlueDot : CardModel;
    public sealed class Parry : CardModel;
    public sealed class PillarOfCreation : CardModel;
    public sealed class Royalties : CardModel;
    public sealed class SwordSage : CardModel;
    public sealed class Tyranny : CardModel;
    public sealed class VoidForm : CardModel;

    // Necrobinder
    public sealed class Calcify : CardModel;
    public sealed class Countdown : CardModel;
    public sealed class DevourLife : CardModel;
    public sealed class Haunt : CardModel;
    public sealed class NecroMastery : CardModel;
    public sealed class ReaperForm : CardModel;
    public sealed class Shroud : CardModel;
    public sealed class SleightOfFlesh : CardModel;
    public sealed class SpiritOfAsh : CardModel;
    public sealed class CallOfTheVoid : CardModel;
    public sealed class DanseMacabre : CardModel;
    public sealed class Demesne : CardModel;
    public sealed class ForbiddenGrimoire : CardModel;
    public sealed class Friendship : CardModel;
    public sealed class Lethality : CardModel;
    public sealed class Neurosurge : CardModel;
    public sealed class Pagestorm : CardModel;
    public sealed class SentryMode : CardModel;

    // Colorless
    public sealed class EternalArmor : CardModel;
    public sealed class Fasten : CardModel;
    public sealed class PrepTime : CardModel;
    public sealed class Prowess : CardModel;
    public sealed class Automation : CardModel;
    public sealed class Calamity : CardModel;
    public sealed class Entropy : CardModel;
    public sealed class Mayhem : CardModel;
    public sealed class Nostalgia : CardModel;
    public sealed class Panache : CardModel;
    public sealed class RollingBoulder : CardModel;
    public sealed class Stratagem : CardModel;

    // Fool
    public sealed class SorrowfulStrike : CardModel;
    public sealed class Jest : CardModel;
    public sealed class BrightenUp : CardModel;
    public sealed class Rummage : CardModel;
    public sealed class Vitriol : CardModel;
    public sealed class Sadness : CardModel;
    public sealed class AllIsWell : CardModel;
    public sealed class Archery : CardModel;
    public sealed class Ecstasy : CardModel;
    public sealed class Ennui : CardModel;
    public sealed class ExplosiveArrows : CardModel;
    public sealed class HiddenRoots : CardModel;
    public sealed class Honing : CardModel;
    public sealed class LostLute : CardModel;
    public sealed class Lullaby : CardModel;
    public sealed class Motif : CardModel;
    public sealed class MythicForm : CardModel;
    public sealed class Quiver : CardModel;
    public sealed class SerratedArrows : CardModel;
    public sealed class Simpleton : CardModel;
    public sealed class SongPath : CardModel;
    public sealed class Tune : CardModel;
    public sealed class ViolentChord : CardModel;
    public sealed class Yah : CardModel;
}
