namespace CombatSolver;

internal sealed class SearchDiagnosticsSink(
    Action<string> info,
    Action<string> debug)
{
    public void Info(string message) => info(message);

    public void Debug(string message) => debug(message);
}
