# ChoiceSourceAudit

Read-only audit of the locally installed native game assembly. Scans declared model instance methods, async/iterator bodies and same-model helpers for explicit card selection, automatic play, card generation, Shiv/Soul creation and Forge calls. Records generic PowerCmd.Apply edges so delayed sources can be traced back to their cards.

```sh
dotnet run --project tools/ChoiceSourceAudit/ChoiceSourceAudit.csproj -c Release -- /absolute/path/audit.json
```

Set `Sts2DataDir` in the repository `local.props`, or pass it as an MSBuild property, to a game data directory containing sts2.dll, 0Harmony.dll and GodotSharp.dll. The scanner does not start a game or invoke model effects. It records IL read failures and returns nonzero if any occur; it never silently drops them.

Output is an inventory of call sites, not a performance profile or a claim that all matches create player choices. Acquisition-only relics, fixed RNG results, MultiplayerOnly sources, deterministic selectors and generated-card lifecycle helpers require separate classification. It does not enumerate every possible deck synergy, virtual dispatch target, third-party patch or dynamically registered selection source. The reviewed 2026-09-12 table is in `docs/performance/choice-source-inventory-20260912.md`.
