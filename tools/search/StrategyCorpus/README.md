# Strategy refactor corpus

Run from the CombatSolver repository root after building the Release Mod and OfflineSearchHarness:

```powershell
python tools/search/StrategyCorpus/run.py --manifest coverage/corpora/strategy/p0.json --out .local/strategy-refactor-p0/baseline
python tools/search/StrategyCorpus/compare.py --left .local/strategy-refactor-p0/baseline --right .local/strategy-refactor-p0/after --out .local/strategy-refactor-p0/comparison
```

The runner restores each report from `combat_start` through the platform's unattended launcher and runs generated fixtures through OfflineSearchHarness. It uses a fixed VeryHigh profile with 25,000 nodes per solver, DOP 1, a 110-second search budget, and no development script or experimental early-turn search. The launcher has a 180-second outer deadline for startup and cleanup. It reuses one managed headless instance across reports and cleans it on the final report. A failed or timed-out case is recorded once; backups 56 and 63 are attempted in order only when a primary report is unavailable.

For P2-P5 after the 0.47.1 hotfix, use `coverage/corpora/strategy/p2.json`. It retains the four previously comparable player roots and both generated roots. Reports #79 and #85 reached the time boundary in P0 and remain outside exact comparison. Capture this baseline once from the merged hotfix source, then compare each structural stage against it.

`case.json` contains exact input identity, the selected route, complete choice data, quality axes, request totals and pruning counters. The source ZIP and full evidence stay under `.local`. A case is comparable only when native state and continuation restoration both pass and search elapsed time stays at least one second below the configured limit. The comparator also applies this rule to earlier captures. It rejects changed roots or policies, reports the first deterministic difference by field, and classifies the result using the frozen `SolverInterimResultOrdering.IsBetter` rules from 0.47.0. Wall time, memory and GC are observational. The fixed-budget corpus is a structural regression gate, not a claim that it reproduces the previously published 180-second search outcomes.

For P7a, `run.py --case report-24 --beam-weight CurrentEnergy:0.8` runs one controlled sensitivity probe. Repeat `--case` to select more roots. The equivalent headless and offline search arguments change one frozen intermediate Beam weight; normal requests keep the production profile. Compare a selected probe against an existing same-source baseline with `python tools/search/StrategyCorpus/sensitivity.py --baseline .local/strategy-refactor-p2/after-p2-20260928 --probe <probe-directory> --term CurrentEnergy --scale 0.8 --out <output-directory>`. This comparator permits only the declared perturbation to differ in the policy and reports route quality per root. It does not change default weights or treat elapsed time as quality.

For several already captured probes, `matrix.py --baseline <directory> --probe Term:Scale=<probe-directory> --out <new-directory>` reuses the same root and policy checks. `route_divergence.py --baseline <headless-evidence-directory> --witness <better-evidence-directory> --out <new-json>` checks the captured root and executed policy, then reports the first exact action difference, including nested choices and target identity. This is an action comparison; it does not claim where the search dropped the better path.
