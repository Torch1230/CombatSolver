namespace MegaCrit.Sts2.Core.Models
{
    public abstract class CardModel
    {
        public bool IsUpgraded { get; init; }
    }
}

namespace MegaCrit.Sts2.Core.Models.Cards
{
    using MegaCrit.Sts2.Core.Models;

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
}
