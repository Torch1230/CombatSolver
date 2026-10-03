using System.IO.Compression;
using System.Security.Cryptography;
using CombatSolver.Replay;

internal sealed record BatchInput(string Source, string? ArchivePath, string Identity, string? Error = null);

internal sealed class BatchInputs(string cacheDirectory)
{
    private long _extractedBytes;
    private readonly List<BatchInput> _inputs = [];
    private readonly HashSet<string> _identities = new(StringComparer.Ordinal);
    public int Duplicates { get; private set; }

    public IReadOnlyList<BatchInput> Discover(string path)
    {
        Directory.CreateDirectory(cacheDirectory);
        path = Path.GetFullPath(path);
        if (Directory.Exists(path)) VisitDirectory(path);
        else VisitArchive(path, path, 0);
        return _inputs;
    }

    private void VisitDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"input_symlink:{path}");
        if (Directory.Exists(Path.Combine(path, "combat-solver")) || File.Exists(Path.Combine(path, "replay", "checkpoint.json")))
        {
            string packed = Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N") + ".zip");
            using (ZipArchive output = ZipFile.Open(packed, ZipArchiveMode.Create))
            {
                foreach (string file in EnumerateFiles(path).Where(file => IsBundlePath(Path.GetRelativePath(path, file).Replace('\\', '/'))).Order(StringComparer.Ordinal))
                {
                    long length = new FileInfo(file).Length;
                    Account(length);
                    string relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                    CheckpointArchive.ValidateEntryPath(relative);
                    ZipArchiveEntry entry = output.CreateEntry(relative, CompressionLevel.Fastest);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using Stream destination = entry.Open();
                    using Stream source = File.OpenRead(file);
                    source.CopyTo(destination);
                }
            }
            VisitArchive(packed, path, 0);
            return;
        }
        foreach (string child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
        {
            if (Path.GetFullPath(child).StartsWith(Path.GetFullPath(cacheDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetFullPath(child) == Path.GetFullPath(cacheDirectory)) continue;
            if (Directory.Exists(child)) VisitDirectory(child);
            else if (Path.GetExtension(child).Equals(".zip", StringComparison.OrdinalIgnoreCase)) VisitArchive(child, child, 0);
        }
    }

    private IEnumerable<string> EnumerateFiles(string path)
    {
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"input_symlink:{child}");
            if (Directory.Exists(child))
                foreach (string file in EnumerateFiles(child)) yield return file;
            else yield return child;
        }
    }

    private void VisitArchive(string path, string source, int depth)
    {
        string identity = File.Exists(path) ? HashFile(path) : HashText(source);
        if (!_identities.Add(identity)) { Duplicates++; return; }
        try
        {
            if (depth > 4) throw new InvalidDataException("nested_archive_depth_limit");
            using ZipArchive archive = CheckpointArchive.OpenValidated(path, 1024L * 1024 * 1024, 2L * 1024 * 1024 * 1024);
            if (archive.GetEntry(CheckpointArchive.IndexPath) != null || archive.Entries.Any(entry => entry.FullName.StartsWith("combat-solver/", StringComparison.Ordinal)))
            {
                _inputs.Add(new BatchInput(source, path, identity));
                return;
            }
            bool containsNested = archive.Entries.Any(entry => entry.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            foreach (ZipArchiveEntry entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                Account(entry.Length);
                string nested = Path.Combine(cacheDirectory, HashText(identity + ":" + entry.FullName) + ".zip");
                using (Stream input = entry.Open())
                using (FileStream output = new(nested, FileMode.Create, FileAccess.Write)) input.CopyTo(output);
                VisitArchive(nested, source + "!" + entry.FullName, depth + 1);
            }
            string[] roots = archive.Entries.Select(entry =>
            {
                int index = entry.FullName.IndexOf("combat-solver/forensics/", StringComparison.Ordinal);
                if (index < 0 && entry.FullName.EndsWith("/replay/checkpoint.json", StringComparison.Ordinal))
                    index = entry.FullName.Length - "replay/checkpoint.json".Length;
                return index > 0 ? entry.FullName[..index] : null;
            }).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (string root in roots)
            {
                string packed = Path.Combine(cacheDirectory, HashText(identity + ":" + root) + ".zip");
                using (FileStream outputFile = new(packed + ".tmp", FileMode.Create, FileAccess.Write))
                using (ZipArchive output = new(outputFile, ZipArchiveMode.Create))
                    foreach (ZipArchiveEntry entry in archive.Entries.Where(entry => entry.FullName.StartsWith(root, StringComparison.Ordinal) && IsBundlePath(entry.FullName[root.Length..]) && !entry.FullName.EndsWith('/')).OrderBy(entry => entry.FullName, StringComparer.Ordinal))
                    {
                        Account(entry.Length);
                        ZipArchiveEntry copied = output.CreateEntry(entry.FullName[root.Length..], CompressionLevel.Fastest);
                        copied.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        using Stream input = entry.Open();
                        using Stream destination = copied.Open();
                        input.CopyTo(destination);
                    }
                File.Move(packed + ".tmp", packed, true);
                VisitArchive(packed, source + "!" + root, depth + 1);
            }
            if (!containsNested && roots.Length == 0)
                throw new InvalidDataException("aggregate_contains_no_checkpoint_packages");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _inputs.Add(new BatchInput(source, null, identity, error.Message));
        }
    }

    private static bool IsBundlePath(string path) => path == "report.json" || path == "README.txt"
        || path.StartsWith("replay/", StringComparison.Ordinal) || path.StartsWith("diagnostics/", StringComparison.Ordinal)
        || path.StartsWith("combat-solver/", StringComparison.Ordinal);

    private void Account(long bytes)
    {
        _extractedBytes = checked(_extractedBytes + bytes);
        if (bytes > CheckpointArchive.MaximumArchiveBytes || _extractedBytes > 2L * 1024 * 1024 * 1024)
            throw new InvalidDataException("batch_expanded_size_limit");
    }
    internal static string HashFile(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
    internal static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
