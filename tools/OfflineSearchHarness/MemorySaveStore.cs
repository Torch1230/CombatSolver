using System.Text;
using MegaCrit.Sts2.Core.Saves;

namespace OfflineSearchHarness;

/// <summary>
/// 纯内存存档后端。游戏自带的 <c>MockGodotFileIo</c> 引用 Steamworks，加载那条路会拖进原生库；
/// 宿主只需要一个「什么都没有」的存档层，所以自己实现一份。
/// </summary>
internal sealed class MemorySaveStore : ISaveStore
{
    private sealed record Entry(byte[] Content, DateTimeOffset Modified);

    private readonly Dictionary<string, Entry> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);

    private static string Normalize(string path) => path.Replace('\\', '/');

    public string? ReadFile(string path)
        => _files.TryGetValue(Normalize(path), out Entry? entry)
            ? Encoding.UTF8.GetString(entry.Content)
            : null;

    public Task<string?> ReadFileAsync(string path) => Task.FromResult(ReadFile(path));

    public void WriteFile(string path, string content) => WriteFile(path, Encoding.UTF8.GetBytes(content));

    public void WriteFile(string path, byte[] content)
        => _files[Normalize(path)] = new Entry(content, DateTimeOffset.UtcNow);

    public Task WriteFileAsync(string path, string content)
    {
        WriteFile(path, content);
        return Task.CompletedTask;
    }

    public Task WriteFileAsync(string path, byte[] content)
    {
        WriteFile(path, content);
        return Task.CompletedTask;
    }

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public bool DirectoryExists(string path) => _directories.Contains(Normalize(path));

    public void DeleteFile(string path) => _files.Remove(Normalize(path));

    public void RenameFile(string sourcePath, string destinationPath)
    {
        string source = Normalize(sourcePath);
        if (_files.Remove(source, out Entry? entry))
            _files[Normalize(destinationPath)] = entry;
    }

    public string[] GetFilesInDirectory(string directoryPath)
    {
        string prefix = Normalize(directoryPath).TrimEnd('/') + "/";
        return _files.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal)
                && !path.AsSpan(prefix.Length).Contains('/'))
            .Select(path => path[prefix.Length..])
            .ToArray();
    }

    public string[] GetDirectoriesInDirectory(string directoryPath)
    {
        string prefix = Normalize(directoryPath).TrimEnd('/') + "/";
        return _directories
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(path => path[prefix.Length..].Split('/')[0])
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public void CreateDirectory(string directoryPath) => _directories.Add(Normalize(directoryPath).TrimEnd('/'));

    public void DeleteDirectory(string directoryPath)
    {
        string prefix = Normalize(directoryPath).TrimEnd('/');
        _directories.RemoveWhere(path => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal));
        foreach (string path in _files.Keys.Where(path =>
                     path.StartsWith(prefix + "/", StringComparison.Ordinal)).ToArray())
        {
            _files.Remove(path);
        }
    }

    public void DeleteTemporaryFiles(string directoryPath)
    {
        string prefix = Normalize(directoryPath).TrimEnd('/') + "/";
        foreach (string path in _files.Keys.Where(path =>
                     path.StartsWith(prefix, StringComparison.Ordinal)
                     && path.EndsWith(".tmp", StringComparison.Ordinal)).ToArray())
        {
            _files.Remove(path);
        }
    }

    public DateTimeOffset GetLastModifiedTime(string path)
        => _files.TryGetValue(Normalize(path), out Entry? entry)
            ? entry.Modified
            : throw new InvalidOperationException("No file at " + path + "!");

    public int GetFileSize(string path)
        => _files.TryGetValue(Normalize(path), out Entry? entry) ? entry.Content.Length : 0;

    public void SetLastModifiedTime(string path, DateTimeOffset time)
    {
        string key = Normalize(path);
        if (_files.TryGetValue(key, out Entry? entry))
            _files[key] = entry with { Modified = time };
    }

    public string GetFullPath(string filename) => "offline://" + Normalize(filename);
}
