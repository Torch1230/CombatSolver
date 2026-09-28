import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

try:
    import numpy as np
    from ranking_data import EDGE_DTYPE
    from root_provenance import read_assignments, validate_assignments
    AVAILABLE = True
except ModuleNotFoundError as error:
    if error.name != 'numpy':
        raise
    AVAILABLE = False


@unittest.skipUnless(AVAILABLE, 'Optional private numerical environment missing')
class RootProvenanceChecks(unittest.TestCase):
    def setUp(self):
        self.assignments = np.array([2, 2, 2, 8, 8, 8], dtype='<i4')
        self.edges = np.array([(0, 1, .5), (0, 2, .5), (3, 4, .5), (3, 5, .5)], dtype=EDGE_DTYPE)

    def test_global_root_gaps_and_multiple_pools_preserve_identity(self):
        np.testing.assert_array_equal(validate_assignments(self.assignments, self.edges, [2, 5, 8], 2),
                                      self.assignments)
        disconnected = np.array([(0, 1, .5), (2, 3, .5), (4, 5, 1.)], dtype=EDGE_DTYPE)
        actual = validate_assignments(np.array([2, 2, 2, 2, 8, 8]), disconnected, [2, 5, 8], 2)
        self.assertEqual(len(np.unique(actual)), 2)

    def test_rejects_cross_root_pairs_unknown_ids_and_unobserved_rows(self):
        cross = self.edges.copy(); cross['other'][0] = 4
        cases = [(self.assignments, cross, [2, 5, 8], 2),
                 (self.assignments, self.edges, [2, 5], 2),
                 (np.append(self.assignments, 8), self.edges, [2, 5, 8], 2),
                 (self.assignments, self.edges, [2, 5, 8], 3),
                 (self.assignments.astype(float), self.edges, [2, 5, 8], 2)]
        for args in cases:
            with self.assertRaises(ValueError):
                validate_assignments(*args)

    def test_total_weight_cannot_hide_wrong_per_root_weights(self):
        edges = self.edges.copy(); edges['weight'] = [.75, .75, .25, .25]
        self.assertEqual(float(edges['weight'].sum()), 2.)
        with self.assertRaises(ValueError):
            validate_assignments(self.assignments, edges, [2, 5, 8], 2)

    def test_row_duplication_still_has_only_two_roots(self):
        assignments = np.tile(self.assignments, 100)
        edges = np.concatenate([self.edges.copy() for _ in range(100)])
        edges['preferred'] += np.repeat(np.arange(100) * 6, 4)
        edges['other'] += np.repeat(np.arange(100) * 6, 4)
        edges['weight'] /= 100
        validated = validate_assignments(assignments, edges, [2, 5, 8], 2)
        self.assertEqual(len(np.unique(validated)), 2)

    def test_reader_binds_manifest_and_exact_assignment_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            head = dict(character='A', rows=6, participatingRoots=2, rootIndices=[2, 5, 8])
            manifest = dict(exportSchema=1, partition='character', roots=9, heads=[head],
                matrixFormat='row-major-little-endian-float32-explicit-zero',
                edgeFormat='little-endian-int32-int32-float64', marginFormat='little-endian-float64')
            path = directory / 'manifest.json'; path.write_text(json.dumps(manifest))
            self.assignments.tofile(directory / 'head-0.roots.i32')
            mapping = dict(head, assignments='head-0.roots.i32',
                sha256=hashlib.sha256(self.assignments.tobytes()).hexdigest())
            sidecar = dict(schema=1, assignmentFormat='little-endian-int32-source-root-index',
                totalRoots=9, manifestSha256=hashlib.sha256(path.read_bytes()).hexdigest(), heads=[mapping])
            sidecar_path = directory / 'row-roots.json'; sidecar_path.write_text(json.dumps(sidecar))
            np.testing.assert_array_equal(read_assignments(directory, head, self.edges), self.assignments)
            path.write_text(json.dumps(manifest) + ' ')
            with self.assertRaises(ValueError):
                read_assignments(directory, head, self.edges)
            path.write_text(json.dumps(manifest))
            for change in [dict(assignments='../head-0.roots.i32'), dict(rows=5), dict(sha256='bad')]:
                changed = copy.deepcopy(sidecar); changed['heads'][0].update(change)
                sidecar_path.write_text(json.dumps(changed))
                with self.assertRaises(ValueError):
                    read_assignments(directory, head, self.edges)
            sidecar_path.write_text(json.dumps(sidecar))
            (directory / 'head-0.roots.i32').write_bytes(self.assignments.tobytes()[:-4])
            with self.assertRaises(ValueError):
                read_assignments(directory, head, self.edges)

    def test_missing_provenance_is_never_reconstructed_from_components(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            head = dict(character='A')
            (directory / 'manifest.json').write_text(json.dumps(dict(exportSchema=1,
                partition='character', heads=[head],
                matrixFormat='row-major-little-endian-float32-explicit-zero',
                edgeFormat='little-endian-int32-int32-float64', marginFormat='little-endian-float64')))
            with self.assertRaisesRegex(ValueError, 'cannot be guessed'):
                read_assignments(directory, head, self.edges)


if __name__ == '__main__':
    unittest.main()
