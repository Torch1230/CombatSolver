using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using STS2RitsuLib.Patching.Models;
using SignalDictionary = Godot.Collections.Dictionary;

namespace CombatSolver;

// All NodePool<T> reference-type instantiations share this body. Its signal traversal
// owns the returned arrays, dictionaries, variants and marshalled names, but never the nodes.
internal sealed class NodePoolSignalLifetimePatch : IPatchMethod
{
    private static readonly Variant NameKey = Variant.CreateFrom("name");
    private static readonly Variant CallableKey = Variant.CreateFrom("callable");
    private static readonly Variant SignalKey = Variant.CreateFrom("signal");
    public static string PatchId => "combat_solver_node_pool_signal_lifetime";
    public static string Description => "释放对象池信号清理的临时包装";
    public static ModPatchTarget[] GetTargets() =>
        [new(typeof(NodePool<NCard>), "DisconnectIncomingAndOutgoingSignals", [typeof(Node)])];

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        foreach (string method in new[] { nameof(Node.GetSignalList), nameof(Node.GetSignalConnectionList), nameof(Node.GetIncomingConnections) })
            if (code.Count(i => i.operand is MethodInfo m && m.Name == method) != 1)
                throw new InvalidOperationException("Node pool signal traversal changed: " + method);
        return [new(OpCodes.Ldarg_1), new(OpCodes.Call, AccessTools.Method(typeof(NodePoolSignalLifetimePatch), nameof(Disconnect))), new(OpCodes.Ret)];
    }

    internal static void Disconnect(Node node)
    {
        var signals = node.GetSignalList();
        using ((Godot.Collections.Array)signals)
        {
            foreach (SignalDictionary signal in signals)
            {
                using (signal)
                using (Variant nameValue = signal[NameKey])
                using (StringName name = nameValue.AsStringName())
                    DisconnectConnections(node.GetSignalConnectionList(name));
            }
        }
        DisconnectConnections(node.GetIncomingConnections());
        for (int i = 0; i < node.GetChildCount(); i++)
            Disconnect(node.GetChild(i));
    }

    private static void DisconnectConnections(Godot.Collections.Array<SignalDictionary> connections)
    {
        using var ownedArray = (Godot.Collections.Array)connections;
        foreach (SignalDictionary connection in connections)
        {
            using (connection)
            using (Variant callableValue = connection[CallableKey])
            using (Variant signalValue = connection[SignalKey])
            {
                Callable callable = callableValue.AsCallable();
                Signal signal = signalValue.AsSignal();
                // AsCallable/AsSignal marshal new owned StringName wrappers. The delegate
                // callable has no method name; neither target nor owner belongs to this scope.
                using (callable.Method)
                using (signal.Name)
                    DisconnectSignal(callable, signal);
            }
        }
    }

    private static void DisconnectSignal(Callable callable, Signal signal)
    {
        GodotObject target = callable.Target;
        if ((target == null && callable.Method == null && callable.Delegate == null)
            || (target != null && !GodotObject.IsInstanceValid(target))) return;
        Node? targetNode = target as Node;
        if (targetNode != null && !targetNode.IsInsideTree()) return;
        GodotObject owner = signal.Owner;
        if (GodotObject.IsInstanceValid(owner))
        {
            Node? ownerNode = owner as Node;
            if (targetNode != null && targetNode.HasSignal(signal.Name) && targetNode.IsConnected(signal.Name, callable))
                targetNode.Disconnect(signal.Name, callable);
            else if (ownerNode != null && ownerNode.HasSignal(signal.Name) && ownerNode.IsConnected(signal.Name, callable))
                ownerNode.Disconnect(signal.Name, callable);
        }
    }
}
