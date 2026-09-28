"""Bounded contextual updates using explicit root support and actual graph loss.

The two XGBoost proposals can miss a supported alternative when a greedy
branch is pruned. Also inspect the two zero boundaries of selected products,
keeping one supported stump by quadratic gain. This is training-only proposal
selection, not a combat-quality guarantee or an additional runtime mode.
"""
import math

import numpy as np

from ranking_data import loss
from root_support import prune_tree


def leaf_values(tree, matrix):
    """Unscaled leaf values, accumulated in production forest order by callers."""
    result = np.empty(len(matrix), dtype=np.float64)

    def visit(node, rows):
        if node['Feature'] == -1:
            result[rows] = node['Mean']
        else:
            mask = matrix[rows, node['Feature']].astype(np.float64) <= node['Threshold']
            visit(node['Left'], rows[mask])
            visit(node['Right'], rows[~mask])

    visit(tree, np.arange(len(matrix)))
    if not np.isfinite(result).all():
        raise ValueError('Non-finite contextual leaf prediction')
    return result


def zero_leaf():
    return dict(Feature=-1, Threshold=0., Mean=0., Left=None, Right=None)


def best_product_stump(matrix, raw_columns, roots, gradient, hessian, check_deadline):
    """Fixed zero cuts only; no claim to search every supported threshold."""
    gradient, hessian = [v.astype(np.float32).astype(np.float64) for v in (gradient, hessian)]
    parent = float(gradient.sum()) ** 2 / (float(hessian.sum()) + .001)
    best, best_report, best_gain = None, None, 0.
    for column in range(raw_columns, matrix.shape[1]):
        values = matrix[:, column]
        for threshold in (math.nextafter(0., -math.inf), 0.):
            check_deadline()
            mask = values.astype(np.float64) <= threshold
            if not mask.any() or mask.all():
                continue
            gl, gr = float(gradient[mask].sum()), float(gradient[~mask].sum())
            hl, hr = float(hessian[mask].sum()), float(hessian[~mask].sum())
            gain = gl * gl / (hl + .001) + gr * gr / (hr + .001) - parent
            if gain <= best_gain:
                continue
            left = dict(zero_leaf(), Mean=float(np.float32(-.1 * gl / (hl + .001))) / .1)
            right = dict(zero_leaf(), Mean=float(np.float32(-.1 * gr / (hr + .001))) / .1)
            proposal = dict(Feature=0, Threshold=threshold, Mean=0., Left=left, Right=right)
            supported, report = prune_tree(proposal, values[:, None], roots, gradient, hessian)
            if supported is not None and supported['Feature'] == 0:
                best, best_report, best_gain = dict(supported, Feature=column), report, gain
    if best_report is not None:
        best_report = dict(best_report, quadraticGainProxy=best_gain)
    return best, best_report


def choose_update(proposals, matrix, roots, gradient, hessian, forest_sum, margins, edges):
    """Prune all proposals, then choose strict actual loss improvement over zero.

    The first proposal wins exact ties. No unsupported branch reaches export.
    Keeping an unchanged round is explicit and preserves the 64-tree schema.
    """
    selected = zero_leaf()
    _, zero_support = prune_tree(selected, matrix, roots, gradient, hessian)
    if not zero_support['accepted']:
        raise ValueError('Entire head lacks the required root participation')
    before = loss(.1 * forest_sum + margins, edges)
    best_loss, best_sum, selected_name, selected_support = before, forest_sum, 'zero', zero_support
    diagnostics = []
    for name, proposal in proposals:
        if proposal is None:
            diagnostics.append(dict(name=name, proposed=False))
            continue
        tree, support = prune_tree(proposal, matrix, roots, gradient, hessian)
        if tree is None:
            diagnostics.append(dict(name=name, proposed=True, support=support))
            continue
        candidate_sum = forest_sum + leaf_values(tree, matrix)
        candidate_loss = loss(.1 * candidate_sum + margins, edges)
        if not math.isfinite(candidate_loss):
            raise ValueError('Non-finite contextual candidate loss')
        diagnostics.append(dict(name=name, proposed=True, loss=candidate_loss, support=support))
        if candidate_loss < best_loss:
            selected, best_sum, best_loss = tree, candidate_sum, candidate_loss
            selected_name, selected_support = name, support
    return selected, best_sum, dict(selected=selected_name, beforeLoss=before, afterLoss=best_loss,
                                    support=selected_support, proposals=diagnostics)
