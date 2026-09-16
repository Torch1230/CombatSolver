#!/usr/bin/env python3
"""把离线宿主的产物与另一份结果逐字段比较。

两边都是 runs/<label>/ 目录（离线宿主的 run_plan.py 产物，或游戏内无人测试写出的同形目录），
每根比四组：

  solverMetrics   result.json 的 solverMetrics 里与时间/内存/GC 无关的字段（排除表见 EXCLUDED）
  route           route.json 里选中路线每个动作的 turn/kind/cardId/potionId/targetCombatId/cardStateKey
  rootState       result.json 的 rootContinuationStamp（根状态戳记）
  catalog         result.json 的 catalogFingerprint（生成场景目录指纹）

用法：
  python3 tools/OfflineSearchHarness/compare_results.py \
      --left  <离线工作区>/runs --left-prefix  <标签前缀> \
      --right <参考目录>/runs   --right-prefix <标签前缀> \
      --out comparison.json

标签前缀用来把两边不同批次的标签对齐（left 的 `A-BIG-X` 与 right 的 `B-BIG-X` 都归到根 `BIG-X`）。
退出码 0 表示全部字段一致，1 表示有差异（差异明细写在 --out 的 JSON 里）。
"""
import argparse
import json
import sys
from pathlib import Path

# 固定节点预算下与时间/内存/GC 相关的字段不可比，逐项排除。
EXCLUDED = {
    'capturedAtElapsedMilliseconds', 'elapsedMilliseconds', 'totalElapsedMilliseconds',
    'workerAllocatedBytes', 'totalWorkerAllocatedBytes', 'managedHeapBytes',
    'managedLiveBytes', 'managedFragmentedBytes', 'workingSetBytes', 'privateMemoryBytes',
    'totalGen0Collections', 'totalGen1Collections', 'totalGen2Collections',
    'totalGcPauseMilliseconds', 'maxGcPauseMilliseconds', 'gcLifecycle',
    'gcLifecycleAttribution', 'gcLatencyMode', 'noGcRegionActive', 'noGcRegionBudgetBytes',
    'noGcRegionRolloverCount', 'configuredNoGcRegionEnabled', 'configuredNoGcRegionBudgetBytes',
}
ROUTE_FIELDS = ('turn', 'kind', 'cardId', 'potionId', 'targetCombatId', 'cardStateKey')
# 文案是纯显示字段。离线本地化返回键名（见 docs/OFFLINE_SEARCH_HARNESS.md 的「已知限制」），
# 与游戏内比时天然不同，不参与比较。
DISPLAY_FIELDS = {'cardTitle', 'targetName', 'actionTitle', 'potionTitle'}


def read_json(path):
    path = Path(path)
    return json.loads(path.read_text()) if path.exists() else None


def collect(runs, prefix):
    out = {}
    for result in sorted(Path(runs).glob('*/result.json')):
        payload = json.loads(result.read_text())
        label = payload.get('label') or result.parent.name
        root = label[len(prefix) + 1:] if prefix and label.startswith(prefix + '-') else label
        out[root] = (payload, read_json(result.parent / 'route.json'))
    return out


def diff_dict(left, right, excluded=frozenset()):
    rows = []
    for key in sorted(set(left or {}) | set(right or {})):
        if key in excluded:
            continue
        a, b = (left or {}).get(key, '<缺>'), (right or {}).get(key, '<缺>')
        rows.append({'field': key, 'left': a, 'right': b, 'same': a == b})
    return rows


def diff_route(left, right):
    left, right = left or [], right or []
    rows = []
    for index in range(max(len(left), len(right))):
        a = {k: left[index].get(k) for k in ROUTE_FIELDS} if index < len(left) else '<缺>'
        b = {k: right[index].get(k) for k in ROUTE_FIELDS} if index < len(right) else '<缺>'
        rows.append({'field': f'action[{index}]', 'left': a, 'right': b, 'same': a == b})
    return rows


def compare_root(root, left, right):
    left_result, left_route = left
    right_result, right_route = right
    groups = {
        'solverMetrics': diff_dict(
            left_result.get('solverMetrics'), right_result.get('solverMetrics'),
            EXCLUDED | DISPLAY_FIELDS),
        'route': diff_route(left_route, right_route),
        'rootState': diff_dict(
            {'rootContinuationStamp': left_result.get('rootContinuationStamp')},
            {'rootContinuationStamp': right_result.get('rootContinuationStamp')}),
        'catalog': diff_dict(
            {'catalogFingerprint': left_result.get('catalogFingerprint')},
            {'catalogFingerprint': right_result.get('catalogFingerprint')}),
    }
    return {
        'root': root,
        'groups': groups,
        'compared': sum(len(rows) for rows in groups.values()),
        'mismatches': [row for rows in groups.values() for row in rows if not row['same']],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--left', required=True, help='左边的 runs 目录')
    parser.add_argument('--right', required=True, help='右边的 runs 目录')
    parser.add_argument('--left-prefix', default='')
    parser.add_argument('--right-prefix', default='')
    parser.add_argument('--out', required=True)
    args = parser.parse_args()

    left = collect(args.left, args.left_prefix)
    right = collect(args.right, args.right_prefix)
    shared = sorted(set(left) & set(right))
    roots = [compare_root(root, left[root], right[root]) for root in shared]
    report = {
        'left': args.left,
        'right': args.right,
        'roots': len(roots),
        'leftOnly': sorted(set(left) - set(right)),
        'rightOnly': sorted(set(right) - set(left)),
        'comparedFields': sum(row['compared'] for row in roots),
        'mismatchedRoots': [row['root'] for row in roots if row['mismatches']],
        'details': roots,
    }
    Path(args.out).write_text(json.dumps(report, indent=1, ensure_ascii=False))
    ok = not report['mismatchedRoots'] and not report['leftOnly'] and not report['rightOnly']
    print(f"roots={report['roots']} fields={report['comparedFields']} "
          f"mismatched_roots={len(report['mismatchedRoots'])} "
          f"left_only={len(report['leftOnly'])} right_only={len(report['rightOnly'])} "
          f"=> {'IDENTICAL' if ok else 'DIFFERENT'}")
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
