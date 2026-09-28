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
        var corrections = ReadJointCorrections(input, outcomePaths);
        bool balanceCorrections = input.TryGetProperty("balanceCorrectionSources", out var balance)
            && balance.GetBoolean();
        bool crossTurnOutcomes = input.TryGetProperty("crossTurnOutcomes", out var context)
            && context.GetBoolean();
        if (balanceCorrections && corrections == null)
            throw new InvalidDataException("Correction weighting requires explicit paired correction inputs.");
        if (crossTurnOutcomes && !balanceCorrections)
            throw new InvalidDataException("Cross-turn calibration requires source-balanced joint supervision.");
        // Reuse each established sampler exactly. The manifest's paired raw
        // files still require external native-root/provenance and split audits.
        var outcomes = ReadRoots(outcomeFile, requireTrainingTurns: crossTurnOutcomes);
        var imitations = ReadImitationRoots(imitationFile);
        List<(string Id, Model.JointObservation[] Rows)> roots = [];
        for (int i = 0; i < outcomes.Count; i++)
        {
            var rows = Model.JoinTrainingRoot(outcomes[i].Rows, imitations[i].Rows, corrections?[i]);
            _ = OutcomeModelFile.CharacterOf(rows); // Reject mixed native roles.
            roots.Add((Path.GetDirectoryName(outcomePaths[i])!, rows));
        }
        return ExportRanking(roots, directory, clock, "character", "all",
            crossTurnOutcomes ? "cross-turn-sources" : balanceCorrections ? "correction-sources" : "pairs",
            "completed-outcome-and-imitation", rows => crossTurnOutcomes
                ? Model.PrepareContextCalibratedTraining(rows) : Model.PrepareJointTraining(rows, balanceCorrections));
    }

    private static Model.TrainingRow[][]? ReadJointCorrections(JsonElement input, string[] outcomes)
    {
        if (!input.TryGetProperty("correctionInputs", out var paths)) return null;
        if (paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() != outcomes.Length)
            throw new InvalidDataException("Corrections must be aligned to every original physical root.");
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<Model.TrainingRow[]> rows = [];
        for (int i = 0; i < outcomes.Length; i++)
        {
            if (paths[i].ValueKind == JsonValueKind.Null) { rows.Add([]); continue; }
            if (paths[i].ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Expected a correction file or an explicit missing collection.");
            string path = Path.GetFullPath(paths[i].GetString()!);
            if (!seen.Add(path) || Path.GetFileName(path) != "outcome-correction-rows.json")
                throw new InvalidDataException("Correction collections must be unique detached query files.");
            ValidateCorrectionRoot(Path.Combine(Path.GetDirectoryName(outcomes[i])!, "harness-result.json"),
                Path.Combine(Path.GetDirectoryName(path)!, "harness-result.json"));
            using var stream = File.OpenRead(path);
            var source = JsonSerializer.Deserialize<Model.TrainingRow[]>(stream)
                ?? throw new InvalidDataException("Missing correction observations.");
            if (source.Length > 6) throw new InvalidDataException("Too many detached correction queries.");
            Model.ValidateTrainingRows(source);
            rows.Add(source);
        }
        return rows.ToArray();
    }

    internal static void ValidateCorrectionRoot(string originalResult, string correctionResult)
    {
        using var original = JsonDocument.Parse(File.ReadAllText(originalResult));
        using var correction = JsonDocument.Parse(File.ReadAllText(correctionResult));
        foreach (string name in new[] { "rootLiveStamp", "rootContinuationStamp" })
        {
            string Stamp(JsonDocument document)
            {
                if (!document.RootElement.TryGetProperty("search", out var search)
                    || !search.TryGetProperty(name, out var stamp) || stamp.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(stamp.GetString()))
                    throw new InvalidDataException("Missing complete native root evidence for corrections.");
                return stamp.GetString()!;
            }
            if (Stamp(original) != Stamp(correction))
                throw new InvalidDataException("Correction collection belongs to a different physical root.");
        }
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
