# Snapshot temporary list checks

This standalone .NET 9 executable links the actual production `SnapshotListBuffer.cs`.
It checks nested rentals, exceptional population and cleanup, one idle list per owner,
checkpoint clearing, rejection by actual capacity, repeated disposal of the same or copied lease,
independent owners, and collection of released elements while the cache remains alive.

```sh
dotnet run --project tools/SnapshotListBufferChecks/SnapshotListBufferChecks.csproj -c Release
```

The buffer belongs to one `SearchRunContext` and is used only by its owning lane. It
retains at most one cleared list with capacity at most 4096. A lease is a lexical
`ref struct` local; generation checks reject access and disposal from stale copies
after storage is rented again. Raw list references must stay inside their lease.
Production uses one `using` declaration inside `Snapshot`. Checkpoint clearing
happens after workers drain.

The checks cover container ownership, not the game's snapshot scoring or shuffle
implementation. Existing Fork boundary checks compare `StableShuffleProjection` with
native stable shuffle. Candidate validation also needs fixed-work search equivalence
for snapshot scalars, ordered actions, shuffle fingerprints and RNG, including reuse
across different deck sizes. Performance comparison must use ordinary search mode.
