using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

internal static partial class OutcomeValueTraining
{
    private static bool ReadRootExportFlag(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("exportRowRoots", out var flag))
            return false;
        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("exportRowRoots must be an explicit boolean.");
        return flag.GetBoolean();
    }

    private static object WriteRootAssignments(string directory, string stem, string character,
        SearchOutcomeValueModel.PreparedTraining prepared, int[] sourceRootIndices)
    {
        if (prepared.RowRootIndices.Count != prepared.Rows.Count
            || prepared.RowRootIndices.Any(i => (uint)i >= sourceRootIndices.Length)
            || prepared.RowRootIndices.Distinct().Count() != prepared.ParticipatingRoots
            || prepared.Pairs.Any(p => prepared.RowRootIndices[p.Preferred] != prepared.RowRootIndices[p.Other]))
            throw new InvalidDataException("Invalid compacted training root provenance.");
        string name = stem + ".roots.i32";
        using (var writer = new BinaryWriter(File.Create(Path.Combine(directory, name))))
            foreach (int localRoot in prepared.RowRootIndices)
                writer.Write(sourceRootIndices[localRoot]);
        return new { character, rootIndices = sourceRootIndices, rows = prepared.Rows.Count,
            participatingRoots = prepared.ParticipatingRoots, assignments = name,
            sha256 = Hash(Path.Combine(directory, name)) };
    }
}
