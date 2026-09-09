# Run advice capture contracts

Run `dotnet run --project tools/RunAdviceCaptureChecks -c Release` from the repository root.

Links the production capture and evaluator against API-shaped test doubles. Checks damage normalization, source timing/quantity, immutable value capture, starter supplies, base energy and coverage labels. The main Release build independently checks compatibility with the native game assembly. These tests do not execute native hooks, Godot UI, or game behavior.
