#!/usr/bin/env python3
"""Stage 8F (P4) Offline Teacher-Student Auto-Calibration Trainer for HotEvaluator.

Reads preference pairs exported by OfflineSearchHarness (--export-teacher-pairs):
  - .local/bench/teacher-pairs-train.jsonl
  - .local/bench/teacher-pairs-val.jsonl
  - .local/bench/teacher-pairs-schema.json (optional; auto-detected next to train JSONL)

Fits:
  1. Regularized pairwise Logistic / LambdaRank int32 linear weights over the 35 domain-agnostic
     HotCalibrationFeatures (zero card IDs, zero runtime dependencies).
  2. Optional tiny 35->16->1 quantized int16/int32 ReLU MLP (~576 MACs, <0.15 us, 0 B alloc).

Reports exact train & held-out validation pairwise ranking accuracy (overall, per pair_kind,
per character, per room_type, per turn) compared against the current HotEvaluator baseline,
and emits a ready-to-paste zero-allocation C# static ReadOnlySpan<int> / ReadOnlySpan<short>
weight table.

Dependencies:
  - Python 3.10+ standard library only (zero required external packages).
  - Optional: numpy (used automatically for faster vectorized training when installed; pass
    --no-numpy to force the pure-Python fallback).

Example commands:
  python -B tools/search/train_hot_evaluator.py --self-test
  python -B tools/search/train_hot_evaluator.py --model both --loss lambdarank
  python -B tools/search/train_hot_evaluator.py --train .local/bench/teacher-pairs-train.jsonl \\
      --val .local/bench/teacher-pairs-val.jsonl --out-cs .local/bench/HotCalibratedWeights.generated.cs
"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import json
import math
from pathlib import Path
import random
import sys
import time
from typing import Any

try:
    import numpy as _np  # type: ignore[import-not-found]
except ImportError:
    _np = None


REPO_ROOT = Path(__file__).resolve().parents[2]

DEFAULT_FEATURE_NAMES: tuple[str, ...] = (
    "player_hp",              # 0
    "player_max_hp",          # 1
    "player_block",           # 2
    "unblocked_incoming",     # 3
    "lethal_incoming_excess", # 4
    "potion_cost_hp",         # 5
    "enemy_hp_sum",           # 6
    "enemy_block_sum",        # 7
    "alive_enemy_count",      # 8
    "reviving_enemy_count",   # 9
    "focus_fire_gap",         # 10
    "est_rem_turns",          # 11
    "poison_expected_dmg",    # 12
    "player_accelerant",      # 13
    "doom_quadratic_val",     # 14
    "doom_lethal_count",      # 15
    "orb_passive_dmg",        # 16
    "orb_passive_block",      # 17
    "max_dark_evoke",         # 18
    "orb_count",              # 19
    "orb_capacity",           # 20
    "recurring_buff_stacks",  # 21
    "player_core_stats",      # 22
    "player_debuff_stacks",   # 23
    "enemy_debuff_stacks",    # 24
    "enemy_buff_threat",      # 25
    "osty_hp",                # 26
    "playable_hand_count",    # 27
    "energy",                 # 28
    "stars",                  # 29
    "next_turn_carryover",    # 30
    "live_deck_size",         # 31
    "deck_clutter",           # 32
    "scaling_x_rem_turns",    # 33
    "focus_x_orb_count",      # 34
)


def _clamp_int(val: int, lo: int, hi: int) -> int:
    return lo if val < lo else (hi if val > hi else val)


def _sigmoid_scalar(x: float) -> float:
    if x >= 0.0:
        z = math.exp(-min(x, 60.0))
        return 1.0 / (1.0 + z)
    z = math.exp(max(x, -60.0))
    return z / (1.0 + z)


def _softplus_neg_scalar(margin: float) -> float:
    """Stable log(1 + exp(-margin))."""
    if margin >= 0.0:
        return math.log1p(math.exp(-min(margin, 60.0)))
    return -margin + math.log1p(math.exp(max(margin, -60.0)))


def load_schema_names(train_path: Path, schema_path: Path | None) -> list[str]:
    candidate = schema_path if schema_path is not None else (train_path.parent / "teacher-pairs-schema.json")
    if candidate.is_file():
        data = json.loads(candidate.read_text(encoding="utf-8-sig"))
        names = data.get("feature_names")
        if isinstance(names, list) and names and all(isinstance(x, str) for x in names):
            return list(names)
    return list(DEFAULT_FEATURE_NAMES)


def load_pairs_jsonl(
    path: Path,
    feature_count: int,
    allowed_kinds: set[str] | None = None,
) -> list[dict[str, Any]]:
    if not path.is_file():
        raise FileNotFoundError(f"Pair dataset not found: {path}")
    rows: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8-sig") as fh:
        for line_no, raw in enumerate(fh, start=1):
            line = raw.strip()
            if not line:
                continue
            rec = json.loads(line)
            kind = str(rec.get("pair_kind", "unknown"))
            if allowed_kinds is not None and kind not in allowed_kinds:
                continue
            w = rec.get("winner")
            l = rec.get("loser")
            if not isinstance(w, list) or not isinstance(l, list):
                raise ValueError(f"{path}:{line_no}: missing winner/loser feature arrays")
            if len(w) != feature_count or len(l) != feature_count:
                raise ValueError(
                    f"{path}:{line_no}: expected {feature_count} features, got winner={len(w)} loser={len(l)}"
                )
            rows.append({
                "fight_id": str(rec.get("fight_id", f"row_{line_no}")),
                "character_id": str(rec.get("character_id", "UNKNOWN")),
                "encounter_id": str(rec.get("encounter_id", "UNKNOWN")),
                "room_type": str(rec.get("room_type", "Unknown")),
                "game_mode": str(rec.get("game_mode", "Standard")),
                "turn": int(rec.get("turn", 1)),
                "pair_kind": kind,
                "teacher_v_centi": int(rec.get("teacher_v_centi", 0)),
                "winner_eval_score": int(rec.get("winner_eval_score", 0)),
                "loser_eval_score": int(rec.get("loser_eval_score", 0)),
                "winner": [int(v) for v in w],
                "loser": [int(v) for v in l],
            })
    return rows


def compute_sample_weights(
    rows: list[dict[str, Any]],
    fight_balance: bool,
    hard_boost: float,
) -> list[float]:
    if not rows:
        return []
    fight_counts = Counter(r["fight_id"] for r in rows)
    weights: list[float] = []
    for r in rows:
        w = (1.0 / fight_counts[r["fight_id"]]) if fight_balance else 1.0
        turn = r["turn"]
        if turn == 3:
            w *= 0.92
        elif turn >= 4:
            w *= 0.85
        # Emphasize Teacher-Student inversions where current HotEvaluator scored loser >= winner.
        if hard_boost > 1.0 and r["loser_eval_score"] >= r["winner_eval_score"]:
            w *= hard_boost
        weights.append(w)
    mean_w = sum(weights) / len(weights)
    if mean_w > 0.0:
        weights = [w / mean_w for w in weights]
    return weights


def compute_train_standardization(
    train_rows: list[dict[str, Any]],
    dim: int,
) -> tuple[list[float], list[float]]:
    """Computes per-feature (mean, std) strictly on the training split (zero val leakage)."""
    n_states = 2 * len(train_rows)
    if n_states == 0:
        return [0.0] * dim, [1.0] * dim
    sums = [0.0] * dim
    sq_sums = [0.0] * dim
    for r in train_rows:
        for vec in (r["winner"], r["loser"]):
            for j in range(dim):
                v = float(vec[j])
                sums[j] += v
                sq_sums[j] += v * v
    means = [s / n_states for s in sums]
    stds: list[float] = []
    for j in range(dim):
        var = max(0.0, (sq_sums[j] / n_states) - means[j] * means[j])
        sd = math.sqrt(var)
        stds.append(sd if sd > 1e-6 else 1.0)
    return means, stds


def evaluate_scores_summary(
    rows: list[dict[str, Any]],
    margins: list[float],
) -> dict[str, Any]:
    """Computes exact pairwise ranking accuracy, tie rate, and logistic loss overall + by slice."""
    n = len(rows)
    if n == 0:
        return {"pairs": 0, "accuracy": 0.0, "tie_rate": 0.0, "loss": 0.0, "slices": {}}

    correct = 0
    ties = 0
    loss_sum = 0.0
    slice_totals: dict[str, dict[str, list[int]]] = {
        "pair_kind": defaultdict(lambda: [0, 0]),
        "character_id": defaultdict(lambda: [0, 0]),
        "room_type": defaultdict(lambda: [0, 0]),
        "turn": defaultdict(lambda: [0, 0]),
    }

    for r, m in zip(rows, margins):
        is_win = 1 if m > 0.0 else 0
        if m == 0.0:
            ties += 1
        correct += is_win
        loss_sum += _softplus_neg_scalar(m)

        for field, key in (
            ("pair_kind", r["pair_kind"]),
            ("character_id", r["character_id"]),
            ("room_type", r["room_type"]),
            ("turn", f"T{r['turn']}"),
        ):
            bucket = slice_totals[field][key]
            bucket[0] += is_win
            bucket[1] += 1

    slices_out: dict[str, dict[str, dict[str, Any]]] = {}
    for field, buckets in slice_totals.items():
        slices_out[field] = {
            k: {
                "pairs": tot,
                "correct": cor,
                "accuracy": round(cor / tot, 4) if tot > 0 else 0.0,
            }
            for k, (cor, tot) in sorted(buckets.items())
        }

    return {
        "pairs": n,
        "correct": correct,
        "ties": ties,
        "accuracy": round(correct / n, 4),
        "tie_rate": round(ties / n, 4),
        "loss": round(loss_sum / n, 5),
        "slices": slices_out,
    }


def evaluate_baseline_evaluator(rows: list[dict[str, Any]]) -> dict[str, Any]:
    margins = [
        float(r["winner_eval_score"] - r["loser_eval_score"]) / 30000.0
        for r in rows
    ]
    return evaluate_scores_summary(rows, margins)


# =============================================================================
# LINEAR MODEL: Pairwise Logistic / LambdaRank (NumPy + Pure-Python Fallback)
# =============================================================================

def _fit_linear_numpy(
    train_rows: list[dict[str, Any]],
    sample_weights: list[float],
    stds: list[float],
    l2: float,
    loss_kind: str,
    epochs: int,
) -> list[float]:
    np = _np
    assert np is not None
    dim = len(stds)
    n = len(train_rows)
    inv_std = np.array([1.0 / s for s in stds], dtype=np.float64)
    w_mat = np.array([r["winner"] for r in train_rows], dtype=np.float64)
    l_mat = np.array([r["loser"] for r in train_rows], dtype=np.float64)
    dx = (w_mat - l_mat) * inv_std  # (N, D)
    alpha = np.array(sample_weights, dtype=np.float64) / float(max(1, n))

    weights = np.zeros(dim, dtype=np.float64)

    def objective_and_grad(w: Any) -> tuple[float, Any]:
        margins = dx @ w  # (N,)
        # Stable softplus(-m) = max(0, -m) + log1p(exp(-|m|))
        abs_m = np.abs(margins)
        pair_losses = np.maximum(0.0, -margins) + np.log1p(np.exp(-np.minimum(abs_m, 60.0)))
        loss = float(np.dot(alpha, pair_losses) + 0.5 * l2 * np.dot(w, w))

        # sigmoid(-m) = 1 / (1 + exp(m))
        sig_neg = 1.0 / (1.0 + np.exp(np.clip(margins, -60.0, 60.0)))
        if loss_kind == "lambdarank":
            # LambdaRank hardness factor emphasizing near-decision-boundary / inverted pairs
            delta_ndcg = 1.0 + 0.5 * (1.0 / (1.0 + np.exp(np.minimum(abs_m, 60.0))))
            eff_coeff = alpha * sig_neg * delta_ndcg
        else:
            eff_coeff = alpha * sig_neg

        grad = -(dx.T @ eff_coeff) + l2 * w
        return loss, grad

    # Backtracking gradient descent with Barzilai-Borwein step sizing (fast & globally convergent)
    step = 1.0
    prev_w = None
    prev_grad = None
    for _ in range(epochs):
        cur_loss, grad = objective_and_grad(weights)
        grad_norm_sq = float(np.dot(grad, grad))
        if grad_norm_sq < 1e-12:
            break

        if prev_w is not None and prev_grad is not None:
            dw = weights - prev_w
            dg = grad - prev_grad
            denom = float(np.dot(dw, dg))
            if denom > 1e-12:
                step = min(20.0, max(1e-3, float(np.dot(dw, dw)) / denom))

        # Armijo backtracking line search
        accepted = False
        trial_step = step
        for _ in range(25):
            cand = weights - trial_step * grad
            cand_margins = dx @ cand
            cand_abs = np.abs(cand_margins)
            cand_losses = np.maximum(0.0, -cand_margins) + np.log1p(np.exp(-np.minimum(cand_abs, 60.0)))
            cand_obj = float(np.dot(alpha, cand_losses) + 0.5 * l2 * np.dot(cand, cand))
            if cand_obj <= cur_loss - 1e-4 * trial_step * grad_norm_sq:
                prev_w = weights
                prev_grad = grad
                weights = cand
                step = trial_step
                accepted = True
                break
            trial_step *= 0.5
        if not accepted:
            break

    return [float(x) for x in weights]


def _fit_linear_pure_python(
    train_rows: list[dict[str, Any]],
    sample_weights: list[float],
    stds: list[float],
    l2: float,
    loss_kind: str,
    epochs: int,
) -> list[float]:
    dim = len(stds)
    n = len(train_rows)
    inv_std = [1.0 / s for s in stds]
    inv_n = 1.0 / float(max(1, n))

    # Precompute sparse non-zero standardized deltas (huge speedup in pure Python!)
    dx_sparse: list[tuple[float, list[tuple[int, float]]]] = []
    for r, sw in zip(train_rows, sample_weights):
        w_vec = r["winner"]
        l_vec = r["loser"]
        nz: list[tuple[int, float]] = []
        for j in range(dim):
            diff = w_vec[j] - l_vec[j]
            if diff != 0:
                nz.append((j, float(diff) * inv_std[j]))
        dx_sparse.append((sw * inv_n, nz))

    weights = [0.0] * dim

    def eval_obj(w: list[float]) -> float:
        total = 0.5 * l2 * sum(v * v for v in w)
        for a, nz in dx_sparse:
            m = 0.0
            for j, v in nz:
                m += w[j] * v
            total += a * _softplus_neg_scalar(m)
        return total

    def eval_obj_and_grad(w: list[float]) -> tuple[float, list[float]]:
        total = 0.5 * l2 * sum(v * v for v in w)
        grad = [l2 * v for v in w]
        is_lambda = loss_kind == "lambdarank"
        for a, nz in dx_sparse:
            m = 0.0
            for j, v in nz:
                m += w[j] * v
            total += a * _softplus_neg_scalar(m)
            sig_neg = _sigmoid_scalar(-m)
            if is_lambda:
                delta_ndcg = 1.0 + 0.5 * _sigmoid_scalar(-abs(m))
                coeff = a * sig_neg * delta_ndcg
            else:
                coeff = a * sig_neg
            for j, v in nz:
                grad[j] -= coeff * v
        return total, grad

    step = 1.0
    prev_w: list[float] | None = None
    prev_grad: list[float] | None = None

    for _ in range(epochs):
        cur_loss, grad = eval_obj_and_grad(weights)
        grad_norm_sq = sum(g * g for g in grad)
        if grad_norm_sq < 1e-12:
            break

        if prev_w is not None and prev_grad is not None:
            dw = [a - b for a, b in zip(weights, prev_w)]
            dg = [a - b for a, b in zip(grad, prev_grad)]
            denom = sum(a * b for a, b in zip(dw, dg))
            if denom > 1e-12:
                step = min(20.0, max(1e-3, sum(a * a for a in dw) / denom))

        accepted = False
        trial_step = step
        for _ in range(25):
            cand = [w - trial_step * g for w, g in zip(weights, grad)]
            cand_obj = eval_obj(cand)
            if cand_obj <= cur_loss - 1e-4 * trial_step * grad_norm_sq:
                prev_w = weights
                prev_grad = grad
                weights = cand
                step = trial_step
                accepted = True
                break
            trial_step *= 0.5
        if not accepted:
            break

    return weights


def fit_linear_model(
    train_rows: list[dict[str, Any]],
    sample_weights: list[float],
    stds: list[float],
    l2: float,
    loss_kind: str,
    epochs: int,
    use_numpy: bool,
) -> tuple[list[float], list[float]]:
    """Returns (standardized_weights, raw_unstandardized_weights)."""
    if use_numpy and _np is not None:
        w_std = _fit_linear_numpy(train_rows, sample_weights, stds, l2, loss_kind, epochs)
    else:
        w_std = _fit_linear_pure_python(train_rows, sample_weights, stds, l2, loss_kind, epochs)
    w_raw = [ws / sd for ws, sd in zip(w_std, stds)]
    return w_std, w_raw


def quantize_linear_int32(w_raw: list[float], quant_scale: int) -> tuple[list[int], float]:
    """Quantizes raw linear weights to int32 safely (zero int64 dot-product overflow)."""
    max_abs = max((abs(w) for w in w_raw), default=0.0)
    if max_abs <= 1e-12:
        return [0] * len(w_raw), float(quant_scale)
    # Keep max |w_int| <= 2_000_000 so 35 * 5000 * 2_000_000 = 3.5e11 << Int64.MaxValue (9.2e18).
    eff_scale = min(float(quant_scale), 2_000_000.0 / max_abs)
    w_int = [_clamp_int(int(round(w * eff_scale)), -2_000_000, 2_000_000) for w in w_raw]
    return w_int, eff_scale


def linear_margins_float(rows: list[dict[str, Any]], w_raw: list[float]) -> list[float]:
    return [
        sum(wj * (r["winner"][j] - r["loser"][j]) for j, wj in enumerate(w_raw))
        for r in rows
    ]


def linear_margins_int32(rows: list[dict[str, Any]], w_int: list[int], eff_scale: float) -> list[float]:
    inv_scale = 1.0 / max(1.0, eff_scale)
    out: list[float] = []
    for r in rows:
        sw = sum(wj * xj for wj, xj in zip(w_int, r["winner"]))
        sl = sum(wj * xj for wj, xj in zip(w_int, r["loser"]))
        out.append((sw - sl) * inv_scale)
    return out


# =============================================================================
# TINY QUANTIZED 35->16->1 INT16/INT32 MLP (NumPy + Pure-Python Fallback)
# =============================================================================

def _fit_mlp_numpy(
    train_rows: list[dict[str, Any]],
    val_rows: list[dict[str, Any]],
    sample_weights: list[float],
    means: list[float],
    stds: list[float],
    hidden: int,
    l2: float,
    loss_kind: str,
    epochs: int,
    lr: float,
    seed: int,
) -> dict[str, Any]:
    np = _np
    assert np is not None
    dim = len(stds)
    n = len(train_rows)

    mean_arr = np.array(means, dtype=np.float64)
    inv_std = np.array([1.0 / s for s in stds], dtype=np.float64)
    xw = np.clip((np.array([r["winner"] for r in train_rows], dtype=np.float64) - mean_arr) * inv_std, -8.0, 8.0)
    xl = np.clip((np.array([r["loser"] for r in train_rows], dtype=np.float64) - mean_arr) * inv_std, -8.0, 8.0)
    alpha_all = np.array(sample_weights, dtype=np.float64)

    has_val = len(val_rows) > 0
    if has_val:
        vxw = np.clip((np.array([r["winner"] for r in val_rows], dtype=np.float64) - mean_arr) * inv_std, -8.0, 8.0)
        vxl = np.clip((np.array([r["loser"] for r in val_rows], dtype=np.float64) - mean_arr) * inv_std, -8.0, 8.0)
    else:
        vxw = vxl = None

    rng = random.Random(seed)
    s1 = math.sqrt(2.0 / dim)
    s2 = math.sqrt(1.0 / hidden)
    w1 = np.array([[rng.gauss(0.0, s1) for _ in range(dim)] for _ in range(hidden)], dtype=np.float64)
    b1 = np.zeros(hidden, dtype=np.float64)
    w2 = np.array([rng.gauss(0.0, s2) for _ in range(hidden)], dtype=np.float64)

    # Adam state
    m_w1 = np.zeros_like(w1); v_w1 = np.zeros_like(w1)
    m_b1 = np.zeros_like(b1); v_b1 = np.zeros_like(b1)
    m_w2 = np.zeros_like(w2); v_w2 = np.zeros_like(w2)
    beta1, beta2, eps = 0.9, 0.999, 1e-8

    batch_size = min(256, max(32, n))
    best_val_acc = -1.0
    best_val_loss = float("inf")
    best_params = (w1.copy(), b1.copy(), w2.copy())
    step_t = 0
    order = list(range(n))

    for epoch in range(epochs):
        rng.shuffle(order)
        cur_lr = lr * (0.1 + 0.9 * 0.5 * (1.0 + math.cos(math.pi * epoch / max(1, epochs))))

        for start in range(0, n, batch_size):
            idx = order[start:start + batch_size]
            bw = xw[idx]
            bl = xl[idx]
            ba = alpha_all[idx] / float(len(idx))

            # Forward pass (Siamese)
            zw = bw @ w1.T + b1
            hw = np.maximum(0.0, zw)
            sw = hw @ w2

            zl = bl @ w1.T + b1
            hl = np.maximum(0.0, zl)
            sl = hl @ w2

            margins = sw - sl
            sig_neg = 1.0 / (1.0 + np.exp(np.clip(margins, -60.0, 60.0)))
            if loss_kind == "lambdarank":
                abs_m = np.abs(margins)
                coeff = ba * sig_neg * (1.0 + 0.5 / (1.0 + np.exp(np.minimum(abs_m, 60.0))))
            else:
                coeff = ba * sig_neg

            # Gradients
            g_w2 = -((hw - hl).T @ coeff) + l2 * w2
            d_hw = -coeff[:, None] * w2[None, :] * (zw > 0.0)
            d_hl = coeff[:, None] * w2[None, :] * (zl > 0.0)
            g_w1 = (d_hw.T @ bw) + (d_hl.T @ bl) + l2 * w1
            g_b1 = np.sum(d_hw + d_hl, axis=0)

            step_t += 1
            bc1 = 1.0 - (beta1 ** step_t)
            bc2 = 1.0 - (beta2 ** step_t)

            m_w1 = beta1 * m_w1 + (1.0 - beta1) * g_w1
            v_w1 = beta2 * v_w1 + (1.0 - beta2) * (g_w1 * g_w1)
            w1 -= cur_lr * (m_w1 / bc1) / (np.sqrt(v_w1 / bc2) + eps)

            m_b1 = beta1 * m_b1 + (1.0 - beta1) * g_b1
            v_b1 = beta2 * v_b1 + (1.0 - beta2) * (g_b1 * g_b1)
            b1 -= cur_lr * (m_b1 / bc1) / (np.sqrt(v_b1 / bc2) + eps)

            m_w2 = beta1 * m_w2 + (1.0 - beta1) * g_w2
            v_w2 = beta2 * v_w2 + (1.0 - beta2) * (g_w2 * g_w2)
            w2 -= cur_lr * (m_w2 / bc1) / (np.sqrt(v_w2 / bc2) + eps)

        if has_val and ((epoch + 1) % 5 == 0 or epoch + 1 == epochs):
            vm = (np.maximum(0.0, vxw @ w1.T + b1) @ w2) - (np.maximum(0.0, vxl @ w1.T + b1) @ w2)
            v_acc = float(np.mean(vm > 0.0))
            v_abs = np.abs(vm)
            v_loss = float(np.mean(np.maximum(0.0, -vm) + np.log1p(np.exp(-np.minimum(v_abs, 60.0)))))
            if v_acc > best_val_acc or (v_acc == best_val_acc and v_loss < best_val_loss):
                best_val_acc = v_acc
                best_val_loss = v_loss
                best_params = (w1.copy(), b1.copy(), w2.copy())

    if has_val:
        w1, b1, w2 = best_params

    return {
        "W1": [[float(x) for x in row] for row in w1],
        "b1": [float(x) for x in b1],
        "W2": [float(x) for x in w2],
    }


def _fit_mlp_pure_python(
    train_rows: list[dict[str, Any]],
    val_rows: list[dict[str, Any]],
    sample_weights: list[float],
    means: list[float],
    stds: list[float],
    hidden: int,
    l2: float,
    loss_kind: str,
    epochs: int,
    lr: float,
    seed: int,
) -> dict[str, Any]:
    dim = len(stds)
    n = len(train_rows)
    inv_std = [1.0 / s for s in stds]

    def std_vec(vec: list[int]) -> list[float]:
        return [max(-8.0, min(8.0, (float(vec[j]) - means[j]) * inv_std[j])) for j in range(dim)]

    train_w = [std_vec(r["winner"]) for r in train_rows]
    train_l = [std_vec(r["loser"]) for r in train_rows]
    val_w = [std_vec(r["winner"]) for r in val_rows]
    val_l = [std_vec(r["loser"]) for r in val_rows]

    rng = random.Random(seed)
    s1 = math.sqrt(2.0 / dim)
    s2 = math.sqrt(1.0 / hidden)
    w1 = [[rng.gauss(0.0, s1) for _ in range(dim)] for _ in range(hidden)]
    b1 = [0.0] * hidden
    w2 = [rng.gauss(0.0, s2) for _ in range(hidden)]

    m_w1 = [[0.0] * dim for _ in range(hidden)]
    v_w1 = [[0.0] * dim for _ in range(hidden)]
    m_b1 = [0.0] * hidden
    v_b1 = [0.0] * hidden
    m_w2 = [0.0] * hidden
    v_w2 = [0.0] * hidden

    beta1, beta2, eps = 0.9, 0.999, 1e-8
    batch_size = min(128, max(32, n))
    order = list(range(n))
    step_t = 0
    is_lambda = loss_kind == "lambdarank"
    best_val_acc = -1.0
    best_val_loss = float("inf")
    best_params = ([row[:] for row in w1], b1[:], w2[:])

    for epoch in range(epochs):
        rng.shuffle(order)
        cur_lr = lr * (0.1 + 0.9 * 0.5 * (1.0 + math.cos(math.pi * epoch / max(1, epochs))))

        for start in range(0, n, batch_size):
            batch_idx = order[start:start + batch_size]
            inv_bs = 1.0 / float(len(batch_idx))
            g_w1 = [[l2 * w1[h][j] for j in range(dim)] for h in range(hidden)]
            g_b1 = [0.0] * hidden
            g_w2 = [l2 * w2[h] for h in range(hidden)]

            for idx in batch_idx:
                xw = train_w[idx]
                xl = train_l[idx]
                ba = sample_weights[idx] * inv_bs

                hw = [0.0] * hidden
                hl = [0.0] * hidden
                sw = 0.0
                sl = 0.0
                for h in range(hidden):
                    row_w1 = w1[h]
                    zw = b1[h] + sum(row_w1[j] * xw[j] for j in range(dim))
                    zl = b1[h] + sum(row_w1[j] * xl[j] for j in range(dim))
                    if zw > 0.0:
                        hw[h] = zw
                        sw += w2[h] * zw
                    if zl > 0.0:
                        hl[h] = zl
                        sl += w2[h] * zl

                margin = sw - sl
                sig_neg = _sigmoid_scalar(-margin)
                if is_lambda:
                    coeff = ba * sig_neg * (1.0 + 0.5 * _sigmoid_scalar(-abs(margin)))
                else:
                    coeff = ba * sig_neg

                for h in range(hidden):
                    g_w2[h] -= coeff * (hw[h] - hl[h])
                    dw = (-coeff * w2[h]) if hw[h] > 0.0 else 0.0
                    dl = (coeff * w2[h]) if hl[h] > 0.0 else 0.0
                    g_b1[h] += dw + dl
                    if dw != 0.0 or dl != 0.0:
                        g_row = g_w1[h]
                        for j in range(dim):
                            g_row[j] += dw * xw[j] + dl * xl[j]

            step_t += 1
            bc1 = 1.0 - (beta1 ** step_t)
            bc2 = 1.0 - (beta2 ** step_t)

            for h in range(hidden):
                m_w2[h] = beta1 * m_w2[h] + (1.0 - beta1) * g_w2[h]
                v_w2[h] = beta2 * v_w2[h] + (1.0 - beta2) * (g_w2[h] * g_w2[h])
                w2[h] -= cur_lr * (m_w2[h] / bc1) / (math.sqrt(v_w2[h] / bc2) + eps)

                m_b1[h] = beta1 * m_b1[h] + (1.0 - beta1) * g_b1[h]
                v_b1[h] = beta2 * v_b1[h] + (1.0 - beta2) * (g_b1[h] * g_b1[h])
                b1[h] -= cur_lr * (m_b1[h] / bc1) / (math.sqrt(v_b1[h] / bc2) + eps)

                row_w1 = w1[h]
                row_m = m_w1[h]
                row_v = v_w1[h]
                row_g = g_w1[h]
                for j in range(dim):
                    gj = row_g[j]
                    row_m[j] = beta1 * row_m[j] + (1.0 - beta1) * gj
                    row_v[j] = beta2 * row_v[j] + (1.0 - beta2) * (gj * gj)
                    row_w1[j] -= cur_lr * (row_m[j] / bc1) / (math.sqrt(row_v[j] / bc2) + eps)

        if val_w and ((epoch + 1) % 5 == 0 or epoch + 1 == epochs):
            correct = 0
            loss_sum = 0.0
            for xw, xl in zip(val_w, val_l):
                sw = sum(w2[h] * max(0.0, b1[h] + sum(w1[h][j] * xw[j] for j in range(dim))) for h in range(hidden))
                sl = sum(w2[h] * max(0.0, b1[h] + sum(w1[h][j] * xl[j] for j in range(dim))) for h in range(hidden))
                m = sw - sl
                if m > 0.0:
                    correct += 1
                loss_sum += _softplus_neg_scalar(m)
            v_acc = correct / len(val_w)
            v_loss = loss_sum / len(val_w)
            if v_acc > best_val_acc or (v_acc == best_val_acc and v_loss < best_val_loss):
                best_val_acc = v_acc
                best_val_loss = v_loss
                best_params = ([row[:] for row in w1], b1[:], w2[:])

    if val_w:
        w1, b1, w2 = best_params

    return {"W1": w1, "b1": b1, "W2": w2}


def fit_mlp_model(
    train_rows: list[dict[str, Any]],
    val_rows: list[dict[str, Any]],
    sample_weights: list[float],
    means: list[float],
    stds: list[float],
    hidden: int,
    l2: float,
    loss_kind: str,
    epochs: int,
    lr: float,
    seed: int,
    use_numpy: bool,
) -> dict[str, Any]:
    if use_numpy and _np is not None:
        return _fit_mlp_numpy(
            train_rows, val_rows, sample_weights, means, stds, hidden, l2, loss_kind, epochs, lr, seed
        )
    return _fit_mlp_pure_python(
        train_rows, val_rows, sample_weights, means, stds, hidden, l2, loss_kind, epochs, lr, seed
    )


def mlp_margins_float(
    rows: list[dict[str, Any]],
    mlp: dict[str, Any],
    means: list[float],
    stds: list[float],
) -> list[float]:
    w1 = mlp["W1"]
    b1 = mlp["b1"]
    w2 = mlp["W2"]
    hidden = len(w2)
    dim = len(stds)
    inv_std = [1.0 / s for s in stds]

    def score_vec(vec: list[int]) -> float:
        x = [max(-8.0, min(8.0, (float(vec[j]) - means[j]) * inv_std[j])) for j in range(dim)]
        s = 0.0
        for h in range(hidden):
            row_w1 = w1[h]
            z = b1[h] + sum(row_w1[j] * x[j] for j in range(dim))
            if z > 0.0:
                s += w2[h] * z
        return s

    return [score_vec(r["winner"]) - score_vec(r["loser"]) for r in rows]


def quantize_mlp_int16_int32(
    mlp: dict[str, Any],
    means: list[float],
    stds: list[float],
) -> dict[str, Any]:
    """Quantizes 35->H->1 MLP into exact Q8 int16 weights + Q16 int32 biases + Q12 int32 standardization."""
    dim = len(stds)
    hidden = len(mlp["W2"])
    feat_mean_int = [int(round(m)) for m in means]
    feat_inv_std_q12 = [_clamp_int(int(round(4096.0 / max(s, 1e-6))), 1, 65535) for s in stds]

    # Rescale layer 1 / layer 2 if any weight exceeds Q8 int16 range ([-127.99, +127.99]).
    max_w1 = max((abs(v) for row in mlp["W1"] for v in row), default=1.0)
    max_b1 = max((abs(v) for v in mlp["b1"]), default=1.0)
    s1_scale = 256.0
    if max_w1 * s1_scale > 32000.0 or max_b1 * 65536.0 > 1_500_000_000.0:
        factor = max((max_w1 * s1_scale) / 30000.0, (max_b1 * 65536.0) / 1_500_000_000.0)
        s1_scale /= factor

    max_w2 = max((abs(v) for v in mlp["W2"]), default=1.0)
    s2_scale = 256.0
    if max_w2 * s2_scale > 32000.0:
        s2_scale = 30000.0 / max_w2

    w1_q8_flat: list[int] = []
    for h in range(hidden):
        for j in range(dim):
            w1_q8_flat.append(_clamp_int(int(round(mlp["W1"][h][j] * s1_scale)), -32767, 32767))

    # Since xq is Q8 (256x) and w1 is s1_scale, b1 needs 256 * s1_scale.
    b1_scale = 256.0 * s1_scale
    b1_q16 = [_clamp_int(int(round(mlp["b1"][h] * b1_scale)), -2_000_000_000, 2_000_000_000) for h in range(hidden)]
    w2_q8 = [_clamp_int(int(round(mlp["W2"][h] * s2_scale)), -32767, 32767) for h in range(hidden)]

    return {
        "input_dim": dim,
        "hidden_dim": hidden,
        "feature_mean": feat_mean_int,
        "feature_inv_std_q12": feat_inv_std_q12,
        "w1_q8": w1_q8_flat,
        "b1_q16": b1_q16,
        "w2_q8": w2_q8,
        "output_logit_scale": (s1_scale / 256.0) * s2_scale * 256.0,
    }


def eval_quantized_mlp_score(vec: list[int], qmlp: dict[str, Any]) -> int:
    """Bit-for-bit identical integer arithmetic to the emitted C# EvaluateMlp()."""
    dim = qmlp["input_dim"]
    hidden = qmlp["hidden_dim"]
    mean_int = qmlp["feature_mean"]
    inv_std_q12 = qmlp["feature_inv_std_q12"]
    w1_q8 = qmlp["w1_q8"]
    b1_q16 = qmlp["b1_q16"]
    w2_q8 = qmlp["w2_q8"]

    xq = [
        _clamp_int(((vec[j] - mean_int[j]) * inv_std_q12[j]) >> 4, -2048, 2048)
        for j in range(dim)
    ]

    total = 0
    for h in range(hidden):
        row_off = h * dim
        acc = b1_q16[h]
        for j in range(dim):
            acc += w1_q8[row_off + j] * xq[j]
        if acc > 0:
            total += w2_q8[h] * (acc >> 8)
    return total


def mlp_margins_quantized(rows: list[dict[str, Any]], qmlp: dict[str, Any]) -> list[float]:
    inv_scale = 1.0 / max(1.0, float(qmlp["output_logit_scale"]))
    return [
        (eval_quantized_mlp_score(r["winner"], qmlp) - eval_quantized_mlp_score(r["loser"], qmlp)) * inv_scale
        for r in rows
    ]


# =============================================================================
# ZERO-ALLOC C# STATIC ReadOnlySpan<int> / ReadOnlySpan<short> CODEGEN
# =============================================================================

def format_csharp_weight_table(
    feature_names: list[str],
    w_int: list[int],
    linear_meta: dict[str, Any],
    qmlp: dict[str, Any] | None = None,
    mlp_meta: dict[str, Any] | None = None,
) -> str:
    dim = len(feature_names)
    lines: list[str] = [
        "// Auto-generated by tools/search/train_hot_evaluator.py (Stage 8F P4 Offline Auto-Calibration).",
        "// Zero heap allocation: C# static ReadOnlySpan<int> / ReadOnlySpan<short> tables compile directly into RVA data.",
        "using System.Runtime.CompilerServices;",
        "",
        "namespace CombatSolver.HotSearch;",
        "",
        "internal static class HotCalibratedWeights",
        "{",
        f"    public const int FeatureCount = {dim};",
        f"    // Train acc={linear_meta['train_quant_acc']:.4f} | Val acc={linear_meta['val_quant_acc']:.4f} "
        f"| Baseline Val acc={linear_meta['baseline_val_acc']:.4f} | Loss={linear_meta['loss_kind']} L2={linear_meta['l2']:g}",
        "    public static ReadOnlySpan<int> LinearWeights =>",
        "    [",
    ]
    for j, (name, wj) in enumerate(zip(feature_names, w_int)):
        comma = "," if j + 1 < dim else ","
        lines.append(f"        {wj,10}{comma} // [{j:02d}] {name}")
    lines.extend([
        "    ];",
        "",
        "    [MethodImpl(MethodImplOptions.AggressiveInlining)]",
        "    public static long EvaluateLinear(ReadOnlySpan<int> features)",
        "    {",
        "        ReadOnlySpan<int> weights = LinearWeights;",
        "        long score = 0;",
        "        for (int i = 0; i < FeatureCount; i++)",
        "            score += (long)weights[i] * features[i];",
        "        return score;",
        "    }",
    ])

    if qmlp is not None and mlp_meta is not None:
        hidden = qmlp["hidden_dim"]
        lines.extend([
            "",
            f"    public const int MlpHiddenSize = {hidden};",
            f"    // Quantized {dim}->{hidden}->1 int16/int32 MLP | Train acc={mlp_meta['train_quant_acc']:.4f} "
            f"| Val acc={mlp_meta['val_quant_acc']:.4f}",
            "    public static ReadOnlySpan<int> FeatureMean =>",
            "    [",
        ])
        for row_start in range(0, dim, 10):
            chunk = ", ".join(f"{v,5}" for v in qmlp["feature_mean"][row_start:row_start + 10])
            lines.append(f"        {chunk},")
        lines.extend([
            "    ];",
            "",
            "    public static ReadOnlySpan<int> FeatureInvStdQ12 =>",
            "    [",
        ])
        for row_start in range(0, dim, 10):
            chunk = ", ".join(f"{v,5}" for v in qmlp["feature_inv_std_q12"][row_start:row_start + 10])
            lines.append(f"        {chunk},")
        lines.extend([
            "    ];",
            "",
            f"    // Row-major [{hidden} x {dim}] Q8 int16 hidden weights",
            "    public static ReadOnlySpan<short> MlpW1Q8 =>",
            "    [",
        ])
        for h in range(hidden):
            row_off = h * dim
            row_vals = qmlp["w1_q8"][row_off:row_off + dim]
            lines.append(f"        // Hidden unit {h}")
            for c_start in range(0, dim, 12):
                chunk = ", ".join(f"{v,6}" for v in row_vals[c_start:c_start + 12])
                lines.append(f"        {chunk},")
        lines.extend([
            "    ];",
            "",
            "    public static ReadOnlySpan<int> MlpB1Q16 =>",
            "    [",
        ])
        for row_start in range(0, hidden, 8):
            chunk = ", ".join(f"{v,9}" for v in qmlp["b1_q16"][row_start:row_start + 8])
            lines.append(f"        {chunk},")
        lines.extend([
            "    ];",
            "",
            "    public static ReadOnlySpan<short> MlpW2Q8 =>",
            "    [",
        ])
        for row_start in range(0, hidden, 8):
            chunk = ", ".join(f"{v,6}" for v in qmlp["w2_q8"][row_start:row_start + 8])
            lines.append(f"        {chunk},")
        lines.extend([
            "    ];",
            "",
            "    [MethodImpl(MethodImplOptions.AggressiveInlining)]",
            "    public static long EvaluateMlp(ReadOnlySpan<int> features)",
            "    {",
            "        ReadOnlySpan<int> mean = FeatureMean;",
            "        ReadOnlySpan<int> invStd = FeatureInvStdQ12;",
            "        ReadOnlySpan<short> w1 = MlpW1Q8;",
            "        ReadOnlySpan<int> b1 = MlpB1Q16;",
            "        ReadOnlySpan<short> w2 = MlpW2Q8;",
            "        Span<int> xq = stackalloc int[FeatureCount];",
            "        for (int j = 0; j < FeatureCount; j++)",
            "        {",
            "            int q = ((features[j] - mean[j]) * invStd[j]) >> 4;",
            "            xq[j] = Math.Clamp(q, -2048, 2048);",
            "        }",
            "        long score = 0;",
            "        for (int h = 0; h < MlpHiddenSize; h++)",
            "        {",
            "            int rowOff = h * FeatureCount;",
            "            int acc = b1[h];",
            "            for (int j = 0; j < FeatureCount; j++)",
            "                acc += w1[rowOff + j] * xq[j];",
            "            if (acc > 0)",
            "                score += (long)w2[h] * (acc >> 8);",
            "        }",
            "        return score;",
            "    }",
        ])

    lines.append("}")
    return "\n".join(lines) + "\n"


def _check_balanced_delimiters(text: str) -> None:
    pairs = {"{": "}", "(": ")", "[": "]"}
    closing = {v: k for k, v in pairs.items()}
    stack: list[str] = []
    in_line_comment = False
    in_string = False
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if in_line_comment:
            if ch == "\n":
                in_line_comment = False
            i += 1
            continue
        if in_string:
            if ch == "\\" and i + 1 < n:
                i += 2
                continue
            if ch == '"':
                in_string = False
            i += 1
            continue
        if ch == "/" and nxt == "/":
            in_line_comment = True
            i += 2
            continue
        if ch == '"':
            in_string = True
            i += 1
            continue
        if ch in pairs:
            stack.append(ch)
        elif ch in closing:
            if not stack or stack[-1] != closing[ch]:
                raise AssertionError(f"Unbalanced delimiter '{ch}' at offset {i}")
            stack.pop()
        i += 1
    if stack:
        raise AssertionError(f"Unclosed delimiters: {stack}")


# =============================================================================
# BUILT-IN SELF-TEST (Verifies Both NumPy and Pure-Python Fallback End-to-End)
# =============================================================================

def run_self_test() -> int:
    print("=== train_hot_evaluator.py --self-test ===")
    dim = len(DEFAULT_FEATURE_NAMES)
    rng = random.Random(202604)

    # Known realistic ground-truth weights over the 35 domain-agnostic features
    true_w = [0.0] * dim
    true_w[0] = 0.08     # player_hp
    true_w[2] = 0.05     # player_block
    true_w[3] = -0.10    # unblocked_incoming
    true_w[4] = -0.25    # lethal_incoming_excess
    true_w[5] = -0.09    # potion_cost_hp
    true_w[6] = -0.03    # enemy_hp_sum
    true_w[8] = -0.40    # alive_enemy_count
    true_w[12] = 0.04    # poison_expected_dmg
    true_w[14] = 0.04    # doom_quadratic_val
    true_w[17] = 0.06    # orb_passive_block
    true_w[18] = 0.03    # max_dark_evoke
    true_w[21] = 0.05    # recurring_buff_stacks
    true_w[24] = 0.04    # enemy_debuff_stacks
    true_w[26] = 0.03    # osty_hp
    true_w[27] = 0.08    # playable_hand_count
    true_w[28] = 0.25    # energy
    true_w[32] = -0.06   # deck_clutter
    true_w[33] = 0.015   # scaling_x_rem_turns
    true_w[34] = 0.05    # focus_x_orb_count

    chars = ["IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER", "FOOL"]
    rooms = ["Normal", "Elite", "Boss"]
    kinds = ["step_mid_turn", "turn_pre_end", "turn_post_end"]

    def synth_state() -> list[int]:
        hp = rng.randint(25, 95)
        max_hp = max(hp, rng.randint(75, 110))
        block = rng.randint(0, 35)
        unblocked = rng.randint(0, 30)
        lethal = max(0, unblocked - hp + 10) if unblocked >= hp else 0
        rem_turns = rng.randint(1, 8)
        orbs = rng.randint(0, 5)
        focus = rng.randint(-2, 6)
        rec_buff = rng.randint(0, 25)
        return [
            hp, max_hp, block, unblocked, lethal, rng.randint(0, 18),
            rng.randint(20, 350), rng.randint(0, 25), rng.randint(1, 3), rng.randint(0, 1),
            rng.randint(0, 40), rem_turns, rng.randint(0, 90), rng.randint(0, 3),
            rng.randint(0, 80), rng.randint(0, 1), rng.randint(0, 24), rng.randint(0, 20),
            rng.randint(0, 80), orbs, max(orbs, rng.randint(0, 6)), rec_buff,
            rng.randint(-5, 25), rng.randint(0, 10), rng.randint(0, 15), rng.randint(0, 15),
            rng.randint(0, 50), rng.randint(0, 6), rng.randint(0, 5), rng.randint(0, 6),
            rng.randint(0, 30), rng.randint(10, 30), rng.randint(0, 6),
            rec_buff * max(0, rem_turns - 1), focus * orbs,
        ]

    def make_split(prefix: str, count: int) -> list[dict[str, Any]]:
        out: list[dict[str, Any]] = []
        for i in range(count):
            a = synth_state()
            b = synth_state()
            sa = sum(w * x for w, x in zip(true_w, a)) + 0.002 * a[21] * a[11]
            sb = sum(w * x for w, x in zip(true_w, b)) + 0.002 * b[21] * b[11]
            if abs(sa - sb) < 0.25:
                a[0] = min(99, a[0] + 10)
                sa += 0.8
            w_vec, l_vec = (a, b) if sa >= sb else (b, a)
            out.append({
                "fight_id": f"{prefix}_{i // 4:03d}",
                "character_id": chars[i % len(chars)],
                "encounter_id": "SYNTH_ENCOUNTER",
                "room_type": rooms[i % len(rooms)],
                "game_mode": "Standard",
                "turn": 1 + (i % 4),
                "pair_kind": kinds[i % len(kinds)],
                "teacher_v_centi": -200,
                "winner_eval_score": 100000 if (i % 3 != 0) else 80000,
                "loser_eval_score": 90000,
                "winner": w_vec,
                "loser": l_vec,
            })
        return out

    train_rows = make_split("trn", 240)
    val_rows = make_split("val", 120)
    sw = compute_sample_weights(train_rows, fight_balance=True, hard_boost=1.5)
    means, stds = compute_train_standardization(train_rows, dim)

    # Test pure-Python backend unconditionally
    _, w_raw_py = fit_linear_model(train_rows, sw, stds, l2=0.02, loss_kind="lambdarank", epochs=120, use_numpy=False)
    w_int_py, eff_scale_py = quantize_linear_int32(w_raw_py, quant_scale=10000)
    val_lin_py = evaluate_scores_summary(val_rows, linear_margins_int32(val_rows, w_int_py, eff_scale_py))
    print(f"[pure-python] linear int32 val_acc={val_lin_py['accuracy']:.4f} (n={val_lin_py['pairs']})")
    if val_lin_py["accuracy"] < 0.85:
        raise AssertionError(f"Pure-Python linear val accuracy too low: {val_lin_py['accuracy']}")

    mlp_py = fit_mlp_model(
        train_rows, val_rows, sw, means, stds, hidden=16, l2=1e-3,
        loss_kind="lambdarank", epochs=35, lr=0.03, seed=42, use_numpy=False
    )
    qmlp_py = quantize_mlp_int16_int32(mlp_py, means, stds)
    val_mlp_py = evaluate_scores_summary(val_rows, mlp_margins_quantized(val_rows, qmlp_py))
    print(f"[pure-python] mlp int16/32 val_acc={val_mlp_py['accuracy']:.4f}")
    if val_mlp_py["accuracy"] < 0.80:
        raise AssertionError(f"Pure-Python quantized MLP val accuracy too low: {val_mlp_py['accuracy']}")

    # If numpy is installed, verify numpy backend matches pure-Python linear solution closely
    if _np is not None:
        _, w_raw_np = fit_linear_model(train_rows, sw, stds, l2=0.02, loss_kind="lambdarank", epochs=120, use_numpy=True)
        max_diff = max(abs(a - b) for a, b in zip(w_raw_py, w_raw_np))
        w_int_np, eff_scale_np = quantize_linear_int32(w_raw_np, quant_scale=10000)
        val_lin_np = evaluate_scores_summary(val_rows, linear_margins_int32(val_rows, w_int_np, eff_scale_np))
        mlp_np = fit_mlp_model(
            train_rows, val_rows, sw, means, stds, hidden=16, l2=1e-3,
            loss_kind="lambdarank", epochs=60, lr=0.03, seed=42, use_numpy=True
        )
        qmlp_np = quantize_mlp_int16_int32(mlp_np, means, stds)
        val_mlp_np = evaluate_scores_summary(val_rows, mlp_margins_quantized(val_rows, qmlp_np))
        print(
            f"[numpy]       linear int32 val_acc={val_lin_np['accuracy']:.4f} "
            f"(max_w_diff vs py={max_diff:.2e}) | mlp int16/32 val_acc={val_mlp_np['accuracy']:.4f}"
        )
        if max_diff > 1e-3:
            raise AssertionError(f"NumPy vs pure-Python linear weights diverged: max_diff={max_diff}")
        if val_mlp_np["accuracy"] < 0.80:
            raise AssertionError(f"NumPy quantized MLP val accuracy too low: {val_mlp_np['accuracy']}")

    cs_code = format_csharp_weight_table(
        list(DEFAULT_FEATURE_NAMES),
        w_int_py,
        {"train_quant_acc": 0.95, "val_quant_acc": val_lin_py["accuracy"], "baseline_val_acc": 0.67, "loss_kind": "lambdarank", "l2": 0.02},
        qmlp=qmlp_py,
        mlp_meta={"train_quant_acc": 0.95, "val_quant_acc": val_mlp_py["accuracy"]},
    )
    _check_balanced_delimiters(cs_code)
    if "ReadOnlySpan<int> LinearWeights" not in cs_code or "ReadOnlySpan<short> MlpW1Q8" not in cs_code:
        raise AssertionError("Generated C# snippet missing expected ReadOnlySpan tables")

    print("SELF_TEST_OK")
    return 0


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    default_bench = REPO_ROOT / ".local" / "bench"
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--train", type=Path, default=default_bench / "teacher-pairs-train.jsonl",
                        help="Path to training preference pairs JSONL.")
    parser.add_argument("--val", type=Path, default=default_bench / "teacher-pairs-val.jsonl",
                        help="Path to held-out validation preference pairs JSONL.")
    parser.add_argument("--schema", type=Path, default=None,
                        help="Optional path to teacher-pairs-schema.json (auto-detected next to --train if omitted).")
    parser.add_argument("--pair-kinds", type=str, default="all",
                        help="Comma-separated pair_kind filter (e.g. turn_post_end,turn_pre_end) or 'all'.")
    parser.add_argument("--model", choices=("linear", "mlp", "both"), default="both",
                        help="Which model family to fit (default: both linear int32 and 35->16->1 quantized MLP).")
    parser.add_argument("--loss", choices=("logistic", "lambdarank"), default="lambdarank",
                        help="Pairwise ranking loss (default: lambdarank).")
    parser.add_argument("--l2", type=float, default=0.01,
                        help="L2 regularization strength for linear model (ignored if --l2-grid is set).")
    parser.add_argument("--l2-grid", type=str, default="",
                        help="Optional comma-separated L2 grid (e.g. 0.001,0.005,0.01,0.05,0.2) selected on val.")
    parser.add_argument("--mlp-l2", type=float, default=2e-3,
                        help="L2 weight decay for the 35->16->1 MLP (default: 0.002).")
    parser.add_argument("--hidden", type=int, default=16,
                        help="Hidden layer width for tiny quantized MLP (default: 16).")
    parser.add_argument("--epochs", type=int, default=300,
                        help="Max optimization epochs for linear model (default: 300).")
    parser.add_argument("--mlp-epochs", type=int, default=200,
                        help="Training epochs for tiny MLP (default: 200).")
    parser.add_argument("--mlp-lr", type=float, default=0.02,
                        help="Initial Adam learning rate for tiny MLP (default: 0.02).")
    parser.add_argument("--quant-scale", type=int, default=10000,
                        help="Integer scale factor for quantized int32 linear weights (default: 10000).")
    parser.add_argument("--hard-boost", type=float, default=1.5,
                        help="Weight multiplier for pairs where baseline HotEvaluator disagreed with Teacher (default: 1.5).")
    parser.add_argument("--no-fight-balance", action="store_true",
                        help="Disable 1/pairs_in_fight weighting.")
    parser.add_argument("--no-numpy", action="store_true",
                        help="Force pure-Python stdlib backend even when numpy is installed.")
    parser.add_argument("--seed", type=int, default=42,
                        help="Deterministic RNG seed (default: 42).")
    parser.add_argument("--out-cs", type=Path, default=None,
                        help="Optional path to write ready-to-paste C# weight table file.")
    parser.add_argument("--out-report", type=Path, default=None,
                        help="Optional path to write full JSON metrics report.")
    parser.add_argument("--self-test", action="store_true",
                        help="Run built-in synthetic end-to-end verification of both backends and exit.")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if args.self_test:
        return run_self_test()

    t0 = time.perf_counter()
    use_numpy = (_np is not None) and (not args.no_numpy)
    backend_name = f"numpy {_np.__version__}" if use_numpy and _np is not None else "pure-python"

    feature_names = load_schema_names(args.train, args.schema)
    dim = len(feature_names)
    allowed_kinds = None if args.pair_kinds.strip().lower() == "all" else {
        k.strip() for k in args.pair_kinds.split(",") if k.strip()
    }

    train_rows = load_pairs_jsonl(args.train, dim, allowed_kinds)
    val_rows = load_pairs_jsonl(args.val, dim, allowed_kinds) if args.val.is_file() else []

    if not train_rows:
        print(f"ERROR: No training pairs loaded from {args.train} (filter={args.pair_kinds}).", file=sys.stderr)
        return 2

    train_fights = len({r["fight_id"] for r in train_rows})
    val_fights = len({r["fight_id"] for r in val_rows})
    print(
        f"=== Stage 8F HotEvaluator Offline Trainer ({backend_name}) ===\n"
        f"features={dim} loss={args.loss} model={args.model} pair_kinds={args.pair_kinds}\n"
        f"train={len(train_rows)} pairs ({train_fights} fights) | val={len(val_rows)} pairs ({val_fights} fights)"
    )

    # Leakage guard: assert zero fight_id overlap between train and val splits.
    overlap = {r["fight_id"] for r in train_rows} & {r["fight_id"] for r in val_rows}
    if overlap:
        print(f"ERROR: Data leakage detected! {len(overlap)} shared fight_id(s) between train and val.", file=sys.stderr)
        return 3

    sample_weights = compute_sample_weights(
        train_rows, fight_balance=not args.no_fight_balance, hard_boost=args.hard_boost
    )
    means, stds = compute_train_standardization(train_rows, dim)

    baseline_train = evaluate_baseline_evaluator(train_rows)
    baseline_val = evaluate_baseline_evaluator(val_rows) if val_rows else None

    # 1. Fit Linear int32 model (with optional L2 grid search on held-out val)
    l2_candidates = (
        [float(x.strip()) for x in args.l2_grid.split(",") if x.strip()]
        if args.l2_grid.strip()
        else [args.l2]
    )

    best_linear: dict[str, Any] | None = None
    for l2_val in l2_candidates:
        w_std, w_raw = fit_linear_model(
            train_rows, sample_weights, stds, l2=l2_val, loss_kind=args.loss,
            epochs=args.epochs, use_numpy=use_numpy
        )
        w_int, eff_scale = quantize_linear_int32(w_raw, args.quant_scale)
        tr_flt = evaluate_scores_summary(train_rows, linear_margins_float(train_rows, w_raw))
        tr_q = evaluate_scores_summary(train_rows, linear_margins_int32(train_rows, w_int, eff_scale))
        va_flt = evaluate_scores_summary(val_rows, linear_margins_float(val_rows, w_raw)) if val_rows else None
        va_q = evaluate_scores_summary(val_rows, linear_margins_int32(val_rows, w_int, eff_scale)) if val_rows else None

        sel_acc = va_q["accuracy"] if va_q is not None else tr_q["accuracy"]
        sel_loss = va_flt["loss"] if va_flt is not None else tr_flt["loss"]
        cand = {
            "l2": l2_val,
            "w_std": w_std,
            "w_raw": w_raw,
            "w_int": w_int,
            "eff_scale": eff_scale,
            "train_float": tr_flt,
            "train_quant": tr_q,
            "val_float": va_flt,
            "val_quant": va_q,
            "sel_acc": sel_acc,
            "sel_loss": sel_loss,
        }
        if best_linear is None or (cand["sel_acc"] > best_linear["sel_acc"]) or (
            cand["sel_acc"] == best_linear["sel_acc"] and cand["sel_loss"] < best_linear["sel_loss"]
        ):
            best_linear = cand

    assert best_linear is not None

    # 2. Optionally fit tiny 35->H->1 quantized int16/int32 MLP
    mlp_result: dict[str, Any] | None = None
    qmlp: dict[str, Any] | None = None
    if args.model in ("mlp", "both"):
        mlp_params = fit_mlp_model(
            train_rows, val_rows, sample_weights, means, stds,
            hidden=args.hidden, l2=args.mlp_l2, loss_kind=args.loss,
            epochs=args.mlp_epochs, lr=args.mlp_lr, seed=args.seed, use_numpy=use_numpy,
        )
        qmlp = quantize_mlp_int16_int32(mlp_params, means, stds)
        mlp_result = {
            "hidden": args.hidden,
            "l2": args.mlp_l2,
            "train_float": evaluate_scores_summary(train_rows, mlp_margins_float(train_rows, mlp_params, means, stds)),
            "train_quant": evaluate_scores_summary(train_rows, mlp_margins_quantized(train_rows, qmlp)),
            "val_float": evaluate_scores_summary(val_rows, mlp_margins_float(val_rows, mlp_params, means, stds)) if val_rows else None,
            "val_quant": evaluate_scores_summary(val_rows, mlp_margins_quantized(val_rows, qmlp)) if val_rows else None,
        }

    elapsed_s = time.perf_counter() - t0

    # Print summary comparison table
    print()
    print("| Model | Train Pair Acc | Val Pair Acc | Val Loss |")
    print("|---|---:|---:|---:|")
    b_va_acc = f"{baseline_val['accuracy']:.2%}" if baseline_val else "-"
    b_va_loss = f"{baseline_val['loss']:.4f}" if baseline_val else "-"
    print(f"| Baseline HotEvaluator | {baseline_train['accuracy']:.2%} | {b_va_acc} | {b_va_loss} |")

    lf_va_acc = f"{best_linear['val_float']['accuracy']:.2%}" if best_linear["val_float"] else "-"
    lf_va_loss = f"{best_linear['val_float']['loss']:.4f}" if best_linear["val_float"] else "-"
    print(f"| Linear (float, L2={best_linear['l2']:g}) | {best_linear['train_float']['accuracy']:.2%} | {lf_va_acc} | {lf_va_loss} |")

    lq_va_acc = f"{best_linear['val_quant']['accuracy']:.2%}" if best_linear["val_quant"] else "-"
    lq_va_loss = f"{best_linear['val_quant']['loss']:.4f}" if best_linear["val_quant"] else "-"
    print(f"| **Linear (int32)** | **{best_linear['train_quant']['accuracy']:.2%}** | **{lq_va_acc}** | **{lq_va_loss}** |")

    if mlp_result is not None:
        mf_va_acc = f"{mlp_result['val_float']['accuracy']:.2%}" if mlp_result["val_float"] else "-"
        mf_va_loss = f"{mlp_result['val_float']['loss']:.4f}" if mlp_result["val_float"] else "-"
        mq_va_acc = f"{mlp_result['val_quant']['accuracy']:.2%}" if mlp_result["val_quant"] else "-"
        mq_va_loss = f"{mlp_result['val_quant']['loss']:.4f}" if mlp_result["val_quant"] else "-"
        print(f"| MLP {dim}->{args.hidden}->1 (float) | {mlp_result['train_float']['accuracy']:.2%} | {mf_va_acc} | {mf_va_loss} |")
        print(f"| **MLP {dim}->{args.hidden}->1 (int16/32)** | **{mlp_result['train_quant']['accuracy']:.2%}** | **{mq_va_acc}** | **{mq_va_loss}** |")

    # Print held-out val slice breakdown
    val_eval_for_slices = best_linear["val_quant"] or best_linear["train_quant"]
    slice_label = "Held-Out Val" if best_linear["val_quant"] else "Train"
    print(f"\n### {slice_label} Breakdown (Linear int32 vs Baseline)")
    base_slices = (baseline_val or baseline_train)["slices"]
    for field in ("pair_kind", "character_id", "room_type", "turn"):
        items = val_eval_for_slices["slices"].get(field, {})
        if not items:
            continue
        parts = []
        for k, stat in items.items():
            b_acc = base_slices.get(field, {}).get(k, {}).get("accuracy", 0.0)
            parts.append(f"{k}:{stat['accuracy']:.1%}(base {b_acc:.1%},n={stat['pairs']})")
        print(f"  {field:<13}: " + " | ".join(parts))

    # Print top standardized feature weights for physics sanity check
    ranked_feats = sorted(
        range(dim), key=lambda j: abs(best_linear["w_std"][j]), reverse=True
    )
    print("\n### Top Standardized Feature Weights (Sanity Check)")
    for j in ranked_feats[:12]:
        print(
            f"  [{j:02d}] {feature_names[j]:<24} w_std={best_linear['w_std'][j]:+8.4f}  "
            f"w_int32={best_linear['w_int'][j]:+10d}  (mean={means[j]:.2f}, std={stds[j]:.2f})"
        )

    linear_meta = {
        "train_quant_acc": best_linear["train_quant"]["accuracy"],
        "val_quant_acc": best_linear["val_quant"]["accuracy"] if best_linear["val_quant"] else 0.0,
        "baseline_val_acc": baseline_val["accuracy"] if baseline_val else baseline_train["accuracy"],
        "loss_kind": args.loss,
        "l2": best_linear["l2"],
    }
    mlp_meta = (
        {
            "train_quant_acc": mlp_result["train_quant"]["accuracy"],
            "val_quant_acc": mlp_result["val_quant"]["accuracy"] if mlp_result["val_quant"] else 0.0,
        }
        if mlp_result is not None
        else None
    )

    cs_code = format_csharp_weight_table(
        feature_names,
        best_linear["w_int"],
        linear_meta,
        qmlp=qmlp,
        mlp_meta=mlp_meta,
    )

    if args.out_cs is not None:
        args.out_cs.parent.mkdir(parents=True, exist_ok=True)
        args.out_cs.write_text(cs_code, encoding="utf-8")
        print(f"\nWrote C# weight table -> {args.out_cs}")

    report_path = args.out_report if args.out_report is not None else (args.train.parent / "train-hot-evaluator-report.json")
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_payload = {
        "schema_version": 1,
        "backend": backend_name,
        "loss": args.loss,
        "model": args.model,
        "pair_kinds": args.pair_kinds,
        "elapsed_seconds": round(elapsed_s, 3),
        "feature_count": dim,
        "feature_names": feature_names,
        "train_pairs": len(train_rows),
        "train_fights": train_fights,
        "val_pairs": len(val_rows),
        "val_fights": val_fights,
        "baseline_train": baseline_train,
        "baseline_val": baseline_val,
        "linear": {
            "l2": best_linear["l2"],
            "eff_scale": best_linear["eff_scale"],
            "weights_int32": best_linear["w_int"],
            "weights_std": [round(x, 6) for x in best_linear["w_std"]],
            "train_float": best_linear["train_float"],
            "train_quant": best_linear["train_quant"],
            "val_float": best_linear["val_float"],
            "val_quant": best_linear["val_quant"],
        },
        "mlp": (
            {
                "hidden": args.hidden,
                "l2": args.mlp_l2,
                "train_float": mlp_result["train_float"],
                "train_quant": mlp_result["train_quant"],
                "val_float": mlp_result["val_float"],
                "val_quant": mlp_result["val_quant"],
                "quantized": qmlp,
            }
            if mlp_result is not None
            else None
        ),
    }
    report_path.write_text(json.dumps(report_payload, indent=2), encoding="utf-8")
    print(f"Wrote training report -> {report_path} ({elapsed_s:.2f}s)")

    print("\n### Ready-to-Paste C# Zero-Alloc Weight Table")
    print(cs_code)
    return 0


if __name__ == "__main__":
    sys.exit(main())
