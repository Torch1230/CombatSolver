using System.Diagnostics;

namespace Sts2Sim.Bundle;

internal static class SimulatorBuildIdentity
{
    // The .NET SDK embeds the source revision in the Core assembly's informational version.
    internal static string ReadCommit(string assemblyPath)
    {
        string? version = FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion;
        string? revision = version?.Split('+', 2).ElementAtOrDefault(1);
        if (revision?.Length != 40 || !revision.All(char.IsAsciiHexDigit))
            throw new InvalidDataException($"Simulator assembly '{Path.GetFileName(assemblyPath)}' has no Git revision in its informational version.");
        return revision;
    }
}
