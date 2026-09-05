> 本工具现验证已撤回的 listener 实验；生产源码恢复原实现。长搜分配反例及启用方式见 [实验说明](../ExperimentalListenerSlots/README.md)。

# Immutable listener snapshot checks

Run `dotnet run --project tools/HookListenerSnapshotChecks/HookListenerSnapshotChecks.csproj -c Release` from the repository root.

The harness compiles the production snapshot container directly and requires no game assemblies. It checks ordered identity preservation, unchanged snapshots/enumerators after replacement, shared no-op updates, remapping, and replacement across page boundaries. It also checks `TakeOwnership` transfer, delayed promotion on the first actual identity change, reuse of the original snapshot's pages by later siblings, and concurrent sibling replacement/remapping while an old enumerator is paused. `Capture` retains its copying contract; production rebuild callers transfer a private list and never mutate it afterward.

Pass `-- --checks-only` to omit allocation sampling. Default allocation samples compare a single sparse update against copying a flat reference array. Pass `-- --ownership-allocations` to instead compare rebuilding with `Capture` versus `TakeOwnership`, both with no following change and with two changed sibling snapshots. The latter includes initial promotion, so moving allocation into the first sibling cannot appear as a saving. These samples do not measure complete simulation or search cost.

The unattended Fork boundary suite separately exercises real card previews, enchantments, Power projections, both combat/run listener views, and parent/child/grandchild ownership in `UnattendedTestRunner.ListenerSlots.cs`.
