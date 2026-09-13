# Generated combat scenarios

See [configuration, native launchers, reproducibility and limitations](../../docs/GENERATED_COMBAT_SCENARIOS.md).

`random.json` provides A10 starter equipment plus random cards/relics/potions against elites or bosses. `specified.json` fixes some choices and fills the rest randomly. Every selection accepts a total `count` and ordered `ids`; null entries are random, omitted count means the IDs' length.

`run.py` optionally runs a seeded suite with Python 3, using Bash on Linux and PowerShell on Windows. The native launcher can also consume a single JSON directly without Python. Each case has independent input and evidence files; the suite owns one dedicated headless instance and always stops it on completion. `--write-inputs-only` performs no game execution or model resolution.

```bash
python3 tools/GeneratedCombatScenarios/run.py --count 10 --seed BENCH-A10 --output .local/generated-suite
```

Replay an evidence directory's `generated-scenario.resolved.json` to use the exact sampled IDs and setup choices. Preserve the game/Mod version as well as the seed when comparing performance.
