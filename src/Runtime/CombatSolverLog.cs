namespace CombatSolver;

/// <summary>CombatSolver's independent, asynchronously written diagnostic log.</summary>
public sealed class CombatSolverLog
{
    internal CombatDiagnosticJournal Journal { get; }
    internal CombatSolverLog(string directory) => Journal = new(directory);
    public void Info(string message) => Write("info", message);
    public void Debug(string message) => Write("debug", message);
    public void Warn(string message) => Write("warning", message);
    public void Error(string message) => Write("error", message);
    private void Write(string level, string message)
    {
        Journal.Write(level, message);
        PerformanceRecording.Log(level, message);
    }
}
