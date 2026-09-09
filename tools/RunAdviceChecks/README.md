# RunAdvice checks

Run `dotnet run --project tools/RunAdviceChecks/RunAdviceChecks.csproj -c Release`.

Links the production evaluator without game dependencies. Covers continuous shop price penalties across the 150-gold reserve, mixed reserve/surplus spending, monotonicity, affordability, and the zero-score saving baseline. Does not validate game UI or overall recommendation quality.

Also links AdviceMechanics and checks trigger/payoff pairing, diminishing returns, draw inhibition, star supply/demand, mixed-tag order and Claw duplicates. This does not prove native metadata capture or run-wide strategy quality.
