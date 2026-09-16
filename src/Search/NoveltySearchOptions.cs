namespace CombatSolver;

// Search policy only. These facts and limits never participate in state equality.
internal sealed record NoveltySearchOptions
{
    public int Width { get; init; } = 2;
    public int FamiliarAllowance { get; init; }
    public int MaxOpen { get; init; } = 2048;
    public int MaxNoveltyEntries { get; init; } = 1_000_000;
    public int MaxActions { get; init; } = 128;
    public int MaxTurns { get; init; } = 32;

    public void Validate()
    {
        if (Width is < 1 or > 2 || FamiliarAllowance < 0 || MaxOpen < 1
            || MaxNoveltyEntries < 1 || MaxActions < 1 || MaxTurns < 1)
            throw new ArgumentOutOfRangeException(nameof(NoveltySearchOptions));
    }
}
