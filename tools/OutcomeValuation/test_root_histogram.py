import math
import unittest

try:
    import numpy as np
    from root_histogram import RootHistogram, supported_columns
    from root_support import root_participation
    from root_context_round import leaf_values
    from ranking_data import EDGE_DTYPE, derivatives, loss
    AVAILABLE = True
except ModuleNotFoundError as error:
    if error.name != 'numpy':
        raise
    AVAILABLE = False


@unittest.skipUnless(AVAILABLE, 'Optional numerical environment missing')
class RootHistogramChecks(unittest.TestCase):
    def test_vector_support_matches_exact_roots_across_scales(self):
        rng = np.random.default_rng(17)
        base = rng.random((8, 20)); base[rng.random(base.shape) < .4] = 0
        base[:, 0] = 0; base[:, 1] = [1, 1, 1, 0, 0, 0, 0, 0]
        for scale in [1e-100, .01, 1., 1e100]:
            actual = supported_columns(base * scale)
            for index in range(base.shape[1]):
                support = root_participation(np.arange(8), base[:, index] * scale)
                tolerance = 64 * np.finfo(float).eps * max(1, support['distinctRoots'])
                self.assertEqual(actual[index], support['distinctRoots'] >= 3
                                 and support['effectiveRoots'] + tolerance >= 3)

    def test_unsupported_best_split_does_not_hide_supported_alternative(self):
        # Column0 isolates a single physical root with the largest gain.
        # Column1 is weaker but both children have at least three balanced roots.
        matrix = np.column_stack((np.arange(8) == 0, np.arange(8) >= 4)).astype(np.float32)
        roots = np.arange(8)
        gradient = np.array([-10., 1, 1, 1, 2, 2, 2, 2])
        hessian = np.ones(8)
        tree, report = RootHistogram(matrix, [0, 1]).fit(roots, gradient, hessian)
        self.assertEqual(tree['Feature'], 1)
        self.assertEqual(report['support']['collapsedBranches'], 0)
        self.assertTrue(all(s['effectiveRoots'] >= 3 for s in report['support']['leaves']))
        self.assertEqual(len(report['selectedSplits']), 1)

    def test_zero_cuts_and_float32_boundaries_have_identical_masks(self):
        tiny = np.nextafter(np.float32(0), np.float32(1))
        values = np.array([-2., -tiny, -0., 0., tiny, 1., np.finfo(np.float32).max], dtype=np.float32)
        histogram = RootHistogram(values[:, None], [0], products=[0])
        self.assertIn(0., histogram.cuts[0]); self.assertIn(tiny, histogram.cuts[0])
        for cut, value in enumerate(histogram.cuts[0]):
            threshold = math.nextafter(float(value), -math.inf)
            np.testing.assert_array_equal(histogram.bins[:, 0] <= cut,
                                          values.astype(float) <= threshold)

    def test_conditional_reversal_learns_with_supported_growing_splits(self):
        raw = np.array([(gate, signal) for gate in [0, 1] for _ in range(3)
                        for signal in [0, 1]], dtype=np.float32)
        matrix = np.column_stack((raw, raw[:, 0] * raw[:, 1]))
        roots = np.repeat(np.arange(6), 2)
        edges = np.array([(i + int(raw[i, 0]), i + 1 - int(raw[i, 0]), 1.)
                          for i in range(0, 12, 2)], dtype=EDGE_DTYPE)
        histogram = RootHistogram(matrix, [0, 1, 2], products=[2])
        total = np.zeros(12)
        for _ in range(16):
            current = .1 * total
            tree, report = histogram.fit(roots, *derivatives(current, edges))
            total += leaf_values(tree, matrix)
            self.assertLess(loss(.1 * total, edges), loss(current, edges))
            self.assertTrue(all(s['distinctRoots'] >= 3 and s['effectiveRoots'] >= 3 - 1e-12
                                for s in report['support']['leaves']))
        self.assertTrue(np.all(total[edges['preferred']] > total[edges['other']]))

    def test_constant_observations_and_duplicate_rows_do_not_invent_support(self):
        matrix = np.ones((6, 1), dtype=np.float32)
        histogram = RootHistogram(matrix, [0])
        tree, report = histogram.fit(np.repeat(np.arange(3), 2), np.ones(6), np.ones(6))
        self.assertEqual(tree['Feature'], -1)
        self.assertEqual(report['proposedCuts'], 0)
        expected = float(np.float32(-.1 * 6 / (6 + .001)))
        self.assertEqual(.1 * tree['Mean'], expected)
        with self.assertRaises(ValueError):
            histogram.fit(np.repeat(np.arange(2), 3), np.ones(6), np.ones(6))

    def test_invalid_data_or_deadline_fails_explicitly(self):
        for columns in [[], [0, 0], [2], list(range(193))]:
            with self.assertRaises(ValueError):
                RootHistogram(np.ones((6, 2)), columns)
        with self.assertRaises(ValueError):
            RootHistogram(np.array([[float('nan')]]), [0])
        with self.assertRaises(ValueError):
            supported_columns(np.array([[-1., 1.]]))

        def expired():
            raise TimeoutError('test deadline')

        with self.assertRaises(TimeoutError):
            RootHistogram(np.ones((6, 1)), [0], check_deadline=expired)
        histogram = RootHistogram(np.ones((6, 1)), [0])
        with self.assertRaises(TimeoutError):
            histogram.fit(np.arange(6), np.ones(6), np.ones(6), check_deadline=expired)
        with self.assertRaises(ValueError):
            histogram.fit(np.arange(6), np.ones(6), -np.ones(6))

    def test_best_root_split_matches_direct_row_enumeration(self):
        rng = np.random.default_rng(1409)
        roots = np.repeat(np.arange(12), 4)
        matrix = rng.normal(size=(48, 3)).astype(np.float32)
        gradient = rng.normal(size=48).astype(np.float32).astype(float)
        hessian = rng.uniform(.05, .2, size=48).astype(np.float32).astype(float)
        histogram = RootHistogram(matrix, [0, 1, 2])
        tree, diagnostic = histogram.fit(roots, gradient, hessian)
        parent = gradient.sum() ** 2 / (hessian.sum() + .001)
        candidates = []
        for column, cuts in zip(histogram.columns, histogram.cuts):
            for cut in cuts:
                mask = matrix[:, column] < cut
                support = [root_participation(roots[m], hessian[m]) for m in (mask, ~mask)]
                if any(s['distinctRoots'] < 3 or s['effectiveRoots'] < 3 for s in support):
                    continue
                gain = sum(gradient[m].sum() ** 2 / (hessian[m].sum() + .001)
                           for m in (mask, ~mask)) - parent
                candidates.append((float(gain), column, math.nextafter(float(cut), -math.inf)))
        expected = max(candidates, key=lambda c: c[0])
        self.assertGreater(expected[0], 0)
        self.assertEqual((tree['Feature'], tree['Threshold']), expected[1:])
        self.assertAlmostEqual(diagnostic['selectedSplits'][0]['quadraticGainProxy'], expected[0], places=12)

    def test_selected_column_indices_compile_back_to_original_observations(self):
        from context_gates import compile_products, expand_products
        raw = np.array([(gate, signal) for gate in [0, 1] for _ in range(3)
                        for signal in [0, 1]], dtype=np.float32)
        # Leave unused columns between selected observations. A histogram slot
        # must never accidentally become a raw-model feature index.
        matrix = np.zeros((12, 8), dtype=np.float32)
        matrix[:, 2] = raw[:, 0]; matrix[:, 6] = raw[:, 1]
        names = [f'unused/{i}' for i in range(8)]
        names[2] = 'relic/SYNTHETIC/present'; names[6] = 'player/block'
        expanded = expand_products(matrix, names, [(2, 6)])
        roots = np.repeat(np.arange(6), 2)
        edges = np.array([(i + int(raw[i, 0]), i + 1 - int(raw[i, 0]), 1.)
                          for i in range(0, 12, 2)], dtype=EDGE_DTYPE)
        histogram = RootHistogram(expanded, [8, 6, 2], products=[8])
        tree, _ = histogram.fit(roots, *derivatives(np.zeros(12), edges))
        self.assertEqual(tree['Feature'], 8)
        compiled = compile_products(tree, 8, [(2, 6)])
        np.testing.assert_array_equal(leaf_values(tree, expanded), leaf_values(compiled, matrix))


if __name__ == '__main__':
    unittest.main()
