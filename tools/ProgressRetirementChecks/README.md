# ProgressRetirementChecks

This is an L0 lifecycle contract for the production `SearchInteractionState` and
`SearchProgressDisplayState`. `run.py` extracts those production types and supplies
only the smallest independent stubs for the surrounding domain types.

Run the current-source contract with:

```bash
python3 tools/ProgressRetirementChecks/run.py
```

It checks stopped-result retention and stamp expiry, takeover retirement, reset,
single selected-seed materialization, failed materialization cleanup, and a real
closure payload collected through `WeakReference` while the selected result remains.

By default `--source-ref` is still a normal contract run; a source ref must pass.
For the known pre-retirement negative check, opt in explicitly. The script then
requires the exact `worker progress retired failed` assertion, so an unrelated
compile/runtime failure is not accepted:

```bash
python3 tools/ProgressRetirementChecks/run.py --source-ref 91e12e88 --expect-failure
```

This tool does not prove native game behavior, search quality, performance, or
thread scheduling. It only checks the lifecycle ownership contract in an isolated
.NET process.
