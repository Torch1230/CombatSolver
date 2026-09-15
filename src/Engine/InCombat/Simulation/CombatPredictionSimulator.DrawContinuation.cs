using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Players;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionSimulator
{
    internal static List<PredictedCard> ForkExecutionCardList(List<PredictedCard> cards, PredictionForkContext context)
    {
        if (context.TryRemap(cards, out List<PredictedCard>? existing)) return existing!;
        // A completed automatic power/dupe can leave every pile while remaining in
        // an enclosing draw or auto-play list. That list still owns its wrapper.
        List<PredictedCard> result = cards.Select(card =>
            context.TryRemap(card, out PredictedCard? mapped) ? mapped! : card.Fork(context)).ToList();
        context.Register(cards, result);
        return result;
    }

    private sealed record DrawExecutionFrame(Player Player, int DrawCount, bool FromHandDraw,
        int MaxHandSize, List<PredictedCard> Drawn, int Next,
        CombatPredictionCardDrawnEntry? PendingEntry, PredictedCard? PendingCard) : ICombatPredictionExecutionFrame
    {
        public IEnumerable<CombatPredictionHistoryEntry> DeferredEntries => PendingEntry is null ? [] : [PendingEntry];
        public void PrepareFork(PredictionForkContext context) => ForkExecutionCardList(Drawn, context);
        public ICombatPredictionExecutionFrame Fork(PredictionForkContext context)
            => this with { Drawn = context.RequireRemap(Drawn),
                PendingEntry = PendingEntry is null ? null : context.RequireRemap(PendingEntry),
                PendingCard = PendingCard is null ? null : context.RequireRemap(PendingCard) };
        public bool Resume(CombatPredictionSimulator simulator)
            => simulator.ContinueDrawExecution(Player, DrawCount, FromHandDraw, MaxHandSize, Drawn, Next, PendingEntry, PendingCard);
    }
}
