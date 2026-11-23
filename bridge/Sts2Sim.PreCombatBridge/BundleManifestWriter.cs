using Sts2Sim.Bundle;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sts2Sim.PreCombatBridge;

public static class BundleManifestWriter
{
    private const string ManifestFileName = "sts2sim-bundle.manifest.json";

    private static readonly string[] RequiredFiles =
    [
        "Sts2Sim.PreCombatBridge.dll",
        "Sts2Sim.Solver.dll",
        "Sts2Sim.Core.dll",
        "Sts2Sim.PreCombatBridge.deps.json",
    ];

    private static readonly string[] OptionalFiles =
    [
        "System.IO.Hashing.dll",
    ];

    public static string WriteBundleManifest(string outputDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        string resolvedDir = Path.GetFullPath(outputDir);
        if (!Directory.Exists(resolvedDir))
            throw new DirectoryNotFoundException($"Bundle output directory does not exist: {resolvedDir}");

        var fileNames = new List<string>(RequiredFiles.Length + OptionalFiles.Length);
        foreach (string required in RequiredFiles)
        {
            string candidate = Path.Combine(resolvedDir, required);
            if (!File.Exists(candidate))
                throw new FileNotFoundException($"Required bridge bundle file is missing: {candidate}", candidate);
            fileNames.Add(required);
        }

        foreach (string optional in OptionalFiles)
        {
            string candidate = Path.Combine(resolvedDir, optional);
            if (File.Exists(candidate))
                fileNames.Add(optional);
        }

        var entries = new List<BridgeBundleFileManifestEntry>(fileNames.Count);
        foreach (string name in fileNames)
        {
            string path = Path.Combine(resolvedDir, name);
            byte[] bytes = File.ReadAllBytes(path);
            string sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            string? mvid = null;
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                using FileStream stream = File.OpenRead(path);
                using var peReader = new PEReader(stream);
                MetadataReader mdReader = peReader.GetMetadataReader();
                Guid mvidGuid = mdReader.GetGuid(mdReader.GetModuleDefinition().Mvid);
                mvid = mvidGuid.ToString("D");
            }
            entries.Add(new BridgeBundleFileManifestEntry(name, sha256, mvid));
        }

        var manifest = new BridgeBundleManifestDto(
            BridgeAbi.Version,
            BridgeAbi.SupportedGameVersion,
            BridgeAbi.SupportedGameCommit,
            entries,
            SimulatorBuildIdentity.ReadCommit(Path.Combine(resolvedDir, "Sts2Sim.Core.dll")));
        string manifestPath = Path.Combine(resolvedDir, ManifestFileName);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, BridgeAbi.IndentedJsonOptions));
        return manifestPath;
    }
}
