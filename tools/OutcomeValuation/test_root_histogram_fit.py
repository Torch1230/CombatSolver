import io
import json
from contextlib import redirect_stdout
from pathlib import Path
import tempfile
import time
import unittest

import test_root_context_fit as fixtures

if fixtures.AVAILABLE:
    import numpy as np
    from ranking_data import EDGE_DTYPE, loss
    from root_context_fit import fit
    from root_context_round import zero_leaf
    from root_histogram_fit import fit_histogram_rounds
    from xgboost_fit import predict_document


@unittest.skipUnless(fixtures.AVAILABLE, 'Optional private numerical environment missing')
class RootHistogramFitChecks(unittest.TestCase):
    def test_all_power_proposal_can_learn_a_signal_missing_from_prefix(self):
        names = ['pile/hand/count', 'power/player/SYNTHETIC']
        # Three equal roots satisfy participation while each child's absolute
        # Hessian remains below 3; six roots let the ordinary proposal tie it.
        raw = np.column_stack((np.zeros(6), np.tile([0, 1], 3))).astype(np.float32)
        roots = np.repeat(np.arange(3), 2)
        edges = np.array([(i + 1, i, 1.) for i in range(0, len(raw), 2)], dtype=EDGE_DTYPE)
        updates, predicted, reports = fit_histogram_rounds(raw, names, [], roots, edges,
            np.zeros(len(raw)), [zero_leaf()], lambda: None, time.monotonic() + 30,
            signal_names=[names[0]])
        self.assertTrue(all(r['selected'] == 'all-powers' for r in reports))
        self.assertTrue(np.all(predicted[edges['preferred']] > predicted[edges['other']]))
        self.assertEqual([c['columns'] for c in reports[0]['histogramCaches']], [1, 2])
        self.assertTrue(all(not r['histogramCaches'] for r in reports[1:]))
        actual = predict_document(dict(LinearWeights=[0., 0.], Forest=[zero_leaf()] + updates), raw)
        np.testing.assert_array_equal(actual, predicted)

    def test_reversal_uses_retained_predictions_and_supported_compiled_products(self):
        raw, roots, edges = fixtures.RootContextFitChecks.observations()
        names = fixtures.RootContextFitChecks.names
        foundation = dict(LinearWeights=[-.15, .2], Forest=[zero_leaf()])
        margins = predict_document(foundation, raw)
        prefix = [dict(zero_leaf(), Mean=7.25), dict(zero_leaf(), Mean=-.25)]
        updates, predicted, reports = fit_histogram_rounds(raw, names, [(0, 1)], roots,
            edges, margins, prefix, lambda: None, time.monotonic() + 30, signal_names=[names[1]])
        previous = loss(predict_document(dict(foundation, Forest=prefix), raw), edges)
        for report in reports:
            self.assertEqual(report['beforeLoss'], previous)
            self.assertLessEqual(report['afterLoss'], previous)
            previous = report['afterLoss']
            for diagnostic in report['histogramProposals'].values():
                self.assertEqual(diagnostic['support']['collapsedBranches'], 0)
            self.assertTrue(all(s['distinctRoots'] >= 3 and s['effectiveRoots'] >= 3 - 1e-12
                                for s in report['support']['leaves']))
        self.assertGreater(sum(r['productSplits'] for r in reports), 0)
        actual = predict_document(dict(foundation, Forest=prefix + updates), raw)
        np.testing.assert_array_equal(actual, predicted)
        self.assertTrue(np.all(actual[edges['preferred']] > actual[edges['other']]))

    def test_complete_five_head_model_uses_shared_export_and_exact_round_predictions(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            raw, edges = fixtures.RootContextFitChecks().export(directory)
            with redirect_stdout(io.StringIO()):
                fit(directory, directory / 'fitted', 30, round_fitter=fit_histogram_rounds,
                    backend='root-supported-histogram-xgboost-cpu')
            model = json.loads((directory / 'fitted/model.json').read_text())
            metrics = json.loads((directory / 'fitted/metrics.json').read_text())
            self.assertEqual(metrics['backend'], 'root-supported-histogram-xgboost-cpu')
            self.assertEqual(len(model['CharacterModels']), 5)
            for head, document in zip(metrics['heads'], model['CharacterModels'].values(), strict=True):
                self.assertEqual(document['FeatureNames'], fixtures.RootContextFitChecks.names)
                self.assertEqual(len(document['Forest']), 64)
                self.assertEqual(len(head['rounds']), 16)
                self.assertEqual(head['exportedPredictionDrift'], 0.)
                self.assertGreater(head['productSplits'], 0)
                predicted = predict_document(document, raw)
                self.assertTrue(np.all(predicted[edges['preferred']] > predicted[edges['other']]))
            self.assertEqual(len(json.loads((directory / 'fitted/parity-inputs.json').read_text())), 60)

    def test_deadline_and_missing_root_provenance_fail_before_model_publication(self):
        raw, roots, edges = fixtures.RootContextFitChecks.observations()
        def expired():
            raise TimeoutError('synthetic deadline')
        with self.assertRaises(TimeoutError):
            fit_histogram_rounds(raw, fixtures.RootContextFitChecks.names, [], roots, edges,
                np.zeros(len(raw)), [], expired, time.monotonic() - 1,
                signal_names=fixtures.RootContextFitChecks.names)
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            fixtures.RootContextFitChecks().export(directory)
            (directory / 'row-roots.json').unlink()
            with self.assertRaises(ValueError):
                fit(directory, directory / 'fitted', 30, round_fitter=fit_histogram_rounds)
            self.assertFalse((directory / 'fitted/model.json').exists())


if __name__ == '__main__':
    unittest.main()
