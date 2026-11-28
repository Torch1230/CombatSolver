using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace OfflineSearchHarness;

/// <summary>
/// 运行时程序集解析。模块初始化器在 Main 之前跑，所以任何引用 sts2 / CombatSolver /
/// RitsuLib 的方法被 JIT 之前解析器已经装好了（探针 A 的做法，只是提前到模块初始化）。
/// </summary>
internal static class AssemblyBootstrap
{
    public static string Sts2DataDir { get; private set; } = string.Empty;
    public static string RitsuLibDir { get; private set; } = string.Empty;
    public static string RitsuLibCompatDir { get; private set; } = string.Empty;
    public static string RitsuLibSharedDir { get; private set; } = string.Empty;
    public static string CombatSolverDll { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // 支持无 Steam/无仓库路径的便携集群部署：环境变量优先覆盖 runtimeconfig.json 固化路径。
        Sts2DataDir = ResolveConfiguredPath("OFFLINE_HARNESS_STS2_DATA_DIR", "Sts2DataDir");
        RitsuLibDir = ResolveConfiguredPath("OFFLINE_HARNESS_RITSU_LIB_DIR", "RitsuLibDir");
        RitsuLibCompatDir = ResolveConfiguredPath("OFFLINE_HARNESS_RITSU_COMPAT_DIR", "RitsuLibCompatDir");
        RitsuLibSharedDir = ResolveConfiguredPath("OFFLINE_HARNESS_RITSU_SHARED_DIR", "RitsuLibSharedDir");
        // 批量运行器要能按 plan 换掉模组产物（对照不同 DLL 的游戏内跑法），所以环境变量优先。
        CombatSolverDll = ResolveConfiguredPath("OFFLINE_HARNESS_COMBATSOLVER_DLL", "CombatSolverDll");

        AssemblyLoadContext.Default.Resolving += Resolve;
    }

    private static string ResolveConfiguredPath(string envVarName, string appContextKey)
        => Environment.GetEnvironmentVariable(envVarName) is { Length: > 0 } overridden
            ? overridden
            : (string?)AppContext.GetData(appContextKey) ?? string.Empty;

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        foreach (string candidate in Candidates(name.Name))
        {
            if (File.Exists(candidate))
                return context.LoadFromAssemblyPath(Path.GetFullPath(candidate));
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string? simpleName)
    {
        if (string.IsNullOrEmpty(simpleName))
            yield break;
        if (simpleName == "CombatSolver" && CombatSolverDll.Length > 0)
            yield return CombatSolverDll;
        if (RitsuLibCompatDir.Length > 0)
            yield return Path.Combine(RitsuLibCompatDir, simpleName + ".dll");
        if (RitsuLibSharedDir.Length > 0)
            yield return Path.Combine(RitsuLibSharedDir, simpleName + ".dll");
        if (RitsuLibDir.Length > 0)
            yield return Path.Combine(RitsuLibDir, simpleName + ".dll");
        if (Sts2DataDir.Length > 0)
            yield return Path.Combine(Sts2DataDir, simpleName + ".dll");
        if (AppContext.BaseDirectory is { Length: > 0 } baseDir)
            yield return Path.Combine(baseDir, simpleName + ".dll");
    }
}
