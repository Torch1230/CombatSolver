# Monster state fingerprint checks

Loads the selected production DLL and the real game assembly. Calls the private monster-state
fingerprint appender and compares both hash words with the original stable
`OrderBy(CombatId).ThenBy(Name, Ordinal)` field sequence, including an existing hash prefix.
Covers null/empty maps, singleton nullable and boundary IDs, negative values, tied sort keys on
distinct creatures, Unicode names, deterministic random inputs, and parent/child mutation and clear.
The injected known-enemy list is empty; root materialization is covered by the native search check,
not by this isolated contract. No game process is started.

```bash
dotnet build tools/MonsterStateFingerprintChecks -c Release
dotnet tools/MonsterStateFingerprintChecks/bin/Release/net9.0/MonsterStateFingerprintChecks.dll \
  --dll /path/to/CombatSolver.dll --game-dir /path/to/data_sts2_linuxbsd_x86_64
```

The DLL defaults to the repository's Release output; the game directory defaults to the standard
Linux Steam install. Use the real matching game directory, not publicized build references.

Add `--benchmark` for warmed calls on 0/1/4-entry maps. A dynamic delegate invokes the production
method with a stack-local fingerprint builder; reflection and boxing occur only during setup.
It reports current-thread bytes/call and ns/call for 500,000 calls, consuming the hash word.
Run baseline and candidate in separate processes in a fixed ABBA order (optionally with the same
`DOTNET_TieredCompilation=0`); these isolated numbers do not establish whole-search speedups.
