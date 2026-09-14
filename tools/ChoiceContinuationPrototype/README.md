# Choice continuation prototype

An executable, experimental **own-discard checkpoint for Dagger Throw, Acrobatics and Prepared**, pinned to source `1ef4601` / game `0.111.0`. [Results and limits](../../docs/performance/choice-continuation-prototype-20260914.md).

Normal builds exclude `tools/**/*.cs`. Nothing here enables a production search policy, changes search budgets, or runs in the shipped executor. `engine.patch` is the reviewable experimental engine diff; the four C# files are injected only by the dedicated builder.

## Reproduce

Create a disposable worktree at `1ef4601` and configure its `local.props` with the local game and Ritsu references. Do not use a checkout another task is editing or building. Use a new output/evidence directory and an instance name owned by this experiment.

```bash
git worktree add --detach .local/choice-prototype-source 1ef4601
# Configure .local/choice-prototype-source/local.props for this machine.
python3 tools/ChoiceContinuationPrototype/build.py \
  --source .local/choice-prototype-source --output .local/choice-prototype-build
python3 tools/ChoiceContinuationPrototype/run.py \
  --build .local/choice-prototype-build --evidence .local/choice-prototype-evidence \
  --game-root /path/to/local/game --instance my-choice-prototype
```

The builder checks the exact source revision and clean tracked files, applies the patch, builds with copying to the normal game disabled, and restores injected source in `finally`. If forcibly killed, discard/recreate that disposable worktree. It does not overwrite an existing output directory. The runner verifies dedicated native-check markers and measurement outputs, rejecting a normal DLL that merely finishes a generic scenario. The Linux runner uses a dedicated headless instance, a 120-second request limit, ordinary GC, and stops its instance in `finally`. It does not start visible Steam. Use `--card acrobatics` and then `--card prepared` with fresh evidence directories to run the priority cards, each at upgrade levels 0 and 1. Windows execution has not been validated; the existing PowerShell unattended launcher can load the same experimental assembly and `CHOICE-PROTOTYPE` scenario.

## Ownership and eligibility

The one program counter is **manual own-choice resolution, before card-play completion**. The checkpoint owns the paused simulator, explicit frame (card, target, native play, result location, block-before value, trace, history boundary), and copied processed-death IDs. All CLR scopes unwind. It stores no suspended task, closure, or live combat mutation. A private gate serializes forks of its seed; independent children resume outside that gate. Disposing the checkpoint drops its seed/frame; each resume retains only its child.

Only a plain `DAGGER_THROW`, `ACROBATICS` or `PREPARED`, first play of a one-play manual action, an empty explicit choice cursor, and its own hand-discard request qualify. Draw/damage/block transactions, opaque or transaction-bearing store states, nested root traces, pending powers/turn end/knowledge/lamp state, and unsupported history payloads decline. Declining is not an exception handler. Risk enum records, started-play, damage, attack, and resolved draw entries are explicitly supported.

The completed history prefix remains immutable/shared. The active suffix gets new trace identities, a child-owned native `CardPlay` pointing to the child preview, copied mutable `DamageResult` objects and hit-result arrays, and remapped draw completion pairs through the same fork context as state/store. Ordinary simulator `Fork()` rejects an owned suspended seed. Existing transaction assertions remain intact. History risk signatures/counters retain their original values.

Uninterrupted execution and resumption call the same extracted completion/result-pile tails. A second pending selector during resumption returns an incomplete result; the test driver discards that child and replays the original full action. Multi-choice plans and ineligible actions use full replay directly. The checkpoint never edits choices, selects a default on failure, or swallows execution exceptions. Failed/cancelled children release cursor ownership without masking the original exception; that cleanup cannot turn a failed child into a reusable snapshot. The optional synchronous callback in `Resume` exists only to inject contract failures and force two-worker overlap; it is never retained in a frame/checkpoint.

## Evidence

`CHOICE-PROTOTYPE` compares all nine ordinary Dagger Throw discard options against full replay: continuation text, domain fingerprint, all RNG streams, ordered piles, terminal/shuffle values, full history payloads and trace graph. It separately checks exact play/start/finish/preview and deferred-draw identities, later sibling mutation, revisits, completed forks, actual two-worker execution ownership, pre/in-flight cancellation, original exception identity, disposal and weak retention. An upgraded card with completed prior history and a real shuffle exercises the nonempty-prefix case. Replay, enchantment, paired-hook and nested Sly branches exercise full-replay fallback. The two priority scenarios compare all 9/10 Acrobatics options and 8/36 Prepared options/combinations, including real shuffles and nested Sly fallback. Each card/upgrade has a representative native action that must finish its exact `GameAction.CompletionTask` and match the full continuation.

Performance uses a fixed number of one-choice families: **legacy discovery + N full replays** versus **capture + N resumed children**, including forks, prefix retention/remapping, and common post-action work. All options are prepared identically outside timing. Both paths share one simulation-isolation domain and have one second of paired warm-up at each branch count before ABBA/ABBA samples. Dagger Throw uses 4,000 families per sample for N=1/2/4/8. Priority cards use 2,000 families for N=2/4/8, plus N=32 for upgraded Prepared. Serialization, semantic assertions, native execution and forced collections are outside timing. Allocation is `GC.GetAllocatedBytesForCurrentThread`; elapsed time is wall time, not CPU time.

For Dagger Throw, a separate ABBA retention measurement holds 128 families, each with its pending/prefix state and all eight completed children, and measures the change in managed live bytes after full collection. It is a controlled held-state high-water mark, **not process RSS, a full search peak, or visible performance**. Full raw samples are exported as `choice-prototype.json`; the report preserves preliminary drift and all implementation attempts.

The initial Dagger Throw runs accidentally executed rejection diagnostics in the legacy discovery control. Their timing/allocation/retention numbers are superseded; their semantic results remain valid. The corrected executor returns immediately when capture is disabled, and the contracts assert that legacy discovery never populated the rejection diagnostic. The report preserves the superseded samples explicitly.
