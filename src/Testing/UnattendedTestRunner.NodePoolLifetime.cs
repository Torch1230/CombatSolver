using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Pooling;

namespace CombatSolver;

internal sealed partial class UnattendedTestRunner
{
    private void AssertNodePoolLifetime()
    {
        Type tracker = typeof(Node).Assembly.GetType("Godot.DisposablesTracker", throwOnError: true)!;
        var registry = (ConcurrentDictionary<WeakReference<IDisposable>, byte>)tracker
            .GetProperty("OtherInstances", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (Type poolType in new[] { typeof(NodePool<NCard>), typeof(NodePool<NGridCardHolder>) })
        {
            MethodInfo method = poolType.GetMethod("DisconnectIncomingAndOutgoingSignals", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object pool = RuntimeHelpers.GetUninitializedObject(poolType);
            Node root = new(), child = new(), external = new(), outsideTree = new();
            NGame.Instance!.AddChild(root);
            root.AddChild(child);
            NGame.Instance.AddChild(external);
            try
            {
                int calls = 0;
                Callable callback = Callable.From(() => calls++);
                Callable incoming = new(child, Node.MethodName.NotifyPropertyListChanged);
                Callable ignored = new(outsideTree, Node.MethodName.NotifyPropertyListChanged);
                method.Invoke(pool, [root]);
                int before = registry.Count;
                for (int i = 0; i < 200; i++)
                {
                    root.Connect(Node.SignalName.TreeExiting, callback);
                    child.Connect(Node.SignalName.TreeExiting, callback);
                    external.Connect(Node.SignalName.TreeExiting, incoming);
                    root.Connect(Node.SignalName.TreeExiting, ignored);
                    method.Invoke(pool, [root]);
                    if (root.IsConnected(Node.SignalName.TreeExiting, callback)
                        || child.IsConnected(Node.SignalName.TreeExiting, callback)
                        || external.IsConnected(Node.SignalName.TreeExiting, incoming)
                        || !root.IsConnected(Node.SignalName.TreeExiting, ignored))
                        throw new InvalidOperationException("Node pool outgoing/incoming/recursive/off-tree signal behavior changed.");
                    root.Disconnect(Node.SignalName.TreeExiting, ignored);
                    root.EmitSignal(Node.SignalName.TreeExiting);
                    child.EmitSignal(Node.SignalName.TreeExiting);
                }
                int growth = registry.Count - before;
                if (calls != 0 || growth > 0)
                    throw new InvalidOperationException($"Node pool signal cleanup retained wrappers: pool={poolType.Name} growth={growth} calls={calls}.");
                _completedChecks.Add($"NodePoolLifetime:{poolType.GenericTypeArguments[0].Name}:200Reuses:IncomingOutgoingRecursiveOffTree:RegistryGrowth={growth}");
            }
            finally { root.Free(); external.Free(); outsideTree.Free(); }
        }
    }
}
