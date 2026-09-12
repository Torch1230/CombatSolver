"""Exercise the production Bash pool with real producer locks; no game files."""
from pathlib import Path
import subprocess
import tempfile

HELPER = Path(__file__).with_name('headless-runtime.sh').resolve()
DRIVER = r'''
set -euo pipefail
source "$1"
export HR_WORKTREE="$2/worktree" COMBATSOLVER_HEADLESS_HOST_ROOT="$2/host"
hr_select_pool "$2/instances" pool 1
hr_init "$HR_SELECTED_ROOT" "$HR_SELECTED_INSTANCE" "$HR_SELECTED_ROOT/game/SlayTheSpire2" "$HR_SELECTED_ROOT/data" parallel 1 1 1
if [[ $3 == matrix ]]; then
    exec {matrix_fd}>"$HR_ROOT/matrix.lock"
    flock -n "$matrix_fd"
    exec {HR_INSTANCE_FD}>&-
fi
printf '%s\n' "$HR_INSTANCE"
read -r release
'''

with tempfile.TemporaryDirectory(prefix='headless-pool-test-') as directory:
    root = Path(directory)
    children = []

    def start(mode='launcher', success=True):
        child = subprocess.Popen(['bash', '-c', DRIVER, 'pool-test', str(HELPER), directory, mode],
                                 stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        children.append(child)
        if not success:
            out, err = child.communicate(timeout=5)
            assert child.returncode != 0 and 'pool busy' in err, (out, err)
            return
        slot = child.stdout.readline().strip()
        assert slot in ('pool', 'pool-2'), child.stderr.read()
        return child, slot

    def release(child):
        out, err = child.communicate('release\n', timeout=5)
        assert child.returncode == 0, (out, err)

    try:
        first, slot = start()
        assert slot == 'pool'
        second, slot = start()
        assert slot == 'pool-2'
        start(success=False)
        assert len(list((root / 'instances').iterdir())) == 2
        release(first)
        reused, slot = start()
        assert slot == 'pool'
        release(reused)
        release(second)
        matrix, slot = start('matrix')
        assert slot == 'pool'
        other, slot = start()
        assert slot == 'pool-2', 'matrix gap was stolen'
        release(other)
        release(matrix)
        # A live held search reserves its slot; a stale ready file does not.
        import json
        ready_dir = root / 'instances/pool/data/SlayTheSpire2'
        ready_dir.mkdir(parents=True)
        (ready_dir / 'combat_solver_test_ready.json').write_text('{"held":true}')
        import os
        birth = Path(f'/proc/{os.getpid()}/stat').read_text().rsplit(') ', 1)[1].split()[19]
        marker = root / 'instances/pool/process.json'
        marker.write_text(json.dumps({'pid': os.getpid(), 'procStartTimeTicks': birth}))
        held_other, slot = start()
        assert slot == 'pool-2'
        release(held_other)
        marker.write_text(json.dumps({'pid': os.getpid(), 'procStartTimeTicks': '0'}))
        stale_reused, slot = start()
        assert slot == 'pool'
        release(stale_reused)
        # Existing second slot must be reused before recreating a missing first.
        import shutil
        shutil.rmtree(root / 'instances' / 'pool')
        reused, slot = start()
        assert slot == 'pool-2'
        release(reused)
        assert not (root / 'instances' / 'pool').exists()
        print('HEADLESS_POOL_PASSED: reuse, occupied fallback, bounded queue, matrix gaps, existing-before-new')
    finally:
        for child in children:
            if child.poll() is None:
                child.kill()
                child.communicate(timeout=5)
