#!/usr/bin/env python3
"""离线宿主的批量运行器：吃一份 plan JSON，起 N 个宿主进程并行消费，
每根写出 runs/<label>/{result.json,route.json,root-diagnostics.txt,search-policy.json}。

plan 是一个数组，每项：
  label                                   必填，简单目录名（只许字母数字和 . _ -）
  request                                 必填，无人测试请求 JSON 的路径（含 generatedScenarioPath）
  profile                                 可选，Low|Medium|High|VeryHigh|Custom（默认 Custom）
  beam / nodes                            可选，覆盖预设的宽度与节点上限
  maxCardBranchesPerNode                  可选，覆盖预设的每节点卡牌分支上限
  maxPileChoiceBranchesPerAction          可选，覆盖预设的每动作牌堆选择分支上限
  maxHandChoiceBranchesPerAction          可选，覆盖预设的每动作手牌选择分支上限
  maxDegreeOfParallelism                  可选，搜索并行度（默认 1）
  searchBudgetMilliseconds                可选，搜索预算毫秒（默认 600000）
  potionPolicy                            可选，默认 Smart
  searchMode                              可选，Evaluate（默认，单次求解）| Coordinator（生产协调器）
  usePortfolio                            可选，true 时开宽度组合（只对 Coordinator 有效）
  dll                                     可选，换掉运行时加载的 CombatSolver.dll

用法：
  python3 tools/OfflineSearchHarness/run_plan.py --plan <plan.json> --workspace <dir> [--workers 4]
"""
import argparse
import concurrent.futures
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEFAULT_HARNESS = REPO / 'tools/OfflineSearchHarness/bin/Release/net9.0/OfflineSearchHarness.dll'
# 与 run_mac.py 同一条判据：撞到时间边界的一根作废。
TIME_BOUNDARY_PATTERNS = ('TURN_LAYER_BUDGET reason=time', 'SEARCH_TIME_BUDGET')
PROCESS_TIMEOUT_SECONDS = 900
SEARCH_TIME_LIMIT_SECONDS = 600


def build_command(item, out, harness):
    label = item['label']
    args = [
        'dotnet', str(harness),
        '--request', str(Path(item['request']).resolve(strict=True)),
        '--label', label,
        '--out', str(out),
        '--dop', str(item.get('maxDegreeOfParallelism', 1)),
        '--budget-ms', str(item.get('searchBudgetMilliseconds', SEARCH_TIME_LIMIT_SECONDS * 1000)),
        '--potion-policy', item.get('potionPolicy', 'Smart'),
        '--search-mode', item.get('searchMode', 'Evaluate'),
        '--profile', item.get('profile', 'Custom'),
    ]
    for key, flag in (('beam', '--beam'), ('nodes', '--nodes'),
                      ('maxCardBranchesPerNode', '--card-branches'),
                      ('maxPileChoiceBranchesPerAction', '--pile-branches'),
                      ('maxHandChoiceBranchesPerAction', '--hand-branches')):
        if item.get(key) is not None:
            args += [flag, str(item[key])]
    if item.get('usePortfolio'):
        args.append('--use-portfolio')
    return args


def log_messages(out):
    messages = []
    for path in sorted(out.glob('logs/*/*.jsonl')):
        for line in path.read_text(errors='replace').splitlines():
            if not line.strip():
                continue
            try:
                messages.append(json.loads(line).get('Message', ''))
            except json.JSONDecodeError:
                messages.append(line)
    return messages


def run_one(item, workspace, harness, keep_existing):
    label = item['label']
    if not re.fullmatch(r'[A-Za-z0-9_.-]+', label):
        raise ValueError(f'Label must be a simple directory name: {label}')
    out = workspace / 'runs' / label
    if out.exists():
        if not keep_existing:
            raise FileExistsError(f'{out} 已存在；换工作区或加 --reuse')
        if (out / 'result.json').exists():
            existing = json.loads((out / 'result.json').read_text())
            existing['reused'] = True
            return existing
    out.mkdir(parents=True, exist_ok=True)

    env = dict(os.environ)
    if item.get('dll'):
        env['OFFLINE_HARNESS_COMBATSOLVER_DLL'] = str(Path(item['dll']).resolve(strict=True))
    started = time.monotonic()
    with (out / 'stdout.log').open('w') as log:
        process = subprocess.run(build_command(item, out, harness), cwd=REPO, env=env,
                                 stdout=log, stderr=subprocess.STDOUT,
                                 timeout=max(PROCESS_TIMEOUT_SECONDS,
                                             int(item.get('searchBudgetMilliseconds', SEARCH_TIME_LIMIT_SECONDS * 1000)) // 1000 + 300),
                                 check=False)
    wall = time.monotonic() - started

    result_path = out / 'result.json'
    if not result_path.exists():
        return {'label': label, 'status': 'Failed', 'error': '宿主没有写出 result.json',
                'exitCode': process.returncode, 'processWallSeconds': round(wall, 2)}
    result = json.loads(result_path.read_text())
    result['exitCode'] = process.returncode
    result['processWallSeconds'] = round(wall, 2)

    messages = log_messages(out)
    metrics = result.get('solverMetrics') or {}
    hit_time = (metrics.get('boundary') == 'TimeLimit'
                or bool(result.get('timeBoundaryObserved'))
                or any(any(p in m for p in TIME_BOUNDARY_PATTERNS) for m in messages))
    result['timeBoundary'] = hit_time
    result['valid'] = (process.returncode == 0 and result.get('status') == 'Passed' and not hit_time)
    if not result['valid'] and hit_time:
        result.setdefault('error', '撞到搜索时间边界，本根作废')
    result_path.write_text(json.dumps(result, ensure_ascii=False, indent=2))
    if messages:
        (out / 'search-messages.json').write_text(json.dumps(messages, ensure_ascii=False, indent=1))
    return result


def summarize(result):
    metrics = result.get('solverMetrics') or {}
    return {
        'label': result.get('label'),
        'status': result.get('status'),
        'valid': result.get('valid'),
        'totalExpanded': metrics.get('totalExpanded'),
        'totalTransitions': metrics.get('totalTransitions'),
        'boundary': metrics.get('boundary'),
        'projectedBattleHpLost': metrics.get('projectedBattleHpLost'),
        'score': metrics.get('score'),
        'wallSeconds': result.get('wallSeconds'),
        'processWallSeconds': result.get('processWallSeconds'),
        'peakManagedHeapBytes': result.get('peakManagedHeapBytes'),
        'peakWorkingSetBytes': result.get('peakWorkingSetBytes'),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--plan', type=Path, required=True)
    parser.add_argument('--workspace', type=Path, required=True)
    parser.add_argument('--workers', type=int, default=1)
    parser.add_argument('--harness', type=Path, default=DEFAULT_HARNESS)
    parser.add_argument('--reuse', action='store_true', help='已有结果的标签直接跳过')
    parser.add_argument('--runs-jsonl', type=Path, help='逐根摘要追加到这个 jsonl（默认 <workspace>/runs.jsonl）')
    args = parser.parse_args()

    harness = args.harness.resolve(strict=True)
    workspace = args.workspace.resolve()
    workspace.mkdir(parents=True, exist_ok=True)
    plan = json.loads(args.plan.read_text())
    if not isinstance(plan, list) or not plan:
        raise SystemExit('plan 必须是非空数组')
    labels = [item['label'] for item in plan]
    if len(set(labels)) != len(labels):
        raise SystemExit('plan 里有重复标签')

    jsonl = (args.runs_jsonl or (workspace / 'runs.jsonl')).resolve()
    jsonl.parent.mkdir(parents=True, exist_ok=True)
    summaries = []
    failures = 0
    started = time.monotonic()
    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futures = {pool.submit(run_one, item, workspace, harness, args.reuse): item for item in plan}
        with jsonl.open('a') as stream:
            for future in concurrent.futures.as_completed(futures):
                item = futures[future]
                try:
                    result = future.result()
                except Exception as error:  # noqa: BLE001 - 一根失败不该拖垮整批
                    result = {'label': item['label'], 'status': 'Failed',
                              'error': f'{type(error).__name__}: {error}', 'valid': False}
                line = summarize(result)
                summaries.append(line)
                if not result.get('valid'):
                    failures += 1
                    line['error'] = result.get('error')
                stream.write(json.dumps(line, ensure_ascii=False) + '\n')
                stream.flush()
                print(json.dumps(line, ensure_ascii=False), flush=True)

    elapsed = time.monotonic() - started
    (workspace / 'plan-summary.json').write_text(json.dumps({
        'plan': str(args.plan), 'workers': args.workers, 'roots': len(plan),
        'invalid': failures, 'elapsedSeconds': round(elapsed, 2),
        'summaries': sorted(summaries, key=lambda row: row['label'] or ''),
    }, ensure_ascii=False, indent=2))
    print(f'# roots={len(plan)} invalid={failures} workers={args.workers} elapsed={elapsed:.1f}s', flush=True)
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
