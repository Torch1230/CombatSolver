# Player death cleanup checks

Run `python3 tools/PlayerDeathChecks/run.py` with Python 3 and .NET 9. Generated source and output remain under `.local/player-death-checks`; no game process is launched.

The runner extracts the actual `HandlePlayerDeath` and `RemovePowersAfterDeath` methods. State/model/pet-Kill doubles observe which powers are present when pet death starts. Cases cover removable and death-persistent powers, other owners, positive/negative amounts, no pet, pending pet cleanup, orb ordering, idempotence and the existing Illusion removal veto.

This verifies sequencing and the reused removal policy, not real simulation/Fork, native AfterRemoved callbacks, death prevention or complete combat equivalence. An old damage source may be selected with `--damage-source /path/to/old.cs` to reproduce the baseline failure.
