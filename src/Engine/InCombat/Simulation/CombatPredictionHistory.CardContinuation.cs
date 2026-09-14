using CombatSolver.Engine.Common;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace CombatSolver.Engine.InCombat.Simulation;

internal sealed partial class CombatPredictionHistory
{
    internal int PrepareManualCardChoice() { AssertForkable(); SealTail(); return EntryCount; }
    internal bool SupportsManualCardChoice(int start, PredictionTraceFrame action, CardPlay play)
    {
        if (_pendingDeferredEntries != 0 || (_prefix?.Count ?? 0) != start || _tail is null)
            return false;
        int starts = 0;
        foreach (var entry in _tail)
        {
            if (entry is CombatPredictionCardPlayStartedEntry started)
            {
                if (!ReferenceEquals(started.CardPlay, play) || !ReferenceEquals(entry.Trace, action)) return false;
                starts++;
            }
            else if (entry is not (CombatPredictionRiskEntry or CombatPredictionDamageReceivedEntry or CombatPredictionCreatureAttackedEntry
                or CombatPredictionCardDrawnEntry or CombatPredictionCardDrawResolvedEntry
                or CombatPredictionCardsSelectedEntry or CombatPredictionCardCostsRandomizedEntry
                or CombatPredictionCardGenerationOptionsEntry or CombatPredictionCardGeneratedEntry
                or CombatPredictionCardGenerationResolvedEntry))
                return false;
            if (entry.Trace is null) return false;
            bool foundRoot = false;
            foreach (var trace in entry.Trace.Ancestors())
            {
                // This narrow prefix permits only the played card's stable Original as a source.
                if (!ReferenceEquals(trace.Source, action.Source)) return false;
                if (ReferenceEquals(trace, action)) foundRoot = true;
            }
            if (!foundRoot) return false;
        }
        return starts == 1;
    }

    internal CombatPredictionHistory ForkManualCardChoice(PredictionTrace trace, PredictionForkContext context, int start)
    {
        AssertForkable();
        if ((_prefix?.Count ?? 0) != start || _tail is null)
            throw new InvalidOperationException("Manual card continuation history suffix changed.");
        var fork = new CombatPredictionHistory(trace, _prefix, _riskSignatureFirst, _riskSignatureSecond,
            _riskEntryCount, _cardDrawnEntryCount, _orbChanneledEntryCount, _tailCapacityHint);
        fork._tail = new(_tail.Count + 4);
        PredictionTraceFrame RemapTrace(PredictionTraceFrame source)
        {
            if (context.TryRemap(source, out PredictionTraceFrame? mapped)) return mapped!;
            var result = new PredictionTraceFrame { Source = source.Source, Invocation = source.Invocation,
                Parent = source.Parent is null ? null : RemapTrace(source.Parent) };
            context.Register(source, result);
            return result;
        }
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
        foreach (var entry in _tail)
        {
            CombatPredictionHistoryEntry copy = entry switch
            {
                CombatPredictionRiskEntry e => new CombatPredictionRiskEntry { Reason = e.Reason },
                CombatPredictionCardPlayStartedEntry e => new CombatPredictionCardPlayStartedEntry
                    { Card = e.Card, CardPlay = context.RequireRemap(e.CardPlay) },
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
                _ => throw new InvalidOperationException("Unqualified manual card continuation history entry: " + entry.GetType().Name),
            };
            copy.Index = entry.Index;
            copy.Trace = RemapTrace(entry.Trace!);
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
