import json
import unittest

try:
    import numpy as np
    import xgboost as xgb
    from root_support import prune_tree, root_participation
    from ranking_data import EDGE_DTYPE, derivatives
    from xgboost_fit import convert_tree, predict_document, training_parameters
    AVAILABLE = True
except ModuleNotFoundError as error:
    if error.name not in ('numpy', 'xgboost'):
        raise
    AVAILABLE = False


@unittest.skipUnless(AVAILABLE, 'Optional private numerical environment missing')
class RootSupportChecks(unittest.TestCase):
    @staticmethod
    def leaf(value):
        return dict(Feature=-1, Threshold=0., Mean=value, Left=None, Right=None)

    def tree(self):
        return dict(Feature=0, Threshold=.5, Mean=0., Left=self.leaf(1.), Right=self.leaf(-1.))

    def test_participation_is_scale_invariant_and_detects_dominance(self):
        roots = np.array([0, 0, 2, 7])
        for scale in [1e-100, .001, 1., 1e100]:
            support = root_participation(roots, np.array([.3, .7, 1., 1.]) * scale)
            self.assertEqual(support['distinctRoots'], 3)
            self.assertAlmostEqual(support['effectiveRoots'], 3.)
        dominated = root_participation(np.arange(3), np.array([100., 1., 1.]))
        self.assertLess(dominated['effectiveRoots'], 1.05)

    def test_more_correlated_rows_do_not_create_support(self):
        roots = np.tile(np.array([0, 1]), 1000)
        support = root_participation(roots, np.full(len(roots), 1 / 1000))
        self.assertEqual(support['distinctRoots'], 2)
        self.assertEqual(support['effectiveRoots'], 2.)
        tree, report = prune_tree(self.leaf(1.), np.ones((len(roots), 1)), roots,
                                  np.zeros(len(roots)), np.full(len(roots), 1 / 1000))
        self.assertIsNone(tree)
        self.assertFalse(report['accepted'])

    def test_small_curvature_can_retain_balanced_supported_leaves(self):
        matrix = np.array([[0]] * 3 + [[1]] * 3)
        roots = np.tile(np.arange(3), 2)
        original = self.tree()
        pruned, report = prune_tree(original, matrix, roots, np.zeros(6), np.full(6, .01))
        self.assertEqual(pruned, original)
        self.assertEqual(report['collapsedBranches'], 0)
        self.assertTrue(all(x['effectiveRoots'] == 3. and x['hessianMass'] < 3 for x in report['leaves']))

    def test_unsupported_child_collapses_to_regularized_parent(self):
        matrix = np.array([[0]] * 3 + [[1]] * 3)
        roots = np.array([0, 0, 0, 1, 2, 3])
        gradient = np.array([-.05, -.05, -.05, .01, .02, .03])
        hessian = np.full(6, .1)
        tree, report = prune_tree(self.tree(), matrix, roots, gradient, hessian)
        expected = float(np.float32(-.1 * gradient.astype(np.float32).astype(float).sum()
                                   / (hessian.astype(np.float32).astype(float).sum() + .001)))
        self.assertEqual(tree['Feature'], -1)
        self.assertAlmostEqual(.1 * tree['Mean'], expected, places=15)
        self.assertEqual(report['collapsedBranches'], 1)
        self.assertEqual(len(report['leaves']), 1)
        self.assertGreaterEqual(report['leaves'][0]['effectiveRoots'], 3. - 1e-12)

    def test_zero_hessian_and_invalid_inputs_cannot_supply_support(self):
        roots = np.arange(3)
        tree, report = prune_tree(self.leaf(1.), np.ones((3, 1)), roots, np.zeros(3), np.zeros(3))
        self.assertIsNone(tree)
        with self.assertRaises(ValueError):
            root_participation(np.array([0, 0]), np.array([1e308, 1e308]))
        for weights in [np.array([-1., 1., 1.]), np.array([float('nan'), 1., 1.])]:
            with self.assertRaises(ValueError):
                prune_tree(self.leaf(1.), np.ones((3, 1)), roots, np.zeros(3), weights)
        with self.assertRaises(ValueError):
            prune_tree(self.leaf(1.), np.ones((3, 1)), roots, np.zeros(3), np.ones(3), minimum_roots=0)
        invalid = self.tree(); invalid['Right'] = None
        with self.assertRaises(ValueError):
            prune_tree(invalid, np.ones((3, 1)), roots, np.zeros(3), np.ones(3))

    def test_conditional_reversal_with_three_roots_per_context(self):
        raw = np.array([(gate, signal) for gate in [0, 1] for _ in range(3)
                        for signal in [0, 1]], dtype=np.float32)
        matrix = np.column_stack((raw, raw[:, 0] * raw[:, 1]))
        roots = np.repeat(np.arange(6), 2)
        edges = np.array([(i + int(raw[i, 0]), i + 1 - int(raw[i, 0]), 1.)
                          for i in range(0, len(raw), 2)], dtype=EDGE_DTYPE)
        data = xgb.DMatrix(matrix, base_margin=np.zeros(len(raw)), nthread=1)
        params = dict(training_parameters(3), nthread=1, max_depth=4)
        baseline = xgb.train(params, data, num_boost_round=16,
                            obj=lambda scores, _: derivatives(scores, edges))
        np.testing.assert_array_equal(baseline.predict(data, output_margin=True), np.zeros(len(raw)))
        current = np.zeros(len(raw))
        for _ in range(16):
            gradient, hessian = derivatives(current, edges)
            data.set_base_margin(current.astype(np.float32))
            tree = xgb.train(dict(params, min_child_weight=0), data, num_boost_round=1,
                             obj=lambda scores, _: (gradient, hessian))
            imported = convert_tree(json.loads(tree.get_dump(dump_format='json')[0]), 3)
            pruned, report = prune_tree(imported, matrix, roots, gradient, hessian)
            self.assertTrue(report['accepted'])
            self.assertTrue(all(x['distinctRoots'] >= 3 and x['effectiveRoots'] >= 3. - 1e-12
                                for x in report['leaves']))
            current += predict_document(dict(Forest=[pruned], LinearWeights=[0.] * 3), matrix)
        self.assertTrue(np.all(current[edges['preferred']] > current[edges['other']]))


if __name__ == '__main__':
    unittest.main()
