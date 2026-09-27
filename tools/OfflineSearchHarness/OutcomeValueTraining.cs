using System.Diagnostics;
using System.Text.Json;
using CombatSolver;

namespace OfflineSearchHarness;

internal static class OutcomeValueTraining
{
    internal static int Run(string pathsFile, string output)
    {
        Stopwatch clock = Stopwatch.StartNew();
        string[] files = JsonSerializer.Deserialize<string[]>(File.ReadAllText(pathsFile))!;
        List<SearchOutcomeValueModel.TrainingRow> rows = [];
        Random sampler = new(0);
        foreach (string file in files)
        {
            var source = JsonSerializer.Deserialize<SearchOutcomeValueModel.TrainingRow[]>(File.ReadAllText(file))!;
            // Root-balanced cap; sampling never consults old Score or held-out runs.
            sampler.Shuffle(source);
            rows.AddRange(source.Take(1024));
        }
        SearchOutcomeValueModel model = new();
        if (!model.Fit(rows)) throw new InvalidOperationException("Insufficient witnessed outcomes.");
        File.WriteAllText(output, JsonSerializer.Serialize(model.ExportModel()));
        Console.WriteLine(JsonSerializer.Serialize(new { roots = files.Length, rows = rows.Count,
            features = model.ExportModel().FeatureNames.Length,
            fitMilliseconds = clock.Elapsed.TotalMilliseconds, bytes = new FileInfo(output).Length,
            peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 }));
        return 0;
    }
}
