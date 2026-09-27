"""Numerical/format contracts, not evidence of battle decision quality."""
import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

try:
    import numpy as np
    from scipy import sparse
    from factor_fit import fit, normalize, objective, score_raw
    from ranking_data import EDGE_DTYPE, validate_foundation
except ImportError:
    np = None


@unittest.skipIf(np is None, "Optional NumPy/SciPy ranking environment is absent")
class FactorContracts(unittest.TestCase):
    def test_gradient_matches_finite_differences(self):
        x = sparse.csr_matrix([[1., 2., 0.], [-1., 0., 3.], [0., 2., 1.]])
        edges = np.array([(0, 1, .4), (2, 1, .6)], dtype=EDGE_DTYPE)
        factors = np.random.default_rng(2).normal(0, .2, size=6)
        args = (x, x.multiply(x), edges, np.array([.1, -.1, .3]), 2, .001)
        _, gradient = objective(factors, *args)
        for i in range(len(factors)):
            delta = np.zeros_like(factors); delta[i] = 1e-6
            numeric = (objective(factors + delta, *args)[0] - objective(factors - delta, *args)[0]) / 2e-6
            self.assertAlmostEqual(gradient[i], numeric, places=8)

    def test_polynomial_and_self_terms(self):
        x = np.array([[2., 3.], [2., 0.], [-2., 3.]], dtype=np.float32)
        actual = score_raw(x, np.array([[1., 2.], [3., 4.]]), [3., 4.])
        np.testing.assert_array_equal(actual, [84., 6., -60.])

    def test_normalization_and_raw_export_are_equivalent(self):
        x = np.array([[100., 1., 0.], [20., 0., 0.], [-10., 1., 0.]], dtype=np.float32)
        edges = np.array([(0, 1, .5), (2, 1, .5)], dtype=EDGE_DTYPE)
        normalized, scales = normalize(x, edges)
        self.assertGreater(scales[0], 1)
        np.testing.assert_array_equal(scales[1:], [1., 1.])
        factors = np.random.default_rng(3).normal(size=(3, 2)); factors[2] = 0
        projection = normalized @ factors
        expected = .5 * (np.sum(projection ** 2, axis=1)
                        - normalized.multiply(normalized) @ np.sum(factors ** 2, axis=1))
        np.testing.assert_allclose(score_raw(x, factors / scales[:, None], [0., 0., 0.]), expected, atol=1e-12)

    def make_export(self, directory):
        matrix = np.array([[1, 1, 0], [1, -1, 0], [-1, 1, 0], [-1, -1, 0]], dtype="<f4")
        edges = np.array([(0, 1, 1.), (3, 2, 1.)], dtype=EDGE_DTYPE)
        margins = np.zeros(4, dtype="<f8")
        for name, values in (("matrix.f32", matrix), ("edges.pairs", edges), ("margins.f64", margins)):
            values.tofile(directory / name)
        foundation = dict(Schema=9, FeatureNames=["action", "context", "unobserved"],
                          GameMvid="00000000-0000-0000-0000-000000000001", LinearWeights=[0., 0., 0.],
                          Forest=[dict(Feature=-1, Threshold=0, Mean=0, Left=None, Right=None)])
        head = dict(character="", foundation=foundation, rows=4, pairs=2, participatingRoots=2,
                    matrix="matrix.f32", edges="edges.pairs", margins="margins.f64",
                    sha256={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.iterdir()})
        manifest = dict(exportSchema=1, partition="shared", heads=[head],
                        matrixFormat="row-major-little-endian-float32-explicit-zero",
                        edgeFormat="little-endian-int32-int32-float64", marginFormat="little-endian-float64")
        (directory / "manifest.json").write_text(json.dumps(manifest))
        return matrix, foundation

    def test_complete_fit_learns_context_reversal_and_exports_unchanged_linear(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary); matrix, foundation = self.make_export(directory)
            output = directory / "fit"
            fit(directory, output, 30)
            document = json.loads((output / "model.json").read_text())
            self.assertEqual(document["Schema"], 10)
            self.assertEqual(document["LinearWeights"], foundation["LinearWeights"])
            factors = np.array(document["FactorWeights"])
            np.testing.assert_array_equal(factors[2], np.zeros(8))
            scores = score_raw(matrix, factors, document["LinearWeights"])
            self.assertGreater(scores[0], scores[1]); self.assertGreater(scores[3], scores[2])
            np.testing.assert_array_equal(scores, json.loads((output / "parity-expected.json").read_text()))
            with self.assertRaises(ValueError): fit(directory, output, 30)

    def test_stop_or_corrupt_input_never_publishes_model(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary); self.make_export(directory)
            stop = directory / "STOP"; stop.touch()
            with self.assertRaises(TimeoutError): fit(directory, directory / "stopped", 30, stop)
            self.assertFalse((directory / "stopped/model.json").exists())
            (directory / "matrix.f32").write_bytes(b"invalid")
            with self.assertRaises(ValueError): fit(directory, directory / "corrupt", 30)
            self.assertFalse((directory / "corrupt/model.json").exists())

    def test_old_or_nonlinear_foundation_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            _, foundation = self.make_export(Path(temporary))
            for schema in (9, 10): validate_foundation(dict(foundation, Schema=schema))
            for invalid in (dict(foundation, Schema=8), dict(foundation, FactorWeights=[[1.]] * 3),
                            dict(foundation, LinearWeights=[0.]), dict(foundation, LinearWeights=[0., 0., np.nan])):
                with self.assertRaises(ValueError): validate_foundation(invalid)
            tree = copy.deepcopy(foundation); tree["Forest"][0]["Mean"] = 1
            with self.assertRaises(ValueError): validate_foundation(tree)


if __name__ == "__main__":
    unittest.main()
