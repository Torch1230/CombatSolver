"""Convex-objective, sparsity and publication contracts; not battle-quality proof."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

try:
    import numpy as np
    from scipy import sparse
    from ranking_data import EDGE_DTYPE
    from sparse_fit import fit, kkt_residual, objective, prepare, score_raw
except ImportError:
    np = None


@unittest.skipIf(np is None, "Optional NumPy/SciPy ranking environment is absent")
class SparseContracts(unittest.TestCase):
    def make_export(self, directory):
        matrix = np.array([[1., 7., 0.], [0., 7., 0.], [2., 11., 0.], [1., 11., 0.]], dtype="<f4")
        edges = np.array([(0, 1, 1.), (2, 3, 1.)], dtype=EDGE_DTYPE)
        for name, values in (("matrix.f32", matrix), ("edges.pairs", edges), ("margins.f64", np.zeros(4, dtype="<f8"))):
            values.tofile(directory / name)
        foundation = dict(Schema=10, FeatureNames=["signal", "constant-per-root", "absent"],
            GameMvid="00000000-0000-0000-0000-000000000001", LinearWeights=[0., 0., 0.],
            Forest=[dict(Feature=-1, Threshold=0, Mean=0, Left=None, Right=None)])
        head = dict(character="", foundation=foundation, rows=4, pairs=2, participatingRoots=2,
            matrix="matrix.f32", edges="edges.pairs", margins="margins.f64",
            sha256={p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in directory.iterdir()})
        manifest = dict(exportSchema=1, partition="shared", heads=[head],
            matrixFormat="row-major-little-endian-float32-explicit-zero",
            edgeFormat="little-endian-int32-int32-float64", marginFormat="little-endian-float64")
        (directory / "manifest.json").write_text(json.dumps(manifest))
        return matrix, edges

    def test_split_gradient_matches_finite_differences(self):
        delta = sparse.csr_matrix([[1., -2.], [0., 3.], [-1., .5]])
        weight = np.array([.2, .3, .5])
        point = np.array([.1, .7, .4, .2])
        args = (delta, weight, .03, .02)
        _, gradient = objective(point, *args)
        for i in range(len(point)):
            shift = np.zeros_like(point); shift[i] = 1e-6
            numeric = (objective(point + shift, *args)[0] - objective(point - shift, *args)[0]) / 2e-6
            self.assertAlmostEqual(gradient[i], numeric, places=8)

    def test_pair_scaling_and_root_constant_columns(self):
        matrix = np.array([[100., 7., 0.], [20., 7., 0.], [30., 11., 0.], [10., 11., 0.]], dtype=np.float32)
        edges = np.array([(0, 1, .5), (2, 3, .5)], dtype=EDGE_DTYPE)
        delta, weight, active, scale = prepare(matrix, edges)
        np.testing.assert_array_equal(active, [True, False, False])
        self.assertAlmostEqual(scale[0], np.sqrt((80**2 + 20**2) / 2))
        coefficients = np.array([.7])
        original = np.zeros(3); original[active] = coefficients / scale
        scores = score_raw(matrix, original)
        np.testing.assert_allclose(delta @ coefficients, scores[edges["preferred"]] - scores[edges["other"]])
        repeated = np.repeat(edges, 3); repeated["weight"] /= 3
        more, weights, more_active, more_scale = prepare(matrix, repeated)
        np.testing.assert_array_equal(more_active, active)
        np.testing.assert_allclose(more_scale, scale)
        a = objective(np.array([.7, 0.]), delta, weight, .01, .001)
        b = objective(np.array([.7, 0.]), more, weights, .01, .001)
        self.assertAlmostEqual(a[0], b[0]); np.testing.assert_allclose(a[1], b[1])

    def test_analytic_optimum_and_exact_l1_zero(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary); matrix, _ = self.make_export(directory)
            fit(directory, directory / "fit", 30, l1=.1, l2=0)
            model = json.loads((directory / "fit/model.json").read_text())
            self.assertAlmostEqual(model["LinearWeights"][0], np.log(9), places=5)
            self.assertEqual(model["LinearWeights"][1:], [0., 0.])
            self.assertNotIn("FactorWeights", model)
            metrics = json.loads((directory / "fit/metrics.json").read_text())["heads"][0]
            self.assertTrue(metrics["converged"])
            self.assertLess(metrics["kktResidual"], 1e-6)
            np.testing.assert_array_equal(score_raw(matrix, np.array(model["LinearWeights"])),
                json.loads((directory / "fit/parity-expected.json").read_text()))
            fit(directory, directory / "zero", 30, l1=.6, l2=0)
            zero = json.loads((directory / "zero/model.json").read_text())
            self.assertEqual(zero["LinearWeights"], [0., 0., 0.])

    def test_kkt_residual_covers_zero_and_signed_coordinates(self):
        delta = sparse.csr_matrix([[1.]])
        weight = np.array([1.])
        self.assertEqual(kkt_residual(np.array([0.]), delta, weight, .6, 0), 0)
        self.assertAlmostEqual(kkt_residual(np.array([0.]), delta, weight, .1, 0), .4)
        self.assertLess(kkt_residual(np.array([np.log(9)]), delta, weight, .1, 0), 1e-15)
        self.assertLess(kkt_residual(np.array([-np.log(9)]), -delta, weight, .1, 0), 1e-15)

    def test_stop_corrupt_input_and_existing_output_never_publish(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary); self.make_export(directory)
            stop = directory / "STOP"; stop.touch()
            with self.assertRaises(TimeoutError): fit(directory, directory / "stopped", 30, stop)
            self.assertFalse((directory / "stopped/model.json").exists())
            with self.assertRaises(ValueError): fit(directory, directory / "stopped", 30)
            (directory / "matrix.f32").write_bytes(b"invalid")
            with self.assertRaises(ValueError): fit(directory, directory / "corrupt", 30)
            self.assertFalse((directory / "corrupt/model.json").exists())

    def test_invalid_regularization_fails_before_creating_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            for l1, l2 in [(0., 0.), (-1., .1), (np.nan, .1), (np.inf, .1), (.1, -1.), (.1, np.nan), (.1, np.inf)]:
                with self.assertRaises(ValueError): fit(directory, directory / "fit", l1=l1, l2=l2)
            self.assertFalse((directory / "fit").exists())


if __name__ == "__main__":
    unittest.main()
