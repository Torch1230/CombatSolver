# Card hook receiver checks

Run `dotnet run --project tools/CardHookReceiverChecks -c Release`.

Links the production `PredictedCard`, `SimCardPile` and `CardHookReceiver` sources. Game model stubs isolate the receiver/clone contract; they do not simulate the full game. Checks frozen hook membership, branch-local COW replacement, multiple same-type card instances, moved cards, unchanged non-card receivers and parent/live isolation.

`--legacy` dispatches the frozen preview reference used before the fix. It must fail at the first forked hand: Bound cleanup replaces the preview, so a later hand-only hook cannot find its card. The fixed dispatch follows the captured wrapper and must record the original hand count.

This is a COW regression, not native Regret damage, a restored player report, or a whole-combat search test.
