"""Prototype: enforce root participation while selecting histogram splits.

Not connected to a full fitter. At most 256 explicitly selected columns and
32 quantile cuts per column; contextual products also retain both zero cuts.
No new labels, root inference, runtime features or utility scores are created.
"""
import math

import numpy as np

from root_support import prune_tree, root_participation


def contextual_columns(names, signals, products):
    """Include every exported active-power observation, not only prefix winners.

    This chooses available raw columns, never assigns a power value. Products
    keep their expanded-matrix indices. Exceeding the explicit capacity fails;
    no role or ability is silently dropped to fit the cap.
    """
    if (not names or len(set(names)) != len(names)
            or any(not isinstance(n, str) or not n.strip() for n in names)
            or len(set(signals)) != len(signals) or not set(signals) <= set(names)
            or len(products) > 64 or len(set(products)) != len(products)
            or any(type(g) is not int or type(s) is not int or not 0 <= g < len(names)
                   or not 0 <= s < len(names) or g == s for g, s in products)):
        raise ValueError('Invalid contextual column selection')
    lookup = {name: index for index, name in enumerate(names)}
    columns = ({lookup[n] for n in signals} | {g for g, _ in products}
               | {i for i, n in enumerate(names) if n.startswith('power/')}
               | set(range(len(names), len(names) + len(products))))
    if not 1 <= len(columns) <= 256:
        raise ValueError('Contextual column capacity exceeded or no observations selected')
    return sorted(columns)


def supported_columns(root_hessian, minimum=3):
    """Vectorized equivalent of root_participation, one proposed child per column."""
    if (root_hessian.ndim != 2 or not np.isfinite(root_hessian).all()
            or (root_hessian < 0).any()):
        raise ValueError('Invalid root histogram')
    distinct = np.count_nonzero(root_hessian, axis=0)
    scale = np.max(root_hessian, axis=0, initial=0.)
    normalized = np.divide(root_hessian, scale, out=np.zeros_like(root_hessian), where=scale > 0)
    square_sum = np.sum(normalized * normalized, axis=0)
    participation = np.divide(np.sum(normalized, axis=0) ** 2, square_sum,
                               out=np.zeros_like(scale), where=square_sum > 0)
    participation = np.minimum(participation, distinct)
    tolerance = 64 * np.finfo(np.float64).eps * np.maximum(1, distinct)
    return (distinct >= minimum) & (participation + tolerance >= minimum)


class RootHistogram:
    """Cache training cuts once, then fit supported trees on current derivatives.

    The supplied observation matrix must remain unchanged during this object's
    lifetime. Returned feature indices refer to that matrix, not histogram slots.
    All cuts and root support come exclusively from the supplied training graph.
    """
    def __init__(self, matrix, columns, *, products=(), check_deadline=lambda: None):
        self.matrix = np.asarray(matrix, dtype=np.float32)
        if (self.matrix.ndim != 2 or not len(self.matrix) or not np.isfinite(self.matrix).all()
                or not 1 <= len(columns) <= 256 or len(set(columns)) != len(columns)
                or any(type(i) is not int or not 0 <= i < self.matrix.shape[1] for i in columns)
                or len(set(products)) != len(products) or not set(products) <= set(columns)):
            raise ValueError('Invalid bounded histogram observations or columns')
        self.columns = sorted(columns)
        self.cuts = []
        self.bins = np.empty((len(self.matrix), len(columns)), dtype=np.uint8, order='F')
        for slot, column in enumerate(self.columns):
            check_deadline()
            values = self.matrix[:, column]
            # Observed float32 order statistics, including the maximum, retain
            # a possible rare extreme partition without interpolated boundaries.
            cuts = np.quantile(values, np.arange(1, 33) / 32, method='inverted_cdf')
            if column in products:
                cuts = np.concatenate((cuts, [np.float32(0), np.nextafter(np.float32(0), np.float32(1))]))
            cuts = np.unique(cuts.astype(np.float32))
            cuts = cuts[(cuts > values.min()) & (cuts <= values.max())]
            self.cuts.append(cuts)
            # bin<=j is exactly value<cuts[j], including signed zero/subnormals.
            self.bins[:, slot] = np.searchsorted(cuts, values, side='right')

    def fit(self, roots, gradient, hessian, *, check_deadline=lambda: None):
        roots, gradient, hessian = map(np.asarray, (roots, gradient, hessian))
        if (roots.shape != (len(self.matrix),) or roots.dtype.kind not in 'iu' or (roots < 0).any()
                or gradient.shape != roots.shape or hessian.shape != roots.shape
                or not np.isfinite(gradient).all() or not np.isfinite(hessian).all()
                or (hessian < 0).any() or (hessian > np.finfo(np.float32).max).any()
                or (np.abs(gradient) > np.finfo(np.float32).max).any()):
            raise ValueError('Invalid root histogram derivatives')
        gradient, hessian = [v.astype(np.float32).astype(np.float64) for v in (gradient, hessian)]
        _, local_roots = np.unique(roots, return_inverse=True)
        root_count = int(local_roots.max()) + 1
        support = root_participation(roots, hessian)
        tolerance = 64 * np.finfo(np.float64).eps * max(1, support['distinctRoots'])
        if support['distinctRoots'] < 3 or support['effectiveRoots'] + tolerance < 3:
            raise ValueError('Whole tree lacks required root participation')
        metrics = dict(nodesVisited=0, proposedCuts=0, supportedCuts=0, selectedSplits=[])

        def visit(rows, depth):
            check_deadline()
            metrics['nodesVisited'] += 1
            total_gradient, total_hessian = float(gradient[rows].sum()), float(hessian[rows].sum())
            mean = float(np.float32(-.1 * total_gradient / (total_hessian + .001))) / .1
            if not math.isfinite(mean):
                raise ValueError('Non-finite histogram Newton leaf')
            leaf = dict(Feature=-1, Threshold=0., Mean=mean, Left=None, Right=None)
            if depth == 4:
                return leaf
            parent_gain = total_gradient * total_gradient / (total_hessian + .001)
            best_gain, best = 0., None
            for slot, cuts in enumerate(self.cuts):
                check_deadline()
                if not len(cuts):
                    continue
                bins = self.bins[rows, slot].astype(np.int64)
                width = len(cuts) + 1
                histogram = np.bincount(local_roots[rows] * width + bins,
                    weights=hessian[rows], minlength=root_count * width).reshape(root_count, width)
                # Reverse sums avoid subtractive cancellation for rare right children.
                left_roots = np.cumsum(histogram, axis=1)[:, :-1]
                right_roots = np.cumsum(histogram[:, ::-1], axis=1)[:, ::-1][:, 1:]
                supported = supported_columns(left_roots) & supported_columns(right_roots)
                metrics['proposedCuts'] += len(cuts)
                metrics['supportedCuts'] += int(np.count_nonzero(supported))
                if not supported.any():
                    continue
                g = np.bincount(bins, weights=gradient[rows], minlength=width)
                gl, gr = np.cumsum(g)[:-1], np.cumsum(g[::-1])[::-1][1:]
                hl, hr = left_roots.sum(axis=0), right_roots.sum(axis=0)
                gains = gl * gl / (hl + .001) + gr * gr / (hr + .001) - parent_gain
                if not np.isfinite(gains).all():
                    raise ValueError('Non-finite histogram split gain')
                gains[~supported] = -math.inf
                cut = int(np.argmax(gains))
                if gains[cut] > best_gain:
                    best_gain, best = float(gains[cut]), (slot, cut)
            if best is None:
                return leaf
            slot, cut = best
            column = self.columns[slot]
            threshold = math.nextafter(float(self.cuts[slot][cut]), -math.inf)
            mask = self.bins[rows, slot] <= cut
            if not np.array_equal(mask, self.matrix[rows, column].astype(np.float64) <= threshold):
                raise ValueError('Observation mutation or inconsistent histogram boundary')
            metrics['selectedSplits'].append(dict(feature=column, threshold=threshold,
                                                  depth=depth, quadraticGainProxy=best_gain))
            return dict(Feature=column, Threshold=threshold, Mean=0.,
                        Left=visit(rows[mask], depth + 1), Right=visit(rows[~mask], depth + 1))

        tree = visit(np.arange(len(self.matrix)), 0)
        checked, report = prune_tree(tree, self.matrix, roots, gradient, hessian)
        if checked != tree or report['collapsedBranches']:
            raise ValueError('Histogram proposal failed exact row-level root support validation')
        return tree, dict(metrics, support=report)
