#!/usr/bin/env python3
"""Check that identical boundary numbers in separate solvers stay separate."""

from argparse import Namespace
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest

from first_loss import run


class FirstLossTests(unittest.TestCase):
    def test_solver_identity_and_loss_stage(self):
        with TemporaryDirectory() as temporary:
            root = Path(temporary)
            baseline = root / "baseline"
            witness = root / "witness"
            baseline.mkdir()
            witness.mkdir()
            (witness / "ordering-selected-prefixes.json").write_text(
                json.dumps(["root", "target"]), encoding="utf-8")

            def observation(solver, boundary, stage, prefix, selected):
                return {"prefix": prefix, "observation": {
                    "solverId": solver, "boundaryId": boundary, "stage": stage,
                    "stateKey": {"first": 1, "second": 2},
                    "policyLabel": {"potionCount": 0},
                    "retention": {"rawRank": 3, "selectedIndex": selected}}}

            rows = []
            for solver, selected in (("solver-a", None), ("solver-b", 1)):
                rows.extend((
                    observation(solver, 1, "GlobalRetention", "target", selected),
                    observation(solver, 1, "RetentionPoolFinal", "alias", 0),
                    observation(solver, 2, "RetentionPoolFinal", "last", 0),
                ))
            (baseline / "ordering-observations.jsonl").write_text(
                "".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
            output = root / "loss.json"
            run(Namespace(baseline=baseline, witness=witness, out=output))
            result = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(2, len(result["exclusions"]))
            self.assertEqual({"solver-a": 2, "solver-b": 2},
                             result["lastBoundaryIgnoredBySolver"])
            self.assertEqual(
                {"solver-a": "global_retention", "solver-b": "post_global_arbitration"},
                {item["solverId"]: item["stage"] for item in result["exclusions"]})


if __name__ == "__main__":
    unittest.main()
