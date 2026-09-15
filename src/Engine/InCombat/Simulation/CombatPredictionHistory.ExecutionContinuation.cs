using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionHistory
{
    internal bool SupportsExecutionContinuation(int start, IEnumerable<CombatPredictionHistoryEntry> deferred,
        IEnumerable<CardPlay> activePlays)
    {
        if ((_prefix?.Count ?? 0) != start) return false;
        HashSet<CombatPredictionHistoryEntry> unresolved = new(ReferenceEqualityComparer.Instance);
        HashSet<CardPlay> active = new(ReferenceEqualityComparer.Instance);
        foreach (CombatPredictionHistoryEntry entry in _tail ?? [])
        {
            switch (entry)
            {
                case CombatPredictionCardPlayStartedEntry e: active.Add(e.CardPlay); break;
                case CombatPredictionCardPlayFinishedEntry e: active.Remove(e.CardPlay); break;
                case CombatPredictionCardDrawnEntry or CombatPredictionCardGeneratedEntry: unresolved.Add(entry); break;
                case CombatPredictionCardDrawResolvedEntry e: unresolved.Remove(e.OriginalEntry); break;
                case CombatPredictionCardGenerationResolvedEntry e: unresolved.Remove(e.OriginalEntry); break;
                case CombatPredictionRiskEntry or CombatPredictionDamageReceivedEntry or CombatPredictionCreatureAttackedEntry
                    or CombatPredictionCardsSelectedEntry or CombatPredictionCardCostsRandomizedEntry
                    or CombatPredictionCardGenerationOptionsEntry or CombatPredictionAutoPlayFromDrawPileEntry: break;
                default: return false;
            }
        }
        return unresolved.Count == _pendingDeferredEntries && unresolved.SetEquals(deferred)
            && active.SetEquals(activePlays);
    }

    internal CombatPredictionHistory ForkExecutionContinuation(PredictionTrace trace, PredictionForkContext context, int start)
    {
        if ((_prefix?.Count ?? 0) != start)
            throw new InvalidOperationException("Execution continuation history suffix changed.");
        var fork = new CombatPredictionHistory(trace, _prefix, _riskSignatureFirst, _riskSignatureSecond,
            _riskEntryCount, _cardDrawnEntryCount, _orbChanneledEntryCount, _tailCapacityHint)
            { _pendingDeferredEntries = _pendingDeferredEntries };
        fork._tail = new((_tail?.Count ?? 0) + 4);
        DamageResult CopyDamage(DamageResult source)
        {
            if (context.TryRemap(source, out DamageResult? found)) return found!;
            var copy = new DamageResult(source.Receiver, source.Props)
            {
                BlockedDamage = source.BlockedDamage, UnblockedDamage = source.UnblockedDamage,
                OverkillDamage = source.OverkillDamage, WasBlockBroken = source.WasBlockBroken,
                WasFullyBlocked = source.WasFullyBlocked, WasTargetKilled = source.WasTargetKilled,
            };
            context.Register(source, copy);
            return copy;
        }
        PredictedCard CopyOption(PredictedCard source)
            => context.TryRemap(source, out PredictedCard? mapped) ? mapped! : source.Fork(context);
        foreach (var entry in _tail ?? [])
        {
            CombatPredictionHistoryEntry copy = entry switch
            {
                CombatPredictionRiskEntry e => new CombatPredictionRiskEntry { Reason = e.Reason },
                CombatPredictionCardPlayStartedEntry e => new CombatPredictionCardPlayStartedEntry
                    { Card = e.Card, CardPlay = context.RemapOrSelf(e.CardPlay) },
                CombatPredictionCardPlayFinishedEntry e => new CombatPredictionCardPlayFinishedEntry
                    { Card = e.Card, CardPlay = context.RemapOrSelf(e.CardPlay), WasEthereal = e.WasEthereal },
                CombatPredictionAutoPlayFromDrawPileEntry e => new CombatPredictionAutoPlayFromDrawPileEntry { Card = e.Card },
                CombatPredictionDamageReceivedEntry e => new CombatPredictionDamageReceivedEntry
                    { Receiver = e.Receiver, Dealer = e.Dealer, Result = CopyDamage(e.Result), CardSource = e.CardSource, Source = e.Source },
                CombatPredictionCreatureAttackedEntry e => new CombatPredictionCreatureAttackedEntry
                    { Attacker = e.Attacker, HitResults = e.HitResults.Select(CopyDamage).ToArray() },
                CombatPredictionCardDrawnEntry e => new CombatPredictionCardDrawnEntry
                    { Card = e.Card, FromHandDraw = e.FromHandDraw },
                CombatPredictionCardDrawResolvedEntry e => new CombatPredictionCardDrawResolvedEntry
                    { Card = e.Card, OriginalEntry = context.RequireRemap(e.OriginalEntry) },
                CombatPredictionCardsSelectedEntry e => new CombatPredictionCardsSelectedEntry { Cards = e.Cards },
                CombatPredictionCardCostsRandomizedEntry e => new CombatPredictionCardCostsRandomizedEntry { Cards = e.Cards },
                CombatPredictionCardGenerationOptionsEntry e => new CombatPredictionCardGenerationOptionsEntry
                    { Cards = e.Cards, Options = e.Options.Select(CopyOption).ToArray() },
                CombatPredictionCardGeneratedEntry e => new CombatPredictionCardGeneratedEntry
                    { Card = e.Card, Creator = e.Creator, ResultKind = e.ResultKind },
                CombatPredictionCardGenerationResolvedEntry e => new CombatPredictionCardGenerationResolvedEntry
                    { Card = e.Card, OriginalEntry = context.RequireRemap(e.OriginalEntry) },
                _ => throw new InvalidOperationException("Unqualified execution continuation history entry: " + entry.GetType().Name),
            };
            copy.Index = entry.Index;
            copy.Trace = CombatPredictionSimulator.ForkExecutionTrace(entry.Trace, context);
            context.Register(entry, copy);
            fork._tail.Add(copy);
        }
        if (_tailCompletions != null)
        {
            fork._tailCompletions = new(ReferenceEqualityComparer.Instance);
            foreach (var (original, resolved) in _tailCompletions)
                fork._tailCompletions.Add(context.RequireRemap(original), context.RequireRemap(resolved));
        }
        return fork;
    }
}
