using CombatSolver;
using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

internal static class OutcomeJointChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        SolverInterimResult quality = new(true, 0, 2, 2, 0, 0, 0, 0, 2) { Survives = true };
        Model.TrainingRow Outcome(int value, int hp, bool rare) => new(
            rare ? new() { ["x"] = value, ["rare"] = value } : new() { ["x"] = value },
            quality with { ProjectedBattleHpLost = hp, StrategicHpDeficit = hp }, 3, [0, 1],
            FeatureSchema: Model.FeatureSchema);
        Model.ImitationRow Imitation(int value, bool selected, bool rare) => new(
            rare ? new() { ["x"] = value, ["rare"] = value } : new() { ["x"] = value },
            [0, 1], selected, Model.FeatureSchema);
        Model.JointObservation[] Root(bool rare) => Model.JoinTrainingRoot(
            [Outcome(1, 2, rare), Outcome(-1, 3, rare), Outcome(-2, 4, rare) with
                { Outcome = quality with { Won = false, Survives = false }, CompletedDefeat = true }],
            [Imitation(1, true, rare), Imitation(-1, false, rare), Imitation(-2, false, rare)]);
        var first = Root(true);
        var single = Model.PrepareJointTraining([first]);
        check(single.Pairs.Count == 5 && single.PairKinds.SequenceEqual([2, 1, 0, 2]),
            "joint graph retains all outcome priorities and separately identifies imitation edges");
        check(single.ParticipatingRoots == 1 && Math.Abs(single.Pairs.Sum(p => p.Weight) - 1) < 1e-12,
            "both targets and repeated pools share one physical root weight");
        check(single.Rows.Count == 6 && single.Pairs.All(p =>
                ((Model.JointObservation)single.Rows[p.Preferred]).Source.GetType()
                == ((Model.JointObservation)single.Rows[p.Other]).Source.GetType()),
            "equal feature vectors do not join state identities or create cross-target labels");
        var graph = Model.PrepareJointTraining([first, Root(false), Root(false)]);
        check(graph.ParticipatingRoots == 3 && !graph.FeatureNames.Contains("rare"),
            "both targets cannot manufacture independent root support for a rare feature");
        check(Math.Abs(graph.Pairs.Sum(p => p.Weight) - 3) < 1e-12,
            "joint graph normalizes each physical root once");
        var corrupted = first.ToArray();
        corrupted[0] = corrupted[0] with { Groups = first[^1].Groups };
        reject(() => Model.PrepareJointTraining([corrupted]), "cross-target pool collision is rejected");
        reject(() => Model.JoinTrainingRoot([Outcome(1, 2, false)],
            [Imitation(1, true, false) with { OnWinningRoute = null }]), "invalid imitation cannot hide behind outcome labels");
        OutcomeValueTraining.ValidateJointSources(["/a/outcome-rows.json"], ["/a/imitation-rows.json"]);
        reject(() => OutcomeValueTraining.ValidateJointSources(["/a/outcome-rows.json"], ["/b/imitation-rows.json"]),
            "accidentally paired different collection directories are rejected");
        reject(() => OutcomeValueTraining.ValidateJointSources(["/a/outcome-rows.json", "/a/outcome-rows.json"],
            ["/a/imitation-rows.json", "/a/imitation-rows.json"]), "duplicate physical collection directory is rejected");

        var unchanged = Model.PrepareJointTraining([first], balanceCorrections: true);
        check(single.Pairs.SequenceEqual(unchanged.Pairs) && single.Rows.SequenceEqual(unchanged.Rows)
            && single.FeatureNames.SequenceEqual(unchanged.FeatureNames),
            "no correction edges preserve the exact original graph and weights");
        var corrected = Model.JoinTrainingRoot(
            [Outcome(1, 2, false), Outcome(-1, 3, false)],
            [Imitation(1, true, false), Imitation(-1, false, false)],
            [Outcome(-1, 2, true), Outcome(1, 3, true)]);
        var augmented = Model.PrepareJointTraining([corrected], balanceCorrections: true);
        bool IsCorrection(Model.PreparedTraining p, Model.Pair edge)
            => ((Model.JointObservation)p.Rows[edge.Preferred]).IsCorrection;
        check(augmented.Pairs.Count == 3 && augmented.Pairs.All(p =>
                ((Model.JointObservation)augmented.Rows[p.Preferred]).IsCorrection
                == ((Model.JointObservation)augmented.Rows[p.Other]).IsCorrection),
            "correction queries have distinct pools even with identical group IDs and feature vectors");
        check(Math.Abs(augmented.Pairs.Where(p => IsCorrection(augmented, p)).Sum(p => p.Weight) - .5) < 1e-12
            && Math.Abs(augmented.Pairs.Sum(p => p.Weight) - 1) < 1e-12,
            "the two available collection sources share one physical root weight");
        check(augmented.Pairs.Where(p => IsCorrection(augmented, p))
            .All(p => augmented.Rows[p.Preferred].Features["x"] == -1),
            "a correction reversal follows actual outcome labels instead of original imitation");
        var support = Model.PrepareJointTraining([corrected, Root(false), Root(false)], balanceCorrections: true);
        check(support.ParticipatingRoots == 3 && !support.FeatureNames.Contains("rare"),
            "a corrective collection cannot multiply independent feature support");
        var saturated = Model.JoinTrainingRoot(Enumerable.Range(0, 101)
            .Select(i => Outcome(i, i, false)).ToArray(), [],
            [Outcome(-1, 2, false), Outcome(1, 3, false)]);
        var reserved = Model.PrepareJointTraining([saturated], balanceCorrections: true);
        check(reserved.Pairs.Count == 4096 && reserved.Pairs.Count(p => IsCorrection(reserved, p)) == 1,
            "the corrective pair survives a saturated root without exceeding the pair budget");
        check(Math.Abs(reserved.Pairs.Sum(p => p.Weight) - 1) < 1e-12,
            "saturated correction sampling preserves total root weight");
        var invalidPool = corrected.ToArray();
        invalidPool[^1] = invalidPool[^1] with { Groups = corrected[0].Groups };
        reject(() => Model.PrepareJointTraining([invalidPool], balanceCorrections: true),
            "a cross-collection pool collision fails instead of inventing a comparison");
        reject(() => Model.JoinTrainingRoot([], [], Enumerable.Range(0, 7)
            .Select(i => Outcome(i, i, false)).ToArray()), "unbounded corrective rows are rejected");
        var defeated = new[] { Outcome(-1, 2, false), Outcome(1, 3, false) }.Select(row => row with
            { Outcome = quality with { Won = false, Survives = false }, CompletedDefeat = true }).ToArray();
        var noSignal = Model.JoinTrainingRoot([Outcome(1, 2, false), Outcome(-1, 3, false)],
            [Imitation(1, true, false), Imitation(-1, false, false)], defeated);
        var noSignalGraph = Model.PrepareJointTraining([noSignal], balanceCorrections: true);
        check(noSignalGraph.Pairs.Count == 2 && noSignalGraph.Pairs.All(p =>
                !IsCorrection(noSignalGraph, p) && p.Weight == .5),
            "all-defeat corrections supply neither false preferences nor missing training mass");

        string directory = Path.Combine(Path.GetTempPath(), "correction-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string original = Path.Combine(directory, "original.json"), additional = Path.Combine(directory, "additional.json");
            void Write(string path, string live, string continuation) => File.WriteAllText(path,
                System.Text.Json.JsonSerializer.Serialize(new { search = new
                    { rootLiveStamp = live, rootContinuationStamp = continuation } }));
            Write(original, "live-a", "full-a"); Write(additional, "live-a", "full-a");
            OutcomeValueTraining.ValidateCorrectionRoot(original, additional);
            check(true, "matching full native root evidence is accepted");
            Write(additional, "live-b", "full-a");
            reject(() => OutcomeValueTraining.ValidateCorrectionRoot(original, additional), "different live root is rejected");
            Write(additional, "live-a", "full-b");
            reject(() => OutcomeValueTraining.ValidateCorrectionRoot(original, additional), "different continuation root is rejected");
            Write(additional, "live-a", "");
            reject(() => OutcomeValueTraining.ValidateCorrectionRoot(original, additional), "missing native identity is rejected");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
