import hashlib
import io
import json
from contextlib import redirect_stdout
from pathlib import Path
import tempfile
import time
import unittest

try:
    import numpy as np
    from ranking_data import EDGE_DTYPE, derivatives, loss
    from root_context_fit import fit, fit_context_rounds
    from root_context_round import best_product_stump, choose_update, leaf_values, zero_leaf
    from xgboost_fit import predict_document
    AVAILABLE = True
except ModuleNotFoundError as error:
    if error.name not in ('numpy', 'xgboost'):
        raise
    AVAILABLE = False


@unittest.skipUnless(AVAILABLE, 'Optional private numerical environment missing')
class RootContextFitChecks(unittest.TestCase):
    names = ['relic/SYNTHETIC/present', 'pile/hand/type/Attack/count']

    @staticmethod
    def observations():
        raw = np.array([(gate, signal) for gate in [0, 1] for _ in range(3)
                        for signal in [0, 1]], dtype='<f4')
        edges = np.array([(i + int(raw[i, 0]), i + 1 - int(raw[i, 0]), 1.)
                          for i in range(0, len(raw), 2)], dtype=EDGE_DTYPE)
        return raw, np.repeat(np.arange(6), 2), edges

    def test_supported_stump_survives_rejected_greedy_alternative(self):
        raw, roots, edges = self.observations()
        matrix = np.column_stack((raw, raw[:, 0] * raw[:, 1]))
        gradients = derivatives(np.zeros(len(raw)), edges)
        stump, report = best_product_stump(matrix, 2, roots, *gradients, lambda: None)
        self.assertEqual(stump['Feature'], 2)
        self.assertGreater(report['quadraticGainProxy'], 0)
        self.assertTrue(all(s['effectiveRoots'] >= 3 for s in report['leaves']))
        # A rare indicator gives a greedy tree a one-root child. Collapsing
        # it must not erase the separately searched supported product split.
        rare = np.column_stack((matrix, np.arange(len(raw)) == 0)).astype(np.float32)
        greedy = dict(Feature=3, Threshold=.5, Mean=0.,
                      Left=dict(zero_leaf(), Mean=1.), Right=dict(zero_leaf(), Mean=-1.))
        tree, _, diagnostic = choose_update([('greedy', greedy), ('stump', stump)], rare,
            roots, *gradients, np.zeros(len(raw)), np.zeros(len(raw)), edges)
        self.assertEqual(diagnostic['selected'], 'stump')
        self.assertEqual(diagnostic['proposals'][0]['support']['collapsedBranches'], 1)
        self.assertEqual(tree, stump)

    def test_actual_loss_rejects_bad_proposal_and_keeps_exact_ties(self):
        matrix = np.tile(np.array([[0], [1]], dtype=np.float32), (6, 1))
        roots = np.repeat(np.arange(6), 2)
        edges = np.array([(i + 1, i, 1.) for i in range(0, 12, 2)], dtype=EDGE_DTYPE)
        gradient, hessian = derivatives(np.zeros(12), edges)
        bad = dict(Feature=0, Threshold=.5, Mean=0., Left=dict(zero_leaf(), Mean=10.),
                   Right=dict(zero_leaf(), Mean=-10.))
        tree, sums, report = choose_update([('bad', bad), ('tie', zero_leaf())], matrix, roots,
            gradient, hessian, np.zeros(12), np.zeros(12), edges)
        self.assertEqual(tree, zero_leaf())
        self.assertEqual(report['selected'], 'zero')
        self.assertEqual(report['beforeLoss'], report['afterLoss'])
        np.testing.assert_array_equal(sums, np.zeros(12))
        with self.assertRaises(ValueError):
            choose_update([], matrix, np.zeros(12, dtype=int), gradient, hessian,
                          np.zeros(12), np.zeros(12), edges)

    def test_iterative_updates_use_retained_predictions_and_compile_exactly(self):
        matrix, roots, edges = self.observations()
        foundation = dict(LinearWeights=[-.15, .2], Forest=[zero_leaf()])
        margins = predict_document(foundation, matrix)
        prefix = [dict(zero_leaf(), Mean=7.25), dict(zero_leaf(), Mean=-.25)]
        updates, predicted, reports = fit_context_rounds(matrix, self.names, [(0, 1)], roots,
            edges, margins, prefix, lambda: None, time.monotonic() + 30)
        previous = loss(predict_document(dict(foundation, Forest=prefix), matrix), edges)
        for report in reports:
            self.assertEqual(previous, report['beforeLoss'])
            self.assertLessEqual(report['afterLoss'], report['beforeLoss'])
            previous = report['afterLoss']
            self.assertTrue(all(s['distinctRoots'] >= 3 and s['effectiveRoots'] >= 3 - 1e-12
                                for s in report['support']['leaves']))
        actual = predict_document(dict(foundation, Forest=prefix + updates), matrix)
        np.testing.assert_array_equal(actual, predicted)
        self.assertTrue(np.all(actual[edges['preferred']] > actual[edges['other']]))
        self.assertGreater(sum(r['productSplits'] for r in reports), 0)

    def test_no_product_is_explicit_and_deadline_is_enforced(self):
        matrix, roots, edges = self.observations()
        gradient, hessian = derivatives(np.zeros(len(matrix)), edges)
        self.assertEqual(best_product_stump(matrix, 2, roots, gradient, hessian, lambda: None),
                         (None, None))

        def expired():
            raise TimeoutError('test deadline')

        with self.assertRaises(TimeoutError):
            fit_context_rounds(matrix, self.names, [], roots, edges, np.zeros(len(matrix)),
                               [zero_leaf()], expired, time.monotonic() - 1)

    def export(self, directory):
        raw, roots, edges = self.observations()
        raw.tofile(directory / 'data.f32')
        edges.tofile(directory / 'data.pairs')
        np.zeros(len(raw), dtype='<f8').tofile(directory / 'data.f64')
        sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
        hashes = {name: sha(directory / name) for name in ['data.f32', 'data.pairs', 'data.f64']}
        foundation = dict(Schema=10, FeatureNames=self.names,
            GameMvid='00000000-0000-0000-0000-000000000000', Forest=[zero_leaf()],
            LinearWeights=[0., 0.], FactorWeights=None)
        heads, mappings = [], []
        for index, role in enumerate(['DEFECT', 'IRONCLAD', 'NECROBINDER', 'REGENT', 'SILENT']):
            root_ids = list(range(index * 6, (index + 1) * 6))
            name = f'head-{index}.roots.i32'
            (roots + index * 6).astype('<i4').tofile(directory / name)
            head = dict(character=role, rows=len(raw), pairs=len(edges), participatingRoots=6,
                rootIndices=root_ids, foundation=foundation, matrix='data.f32', edges='data.pairs',
                margins='data.f64', sha256=hashes)
            heads.append(head)
            mappings.append(dict(character=role, rows=len(raw), participatingRoots=6,
                rootIndices=root_ids, assignments=name, sha256=sha(directory / name)))
        manifest = dict(exportSchema=1, partition='character', roots=30, heads=heads,
            matrixFormat='row-major-little-endian-float32-explicit-zero',
            edgeFormat='little-endian-int32-int32-float64', marginFormat='little-endian-float64')
        (directory / 'manifest.json').write_text(json.dumps(manifest))
        (directory / 'row-roots.json').write_text(json.dumps(dict(schema=1, totalRoots=30,
            assignmentFormat='little-endian-int32-source-root-index',
            manifestSha256=sha(directory / 'manifest.json'), heads=mappings)))
        return raw, edges

    def test_complete_fitter_preserves_five_heads_raw_schema_and_root_support(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            raw, edges = self.export(directory)
            with redirect_stdout(io.StringIO()):
                fit(directory, directory / 'fitted', 30)
            model = json.loads((directory / 'fitted/model.json').read_text())
            self.assertEqual(model['CharacterSchema'], 1)
            self.assertEqual(len(model['CharacterModels']), 5)
            for head in model['CharacterModels'].values():
                self.assertEqual(head['FeatureNames'], self.names)
                self.assertEqual(len(head['Forest']), 64)
                predicted = predict_document(head, raw)
                self.assertTrue(np.all(predicted[edges['preferred']] > predicted[edges['other']]))
            metrics = json.loads((directory / 'fitted/metrics.json').read_text())
            self.assertTrue(all(h['productSplits'] > 0 and h['exportedPredictionDrift'] == 0
                                for h in metrics['heads']))
            for head in metrics['heads']:
                self.assertEqual(len(head['rounds']), 16)
                self.assertTrue(all(r['afterLoss'] <= r['beforeLoss'] for r in head['rounds']))
            self.assertEqual(len(json.loads((directory / 'fitted/parity-inputs.json').read_text())), 60)

    def test_missing_provenance_never_publishes_a_model(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            self.export(directory)
            (directory / 'row-roots.json').unlink()
            with self.assertRaises(ValueError):
                fit(directory, directory / 'fitted', 30)
            self.assertFalse((directory / 'fitted/model.json').exists())


if __name__ == '__main__':
    unittest.main()
