using System.Reflection.Metadata;
using Sts2Sim.Bundle;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sts2SimBridgeStager;

internal static class Program
{
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static int Main(string[] args)
    {
        if (args.Length == 2 && string.Equals(args[0], "--clean", StringComparison.Ordinal))
        {
            string cleanDir = Path.GetFullPath(args[1]);
            if (Directory.Exists(cleanDir))
                Directory.Delete(cleanDir, recursive: true);
            return 0;
        }

        if (args.Length is 6 or 7 && string.Equals(args[0], "--stage", StringComparison.Ordinal))
        {
            string sourceDir = Path.GetFullPath(args[1]);
            string targetDir = Path.GetFullPath(args[2]);
            if (!int.TryParse(args[3], out int abiVersion) || abiVersion <= 0)
            {
                Console.Error.WriteLine($"Invalid abiVersion: '{args[3]}'.");
                return 2;
            }
            string supportedGameVersion = args[4];
            string supportedGameCommit = args[5];
            if (string.IsNullOrWhiteSpace(supportedGameVersion) || string.IsNullOrWhiteSpace(supportedGameCommit))
            {
                Console.Error.WriteLine("supportedGameVersion and supportedGameCommit must be non-empty.");
                return 2;
            }

            return StageBundle(sourceDir, targetDir, abiVersion, supportedGameVersion, supportedGameCommit,
                args.Length == 7 ? args[6] : null);
        }

        Console.Error.WriteLine(
            "Usage: Sts2SimBridgeStager --clean <targetDir> | --stage <sourceDir> <targetDir> <abiVersion> <gameVersion> <gameCommit> [expectedSimulatorCommit]");
        return 2;
    }

    private static int StageBundle(
        string sourceDir,
        string targetDir,
        int abiVersion,
        string supportedGameVersion,
        string supportedGameCommit,
        string? expectedSimulatorCommit)
    {
        if (HasReparsePointInHierarchy(sourceDir, out string? sourceReparse))
        {
            Console.Error.WriteLine(
                $"Refusing to stage Sts2Sim bridge from path containing a reparse point or symlink: '{sourceReparse}'.");
            return 2;
        }
        if (HasReparsePointInHierarchy(targetDir, out string? targetReparse))
        {
            Console.Error.WriteLine(
                $"Refusing to stage Sts2Sim bridge into path containing a reparse point or symlink: '{targetReparse}'.");
            return 2;
        }

        string canonicalSource = ResolvePhysicalPath(sourceDir);
        string canonicalTarget = ResolvePhysicalPath(targetDir);
        if (PathsOverlap(sourceDir, targetDir) || PathsOverlap(canonicalSource, canonicalTarget))
        {
            Console.Error.WriteLine(
                $"Refusing to stage Sts2Sim bridge because source '{sourceDir}' ('{canonicalSource}') and target '{targetDir}' ('{canonicalTarget}') overlap.");
            return 2;
        }

        if (!Directory.Exists(sourceDir))
        {
            Console.Error.WriteLine($"Sts2Sim bridge source directory does not exist: {sourceDir}");
            return 1;
        }

        foreach (string required in RequiredFiles)
        {
            string candidate = Path.Combine(sourceDir, required);
            if (!File.Exists(candidate))
            {
                Console.Error.WriteLine($"Required Sts2Sim bridge artifact missing: {candidate}");
                return 1;
            }
            if (IsReparsePoint(candidate))
            {
                Console.Error.WriteLine($"Required Sts2Sim bridge artifact cannot be a reparse point or symlink: {candidate}");
                return 2;
            }
        }
        foreach (string optional in OptionalFiles)
        {
            string candidate = Path.Combine(sourceDir, optional);
            if (File.Exists(candidate) && IsReparsePoint(candidate))
            {
                Console.Error.WriteLine($"Optional Sts2Sim bridge artifact cannot be a reparse point or symlink: {candidate}");
                return 2;
            }
        }

        string? parentDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(targetDir));
        if (string.IsNullOrWhiteSpace(parentDir))
        {
            Console.Error.WriteLine($"Target bridge directory must have a parent directory: {targetDir}");
            return 2;
        }
        Directory.CreateDirectory(parentDir);

        string stagingDir = Path.Combine(parentDir, $".sts2sim-stage-{Guid.NewGuid():N}");
        string backupDir = Path.Combine(parentDir, $".sts2sim-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            var filesToStage = new List<string>(RequiredFiles);
            foreach (string optional in OptionalFiles)
            {
                if (File.Exists(Path.Combine(sourceDir, optional)))
                    filesToStage.Add(optional);
            }

            var manifestFiles = new List<ManifestFileEntry>(filesToStage.Count);
            foreach (string fileName in filesToStage)
            {
                string src = Path.Combine(sourceDir, fileName);
                string dst = Path.Combine(stagingDir, fileName);
                File.Copy(src, dst, overwrite: true);

                string sha256;
                using (FileStream hashStream = File.OpenRead(dst))
                    sha256 = Convert.ToHexString(SHA256.HashData(hashStream));

                string? mvid = null;
                if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    using FileStream peStream = File.OpenRead(dst);
                    using var peReader = new PEReader(peStream);
                    MetadataReader metadataReader = peReader.GetMetadataReader();
                    Guid mvidGuid = metadataReader.GetGuid(metadataReader.GetModuleDefinition().Mvid);
                    mvid = mvidGuid.ToString("D");
                }

                manifestFiles.Add(new ManifestFileEntry(fileName, sha256, mvid));
            }

            string simulatorCommit;
            try
            {
                simulatorCommit = SimulatorBuildIdentity.ReadCommit(Path.Combine(stagingDir, "Sts2Sim.Core.dll"));
            }
            catch (InvalidDataException error)
            {
                Console.Error.WriteLine(error.Message);
                return 2;
            }
            if (expectedSimulatorCommit != null && !string.Equals(expectedSimulatorCommit, simulatorCommit, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Core revision '{simulatorCommit}' does not match expected simulator revision '{expectedSimulatorCommit}'.");
                return 2;
            }
            var manifest = new BundleManifest(
                abiVersion,
                supportedGameVersion,
                supportedGameCommit,
                simulatorCommit,
                manifestFiles);
            string manifestJson = JsonSerializer.Serialize(manifest, JsonOptions);
            string manifestPath = Path.Combine(stagingDir, "sts2sim-bundle.manifest.json");
            File.WriteAllText(manifestPath, manifestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            bool backedUpExisting = false;
            if (Directory.Exists(targetDir))
            {
                Directory.Move(targetDir, backupDir);
                backedUpExisting = true;
            }
            try
            {
                Directory.Move(stagingDir, targetDir);
            }
            catch
            {
                if (backedUpExisting && !Directory.Exists(targetDir) && Directory.Exists(backupDir))
                    Directory.Move(backupDir, targetDir);
                throw;
            }

            if (backedUpExisting && Directory.Exists(backupDir))
                Directory.Delete(backupDir, recursive: true);

            return 0;
        }
        finally
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return false;
        FileAttributes attrs = File.GetAttributes(path);
        return (attrs & FileAttributes.ReparsePoint) != 0;
    }

    private static bool HasReparsePointInHierarchy(string path, out string? reparsePath)
    {
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        while (!string.IsNullOrEmpty(current))
        {
            if (IsReparsePoint(current))
            {
                reparsePath = current;
                return true;
            }
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                break;
            current = parent;
        }
        reparsePath = null;
        return false;
    }

    private static string ResolvePhysicalPath(string path)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var trailingSegments = new Stack<string>();
        string current = fullPath;

        while (!Directory.Exists(current) && !File.Exists(current))
        {
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                return fullPath;
            trailingSegments.Push(Path.GetFileName(current));
            current = parent;
        }

        string physical;
        string? currentParent = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(currentParent) || string.Equals(currentParent, current, StringComparison.Ordinal))
        {
            physical = current;
        }
        else
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            FileSystemInfo? resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            physical = Path.TrimEndingDirectorySeparator(resolved?.FullName ?? info.FullName);

            // Iteratively resolve in case parent directories of the link target also contain symlinks.
            string? resolvedParent = Path.GetDirectoryName(physical);
            if (!string.IsNullOrEmpty(resolvedParent) && !string.Equals(resolvedParent, physical, StringComparison.Ordinal))
            {
                physical = Path.Combine(ResolvePhysicalPath(resolvedParent), Path.GetFileName(physical));
            }
        }

        while (trailingSegments.Count > 0)
            physical = Path.Combine(physical, trailingSegments.Pop());
        return physical;
    }

    private static bool PathsOverlap(string sourceDir, string targetDir)
    {
        StringComparison comparison = global::System.OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string sourcePrefix = Path.GetFullPath(sourceDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string targetPrefix = Path.GetFullPath(targetDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return string.Equals(sourcePrefix, targetPrefix, comparison)
            || targetPrefix.StartsWith(sourcePrefix, comparison)
            || sourcePrefix.StartsWith(targetPrefix, comparison);
    }

    private sealed record BundleManifest(
        [property: JsonPropertyName("abiVersion")] int AbiVersion,
        [property: JsonPropertyName("supportedGameVersion")] string SupportedGameVersion,
        [property: JsonPropertyName("supportedGameCommit")] string SupportedGameCommit,
        [property: JsonPropertyName("simulatorCommit")] string SimulatorCommit,
        [property: JsonPropertyName("files")] IReadOnlyList<ManifestFileEntry> Files);

    private sealed record ManifestFileEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("mvid")] string? Mvid);
}
