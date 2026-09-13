#!/usr/bin/env python3
"""Compare a census result with an uninstrumented full result dump."""
import argparse
import json
import re
from pathlib import Path
p = argparse.ArgumentParser()
p.add_argument('baseline', type=Path)
p.add_argument('candidate', type=Path)
a = p.parse_args()
runtime=set('worker_allocated_bytes allocated_per_transition gc0 gc1 gc2 gc_pause_ms max_gc_pause_ms worker_yields frame_recovery_waits frame_recovery_wait_ms elapsed_ms total_elapsed_ms total_worker_allocated_bytes total_gc0 total_gc1 total_gc2 total_gc_pause_ms total_max_gc_pause_ms main_thread_frames p95_main_thread_gap_ms p99_main_thread_gap_ms max_main_thread_gap_ms main_thread_gap_ms main_thread_over_33ms main_thread_over_50ms main_thread_over_100ms managed_live_bytes managed_heap_bytes managed_fragmented_bytes process_working_set_bytes process_private_bytes'.split())
scheduling=set('parallel_waves parallel_work_items parallel_action_waves parallel_action_work_items deferred_round_choice_actions deferred_round_choice_width_total deferred_round_choice_finite_fallbacks deferred_round_choice_finite_primary_layers deferred_round_choice_finite_pending_fallbacks parallel_round_choice_waves parallel_round_choice_work_items'.split())

def read(path):
    d = json.loads(path.read_text())
    f = dict(re.findall(r'(\w+)=([^ ]+)', d['result'].splitlines()[0]))
    normalized_forks = int(f['forks']) - int(f.get('round_prefix_captures', 0))
    fixed = {k: v for k, v in f.items() if k not in runtime | scheduling | {'forks', 'round_prefix_captures', 'round_prefix_reuses'}}
    return d, fixed, normalized_forks
base, bf, bn = read(a.baseline)
candidate, cf, cn = read(a.candidate)
assert bf == cf, 'Non-runtime result fields differ'
assert bn == cn, 'Normalized logical forks differ'
assert base['actions'] == candidate['actions'], 'Full actions differ'
assert base['result'].splitlines()[1:] == candidate['result'].splitlines()[1:], 'Route text differs'
print(json.dumps(dict(fixedFields=len(bf), actions=len(base['actions']), normalizedForks=bn, routeTextEqual=True)))
