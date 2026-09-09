# Search objective contracts

Run `dotnet run --project tools/SearchObjectiveChecks/SearchObjectiveChecks.csproj -c Release`.

Links production objective and ordering code; only the runtime result DTO is stubbed. Covers the 30 HP default, reward constraints, custom reserves, balanced loss-before-growth ordering, and no-gain fallback without reward-only HP gating. Does not run game simulation, beam search, potion eligibility, or UI.
