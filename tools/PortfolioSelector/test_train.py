"""Offline contracts for training isolation, C# export and conservative gating."""
import contextlib
import copy
import hashlib
import io
import json
from pathlib import Path
import re
import sys
import tempfile
import unittest
from unittest.mock import patch

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import train as trainer


SOLVER_ID = "11111111-1111-4111-8111-111111111111"
GAME_ID = "22222222-2222-4222-8222-222222222222"


def features(value=0.0):
    result = [0.0] * len(trainer.FEATURE_NAMES)
    result[0] = value
    return result


def observation(value=0.0, improved=False, elapsed=10, **overrides):
    return {"features": features(value), "decision": "Observe", "ran": True,
            "improved": improved, "labelUsable": True, "elapsedMilliseconds": elapsed,
            "termination": "None", **overrides}


def document(rows, **overrides):
    return {"schemaVersion": 1, "solverAssemblyId": SOLVER_ID, "gameAssemblyId": GAME_ID,
            "featureNames": trainer.FEATURE_NAMES, "observations": rows, **overrides}


class TrainingTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.manifest = self.root / "manifest.json"
        self.runs = self.root / "runs"
        self.model = self.root / "output" / "model.json"
        self.report = self.root / "output" / "report.json"
        self.items = []

    def add(self, label, split, rows, battle=None, **metadata):
        item = {"label": label, "split": split}
        if battle is not None:
            item["battleId"] = battle
        self.items.append(item)
        path = self.runs / label / "portfolio-observations.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(document(rows, **metadata)), encoding="utf-8")
        self.manifest.write_text(json.dumps(self.items), encoding="utf-8")
        return path

    def add_training(self, count=4):
        for index in range(count):
            self.add(f"train-{index}", "train",
                     [observation(0.1)] * 8 + [observation(1.1, True)] * 8)

    def run_training(self, **kwargs):
        result = trainer.train(self.manifest, self.runs, self.model, self.report, **kwargs)
        model = json.loads(self.model.read_text(encoding="utf-8"))
        self.assertEqual(result, json.loads(self.report.read_text(encoding="utf-8")))
        self.assertEqual(result["modelSha256"], hashlib.sha256(self.model.read_bytes()).hexdigest())
        return model, result

    def assert_disabled(self, model, report):
        self.assertEqual("no_candidate", report["status"])
        self.assertEqual(0, model["skipThreshold"])
        self.assertGreater(model["minimumBattles"], max(node["battles"] for node in model["nodes"]))
        self.assertFalse(trainer.should_skip(model, features(0.1)))
        self.assertEqual(0, report["validation"]["skippedRows"])
        self.assertEqual(0, report["validation"]["offlineSavedMilliseconds"])

    def test_test_files_are_never_opened_or_resolved(self):
        self.add_training()
        self.add("validation", "validation", [observation(0.1), observation(1.1, True)])
        poison = self.add("test-poison", "test", [], schemaVersion=999)
        poison.write_text("not JSON", encoding="utf-8")
        self.items.append({"label": "test-missing", "split": "test"})
        self.manifest.write_text(json.dumps(self.items), encoding="utf-8")
        original_open, original_resolve = Path.open, Path.resolve

        def guarded_open(path, *args, **kwargs):
            self.assertNotIn(path.parent.name, ("test-poison", "test-missing"))
            return original_open(path, *args, **kwargs)

        def guarded_resolve(path, *args, **kwargs):
            self.assertNotIn(path.parent.name, ("test-poison", "test-missing"))
            return original_resolve(path, *args, **kwargs)

        with patch.object(Path, "open", guarded_open), patch.object(Path, "resolve", guarded_resolve):
            model, report = self.run_training()
        self.assertEqual("selected", report["status"])
        self.assertFalse(report["testObservationsRead"])
        self.assertNotIn("test", report)
        self.assertEqual(4, model["nodes"][0]["battles"])

    def test_mixed_metadata_is_rejected_even_without_usable_rows(self):
        self.add_training()
        path = self.add("validation", "validation", [])
        variants = [
            {"solverAssemblyId": GAME_ID}, {"gameAssemblyId": SOLVER_ID},
            {"schemaVersion": 2}, {"schemaVersion": True},
            {"featureNames": trainer.FEATURE_NAMES[::-1]},
            {"featureNames": trainer.FEATURE_NAMES[:-1]},
        ]
        for variant in variants:
            with self.subTest(variant=variant):
                path.write_text(json.dumps(document([], **variant)), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "mixed|schemaVersion|featureNames"):
                    trainer.train(self.manifest, self.runs, self.model, self.report)
                self.assertFalse(self.model.exists())
                self.assertFalse(self.report.exists())

    def test_invalid_assembly_id_is_rejected(self):
        self.add("train", "train", [observation()], solverAssemblyId="old-ranking-model")
        with self.assertRaisesRegex(ValueError, "GUID"):
            trainer.load_splits(self.manifest, self.runs)

    def test_cross_split_battle_and_duplicate_label_are_rejected(self):
        cases = [
            [{"label": "a", "split": "train"}, {"label": "a", "split": "validation"}],
            [{"label": "a", "split": "train", "battleId": "same"},
             {"label": "b", "split": "validation", "battleId": "same"}],
            [{"label": "a", "split": "train", "battleId": "same"},
             {"label": "b", "split": "test", "battleId": "same"}],
        ]
        for items in cases:
            with self.subTest(items=items):
                self.manifest.write_text(json.dumps(items), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "duplicate|crosses splits"):
                    trainer.load_splits(self.manifest, self.runs)

    def test_aliases_cannot_inflate_independent_battle_support(self):
        for index in range(4):
            self.add(f"train-{index}", "train", [observation(0.1)] * 20, battle="one-battle")
        self.add("validation", "validation", [observation(0.1)])
        model, report = self.run_training()
        self.assert_disabled(model, report)
        self.assertEqual(80, model["nodes"][0]["samples"])
        self.assertEqual(1, model["nodes"][0]["battles"])
        self.assertEqual(1, report["train"]["battleCount"])

    def test_validation_never_changes_tree_bounds_or_evidence(self):
        self.add_training()
        path = self.add("validation", "validation", [observation(0.1)])
        first, _ = self.run_training()
        changed = [observation(0.1, True)] * 100 + [observation(-1000), observation(1000)]
        path.write_text(json.dumps(document(changed)), encoding="utf-8")
        with patch.object(trainer, "fit_tree", wraps=trainer.fit_tree) as fit:
            second, report = self.run_training()
        self.assertEqual(1, fit.call_count)
        self.assertEqual(64, len(fit.call_args.args[0]))
        self.assertEqual({f"train-{i}" for i in range(4)}, {row.battle for row in fit.call_args.args[0]})
        for key in ("nodes", "minimum", "maximum"):
            self.assertEqual(first[key], second[key])
        self.assert_disabled(second, report)
        self.assertEqual(0.1, second["minimum"][0])
        self.assertEqual(1.1, second["maximum"][0])

    def test_only_ran_and_label_usable_rows_train_and_score(self):
        self.add_training()
        self.add("excluded", "train", [
            observation(-10000, True, ran=False, labelUsable=True),
            observation(10000, True, labelUsable=False),
            observation(features=None, improved=None, labelUsable=None),
        ])
        self.add("validation", "validation", [
            observation(0.1, elapsed=7.5), observation(1.1, True, elapsed=20),
            observation(-10000, True, elapsed=50000, ran=False),
            observation(0.1, True, elapsed=90000, labelUsable=False),
        ])
        model, report = self.run_training()
        self.assertEqual(64, model["nodes"][0]["samples"])
        self.assertEqual(4, model["nodes"][0]["battles"])
        self.assertEqual(67, report["train"]["totalRows"])
        self.assertEqual(64, report["train"]["validRows"])
        self.assertEqual(5, report["train"]["battleCount"])
        self.assertEqual(4, report["train"]["validBattleCount"])
        self.assertEqual(0.1, model["minimum"][0])
        self.assertEqual(1.1, model["maximum"][0])
        validation = report["validation"]
        self.assertEqual(4, validation["totalRows"])
        self.assertEqual(2, validation["validRows"])
        self.assertEqual(0.5, validation["skipRate"])
        self.assertEqual(0, validation["missedImprovements"])
        self.assertEqual(7.5, validation["offlineSavedMilliseconds"])
        self.assertEqual(0, validation["baselines"]["always-run"]["offlineSavedMilliseconds"])
        self.assertEqual(1, validation["baselines"]["always-skip"]["missedImprovements"])
        self.assertEqual(27.5, validation["baselines"]["always-skip"]["offlineSavedMilliseconds"])
        self.assertIn("not end-to-end", report["savingsScope"])
        self.assertGreaterEqual(report["trainingMilliseconds"], 0)

    def test_validation_selects_lowest_safe_threshold_at_equal_savings(self):
        for positives, expected in ((0, 0.0), (1, 0.025), (3, 0.05)):
            with self.subTest(positives=positives):
                self.items = []
                for index in range(4):
                    rows = [observation(0.1, improved=index == 0 and j < positives) for j in range(25)]
                    self.add(f"train-{index}", "train", rows)
                self.add("validation", "validation", [observation(0.1)])
                model, report = self.run_training()
                self.assertEqual("selected", report["status"])
                self.assertEqual(expected, model["skipThreshold"])
                self.assertEqual(positives / 100, model["nodes"][0]["improvementRate"])
                self.assertEqual([0, 0.025, 0.05], [c["skipThreshold"] for c in report["selection"]["candidates"]])
                self.assertEqual(positives, report["train"]["missedImprovements"])
                self.assertEqual(0, report["validation"]["missedImprovements"])

    def test_safe_threshold_wins_over_larger_unsafe_savings(self):
        for index in range(4):
            rows = [observation(0.1)] * 10
            rows += [observation(1.1, index == 0 and j == 0) for j in range(10)]
            self.add(f"train-{index}", "train", rows)
        self.add("validation", "validation", [observation(0.1, elapsed=5), observation(1.1, True, elapsed=1000)])
        model, report = self.run_training()
        self.assertEqual("selected", report["status"])
        self.assertEqual(0, model["skipThreshold"])
        self.assertEqual(5, report["validation"]["offlineSavedMilliseconds"])
        for candidate in report["selection"]["candidates"][1:]:
            self.assertFalse(candidate["eligible"])
            self.assertEqual(1, candidate["validation"]["missedImprovements"])
            self.assertEqual(1005, candidate["validation"]["offlineSavedMilliseconds"])

    def test_unsafe_or_unmeasured_validation_always_disables_skipping(self):
        self.add_training()
        for rows in ([observation(0.1, True)], [observation(0.1, elapsed=0)], [],
                     [observation(0.1, labelUsable=False)], [observation(1000)]):
            with self.subTest(rows=rows):
                self.items = [item for item in self.items if item["split"] == "train"]
                self.add("validation", "validation", rows)
                model, report = self.run_training()
                self.assert_disabled(model, report)

    def test_absent_validation_disables_skipping(self):
        self.add_training()
        model, report = self.run_training()
        self.assert_disabled(model, report)
        self.assertEqual("no_usable_validation", report["selection"]["reason"])

    def test_validation_battles_cannot_supply_training_support(self):
        self.add_training(count=2)
        for index in range(10):
            self.add(f"validation-{index}", "validation", [observation(0.1)])
        model, report = self.run_training(minimum_battles=3)
        self.assert_disabled(model, report)
        self.assertEqual(2, model["nodes"][0]["battles"])
        self.assertEqual(10, report["validation"]["validBattleCount"])

    def test_minimum_battles_is_applied_per_leaf(self):
        for index in range(4):
            rows = [observation(0.1)] * 12 if index == 0 else [observation(1.1, True)] * 12
            self.add(f"train-{index}", "train", rows)
        self.add("validation", "validation", [observation(0.1)])
        model, report = self.run_training(minimum_battles=3)
        self.assert_disabled(model, report)
        self.assertEqual(4, model["nodes"][0]["battles"])
        negative = model["nodes"][trainer.leaf_index(model, features(0.1))]
        self.assertEqual(12, negative["samples"])
        self.assertEqual(1, negative["battles"])

    def test_too_few_samples_disables_single_leaf_tree(self):
        for index in range(4):
            self.add(f"train-{index}", "train", [observation(0.1)])
        self.add("validation", "validation", [observation(0.1)])
        model, report = self.run_training()
        self.assert_disabled(model, report)
        self.assertEqual("insufficient_training_samples", report["selection"]["reason"])
        self.assertEqual(4, model["nodes"][0]["samples"])

    def test_no_usable_training_is_an_error_without_fabricated_model(self):
        self.add("train", "train", [observation(labelUsable=False)])
        self.add("validation", "validation", [observation()] * 100)
        with self.assertRaisesRegex(ValueError, "no usable train"):
            trainer.train(self.manifest, self.runs, self.model, self.report)
        self.assertFalse(self.model.exists())
        self.assertFalse(self.report.exists())

    def test_single_positive_class_cannot_skip(self):
        for index in range(4):
            self.add(f"train-{index}", "train", [observation(0.1, True)] * 12)
        self.add("validation", "validation", [observation(0.1)])
        model, report = self.run_training()
        self.assert_disabled(model, report)
        self.assertEqual(1.0, model["nodes"][0]["improvementRate"])

    def test_unknown_features_run_and_timing_drift_matches_csharp(self):
        self.add_training()
        self.add("validation", "validation", [observation(0.1)])
        model, _ = self.run_training()
        self.assertTrue(trainer.should_skip(model, features(0.1)))
        self.assertFalse(trainer.should_skip(model, features(-0.1)))
        self.assertFalse(trainer.should_skip(model, features(2)))
        self.assertFalse(trainer.should_skip(model, features(float("nan"))))
        self.assertFalse(trainer.should_skip(model, features(float("inf"))))
        self.assertFalse(trainer.should_skip(model, []))
        for index in range(1, len(trainer.FEATURE_NAMES)):
            drifted = features(0.1)
            drifted[index] = 1000000
            self.assertEqual(13 <= index <= 23, trainer.should_skip(model, drifted), index)

    def test_invalid_usable_values_are_rejected(self):
        variants = [
            {"improved": None}, {"improved": 0}, {"ran": 1}, {"labelUsable": "true"},
            {"features": features(float("nan"))}, {"features": features(float("inf"))},
            {"features": features(1e100)}, {"features": features(True)}, {"features": []},
            {"elapsedMilliseconds": -1}, {"elapsedMilliseconds": float("inf")},
        ]
        for variant in variants:
            with self.subTest(variant=variant):
                with self.assertRaises(ValueError):
                    trainer.usable_observation(observation(**variant), "battle", "fixture")

    def test_export_predictions_probabilities_and_node_links(self):
        rows = []
        for battle in range(4):
            for state in range(8):
                values = features()
                values[:3] = [0.1 + ((state >> bit) & 1) for bit in range(3)]
                for _ in range(3):
                    rows.append(trainer.Observation(tuple(values), bool(state.bit_count() % 2), 10, str(battle)))
        classifier, matrix, _ = trainer.fit_tree(rows, max_depth=3, min_samples_leaf=12)
        model = trainer.export_model(classifier, matrix, rows, trainer.metadata(document([]), "fixture"), 4)
        model = json.loads(json.dumps(model, allow_nan=False))
        pending, visited = [0], set()
        while pending:
            index = pending.pop()
            self.assertNotIn(index, visited)
            visited.add(index)
            node = model["nodes"][index]
            self.assertGreaterEqual(node["samples"], 12)
            self.assertGreaterEqual(node["battles"], 1)
            self.assertLessEqual(node["battles"], node["samples"])
            if node["feature"] == -1:
                self.assertEqual((-1, -1), (node["left"], node["right"]))
            else:
                pending.extend((node["left"], node["right"]))
        self.assertEqual(set(range(len(model["nodes"]))), visited)
        self.assertGreater(len(visited), 3)
        self.assertLessEqual(len(visited), 15)
        probes = np.random.default_rng(0).uniform(-0.5, 1.5, size=(100, len(trainer.FEATURE_NAMES)))
        probes = np.concatenate((matrix, probes))
        expected = classifier.predict_proba(probes)[:, list(classifier.classes_).index(1)]
        actual = [model["nodes"][trainer.leaf_index(model, values)]["improvementRate"] for values in probes]
        np.testing.assert_allclose(expected, actual)
        np.testing.assert_array_equal(classifier.predict(probes), np.asarray(actual) > 0.5)
        np.testing.assert_array_equal(classifier.apply(probes), [trainer.leaf_index(model, v) for v in probes])
        self.assertEqual(np.dtype("float32"), matrix.dtype)

    def test_float32_threshold_boundary_matches_sklearn(self):
        left, right = 1.0, 1.0 + 2 ** -22
        rows = [trainer.Observation(tuple(features(value)), improved, 10, str(battle))
                for battle in range(4) for value, improved in ((left, False), (right, True))
                for _ in range(8)]
        classifier, matrix, _ = trainer.fit_tree(rows, 2, 12)
        model = trainer.export_model(classifier, matrix, rows, trainer.metadata(document([]), "fixture"), 4)
        threshold = model["nodes"][0]["threshold"]
        rounded_left = np.nextafter(threshold, float("inf"))
        self.assertGreater(rounded_left, threshold)
        self.assertLessEqual(float(np.float32(rounded_left)), threshold)
        probes = [features(value) for value in (left, right, threshold, rounded_left,
                  np.nextafter(threshold, float("-inf")),
                  float(np.nextafter(np.float32(threshold), np.float32(float("inf")))))]
        np.testing.assert_array_equal(classifier.apply(probes), [trainer.leaf_index(model, v) for v in probes])
        self.assertEqual(model["nodes"][0]["left"], trainer.leaf_index(model, features(rounded_left)))

    def test_feature_schema_matches_checked_in_csharp(self):
        source = Path(__file__).resolve().parents[2] / "src" / "Search" / "BeamPortfolioSelector.cs"
        text = source.read_text(encoding="utf-8")
        names = re.search(r"string\[\] FeatureNames(?:Storage)?\s*=\s*\[(.*?)\];", text, re.S)
        self.assertIsNotNone(names)
        self.assertEqual(trainer.FEATURE_NAMES, re.findall(r'"([^"]+)"', names.group(1)))
        self.assertIn(f"const int SchemaVersion = {trainer.SCHEMA_VERSION};", text)

    def test_manifest_items_wrapper_and_cli_write_separate_outputs(self):
        self.add_training()
        self.manifest.write_text(json.dumps({"items": self.items}), encoding="utf-8")
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = trainer.main(["--manifest", str(self.manifest), "--runs", str(self.runs),
                                 "--model", str(self.model), "--report", str(self.report),
                                 "--max-depth", "3", "--min-samples-leaf", "8", "--minimum-battles", "3"])
        self.assertEqual(0, code)
        self.assertEqual("no_candidate", json.loads(output.getvalue())["status"])
        model = json.loads(self.model.read_text(encoding="utf-8"))
        report = json.loads(self.report.read_text(encoding="utf-8"))
        self.assert_disabled(model, report)
        self.assertNotIn("train", model)
        self.assertEqual(3, report["hyperparameters"]["maxDepth"])
        self.assertEqual(8, report["hyperparameters"]["minSamplesLeaf"])

    def test_hyperparameter_limits_and_output_collisions(self):
        self.add_training()
        for kwargs in ({"max_depth": 4}, {"min_samples_leaf": 1}, {"minimum_battles": 2}):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                trainer.train(self.manifest, self.runs, self.model, self.report, **kwargs)
        for model, report in ((self.model, self.model), (self.manifest, self.report),
                              (self.runs / "train-0" / "portfolio-observations.json", self.report)):
            with self.subTest(model=model, report=report), self.assertRaises(ValueError):
                trainer.train(self.manifest, self.runs, model, report)


if __name__ == "__main__":
    unittest.main()
