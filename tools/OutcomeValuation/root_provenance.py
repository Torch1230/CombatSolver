"""Validate optional C# row-to-root provenance without inferring it from data.

Distinct input roots are not a proof of statistical independence. Dataset
isolation and native-root provenance audits remain required before fitting.
"""
import hashlib
import json
from pathlib import Path

import numpy as np

from ranking_data import EDGE_DTYPE, read_manifest


def validate_assignments(assignments, edges, source_roots, participating_roots):
    assignments = np.asarray(assignments)
    if (assignments.ndim != 1 or assignments.dtype.kind not in 'iu' or not len(assignments)
            or edges.ndim != 1 or edges.dtype != EDGE_DTYPE or not len(edges)
            or not source_roots or any(type(i) is not int or i < 0 for i in source_roots)
            or len(set(source_roots)) != len(source_roots)
            or type(participating_roots) is not int or participating_roots < 1
            or not np.isin(assignments, source_roots).all()
            or not np.isfinite(edges['weight']).all() or (edges['weight'] <= 0).any()
            or any((edges[k] < 0).any() or (edges[k] >= len(assignments)).any()
                   for k in ('preferred', 'other'))):
        raise ValueError('Invalid row-to-root provenance')
    roots, inverse = np.unique(assignments, return_inverse=True)
    preferred, other = edges['preferred'], edges['other']
    if (len(roots) != participating_roots or (preferred == other).any()
            or not np.array_equal(inverse[preferred], inverse[other])
            or len(np.unique(np.concatenate((preferred, other)))) != len(assignments)):
        raise ValueError('Preference endpoints do not preserve compacted physical roots')
    mass = np.bincount(inverse[preferred], weights=edges['weight'], minlength=len(roots))
    if not np.allclose(mass, 1., rtol=1e-12, atol=1e-12):
        raise ValueError('Each participating root must retain total preference weight one')
    return assignments


def read_assignments(directory, head, edges):
    manifest = read_manifest(directory)
    if head not in manifest['heads']:
        raise ValueError('Head is not part of the authoritative export')
    path = directory / 'row-roots.json'
    if not path.exists():
        raise ValueError('Explicit C# row-root export required; roots cannot be guessed')
    sidecar = json.loads(path.read_text())
    manifest_hash = hashlib.sha256((directory / 'manifest.json').read_bytes()).hexdigest()
    if (sidecar['schema'] != 1
            or sidecar['assignmentFormat'] != 'little-endian-int32-source-root-index'
            or sidecar['manifestSha256'] != manifest_hash
            or sidecar['totalRoots'] != manifest['roots']
            or sorted(h['character'] for h in sidecar['heads'])
                != sorted(h['character'] for h in manifest['heads'])):
        raise ValueError('Root sidecar does not match the exact export')
    mapping = next(h for h in sidecar['heads'] if h['character'] == head['character'])
    if any(mapping[k] != head[k] for k in ('rows', 'participatingRoots', 'rootIndices')):
        raise ValueError('Root sidecar does not match head metadata')
    if (type(manifest['roots']) is not int or manifest['roots'] < 1
            or any(type(i) is not int or not 0 <= i < manifest['roots'] for i in head['rootIndices'])):
        raise ValueError('Invalid global input root indices')
    name = mapping['assignments']
    if not isinstance(name, str) or Path(name).name != name:
        raise ValueError('Root assignment files must be local')
    encoded = (directory / name).read_bytes()
    if (len(encoded) != head['rows'] * 4
            or hashlib.sha256(encoded).hexdigest() != mapping['sha256']):
        raise ValueError('Root assignment size or hash mismatch')
    assignments = np.frombuffer(encoded, dtype='<i4')
    return validate_assignments(assignments, edges, head['rootIndices'], head['participatingRoots'])
