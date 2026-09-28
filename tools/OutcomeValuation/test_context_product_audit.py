import itertools
import hashlib
import io
import json
from contextlib import redirect_stdout
from pathlib import Path
import tempfile
import unittest

try:
    import numpy as np
    from context_product_audit import hessian_bounds
    AVAILABLE = True
except ModuleNotFoundError as error:
    if error.name not in ('numpy', 'xgboost'):
        raise
    AVAILABLE = False


@unittest.skipUnless(AVAILABLE, 'Optional private numerical environment missing')
class ContextFeasibilityChecks(unittest.TestCase):
    def test_positive_negative_and_zero_are_separate_bounds(self):
        columns = np.array([[0., 0.], [1., 0.], [2., 0.], [-1., 0.], [-2., 0.]])
        bounds = hessian_bounds(columns, np.array([100., 1., 1., 2., .5]), 3.)
        self.assertEqual(bounds[0]['positiveMass'], 2.)
        self.assertEqual(bounds[0]['negativeMass'], 2.5)
        self.assertTrue(bounds[0]['blockedByHessian'])
        self.assertEqual(bounds[1]['nonzeroChildUpperMass'], 0.)
        self.assertTrue(bounds[1]['blockedByHessian'])

    def test_bound_applies_to_every_descendant_subset_and_threshold(self):
        values = np.array([-2., -1., 0., 0., 1., 2.])
        hessian = np.array([.25, .5, 4., 4., .5, .25])
        bound = hessian_bounds(values[:, None], hessian, 1.)[0]
        self.assertTrue(bound['blockedByHessian'])
        for mask in itertools.product([False, True], repeat=len(values)):
            subset = np.array(mask)
            for threshold in [-3., -2., -1., -.5, 0., .5, 1., 2., 3.]:
                left = subset & (values <= threshold)
                right = subset & (values > threshold)
                self.assertLess(min(hessian[left].sum(), hessian[right].sum()), 1.)

    def test_row_duplication_does_not_manufacture_hessian_mass(self):
        base = hessian_bounds(np.array([[1.], [0.]]), np.array([.5, .5]), 3.)[0]
        repeated = hessian_bounds(np.tile([[1.], [0.]], (1024, 1)),
                                   np.full(2048, .5 / 1024), 3.)[0]
        self.assertEqual(base['nonzeroChildUpperMass'], repeated['nonzeroChildUpperMass'])
        self.assertTrue(repeated['blockedByHessian'])

    def test_exact_cutoff_and_borderline_are_not_called_impossible(self):
        for mass in [3., 3. - 1e-7, 4.]:
            result = hessian_bounds(np.array([[1.], [0.]]), np.array([mass, 10.]), 3.)[0]
            self.assertFalse(result['blockedByHessian'])

    def test_bad_hessian_or_observation_is_rejected(self):
        for bad in [-1., float('nan'), float('inf'), 1e100]:
            with self.assertRaises(ValueError):
                hessian_bounds(np.array([[1.]]), np.array([bad]), 3.)
        with self.assertRaises(ValueError):
            hessian_bounds(np.array([[float('nan')]]), np.array([1.]), 3.)
        with self.assertRaises(ValueError):
            hessian_bounds(np.array([[1.]]), np.array([1., 2.]), 3.)

    def test_saved_round_replay_matches_actual_training_hessian(self):
        import xgboost as xgb
        from context_product_audit import audit
        from context_gates import expand_products
        from ranking_data import EDGE_DTYPE, derivatives, loss
        from xgboost_fit import training_parameters

        names = ['relic/SYNTHETIC/present', 'player/block']
        raw = np.array([(gate, signal) for gate in [0, 1] for _ in range(24)
                        for signal in [0, 1]], dtype='<f4')
        edges = np.array([(i + int(raw[i, 0]), i + 1 - int(raw[i, 0]), 1.)
                          for i in range(0, len(raw), 2)], dtype=EDGE_DTYPE)
        margins = np.asarray(raw[:, 1] * .25, dtype='<f8')
        foundation = dict(Schema=10, FeatureNames=names, LinearWeights=[0., .25],
            FactorWeights=None, Forest=[dict(Feature=-1, Threshold=0., Mean=0.)])
        params = dict(training_parameters(3), nthread=1)
        data = xgb.DMatrix(raw, base_margin=margins.astype(np.float32), nthread=1)
        prefix = xgb.train(params, data, num_boost_round=48,
                           obj=lambda scores, _: derivatives(scores, edges))
        before = prefix.predict(data, output_margin=True)
        expanded = expand_products(raw, names, [(0, 1)])
        data = xgb.DMatrix(expanded, base_margin=before, nthread=1)
        recorded = []

        def objective(scores, _):
            gradient, hessian = derivatives(scores, edges)
            recorded.append(hessian_bounds(expanded[:, 2:], hessian, 3.))
            return gradient, hessian

        suffix = xgb.train(dict(params, max_depth=4), data,
                           num_boost_round=16, obj=objective)
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            raw.tofile(directory / 'data.f32')
            edges.tofile(directory / 'data.pairs')
            margins.tofile(directory / 'data.f64')
            hashes = {name: hashlib.sha256((directory / name).read_bytes()).hexdigest()
                      for name in ['data.f32', 'data.pairs', 'data.f64']}
            head = dict(character='', foundation=foundation, rows=len(raw), pairs=len(edges),
                        participatingRoots=48, matrix='data.f32', edges='data.pairs',
                        margins='data.f64', sha256=hashes)
            manifest = dict(exportSchema=1, partition='shared', heads=[head],
                matrixFormat='row-major-little-endian-float32-explicit-zero',
                edgeFormat='little-endian-int32-int32-float64', marginFormat='little-endian-float64')
            (directory / 'manifest.json').write_text(json.dumps(manifest))
            prefix.save_model(directory / 'prefix-0.json')
            suffix.save_model(directory / 'suffix-0.json')
            metrics = dict(backend='context-gated-xgboost-cpu', parameters=params,
                ordinaryRounds=48, contextualRounds=16, heads=[dict(character='',
                    prefixLoss=loss(before, edges), productColumns=1,
                    screening=dict(selected=[dict(gate=names[0], signal=names[1])]))])
            (directory / 'metrics.json').write_text(json.dumps(metrics))
            with redirect_stdout(io.StringIO()):
                result = audit(directory, directory, directory / 'audit.json', 20)
            rounds = result['heads'][0]['rounds']
            self.assertEqual(len(rounds), 16)
            self.assertEqual([r['productBounds'] for r in rounds], recorded)
            self.assertEqual(result['newTrainingRuns'], 0)
            self.assertEqual(result['newCombatSearches'], 0)
            self.assertFalse(result['finalTestOpened'])


if __name__ == '__main__':
    unittest.main()
