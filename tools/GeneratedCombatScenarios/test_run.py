import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('generated_suite', Path(__file__).with_name('run.py'))
suite = importlib.util.module_from_spec(spec)
spec.loader.exec_module(suite)


class SuiteLifecycle(unittest.TestCase):
    def exercise(self, interrupt=False):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)/'evidence'
            argv = ['run.py', '--count', '2', '--output', str(output), '--continue-on-failure']
            calls = []
            def fake_run(command, **kwargs):
                calls.append(command)
                normalized = [x.replace('-', '').lower() for x in command]
                if 'stopinstance' in normalized:
                    return subprocess.CompletedProcess(command, 0)
                if interrupt:
                    raise KeyboardInterrupt()
                directory = Path(command[normalized.index('evidencedirectory')+1])
                failed = directory.name == '0000'
                (directory/'result.json').write_text(json.dumps(dict(status='Failed' if failed else 'Passed',
                    runId=directory.name, error='fixture failure' if failed else None)))
                return subprocess.CompletedProcess(command, 1 if failed else 0)
            with patch.object(sys, 'argv', argv), patch.object(subprocess, 'run', fake_run):
                if interrupt:
                    with self.assertRaises(KeyboardInterrupt):
                        suite.main()
                else:
                    self.assertEqual(1, suite.main())
                    rows = [json.loads(line) for line in (output/'results.jsonl').read_text().splitlines()]
                    self.assertEqual(['Failed', 'Passed'], [x['status'] for x in rows])
                    self.assertNotEqual(rows[0]['evidence'], rows[1]['evidence'])
                    self.assertEqual('fixture failure', rows[0]['error'])
            stop_flags = ['stopinstance' in [x.replace('-', '').lower() for x in c] for c in calls]
            self.assertTrue(stop_flags[0])
            self.assertTrue(stop_flags[-1])
            self.assertEqual(2 if interrupt else 3, sum(stop_flags))

    def test_failure_is_preserved_and_restarts_before_next_case(self):
        self.exercise()

    def test_interrupt_always_stops_owned_instance(self):
        self.exercise(interrupt=True)


if __name__ == '__main__':
    unittest.main()
