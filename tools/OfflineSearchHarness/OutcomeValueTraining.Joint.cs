using System.Diagnostics;
using System.Text.Json;
using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

internal static partial class OutcomeValueTraining
{
    internal static int ExportJoint(string pathsFile, string directory)
    {
        var clock = Stopwatch.StartNew();
        using var specification = JsonDocument.Parse(File.ReadAllText(pathsFile));
        var input = specification.RootElement;
        if (input.GetProperty("schemaVersion").GetInt32() != 1
            || input.GetProperty("trainingTarget").GetString() != "completed-outcome-and-imitation")
            throw new InvalidDataException("Expected explicit joint ranking inputs.");
        string outcomeFile = input.GetProperty("outcomeInputs").GetString()
            ?? throw new InvalidDataException("Missing outcome inputs.");
        string imitationFile = input.GetProperty("imitationInputs").GetString()
            ?? throw new InvalidDataException("Missing imitation inputs.");
        using var outcome = JsonDocument.Parse(File.ReadAllText(outcomeFile));
        using var imitation = JsonDocument.Parse(File.ReadAllText(imitationFile));
        if (ReadPolicy(outcome.RootElement) != ("character", "all", "pairs"))
            throw new InvalidDataException("Joint fitting requires character heads and unmodified outcome pairs.");
        string[] Paths(JsonDocument source) => source.RootElement.GetProperty("roots").EnumerateArray()
            .Select(e => e.ValueKind == JsonValueKind.String ? Path.GetFullPath(e.GetString()!)
                : throw new InvalidDataException("Joint fitting requires one collection per physical root.")).ToArray();
        string[] outcomePaths = Paths(outcome), imitationPaths = Paths(imitation);
        ValidateJointSources(outcomePaths, imitationPaths);
        // Reuse each established sampler exactly. The manifest's paired raw
        // files still require external native-root/provenance and split audits.
        var outcomes = ReadRoots(outcomeFile);
        var imitations = ReadImitationRoots(imitationFile);
        List<(string Id, Model.JointObservation[] Rows)> roots = [];
        for (int i = 0; i < outcomes.Count; i++)
        {
            var rows = Model.JoinTrainingRoot(outcomes[i].Rows, imitations[i].Rows);
            _ = OutcomeModelFile.CharacterOf(rows); // Reject mixed native roles.
            roots.Add((Path.GetDirectoryName(outcomePaths[i])!, rows));
        }
        return ExportRanking(roots, directory, clock, "character", "all", "pairs",
            "completed-outcome-and-imitation", Model.PrepareJointTraining);
    }

    internal static void ValidateJointSources(string[] outcomes, string[] imitations)
    {
        if (outcomes.Length == 0 || outcomes.Length != imitations.Length)
            throw new InvalidDataException("Unpaired joint ranking roots.");
        HashSet<string> directories = new(StringComparer.Ordinal);
        for (int i = 0; i < outcomes.Length; i++)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(outcomes[i]));
            if (Path.GetFileName(outcomes[i]) != "outcome-rows.json"
                || Path.GetFileName(imitations[i]) != "imitation-rows.json"
                || directory != Path.GetDirectoryName(Path.GetFullPath(imitations[i]))
                || directory == null || !directories.Add(directory))
                throw new InvalidDataException("Joint targets must come from unique, paired collection directories.");
        }
    }
}
