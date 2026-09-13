namespace CombatSolver.Engine.Common;

internal static class BaseLibCloneConcurrency
{
    private const string BaseLibClonePatchTypeName =
        "BaseLib.Utils.ICloneableField+CloneSpireFields";
    private static readonly object Gate = new();
    private static readonly Lazy<bool> BaseLibClonePatchLoaded = new(
        () => AppDomain.CurrentDomain.GetAssemblies().Any(
            assembly => assembly.GetType(BaseLibClonePatchTypeName, throwOnError: false) != null));

    public static bool IsRequired => BaseLibClonePatchLoaded.Value;

    public static bool Enter()
    {
        if (!IsRequired)
            return false;
        Monitor.Enter(Gate);
        return true;
    }

    public static void Exit(bool entered)
    {
        if (entered)
            Monitor.Exit(Gate);
    }
}
