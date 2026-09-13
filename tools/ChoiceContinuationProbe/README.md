# Choice continuation census

Research-only instrumentation; no production executor. See [findings](../../docs/performance/resumable-choice-research-20260913.md).

Use a disposable checkout of the documented source commit with local game references configured. `build.py` temporarily injects diagnostic hooks, builds with copying disabled, and restores injected source in `finally`. Do not build concurrently or use a checkout another task is editing. A forced process kill can interrupt restoration; discard/recreate that disposable checkout if interrupted. An exact source-anchor mismatch fails explicitly.

```bash
python3 tools/ChoiceContinuationProbe/build.py --source <disposable-checkout> --output <diagnostic-build>
python3 tools/ChoiceContinuationProbe/run.py --template <reviewed-command.json> --template-root <template-repository> --build <diagnostic-build> --evidence <new-evidence-directory> --instance <dedicated-instance>
python3 tools/ChoiceContinuationProbe/compare.py <baseline-details.json> <new-evidence-directory>/details.json
```

The template is an existing unattended argument array. Review its input archive, policy, budgets and paths before execution. It must include the overridden build/evidence/instance/scenario flags and a timeout no greater than120 seconds. The runner stops and starts the named dedicated headless instance, and stops it again in `finally`; never name an instance owned by another task. The runner does not change the template's search budget or policy. The archived census uses a 20000-node primary-only profile, not the complete original request.

Output contains `continuation-census.json`, complete action/result `details.json`, command and regular request evidence. `compare.py` compares all result fields except explicitly enumerated runtime/scheduling counters, normalizes physical forks by round-prefix captures, and checks complete actions and remaining result text. It is a search-result oracle, not a continuation-state equivalence test.

Counts group by actual parent-node reference plus action fingerprint with selection chains removed. Repeated counts do not prove legal prefix sharing. Initial eligibility rejects decorations and active/uninspected boundary states; read-only `AssertForkable` is invoked only for audited repository-owned implementations. Cumulative Stopwatch ticks are not wall time or CPU samples; scope allocations exclude outer Fork/snapshot and are diagnostic estimates. Both include first executions that cannot be eliminated. Tools are excluded from production compilation; local builds and full evidence are not committed.
