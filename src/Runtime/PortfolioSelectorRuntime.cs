using System.Text.Json;

namespace CombatSolver;

internal static class PortfolioSelectorRuntime
{
    internal const string EnvironmentVariable = "COMBATSOLVER_PORTFOLIO_SELECTOR";
    private static readonly Lazy<BeamPortfolioSelector?> Model = new(Load);

    public static BeamPortfolioExperiment? Capture()
        => Model.Value is { } model ? new BeamPortfolioExperiment(model, null) : null;

    private static BeamPortfolioSelector? Load()
    {
        string? path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            if (new FileInfo(path).Length > 262144)
                throw new InvalidDataException("Portfolio selector model exceeds 256 KiB.");
            BeamPortfolioSelector model = BeamPortfolioSelector.Parse(File.ReadAllText(path));
            Entry.Logger.Info($"[CombatSolver/Test] PORTFOLIO_SELECTOR_LOADED model={model.ModelId}");
            return model;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            Entry.Logger.Warn($"[CombatSolver/Test] PORTFOLIO_SELECTOR_DISABLED reason={error.Message}");
            return null;
        }
    }
}
