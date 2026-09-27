using System.Reflection;
using CombatSolver;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace OfflineSearchHarness;

internal static class SharedEvidenceChecks
{
    internal static int Run()
    {
        int checks = 0;
        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Shared evidence: " + name);
            checks++;
        }
        // Scalar-only fixtures, like RankingChecks. No simulator is constructed or consumed.
        ConstructorInfo ctor = typeof(SimulationSnapshot).GetConstructors().Single();
        var snapshot = (SimulationSnapshot)ctor.Invoke(ctor.GetParameters().Select(p =>
            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray());
        SearchNode Node(ulong id, SearchNode? parent = null, string card = "A") => new(
            parent == null ? null : new PlanAction(PlanActionKind.PlayCard, 1, CardId: card),
            parent == null ? 0 : parent.ActionCount + 1, 0, 0, 1, default, 0, 100 - id,
            new(id, id + 1), false, SearchBoundaryReason.None, false, parent, snapshot, default!);
        SearchNode root = Node(1), a = Node(2, root), b = Node(3, a, "B");
        SearchNode terminal = Node(4, b) with { IsTerminal = true };
        SolverInterimResult quality = new(true, 0, 5, 5, 0, 0, 0, 99999, 3) { Survives = true };
        SharedSearchEvidence evidence = new();
        evidence.ObserveVictory(terminal, quality);
        Require(evidence.OutcomeFor(root) != null && evidence.OutcomeFor(a) != null
            && evidence.OutcomeFor(b) != null, "completed outcome backs up every ancestor");
        Require(evidence.OutcomeFor(b)!.Score == 0, "handwritten terminal score is not evidence");
        SearchNode alternative = Node(20, root, "alternative");
        evidence.ObserveVictory(Node(21, alternative) with { IsTerminal = true },
            quality with { StrategicHpDeficit = 8 });
        Require(evidence.OutcomeFor(alternative)?.StrategicHpDeficit == 8,
            "suboptimal complete branches also supply local evidence");
        var copied = b with { Score = -1e30, RetentionRank = 700 };
        Require(evidence.OutcomeFor(copied) != null, "ranking and mutable retention metadata do not split evidence");
        foreach (SearchNode changed in new[]
        {
            b with { Parent = a with { Action = a.Action! with { CardId = "different history" } } },
            b with { FutureSoldHp = 1 }, b with { PotionCount = 1 }, b with { PotionStrategicCost = 1 },
            b with { Turn = 2 }, b with { StateKey = new(333, 444) },
            b with { Action = b.Action! with { TargetCombatId = 42 } },
            b with { Action = b.Action! with { CardStateOccurrence = 2 } },
            b with { Action = b.Action! with { CardStateKey = "changed physical card" } },
            b with { Action = b.Action! with { CardEnchantmentId = "changed enchantment" } },
            b with { Action = b.Action! with { ReplayCount = 1 } },
        }) Require(evidence.OutcomeFor(changed) == null, "different state/history/policy cannot reuse outcome");
        var choice = new PlanCardChoice(PlanChoiceEffect.Discard, PileType.Hand,
            [new("C", 0, "s", 0, 0, "title")]);
        SearchNode chosen = b with { Action = b.Action! with { Choice = choice } };
        Require(SearchEvidenceKey.Capture(chosen) != SearchEvidenceKey.Capture(b), "choice identity");
        Require(SearchEvidenceKey.Capture(chosen) == SearchEvidenceKey.Capture(chosen with
        { Action = chosen.Action! with { CardTitle = "translated", Choice = choice with
            { Cards = [choice.Cards[0] with { Title = "translated" }] } } }), "display is irrelevant");
        Require(SearchEvidenceKey.Capture(b with { Parent = a with { TurnSetupChoices = [choice] } })
            != SearchEvidenceKey.Capture(b), "root preparation identity");
        SharedSearchEvidence rejected = new();
        rejected.ObserveVictory(terminal with { HasPredictionRisk = true }, quality);
        rejected.ObserveVictory(terminal with { BoundaryReason = SearchBoundaryReason.PendingChoice }, quality);
        rejected.ObserveVictory(terminal, quality with { Won = false });
        rejected.ObserveVictory(terminal, quality with { Survives = false });
        Require(rejected.OutcomeBackups == 0, "incomplete/risky/dead results never become evidence");
        Require(new SharedSearchEvidence().OutcomeFor(b) == null, "pass isolation");
        var key = SearchEvidenceKey.Capture(b);
        var probe = new CombatBeamSolver.StandPatEvaluation(false, 12, 48, 36, Reusable: true);
        evidence.StoreProbe(key, probe);
        Require(evidence.TryProbe(key, out var cached) && cached == probe, "probe scalar reuse");
        Require(!evidence.TryProbe(key with { Path = new(20, 30) }, out _), "probe history isolation");
        rejected.StoreProbe(key, probe with { Reusable = false });
        Require(!rejected.TryProbe(key, out _), "pending/risky probe not cached");
        SearchNode low = Node(10, root), high = Node(11, root), unknown1 = Node(12, root), unknown2 = Node(13, root);
        SharedSearchEvidence ranks = new();
        ranks.StoreOutcome(SearchEvidenceKey.Capture(low), quality);
        ranks.StoreOutcome(SearchEvidenceKey.Capture(high), quality with { StrategicHpDeficit = 2 });
        // Supplied scores are the frozen Beam scores, not the base node scores.
        List<(SearchNode Node, double Score)> ranked =
            [(terminal, 200), (unknown1, 100), (low, 100), (unknown2, 100), (high, 100)];
        ranks.RankTies(ranked);
        Require(ranked.Select(x => x.Node).SequenceEqual([terminal, unknown1, high, unknown2, low]),
            "real outcomes break Beam ties while unknown and terminal positions survive");
        Require(ranks.ReorderedCandidates == 2, "only observed tied candidates exchange positions");
        ranked = [(low, 101), (high, 100)]; ranks.RankTies(ranked);
        Require(ranked[0].Node == low, "witness never overrides unequal Beam scores");
        var longer = high with { ActionCount = high.ActionCount + 1 };
        ranks.StoreOutcome(SearchEvidenceKey.Capture(longer), quality with { StrategicHpDeficit = 1 });
        ranked = [(low, 100), (longer, 100)]; ranks.RankTies(ranked);
        Require(ranked[0].Node == low, "witness never overrides unequal action counts");
        SharedSearchEvidence empty = new();
        ranked = [(unknown2, 100), (unknown1, 100)]; empty.RankTies(ranked);
        Require(ranked.Select(x => x.Node).SequenceEqual([unknown2, unknown1]), "no evidence leaves ranking alone");
        for (ulong i = 0; i < SharedSearchEvidence.Capacity * 3; i++)
            evidence.StoreProbe(new(new(i, 0), new(i, 1)), probe);
        Require(evidence.Count <= SharedSearchEvidence.Capacity && evidence.Evictions > 0, "fixed memory and eviction");
        // A table collision must be a miss, never another path's successful probe.
        var latest = new SearchEvidenceKey(new(0, 0), new(123, 456));
        evidence.StoreProbe(latest, probe);
        Require(!evidence.TryProbe(latest with { Path = new(1, 2) }, out _), "replacement does not fabricate equivalence");
        Console.WriteLine($"Shared evidence: {checks} assertions passed.");
        return 0;
    }
}
