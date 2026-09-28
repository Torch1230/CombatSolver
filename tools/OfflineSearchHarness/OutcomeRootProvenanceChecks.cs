using System.Security.Cryptography;
using System.Text.Json;
using CombatSolver;
using Model = CombatSolver.SearchOutcomeValueModel;

namespace OfflineSearchHarness;

internal static class OutcomeRootProvenanceChecks
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        string directory = Path.Combine(Path.GetTempPath(), "outcome-root-provenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string[] roles = ["B", "A", "A", "B"];
            string[] roots = Enumerable.Range(0, roles.Length).Select(i => Path.Combine(directory, i + ".json")).ToArray();
            for (int root = 0; root < roots.Length; root++)
            {
                var rows = Enumerable.Range(0, 3).Select(i => new Model.TrainingRow(
                    new() { ["x"] = i, ["character/" + roles[root]] = 1 },
                    new SolverInterimResult(root != 1, 0, i, i, 0, 0, 0, 0, 2) { Survives = root != 1 },
                    3, [0], CompletedDefeat: root == 1, FeatureSchema: Model.FeatureSchema)).ToArray();
                File.WriteAllText(roots[root], JsonSerializer.Serialize(rows));
            }
            string inputs = Path.Combine(directory, "inputs.json");
            string plain = Path.Combine(directory, "plain"), mapped = Path.Combine(directory, "mapped");
            File.WriteAllText(inputs, JsonSerializer.Serialize(new { schemaVersion = 1, maximumRowsPerRoot = 64,
                partition = "character", roots }));
            OutcomeValueTraining.Export(inputs, plain);
            check(!File.Exists(Path.Combine(plain, "row-roots.json"))
                && !Directory.EnumerateFiles(plain, "*.roots.i32").Any(),
                "legacy export creates no optional root sidecars");
            File.WriteAllText(inputs, JsonSerializer.Serialize(new { schemaVersion = 1, maximumRowsPerRoot = 64,
                partition = "character", roots,
                exportRowRoots = true }));
            OutcomeValueTraining.Export(inputs, mapped);
            using var before = JsonDocument.Parse(File.ReadAllText(Path.Combine(plain, "manifest.json")));
            using var after = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapped, "manifest.json")));
            check(before.RootElement.GetProperty("heads").GetRawText() == after.RootElement.GetProperty("heads").GetRawText(),
                "enabling provenance keeps all original head metadata and foundations identical");
            foreach (var head in before.RootElement.GetProperty("heads").EnumerateArray())
                foreach (string key in new[] { "matrix", "edges", "margins" })
                {
                    string file = head.GetProperty(key).GetString()!;
                    check(File.ReadAllBytes(Path.Combine(plain, file)).SequenceEqual(File.ReadAllBytes(Path.Combine(mapped, file))),
                        "provenance preserves exact original " + key + " bytes");
                }
            using var sidecar = JsonDocument.Parse(File.ReadAllText(Path.Combine(mapped, "row-roots.json")));
            var value = sidecar.RootElement;
            string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            check(value.GetProperty("schema").GetInt32() == 1 && value.GetProperty("totalRoots").GetInt32() == 4
                && value.GetProperty("manifestSha256").GetString() == Digest(Path.Combine(mapped, "manifest.json")),
                "root sidecar binds the exact exported graph manifest");
            foreach (var head in value.GetProperty("heads").EnumerateArray())
            {
                string file = Path.Combine(mapped, head.GetProperty("assignments").GetString()!);
                using var reader = new BinaryReader(File.OpenRead(file));
                int count = head.GetProperty("rows").GetInt32();
                int[] assignments = Enumerable.Range(0, count).Select(_ => reader.ReadInt32()).ToArray();
                check(reader.BaseStream.Position == reader.BaseStream.Length
                    && head.GetProperty("sha256").GetString() == Digest(file),
                    "root assignment bytes have exact row length and digest");
                check(head.GetProperty("character").GetString() == "A"
                    ? assignments.SequenceEqual([2, 2, 2])
                    : assignments.SequenceEqual([0, 0, 0, 3, 3, 3]),
                    "role grouping and unpaired-root removal preserve global input root identities");
            }
            File.WriteAllText(inputs, JsonSerializer.Serialize(new { schemaVersion = 1, maximumRowsPerRoot = 64,
                partition = "character", roots,
                exportRowRoots = "yes" }));
            reject(() => OutcomeValueTraining.Export(inputs, Path.Combine(directory, "invalid")),
                "root provenance opt-in rejects non-boolean values");
        }
        finally { Directory.Delete(directory, true); }
    }
}
