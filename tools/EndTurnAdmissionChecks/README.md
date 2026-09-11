# EndTurn admission checks

Run `python3 tools/EndTurnAdmissionChecks/run.py` from the repository. Requires Python 3 and .NET 9; does not start the game. Generated sources and build outputs stay in `.local/end-turn-admission-checks`.

The runner compiles the actual `BuildAcceptedEndTurnNodes`, raw EndTurn generation, cross-turn pruning, admission predicate, materialization loop and `OwnedExpansionBatch`. Combat simulation, cycle quality/lease issuance and transposition decisions are deterministic boundary doubles. This verifies pipeline ordering and ownership, not native combat equivalence or full search quality.

Covers valid/revoked/no parent leases, multiple EndTurn choices with at most one exit lane, terminal boundaries, pruning and transposition rejection, stand-pat publication, early iterator disposal and generation failure. The pre-fix production method fails because a pending observation reaches transposition admission.

To reproduce against an older source, export its `CombatBeamSolver.Expansion.cs` outside tracked files and run:

```sh
python3 tools/EndTurnAdmissionChecks/run.py --expansion-source .local/end-turn-admission-baseline.cs
```
