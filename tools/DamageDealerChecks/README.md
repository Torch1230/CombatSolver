# Damage dealer liveness checks

Run `python3 tools/DamageDealerChecks/run.py` with Python 3 and .NET 9. The runner copies the production damage overloads and single-target dispatcher into `.local/damage-dealer-checks`; per-target effects and post-damage hooks are deterministic doubles. It does not start the game.

The checks cross live and branch death state for every public overload, reject live reads, and cover independent branches, revival, null damage source and empty target lists. A dead branch source must never reach per-target resolution or post-damage hooks. This verifies entry-point behavior, not native damage calculation or the full death/draw chain.

To test an older production source:

```sh
python3 tools/DamageDealerChecks/run.py --source .local/damage-dealer-baseline.cs
```
