import sys
import tempfile
import time
import unittest
from pathlib import Path

from run import generate, read, run_process


class CollectionContracts(unittest.TestCase):
    def test_expired_deadline_does_not_launch(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            marker = root / 'launched'
            command = [sys.executable, '-c', 'from pathlib import Path; Path(__import__("sys").argv[1]).touch()', str(marker)]
            self.assertEqual(run_process(command, root / 'log', time.monotonic() - 1), ('BatchDeadline', None))
            self.assertFalse(marker.exists())

    def test_deadline_kills_slow_process_and_preserves_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            started = time.monotonic()
            self.assertEqual(run_process([sys.executable, '-c', 'import time; time.sleep(10)'],
                                         root / 'timeout.log', started + 0.1), ('Timeout', None))
            self.assertLess(time.monotonic() - started, 2)
            self.assertEqual(run_process([sys.executable, '-c', 'raise SystemExit(7)'],
                                         root / 'failure.log', time.monotonic() + 2), ('Failed', 7))

    def test_heldout_inputs_cover_all_characters_and_different_encounters(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            screen = generate(root / 'screen')
            heldout = generate(root / 'heldout', 'heldout')
            self.assertEqual(len(screen), 12)
            self.assertEqual(len(heldout), 5)
            scenarios = [read(Path(read(Path(c['request']))['generatedScenarioPath'])) for c in heldout]
            self.assertEqual(len({s['characterId'] for s in scenarios}), 5)
            self.assertTrue(all(s['encounterKind'] == 'Elite' for s in scenarios))


if __name__ == '__main__':
    unittest.main()
