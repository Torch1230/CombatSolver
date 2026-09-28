import json
import hashlib
import io
import math
from contextlib import redirect_stdout
from pathlib import Path
import tempfile
import unittest

try:
    import numpy as np
    import xgboost as xgb
    from context_gates import compile_products, expand_products, select_products, signal_columns
    from xgboost_fit import convert_tree, derivatives, loss, predict_document, training_parameters
    AVAILABLE = True
except ModuleNotFoundError as error:
    if error.name not in ("numpy", "xgboost"):
        raise
    AVAILABLE = False


@unittest.skipUnless(AVAILABLE, "Optional private XGBoost environment is not installed")
class ContextGateChecks(unittest.TestCase):
    names = ["relic/SYNTHETIC/present", "pile/hand/type/Attack/count"]

    def test_unused_aggregate_can_supply_conditional_signal(self):
        document = dict(FeatureNames=self.names, LinearWeights=[0., 0.], Forest=[dict(Feature=-1)])
        self.assertEqual(signal_columns(document), [1])

    def test_product_compilation_handles_negative_and_boundary_values(self):
        signal = np.array([-10, -1, -0., 0, np.nextafter(np.float32(0), np.float32(1)),
                           1, 10, np.finfo(np.float32).max], dtype=np.float32)
        raw = np.array([(gate, value) for gate in [0, 1] for value in signal], dtype=np.float32)
        expanded = expand_products(raw, self.names, [(0, 1)])
        for boundary in [-1., -0., 0., float(signal[4]), 1., 10.]:
            source = dict(split="f2", split_condition=boundary, yes=1, no=2, missing=1,
                          children=[dict(nodeid=1, leaf=.25), dict(nodeid=2, leaf=-.5)])
            tree = convert_tree(source, 3)
            compiled = compile_products(tree, 2, [(0, 1)])
            expected = predict_document(dict(Forest=[tree], LinearWeights=[0.] * 3), expanded)
            actual = predict_document(dict(Forest=[compiled], LinearWeights=[0.] * 2), raw)
            np.testing.assert_array_equal(actual, expected)

    def test_binary_presence_is_required(self):
        for value in [-1, .5, 2, math.nan, math.inf]:
            with self.assertRaises(ValueError):
                expand_products(np.array([[value, 3.]]), self.names, [(0, 1)])
        with self.assertRaises(ValueError):
            expand_products(np.array([[1, 3.]]), ["player/energy", self.names[1]], [(0, 1)])
        with self.assertRaises(ValueError):
            expand_products(np.array([[1, 3.]]), self.names, [(0, 1), (0, 1)])

    def test_repeated_gates_fit_existing_depth_and_original_feature_domain(self):
        leaf = dict(Feature=-1, Threshold=0, Mean=1., Left=None, Right=None)
        tree = leaf
        for depth in range(4):
            tree = dict(Feature=2, Threshold=depth - 1, Mean=0, Left=tree, Right=leaf)
        compiled = compile_products(tree, 2, [(0, 1)])

        def inspect(node, depth=0):
            self.assertLessEqual(depth, 8)
            self.assertLess(node['Feature'], 2)
            if node['Feature'] >= 0:
                inspect(node['Left'], depth + 1)
                inspect(node['Right'], depth + 1)

        inspect(compiled)
        raw = np.array([(gate, signal) for gate in [0, 1] for signal in range(-3, 5)], dtype=np.float32)
        augmented = expand_products(raw, self.names, [(0, 1)])
        np.testing.assert_array_equal(
            predict_document(dict(Forest=[tree], LinearWeights=[0.] * 3), augmented),
            predict_document(dict(Forest=[compiled], LinearWeights=[0.] * 2), raw))
        deeper = dict(Feature=2, Threshold=0, Mean=0, Left=tree, Right=leaf)
        with self.assertRaises(ValueError):
            compile_products(deeper, 2, [(0, 1)])

    def test_balanced_conditional_reversal_is_learnable_after_expansion(self):
        # Independent synthetic roots, each with two observations. Preferences
        # reverse with a root-constant context. Each root has total weight one.
        # This is a model-capacity check, not a simulated combat or extra data.
        from ranking_data import EDGE_DTYPE
        raw = np.array([(gate, signal) for gate in [0, 1] for _ in range(24)
                        for signal in [0, 1]], dtype=np.float32)
        edges = np.array([(i + int(raw[i, 0]), i + 1 - int(raw[i, 0]), 1.)
                          for i in range(0, len(raw), 2)], dtype=EDGE_DTYPE)
        zeros = np.zeros(len(raw), dtype=np.float32)
        params = dict(training_parameters(3), nthread=1, max_depth=4)
        original_data = xgb.DMatrix(raw, base_margin=zeros, nthread=1)
        ordinary = xgb.train(params, original_data, num_boost_round=16,
                             obj=lambda scores, _: derivatives(scores, edges))
        before = ordinary.predict(original_data, output_margin=True)
        np.testing.assert_array_equal(before, zeros)
        products, selection = select_products(raw, self.names, [1], *derivatives(before, edges))
        self.assertEqual(products, [(0, 1)])
        self.assertEqual(selection['positiveCandidates'], 1)
        expanded = expand_products(raw, self.names, products)
        data = xgb.DMatrix(expanded, base_margin=zeros, nthread=1)
        booster = xgb.train(params, data, num_boost_round=16,
                            obj=lambda scores, _: derivatives(scores, edges))
        trees = [compile_products(convert_tree(json.loads(t), 3), 2, products)
                 for t in booster.get_dump(dump_format="json")]
        actual = predict_document(dict(Forest=trees, LinearWeights=[0., 0.]), raw)
        np.testing.assert_allclose(actual, booster.predict(data, output_margin=True), atol=1e-6)
        self.assertTrue(np.all(actual[edges['preferred']] > actual[edges['other']]))
        self.assertLess(loss(actual, edges), loss(before, edges))

    def test_complete_fitter_preserves_five_heads_and_raw_feature_schema(self):
        from context_gate_fit import fit
        from ranking_data import EDGE_DTYPE
        raw = np.array([(gate, signal) for gate in [0, 1] for _ in range(24)
                        for signal in [0, 1]], dtype='<f4')
        edges = np.array([(i + int(raw[i, 0]), i + 1 - int(raw[i, 0]), 1.)
                          for i in range(0, len(raw), 2)], dtype=EDGE_DTYPE)
        margins = np.zeros(len(raw), dtype='<f8')
        foundation = dict(Schema=10, FeatureNames=self.names,
            GameMvid='00000000-0000-0000-0000-000000000000',
            Forest=[dict(Feature=-1, Threshold=0, Mean=0, Left=None, Right=None)],
            LinearWeights=[0., 0.], FactorWeights=None)
        roles = ['DEFECT', 'IRONCLAD', 'NECROBINDER', 'REGENT', 'SILENT']
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            raw.tofile(directory / 'data.f32')
            edges.tofile(directory / 'data.pairs')
            margins.tofile(directory / 'data.f64')
            hashes = {name: hashlib.sha256((directory / name).read_bytes()).hexdigest()
                      for name in ['data.f32', 'data.pairs', 'data.f64']}
            heads = [dict(character=role, rows=len(raw), pairs=len(edges), participatingRoots=48,
                          foundation=foundation, matrix='data.f32', edges='data.pairs',
                          margins='data.f64', sha256=hashes) for role in roles]
            manifest = dict(exportSchema=1, partition='character', heads=heads,
                matrixFormat='row-major-little-endian-float32-explicit-zero',
                edgeFormat='little-endian-int32-int32-float64', marginFormat='little-endian-float64')
            (directory / 'manifest.json').write_text(json.dumps(manifest))
            with redirect_stdout(io.StringIO()):
                fit(directory, directory / 'fitted', 30)
            model = json.loads((directory / 'fitted/model.json').read_text())
            self.assertEqual(model['CharacterSchema'], 1)
            self.assertEqual(list(model['CharacterModels']), roles)
            for head in model['CharacterModels'].values():
                self.assertEqual(head['Schema'], 10)
                self.assertEqual(head['FeatureNames'], self.names)
                self.assertEqual(len(head['Forest']), 64)
                predicted = predict_document(head, raw)
                self.assertTrue(np.all(predicted[edges['preferred']] > predicted[edges['other']]))
            metrics = json.loads((directory / 'fitted/metrics.json').read_text())
            self.assertTrue(all(h['productColumns'] == 1 for h in metrics['heads']))
            self.assertTrue(all(h['float32PairSignChanges'] == 0 for h in metrics['heads']))
            self.assertEqual(len(json.loads((directory / 'fitted/parity-inputs.json').read_text())), 480)


if __name__ == '__main__':
    unittest.main()
