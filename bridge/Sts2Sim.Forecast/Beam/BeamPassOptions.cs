using CombatSolver.Beam.Policy;

namespace CombatSolver.Beam;

internal enum BeamMemberSearchMode
{
    Default = 0,
    AggressivePowerCommitment = 1,
    BaseScoreOnly = 2,
    OffensiveRefinement = 3,
}

internal delegate void EarlyTurnScoutObserver(int completedTurns, IReadOnlyList<EarlyTurnFrontierCandidate> candidates);

internal sealed record BeamPassOptions
{
    public static BeamPassOptions Default { get; } = new();

    public BeamMemberSearchMode MemberMode { get; init; } = BeamMemberSearchMode.Default;
    public bool SecondRankBand { get; init; }
    public bool BaseScoreOnly => MemberMode == BeamMemberSearchMode.BaseScoreOnly;
    public bool AggressivePowerCommitment => MemberMode == BeamMemberSearchMode.AggressivePowerCommitment;
    public bool OffensiveRefinement => MemberMode == BeamMemberSearchMode.OffensiveRefinement;

    public int OpeningPreRootBattleHpLost { get; init; }
    public bool UseParentNodeBudget { get; init; }
    public bool UseTranspositionAdmission { get; init; } = true;
    public bool DisableTargetStop { get; init; }
    public bool AllowPolicyUnsatisfiedSelection { get; init; }
    public bool EnablePowerCommitmentSeats { get; init; } = true;

    public SearchNode? StartNodeOverride { get; init; }
    public StateEvaluator? RootBaselineEvaluator { get; init; }
    public PolicyRootOperands? RootPolicyOperands { get; init; }
    public BeamPrimarySearchIncumbent? PrimaryIncumbent { get; init; }
    public bool DisablePrimaryIncumbentPruning { get; init; }

    public int? EarlyTurnScoutDepth { get; init; }
    public EarlyTurnScoutObserver? EarlyTurnScoutObserver { get; init; }

    public SearchWorkLedger? WorkLedger { get; init; }
    public MemberBudgetController? BudgetController { get; init; }
    public ForecastLanePool? SharedLanePool { get; init; }
    public int HomeLaneOffset { get; init; }
    public int? MaxConcurrency { get; init; }
    public string ActivePhaseLabel { get; init; } = "Beam";
}
