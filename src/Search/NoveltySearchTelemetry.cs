namespace CombatSolver;

internal sealed record NoveltySearchImprovement(long ElapsedMs, int Expanded, int Loss, int Potions, int Turn);

internal sealed record NoveltySearchTelemetry(
    long Generated, int PeakOpen, int HorizonLeaves, int NonNovelPruned,
    int FamiliarAdmitted, int OpenDropped, int Wins, long? FirstVictoryMs,
    int? FirstVictoryExpanded, int? MinimumCumulativeLoss, int? MinimumLossExpanded,
    long? MinimumLossMs, string Stop, int NoveltyEntries, int NoveltyAtoms,
    int NoveltyPartitions, long Unary, long Binary, long Familiar,
    IReadOnlyList<NoveltySearchImprovement> Improvements);

internal sealed record NoveltyPortfolioTelemetry(
    string Stop, long ExplorationExpanded, long ExplorationElapsed,
    NoveltySearchTelemetry? ExplorationDetails, bool BaselineRan, string Selected,
    int? ExplorationLoss, bool ExplorationWon, int? BaselineLoss, bool BaselineWon,
    int RemainingNodes, int RemainingMs);
