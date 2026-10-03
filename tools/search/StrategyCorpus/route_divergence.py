#!/usr/bin/env python3
"""Find the first action divergence between two comparable search results."""

import argparse
import hashlib
import json
from pathlib import Path

from compare import better, first_difference


ACTION_FIELDS = (
    "kind", "turn", "cardId", "cardOccurrence", "cardStateKey",
    "cardStateOccurrence", "targetIndex", "targetCombatId", "potionSlot",
    "potionId", "replayCount", "endsPlayerTurn", "nestedChoicesBeforePrimary",
)
CHOICE_FIELDS = ("effect", "sourcePile", "sourceId", "contextId", "timing")
CHOICE_CARD_FIELDS = (
    "cardId", "upgradeLevel", "stateKey", "sourceOccurrence", "optionOccurrence",
)


def read_result(directory):
    result = json.loads((directory / "result.json").read_text(encoding="utf-8-sig"))
    search = json.loads((directory / "search-result.json").read_text(encoding="utf-8-sig"))
    if result.get("status") != "Passed" or search.get("resultScope") != "SearchCompletion":
        raise ValueError(f"Search result is not complete: {directory}")
    return result, search


def choice_identity(choice):
    if choice is None:
        return None
    return {**{key: choice.get(key) for key in CHOICE_FIELDS},
            "cards": [{key: card.get(key) for key in CHOICE_CARD_FIELDS}
                      for card in choice.get("cards", [])]}


def action_identity(action):
    return {**{key: action.get(key) for key in ACTION_FIELDS},
            "choice": choice_identity(action.get("choice")),
            "nestedChoices": [choice_identity(item) for item in action.get("nestedChoices") or []],
            "turnStartChoices": [choice_identity(item) for item in action.get("turnStartChoices") or []]}


def compare_routes(baseline, witness):
    before_result, before = baseline
    after_result, after = witness
    root = before.get("rootContinuationStamp")
    if not root or root != after.get("rootContinuationStamp"):
        raise ValueError("Routes do not share the same captured combat root")
    policy = before.get("policy")
    if policy is None or policy != after.get("policy"):
        raise ValueError("Routes do not share the same executed policy")
    before_actions = [action_identity(item) for item in before["actions"]]
    after_actions = [action_identity(item) for item in after["actions"]]
    shared = next((index for index, (left, right) in enumerate(zip(before_actions, after_actions))
                   if left != right), min(len(before_actions), len(after_actions)))
    before_quality = before["comparisonQuality"]
    after_quality = after["comparisonQuality"]
    relation = ("better" if better(after_quality, before_quality) else
                "worse" if better(before_quality, after_quality) else "same")
    difference = (first_difference(before_actions[shared], after_actions[shared])
                  if shared < min(len(before_actions), len(after_actions)) else None)
    return {
        "scope": "first_action_divergence_not_search_path_loss",
        "rootSha256": hashlib.sha256(root.encode("utf-8")).hexdigest(),
        "baselineRunId": before_result.get("runId"),
        "witnessRunId": after_result.get("runId"),
        "qualityRelation": relation,
        "baselineOutcome": {key: (before_result.get("solverMetrics") or {}).get(key)
                            for key in ("projectedBattleHpLost", "potionCount", "finalHp")},
        "witnessOutcome": {key: (after_result.get("solverMetrics") or {}).get(key)
                           for key in ("projectedBattleHpLost", "potionCount", "finalHp")},
        "sharedActionCount": shared,
        "baselineActionCount": len(before_actions),
        "witnessActionCount": len(after_actions),
        "firstDivergentStep": shared + 1 if shared < max(len(before_actions), len(after_actions)) else None,
        "firstDifference": difference,
        "baselineNext": before_actions[shared] if shared < len(before_actions) else None,
        "witnessNext": after_actions[shared] if shared < len(after_actions) else None,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--witness", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if args.out.exists():
        raise FileExistsError(f"Output already exists: {args.out}")
    report = compare_routes(read_result(args.baseline), read_result(args.witness))
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"quality={report['qualityRelation']} shared_actions={report['sharedActionCount']} "
          f"first_divergent_step={report['firstDivergentStep']} "
          f"field={(report['firstDifference'] or {}).get('path', 'length')}")


if __name__ == "__main__":
    main()
