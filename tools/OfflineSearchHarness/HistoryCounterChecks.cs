using System.Diagnostics;
using System.Text.Json;
using CombatSolver;
using CombatSolver.Engine.Common;
using CombatSolver.Engine.InCombat.Simulation;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Orbs;

namespace OfflineSearchHarness;

internal static class HistoryCounterChecks
{
    internal static void Run(CombatState combat, string output)
    {
        var root = CombatRootSnapshot.Capture(combat);
        var owner = combat.Players.Single();
        var sim = root.ForkSimulator();
        var history = sim.History;
        var card = sim.State.GetPlayerCombatState(owner).Hand.Cards.First();
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++;
        }
        void Equal(CombatPredictionHistory h)
        {
            var scan = CombatHistoryCounters.Scan(h, owner);
            Check(scan == h.GetCounters(owner), "scan equality");
            StateFingerprintBuilder a = new(), b = new();
            CombatHistoryCounterKey.AppendCounters(ref a, scan);
            CombatHistoryCounterKey.AppendCounters(ref b, h.GetCounters(owner));
            Check(a.Finish() == b.Finish(), "key bits");
        }
        CardPlay Play(bool auto = false) => new()
        {
            Card = card.MutablePreview, Player = owner, Target = null,
            ResultPile = PileType.Discard, Resources = default, IsAutoPlay = auto, PlayIndex = 0, PlayCount = 1,
        };
        Equal(history);
        var initial = history.GetCounters(owner);
        using (sim.PushActionSource(card.Original, PredictionActionKind.CardPlay))
        {
            int start = history.PrepareManualCardChoice();
            var play = Play();
            history.CardPlayStarted(card, play);
            Check(history.GetCounters(owner) == initial, "start is not finish");
            var draw = history.CardDrawn(card, true);
            var generated = history.CardGenerated(card, owner, CardGenerationResultKind.Fixed);
            Equal(history);
            var suspended = history.ForkExecutionContinuation(new PredictionTrace(), new PredictionForkContext(), start);
            Equal(suspended);
            // The resumed copy owns its pending entries. Completing them does not add
            // a second draw/generation count, and a duplicate completion still fails.
            var copiedDraw = (CombatPredictionCardDrawnEntry)suspended[draw.Index];
            var copiedGenerated = (CombatPredictionCardGeneratedEntry)suspended[generated.Index];
            var beforeResolve = suspended.GetCounters(owner);
            suspended.CardDrawResolved(copiedDraw, card);
            suspended.CardGenerationResolved(copiedGenerated, card);
            Check(beforeResolve == suspended.GetCounters(owner), "resume counts once");
            try { suspended.CardDrawResolved(copiedDraw, card); throw new Exception("duplicate accepted"); }
            catch (InvalidOperationException) { Check(beforeResolve == suspended.GetCounters(owner), "failed resolve unchanged"); }
            history.CardDrawResolved(draw, card);
            history.CardGenerationResolved(generated, card);
            var context = new PredictionForkContext();
            context.Register(play, Play());
            var manual = history.ForkManualCardChoice(new PredictionTrace(), context, start);
            Equal(manual);
            var nested = Play(true);
            history.CardPlayStarted(card, nested);
            history.CardPlayFinished(card, nested, true);
            history.CardPlayFinished(card, play, false);
            history.OrbChanneled(sim.State.GetPlayerCombatState(owner).OrbQueue.Orbs.OfType<LightningOrb>().First());
            history.DamageReceived(owner.Creature, null, new DamageResult(owner.Creature, default) { UnblockedDamage = 2 }, null, CombatDamageSource.Unknown);
            history.DamageReceived(owner.Creature, null, new DamageResult(owner.Creature, default) { UnblockedDamage = 0 }, null, CombatDamageSource.Unknown);
        }
        Equal(history);
        var counts = history.GetCounters(owner);
        Check(counts == new CombatHistoryCounters(initial.FinishedPlays + 2, initial.EtherealPlays + 1,
            initial.LightningChannels + 1, initial.UnblockedHitsReceived + 1, initial.CardsDrawn + 1, initial.CardsGenerated + 1), "six event totals");
        var child = sim.Fork();
        Equal(child.History);
        using (child.PushActionSource(card.Original, PredictionActionKind.CardPlay))
            child.History.CardPlayFinished(card, Play(), false);
        Equal(child.History);
        Check(history.GetCounters(owner) == counts, "parent unchanged");
        Equal(root.ForkSimulator().History);

        // Same captured history, same builder and output consumption, alternate arms
        // after warmup. This measures the history suffix only, not entire searches.
        using (sim.PushActionSource(card.Original, PredictionActionKind.CardPlay))
            for (int i = 0; i < 512; i++) history.CardPlayFinished(card, Play(), false);
        StateFingerprint checksum = default;
        double Measure(bool scan, int iterations)
        {
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                StateFingerprintBuilder key = new();
                CombatHistoryCounterKey.AppendCounters(ref key, scan ? CombatHistoryCounters.Scan(history, owner) : history.GetCounters(owner));
                checksum = key.Finish();
            }
            return watch.Elapsed.TotalMilliseconds;
        }
        Measure(true, 1000); Measure(false, 1000);
        List<object> timings = [];
        for (int repeat = 0; repeat < 6; repeat++)
        {
            bool scanFirst = repeat % 2 == 0;
            double first = Measure(scanFirst, 20000), second = Measure(!scanFirst, 20000);
            timings.Add(new { repeat, scanMs = scanFirst ? first : second, incrementalMs = scanFirst ? second : first });
        }
        File.WriteAllText(Path.Combine(output, "history-checks.json"), JsonSerializer.Serialize(new
        { checks, status = "Passed", historyEvents = history.Entries.Count, iterations = 20000, timings, checksum = checksum.ToString() }));
        Console.WriteLine($"HISTORY_COUNTER_CHECKS Passed checks={checks}");
    }
}
