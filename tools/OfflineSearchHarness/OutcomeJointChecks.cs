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
    }
}
