using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

namespace CombatSolver;

internal sealed class DevelopmentStrategyLoader : IDisposable
{
    private sealed class ScriptLoadContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IDevelopmentSearchStrategy).Assembly.GetName().Name)
                return typeof(IDevelopmentSearchStrategy).Assembly;
            string? path = _resolver.ResolveAssemblyToPath(name);
            return path == null ? null : LoadFromAssemblyPath(path);
        }
    }

    private readonly ScriptLoadContext _context;
    public DevelopmentSearchStrategy? Strategy { get; private set; }

    private DevelopmentStrategyLoader(string assemblyPath, string parametersPath, string parametersHash)
    {
        byte[] bytes = File.ReadAllBytes(parametersPath);
        string actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actualHash.Equals(parametersHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Strategy parameter hash mismatch.");
        Dictionary<string, double> values = JsonSerializer.Deserialize<Dictionary<string, double>>(bytes)
            ?? throw new InvalidDataException("Strategy parameters must be a JSON object.");
        if (values.Values.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("Strategy parameters must be finite numbers.");
        _context = new ScriptLoadContext(assemblyPath);
        try
        {
            Assembly assembly = _context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            Type[] implementations = assembly.GetExportedTypes()
                .Where(type => !type.IsAbstract && typeof(IDevelopmentSearchStrategy).IsAssignableFrom(type)).ToArray();
            if (implementations.Length != 1)
                throw new InvalidDataException("Strategy assembly must export exactly one IDevelopmentSearchStrategy implementation.");
            IDevelopmentSearchStrategy script = (IDevelopmentSearchStrategy?)Activator.CreateInstance(implementations[0])
                ?? throw new InvalidDataException("Strategy implementation requires a public parameterless constructor.");
            Strategy = new DevelopmentSearchStrategy(script, new ReadOnlyDictionary<string, double>(values));
        }
        catch
        {
            _context.Unload();
            throw;
        }
    }

    public static DevelopmentStrategyLoader Load(string assemblyPath, string parametersPath, string parametersHash)
        => new(assemblyPath, parametersPath, parametersHash);

    public void Dispose()
    {
        Strategy = null;
        _context.Unload();
    }
}
