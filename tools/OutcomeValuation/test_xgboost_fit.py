import json
import math
import unittest

try:
    import numpy as np
    import xgboost as xgb
    from xgboost_fit import EDGE_DTYPE, PARAMETERS, convert_tree, derivatives, loss, predict_document, training_parameters
    AVAILABLE = True
except ModuleNotFoundError as error:
    if error.name not in ("numpy", "xgboost"):
        raise
    AVAILABLE = False


@unittest.skipUnless(AVAILABLE, "Optional private XGBoost environment is not installed")
class RankingBackendChecks(unittest.TestCase):
    def test_gradient_and_hessian_upper_bound(self):
        edges = np.array([(0, 1, .5), (0, 2, .5), (2, 3, 1.)], dtype=EDGE_DTYPE)
        scores = np.array([.4, -.8, .6, -.2])
        gradient, diagonal = derivatives(scores, edges)
        numerical = np.empty(4)
        hessian = np.empty((4, 4))
        for i in range(4):
            plus, minus = scores.copy(), scores.copy()
            plus[i] += 1e-5
            minus[i] -= 1e-5
            numerical[i] = (loss(plus, edges) - loss(minus, edges)) / 2e-5
            hessian[:, i] = (derivatives(plus, edges)[0] - derivatives(minus, edges)[0]) / 2e-5
        np.testing.assert_allclose(gradient, numerical, atol=1e-9)
        self.assertAlmostEqual(float(gradient.sum()), 0)
        self.assertGreaterEqual(np.linalg.eigvalsh(np.diag(diagonal) - hessian).min(), -1e-9)

    def test_extreme_margin_is_finite(self):
        edges = np.array([(0, 1, 1.)], dtype=EDGE_DTYPE)
        for scores in (np.array([-1000., 1000.]), np.array([1000., -1000.])):
            g, h = derivatives(scores, edges)
            self.assertTrue(np.isfinite(g).all() and np.isfinite(h).all())
            self.assertTrue(math.isfinite(loss(scores, edges)))

    def test_strict_float_split_is_preserved(self):
        for boundary in [np.float32(0), np.float32(-0.), np.float32(1), np.float32(-1),
                         np.nextafter(np.float32(0), np.float32(1)),
                         np.finfo(np.float32).min, np.finfo(np.float32).max]:
            node = dict(nodeid=0, split="f0", split_condition=float(boundary), yes=1, no=2,
                        missing=1, children=[dict(nodeid=1, leaf=.25), dict(nodeid=2, leaf=-.5)])
            tree = convert_tree(node, 1)
            with np.errstate(over="ignore"):
                values = np.array([np.nextafter(boundary, np.float32(-np.inf)), boundary,
                                   np.nextafter(boundary, np.float32(np.inf)), 0], dtype=np.float32)
            values = values[np.isfinite(values)]
            doc = dict(Forest=[tree], LinearWeights=[0.])
            actual = predict_document(doc, values.reshape(-1, 1))
            np.testing.assert_array_equal(actual, np.where(values < boundary, .25, -.5))
            self.assertTrue(math.isfinite(tree["Threshold"]))

    def test_numeric_booster_import_and_preference_direction(self):
        matrix = np.arange(40, dtype=np.float32).reshape(-1, 1)
        edges = np.array([(i + 1, i, 1 / 39) for i in range(39)], dtype=EDGE_DTYPE)
        data = xgb.DMatrix(matrix, base_margin=np.zeros(40, dtype=np.float32), nthread=4)
        model = xgb.train(PARAMETERS, data, num_boost_round=16,
                          obj=lambda scores, _: derivatives(scores, edges))
        doc = dict(Forest=[convert_tree(json.loads(s), 1) for s in model.get_dump(dump_format="json")],
                   LinearWeights=[0.])
        predicted = predict_document(doc, matrix)
        np.testing.assert_allclose(predicted, model.predict(data, output_margin=True), atol=1e-6, rtol=1e-6)
        self.assertLess(loss(predicted, edges), loss(np.zeros(40), edges))
        self.assertGreater(predicted[-1], predicted[0])

    def test_leaf_regularization_uses_root_weight_not_repeated_rows(self):
        # 200 correlated rows from one root still supply at most one unit of
        # Hessian. A three-unit leaf cannot be fabricated by a large row count.
        edges = np.array([(i + 1, i, 1 / 199) for i in range(199)], dtype=EDGE_DTYPE)
        scores = np.zeros(200)
        self.assertAlmostEqual(float(derivatives(scores, edges)[1].sum()), 1)
        matrix = np.arange(200, dtype=np.float32).reshape(-1, 1)
        data = xgb.DMatrix(matrix, base_margin=scores.astype(np.float32), nthread=4)
        model = xgb.train(training_parameters(3), data, num_boost_round=8,
                          obj=lambda predicted, _: derivatives(predicted, edges))
        self.assertTrue(all("leaf" in json.loads(t) for t in model.get_dump(dump_format="json")))

    def test_regularized_tree_can_learn_with_sufficient_root_weight(self):
        matrix = np.tile(np.arange(40, dtype=np.float32), 12).reshape(-1, 1)
        edges = np.array([(root * 40 + i + 1, root * 40 + i, 1 / 39)
                          for root in range(12) for i in range(39)], dtype=EDGE_DTYPE)
        data = xgb.DMatrix(matrix, base_margin=np.zeros(480, dtype=np.float32), nthread=4)
        model = xgb.train(training_parameters(3), data, num_boost_round=16,
                          obj=lambda predicted, _: derivatives(predicted, edges))
        self.assertLess(loss(model.predict(data, output_margin=True), edges), loss(np.zeros(480), edges))
        self.assertTrue(any("children" in json.loads(t) for t in model.get_dump(dump_format="json")))

    def test_invalid_regularization_and_default_preservation(self):
        self.assertEqual(training_parameters(), PARAMETERS)
        for invalid in (-1, float("inf"), float("nan")):
            with self.assertRaises(ValueError):
                training_parameters(invalid)
        configured = training_parameters(3)
        self.assertEqual(configured["min_child_weight"], 3)
        self.assertEqual(PARAMETERS["min_child_weight"], 0)

    def test_missing_values_and_unsupported_split_fail(self):
        with self.assertRaises(ValueError):
            predict_document(dict(Forest=[], LinearWeights=[0.]), [[np.nan]])
        with self.assertRaises(ValueError):
            convert_tree(dict(split="category", split_condition=[1]), 1)


if __name__ == "__main__":
    unittest.main()
