"""Prototype root-aware pruning; not used by any default or frozen fitter.

The participation ratio (sum H_root)^2 / sum H_root^2 measures concentration
of leaf Hessian across supplied physical roots. It is not an estimate of
independent statistical sample size or evidence of combat quality.
"""
import math

import numpy as np


def root_participation(root_indices, hessian):
    roots, weights = np.asarray(root_indices), np.asarray(hessian)
    if (roots.ndim != 1 or roots.dtype.kind not in 'iu' or weights.shape != roots.shape
            or (roots < 0).any() or not np.isfinite(weights).all() or (weights < 0).any()):
        raise ValueError('Invalid root Hessian observations')
    _, inverse = np.unique(roots, return_inverse=True)
    mass = np.bincount(inverse, weights=weights)
    with np.errstate(over='ignore'):
        total = float(mass.sum())
    if not np.isfinite(mass).all() or not math.isfinite(total):
        raise ValueError('Non-finite aggregate root Hessian')
    mass = mass[mass > 0]
    if not len(mass):
        return dict(distinctRoots=0, effectiveRoots=0., hessianMass=0.)
    # Normalizing first keeps the ratio finite for very small/large weights.
    scaled = mass / mass.max()
    effective = min(float(len(mass)), float(scaled.sum() ** 2 / np.dot(scaled, scaled)))
    return dict(distinctRoots=len(mass), effectiveRoots=effective, hessianMass=total)


def prune_tree(tree, matrix, root_indices, gradient, hessian, *, minimum_roots=3,
               regularization=.001, learning_rate=.1):
    """Collapse unsupported branches using their parent's regularized Newton leaf.

    The input is an imported <=-threshold tree on finite float32 observations.
    Existing supported leaves are preserved. A tree with insufficient total
    root participation returns None and must not be published as a learned tree.
    Callers must fit later rounds against the pruned predictions, not the
    original booster. Authoritative root assignments must be validated first.
    """
    matrix = np.asarray(matrix, dtype=np.float32)
    roots, gradient, hessian = map(np.asarray, (root_indices, gradient, hessian))
    if (matrix.ndim != 2 or not len(matrix) or roots.shape != (len(matrix),)
            or roots.dtype.kind not in 'iu' or (roots < 0).any()
            or gradient.shape != roots.shape or hessian.shape != roots.shape
            or not np.isfinite(matrix).all() or not np.isfinite(gradient).all()
            or not np.isfinite(hessian).all() or (hessian < 0).any()
            or (np.abs(gradient) > np.finfo(np.float32).max).any()
            or (hessian > np.finfo(np.float32).max).any()
            or type(minimum_roots) is not int or minimum_roots < 1
            or not math.isfinite(regularization) or regularization <= 0
            or not math.isfinite(learning_rate) or not 0 < learning_rate <= 1):
        raise ValueError('Invalid root-aware pruning inputs')
    gradient = gradient.astype(np.float32).astype(np.float64)
    hessian = hessian.astype(np.float32).astype(np.float64)

    def inspect(node, depth=0):
        feature = node['Feature']
        if (depth > 6 or type(feature) is not int or not -1 <= feature < matrix.shape[1]
                or not math.isfinite(node['Threshold']) or not math.isfinite(node['Mean'])):
            raise ValueError('Invalid imported tree')
        if feature == -1:
            if node.get('Left') is not None or node.get('Right') is not None:
                raise ValueError('Invalid imported leaf')
        else:
            if node.get('Left') is None or node.get('Right') is None:
                raise ValueError('Incomplete imported split')
            inspect(node['Left'], depth + 1)
            inspect(node['Right'], depth + 1)

    inspect(tree)

    def visit(node, rows):
        support = root_participation(roots[rows], hessian[rows])
        tolerance = 64 * np.finfo(np.float64).eps * max(1, support['distinctRoots'])
        if (support['distinctRoots'] < minimum_roots
                or support['effectiveRoots'] + tolerance < minimum_roots):
            return None, [], 0
        if node['Feature'] == -1:
            return dict(node), [support], 0
        mask = matrix[rows, node['Feature']].astype(np.float64) <= node['Threshold']
        left, left_support, left_collapsed = visit(node['Left'], rows[mask])
        right, right_support, right_collapsed = visit(node['Right'], rows[~mask])
        if left is None or right is None:
            step = float(np.float32(-learning_rate * gradient[rows].sum()
                                   / (hessian[rows].sum() + regularization)))
            if not math.isfinite(step):
                raise ValueError('Non-finite collapsed Newton leaf')
            # Production applies the fixed 0.1 forest scale after summation.
            leaf = dict(Feature=-1, Threshold=0., Mean=step / .1, Left=None, Right=None)
            return leaf, [support], 1
        return (dict(node, Left=left, Right=right), left_support + right_support,
                left_collapsed + right_collapsed)

    pruned, leaves, collapsed = visit(tree, np.arange(len(matrix)))
    return pruned, dict(accepted=pruned is not None, collapsedBranches=collapsed,
                        minimumRootParticipation=minimum_roots, leaves=leaves)
